namespace VHSDecode.Core.Dsp;

public static class SyncConfidenceCalculator
{
    public static int Compute(
        IReadOnlyList<double> lineLocations,
        int lineCount,
        int initialConfidence = 100,
        int lineOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(lineLocations);
        if (lineCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineCount));
        }

        if (lineOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineOffset));
        }

        int confidence = Math.Clamp(initialConfidence, 0, 100);
        int start = Math.Min(lineOffset, lineLocations.Count);
        int end = Math.Min(start + lineCount, lineLocations.Count);
        if (end - start < 3)
        {
            return confidence;
        }

        double maximumSecondDifference = double.NegativeInfinity;
        double previousDifference = lineLocations[start + 1] - lineLocations[start];
        for (int i = start + 2; i < end; i++)
        {
            double difference = lineLocations[i] - lineLocations[i - 1];
            maximumSecondDifference = Math.Max(maximumSecondDifference, difference - previousDifference);
            previousDifference = difference;
        }

        return maximumSecondDifference > 4.0
            ? Math.Min(confidence, 45)
            : confidence;
    }
}
