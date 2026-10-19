using System.Numerics;

namespace VHSDecode.Core.HiFi;

/// <summary>Centered, zero-padded FIR operations matching scipy.signal.resample_poly.</summary>
internal static class BetaHiFiSignalDsp
{
    internal static double[] FirWin(int length, double normalizedCutoff, double beta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 3);
        if ((length & 1) == 0 || normalizedCutoff <= 0 || normalizedCutoff >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedCutoff));
        }

        var taps = new double[length];
        int half = (length - 1) / 2;
        double denominator = BesselI0(beta);
        double sum = 0;
        for (int n = 0; n < length; n++)
        {
            double distance = n - half;
            double argument = normalizedCutoff * distance;
            double sinc = argument == 0 ? 1 : Math.Sin(Math.PI * argument) / (Math.PI * argument);
            double position = distance / half;
            double window = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - position * position))) / denominator;
            taps[n] = normalizedCutoff * sinc * window;
            sum += taps[n];
        }

        for (int n = 0; n < length; n++)
        {
            taps[n] /= sum;
        }

        return taps;
    }

    internal static float[] Decimate(ReadOnlySpan<float> input, int factor, ReadOnlySpan<float> taps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        if (taps.IsEmpty || (taps.Length & 1) == 0)
        {
            throw new ArgumentException("A symmetric odd-length FIR is required.", nameof(taps));
        }

        var output = new float[checked((input.Length + factor - 1) / factor)];
        int half = taps.Length / 2;
        for (int n = 0; n < output.Length; n++)
        {
            int center = checked(n * factor + half);
            int start = Math.Max(0, center - taps.Length + 1);
            int end = Math.Min(input.Length, center + 1);
            if (start >= end)
            {
                continue;
            }

            // The symmetric FIR reverses to increasing contiguous coefficients.
            output[n] = Dot(input.Slice(start, end - start), taps.Slice(start + taps.Length - 1 - center, end - start));
        }

        return output;
    }

    internal static double[] Resample(ReadOnlySpan<double> input, int up, int down)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(up, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(down, 1);
        int divisor = GreatestCommonDivisor(up, down);
        up /= divisor;
        down /= divisor;
        if (up == down)
        {
            return input.ToArray();
        }

        int maximum = Math.Max(up, down);
        double[] taps = FirWin(checked(20 * maximum + 1), 1.0 / maximum, 5.0);
        for (int i = 0; i < taps.Length; i++)
        {
            taps[i] *= up;
        }

        int half = taps.Length / 2;
        var output = new double[checked((int)(((long)input.Length * up + down - 1) / down))];
        for (int n = 0; n < output.Length; n++)
        {
            long center = (long)n * down + half;
            int start = (int)Math.Max(0, (center - taps.Length + up) / up);
            int end = (int)Math.Min(input.Length - 1L, center / up);
            double sum = 0;
            for (int k = start; k <= end; k++)
            {
                int coefficient = checked((int)(center - (long)k * up));
                if ((uint)coefficient < taps.Length)
                {
                    sum += input[k] * taps[coefficient];
                }
            }

            output[n] = sum;
        }

        return output;
    }

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int i = 0;
        Vector<float> accumulator = Vector<float>.Zero;
        for (; i <= left.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            accumulator += new Vector<float>(left.Slice(i)) * new Vector<float>(right.Slice(i));
        }

        float sum = Vector.Sum(accumulator);
        for (; i < left.Length; i++)
        {
            sum += left[i] * right[i];
        }

        return sum;
    }

    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double squared = x * x / 4;
        for (int k = 1; k < 100; k++)
        {
            term *= squared / (k * k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
