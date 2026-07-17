namespace VHSDecode.Core.Dsp;

public sealed record FallbackVSyncResolution(
    double Line0Location,
    double? LastLineLocation,
    bool? IsFirstField,
    int FirstFieldConfidence,
    string? DiagnosticMessage = null);

public static class FallbackVSyncResolver
{
    public static FallbackVSyncResolution? Resolve(
        IReadOnlyList<ClassifiedSyncPulse> validPulses,
        IReadOnlyList<Pulse> rawPulses,
        ReadOnlySpan<double> demodLowPass,
        SyncRange vSyncRange,
        double meanLineLength,
        int numEqualizingPulses,
        int frameLines,
        bool relaxed = false,
        double? expectedLine0 = null,
        bool? expectedFirstField = null)
    {
        if (!double.IsFinite(meanLineLength) || meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        if (numEqualizingPulses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numEqualizingPulses));
        }

        if (frameLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameLines));
        }

        if (rawPulses.Count == 0)
        {
            return null;
        }

        IReadOnlyList<Pulse> pulses = FilterClosePulses(rawPulses, meanLineLength);
        double shortPulseMaximum = 0.2 * meanLineLength;
        double longPulseMinimum = 0.35 * meanLineLength;
        double limit = meanLineLength * (frameLines - 1) / 2.0;
        bool palTiming = frameLines == 625;

        double? line0 = null;
        double? backupLine0 = null;
        bool? firstField = null;
        bool? backupFirstField = null;
        int firstFieldConfidence = -1;
        int backupFirstFieldConfidence = -1;
        string? diagnosticMessage = null;

        // Upstream tries the end of the broad VSYNC pulses first.
        for (int i = 15; !line0.HasValue && i < pulses.Count - 2; i++)
        {
            double d0 = DistanceLines(pulses[i - 2], pulses[i - 1], meanLineLength);
            double d1 = DistanceLines(pulses[i - 1], pulses[i], meanLineLength);
            double d2 = DistanceLines(pulses[i], pulses[i + 1], meanLineLength);
            double d3 = DistanceLines(pulses[i + 1], pulses[i + 2], meanLineLength);
            if (!Near(d0, 0.5) || !Near(d1, 0.5) || !Near(d2, 0.5) || !Near(d3, 0.5)
                || pulses[i - 2].Length <= longPulseMinimum
                || pulses[i - 1].Length <= longPulseMinimum
                || pulses[i].Length >= shortPulseMaximum
                || pulses[i + 1].Length >= shortPulseMaximum
                || pulses[i + 2].Length >= shortPulseMaximum)
            {
                continue;
            }

            double measuredLineLength = (d0 + d1 + d2 + d3) * (meanLineLength / 2.0);
            double? lineOffset = null;
            int halfLines = CountFollowingHalfLines(pulses, i, meanLineLength, shortPulseMaximum);
            if (halfLines == 4 && palTiming)
            {
                firstField = false;
                firstFieldConfidence = 100;
                lineOffset = 5.0;
            }
            else if (halfLines == 5)
            {
                firstField = palTiming;
                firstFieldConfidence = 100;
                lineOffset = palTiming ? 4.5 : 5.5;
            }
            else if (halfLines == 6 && frameLines == 525)
            {
                firstField = true;
                lineOffset = 6.0;
            }

            if (!lineOffset.HasValue)
            {
                (int phase0, int phase1, int other) = CountPulsePhases(pulses, i, 15, 30, measuredLineLength);
                int phaseTotal = phase0 + phase1 + other;
                int phase = DominantPhase(phase0, phase1, other);
                if (phase == 0)
                {
                    firstField = !palTiming;
                    lineOffset = palTiming ? 5.0 : 6.0;
                    firstFieldConfidence = phase0 * 100 / phaseTotal;
                }
                else if (phase == 1)
                {
                    firstField = palTiming;
                    lineOffset = palTiming ? 4.5 : 5.5;
                    firstFieldConfidence = phase1 * 100 / phaseTotal;
                }
            }

            if (lineOffset.HasValue)
            {
                double estimate = pulses[i - 2].Start - (lineOffset.Value * measuredLineLength);
                SaveBackup(
                    estimate,
                    firstField,
                    firstFieldConfidence,
                    ref backupLine0,
                    ref backupFirstField,
                    ref backupFirstFieldConfidence);
                int start = Math.Max(0, i - (relaxed ? 20 : 16));
                int end = relaxed ? i : i - 4;
                line0 = FindPulseNear(pulses, start, end, estimate, meanLineLength, 0.08);
            }
        }

        // Then try the beginning of the broad VSYNC pulses.
        for (int i = 10; (!line0.HasValue || line0 > limit) && i < pulses.Count - 2; i++)
        {
            double d0 = DistanceLines(pulses[i - 2], pulses[i - 1], meanLineLength);
            double d1 = DistanceLines(pulses[i - 1], pulses[i], meanLineLength);
            double d2 = DistanceLines(pulses[i], pulses[i + 1], meanLineLength);
            double d3 = DistanceLines(pulses[i + 1], pulses[i + 2], meanLineLength);
            if (!Near(d0, 0.5) || !Near(d1, 0.5) || !Near(d2, 0.5) || !Near(d3, 0.5)
                || pulses[i - 2].Length >= shortPulseMaximum
                || pulses[i - 1].Length >= shortPulseMaximum
                || pulses[i].Length <= longPulseMinimum
                || pulses[i + 1].Length <= longPulseMinimum
                || pulses[i + 2].Length <= longPulseMinimum)
            {
                continue;
            }

            double measuredLineLength = (d0 + d1 + d2 + d3) * (meanLineLength / 2.0);
            (int phase0, int phase1, int other) = CountPulsePhases(pulses, i, 10, 25, measuredLineLength);
            int phaseTotal = phase0 + phase1 + other;
            int phase = DominantPhase(phase0, phase1, other);
            double? lineOffset = null;
            bool? candidateFirstField = null;
            int candidateConfidence = -1;
            if (phase == 0)
            {
                lineOffset = palTiming ? 2.0 : 3.0;
                candidateFirstField = true;
                candidateConfidence = phase0 * 100 / phaseTotal;
            }
            else if (phase == 1)
            {
                lineOffset = 2.5;
                candidateFirstField = false;
                candidateConfidence = phase1 * 100 / phaseTotal;
            }

            if (!lineOffset.HasValue)
            {
                continue;
            }

            double estimate = pulses[i - 2].Start - (lineOffset.Value * measuredLineLength);
            SaveBackup(
                estimate,
                candidateFirstField,
                candidateConfidence,
                ref backupLine0,
                ref backupFirstField,
                ref backupFirstFieldConfidence);
            int start = Math.Max(0, i - (relaxed ? 15 : 10));
            int end = relaxed ? i : i - 3;
            double? found = FindPulseNear(pulses, start, end, estimate, meanLineLength, 0.08);
            if (found.HasValue)
            {
                if (line0 != found || candidateConfidence > firstFieldConfidence)
                {
                    firstField = candidateFirstField;
                    // v0.4.0 accidentally retains the previous confidence here.
                }

                line0 = found;
            }
        }

        // Next, detect the EQ2-to-HSYNC transition at the end of blanking.
        for (int i = 10; (!line0.HasValue || line0 > limit) && i < pulses.Count - 2; i++)
        {
            double dPre = DistanceLines(pulses[i - 3], pulses[i - 2], meanLineLength);
            double d0 = DistanceLines(pulses[i - 2], pulses[i - 1], meanLineLength);
            double d1 = DistanceLines(pulses[i - 1], pulses[i], meanLineLength);
            double d2 = DistanceLines(pulses[i], pulses[i + 1], meanLineLength);
            double d3 = DistanceLines(pulses[i + 1], pulses[i + 2], meanLineLength);
            bool intervalsMatch = relaxed
                ? Near(d0, 0.5) && Near(d1, 0.5) && Near(d2, 1.0) && Near(d3, 1.0)
                : Near(dPre, 0.5) && Near(d0, 0.5) && Near(d1, 0.5) && Near(d2, 1.0) && Near(d3, 1.0);
            if (!intervalsMatch
                || pulses[i - 2].Length >= shortPulseMaximum
                || pulses[i - 1].Length >= shortPulseMaximum
                || pulses[i].Length >= shortPulseMaximum
                || pulses[i + 1].Length >= shortPulseMaximum
                || pulses[i + 2].Length >= shortPulseMaximum)
            {
                continue;
            }

            double measuredLineLength = (d0 + d1 + d2 + d3) * (meanLineLength / 3.0);
            double equalizingLength = (pulses[i - 2].Length + pulses[i - 1].Length) / 2.0;
            double hSyncLength = (pulses[i + 1].Length + pulses[i + 2].Length) / 2.0;
            if (equalizingLength <= 0.0 || hSyncLength / equalizingLength <= 1.75)
            {
                continue;
            }

            double? lineOffset = null;
            bool? candidateFirstField = null;
            int candidateConfidence = -1;
            if (pulses[i].Length < equalizingLength * 1.25)
            {
                lineOffset = palTiming ? 7.0 : 8.0;
                candidateFirstField = false;
                candidateConfidence = pulses[i].Length < equalizingLength * 1.1 ? 80 : 60;
            }
            else if (pulses[i].Length > hSyncLength * 0.75)
            {
                lineOffset = palTiming ? 7.0 : 9.0;
                candidateFirstField = true;
                candidateConfidence = pulses[i].Length > hSyncLength * 0.9 ? 80 : 60;
            }

            if (!lineOffset.HasValue)
            {
                continue;
            }

            double estimate = pulses[i - 2].Start - (lineOffset.Value * measuredLineLength);
            SaveBackup(
                estimate,
                candidateFirstField,
                candidateConfidence,
                ref backupLine0,
                ref backupFirstField,
                ref backupFirstFieldConfidence);
            int start = Math.Max(0, i - (relaxed ? 25 : 20));
            int end = relaxed ? i : i - 4;
            double? found = FindPulseNear(pulses, start, end, estimate, meanLineLength, 0.08);
            if (found.HasValue)
            {
                if (line0 != found || candidateConfidence > firstFieldConfidence)
                {
                    firstField = candidateFirstField;
                    // v0.4.0 accidentally retains the previous confidence here.
                }

                line0 = found;
            }
        }

        // Finally, detect the HSYNC-to-EQ1 transition at the beginning of blanking.
        for (int i = 2; (!line0.HasValue || line0 > limit) && i < pulses.Count - 3; i++)
        {
            double d0 = DistanceLines(pulses[i - 2], pulses[i - 1], meanLineLength);
            double d1 = DistanceLines(pulses[i - 1], pulses[i], meanLineLength);
            double d2 = DistanceLines(pulses[i], pulses[i + 1], meanLineLength);
            double d3 = DistanceLines(pulses[i + 1], pulses[i + 2], meanLineLength);
            double d4 = DistanceLines(pulses[i + 2], pulses[i + 3], meanLineLength);
            bool intervalsMatch = relaxed
                ? Near(d0, 1.0) && Near(d1, 1.0) && Near(d2, 0.5) && Near(d3, 0.5)
                : Near(d0, 1.0) && Near(d1, 1.0) && Near(d2, 0.5) && Near(d3, 0.5) && Near(d4, 0.5);
            if (!intervalsMatch
                || pulses[i - 2].Length >= shortPulseMaximum
                || pulses[i - 1].Length >= shortPulseMaximum
                || pulses[i].Length >= shortPulseMaximum
                || pulses[i + 1].Length >= shortPulseMaximum
                || pulses[i + 2].Length >= shortPulseMaximum)
            {
                continue;
            }

            double hSyncLength = (pulses[i - 2].Length + pulses[i - 1].Length) / 2.0;
            double equalizingLength = (pulses[i + 1].Length + pulses[i + 2].Length) / 2.0;
            if (equalizingLength > 0.0 && hSyncLength / equalizingLength > 1.75)
            {
                if (pulses[i].Length < equalizingLength * 1.25)
                {
                    int candidateConfidence = pulses[i].Length < equalizingLength * 1.1 ? 60 : 40;
                    double candidate = pulses[i - 1].Start;
                    if (line0 != candidate || candidateConfidence > firstFieldConfidence)
                    {
                        firstFieldConfidence = candidateConfidence;
                        firstField = !palTiming;
                    }

                    line0 = candidate;
                }
                else if (pulses[i].Length > hSyncLength * 0.75)
                {
                    int candidateConfidence = pulses[i].Length > hSyncLength * 0.9 ? 60 : 40;
                    double candidate = pulses[i].Start;
                    if (line0 != candidate || candidateConfidence > firstFieldConfidence)
                    {
                        firstFieldConfidence = candidateConfidence;
                        firstField = !palTiming;
                    }

                    line0 = candidate;
                }
            }

            if (!line0.HasValue)
            {
                (double previousMean, double previousStd) = SliceStats(
                    demodLowPass,
                    pulses[i - 1].Start + pulses[i - 1].Length + 40,
                    pulses[i].Start - 40);
                (double intervalMean, double intervalStd) = SliceStats(
                    demodLowPass,
                    pulses[i].Start + pulses[i].Length + 40,
                    pulses[i + 1].Start - 40);
                (double nextMean, double nextStd) = SliceStats(
                    demodLowPass,
                    pulses[i + 1].Start + pulses[i + 1].Length + 40,
                    pulses[i + 2].Start - 40);
                if (RelativeDifference(previousMean, intervalMean) < 0.05
                    && RelativeDifference(previousMean, nextMean) > 0.15
                    && Math.Abs(previousStd - intervalStd) * 2.0 < Math.Abs(previousStd - nextStd))
                {
                    if (line0 != pulses[i - 1].Start || 20 > firstFieldConfidence)
                    {
                        if (palTiming)
                        {
                            firstField = false;
                        }
                        firstFieldConfidence = 20;
                    }

                    line0 = pulses[i - 1].Start;
                }
                else if (RelativeDifference(previousMean, intervalMean) > 0.15
                    && RelativeDifference(previousMean, nextMean) > 0.15
                    && intervalStd * 2.0 > previousStd)
                {
                    if (line0 != pulses[i].Start || 20 > firstFieldConfidence)
                    {
                        firstField = !palTiming;
                        firstFieldConfidence = 20;
                    }

                    line0 = pulses[i].Start;
                }
            }
        }

        if (line0 > limit
            && backupLine0.HasValue
            && backupLine0 < line0 - (meanLineLength * (frameLines - 5) / 2.0))
        {
            diagnosticMessage =
                "WARNING, line0 hsync not found for current field, but vsync area found, using predicted position, result may be garbled.";
            UseBackup(ref line0, ref firstField, ref firstFieldConfidence, backupLine0, backupFirstField, backupFirstFieldConfidence);
        }
        else if (line0 > limit
            && relaxed
            && backupLine0.HasValue
            && backupLine0 < limit)
        {
            diagnosticMessage = "Switching to backup line0 estimation as primary is out of range.";
            UseBackup(ref line0, ref firstField, ref firstFieldConfidence, backupLine0, backupFirstField, backupFirstFieldConfidence);
        }
        else if (line0 > limit && !expectedLine0.HasValue)
        {
            diagnosticMessage =
                "WARNING, line0 hsync not found for current field, probably skipping one field.";
        }

        if (!line0.HasValue && backupLine0.HasValue)
        {
            diagnosticMessage =
                "WARNING, line0 hsync not found in entire block, but vsync area found, using predicted position, result may be garbled.";
            UseBackup(ref line0, ref firstField, ref firstFieldConfidence, backupLine0, backupFirstField, backupFirstFieldConfidence);
        }

        if ((!line0.HasValue || line0 > limit)
            && expectedLine0.HasValue
            && expectedLine0 < limit
            && expectedLine0 > (-5.0 * meanLineLength))
        {
            Pulse? best = null;
            double bestDistance = double.PositiveInfinity;
            foreach (Pulse pulse in pulses)
            {
                double distance = Math.Abs(pulse.Start - expectedLine0.Value);
                if (distance < 0.7 * meanLineLength && distance < bestDistance)
                {
                    best = pulse;
                    bestDistance = distance;
                }
            }

            if (best.HasValue)
            {
                line0 = best.Value.Start;
                if (expectedFirstField.HasValue)
                {
                    firstField = expectedFirstField;
                    firstFieldConfidence = 50;
                }
            }
            else if (relaxed && expectedLine0 > 0.0)
            {
                line0 = expectedLine0;
                if (expectedFirstField.HasValue)
                {
                    firstField = expectedFirstField;
                    firstFieldConfidence = 40;
                }
            }
            else if (line0 > limit)
            {
                diagnosticMessage =
                    "WARNING, line0 hsync not found for current field, probably skipping one field.";
            }
        }

        if (line0.HasValue)
        {
            return new FallbackVSyncResolution(
                line0.Value,
                null,
                firstField,
                firstFieldConfidence,
                diagnosticMessage);
        }

        List<Pulse> longPulses = rawPulses
            .Where(pulse => pulse.Length >= vSyncRange.Minimum && pulse.Length <= vSyncRange.Maximum * 10.0)
            .ToList();
        if (longPulses.Count == 0)
        {
            return null;
        }

        double firstLongPulse = longPulses[0].Start;
        foreach (ClassifiedSyncPulse pulse in validPulses)
        {
            if (pulse.Pulse.Start > firstLongPulse)
            {
                break;
            }

            if (pulse.Kind == SyncPulseKind.HSync)
            {
                line0 = pulse.Pulse.Start;
            }
        }

        if (!line0.HasValue)
        {
            diagnosticMessage =
                "WARNING, line0 hsync not found, guessing something, result may be garbled.";
            line0 = firstLongPulse - (3.0 * meanLineLength);
        }

        double? lastLineLocation = longPulses.Count == 6
            && longPulses[3].Start - longPulses[2].Start > vSyncRange.Maximum * 10.0
                ? longPulses[3].Start - (numEqualizingPulses * meanLineLength)
                : null;
        return new FallbackVSyncResolution(
            line0.Value,
            lastLineLocation,
            null,
            -1,
            diagnosticMessage);
    }

    private static IReadOnlyList<Pulse> FilterClosePulses(IReadOnlyList<Pulse> rawPulses, double lineLength)
    {
        var filtered = new List<Pulse> { rawPulses[0] };
        int i = 1;
        while (i < rawPulses.Count - 2)
        {
            if (rawPulses[i + 1].Start - rawPulses[i].Start > 0.45 * lineLength
                && rawPulses[i].Start - rawPulses[i - 1].Start > 0.45 * lineLength)
            {
                filtered.Add(rawPulses[i]);
            }
            else
            {
                double d12 = DistanceLines(rawPulses[i - 1], rawPulses[i], lineLength);
                double d13 = DistanceLines(rawPulses[i - 1], rawPulses[i + 1], lineLength);
                double d24 = DistanceLines(rawPulses[i], rawPulses[i + 2], lineLength);
                double d34 = DistanceLines(rawPulses[i + 1], rawPulses[i + 2], lineLength);
                double e12 = Math.Min(Math.Abs(d12 - 0.5), Math.Abs(d12 - 1.0));
                double e13 = Math.Min(Math.Abs(d13 - 0.5), Math.Abs(d13 - 1.0));
                double e24 = Math.Min(Math.Abs(d24 - 0.5), Math.Abs(d24 - 1.0));
                double e34 = Math.Min(Math.Abs(d34 - 0.5), Math.Abs(d34 - 1.0));
                filtered.Add(e13 + e34 < e12 + e24 ? rawPulses[i + 1] : rawPulses[i]);
                i++;
            }

            i++;
        }

        while (i < rawPulses.Count)
        {
            filtered.Add(rawPulses[i]);
            i++;
        }

        return filtered;
    }

    private static int CountFollowingHalfLines(
        IReadOnlyList<Pulse> pulses,
        int start,
        double lineLength,
        double shortPulseMaximum)
    {
        int halfLines = 0;
        for (int j = start; j < Math.Min(start + 9, pulses.Count - 1); j++)
        {
            double distance = DistanceLines(pulses[j], pulses[j + 1], lineLength);
            if (Near(distance, 0.5)
                && pulses[j].Length < shortPulseMaximum
                && pulses[j + 1].Length < shortPulseMaximum)
            {
                halfLines++;
            }
            else if (Near(distance, 1.0)
                && pulses[j].Length < shortPulseMaximum
                && pulses[j + 1].Length < shortPulseMaximum)
            {
                break;
            }
            else
            {
                return 0;
            }
        }

        return halfLines;
    }

    private static (int Phase0, int Phase1, int Other) CountPulsePhases(
        IReadOnlyList<Pulse> pulses,
        int anchor,
        int minimumDistance,
        int maximumDistance,
        double measuredLineLength)
    {
        int phase0 = 0;
        int phase1 = 0;
        int other = 0;
        for (int distance = minimumDistance; distance <= Math.Min(anchor, maximumDistance); distance++)
        {
            int parity = RoundedHalfLineParity(pulses[anchor - 2].Start - pulses[anchor - distance].Start, measuredLineLength)
                + RoundedHalfLineParity(pulses[anchor - 2].Start - pulses[anchor - distance + 1].Start, measuredLineLength)
                + RoundedHalfLineParity(pulses[anchor - 2].Start - pulses[anchor - distance + 2].Start, measuredLineLength);
            if (parity == 0)
            {
                phase0++;
            }
            else if (parity == 3)
            {
                phase1++;
            }
            else
            {
                other++;
            }

            if (phase0 + phase1 >= 5)
            {
                break;
            }
        }

        return (phase0, phase1, other);
    }

    private static int DominantPhase(int phase0, int phase1, int other)
    {
        if (phase0 >= phase1 && phase0 >= other)
        {
            return 0;
        }

        return phase1 >= other ? 1 : 2;
    }

    private static int RoundedHalfLineParity(double distance, double measuredLineLength)
    {
        int rounded = (int)Math.Round((distance / measuredLineLength) * 2.0, MidpointRounding.ToEven);
        return ((rounded % 2) + 2) % 2;
    }

    private static double? FindPulseNear(
        IReadOnlyList<Pulse> pulses,
        int start,
        int endExclusive,
        double estimate,
        double lineLength,
        double toleranceLines)
    {
        for (int i = Math.Max(0, start); i < Math.Min(endExclusive, pulses.Count); i++)
        {
            if (Math.Abs(pulses[i].Start - estimate) / lineLength < toleranceLines)
            {
                return pulses[i].Start;
            }
        }

        return null;
    }

    private static void SaveBackup(
        double estimate,
        bool? firstField,
        int confidence,
        ref double? backupLine0,
        ref bool? backupFirstField,
        ref int backupConfidence)
    {
        if (backupLine0.HasValue)
        {
            return;
        }

        backupLine0 = estimate;
        backupFirstField = firstField;
        backupConfidence = confidence;
    }

    private static void UseBackup(
        ref double? line0,
        ref bool? firstField,
        ref int confidence,
        double? backupLine0,
        bool? backupFirstField,
        int backupConfidence)
    {
        line0 = backupLine0;
        firstField = backupFirstField;
        confidence = backupConfidence - 20;
    }

    private static (double Mean, double StandardDeviation) SliceStats(
        ReadOnlySpan<double> values,
        int start,
        int endExclusive)
    {
        start = NormalizeNumpySliceIndex(start, values.Length);
        endExclusive = NormalizeNumpySliceIndex(endExclusive, values.Length);
        if (endExclusive <= start)
        {
            return (double.NaN, double.NaN);
        }

        return NumpyReduction.MeanStandardDeviationFloat64(values[start..endExclusive]);
    }

    private static int NormalizeNumpySliceIndex(int index, int length)
    {
        long normalized = index;
        if (normalized < 0)
        {
            normalized += length;
        }

        return (int)Math.Clamp(normalized, 0L, length);
    }

    private static double RelativeDifference(double first, double second)
        => Math.Abs(first - second) / (first + second);

    private static bool Near(double value, double target)
        => Math.Abs(value - target) < 0.06;

    private static double DistanceLines(Pulse first, Pulse second, double lineLength)
        => (second.Start - first.Start) / lineLength;
}
