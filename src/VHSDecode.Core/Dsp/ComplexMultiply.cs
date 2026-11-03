using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

internal static class ComplexMultiply
{
    private static readonly Vector256<double> AbsoluteValueMask = Vector256.Create(BitConverter.UInt64BitsToDouble(0x7FFF_FFFF_FFFF_FFFFUL));
    private static readonly Vector256<double> MaximumFinite = Vector256.Create(double.MaxValue);

    internal static void ApplyInPlace(Span<Complex> values, ReadOnlySpan<Complex> multipliers)
        => Apply(values, multipliers, values);

    private static void Scalar(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right, Span<Complex> output)
    {
        for (int index = 0; index < left.Length; index++)
        {
            output[index] = left[index] * right[index];
        }
    }

    internal static unsafe void Apply(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right, Span<Complex> output)
    {
        if (left.Length != right.Length || output.Length != left.Length)
        {
            throw new ArgumentException("Complex multiply spans must have the same length.");
        }
        if ((left.Overlaps(output, out int leftOffset) && leftOffset != 0)
            || (right.Overlaps(output, out int rightOffset) && rightOffset != 0))
        {
            // Shifted aliases retain the original scalar read/write order.
            Scalar(left, right, output);
            return;
        }

        int index = 0;
        if (Avx.IsSupported)
        {
            ReadOnlySpan<double> leftComponents = MemoryMarshal.Cast<Complex, double>(left);
            ReadOnlySpan<double> rightComponents = MemoryMarshal.Cast<Complex, double>(right);
            Span<double> outputComponents = MemoryMarshal.Cast<Complex, double>(output);
            fixed (double* leftPointer = leftComponents)
            fixed (double* rightPointer = rightComponents)
            fixed (double* outputPointer = outputComponents)
            {
                int end = left.Length & ~1;
                for (; index < end; index += 2)
                {
                    int offset = index * 2;
                    Vector256<double> l = Avx.LoadVector256(leftPointer + offset);
                    Vector256<double> r = Avx.LoadVector256(rightPointer + offset);
                    Vector256<double> first = Avx.Multiply(l, Avx.Permute(r, 0x0));
                    Vector256<double> second = Avx.Multiply(Avx.Permute(l, 0x5), Avx.Permute(r, 0xf));
                    // Real lanes: ar*br - ai*bi; imaginary lanes: ai*br + ar*bi.
                    // Separate products preserve the actual runtime operator's order
                    // and non-FMA rounding. Exceptional results use its NaN recovery.
                    Vector256<double> result = Avx.AddSubtract(first, second);
                    Vector256<double> finite = Avx.Compare(Avx.And(result, AbsoluteValueMask), MaximumFinite,
                        FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
                    if (Avx.MoveMask(finite) == 0xf)
                    {
                        Avx.Store(outputPointer + offset, result);
                    }
                    else
                    {
                        output[index] = left[index] * right[index];
                        output[index + 1] = left[index + 1] * right[index + 1];
                    }
                }
            }
        }
        for (; index < left.Length; index++)
        {
            output[index] = left[index] * right[index];
        }
    }
}
