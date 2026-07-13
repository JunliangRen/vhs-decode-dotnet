using System.Numerics;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

public static class LaserDiscAnalogAudioPhase2
{
    private const int OverlapSkip = 512;
    private const double PeakThresholdHz = 500_000.0;
    private const int PeakReplacementRadius = 8;

    public static LaserDiscAnalogAudioBlock Apply(
        LaserDiscAnalogAudioBlock fieldAudio,
        LaserDiscAnalogAudioFilterSet filters)
    {
        ArgumentNullException.ThrowIfNull(fieldAudio);
        ArgumentNullException.ThrowIfNull(filters);
        if (fieldAudio.Left.Length != fieldAudio.Right.Length)
        {
            throw new ArgumentException("LD analog audio channel lengths did not match.", nameof(fieldAudio));
        }

        int blockLength = filters.Left.Stage2Filter.Length;
        if (blockLength <= OverlapSkip || filters.Right.Stage2Filter.Length != blockLength)
        {
            throw new ArgumentException("LD analog audio phase-2 filters must have matching lengths greater than 512.", nameof(filters));
        }

        if (fieldAudio.Left.Length == 0)
        {
            return fieldAudio;
        }

        var left = new double[fieldAudio.Left.Length];
        var right = new double[fieldAudio.Right.Length];
        (double[] firstLeft, double[] firstRight) = FilterBlock(fieldAudio, filters, 0, blockLength);
        if (firstLeft.Length >= left.Length)
        {
            Array.Copy(firstLeft, left, left.Length);
            Array.Copy(firstRight, right, right.Length);
            return new LaserDiscAnalogAudioBlock(left, right, fieldAudio.DecimationFactor);
        }

        Array.Copy(firstLeft, left, firstLeft.Length);
        Array.Copy(firstRight, right, firstRight.Length);
        int jump = blockLength - OverlapSkip;
        int outputStart = firstLeft.Length;
        for (int sample = jump; sample < fieldAudio.Left.Length - jump; sample += jump)
        {
            (double[] blockLeft, double[] blockRight) = FilterBlock(fieldAudio, filters, sample, blockLength);
            int copyLength = blockLeft.Length - OverlapSkip;
            Array.Copy(blockLeft, OverlapSkip, left, outputStart, copyLength);
            Array.Copy(blockRight, OverlapSkip, right, outputStart, copyLength);
            outputStart += copyLength;
        }

        (double[] finalLeft, double[] finalRight) = FilterBlock(
            fieldAudio,
            filters,
            fieldAudio.Left.Length - blockLength - 1,
            blockLength);
        int finalLength = finalLeft.Length - OverlapSkip;
        int finalStart = left.Length - finalLength;
        Array.Copy(finalLeft, OverlapSkip, left, finalStart, finalLength);
        Array.Copy(finalRight, OverlapSkip, right, finalStart, finalLength);
        return new LaserDiscAnalogAudioBlock(left, right, fieldAudio.DecimationFactor);
    }

    private static (double[] Left, double[] Right) FilterBlock(
        LaserDiscAnalogAudioBlock fieldAudio,
        LaserDiscAnalogAudioFilterSet filters,
        int start,
        int blockLength)
    {
        int count = Math.Min(blockLength, fieldAudio.Left.Length - start);
        double[] left = PrepareChannel(fieldAudio.Left, start, count, filters.Left.CenterFrequencyHz);
        int[] peaks = FindPeaks(left, PeakThresholdHz);
        SuppressPeaks(left, peaks);

        double[] right = PrepareChannel(fieldAudio.Right, start, count, filters.Right.CenterFrequencyHz);
        SuppressPeaks(right, peaks);
        return (
            FilterChannel(left, filters.Left.Stage2Filter, filters.Left.CenterFrequencyHz),
            FilterChannel(right, filters.Right.Stage2Filter, filters.Right.CenterFrequencyHz));
    }

    private static double[] PrepareChannel(double[] source, int start, int count, double centerFrequencyHz)
    {
        var raw = new double[count];
        float center = (float)centerFrequencyHz;
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (float)source[start + i] - center;
        }

        return raw;
    }

    private static int[] FindPeaks(double[] source, double low)
    {
        if (source.Length < 2)
        {
            return [];
        }

        double last = source[^1] < low ? 0.0 : source[^1];
        var peaks = new List<int>();
        for (int i = 0; i < source.Length - 1; i++)
        {
            double current = source[i] < low ? 0.0 : source[i];
            double next = source[i + 1] < low ? 0.0 : source[i + 1];
            if (current > last && next > current)
            {
                peaks.Add(i - 1);
            }
        }

        return peaks.ToArray();
    }

    private static void SuppressPeaks(double[] source, IReadOnlyList<int> peaks)
    {
        foreach (int peak in peaks)
        {
            int start = Math.Max(0, peak - PeakReplacementRadius);
            int end = Math.Min(peak + PeakReplacementRadius, source.Length);
            Array.Clear(source, start, end - start);
        }
    }

    private static double[] FilterChannel(double[] raw, Complex[] filter, double centerFrequencyHz)
    {
        var padded = new double[filter.Length];
        Array.Copy(raw, padded, raw.Length);
        Complex[] spectrum = PocketFftComplex.ForwardDucc(padded);
        for (int i = 0; i < spectrum.Length; i++)
        {
            Complex left = spectrum[i];
            Complex right = filter[i];
            spectrum[i] = new Complex(
                Math.FusedMultiplyAdd(
                    left.Real,
                    right.Real,
                    -(left.Imaginary * right.Imaginary)),
                Math.FusedMultiplyAdd(
                    left.Real,
                    right.Imaginary,
                    left.Imaginary * right.Real));
        }

        Complex[] filtered = PocketFftComplex.InverseDucc(spectrum);
        var output = new double[raw.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = filtered[i].Real + centerFrequencyHz;
        }

        return output;
    }
}
