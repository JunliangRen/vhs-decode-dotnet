using System.Numerics;

namespace VHSDecode.Core.Dsp;

internal static class NumpyReduction
{
    private const int IntroselectThreshold = 32 * 1024;
    private const int PartitionSortThreshold = 32;

    public static double MedianFloat64(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            return double.NaN;
        }

        double[] working = values.ToArray();
        bool hasPositiveZero = false;
        bool hasNegativeZero = false;
        for (int i = 0; i < working.Length; i++)
        {
            double value = working[i];
            if (double.IsNaN(value))
            {
                return value;
            }

            if (value == 0.0)
            {
                if (BitConverter.DoubleToInt64Bits(value) < 0)
                {
                    hasNegativeZero = true;
                }
                else
                {
                    hasPositiveZero = true;
                }
            }
        }

        if (working.Length < IntroselectThreshold || (hasPositiveZero && hasNegativeZero))
        {
            Array.Sort(working);
            return SortedMedian(working);
        }

        int middle = working.Length / 2;
        double upper = SelectKth(working, middle);
        if ((working.Length & 1) != 0)
        {
            return upper;
        }

        double lower = working[0];
        for (int i = 1; i < middle; i++)
        {
            if (working[i].CompareTo(lower) > 0)
            {
                lower = working[i];
            }
        }

        return (lower + upper) / 2.0;
    }

    private static double SortedMedian(double[] sorted)
    {
        int middle = sorted.Length / 2;
        return (sorted.Length & 1) == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    private static double SelectKth(double[] values, int target)
    {
        int left = 0;
        int right = values.Length - 1;
        int depthLimit = 2 * (BitOperations.Log2((uint)values.Length) + 1);
        while (left < right)
        {
            int length = right - left + 1;
            if (length <= PartitionSortThreshold || depthLimit-- == 0)
            {
                Array.Sort(values, left, length);
                return values[target];
            }

            double pivot = MedianOfThree(
                values[left],
                values[left + (length / 2)],
                values[right]);
            int lower = left;
            int index = left;
            int upper = right;
            while (index <= upper)
            {
                int comparison = values[index].CompareTo(pivot);
                if (comparison < 0)
                {
                    (values[lower], values[index]) = (values[index], values[lower]);
                    lower++;
                    index++;
                }
                else if (comparison > 0)
                {
                    (values[index], values[upper]) = (values[upper], values[index]);
                    upper--;
                }
                else
                {
                    index++;
                }
            }

            if (target < lower)
            {
                right = lower - 1;
            }
            else if (target > upper)
            {
                left = upper + 1;
            }
            else
            {
                return values[target];
            }
        }

        return values[target];
    }

    private static double MedianOfThree(double first, double second, double third)
    {
        if (first.CompareTo(second) > 0)
        {
            (first, second) = (second, first);
        }

        if (second.CompareTo(third) > 0)
        {
            (second, third) = (third, second);
        }

        if (first.CompareTo(second) > 0)
        {
            (first, second) = (second, first);
        }

        return second;
    }

    public static float MeanFloat32(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            return float.NaN;
        }

        return PairwiseSumFloat32(values) / values.Length;
    }

    public static float MeanFloat32(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
        {
            return float.NaN;
        }

        return PairwiseSumFloat32(values) / values.Length;
    }

    public static (float Mean, float StandardDeviation) MeanStandardDeviationFloat32(
        ReadOnlySpan<float> values)
    {
        float mean = MeanFloat32(values);
        Span<float> squaredDistances = values.Length <= 512
            ? stackalloc float[values.Length]
            : new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            float distance = values[i] - mean;
            squaredDistances[i] = distance * distance;
        }

        float variance = MeanFloat32(squaredDistances);
        return (mean, MathF.Sqrt(variance));
    }

    public static double MeanFloat64(ReadOnlySpan<double> values)
        => PairwiseSumFloat64(values) / values.Length;

    public static (double Mean, double StandardDeviation) MeanStandardDeviationFloat64(
        ReadOnlySpan<double> values)
    {
        double mean = MeanFloat64(values);
        Span<double> squaredDistances = values.Length <= 512
            ? stackalloc double[values.Length]
            : new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double distance = values[i] - mean;
            squaredDistances[i] = distance * distance;
        }

        double variance = MeanFloat64(squaredDistances);
        return (mean, Math.Sqrt(variance));
    }

    public static Complex MeanComplex128(ReadOnlySpan<Complex> values)
    {
        Complex sum = PairwiseSumComplex128(values);
        double scale = 1.0 / values.Length;
        return new Complex(sum.Real * scale, sum.Imaginary * scale);
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

    private static float PairwiseSumFloat32(ReadOnlySpan<float> values)
    {
        const int pairwiseBlockSize = 128;
        if (values.Length < 8)
        {
            float scalarSum = -0.0f;
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
            return PairwiseSumFloat32(values[..split])
                + PairwiseSumFloat32(values[split..]);
        }

        float sum0 = values[0];
        float sum1 = values[1];
        float sum2 = values[2];
        float sum3 = values[3];
        float sum4 = values[4];
        float sum5 = values[5];
        float sum6 = values[6];
        float sum7 = values[7];
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

        float combinedSum = ((sum0 + sum1) + (sum2 + sum3))
            + ((sum4 + sum5) + (sum6 + sum7));
        for (; index < values.Length; index++)
        {
            combinedSum += values[index];
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

    private static Complex PairwiseSumComplex128(ReadOnlySpan<Complex> values)
    {
        const int pairwiseBlockSize = 64;
        if (values.Length < 4)
        {
            double real = -0.0;
            double imaginary = -0.0;
            for (int i = 0; i < values.Length; i++)
            {
                real += values[i].Real;
                imaginary += values[i].Imaginary;
            }

            return new Complex(real, imaginary);
        }

        if (values.Length > pairwiseBlockSize)
        {
            int split = values.Length / 2;
            split -= split % 4;
            Complex left = PairwiseSumComplex128(values[..split]);
            Complex right = PairwiseSumComplex128(values[split..]);
            return new Complex(left.Real + right.Real, left.Imaginary + right.Imaginary);
        }

        Span<double> realAccumulators = stackalloc double[4];
        Span<double> imaginaryAccumulators = stackalloc double[4];
        for (int lane = 0; lane < 4; lane++)
        {
            realAccumulators[lane] = values[lane].Real;
            imaginaryAccumulators[lane] = values[lane].Imaginary;
        }

        int index = 4;
        int vectorizedEnd = values.Length - (values.Length % 4);
        for (; index < vectorizedEnd; index += 4)
        {
            for (int lane = 0; lane < 4; lane++)
            {
                realAccumulators[lane] += values[index + lane].Real;
                imaginaryAccumulators[lane] += values[index + lane].Imaginary;
            }
        }

        double realSum = (realAccumulators[0] + realAccumulators[1])
            + (realAccumulators[2] + realAccumulators[3]);
        double imaginarySum = (imaginaryAccumulators[0] + imaginaryAccumulators[1])
            + (imaginaryAccumulators[2] + imaginaryAccumulators[3]);
        for (; index < values.Length; index++)
        {
            realSum += values[index].Real;
            imaginarySum += values[index].Imaginary;
        }

        return new Complex(realSum, imaginarySum);
    }
}
