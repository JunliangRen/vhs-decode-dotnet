using System.Buffers;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

public static class ChromaTransientImprovement
{
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
        ArgumentOutOfRangeException.ThrowIfNegative(lineStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);
        if (lineStart >= chromaData.Length)
        {
            return;
        }

        if (width < 0L || mix == 0.0)
        {
            return;
        }

        int lineCount = (chromaData.Length - lineStart) / lineLength;
        if (lineCount <= 0)
        {
            return;
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
            return;
        }

        float[] lineBuffer = ArrayPool<float>.Shared.Rent(lineLength);
        try
        {
            int first = (int)firstSample;
            int end = (int)endSample;
            int radius = checked((int)sweepRadius);
            int vectorEnd = first + ((end - first) & ~7);
            for (int line = 0; line < lineCount; line++)
            {
                int lineOffset = checked(lineStart + (line * lineLength));
                for (int pass = 0; pass < PassCount; pass++)
                {
                    for (int sample = 0; sample < lineLength; sample++)
                    {
                        lineBuffer[sample] = (float)chromaData[lineOffset + sample];
                    }

                    float currentMix = mixFactors[pass];
                    for (int sample = first; sample < end; sample++)
                    {
                        int index = lineOffset + sample;
                        float currentI = lineBuffer[sample];
                        float currentQ = lineBuffer[sample - 1];
                        float pastI = lineBuffer[sample - radius];
                        float pastQ = lineBuffer[sample - radius - 1];
                        float futureI = lineBuffer[sample + radius];
                        float futureQ = lineBuffer[sample + radius - 1];

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

                        double gate = totalDistance > threshold ? 1.0 : 0.0;
                        double progress = totalDistance != 0.0f
                            ? sample < vectorEnd
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
        }
        finally
        {
            ArrayPool<float>.Shared.Return(lineBuffer);
        }
    }

    private static float FastVectorizedQuotient(float numerator, float denominator)
    {
        float reciprocal = Sse.IsSupported
            ? Sse.ReciprocalScalar(Vector128.CreateScalar(denominator)).ToScalar()
            : 1.0f / denominator;
        float initial = reciprocal * numerator;
        float residual = MathF.FusedMultiplyAdd(
            denominator,
            initial,
            -numerator);
        return MathF.FusedMultiplyAdd(-residual, reciprocal, initial);
    }
}
