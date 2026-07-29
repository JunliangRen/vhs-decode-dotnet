using System.Collections.Concurrent;
using System.Numerics;

namespace VHSDecode.Core.Dsp;

public readonly record struct VhsMeasuredSyncPulse(
    int Start,
    int Length,
    double Transition,
    double SyncLevel,
    double BlankLevel);

public sealed record VhsSyncDetectionResult(
    IReadOnlyList<VhsMeasuredSyncPulse> Pulses,
    double SyncTipLevel,
    double BlankLevel);

public sealed class VhsSyncDetector
{
    private const int MaximumParallelBoxcarWorkers = 4;
    private const int MinimumParallelBoxcarSamples = 65_536;
    private const double SyncSpacingTolerance = 0.15;
    private const int MinimumGridLength = 8;
    private const int PartitionSortThreshold = 32;
    private readonly double _hSyncLength;
    private readonly double _backPorchLength;
    private readonly int _lineLength;
    private readonly double _approximateTransition;
    private readonly int _workerThreads;
    private readonly ConcurrentBag<VhsSyncWorkspace> _workspaces = [];

    // Upstream: oyvindln/vhs-decode
    // Baseline: 2f21e8ed6018b14561396cc95f1f6828054470b8
    // Source: vhsdecode/field.py FieldShared.get_pulses and _get_pulses
    // Port policy: preserve float64 conversion points and per-stage operation order.
    public VhsSyncDetector(
        double hSyncLength,
        double backPorchLength,
        int lineLength,
        double approximateTransition)
        : this(
            hSyncLength,
            backPorchLength,
            lineLength,
            approximateTransition,
            workerThreads: 1)
    {
    }

    internal VhsSyncDetector(
        double hSyncLength,
        double backPorchLength,
        int lineLength,
        double approximateTransition,
        int workerThreads)
    {
        _hSyncLength = double.IsFinite(hSyncLength) && hSyncLength > 0.0
            ? hSyncLength
            : throw new ArgumentOutOfRangeException(nameof(hSyncLength));
        _backPorchLength = double.IsFinite(backPorchLength) && backPorchLength > 0.0
            ? backPorchLength
            : throw new ArgumentOutOfRangeException(nameof(backPorchLength));
        _lineLength = lineLength > 0
            ? lineLength
            : throw new ArgumentOutOfRangeException(nameof(lineLength));
        _approximateTransition = double.IsFinite(approximateTransition) && approximateTransition > 0.0
            ? approximateTransition
            : throw new ArgumentOutOfRangeException(nameof(approximateTransition));
        _workerThreads = Math.Clamp(
            workerThreads,
            1,
            MaximumParallelBoxcarWorkers);
    }

    internal VhsSyncDetectionResult Detect(
        double[] demodulated,
        bool detectLevels,
        double syncTipEstimate,
        double blankingEstimate)
    {
        ArgumentNullException.ThrowIfNull(demodulated);
        if (_workerThreads == 1
            || demodulated.Length < MinimumParallelBoxcarSamples)
        {
            return Detect(
                demodulated.AsSpan(),
                detectLevels,
                syncTipEstimate,
                blankingEstimate);
        }

        int windowSize = Math.Max(3, (int)_approximateTransition);
        if ((windowSize & 1) == 0)
        {
            windowSize++;
        }

        VhsSyncWorkspace workspace =
            _workspaces.TryTake(out VhsSyncWorkspace? available)
                ? available
                : new VhsSyncWorkspace();
        try
        {
            int filteredLength = Math.Max(
                demodulated.Length,
                windowSize);
            double[] filtered =
                workspace.EnsureFilteredLength(filteredLength);
            ConvolveBoxcarSameParallel(
                demodulated,
                windowSize,
                filtered,
                filteredLength,
                _workerThreads);
            if (detectLevels)
            {
                double[] partitioned =
                    workspace.EnsurePartitionedLength(filteredLength);
                (syncTipEstimate, blankingEstimate) =
                    EstimateLevels(
                        filtered.AsSpan(0, filteredLength),
                        partitioned);
            }

            return DetectFiltered(
                filtered.AsSpan(0, filteredLength),
                syncTipEstimate,
                blankingEstimate);
        }
        finally
        {
            _workspaces.Add(workspace);
        }
    }

    public VhsSyncDetectionResult Detect(
        ReadOnlySpan<double> demodulated,
        bool detectLevels,
        double syncTipEstimate,
        double blankingEstimate)
    {
        if (demodulated.IsEmpty)
        {
            return new VhsSyncDetectionResult([], syncTipEstimate, blankingEstimate);
        }

        int windowSize = Math.Max(3, (int)_approximateTransition);
        if ((windowSize & 1) == 0)
        {
            windowSize++;
        }

        VhsSyncWorkspace workspace = _workspaces.TryTake(out VhsSyncWorkspace? available)
            ? available
            : new VhsSyncWorkspace();
        try
        {
            int filteredLength = Math.Max(demodulated.Length, windowSize);
            double[] filtered = workspace.EnsureFilteredLength(filteredLength);
            ConvolveBoxcarSame(
                demodulated,
                windowSize,
                filtered.AsSpan(0, filteredLength));
            if (detectLevels)
            {
                double[] partitioned = workspace.EnsurePartitionedLength(filteredLength);
                (syncTipEstimate, blankingEstimate) =
                    EstimateLevels(
                        filtered.AsSpan(0, filteredLength),
                        partitioned);
            }

            return DetectFiltered(
                filtered.AsSpan(0, filteredLength),
                syncTipEstimate,
                blankingEstimate);
        }
        finally
        {
            _workspaces.Add(workspace);
        }
    }

    private static void ConvolveBoxcarSameParallel(
        double[] values,
        int windowSize,
        double[] output,
        int outputLength,
        int workerThreads)
    {
        int workerCount = Math.Min(workerThreads, outputLength);
        Parallel.For(
            fromInclusive: 0,
            toExclusive: workerCount,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount
            },
            worker =>
            {
                int start = (int)(
                    ((long)outputLength * worker)
                    / workerCount);
                int end = (int)(
                    ((long)outputLength * (worker + 1))
                    / workerCount);
                ConvolveBoxcarRange(
                    values,
                    windowSize,
                    output,
                    start,
                    end);
            });
    }

    private static void ConvolveBoxcarRange(
        double[] values,
        int windowSize,
        double[] output,
        int start,
        int end)
    {
        int firstFullIndex =
            (Math.Min(values.Length, windowSize) - 1) / 2;
        double scale = 1.0 / windowSize;
        for (int outputIndex = start;
            outputIndex < end;
            outputIndex++)
        {
            int fullIndex = firstFullIndex + outputIndex;
            int sourceStart = Math.Max(
                0,
                fullIndex - (windowSize - 1));
            int sourceEnd = Math.Min(values.Length - 1, fullIndex);
            double sum = 0.0;
            for (int sourceIndex = sourceStart;
                sourceIndex <= sourceEnd;
                sourceIndex++)
            {
                sum += values[sourceIndex] * scale;
            }

            output[outputIndex] = sum;
        }
    }

    internal static double[] ConvolveBoxcarSame(ReadOnlySpan<double> values, int windowSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        if ((windowSize & 1) == 0)
        {
            throw new ArgumentException("The boxcar window must have odd length.", nameof(windowSize));
        }

        if (values.IsEmpty)
        {
            return [];
        }

        var output = new double[Math.Max(values.Length, windowSize)];
        ConvolveBoxcarSame(values, windowSize, output);
        return output;
    }

    private static void ConvolveBoxcarSame(
        ReadOnlySpan<double> values,
        int windowSize,
        Span<double> output)
    {
        int outputLength = Math.Max(values.Length, windowSize);
        if (output.Length != outputLength)
        {
            throw new ArgumentException(
                "The output length must match NumPy same-mode convolution.",
                nameof(output));
        }

        int firstFullIndex = (Math.Min(values.Length, windowSize) - 1) / 2;
        double scale = 1.0 / windowSize;
        for (int outputIndex = 0; outputIndex < outputLength; outputIndex++)
        {
            int fullIndex = firstFullIndex + outputIndex;
            int sourceStart = Math.Max(0, fullIndex - (windowSize - 1));
            int sourceEnd = Math.Min(values.Length - 1, fullIndex);
            double sum = 0.0;
            for (int sourceIndex = sourceStart; sourceIndex <= sourceEnd; sourceIndex++)
            {
                sum += values[sourceIndex] * scale;
            }

            output[outputIndex] = sum;
        }
    }

    private VhsSyncDetectionResult DetectFiltered(
        ReadOnlySpan<double> filtered,
        double syncTipEstimate,
        double blankingEstimate)
    {
        int sampleCount = filtered.Length;
        double slicerLevelEstimate = (syncTipEstimate + blankingEstimate) / 2.0;
        int candidateStride = Math.Max(10, _lineLength / 2);
        int initialCapacity = Math.Max(4, sampleCount / candidateStride);
        var hSyncFalls = new List<int>(initialCapacity);
        var hSyncRises = new List<int>(initialCapacity);
        double minimumWidth = _hSyncLength * 0.6;
        double maximumWidth = _hSyncLength * 1.4;
        int fallingIndex = -1;
        for (int index = 0; index < sampleCount - 1; index++)
        {
            if (filtered[index] >= slicerLevelEstimate
                && filtered[index + 1] < slicerLevelEstimate)
            {
                fallingIndex = index;
            }
            else if (fallingIndex != -1
                     && filtered[index] < slicerLevelEstimate
                     && filtered[index + 1] >= slicerLevelEstimate)
            {
                int width = index - fallingIndex;
                if (minimumWidth < width && width < maximumWidth)
                {
                    hSyncFalls.Add(fallingIndex);
                    hSyncRises.Add(index);
                }

                fallingIndex = -1;
            }
        }

        if (hSyncFalls.Count == 0)
        {
            return new VhsSyncDetectionResult([], syncTipEstimate, blankingEstimate);
        }

        int candidateCount = hSyncFalls.Count;
        int[] falls = hSyncFalls.ToArray();
        int[] rises = hSyncRises.ToArray();
        var candidateSyncLevels = new double[candidateCount];
        var candidatePorchLevels = new double[candidateCount];
        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            int middle = (falls[candidate] + rises[candidate]) / 2;
            candidateSyncLevels[candidate] = UpperMedianOfWindow(
                filtered,
                Math.Max(0, middle - 2),
                Math.Min(sampleCount, middle + 3),
                syncTipEstimate);

            int porchCenter = (int)(rises[candidate] + (_backPorchLength * 0.5));
            candidatePorchLevels[candidate] = UpperMedianOfWindow(
                filtered,
                Math.Max(0, porchCenter - 2),
                Math.Min(sampleCount, porchCenter + 3),
                blankingEstimate);
        }

        double[] sortedSync = candidateSyncLevels.ToArray();
        Array.Sort(sortedSync, NumpyDoubleComparer.Instance);
        double medianSync = sortedSync[candidateCount / 2];
        var absoluteDeviations = new double[candidateCount];
        for (int index = 0; index < absoluteDeviations.Length; index++)
        {
            absoluteDeviations[index] = Math.Abs(candidateSyncLevels[index] - medianSync);
        }

        Array.Sort(absoluteDeviations, NumpyDoubleComparer.Instance);
        double medianAbsoluteDeviation = absoluteDeviations[candidateCount / 2];
        if (!(medianAbsoluteDeviation > 0.0))
        {
            medianAbsoluteDeviation = 1.0;
        }

        int amplitudeCount = 0;
        for (int index = 0; index < candidateCount; index++)
        {
            if (Math.Abs(candidateSyncLevels[index] - medianSync)
                <= 2.5 * medianAbsoluteDeviation)
            {
                falls[amplitudeCount] = falls[index];
                rises[amplitudeCount] = rises[index];
                candidateSyncLevels[amplitudeCount] = candidateSyncLevels[index];
                candidatePorchLevels[amplitudeCount] = candidatePorchLevels[index];
                amplitudeCount++;
            }
        }

        if (amplitudeCount == 0)
        {
            return new VhsSyncDetectionResult([], syncTipEstimate, blankingEstimate);
        }

        double effectiveLineLength = _lineLength * (1.0 + SyncSpacingTolerance);
        double jitterTolerance = _lineLength * 0.1;
        var gridSupportCount = new int[amplitudeCount];
        FillOrderedGridSupportCounts(
            falls,
            amplitudeCount,
            effectiveLineLength,
            jitterTolerance,
            gridSupportCount);

        var finalMask = new bool[amplitudeCount];
        int hSyncFitCount = 0;
        for (int index = 0; index < amplitudeCount; index++)
        {
            if (gridSupportCount[index] >= MinimumGridLength)
            {
                finalMask[index] = true;
                hSyncFitCount++;
            }
        }

        double syncTipLevel = syncTipEstimate;
        double backPorchLevel = blankingEstimate;
        if (hSyncFitCount > 0)
        {
            (double syncSum, double porchSum) = SumSelectedLevelsInUpstreamOrder(
                candidateSyncLevels,
                candidatePorchLevels,
                finalMask,
                amplitudeCount);
            double reciprocalFitCount = 1.0 / hSyncFitCount;
            syncTipLevel = syncSum * reciprocalFitCount;
            backPorchLevel = porchSum * reciprocalFitCount;
        }
        else
        {
            return new VhsSyncDetectionResult([], syncTipLevel, backPorchLevel);
        }

        double preciseMidpoint = (syncTipLevel + backPorchLevel) / 2.0;
        var fallingEdges = new List<int>(initialCapacity);
        var risingEdges = new List<int>(initialCapacity);
        fallingIndex = -1;
        for (int index = 0; index < sampleCount - 1; index++)
        {
            if (filtered[index] >= preciseMidpoint
                && filtered[index + 1] < preciseMidpoint)
            {
                fallingIndex = index;
            }
            else if (fallingIndex != -1
                     && filtered[index] < preciseMidpoint
                     && filtered[index + 1] >= preciseMidpoint)
            {
                bool belongsToValidGrid = false;
                for (int candidate = 0; candidate < amplitudeCount; candidate++)
                {
                    if (!finalMask[candidate])
                    {
                        continue;
                    }

                    int delta = Math.Abs(fallingIndex - falls[candidate]);
                    double remainder = delta % effectiveLineLength;
                    if (remainder < jitterTolerance
                        || remainder > effectiveLineLength - jitterTolerance)
                    {
                        belongsToValidGrid = true;
                        break;
                    }
                }

                if (belongsToValidGrid)
                {
                    fallingEdges.Add(fallingIndex);
                    risingEdges.Add(index);
                }

                fallingIndex = -1;
            }
        }

        if (fallingEdges.Count == 0)
        {
            return new VhsSyncDetectionResult([], syncTipLevel, backPorchLevel);
        }

        var slopes = new double[fallingEdges.Count];
        int slopeCount = 0;
        for (int index = 0; index < fallingEdges.Count; index++)
        {
            int rise = risingEdges[index];
            if (10 < rise && rise < sampleCount - 10)
            {
                slopes[slopeCount++] = Math.Abs(filtered[rise + 1] - filtered[rise - 1]);
            }
        }

        double transition;
        if (slopeCount > 0)
        {
            Array.Sort(slopes, 0, slopeCount, NumpyDoubleComparer.Instance);
            double fitSharpness = Math.Max(
                0.1,
                slopes[slopeCount / 2] / Math.Max(1e-5, backPorchLevel - syncTipLevel));
            transition = 1.0 / fitSharpness;
        }
        else
        {
            transition = _approximateTransition;
        }

        var pulses = new VhsMeasuredSyncPulse[fallingEdges.Count];
        int pulseCount = 0;
        for (int index = 0; index < fallingEdges.Count; index++)
        {
            int fall = fallingEdges[index];
            int rise = risingEdges[index];
            double fallingFirst = filtered[fall] - preciseMidpoint;
            double fallingSecond = filtered[fall + 1] - preciseMidpoint;
            double fallingDifference = fallingFirst - fallingSecond;
            double subpixelFall = fall + (fallingDifference != 0.0
                ? fallingFirst / fallingDifference
                : 0.0);

            double risingFirst = filtered[rise] - preciseMidpoint;
            double risingSecond = filtered[rise + 1] - preciseMidpoint;
            double risingDifference = risingSecond - risingFirst;
            double subpixelRise = rise + (risingDifference != 0.0
                ? Math.Abs(risingFirst) / risingDifference
                : 0.0);

            double calculatedLength = subpixelRise - subpixelFall;
            if (calculatedLength <= 0.0)
            {
                continue;
            }

            int syncIndex = (int)(subpixelFall + (calculatedLength * 0.5));
            int porchIndex = (int)(subpixelRise + (_backPorchLength * 0.5));
            double pulseSyncLevel = syncIndex >= 0 && syncIndex < sampleCount
                ? filtered[syncIndex]
                : syncTipLevel;
            double pulseBlankLevel = porchIndex >= 0 && porchIndex < sampleCount
                ? filtered[porchIndex]
                : backPorchLevel;
            pulses[pulseCount++] = new VhsMeasuredSyncPulse(
                checked((int)Math.Round(subpixelFall, MidpointRounding.ToEven)),
                checked((int)Math.Round(calculatedLength, MidpointRounding.ToEven)),
                transition * 2.0,
                pulseSyncLevel,
                pulseBlankLevel);
        }

        return new VhsSyncDetectionResult(
            pulseCount == pulses.Length ? pulses : pulses[..pulseCount],
            syncTipLevel,
            backPorchLevel);
    }

    internal static void FillOrderedGridSupportCounts(
        ReadOnlySpan<int> falls,
        int count,
        double effectiveLineLength,
        double jitterTolerance,
        Span<int> supportCounts)
    {
        if ((uint)count > (uint)falls.Length
            || (uint)count > (uint)supportCounts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Span<int> counts = supportCounts[..count];
        counts.Fill(1);
        double upperTolerance = effectiveLineLength - jitterTolerance;
        for (int first = 0; first < count - 1; first++)
        {
            for (int second = first + 1; second < count; second++)
            {
                int delta = falls[second] - falls[first];
                double remainder = delta % effectiveLineLength;
                if (remainder < jitterTolerance
                    || remainder > upperTolerance)
                {
                    counts[first]++;
                    counts[second]++;
                }
            }
        }
    }

    internal static (double SyncSum, double PorchSum) SumSelectedLevelsInUpstreamOrder(
        ReadOnlySpan<double> syncLevels,
        ReadOnlySpan<double> porchLevels,
        ReadOnlySpan<bool> selected,
        int count)
    {
        if ((uint)count > (uint)syncLevels.Length
            || (uint)count > (uint)porchLevels.Length
            || (uint)count > (uint)selected.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        // Numba's fastmath loop in the pinned upstream baseline is reduced as
        // two four-lane accumulators over groups of eight, followed by
        // (lane0 + lane2) + (lane1 + lane3). Keep that order explicit.
        Span<double> firstSync = stackalloc double[4];
        Span<double> secondSync = stackalloc double[4];
        Span<double> firstPorch = stackalloc double[4];
        Span<double> secondPorch = stackalloc double[4];
        firstSync.Clear();
        secondSync.Clear();
        firstPorch.Clear();
        secondPorch.Clear();
        int vectorizedLength = count & ~7;
        for (int index = 0; index < vectorizedLength; index += 8)
        {
            for (int lane = 0; lane < 4; lane++)
            {
                int firstIndex = index + lane;
                if (selected[firstIndex])
                {
                    firstSync[lane] += syncLevels[firstIndex];
                    firstPorch[lane] += porchLevels[firstIndex];
                }

                int secondIndex = firstIndex + 4;
                if (selected[secondIndex])
                {
                    secondSync[lane] += syncLevels[secondIndex];
                    secondPorch[lane] += porchLevels[secondIndex];
                }
            }
        }

        double syncLane0 = secondSync[0] + firstSync[0];
        double syncLane1 = secondSync[1] + firstSync[1];
        double syncLane2 = secondSync[2] + firstSync[2];
        double syncLane3 = secondSync[3] + firstSync[3];
        double syncSum = (syncLane0 + syncLane2) + (syncLane1 + syncLane3);

        double porchLane0 = secondPorch[0] + firstPorch[0];
        double porchLane1 = secondPorch[1] + firstPorch[1];
        double porchLane2 = secondPorch[2] + firstPorch[2];
        double porchLane3 = secondPorch[3] + firstPorch[3];
        double porchSum = (porchLane0 + porchLane2) + (porchLane1 + porchLane3);

        for (int index = vectorizedLength; index < count; index++)
        {
            if (selected[index])
            {
                syncSum = syncLevels[index] + syncSum;
                porchSum = porchLevels[index] + porchSum;
            }
        }

        return (syncSum, porchSum);
    }

    private static (double SyncTip, double Blanking) EstimateLevels(
        ReadOnlySpan<double> filtered,
        double[] partitioned)
    {
        if (partitioned.Length < filtered.Length)
        {
            throw new ArgumentException(
                "The partition workspace must be at least the filtered length.",
                nameof(partitioned));
        }

        int syncIndex = (int)(filtered.Length * 0.05);
        int blankingIndex = (int)(filtered.Length * 0.25);
        filtered.CopyTo(partitioned);
        double syncTip = SelectKth(partitioned, syncIndex, filtered.Length);
        double blanking = SelectKth(partitioned, blankingIndex, filtered.Length);
        return (syncTip, blanking);
    }

    private static double UpperMedianOfWindow(
        ReadOnlySpan<double> values,
        int start,
        int end,
        double fallback)
    {
        int length = end - start;
        if (length <= 0)
        {
            return fallback;
        }

        Span<double> window = stackalloc double[5];
        values[start..end].CopyTo(window);
        for (int index = 1; index < length; index++)
        {
            double value = window[index];
            int insertion = index - 1;
            while (insertion >= 0
                   && NumpyDoubleComparer.Instance.Compare(window[insertion], value) > 0)
            {
                window[insertion + 1] = window[insertion];
                insertion--;
            }

            window[insertion + 1] = value;
        }

        return window[length / 2];
    }

    private static double SelectKth(double[] values, int target, int count)
    {
        int left = 0;
        int right = count - 1;
        int depthLimit = 2 * (BitOperations.Log2((uint)count) + 1);
        while (left < right)
        {
            int length = right - left + 1;
            if (length <= PartitionSortThreshold || depthLimit-- == 0)
            {
                Array.Sort(values, left, length, NumpyDoubleComparer.Instance);
                return values[target];
            }

            double pivot = MedianOfThree(
                values[left],
                values[left + (length / 2)],
                values[right]);
            int lower = left;
            int index = left;
            int upper = right;
            while (index <= upper)
            {
                int comparison = NumpyDoubleComparer.Instance.Compare(values[index], pivot);
                if (comparison < 0)
                {
                    (values[lower], values[index]) = (values[index], values[lower]);
                    lower++;
                    index++;
                }
                else if (comparison > 0)
                {
                    (values[index], values[upper]) = (values[upper], values[index]);
                    upper--;
                }
                else
                {
                    index++;
                }
            }

            if (target < lower)
            {
                right = lower - 1;
            }
            else if (target > upper)
            {
                left = upper + 1;
            }
            else
            {
                return values[target];
            }
        }

        return values[target];
    }

    private static double MedianOfThree(double first, double second, double third)
    {
        if (NumpyDoubleComparer.Instance.Compare(first, second) > 0)
        {
            (first, second) = (second, first);
        }

        if (NumpyDoubleComparer.Instance.Compare(second, third) > 0)
        {
            (second, third) = (third, second);
        }

        if (NumpyDoubleComparer.Instance.Compare(first, second) > 0)
        {
            (first, second) = (second, first);
        }

        return second;
    }

    private sealed class NumpyDoubleComparer : IComparer<double>
    {
        public static NumpyDoubleComparer Instance { get; } = new();

        public int Compare(double first, double second)
        {
            bool firstNaN = double.IsNaN(first);
            bool secondNaN = double.IsNaN(second);
            if (firstNaN)
            {
                return secondNaN ? 0 : 1;
            }

            return secondNaN ? -1 : first.CompareTo(second);
        }
    }

    private sealed class VhsSyncWorkspace
    {
        private double[] _filtered = [];
        private double[] _partitioned = [];

        public double[] EnsureFilteredLength(int length)
        {
            if (_filtered.Length < length)
            {
                _filtered = GC.AllocateUninitializedArray<double>(length);
            }

            return _filtered;
        }

        public double[] EnsurePartitionedLength(int length)
        {
            if (_partitioned.Length < length)
            {
                _partitioned = GC.AllocateUninitializedArray<double>(length);
            }

            return _partitioned;
        }
    }
}
