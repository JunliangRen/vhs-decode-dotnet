using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    private const int ConservativeParallelBoxcarWorkers = 4;
    private const int MaximumParallelBoxcarWorkers = 8;
    private const int MaximumBufferedThresholdCrossingsPerWorker = 16_384;
    private const int MinimumParallelBoxcarSamples = 65_536;
    private const int MinimumParallelEdgeScanSamples = 65_536;
    private const int MinimumParallelRadixSamples = 524_288;
    private const int ParallelRadixFirstShift = 21;
    private const int ParallelRadixSecondShift = 10;
    private const int ParallelRadixFirstWidth = 1 << 11;
    private const int ParallelRadixSecondWidth = 1 << 11;
    private const int ParallelRadixThirdWidth = 1 << 10;
    private const int MaximumParallelRadixHistogramLength = ParallelRadixSecondWidth * 2;
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
    private readonly bool _useCompactParallelRadix;
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
        bool parallelizePreciseEdgeScan = true,
        bool useCompactParallelRadix = true,
        bool useWideParallelPreprocessing = true)
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
        _workerThreads = ResolveParallelWorkerCount(
            workerThreads,
            useWideParallelPreprocessing);
        _parallelizePreciseEdgeScan = parallelizePreciseEdgeScan;
        _useCompactParallelRadix = useCompactParallelRadix;
    }

    internal static int ResolveParallelWorkerCount(
        int workerThreads,
        bool useWideParallelPreprocessing)
    {
        int maximumWorkers = useWideParallelPreprocessing
            && workerThreads >= MaximumParallelBoxcarWorkers
                ? MaximumParallelBoxcarWorkers
                : ConservativeParallelBoxcarWorkers;
        return Math.Clamp(workerThreads, 1, maximumWorkers);
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
                        _workerThreads,
                        _useCompactParallelRadix);
            }

            return DetectFiltered(
                filtered.AsSpan(0, filteredLength),
                syncTipEstimate,
                blankingEstimate,
                workspace,
                filtered,
                _workerThreads);
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
                blankingEstimate,
                workspace);
        }
        finally
        {
            _workspaces.Add(workspace);
        }
    }

    // Experimental Approx island. Production callers remain on Detect(double).
    // The float32 boxcar and scans intentionally form a separate numerical contract.
    internal VhsSyncDetectionResult DetectApproxFloat32(
        float[] demodulated,
        bool detectLevels,
        double syncTipEstimate,
        double blankingEstimate,
        bool allowAvx = true)
    {
        ArgumentNullException.ThrowIfNull(demodulated);
        if (_workerThreads == 1
            || demodulated.Length < MinimumParallelBoxcarSamples)
        {
            return DetectApproxFloat32(
                demodulated.AsSpan(),
                detectLevels,
                syncTipEstimate,
                blankingEstimate,
                allowAvx);
        }

        int windowSize = Math.Max(3, (int)_approximateTransition);
        if ((windowSize & 1) == 0)
        {
            windowSize++;
        }

        if (windowSize != 9)
        {
            throw new InvalidOperationException(
                "The Approx float32 sync specialization requires a nine-tap boxcar.");
        }

        VhsSyncWorkspace workspace =
            _workspaces.TryTake(out VhsSyncWorkspace? available)
                ? available
                : new VhsSyncWorkspace();
        try
        {
            int filteredLength = Math.Max(demodulated.Length, windowSize);
            float[] filtered = workspace.EnsureFloat32FilteredLength(filteredLength);
            ConvolveBoxcarSameParallelFloat32(
                demodulated,
                windowSize,
                filtered,
                filteredLength,
                _workerThreads,
                allowAvx);
            if (detectLevels)
            {
                (syncTipEstimate, blankingEstimate) = EstimateLevelsParallelFloat32(
                    filtered,
                    filteredLength,
                    workspace,
                    _workerThreads,
                    _useCompactParallelRadix);
            }

            return DetectFilteredFloat32(
                filtered.AsSpan(0, filteredLength),
                syncTipEstimate,
                blankingEstimate,
                workspace,
                allowAvx,
                filtered,
                _workerThreads);
        }
        finally
        {
            _workspaces.Add(workspace);
        }
    }

    internal VhsSyncDetectionResult DetectApproxFloat32(
        ReadOnlySpan<float> demodulated,
        bool detectLevels,
        double syncTipEstimate,
        double blankingEstimate,
        bool allowAvx = true)
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

        if (windowSize != 9)
        {
            throw new InvalidOperationException(
                "The Approx float32 sync specialization requires a nine-tap boxcar.");
        }

        VhsSyncWorkspace workspace = _workspaces.TryTake(out VhsSyncWorkspace? available)
            ? available
            : new VhsSyncWorkspace();
        try
        {
            int filteredLength = Math.Max(demodulated.Length, windowSize);
            float[] filtered = workspace.EnsureFloat32FilteredLength(filteredLength);
            ConvolveBoxcarSameFloat32(
                demodulated,
                windowSize,
                filtered.AsSpan(0, filteredLength),
                allowAvx);
            if (detectLevels)
            {
                (syncTipEstimate, blankingEstimate) = EstimateLevelsFloat32(
                    filtered.AsSpan(0, filteredLength),
                    workspace);
            }

            return DetectFilteredFloat32(
                filtered.AsSpan(0, filteredLength),
                syncTipEstimate,
                blankingEstimate,
                workspace,
                allowAvx);
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
        if ((uint)outputLength > (uint)output.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputLength),
                outputLength,
                "The output length must fit within the destination array.");
        }

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

    internal static void ConvolveBoxcarSameParallelFloat32(
        float[] values,
        int windowSize,
        float[] output,
        int outputLength,
        int workerThreads,
        bool allowAvx)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(output);
        if ((uint)outputLength > (uint)output.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputLength),
                outputLength,
                "The output length must fit within the destination array.");
        }

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
                ConvolveBoxcarRangeFloat32(
                    values,
                    windowSize,
                    output,
                    start,
                    end,
                    allowAvx);
            });
    }

    private static void WidenFloat32Parallel(
        float[] values,
        double[] output,
        int length,
        int workerThreads)
    {
        int workerCount = Math.Min(workerThreads, length);
        Parallel.For(
            fromInclusive: 0,
            toExclusive: workerCount,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount
            },
            worker =>
            {
                int start = (int)(((long)length * worker) / workerCount);
                int end = (int)(((long)length * (worker + 1)) / workerCount);
                for (int index = start; index < end; index++)
                {
                    output[index] = values[index];
                }
            });
    }

    private static void WidenFloat32(
        ReadOnlySpan<float> values,
        double[] output)
    {
        if (output.Length < values.Length)
        {
            throw new ArgumentException(
                "The float32 level workspace is too small.",
                nameof(output));
        }

        for (int index = 0; index < values.Length; index++)
        {
            output[index] = values[index];
        }
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

    private static void ConvolveBoxcarRangeFloat32(
        float[] values,
        int windowSize,
        float[] output,
        int start,
        int end,
        bool allowAvx)
    {
        if (windowSize == 9 && values.Length >= 9)
        {
            ConvolveBoxcarRange9Float32(values, output, start, end, allowAvx);
            return;
        }

        int firstFullIndex =
            (Math.Min(values.Length, windowSize) - 1) / 2;
        float scale = 1.0f / windowSize;
        for (int outputIndex = start; outputIndex < end; outputIndex++)
        {
            int fullIndex = firstFullIndex + outputIndex;
            int sourceStart = Math.Max(
                0,
                fullIndex - (windowSize - 1));
            int sourceEnd = Math.Min(values.Length - 1, fullIndex);
            float sum = 0.0f;
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
        ReadOnlySpan<double> values,
        Span<double> output,
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
            int outputIndex = interiorStart;
            if (Avx.IsSupported)
            {
                Vector256<double> scale = Vector256.Create(Scale);
                int vectorEnd = interiorEnd - ((interiorEnd - outputIndex) & 3);
                for (; outputIndex < vectorEnd; outputIndex += 4)
                {
                    double* source = valuesPointer + outputIndex - HalfWindow;
                    Vector256<double> sum = Vector256<double>.Zero;
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 1), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 2), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 3), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 4), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 5), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 6), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 7), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 8), scale));
                    Vector256<double> unordered = Avx.Compare(
                        sum,
                        sum,
                        FloatComparisonMode.UnorderedNotEqualNonSignaling);
                    if (Avx.MoveMask(unordered) == 0)
                    {
                        Avx.Store(outputPointer + outputIndex, sum);
                    }
                    else
                    {
                        // SIMD NaN propagation can select a different payload than the
                        // scalar upstream order. Recompute only the affected quartet;
                        // finite RF data remains on the vector path.
                        ConvolveBoxcarRangeEdge9(
                            values,
                            output,
                            outputIndex,
                            outputIndex + 4);
                    }
                }
            }

            ConvolveBoxcarRangeEdge9(values, output, outputIndex, interiorEnd);
        }

        ConvolveBoxcarRangeEdge9(values, output, Math.Max(start, interiorEnd), end);
    }

    private static void ConvolveBoxcarRangeEdge9(
        ReadOnlySpan<double> values,
        Span<double> output,
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

    internal static float[] ConvolveBoxcarSameFloat32(
        ReadOnlySpan<float> values,
        int windowSize,
        bool allowAvx)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        if ((windowSize & 1) == 0)
        {
            throw new ArgumentException(
                "The boxcar window must have odd length.",
                nameof(windowSize));
        }

        if (values.IsEmpty)
        {
            return [];
        }

        var output = new float[Math.Max(values.Length, windowSize)];
        ConvolveBoxcarSameFloat32(values, windowSize, output, allowAvx);
        return output;
    }

    private static void ConvolveBoxcarSameFloat32(
        ReadOnlySpan<float> values,
        int windowSize,
        Span<float> output,
        bool allowAvx)
    {
        int outputLength = Math.Max(values.Length, windowSize);
        if (output.Length != outputLength)
        {
            throw new ArgumentException(
                "The output length must match NumPy same-mode convolution.",
                nameof(output));
        }

        if (windowSize == 9 && values.Length >= 9)
        {
            ConvolveBoxcarRange9Float32(values, output, 0, outputLength, allowAvx);
            return;
        }

        int firstFullIndex = (Math.Min(values.Length, windowSize) - 1) / 2;
        float scale = 1.0f / windowSize;
        for (int outputIndex = 0; outputIndex < outputLength; outputIndex++)
        {
            int fullIndex = firstFullIndex + outputIndex;
            int sourceStart = Math.Max(0, fullIndex - (windowSize - 1));
            int sourceEnd = Math.Min(values.Length - 1, fullIndex);
            float sum = 0.0f;
            for (int sourceIndex = sourceStart; sourceIndex <= sourceEnd; sourceIndex++)
            {
                sum += values[sourceIndex] * scale;
            }

            output[outputIndex] = sum;
        }
    }

    private static unsafe void ConvolveBoxcarRange9Float32(
        ReadOnlySpan<float> values,
        Span<float> output,
        int start,
        int end,
        bool allowAvx)
    {
        const int HalfWindow = 4;
        const float Scale = 1.0f / 9.0f;
        int interiorStart = Math.Max(start, HalfWindow);
        int interiorEnd = Math.Min(end, values.Length - HalfWindow);
        ConvolveBoxcarRangeEdge9Float32(
            values,
            output,
            start,
            Math.Min(end, interiorStart));

        fixed (float* valuesPointer = values)
        fixed (float* outputPointer = output)
        {
            int outputIndex = interiorStart;
            if (allowAvx && Avx.IsSupported)
            {
                Vector256<float> scale = Vector256.Create(Scale);
                int vectorEnd = interiorEnd - ((interiorEnd - outputIndex) & 7);
                for (; outputIndex < vectorEnd; outputIndex += 8)
                {
                    float* source = valuesPointer + outputIndex - HalfWindow;
                    Vector256<float> sum = Vector256<float>.Zero;
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 1), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 2), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 3), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 4), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 5), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 6), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 7), scale));
                    sum = Avx.Add(sum, Avx.Multiply(Avx.LoadVector256(source + 8), scale));
                    Vector256<float> unordered = Avx.Compare(
                        sum,
                        sum,
                        FloatComparisonMode.UnorderedNotEqualNonSignaling);
                    if (Avx.MoveMask(unordered) == 0)
                    {
                        Avx.Store(outputPointer + outputIndex, sum);
                    }
                    else
                    {
                        ConvolveBoxcarRangeEdge9Float32(
                            values,
                            output,
                            outputIndex,
                            outputIndex + 8);
                    }
                }
            }

            ConvolveBoxcarRangeEdge9Float32(
                values,
                output,
                outputIndex,
                interiorEnd);
        }

        ConvolveBoxcarRangeEdge9Float32(
            values,
            output,
            Math.Max(start, interiorEnd),
            end);
    }

    private static void ConvolveBoxcarRangeEdge9Float32(
        ReadOnlySpan<float> values,
        Span<float> output,
        int start,
        int end)
    {
        const int HalfWindow = 4;
        const float Scale = 1.0f / 9.0f;
        for (int outputIndex = start; outputIndex < end; outputIndex++)
        {
            int sourceStart = Math.Max(0, outputIndex - HalfWindow);
            int sourceEnd = Math.Min(values.Length - 1, outputIndex + HalfWindow);
            float sum = 0.0f;
            for (int sourceIndex = sourceStart; sourceIndex <= sourceEnd; sourceIndex++)
            {
                sum += values[sourceIndex] * Scale;
            }

            output[outputIndex] = sum;
        }
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

        if (windowSize == 9 && values.Length >= 9)
        {
            ConvolveBoxcarRange9(values, output, 0, outputLength);
            return;
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
        VhsSyncWorkspace workspace,
        double[]? parallelFiltered = null,
        int parallelWorkerThreads = 1)
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
        double[] candidateSyncLevels = workspace.EnsureCandidateSyncLevels(candidateCount);
        double[] candidatePorchLevels = workspace.EnsureCandidatePorchLevels(candidateCount);
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

        double[] statisticsScratch = workspace.EnsureStatisticsScratch(candidateCount);
        candidateSyncLevels.AsSpan(0, candidateCount).CopyTo(statisticsScratch);
        Array.Sort(
            statisticsScratch,
            0,
            candidateCount,
            NumpyDoubleComparer.Instance);
        double medianSync = statisticsScratch[candidateCount / 2];
        for (int index = 0; index < candidateCount; index++)
        {
            statisticsScratch[index] = Math.Abs(candidateSyncLevels[index] - medianSync);
        }

        Array.Sort(
            statisticsScratch,
            0,
            candidateCount,
            NumpyDoubleComparer.Instance);
        double medianAbsoluteDeviation = statisticsScratch[candidateCount / 2];
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
        int[] gridSupportCount = workspace.EnsureGridSupportCounts(amplitudeCount);
        FillOrderedGridSupportCounts(
            falls,
            amplitudeCount,
            effectiveLineLength,
            jitterTolerance,
            gridSupportCount);

        bool[] finalMask = workspace.PrepareFinalMask(amplitudeCount);
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
                    workspace);
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
            FindPreciseEdgesOnValidGrid(
                filtered,
                preciseMidpoint,
                falls,
                finalMask,
                amplitudeCount,
                effectiveLineLength,
                jitterTolerance,
                fallingEdges,
                risingEdges,
                allowAvx: true);
        }

        if (fallingEdges.Count == 0)
        {
            return new VhsSyncDetectionResult([], syncTipLevel, backPorchLevel);
        }

        double[] slopes = workspace.EnsureStatisticsScratch(fallingEdges.Count);
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

    private VhsSyncDetectionResult DetectFilteredFloat32(
        ReadOnlySpan<float> filtered,
        double syncTipEstimate,
        double blankingEstimate,
        VhsSyncWorkspace workspace,
        bool allowAvx,
        float[]? parallelFiltered = null,
        int parallelWorkerThreads = 1)
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
            (falls, rises) = FindInitialEdgesParallelFloat32(
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
            (falls, rises) = FindInitialEdgesSequentialFloat32(
                filtered,
                slicerLevelEstimate,
                minimumWidth,
                maximumWidth,
                initialCapacity,
                allowAvx);
        }

        if (falls.Length == 0)
        {
            return new VhsSyncDetectionResult([], syncTipEstimate, blankingEstimate);
        }

        int candidateCount = falls.Length;
        double[] candidateSyncLevels = workspace.EnsureCandidateSyncLevels(candidateCount);
        double[] candidatePorchLevels = workspace.EnsureCandidatePorchLevels(candidateCount);
        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            int middle = (falls[candidate] + rises[candidate]) / 2;
            candidateSyncLevels[candidate] = UpperMedianOfWindowFloat32(
                filtered,
                Math.Max(0, middle - 2),
                Math.Min(sampleCount, middle + 3),
                syncTipEstimate);

            int porchCenter = (int)(rises[candidate] + (_backPorchLength * 0.5));
            candidatePorchLevels[candidate] = UpperMedianOfWindowFloat32(
                filtered,
                Math.Max(0, porchCenter - 2),
                Math.Min(sampleCount, porchCenter + 3),
                blankingEstimate);
        }

        double[] statisticsScratch = workspace.EnsureStatisticsScratch(candidateCount);
        candidateSyncLevels.AsSpan(0, candidateCount).CopyTo(statisticsScratch);
        Array.Sort(
            statisticsScratch,
            0,
            candidateCount,
            NumpyDoubleComparer.Instance);
        double medianSync = statisticsScratch[candidateCount / 2];
        for (int index = 0; index < candidateCount; index++)
        {
            statisticsScratch[index] = Math.Abs(candidateSyncLevels[index] - medianSync);
        }

        Array.Sort(
            statisticsScratch,
            0,
            candidateCount,
            NumpyDoubleComparer.Instance);
        double medianAbsoluteDeviation = statisticsScratch[candidateCount / 2];
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
        int[] gridSupportCount = workspace.EnsureGridSupportCounts(amplitudeCount);
        FillOrderedGridSupportCounts(
            falls,
            amplitudeCount,
            effectiveLineLength,
            jitterTolerance,
            gridSupportCount);

        bool[] finalMask = workspace.PrepareFinalMask(amplitudeCount);
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
                FindThresholdCrossingsParallelFloat32(
                    parallelFiltered,
                    sampleCount,
                    preciseMidpoint,
                    parallelWorkerThreads,
                    initialCapacity,
                    workspace);
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
            FindPreciseEdgesOnValidGridFloat32(
                filtered,
                preciseMidpoint,
                falls,
                finalMask,
                amplitudeCount,
                effectiveLineLength,
                jitterTolerance,
                fallingEdges,
                risingEdges,
                allowAvx);
        }

        if (fallingEdges.Count == 0)
        {
            return new VhsSyncDetectionResult([], syncTipLevel, backPorchLevel);
        }

        double[] slopes = workspace.EnsureStatisticsScratch(fallingEdges.Count);
        int slopeCount = 0;
        for (int index = 0; index < fallingEdges.Count; index++)
        {
            int rise = risingEdges[index];
            if (10 < rise && rise < sampleCount - 10)
            {
                slopes[slopeCount++] = Math.Abs(
                    (double)filtered[rise + 1] - filtered[rise - 1]);
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
            double fallingFirst = (double)filtered[fall] - preciseMidpoint;
            double fallingSecond = (double)filtered[fall + 1] - preciseMidpoint;
            double fallingDifference = fallingFirst - fallingSecond;
            double subpixelFall = fall + (fallingDifference != 0.0
                ? fallingFirst / fallingDifference
                : 0.0);

            double risingFirst = (double)filtered[rise] - preciseMidpoint;
            double risingSecond = (double)filtered[rise + 1] - preciseMidpoint;
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

    private static (int[] Falls, int[] Rises) FindInitialEdgesParallelFloat32(
        float[] filtered,
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
        float slicerThreshold = CeilingToFloatThreshold(slicerLevel);
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
                    if (filtered[index] >= slicerThreshold
                        && filtered[index + 1] < slicerThreshold)
                    {
                        fallingIndex = index;
                    }
                    else if (fallingIndex != -1
                             && filtered[index] < slicerThreshold
                             && filtered[index + 1] >= slicerThreshold)
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

    internal static unsafe (int[] Falls, int[] Rises) FindInitialEdgesSequentialFloat32(
        ReadOnlySpan<float> filtered,
        double slicerLevel,
        double minimumWidth,
        double maximumWidth,
        int initialCapacity,
        bool allowAvx)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        var falls = new List<int>(initialCapacity);
        var rises = new List<int>(initialCapacity);
        int comparisonCount = Math.Max(0, filtered.Length - 1);
        float slicerThreshold = CeilingToFloatThreshold(slicerLevel);
        int fallingIndex = -1;
        int index = 0;
        if (allowAvx && Avx.IsSupported && comparisonCount >= 8)
        {
            fixed (float* filteredPointer = filtered)
            {
                Vector256<float> threshold = Vector256.Create(slicerThreshold);
                int vectorizedEnd = comparisonCount & ~7;
                for (; index < vectorizedEnd; index += 8)
                {
                    Vector256<float> current = Avx.LoadVector256(filteredPointer + index);
                    Vector256<float> next = Avx.LoadVector256(filteredPointer + index + 1);
                    int fallingMask = Avx.MoveMask(Avx.And(
                        Avx.Compare(
                            threshold,
                            current,
                            FloatComparisonMode.OrderedLessThanOrEqualNonSignaling),
                        Avx.Compare(
                            next,
                            threshold,
                            FloatComparisonMode.OrderedLessThanNonSignaling)));
                    int risingMask = Avx.MoveMask(Avx.And(
                        Avx.Compare(
                            current,
                            threshold,
                            FloatComparisonMode.OrderedLessThanNonSignaling),
                        Avx.Compare(
                            threshold,
                            next,
                            FloatComparisonMode.OrderedLessThanOrEqualNonSignaling)));
                    int crossingMask = fallingMask | risingMask;
                    while (crossingMask != 0)
                    {
                        int lane = BitOperations.TrailingZeroCount((uint)crossingMask);
                        int bit = 1 << lane;
                        int crossingIndex = index + lane;
                        if ((fallingMask & bit) != 0)
                        {
                            fallingIndex = crossingIndex;
                        }
                        else if (fallingIndex != -1)
                        {
                            int width = crossingIndex - fallingIndex;
                            if (minimumWidth < width && width < maximumWidth)
                            {
                                falls.Add(fallingIndex);
                                rises.Add(crossingIndex);
                            }

                            fallingIndex = -1;
                        }

                        crossingMask &= crossingMask - 1;
                    }
                }
            }
        }

        for (; index < comparisonCount; index++)
        {
            if (filtered[index] >= slicerThreshold
                && filtered[index + 1] < slicerThreshold)
            {
                fallingIndex = index;
            }
            else if (fallingIndex != -1
                     && filtered[index] < slicerThreshold
                     && filtered[index + 1] >= slicerThreshold)
            {
                int width = index - fallingIndex;
                if (minimumWidth < width && width < maximumWidth)
                {
                    falls.Add(fallingIndex);
                    rises.Add(index);
                }

                fallingIndex = -1;
            }
        }

        return (falls.ToArray(), rises.ToArray());
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

    private static (List<int>[] CrossingsByWorker, int WorkerCount, bool Overflowed)
        FindThresholdCrossingsParallelFloat32(
        float[] filtered,
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
        float thresholdBoundary = CeilingToFloatThreshold(threshold);
        Parallel.For(
            0,
            workerCount,
            new ParallelOptions { MaxDegreeOfParallelism = workerCount },
            worker =>
            {
                int start = (int)(((long)scanLimit * worker) / workerCount);
                int end = (int)(((long)scanLimit * (worker + 1)) / workerCount);
                List<int> crossings = crossingsByWorker[worker];
                if (!TryFillThresholdCrossingsPartitionFloat32(
                    filtered,
                    start,
                    end,
                    thresholdBoundary,
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

    internal static bool TryFillThresholdCrossingsPartitionFloat32(
        float[] filtered,
        int start,
        int end,
        float threshold,
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

    internal static unsafe void FindPreciseEdgesOnValidGrid(
        ReadOnlySpan<double> filtered,
        double preciseMidpoint,
        int[] falls,
        bool[] finalMask,
        int amplitudeCount,
        double effectiveLineLength,
        double jitterTolerance,
        List<int> fallingEdges,
        List<int> risingEdges,
        bool allowAvx)
    {
        ArgumentNullException.ThrowIfNull(falls);
        ArgumentNullException.ThrowIfNull(finalMask);
        ArgumentNullException.ThrowIfNull(fallingEdges);
        ArgumentNullException.ThrowIfNull(risingEdges);
        if ((uint)amplitudeCount > (uint)falls.Length
            || (uint)amplitudeCount > (uint)finalMask.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(amplitudeCount));
        }

        int comparisonCount = Math.Max(0, filtered.Length - 1);
        int fallingIndex = -1;
        int index = 0;
        if (allowAvx && Avx.IsSupported && comparisonCount >= 4)
        {
            fixed (double* filteredPointer = filtered)
            {
                Vector256<double> midpoint = Vector256.Create(preciseMidpoint);
                int vectorizedEnd = comparisonCount & ~3;
                for (; index < vectorizedEnd; index += 4)
                {
                    Vector256<double> current = Avx.LoadVector256(
                        filteredPointer + index);
                    Vector256<double> next = Avx.LoadVector256(
                        filteredPointer + index + 1);
                    int fallingMask = Avx.MoveMask(Avx.And(
                        Avx.Compare(
                            midpoint,
                            current,
                            FloatComparisonMode.OrderedLessThanOrEqualNonSignaling),
                        Avx.Compare(
                            next,
                            midpoint,
                            FloatComparisonMode.OrderedLessThanNonSignaling)));
                    int risingMask = Avx.MoveMask(Avx.And(
                        Avx.Compare(
                            current,
                            midpoint,
                            FloatComparisonMode.OrderedLessThanNonSignaling),
                        Avx.Compare(
                            midpoint,
                            next,
                            FloatComparisonMode.OrderedLessThanOrEqualNonSignaling)));
                    int crossingMask = fallingMask | risingMask;
                    // Commit crossings in scalar index order so pulse state is identical.
                    while (crossingMask != 0)
                    {
                        int lane = BitOperations.TrailingZeroCount(
                            (uint)crossingMask);
                        int bit = 1 << lane;
                        int crossingIndex = index + lane;
                        if ((fallingMask & bit) != 0)
                        {
                            fallingIndex = crossingIndex;
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
                                risingEdges.Add(crossingIndex);
                            }

                            fallingIndex = -1;
                        }

                        crossingMask &= crossingMask - 1;
                    }
                }
            }
        }

        for (; index < comparisonCount; index++)
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
                if (IsOnValidGrid(
                    fallingIndex,
                    falls,
                    finalMask,
                    amplitudeCount,
                    effectiveLineLength,
                    jitterTolerance))
                {
                    fallingEdges.Add(fallingIndex);
                    risingEdges.Add(index);
                }

                fallingIndex = -1;
            }
        }
    }

    internal static unsafe void FindPreciseEdgesOnValidGridFloat32(
        ReadOnlySpan<float> filtered,
        double preciseMidpoint,
        int[] falls,
        bool[] finalMask,
        int amplitudeCount,
        double effectiveLineLength,
        double jitterTolerance,
        List<int> fallingEdges,
        List<int> risingEdges,
        bool allowAvx)
    {
        ArgumentNullException.ThrowIfNull(falls);
        ArgumentNullException.ThrowIfNull(finalMask);
        ArgumentNullException.ThrowIfNull(fallingEdges);
        ArgumentNullException.ThrowIfNull(risingEdges);
        if ((uint)amplitudeCount > (uint)falls.Length
            || (uint)amplitudeCount > (uint)finalMask.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(amplitudeCount));
        }

        int comparisonCount = Math.Max(0, filtered.Length - 1);
        float midpointBoundary = CeilingToFloatThreshold(preciseMidpoint);
        int fallingIndex = -1;
        int index = 0;
        if (allowAvx && Avx.IsSupported && comparisonCount >= 8)
        {
            fixed (float* filteredPointer = filtered)
            {
                Vector256<float> midpoint = Vector256.Create(midpointBoundary);
                int vectorizedEnd = comparisonCount & ~7;
                for (; index < vectorizedEnd; index += 8)
                {
                    Vector256<float> current = Avx.LoadVector256(
                        filteredPointer + index);
                    Vector256<float> next = Avx.LoadVector256(
                        filteredPointer + index + 1);
                    int fallingMask = Avx.MoveMask(Avx.And(
                        Avx.Compare(
                            midpoint,
                            current,
                            FloatComparisonMode.OrderedLessThanOrEqualNonSignaling),
                        Avx.Compare(
                            next,
                            midpoint,
                            FloatComparisonMode.OrderedLessThanNonSignaling)));
                    int risingMask = Avx.MoveMask(Avx.And(
                        Avx.Compare(
                            current,
                            midpoint,
                            FloatComparisonMode.OrderedLessThanNonSignaling),
                        Avx.Compare(
                            midpoint,
                            next,
                            FloatComparisonMode.OrderedLessThanOrEqualNonSignaling)));
                    int crossingMask = fallingMask | risingMask;
                    while (crossingMask != 0)
                    {
                        int lane = BitOperations.TrailingZeroCount((uint)crossingMask);
                        int bit = 1 << lane;
                        int crossingIndex = index + lane;
                        if ((fallingMask & bit) != 0)
                        {
                            fallingIndex = crossingIndex;
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
                                risingEdges.Add(crossingIndex);
                            }

                            fallingIndex = -1;
                        }

                        crossingMask &= crossingMask - 1;
                    }
                }
            }
        }

        for (; index < comparisonCount; index++)
        {
            if (filtered[index] >= midpointBoundary
                && filtered[index + 1] < midpointBoundary)
            {
                fallingIndex = index;
            }
            else if (fallingIndex != -1
                     && filtered[index] < midpointBoundary
                     && filtered[index + 1] >= midpointBoundary)
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
                    risingEdges.Add(index);
                }

                fallingIndex = -1;
            }
        }
    }

    private static float CeilingToFloatThreshold(double value)
    {
        // For a float sample f, comparing f against this boundary is equivalent
        // to first widening f to double and comparing it against value.
        float rounded = (float)value;
        if (float.IsNaN(rounded) || (double)rounded >= value)
        {
            return rounded;
        }

        return MathF.BitIncrement(rounded);
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
        int workerThreads,
        bool useCompactParallelRadix)
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
            workspace.EnsureWorkerHistograms(
                workerThreads,
                useCompactParallelRadix),
            workspace.EnsureWorkerFlags(workerThreads),
            syncIndex,
            blankingIndex,
            workerThreads,
            useCompactParallelRadix);
    }

    private static (double SyncTip, double Blanking) EstimateLevelsFloat32(
        ReadOnlySpan<float> filtered,
        VhsSyncWorkspace workspace)
    {
        int syncIndex = (int)(filtered.Length * 0.05);
        int blankingIndex = (int)(filtered.Length * 0.25);
        return SelectLevelQuantilesRadixFloat32(
            filtered,
            workspace.EnsureFloat32LevelValuesLength(filtered.Length),
            workspace.EnsurePartitionedLength(filtered.Length),
            workspace.EnsureHighHistogram(),
            workspace.EnsureMiddleHistograms(),
            syncIndex,
            blankingIndex);
    }

    private static (double SyncTip, double Blanking) EstimateLevelsParallelFloat32(
        float[] filtered,
        int filteredLength,
        VhsSyncWorkspace workspace,
        int workerThreads,
        bool useCompactParallelRadix)
    {
        if (workerThreads <= 1
            || filteredLength < MinimumParallelRadixSamples)
        {
            return EstimateLevelsFloat32(
                filtered.AsSpan(0, filteredLength),
                workspace);
        }

        int syncIndex = (int)(filteredLength * 0.05);
        int blankingIndex = (int)(filteredLength * 0.25);
        return SelectLevelQuantilesRadixFloat32Parallel(
            filtered,
            filteredLength,
            workspace.EnsureFloat32LevelValuesLength(filteredLength),
            workspace.EnsurePartitionedLength(filteredLength),
            workspace.EnsureHighHistogram(),
            workspace.EnsureMiddleHistograms(),
            workspace.EnsureWorkerHistograms(
                workerThreads,
                useCompactParallelRadix),
            workspace.EnsureWorkerFlags(workerThreads),
            syncIndex,
            blankingIndex,
            workerThreads,
            useCompactParallelRadix);
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

    private static double UpperMedianOfWindowFloat32(
        ReadOnlySpan<float> values,
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
        for (int index = 0; index < length; index++)
        {
            window[index] = values[start + index];
        }

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
            workerThreads: 1,
            useCompactParallelRadix: false);

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
        int workerThreads,
        bool useCompactParallelRadix = true)
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
            workerThreads,
            useCompactParallelRadix);
    }

    internal static (double SyncTip, double Blanking) SelectLevelQuantilesRadixFloat32(
        ReadOnlySpan<float> values,
        double[] widenedValues,
        double[] scratch,
        int[] highHistogram,
        int[] middleHistograms,
        int syncTarget,
        int blankingTarget)
    {
        ValidateFloat32QuantileArguments(
            values.Length,
            widenedValues,
            scratch,
            highHistogram,
            middleHistograms,
            syncTarget,
            blankingTarget);

        bool finiteNonZero = FillFirstHistogramSequentialFloat32(
            values,
            highHistogram);
        if (!finiteNonZero)
        {
            WidenFloat32(values, widenedValues);
            return SelectLevelQuantilesRadix(
                widenedValues.AsSpan(0, values.Length),
                scratch,
                highHistogram,
                middleHistograms,
                syncTarget,
                blankingTarget);
        }

        BucketSelection syncFirst = LocateBucket(
            highHistogram.AsSpan(0, ParallelRadixFirstWidth),
            syncTarget);
        BucketSelection blankingFirst = LocateBucket(
            highHistogram.AsSpan(0, ParallelRadixFirstWidth),
            blankingTarget);
        int secondHistogramOffset = syncFirst.Bucket == blankingFirst.Bucket
            ? 0
            : ParallelRadixSecondWidth;
        int secondHistogramLength = secondHistogramOffset + ParallelRadixSecondWidth;
        FillSecondHistogramsSequentialFloat32(
            values,
            middleHistograms,
            syncFirst.Bucket,
            blankingFirst.Bucket,
            secondHistogramOffset,
            secondHistogramLength);

        BucketSelection syncSecond = LocateBucket(
            middleHistograms.AsSpan(0, ParallelRadixSecondWidth),
            syncFirst.RankWithinBucket);
        BucketSelection blankingSecond = LocateBucket(
            middleHistograms.AsSpan(secondHistogramOffset, ParallelRadixSecondWidth),
            blankingFirst.RankWithinBucket);
        int syncSecondPrefix =
            (syncFirst.Bucket << 11) | syncSecond.Bucket;
        int blankingSecondPrefix =
            (blankingFirst.Bucket << 11) | blankingSecond.Bucket;
        int thirdHistogramOffset = syncSecondPrefix == blankingSecondPrefix
            ? 0
            : ParallelRadixThirdWidth;
        int thirdHistogramLength = thirdHistogramOffset + ParallelRadixThirdWidth;
        FillThirdHistogramsSequentialFloat32(
            values,
            highHistogram,
            syncSecondPrefix,
            blankingSecondPrefix,
            thirdHistogramOffset,
            thirdHistogramLength);

        BucketSelection syncThird = LocateBucket(
            highHistogram.AsSpan(0, ParallelRadixThirdWidth),
            syncSecond.RankWithinBucket);
        BucketSelection blankingThird = LocateBucket(
            highHistogram.AsSpan(thirdHistogramOffset, ParallelRadixThirdWidth),
            blankingSecond.RankWithinBucket);
        uint syncKey = ((uint)syncSecondPrefix << 10) | (uint)syncThird.Bucket;
        uint blankingKey =
            ((uint)blankingSecondPrefix << 10) | (uint)blankingThird.Bucket;
        return (
            SortableKeyToFloat32(syncKey),
            SortableKeyToFloat32(blankingKey));
    }

    internal static (double SyncTip, double Blanking) SelectLevelQuantilesRadixFloat32Parallel(
        float[] values,
        int valueCount,
        double[] widenedValues,
        double[] scratch,
        int[] firstAndThirdHistograms,
        int[] secondHistograms,
        int[] workerHistograms,
        int[] workerFlags,
        int syncTarget,
        int blankingTarget,
        int workerThreads,
        bool useCompactParallelRadix = true)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueCount);
        if (valueCount > values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(valueCount));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerThreads);
        ValidateFloat32QuantileArguments(
            valueCount,
            widenedValues,
            scratch,
            firstAndThirdHistograms,
            secondHistograms,
            syncTarget,
            blankingTarget);
        ArgumentNullException.ThrowIfNull(workerHistograms);
        ArgumentNullException.ThrowIfNull(workerFlags);

        if (!useCompactParallelRadix)
        {
            WidenFloat32Parallel(
                values,
                widenedValues,
                valueCount,
                workerThreads);
            return SelectLevelQuantilesRadixParallel(
                widenedValues,
                valueCount,
                scratch,
                firstAndThirdHistograms,
                secondHistograms,
                workerHistograms,
                workerFlags,
                syncTarget,
                blankingTarget,
                workerThreads,
                useCompactParallelRadix: false);
        }

        bool finiteNonZero = FillParallelFirstHistogramFloat32(
            values,
            valueCount,
            firstAndThirdHistograms,
            workerHistograms,
            workerFlags,
            workerThreads);
        if (!finiteNonZero)
        {
            WidenFloat32Parallel(
                values,
                widenedValues,
                valueCount,
                workerThreads);
            return SelectLevelQuantilesRadixParallel(
                widenedValues,
                valueCount,
                scratch,
                firstAndThirdHistograms,
                secondHistograms,
                workerHistograms,
                workerFlags,
                syncTarget,
                blankingTarget,
                workerThreads,
                useCompactParallelRadix: true);
        }

        BucketSelection syncFirst = LocateBucket(
            firstAndThirdHistograms.AsSpan(0, ParallelRadixFirstWidth),
            syncTarget);
        BucketSelection blankingFirst = LocateBucket(
            firstAndThirdHistograms.AsSpan(0, ParallelRadixFirstWidth),
            blankingTarget);
        int secondHistogramOffset = syncFirst.Bucket == blankingFirst.Bucket
            ? 0
            : ParallelRadixSecondWidth;
        int secondHistogramLength = secondHistogramOffset + ParallelRadixSecondWidth;
        FillParallelChildHistogramsFloat32(
            values,
            valueCount,
            secondHistograms,
            workerHistograms,
            syncFirst.Bucket,
            blankingFirst.Bucket,
            secondHistogramOffset,
            secondHistogramLength,
            parentShift: ParallelRadixFirstShift,
            childShift: ParallelRadixSecondShift,
            childMask: ParallelRadixSecondWidth - 1,
            workerThreads);

        BucketSelection syncSecond = LocateBucket(
            secondHistograms.AsSpan(0, ParallelRadixSecondWidth),
            syncFirst.RankWithinBucket);
        BucketSelection blankingSecond = LocateBucket(
            secondHistograms.AsSpan(secondHistogramOffset, ParallelRadixSecondWidth),
            blankingFirst.RankWithinBucket);
        int syncSecondPrefix =
            (syncFirst.Bucket << 11) | syncSecond.Bucket;
        int blankingSecondPrefix =
            (blankingFirst.Bucket << 11) | blankingSecond.Bucket;
        int thirdHistogramOffset = syncSecondPrefix == blankingSecondPrefix
            ? 0
            : ParallelRadixThirdWidth;
        int thirdHistogramLength = thirdHistogramOffset + ParallelRadixThirdWidth;
        FillParallelChildHistogramsFloat32(
            values,
            valueCount,
            firstAndThirdHistograms,
            workerHistograms,
            syncSecondPrefix,
            blankingSecondPrefix,
            thirdHistogramOffset,
            thirdHistogramLength,
            parentShift: ParallelRadixSecondShift,
            childShift: 0,
            childMask: ParallelRadixThirdWidth - 1,
            workerThreads);

        BucketSelection syncThird = LocateBucket(
            firstAndThirdHistograms.AsSpan(0, ParallelRadixThirdWidth),
            syncSecond.RankWithinBucket);
        BucketSelection blankingThird = LocateBucket(
            firstAndThirdHistograms.AsSpan(thirdHistogramOffset, ParallelRadixThirdWidth),
            blankingSecond.RankWithinBucket);
        uint syncKey = ((uint)syncSecondPrefix << 10) | (uint)syncThird.Bucket;
        uint blankingKey =
            ((uint)blankingSecondPrefix << 10) | (uint)blankingThird.Bucket;
        return (
            SortableKeyToFloat32(syncKey),
            SortableKeyToFloat32(blankingKey));
    }

    private static void ValidateFloat32QuantileArguments(
        int valueCount,
        double[] widenedValues,
        double[] scratch,
        int[] highHistogram,
        int[] middleHistograms,
        int syncTarget,
        int blankingTarget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueCount);
        ArgumentNullException.ThrowIfNull(widenedValues);
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(highHistogram);
        ArgumentNullException.ThrowIfNull(middleHistograms);
        if ((uint)syncTarget >= (uint)valueCount
            || (uint)blankingTarget >= (uint)valueCount
            || syncTarget > blankingTarget)
        {
            throw new ArgumentOutOfRangeException(nameof(syncTarget));
        }

        if (widenedValues.Length < valueCount
            || scratch.Length < valueCount
            || highHistogram.Length < RadixHistogramWidth
            || middleHistograms.Length < RadixHistogramWidth * 2)
        {
            throw new ArgumentException("The float32 radix quantile workspaces are too small.");
        }
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
        int workerThreads,
        bool useCompactParallelRadix)
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

        if (parallelValues is not null)
        {
            ArgumentNullException.ThrowIfNull(workerHistograms);
            ArgumentNullException.ThrowIfNull(workerFlags);

            return useCompactParallelRadix
                ? SelectLevelQuantilesRadixParallelThreeStage(
                    parallelValues,
                    parallelValueCount,
                    scratch,
                    highHistogram,
                    middleHistograms,
                    workerHistograms,
                    workerFlags,
                    syncTarget,
                    blankingTarget,
                    workerThreads)
                : SelectLevelQuantilesRadixParallelTwoStage(
                    parallelValues,
                    parallelValueCount,
                    scratch,
                    highHistogram,
                    middleHistograms,
                    workerHistograms,
                    workerFlags,
                    syncTarget,
                    blankingTarget,
                    workerThreads);
        }

        bool finiteNonZero = FillHighHistogramSequential(values, highHistogram);
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
        FillMiddleHistogramsSequential(
            values,
            middleHistograms,
            syncHigh.Bucket,
            blankingHigh.Bucket,
            blankingHistogramOffset,
            middleHistogramLength);

        BucketSelection syncMiddle = LocateBucket(
            middleHistograms.AsSpan(0, RadixHistogramWidth),
            syncHigh.RankWithinBucket);
        BucketSelection blankingMiddle = LocateBucket(
            middleHistograms.AsSpan(blankingHistogramOffset, RadixHistogramWidth),
            blankingHigh.RankWithinBucket);
        uint syncPrefix = ((uint)syncHigh.Bucket << 16) | (uint)syncMiddle.Bucket;
        uint blankingPrefix = ((uint)blankingHigh.Bucket << 16) | (uint)blankingMiddle.Bucket;

        return SelectLevelQuantilesFromPrefixes(
            values,
            scratch,
            syncPrefix,
            blankingPrefix,
            syncMiddle,
            blankingMiddle);
    }

    private static (double SyncTip, double Blanking) SelectLevelQuantilesRadixParallelThreeStage(
        double[] values,
        int valueCount,
        double[] scratch,
        int[] firstAndThirdHistograms,
        int[] secondHistograms,
        int[] workerHistograms,
        int[] workerFlags,
        int syncTarget,
        int blankingTarget,
        int workerThreads)
    {
        bool finiteNonZero = FillParallelFirstHistogram(
            values,
            valueCount,
            firstAndThirdHistograms,
            workerHistograms,
            workerFlags,
            workerThreads);
        if (!finiteNonZero)
        {
            values.AsSpan(0, valueCount).CopyTo(scratch);
            return SelectLevelQuantilesSequential(
                scratch,
                syncTarget,
                blankingTarget,
                valueCount);
        }

        BucketSelection syncFirst = LocateBucket(
            firstAndThirdHistograms.AsSpan(0, ParallelRadixFirstWidth),
            syncTarget);
        BucketSelection blankingFirst = LocateBucket(
            firstAndThirdHistograms.AsSpan(0, ParallelRadixFirstWidth),
            blankingTarget);

        int secondHistogramOffset = syncFirst.Bucket == blankingFirst.Bucket
            ? 0
            : ParallelRadixSecondWidth;
        int secondHistogramLength = secondHistogramOffset + ParallelRadixSecondWidth;
        FillParallelChildHistograms(
            values,
            valueCount,
            secondHistograms,
            workerHistograms,
            syncFirst.Bucket,
            blankingFirst.Bucket,
            secondHistogramOffset,
            secondHistogramLength,
            parentShift: ParallelRadixFirstShift,
            childShift: ParallelRadixSecondShift,
            childMask: ParallelRadixSecondWidth - 1,
            workerThreads);

        BucketSelection syncSecond = LocateBucket(
            secondHistograms.AsSpan(0, ParallelRadixSecondWidth),
            syncFirst.RankWithinBucket);
        BucketSelection blankingSecond = LocateBucket(
            secondHistograms.AsSpan(secondHistogramOffset, ParallelRadixSecondWidth),
            blankingFirst.RankWithinBucket);
        int syncSecondPrefix =
            (syncFirst.Bucket << 11) | syncSecond.Bucket;
        int blankingSecondPrefix =
            (blankingFirst.Bucket << 11) | blankingSecond.Bucket;

        int thirdHistogramOffset = syncSecondPrefix == blankingSecondPrefix
            ? 0
            : ParallelRadixThirdWidth;
        int thirdHistogramLength = thirdHistogramOffset + ParallelRadixThirdWidth;
        FillParallelChildHistograms(
            values,
            valueCount,
            firstAndThirdHistograms,
            workerHistograms,
            syncSecondPrefix,
            blankingSecondPrefix,
            thirdHistogramOffset,
            thirdHistogramLength,
            parentShift: ParallelRadixSecondShift,
            childShift: 0,
            childMask: ParallelRadixThirdWidth - 1,
            workerThreads);

        BucketSelection syncThird = LocateBucket(
            firstAndThirdHistograms.AsSpan(0, ParallelRadixThirdWidth),
            syncSecond.RankWithinBucket);
        BucketSelection blankingThird = LocateBucket(
            firstAndThirdHistograms.AsSpan(thirdHistogramOffset, ParallelRadixThirdWidth),
            blankingSecond.RankWithinBucket);
        uint syncPrefix = ((uint)syncSecondPrefix << 10) | (uint)syncThird.Bucket;
        uint blankingPrefix = ((uint)blankingSecondPrefix << 10) | (uint)blankingThird.Bucket;

        return SelectLevelQuantilesFromPrefixesParallel(
            values,
            valueCount,
            scratch,
            syncPrefix,
            blankingPrefix,
            syncThird,
            blankingThird,
            workerHistograms,
            thirdHistogramLength,
            workerThreads);
    }

    private static (double SyncTip, double Blanking) SelectLevelQuantilesRadixParallelTwoStage(
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
        bool finiteNonZero = FillParallelHighHistogramTwoStage(
            values,
            valueCount,
            highHistogram,
            workerHistograms,
            workerFlags,
            workerThreads);
        if (!finiteNonZero)
        {
            values.AsSpan(0, valueCount).CopyTo(scratch);
            return SelectLevelQuantilesSequential(
                scratch,
                syncTarget,
                blankingTarget,
                valueCount);
        }

        BucketSelection syncHigh = LocateBucket(
            highHistogram.AsSpan(0, RadixHistogramWidth),
            syncTarget);
        BucketSelection blankingHigh = LocateBucket(
            highHistogram.AsSpan(0, RadixHistogramWidth),
            blankingTarget);

        int blankingHistogramOffset = syncHigh.Bucket == blankingHigh.Bucket
            ? 0
            : RadixHistogramWidth;
        int middleHistogramLength = blankingHistogramOffset + RadixHistogramWidth;
        FillParallelMiddleHistogramsTwoStage(
            values,
            valueCount,
            middleHistograms,
            workerHistograms,
            syncHigh.Bucket,
            blankingHigh.Bucket,
            blankingHistogramOffset,
            middleHistogramLength,
            workerThreads);

        BucketSelection syncMiddle = LocateBucket(
            middleHistograms.AsSpan(0, RadixHistogramWidth),
            syncHigh.RankWithinBucket);
        BucketSelection blankingMiddle = LocateBucket(
            middleHistograms.AsSpan(blankingHistogramOffset, RadixHistogramWidth),
            blankingHigh.RankWithinBucket);
        uint syncPrefix = ((uint)syncHigh.Bucket << 16) | (uint)syncMiddle.Bucket;
        uint blankingPrefix = ((uint)blankingHigh.Bucket << 16) | (uint)blankingMiddle.Bucket;

        return SelectLevelQuantilesFromPrefixes(
            values.AsSpan(0, valueCount),
            scratch,
            syncPrefix,
            blankingPrefix,
            syncMiddle,
            blankingMiddle);
    }

    private static (double SyncTip, double Blanking) SelectLevelQuantilesFromPrefixes(
        ReadOnlySpan<double> values,
        double[] scratch,
        uint syncPrefix,
        uint blankingPrefix,
        BucketSelection syncSelection,
        BucketSelection blankingSelection)
    {

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

            System.Diagnostics.Debug.Assert(write == syncSelection.Count);
            return SelectTwoInRange(
                scratch,
                left: 0,
                count: write,
                syncSelection.RankWithinBucket,
                blankingSelection.RankWithinBucket);
        }

        int syncWrite = 0;
        int blankingStart = syncSelection.Count;
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

        System.Diagnostics.Debug.Assert(syncWrite == syncSelection.Count);
        System.Diagnostics.Debug.Assert(blankingWrite - blankingStart == blankingSelection.Count);
        double syncTip = SelectKth(
            scratch,
            syncSelection.RankWithinBucket,
            left: 0,
            right: syncWrite - 1,
            out _,
            out _);
        int blankingTargetInScratch = blankingStart + blankingSelection.RankWithinBucket;
        double blanking = SelectKth(
            scratch,
            blankingTargetInScratch,
            left: blankingStart,
            right: blankingWrite - 1,
            out _,
            out _);
        return (syncTip, blanking);
    }

    private static (double SyncTip, double Blanking) SelectLevelQuantilesFromPrefixesParallel(
        double[] values,
        int valueCount,
        double[] scratch,
        uint syncPrefix,
        uint blankingPrefix,
        BucketSelection syncSelection,
        BucketSelection blankingSelection,
        int[] workerHistograms,
        int workerHistogramLength,
        int workerThreads)
    {
        int syncBucket = (int)(syncPrefix & (ParallelRadixThirdWidth - 1));
        int blankingBucket = (int)(blankingPrefix & (ParallelRadixThirdWidth - 1));
        if (syncPrefix != blankingPrefix
            && (syncPrefix >> 10) != (blankingPrefix >> 10))
        {
            blankingBucket += ParallelRadixThirdWidth;
        }

        int syncWrite = 0;
        int blankingWrite = syncPrefix == blankingPrefix
            ? 0
            : syncSelection.Count;
        for (int worker = 0; worker < workerThreads; worker++)
        {
            int workerOffset = worker * workerHistogramLength;
            int syncEntry = workerOffset + syncBucket;
            int syncCount = workerHistograms[syncEntry];
            workerHistograms[syncEntry] = syncWrite;
            syncWrite += syncCount;

            if (syncPrefix != blankingPrefix)
            {
                int blankingEntry = workerOffset + blankingBucket;
                int blankingCount = workerHistograms[blankingEntry];
                workerHistograms[blankingEntry] = blankingWrite;
                blankingWrite += blankingCount;
            }
        }

        System.Diagnostics.Debug.Assert(syncWrite == syncSelection.Count);
        System.Diagnostics.Debug.Assert(
            syncPrefix == blankingPrefix
                || blankingWrite - syncSelection.Count == blankingSelection.Count);
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
                int workerOffset = worker * workerHistogramLength;
                int syncDestination = workerHistograms[workerOffset + syncBucket];
                int blankingDestination = syncPrefix == blankingPrefix
                    ? 0
                    : workerHistograms[workerOffset + blankingBucket];
                for (int index = start; index < end; index++)
                {
                    double value = values[index];
                    uint prefix = SortablePrefix(value);
                    if (prefix == syncPrefix)
                    {
                        scratch[syncDestination++] = value;
                    }
                    else if (prefix == blankingPrefix)
                    {
                        scratch[blankingDestination++] = value;
                    }
                }
            });

        if (syncPrefix == blankingPrefix)
        {
            return SelectTwoInRange(
                scratch,
                left: 0,
                count: syncWrite,
                syncSelection.RankWithinBucket,
                blankingSelection.RankWithinBucket);
        }

        double syncTip = SelectKth(
            scratch,
            syncSelection.RankWithinBucket,
            left: 0,
            right: syncWrite - 1,
            out _,
            out _);
        int blankingTargetInScratch =
            syncSelection.Count + blankingSelection.RankWithinBucket;
        double blanking = SelectKth(
            scratch,
            blankingTargetInScratch,
            left: syncSelection.Count,
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

    private static unsafe bool FillFirstHistogramSequentialFloat32(
        ReadOnlySpan<float> values,
        int[] firstHistogram)
    {
        Array.Clear(firstHistogram, 0, ParallelRadixFirstWidth);
        fixed (float* valuesPointer = values)
        fixed (int* histogramPointer = firstHistogram)
        {
            float* end = valuesPointer + values.Length;
            for (float* valuePointer = valuesPointer;
                valuePointer < end;
                valuePointer++)
            {
                uint bits = *(uint*)valuePointer;
                uint absoluteBits = bits & 0x7FFF_FFFFU;
                if (absoluteBits - 1U >= 0x7F7F_FFFFU)
                {
                    return false;
                }

                histogramPointer[
                    SortableKey(bits) >> ParallelRadixFirstShift]++;
            }
        }

        return true;
    }

    private static unsafe void FillSecondHistogramsSequentialFloat32(
        ReadOnlySpan<float> values,
        int[] childHistograms,
        int firstParentBucket,
        int secondParentBucket,
        int secondHistogramOffset,
        int histogramLength)
    {
        const uint ParentSignBit = 1U << (31 - ParallelRadixFirstShift);
        const uint ParentMask = ParallelRadixFirstWidth - 1;
        const uint ChildMask = ParallelRadixSecondWidth - 1;
        uint firstSortableParent = (uint)firstParentBucket;
        uint secondSortableParent = (uint)secondParentBucket;
        uint firstRawParent = (firstSortableParent & ParentSignBit) != 0
            ? firstSortableParent ^ ParentSignBit
            : ~firstSortableParent & ParentMask;
        uint secondRawParent = (secondSortableParent & ParentSignBit) != 0
            ? secondSortableParent ^ ParentSignBit
            : ~secondSortableParent & ParentMask;
        uint firstChildXor = (firstSortableParent & ParentSignBit) == 0
            ? ChildMask
            : 0U;
        uint secondChildXor = (secondSortableParent & ParentSignBit) == 0
            ? ChildMask
            : 0U;

        Array.Clear(childHistograms, 0, histogramLength);
        fixed (float* valuesPointer = values)
        fixed (int* histogramPointer = childHistograms)
        {
            float* end = valuesPointer + values.Length;
            for (float* valuePointer = valuesPointer;
                valuePointer < end;
                valuePointer++)
            {
                uint bits = *(uint*)valuePointer;
                uint parent = bits >> ParallelRadixFirstShift;
                if (parent == firstRawParent)
                {
                    int child = (int)(
                        ((bits >> ParallelRadixSecondShift) & ChildMask)
                        ^ firstChildXor);
                    histogramPointer[child]++;
                }
                else if (parent == secondRawParent)
                {
                    int child = (int)(
                        ((bits >> ParallelRadixSecondShift) & ChildMask)
                        ^ secondChildXor);
                    histogramPointer[secondHistogramOffset + child]++;
                }
            }
        }
    }

    private static unsafe void FillThirdHistogramsSequentialFloat32(
        ReadOnlySpan<float> values,
        int[] childHistograms,
        int firstParentBucket,
        int secondParentBucket,
        int secondHistogramOffset,
        int histogramLength)
    {
        const uint ParentSignBit = 1U << (31 - ParallelRadixSecondShift);
        const uint ParentMask = (1U << (32 - ParallelRadixSecondShift)) - 1U;
        const uint ChildMask = ParallelRadixThirdWidth - 1;
        uint firstSortableParent = (uint)firstParentBucket;
        uint secondSortableParent = (uint)secondParentBucket;
        uint firstRawParent = (firstSortableParent & ParentSignBit) != 0
            ? firstSortableParent ^ ParentSignBit
            : ~firstSortableParent & ParentMask;
        uint secondRawParent = (secondSortableParent & ParentSignBit) != 0
            ? secondSortableParent ^ ParentSignBit
            : ~secondSortableParent & ParentMask;
        uint firstChildXor = (firstSortableParent & ParentSignBit) == 0
            ? ChildMask
            : 0U;
        uint secondChildXor = (secondSortableParent & ParentSignBit) == 0
            ? ChildMask
            : 0U;

        Array.Clear(childHistograms, 0, histogramLength);
        fixed (float* valuesPointer = values)
        fixed (int* histogramPointer = childHistograms)
        {
            float* end = valuesPointer + values.Length;
            for (float* valuePointer = valuesPointer;
                valuePointer < end;
                valuePointer++)
            {
                uint bits = *(uint*)valuePointer;
                uint parent = bits >> ParallelRadixSecondShift;
                if (parent == firstRawParent)
                {
                    int child = (int)((bits & ChildMask) ^ firstChildXor);
                    histogramPointer[child]++;
                }
                else if (parent == secondRawParent)
                {
                    int child = (int)((bits & ChildMask) ^ secondChildXor);
                    histogramPointer[secondHistogramOffset + child]++;
                }
            }
        }
    }

    private static bool FillParallelFirstHistogram(
        double[] values,
        int valueCount,
        int[] firstHistogram,
        int[] workerHistograms,
        int[] workerFlags,
        int workerThreads)
    {
        int workerHistogramLength = checked(
            workerThreads * ParallelRadixFirstWidth);
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
                int histogramOffset = worker * ParallelRadixFirstWidth;
                if (FillFirstHistogramRange(
                        values,
                        start,
                        end,
                        workerHistograms,
                        histogramOffset))
                {
                    workerFlags[worker] = 1;
                }
            });

        for (int worker = 0; worker < workerThreads; worker++)
        {
            if (workerFlags[worker] != 0)
            {
                return false;
            }
        }

        MergeWorkerHistograms(
            workerHistograms,
            workerThreads,
            ParallelRadixFirstWidth,
            firstHistogram);

        return true;
    }

    private static unsafe bool FillFirstHistogramRange(
        double[] values,
        int start,
        int end,
        int[] workerHistograms,
        int histogramOffset)
    {
        bool exceptionalValueFound = false;
        fixed (double* valuesPointer = values)
        fixed (int* histogramPointer = workerHistograms)
        {
            int* workerHistogram = histogramPointer + histogramOffset;
            for (double* valuePointer = valuesPointer + start;
                valuePointer < valuesPointer + end;
                valuePointer++)
            {
                double value = *valuePointer;
                if (!double.IsFinite(value) || value == 0.0)
                {
                    exceptionalValueFound = true;
                    continue;
                }

                uint prefix = SortablePrefix(value);
                workerHistogram[prefix >> ParallelRadixFirstShift]++;
            }
        }

        return exceptionalValueFound;
    }

    private static bool FillParallelFirstHistogramFloat32(
        float[] values,
        int valueCount,
        int[] firstHistogram,
        int[] workerHistograms,
        int[] workerFlags,
        int workerThreads)
    {
        int workerHistogramLength = checked(
            workerThreads * ParallelRadixFirstWidth);
        if (workerHistograms.Length < workerHistogramLength
            || workerFlags.Length < workerThreads)
        {
            throw new ArgumentException(
                "The parallel float32 radix workspaces are too small.");
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
                int histogramOffset = worker * ParallelRadixFirstWidth;
                if (FillFirstHistogramRangeFloat32(
                        values,
                        start,
                        end,
                        workerHistograms,
                        histogramOffset))
                {
                    workerFlags[worker] = 1;
                }
            });

        for (int worker = 0; worker < workerThreads; worker++)
        {
            if (workerFlags[worker] != 0)
            {
                return false;
            }
        }

        MergeWorkerHistograms(
            workerHistograms,
            workerThreads,
            ParallelRadixFirstWidth,
            firstHistogram);
        return true;
    }

    private static unsafe bool FillFirstHistogramRangeFloat32(
        float[] values,
        int start,
        int end,
        int[] workerHistograms,
        int histogramOffset)
    {
        bool exceptionalValueFound = false;
        fixed (float* valuesPointer = values)
        fixed (int* histogramPointer = workerHistograms)
        {
            int* workerHistogram = histogramPointer + histogramOffset;
            for (float* valuePointer = valuesPointer + start;
                valuePointer < valuesPointer + end;
                valuePointer++)
            {
                float value = *valuePointer;
                if (!float.IsFinite(value) || value == 0.0f)
                {
                    exceptionalValueFound = true;
                    continue;
                }

                uint key = SortableKey(value);
                workerHistogram[key >> ParallelRadixFirstShift]++;
            }
        }

        return exceptionalValueFound;
    }

    private static bool FillParallelHighHistogramTwoStage(
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

        MergeWorkerHistograms(
            workerHistograms,
            workerThreads,
            RadixHistogramWidth,
            highHistogram);

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

    private static void FillParallelMiddleHistogramsTwoStage(
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

        MergeWorkerHistograms(
            workerHistograms,
            workerThreads,
            middleHistogramLength,
            middleHistograms);
    }

    private static void FillParallelChildHistograms(
        double[] values,
        int valueCount,
        int[] histograms,
        int[] workerHistograms,
        int firstParentBucket,
        int secondParentBucket,
        int secondHistogramOffset,
        int histogramLength,
        int parentShift,
        int childShift,
        int childMask,
        int workerThreads)
    {
        int workerHistogramLength = checked(
            workerThreads * histogramLength);
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
                int workerOffset = worker * histogramLength;
                FillChildHistogramRange(
                    values,
                    start,
                    end,
                    workerHistograms,
                    workerOffset,
                    firstParentBucket,
                    secondParentBucket,
                    secondHistogramOffset,
                    parentShift,
                    childShift,
                    childMask);
            });

        MergeWorkerHistograms(
            workerHistograms,
            workerThreads,
            histogramLength,
            histograms);
    }

    private static unsafe void FillChildHistogramRange(
        double[] values,
        int start,
        int end,
        int[] workerHistograms,
        int workerOffset,
        int firstParentBucket,
        int secondParentBucket,
        int secondHistogramOffset,
        int parentShift,
        int childShift,
        int childMask)
    {
        fixed (double* valuesPointer = values)
        fixed (int* histogramPointer = workerHistograms)
        {
            int* workerHistogram = histogramPointer + workerOffset;
            for (double* valuePointer = valuesPointer + start;
                valuePointer < valuesPointer + end;
                valuePointer++)
            {
                uint prefix = SortablePrefix(*valuePointer);
                int parent = (int)(prefix >> parentShift);
                int child = (int)((prefix >> childShift) & (uint)childMask);
                if (parent == firstParentBucket)
                {
                    workerHistogram[child]++;
                }
                else if (parent == secondParentBucket)
                {
                    workerHistogram[secondHistogramOffset + child]++;
                }
            }
        }
    }

    private static void FillParallelChildHistogramsFloat32(
        float[] values,
        int valueCount,
        int[] histograms,
        int[] workerHistograms,
        int firstParentBucket,
        int secondParentBucket,
        int secondHistogramOffset,
        int histogramLength,
        int parentShift,
        int childShift,
        int childMask,
        int workerThreads)
    {
        int workerHistogramLength = checked(workerThreads * histogramLength);
        if (workerHistograms.Length < workerHistogramLength)
        {
            throw new ArgumentException(
                "The parallel float32 radix histogram workspace is too small.",
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
                FillChildHistogramRangeFloat32(
                    values,
                    start,
                    end,
                    workerHistograms,
                    worker * histogramLength,
                    firstParentBucket,
                    secondParentBucket,
                    secondHistogramOffset,
                    parentShift,
                    childShift,
                    childMask);
            });

        MergeWorkerHistograms(
            workerHistograms,
            workerThreads,
            histogramLength,
            histograms);
    }

    private static unsafe void FillChildHistogramRangeFloat32(
        float[] values,
        int start,
        int end,
        int[] workerHistograms,
        int workerOffset,
        int firstParentBucket,
        int secondParentBucket,
        int secondHistogramOffset,
        int parentShift,
        int childShift,
        int childMask)
    {
        fixed (float* valuesPointer = values)
        fixed (int* histogramPointer = workerHistograms)
        {
            int* workerHistogram = histogramPointer + workerOffset;
            for (float* valuePointer = valuesPointer + start;
                valuePointer < valuesPointer + end;
                valuePointer++)
            {
                uint key = SortableKey(*valuePointer);
                int parent = (int)(key >> parentShift);
                int child = (int)((key >> childShift) & (uint)childMask);
                if (parent == firstParentBucket)
                {
                    workerHistogram[child]++;
                }
                else if (parent == secondParentBucket)
                {
                    workerHistogram[secondHistogramOffset + child]++;
                }
            }
        }
    }

    private static unsafe void MergeWorkerHistograms(
        int[] workerHistograms,
        int workerCount,
        int histogramLength,
        int[] destination)
    {
        ArgumentNullException.ThrowIfNull(workerHistograms);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(histogramLength);
        if (destination.Length < histogramLength
            || workerHistograms.Length < checked(workerCount * histogramLength))
        {
            throw new ArgumentException("The histogram merge buffers are too small.");
        }

        workerHistograms.AsSpan(0, histogramLength).CopyTo(destination);
        fixed (int* workerHistogramPointer = workerHistograms)
        fixed (int* destinationPointer = destination)
        {
            for (int worker = 1; worker < workerCount; worker++)
            {
                int workerOffset = worker * histogramLength;
                int bucket = 0;
                if (Avx2.IsSupported)
                {
                    int vectorEnd = histogramLength & ~7;
                    for (; bucket < vectorEnd; bucket += 8)
                    {
                        Vector256<int> merged = Avx2.Add(
                            Avx.LoadVector256(destinationPointer + bucket),
                            Avx.LoadVector256(workerHistogramPointer + workerOffset + bucket));
                        Avx.Store(destinationPointer + bucket, merged);
                    }
                }

                for (; bucket < histogramLength; bucket++)
                {
                    destinationPointer[bucket] += workerHistogramPointer[workerOffset + bucket];
                }
            }
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

    private static uint SortableKey(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        return SortableKey(bits);
    }

    private static uint SortableKey(uint bits)
        => bits ^ ((uint)((int)bits >> 31) | 0x8000_0000U);

    private static float SortableKeyToFloat32(uint key)
    {
        uint bits = (key & 0x8000_0000U) != 0
            ? key ^ 0x8000_0000U
            : ~key;
        return BitConverter.UInt32BitsToSingle(bits);
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
        private float[] _float32Filtered = [];
        private double[] _float32LevelValues = [];
        private double[] _partitioned = [];
        private double[] _candidateSyncLevels = [];
        private double[] _candidatePorchLevels = [];
        private double[] _statisticsScratch = [];
        private int[] _highHistogram = [];
        private int[] _middleHistograms = [];
        private int[] _gridSupportCounts = [];
        private bool[] _finalMask = [];
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

        public float[] EnsureFloat32FilteredLength(int length)
        {
            if (_float32Filtered.Length < length)
            {
                _float32Filtered = GC.AllocateUninitializedArray<float>(length);
            }

            return _float32Filtered;
        }

        public double[] EnsureFloat32LevelValuesLength(int length)
        {
            if (_float32LevelValues.Length < length)
            {
                _float32LevelValues = GC.AllocateUninitializedArray<double>(length);
            }

            return _float32LevelValues;
        }

        public double[] EnsurePartitionedLength(int length)
        {
            if (_partitioned.Length < length)
            {
                _partitioned = GC.AllocateUninitializedArray<double>(length);
            }

            return _partitioned;
        }

        public double[] EnsureCandidateSyncLevels(int length)
        {
            if (_candidateSyncLevels.Length < length)
            {
                _candidateSyncLevels = GC.AllocateUninitializedArray<double>(length);
            }

            return _candidateSyncLevels;
        }

        public double[] EnsureCandidatePorchLevels(int length)
        {
            if (_candidatePorchLevels.Length < length)
            {
                _candidatePorchLevels = GC.AllocateUninitializedArray<double>(length);
            }

            return _candidatePorchLevels;
        }

        public double[] EnsureStatisticsScratch(int length)
        {
            if (_statisticsScratch.Length < length)
            {
                _statisticsScratch = GC.AllocateUninitializedArray<double>(length);
            }

            return _statisticsScratch;
        }

        public int[] EnsureGridSupportCounts(int length)
        {
            if (_gridSupportCounts.Length < length)
            {
                _gridSupportCounts = GC.AllocateUninitializedArray<int>(length);
            }

            return _gridSupportCounts;
        }

        public bool[] PrepareFinalMask(int length)
        {
            if (_finalMask.Length < length)
            {
                _finalMask = GC.AllocateUninitializedArray<bool>(length);
            }

            Array.Clear(_finalMask, 0, length);
            return _finalMask;
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

        public int[] EnsureWorkerHistograms(
            int workerThreads,
            bool useCompactParallelRadix)
        {
            int length = checked(
                workerThreads
                * (useCompactParallelRadix
                    ? MaximumParallelRadixHistogramLength
                    : RadixHistogramWidth * 2));
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
