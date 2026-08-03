using System.Buffers;

namespace VHSDecode.Core.Dsp;

public static class ChromaTransientImprovement
{
    private const int MaximumParallelWorkers = 8;
    private const int PassCount = 4;
    private const double Decay = 0.25;

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
            for (int sample = 0; sample < lineLength; sample++)
            {
                lineBuffer[sample] = (float)chromaData[lineOffset + sample];
            }

            float currentMix = parameters.MixForPass(pass);
            for (int sample = parameters.First; sample < parameters.End; sample++)
            {
                int index = lineOffset + sample;
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
                chromaData[index] = (float)Math.FusedMultiplyAdd(
                    currentMix * gate,
                    targetDelta,
                    currentI);
            }
        }
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
        const int EstimateNumerator = 1 << 25;

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
        int midpointDenominator = 4_097 + (2 * bucket);
        int estimate = EstimateNumerator / midpointDenominator;
        int remainder = EstimateNumerator % midpointDenominator;
        if ((remainder * 2) >= midpointDenominator)
        {
            estimate++;
        }

        uint resultMantissa = (uint)(estimate - 4_096) << 11;
        uint resultExponent = (uint)(253 - exponent) << 23;
        return BitConverter.UInt32BitsToSingle(
            sign | resultExponent | resultMantissa);
    }
}
