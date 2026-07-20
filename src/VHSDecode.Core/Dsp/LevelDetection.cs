namespace VHSDecode.Core.Dsp;

public sealed record SerrationLevelRefinement(
    double SyncLevel,
    double BlankLevel,
    int VsyncPulseCount,
    int PulseCount);

public enum SerrationLevelFailureKind
{
    None,
    MissingLevels,
    NonFiniteLevels,
    LevelCheckFailed
}

public static class LevelDetection
{
    public static (int[] Locations, double[] Means) FallbackVsyncLocationMeans(
        ReadOnlySpan<double> demod05,
        IReadOnlyList<Pulse> pulses,
        double sampleFrequencyMHz,
        double minimumLength,
        double maximumLength)
    {
        int meanPositionOffset = (int)sampleFrequencyMHz;
        var locations = new List<int>();
        var means = new List<double>();

        for (int i = 0; i < pulses.Count; i++)
        {
            Pulse pulse = pulses[i];
            if (pulse.Length <= minimumLength || pulse.Length >= maximumLength)
            {
                continue;
            }

            int start = pulse.Start + meanPositionOffset;
            int end = pulse.Start + pulse.Length - meanPositionOffset;
            if (start < 0 || end > demod05.Length || end <= start)
            {
                throw new ArgumentException("Pulse mean window falls outside the demodulated data.");
            }

            locations.Add(i);
            means.Add(Mean(demod05[start..end]));
        }

        return (locations.ToArray(), means.ToArray());
    }

    public static (double SyncLevel, double BlankLevel)? FindSyncLevels(
        ReadOnlySpan<double> data,
        double lineFrequency,
        int divisor = 1)
    {
        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor));
        }

        if (data.IsEmpty)
        {
            return null;
        }

        if (divisor > 1)
        {
            double[] reduced = new double[(data.Length + divisor - 1) / divisor];
            for (int i = 0, source = 0; i < reduced.Length; i++, source += divisor)
            {
                reduced[i] = data[source];
            }

            return FindSyncLevelsCore(reduced, Math.Max(1.0, lineFrequency / divisor));
        }

        return FindSyncLevelsCore(data, lineFrequency);
    }

    public static SerrationLevelRefinement? RefineSerrationLevels(
        ReadOnlySpan<double> demodulatedLowPass,
        double initialSyncLevel,
        double initialBlankLevel,
        SyncAnalyzer analyzer,
        double referenceSyncLevel,
        double hzIre)
        => RefineSerrationLevels(
            demodulatedLowPass,
            initialSyncLevel,
            initialBlankLevel,
            analyzer,
            referenceSyncLevel,
            hzIre,
            out _);

    public static SerrationLevelRefinement? RefineSerrationLevels(
        ReadOnlySpan<double> demodulatedLowPass,
        double initialSyncLevel,
        double initialBlankLevel,
        SyncAnalyzer analyzer,
        double referenceSyncLevel,
        double hzIre,
        out SerrationLevelFailureKind failureKind)
        => RefineSerrationLevels(
            demodulatedLowPass,
            initialSyncLevel,
            initialBlankLevel,
            analyzer,
            referenceSyncLevel,
            hzIre,
            out failureKind,
            out _);

    public static SerrationLevelRefinement? RefineSerrationLevels(
        ReadOnlySpan<double> demodulatedLowPass,
        double initialSyncLevel,
        double initialBlankLevel,
        SyncAnalyzer analyzer,
        double referenceSyncLevel,
        double hzIre,
        out SerrationLevelFailureKind failureKind,
        out double? measuredSyncLevel)
    {
        failureKind = SerrationLevelFailureKind.MissingLevels;
        measuredSyncLevel = null;
        if (demodulatedLowPass.IsEmpty)
        {
            return null;
        }

        double threshold = (initialSyncLevel + initialBlankLevel) / 2.0;
        int minimumPulseLength = Math.Max(0, (int)Math.Ceiling(analyzer.UsecToSamples(analyzer.EqualizingPulseUs) / 8.0));
        int maximumPulseLength = Math.Max(1, (int)Math.Floor(analyzer.NominalLineLength * 5.0));
        IReadOnlyList<Pulse> pulses = PulseDetection.FindPulses(
            demodulatedLowPass,
            threshold,
            minimumPulseLength,
            maximumPulseLength);

        return RefineSerrationLevelsFromPulses(
            demodulatedLowPass,
            pulses,
            analyzer,
            referenceSyncLevel,
            hzIre,
            out failureKind,
            out measuredSyncLevel);
    }

    private static SerrationLevelRefinement? RefineSerrationLevelsFromPulses(
        ReadOnlySpan<double> demodulatedLowPass,
        IReadOnlyList<Pulse> pulses,
        SyncAnalyzer analyzer,
        double referenceSyncLevel,
        double hzIre,
        out SerrationLevelFailureKind failureKind,
        out double? measuredSyncLevel)
    {
        failureKind = SerrationLevelFailureKind.MissingLevels;
        measuredSyncLevel = null;

        double vsyncLength = analyzer.UsecToSamples(analyzer.VSyncPulseUs);
        (int[] locations, double[] means) = FallbackVsyncLocationMeans(
            demodulatedLowPass,
            pulses,
            analyzer.SampleRateMHz,
            vsyncLength * 0.8,
            vsyncLength * 1.2);
        if (means.Length == 0)
        {
            return null;
        }

        double syncLevel = Median(means);
        measuredSyncLevel = syncLevel;
        double[] blackMeans = PulsesBlackLevelMeans(
            demodulatedLowPass,
            pulses,
            locations,
            analyzer.SampleRateMHz);
        if (blackMeans.Length == 0)
        {
            failureKind = SerrationLevelFailureKind.NonFiniteLevels;
            return null;
        }

        double blankLevel = Median(blackMeans);
        if (!double.IsFinite(syncLevel)
            || !double.IsFinite(blankLevel)
            || blankLevel < syncLevel)
        {
            failureKind = SerrationLevelFailureKind.NonFiniteLevels;
            return null;
        }

        if (means.Length <= 3
            || !VsyncSerrationDetector.CheckLevels(
                demodulatedLowPass,
                referenceSyncLevel,
                syncLevel,
                blankLevel,
                referenceSyncLevel,
                hzIre))
        {
            failureKind = SerrationLevelFailureKind.LevelCheckFailed;
            return null;
        }

        failureKind = SerrationLevelFailureKind.None;
        return new SerrationLevelRefinement(syncLevel, blankLevel, means.Length, pulses.Count);
    }

    public static SerrationLevelRefinement? SearchFallbackSerrationLevels(
        ReadOnlySpan<double> demodulatedLowPass,
        SyncAnalyzer analyzer,
        int divisor,
        double blankLevel,
        double referenceSyncLevel,
        double hzIre,
        bool checkLongPulses)
        => SearchFallbackSerrationLevels(
            demodulatedLowPass,
            analyzer,
            divisor,
            blankLevel,
            referenceSyncLevel,
            hzIre,
            checkLongPulses,
            out _);

    public static SerrationLevelRefinement? SearchFallbackSerrationLevels(
        ReadOnlySpan<double> demodulatedLowPass,
        SyncAnalyzer analyzer,
        int divisor,
        double blankLevel,
        double referenceSyncLevel,
        double hzIre,
        bool checkLongPulses,
        out SerrationLevelFailureKind failureKind)
        => SearchFallbackSerrationLevels(
            demodulatedLowPass,
            analyzer,
            divisor,
            blankLevel,
            referenceSyncLevel,
            hzIre,
            checkLongPulses,
            out failureKind,
            out _);

    public static SerrationLevelRefinement? SearchFallbackSerrationLevels(
        ReadOnlySpan<double> demodulatedLowPass,
        SyncAnalyzer analyzer,
        int divisor,
        double blankLevel,
        double referenceSyncLevel,
        double hzIre,
        bool checkLongPulses,
        out SerrationLevelFailureKind failureKind,
        out double? measuredSyncLevel)
    {
        failureKind = SerrationLevelFailureKind.MissingLevels;
        measuredSyncLevel = null;
        if (demodulatedLowPass.IsEmpty || divisor <= 0 || hzIre == 0.0)
        {
            return null;
        }

        double minimumSync = demodulatedLowPass[0];
        for (int i = 1; i < demodulatedLowPass.Length; i++)
        {
            minimumSync = Math.Min(minimumSync, demodulatedLowPass[i]);
        }

        double minimumVsyncLength = analyzer.UsecToSamples(analyzer.VSyncPulseUs) * 0.8;
        double minimumLongPulseLength = analyzer.UsecToSamples(analyzer.VSyncPulseUs) * 2.6;
        double maximumLongPulseLength = analyzer.NominalLineLength * 5.0;
        int previousVsyncCount = 0;
        int previousLongPulseCount = 0;
        double previousMinimumSync = minimumSync;
        bool foundCandidate = false;
        bool checkNext = true;
        IReadOnlyList<Pulse> pulses = [];

        for (int retry = 0; retry < 30; retry++)
        {
            double threshold = (minimumSync + blankLevel) / 2.0;
            pulses = FindReducedPulses(
                demodulatedLowPass,
                threshold,
                analyzer,
                divisor);
            if (pulses.Count > 200)
            {
                int vsyncCount = pulses.Count(pulse => pulse.Length > minimumVsyncLength);
                int longPulseCount = checkLongPulses && vsyncCount <= 2
                    ? pulses.Count(pulse => PulseDetection.InRange(
                        pulse.Length,
                        minimumLongPulseLength,
                        maximumLongPulseLength))
                    : 0;
                if (vsyncCount > 4 || longPulseCount >= 1)
                {
                    if ((vsyncCount == 12 || longPulseCount == 2) && !checkNext)
                    {
                        break;
                    }

                    if (!foundCandidate
                        || vsyncCount > previousVsyncCount
                        || longPulseCount > previousLongPulseCount)
                    {
                        foundCandidate = true;
                        previousVsyncCount = vsyncCount;
                        previousLongPulseCount = longPulseCount;
                        previousMinimumSync = minimumSync;
                        checkNext = true;
                    }
                    else if (vsyncCount < previousVsyncCount
                        || longPulseCount < previousLongPulseCount
                        || !checkNext)
                    {
                        minimumSync = previousMinimumSync;
                        pulses = FindReducedPulses(
                            demodulatedLowPass,
                            (minimumSync + blankLevel) / 2.0,
                            analyzer,
                            divisor: 1);
                        break;
                    }
                    else
                    {
                        checkNext = false;
                    }
                }
            }

            minimumSync += hzIre * 5.0;
        }

        return RefineSerrationLevelsFromPulses(
            demodulatedLowPass,
            pulses,
            analyzer,
            referenceSyncLevel,
            hzIre,
            out failureKind,
            out measuredSyncLevel);
    }

    public static double[] PulsesBlackLevelMeans(
        ReadOnlySpan<double> demodulatedLowPass,
        IReadOnlyList<Pulse> pulses,
        IReadOnlyList<int> vsyncLocations,
        double sampleFrequencyMHz)
    {
        if (vsyncLocations.Count == 0 || pulses.Count == 0)
        {
            return [];
        }

        int beforeFirst = vsyncLocations[0];
        int afterLast = vsyncLocations[^1];
        int lastIndex = pulses.Count - 1;
        if (vsyncLocations.Count != 12)
        {
            while (beforeFirst > 1
                && pulses[beforeFirst].Start - pulses[beforeFirst - 1].Start < 600)
            {
                beforeFirst--;
            }

            while (afterLast < lastIndex
                && pulses[afterLast].Start - pulses[afterLast + 1].Start < 600)
            {
                afterLast++;
            }
        }

        var indices = new List<int>();
        if (beforeFirst > 1)
        {
            for (int i = Math.Max(beforeFirst - 5, 1); i < beforeFirst; i++)
            {
                indices.Add(i);
            }
        }

        if (afterLast < lastIndex - 1)
        {
            for (int i = afterLast + 1; i < Math.Max(afterLast + 6, lastIndex); i++)
            {
                indices.Add(i);
            }
        }

        var means = new List<double>();
        foreach (int index in indices)
        {
            if (index < 0 || index >= pulses.Count)
            {
                continue;
            }

            Pulse pulse = pulses[index];
            if (!PulseDetection.InRange(
                    pulse.Length,
                    sampleFrequencyMHz * 0.75,
                    sampleFrequencyMHz * 3.0))
            {
                continue;
            }

            int start = Math.Clamp((int)(pulse.Start + (sampleFrequencyMHz * 5.0)), 0, demodulatedLowPass.Length);
            int end = Math.Clamp((int)(pulse.Start + (sampleFrequencyMHz * 20.0)), start, demodulatedLowPass.Length);
            if (end > start)
            {
                means.Add(Mean(demodulatedLowPass[start..end]));
            }
        }

        return means.ToArray();
    }

    private static IReadOnlyList<Pulse> FindReducedPulses(
        ReadOnlySpan<double> data,
        double threshold,
        SyncAnalyzer analyzer,
        int divisor)
    {
        int minimumLength = Math.Max(0, (int)Math.Ceiling(
            (analyzer.UsecToSamples(analyzer.EqualizingPulseUs) / 8.0) / divisor));
        int maximumLength = Math.Max(1, (int)Math.Floor(
            (analyzer.NominalLineLength * 5.0) / divisor));
        if (divisor == 1)
        {
            return PulseDetection.FindPulses(data, threshold, minimumLength, maximumLength);
        }

        var reduced = new double[(data.Length + divisor - 1) / divisor];
        for (int i = 0, source = 0; i < reduced.Length; i++, source += divisor)
        {
            reduced[i] = data[source];
        }

        return PulseDetection.FindPulses(reduced, threshold, minimumLength, maximumLength)
            .Select(pulse => new Pulse(pulse.Start * divisor, pulse.Length * divisor))
            .ToArray();
    }

    private static (double SyncLevel, double BlankLevel)? FindSyncLevelsCore(
        ReadOnlySpan<double> data,
        double lineFrequency)
    {
        double syncMinimum = data[0];
        double maximum = data[0];
        for (int i = 1; i < data.Length; i++)
        {
            syncMinimum = Math.Min(syncMinimum, data[i]);
            maximum = Math.Max(maximum, data[i]);
        }

        double threshold = syncMinimum + ((maximum - syncMinimum) / 15.0);
        int offset = 0;
        while (true)
        {
            int searchStart = FindFirst(data, offset, value => value < threshold);
            if (searchStart < 0)
            {
                return null;
            }

            int nextCrossRaw = FindFirst(data, searchStart, value => value >= threshold);
            if (nextCrossRaw < 0)
            {
                return null;
            }

            int nextCross = nextCrossRaw + (int)(1.5 * lineFrequency);
            if (nextCross >= data.Length)
            {
                return null;
            }

            double blankLevel = data[nextCross] + offset;
            if (blankLevel > threshold)
            {
                return (syncMinimum, blankLevel);
            }

            offset += (int)(lineFrequency * 50.0);
            if (offset > data.Length - 10)
            {
                return null;
            }
        }
    }

    private static double Mean(ReadOnlySpan<double> data)
        => NumpyReduction.MeanFloat64(data);

    private static double Median(ReadOnlySpan<double> data)
        => NumpyReduction.MedianFloat64(data);

    private static int FindFirst(ReadOnlySpan<double> data, int start, Func<double, bool> predicate)
    {
        for (int i = Math.Max(0, start); i < data.Length; i++)
        {
            if (predicate(data[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
