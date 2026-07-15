namespace VHSDecode.Core.HiFi;

// Modified SciPy peak-finding adaptation; see THIRD-PARTY-NOTICES.md.
internal readonly record struct SciPyPeak(
    int Index,
    double Prominence,
    int LeftBase,
    int RightBase,
    double Width,
    double LeftIntersection,
    double RightIntersection);

internal static class SciPyPeakFinder
{
    internal static List<SciPyPeak> Find(
        ReadOnlySpan<float> signal,
        double? minimumThreshold,
        double? distance,
        double? minimumProminence,
        double? minimumWidth)
    {
        float[] values = signal.ToArray();
        List<int> peaks = FindLocalMaxima(values);
        if (minimumThreshold.HasValue)
        {
            peaks = peaks
                .Where(peak => minimumThreshold.Value <= Math.Min(
                    (double)values[peak] - values[peak - 1],
                    (double)values[peak] - values[peak + 1]))
                .ToList();
        }

        if (distance.HasValue)
        {
            peaks = SelectByDistance(values, peaks, distance.Value);
        }

        var properties = new List<SciPyPeak>(peaks.Count);
        foreach (int peak in peaks)
        {
            (double prominence, int leftBase, int rightBase) =
                CalculateProminence(values, peak);
            if (minimumProminence.HasValue && prominence < minimumProminence.Value)
            {
                continue;
            }

            (double width, double leftIntersection, double rightIntersection) =
                CalculateWidth(values, peak, prominence, leftBase, rightBase);
            if (minimumWidth.HasValue && width < minimumWidth.Value)
            {
                continue;
            }

            properties.Add(new SciPyPeak(
                peak,
                prominence,
                leftBase,
                rightBase,
                width,
                leftIntersection,
                rightIntersection));
        }

        return properties;
    }

    internal static List<int> FindLocalMaxima(ReadOnlySpan<float> signal)
    {
        var peaks = new List<int>(signal.Length / 2);
        int i = 1;
        int maximumIndex = signal.Length - 1;
        while (i < maximumIndex)
        {
            if (signal[i - 1] < signal[i])
            {
                int ahead = i + 1;
                while (ahead < maximumIndex && signal[ahead] == signal[i])
                {
                    ahead++;
                }

                if (signal[ahead] < signal[i])
                {
                    int rightEdge = ahead - 1;
                    peaks.Add((i + rightEdge) / 2);
                    i = ahead;
                }
            }

            i++;
        }

        return peaks;
    }

    private static List<int> SelectByDistance(
        IReadOnlyList<float> signal,
        IReadOnlyList<int> peaks,
        double distance)
    {
        int minimumDistance = checked((int)Math.Ceiling(distance));
        var priorities = new double[peaks.Count];
        for (int i = 0; i < priorities.Length; i++)
        {
            priorities[i] = signal[peaks[i]];
        }

        int[] order = NumpyAvx2ArgSort.SortIndices(priorities);
        var keep = Enumerable.Repeat(true, peaks.Count).ToArray();
        for (int orderIndex = order.Length - 1; orderIndex >= 0; orderIndex--)
        {
            int position = order[orderIndex];
            if (!keep[position])
            {
                continue;
            }

            int neighbor = position - 1;
            while (neighbor >= 0 && peaks[position] - peaks[neighbor] < minimumDistance)
            {
                keep[neighbor] = false;
                neighbor--;
            }

            neighbor = position + 1;
            while (neighbor < peaks.Count && peaks[neighbor] - peaks[position] < minimumDistance)
            {
                keep[neighbor] = false;
                neighbor++;
            }
        }

        var selected = new List<int>(peaks.Count);
        for (int i = 0; i < peaks.Count; i++)
        {
            if (keep[i])
            {
                selected.Add(peaks[i]);
            }
        }

        return selected;
    }

    private static (double Prominence, int LeftBase, int RightBase) CalculateProminence(
        ReadOnlySpan<float> signal,
        int peak)
    {
        double peakHeight = signal[peak];
        int leftBase = peak;
        double leftMinimum = peakHeight;
        int i = peak;
        while (i >= 0 && signal[i] <= peakHeight)
        {
            if (signal[i] < leftMinimum)
            {
                leftMinimum = signal[i];
                leftBase = i;
            }

            i--;
        }

        int rightBase = peak;
        double rightMinimum = peakHeight;
        i = peak;
        while (i < signal.Length && signal[i] <= peakHeight)
        {
            if (signal[i] < rightMinimum)
            {
                rightMinimum = signal[i];
                rightBase = i;
            }

            i++;
        }

        return (peakHeight - Math.Max(leftMinimum, rightMinimum), leftBase, rightBase);
    }

    private static (double Width, double LeftIntersection, double RightIntersection) CalculateWidth(
        ReadOnlySpan<float> signal,
        int peak,
        double prominence,
        int leftBase,
        int rightBase)
    {
        double height = signal[peak] - (prominence * 0.5);
        int i = peak;
        while (leftBase < i && height < signal[i])
        {
            i--;
        }

        double leftIntersection = i;
        if (signal[i] < height && signal[i + 1] != signal[i])
        {
            leftIntersection += (height - signal[i]) /
                ((double)signal[i + 1] - signal[i]);
        }

        i = peak;
        while (i < rightBase && height < signal[i])
        {
            i++;
        }

        double rightIntersection = i;
        if (signal[i] < height && signal[i - 1] != signal[i])
        {
            rightIntersection -= (height - signal[i]) /
                ((double)signal[i - 1] - signal[i]);
        }

        return (
            rightIntersection - leftIntersection,
            leftIntersection,
            rightIntersection);
    }
}
