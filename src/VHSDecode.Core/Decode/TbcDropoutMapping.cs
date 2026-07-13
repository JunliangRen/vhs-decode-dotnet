using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

public readonly record struct RfDropoutRange(double Start, double End);

public sealed record TbcDropoutMap(int[] FieldLine, int[] StartX, int[] EndX)
{
    public static TbcDropoutMap Empty { get; } = new([], [], []);

    public int Count => FieldLine.Length;
}

public enum TbcDropoutDetectionMode
{
    Disabled,
    TapeEnvelope,
    LaserDiscDemod
}

public sealed record TbcDropoutDetectionOptions(
    bool Enabled,
    double ThresholdFraction,
    double? AbsoluteThreshold,
    double Hysteresis,
    TbcDropoutDetectionMode Mode = TbcDropoutDetectionMode.TapeEnvelope)
{
    public static TbcDropoutDetectionOptions Disabled { get; } = new(
        false,
        0.18,
        null,
        1.25,
        TbcDropoutDetectionMode.Disabled);

    public static TbcDropoutDetectionOptions DefaultDdd { get; } = new(true, 0.18, null, 1.25);

    public static TbcDropoutDetectionOptions DefaultCxAdc { get; } = new(true, 0.35, null, 1.25);

    public static TbcDropoutDetectionOptions LaserDisc { get; } = new(
        true,
        0.18,
        null,
        1.25,
        TbcDropoutDetectionMode.LaserDiscDemod);
}

public static class RfDropoutDetector
{
    public const int DefaultMergeThreshold = 30;
    public const int DefaultMinimumLength = 10;

    public static IReadOnlyList<RfDropoutRange> FindDropouts(
        ReadOnlySpan<double> envelope,
        int start,
        int end,
        double threshold,
        double hysteresis,
        int mergeThreshold = DefaultMergeThreshold,
        int minimumLength = DefaultMinimumLength)
    {
        if (start < 0 || end < start || end > envelope.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (threshold < 0 || hysteresis <= 0 || mergeThreshold < 0 || minimumLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        double downThreshold = threshold;
        double upThreshold = threshold * hysteresis;
        var rawRanges = new List<(int Start, int End)>();
        int dropoutIndex = -1;

        for (int i = start; i < end; i++)
        {
            double value = envelope[i];
            if (value <= downThreshold)
            {
                bool dropoutEnded = dropoutIndex >= 0
                    && rawRanges[dropoutIndex].End != -1
                    && i - rawRanges[dropoutIndex].End > mergeThreshold;
                if (dropoutIndex == -1 || dropoutEnded)
                {
                    dropoutIndex++;
                    rawRanges.Add((i, -1));
                }
            }
            else if (value >= upThreshold
                && dropoutIndex != -1
                && rawRanges[dropoutIndex].End == -1)
            {
                rawRanges[dropoutIndex] = (rawRanges[dropoutIndex].Start, i);
            }
        }

        if (dropoutIndex != -1 && rawRanges[dropoutIndex].End == -1)
        {
            rawRanges[dropoutIndex] = (rawRanges[dropoutIndex].Start, end);
        }

        return rawRanges
            .Where(range => range.End - range.Start > minimumLength)
            .Select(range => new RfDropoutRange(range.Start, range.End))
            .ToArray();
    }

    public static IReadOnlyList<RfDropoutRange> FindMarkedRanges(
        ReadOnlySpan<bool> marks,
        int start,
        int end,
        int mergeThreshold = DefaultMergeThreshold,
        int minimumLength = DefaultMinimumLength,
        int paddingBefore = 0,
        int paddingAfter = 0)
    {
        if (start < 0 || end < start || end > marks.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (mergeThreshold < 0 || minimumLength < 0 || paddingBefore < 0 || paddingAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeThreshold));
        }

        var ranges = new List<RfDropoutRange>();
        int currentStart = -1;
        int currentEnd = -1;

        for (int i = start; i < end; i++)
        {
            if (!marks[i])
            {
                continue;
            }

            if (currentStart < 0)
            {
                currentStart = i;
                currentEnd = i + 1;
            }
            else if (i <= currentEnd + mergeThreshold)
            {
                currentEnd = i + 1;
            }
            else
            {
                AddIfLongEnough(ranges, Math.Max(start, currentStart - paddingBefore), Math.Min(end, currentEnd + paddingAfter), minimumLength);
                currentStart = i;
                currentEnd = i + 1;
            }
        }

        if (currentStart >= 0)
        {
            AddIfLongEnough(ranges, Math.Max(start, currentStart - paddingBefore), Math.Min(end, currentEnd + paddingAfter), minimumLength);
        }

        return ranges;
    }

    public static IReadOnlyList<RfDropoutRange> MergeRanges(
        IEnumerable<RfDropoutRange> ranges,
        int mergeThreshold = DefaultMergeThreshold,
        int minimumLength = 0)
    {
        if (mergeThreshold < 0 || minimumLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeThreshold));
        }

        List<RfDropoutRange> sorted = ranges
            .Where(range => range.End > range.Start)
            .OrderBy(range => range.Start)
            .ToList();
        if (sorted.Count == 0)
        {
            return [];
        }

        var merged = new List<RfDropoutRange>();
        double currentStart = sorted[0].Start;
        double currentEnd = sorted[0].End;

        for (int i = 1; i < sorted.Count; i++)
        {
            RfDropoutRange range = sorted[i];
            if (range.Start <= currentEnd + mergeThreshold)
            {
                currentEnd = Math.Max(currentEnd, range.End);
                continue;
            }

            AddIfLongEnough(merged, currentStart, currentEnd, minimumLength);
            currentStart = range.Start;
            currentEnd = range.End;
        }

        AddIfLongEnough(merged, currentStart, currentEnd, minimumLength);
        return merged;
    }

    private static void AddIfLongEnough(List<RfDropoutRange> ranges, int start, int end, int minimumLength)
    {
        if (end - start > minimumLength)
        {
            ranges.Add(new RfDropoutRange(start, end));
        }
    }

    private static void AddIfLongEnough(List<RfDropoutRange> ranges, double start, double end, int minimumLength)
    {
        if (end - start > minimumLength)
        {
            ranges.Add(new RfDropoutRange(start, end));
        }
    }
}

public static class LaserDiscDropoutDetector
{
    public const int MergeDistance = 20;
    public const int PaddingBefore = 8;
    public const int PaddingAfter = 4;

    public static IReadOnlyList<RfDropoutRange> BuildErrorRanges(
        ReadOnlySpan<bool> errorMap,
        int start,
        int end,
        double sampleRateMHz)
    {
        if (start < 0 || end < start || end > errorMap.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (!double.IsFinite(sampleRateMHz) || sampleRateMHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateMHz));
        }

        int firstError = -1;
        for (int i = start; i < end; i++)
        {
            if (errorMap[i])
            {
                firstError = i;
                break;
            }
        }

        if (firstError < 0)
        {
            return [];
        }

        var ranges = new List<RfDropoutRange>();
        double currentStart = firstError;
        double currentEnd = firstError;
        for (int error = firstError; error < end; error++)
        {
            if (!errorMap[error])
            {
                continue;
            }

            if (error > currentStart && error <= currentEnd + MergeDistance)
            {
                double padding = (error - currentStart) * 1.7;
                padding = Math.Min(padding, sampleRateMHz * 12.0);
                currentEnd = currentStart + padding;
            }
            else if (error > firstError)
            {
                ranges.Add(new RfDropoutRange(currentStart - PaddingBefore, currentEnd + PaddingAfter));
                currentStart = error;
                currentEnd = error;
            }
        }

        ranges.Add(new RfDropoutRange(currentStart, currentEnd));
        return ranges;
    }
}

public static class TbcDropoutMapper
{
    public static TbcDropoutMap MapTapeRfToTbc(
        IReadOnlyList<RfDropoutRange> dropouts,
        IReadOnlyList<double> lineLocations,
        int outputLineLength,
        int startLine,
        int endLine,
        int lineOffset)
    {
        if (outputLineLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLineLength));
        }

        if (startLine < 0 || endLine <= startLine || endLine >= lineLocations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startLine));
        }

        var fieldLines = new List<int>();
        var starts = new List<int>();
        var ends = new List<int>();
        int lineIndex = startLine;
        double lineStart = lineLocations[lineIndex];
        double lineEnd = lineLocations[lineIndex + 1];

        foreach (RfDropoutRange dropout in dropouts)
        {
            bool foundStart = false;
            while (lineIndex < endLine)
            {
                double lineLength = lineEnd - lineStart;
                if ((dropout.Start >= lineStart || lineIndex == startLine)
                    && dropout.Start < lineEnd
                    && lineLength > 0.0)
                {
                    int startPixel = (int)Math.Floor(
                        ((dropout.Start - lineStart) / lineLength) * outputLineLength);
                    fieldLines.Add(lineIndex - lineOffset);
                    starts.Add(Math.Max(0, startPixel));
                    foundStart = true;
                    break;
                }

                lineIndex++;
                if (lineIndex < endLine)
                {
                    lineStart = lineLocations[lineIndex];
                    lineEnd = lineLocations[lineIndex + 1];
                }
            }

            if (!foundStart)
            {
                break;
            }

            while (lineIndex < endLine)
            {
                double lineLength = lineEnd - lineStart;
                if (dropout.End < lineEnd && lineLength > 0.0)
                {
                    int endPixel = (int)Math.Ceiling(
                        ((dropout.End - lineStart) / lineLength) * outputLineLength);
                    ends.Add(Math.Min(outputLineLength, endPixel));
                    break;
                }

                ends.Add(outputLineLength);
                lineIndex++;
                if (lineIndex < endLine)
                {
                    lineStart = lineLocations[lineIndex];
                    lineEnd = lineLocations[lineIndex + 1];
                    starts.Add(0);
                    fieldLines.Add(lineIndex - lineOffset);
                }
            }
        }

        return fieldLines.Count == 0
            ? TbcDropoutMap.Empty
            : new TbcDropoutMap(fieldLines.ToArray(), starts.ToArray(), ends.ToArray());
    }

    public static TbcDropoutMap MapRfToTbc(
        IReadOnlyList<RfDropoutRange> dropouts,
        IReadOnlyList<double> lineLocations,
        int outputLineLength,
        int startLine = 0,
        int? endLine = null,
        int lineNumberOffset = 0)
    {
        if (outputLineLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLineLength));
        }

        int actualEndLine = endLine ?? lineLocations.Count - 1;
        if (startLine < 0 || actualEndLine <= startLine || actualEndLine >= lineLocations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startLine));
        }

        var fieldLines = new List<int>();
        var starts = new List<int>();
        var ends = new List<int>();

        foreach (RfDropoutRange dropout in dropouts)
        {
            double dropoutStart = Math.Max(dropout.Start, lineLocations[startLine]);
            double dropoutEnd = Math.Min(dropout.End, lineLocations[actualEndLine]);
            if (dropoutEnd <= dropoutStart)
            {
                continue;
            }

            int line = FindLine(lineLocations, startLine, actualEndLine, dropoutStart);
            while (line < actualEndLine && dropoutEnd > lineLocations[line])
            {
                double lineStart = lineLocations[line];
                double lineEnd = lineLocations[line + 1];
                double lineLength = lineEnd - lineStart;
                if (lineLength <= 0)
                {
                    throw new ArgumentException("Line locations must be strictly increasing.", nameof(lineLocations));
                }

                double segmentStart = Math.Max(dropoutStart, lineStart);
                double segmentEnd = Math.Min(dropoutEnd, lineEnd);
                if (segmentEnd <= segmentStart)
                {
                    break;
                }

                fieldLines.Add(line - lineNumberOffset);
                starts.Add(ClampPixel((int)Math.Floor((segmentStart - lineStart) / lineLength * outputLineLength), outputLineLength));
                ends.Add(ClampPixel((int)Math.Ceiling((segmentEnd - lineStart) / lineLength * outputLineLength), outputLineLength));

                if (dropoutEnd <= lineEnd)
                {
                    break;
                }

                line++;
            }
        }

        return fieldLines.Count == 0
            ? TbcDropoutMap.Empty
            : new TbcDropoutMap(fieldLines.ToArray(), starts.ToArray(), ends.ToArray());
    }

    public static TbcDropoutMap MapLaserDiscRfToTbc(
        IReadOnlyList<RfDropoutRange> dropouts,
        IReadOnlyList<double> lineLocations,
        int outputLineLength,
        int lineOffset,
        int lineCount)
    {
        if (outputLineLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLineLength));
        }

        int firstLine = lineOffset;
        int endLine = lineOffset + lineCount;
        if (lineOffset < 0 || lineCount <= 0 || endLine >= lineLocations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(lineOffset));
        }

        var fieldLines = new List<int>();
        var starts = new List<int>();
        var ends = new List<int>();
        var remaining = new Queue<RfDropoutRange>(dropouts);
        if (remaining.Count == 0)
        {
            return TbcDropoutMap.Empty;
        }

        RfDropoutRange? current = remaining.Dequeue();
        while (remaining.Count > 0 && current.Value.Start < lineLocations[lineOffset])
        {
            current = remaining.Dequeue();
        }

        for (int line = firstLine; line < endLine && current.HasValue; line++)
        {
            while (current.HasValue
                && PulseDetection.InRange(current.Value.Start, lineLocations[line], lineLocations[line + 1]))
            {
                double lineLength = lineLocations[line + 1] - lineLocations[line];
                if (lineLength <= 0.0)
                {
                    throw new ArgumentException("Line locations must be strictly increasing.", nameof(lineLocations));
                }

                int startPixel = (int)(((current.Value.Start - lineLocations[line]) / lineLength) * outputLineLength);
                int endPixel = checked((int)Math.Round(
                    ((current.Value.End - lineLocations[line]) / lineLength) * outputLineLength,
                    MidpointRounding.ToEven));
                int outputLine = line - lineOffset;
                if (endPixel > outputLineLength)
                {
                    int lineSpan = endPixel / outputLineLength;
                    fieldLines.Add(outputLine);
                    starts.Add(startPixel);
                    ends.Add(outputLineLength);
                    for (int n = 0; n < lineSpan - 1; n++)
                    {
                        fieldLines.Add(outputLine + n + 1);
                        starts.Add(0);
                        ends.Add(outputLineLength);
                    }

                    fieldLines.Add(outputLine + lineSpan);
                    starts.Add(0);
                    ends.Add(endPixel % outputLineLength);
                }
                else
                {
                    fieldLines.Add(outputLine);
                    starts.Add(startPixel);
                    ends.Add(endPixel);
                }

                current = remaining.Count > 0 ? remaining.Dequeue() : null;
            }
        }

        return fieldLines.Count == 0
            ? TbcDropoutMap.Empty
            : new TbcDropoutMap(fieldLines.ToArray(), starts.ToArray(), ends.ToArray());
    }

    private static int FindLine(IReadOnlyList<double> lineLocations, int startLine, int endLine, double sample)
    {
        int line = startLine;
        while (line + 1 < endLine && sample >= lineLocations[line + 1])
        {
            line++;
        }

        return line;
    }

    private static int ClampPixel(int value, int outputLineLength)
    {
        return Math.Clamp(value, 0, outputLineLength);
    }
}
