using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Dsp;

public sealed record CvbsPulseDetectionResult(
    IReadOnlyList<Pulse> Pulses,
    double Threshold,
    bool Recalibrated);

public static class CvbsPulseDetector
{
    public static CvbsPulseDetectionResult? Refine(
        ReadOnlySpan<double> syncReference,
        IReadOnlyList<Pulse> initialPulses,
        double initialThreshold,
        SyncAnalyzer analyzer,
        VideoOutputConverter converter)
        => RefineCore(
            syncReference,
            initialPulses,
            initialThreshold,
            analyzer,
            converter,
            equalizingMaximumUs: 3.0);

    public static CvbsPulseDetectionResult? RefineLaserDisc(
        ReadOnlySpan<double> syncReference,
        IReadOnlyList<Pulse> initialPulses,
        double initialThreshold,
        SyncAnalyzer analyzer,
        VideoOutputConverter converter)
        => RefineCore(
            syncReference,
            initialPulses,
            initialThreshold,
            analyzer,
            converter,
            equalizingMaximumUs: 2.5);

    private static CvbsPulseDetectionResult? RefineCore(
        ReadOnlySpan<double> syncReference,
        IReadOnlyList<Pulse> initialPulses,
        double initialThreshold,
        SyncAnalyzer analyzer,
        VideoOutputConverter converter,
        double equalizingMaximumUs)
    {
        ArgumentNullException.ThrowIfNull(initialPulses);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(converter);
        if (initialPulses.Count == 0)
        {
            return new CvbsPulseDetectionResult([], initialThreshold, false);
        }

        int longPulseMinimum = (int)analyzer.UsecToSamples(10.0);
        var longPulseIndices = new List<int>();
        var syncMeans = new List<double>();
        int oneMicrosecond = (int)analyzer.UsecToSamples(1.0);
        for (int i = 0; i < initialPulses.Count; i++)
        {
            Pulse pulse = initialPulses[i];
            if (pulse.Length <= longPulseMinimum)
            {
                continue;
            }

            int start = Math.Clamp(pulse.Start + oneMicrosecond, 0, syncReference.Length);
            int end = Math.Clamp(pulse.Start + pulse.Length - oneMicrosecond, start, syncReference.Length);
            if (end > start)
            {
                longPulseIndices.Add(i);
                syncMeans.Add(Mean(syncReference[start..end]));
            }
        }

        if (syncMeans.Count == 0)
        {
            return null;
        }

        double syncLevel = Median(syncMeans);
        if (Math.Abs(converter.HzToIre(syncLevel) - converter.VSyncIre) < 5.0)
        {
            return new CvbsPulseDetectionResult(initialPulses, initialThreshold, false);
        }

        var blankMeans = new List<double>();
        AddBlankMeans(
            longPulseIndices[0] - 5,
            longPulseIndices[0],
            initialPulses,
            syncReference,
            analyzer,
            equalizingMaximumUs,
            blankMeans);
        AddBlankMeans(
            longPulseIndices[^1] + 1,
            longPulseIndices[^1] + 6,
            initialPulses,
            syncReference,
            analyzer,
            equalizingMaximumUs,
            blankMeans);
        if (blankMeans.Count == 0)
        {
            return new CvbsPulseDetectionResult([], double.NaN, true);
        }

        double threshold = (Median(blankMeans) + syncLevel) / 2.0;
        IReadOnlyList<Pulse> pulses = PulseDetection.FindPulses(
            syncReference,
            threshold,
            minimumSyncLength: 0,
            maximumSyncLength: 5000);
        return new CvbsPulseDetectionResult(pulses, threshold, true);
    }

    private static void AddBlankMeans(
        int firstIndex,
        int endIndex,
        IReadOnlyList<Pulse> pulses,
        ReadOnlySpan<double> syncReference,
        SyncAnalyzer analyzer,
        double equalizingMaximumUs,
        List<double> output)
    {
        double minimumLength = analyzer.UsecToSamples(0.75);
        double maximumLength = analyzer.UsecToSamples(equalizingMaximumUs);
        int fiveMicroseconds = (int)analyzer.UsecToSamples(5.0);
        int twentyMicroseconds = (int)analyzer.UsecToSamples(20.0);
        for (int i = Math.Max(0, firstIndex); i < Math.Min(endIndex, pulses.Count); i++)
        {
            Pulse pulse = pulses[i];
            if (!PulseDetection.InRange(pulse.Length, minimumLength, maximumLength))
            {
                continue;
            }

            int start = Math.Clamp(pulse.Start + fiveMicroseconds, 0, syncReference.Length);
            int end = Math.Clamp(pulse.Start + twentyMicroseconds, start, syncReference.Length);
            if (end > start)
            {
                output.Add(Mean(syncReference[start..end]));
            }
        }
    }

    private static double Mean(ReadOnlySpan<double> values)
        => NumpyReduction.MeanFloat64(values);

    private static double Median(List<double> values)
    {
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return (sorted.Length & 1) == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
