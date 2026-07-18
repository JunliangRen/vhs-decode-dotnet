using System.Numerics;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class NumpyComplexMultiplyTests
{
    [Theory(DisplayName = "SIMD complex multiply matches scalar NumPy rounding")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(32_769)]
    public void SimdComplexMultiplyMatchesScalarNumpyRounding(int length)
    {
        Complex[] left = BuildValues(length, 0xD1B54A32D192ED03UL);
        Complex[] right = BuildValues(length, 0x94D049BB133111EBUL);
        Complex[] expected = ScalarMultiply(left, right);
        var actual = new Complex[length];

        NumpyComplexMultiply.Apply(left, right, actual);

        AssertComplexBitsEqual(expected, actual);
    }

    [Fact(DisplayName = "SIMD in-place complex multiply preserves scalar special values")]
    public void SimdInPlaceComplexMultiplyPreservesScalarSpecialValues()
    {
        double positiveNan = BitConverter.UInt64BitsToDouble(0x7FF8000000000123UL);
        double negativeNan = BitConverter.UInt64BitsToDouble(0xFFF8000000000456UL);
        Complex[] left =
        [
            new(0.0, -0.0),
            new(double.PositiveInfinity, 1.0),
            new(double.NegativeInfinity, -1.0),
            new(positiveNan, 3.0),
            new(4.0, negativeNan),
            new(double.MaxValue, double.Epsilon),
            new(1.0 / 3.0, -1.0 / 7.0)
        ];
        Complex[] right =
        [
            new(-0.0, 0.0),
            new(2.0, -3.0),
            new(-4.0, 5.0),
            new(6.0, -7.0),
            new(-8.0, 9.0),
            new(double.Epsilon, double.MaxValue),
            new(-11.0 / 13.0, 17.0 / 19.0)
        ];
        Complex[] expected = ScalarMultiply(left, right);

        NumpyComplexMultiply.ApplyInPlace(left, right);

        AssertComplexBitsEqual(expected, left);
    }

    [Fact(DisplayName = "SIMD complex multiply preserves special-value semantics")]
    public void SimdComplexMultiplyPreservesSpecialValueSemantics()
    {
        double[] values =
        [
            0.0,
            -0.0,
            double.Epsilon,
            -double.Epsilon,
            BitConverter.UInt64BitsToDouble(0x0010000000000000UL),
            BitConverter.UInt64BitsToDouble(0x8010000000000000UL),
            1.0,
            -1.0,
            double.MaxValue,
            -double.MaxValue,
            double.PositiveInfinity,
            double.NegativeInfinity,
            BitConverter.UInt64BitsToDouble(0x7FF8000000000123UL),
            BitConverter.UInt64BitsToDouble(0xFFF8000000000456UL)
        ];
        int length = values.Length * values.Length * values.Length * values.Length;
        var left = new Complex[length];
        var right = new Complex[length];
        int index = 0;
        foreach (double leftReal in values)
        {
            foreach (double leftImaginary in values)
            {
                foreach (double rightReal in values)
                {
                    foreach (double rightImaginary in values)
                    {
                        left[index] = new Complex(leftReal, leftImaginary);
                        right[index] = new Complex(rightReal, rightImaginary);
                        index++;
                    }
                }
            }
        }

        Complex[] expected = ScalarMultiply(left, right);
        var actual = new Complex[length];

        NumpyComplexMultiply.Apply(left, right, actual);

        AssertComplexValuesEquivalent(expected, actual);
    }

    [Fact(DisplayName = "SIMD complex multiply does not allocate")]
    public void SimdComplexMultiplyDoesNotAllocate()
    {
        Complex[] left = BuildValues(4_096, 0xD1B54A32D192ED03UL);
        Complex[] right = BuildValues(4_096, 0x94D049BB133111EBUL);
        var output = new Complex[left.Length];
        NumpyComplexMultiply.Apply(left, right, output);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 32; iteration++)
        {
            NumpyComplexMultiply.Apply(left, right, output);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }

    private static Complex[] BuildValues(int length, ulong state)
    {
        var values = new Complex[length];
        for (int index = 0; index < values.Length; index++)
        {
            state = unchecked((state * 6364136223846793005UL) + 1442695040888963407UL);
            double real = ((long)(state >> 11) - (1L << 52)) / (double)(1L << 24);
            state = unchecked((state * 6364136223846793005UL) + 1442695040888963407UL);
            double imaginary = ((long)(state >> 11) - (1L << 52)) / (double)(1L << 24);
            values[index] = new Complex(real, imaginary);
        }

        return values;
    }

    private static Complex[] ScalarMultiply(Complex[] left, Complex[] right)
    {
        var output = new Complex[left.Length];
        for (int index = 0; index < output.Length; index++)
        {
            output[index] = new Complex(
                Math.FusedMultiplyAdd(
                    left[index].Real,
                    right[index].Real,
                    -(left[index].Imaginary * right[index].Imaginary)),
                Math.FusedMultiplyAdd(
                    left[index].Real,
                    right[index].Imaginary,
                    left[index].Imaginary * right[index].Real));
        }

        return output;
    }

    private static void AssertComplexBitsEqual(Complex[] expected, Complex[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            ulong expectedReal = BitConverter.DoubleToUInt64Bits(expected[index].Real);
            ulong actualReal = BitConverter.DoubleToUInt64Bits(actual[index].Real);
            Assert.True(
                expectedReal == actualReal,
                $"Real component {index} differs: expected 0x{expectedReal:X16}, actual 0x{actualReal:X16}.");
            ulong expectedImaginary = BitConverter.DoubleToUInt64Bits(expected[index].Imaginary);
            ulong actualImaginary = BitConverter.DoubleToUInt64Bits(actual[index].Imaginary);
            Assert.True(
                expectedImaginary == actualImaginary,
                $"Imaginary component {index} differs: expected 0x{expectedImaginary:X16}, actual 0x{actualImaginary:X16}.");
        }
    }

    private static void AssertComplexValuesEquivalent(Complex[] expected, Complex[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            AssertComponentEquivalent(expected[index].Real, actual[index].Real, index, "Real");
            AssertComponentEquivalent(expected[index].Imaginary, actual[index].Imaginary, index, "Imaginary");
        }
    }

    private static void AssertComponentEquivalent(
        double expected,
        double actual,
        int index,
        string component)
    {
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual), $"{component} component {index} must remain NaN.");
            return;
        }

        ulong expectedBits = BitConverter.DoubleToUInt64Bits(expected);
        ulong actualBits = BitConverter.DoubleToUInt64Bits(actual);
        Assert.True(
            expectedBits == actualBits,
            $"{component} component {index} differs: expected 0x{expectedBits:X16}, actual 0x{actualBits:X16}.");
    }
}
