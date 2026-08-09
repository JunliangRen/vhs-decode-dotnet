using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class NumpyReductionMedianTests
{
    [Theory(DisplayName = "Large float64 median matches the sorted reference")]
    [InlineData(4_095)]
    [InlineData(4_096)]
    [InlineData(4_097)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    [InlineData(51_180)]
    [InlineData(131_072)]
    [InlineData(355_255)]
    [InlineData(355_256)]
    public void LargeFloat64MedianMatchesSortedReference(int length)
    {
        var values = new double[length];
        ulong state = 0xD1B54A32D192ED03UL;
        for (int index = 0; index < values.Length; index++)
        {
            state = unchecked((state * 6364136223846793005UL) + 1442695040888963407UL);
            values[index] = index % 97 == 0
                ? 1.25
                : ((long)(state >> 11) - (1L << 52)) / (double)(1L << 31);
        }

        AssertMedianMatchesSortedReference(values);
    }

    [Fact(DisplayName = "Large float64 median preserves the first NaN payload")]
    public void LargeFloat64MedianPreservesFirstNanPayload()
    {
        var values = new double[131_072];
        values[17] = BitConverter.UInt64BitsToDouble(0x7FF8000000000123UL);
        values[80_000] = BitConverter.UInt64BitsToDouble(0xFFF8000000000456UL);

        Assert.Equal(
            0x7FF8000000000123UL,
            BitConverter.DoubleToUInt64Bits(NumpyReduction.MedianFloat64(values)));
    }

    [Fact(DisplayName = "Large float64 median handles ordered and duplicate values")]
    public void LargeFloat64MedianHandlesOrderedAndDuplicateValues()
    {
        const int length = 131_072;
        AssertMedianMatchesSortedReference(
            Enumerable.Range(0, length).Select(static value => (double)value).ToArray());
        AssertMedianMatchesSortedReference(
            Enumerable.Range(0, length).Select(static value => (double)(length - value)).ToArray());
        AssertMedianMatchesSortedReference(Enumerable.Repeat(7.25, length).ToArray());
        AssertMedianMatchesSortedReference(
            Enumerable.Range(0, length)
                .Select(static value => value % 5 == 0 ? double.PositiveInfinity : value % 11)
                .ToArray());
    }

    [Fact(DisplayName = "Large float64 median preserves sorted mixed-zero semantics")]
    public void LargeFloat64MedianPreservesSortedMixedZeroSemantics()
    {
        var values = new double[131_073];
        Array.Fill(values, -1.0, 0, values.Length / 2);
        values[values.Length / 2] = -0.0;
        values[(values.Length / 2) + 1] = 0.0;
        Array.Fill(values, 1.0, (values.Length / 2) + 2, values.Length - ((values.Length / 2) + 2));

        AssertMedianMatchesSortedReference(values);
    }

    [Theory(DisplayName = "Caller scratch float64 median remains bit exact")]
    [InlineData(31)]
    [InlineData(4_095)]
    [InlineData(4_096)]
    [InlineData(4_097)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    [InlineData(51_180)]
    public void CallerScratchFloat64MedianRemainsBitExact(int length)
    {
        var values = new double[length];
        ulong state = 0xA0761D6478BD642FUL;
        for (int index = 0; index < values.Length; index++)
        {
            state = unchecked((state * 6364136223846793005UL) + 1442695040888963407UL);
            values[index] = index % 101 == 0
                ? -3.5
                : ((long)(state >> 11) - (1L << 52)) / (double)(1L << 29);
        }

        double[] original = [.. values];
        var scratch = Enumerable.Repeat(123456.75, length + 7).ToArray();
        ulong expected = BitConverter.DoubleToUInt64Bits(NumpyReduction.MedianFloat64(values));
        ulong actual = BitConverter.DoubleToUInt64Bits(NumpyReduction.MedianFloat64(values, scratch));

        Assert.Equal(expected, actual);
        Assert.Equal(original, values);
        Assert.All(scratch.AsSpan(length).ToArray(), value => Assert.Equal(123456.75, value));
    }

    [Fact(DisplayName = "Caller scratch median preserves NaN and mixed-zero semantics")]
    public void CallerScratchMedianPreservesNanAndMixedZeroSemantics()
    {
        var nanValues = new double[40_000];
        nanValues[7] = BitConverter.UInt64BitsToDouble(0x7FF8000000000123UL);
        nanValues[30_000] = BitConverter.UInt64BitsToDouble(0xFFF8000000000456UL);
        var scratch = new double[nanValues.Length];

        Assert.Equal(
            0x7FF8000000000123UL,
            BitConverter.DoubleToUInt64Bits(NumpyReduction.MedianFloat64(nanValues, scratch)));

        double[] mixedZeros = [-1.0, -0.0, 0.0, 1.0];
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(NumpyReduction.MedianFloat64(mixedZeros)),
            BitConverter.DoubleToUInt64Bits(
                NumpyReduction.MedianFloat64(mixedZeros, scratch)));
    }

    [Fact(DisplayName = "Caller scratch median avoids warm-path allocation")]
    public void CallerScratchMedianAvoidsWarmPathAllocation()
    {
        double[] values = Enumerable.Range(0, 31).Select(static value => (double)value).ToArray();
        var scratch = new double[values.Length];
        _ = NumpyReduction.MedianFloat64(values, scratch);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 100; iteration++)
        {
            _ = NumpyReduction.MedianFloat64(values, scratch);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 256, $"Expected at most 256 allocated bytes, observed {allocated}.");
    }

    private static void AssertMedianMatchesSortedReference(double[] values)
    {
        double[] original = [.. values];
        double[] sorted = [.. values];
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        double expected = (sorted.Length & 1) == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];

        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(expected),
            BitConverter.DoubleToUInt64Bits(NumpyReduction.MedianFloat64(values)));
        Assert.Equal(original, values);
    }
}
