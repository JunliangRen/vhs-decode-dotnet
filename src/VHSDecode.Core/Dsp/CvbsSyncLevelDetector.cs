namespace VHSDecode.Core.Dsp;

public readonly record struct CvbsSyncLevels(double SyncLevel, double BlankLevel);

public static class CvbsSyncLevelDetector
{
    public static CvbsSyncLevels? Find(ReadOnlySpan<double> syncReference, SyncAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        if (syncReference.Length <= 10)
        {
            return null;
        }

        ReadOnlySpan<double> data = syncReference[10..];
        double syncMinimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        for (int i = 0; i < data.Length; i++)
        {
            syncMinimum = Math.Min(syncMinimum, data[i]);
            maximum = Math.Max(maximum, data[i]);
        }

        double approximateSyncMaximum = syncMinimum + ((maximum - syncMinimum) / 15.0);
        bool[] onSync = new bool[data.Length];
        for (int i = 0; i < onSync.Length; i++)
        {
            onSync[i] = data[i] < approximateSyncMaximum;
        }

        int offset = 0;
        int retryAdvance = Math.Max(1, (int)analyzer.UsecToSamples(50.0));
        int porchOffset = Math.Max(0, (int)analyzer.UsecToSamples(1.5));
        while (offset <= data.Length - 10)
        {
            int searchStart = FindFirst(onSync, offset, value: true);
            if (searchStart < 0)
            {
                return null;
            }

            searchStart -= offset;
            int nextCrossRaw = FindFirst(onSync, searchStart, value: false);
            if (nextCrossRaw < 0)
            {
                return null;
            }

            int nextCross = nextCrossRaw + porchOffset;
            if (nextCross >= data.Length)
            {
                return null;
            }

            double blankLevel = data[nextCross] + offset;
            if (blankLevel > approximateSyncMaximum)
            {
                return new CvbsSyncLevels(syncMinimum, blankLevel);
            }

            offset += retryAdvance;
        }

        return null;
    }

    private static int FindFirst(bool[] values, int start, bool value)
    {
        for (int i = Math.Max(0, start); i < values.Length; i++)
        {
            if (values[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
