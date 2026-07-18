using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class RfFloat32QuantizationTests
{
    [Fact(DisplayName = "RF float32 quantization remains bit-exact for arbitrary lengths")]
    public void RfFloat32QuantizationRemainsBitExactForArbitraryLengths()
    {
        foreach (int length in new[] { 0, 1, 3, 4, 5, 31, 32, 33, 32_768 })
        {
            var values = new double[length];
            ulong state = 0x9E3779B97F4A7C15UL;
            for (int index = 0; index < values.Length; index++)
            {
                state = unchecked((state * 2862933555777941757UL) + 3037000493UL);
                values[index] = ((long)(state >> 11) - (1L << 52)) / 65_536.0;
            }

            AssertMatchesScalarQuantization(values);
        }
    }

    [Fact(DisplayName = "RF float32 quantization preserves scalar special-value semantics")]
    public void RfFloat32QuantizationPreservesScalarSpecialValueSemantics()
    {
        double positiveNan = BitConverter.UInt64BitsToDouble(0x7FF8000000000123UL);
        double negativeNan = BitConverter.UInt64BitsToDouble(0xFFF8000000000456UL);
        AssertMatchesScalarQuantization(
        [
            0.0,
            -0.0,
            double.Epsilon,
            -double.Epsilon,
            float.MaxValue,
            -float.MaxValue,
            double.MaxValue,
            double.MinValue,
            double.PositiveInfinity,
            double.NegativeInfinity,
            positiveNan,
            negativeNan,
            1.0 / 3.0
        ]);
    }

    private static void AssertMatchesScalarQuantization(double[] values)
    {
        double[] expected = values.Select(static value => (double)(float)value).ToArray();

        RfBlockDecodePipeline.QuantizeToFloat32InPlace(values);

        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            values.Select(BitConverter.DoubleToUInt64Bits));
    }
}
