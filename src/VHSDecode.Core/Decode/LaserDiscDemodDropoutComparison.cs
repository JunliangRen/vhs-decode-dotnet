using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Decode;

internal static class LaserDiscDemodDropoutComparison
{
    internal readonly record struct Thresholds(
        float ValidMinimum,
        float ValidMaximum,
        float SyncMinimum,
        float ValidMinimum05,
        float ValidMaximum05,
        float SyncMinimum05,
        double RawDemodMaximum,
        float RfHighPassMaximum);

    internal static void FillErrorMap(
        RfDecodedSpan span,
        bool[] syncArea,
        bool[] errorMap,
        int startSample,
        int endSample,
        Thresholds thresholds,
        bool hasRawDemod,
        bool hasVideoLowPass,
        bool hasRfHighPass,
        int rfHighPassOffset)
    {
        // Keep malformed/short inputs on the checked scalar path. In particular,
        // do not add eager exceptions or change partial writes before an exception.
        if (!Avx.IsSupported || !BitConverter.IsLittleEndian
            || startSample < 0 || endSample < startSample || endSample - startSample < 4
            || span is null || span.Video is null || syncArea is null || errorMap is null
            || endSample > span.Video.Length || endSample > syncArea.Length || endSample > errorMap.Length
            || (hasRawDemod && (span.DemodRaw is null || endSample > span.DemodRaw.Length))
            || (hasVideoLowPass && (span.VideoLowPass is null || endSample > span.VideoLowPass.Length))
            || (hasRfHighPass && (span.RfHighPass is null || endSample > span.RfHighPass.Length)))
        {
            FillScalar(span!, syncArea!, errorMap!, startSample, endSample, thresholds,
                hasRawDemod, hasVideoLowPass, hasRfHighPass, rfHighPassOffset);
            return;
        }

        ReadOnlySpan<double> raw = span.DemodRaw;
        ReadOnlySpan<double> lowPass = span.VideoLowPass;
        ref double videoFirst = ref MemoryMarshal.GetArrayDataReference(span.Video);
        ref double rawFirst = ref MemoryMarshal.GetReference(raw);
        ref double lowFirst = ref MemoryMarshal.GetReference(lowPass);
        ref byte syncFirst = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(syncArea.AsSpan()));
        ref byte errorFirst = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(errorMap.AsSpan()));
        Vector128<float> validMin = Vector128.Create(thresholds.ValidMinimum);
        Vector128<float> validMax = Vector128.Create(thresholds.ValidMaximum);
        Vector128<float> syncMin = Vector128.Create(thresholds.SyncMinimum);
        Vector128<float> validMin05 = Vector128.Create(thresholds.ValidMinimum05);
        Vector128<float> validMax05 = Vector128.Create(thresholds.ValidMaximum05);
        Vector128<float> syncMin05 = Vector128.Create(thresholds.SyncMinimum05);
        Vector256<double> rawMaximum = Vector256.Create(thresholds.RawDemodMaximum);
        int index = startSample;
        for (; index <= endSample - 4; index += 4)
        {
            uint syncBytes = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref syncFirst, index));
            Vector128<float> selection = BuildSyncSelection(syncBytes);
            Vector128<float> minimum = Select(selection, syncMin, validMin);
            Vector128<float> video = Avx.ConvertToVector128Single(
                Vector256.LoadUnsafe(ref videoFirst, (nuint)index));
            int mask = Sse.MoveMask(Sse.Or(Sse.CompareLessThan(video, minimum), Sse.CompareGreaterThan(video, validMax)));
            if (hasRawDemod)
            {
                Vector128<float> rounded = Avx.ConvertToVector128Single(
                    Vector256.LoadUnsafe(ref rawFirst, (nuint)index));
                // Original comparison: float32-rounded sample against a double bound.
                mask |= Avx.MoveMask(Avx.Compare(Avx.ConvertToVector256Double(rounded), rawMaximum,
                    FloatComparisonMode.OrderedGreaterThanNonSignaling));
            }
            if (hasVideoLowPass)
            {
                Vector128<float> minimum05 = Select(selection, syncMin05, validMin05);
                Vector128<float> lowVideo = Avx.ConvertToVector128Single(
                    Vector256.LoadUnsafe(ref lowFirst, (nuint)index));
                mask |= Sse.MoveMask(Sse.Or(Sse.CompareLessThan(lowVideo, minimum05), Sse.CompareGreaterThan(lowVideo, validMax05)));
            }

            // AVX runs on little-endian hardware here; expand four bits into four
            // canonical bool bytes. False entries overwrite previous map contents.
            uint packed = (uint)((mask & 1) | ((mask & 2) << 7) | ((mask & 4) << 14) | ((mask & 8) << 21));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref errorFirst, index), packed);
        }
        for (; index < endSample; index++)
        {
            float minimum = syncArea[index] ? thresholds.SyncMinimum : thresholds.ValidMinimum;
            float video = (float)span.Video[index];
            bool videoOutOfRange = video < minimum || video > thresholds.ValidMaximum;
            bool rawDemodOutOfRange = hasRawDemod && (float)span.DemodRaw[index] > thresholds.RawDemodMaximum;
            bool videoLowPassOutOfRange = false;
            if (hasVideoLowPass)
            {
                float lowPassMinimum = syncArea[index] ? thresholds.SyncMinimum05 : thresholds.ValidMinimum05;
                float lowPassVideo = (float)span.VideoLowPass![index];
                videoLowPassOutOfRange = lowPassVideo < lowPassMinimum || lowPassVideo > thresholds.ValidMaximum05;
            }
            errorMap[index] = videoOutOfRange || rawDemodOutOfRange || videoLowPassOutOfRange;
        }

        // The complete base map must precede RFHPF writes: a positive offset can
        // target a later entry that would otherwise be overwritten with false.
        if (hasRfHighPass)
        {
            ref double highFirst = ref MemoryMarshal.GetArrayDataReference(span.RfHighPass!);
            Vector128<float> minimum = Vector128.Create(-thresholds.RfHighPassMaximum);
            Vector128<float> maximum = Vector128.Create(thresholds.RfHighPassMaximum);
            index = startSample;
            for (; index <= endSample - 4; index += 4)
            {
                Vector128<float> samples = Avx.ConvertToVector128Single(
                    Vector256.LoadUnsafe(ref highFirst, (nuint)index));
                uint mask = (uint)Sse.MoveMask(Sse.Or(Sse.CompareLessThan(samples, minimum), Sse.CompareGreaterThan(samples, maximum)));
                while (mask != 0)
                {
                    int lane = BitOperations.TrailingZeroCount(mask);
                    int mapped = index + lane + rfHighPassOffset;
                    if (mapped >= startSample && mapped < endSample) errorMap[mapped] = true;
                    mask &= mask - 1;
                }
            }
            for (; index < endSample; index++)
            {
                float value = (float)span.RfHighPass![index];
                if (!(value < -thresholds.RfHighPassMaximum || value > thresholds.RfHighPassMaximum)) continue;
                int mapped = index + rfHighPassOffset;
                if (mapped >= startSample && mapped < endSample) errorMap[mapped] = true;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> BuildSyncSelection(uint syncBytes)
    {
        Vector128<byte> packed = Vector128.CreateScalar(syncBytes).AsByte();
        Vector128<ushort> words = Sse2.UnpackLow(packed, Vector128<byte>.Zero).AsUInt16();
        Vector128<int> integers = Sse2.UnpackLow(words, Vector128<ushort>.Zero).AsInt32();
        return Sse2.CompareGreaterThan(integers, Vector128<int>.Zero).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Select(Vector128<float> selection, Vector128<float> whenTrue, Vector128<float> whenFalse)
        => Sse.Or(Sse.And(selection, whenTrue), Sse.AndNot(selection, whenFalse));

    private static void FillScalar(
        RfDecodedSpan span,
        bool[] syncArea,
        bool[] errorMap,
        int startSample,
        int endSample,
        Thresholds thresholds,
        bool hasRawDemod,
        bool hasVideoLowPass,
        bool hasRfHighPass,
        int rfHighPassOffset)
    {
        for (int i = startSample; i < endSample; i++)
        {
            float minimum = syncArea[i] ? thresholds.SyncMinimum : thresholds.ValidMinimum;
            float video = (float)span.Video[i];
            bool videoOutOfRange = video < minimum || video > thresholds.ValidMaximum;
            bool rawDemodOutOfRange = hasRawDemod && (float)span.DemodRaw[i] > thresholds.RawDemodMaximum;
            bool videoLowPassOutOfRange = false;
            if (hasVideoLowPass)
            {
                float lowPassMinimum = syncArea[i] ? thresholds.SyncMinimum05 : thresholds.ValidMinimum05;
                float lowPassVideo = (float)span.VideoLowPass![i];
                videoLowPassOutOfRange = lowPassVideo < lowPassMinimum || lowPassVideo > thresholds.ValidMaximum05;
            }
            errorMap[i] = videoOutOfRange || rawDemodOutOfRange || videoLowPassOutOfRange;
        }
        if (hasRfHighPass)
        {
            for (int i = startSample; i < endSample; i++)
            {
                float value = (float)span.RfHighPass![i];
                if (!(value < -thresholds.RfHighPassMaximum || value > thresholds.RfHighPassMaximum)) continue;
                int mapped = i + rfHighPassOffset;
                if (mapped >= startSample && mapped < endSample) errorMap[mapped] = true;
            }
        }
    }
}
