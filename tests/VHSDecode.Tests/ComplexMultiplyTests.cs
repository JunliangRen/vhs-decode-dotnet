using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ComplexMultiplyTests
{
    [Theory(DisplayName = "Ordinary SIMD complex multiply preserves runtime operator rounding")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1_024)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    public void PreservesRuntimeRoundingWithTailsAndOffsets(int length)
    {
        Complex[] left = BuildInput(length + 1, 0xD1B54A32D192ED03UL);
        Complex[] right = BuildInput(length + 1, 0x94D049BB133111EBUL);
        Check(left.AsSpan(1), right.AsSpan(1));
    }

    [Fact(DisplayName = "Ordinary SIMD complex multiply retains overflow and NaN recovery")]
    public void RetainsOverflowAndNaNRecovery()
    {
        double[] values =
        [
            0.0, -0.0, double.Epsilon, -double.Epsilon,
            BitConverter.UInt64BitsToDouble(0x0010000000000000UL),
            -BitConverter.UInt64BitsToDouble(0x0010000000000000UL),
            1.0, Math.BitIncrement(1.0), -1.0, Math.Sqrt(double.MaxValue),
            double.MaxValue, -double.MaxValue, double.PositiveInfinity, double.NegativeInfinity,
            BitConverter.UInt64BitsToDouble(0x7FF8000000000123UL),
            BitConverter.UInt64BitsToDouble(0xFFF8000000000456UL),
            BitConverter.UInt64BitsToDouble(0x7FF0000000000123UL),
            BitConverter.UInt64BitsToDouble(0xFFF0000000000456UL)
        ];
        Complex[] edges = values.SelectMany(real => values.Select(imaginary => new Complex(real, imaginary))).ToArray();
        var left = new Complex[edges.Length * edges.Length];
        var right = new Complex[left.Length];
        for (int index = 0; index < left.Length; index++)
        {
            left[index] = edges[index / edges.Length];
            right[index] = edges[index % edges.Length];
        }
        Check(left, right);
    }

    [Fact(DisplayName = "Ordinary complex multiply retains separate-product rounding instead of FMA")]
    public void RetainsSeparateProductRoundingInsteadOfFma()
    {
        double epsilon = Math.ScaleB(1.0, -27);
        Complex[] left = [new(1.0 + epsilon, 1.0), new(1.0 + epsilon, 1.0)];
        Complex[] right = [new(1.0 - epsilon, 1.0), new(1.0 - epsilon, 1.0)];
        var actual = new Complex[2];
        ComplexMultiply.Apply(left, right, actual);
        AssertDefinedBits(Scalar(left, right), actual);
        Assert.Equal(0.0, actual[0].Real);
        Assert.NotEqual(actual[0].Real, Math.FusedMultiplyAdd(left[0].Real, right[0].Real, -1.0));
    }

    [Fact(DisplayName = "Ordinary complex multiply supports owned and shifted aliases")]
    public void PreservesOwnedAndShiftedAliasSemantics()
    {
        const int length = 33;
        Complex[] left = BuildInput(length, 123), right = BuildInput(length, 456);
        Complex[] expected = Scalar(left, right);
        Complex[] actual = left.ToArray();
        ComplexMultiply.ApplyInPlace(actual, right);
        AssertDefinedBits(expected, actual);
        actual = right.ToArray();
        ComplexMultiply.Apply(left, actual, actual);
        AssertDefinedBits(expected, actual);

        Complex[] oldShifted = BuildInput(length + 1, 789), newShifted = oldShifted.ToArray();
        for (int index = 0; index < length; index++) oldShifted[index + 1] = oldShifted[index] * right[index];
        ComplexMultiply.Apply(newShifted.AsSpan(0, length), right, newShifted.AsSpan(1, length));
        AssertDefinedBits(oldShifted, newShifted);
    }

    private static void Check(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
    {
        Complex[] originalLeft = left.ToArray(), originalRight = right.ToArray();
        Complex guard = new(0.125, -913.5);
        var actual = Enumerable.Repeat(guard, left.Length + 2).ToArray();
        ComplexMultiply.Apply(left, right, actual.AsSpan(1, left.Length));
        AssertDefinedBits(Scalar(left, right), actual.AsSpan(1, left.Length));
        Assert.Equal(guard, actual[0]);
        Assert.Equal(guard, actual[^1]);
        Assert.True(MemoryMarshal.AsBytes(left).SequenceEqual(MemoryMarshal.AsBytes(originalLeft.AsSpan())));
        Assert.True(MemoryMarshal.AsBytes(right).SequenceEqual(MemoryMarshal.AsBytes(originalRight.AsSpan())));
    }

    private static Complex[] Scalar(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
    {
        var output = new Complex[left.Length];
        for (int index = 0; index < output.Length; index++) output[index] = left[index] * right[index];
        return output;
    }

    private static Complex[] BuildInput(int length, ulong state)
    {
        var output = new Complex[length];
        for (int index = 0; index < length; index++) output[index] = new Complex(Next(), Next());
        return output;

        double Next()
        {
            state = unchecked((state * 2862933555777941757UL) + 3037000493UL);
            return BitConverter.UInt64BitsToDouble(state);
        }
    }

    private static void AssertDefinedBits(ReadOnlySpan<Complex> expected, ReadOnlySpan<Complex> actual)
    {
        ReadOnlySpan<double> expectedValues = MemoryMarshal.Cast<Complex, double>(expected);
        ReadOnlySpan<double> actualValues = MemoryMarshal.Cast<Complex, double>(actual);
        Assert.Equal(expectedValues.Length, actualValues.Length);
        for (int index = 0; index < expectedValues.Length; index++)
        {
            if (double.IsNaN(expectedValues[index])) Assert.True(double.IsNaN(actualValues[index]));
            else Assert.Equal(BitConverter.DoubleToUInt64Bits(expectedValues[index]), BitConverter.DoubleToUInt64Bits(actualValues[index]));
        }
    }
}
