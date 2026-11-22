using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Tbc;

internal static partial class CvbsReceiver
{
    internal sealed record VBlankAcquisition(double EqualizingStart, double LineZero, bool IsFirstField);

    internal static VBlankAcquisition? RecoverBlanking(
        IReadOnlyList<Pulse> pulses, SyncAnalyzer analyzer, string system)
    {
        double period = analyzer.NominalLineLength;
        double half = period / 2;
        int count = analyzer.NumPulses;
        double section = count * half;
        double usec = analyzer.SampleRateMHz;
        // Measure each pulse type independently. At one separator level an
        // AC-coupled HSync can be several microseconds wider than expected.
        var equalizing = new SyncRange(1.35 * usec, 3.45 * usec);
        var vertical = new SyncRange(18 * usec, 31 * usec);
        var horizontal = new SyncRange(3.5 * usec, 13 * usec);
        double tolerance = .075 * period;
        for (int i = 0; i < pulses.Count; i++)
        {
            Pulse firstBroad = pulses[i];
            if (!vertical.Contains(firstBroad.Length)
                || CountOnGrid(firstBroad.Start, half, count, vertical) < count - 1)
            {
                continue;
            }

            double before = firstBroad.Start - section;
            double after = firstBroad.Start + section;
            if (CountOnGrid(before, half, count, equalizing) < count - 2
                || CountOnGrid(after, half, count, equalizing) < count - 2)
            {
                continue;
            }

            VBlankAcquisition? first = MatchBoundary(isFirstField: true);
            VBlankAcquisition? second = MatchBoundary(isFirstField: false);
            // Require a unique field phase from both sides of the blanking
            // interval before letting horizontal tracking take over.
            if ((first is null) != (second is null))
            {
                return first ?? second;
            }

            VBlankAcquisition? MatchBoundary(bool isFirstField)
            {
                (double beforeGap, double afterGap) = VBlankSyncResolver.ExpectedBoundaryLengths(system, isFirstField);
                double previous = before - beforeGap * period;
                double following = after + section - half + afterGap * period;
                if (CountOnGrid(previous, -period, 4, horizontal) < 3
                    || CountOnGrid(following, period, 7, horizontal) < 6
                    || Nearest(following, horizontal) is not { } firstHSync)
                {
                    return null;
                }

                double lineZero = firstHSync.Start
                    - VBlankSyncResolver.FirstHSyncLine(system, count, isFirstField) * period;
                return lineZero >= 0 ? new VBlankAcquisition(before, lineZero, isFirstField) : null;
            }
        }
        return null;

        int CountOnGrid(double start, double step, int length, SyncRange width)
        {
            int matched = 0;
            for (int k = 0; k < length; k++)
            {
                matched += Nearest(start + k * step, width).HasValue ? 1 : 0;
            }
            return matched;
        }

        Pulse? Nearest(double expected, SyncRange width)
        {
            int lo = 0;
            int hi = pulses.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (pulses[mid].Start < expected - tolerance)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            Pulse? nearest = null;
            double best = tolerance;
            for (int j = lo; j < pulses.Count && pulses[j].Start <= expected + tolerance; j++)
            {
                double distance = Math.Abs(pulses[j].Start - expected);
                if (distance <= best && width.Contains(pulses[j].Length))
                {
                    nearest = pulses[j];
                    best = distance;
                }
            }
            return nearest;
        }
    }
}
