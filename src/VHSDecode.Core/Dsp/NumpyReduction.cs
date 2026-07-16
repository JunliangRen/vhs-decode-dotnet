namespace VHSDecode.Core.Dsp;

internal static class NumpyReduction
{
    public static float MeanFloat32(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            return float.NaN;
        }

        return PairwiseSumFloat32(values) / values.Length;
    }

    public static double MeanFloat64(ReadOnlySpan<double> values)
        => PairwiseSumFloat64(values) / values.Length;

    public static (double Mean, double StandardDeviation) MeanStandardDeviationFloat64(
        ReadOnlySpan<double> values)
    {
        double mean = MeanFloat64(values);
        var squaredDistances = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double distance = values[i] - mean;
            squaredDistances[i] = distance * distance;
        }

        double variance = MeanFloat64(squaredDistances);
        return (mean, Math.Sqrt(variance));
    }

    private static float PairwiseSumFloat32(ReadOnlySpan<double> values)
    {
        const int pairwiseBlockSize = 128;
        if (values.Length < 8)
        {
            float scalarSum = -0.0f;
            for (int i = 0; i < values.Length; i++)
            {
                scalarSum += (float)values[i];
            }

            return scalarSum;
        }

        if (values.Length > pairwiseBlockSize)
        {
            int split = values.Length / 2;
            split -= split % 8;
            return PairwiseSumFloat32(values[..split])
                + PairwiseSumFloat32(values[split..]);
        }

        float sum0 = (float)values[0];
        float sum1 = (float)values[1];
        float sum2 = (float)values[2];
        float sum3 = (float)values[3];
        float sum4 = (float)values[4];
        float sum5 = (float)values[5];
        float sum6 = (float)values[6];
        float sum7 = (float)values[7];
        int index = 8;
        int vectorizedEnd = values.Length - (values.Length % 8);
        for (; index < vectorizedEnd; index += 8)
        {
            sum0 += (float)values[index];
            sum1 += (float)values[index + 1];
            sum2 += (float)values[index + 2];
            sum3 += (float)values[index + 3];
            sum4 += (float)values[index + 4];
            sum5 += (float)values[index + 5];
            sum6 += (float)values[index + 6];
            sum7 += (float)values[index + 7];
        }

        float combinedSum = ((sum0 + sum1) + (sum2 + sum3))
            + ((sum4 + sum5) + (sum6 + sum7));
        for (; index < values.Length; index++)
        {
            combinedSum += (float)values[index];
        }

        return combinedSum;
    }

    private static double PairwiseSumFloat64(ReadOnlySpan<double> values)
    {
        const int pairwiseBlockSize = 128;
        if (values.Length < 8)
        {
            double scalarSum = -0.0;
            for (int i = 0; i < values.Length; i++)
            {
                scalarSum += values[i];
            }

            return scalarSum;
        }

        if (values.Length > pairwiseBlockSize)
        {
            int split = values.Length / 2;
            split -= split % 8;
            return PairwiseSumFloat64(values[..split])
                + PairwiseSumFloat64(values[split..]);
        }

        double sum0 = values[0];
        double sum1 = values[1];
        double sum2 = values[2];
        double sum3 = values[3];
        double sum4 = values[4];
        double sum5 = values[5];
        double sum6 = values[6];
        double sum7 = values[7];
        int index = 8;
        int vectorizedEnd = values.Length - (values.Length % 8);
        for (; index < vectorizedEnd; index += 8)
        {
            sum0 += values[index];
            sum1 += values[index + 1];
            sum2 += values[index + 2];
            sum3 += values[index + 3];
            sum4 += values[index + 4];
            sum5 += values[index + 5];
            sum6 += values[index + 6];
            sum7 += values[index + 7];
        }

        double combinedSum = ((sum0 + sum1) + (sum2 + sum3))
            + ((sum4 + sum5) + (sum6 + sum7));
        for (; index < values.Length; index++)
        {
            combinedSum += values[index];
        }

        return combinedSum;
    }
}
