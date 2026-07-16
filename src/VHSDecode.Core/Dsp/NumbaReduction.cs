namespace VHSDecode.Core.Dsp;

internal static class NumbaReduction
{
    public static float[] ToFloat32(ReadOnlySpan<double> values)
    {
        var output = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            output[i] = (float)values[i];
        }

        return output;
    }

    public static float[] CenterFloat32(ReadOnlySpan<double> values)
    {
        float[] output = ToFloat32(values);
        float mean = MeanFloat32(output);
        for (int i = 0; i < output.Length; i++)
        {
            output[i] -= mean;
        }

        return output;
    }

    public static float MeanFloat32(ReadOnlySpan<float> values)
    {
        float sum = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    public static float StandardDeviationFloat32(ReadOnlySpan<double> values)
        => StandardDeviationFloat32(ToFloat32(values));

    public static float StandardDeviationFloat32(ReadOnlySpan<float> values)
    {
        float mean = MeanFloat32(values);
        float squaredDistanceSum = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            float distance = values[i] - mean;
            squaredDistanceSum += distance * distance;
        }

        return MathF.Sqrt(squaredDistanceSum / values.Length);
    }

    public static float MaxFloat32(ReadOnlySpan<float> values)
    {
        float maximum = float.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            maximum = MathF.Max(maximum, values[i]);
        }

        return maximum;
    }

    public static float MaxAbsFloat32(ReadOnlySpan<float> values)
    {
        float maximum = float.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            maximum = MathF.Max(maximum, MathF.Abs(values[i]));
        }

        return maximum;
    }

    public static double MedianFloat32(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            return float.NaN;
        }

        var sorted = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            sorted[i] = (float)values[i];
        }

        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (float)((sorted[middle - 1] + sorted[middle]) / 2.0f)
            : sorted[middle];
    }

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
