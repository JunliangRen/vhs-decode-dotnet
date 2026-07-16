using System.Text.Json;
using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Dsp;

public enum SyncPulseKind
{
    HSync = 0,
    Equalizing = 1,
    VSync = 2,
    EqualizingSecond = 3
}

public readonly record struct SyncRange(double Minimum, double Maximum)
{
    public bool Contains(double value) => value >= Minimum && value <= Maximum;
}

public sealed record SyncTiming(
    double NominalLineLength,
    double HSyncMedian,
    double HSyncOffset,
    SyncRange HSync,
    SyncRange Equalizing,
    SyncRange VSync);

public sealed record ClassifiedSyncPulse(SyncPulseKind Kind, Pulse Pulse, bool InOrder);

public sealed record LineLocationResult(double[] Locations, bool[] Filled);

public sealed class SyncAnalyzer
{
    public SyncAnalyzer(
        double sampleRateHz,
        double linePeriodUs,
        double hsyncPulseUs,
        double equalizingPulseUs,
        double vsyncPulseUs,
        int numPulses = 6,
        double hsyncToleranceUs = 0.5,
        double equalizingToleranceUs = 0.5)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        SampleRateHz = sampleRateHz;
        SampleRateMHz = sampleRateHz / 1_000_000.0;
        LinePeriodUs = linePeriodUs;
        HSyncPulseUs = hsyncPulseUs;
        EqualizingPulseUs = equalizingPulseUs;
        VSyncPulseUs = vsyncPulseUs;
        NumPulses = numPulses > 0
            ? numPulses
            : throw new ArgumentOutOfRangeException(nameof(numPulses));
        HSyncToleranceUs = hsyncToleranceUs > 0.0
            ? hsyncToleranceUs
            : throw new ArgumentOutOfRangeException(nameof(hsyncToleranceUs));
        EqualizingToleranceUs = equalizingToleranceUs > 0.0
            ? equalizingToleranceUs
            : throw new ArgumentOutOfRangeException(nameof(equalizingToleranceUs));
        NominalLineLength = UsecToSamples(linePeriodUs);
    }

    public double SampleRateHz { get; }

    public double SampleRateMHz { get; }

    public double LinePeriodUs { get; }

    public double HSyncPulseUs { get; }

    public double EqualizingPulseUs { get; }

    public double VSyncPulseUs { get; }

    public int NumPulses { get; }

    public double HSyncToleranceUs { get; }

    public double EqualizingToleranceUs { get; }

    public double NominalLineLength { get; }

    public static SyncAnalyzer FromParameters(
        FormatParameterSet parameters,
        double sampleRateHz,
        double hsyncToleranceUs = 0.5,
        double equalizingToleranceUs = 0.5)
    {
        JsonElement sys = parameters.SysParams;
        double linePeriodUs = sys.GetProperty("line_period").GetDouble();
        double sampleRateMHz = sampleRateHz / 1_000_000.0;
        if (sampleRateMHz > 0.0)
        {
            double nominalLineLength = Math.Round(
                linePeriodUs * sampleRateMHz,
                MidpointRounding.ToEven);
            linePeriodUs = nominalLineLength / sampleRateMHz;
        }

        return new SyncAnalyzer(
            sampleRateHz,
            linePeriodUs,
            sys.GetProperty("hsyncPulseUS").GetDouble(),
            sys.GetProperty("eqPulseUS").GetDouble(),
            sys.GetProperty("vsyncPulseUS").GetDouble(),
            sys.GetProperty("numPulses").GetInt32(),
            hsyncToleranceUs,
            equalizingToleranceUs);
    }

    public double UsecToSamples(double microseconds) => microseconds * SampleRateMHz;

    public IReadOnlyList<Pulse> FindRawPulses(
        ReadOnlySpan<double> syncReference,
        double threshold,
        double minimumPulseUs,
        double maximumPulseUs)
    {
        return PulseDetection.FindPulses(
            syncReference,
            threshold,
            minimumSyncLength: (int)Math.Round(UsecToSamples(minimumPulseUs)),
            maximumSyncLength: (int)Math.Round(UsecToSamples(maximumPulseUs)));
    }

    public SyncTiming EstimateTiming(IReadOnlyList<Pulse> rawPulses)
    {
        double hsyncCheckMin = UsecToSamples(HSyncPulseUs - 1.75);
        double hsyncCheckMax = UsecToSamples(HSyncPulseUs + 2.0);
        var hsyncLengths = new List<double>();
        foreach (Pulse pulse in rawPulses)
        {
            if (PulseDetection.InRange(pulse.Length, hsyncCheckMin, hsyncCheckMax))
            {
                hsyncLengths.Add(pulse.Length);
            }
        }

        double hsyncMedian = hsyncLengths.Count > 0 ? Median(hsyncLengths) : UsecToSamples(HSyncPulseUs);
        double hsyncTypical = UsecToSamples(HSyncPulseUs);
        double offset = hsyncMedian - hsyncTypical;
        return new SyncTiming(
            NominalLineLength,
            hsyncMedian,
            offset,
            new SyncRange(
                hsyncMedian - UsecToSamples(HSyncToleranceUs),
                hsyncMedian + UsecToSamples(HSyncToleranceUs)),
            new SyncRange(
                UsecToSamples(EqualizingPulseUs - EqualizingToleranceUs) + offset,
                UsecToSamples(EqualizingPulseUs + EqualizingToleranceUs) + offset),
            new SyncRange(UsecToSamples(VSyncPulseUs * 0.5) + offset, UsecToSamples(VSyncPulseUs + 1.0) + offset));
    }

    public IReadOnlyList<ClassifiedSyncPulse> ClassifyPulses(IReadOnlyList<Pulse> rawPulses, SyncTiming timing)
    {
        var classified = new List<ClassifiedSyncPulse>();
        foreach (Pulse pulse in rawPulses)
        {
            SyncPulseKind? kind = ClassifyPulse(pulse, timing);

            if (!kind.HasValue)
            {
                continue;
            }

            bool inOrder = classified.Count > 0 && PulseQualityCheck(classified[^1], kind.Value, pulse);
            classified.Add(new ClassifiedSyncPulse(kind.Value, pulse, inOrder));
        }

        return classified;
    }

    public IReadOnlyList<ClassifiedSyncPulse> RefinePulses(
        IReadOnlyList<Pulse> rawPulses,
        SyncTiming timing)
        => RefinePulses(rawPulses, timing, [], 0.0);

    public IReadOnlyList<ClassifiedSyncPulse> RefinePulses(
        IReadOnlyList<Pulse> rawPulses,
        SyncTiming timing,
        ReadOnlySpan<double> syncReference,
        double hsyncRescueStepHz)
    {
        Pulse[] workingPulses = rawPulses.ToArray();
        var refined = new List<ClassifiedSyncPulse>();
        int index = 0;
        while (index < workingPulses.Length)
        {
            Pulse pulse = workingPulses[index];
            if (timing.HSync.Contains(pulse.Length))
            {
                bool inOrder = refined.Count > 0
                    && PulseQualityCheck(refined[^1], SyncPulseKind.HSync, pulse);
                refined.Add(new ClassifiedSyncPulse(SyncPulseKind.HSync, pulse, inOrder));
                index++;
                continue;
            }

            if (!syncReference.IsEmpty
                && hsyncRescueStepHz > 0.0
                && PulseDetection.InRange(
                    pulse.Length,
                    timing.HSync.Maximum,
                    timing.HSync.Maximum * 3.0))
            {
                Pulse? rescued = TryRescueLongHSync(
                    pulse,
                    syncReference,
                    hsyncRescueStepHz);
                if (rescued.HasValue && rescued.Value != pulse)
                {
                    workingPulses[index] = rescued.Value;
                    continue;
                }

                index++;
                continue;
            }

            bool canStartVBlank = index > 2
                && timing.Equalizing.Contains(pulse.Length)
                && refined.Count > 0
                && refined[^1].Kind == SyncPulseKind.HSync;
            if (!canStartVBlank)
            {
                index++;
                continue;
            }

            (IReadOnlyList<ClassifiedSyncPulse> Pulses, int EndIndex)? vBlank =
                TryReadVBlank(workingPulses, timing, index - 2, Math.Min(workingPulses.Length, index + 24));
            if (!vBlank.HasValue)
            {
                index++;
                continue;
            }

            foreach (ClassifiedSyncPulse validPulse in vBlank.Value.Pulses.Skip(2))
            {
                refined.Add(validPulse);
            }

            index += Math.Max(1, vBlank.Value.Pulses.Count - 2);
        }

        return refined;
    }

    private Pulse? TryRescueLongHSync(
        Pulse pulse,
        ReadOnlySpan<double> syncReference,
        double hsyncRescueStepHz)
    {
        int start = Math.Clamp(pulse.Start, 0, syncReference.Length);
        int end = Math.Clamp(pulse.Start + pulse.Length, start, syncReference.Length);
        if (start >= end)
        {
            return null;
        }

        ReadOnlySpan<double> pulseData = syncReference[start..end];
        double threshold = pulseData[0] - hsyncRescueStepHz;
        IReadOnlyList<Pulse> candidates = PulseDetection.FindPulses(
            pulseData,
            threshold,
            minimumSyncLength: Math.Max(0, (int)Math.Ceiling(UsecToSamples(EqualizingPulseUs) / 8.0)),
            maximumSyncLength: Math.Max(1, (int)(NominalLineLength * 5.0)));
        return candidates.Count > 0
            ? new Pulse(start + candidates[0].Start, candidates[0].Length)
            : null;
    }

    private (IReadOnlyList<ClassifiedSyncPulse> Pulses, int EndIndex)? TryReadVBlank(
        IReadOnlyList<Pulse> rawPulses,
        SyncTiming timing,
        int start,
        int endExclusive)
    {
        var valid = new List<ClassifiedSyncPulse>();
        SyncPulseKind? state = null;
        double stateEnd = 0.0;
        double? stateLength = null;
        for (int index = Math.Max(0, start); index < endExclusive; index++)
        {
            Pulse pulse = rawPulses[index];
            SyncPulseKind? rawKind = ClassifyPulse(pulse, timing);
            if (!rawKind.HasValue)
            {
                continue;
            }

            SyncPulseKind? accepted = null;
            bool done = false;
            switch (state)
            {
                case null when rawKind == SyncPulseKind.HSync:
                    accepted = SyncPulseKind.HSync;
                    break;
                case SyncPulseKind.HSync:
                    if (rawKind == SyncPulseKind.HSync)
                    {
                        accepted = SyncPulseKind.HSync;
                    }
                    else if (rawKind == SyncPulseKind.Equalizing)
                    {
                        accepted = SyncPulseKind.Equalizing;
                        stateLength = NumPulses / 2.0;
                    }
                    else if (rawKind == SyncPulseKind.VSync)
                    {
                        accepted = SyncPulseKind.VSync;
                    }

                    break;
                case SyncPulseKind.Equalizing:
                    if (rawKind == SyncPulseKind.Equalizing)
                    {
                        accepted = SyncPulseKind.Equalizing;
                    }
                    else if (rawKind == SyncPulseKind.VSync)
                    {
                        accepted = SyncPulseKind.VSync;
                        stateLength = NumPulses / 2.0;
                    }
                    else if (rawKind == SyncPulseKind.HSync)
                    {
                        accepted = SyncPulseKind.HSync;
                    }

                    break;
                case SyncPulseKind.VSync:
                    if (rawKind == SyncPulseKind.Equalizing)
                    {
                        accepted = SyncPulseKind.EqualizingSecond;
                        stateLength = NumPulses / 2.0;
                    }
                    else if (rawKind == SyncPulseKind.VSync)
                    {
                        accepted = SyncPulseKind.VSync;
                    }
                    else if (rawKind == SyncPulseKind.HSync && pulse.Start > stateEnd)
                    {
                        accepted = SyncPulseKind.HSync;
                    }

                    break;
                case SyncPulseKind.EqualizingSecond:
                    if (rawKind == SyncPulseKind.Equalizing)
                    {
                        accepted = SyncPulseKind.EqualizingSecond;
                    }
                    else if (rawKind == SyncPulseKind.HSync)
                    {
                        accepted = SyncPulseKind.HSync;
                        done = true;
                    }

                    break;
            }

            if (!accepted.HasValue)
            {
                continue;
            }

            if (accepted != state)
            {
                if (pulse.Start < stateEnd)
                {
                    accepted = null;
                }
                else if (stateLength.HasValue)
                {
                    stateEnd = pulse.Start + ((stateLength.Value - 0.1) * NominalLineLength);
                    stateLength = null;
                }
            }

            if (accepted.HasValue)
            {
                bool inOrder = valid.Count > 0
                    && PulseQualityCheck(valid[^1], accepted.Value, pulse);
                valid.Add(new ClassifiedSyncPulse(accepted.Value, pulse, inOrder));
                state = accepted;
            }

            if (done)
            {
                return (valid, index);
            }
        }

        return null;
    }

    private static SyncPulseKind? ClassifyPulse(Pulse pulse, SyncTiming timing)
    {
        if (timing.HSync.Contains(pulse.Length))
        {
            return SyncPulseKind.HSync;
        }

        if (timing.Equalizing.Contains(pulse.Length))
        {
            return SyncPulseKind.Equalizing;
        }

        return timing.VSync.Contains(pulse.Length)
            ? SyncPulseKind.VSync
            : null;
    }

    public double ComputeMeanLineLength(IReadOnlyList<ClassifiedSyncPulse> validPulses)
    {
        int runStart = -1;
        int runLength = -1;
        int currentStart = -1;
        int currentLength = 0;

        for (int i = 0; i < validPulses.Count; i++)
        {
            if (validPulses[i].Kind != SyncPulseKind.HSync)
            {
                if (currentStart >= 0 && currentLength > runLength)
                {
                    runStart = currentStart;
                    runLength = currentLength;
                }

                currentStart = -1;
                currentLength = 0;
            }
            else if (currentStart < 0)
            {
                currentStart = i;
                currentLength = 0;
            }
            else
            {
                currentLength++;
            }
        }

        if (currentStart >= 0 && currentLength > runLength)
        {
            runStart = currentStart;
            runLength = currentLength;
        }

        var lineLengths = new List<double>();
        for (int i = runStart + 1; i < runStart + runLength; i++)
        {
            double lineLength = validPulses[i].Pulse.Start - validPulses[i - 1].Pulse.Start;
            if (PulseDetection.InRange(lineLength / NominalLineLength, 0.95, 1.05))
            {
                lineLengths.Add(lineLength);
            }
        }

        return lineLengths.Count > 0 ? Mean(lineLengths) : NominalLineLength;
    }

    public LineLocationResult BuildLineLocations(
        IReadOnlyList<ClassifiedSyncPulse> validPulses,
        double line0Location,
        double meanLineLength,
        int processedLines,
        double hsyncToleranceLines)
    {
        if (processedLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processedLines));
        }

        var locations = new double[processedLines];
        var distances = new double[processedLines];
        Array.Fill(locations, -1.0);
        Array.Fill(distances, double.PositiveInfinity);

        foreach (ClassifiedSyncPulse pulse in validPulses)
        {
            double lineLocation = (pulse.Pulse.Start - line0Location) / meanLineLength;
            int roundedLine = (int)Math.Round(lineLocation, MidpointRounding.AwayFromZero);
            double distance = Math.Abs(lineLocation - roundedLine);
            if (roundedLine < 0 || roundedLine >= processedLines || distance > hsyncToleranceLines)
            {
                continue;
            }

            if (roundedLine > 0 && !pulse.InOrder)
            {
                if (pulse.Kind != SyncPulseKind.HSync || roundedLine < 10)
                {
                    continue;
                }
            }

            if (distance <= distances[roundedLine])
            {
                locations[roundedLine] = pulse.Pulse.Start;
                distances[roundedLine] = distance;
            }
        }

        return FillMissingLocations(locations, meanLineLength);
    }

    public LineLocationResult BuildUpstreamLineLocations(
        IReadOnlyList<ClassifiedSyncPulse> validPulses,
        double referencePulse,
        int referenceLine,
        double meanLineLength,
        int processedLines,
        bool preferEarlierPulseOnEqualDistance = false)
    {
        if (!double.IsFinite(referencePulse))
        {
            throw new ArgumentOutOfRangeException(nameof(referencePulse));
        }

        if (!double.IsFinite(meanLineLength) || meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        if (processedLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processedLines));
        }

        double[] pulseLocations = validPulses
            .Select(pulse => (double)pulse.Pulse.Start)
            .OrderBy(location => location)
            .ToArray();
        var lineLocations = new double[processedLines];
        var lineLocationErrors = new bool[processedLines];
        int currentPulseIndex = 0;
        double maximumDistance = meanLineLength / 1.5;
        for (int line = 0; line < processedLines; line++)
        {
            double expected = referencePulse + (meanLineLength * (line - referenceLine));
            lineLocations[line] = expected;
            if (currentPulseIndex >= pulseLocations.Length)
            {
                continue;
            }

            double currentDistance = Math.Abs(pulseLocations[currentPulseIndex] - expected);
            double smallestDistance = maximumDistance;
            double selectedPulse = -1.0;
            int searchIndex = currentPulseIndex;
            while (searchIndex < pulseLocations.Length - 1)
            {
                if (currentDistance <= smallestDistance)
                {
                    smallestDistance = currentDistance;
                    currentPulseIndex = searchIndex;
                    selectedPulse = pulseLocations[searchIndex];
                }

                double nextDistance = Math.Abs(pulseLocations[searchIndex + 1] - expected);
                if (nextDistance > currentDistance
                    || (preferEarlierPulseOnEqualDistance && nextDistance == currentDistance))
                {
                    break;
                }

                currentDistance = nextDistance;
                searchIndex++;
            }

            if (selectedPulse >= 0.0)
            {
                lineLocations[line] = selectedPulse;
                currentPulseIndex++;
            }
        }

        return new LineLocationResult(lineLocations, lineLocationErrors);
    }

    private LineLocationResult FillMissingLocations(double[] detected, double meanLineLength)
    {
        var output = detected.ToArray();
        var filled = new bool[detected.Length];

        if (output[0] < 0)
        {
            int nextValid = FindNextValid(detected, 0);
            if (nextValid < 0)
            {
                throw new ArgumentException("No valid line locations were detected.");
            }

            output[0] = detected[nextValid] - (nextValid * meanLineLength);
            filled[0] = true;
        }

        for (int line = 1; line < output.Length; line++)
        {
            if (output[line] >= 0)
            {
                continue;
            }

            int previousValid = FindPreviousValid(detected, line);
            int nextValid = FindNextValid(detected, line);
            double averageLength;
            if (previousValid < 0 && nextValid >= 0)
            {
                averageLength = meanLineLength;
                output[line] = detected[nextValid] - (averageLength * (nextValid - line));
            }
            else if (nextValid >= 0)
            {
                averageLength = (detected[nextValid] - detected[previousValid]) / (nextValid - previousValid);
                output[line] = detected[previousValid] + (averageLength * (line - previousValid));
            }
            else if (previousValid >= 0)
            {
                averageLength = meanLineLength;
                output[line] = detected[previousValid] + (averageLength * (line - previousValid));
            }
            else
            {
                throw new ArgumentException("No valid line locations were detected.");
            }

            filled[line] = true;
        }

        return new LineLocationResult(output, filled);
    }

    private bool PulseQualityCheck(ClassifiedSyncPulse previous, SyncPulseKind kind, Pulse pulse)
    {
        (double min, double max) expectedRange = previous.Kind != SyncPulseKind.HSync && kind != SyncPulseKind.HSync
            ? (0.4, 0.6)
            : previous.Kind == SyncPulseKind.HSync && kind == SyncPulseKind.HSync
                ? (0.9, 1.1)
                : (0.4, 1.1);

        double lineLength = (pulse.Start - previous.Pulse.Start) / NominalLineLength;
        return PulseDetection.InRange(lineLength, expectedRange.min, expectedRange.max);
    }

    private static int FindPreviousValid(double[] values, int start)
    {
        for (int i = Math.Min(start, values.Length - 1); i >= 0; i--)
        {
            if (values[i] >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindNextValid(double[] values, int start)
    {
        for (int i = Math.Max(0, start); i < values.Length; i++)
        {
            if (values[i] >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static double Mean(IReadOnlyList<double> values)
    {
        double sum = 0.0;
        foreach (double value in values)
        {
            sum += value;
        }

        return sum / values.Count;
    }

    private static double Median(List<double> values)
        => NumpyReduction.MedianFloat64(values.ToArray());
}
