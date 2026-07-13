namespace VHSDecode.Core.Dsp;

public static class ChromaTrapFilter
{
    public static double[] Apply(ReadOnlySpan<double> luminance, double sampleRateHz, double fscHz)
    {
        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (fscHz <= 0.0 || fscHz >= sampleRateHz / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(fscHz));
        }

        if (luminance.IsEmpty)
        {
            return [];
        }

        double fscMHz = fscHz / 1_000_000.0;
        int targetRate = checked((int)((fscMHz * 8.0) * 1_000_000.0));
        (int numerator, int denominator) = SoxrQuickResampler.ApproximateRatio(
            targetRate / sampleRateHz,
            maxDenominator: 1000);
        double[] resampled = SoxrQuickResampler.Resample(luminance, denominator, numerator);
        var combed = new double[resampled.Length];
        const int delay = 4;
        for (int i = 0; i < combed.Length; i++)
        {
            int delayedIndex = i >= delay ? i - delay : combed.Length + i - delay;
            combed[i] = (resampled[i] + resampled[delayedIndex]) * 0.5;
        }

        double[] result = SoxrQuickResampler.Resample(combed, numerator, denominator);
        return PadOrTruncate(result, luminance);
    }

    private static double[] PadOrTruncate(ReadOnlySpan<double> data, ReadOnlySpan<double> filler)
    {
        if (filler.Length > data.Length)
        {
            int missing = filler.Length - data.Length;
            int start = data.Length - missing;
            if (start < 0)
            {
                start = Math.Max(0, filler.Length + start);
            }

            int stop = Math.Min(data.Length, filler.Length);
            int appendLength = Math.Max(0, stop - start);
            var output = new double[data.Length + appendLength];
            data.CopyTo(output);
            filler.Slice(start, appendLength).CopyTo(output.AsSpan(data.Length));
            return output;
        }

        return data[(data.Length - filler.Length)..].ToArray();
    }
}
