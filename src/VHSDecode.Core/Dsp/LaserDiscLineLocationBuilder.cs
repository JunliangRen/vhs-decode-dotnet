namespace VHSDecode.Core.Dsp;

using VHSDecode.Core.Tbc;

public sealed record LaserDiscLineLocationBuildResult(
    LineLocationResult LineLocations,
    bool SkipDetected);

public static class LaserDiscLineLocationBuilder
{
    private const double HSyncToleranceLines = 0.4;

    public static LaserDiscLineLocationBuildResult Build(
        IReadOnlyList<ClassifiedSyncPulse> validPulses,
        double line0Location,
        double? nextVBlankLocation,
        double meanLineLength,
        double nominalLineLength,
        int fieldLineCount,
        int processedLines,
        int outputLineCount)
    {
        ArgumentNullException.ThrowIfNull(validPulses);
        if (!double.IsFinite(line0Location))
        {
            throw new ArgumentOutOfRangeException(nameof(line0Location));
        }

        if (!double.IsFinite(meanLineLength) || meanLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanLineLength));
        }

        if (!double.IsFinite(nominalLineLength) || nominalLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalLineLength));
        }

        if (fieldLineCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldLineCount));
        }

        if (processedLines <= 0 || outputLineCount < 0 || outputLineCount >= processedLines)
        {
            throw new ArgumentOutOfRangeException(nameof(processedLines));
        }

        bool skipDetected = nextVBlankLocation.HasValue
            && ((nextVBlankLocation.Value - line0Location) / nominalLineLength) < fieldLineCount - 5;
        var detected = new double[processedLines];
        var distances = new double[processedLines];
        Array.Fill(detected, -1.0);
        Array.Fill(distances, double.PositiveInfinity);

        foreach (ClassifiedSyncPulse pulse in validPulses)
        {
            double lineLocation = (pulse.Pulse.Start - line0Location) / meanLineLength;
            int roundedLine = RoundToEven(lineLocation);
            double distance = Math.Abs(lineLocation - roundedLine);

            if (skipDetected && pulse.Kind == SyncPulseKind.HSync && roundedLine > 23)
            {
                double endLineLocation = fieldLineCount
                    - ((nextVBlankLocation!.Value - pulse.Pulse.Start) / meanLineLength);
                int roundedEndLine = RoundToEven(endLineLocation);
                double endDistance = Math.Abs(endLineLocation - roundedEndLine);
                if (endDistance < distance)
                {
                    lineLocation = endLineLocation;
                    roundedLine = roundedEndLine;
                    distance = endDistance;
                }
            }

            if (roundedLine < 0
                || roundedLine >= processedLines
                || distance > HSyncToleranceLines
                || distance > distances[roundedLine])
            {
                continue;
            }

            if (roundedLine > 0
                && !pulse.InOrder
                && (pulse.Kind != SyncPulseKind.HSync || roundedLine < 10))
            {
                continue;
            }

            detected[roundedLine] = pulse.Pulse.Start;
            distances[roundedLine] = distance;
        }

        var filled = detected.ToArray();
        var errors = new bool[processedLines];
        if (filled[0] < 0.0)
        {
            int nextValid = FindNextValid(detected, 0, outputLineCount);
            if (nextValid < 0)
            {
                throw new InvalidOperationException("No valid LaserDisc line locations were detected.");
            }

            filled[0] = detected[nextValid] - (nextValid * meanLineLength);
            if (filled[0] < nominalLineLength)
            {
                throw new InvalidOperationException("The first LaserDisc line location was outside the decoded span.");
            }
        }

        for (int line = 1; line < filled.Length; line++)
        {
            if (filled[line] >= 0.0)
            {
                continue;
            }

            errors[line] = true;
            int previousValid = FindPreviousValid(detected, line);
            int nextValid = FindNextValid(detected, line, outputLineCount);
            if (previousValid < 0)
            {
                filled[line] = detected[nextValid] - (nominalLineLength * (nextValid - line));
            }
            else if (nextValid >= 0)
            {
                double averageLength = (detected[nextValid] - detected[previousValid]) / (nextValid - previousValid);
                filled[line] = detected[previousValid] + (averageLength * (line - previousValid));
            }
            else
            {
                filled[line] = detected[previousValid] + (nominalLineLength * (line - previousValid));
            }
        }

        return new LaserDiscLineLocationBuildResult(new LineLocationResult(filled, errors), skipDetected);
    }

    private static int RoundToEven(double value)
    {
        return checked((int)Math.Round(value, MidpointRounding.ToEven));
    }

    private static int FindPreviousValid(IReadOnlyList<double> locations, int start)
    {
        for (int index = start; index >= 0; index--)
        {
            if (locations[index] > 0.0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindNextValid(IReadOnlyList<double> locations, int start, int outputLineCount)
    {
        for (int index = start; index <= outputLineCount; index++)
        {
            if (locations[index] > 0.0)
            {
                return index;
            }
        }

        return -1;
    }
}

public static class LaserDiscPlayerSkipDetector
{
    private const int LookaheadLines = 8;

    public static int ScorePreviousFieldEnd(
        ReadOnlySpan<double> video,
        IReadOnlyList<double> lineLocations,
        int outputLineCount,
        int lineOffset,
        double nominalLineLength,
        VideoOutputConverter converter)
    {
        ArgumentNullException.ThrowIfNull(lineLocations);
        ArgumentNullException.ThrowIfNull(converter);
        if (outputLineCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLineCount));
        }

        if (lineOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineOffset));
        }

        if (!double.IsFinite(nominalLineLength) || nominalLineLength <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalLineLength));
        }

        int score = 0;
        int vSyncLines = 0;
        for (int line = outputLineCount; line < outputLineCount + LookaheadLines; line++)
        {
            int physicalLine = line + lineOffset;
            if (physicalLine < 0 || physicalLine >= lineLocations.Count)
            {
                score--;
                continue;
            }

            int start = (int)lineLocations[physicalLine];
            int end = (int)(lineLocations[physicalLine] + nominalLineLength + 1.0);
            start = Math.Clamp(start, 0, video.Length);
            end = Math.Clamp(end, start, video.Length);
            if (end <= start)
            {
                score--;
                continue;
            }

            double lineIre = converter.HzToIre(Median(video.Slice(start, end - start)));
            if (PulseDetection.InRange(lineIre, converter.VSyncIre - 10.0, converter.VSyncIre / 2.0))
            {
                vSyncLines++;
            }
            else if (PulseDetection.InRange(lineIre, -5.0, 5.0))
            {
                score++;
            }
            else
            {
                score--;
            }
        }

        if (vSyncLines >= 2)
        {
            return 100;
        }

        if (vSyncLines == 1 && score > 0)
        {
            return 50;
        }

        return score > 0 ? 25 : 0;
    }

    public static bool IsFirstVBlankWithinPulseLimit(
        IReadOnlyList<ClassifiedSyncPulse> validPulses,
        double firstEqualizingLocation,
        int previousSkipScore,
        int pulseLimit = 100)
    {
        ArgumentNullException.ThrowIfNull(validPulses);
        if (pulseLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pulseLimit));
        }

        if (previousSkipScore < 50)
        {
            return true;
        }

        for (int index = 0; index < validPulses.Count; index++)
        {
            if (validPulses[index].Pulse.Start >= firstEqualizingLocation)
            {
                return index <= pulseLimit;
            }
        }

        return false;
    }

    private static double Median(ReadOnlySpan<double> values)
    {
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }
}
