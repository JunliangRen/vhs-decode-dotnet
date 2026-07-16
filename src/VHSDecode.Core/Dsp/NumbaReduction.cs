namespace VHSDecode.Core.Dsp;

internal static class NumbaReduction
{
    public static double MeanFloat64(ReadOnlySpan<double> values)
    {
        double sum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    public static (double Mean, double StandardDeviation) MeanStandardDeviationFloat64(
        ReadOnlySpan<double> values)
    {
        double mean = MeanFloat64(values);
        double squaredDistanceSum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            double distance = values[i] - mean;
            squaredDistanceSum += distance * distance;
        }

        return (mean, Math.Sqrt(squaredDistanceSum / values.Length));
    }

    public static double MeanFloat64FastMath(ReadOnlySpan<double> values)
    {
        const int VectorWidth = 4;
        const int Interleave = 4;
        const int Stride = VectorWidth * Interleave;
        Span<double> accumulators = stackalloc double[Stride];
        int index = 0;
        int vectorizedEnd = values.Length - (values.Length % Stride);
        for (; index < vectorizedEnd; index += Stride)
        {
            for (int group = 0; group < Interleave; group++)
            {
                for (int lane = 0; lane < VectorWidth; lane++)
                {
                    int accumulator = (group * VectorWidth) + lane;
                    accumulators[accumulator] += values[index + accumulator];
                }
            }
        }

        Span<double> lanes = stackalloc double[VectorWidth];
        for (int lane = 0; lane < VectorWidth; lane++)
        {
            double left = accumulators[lane] + accumulators[VectorWidth + lane];
            double right = accumulators[(2 * VectorWidth) + lane]
                + accumulators[(3 * VectorWidth) + lane];
            lanes[lane] = left + right;
        }

        double sum = (lanes[0] + lanes[2]) + (lanes[1] + lanes[3]);
        int fourLaneEnd = values.Length - ((values.Length - index) % VectorWidth);
        if (index < fourLaneEnd)
        {
            lanes.Clear();
            lanes[0] = sum;
            for (; index < fourLaneEnd; index += VectorWidth)
            {
                for (int lane = 0; lane < VectorWidth; lane++)
                {
                    lanes[lane] += values[index + lane];
                }
            }

            sum = (lanes[0] + lanes[2]) + (lanes[1] + lanes[3]);
        }

        for (; index < values.Length; index++)
        {
            sum += values[index];
        }

        return sum / values.Length;
    }
}
