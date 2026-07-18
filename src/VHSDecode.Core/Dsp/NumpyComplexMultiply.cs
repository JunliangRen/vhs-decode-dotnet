using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

internal static class NumpyComplexMultiply
{
    private static readonly Vector256<double> SubtractRealLanes =
        Vector256.Create(-0.0, 0.0, -0.0, 0.0);
    private static readonly Vector256<double> AbsoluteValueMask = Vector256.Create(
        BitConverter.UInt64BitsToDouble(0x7FFFFFFFFFFFFFFFUL));
    private static readonly Vector256<double> MaximumFinite = Vector256.Create(double.MaxValue);

    public static unsafe void Apply(
        ReadOnlySpan<Complex> left,
        ReadOnlySpan<Complex> right,
        Span<Complex> destination)
    {
        if (right.Length != left.Length || destination.Length != left.Length)
        {
            throw new ArgumentException("Complex multiply spans must have the same length.");
        }

        int index = 0;
        if (Avx.IsSupported && Fma.IsSupported)
        {
            ReadOnlySpan<double> leftValues = MemoryMarshal.Cast<Complex, double>(left);
            ReadOnlySpan<double> rightValues = MemoryMarshal.Cast<Complex, double>(right);
            Span<double> destinationValues = MemoryMarshal.Cast<Complex, double>(destination);
            fixed (double* leftPointer = leftValues)
            fixed (double* rightPointer = rightValues)
            fixed (double* destinationPointer = destinationValues)
            {
                int vectorizedEnd = left.Length - (left.Length % 2);
                for (; index < vectorizedEnd; index += 2)
                {
                    int valueIndex = index * 2;
                    Vector256<double> leftVector = Avx.LoadVector256(leftPointer + valueIndex);
                    Vector256<double> rightVector = Avx.LoadVector256(rightPointer + valueIndex);
                    Vector256<double> secondProducts = Avx.Multiply(
                        Avx.Permute(leftVector, 0b1111),
                        Avx.Permute(rightVector, 0b0101));
                    Vector256<double> result = Fma.MultiplyAdd(
                        Avx.Permute(leftVector, 0b0000),
                        rightVector,
                        Avx.Xor(secondProducts, SubtractRealLanes));
                    Vector256<double> finiteLanes = Avx.Compare(
                        Avx.And(result, AbsoluteValueMask),
                        MaximumFinite,
                        FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
                    if (Avx.MoveMask(finiteLanes) == 0b1111)
                    {
                        Avx.Store(destinationPointer + valueIndex, result);
                    }
                    else
                    {
                        destination[index] = ApplyScalar(left[index], right[index]);
                        destination[index + 1] = ApplyScalar(left[index + 1], right[index + 1]);
                    }
                }
            }
        }

        for (; index < left.Length; index++)
        {
            destination[index] = ApplyScalar(left[index], right[index]);
        }
    }

    public static void ApplyInPlace(Span<Complex> left, ReadOnlySpan<Complex> right)
        => Apply(left, right, left);

    private static Complex ApplyScalar(Complex left, Complex right)
    {
        return new Complex(
            Math.FusedMultiplyAdd(
                left.Real,
                right.Real,
                -(left.Imaginary * right.Imaginary)),
            Math.FusedMultiplyAdd(
                left.Real,
                right.Imaginary,
                left.Imaginary * right.Real));
    }
}
