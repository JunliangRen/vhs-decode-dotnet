using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscPercentileNumericsCompatibilityTests
{
    [Theory(DisplayName = "LD calibration percentiles match NumPy float32 baselines")]
    [InlineData(15.0, 257, 3UL, 0x415B338B40000000UL)]
    [InlineData(50.0, 258, 5UL, 0x415CB4AE40000000UL)]
    [InlineData(85.0, 168, 7UL, 0x415DCC1BC0000000UL)]
    public void LaserDiscCalibrationPercentilesMatchNumpyFloat32Baselines(
        double requestedPercentile,
        int length,
        ulong seed,
        ulong expectedBits)
    {
        var values = new double[length];
        ulong state = seed;
        for (int i = 0; i < values.Length; i++)
        {
            state = unchecked(
                (state * 6_364_136_223_846_793_005UL)
                + 1_442_695_040_888_963_407UL);
            values[i] = 7_000_000.0 + ((state >> 32) % 1_000_001UL);
        }

        double percentile = TbcFieldDecodePipeline.Percentile(values, requestedPercentile);

        Assert.Equal(expectedBits, BitConverter.DoubleToUInt64Bits(percentile));
    }

    [Fact(DisplayName = "LD AGC percentile uses NumPy right-weight interpolation order")]
    public void LaserDiscAgcPercentileUsesNumpyRightWeightInterpolationOrder()
    {
        double left = BitConverter.Int64BitsToDouble(0x415CF69F00000000);
        double right = BitConverter.Int64BitsToDouble(0x415FA24880000000);
        var values = new double[168];
        Array.Fill(values, left, 0, 142);
        Array.Fill(values, right, 142, values.Length - 142);

        double percentile = TbcFieldDecodePipeline.Percentile(values, 85.0);

        Assert.Equal(0x415F8019A0000000UL, BitConverter.DoubleToUInt64Bits(percentile));
    }

    [Fact(DisplayName = "LD AGC percentile propagates NumPy NaN inputs")]
    public void LaserDiscAgcPercentilePropagatesNumpyNanInputs()
    {
        double percentile = TbcFieldDecodePipeline.Percentile([1.0, 2.0, double.NaN], 85.0);

        Assert.True(double.IsNaN(percentile));
    }
}
