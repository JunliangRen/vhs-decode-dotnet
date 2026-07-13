using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Dsp;

public sealed record FieldParityDetection(bool IsFirstField, int Confidence, string Method);

public static class FieldParityDetector
{
    private sealed record BoundaryConfidence(
        int Consensus,
        int Detected,
        int FirstConfidence,
        int SecondConfidence,
        int ProgressiveConfidence);

    public static FieldParityDetection ResolveCadence(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        double meanLineLength,
        string system,
        IReadOnlyList<int> fieldLines,
        bool? previousFirstField,
        int minimumConfidence,
        bool hasPreviousHSync,
        bool hasFallbackLine = false,
        bool? fallbackFirstField = null,
        int fallbackConfidence = -1)
    {
        ArgumentNullException.ThrowIfNull(pulses);
        ArgumentNullException.ThrowIfNull(fieldLines);
        if (meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        BoundaryConfidence boundary = MeasureVBlankBoundaryConsensus(
            pulses,
            meanLineLength,
            FormatCatalog.ParentSystem(system),
            fieldLines);
        bool isFirstField;
        string method;
        int confidence = 0;
        if (previousFirstField.HasValue)
        {
            isFirstField = !previousFirstField.Value;
            method = "previous-cadence";
        }
        else
        {
            isFirstField = boundary.Detected == 0
                || Math.Round(
                    boundary.Consensus / (double)boundary.Detected,
                    MidpointRounding.AwayFromZero) == 1.0
                || fallbackFirstField == true;
            method = "initial-cadence";
        }

        int effectiveMinimum = minimumConfidence;
        if (boundary.Detected > 0 && !hasFallbackLine && !hasPreviousHSync && effectiveMinimum > 50)
        {
            effectiveMinimum = 50;
        }

        if (boundary.FirstConfidence >= effectiveMinimum
            && boundary.FirstConfidence > boundary.SecondConfidence)
        {
            isFirstField = true;
            confidence = boundary.FirstConfidence;
            method = "vblank-boundary-consensus";
        }
        else if (boundary.SecondConfidence >= effectiveMinimum
            && boundary.FirstConfidence < boundary.SecondConfidence)
        {
            isFirstField = false;
            confidence = boundary.SecondConfidence;
            method = "vblank-boundary-consensus";
        }

        if ((hasFallbackLine || fallbackFirstField.HasValue)
            && fallbackConfidence > boundary.FirstConfidence
            && fallbackConfidence > boundary.SecondConfidence)
        {
            isFirstField = fallbackFirstField == true;
            confidence = fallbackConfidence;
            method = "fallback-vsync";
        }

        return new FieldParityDetection(isFirstField, confidence, method);
    }

    public static FieldParityDetection? Detect(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        double meanLineLength,
        string system,
        int minimumConfidence = 1,
        IReadOnlyList<int>? fieldLines = null)
    {
        if (pulses.Count < 3 || meanLineLength <= 0)
        {
            return null;
        }

        string parent = FormatCatalog.ParentSystem(system);
        FieldParityDetection? consensus = DetectFromVBlankBoundaryConsensus(
            pulses,
            meanLineLength,
            parent,
            fieldLines);
        if (consensus is not null && consensus.Confidence >= minimumConfidence)
        {
            return consensus;
        }

        FieldParityDetection? detection = parent == "PAL"
            ? DetectFromVSyncBoundaryGaps(pulses, meanLineLength, firstFieldGapLines: 0.5, secondFieldGapLines: 1.0)
            : DetectFromVSyncBoundaryGaps(pulses, meanLineLength, firstFieldGapLines: 1.0, secondFieldGapLines: 0.5);
        return detection is not null && detection.Confidence >= minimumConfidence ? detection : null;
    }

    private static FieldParityDetection? DetectFromVBlankBoundaryConsensus(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        double meanLineLength,
        string parentSystem,
        IReadOnlyList<int>? fieldLines)
    {
        if (fieldLines is null)
        {
            return null;
        }

        BoundaryConfidence boundary = MeasureVBlankBoundaryConsensus(
            pulses,
            meanLineLength,
            parentSystem,
            fieldLines);
        if (boundary.Detected == 0 || boundary.FirstConfidence == boundary.SecondConfidence)
        {
            return null;
        }

        return boundary.FirstConfidence > boundary.SecondConfidence
            ? new FieldParityDetection(true, boundary.FirstConfidence, "vblank-boundary-consensus")
            : new FieldParityDetection(false, boundary.SecondConfidence, "vblank-boundary-consensus");
    }

    private static BoundaryConfidence MeasureVBlankBoundaryConsensus(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        double meanLineLength,
        string parentSystem,
        IReadOnlyList<int> fieldLines)
    {
        if (fieldLines.Count < 2 || pulses.Count == 0)
        {
            return new BoundaryConfidence(0, 0, 0, 0, 0);
        }

        double[] fieldOrderLengths = [-1.0, -1.0, -1.0, -1.0];
        int fieldGroup = 0;
        int lastPulse = -1;
        double secondVBlankBoundary = pulses[0].Pulse.Start + (fieldLines[0] * meanLineLength);
        for (int i = 0; i < pulses.Count; i++)
        {
            ClassifiedSyncPulse current = pulses[i];
            if (lastPulse >= 0 && current.InOrder)
            {
                ClassifiedSyncPulse previous = pulses[lastPulse];
                if (fieldGroup == 0 && current.Pulse.Start > secondVBlankBoundary)
                {
                    fieldGroup = 2;
                }

                if (previous.Kind == SyncPulseKind.HSync && current.Kind != SyncPulseKind.HSync)
                {
                    fieldOrderLengths[fieldGroup] = RoundNearestHalfLine(
                        (current.Pulse.Start - previous.Pulse.Start) / meanLineLength);
                }
                else if (previous.Kind != SyncPulseKind.HSync && current.Kind == SyncPulseKind.HSync)
                {
                    fieldOrderLengths[fieldGroup + 1] = RoundNearestHalfLine(
                        (current.Pulse.Start - previous.Pulse.Start) / meanLineLength);
                }
            }

            lastPulse = i;
        }

        double[] firstFieldLengths = parentSystem == "NTSC"
            ? [1.0, 0.5, 0.5, 1.0]
            : [0.5, 0.5, 1.0, 1.0];
        double[] secondFieldLengths = parentSystem == "NTSC"
            ? [0.5, 1.0, 1.0, 0.5]
            : [1.0, 1.0, 0.5, 0.5];
        double[] progressiveFieldLengths = parentSystem == "NTSC"
            ? [1.0, 0.5, 1.0, 0.5]
            : [0.5, 0.5, 0.5, 0.5];

        int consensus = 0;
        int detected = 0;
        int progressiveConsensus = 0;
        int progressiveDetected = 0;
        for (int i = 0; i < fieldOrderLengths.Length; i++)
        {
            double length = fieldOrderLengths[i];
            if (length < 0.0)
            {
                continue;
            }

            if (Math.Abs(length - firstFieldLengths[i]) < 1e-9)
            {
                consensus++;
                detected++;
            }
            else if (Math.Abs(length - secondFieldLengths[i]) < 1e-9)
            {
                detected++;
            }

            if (Math.Abs(length - progressiveFieldLengths[i]) < 1e-9)
            {
                progressiveConsensus++;
                progressiveDetected++;
            }

            progressiveDetected++;
        }

        if (detected == 0)
        {
            return new BoundaryConfidence(0, 0, 0, 0, 0);
        }

        double weighting = detected / (double)fieldOrderLengths.Length;
        int firstConfidence = (int)Math.Round(
            (consensus / (double)detected) * weighting * 100.0,
            MidpointRounding.AwayFromZero);
        int secondConfidence = (int)Math.Round(
            ((detected - consensus) / (double)detected) * weighting * 100.0,
            MidpointRounding.AwayFromZero);
        int progressiveConfidence = progressiveDetected == 0
            ? 0
            : (int)Math.Round(
                ((progressiveDetected - progressiveConsensus) / (double)progressiveDetected)
                    * (progressiveDetected / (double)fieldOrderLengths.Length)
                    * 100.0,
                MidpointRounding.AwayFromZero);
        return new BoundaryConfidence(
            consensus,
            detected,
            firstConfidence,
            secondConfidence,
            progressiveConfidence);
    }

    private static double RoundNearestHalfLine(double value)
    {
        double halfRounded = Math.Round(value / 0.5, MidpointRounding.AwayFromZero) * 0.5;
        return Math.Round(halfRounded * 10.0, MidpointRounding.AwayFromZero) / 10.0;
    }

    private static FieldParityDetection? DetectFromVSyncBoundaryGaps(
        IReadOnlyList<ClassifiedSyncPulse> pulses,
        double meanLineLength,
        double firstFieldGapLines,
        double secondFieldGapLines)
    {
        (int start, int end)? run = FirstVSyncRun(pulses);
        if (!run.HasValue)
        {
            return null;
        }

        int start = run.Value.start;
        int end = run.Value.end;
        if (start <= 0 || end + 1 >= pulses.Count)
        {
            return null;
        }

        double gapBefore = (pulses[start].Pulse.Start - pulses[start - 1].Pulse.Start) / meanLineLength;
        double gapAfter = (pulses[end + 1].Pulse.Start - pulses[end].Pulse.Start) / meanLineLength;
        double firstScore = Math.Abs(gapBefore - firstFieldGapLines) + Math.Abs(gapAfter - firstFieldGapLines);
        double secondScore = Math.Abs(gapBefore - secondFieldGapLines) + Math.Abs(gapAfter - secondFieldGapLines);
        double bestScore = Math.Min(firstScore, secondScore);
        double ambiguity = Math.Abs(firstScore - secondScore);

        if (bestScore > 0.35 || ambiguity < 0.20)
        {
            return null;
        }

        int confidence = Math.Clamp((int)Math.Round((1.0 - Math.Min(1.0, bestScore)) * 100.0), 1, 100);
        return new FieldParityDetection(firstScore < secondScore, confidence, "vsync-boundary-gaps");
    }

    private static (int Start, int End)? FirstVSyncRun(IReadOnlyList<ClassifiedSyncPulse> pulses)
    {
        int start = -1;
        for (int i = 0; i < pulses.Count; i++)
        {
            if (pulses[i].Kind == SyncPulseKind.VSync)
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                return (start, i - 1);
            }
        }

        return start >= 0 ? (start, pulses.Count - 1) : null;
    }
}
