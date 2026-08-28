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

    [Theory(DisplayName = "VHS Rust split float32 unwrap matches narrowed double split bits")]
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
    public void VhsRustSplitFloat32UnwrapMatchesNarrowedDoubleSplitBits(int length)
    {
        (float[] real, float[] imaginary) = BuildFiniteFloatInput(length);

        AssertFloatSplitMatchesNarrowedDoubleSplit(real, imaginary, 40_000_001.25);
    }

    [Fact(DisplayName = "VHS Rust split float32 unwrap preserves special-value fallback bits")]
    public void VhsRustSplitFloat32UnwrapPreservesSpecialValueFallbackBits()
    {
        float[] real =
        [
            0.0f,
            -0.0f,
            1.0f,
            float.Epsilon,
            -float.MaxValue,
            float.PositiveInfinity,
            1.0f,
            BitConverter.UInt32BitsToSingle(0xFFC01234U),
            -3.0f,
            float.NegativeInfinity,
            BitConverter.UInt32BitsToSingle(0x7FC05678U),
            2.0f,
            -2.0f,
            0.25f,
            -0.25f,
            8.0f,
            -8.0f
        ];
        float[] imaginary =
        [
            0.0f,
            0.0f,
            -1.0f,
            -float.Epsilon,
            float.MaxValue,
            1.0f,
            float.NegativeInfinity,
            2.0f,
            BitConverter.UInt32BitsToSingle(0x7FC07654U),
            -2.0f,
            -1.0f,
            float.PositiveInfinity,
            -0.0f,
            float.Epsilon,
            -float.Epsilon,
            -4.0f,
            4.0f
        ];

        AssertFloatSplitMatchesNarrowedDoubleSplit(real, imaginary, 17_900_000.0);
    }

    [Fact(DisplayName = "VHS Rust split float32 caller buffer does not allocate after warm-up")]
    public void VhsRustSplitFloat32CallerBufferDoesNotAllocateAfterWarmUp()
    {
        (float[] real, float[] imaginary) = BuildFiniteFloatInput(32_768);
        var output = new float[real.Length];
        PortedMath.UnwrapHilbertVhsRustApproximation(real, imaginary, 40_000_000.0, output);

        long before = GC.GetAllocatedBytesForCurrentThread();
        PortedMath.UnwrapHilbertVhsRustApproximation(real, imaginary, 40_000_000.0, output);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated < 256,
            $"Warm VHS Rust split float32 unwrap allocated {allocated:N0} bytes.");
    }

    [Fact(DisplayName = "VHS Rust split float32 validates span lengths")]
    public void VhsRustSplitFloat32ValidatesSpanLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            PortedMath.UnwrapHilbertVhsRustApproximation(
                ReadOnlySpan<float>.Empty,
                ReadOnlySpan<float>.Empty,
                40_000_000.0,
                Span<float>.Empty));
        Assert.Throws<ArgumentException>(() =>
            PortedMath.UnwrapHilbertVhsRustApproximation(
                new float[2],
                new float[1],
                40_000_000.0,
                new float[2]));
        Assert.Throws<ArgumentException>(() =>
            PortedMath.UnwrapHilbertVhsRustApproximation(
                new float[2],
                new float[2],
                40_000_000.0,
                new float[1]));
    }

    private static void AssertFloatSplitMatchesNarrowedDoubleSplit(
        float[] real,
        float[] imaginary,
        double frequencyHz)
    {
        double[] realDouble = real.Select(static value => (double)value).ToArray();
        double[] imaginaryDouble = imaginary.Select(static value => (double)value).ToArray();
        double[] expectedDouble = PortedMath.UnwrapHilbertVhsRustApproximation(
            realDouble,
            imaginaryDouble,
            frequencyHz);
        var actual = Enumerable.Repeat(float.NaN, real.Length).ToArray();

        PortedMath.UnwrapHilbertVhsRustApproximation(real, imaginary, frequencyHz, actual);

        Assert.Equal(
            expectedDouble.Select(static value => BitConverter.SingleToUInt32Bits((float)value)),
            actual.Select(BitConverter.SingleToUInt32Bits));
    }

    private static (float[] Real, float[] Imaginary) BuildFiniteFloatInput(int length)
    {
        var real = new float[length];
        var imaginary = new float[length];
        for (int i = 0; i < length; i++)
        {
            float amplitude = 1.0f + (i % 17);
            real[i] = MathF.Cos(i * 0.0137f) * amplitude;
            imaginary[i] = MathF.Sin(i * 0.0179f) * (amplitude + 0.25f);
        }

        return (real, imaginary);
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

            if (i == 25)
            {
                input[i] = new Complex(double.PositiveInfinity, -2.0);
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
