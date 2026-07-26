using System.Collections.Concurrent;

namespace VHSDecode.Core.Dsp;

internal readonly record struct VhsVSyncLevelRefinementResult(
    double SyncTipLevel,
    double BlankLevel,
    int SyncSampleCount,
    int BlankSampleCount);

internal sealed class VhsVSyncLevelRefiner
{
    private const double MinimumYieldFraction = 0.15;
    private const int MinimumYieldSamples = 5;
    private const double MadMultiplier = 3.5;
    private const double MadEpsilon = 1e-5;
    private const double MinimumAmplitudeRatio = 0.5;
    private const double MaximumAmplitudeRatio = 1.5;
    private const double MinimumSignalPower = 1e-5;
    private const double MinimumVariance = 1e-6;
    private const double MinimumAcceptableSnr = 9.0;
    private const double SnrThreshold = 20.0;
    private readonly ConcurrentBag<VhsVSyncLevelWorkspace> _workspaces = [];

    // Upstream: oyvindln/vhs-decode
    // Baseline: 2f21e8ed6018b14561396cc95f1f6828054470b8
    // Source: vhsdecode/field.py FieldShared._refine_levels_from_vsync_numba
    // Port policy: preserve the emitted Numba fastmath reduction and FMA order.
    public VhsVSyncLevelRefinementResult RefineField(
        ReadOnlySpan<double> demodulated,
        double line0Location,
        double meanLineLength,
        double originalSyncTip,
        double originalBlank)
    {
        int start = checked((int)Math.Round(
            line0Location + meanLineLength,
            MidpointRounding.ToEven));
        int end = checked((int)Math.Round(
            start + (meanLineLength * 8.5),
            MidpointRounding.ToEven));
        start = NormalizePythonSliceIndex(start, demodulated.Length);
        end = NormalizePythonSliceIndex(end, demodulated.Length);
        return end > start
            ? Refine(demodulated[start..end], originalSyncTip, originalBlank)
            : new VhsVSyncLevelRefinementResult(
                originalSyncTip,
                originalBlank,
                SyncSampleCount: 0,
                BlankSampleCount: 0);
    }

    public VhsVSyncLevelRefinementResult Refine(
        ReadOnlySpan<double> vSync,
        double originalSyncTip,
        double originalBlank)
    {
        if (vSync.IsEmpty)
        {
            return new VhsVSyncLevelRefinementResult(
                originalSyncTip,
                originalBlank,
                SyncSampleCount: 0,
                BlankSampleCount: 0);
        }

        VhsVSyncLevelWorkspace workspace = _workspaces.TryTake(
            out VhsVSyncLevelWorkspace? available)
            ? available
            : new VhsVSyncLevelWorkspace();
        try
        {
            workspace.EnsureLength(vSync.Length);
            Span<double> syncSamples = workspace.SyncSamples.AsSpan(0, vSync.Length);
            Span<double> blankSamples = workspace.BlankSamples.AsSpan(0, vSync.Length);
            int syncCount = 0;
            int blankCount = 0;
            foreach (double sample in vSync)
            {
                if (Math.Abs(sample - originalSyncTip) < Math.Abs(sample - originalBlank))
                {
                    syncSamples[syncCount++] = sample;
                }
                else
                {
                    blankSamples[blankCount++] = sample;
                }
            }

            syncCount = FilterLevelMad(
                syncSamples,
                syncCount,
                workspace.Deviations,
                workspace.MedianScratch);
            blankCount = FilterLevelMad(
                blankSamples,
                blankCount,
                workspace.Deviations,
                workspace.MedianScratch);

            double refinedSync = originalSyncTip;
            double refinedBlank = originalBlank;
            int minimumYield = Math.Max(
                (int)(vSync.Length * MinimumYieldFraction),
                MinimumYieldSamples);
            if (syncCount >= minimumYield && blankCount >= minimumYield)
            {
                ReadOnlySpan<double> selectedSync = syncSamples[..syncCount];
                ReadOnlySpan<double> selectedBlank = blankSamples[..blankCount];
                double meanSync = MeanInUpstreamOrder(selectedSync);
                double meanBlank = MeanInUpstreamOrder(selectedBlank);
                double measuredAmplitude = meanBlank - meanSync;
                double expectedAmplitude = originalBlank - originalSyncTip;
                if ((MinimumAmplitudeRatio * expectedAmplitude) < measuredAmplitude
                    && measuredAmplitude < (MaximumAmplitudeRatio * expectedAmplitude))
                {
                    double signalPower = measuredAmplitude * measuredAmplitude;
                    if (signalPower < MinimumSignalPower)
                    {
                        signalPower = MinimumSignalPower;
                    }

                    double syncVariance = VarianceInUpstreamOrder(selectedSync);
                    if (syncVariance < MinimumVariance)
                    {
                        syncVariance = MinimumVariance;
                    }

                    double syncSnr = signalPower / syncVariance;
                    if (syncSnr >= MinimumAcceptableSnr)
                    {
                        refinedSync = BlendLevelInUpstreamOrder(
                            originalSyncTip,
                            meanSync,
                            syncSnr);
                    }

                    double blankVariance = VarianceInUpstreamOrder(selectedBlank);
                    if (blankVariance < MinimumVariance)
                    {
                        blankVariance = MinimumVariance;
                    }

                    double blankSnr = signalPower / blankVariance;
                    if (blankSnr >= MinimumAcceptableSnr)
                    {
                        refinedBlank = BlendLevelInUpstreamOrder(
                            originalBlank,
                            meanBlank,
                            blankSnr);
                    }
                }
            }

            return new VhsVSyncLevelRefinementResult(
                refinedSync,
                refinedBlank,
                syncCount,
                blankCount);
        }
        finally
        {
            _workspaces.Add(workspace);
        }
    }

    internal static (double Mean, double Variance) MeanVarianceInUpstreamOrder(
        ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            return (double.NaN, double.NaN);
        }

        return (
            MeanInUpstreamOrder(values),
            VarianceInUpstreamOrder(values));
    }

    internal static int NormalizePythonSliceIndex(int index, int length)
    {
        if (index < 0)
        {
            index += length;
            if (index < 0)
            {
                return 0;
            }
        }

        return Math.Min(index, length);
    }

    private static int FilterLevelMad(
        Span<double> samples,
        int count,
        double[] deviations,
        double[] medianScratch)
    {
        if (count <= MinimumYieldSamples)
        {
            return count;
        }

        double median = NumpyReduction.MedianFloat64(samples[..count], medianScratch);
        for (int index = 0; index < count; index++)
        {
            deviations[index] = Math.Abs(samples[index] - median);
        }

        double mad = NumpyReduction.MedianFloat64(
            deviations.AsSpan(0, count),
            medianScratch);
        if (mad < MadEpsilon)
        {
            mad = MadEpsilon;
        }

        double maximumDeviation = MadMultiplier * mad;
        int cleanCount = 0;
        for (int index = 0; index < count; index++)
        {
            if (deviations[index] < maximumDeviation)
            {
                samples[cleanCount++] = samples[index];
            }
        }

        return cleanCount;
    }

    private static double MeanInUpstreamOrder(ReadOnlySpan<double> values)
        => SumInUpstreamOrder(values) / values.Length;

    private static double VarianceInUpstreamOrder(ReadOnlySpan<double> values)
    {
        double mean = MeanInUpstreamOrder(values);
        int vectorizedLength = values.Length & ~15;
        Span<double> accumulators = stackalloc double[16];
        accumulators.Clear();
        for (int index = 0; index < vectorizedLength; index += 16)
        {
            for (int lane = 0; lane < 16; lane++)
            {
                double delta = values[index + lane] - mean;
                accumulators[lane] = Math.FusedMultiplyAdd(
                    delta,
                    delta,
                    accumulators[lane]);
            }
        }

        double sum = ReduceSixteenLanes(accumulators);
        int epilogueLength = values.Length & ~3;
        if (vectorizedLength < epilogueLength)
        {
            Span<double> epilogue = stackalloc double[4];
            epilogue.Clear();
            epilogue[0] = sum;
            for (int index = vectorizedLength; index < epilogueLength; index += 4)
            {
                for (int lane = 0; lane < 4; lane++)
                {
                    double delta = values[index + lane] - mean;
                    epilogue[lane] = Math.FusedMultiplyAdd(
                        delta,
                        delta,
                        epilogue[lane]);
                }
            }

            sum = ReduceFourLanes(epilogue);
        }

        for (int index = epilogueLength; index < values.Length; index++)
        {
            double delta = values[index] - mean;
            sum = Math.FusedMultiplyAdd(delta, delta, sum);
        }

        return sum / values.Length;
    }

    private static double SumInUpstreamOrder(ReadOnlySpan<double> values)
    {
        int vectorizedLength = values.Length & ~15;
        Span<double> accumulators = stackalloc double[16];
        accumulators.Clear();
        for (int index = 0; index < vectorizedLength; index += 16)
        {
            for (int lane = 0; lane < 16; lane++)
            {
                accumulators[lane] = values[index + lane] + accumulators[lane];
            }
        }

        double sum = ReduceSixteenLanes(accumulators);
        int epilogueLength = values.Length & ~3;
        if (vectorizedLength < epilogueLength)
        {
            Span<double> epilogue = stackalloc double[4];
            epilogue.Clear();
            epilogue[0] = sum;
            for (int index = vectorizedLength; index < epilogueLength; index += 4)
            {
                for (int lane = 0; lane < 4; lane++)
                {
                    epilogue[lane] = values[index + lane] + epilogue[lane];
                }
            }

            sum = ReduceFourLanes(epilogue);
        }

        for (int index = epilogueLength; index < values.Length; index++)
        {
            sum = values[index] + sum;
        }

        return sum;
    }

    private static double ReduceSixteenLanes(ReadOnlySpan<double> values)
    {
        double lane0 = (values[4] + values[0]) + (values[12] + values[8]);
        double lane1 = (values[5] + values[1]) + (values[13] + values[9]);
        double lane2 = (values[6] + values[2]) + (values[14] + values[10]);
        double lane3 = (values[7] + values[3]) + (values[15] + values[11]);
        return (lane3 + lane1) + (lane2 + lane0);
    }

    private static double ReduceFourLanes(ReadOnlySpan<double> values)
        => (values[3] + values[1]) + (values[2] + values[0]);

    private static double BlendLevelInUpstreamOrder(
        double original,
        double mean,
        double snr)
        => original + ((snr * (mean - original)) / (SnrThreshold + snr));

    private sealed class VhsVSyncLevelWorkspace
    {
        public double[] SyncSamples { get; private set; } = [];

        public double[] BlankSamples { get; private set; } = [];

        public double[] Deviations { get; private set; } = [];

        public double[] MedianScratch { get; private set; } = [];

        public void EnsureLength(int length)
        {
            if (SyncSamples.Length >= length)
            {
                return;
            }

            double[] syncSamples = GC.AllocateUninitializedArray<double>(length);
            double[] blankSamples = GC.AllocateUninitializedArray<double>(length);
            double[] deviations = GC.AllocateUninitializedArray<double>(length);
            double[] medianScratch = GC.AllocateUninitializedArray<double>(length);
            SyncSamples = syncSamples;
            BlankSamples = blankSamples;
            Deviations = deviations;
            MedianScratch = medianScratch;
        }
    }
}
