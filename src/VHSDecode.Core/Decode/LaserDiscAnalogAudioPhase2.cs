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
            return new LaserDiscAnalogAudioBlock(
                left,
                right,
                fieldAudio.DecimationFactor,
                UsesFloat32Storage: false);
        }

        CopyAsFloat32(firstLeft, 0, left, 0, firstLeft.Length);
        CopyAsFloat32(firstRight, 0, right, 0, firstRight.Length);
        int jump = blockLength - OverlapSkip;
        int outputStart = firstLeft.Length;
        for (int sample = jump; sample < fieldAudio.Left.Length - jump; sample += jump)
        {
            (double[] blockLeft, double[] blockRight) = FilterBlock(fieldAudio, filters, sample, blockLength);
            int copyLength = blockLeft.Length - OverlapSkip;
            CopyAsFloat32(blockLeft, OverlapSkip, left, outputStart, copyLength);
            CopyAsFloat32(blockRight, OverlapSkip, right, outputStart, copyLength);
            outputStart += copyLength;
        }

        (double[] finalLeft, double[] finalRight) = FilterBlock(
            fieldAudio,
            filters,
            fieldAudio.Left.Length - blockLength - 1,
            blockLength);
        int finalLength = finalLeft.Length - OverlapSkip;
        int finalStart = left.Length - finalLength;
        CopyAsFloat32(finalLeft, OverlapSkip, left, finalStart, finalLength);
        CopyAsFloat32(finalRight, OverlapSkip, right, finalStart, finalLength);
        return new LaserDiscAnalogAudioBlock(
            left,
            right,
            fieldAudio.DecimationFactor,
            UsesFloat32Storage: true);
    }

    private static void CopyAsFloat32(
        double[] source,
        int sourceIndex,
        double[] destination,
        int destinationIndex,
        int length)
    {
        for (int i = 0; i < length; i++)
        {
            destination[destinationIndex + i] = (float)source[sourceIndex + i];
        }
    }

    private static (double[] Left, double[] Right) FilterBlock(
        LaserDiscAnalogAudioBlock fieldAudio,
        LaserDiscAnalogAudioFilterSet filters,
        int start,
        int blockLength)
    {
        int count = Math.Min(blockLength, fieldAudio.Left.Length - start);
        float[] left = PrepareChannel(
            fieldAudio.Left,
            start,
            count,
            filters.Left.CenterFrequencyHz);
        int[] peaks = FindPeaks(left, PeakThresholdHz);
        SuppressPeaks(left, peaks);

        float[] right = PrepareChannel(
            fieldAudio.Right,
            start,
            count,
            filters.Right.CenterFrequencyHz);
        SuppressPeaks(right, peaks);
        return (
            FilterChannel(left, filters.Left.Stage2Filter, filters.Left.CenterFrequencyHz),
            FilterChannel(right, filters.Right.Stage2Filter, filters.Right.CenterFrequencyHz));
    }

    private static float[] PrepareChannel(
        double[] source,
        int start,
        int count,
        double centerFrequencyHz)
    {
        var raw = new float[count];
        float center = (float)centerFrequencyHz;
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (float)source[start + i] - center;
        }

        return raw;
    }

    private static int[] FindPeaks(float[] source, double low)
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

    private static void SuppressPeaks(float[] source, IReadOnlyList<int> peaks)
    {
        foreach (int peak in peaks)
        {
            int start = Math.Max(0, peak - PeakReplacementRadius);
            int end = Math.Min(peak + PeakReplacementRadius, source.Length);
            Array.Clear(source, start, end - start);
        }
    }

    private static double[] FilterChannel(
        float[] raw,
        Complex[] filter,
        double centerFrequencyHz)
    {
        Complex[] spectrum;
        if (raw.Length == filter.Length)
        {
            Complex32[] halfSpectrum = PocketFftReal32.ForwardDucc(raw);
            spectrum = new Complex[raw.Length];
            spectrum[0] = new Complex(halfSpectrum[0].Real, -0.0);
            spectrum[halfSpectrum.Length - 1] = new Complex(
                halfSpectrum[^1].Real,
                -0.0);
            for (int i = 1; i < halfSpectrum.Length - 1; i++)
            {
                Complex32 value = halfSpectrum[i];
                spectrum[i] = new Complex(value.Real, value.Imaginary);
                spectrum[^i] = new Complex(value.Real, -value.Imaginary);
            }
        }
        else
        {
            var padded = new double[filter.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                padded[i] = raw[i];
            }

            spectrum = PocketFftComplex.ForwardDucc(padded);
        }

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

        PocketFftComplex.InverseDuccInPlace(spectrum);
        var output = new double[raw.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = spectrum[i].Real + centerFrequencyHz;
        }

        return output;
    }
}
