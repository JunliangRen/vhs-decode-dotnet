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
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(32_768)]
    public void VhsRustUnwrapSimdRemainsBitExact(int length)
    {
        Complex[] input = BuildInput(length);
        double[] expected = PortedMath.UnwrapHilbertVhsRustApproximationScalar(input, 40_000_000.0);

        double[] actual = PortedMath.UnwrapHilbertVhsRustApproximation(input, 40_000_000.0);
        double[] complexBuffered = Enumerable.Repeat(double.NaN, length).ToArray();
        PortedMath.UnwrapHilbertVhsRustApproximation(
            input,
            40_000_000.0,
            complexBuffered);
        double[] split = PortedMath.UnwrapHilbertVhsRustApproximation(
            input.Select(value => value.Real).ToArray(),
            input.Select(value => value.Imaginary).ToArray(),
            40_000_000.0);
        double[] buffered = Enumerable.Repeat(double.NaN, length).ToArray();
        PortedMath.UnwrapHilbertVhsRustApproximation(
            input.Select(value => value.Real).ToArray(),
            input.Select(value => value.Imaginary).ToArray(),
            40_000_000.0,
            buffered);

        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            actual.Select(BitConverter.DoubleToUInt64Bits));
        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            complexBuffered.Select(BitConverter.DoubleToUInt64Bits));
        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            split.Select(BitConverter.DoubleToUInt64Bits));
        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            buffered.Select(BitConverter.DoubleToUInt64Bits));
    }

    [Fact(DisplayName = "VHS Rust unwrap caller buffer does not allocate after warm-up")]
    public void VhsRustUnwrapCallerBufferDoesNotAllocateAfterWarmUp()
    {
        Complex[] input = BuildInput(32_768);
        double[] real = input.Select(value => value.Real).ToArray();
        double[] imaginary = input.Select(value => value.Imaginary).ToArray();
        var output = new double[input.Length];
        PortedMath.UnwrapHilbertVhsRustApproximation(
            input,
            40_000_000.0,
            output);
        PortedMath.UnwrapHilbertVhsRustApproximation(
            real,
            imaginary,
            40_000_000.0,
            output);

        long before = GC.GetAllocatedBytesForCurrentThread();
        PortedMath.UnwrapHilbertVhsRustApproximation(
            input,
            40_000_000.0,
            output);
        PortedMath.UnwrapHilbertVhsRustApproximation(
            real,
            imaginary,
            40_000_000.0,
            output);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated < 256,
            $"Warm VHS Rust unwrap caller-buffer path allocated {allocated:N0} bytes.");
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
