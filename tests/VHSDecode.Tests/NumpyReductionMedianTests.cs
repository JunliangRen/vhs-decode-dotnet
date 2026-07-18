using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class NumpyReductionMedianTests
{
    [Theory(DisplayName = "Large float64 median matches the sorted reference")]
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
