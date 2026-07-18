using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class RfEnvelopeSimdTests
{
    [Theory(DisplayName = "VHS RF envelope preparation remains bit-exact")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(32_768)]
    public void VhsRfEnvelopePreparationRemainsBitExact(int length)
    {
        double[] input = BuildInput(length);
        double[] expected = BuildScalarReference(input);

        double[] actual = RfDemodulator.BuildVhsRawEnvelope(input);

        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            actual.Select(BitConverter.DoubleToUInt64Bits));
    }

    private static double[] BuildInput(int length)
    {
        double[] edgeValues =
        [
            0.0,
            -0.0,
            1.0,
            -1.0,
            double.Epsilon,
            -double.Epsilon,
            double.PositiveInfinity,
            double.NegativeInfinity,
            BitConverter.UInt64BitsToDouble(0xFFF8000000001234UL),
            12345.6789012345,
            -98765.4321098765
        ];
        var input = new double[length];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = i < edgeValues.Length
                ? edgeValues[i]
                : Math.Sin(i * 0.0031) * (1.0 + (i % 17));
        }

        return input;
    }

    private static double[] BuildScalarReference(ReadOnlySpan<double> input)
    {
        var output = new double[input.Length];
        int shift = 4 % output.Length;
        int split = output.Length - shift;
        for (int i = 0; i < split; i++)
        {
            output[i + shift] = MathF.Abs((float)input[i]);
        }

        for (int i = split; i < output.Length; i++)
        {
            output[i - split] = MathF.Abs((float)input[i]);
        }

        return output;
    }
}
