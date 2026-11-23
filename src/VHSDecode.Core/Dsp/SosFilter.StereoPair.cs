using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

public static partial class SosFilter
{
    private const int StereoPairTileSamples = 8192;

    // Keep combined RF output allocations bounded per worker.
    internal const int MaximumStereoPairSamples = 4_194_304;

    internal static ((float[] Left, float[] Right) First, (float[] Left, float[] Right) Second)?
        TryApplyForwardBackwardFloat32StereoPair(
            IReadOnlyList<SosSection> left,
            IReadOnlyList<SosSection> right,
            ReadOnlySpan<float> first,
            ReadOnlySpan<float> second)
    {
        int sectionCount = left.Count;
        int tail = sectionCount % StereoGroupSize;
        if (!Sse2.IsSupported || first.Length != second.Length || sectionCount != right.Count
            || sectionCount is < 1 or > 32 || tail is not (0 or StereoTailSize))
        {
            return null;
        }

        int edge = DefaultPadLength(left);
        if (edge != DefaultPadLength(right) || first.Length <= edge
            || first.Length > MaximumStereoPairSamples - 2 * edge)
        {
            return null;
        }

        Span<float> firstEdges = stackalloc float[2 * edge];
        Span<float> secondEdges = stackalloc float[2 * edge];
        WriteOddExtensionEdgesFloat32(first, firstEdges[..edge], firstEdges[edge..]);
        WriteOddExtensionEdgesFloat32(second, secondEdges[..edge], secondEdges[edge..]);

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
                RepeatStereo(l.B0, r.B0), RepeatStereo(l.B1, r.B1), RepeatStereo(l.B2, r.B2),
                RepeatStereo(l.A1, r.A1), RepeatStereo(l.A2, r.A2));
        }

        Span<float> leftZi = stackalloc float[2 * sectionCount];
        Span<float> rightZi = stackalloc float[2 * sectionCount];
        SteadyStateInitialConditionsFloat32(leftSections, leftZi);
        SteadyStateInitialConditionsFloat32(rightSections, rightZi);
        Span<Vector128<float>> initial = stackalloc Vector128<float>[2 * sectionCount];
        Span<Vector128<float>> state = stackalloc Vector128<float>[2 * sectionCount];
        Vector128<float> head = PackTwoInputs(
            edge == 0 ? first[0] : firstEdges[0],
            edge == 0 ? second[0] : secondEdges[0]);
        for (int i = 0; i < initial.Length; i++)
        {
            initial[i] = RepeatStereo(leftZi[i], rightZi[i]);
            state[i] = Sse.Multiply(initial[i], head);
        }

        var firstLeft = new float[first.Length];
        var firstRight = new float[first.Length];
        var secondLeft = new float[first.Length];
        var secondRight = new float[first.Length];
        var main = new StereoPairChannels(firstLeft, firstRight, secondLeft, secondRight);
        Span<float> leftPad = stackalloc float[4 * edge];
        Span<float> rightPad = stackalloc float[4 * edge];
        var prefix = new StereoPairChannels(leftPad, edge);
        var suffix = new StereoPairChannels(rightPad, edge);

        Vector128<float>[] rented = ArrayPool<Vector128<float>>.Shared.Rent(StereoPairTileSamples);
        try
        {
            Span<Vector128<float>> scratch = rented.AsSpan(0, StereoPairTileSamples);
            // The output arrays also hold the forward results. Only the current
            // tile is packed, and the backward pass replaces each region in place.
            if (!ForwardStereoPair(sections, state, scratch, firstEdges[..edge], secondEdges[..edge], prefix)
                || !ForwardStereoPair(sections, state, scratch, first, second, main)
                || !ForwardStereoPair(sections, state, scratch, firstEdges[edge..], secondEdges[edge..], suffix))
            {
                return null;
            }

            Vector128<float> last = edge == 0 ? main.Load(first.Length - 1) : suffix.Load(edge - 1);
            for (int i = 0; i < state.Length; i++)
            {
                state[i] = Sse.Multiply(initial[i], last);
            }
            return BackwardStereoPair(sections, state, scratch, suffix)
                && BackwardStereoPair(sections, state, scratch, main)
                && BackwardStereoPair(sections, state, scratch, prefix)
                ? ((firstLeft, firstRight), (secondLeft, secondRight))
                : null;
        }
        finally
        {
            ArrayPool<Vector128<float>>.Shared.Return(rented);
        }
    }

    private static Vector128<float> RepeatStereo(float left, float right)
        => Vector128.Create(left, right, left, right);

    private static Vector128<float> PackTwoInputs(float first, float second)
        => Vector128.Create(first, first, second, second);

    private static bool ForwardStereoPair(
        ReadOnlySpan<StereoSosSection> sections, Span<Vector128<float>> state,
        Span<Vector128<float>> scratch, ReadOnlySpan<float> first, ReadOnlySpan<float> second,
        StereoPairChannels output)
    {
        for (int start = 0; start < first.Length;)
        {
            int length = Math.Min(scratch.Length, first.Length - start);
            Span<Vector128<float>> tile = scratch[..length];
            if (!PackPairInput(first.Slice(start, length), second.Slice(start, length), tile))
            {
                return false;
            }

            ApplyStereoPass(sections, tile, state, backward: false);
            if (!StoreStereoPairTile(tile, output, start))
            {
                return false;
            }
            start += length;
        }

        return true;
    }

    private static bool PackPairInput(
        ReadOnlySpan<float> first, ReadOnlySpan<float> second, Span<Vector128<float>> tile)
    {
        ref float firstInput = ref MemoryMarshal.GetReference(first);
        ref float secondInput = ref MemoryMarshal.GetReference(second);
        Vector128<uint> exponent = Vector128.Create(FloatExponentMask);
        Vector128<uint> invalid = Vector128<uint>.Zero;
        int i = 0;
        for (; i <= tile.Length - 4; i += 4)
        {
            Vector128<float> a = Vector128.LoadUnsafe(ref firstInput, (nuint)i);
            Vector128<float> b = Vector128.LoadUnsafe(ref secondInput, (nuint)i);
            invalid |= Sse2.CompareEqual(a.AsUInt32() & exponent, exponent);
            invalid |= Sse2.CompareEqual(b.AsUInt32() & exponent, exponent);
            tile[i] = Sse.Shuffle(a, b, 0x00);
            tile[i + 1] = Sse.Shuffle(a, b, 0x55);
            tile[i + 2] = Sse.Shuffle(a, b, 0xaa);
            tile[i + 3] = Sse.Shuffle(a, b, 0xff);
        }

        bool finite = Sse2.MoveMask(invalid.AsSByte()) == 0;
        for (; i < tile.Length; i++)
        {
            float a = first[i];
            float b = second[i];
            finite &= float.IsFinite(a) & float.IsFinite(b);
            tile[i] = PackTwoInputs(a, b);
        }
        return finite;
    }

    private static bool BackwardStereoPair(
        ReadOnlySpan<StereoSosSection> sections, Span<Vector128<float>> state,
        Span<Vector128<float>> scratch, StereoPairChannels output)
    {
        for (int end = output.Length; end > 0;)
        {
            int length = Math.Min(scratch.Length, end);
            int start = end - length;
            Span<Vector128<float>> tile = scratch[..length];
            output.LoadTile(start, tile);

            ApplyStereoPass(sections, tile, state, backward: true);
            if (!StoreStereoPairTile(tile, output, start))
            {
                return false;
            }
            end = start;
        }

        return true;
    }

    private static bool StoreStereoPairTile(ReadOnlySpan<Vector128<float>> tile, StereoPairChannels output, int start)
    {
        Vector128<uint> exponent = Vector128.Create(FloatExponentMask);
        Vector128<uint> invalid = Vector128<uint>.Zero;
        int i = 0;
        for (; i <= tile.Length - 4; i += 4)
        {
            Transpose4(tile[i], tile[i + 1], tile[i + 2], tile[i + 3],
                out var firstLeft, out var firstRight, out var secondLeft, out var secondRight);
            invalid |= Sse2.CompareEqual(firstLeft.AsUInt32() & exponent, exponent);
            invalid |= Sse2.CompareEqual(firstRight.AsUInt32() & exponent, exponent);
            invalid |= Sse2.CompareEqual(secondLeft.AsUInt32() & exponent, exponent);
            invalid |= Sse2.CompareEqual(secondRight.AsUInt32() & exponent, exponent);
            output.Store4(start + i, firstLeft, firstRight, secondLeft, secondRight);
        }

        for (; i < tile.Length; i++)
        {
            Vector128<float> sample = tile[i];
            invalid |= Sse2.CompareEqual(sample.AsUInt32() & exponent, exponent);
            output.Store(start + i, sample);
        }
        return Sse2.MoveMask(invalid.AsSByte()) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4(
        Vector128<float> a, Vector128<float> b, Vector128<float> c, Vector128<float> d,
        out Vector128<float> first, out Vector128<float> second,
        out Vector128<float> third, out Vector128<float> fourth)
    {
        Vector128<float> lowAB = Sse.UnpackLow(a, b);
        Vector128<float> lowCD = Sse.UnpackLow(c, d);
        Vector128<float> highAB = Sse.UnpackHigh(a, b);
        Vector128<float> highCD = Sse.UnpackHigh(c, d);
        first = Sse.Shuffle(lowAB, lowCD, 0x44);
        second = Sse.Shuffle(lowAB, lowCD, 0xee);
        third = Sse.Shuffle(highAB, highCD, 0x44);
        fourth = Sse.Shuffle(highAB, highCD, 0xee);
    }

    private readonly ref struct StereoPairChannels(
        Span<float> firstLeft, Span<float> firstRight, Span<float> secondLeft, Span<float> secondRight)
    {
        private readonly Span<float> _firstLeft = firstLeft;
        private readonly Span<float> _firstRight = firstRight;
        private readonly Span<float> _secondLeft = secondLeft;
        private readonly Span<float> _secondRight = secondRight;

        internal StereoPairChannels(Span<float> buffer, int length)
            : this(buffer[..length], buffer.Slice(length, length),
                buffer.Slice(2 * length, length), buffer.Slice(3 * length, length))
        {
        }

        internal int Length => _firstLeft.Length;

        internal Vector128<float> Load(int index)
            => Vector128.Create(_firstLeft[index], _firstRight[index], _secondLeft[index], _secondRight[index]);

        internal void LoadTile(int start, Span<Vector128<float>> tile)
        {
            ref float firstLeft = ref MemoryMarshal.GetReference(_firstLeft);
            ref float firstRight = ref MemoryMarshal.GetReference(_firstRight);
            ref float secondLeft = ref MemoryMarshal.GetReference(_secondLeft);
            ref float secondRight = ref MemoryMarshal.GetReference(_secondRight);
            int i = 0;
            for (; i <= tile.Length - 4; i += 4)
            {
                nuint index = (nuint)(start + i);
                Transpose4(
                    Vector128.LoadUnsafe(ref firstLeft, index), Vector128.LoadUnsafe(ref firstRight, index),
                    Vector128.LoadUnsafe(ref secondLeft, index), Vector128.LoadUnsafe(ref secondRight, index),
                    out tile[i], out tile[i + 1], out tile[i + 2], out tile[i + 3]);
            }

            for (; i < tile.Length; i++)
            {
                tile[i] = Load(start + i);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Store4(int index, Vector128<float> firstLeft, Vector128<float> firstRight,
            Vector128<float> secondLeft, Vector128<float> secondRight)
        {
            firstLeft.StoreUnsafe(ref MemoryMarshal.GetReference(_firstLeft), (nuint)index);
            firstRight.StoreUnsafe(ref MemoryMarshal.GetReference(_firstRight), (nuint)index);
            secondLeft.StoreUnsafe(ref MemoryMarshal.GetReference(_secondLeft), (nuint)index);
            secondRight.StoreUnsafe(ref MemoryMarshal.GetReference(_secondRight), (nuint)index);
        }

        internal void Store(int index, Vector128<float> value)
        {
            _firstLeft[index] = value.GetElement(0);
            _firstRight[index] = value.GetElement(1);
            _secondLeft[index] = value.GetElement(2);
            _secondRight[index] = value.GetElement(3);
        }
    }
}
