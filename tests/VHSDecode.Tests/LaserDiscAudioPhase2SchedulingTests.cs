using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscAudioPhase2SchedulingTests
{
    public static IEnumerable<object[]> SingleFields()
    {
        foreach (int length in new[] { 1, 1024, 29730, 30721, 32767 })
        foreach (int budget in new[] { 0, 1, 2, 4, 20 })
            yield return [length, budget];
    }

    [Theory]
    [MemberData(nameof(SingleFields))]
    public void SinglePaddedDecodeMatchesPublicRawBitsAndOwnership(int length, int budget)
    {
        LaserDiscAnalogAudioFilterSet filters = Filters();
        LaserDiscAnalogAudioBlock input = Input(length, filters) with { UsesFloat32Storage = budget % 2 == 0 };
        double[] leftSource = input.Left.ToArray(), rightSource = input.Right.ToArray();
        Complex[] leftFilter = filters.Left.Stage2Filter.ToArray(), rightFilter = filters.Right.Stage2Filter.ToArray();
        LaserDiscAnalogAudioBlock expected = LaserDiscAnalogAudioPhase2.Apply(input, filters);
        LaserDiscAnalogAudioBlock actual = LaserDiscAnalogAudioPhase2.ApplyForDecode(input, filters, budget);
        Strict(expected, actual);
        Assert.False(actual.UsesFloat32Storage);
        Assert.NotSame(input.Left, actual.Left); Assert.NotSame(input.Right, actual.Right);
        Assert.NotSame(actual.Left, actual.Right);
        double[] retainedLeft = actual.Left.ToArray(), retainedRight = actual.Right.ToArray();
        LaserDiscAnalogAudioBlock later = LaserDiscAnalogAudioPhase2.ApplyForDecode(input, filters, budget);
        Assert.NotSame(actual.Left, later.Left); Assert.NotSame(actual.Right, later.Right);
        later.Left[0] = -12345;
        Bits(retainedLeft, actual.Left); Bits(retainedRight, actual.Right);
        Bits(leftSource, input.Left); Bits(rightSource, input.Right);
        Assert.True(MemoryMarshal.AsBytes(leftFilter.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(filters.Left.Stage2Filter.AsSpan())));
        Assert.True(MemoryMarshal.AsBytes(rightFilter.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(filters.Right.Stage2Filter.AsSpan())));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    [InlineData(32769)]
    [InlineData(65537)]
    public void ZeroFullRealAndMultiblockRetainPublicBehavior(int length)
    {
        LaserDiscAnalogAudioFilterSet filters = Filters();
        LaserDiscAnalogAudioBlock input = Input(length, filters);
        LaserDiscAnalogAudioBlock expected = LaserDiscAnalogAudioPhase2.Apply(input, filters);
        LaserDiscAnalogAudioBlock actual = LaserDiscAnalogAudioPhase2.ApplyForDecode(input, filters, 20);
        Strict(expected, actual);
        if (length == 0) Assert.Same(input, actual);
        if (length > 32768) Assert.True(actual.UsesFloat32Storage);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(16384)]
    public void OtherFilterSizesRetainOriginalPath(int filterLength)
    {
        LaserDiscAnalogAudioFilterSet filters = Filters(filterLength);
        LaserDiscAnalogAudioBlock input = Input(769, filters);
        Strict(LaserDiscAnalogAudioPhase2.Apply(input, filters), LaserDiscAnalogAudioPhase2.ApplyForDecode(input, filters, 20));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("signed-zero")]
    [InlineData("subnormal")]
    [InlineData("cap")]
    [InlineData("rising-peaks")]
    public void FinitePreparedEdgesKeepStrictBits(string kind)
    {
        LaserDiscAnalogAudioFilterSet filters = Filters(leftCenter: 0, rightCenter: -0.0);
        LaserDiscAnalogAudioBlock input = Input(1024, filters);
        for (int i = 0; i < input.Left.Length; i++)
        {
            if (kind == "zero") input.Left[i] = input.Right[i] = 0;
            else if (kind == "signed-zero") { input.Left[i] = i % 2 == 0 ? -0.0 : 0.0; input.Right[i] = -input.Left[i]; }
            else if (kind == "subnormal")
            {
                input.Left[i] = BitConverter.UInt32BitsToSingle((uint)(i % 31 + 1)); input.Right[i] = -input.Left[i];
                filters.Left.Stage2Filter[i] = new Complex(double.Epsilon, -double.Epsilon);
                filters.Right.Stage2Filter[i] = new Complex(-double.Epsilon, double.Epsilon);
            }
            else if (kind == "cap")
            {
                input.Left[i] = 4_294_967_296; input.Right[i] = -4_294_967_296;
                filters.Left.Stage2Filter[i] = new Complex(4_294_967_296, -4_294_967_296);
                filters.Right.Stage2Filter[i] = new Complex(-4_294_967_296, 4_294_967_296);
            }
            else { input.Left[i] = (i % 40) * 100_000; input.Right[i] = ((i * 7) % 37) * 80_000; }
        }
        foreach (int budget in new[] { 0, 1, 2, 4, 20 })
            Strict(LaserDiscAnalogAudioPhase2.Apply(input, filters), LaserDiscAnalogAudioPhase2.ApplyForDecode(input, filters, budget));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void MalformedShapesKeepPublicExceptionOrder(int kind)
    {
        LaserDiscAnalogAudioFilterSet? filters = Filters();
        LaserDiscAnalogAudioBlock? input = Input(8, filters);
        if (kind == 0) { input = null; filters = null; }
        else if (kind == 1) { input = input with { Right = new double[9] }; filters = Filters(512); }
        else if (kind == 2) { input = Input(0, filters); filters = Filters(512); }
        else if (kind == 3) { input = input with { Left = null! }; filters = filters with { Left = null! }; }
        else if (kind == 4) filters = filters with { Left = filters.Left with { Stage2Filter = null! } };
        else if (kind == 5) filters = null;
        else filters = filters with { Right = filters.Right with { Stage2Filter = new Complex[32767] } };
        Exception expected = Assert.ThrowsAny<Exception>(() => LaserDiscAnalogAudioPhase2.Apply(input!, filters!));
        foreach (int budget in new[] { 0, 1, 2, 20 })
        {
            Exception actual = Assert.ThrowsAny<Exception>(() => LaserDiscAnalogAudioPhase2.ApplyForDecode(input!, filters!, budget));
            Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal(expected.HResult, actual.HResult);
            Assert.Equal(expected.Message, actual.Message);
            Assert.Equal((expected as ArgumentException)?.ParamName, (actual as ArgumentException)?.ParamName);
        }
    }

    [Theory]
    [InlineData("raw-nan")]
    [InlineData("raw-inf")]
    [InlineData("filter-nan")]
    [InlineData("filter-max")]
    [InlineData("center-nan")]
    [InlineData("above-cap")]
    public void SpecialSerialFallbackKeepsDefinedBitsAndNaNClass(string kind)
    {
        LaserDiscAnalogAudioFilterSet filters = Filters(leftCenter: 0, rightCenter: 0);
        LaserDiscAnalogAudioBlock input = Input(1024, filters);
        if (kind == "raw-nan") input.Left[0] = BitConverter.UInt64BitsToDouble(0x7FF0123456789ABC);
        else if (kind == "raw-inf") input.Right[0] = double.PositiveInfinity;
        else if (kind == "filter-nan") filters.Right.Stage2Filter[0] = new Complex(double.NaN, 1);
        else if (kind == "filter-max") filters.Left.Stage2Filter[0] = new Complex(double.MaxValue, double.MaxValue);
        else if (kind == "center-nan") filters = filters with { Left = filters.Left with { CenterFrequencyHz = double.NaN } };
        else input.Left[0] = BitConverter.UInt32BitsToSingle(0x4F800001);
        double[] leftSource = input.Left.ToArray(), rightSource = input.Right.ToArray();
        LaserDiscAnalogAudioBlock expected = LaserDiscAnalogAudioPhase2.Apply(input, filters);
        LaserDiscAnalogAudioBlock actual = LaserDiscAnalogAudioPhase2.ApplyForDecode(input, filters, 20);
        Assert.Equal(expected.DecimationFactor, actual.DecimationFactor); Assert.Equal(expected.UsesFloat32Storage, actual.UsesFloat32Storage);
        DefinedBitsAndNaNClass(expected.Left, actual.Left); DefinedBitsAndNaNClass(expected.Right, actual.Right);
        Bits(leftSource, input.Left); Bits(rightSource, input.Right);
    }

    [Fact]
    public void PipelineWithoutAudioFiltersRetainsSameReferenceInBothEntrypoints()
    {
        var filters = new DecodeFilterSet([], [], [], [], [], [], null, [], [], [], [], [], [], null);
        using var pipeline = new RfBlockDecodePipeline(new NeverReadLoader(), filters, 40_000_000);
        var input = new LaserDiscAnalogAudioBlock([1.0], [2.0], 32, true);
        Assert.Same(input, pipeline.ApplyLaserDiscAnalogAudioPhase2(input));
        foreach (int budget in new[] { 0, 1, 2, 4, 20 }) Assert.Same(input, pipeline.ApplyLaserDiscAnalogAudioPhase2(input, budget));
    }

    private static LaserDiscAnalogAudioFilterSet Filters(int length = 32768, double leftCenter = 2_300_000.123456, double rightCenter = 2_800_000.654321)
    {
        Complex[] left = new Complex[length], right = new Complex[length];
        for (int i = 0; i < length; i++) { left[i] = new Complex(0.5 + i % 17 / 64.0, (i % 11 - 5) / 128.0); right[i] = new Complex(0.25 + i % 13 / 32.0, (i % 7 - 3) / 64.0); }
        return new(new(0, length, 1_250_000, 0, leftCenter, [], left), new(0, length, 1_250_000, 0, rightCenter, [], right), 32);
    }

    private static LaserDiscAnalogAudioBlock Input(int length, LaserDiscAnalogAudioFilterSet filters)
    {
        var left = new double[length]; var right = new double[length];
        for (int i = 0; i < length; i++) { left[i] = filters.Left.CenterFrequencyHz + (i * 73 % 997 - 498) * 0.125; right[i] = filters.Right.CenterFrequencyHz + (i * 97 % 991 - 495) * 0.0625; }
        return new(left, right, 32, true);
    }

    private static void Strict(LaserDiscAnalogAudioBlock expected, LaserDiscAnalogAudioBlock actual)
    {
        Assert.Equal(expected.DecimationFactor, actual.DecimationFactor); Assert.Equal(expected.UsesFloat32Storage, actual.UsesFloat32Storage);
        Bits(expected.Left, actual.Left); Bits(expected.Right, actual.Right);
    }

    private static void Bits(double[] expected, double[] actual)
        => Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));

    private static void DefinedBitsAndNaNClass(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            // Existing scalar FFT contract: NaN sign/payload may vary by JIT tier.
            // This fallback test claims defined bits/NaN class, never raw NaN equality.
            if (double.IsNaN(expected[i]) && double.IsNaN(actual[i])) continue;
            Assert.Equal(BitConverter.DoubleToUInt64Bits(expected[i]), BitConverter.DoubleToUInt64Bits(actual[i]));
        }
    }

    private sealed class NeverReadLoader : IRfSampleLoader
    {
        public double[]? Read(Stream stream, long sample, int readLength) => throw new InvalidOperationException("No loader read expected.");
    }
}
