using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Tbc;

/// <summary>
/// Experimental four-tap Catmull-Rom sampler for the lossy Approx contract.
/// It is selected only by the explicit Approx Catmull-Rom resampling contracts.
/// </summary>
internal static class ApproxCatmullRom4Resampler
{
    internal const int TapCount = 4;
    private const int VectorWidth = 8;

    internal enum ExecutionMode
    {
        Auto,
        ForceScalar,
        ForceVector
    }

    internal static bool IsVectorizationSupported
        => Avx.IsSupported && Avx2.IsSupported && Fma.IsSupported;

    private static readonly Vector256<float> FloatAbsoluteValueMask = Vector256.Create(
        BitConverter.UInt32BitsToSingle(0x7FFF_FFFFu));
    private static readonly Vector256<float> FloatMaximumFinite = Vector256.Create(float.MaxValue);

    /// <summary>
    /// Samples one source position using tape-decode-rs-compatible scalar f32 arithmetic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Sample(ReadOnlySpan<double> source, double sourcePosition)
    {
        ValidateSource(source);
        ref double sourceReference = ref MemoryMarshal.GetReference(source);
        return SampleUnchecked(ref sourceReference, source.Length - 3, sourcePosition);
    }

    /// <summary>
    /// Samples one source position directly from an f32 source without widening it first.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Sample(ReadOnlySpan<float> source, double sourcePosition)
    {
        ValidateSource(source);
        ref float sourceReference = ref MemoryMarshal.GetReference(source);
        return SampleUnchecked(ref sourceReference, source.Length - 3, sourcePosition);
    }

    /// <summary>
    /// Resamples prepared positions and level adjustments into an f32 destination.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Resample(
        ReadOnlySpan<double> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        Span<float> destination)
        => Resample(
            source,
            sourcePositions,
            levelAdjusts,
            sourcePositionShift: 0.0,
            destination);

    /// <summary>
    /// Resamples an f32 source directly into an f32 destination.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Resample(
        ReadOnlySpan<float> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        Span<float> destination)
        => Resample(
            source,
            sourcePositions,
            levelAdjusts,
            sourcePositionShift: 0.0,
            destination);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Resample(
        ReadOnlySpan<double> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        double sourcePositionShift,
        Span<float> destination,
        ExecutionMode executionMode = ExecutionMode.Auto)
    {
        ValidateBatch(source, sourcePositions, levelAdjusts, destination.Length);
        ValidateSourcePositionShift(sourcePositionShift);

        ref double sourceReference = ref MemoryMarshal.GetReference(source);
        ref double positionReference = ref MemoryMarshal.GetReference(sourcePositions);
        ref double levelReference = ref MemoryMarshal.GetReference(levelAdjusts);
        ref float destinationReference = ref MemoryMarshal.GetReference(destination);
        double maximumPosition = source.Length - 3;

        int index = 0;
        if (ShouldUseVector(executionMode, destination.Length) &&
            !DestinationOverlapsInputs(source, sourcePositions, levelAdjusts, destination))
        {
            unsafe
            {
                fixed (double* sourcePointer = source)
                {
                    for (; index <= destination.Length - VectorWidth; index += VectorWidth)
                    {
                        if (TrySampleVector(
                                sourcePointer,
                                ref positionReference,
                                ref levelReference,
                                index,
                                sourcePositionShift,
                                maximumPosition,
                                out Vector256<float> samples))
                        {
                            samples.StoreUnsafe(ref destinationReference, (nuint)index);
                            continue;
                        }

                        for (int lane = 0; lane < VectorWidth; lane++)
                        {
                            int scalarIndex = index + lane;
                            Unsafe.Add(ref destinationReference, scalarIndex) = SampleAdjustedUnchecked(
                                ref sourceReference,
                                maximumPosition,
                                Unsafe.Add(ref positionReference, scalarIndex) + sourcePositionShift,
                                Unsafe.Add(ref levelReference, scalarIndex));
                        }
                    }
                }
            }
        }

        for (; index < destination.Length; index++)
        {
            Unsafe.Add(ref destinationReference, index) = SampleAdjustedUnchecked(
                ref sourceReference,
                maximumPosition,
                Unsafe.Add(ref positionReference, index) + sourcePositionShift,
                Unsafe.Add(ref levelReference, index));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Resample(
        ReadOnlySpan<float> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        double sourcePositionShift,
        Span<float> destination,
        ExecutionMode executionMode = ExecutionMode.Auto)
    {
        ValidateBatch(source, sourcePositions, levelAdjusts, destination.Length);
        ValidateSourcePositionShift(sourcePositionShift);

        ref float sourceReference = ref MemoryMarshal.GetReference(source);
        ref double positionReference = ref MemoryMarshal.GetReference(sourcePositions);
        ref double levelReference = ref MemoryMarshal.GetReference(levelAdjusts);
        ref float destinationReference = ref MemoryMarshal.GetReference(destination);
        double maximumPosition = source.Length - 3;

        int index = 0;
        if (ShouldUseVector(executionMode, destination.Length) &&
            !DestinationOverlapsInputs(source, sourcePositions, levelAdjusts, destination))
        {
            unsafe
            {
                fixed (float* sourcePointer = source)
                {
                    for (; index <= destination.Length - VectorWidth; index += VectorWidth)
                    {
                        if (TrySampleVector(
                                sourcePointer,
                                ref positionReference,
                                ref levelReference,
                                index,
                                sourcePositionShift,
                                maximumPosition,
                                out Vector256<float> samples))
                        {
                            samples.StoreUnsafe(ref destinationReference, (nuint)index);
                            continue;
                        }

                        for (int lane = 0; lane < VectorWidth; lane++)
                        {
                            int scalarIndex = index + lane;
                            Unsafe.Add(ref destinationReference, scalarIndex) = SampleAdjustedUnchecked(
                                ref sourceReference,
                                maximumPosition,
                                Unsafe.Add(ref positionReference, scalarIndex) + sourcePositionShift,
                                Unsafe.Add(ref levelReference, scalarIndex));
                        }
                    }
                }
            }
        }

        for (; index < destination.Length; index++)
        {
            Unsafe.Add(ref destinationReference, index) = SampleAdjustedUnchecked(
                ref sourceReference,
                maximumPosition,
                Unsafe.Add(ref positionReference, index) + sourcePositionShift,
                Unsafe.Add(ref levelReference, index));
        }
    }

    /// <summary>
    /// Resamples prepared positions and level adjustments into the current double TBC buffer shape.
    /// Each stored value is nevertheless the exact widening of the f32 Approx result.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Resample(
        ReadOnlySpan<double> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        Span<double> destination)
        => Resample(
            source,
            sourcePositions,
            levelAdjusts,
            sourcePositionShift: 0.0,
            destination);

    /// <summary>
    /// Resamples an f32 source into the current double TBC buffer shape.
    /// Each stored value is the exact widening of the f32 Approx result.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Resample(
        ReadOnlySpan<float> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        Span<double> destination)
        => Resample(
            source,
            sourcePositions,
            levelAdjusts,
            sourcePositionShift: 0.0,
            destination);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Resample(
        ReadOnlySpan<double> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        double sourcePositionShift,
        Span<double> destination,
        ExecutionMode executionMode = ExecutionMode.Auto)
    {
        ValidateBatch(source, sourcePositions, levelAdjusts, destination.Length);
        ValidateSourcePositionShift(sourcePositionShift);

        ref double sourceReference = ref MemoryMarshal.GetReference(source);
        ref double positionReference = ref MemoryMarshal.GetReference(sourcePositions);
        ref double levelReference = ref MemoryMarshal.GetReference(levelAdjusts);
        ref double destinationReference = ref MemoryMarshal.GetReference(destination);
        double maximumPosition = source.Length - 3;

        int index = 0;
        if (ShouldUseVector(executionMode, destination.Length) &&
            !DestinationOverlapsInputs(source, sourcePositions, levelAdjusts, destination))
        {
            unsafe
            {
                fixed (double* sourcePointer = source)
                {
                    for (; index <= destination.Length - VectorWidth; index += VectorWidth)
                    {
                        if (TrySampleVector(
                                sourcePointer,
                                ref positionReference,
                                ref levelReference,
                                index,
                                sourcePositionShift,
                                maximumPosition,
                                out Vector256<float> samples))
                        {
                            Avx.ConvertToVector256Double(samples.GetLower())
                                .StoreUnsafe(ref destinationReference, (nuint)index);
                            Avx.ConvertToVector256Double(samples.GetUpper())
                                .StoreUnsafe(ref destinationReference, (nuint)(index + 4));
                            continue;
                        }

                        for (int lane = 0; lane < VectorWidth; lane++)
                        {
                            int scalarIndex = index + lane;
                            Unsafe.Add(ref destinationReference, scalarIndex) = SampleAdjustedUnchecked(
                                ref sourceReference,
                                maximumPosition,
                                Unsafe.Add(ref positionReference, scalarIndex) + sourcePositionShift,
                                Unsafe.Add(ref levelReference, scalarIndex));
                        }
                    }
                }
            }
        }

        for (; index < destination.Length; index++)
        {
            Unsafe.Add(ref destinationReference, index) = SampleAdjustedUnchecked(
                ref sourceReference,
                maximumPosition,
                Unsafe.Add(ref positionReference, index) + sourcePositionShift,
                Unsafe.Add(ref levelReference, index));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Resample(
        ReadOnlySpan<float> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        double sourcePositionShift,
        Span<double> destination,
        ExecutionMode executionMode = ExecutionMode.Auto)
    {
        ValidateBatch(source, sourcePositions, levelAdjusts, destination.Length);
        ValidateSourcePositionShift(sourcePositionShift);

        ref float sourceReference = ref MemoryMarshal.GetReference(source);
        ref double positionReference = ref MemoryMarshal.GetReference(sourcePositions);
        ref double levelReference = ref MemoryMarshal.GetReference(levelAdjusts);
        ref double destinationReference = ref MemoryMarshal.GetReference(destination);
        double maximumPosition = source.Length - 3;

        int index = 0;
        if (ShouldUseVector(executionMode, destination.Length) &&
            !DestinationOverlapsInputs(source, sourcePositions, levelAdjusts, destination))
        {
            unsafe
            {
                fixed (float* sourcePointer = source)
                {
                    for (; index <= destination.Length - VectorWidth; index += VectorWidth)
                    {
                        if (TrySampleVector(
                                sourcePointer,
                                ref positionReference,
                                ref levelReference,
                                index,
                                sourcePositionShift,
                                maximumPosition,
                                out Vector256<float> samples))
                        {
                            Avx.ConvertToVector256Double(samples.GetLower())
                                .StoreUnsafe(ref destinationReference, (nuint)index);
                            Avx.ConvertToVector256Double(samples.GetUpper())
                                .StoreUnsafe(ref destinationReference, (nuint)(index + 4));
                            continue;
                        }

                        for (int lane = 0; lane < VectorWidth; lane++)
                        {
                            int scalarIndex = index + lane;
                            Unsafe.Add(ref destinationReference, scalarIndex) = SampleAdjustedUnchecked(
                                ref sourceReference,
                                maximumPosition,
                                Unsafe.Add(ref positionReference, scalarIndex) + sourcePositionShift,
                                Unsafe.Add(ref levelReference, scalarIndex));
                        }
                    }
                }
            }
        }

        for (; index < destination.Length; index++)
        {
            Unsafe.Add(ref destinationReference, index) = SampleAdjustedUnchecked(
                ref sourceReference,
                maximumPosition,
                Unsafe.Add(ref positionReference, index) + sourcePositionShift,
                Unsafe.Add(ref levelReference, index));
        }
    }

    /// <summary>
    /// Resamples directly into the existing TBC UInt16 output conversion contract.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ResampleToUInt16(
        ReadOnlySpan<double> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        VideoOutputConverter converter,
        Span<ushort> destination)
        => ResampleToUInt16(
            source,
            sourcePositions,
            levelAdjusts,
            sourcePositionShift: 0.0,
            converter,
            destination);

    /// <summary>
    /// Resamples an f32 source directly into the existing TBC UInt16 output conversion contract.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ResampleToUInt16(
        ReadOnlySpan<float> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        VideoOutputConverter converter,
        Span<ushort> destination)
        => ResampleToUInt16(
            source,
            sourcePositions,
            levelAdjusts,
            sourcePositionShift: 0.0,
            converter,
            destination);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ResampleToUInt16(
        ReadOnlySpan<double> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        double sourcePositionShift,
        VideoOutputConverter converter,
        Span<ushort> destination,
        ExecutionMode executionMode = ExecutionMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ValidateBatch(source, sourcePositions, levelAdjusts, destination.Length);
        ValidateSourcePositionShift(sourcePositionShift);

        VideoOutputConverter.FastMathConversion conversion = converter.CreateFastMathConversion();
        ref double sourceReference = ref MemoryMarshal.GetReference(source);
        ref double positionReference = ref MemoryMarshal.GetReference(sourcePositions);
        ref double levelReference = ref MemoryMarshal.GetReference(levelAdjusts);
        ref ushort destinationReference = ref MemoryMarshal.GetReference(destination);
        double maximumPosition = source.Length - 3;

        int index = 0;
        if (ShouldUseVector(executionMode, destination.Length) &&
            !DestinationOverlapsInputs(source, sourcePositions, levelAdjusts, destination))
        {
            Span<float> vectorBuffer = stackalloc float[VectorWidth];
            ref float vectorBufferReference = ref MemoryMarshal.GetReference(vectorBuffer);
            unsafe
            {
                fixed (double* sourcePointer = source)
                {
                    for (; index <= destination.Length - VectorWidth; index += VectorWidth)
                    {
                        if (TrySampleVector(
                                sourcePointer,
                                ref positionReference,
                                ref levelReference,
                                index,
                                sourcePositionShift,
                                maximumPosition,
                                out Vector256<float> samples))
                        {
                            samples.StoreUnsafe(ref vectorBufferReference);
                            for (int lane = 0; lane < VectorWidth; lane++)
                            {
                                Unsafe.Add(ref destinationReference, index + lane) = conversion.Convert(
                                    Unsafe.Add(ref vectorBufferReference, lane));
                            }

                            continue;
                        }

                        for (int lane = 0; lane < VectorWidth; lane++)
                        {
                            int scalarIndex = index + lane;
                            float sample = SampleAdjustedUnchecked(
                                ref sourceReference,
                                maximumPosition,
                                Unsafe.Add(ref positionReference, scalarIndex) + sourcePositionShift,
                                Unsafe.Add(ref levelReference, scalarIndex));
                            Unsafe.Add(ref destinationReference, scalarIndex) = conversion.Convert(sample);
                        }
                    }
                }
            }
        }

        for (; index < destination.Length; index++)
        {
            float sample = SampleAdjustedUnchecked(
                ref sourceReference,
                maximumPosition,
                Unsafe.Add(ref positionReference, index) + sourcePositionShift,
                Unsafe.Add(ref levelReference, index));
            Unsafe.Add(ref destinationReference, index) = conversion.Convert(sample);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ResampleToUInt16(
        ReadOnlySpan<float> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        double sourcePositionShift,
        VideoOutputConverter converter,
        Span<ushort> destination,
        ExecutionMode executionMode = ExecutionMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ValidateBatch(source, sourcePositions, levelAdjusts, destination.Length);
        ValidateSourcePositionShift(sourcePositionShift);

        VideoOutputConverter.FastMathConversion conversion = converter.CreateFastMathConversion();
        ref float sourceReference = ref MemoryMarshal.GetReference(source);
        ref double positionReference = ref MemoryMarshal.GetReference(sourcePositions);
        ref double levelReference = ref MemoryMarshal.GetReference(levelAdjusts);
        ref ushort destinationReference = ref MemoryMarshal.GetReference(destination);
        double maximumPosition = source.Length - 3;

        int index = 0;
        if (ShouldUseVector(executionMode, destination.Length) &&
            !DestinationOverlapsInputs(source, sourcePositions, levelAdjusts, destination))
        {
            Span<float> vectorBuffer = stackalloc float[VectorWidth];
            ref float vectorBufferReference = ref MemoryMarshal.GetReference(vectorBuffer);
            unsafe
            {
                fixed (float* sourcePointer = source)
                {
                    for (; index <= destination.Length - VectorWidth; index += VectorWidth)
                    {
                        if (TrySampleVector(
                                sourcePointer,
                                ref positionReference,
                                ref levelReference,
                                index,
                                sourcePositionShift,
                                maximumPosition,
                                out Vector256<float> samples))
                        {
                            samples.StoreUnsafe(ref vectorBufferReference);
                            for (int lane = 0; lane < VectorWidth; lane++)
                            {
                                Unsafe.Add(ref destinationReference, index + lane) = conversion.Convert(
                                    Unsafe.Add(ref vectorBufferReference, lane));
                            }

                            continue;
                        }

                        for (int lane = 0; lane < VectorWidth; lane++)
                        {
                            int scalarIndex = index + lane;
                            float sample = SampleAdjustedUnchecked(
                                ref sourceReference,
                                maximumPosition,
                                Unsafe.Add(ref positionReference, scalarIndex) + sourcePositionShift,
                                Unsafe.Add(ref levelReference, scalarIndex));
                            Unsafe.Add(ref destinationReference, scalarIndex) = conversion.Convert(sample);
                        }
                    }
                }
            }
        }

        for (; index < destination.Length; index++)
        {
            float sample = SampleAdjustedUnchecked(
                ref sourceReference,
                maximumPosition,
                Unsafe.Add(ref positionReference, index) + sourcePositionShift,
                Unsafe.Add(ref levelReference, index));
            Unsafe.Add(ref destinationReference, index) = conversion.Convert(sample);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldUseVector(ExecutionMode executionMode, int destinationLength)
    {
        switch (executionMode)
        {
            case ExecutionMode.Auto:
                return IsVectorizationSupported && destinationLength >= VectorWidth;
            case ExecutionMode.ForceScalar:
                return false;
            case ExecutionMode.ForceVector:
                if (!IsVectorizationSupported)
                {
                    throw new PlatformNotSupportedException(
                        "Forced Approx Catmull-Rom vectorization requires AVX, AVX2, and FMA.");
                }

                return destinationLength >= VectorWidth;
            default:
                throw new ArgumentOutOfRangeException(nameof(executionMode));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DestinationOverlapsInputs<TSource, TDestination>(
        ReadOnlySpan<TSource> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        Span<TDestination> destination)
        where TSource : unmanaged
        where TDestination : unmanaged
    {
        ReadOnlySpan<byte> destinationBytes = MemoryMarshal.AsBytes(destination);
        return MemoryMarshal.AsBytes(source).Overlaps(destinationBytes) ||
            MemoryMarshal.AsBytes(sourcePositions).Overlaps(destinationBytes) ||
            MemoryMarshal.AsBytes(levelAdjusts).Overlaps(destinationBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrepareCoordinates(
        ref double sourcePositions,
        ref double levelAdjusts,
        int index,
        double sourcePositionShift,
        double maximumPosition,
        out Vector128<int> lowerIndices,
        out Vector128<int> upperIndices,
        out Vector256<float> fractions,
        out Vector256<float> levels)
    {
        Vector256<double> shift = Vector256.Create(sourcePositionShift);
        Vector256<double> minimum = Vector256.Create(1.0);
        Vector256<double> maximum = Vector256.Create(maximumPosition);
        Vector256<double> lowerCoordinates = ClampCoordinates(
            Vector256.LoadUnsafe(ref sourcePositions, (nuint)index),
            shift,
            minimum,
            maximum);
        Vector256<double> upperCoordinates = ClampCoordinates(
            Vector256.LoadUnsafe(ref sourcePositions, (nuint)(index + 4)),
            shift,
            minimum,
            maximum);

        lowerIndices = Avx.ConvertToVector128Int32WithTruncation(lowerCoordinates);
        upperIndices = Avx.ConvertToVector128Int32WithTruncation(upperCoordinates);
        Vector128<float> lowerFractions = Avx.ConvertToVector128Single(
            Avx.Subtract(lowerCoordinates, Avx.ConvertToVector256Double(lowerIndices)));
        Vector128<float> upperFractions = Avx.ConvertToVector128Single(
            Avx.Subtract(upperCoordinates, Avx.ConvertToVector256Double(upperIndices)));
        fractions = Vector256.Create(lowerFractions, upperFractions);

        Vector128<float> lowerLevels = Avx.ConvertToVector128Single(
            Vector256.LoadUnsafe(ref levelAdjusts, (nuint)index));
        Vector128<float> upperLevels = Avx.ConvertToVector128Single(
            Vector256.LoadUnsafe(ref levelAdjusts, (nuint)(index + 4)));
        levels = Vector256.Create(lowerLevels, upperLevels);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> ClampCoordinates(
        Vector256<double> coordinates,
        Vector256<double> shift,
        Vector256<double> minimum,
        Vector256<double> maximum)
    {
        coordinates = Avx.Add(coordinates, shift);
        coordinates = Avx.Max(coordinates, minimum);
        return Avx.Min(coordinates, maximum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool TrySampleVector(
        double* source,
        ref double sourcePositions,
        ref double levelAdjusts,
        int index,
        double sourcePositionShift,
        double maximumPosition,
        out Vector256<float> samples)
    {
        PrepareCoordinates(
            ref sourcePositions,
            ref levelAdjusts,
            index,
            sourcePositionShift,
            maximumPosition,
            out Vector128<int> lowerIndices,
            out Vector128<int> upperIndices,
            out Vector256<float> fractions,
            out Vector256<float> levels);

        Vector256<float> p0 = GatherDoubleAsSingle(source, lowerIndices, upperIndices, -1);
        Vector256<float> p1 = GatherDoubleAsSingle(source, lowerIndices, upperIndices, 0);
        Vector256<float> p2 = GatherDoubleAsSingle(source, lowerIndices, upperIndices, 1);
        Vector256<float> p3 = GatherDoubleAsSingle(source, lowerIndices, upperIndices, 2);
        samples = Avx.Multiply(levels, CatmullRom4Vector(p0, p1, p2, p3, fractions));
        if (!AllFinite(samples))
        {
            samples = default;
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool TrySampleVector(
        float* source,
        ref double sourcePositions,
        ref double levelAdjusts,
        int index,
        double sourcePositionShift,
        double maximumPosition,
        out Vector256<float> samples)
    {
        PrepareCoordinates(
            ref sourcePositions,
            ref levelAdjusts,
            index,
            sourcePositionShift,
            maximumPosition,
            out Vector128<int> lowerIndices,
            out Vector128<int> upperIndices,
            out Vector256<float> fractions,
            out Vector256<float> levels);

        Vector256<int> indices = Vector256.Create(lowerIndices, upperIndices);
        Vector256<float> p0 = Avx2.GatherVector256(source, Avx2.Add(indices, Vector256.Create(-1)), 4);
        Vector256<float> p1 = Avx2.GatherVector256(source, indices, 4);
        Vector256<float> p2 = Avx2.GatherVector256(source, Avx2.Add(indices, Vector256.Create(1)), 4);
        Vector256<float> p3 = Avx2.GatherVector256(source, Avx2.Add(indices, Vector256.Create(2)), 4);
        samples = Avx.Multiply(levels, CatmullRom4Vector(p0, p1, p2, p3, fractions));
        if (!AllFinite(samples))
        {
            samples = default;
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<float> GatherDoubleAsSingle(
        double* source,
        Vector128<int> lowerIndices,
        Vector128<int> upperIndices,
        int offset)
    {
        Vector128<int> offsetVector = Vector128.Create(offset);
        Vector256<double> lower = Avx2.GatherVector256(
            source,
            Sse2.Add(lowerIndices, offsetVector),
            8);
        Vector256<double> upper = Avx2.GatherVector256(
            source,
            Sse2.Add(upperIndices, offsetVector),
            8);
        return Vector256.Create(
            Avx.ConvertToVector128Single(lower),
            Avx.ConvertToVector128Single(upper));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> CatmullRom4Vector(
        Vector256<float> p0,
        Vector256<float> p1,
        Vector256<float> p2,
        Vector256<float> p3,
        Vector256<float> fraction)
    {
        Vector256<float> a = Avx.Subtract(p2, p0);
        Vector256<float> b = Avx.Subtract(
            Avx.Multiply(Vector256.Create(2.0f), p0),
            Avx.Multiply(Vector256.Create(5.0f), p1));
        b = Avx.Add(b, Avx.Multiply(Vector256.Create(4.0f), p2));
        b = Avx.Subtract(b, p3);
        Vector256<float> c = Avx.Multiply(
            Vector256.Create(3.0f),
            Avx.Subtract(p1, p2));
        c = Avx.Add(c, p3);
        c = Avx.Subtract(c, p0);
        Vector256<float> polynomial = Fma.MultiplyAdd(c, fraction, b);
        polynomial = Fma.MultiplyAdd(polynomial, fraction, a);
        return Fma.MultiplyAdd(
            Avx.Multiply(Vector256.Create(0.5f), fraction),
            polynomial,
            p1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllFinite(Vector256<float> value)
        // Only finite packed results are committed. The fully evaluated Catmull expression
        // propagates every non-finite tap, level, or intermediate to a non-finite result;
        // the caller then replays that whole packet through the scalar path for exact NaN bits.
        => Avx.MoveMask(FiniteMask(value)) == 0xFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> FiniteMask(Vector256<float> value)
        => Avx.Compare(
            Avx.And(value, FloatAbsoluteValueMask),
            FloatMaximumFinite,
            FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleAdjustedUnchecked(
        ref double source,
        double maximumPosition,
        double sourcePosition,
        double levelAdjust)
    {
        float sample = SampleUnchecked(ref source, maximumPosition, sourcePosition);
        return (float)levelAdjust * sample;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleAdjustedUnchecked(
        ref float source,
        double maximumPosition,
        double sourcePosition,
        double levelAdjust)
    {
        float sample = SampleUnchecked(ref source, maximumPosition, sourcePosition);
        return (float)levelAdjust * sample;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleUnchecked(ref double source, double maximumPosition, double sourcePosition)
    {
        // Rust uses coord.max(1.0).min(len - 3). The first comparison is written
        // this way so NaN folds to the low edge just as f64::max does.
        double coordinate = sourcePosition;
        if (!(coordinate >= 1.0))
        {
            coordinate = 1.0;
        }
        else if (coordinate > maximumPosition)
        {
            coordinate = maximumPosition;
        }

        int sourceIndex = (int)coordinate;
        float fraction = (float)(coordinate - sourceIndex);
        float p0 = (float)Unsafe.Add(ref source, sourceIndex - 1);
        float p1 = (float)Unsafe.Add(ref source, sourceIndex);
        float p2 = (float)Unsafe.Add(ref source, sourceIndex + 1);
        float p3 = (float)Unsafe.Add(ref source, sourceIndex + 2);
        return CatmullRom4(p0, p1, p2, p3, fraction);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleUnchecked(ref float source, double maximumPosition, double sourcePosition)
    {
        // Keep coordinate handling identical to the double-source entry point.
        double coordinate = sourcePosition;
        if (!(coordinate >= 1.0))
        {
            coordinate = 1.0;
        }
        else if (coordinate > maximumPosition)
        {
            coordinate = maximumPosition;
        }

        int sourceIndex = (int)coordinate;
        float fraction = (float)(coordinate - sourceIndex);
        float p0 = Unsafe.Add(ref source, sourceIndex - 1);
        float p1 = Unsafe.Add(ref source, sourceIndex);
        float p2 = Unsafe.Add(ref source, sourceIndex + 1);
        float p3 = Unsafe.Add(ref source, sourceIndex + 2);
        return CatmullRom4(p0, p1, p2, p3, fraction);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CatmullRom4(float p0, float p1, float p2, float p3, float fraction)
    {
        float a = p2 - p0;
        float b = (2.0f * p0) - (5.0f * p1) + (4.0f * p2) - p3;
        float c = (3.0f * (p1 - p2)) + p3 - p0;
        float polynomial = MathF.FusedMultiplyAdd(c, fraction, b);
        polynomial = MathF.FusedMultiplyAdd(polynomial, fraction, a);
        return MathF.FusedMultiplyAdd(0.5f * fraction, polynomial, p1);
    }

    private static void ValidateSource(ReadOnlySpan<double> source)
    {
        if (source.Length < TapCount)
        {
            throw new ArgumentException(
                $"Source must contain at least {TapCount} samples for a four-tap window.",
                nameof(source));
        }
    }

    private static void ValidateSource(ReadOnlySpan<float> source)
    {
        if (source.Length < TapCount)
        {
            throw new ArgumentException(
                $"Source must contain at least {TapCount} samples for a four-tap window.",
                nameof(source));
        }
    }

    private static void ValidateBatch(
        ReadOnlySpan<double> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        int destinationLength)
    {
        ValidateSource(source);
        if (sourcePositions.Length != levelAdjusts.Length)
        {
            throw new ArgumentException(
                "Level-adjust count must match source-position count.",
                nameof(levelAdjusts));
        }

        if (destinationLength != sourcePositions.Length)
        {
            throw new ArgumentException(
                "Destination length must match source-position count.",
                "destination");
        }
    }

    private static void ValidateBatch(
        ReadOnlySpan<float> source,
        ReadOnlySpan<double> sourcePositions,
        ReadOnlySpan<double> levelAdjusts,
        int destinationLength)
    {
        ValidateSource(source);
        if (sourcePositions.Length != levelAdjusts.Length)
        {
            throw new ArgumentException(
                "Level-adjust count must match source-position count.",
                nameof(levelAdjusts));
        }

        if (destinationLength != sourcePositions.Length)
        {
            throw new ArgumentException(
                "Destination length must match source-position count.",
                "destination");
        }
    }

    private static void ValidateSourcePositionShift(double sourcePositionShift)
    {
        if (!double.IsFinite(sourcePositionShift))
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePositionShift));
        }
    }
}
