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
    private const int MaximumBufferedThresholdCrossingsPerWorker = 16_384;
    private const int MinimumParallelBoxcarSamples = 65_536;
    private const int MinimumParallelEdgeScanSamples = 65_536;
    private const int MinimumParallelRadixSamples = 524_288;
    private const double SyncSpacingTolerance = 0.15;
    private const int MinimumGridLength = 8;
    private const int PartitionSortThreshold = 32;
    internal const int RadixHistogramWidth = 1 << 16;
    private readonly double _hSyncLength;
    private readonly double _backPorchLength;
    private readonly int _lineLength;
    private readonly double _approximateTransition;
    private readonly int _workerThreads;
    private readonly bool _parallelizePreciseEdgeScan;
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
        int workerThreads,
        bool parallelizePreciseEdgeScan = true)
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
        _parallelizePreciseEdgeScan = parallelizePreciseEdgeScan;
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
                (syncTipEstimate, blankingEstimate) =
                    EstimateLevelsParallel(
                        filtered,
                        filteredLength,
                        workspace,
                        _workerThreads);
            }

            return DetectFiltered(
                filtered.AsSpan(0, filteredLength),
                syncTipEstimate,
                blankingEstimate,
                filtered,
                _workerThreads,
                workspace);
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
                (syncTipEstimate, blankingEstimate) =
                    EstimateLevels(
                        filtered.AsSpan(0, filteredLength),
                        workspace);
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

    internal static void ConvolveBoxcarSameParallel(
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
        if (windowSize == 9 && values.Length >= 9)
        {
            ConvolveBoxcarRange9(values, output, start, end);
            return;
        }

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

    private static unsafe void ConvolveBoxcarRange9(
        double[] values,
        double[] output,
        int start,
        int end)
    {
        const int HalfWindow = 4;
        const double Scale = 1.0 / 9.0;
        int interiorStart = Math.Max(start, HalfWindow);
        int interiorEnd = Math.Min(end, values.Length - HalfWindow);
        ConvolveBoxcarRangeEdge9(values, output, start, Math.Min(end, interiorStart));

        fixed (double* valuesPointer = values)
        fixed (double* outputPointer = output)
        {
            for (int outputIndex = interiorStart; outputIndex < interiorEnd; outputIndex++)
            {
                double* source = valuesPointer + outputIndex - HalfWindow;
                double sum = 0.0;
                sum += source[0] * Scale;
                sum += source[1] * Scale;
                sum += source[2] * Scale;
                sum += source[3] * Scale;
                sum += source[4] * Scale;
                sum += source[5] * Scale;
                sum += source[6] * Scale;
                sum += source[7] * Scale;
                sum += source[8] * Scale;
                outputPointer[outputIndex] = sum;
            }
        }

        ConvolveBoxcarRangeEdge9(values, output, Math.Max(start, interiorEnd), end);
    }

    private static void ConvolveBoxcarRangeEdge9(
        double[] values,
        double[] output,
        int start,
        int end)
    {
        const int HalfWindow = 4;
        const double Scale = 1.0 / 9.0;
        for (int outputIndex = start; outputIndex < end; outputIndex++)
        {
            int sourceStart = Math.Max(0, outputIndex - HalfWindow);
            int sourceEnd = Math.Min(values.Length - 1, outputIndex + HalfWindow);
            double sum = 0.0;
            for (int sourceIndex = sourceStart; sourceIndex <= sourceEnd; sourceIndex++)
            {
                sum += values[sourceIndex] * Scale;
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
        double blankingEstimate,
        double[]? parallelFiltered = null,
        int parallelWorkerThreads = 1,
        VhsSyncWorkspace? parallelWorkspace = null)
    {
        int sampleCount = filtered.Length;
        double slicerLevelEstimate = (syncTipEstimate + blankingEstimate) / 2.0;
        int candidateStride = Math.Max(10, _lineLength / 2);
        int initialCapacity = Math.Max(4, sampleCount / candidateStride);
        double minimumWidth = _hSyncLength * 0.6;
        double maximumWidth = _hSyncLength * 1.4;
        int fallingIndex = -1;
        int[] falls;
        int[] rises;
        if (parallelFiltered is not null
            && parallelWorkerThreads > 1
            && sampleCount >= MinimumParallelEdgeScanSamples)
        {
            (falls, rises) = FindInitialEdgesParallel(
                parallelFiltered,
                sampleCount,
                slicerLevelEstimate,
                minimumWidth,
                maximumWidth,
                parallelWorkerThreads,
                initialCapacity);
        }
        else
        {
            var hSyncFalls = new List<int>(initialCapacity);
            var hSyncRises = new List<int>(initialCapacity);
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

            falls = hSyncFalls.ToArray();
            rises = hSyncRises.ToArray();
        }

        if (falls.Length == 0)
        {
            return new VhsSyncDetectionResult([], syncTipEstimate, blankingEstimate);
        }

        int candidateCount = falls.Length;
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
        bool preciseScanCompleted = false;
        if (_parallelizePreciseEdgeScan
            && parallelFiltered is not null
            && parallelWorkerThreads > 1
            && sampleCount >= MinimumParallelEdgeScanSamples)
        {
            (List<int>[] crossingsByWorker, int workerCount, bool overflowed) =
                FindThresholdCrossingsParallel(
                    parallelFiltered,
                    sampleCount,
                    preciseMidpoint,
                    parallelWorkerThreads,
                    initialCapacity,
                    parallelWorkspace!);
            if (!overflowed)
            {
                fallingIndex = -1;
                for (int worker = 0; worker < workerCount; worker++)
                {
                    List<int> crossings = crossingsByWorker[worker];
                    for (int index = 0; index < crossings.Count; index++)
                    {
                        int crossing = crossings[index];
                        if (crossing >= 0)
                        {
                            fallingIndex = crossing;
                        }
                        else if (fallingIndex != -1)
                        {
                            if (IsOnValidGrid(
                                fallingIndex,
                                falls,
                                finalMask,
                                amplitudeCount,
                                effectiveLineLength,
                                jitterTolerance))
                            {
                                fallingEdges.Add(fallingIndex);
                                risingEdges.Add(~crossing);
                            }

                            fallingIndex = -1;
                        }
                    }
                }

                preciseScanCompleted = true;
            }
        }

        if (!preciseScanCompleted)
        {
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

    private static (int[] Falls, int[] Rises) FindInitialEdgesParallel(
        double[] filtered,
        int sampleCount,
        double slicerLevel,
        double minimumWidth,
        double maximumWidth,
        int workerThreads,
        int initialCapacity)
    {
        int scanLimit = sampleCount - 1;
        int workerCount = Math.Min(workerThreads, scanLimit);
        int overlap = maximumWidth >= scanLimit - 2.0
            ? scanLimit
            : (int)Math.Ceiling(maximumWidth) + 2;
        var fallsByWorker = new List<int>[workerCount];
        var risesByWorker = new List<int>[workerCount];
        Parallel.For(
            0,
            workerCount,
            new ParallelOptions { MaxDegreeOfParallelism = workerCount },
            worker =>
            {
                int coreStart = (int)(((long)scanLimit * worker) / workerCount);
                int coreEnd = (int)(((long)scanLimit * (worker + 1)) / workerCount);
                int scanStart = Math.Max(0, coreStart - overlap);
                int scanEnd = (int)Math.Min(scanLimit, (long)coreEnd + overlap);
                int partitionCapacity = Math.Max(4, (initialCapacity / workerCount) + 2);
                var localFalls = new List<int>(partitionCapacity);
                var localRises = new List<int>(partitionCapacity);
                int fallingIndex = -1;
                for (int index = scanStart; index < scanEnd; index++)
                {
                    if (filtered[index] >= slicerLevel
                        && filtered[index + 1] < slicerLevel)
                    {
                        fallingIndex = index;
                    }
                    else if (fallingIndex != -1
                             && filtered[index] < slicerLevel
                             && filtered[index + 1] >= slicerLevel)
                    {
                        int width = index - fallingIndex;
                        if (fallingIndex >= coreStart
                            && fallingIndex < coreEnd
                            && minimumWidth < width
                            && width < maximumWidth)
                        {
                            localFalls.Add(fallingIndex);
                            localRises.Add(index);
                        }

                        fallingIndex = -1;
                    }
                }

                fallsByWorker[worker] = localFalls;
                risesByWorker[worker] = localRises;
            });

        int candidateCount = 0;
        for (int worker = 0; worker < workerCount; worker++)
        {
            candidateCount = checked(candidateCount + fallsByWorker[worker].Count);
        }

        var falls = new int[candidateCount];
        var rises = new int[candidateCount];
        int destination = 0;
        for (int worker = 0; worker < workerCount; worker++)
        {
            List<int> localFalls = fallsByWorker[worker];
            List<int> localRises = risesByWorker[worker];
            localFalls.CopyTo(falls, destination);
            localRises.CopyTo(rises, destination);
            destination += localFalls.Count;
        }

        return (falls, rises);
    }

    private static (List<int>[] CrossingsByWorker, int WorkerCount, bool Overflowed)
        FindThresholdCrossingsParallel(
        double[] filtered,
        int sampleCount,
        double threshold,
        int workerThreads,
        int initialCapacity,
        VhsSyncWorkspace workspace)
    {
        int scanLimit = sampleCount - 1;
        int workerCount = Math.Min(workerThreads, scanLimit);
        int partitionCapacity = Math.Min(
            MaximumBufferedThresholdCrossingsPerWorker,
            Math.Max(
                8,
                checked((int)((((long)initialCapacity * 2) / workerCount) + 2))));
        List<int>[] crossingsByWorker = workspace.PrepareThresholdCrossingLists(
            workerCount,
            partitionCapacity);
        int[] overflowFlags = workspace.PrepareThresholdCrossingOverflowFlags(workerCount);
        Parallel.For(
            0,
            workerCount,
            new ParallelOptions { MaxDegreeOfParallelism = workerCount },
            worker =>
            {
                int start = (int)(((long)scanLimit * worker) / workerCount);
                int end = (int)(((long)scanLimit * (worker + 1)) / workerCount);
                List<int> crossings = crossingsByWorker[worker];
                if (!TryFillThresholdCrossingsPartition(
                    filtered,
                    start,
                    end,
                    threshold,
                    MaximumBufferedThresholdCrossingsPerWorker,
                    crossings))
                {
                    overflowFlags[worker] = 1;
                }
            });

        bool overflowed = Array.IndexOf(overflowFlags, 1, 0, workerCount) >= 0;
        return (crossingsByWorker, workerCount, overflowed);
    }

    internal static bool TryFillThresholdCrossingsPartition(
        double[] filtered,
        int start,
        int end,
        double threshold,
        int maximumCrossings,
        List<int> crossings)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCrossings);
        for (int index = start; index < end; index++)
        {
            int crossing;
            if (filtered[index] >= threshold
                && filtered[index + 1] < threshold)
            {
                crossing = index;
            }
            else if (filtered[index] < threshold
                     && filtered[index + 1] >= threshold)
            {
                // Complements keep rising index zero distinct in the shared event list.
                crossing = ~index;
            }
            else
            {
                continue;
            }

            if (crossings.Count >= maximumCrossings)
            {
                return false;
            }

            crossings.Add(crossing);
        }

        return true;
    }

    private static bool IsOnValidGrid(
        int fallingIndex,
        int[] falls,
        bool[] finalMask,
        int amplitudeCount,
        double effectiveLineLength,
        double jitterTolerance)
    {
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
                return true;
            }
        }

        return false;
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
        VhsSyncWorkspace workspace)
    {
        int syncIndex = (int)(filtered.Length * 0.05);
        int blankingIndex = (int)(filtered.Length * 0.25);
        return SelectLevelQuantilesRadix(
            filtered,
            workspace.EnsurePartitionedLength(filtered.Length),
            workspace.EnsureHighHistogram(),
            workspace.EnsureMiddleHistograms(),
            syncIndex,
            blankingIndex);
    }

    private static (double SyncTip, double Blanking) EstimateLevelsParallel(
        double[] filtered,
        int filteredLength,
        VhsSyncWorkspace workspace,
        int workerThreads)
    {
        if (workerThreads <= 1
            || filteredLength < MinimumParallelRadixSamples)
        {
            return EstimateLevels(
                filtered.AsSpan(0, filteredLength),
                workspace);
        }

        int syncIndex = (int)(filteredLength * 0.05);
        int blankingIndex = (int)(filteredLength * 0.25);
        return SelectLevelQuantilesRadixParallel(
            filtered,
            filteredLength,
            workspace.EnsurePartitionedLength(filteredLength),
            workspace.EnsureHighHistogram(),
            workspace.EnsureMiddleHistograms(),
            workspace.EnsureWorkerHistograms(workerThreads),
            workspace.EnsureWorkerFlags(workerThreads),
            syncIndex,
            blankingIndex,
            workerThreads);
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

    internal static (double SyncTip, double Blanking) SelectLevelQuantilesRadix(
        ReadOnlySpan<double> values,
        double[] scratch,
        int[] highHistogram,
        int[] middleHistograms,
        int syncTarget,
        int blankingTarget)
        => SelectLevelQuantilesRadixCore(
            values,
            parallelValues: null,
            parallelValueCount: 0,
            scratch,
            highHistogram,
            middleHistograms,
            workerHistograms: null,
            workerFlags: null,
            syncTarget,
            blankingTarget,
            workerThreads: 1);

    internal static (double SyncTip, double Blanking) SelectLevelQuantilesRadixParallel(
        double[] values,
        int valueCount,
        double[] scratch,
        int[] highHistogram,
        int[] middleHistograms,
        int[] workerHistograms,
        int[] workerFlags,
        int syncTarget,
        int blankingTarget,
        int workerThreads)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueCount);
        if (valueCount > values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(valueCount));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerThreads);
        return SelectLevelQuantilesRadixCore(
            values.AsSpan(0, valueCount),
            values,
            valueCount,
            scratch,
            highHistogram,
            middleHistograms,
            workerHistograms,
            workerFlags,
            syncTarget,
            blankingTarget,
            workerThreads);
    }

    private static (double SyncTip, double Blanking) SelectLevelQuantilesRadixCore(
        ReadOnlySpan<double> values,
        double[]? parallelValues,
        int parallelValueCount,
        double[] scratch,
        int[] highHistogram,
        int[] middleHistograms,
        int[]? workerHistograms,
        int[]? workerFlags,
        int syncTarget,
        int blankingTarget,
        int workerThreads)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("At least one level sample is required.", nameof(values));
        }

        if ((uint)syncTarget >= (uint)values.Length
            || (uint)blankingTarget >= (uint)values.Length
            || syncTarget > blankingTarget)
        {
            throw new ArgumentOutOfRangeException(nameof(syncTarget));
        }

        if (scratch.Length < values.Length
            || highHistogram.Length < RadixHistogramWidth
            || middleHistograms.Length < RadixHistogramWidth * 2)
        {
            throw new ArgumentException("The radix quantile workspaces are too small.");
        }

        bool finiteNonZero;
        if (parallelValues is not null)
        {
            ArgumentNullException.ThrowIfNull(workerHistograms);
            ArgumentNullException.ThrowIfNull(workerFlags);

            finiteNonZero = FillHighHistogramParallel(
                parallelValues,
                parallelValueCount,
                highHistogram,
                workerHistograms,
                workerFlags,
                workerThreads);
        }
        else
        {
            finiteNonZero = FillHighHistogramSequential(values, highHistogram);
        }

        if (!finiteNonZero)
        {
            values.CopyTo(scratch);
            return SelectLevelQuantilesSequential(
                scratch,
                syncTarget,
                blankingTarget,
                values.Length);
        }

        BucketSelection syncHigh = LocateBucket(
            highHistogram.AsSpan(0, RadixHistogramWidth),
            syncTarget);
        BucketSelection blankingHigh = LocateBucket(
            highHistogram.AsSpan(0, RadixHistogramWidth),
            blankingTarget);

        int middleHistogramLength = syncHigh.Bucket == blankingHigh.Bucket
            ? RadixHistogramWidth
            : RadixHistogramWidth * 2;
        int blankingHistogramOffset = syncHigh.Bucket == blankingHigh.Bucket
            ? 0
            : RadixHistogramWidth;
        if (parallelValues is not null)
        {
            FillMiddleHistogramsParallel(
                parallelValues,
                parallelValueCount,
                middleHistograms,
                workerHistograms!,
                syncHigh.Bucket,
                blankingHigh.Bucket,
                blankingHistogramOffset,
                middleHistogramLength,
                workerThreads);
        }
        else
        {
            FillMiddleHistogramsSequential(
                values,
                middleHistograms,
                syncHigh.Bucket,
                blankingHigh.Bucket,
                blankingHistogramOffset,
                middleHistogramLength);
        }

        BucketSelection syncMiddle = LocateBucket(
            middleHistograms.AsSpan(0, RadixHistogramWidth),
            syncHigh.RankWithinBucket);
        BucketSelection blankingMiddle = LocateBucket(
            middleHistograms.AsSpan(blankingHistogramOffset, RadixHistogramWidth),
            blankingHigh.RankWithinBucket);
        uint syncPrefix = ((uint)syncHigh.Bucket << 16) | (uint)syncMiddle.Bucket;
        uint blankingPrefix = ((uint)blankingHigh.Bucket << 16) | (uint)blankingMiddle.Bucket;

        if (syncPrefix == blankingPrefix)
        {
            int write = 0;
            for (int index = 0; index < values.Length; index++)
            {
                double value = values[index];
                if (SortablePrefix(value) == syncPrefix)
                {
                    scratch[write++] = value;
                }
            }

            System.Diagnostics.Debug.Assert(write == syncMiddle.Count);
            return SelectTwoInRange(
                scratch,
                left: 0,
                count: write,
                syncMiddle.RankWithinBucket,
                blankingMiddle.RankWithinBucket);
        }

        int syncWrite = 0;
        int blankingStart = syncMiddle.Count;
        int blankingWrite = blankingStart;
        for (int index = 0; index < values.Length; index++)
        {
            double value = values[index];
            uint prefix = SortablePrefix(value);
            if (prefix == syncPrefix)
            {
                scratch[syncWrite++] = value;
            }
            else if (prefix == blankingPrefix)
            {
                scratch[blankingWrite++] = value;
            }
        }

        System.Diagnostics.Debug.Assert(syncWrite == syncMiddle.Count);
        System.Diagnostics.Debug.Assert(blankingWrite - blankingStart == blankingMiddle.Count);
        double syncTip = SelectKth(
            scratch,
            syncMiddle.RankWithinBucket,
            left: 0,
            right: syncWrite - 1,
            out _,
            out _);
        int blankingTargetInScratch = blankingStart + blankingMiddle.RankWithinBucket;
        double blanking = SelectKth(
            scratch,
            blankingTargetInScratch,
            left: blankingStart,
            right: blankingWrite - 1,
            out _,
            out _);
        return (syncTip, blanking);
    }

    private static bool FillHighHistogramSequential(
        ReadOnlySpan<double> values,
        int[] highHistogram)
    {
        Array.Clear(highHistogram, 0, RadixHistogramWidth);
        for (int index = 0; index < values.Length; index++)
        {
            double value = values[index];
            if (!double.IsFinite(value) || value == 0.0)
            {
                return false;
            }

            uint prefix = SortablePrefix(value);
            highHistogram[prefix >> 16]++;
        }

        return true;
    }

    private static bool FillHighHistogramParallel(
        double[] values,
        int valueCount,
        int[] highHistogram,
        int[] workerHistograms,
        int[] workerFlags,
        int workerThreads)
    {
        int workerHistogramLength = checked(
            workerThreads * RadixHistogramWidth);
        if (workerHistograms.Length < workerHistogramLength
            || workerFlags.Length < workerThreads)
        {
            throw new ArgumentException(
                "The parallel radix workspaces are too small.");
        }

        Array.Clear(workerHistograms, 0, workerHistogramLength);
        Array.Clear(workerFlags, 0, workerThreads);
        Parallel.For(
            fromInclusive: 0,
            toExclusive: workerThreads,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workerThreads
            },
            worker =>
            {
                int start = (int)(((long)valueCount * worker) / workerThreads);
                int end = (int)(((long)valueCount * (worker + 1)) / workerThreads);
                int histogramOffset = worker * RadixHistogramWidth;
                for (int index = start; index < end; index++)
                {
                    double value = values[index];
                    if (!double.IsFinite(value) || value == 0.0)
                    {
                        workerFlags[worker] = 1;
                        continue;
                    }

                    uint prefix = SortablePrefix(value);
                    workerHistograms[histogramOffset + (prefix >> 16)]++;
                }
            });

        for (int worker = 0; worker < workerThreads; worker++)
        {
            if (workerFlags[worker] != 0)
            {
                return false;
            }
        }

        for (int bucket = 0; bucket < RadixHistogramWidth; bucket++)
        {
            int count = 0;
            for (int worker = 0; worker < workerThreads; worker++)
            {
                count += workerHistograms[
                    (worker * RadixHistogramWidth) + bucket];
            }

            highHistogram[bucket] = count;
        }

        return true;
    }

    private static void FillMiddleHistogramsSequential(
        ReadOnlySpan<double> values,
        int[] middleHistograms,
        int syncHighBucket,
        int blankingHighBucket,
        int blankingHistogramOffset,
        int middleHistogramLength)
    {
        Array.Clear(middleHistograms, 0, middleHistogramLength);
        for (int index = 0; index < values.Length; index++)
        {
            uint prefix = SortablePrefix(values[index]);
            int high = (int)(prefix >> 16);
            int middle = (int)(prefix & 0xFFFF);
            if (high == syncHighBucket)
            {
                middleHistograms[middle]++;
            }
            else if (high == blankingHighBucket)
            {
                middleHistograms[blankingHistogramOffset + middle]++;
            }
        }
    }

    private static void FillMiddleHistogramsParallel(
        double[] values,
        int valueCount,
        int[] middleHistograms,
        int[] workerHistograms,
        int syncHighBucket,
        int blankingHighBucket,
        int blankingHistogramOffset,
        int middleHistogramLength,
        int workerThreads)
    {
        int workerHistogramLength = checked(
            workerThreads * middleHistogramLength);
        if (workerHistograms.Length < workerHistogramLength)
        {
            throw new ArgumentException(
                "The parallel radix histogram workspace is too small.",
                nameof(workerHistograms));
        }

        Array.Clear(workerHistograms, 0, workerHistogramLength);
        Parallel.For(
            fromInclusive: 0,
            toExclusive: workerThreads,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workerThreads
            },
            worker =>
            {
                int start = (int)(((long)valueCount * worker) / workerThreads);
                int end = (int)(((long)valueCount * (worker + 1)) / workerThreads);
                int workerOffset = worker * middleHistogramLength;
                for (int index = start; index < end; index++)
                {
                    uint prefix = SortablePrefix(values[index]);
                    int high = (int)(prefix >> 16);
                    int middle = (int)(prefix & 0xFFFF);
                    if (high == syncHighBucket)
                    {
                        workerHistograms[workerOffset + middle]++;
                    }
                    else if (high == blankingHighBucket)
                    {
                        workerHistograms[
                            workerOffset + blankingHistogramOffset + middle]++;
                    }
                }
            });

        for (int bucket = 0; bucket < middleHistogramLength; bucket++)
        {
            int count = 0;
            for (int worker = 0; worker < workerThreads; worker++)
            {
                count += workerHistograms[
                    (worker * middleHistogramLength) + bucket];
            }

            middleHistograms[bucket] = count;
        }
    }

    private static uint SortablePrefix(double value)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        ulong key = (bits & 0x8000_0000_0000_0000UL) != 0
            ? ~bits
            : bits ^ 0x8000_0000_0000_0000UL;
        return (uint)(key >> 32);
    }

    private static BucketSelection LocateBucket(ReadOnlySpan<int> histogram, int target)
    {
        int before = 0;
        for (int bucket = 0; bucket < histogram.Length; bucket++)
        {
            int count = histogram[bucket];
            if (target < before + count)
            {
                return new BucketSelection(bucket, target - before, count);
            }

            before += count;
        }

        throw new ArgumentOutOfRangeException(nameof(target));
    }

    internal static (double SyncTip, double Blanking) SelectLevelQuantiles(
        double[] values,
        int syncTarget,
        int blankingTarget,
        int count)
    {
        for (int index = 0; index < count; index++)
        {
            double value = values[index];
            if (!double.IsFinite(value) || value == 0.0)
            {
                return SelectLevelQuantilesSequential(
                    values,
                    syncTarget,
                    blankingTarget,
                    count);
            }
        }

        return SelectTwoInRange(
            values,
            left: 0,
            count,
            syncTarget,
            blankingTarget);
    }

    private static (double SyncTip, double Blanking) SelectTwoInRange(
        double[] values,
        int left,
        int count,
        int syncTargetWithinRange,
        int blankingTargetWithinRange)
    {
        int syncTarget = left + syncTargetWithinRange;
        int blankingTarget = left + blankingTargetWithinRange;
        double blanking = SelectKth(
            values,
            blankingTarget,
            left,
            right: left + count - 1,
            out int lowerBound,
            out _);
        double syncTip = syncTarget < lowerBound
            ? SelectKth(
                values,
                syncTarget,
                left,
                right: lowerBound - 1,
                out _,
                out _)
            : values[syncTarget];
        return (syncTip, blanking);
    }

    internal static (double SyncTip, double Blanking) SelectLevelQuantilesSequential(
        double[] values,
        int syncTarget,
        int blankingTarget,
        int count)
    {
        double syncTip = SelectKth(values, syncTarget, count);
        double blanking = SelectKth(values, blankingTarget, count);
        return (syncTip, blanking);
    }

    private static double SelectKth(double[] values, int target, int count)
        => SelectKth(
            values,
            target,
            left: 0,
            right: count - 1,
            out _,
            out _);

    private static double SelectKth(
        double[] values,
        int target,
        int left,
        int right,
        out int lowerBound,
        out int upperBound)
    {
        int depthLimit =
            2 * (BitOperations.Log2((uint)(right - left + 1)) + 1);
        while (left < right)
        {
            int length = right - left + 1;
            if (length <= PartitionSortThreshold || depthLimit-- == 0)
            {
                Array.Sort(values, left, length, NumpyDoubleComparer.Instance);
                lowerBound = target;
                upperBound = target;
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
                lowerBound = lower;
                upperBound = upper;
                return values[target];
            }
        }

        lowerBound = left;
        upperBound = right;
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

    private readonly record struct BucketSelection(
        int Bucket,
        int RankWithinBucket,
        int Count);

    private sealed class VhsSyncWorkspace
    {
        private double[] _filtered = [];
        private double[] _partitioned = [];
        private int[] _highHistogram = [];
        private int[] _middleHistograms = [];
        private int[] _workerHistograms = [];
        private int[] _workerFlags = [];
        private List<int>[] _thresholdCrossingsByWorker = [];
        private int[] _thresholdCrossingOverflowFlags = [];

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

        public int[] EnsureHighHistogram()
        {
            if (_highHistogram.Length < RadixHistogramWidth)
            {
                _highHistogram = GC.AllocateUninitializedArray<int>(RadixHistogramWidth);
            }

            return _highHistogram;
        }

        public int[] EnsureMiddleHistograms()
        {
            int length = RadixHistogramWidth * 2;
            if (_middleHistograms.Length < length)
            {
                _middleHistograms = GC.AllocateUninitializedArray<int>(length);
            }

            return _middleHistograms;
        }

        public int[] EnsureWorkerHistograms(int workerThreads)
        {
            int length = checked(
                workerThreads * RadixHistogramWidth * 2);
            if (_workerHistograms.Length < length)
            {
                _workerHistograms = GC.AllocateUninitializedArray<int>(length);
            }

            return _workerHistograms;
        }

        public int[] EnsureWorkerFlags(int workerThreads)
        {
            if (_workerFlags.Length < workerThreads)
            {
                _workerFlags = GC.AllocateUninitializedArray<int>(workerThreads);
            }

            return _workerFlags;
        }

        public List<int>[] PrepareThresholdCrossingLists(
            int workerCount,
            int partitionCapacity)
        {
            if (_thresholdCrossingsByWorker.Length < workerCount)
            {
                Array.Resize(ref _thresholdCrossingsByWorker, workerCount);
            }

            for (int worker = 0; worker < workerCount; worker++)
            {
                List<int> crossings = _thresholdCrossingsByWorker[worker]
                    ??= new List<int>(partitionCapacity);
                crossings.Clear();
                crossings.EnsureCapacity(partitionCapacity);
            }

            return _thresholdCrossingsByWorker;
        }

        public int[] PrepareThresholdCrossingOverflowFlags(int workerCount)
        {
            if (_thresholdCrossingOverflowFlags.Length < workerCount)
            {
                _thresholdCrossingOverflowFlags = new int[workerCount];
            }

            Array.Clear(_thresholdCrossingOverflowFlags, 0, workerCount);
            return _thresholdCrossingOverflowFlags;
        }
    }
}
