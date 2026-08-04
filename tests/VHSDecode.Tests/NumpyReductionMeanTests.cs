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
        double signalingNaN = BitConverter.UInt64BitsToDouble(0x7FF0_0000_0000_0123UL);
        double quietNaN = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0456UL);
        double negativeSignalingNaN =
            BitConverter.UInt64BitsToDouble(0xFFF0_0000_0000_0789UL);
        double halfMinimumSubnormal = Math.ScaleB(1.0, -150);
        double[] conversionBoundaries =
        [
            Math.BitDecrement(halfMinimumSubnormal),
            halfMinimumSubnormal,
            Math.BitIncrement(halfMinimumSubnormal),
            -Math.BitDecrement(halfMinimumSubnormal),
            -halfMinimumSubnormal,
            -Math.BitIncrement(halfMinimumSubnormal)
        ];

        var cases = new List<double[]>
        {
            CreateFilledValues(8, -0.0),
            CreateSparseValues(8, 3, (double)float.MaxValue),
            CreateSparseValues(8, 4, Math.BitIncrement((double)float.MaxValue)),
            CreateSparseValues(8, 5, double.MaxValue),
            CreateSparseValues(8, 7, signalingNaN),
            CreateSparseValues(9, 8, quietNaN),
            CreateSparseValues(128, 127, negativeSignalingNaN),
            CreateSparseValues(129, 128, signalingNaN),
            CreateSparseValues(8, 2, double.PositiveInfinity),
            CreateSparseValues(9, 8, double.NegativeInfinity)
        };
        foreach (double boundary in conversionBoundaries)
        {
            cases.Add(CreateFilledValues(8, boundary));
        }

        foreach (double[] values in cases)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(ScalarMeanFloat32(values)),
                BitConverter.SingleToUInt32Bits(NumpyReduction.MeanFloat32(values)));
        }
    }

    private static double[] CreateFilledValues(int length, double value)
    {
        var values = new double[length];
        Array.Fill(values, value);
        return values;
    }

    private static double[] CreateSparseValues(int length, int index, double value)
    {
        var values = new double[length];
        values[index] = value;
        return values;
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
