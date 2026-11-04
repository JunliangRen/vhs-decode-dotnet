using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.Intrinsics.X86;
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

        (double[] firstLeft, double[] firstRight) = FilterBlock(fieldAudio, filters, 0, blockLength);
        if (firstLeft.Length >= fieldAudio.Left.Length)
        {
            return new LaserDiscAnalogAudioBlock(
                firstLeft,
                firstRight,
                fieldAudio.DecimationFactor,
                UsesFloat32Storage: false);
        }

        var left = new double[fieldAudio.Left.Length];
        var right = new double[fieldAudio.Right.Length];
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
            var padded = new Complex[filter.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                padded[i] = new Complex(raw[i], 0.0);
            }

            PocketFftComplex.ForwardDuccInPlace(padded);
            spectrum = padded;
        }

        NumpyComplexMultiply.ApplyInPlace(spectrum, filter);

        PocketFftComplex.InverseDuccInPlace(spectrum);
        var output = new double[raw.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = spectrum[i].Real + centerFrequencyHz;
        }

        return output;
    }

    // Decode-only single padded field; public Apply retains the original serial path.
    internal static LaserDiscAnalogAudioBlock ApplyForDecode(
        LaserDiscAnalogAudioBlock fieldAudio,
        LaserDiscAnalogAudioFilterSet filters,
        int workerBudget)
    {
        const int parallelBlockLength = 32 * 1024;
        if (workerBudget < 2 || !Avx2.IsSupported
            || fieldAudio is null || filters is null
            || fieldAudio.Left is null || fieldAudio.Right is null
            || filters.Left is null || filters.Right is null
            || filters.Left.Stage2Filter is null || filters.Right.Stage2Filter is null
            || fieldAudio.Left.Length == 0
            || fieldAudio.Left.Length >= parallelBlockLength
            || fieldAudio.Right.Length != fieldAudio.Left.Length
            || filters.Left.Stage2Filter.Length != parallelBlockLength
            || filters.Right.Stage2Filter.Length != parallelBlockLength)
        {
            return Apply(fieldAudio!, filters!);
        }

        int count = fieldAudio.Left.Length;
        float[] left = PrepareChannel(
            fieldAudio.Left,
            0,
            count,
            filters.Left.CenterFrequencyHz);
        int[] peaks = FindPeaks(left, PeakThresholdHz);
        SuppressPeaks(left, peaks);
        float[] right = PrepareChannel(
            fieldAudio.Right,
            0,
            count,
            filters.Right.CenterFrequencyHz);
        SuppressPeaks(right, peaks);

        double[] leftOutput;
        double[] rightOutput;
        if (HasBoundedPreparedAudio(
                left,
                right,
                filters.Left.Stage2Filter,
                filters.Right.Stage2Filter,
                filters.Left.CenterFrequencyHz,
                filters.Right.CenterFrequencyHz))
        {
            (leftOutput, rightOutput) = FilterPreparedChannelsParallel(
                left,
                right,
                filters.Left.Stage2Filter,
                filters.Right.Stage2Filter,
                filters.Left.CenterFrequencyHz,
                filters.Right.CenterFrequencyHz);
        }
        else
        {
            // Preparation already ran once in the same order as FilterBlock.
            leftOutput = FilterChannel(left, filters.Left.Stage2Filter, filters.Left.CenterFrequencyHz);
            rightOutput = FilterChannel(right, filters.Right.Stage2Filter, filters.Right.CenterFrequencyHz);
        }

        return new LaserDiscAnalogAudioBlock(
            leftOutput,
            rightOutput,
            fieldAudio.DecimationFactor,
            UsesFloat32Storage: false);
    }

    private static bool HasBoundedPreparedAudio(
        float[] left,
        float[] right,
        Complex[] leftFilter,
        Complex[] rightFilter,
        double leftCenter,
        double rightCenter)
    {
        // Exact 2^32 limits keep this fixed 32K graph far from double overflow.
        // Integer tests reject special values without quieting their NaN bits.
        if (!IsBoundedAudioValue(leftCenter) || !IsBoundedAudioValue(rightCenter))
        {
            return false;
        }

        foreach (float value in left) if (!IsBoundedAudioValue(value)) return false;
        foreach (float value in right) if (!IsBoundedAudioValue(value)) return false;
        foreach (Complex value in leftFilter)
            if (!IsBoundedAudioValue(value.Real) || !IsBoundedAudioValue(value.Imaginary)) return false;
        foreach (Complex value in rightFilter)
            if (!IsBoundedAudioValue(value.Real) || !IsBoundedAudioValue(value.Imaginary)) return false;
        return true;
    }

    private static bool IsBoundedAudioValue(float value)
        => (BitConverter.SingleToUInt32Bits(value) & 0x7FFF_FFFFU) <= 0x4F80_0000U;

    private static bool IsBoundedAudioValue(double value)
        => (BitConverter.DoubleToUInt64Bits(value) & 0x7FFF_FFFF_FFFF_FFFFUL) <= 0x41F0_0000_0000_0000UL;

    private static (double[] Left, double[] Right) FilterPreparedChannelsParallel(
        float[] left,
        float[] right,
        Complex[] leftFilter,
        Complex[] rightFilter,
        double leftCenter,
        double rightCenter)
    {
        // Keep the task closure out of every public/serial fallback entry.
        Task<double[]> rightTask = Task.Run(() => FilterChannel(right, rightFilter, rightCenter));
        double[]? leftOutput = null;
        double[]? rightOutput = null;
        ExceptionDispatchInfo? leftError = null;
        ExceptionDispatchInfo? rightError = null;
        try
        {
            leftOutput = FilterChannel(left, leftFilter, leftCenter);
        }
        catch (Exception error)
        {
            leftError = ExceptionDispatchInfo.Capture(error);
        }

        try
        {
            rightOutput = rightTask.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            rightError = ExceptionDispatchInfo.Capture(error);
        }

        leftError?.Throw();
        rightError?.Throw();
        return (leftOutput!, rightOutput!);
    }
}
