using System.Numerics;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsRustUnwrapSimdTests
{
    [Theory(DisplayName = "VHS Rust unwrap SIMD remains bit-exact")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(32_768)]
    public void VhsRustUnwrapSimdRemainsBitExact(int length)
    {
        Complex[] input = BuildInput(length);
        double[] expected = PortedMath.UnwrapHilbertVhsRustApproximationScalar(input, 40_000_000.0);

        double[] actual = PortedMath.UnwrapHilbertVhsRustApproximation(input, 40_000_000.0);

        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            actual.Select(BitConverter.DoubleToUInt64Bits));
    }

    private static Complex[] BuildInput(int length)
    {
        Complex[] edgeValues =
        [
            Complex.Zero,
            new(-0.0, 0.0),
            new(1.0, -1.0),
            new(double.Epsilon, -double.Epsilon),
            new(-double.MaxValue, double.MaxValue),
            new(double.PositiveInfinity, 1.0),
            new(1.0, double.NegativeInfinity),
            new(BitConverter.UInt64BitsToDouble(0xFFF8000000001234UL), 2.0),
            new(-3.0, BitConverter.UInt64BitsToDouble(0x7FF8000000005678UL))
        ];
        var input = new Complex[length];
        for (int i = 0; i < input.Length; i++)
        {
            if (i < edgeValues.Length)
            {
                input[i] = edgeValues[i];
                continue;
            }

            double amplitude = 1.0 + (i % 17);
            input[i] = new Complex(
                Math.Cos(i * 0.0137) * amplitude,
                Math.Sin(i * 0.0179) * (amplitude + 0.25));
        }

        return input;
    }
}
