using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

public static class ChromaTransientImprovement
{
    private const int MaximumParallelWorkers = 8;
    private const int PassCount = 4;
    private const double Decay = 0.25;
    private static readonly uint[] PinnedReciprocalMantissas =
        CreatePinnedReciprocalMantissas();

    public static void ApplyInPlace(
        Span<double> chromaData,
        int lineStart,
        int lineLength,
        double baseNoiseFloor,
        long width,
        double mix)
    {
        if (!TryCreateParameters(
                chromaData.Length,
                lineStart,
                lineLength,
                baseNoiseFloor,
                width,
                mix,
                out CtiParameters parameters))
        {
            return;
        }

        float[] lineBuffer = ArrayPool<float>.Shared.Rent(lineLength);
        try
        {
            for (int line = 0; line < parameters.LineCount; line++)
            {
                ProcessLine(
                    chromaData,
                    lineStart,
                    lineLength,
                    line,
                    parameters,
                    lineBuffer);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(lineBuffer);
        }
    }

    internal static void ApplyInPlace(
        double[] chromaData,
        int lineStart,
        int lineLength,
        double baseNoiseFloor,
        long width,
        double mix,
        int workerThreads)
    {
        ArgumentNullException.ThrowIfNull(chromaData);
        if (workerThreads <= 1)
        {
            ApplyInPlace(
                chromaData.AsSpan(),
                lineStart,
                lineLength,
                baseNoiseFloor,
                width,
                mix);
            return;
        }

        if (!TryCreateParameters(
                chromaData.Length,
                lineStart,
                lineLength,
                baseNoiseFloor,
                width,
                mix,
                out CtiParameters parameters))
        {
            return;
        }

        Parallel.For(
            fromInclusive: 0,
            toExclusive: parameters.LineCount,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(
                    Math.Min(workerThreads, parameters.LineCount),
                    MaximumParallelWorkers)
            },
            () => ArrayPool<float>.Shared.Rent(lineLength),
            (line, _, lineBuffer) =>
            {
                ProcessLine(
                    chromaData,
                    lineStart,
                    lineLength,
                    line,
                    parameters,
                    lineBuffer);
                return lineBuffer;
            },
            lineBuffer => ArrayPool<float>.Shared.Return(lineBuffer));
    }

    private static bool TryCreateParameters(
        int dataLength,
        int lineStart,
        int lineLength,
        double baseNoiseFloor,
        long width,
        double mix,
        out CtiParameters parameters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);
        if (lineStart >= dataLength || width < 0L || mix == 0.0)
        {
            parameters = default;
            return false;
        }

        int lineCount = (dataLength - lineStart) / lineLength;
        if (lineCount <= 0)
        {
            parameters = default;
            return false;
        }

        Span<float> mixFactors = stackalloc float[PassCount];
        for (int pass = 0; pass < mixFactors.Length; pass++)
        {
            mixFactors[pass] = (float)(mix * Math.Pow(Decay, pass));
        }

        long sweepRadius = Math.Max(4L, unchecked(width * 4L));
        double threshold = baseNoiseFloor * Math.Sqrt(width);
        long firstSample = unchecked(sweepRadius + 1L);
        long endSample = unchecked(lineLength - (sweepRadius + 1L));
        if (firstSample < 0L
            || firstSample >= endSample
            || firstSample > int.MaxValue
            || endSample > int.MaxValue)
        {
            parameters = default;
            return false;
        }

        int first = (int)firstSample;
        int end = (int)endSample;
        parameters = new CtiParameters(
            lineCount,
            first,
            end,
            checked((int)sweepRadius),
            first + ((end - first) & ~7),
            threshold,
            mixFactors[0],
            mixFactors[1],
            mixFactors[2],
            mixFactors[3]);
        return true;
    }

    private static void ProcessLine(
        Span<double> chromaData,
        int lineStart,
        int lineLength,
        int line,
        CtiParameters parameters,
        Span<float> lineBuffer)
    {
        int lineOffset = checked(lineStart + (line * lineLength));
        for (int pass = 0; pass < PassCount; pass++)
        {
            ChromaSuperGaussianFinalFilter.CopyFloat64ToFloat32(
                chromaData.Slice(lineOffset, lineLength),
                lineBuffer);

            float currentMix = parameters.MixForPass(pass);
            int sample = parameters.First;
            if (Avx.IsSupported && Fma.IsSupported && Sse41.IsSupported)
            {
                sample = ProcessVectorizedDistanceRange(
                    chromaData,
                    lineOffset,
                    lineBuffer,
                    parameters,
                    currentMix);
            }

            for (; sample < parameters.End; sample++)
            {
                float currentI = lineBuffer[sample];
                float currentQ = lineBuffer[sample - 1];
                float pastI = lineBuffer[sample - parameters.Radius];
                float pastQ = lineBuffer[sample - parameters.Radius - 1];
                float futureI = lineBuffer[sample + parameters.Radius];
                float futureQ = lineBuffer[sample + parameters.Radius - 1];

                float deltaBackI = currentI - pastI;
                float deltaBackQ = currentQ - pastQ;
                float distanceBack = MathF.Sqrt(
                    MathF.FusedMultiplyAdd(
                        deltaBackQ,
                        deltaBackQ,
                        deltaBackI * deltaBackI));
                float deltaForwardI = futureI - currentI;
                float deltaForwardQ = futureQ - currentQ;
                float distanceForward = MathF.Sqrt(
                    MathF.FusedMultiplyAdd(
                        deltaForwardQ,
                        deltaForwardQ,
                        deltaForwardI * deltaForwardI));
                float totalDistance = distanceBack + distanceForward;

                double gate = totalDistance > parameters.Threshold ? 1.0 : 0.0;
                double progress = totalDistance != 0.0f
                    ? sample < parameters.VectorEnd
                        ? FastVectorizedQuotient(distanceBack, totalDistance)
                        : distanceBack / totalDistance
                    : 0.0;
                FinishSample(
                    chromaData,
                    lineOffset,
                    lineBuffer,
                    sample,
                    parameters,
                    currentMix,
                    progress,
                    gate);
            }
        }
    }

    private static int ProcessVectorizedDistanceRange(
        Span<double> chromaData,
        int lineOffset,
        Span<float> lineBuffer,
        CtiParameters parameters,
        float currentMix)
    {
        Span<float> reciprocalEstimates = stackalloc float[8];
        Span<float> totalDistances = stackalloc float[8];
        Vector256<float> signBits = Vector256.Create(-0.0f);
        int sample = parameters.First;
        for (; sample < parameters.VectorEnd; sample += 8)
        {
            Vector256<float> currentI = LoadVector(lineBuffer, sample);
            Vector256<float> currentQ = LoadVector(lineBuffer, sample - 1);
            Vector256<float> pastI = LoadVector(
                lineBuffer,
                sample - parameters.Radius);
            Vector256<float> pastQ = LoadVector(
                lineBuffer,
                sample - parameters.Radius - 1);
            Vector256<float> futureI = LoadVector(
                lineBuffer,
                sample + parameters.Radius);
            Vector256<float> futureQ = LoadVector(
                lineBuffer,
                sample + parameters.Radius - 1);

            Vector256<float> deltaBackI = Avx.Subtract(currentI, pastI);
            Vector256<float> deltaBackQ = Avx.Subtract(currentQ, pastQ);
            Vector256<float> distanceBack = Avx.Sqrt(Fma.MultiplyAdd(
                deltaBackQ,
                deltaBackQ,
                Avx.Multiply(deltaBackI, deltaBackI)));
            Vector256<float> deltaForwardI = Avx.Subtract(futureI, currentI);
            Vector256<float> deltaForwardQ = Avx.Subtract(futureQ, currentQ);
            Vector256<float> distanceForward = Avx.Sqrt(Fma.MultiplyAdd(
                deltaForwardQ,
                deltaForwardQ,
                Avx.Multiply(deltaForwardI, deltaForwardI)));
            Vector256<float> totalDistance = Avx.Add(
                distanceBack,
                distanceForward);
            Vector256<float> reciprocals;
            if (Avx2.IsSupported)
            {
                reciprocals = PinnedReciprocalEstimateVector(totalDistance);
            }
            else
            {
                totalDistance.CopyTo(totalDistances);
                for (int lane = 0; lane < 8; lane++)
                {
                    reciprocalEstimates[lane] =
                        PinnedReciprocalEstimate(totalDistances[lane]);
                }

                reciprocals = LoadVector(reciprocalEstimates, 0);
            }
            Vector256<float> initialProgress = Avx.Multiply(
                reciprocals,
                distanceBack);
            Vector256<float> residual = Fma.MultiplyAdd(
                totalDistance,
                initialProgress,
                Avx.Xor(distanceBack, signBits));
            Vector256<float> progress = Fma.MultiplyAdd(
                Avx.Xor(residual, signBits),
                reciprocals,
                initialProgress);
            Vector256<float> nonzeroDistance = Avx.Compare(
                totalDistance,
                Vector256<float>.Zero,
                FloatComparisonMode.UnorderedNotEqualNonSignaling);
            progress = Avx.And(progress, nonzeroDistance);
            FinishVectorizedSamples(
                chromaData,
                lineOffset + sample,
                currentI,
                pastI,
                futureI,
                totalDistance,
                progress,
                parameters.Threshold,
                currentMix);
        }

        return sample;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LoadVector(Span<float> values, int index)
        => MemoryMarshal.Cast<float, Vector256<float>>(values.Slice(index, 8))[0];

    private static void FinishVectorizedSamples(
        Span<double> chromaData,
        int outputOffset,
        Vector256<float> current,
        Vector256<float> past,
        Vector256<float> future,
        Vector256<float> totalDistance,
        Vector256<float> progress,
        double threshold,
        float currentMix)
    {
        FinishFourVectorizedSamples(
            chromaData,
            outputOffset,
            current.GetLower(),
            past.GetLower(),
            future.GetLower(),
            totalDistance.GetLower(),
            progress.GetLower(),
            threshold,
            currentMix);
        FinishFourVectorizedSamples(
            chromaData,
            outputOffset + 4,
            current.GetUpper(),
            past.GetUpper(),
            future.GetUpper(),
            totalDistance.GetUpper(),
            progress.GetUpper(),
            threshold,
            currentMix);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FinishFourVectorizedSamples(
        Span<double> chromaData,
        int outputOffset,
        Vector128<float> currentFloat,
        Vector128<float> pastFloat,
        Vector128<float> futureFloat,
        Vector128<float> totalDistanceFloat,
        Vector128<float> progressFloat,
        double threshold,
        float currentMix)
    {
        Vector128<float> lowerHalfFloat = Sse.CompareLessThan(
            progressFloat,
            Vector128.Create(0.5f));
        Vector128<float> anchorAFloat = Sse41.BlendVariable(
            currentFloat,
            pastFloat,
            lowerHalfFloat);
        Vector128<float> anchorBFloat = Sse41.BlendVariable(
            futureFloat,
            currentFloat,
            lowerHalfFloat);
        Vector128<float> anchorDeltaFloat = Sse.Subtract(
            anchorBFloat,
            anchorAFloat);

        Vector256<double> current = Avx.ConvertToVector256Double(currentFloat);
        Vector256<double> anchorA = Avx.ConvertToVector256Double(anchorAFloat);
        Vector256<double> progress = Avx.ConvertToVector256Double(progressFloat);
        Vector256<double> one = Vector256.Create(1.0);
        Vector256<double> inverseProgress = Avx.Subtract(one, progress);
        Vector256<double> lowerWeight = Avx.Multiply(
            Vector256.Create(4.0),
            Avx.Multiply(progress, progress));
        Vector256<double> upperWeight = Fma.MultiplyAdd(
            Vector256.Create(-4.0),
            Avx.Multiply(inverseProgress, inverseProgress),
            one);
        Vector256<double> lowerHalf = Avx.Compare(
            progress,
            Vector256.Create(0.5),
            FloatComparisonMode.OrderedLessThanNonSignaling);
        Vector256<double> weight = Avx.BlendVariable(
            upperWeight,
            lowerWeight,
            lowerHalf);
        Vector256<double> targetDelta = Fma.MultiplyAdd(
            weight,
            Avx.ConvertToVector256Double(anchorDeltaFloat),
            Avx.Subtract(anchorA, current));

        Vector256<double> gateMask = Avx.Compare(
            Avx.ConvertToVector256Double(totalDistanceFloat),
            Vector256.Create(threshold),
            FloatComparisonMode.OrderedGreaterThanNonSignaling);
        Vector256<double> gate = Avx.And(gateMask, one);
        Vector256<double> result = Fma.MultiplyAdd(
            Avx.Multiply(Vector256.Create((double)currentMix), gate),
            targetDelta,
            current);
        Vector128<float> rounded = Avx.ConvertToVector128Single(result);
        MemoryMarshal.Cast<double, Vector256<double>>(
            chromaData.Slice(outputOffset, 4))[0] =
            Avx.ConvertToVector256Double(rounded);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FinishSample(
        Span<double> chromaData,
        int lineOffset,
        Span<float> lineBuffer,
        int sample,
        CtiParameters parameters,
        float currentMix,
        double progress,
        double gate)
    {
        float currentI = lineBuffer[sample];
        float pastI = lineBuffer[sample - parameters.Radius];
        float futureI = lineBuffer[sample + parameters.Radius];
        bool lowerHalf = progress < 0.5;
        double inverseProgress = 1.0 - progress;
        double lowerWeight = 4.0 * (progress * progress);
        double upperWeight = Math.FusedMultiplyAdd(
            -4.0,
            inverseProgress * inverseProgress,
            1.0);
        double weight = lowerHalf ? lowerWeight : upperWeight;
        float anchorA = lowerHalf ? pastI : currentI;
        float anchorB = lowerHalf ? currentI : futureI;
        double targetDelta = Math.FusedMultiplyAdd(
            weight,
            anchorB - anchorA,
            (double)anchorA - currentI);
        chromaData[lineOffset + sample] = (float)Math.FusedMultiplyAdd(
            currentMix * gate,
            targetDelta,
            currentI);
    }

    private readonly record struct CtiParameters(
        int LineCount,
        int First,
        int End,
        int Radius,
        int VectorEnd,
        double Threshold,
        float Mix0,
        float Mix1,
        float Mix2,
        float Mix3)
    {
        public float MixForPass(int pass)
        {
            return pass switch
            {
                0 => Mix0,
                1 => Mix1,
                2 => Mix2,
                3 => Mix3,
                _ => throw new ArgumentOutOfRangeException(nameof(pass))
            };
        }
    }

    private static float FastVectorizedQuotient(float numerator, float denominator)
    {
        float reciprocal = PinnedReciprocalEstimate(denominator);
        float initial = reciprocal * numerator;
        float residual = MathF.FusedMultiplyAdd(
            denominator,
            initial,
            -numerator);
        return MathF.FusedMultiplyAdd(-residual, reciprocal, initial);
    }

    internal static float PinnedReciprocalEstimate(float value)
    {
        const uint SignMask = 0x80000000u;
        const uint MantissaMask = 0x007FFFFFu;
        const uint InfinityBits = 0x7F800000u;
        const uint QuietNaNBit = 0x00400000u;
        uint bits = BitConverter.SingleToUInt32Bits(value);
        uint sign = bits & SignMask;
        int exponent = (int)((bits >> 23) & 0xFFu);
        uint mantissa = bits & MantissaMask;

        if (exponent == 0)
        {
            return BitConverter.UInt32BitsToSingle(sign | InfinityBits);
        }

        if (exponent == 0xFF)
        {
            return mantissa == 0
                ? BitConverter.UInt32BitsToSingle(sign)
                : BitConverter.UInt32BitsToSingle(bits | QuietNaNBit);
        }

        if (exponent >= 253)
        {
            return BitConverter.UInt32BitsToSingle(sign);
        }

        // For bucket midpoint x=(4097+2b)/4096, the pinned RCPSS
        // significand is round(2^25/(4097+2b)).
        int bucket = (int)(mantissa >> 12);
        uint resultMantissa = PinnedReciprocalMantissas[bucket];
        uint resultExponent = (uint)(253 - exponent) << 23;
        return BitConverter.UInt32BitsToSingle(
            sign | resultExponent | resultMantissa);
    }

    internal static void PinnedReciprocalEstimatesForTesting(
        ReadOnlySpan<float> input,
        Span<float> output,
        bool permitAvx2)
    {
        if (output.Length != input.Length)
        {
            throw new ArgumentException(
                "Output length must match the reciprocal input length.",
                nameof(output));
        }

        int index = 0;
        if (permitAvx2 && Avx2.IsSupported)
        {
            int vectorizedEnd = input.Length & ~7;
            for (; index < vectorizedEnd; index += 8)
            {
                Vector256<float> values =
                    MemoryMarshal.Cast<float, Vector256<float>>(input.Slice(index, 8))[0];
                MemoryMarshal.Cast<float, Vector256<float>>(output.Slice(index, 8))[0] =
                    PinnedReciprocalEstimateVector(values);
            }
        }

        for (; index < input.Length; index++)
        {
            output[index] = PinnedReciprocalEstimate(input[index]);
        }
    }

    private static unsafe Vector256<float> PinnedReciprocalEstimateVector(
        Vector256<float> values)
    {
        Vector256<int> bits = values.AsInt32();
        Vector256<int> sign = Avx2.And(
            bits,
            Vector256.Create(unchecked((int)0x8000_0000u)));
        Vector256<int> mantissa = Avx2.And(bits, Vector256.Create(0x007F_FFFF));
        Vector256<int> exponent = Avx2.And(
            Avx2.ShiftRightLogical(bits.AsUInt32(), 23).AsInt32(),
            Vector256.Create(0xFF));
        Vector256<int> buckets =
            Avx2.ShiftRightLogical(mantissa.AsUInt32(), 12).AsInt32();

        Vector256<int> resultMantissas;
        fixed (uint* table = PinnedReciprocalMantissas)
        {
            resultMantissas = Avx2.GatherVector256(table, buckets, 4).AsInt32();
        }

        Vector256<int> resultExponents = Avx2.ShiftLeftLogical(
            Avx2.Subtract(Vector256.Create(253), exponent).AsUInt32(),
            23).AsInt32();
        Vector256<int> result = Avx2.Or(
            sign,
            Avx2.Or(resultExponents, resultMantissas));

        Vector256<int> zeroExponent = Avx2.CompareEqual(
            exponent,
            Vector256<int>.Zero);
        result = SelectBits(
            result,
            Avx2.Or(sign, Vector256.Create(0x7F80_0000)),
            zeroExponent);

        Vector256<int> highExponent = Avx2.CompareGreaterThan(
            exponent,
            Vector256.Create(252));
        result = SelectBits(result, sign, highExponent);

        Vector256<int> exponent255 = Avx2.CompareEqual(
            exponent,
            Vector256.Create(255));
        Vector256<int> nonzeroMantissa = Avx2.Xor(
            Avx2.CompareEqual(mantissa, Vector256<int>.Zero),
            Vector256.Create(-1));
        Vector256<int> nan = Avx2.And(exponent255, nonzeroMantissa);
        result = SelectBits(
            result,
            Avx2.Or(bits, Vector256.Create(0x0040_0000)),
            nan);
        return result.AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> SelectBits(
        Vector256<int> falseValue,
        Vector256<int> trueValue,
        Vector256<int> mask)
        => Avx2.Xor(
            falseValue,
            Avx2.And(mask, Avx2.Xor(falseValue, trueValue)));

    private static uint[] CreatePinnedReciprocalMantissas()
    {
        const int EstimateNumerator = 1 << 25;
        var result = new uint[2_048];
        for (int bucket = 0; bucket < result.Length; bucket++)
        {
            int midpointDenominator = 4_097 + (2 * bucket);
            int estimate = EstimateNumerator / midpointDenominator;
            int remainder = EstimateNumerator % midpointDenominator;
            if ((remainder * 2) >= midpointDenominator)
            {
                estimate++;
            }

            result[bucket] = (uint)(estimate - 4_096) << 11;
        }

        return result;
    }
}
