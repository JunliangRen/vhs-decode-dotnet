using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

public static partial class SosFilter
{
    private const int StereoGroupSize = 6;
    private const int StereoTailSize = 4;
    private const uint FloatExponentMask = 0x7f80_0000u;

    private readonly record struct StereoSosSection(
        Vector128<float> B0, Vector128<float> B1, Vector128<float> B2,
        Vector128<float> A1, Vector128<float> A2);

    internal static (float[] Left, float[] Right)? TryApplyForwardBackwardFloat32Stereo(
        IReadOnlyList<SosSection> left,
        IReadOnlyList<SosSection> right,
        ReadOnlySpan<float> input)
    {
        int sectionCount = left.Count;
        int tail = sectionCount % StereoGroupSize;
        if (!Sse2.IsSupported || input.IsEmpty || sectionCount != right.Count
            || sectionCount is < 1 or > 32 || tail is not (0 or StereoTailSize))
        {
            return null;
        }

        int edge = DefaultPadLength(left);
        if (edge != DefaultPadLength(right) || input.Length <= edge)
        {
            return null;
        }

        int length = checked(input.Length + 2 * edge);
        ulong[] rented = ArrayPool<ulong>.Shared.Rent(length);
        try
        {
            Span<ulong> samples = rented.AsSpan(0, length);
            Span<float> edges = stackalloc float[2 * edge];
            WriteOddExtensionEdgesFloat32(input, edges[..edge], edges[edge..]);
            if (!PackStereoInput(edges[..edge], samples[..edge])
                || !PackStereoInput(input, samples.Slice(edge, input.Length))
                || !PackStereoInput(edges[edge..], samples[(edge + input.Length)..]))
            {
                return null;
            }

            Span<FloatSosSection> leftSections = stackalloc FloatSosSection[sectionCount];
            Span<FloatSosSection> rightSections = stackalloc FloatSosSection[sectionCount];
            ConvertToFloat32(left, leftSections);
            ConvertToFloat32(right, rightSections);

            Span<StereoSosSection> sections = stackalloc StereoSosSection[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                FloatSosSection l = leftSections[i];
                FloatSosSection r = rightSections[i];
                sections[i] = new StereoSosSection(
                    Stereo(l.B0, r.B0), Stereo(l.B1, r.B1), Stereo(l.B2, r.B2),
                    Stereo(l.A1, r.A1), Stereo(l.A2, r.A2));
            }

            Span<float> leftZi = stackalloc float[2 * sectionCount];
            Span<float> rightZi = stackalloc float[2 * sectionCount];
            SteadyStateInitialConditionsFloat32(leftSections, leftZi);
            SteadyStateInitialConditionsFloat32(rightSections, rightZi);

            Span<Vector128<float>> state = stackalloc Vector128<float>[2 * sectionCount];
            ScaleStereoState(leftZi, rightZi, samples[0], state);
            ApplyStereoPass(sections, samples, state, backward: false);
            ScaleStereoState(leftZi, rightZi, samples[^1], state);
            ApplyStereoPass(sections, samples, state, backward: true);

            return UnpackStereoOutput(samples.Slice(edge, input.Length));
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
        }
    }

    private static Vector128<float> Stereo(float left, float right)
        => Vector128.Create(left, right, 0.0f, 0.0f);

    private static bool PackStereoInput(ReadOnlySpan<float> input, Span<ulong> output)
    {
        int i = 0;
        ref float firstInput = ref MemoryMarshal.GetReference(input);
        ref ulong firstOutput = ref MemoryMarshal.GetReference(output);
        Vector128<uint> exponent = Vector128.Create(FloatExponentMask);
        Vector128<uint> invalid = Vector128<uint>.Zero;
        for (; i <= input.Length - 4; i += 4)
        {
            Vector128<uint> samples = Vector128.LoadUnsafe(ref firstInput, (nuint)i).AsUInt32();
            invalid |= Sse2.CompareEqual(samples & exponent, exponent);
            Sse2.UnpackLow(samples, samples).AsUInt64().StoreUnsafe(ref firstOutput, (nuint)i);
            Sse2.UnpackHigh(samples, samples).AsUInt64().StoreUnsafe(ref firstOutput, (nuint)(i + 2));
        }
        bool finite = Sse2.MoveMask(invalid.AsSByte()) == 0;

        for (; i < input.Length; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(input[i]);
            finite &= IsFiniteFloatBits(bits);
            output[i] = bits | ((ulong)bits << 32);
        }

        return finite;
    }

    private static (float[] Left, float[] Right)? UnpackStereoOutput(ReadOnlySpan<ulong> samples)
    {
        var left = new float[samples.Length];
        var right = new float[samples.Length];
        int i = 0;
        ref ulong firstSample = ref MemoryMarshal.GetReference(samples);
        ref float leftOutput = ref MemoryMarshal.GetArrayDataReference(left);
        ref float rightOutput = ref MemoryMarshal.GetArrayDataReference(right);
        Vector128<uint> exponent = Vector128.Create(FloatExponentMask);
        Vector128<uint> invalid = Vector128<uint>.Zero;
        for (; i <= samples.Length - 4; i += 4)
        {
            Vector128<float> first = Vector128.LoadUnsafe(ref firstSample, (nuint)i).AsSingle();
            Vector128<float> second = Vector128.LoadUnsafe(ref firstSample, (nuint)(i + 2)).AsSingle();
            invalid |= Sse2.CompareEqual(first.AsUInt32() & exponent, exponent);
            invalid |= Sse2.CompareEqual(second.AsUInt32() & exponent, exponent);
            // Select the even and odd lanes from adjacent packed stereo pairs.
            Sse.Shuffle(first, second, 0x88).StoreUnsafe(ref leftOutput, (nuint)i);
            Sse.Shuffle(first, second, 0xdd).StoreUnsafe(ref rightOutput, (nuint)i);
        }
        bool finite = Sse2.MoveMask(invalid.AsSByte()) == 0;

        for (; i < samples.Length; i++)
        {
            ulong pair = samples[i];
            uint l = (uint)pair;
            uint r = (uint)(pair >> 32);
            finite &= IsFiniteFloatBits(l) & IsFiniteFloatBits(r);
            left[i] = BitConverter.UInt32BitsToSingle(l);
            right[i] = BitConverter.UInt32BitsToSingle(r);
        }

        // Retain scalar behavior for non-finite sample values and NaN payloads.
        return finite ? (left, right) : null;
    }

    private static bool IsFiniteFloatBits(uint value) => (value & FloatExponentMask) != FloatExponentMask;

    private static void ScaleStereoState(
        ReadOnlySpan<float> leftZi,
        ReadOnlySpan<float> rightZi,
        ulong sample,
        Span<Vector128<float>> state)
    {
        Vector128<float> scale = Vector128.CreateScalar(sample).AsSingle();
        for (int i = 0; i < state.Length; i++)
        {
            state[i] = Sse.Multiply(Stereo(leftZi[i], rightZi[i]), scale);
        }
    }

    private static void ApplyStereoPass<TPacked>(
        ReadOnlySpan<StereoSosSection> sections,
        Span<TPacked> samples,
        Span<Vector128<float>> state,
        bool backward) where TPacked : unmanaged
    {
        // Each lane keeps its own coefficients and state. Separate operations
        // retain the FP32 rounding points from the scalar SOS recurrence.
        int section = 0;
        for (; section <= sections.Length - StereoGroupSize; section += StereoGroupSize)
        {
            ReadOnlySpan<StereoSosSection> group = sections.Slice(section, StereoGroupSize);
            Span<Vector128<float>> groupState = state.Slice(2 * section, 2 * StereoGroupSize);
            if (!HasUnitNumeratorEnds<TPacked>(group[1..]))
            {
                ApplyStereo6<TPacked, GeneralNumerator>(group, samples, groupState, backward);
            }
            else if (HasUnitNumeratorEnds<TPacked>(group[..1]))
            {
                ApplyStereo6<TPacked, UnitNumeratorEnds>(group, samples, groupState, backward);
            }
            else
            {
                ApplyStereo6<TPacked, GeneralFirstNumerator>(group, samples, groupState, backward);
            }
        }

        if (section < sections.Length)
        {
            ReadOnlySpan<StereoSosSection> group = sections[section..];
            Span<Vector128<float>> groupState = state[(2 * section)..];
            if (HasUnitNumeratorEnds<TPacked>(group))
            {
                ApplyStereo4<TPacked, UnitNumeratorEnds>(group, samples, groupState, backward);
            }
            else
            {
                ApplyStereo4<TPacked, GeneralNumerator>(group, samples, groupState, backward);
            }
        }
    }

    private readonly struct GeneralNumerator;
    private readonly struct GeneralFirstNumerator;
    private readonly struct UnitNumeratorEnds;

    private static bool HasUnitNumeratorEnds<TPacked>(ReadOnlySpan<StereoSosSection> sections)
        where TPacked : unmanaged
    {
        // Single-block stereo vectors use two lanes. A paired block uses all four.
        int activeLanes = typeof(TPacked) == typeof(ulong) ? 0b0011 : 0b1111;
        var one = Vector128.Create(1.0f);
        foreach (StereoSosSection section in sections)
        {
            int units = Sse.MoveMask(Sse.CompareEqual(section.B0, one) & Sse.CompareEqual(section.B2, one));
            if ((units & activeLanes) != activeLanes)
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyStereo4<TPacked, TNumerator>(
        ReadOnlySpan<StereoSosSection> sections,
        Span<TPacked> samples,
        Span<Vector128<float>> state,
        bool backward) where TPacked : unmanaged where TNumerator : struct
    {
        StereoSosSection first = sections[0];
        StereoSosSection second = sections[1];
        StereoSosSection third = sections[2];
        StereoSosSection fourth = sections[3];
        Vector128<float> firstZ1 = state[0];
        Vector128<float> firstZ2 = state[1];
        Vector128<float> secondZ1 = state[2];
        Vector128<float> secondZ2 = state[3];
        Vector128<float> thirdZ1 = state[4];
        Vector128<float> thirdZ2 = state[5];
        Vector128<float> fourthZ1 = state[6];
        Vector128<float> fourthZ2 = state[7];

        bool firstIsUnit = typeof(TNumerator) == typeof(UnitNumeratorEnds);
        bool restAreUnit = typeof(TNumerator) != typeof(GeneralNumerator);
        int step = backward ? -1 : 1;
        int index = backward ? samples.Length - 1 : 0;
        ref TPacked firstSample = ref MemoryMarshal.GetReference(samples);
        for (int sample = 0; sample < samples.Length; sample++, index += step)
        {
            ref TPacked packed = ref Unsafe.Add(ref firstSample, index);
            Vector128<float> value = LoadStereoSample(ref packed);
            Vector128<float> filtered = Sse.Add(firstIsUnit ? value : Sse.Multiply(first.B0, value), firstZ1);
            firstZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(first.B1, value), Sse.Multiply(first.A1, filtered)), firstZ2);
            firstZ2 = Sse.Subtract(firstIsUnit ? value : Sse.Multiply(first.B2, value), Sse.Multiply(first.A2, filtered));

            value = filtered;
            filtered = Sse.Add(restAreUnit ? value : Sse.Multiply(second.B0, value), secondZ1);
            secondZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(second.B1, value), Sse.Multiply(second.A1, filtered)), secondZ2);
            secondZ2 = Sse.Subtract(restAreUnit ? value : Sse.Multiply(second.B2, value), Sse.Multiply(second.A2, filtered));

            value = filtered;
            filtered = Sse.Add(restAreUnit ? value : Sse.Multiply(third.B0, value), thirdZ1);
            thirdZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(third.B1, value), Sse.Multiply(third.A1, filtered)), thirdZ2);
            thirdZ2 = Sse.Subtract(restAreUnit ? value : Sse.Multiply(third.B2, value), Sse.Multiply(third.A2, filtered));

            value = filtered;
            filtered = Sse.Add(restAreUnit ? value : Sse.Multiply(fourth.B0, value), fourthZ1);
            fourthZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(fourth.B1, value), Sse.Multiply(fourth.A1, filtered)), fourthZ2);
            fourthZ2 = Sse.Subtract(restAreUnit ? value : Sse.Multiply(fourth.B2, value), Sse.Multiply(fourth.A2, filtered));

            StoreStereoSample(ref packed, filtered);
        }

        if (typeof(TPacked) == typeof(Vector128<float>))
        {
            state[0] = firstZ1;
            state[1] = firstZ2;
            state[2] = secondZ1;
            state[3] = secondZ2;
            state[4] = thirdZ1;
            state[5] = thirdZ2;
            state[6] = fourthZ1;
            state[7] = fourthZ2;
        }
    }

    private static void ApplyStereo6<TPacked, TNumerator>(
        ReadOnlySpan<StereoSosSection> sections,
        Span<TPacked> samples,
        Span<Vector128<float>> state,
        bool backward) where TPacked : unmanaged where TNumerator : struct
    {
        StereoSosSection first = sections[0];
        StereoSosSection second = sections[1];
        StereoSosSection third = sections[2];
        StereoSosSection fourth = sections[3];
        StereoSosSection fifth = sections[4];
        StereoSosSection sixth = sections[5];
        Vector128<float> firstZ1 = state[0];
        Vector128<float> firstZ2 = state[1];
        Vector128<float> secondZ1 = state[2];
        Vector128<float> secondZ2 = state[3];
        Vector128<float> thirdZ1 = state[4];
        Vector128<float> thirdZ2 = state[5];
        Vector128<float> fourthZ1 = state[6];
        Vector128<float> fourthZ2 = state[7];
        Vector128<float> fifthZ1 = state[8];
        Vector128<float> fifthZ2 = state[9];
        Vector128<float> sixthZ1 = state[10];
        Vector128<float> sixthZ2 = state[11];

        bool firstIsUnit = typeof(TNumerator) == typeof(UnitNumeratorEnds);
        bool restAreUnit = typeof(TNumerator) != typeof(GeneralNumerator);
        int step = backward ? -1 : 1;
        int index = backward ? samples.Length - 1 : 0;
        ref TPacked firstSample = ref MemoryMarshal.GetReference(samples);
        for (int sample = 0; sample < samples.Length; sample++, index += step)
        {
            ref TPacked packed = ref Unsafe.Add(ref firstSample, index);
            Vector128<float> value = LoadStereoSample(ref packed);
            Vector128<float> filtered = Sse.Add(firstIsUnit ? value : Sse.Multiply(first.B0, value), firstZ1);
            firstZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(first.B1, value), Sse.Multiply(first.A1, filtered)), firstZ2);
            firstZ2 = Sse.Subtract(firstIsUnit ? value : Sse.Multiply(first.B2, value), Sse.Multiply(first.A2, filtered));

            value = filtered;
            filtered = Sse.Add(restAreUnit ? value : Sse.Multiply(second.B0, value), secondZ1);
            secondZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(second.B1, value), Sse.Multiply(second.A1, filtered)), secondZ2);
            secondZ2 = Sse.Subtract(restAreUnit ? value : Sse.Multiply(second.B2, value), Sse.Multiply(second.A2, filtered));

            value = filtered;
            filtered = Sse.Add(restAreUnit ? value : Sse.Multiply(third.B0, value), thirdZ1);
            thirdZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(third.B1, value), Sse.Multiply(third.A1, filtered)), thirdZ2);
            thirdZ2 = Sse.Subtract(restAreUnit ? value : Sse.Multiply(third.B2, value), Sse.Multiply(third.A2, filtered));

            value = filtered;
            filtered = Sse.Add(restAreUnit ? value : Sse.Multiply(fourth.B0, value), fourthZ1);
            fourthZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(fourth.B1, value), Sse.Multiply(fourth.A1, filtered)), fourthZ2);
            fourthZ2 = Sse.Subtract(restAreUnit ? value : Sse.Multiply(fourth.B2, value), Sse.Multiply(fourth.A2, filtered));

            value = filtered;
            filtered = Sse.Add(restAreUnit ? value : Sse.Multiply(fifth.B0, value), fifthZ1);
            fifthZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(fifth.B1, value), Sse.Multiply(fifth.A1, filtered)), fifthZ2);
            fifthZ2 = Sse.Subtract(restAreUnit ? value : Sse.Multiply(fifth.B2, value), Sse.Multiply(fifth.A2, filtered));

            value = filtered;
            filtered = Sse.Add(restAreUnit ? value : Sse.Multiply(sixth.B0, value), sixthZ1);
            sixthZ1 = Sse.Add(Sse.Subtract(Sse.Multiply(sixth.B1, value), Sse.Multiply(sixth.A1, filtered)), sixthZ2);
            sixthZ2 = Sse.Subtract(restAreUnit ? value : Sse.Multiply(sixth.B2, value), Sse.Multiply(sixth.A2, filtered));

            StoreStereoSample(ref packed, filtered);
        }

        if (typeof(TPacked) == typeof(Vector128<float>))
        {
            state[0] = firstZ1;
            state[1] = firstZ2;
            state[2] = secondZ1;
            state[3] = secondZ2;
            state[4] = thirdZ1;
            state[5] = thirdZ2;
            state[6] = fourthZ1;
            state[7] = fourthZ2;
            state[8] = fifthZ1;
            state[9] = fifthZ2;
            state[10] = sixthZ1;
            state[11] = sixthZ2;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> LoadStereoSample<TPacked>(ref TPacked packed) where TPacked : unmanaged
        => typeof(TPacked) == typeof(ulong)
            ? Vector128.CreateScalar(Unsafe.As<TPacked, ulong>(ref packed)).AsSingle()
            : Unsafe.As<TPacked, Vector128<float>>(ref packed);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreStereoSample<TPacked>(ref TPacked packed, Vector128<float> value) where TPacked : unmanaged
    {
        if (typeof(TPacked) == typeof(ulong))
        {
            Unsafe.As<TPacked, ulong>(ref packed) = value.AsUInt64().ToScalar();
        }
        else
        {
            Unsafe.As<TPacked, Vector128<float>>(ref packed) = value;
        }
    }
}
