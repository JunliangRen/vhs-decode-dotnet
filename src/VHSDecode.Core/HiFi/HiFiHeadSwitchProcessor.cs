using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

internal readonly record struct HiFiHeadSwitchPeak(
    int Center,
    double Start,
    double End,
    double Prominence);

internal sealed record HiFiHeadSwitchDetection(
    float[] Filtered,
    float[] Absolute,
    float Mean,
    float StandardDeviation,
    IReadOnlyList<HiFiHeadSwitchPeak> Peaks);

internal sealed class HiFiHeadSwitchProcessor
{
    private const int FilterOrder = 22;
    private const double FilterStopAttenuationDb = 200.0;
    private const double FilterCutoffHz = 28_000.0;
    private const double PeakProminenceLimit = 3.0;
    private const double InterpolationPaddingSeconds = 35e-6;
    private const double NeighborRangeSeconds = 200e-6;
    private readonly SosSection[] _sections;

    internal HiFiHeadSwitchProcessor(int sampleRateHz, double fieldRateHz)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(fieldRateHz) || fieldRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldRateHz));
        }

        SampleRateHz = sampleRateHz;
        FieldRateHz = fieldRateHz;
        _sections = IirFilterDesign.ChebyshevTypeIIHighPassSos(
            FilterOrder,
            FilterStopAttenuationDb,
            FilterCutoffHz,
            sampleRateHz);
    }

    internal int SampleRateHz { get; }
    internal double FieldRateHz { get; }
    internal ReadOnlyMemory<SosSection> Sections => _sections;

    internal HiFiHeadSwitchDetection Detect(ReadOnlySpan<float> audio)
    {
        float[] filtered = SosFilter.ApplyForwardBackwardFloat32(_sections, audio);
        var absolute = new float[filtered.Length];
        for (int i = 0; i < absolute.Length; i++)
        {
            absolute[i] = MathF.Abs(filtered[i]);
        }

        (float mean, float standardDeviation) =
            HiFiAudioProcessing.NumbaFastMeanStandardDeviation(filtered);
        double driftHz = FieldRateHz * 0.1;
        double peakDistanceSeconds = 1.0 / (FieldRateHz + driftHz);
        List<SciPyPeak> primaryPeaks = SciPyPeakFinder.Find(
            absolute,
            minimumThreshold: null,
            distance: peakDistanceSeconds * SampleRateHz,
            minimumProminence: null,
            minimumWidth: 1.0);
        float neighborThreshold = mean + standardDeviation;
        int neighborSearchWidth = checked((int)Math.Round(
            NeighborRangeSeconds * SampleRateHz,
            MidpointRounding.ToEven));

        var peaks = new List<HiFiHeadSwitchPeak>();
        foreach (SciPyPeak peak in primaryPeaks)
        {
            peaks.Add(ToHeadSwitchPeak(peak, 0));
            int start = Math.Max(0, checked((int)Math.Floor(
                peak.LeftIntersection - neighborSearchWidth)));
            int end = Math.Min(absolute.Length, checked((int)Math.Ceiling(
                peak.RightIntersection + neighborSearchWidth)));
            List<SciPyPeak> neighbors = SciPyPeakFinder.Find(
                absolute.AsSpan(start, end - start),
                minimumThreshold: neighborThreshold,
                distance: 1.0,
                minimumProminence: 0.25,
                minimumWidth: 1.0);
            peaks.AddRange(neighbors.Select(neighbor => ToHeadSwitchPeak(neighbor, start)));
        }

        return new HiFiHeadSwitchDetection(
            filtered,
            absolute,
            mean,
            standardDeviation,
            peaks);
    }

    internal List<HiFiSampleRange> CalculateBoundaries(
        IReadOnlyList<HiFiHeadSwitchPeak> peaks)
    {
        int paddingSamples = checked((int)Math.Round(
            InterpolationPaddingSeconds * SampleRateHz,
            MidpointRounding.ToEven));
        var boundaries = new List<HiFiSampleRange>(peaks.Count);
        foreach (HiFiHeadSwitchPeak peak in peaks)
        {
            double widthPadding = peak.Prominence * paddingSamples;
            boundaries.Add(new HiFiSampleRange(
                checked((int)Math.Floor(peak.Start - widthPadding)),
                checked((int)Math.Ceiling(peak.End + widthPadding))));
        }

        boundaries.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<HiFiSampleRange>(boundaries.Count);
        foreach (HiFiSampleRange boundary in boundaries)
        {
            if (merged.Count == 0 || merged[^1].End < boundary.Start)
            {
                merged.Add(boundary);
            }
            else
            {
                merged[^1] = merged[^1] with
                {
                    End = Math.Max(merged[^1].End, boundary.End)
                };
            }
        }

        return merged;
    }

    internal static float[] InterpolateBoundaries(
        ReadOnlySpan<float> audio,
        IReadOnlyList<HiFiSampleRange> boundaries)
    {
        float[] output = audio.ToArray();
        float[] interpolatorInput = audio.ToArray();
        var valid = Enumerable.Repeat(true, audio.Length).ToArray();
        foreach (HiFiSampleRange boundary in boundaries)
        {
            int sliceStart = NormalizeSliceIndex(boundary.Start, audio.Length);
            int sliceEnd = NormalizeSliceIndex(boundary.End, audio.Length);
            for (int i = sliceStart; i < sliceEnd; i++)
            {
                valid[i] = false;
            }
        }

        foreach (HiFiSampleRange boundary in boundaries)
        {
            int smoothingSize = 1 + boundary.End - boundary.Start;
            if (boundary.Start < 0)
            {
                int end = Math.Min(boundary.End, output.Length);
                if (end > 0)
                {
                    output.AsSpan(0, end).Fill(output[boundary.End]);
                }

                continue;
            }

            if (boundary.End > audio.Length)
            {
                if (boundary.Start < output.Length)
                {
                    output.AsSpan(boundary.Start).Fill(output[boundary.Start]);
                }

                continue;
            }

            int left = boundary.Start - 1;
            while (left >= 0 && !valid[left])
            {
                left--;
            }

            int right = boundary.End;
            while (right < audio.Length && !valid[right])
            {
                right++;
            }

            if (left < 0 || right >= audio.Length)
            {
                throw new InvalidOperationException("Head-switch interpolation has no samples outside the requested gap.");
            }

            double denominator = right - left;
            for (int i = boundary.Start; i < boundary.End; i++)
            {
                double rightWeight = (i - left) / denominator;
                double leftWeight = (right - i) / denominator;
                output[i] = (float)((rightWeight * interpolatorInput[right])
                    + (leftWeight * interpolatorInput[left]));
            }

            int sliceStart = NormalizeSliceIndex(boundary.Start - smoothingSize, output.Length);
            int sliceEnd = NormalizeSliceIndex(boundary.End + smoothingSize, output.Length);
            if (sliceStart >= sliceEnd)
            {
                continue;
            }

            float[] smoothingInput = output.AsSpan(sliceStart, sliceEnd - sliceStart).ToArray();
            int halfWindow = checked((int)Math.Ceiling(smoothingSize / 4.0));
            for (int i = 0; i < smoothingInput.Length; i++)
            {
                int meanStart = Math.Max(0, i - halfWindow);
                int meanEnd = Math.Min(smoothingInput.Length, i + halfWindow + 1);
                output[sliceStart + i] = HiFiAudioProcessing.NumbaFastMean(
                    smoothingInput.AsSpan(meanStart, meanEnd - meanStart));
            }
        }

        return output;
    }

    internal float[] RemoveNoise(ReadOnlySpan<float> audio)
    {
        HiFiHeadSwitchDetection detection = Detect(audio);
        List<HiFiSampleRange> boundaries = CalculateBoundaries(detection.Peaks);
        return InterpolateBoundaries(audio, boundaries);
    }

    private static HiFiHeadSwitchPeak ToHeadSwitchPeak(SciPyPeak peak, int offset)
        => new(
            peak.Index + offset,
            peak.LeftIntersection + offset,
            peak.RightIntersection + offset,
            Math.Max(Math.Min(peak.Prominence, PeakProminenceLimit), 0.0));

    private static int NormalizeSliceIndex(int index, int length)
        => index < 0
            ? Math.Max(0, length + index)
            : Math.Min(index, length);
}
