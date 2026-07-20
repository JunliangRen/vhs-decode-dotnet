using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Dsp;

public sealed record VBlankPulseGroup(
    double PreviousHSync,
    double Equalizing1Start,
    double? VSyncStart,
    double Equalizing2Start,
    double Equalizing2End,
    double FollowingHSync);

public sealed record VBlankSyncEstimate(
    double Line0Location,
    double FirstHSyncLocation,
    double FirstHSyncLine,
    int ValidDistanceCount,
    double UnalignedFirstHSyncLocation = double.NaN);

public sealed record VBlankSyncConsensus(
    VBlankSyncEstimate? First,
    VBlankSyncEstimate? Last,
    VBlankSyncEstimate? Combined);

public static class VBlankSyncResolver
{
    private const double VSyncToleranceLines = 0.5;

    private readonly record struct SyncMarker(double Sample, double Line);

    private readonly record struct DistanceAccumulator(
        double FirstHSyncLocationSum,
        double DistanceOffsetSum,
        int Count)
    {
        public static DistanceAccumulator operator +(DistanceAccumulator left, DistanceAccumulator right)
            => new(
                left.FirstHSyncLocationSum + right.FirstHSyncLocationSum,
                left.DistanceOffsetSum + right.DistanceOffsetSum,
                left.Count + right.Count);
    }

    private readonly record struct FieldBoundaryLengths(
        double FirstHSync,
        double FirstEqualizing2,
        double LastHSync,
        double LastEqualizing2);

    public static VBlankPulseGroup? FindFirstGroup(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        double? minimumEqualizing1Start = null,
        int blankLengthThreshold = 0)
    {
        if (blankLengthThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blankLengthThreshold));
        }

        for (int start = 1; start < pulses.Count; start++)
        {
            ClassifiedSyncPulse previous = pulses[start - 1];
            ClassifiedSyncPulse current = pulses[start];
            if (previous.Kind != SyncPulseKind.HSync
                || current.Kind == SyncPulseKind.HSync
                || !current.InOrder
                || (minimumEqualizing1Start.HasValue
                    && current.Pulse.Start <= minimumEqualizing1Start.Value))
            {
                continue;
            }

            int end = start;
            while (end < pulses.Count && pulses[end].Kind != SyncPulseKind.HSync)
            {
                end++;
            }

            if (end >= pulses.Count || !pulses[end].InOrder)
            {
                start = Math.Max(start, end - 1);
                continue;
            }

            if ((end - 1 - start) <= blankLengthThreshold)
            {
                start = Math.Max(start, end - 1);
                continue;
            }

            int firstVSync = FindKind(pulses, start, end, SyncPulseKind.VSync);
            int firstEqualizingAfterVSync = firstVSync >= 0
                ? FindEqualizing(pulses, firstVSync + 1, end)
                : -1;
            if (firstVSync < 0 || firstEqualizingAfterVSync < 0)
            {
                start = Math.Max(start, end - 1);
                continue;
            }

            double? vSyncStart = firstVSync > start
                && IsEqualizing(pulses[firstVSync - 1].Kind)
                && pulses[firstVSync].InOrder
                    ? pulses[firstVSync].Pulse.Start
                    : null;
            return new VBlankPulseGroup(
                previous.Pulse.Start,
                current.Pulse.Start,
                vSyncStart,
                pulses[firstEqualizingAfterVSync].Pulse.Start,
                pulses[end - 1].Pulse.Start,
                pulses[end].Pulse.Start);
        }

        return null;
    }

    public static VBlankSyncEstimate? EstimateLine0(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        VBlankPulseGroup group,
        double meanLineLength,
        string system,
        int numEqualizingPulses,
        bool isFirstField,
        int currentFieldLines)
    {
        if (!double.IsFinite(meanLineLength) || meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        if (numEqualizingPulses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numEqualizingPulses));
        }

        string parentSystem = FormatCatalog.ParentSystem(system);
        (double firstHSyncLength, double secondEqualizingLength) =
            ExpectedBoundaryLengths(parentSystem, isFirstField);
        double sectionLines = numEqualizingPulses / 2.0;
        double equalizing1Line = firstHSyncLength;
        double vSyncStartLine = equalizing1Line + sectionLines;
        double equalizing2StartLine = vSyncStartLine + sectionLines;
        double equalizing2EndLine = equalizing2StartLine + sectionLines - 0.5;
        double firstHSyncLine = equalizing2EndLine + secondEqualizingLength;

        var markers = new List<(double Sample, double Line)>(4)
        {
            (group.Equalizing1Start, equalizing1Line)
        };
        if (group.VSyncStart.HasValue)
        {
            markers.Add((group.VSyncStart.Value, vSyncStartLine));
        }

        markers.Add((group.Equalizing2Start, equalizing2StartLine));
        markers.Add((group.Equalizing2End, equalizing2EndLine));

        double firstHSyncLocationSum = 0.0;
        double distanceOffsetSum = 0.0;
        int validDistanceCount = 0;
        for (int first = 0; first < markers.Count; first++)
        {
            for (int second = first + 1; second < markers.Count; second++)
            {
                double actualLines = (markers[first].Sample - markers[second].Sample) / meanLineLength;
                double expectedLines = markers[first].Line - markers[second].Line;
                if (actualLines <= expectedLines - VSyncToleranceLines
                    || actualLines >= expectedLines + VSyncToleranceLines)
                {
                    continue;
                }

                distanceOffsetSum += actualLines - expectedLines;
                firstHSyncLocationSum += markers[second].Sample
                    + (meanLineLength * (firstHSyncLine - markers[second].Line));
                validDistanceCount++;
            }
        }

        if (validDistanceCount == 0)
        {
            return null;
        }

        double distanceOffset = distanceOffsetSum / validDistanceCount;
        double firstHSyncLocation = Math.Round(
            (firstHSyncLocationSum + distanceOffset) / validDistanceCount,
            MidpointRounding.AwayFromZero);
        double unalignedFirstHSyncLocation = firstHSyncLocation;
        double hSyncOffsetSum = 0.0;
        int hSyncCount = 0;
        foreach (ClassifiedSyncPulse pulse in pulses)
        {
            if (pulse.Kind != SyncPulseKind.HSync || !pulse.InOrder)
            {
                continue;
            }

            double line = ((pulse.Pulse.Start - firstHSyncLocation) / meanLineLength) + firstHSyncLine;
            int roundedLine = (int)Math.Round(line, MidpointRounding.AwayFromZero);
            if (roundedLine > currentFieldLines)
            {
                break;
            }

            if (roundedLine >= firstHSyncLine)
            {
                hSyncOffsetSum += firstHSyncLocation
                    + (meanLineLength * (roundedLine - firstHSyncLine))
                    - pulse.Pulse.Start;
                hSyncCount++;
            }
        }

        if (hSyncCount > 0)
        {
            firstHSyncLocation -= hSyncOffsetSum / hSyncCount;
        }

        return new VBlankSyncEstimate(
            firstHSyncLocation - (meanLineLength * firstHSyncLine),
            firstHSyncLocation,
            firstHSyncLine,
            validDistanceCount,
            unalignedFirstHSyncLocation);
    }

    public static VBlankSyncConsensus EstimateLine0FromGroups(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        VBlankPulseGroup firstGroup,
        VBlankPulseGroup? lastGroup,
        double meanLineLength,
        string system,
        int numEqualizingPulses,
        bool isFirstField,
        int currentFieldLines)
    {
        if (!double.IsFinite(meanLineLength) || meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        if (numEqualizingPulses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numEqualizingPulses));
        }

        if (currentFieldLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentFieldLines));
        }

        FieldBoundaryLengths boundaries = ExpectedFieldBoundaryLengths(
            FormatCatalog.ParentSystem(system),
            isFirstField);
        double sectionLines = numEqualizingPulses / 2.0;
        SyncMarker[] firstMarkers = BuildMarkers(
            firstGroup,
            boundaries.FirstHSync,
            sectionLines);
        double firstHSyncLine = firstMarkers[^1].Line + boundaries.FirstEqualizing2;
        DistanceAccumulator firstAccumulator = AccumulateWithin(
            firstMarkers,
            meanLineLength,
            firstHSyncLine);
        VBlankSyncEstimate? first = FinalizeEstimate(
            pulses,
            firstAccumulator,
            meanLineLength,
            firstHSyncLine,
            currentFieldLines);

        if (lastGroup is null)
        {
            return new VBlankSyncConsensus(first, null, null);
        }

        SyncMarker[] lastMarkers = BuildMarkers(
            lastGroup,
            currentFieldLines + boundaries.LastHSync,
            sectionLines);
        DistanceAccumulator lastAccumulator = AccumulateWithin(
            lastMarkers,
            meanLineLength,
            firstHSyncLine);
        VBlankSyncEstimate? last = FinalizeEstimate(
            pulses,
            lastAccumulator,
            meanLineLength,
            firstHSyncLine,
            currentFieldLines);

        VBlankSyncEstimate? combined = null;
        if (firstAccumulator.Count > 0 && lastAccumulator.Count > 0)
        {
            double firstRawEstimate = firstAccumulator.FirstHSyncLocationSum / firstAccumulator.Count;
            double lastRawEstimate = lastAccumulator.FirstHSyncLocationSum / lastAccumulator.Count;
            if (Math.Abs(firstRawEstimate - lastRawEstimate) < VSyncToleranceLines * meanLineLength)
            {
                DistanceAccumulator crossAccumulator = AccumulateAcross(
                    firstMarkers,
                    lastMarkers,
                    meanLineLength,
                    firstHSyncLine);
                combined = FinalizeEstimate(
                    pulses,
                    firstAccumulator + lastAccumulator + crossAccumulator,
                    meanLineLength,
                    firstHSyncLine,
                    currentFieldLines);
            }
        }

        return new VBlankSyncConsensus(first, last, combined);
    }

    public static VBlankSyncConsensus EstimateLine0FromTransitions(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        double meanLineLength,
        string system,
        int numEqualizingPulses,
        bool isFirstField,
        int currentFieldLines,
        int firstFieldLines)
    {
        ArgumentNullException.ThrowIfNull(pulses);
        if (!double.IsFinite(meanLineLength) || meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        if (numEqualizingPulses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numEqualizingPulses));
        }

        if (currentFieldLines <= 0 || firstFieldLines <= 0)
        {
            throw new ArgumentOutOfRangeException(
                currentFieldLines <= 0 ? nameof(currentFieldLines) : nameof(firstFieldLines));
        }

        if (pulses.Count == 0)
        {
            return new VBlankSyncConsensus(null, null, null);
        }

        var firstSamples = new double?[4];
        var lastSamples = new double?[4];
        double?[] activeSamples = firstSamples;
        int lastPulse = -1;
        for (int index = 0; index < pulses.Count; index++)
        {
            ClassifiedSyncPulse current = pulses[index];
            if (lastPulse >= 0 && current.InOrder)
            {
                if (ReferenceEquals(activeSamples, firstSamples)
                    && current.Pulse.Start
                        > pulses[0].Pulse.Start + (firstFieldLines * meanLineLength))
                {
                    activeSamples = lastSamples;
                }

                ClassifiedSyncPulse previous = pulses[lastPulse];
                if (previous.Kind == SyncPulseKind.HSync && current.Kind != SyncPulseKind.HSync)
                {
                    activeSamples[0] = current.Pulse.Start;
                }
                else if (previous.Kind == SyncPulseKind.Equalizing && current.Kind == SyncPulseKind.VSync)
                {
                    activeSamples[1] = current.Pulse.Start;
                }
                else if (previous.Kind == SyncPulseKind.VSync && current.Kind == SyncPulseKind.EqualizingSecond)
                {
                    activeSamples[2] = current.Pulse.Start;
                }
                else if (previous.Kind != SyncPulseKind.HSync && current.Kind == SyncPulseKind.HSync)
                {
                    activeSamples[3] = previous.Pulse.Start;
                }
            }

            lastPulse = index;
        }

        FieldBoundaryLengths boundaries = ExpectedFieldBoundaryLengths(
            FormatCatalog.ParentSystem(system),
            isFirstField);
        double sectionLines = numEqualizingPulses / 2.0;
        double firstHSyncLine = boundaries.FirstHSync
            + (sectionLines * 3.0)
            - 0.5
            + boundaries.FirstEqualizing2;
        SyncMarker[] firstMarkers = BuildPartialMarkers(
            firstSamples,
            boundaries.FirstHSync,
            sectionLines);
        SyncMarker[] lastMarkers = BuildPartialMarkers(
            lastSamples,
            currentFieldLines + boundaries.LastHSync,
            sectionLines);
        DistanceAccumulator firstAccumulator = AccumulateWithin(
            firstMarkers,
            meanLineLength,
            firstHSyncLine);
        DistanceAccumulator lastAccumulator = AccumulateWithin(
            lastMarkers,
            meanLineLength,
            firstHSyncLine);
        VBlankSyncEstimate? first = FinalizeEstimate(
            pulses,
            firstAccumulator,
            meanLineLength,
            firstHSyncLine,
            currentFieldLines);
        VBlankSyncEstimate? last = FinalizeEstimate(
            pulses,
            lastAccumulator,
            meanLineLength,
            firstHSyncLine,
            currentFieldLines);

        VBlankSyncEstimate? combined = null;
        if (firstAccumulator.Count > 0 && lastAccumulator.Count > 0)
        {
            double firstRawEstimate = firstAccumulator.FirstHSyncLocationSum / firstAccumulator.Count;
            double lastRawEstimate = lastAccumulator.FirstHSyncLocationSum / lastAccumulator.Count;
            if (Math.Abs(firstRawEstimate - lastRawEstimate) < VSyncToleranceLines * meanLineLength)
            {
                DistanceAccumulator crossAccumulator = AccumulateAcross(
                    firstMarkers,
                    lastMarkers,
                    meanLineLength,
                    firstHSyncLine);
                combined = FinalizeEstimate(
                    pulses,
                    firstAccumulator + lastAccumulator + crossAccumulator,
                    meanLineLength,
                    firstHSyncLine,
                    currentFieldLines);
            }
        }

        return new VBlankSyncConsensus(first, last, combined);
    }

    public static double FirstHSyncLine(
        string system,
        int numEqualizingPulses,
        bool isFirstField)
    {
        if (numEqualizingPulses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numEqualizingPulses));
        }

        FieldBoundaryLengths boundaries = ExpectedFieldBoundaryLengths(
            FormatCatalog.ParentSystem(system),
            isFirstField);
        double sectionLines = numEqualizingPulses / 2.0;
        return boundaries.FirstHSync
            + (sectionLines * 3.0)
            - 0.5
            + boundaries.FirstEqualizing2;
    }

    public static double NextFieldLocation(
        string system,
        int numEqualizingPulses,
        bool isFirstField,
        int currentFieldLines,
        double meanLineLength,
        double firstHSyncLocation)
    {
        if (currentFieldLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentFieldLines));
        }

        if (!double.IsFinite(meanLineLength) || meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        if (!double.IsFinite(firstHSyncLocation))
        {
            throw new ArgumentOutOfRangeException(nameof(firstHSyncLocation));
        }

        FieldBoundaryLengths boundaries = ExpectedFieldBoundaryLengths(
            FormatCatalog.ParentSystem(system),
            isFirstField);
        double firstHSyncLine = FirstHSyncLine(system, numEqualizingPulses, isFirstField);
        double nextVBlankEqualizing1Line = currentFieldLines + boundaries.LastHSync;
        return firstHSyncLocation
            + (meanLineLength * (nextVBlankEqualizing1Line - firstHSyncLine));
    }

    public static bool HasValidStateMachineTiming(
        VBlankPulseGroup group,
        double meanLineLength,
        int numEqualizingPulses)
    {
        if (!double.IsFinite(meanLineLength) || meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        if (numEqualizingPulses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numEqualizingPulses));
        }

        double earliestTransition = ((numEqualizingPulses / 2.0) - 0.1) * meanLineLength;
        if (group.FollowingHSync - group.Equalizing2Start < earliestTransition)
        {
            return false;
        }

        return !group.VSyncStart.HasValue
            || (group.VSyncStart.Value - group.Equalizing1Start >= earliestTransition
                && group.Equalizing2Start - group.VSyncStart.Value >= earliestTransition);
    }

    private static int FindKind(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        int start,
        int end,
        SyncPulseKind kind)
    {
        for (int i = Math.Max(0, start); i < Math.Min(end, pulses.Count); i++)
        {
            if (pulses[i].Kind == kind && pulses[i].InOrder)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindEqualizing(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        int start,
        int end)
    {
        for (int i = Math.Max(0, start); i < Math.Min(end, pulses.Count); i++)
        {
            if (IsEqualizing(pulses[i].Kind) && pulses[i].InOrder)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsEqualizing(SyncPulseKind kind) =>
        kind is SyncPulseKind.Equalizing or SyncPulseKind.EqualizingSecond;

    private static SyncMarker[] BuildMarkers(
        VBlankPulseGroup group,
        double equalizing1Line,
        double sectionLines)
    {
        double vSyncLine = equalizing1Line + sectionLines;
        double equalizing2StartLine = vSyncLine + sectionLines;
        double equalizing2EndLine = equalizing2StartLine + sectionLines - 0.5;
        var markers = new List<SyncMarker>(4)
        {
            new(group.Equalizing1Start, equalizing1Line)
        };
        if (group.VSyncStart.HasValue)
        {
            markers.Add(new SyncMarker(group.VSyncStart.Value, vSyncLine));
        }

        markers.Add(new SyncMarker(group.Equalizing2Start, equalizing2StartLine));
        markers.Add(new SyncMarker(group.Equalizing2End, equalizing2EndLine));
        return markers.ToArray();
    }

    private static SyncMarker[] BuildPartialMarkers(
        IReadOnlyList<double?> samples,
        double equalizing1Line,
        double sectionLines)
    {
        double[] lines =
        [
            equalizing1Line,
            equalizing1Line + sectionLines,
            equalizing1Line + (sectionLines * 2.0),
            equalizing1Line + (sectionLines * 3.0) - 0.5
        ];
        var markers = new List<SyncMarker>(4);
        for (int index = 0; index < Math.Min(samples.Count, lines.Length); index++)
        {
            if (samples[index].HasValue)
            {
                markers.Add(new SyncMarker(samples[index]!.Value, lines[index]));
            }
        }

        return markers.ToArray();
    }

    private static DistanceAccumulator AccumulateWithin(
        IReadOnlyList<SyncMarker> markers,
        double meanLineLength,
        double firstHSyncLine)
    {
        DistanceAccumulator accumulator = default;
        for (int first = 0; first < markers.Count; first++)
        {
            for (int second = first + 1; second < markers.Count; second++)
            {
                accumulator += AccumulatePair(
                    markers[first],
                    markers[second],
                    meanLineLength,
                    firstHSyncLine);
            }
        }

        return accumulator;
    }

    private static DistanceAccumulator AccumulateAcross(
        IReadOnlyList<SyncMarker> firstMarkers,
        IReadOnlyList<SyncMarker> lastMarkers,
        double meanLineLength,
        double firstHSyncLine)
    {
        DistanceAccumulator accumulator = default;
        foreach (SyncMarker first in firstMarkers)
        {
            foreach (SyncMarker second in lastMarkers)
            {
                accumulator += AccumulatePair(first, second, meanLineLength, firstHSyncLine);
            }
        }

        return accumulator;
    }

    private static DistanceAccumulator AccumulatePair(
        SyncMarker first,
        SyncMarker second,
        double meanLineLength,
        double firstHSyncLine)
    {
        double actualLines = (first.Sample - second.Sample) / meanLineLength;
        double expectedLines = first.Line - second.Line;
        if (actualLines <= expectedLines - VSyncToleranceLines
            || actualLines >= expectedLines + VSyncToleranceLines)
        {
            return default;
        }

        return new DistanceAccumulator(
            second.Sample + (meanLineLength * (firstHSyncLine - second.Line)),
            actualLines - expectedLines,
            1);
    }

    private static VBlankSyncEstimate? FinalizeEstimate(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        DistanceAccumulator accumulator,
        double meanLineLength,
        double firstHSyncLine,
        int currentFieldLines)
    {
        if (accumulator.Count == 0)
        {
            return null;
        }

        double firstHSyncLocation = Math.Round(
            (accumulator.FirstHSyncLocationSum + accumulator.DistanceOffsetSum) / accumulator.Count,
            MidpointRounding.AwayFromZero);
        double unalignedFirstHSyncLocation = firstHSyncLocation;
        double hSyncOffsetSum = 0.0;
        int hSyncCount = 0;
        foreach (ClassifiedSyncPulse pulse in pulses)
        {
            if (pulse.Kind != SyncPulseKind.HSync || !pulse.InOrder)
            {
                continue;
            }

            double line = ((pulse.Pulse.Start - firstHSyncLocation) / meanLineLength) + firstHSyncLine;
            int roundedLine = (int)Math.Round(line, MidpointRounding.AwayFromZero);
            if (roundedLine > currentFieldLines)
            {
                break;
            }

            if (roundedLine >= firstHSyncLine)
            {
                hSyncOffsetSum += firstHSyncLocation
                    + (meanLineLength * (roundedLine - firstHSyncLine))
                    - pulse.Pulse.Start;
                hSyncCount++;
            }
        }

        if (hSyncCount > 0)
        {
            firstHSyncLocation -= hSyncOffsetSum / hSyncCount;
        }

        return new VBlankSyncEstimate(
            firstHSyncLocation - (meanLineLength * firstHSyncLine),
            firstHSyncLocation,
            firstHSyncLine,
            accumulator.Count,
            unalignedFirstHSyncLocation);
    }

    private static (double FirstHSyncLength, double SecondEqualizingLength) ExpectedBoundaryLengths(
        string parentSystem,
        bool isFirstField)
    {
        return (parentSystem, isFirstField) switch
        {
            ("NTSC", true) => (1.0, 0.5),
            ("NTSC", false) => (0.5, 1.0),
            ("PAL", true) => (0.5, 0.5),
            ("PAL", false) => (1.0, 1.0),
            _ => throw new ArgumentOutOfRangeException(nameof(parentSystem))
        };
    }

    private static FieldBoundaryLengths ExpectedFieldBoundaryLengths(
        string parentSystem,
        bool isFirstField)
    {
        return (parentSystem, isFirstField) switch
        {
            ("NTSC", true) => new(1.0, 0.5, 0.5, 1.0),
            ("NTSC", false) => new(0.5, 1.0, 1.0, 0.5),
            ("PAL", true) => new(0.5, 0.5, 1.0, 1.0),
            ("PAL", false) => new(1.0, 1.0, 0.5, 0.5),
            _ => throw new ArgumentOutOfRangeException(nameof(parentSystem))
        };
    }
}
