using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

internal static class NumpyComplexMultiply
{
    private const byte DuplicateFirstTwoLanes = 0b0101_0000;
    private const byte DuplicateLastTwoLanes = 0b1111_1010;
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

    public static unsafe void ApplyTwoComplexAndRealInPlace(
        Span<Complex> values,
        ReadOnlySpan<Complex> firstComplexMultipliers,
        ReadOnlySpan<Complex> secondComplexMultipliers,
        ReadOnlySpan<double> realMultipliers)
    {
        if (firstComplexMultipliers.Length != values.Length
            || secondComplexMultipliers.Length != values.Length
            || realMultipliers.Length != values.Length)
        {
            throw new ArgumentException(
                "Complex values and all multiplier spans must have the same length.");
        }

        if (values.Overlaps(firstComplexMultipliers)
            || values.Overlaps(secondComplexMultipliers)
            || MemoryMarshal.AsBytes(values).Overlaps(MemoryMarshal.AsBytes(realMultipliers)))
        {
            ApplyInPlace(values, firstComplexMultipliers);
            ApplyInPlace(values, secondComplexMultipliers);
            ApplyRealInPlace(values, realMultipliers);
            return;
        }

        int index = 0;
        if (Avx.IsSupported && Fma.IsSupported)
        {
            Span<double> valueComponents = MemoryMarshal.Cast<Complex, double>(values);
            ReadOnlySpan<double> firstComponents =
                MemoryMarshal.Cast<Complex, double>(firstComplexMultipliers);
            ReadOnlySpan<double> secondComponents =
                MemoryMarshal.Cast<Complex, double>(secondComplexMultipliers);
            fixed (double* valuePointer = valueComponents)
            fixed (double* firstPointer = firstComponents)
            fixed (double* secondPointer = secondComponents)
            {
                int vectorizedEnd = values.Length - (values.Length % 2);
                for (; index < vectorizedEnd; index += 2)
                {
                    int componentIndex = index * 2;
                    Vector256<double> valueVector = Avx.LoadVector256(valuePointer + componentIndex);
                    Vector256<double> firstVector = Avx.LoadVector256(firstPointer + componentIndex);
                    Vector256<double> firstProducts = Avx.Multiply(
                        Avx.Permute(valueVector, 0b1111),
                        Avx.Permute(firstVector, 0b0101));
                    Vector256<double> firstResult = Fma.MultiplyAdd(
                        Avx.Permute(valueVector, 0b0000),
                        firstVector,
                        Avx.Xor(firstProducts, SubtractRealLanes));
                    Vector256<double> firstFinite = Avx.Compare(
                        Avx.And(firstResult, AbsoluteValueMask),
                        MaximumFinite,
                        FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
                    if (Avx.MoveMask(firstFinite) != 0b1111)
                    {
                        ApplyThreeScalarMultipliers(
                            values,
                            firstComplexMultipliers,
                            secondComplexMultipliers,
                            realMultipliers,
                            index);
                        continue;
                    }

                    Vector256<double> secondVector = Avx.LoadVector256(secondPointer + componentIndex);
                    Vector256<double> secondProducts = Avx.Multiply(
                        Avx.Permute(firstResult, 0b1111),
                        Avx.Permute(secondVector, 0b0101));
                    Vector256<double> secondResult = Fma.MultiplyAdd(
                        Avx.Permute(firstResult, 0b0000),
                        secondVector,
                        Avx.Xor(secondProducts, SubtractRealLanes));
                    Vector256<double> secondFinite = Avx.Compare(
                        Avx.And(secondResult, AbsoluteValueMask),
                        MaximumFinite,
                        FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
                    if (Avx.MoveMask(secondFinite) != 0b1111)
                    {
                        ApplyThreeScalarMultipliers(
                            values,
                            firstComplexMultipliers,
                            secondComplexMultipliers,
                            realMultipliers,
                            index);
                        continue;
                    }

                    Vector256<double> realVector = Vector256.Create(
                        realMultipliers[index],
                        realMultipliers[index],
                        realMultipliers[index + 1],
                        realMultipliers[index + 1]);
                    Vector256<double> realFinite = Avx.Compare(
                        Avx.And(realVector, AbsoluteValueMask),
                        MaximumFinite,
                        FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
                    if (Avx.MoveMask(realFinite) == 0b1111)
                    {
                        Avx.Store(
                            valuePointer + componentIndex,
                            Avx.Multiply(secondResult, realVector));
                    }
                    else
                    {
                        ApplyThreeScalarMultipliers(
                            values,
                            firstComplexMultipliers,
                            secondComplexMultipliers,
                            realMultipliers,
                            index);
                    }
                }
            }
        }

        for (; index < values.Length; index++)
        {
            Complex value = ApplyScalar(values[index], firstComplexMultipliers[index]);
            value = ApplyScalar(value, secondComplexMultipliers[index]);
            values[index] = value * realMultipliers[index];
        }
    }

    public static void ApplyRealInPlace(
        Span<Complex> values,
        ReadOnlySpan<double> multipliers)
    {
        if (multipliers.Length != values.Length)
        {
            throw new ArgumentException(
                "Complex values and real multipliers must have the same length.");
        }

        int index = 0;
        if (Avx2.IsSupported)
        {
            Span<double> components = MemoryMarshal.Cast<Complex, double>(values);
            ref double componentReference = ref MemoryMarshal.GetReference(components);
            ref double multiplierReference = ref MemoryMarshal.GetReference(multipliers);
            int vectorizedEnd = values.Length - (values.Length % 4);
            for (; index < vectorizedEnd; index += 4)
            {
                Vector256<double> multiplier = Vector256.LoadUnsafe(
                    ref multiplierReference,
                    (nuint)index);
                Vector256<double> lowerMultipliers = Avx2.Permute4x64(
                    multiplier,
                    DuplicateFirstTwoLanes);
                Vector256<double> upperMultipliers = Avx2.Permute4x64(
                    multiplier,
                    DuplicateLastTwoLanes);
                nuint componentIndex = (nuint)(index * 2);
                Vector256<double> lowerComponents = Vector256.LoadUnsafe(
                    ref componentReference,
                    componentIndex);
                Vector256<double> upperComponents = Vector256.LoadUnsafe(
                    ref componentReference,
                    componentIndex + 4);
                Vector256<double> lowerFinite = Avx.Compare(
                    Avx.And(lowerComponents, AbsoluteValueMask),
                    MaximumFinite,
                    FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
                Vector256<double> upperFinite = Avx.Compare(
                    Avx.And(upperComponents, AbsoluteValueMask),
                    MaximumFinite,
                    FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
                Vector256<double> multiplierFinite = Avx.Compare(
                    Avx.And(multiplier, AbsoluteValueMask),
                    MaximumFinite,
                    FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
                if (Avx.MoveMask(multiplierFinite) == 0b1111
                    && Avx.MoveMask(lowerFinite) == 0b1111
                    && Avx.MoveMask(upperFinite) == 0b1111)
                {
                    Avx.Multiply(lowerComponents, lowerMultipliers)
                        .StoreUnsafe(ref componentReference, componentIndex);
                    Avx.Multiply(upperComponents, upperMultipliers)
                        .StoreUnsafe(ref componentReference, componentIndex + 4);
                }
                else
                {
                    for (int scalar = index; scalar < index + 4; scalar++)
                    {
                        values[scalar] *= multipliers[scalar];
                    }
                }
            }
        }

        for (; index < values.Length; index++)
        {
            values[index] *= multipliers[index];
        }
    }

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

    private static void ApplyThreeScalarMultipliers(
        Span<Complex> values,
        ReadOnlySpan<Complex> firstComplexMultipliers,
        ReadOnlySpan<Complex> secondComplexMultipliers,
        ReadOnlySpan<double> realMultipliers,
        int index)
    {
        for (int scalar = index; scalar < index + 2; scalar++)
        {
            Complex value = ApplyScalar(values[scalar], firstComplexMultipliers[scalar]);
            value = ApplyScalar(value, secondComplexMultipliers[scalar]);
            values[scalar] = value * realMultipliers[scalar];
        }
    }
}
