using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class NumpyReductionMeanTests
{
    [Theory(DisplayName = "Float32 mean preserves NumPy pairwise summation order")]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1_003)]
    [InlineData(65_537)]
    public void Float32MeanPreservesNumpyPairwiseSummationOrder(int length)
    {
        var values = new double[length];
        ulong state = 0xD1B54A32D192ED03UL;
        for (int index = 0; index < values.Length; index++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            float magnitude = (float)((state * 0x2545F4914F6CDD1DUL) >> 40);
            float signed = (index & 3) switch
            {
                0 => magnitude,
                1 => -magnitude,
                2 => magnitude * 0.000_001f,
                _ => -magnitude * 0.000_001f
            };
            values[index] = signed + ((index % 11) * 0.0625);
        }

        float expected = ScalarMeanFloat32(values);
        float actual = NumpyReduction.MeanFloat32(values);

        Assert.Equal(
            BitConverter.SingleToUInt32Bits(expected),
            BitConverter.SingleToUInt32Bits(actual));
    }

    [Fact(DisplayName = "Float32 mean preserves exceptional IEEE values")]
    public void Float32MeanPreservesExceptionalIeeeValues()
    {
        double[] values =
        [
            -0.0,
            0.0,
            double.Epsilon,
            -double.Epsilon,
            float.Epsilon,
            -float.Epsilon,
            float.MaxValue,
            -float.MaxValue,
            double.PositiveInfinity,
            double.NegativeInfinity,
            BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0123UL),
            BitConverter.UInt64BitsToDouble(0xFFF8_0000_0000_0456UL),
            1.0,
            -1.0,
            0.5,
            -0.5
        ];

        Assert.Equal(
            BitConverter.SingleToUInt32Bits(ScalarMeanFloat32(values)),
            BitConverter.SingleToUInt32Bits(NumpyReduction.MeanFloat32(values)));
    }

    private static float ScalarMeanFloat32(ReadOnlySpan<double> values)
        => ScalarPairwiseSumFloat32(values) / values.Length;

    private static float ScalarPairwiseSumFloat32(ReadOnlySpan<double> values)
    {
        const int pairwiseBlockSize = 128;
        if (values.Length < 8)
        {
            float scalarSum = -0.0f;
            for (int scalarIndex = 0; scalarIndex < values.Length; scalarIndex++)
            {
                scalarSum += (float)values[scalarIndex];
            }

            return scalarSum;
        }

        if (values.Length > pairwiseBlockSize)
        {
            int split = values.Length / 2;
            split -= split % 8;
            return ScalarPairwiseSumFloat32(values[..split])
                + ScalarPairwiseSumFloat32(values[split..]);
        }

        float sum0 = (float)values[0];
        float sum1 = (float)values[1];
        float sum2 = (float)values[2];
        float sum3 = (float)values[3];
        float sum4 = (float)values[4];
        float sum5 = (float)values[5];
        float sum6 = (float)values[6];
        float sum7 = (float)values[7];
        int index = 8;
        int vectorizedEnd = values.Length - (values.Length % 8);
        for (; index < vectorizedEnd; index += 8)
        {
            sum0 += (float)values[index];
            sum1 += (float)values[index + 1];
            sum2 += (float)values[index + 2];
            sum3 += (float)values[index + 3];
            sum4 += (float)values[index + 4];
            sum5 += (float)values[index + 5];
            sum6 += (float)values[index + 6];
            sum7 += (float)values[index + 7];
        }

        float combinedSum = ((sum0 + sum1) + (sum2 + sum3))
            + ((sum4 + sum5) + (sum6 + sum7));
        for (; index < values.Length; index++)
        {
            combinedSum += (float)values[index];
        }

        return combinedSum;
    }
}
