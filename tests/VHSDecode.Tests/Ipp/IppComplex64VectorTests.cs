using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppComplex64VectorTests
{
    [Fact(DisplayName = "IPP complex multiply matches scalar math on unaligned slices")]
    public void ComplexMultiplyMatchesScalarOnUnalignedSlices()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 4_097;
        Complex[] leftBacking = BuildValues(Length + 3, 0.017, 0.031);
        Complex[] rightBacking = BuildValues(Length + 5, 0.023, 0.013);
        var outputBacking = new Complex[Length + 7];
        ReadOnlySpan<Complex> left = leftBacking.AsSpan(1, Length);
        ReadOnlySpan<Complex> right = rightBacking.AsSpan(2, Length);
        Span<Complex> output = outputBacking.AsSpan(3, Length);
        var expected = new Complex[Length];
        for (int index = 0; index < expected.Length; index++)
        {
            expected[index] = left[index] * right[index];
        }

        IppComplex64Vector.Multiply(left, right, output);

        AssertComplexClose(expected, output, 5e-13);
    }

    [Fact(DisplayName = "IPP complex magnitude and phase match scalar math on unaligned slices")]
    public void ComplexMagnitudePhaseMatchesScalarOnUnalignedSlices()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 4_097;
        Complex[] inputBacking = BuildValues(Length + 3, 0.019, 0.027);
        var magnitudeBacking = new double[Length + 5];
        var phaseBacking = new double[Length + 7];
        ReadOnlySpan<Complex> input = inputBacking.AsSpan(1, Length);
        Span<double> magnitude = magnitudeBacking.AsSpan(2, Length);
        Span<double> phase = phaseBacking.AsSpan(3, Length);

        IppComplex64Vector.MagnitudePhase(input, magnitude, phase);

        for (int index = 0; index < input.Length; index++)
        {
            double expectedMagnitude = Complex.Abs(input[index]);
            double expectedPhase = Math.Atan2(input[index].Imaginary, input[index].Real);
            Assert.InRange(
                Math.Abs(expectedMagnitude - magnitude[index]),
                0.0,
                5e-14 * Math.Max(1.0, expectedMagnitude));
            Assert.InRange(Math.Abs(expectedPhase - phase[index]), 0.0, 5e-14);
        }

        Array.Clear(magnitudeBacking);
        Array.Clear(phaseBacking);
        IppComplex64Vector.Magnitude(input, magnitude);
        IppComplex64Vector.Phase(input, phase);
        for (int index = 0; index < input.Length; index++)
        {
            Assert.InRange(
                Math.Abs(Complex.Abs(input[index]) - magnitude[index]),
                0.0,
                5e-14 * Math.Max(1.0, Complex.Abs(input[index])));
            Assert.InRange(
                Math.Abs(Math.Atan2(input[index].Imaginary, input[index].Real) - phase[index]),
                0.0,
                5e-14);
        }
    }

    [Fact(DisplayName = "IPP complex vector functions accept IEEE special values without bit claims")]
    public void ComplexVectorFunctionsAcceptSpecialValuesAsSmokeCoverage()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double positiveNan = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0123UL);
        Complex[] left =
        [
            new(0.0, -0.0),
            new(BitConverter.Int64BitsToDouble(1), -BitConverter.Int64BitsToDouble(1)),
            new(double.PositiveInfinity, 1.0),
            new(double.NegativeInfinity, -0.0),
            new(positiveNan, 3.0),
            new(4.0, double.NaN)
        ];
        Complex[] right =
        [
            new(-0.0, 0.0),
            new(2.0, -3.0),
            new(0.0, 2.0),
            new(-4.0, 0.0),
            new(6.0, -7.0),
            new(-8.0, 9.0)
        ];
        var product = new Complex[left.Length];
        var magnitude = new double[left.Length];
        var phase = new double[left.Length];

        IppComplex64Vector.Multiply(left, right, product);
        IppComplex64Vector.MagnitudePhase(left, magnitude, phase);

        Assert.Equal(left.Length, product.Length);
        Assert.Contains(product, value => double.IsNaN(value.Real) || double.IsNaN(value.Imaginary));
        Assert.Contains(magnitude, double.IsNaN);
        Assert.Contains(phase, double.IsNaN);
        Assert.True(double.IsFinite(product[1].Real) || double.IsSubnormal(product[1].Real));
    }

    [Fact(DisplayName = "IPP complex vector wrappers validate lengths and all overlap forms")]
    public void ComplexVectorWrappersValidateLengthsAndOverlap()
    {
        Assert.Throws<ArgumentException>(
            () => IppComplex64Vector.Multiply(new Complex[2], new Complex[3], new Complex[3]));
        Assert.Throws<ArgumentException>(
            () => IppComplex64Vector.Multiply(new Complex[3], new Complex[3], new Complex[2]));

        var left = new Complex[8];
        var right = new Complex[8];
        Assert.Throws<ArgumentException>(() => IppComplex64Vector.Multiply(left, right, left));
        Assert.Throws<ArgumentException>(() => IppComplex64Vector.Multiply(left, right, right));
        Assert.Throws<ArgumentException>(
            () => IppComplex64Vector.MagnitudePhase(left, default, default));
        Assert.Throws<ArgumentException>(() => IppComplex64Vector.Magnitude(left, new double[7]));
        Assert.Throws<ArgumentException>(
            () => IppComplex64Vector.MagnitudePhase(left, new double[8], new double[7]));

        var sharedOutput = new double[16];
        Assert.Throws<ArgumentException>(
            () => IppComplex64Vector.MagnitudePhase(
                left,
                sharedOutput.AsSpan(0, 8),
                sharedOutput.AsSpan(1, 8)));
        Assert.Throws<ArgumentException>(() => CallWithComplexRealOverlap(left));

        IppComplex64Vector.Multiply([], [], []);
        IppComplex64Vector.MagnitudePhase([], default, default);
    }

    [Fact(DisplayName = "IPP complex vector functions remain deterministic under parallel calls")]
    public void ComplexVectorFunctionsRemainDeterministicInParallel()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 2_048;
        Complex[] left = BuildValues(Length, 0.017, 0.031);
        Complex[] right = BuildValues(Length, 0.023, 0.013);
        Complex[] expectedProduct = Enumerable.Range(0, Length)
            .Select(index => left[index] * right[index])
            .ToArray();

        Parallel.For(
            0,
            16,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            _ =>
            {
                var product = new Complex[Length];
                var magnitude = new double[Length];
                var phase = new double[Length];
                IppComplex64Vector.Multiply(left, right, product);
                IppComplex64Vector.MagnitudePhase(left, magnitude, phase);
                AssertComplexClose(expectedProduct, product, 5e-13);
                for (int index = 0; index < Length; index++)
                {
                    Assert.InRange(
                        Math.Abs(Complex.Abs(left[index]) - magnitude[index]),
                        0.0,
                        5e-14 * Math.Max(1.0, Complex.Abs(left[index])));
                    Assert.InRange(
                        Math.Abs(Math.Atan2(left[index].Imaginary, left[index].Real) - phase[index]),
                        0.0,
                        5e-14);
                }
            });
    }

    private static Complex[] BuildValues(int length, double realRate, double imaginaryRate)
        => Enumerable.Range(0, length)
            .Select(index => new Complex(
                Math.Sin(index * realRate) + ((index % 11) * 0.0001),
                Math.Cos(index * imaginaryRate) - ((index % 7) * 0.0002)))
            .ToArray();

    private static void CallWithComplexRealOverlap(Complex[] values)
    {
        Span<double> overlapping = MemoryMarshal.Cast<Complex, double>(values);
        IppComplex64Vector.Magnitude(values, overlapping[..values.Length]);
    }

    private static void AssertComplexClose(
        ReadOnlySpan<Complex> expected,
        ReadOnlySpan<Complex> actual,
        double relativeTolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            double tolerance = relativeTolerance * Math.Max(1.0, expected[index].Magnitude);
            Assert.InRange(Complex.Abs(expected[index] - actual[index]), 0.0, tolerance);
        }
    }
}
