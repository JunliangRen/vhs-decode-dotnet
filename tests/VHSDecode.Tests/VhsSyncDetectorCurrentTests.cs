using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsSyncDetectorCurrentTests
{
    private const string CoordinateHash =
        "D09201E5DA03460E830F3302A088524CEB3A1BDC7666B9252EE1D11946DAC37B";

    [Theory(DisplayName = "Current VHS sync detector matches the PR 341 multi-grid oracle")]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentVhsSyncDetectorMatchesPr341MultiGridOracle(bool detectLevels)
    {
        double[] signal = BuildTwoGridSignal();
        double[] original = signal.ToArray();
        var detector = new VhsSyncDetector(
            hSyncLength: 10.0,
            backPorchLength: 8.0,
            lineLength: 100,
            approximateTransition: 3.0);

        VhsSyncDetectionResult result = detector.Detect(
            signal,
            detectLevels,
            syncTipEstimate: -5.0,
            blankingEstimate: 100.0);

        Assert.Equal(original, signal);
        Assert.Equal(70, result.Pulses.Count);
        Assert.Equal(CoordinateHash, HashCoordinates(result.Pulses));
        Assert.Equal(
            unchecked((long)0xBFCC8DC8DC8DC8DDUL),
            BitConverter.DoubleToInt64Bits(result.SyncTipLevel));
        Assert.Equal(
            unchecked((long)0x4059000000000000UL),
            BitConverter.DoubleToInt64Bits(result.BlankLevel));
        Assert.Equal(
            unchecked((long)0x4007C570F48CACE1UL),
            BitConverter.DoubleToInt64Bits(result.Pulses[0].Transition));
    }

    [Fact(DisplayName = "Current VHS sync detector preserves estimates when no pulse exists")]
    public void CurrentVhsSyncDetectorPreservesEstimatesWhenNoPulseExists()
    {
        var detector = new VhsSyncDetector(10.0, 8.0, 100, 3.0);

        VhsSyncDetectionResult result = detector.Detect(
            Enumerable.Repeat(42.0, 1000).ToArray(),
            detectLevels: false,
            syncTipEstimate: -5.25,
            blankingEstimate: 101.5);

        Assert.Empty(result.Pulses);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(-5.25),
            BitConverter.DoubleToInt64Bits(result.SyncTipLevel));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(101.5),
            BitConverter.DoubleToInt64Bits(result.BlankLevel));
    }

    [Fact(DisplayName = "Current VHS sync detector uses the estimate for an empty porch window")]
    public void CurrentVhsSyncDetectorUsesEstimateForEmptyPorchWindow()
    {
        var signal = Enumerable.Repeat(100.0, 1800).ToArray();
        for (int start = 50; start < signal.Length; start += 115)
        {
            PaintPulse(signal, start, Math.Min(10, signal.Length - start), -2.0);
        }

        var detector = new VhsSyncDetector(
            hSyncLength: 10.0,
            backPorchLength: 110.0,
            lineLength: 100,
            approximateTransition: 3.0);

        VhsSyncDetectionResult result = detector.Detect(
            signal,
            detectLevels: false,
            syncTipEstimate: -5.0,
            blankingEstimate: 100.0);

        Assert.NotEmpty(result.Pulses);
        Assert.True(double.IsFinite(result.BlankLevel));
        Assert.Equal(100.0, result.BlankLevel);
    }

    [Fact(DisplayName = "Current VHS level reduction matches Numba fastmath order")]
    public void CurrentVhsLevelReductionMatchesNumbaFastMathOrder()
    {
        const int Count = 37;
        var syncLevels = new double[Count];
        var porchLevels = new double[Count];
        var selected = new bool[Count];
        for (int index = 0; index < Count; index++)
        {
            syncLevels[index] =
                3_800_000.0 + (((index * 37) % 19) * 0.123456789) + (index * 1e-7);
            porchLevels[index] =
                4_130_000.0 + (((index * 29) % 23) * 0.234567891) - (index * 2e-7);
            selected[index] = index % 5 != 1 && index is not 8 and not 24;
        }

        (double syncSum, double porchSum) =
            VhsSyncDetector.SumSelectedLevelsInUpstreamOrder(
                syncLevels,
                porchLevels,
                selected,
                Count);

        Assert.Equal(
            unchecked((long)0x41987635748B1C79UL),
            BitConverter.DoubleToInt64Bits(syncSum));
        Assert.Equal(
            unchecked((long)0x419A9608D3D9F8B2UL),
            BitConverter.DoubleToInt64Bits(porchSum));
    }

    [Fact(DisplayName = "Current VHS level reduction clears its stack accumulators")]
    public void CurrentVhsLevelReductionClearsItsStackAccumulators()
    {
        double[] syncLevels = Enumerable.Range(0, 32)
            .Select(static index => 3_800_000.0 + index)
            .ToArray();
        double[] porchLevels = Enumerable.Range(0, 32)
            .Select(static index => 4_100_000.0 + index)
            .ToArray();
        bool[] selected = Enumerable.Repeat(true, 32).ToArray();

        for (int iteration = 0; iteration < 16; iteration++)
        {
            _ = PoisonReductionStack();
            (double syncSum, double porchSum) =
                VhsSyncDetector.SumSelectedLevelsInUpstreamOrder(
                    syncLevels,
                    porchLevels,
                    selected,
                    selected.Length);

            Assert.Equal(121_600_496.0, syncSum);
            Assert.Equal(131_200_496.0, porchSum);
        }
    }

    [Fact(DisplayName = "Current VHS symmetric grid counting matches the ordered-pair oracle")]
    public void CurrentVhsSymmetricGridCountingMatchesOrderedPairOracle()
    {
        const int Count = 313;
        const double EffectiveLineLength = 2_944.75;
        const double JitterTolerance = 256.0;
        var falls = new int[Count];
        for (int index = 1; index < falls.Length; index++)
        {
            int spacing = index % 17 == 0
                ? 0
                : 2_560 + (((index * 37) % 401) - 200);
            falls[index] = falls[index - 1] + spacing;
        }

        int[] expected = CountGridSupportOrderedPairReference(
            falls,
            EffectiveLineLength,
            JitterTolerance);
        var actual = Enumerable.Repeat(int.MinValue, Count + 3).ToArray();

        VhsSyncDetector.FillOrderedGridSupportCounts(
            falls,
            Count,
            EffectiveLineLength,
            JitterTolerance,
            actual);

        Assert.Equal(expected, actual[..Count]);
        Assert.All(actual[Count..], value => Assert.Equal(int.MinValue, value));
    }

    [Fact(DisplayName = "Current VHS sync detector reuses its full-field workspace")]
    public void CurrentVhsSyncDetectorReusesFullFieldWorkspace()
    {
        var signal = new double[1_000_000];
        Array.Fill(signal, 42.0);
        var detector = new VhsSyncDetector(188.0, 152.0, 2560, 8.8);
        _ = detector.Detect(
            signal,
            detectLevels: true,
            syncTipEstimate: 3_800_000.0,
            blankingEstimate: 4_100_000.0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        VhsSyncDetectionResult result = detector.Detect(
            signal,
            detectLevels: true,
            syncTipEstimate: 3_800_000.0,
            blankingEstimate: 4_100_000.0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(result.Pulses);
        Assert.InRange(allocated, 0, 64 * 1024);
    }

    [Fact(DisplayName = "Parallel current VHS sync detector reuses radix workspaces without caller allocation")]
    public void ParallelCurrentVhsSyncDetectorReusesRadixWorkspacesWithoutCallerAllocation()
    {
        var signal = new double[1_000_000];
        Array.Fill(signal, 42.0);
        var detector = new VhsSyncDetector(
            188.0,
            152.0,
            2_560,
            8.8,
            workerThreads: 20);
        _ = detector.Detect(
            signal,
            detectLevels: true,
            syncTipEstimate: 3_800_000.0,
            blankingEstimate: 4_100_000.0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        VhsSyncDetectionResult result = detector.Detect(
            signal,
            detectLevels: true,
            syncTipEstimate: 3_800_000.0,
            blankingEstimate: 4_100_000.0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(result.Pulses);
        Assert.InRange(allocated, 0, 256 * 1024);
    }

    [Fact(DisplayName = "Current VHS level quantiles match sequential Quickselect for finite values")]
    public void CurrentVhsLevelQuantilesMatchSequentialQuickselectForFiniteValues()
    {
        var random = new Random(4_315_520);
        for (int iteration = 0; iteration < 256; iteration++)
        {
            int length = 33 + random.Next(8_192);
            var source = new double[length];
            for (int index = 0; index < source.Length; index++)
            {
                source[index] = index % 17 == 0
                    ? 4_100_000.0
                    : 3_700_000.0 + (random.NextDouble() * 600_000.0);
            }

            int syncTarget = (int)(length * 0.05);
            int blankingTarget = (int)(length * 0.25);
            double[] expectedWork = source.ToArray();
            double[] actualWork = source.ToArray();
            (double expectedSync, double expectedBlanking) =
                VhsSyncDetector.SelectLevelQuantilesSequential(
                    expectedWork,
                    syncTarget,
                    blankingTarget,
                    length);
            (double actualSync, double actualBlanking) =
                VhsSyncDetector.SelectLevelQuantiles(
                    actualWork,
                    syncTarget,
                    blankingTarget,
                    length);

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedSync),
                BitConverter.DoubleToInt64Bits(actualSync));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedBlanking),
                BitConverter.DoubleToInt64Bits(actualBlanking));
        }
    }

    [Fact(DisplayName = "Current VHS level quantiles preserve exceptional-value fallback")]
    public void CurrentVhsLevelQuantilesPreserveExceptionalValueFallback()
    {
        long[] exceptionalBits =
        [
            0,
            unchecked((long)0x8000000000000000UL),
            unchecked((long)0x7FF0000000000000UL),
            unchecked((long)0xFFF0000000000000UL),
            unchecked((long)0x7FF8000000000001UL),
            unchecked((long)0xFFF8000000000001UL)
        ];
        var random = new Random(4_315_520);
        for (int iteration = 0; iteration < 128; iteration++)
        {
            int length = 33 + random.Next(4_096);
            var source = new double[length];
            for (int index = 0; index < source.Length; index++)
            {
                source[index] = BitConverter.Int64BitsToDouble(random.NextInt64());
            }

            source[iteration % length] = BitConverter.Int64BitsToDouble(
                exceptionalBits[iteration % exceptionalBits.Length]);
            int syncTarget = (int)(length * 0.05);
            int blankingTarget = (int)(length * 0.25);
            double[] expectedWork = source.ToArray();
            double[] actualWork = source.ToArray();
            (double expectedSync, double expectedBlanking) =
                VhsSyncDetector.SelectLevelQuantilesSequential(
                    expectedWork,
                    syncTarget,
                    blankingTarget,
                    length);
            (double actualSync, double actualBlanking) =
                VhsSyncDetector.SelectLevelQuantiles(
                    actualWork,
                    syncTarget,
                    blankingTarget,
                    length);

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedSync),
                BitConverter.DoubleToInt64Bits(actualSync));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedBlanking),
                BitConverter.DoubleToInt64Bits(actualBlanking));
            Assert.Equal(
                expectedWork.Select(BitConverter.DoubleToInt64Bits),
                actualWork.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    [Fact(DisplayName = "Current VHS radix level quantiles match retained Quickselect bit for bit")]
    public void CurrentVhsRadixLevelQuantilesMatchRetainedQuickselectBitForBit()
    {
        var random = new Random(34_104_315);
        int[] lengths =
        [
            1, 2, 3, 4, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65,
            127, 128, 129, 257, 1_024, 8_192
        ];
        foreach (int length in lengths)
        {
            for (int iteration = 0; iteration < 64; iteration++)
            {
                var source = new double[length];
                for (int index = 0; index < source.Length; index++)
                {
                    source[index] = index % 17 == 0
                        ? 4_100_000.0
                        : (random.Next(2) == 0 ? -1.0 : 1.0)
                            * (1.0 + (random.NextDouble() * 10_000_000.0));
                }

                double[] original = source.ToArray();
                int syncTarget = (int)(length * 0.05);
                int blankingTarget = (int)(length * 0.25);
                double[] expectedWork = source.ToArray();
                double[] actualWork = new double[length];
                var highHistogram = new int[VhsSyncDetector.RadixHistogramWidth];
                var middleHistograms = new int[VhsSyncDetector.RadixHistogramWidth * 2];
                (double expectedSync, double expectedBlanking) =
                    VhsSyncDetector.SelectLevelQuantiles(
                        expectedWork,
                        syncTarget,
                        blankingTarget,
                        length);
                (double actualSync, double actualBlanking) =
                    VhsSyncDetector.SelectLevelQuantilesRadix(
                        source,
                        actualWork,
                        highHistogram,
                        middleHistograms,
                        syncTarget,
                        blankingTarget);

                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expectedSync),
                    BitConverter.DoubleToInt64Bits(actualSync));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expectedBlanking),
                    BitConverter.DoubleToInt64Bits(actualBlanking));
                Assert.Equal(original, source);
            }
        }
    }

    [Fact(DisplayName = "Current VHS radix level quantiles preserve exceptional fallback workspace")]
    public void CurrentVhsRadixLevelQuantilesPreserveExceptionalFallbackWorkspace()
    {
        long[] exceptionalBits =
        [
            0,
            unchecked((long)0x8000000000000000UL),
            unchecked((long)0x7FF0000000000000UL),
            unchecked((long)0xFFF0000000000000UL),
            unchecked((long)0x7FF8000000000001UL),
            unchecked((long)0xFFF8000000000042UL)
        ];
        var random = new Random(341);
        var highHistogram = new int[VhsSyncDetector.RadixHistogramWidth];
        var middleHistograms = new int[VhsSyncDetector.RadixHistogramWidth * 2];
        for (int iteration = 0; iteration < 128; iteration++)
        {
            int length = 33 + random.Next(4_096);
            var source = new double[length];
            for (int index = 0; index < source.Length; index++)
            {
                source[index] = 3_700_000.0 + (random.NextDouble() * 600_000.0);
            }

            source[iteration % length] = BitConverter.Int64BitsToDouble(
                exceptionalBits[iteration % exceptionalBits.Length]);
            int syncTarget = (int)(length * 0.05);
            int blankingTarget = (int)(length * 0.25);
            double[] expectedWork = source.ToArray();
            double[] actualWork = new double[length];
            (double expectedSync, double expectedBlanking) =
                VhsSyncDetector.SelectLevelQuantiles(
                    expectedWork,
                    syncTarget,
                    blankingTarget,
                    length);
            (double actualSync, double actualBlanking) =
                VhsSyncDetector.SelectLevelQuantilesRadix(
                    source,
                    actualWork,
                    highHistogram,
                    middleHistograms,
                    syncTarget,
                    blankingTarget);

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedSync),
                BitConverter.DoubleToInt64Bits(actualSync));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedBlanking),
                BitConverter.DoubleToInt64Bits(actualBlanking));
            Assert.Equal(
                expectedWork.Select(BitConverter.DoubleToInt64Bits),
                actualWork.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    [Fact(DisplayName = "Parallel current VHS radix quantiles match serial bit for bit")]
    public void ParallelCurrentVhsRadixQuantilesMatchSerialBitForBit()
    {
        const int length = 600_013;
        var random = new Random(34_104_315);
        double[][] sources =
        [
            Enumerable.Range(0, length)
                .Select(index => index % 19 == 0
                    ? 4_100_000.0
                    : 4_000_000.0 + random.NextDouble())
                .ToArray(),
            Enumerable.Range(0, length)
                .Select(index => index % 23 == 0
                    ? -7_500_000.0
                    : (random.Next(2) == 0 ? -1.0 : 1.0)
                        * (1.0 + (random.NextDouble() * 10_000_000.0)))
                .ToArray()
        ];
        const int maximumWorkers = 20;
        var workerHistograms = Enumerable.Repeat(
            int.MinValue,
            maximumWorkers * VhsSyncDetector.RadixHistogramWidth * 2).ToArray();
        var workerFlags = Enumerable.Repeat(int.MinValue, maximumWorkers).ToArray();

        foreach (double[] source in sources)
        {
            var backing = new double[source.Length + 17];
            source.CopyTo(backing, 0);
            for (int index = source.Length; index < backing.Length; index++)
            {
                backing[index] = index % 3 switch
                {
                    0 => double.NaN,
                    1 => 0.0,
                    _ => double.MaxValue
                };
            }

            int syncTarget = (int)(source.Length * 0.05);
            int blankingTarget = (int)(source.Length * 0.25);
            var expectedScratch = new double[source.Length];
            (double expectedSync, double expectedBlanking) =
                VhsSyncDetector.SelectLevelQuantilesRadix(
                    source,
                    expectedScratch,
                    new int[VhsSyncDetector.RadixHistogramWidth],
                    new int[VhsSyncDetector.RadixHistogramWidth * 2],
                    syncTarget,
                    blankingTarget);

            foreach (int workers in new[] { 2, 3, 4, 5, 10, 20 })
            {
                var actualScratch = new double[source.Length];
                (double actualSync, double actualBlanking) =
                    VhsSyncDetector.SelectLevelQuantilesRadixParallel(
                        backing,
                        source.Length,
                        actualScratch,
                        new int[VhsSyncDetector.RadixHistogramWidth],
                        new int[VhsSyncDetector.RadixHistogramWidth * 2],
                        workerHistograms,
                        workerFlags,
                        syncTarget,
                        blankingTarget,
                        workers);

                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expectedSync),
                    BitConverter.DoubleToInt64Bits(actualSync));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expectedBlanking),
                    BitConverter.DoubleToInt64Bits(actualBlanking));
                Assert.All(
                    workerFlags.AsSpan(0, workers).ToArray(),
                    flag => Assert.Equal(0, flag));

                var denseScratch = new double[source.Length];
                (double denseSync, double denseBlanking) =
                    VhsSyncDetector.SelectLevelQuantilesRadixParallel(
                        backing,
                        source.Length,
                        denseScratch,
                        new int[VhsSyncDetector.RadixHistogramWidth],
                        new int[VhsSyncDetector.RadixHistogramWidth * 2],
                        workerHistograms,
                        workerFlags,
                        syncTarget,
                        blankingTarget,
                        workers,
                        useCompactParallelRadix: false);

                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expectedSync),
                    BitConverter.DoubleToInt64Bits(denseSync));
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expectedBlanking),
                    BitConverter.DoubleToInt64Bits(denseBlanking));
                Assert.All(
                    workerFlags.AsSpan(0, workers).ToArray(),
                    flag => Assert.Equal(0, flag));
            }
        }
    }

    [Fact(DisplayName = "Parallel current VHS radix quantiles preserve exceptional fallback")]
    public void ParallelCurrentVhsRadixQuantilesPreserveExceptionalFallback()
    {
        const int workers = 4;
        const int length = 2_048;
        long[] exceptionalBits =
        [
            0,
            unchecked((long)0x8000000000000000UL),
            unchecked((long)0x7FF0000000000000UL),
            unchecked((long)0xFFF0000000000000UL),
            unchecked((long)0x7FF8000000000042UL),
            unchecked((long)0xFFF8000000000042UL)
        ];
        int[] exceptionalIndexes = [0, 511, 512, 1_023, 1_024, 1_535, 1_536, 2_047];
        foreach (long exceptionalBitsValue in exceptionalBits)
        {
            var source = Enumerable.Range(0, length)
                .Select(index => 3_700_000.0 + (index * 0.125))
                .ToArray();
            foreach (int index in exceptionalIndexes)
            {
                source[index] = BitConverter.Int64BitsToDouble(exceptionalBitsValue);
            }

            int syncTarget = (int)(length * 0.05);
            int blankingTarget = (int)(length * 0.25);
            double[] expectedScratch = source.ToArray();
            (double expectedSync, double expectedBlanking) =
                VhsSyncDetector.SelectLevelQuantilesSequential(
                    expectedScratch,
                    syncTarget,
                    blankingTarget,
                    length);
            var actualScratch = Enumerable.Repeat(double.NaN, length).ToArray();
            var workerHistograms = Enumerable.Repeat(
                int.MinValue,
                workers * VhsSyncDetector.RadixHistogramWidth * 2).ToArray();
            var workerFlags = Enumerable.Repeat(int.MinValue, workers).ToArray();

            (double actualSync, double actualBlanking) =
                VhsSyncDetector.SelectLevelQuantilesRadixParallel(
                    source,
                    source.Length,
                    actualScratch,
                    new int[VhsSyncDetector.RadixHistogramWidth],
                    new int[VhsSyncDetector.RadixHistogramWidth * 2],
                    workerHistograms,
                    workerFlags,
                    syncTarget,
                    blankingTarget,
                    workers);

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedSync),
                BitConverter.DoubleToInt64Bits(actualSync));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedBlanking),
                BitConverter.DoubleToInt64Bits(actualBlanking));
            Assert.Equal(
                expectedScratch.Select(BitConverter.DoubleToInt64Bits),
                actualScratch.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    [Theory(DisplayName = "Parallel current VHS sync preprocessing matches serial detection across partitions")]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ParallelCurrentVhsSyncPreprocessingMatchesSerialDetectionAcrossPartitions(
        int workers)
    {
        const int LineLength = 2_500;
        var signal = new double[1_200_001];
        for (int index = 0; index < signal.Length; index++)
        {
            signal[index] =
                100.0 + ((((index * 37) % 17) - 8) * 0.125);
        }

        for (int start = 2_437;
            start + 188 < signal.Length;
            start += LineLength)
        {
            PaintPulse(signal, start, 188, -2.0);
        }

        var serialDetector = new VhsSyncDetector(
            188.0,
            152.0,
            LineLength,
            8.8,
            workerThreads: 1);
        var parallelDetector = new VhsSyncDetector(
            188.0,
            152.0,
            LineLength,
            8.8,
            workerThreads: workers);
        var initialScanOnlyDetector = new VhsSyncDetector(
            188.0,
            152.0,
            LineLength,
            8.8,
            workerThreads: workers,
            parallelizePreciseEdgeScan: false);

        VhsSyncDetectionResult expected = serialDetector.Detect(
            signal,
            detectLevels: true,
            syncTipEstimate: -5.0,
            blankingEstimate: 100.0);
        VhsSyncDetectionResult actual = parallelDetector.Detect(
            signal,
            detectLevels: true,
            syncTipEstimate: -5.0,
            blankingEstimate: 100.0);
        VhsSyncDetectionResult initialScanOnly = initialScanOnlyDetector.Detect(
            signal,
            detectLevels: true,
            syncTipEstimate: -5.0,
            blankingEstimate: 100.0);

        int scanLimit = signal.Length - 1;
        for (int partition = 1; partition < workers; partition++)
        {
            int boundary = (int)(((long)scanLimit * partition) / workers);
            Assert.Contains(
                expected.Pulses,
                pulse => pulse.Start < boundary
                         && pulse.Start + pulse.Length > boundary);
        }
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.SyncTipLevel),
            BitConverter.DoubleToInt64Bits(actual.SyncTipLevel));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.BlankLevel),
            BitConverter.DoubleToInt64Bits(actual.BlankLevel));
        Assert.Equal(expected.Pulses.Count, actual.Pulses.Count);
        Assert.Equal(expected.Pulses, initialScanOnly.Pulses);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.SyncTipLevel),
            BitConverter.DoubleToInt64Bits(initialScanOnly.SyncTipLevel));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.BlankLevel),
            BitConverter.DoubleToInt64Bits(initialScanOnly.BlankLevel));
        for (int index = 0; index < expected.Pulses.Count; index++)
        {
            VhsMeasuredSyncPulse expectedPulse = expected.Pulses[index];
            VhsMeasuredSyncPulse actualPulse = actual.Pulses[index];
            Assert.Equal(expectedPulse.Start, actualPulse.Start);
            Assert.Equal(expectedPulse.Length, actualPulse.Length);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedPulse.Transition),
                BitConverter.DoubleToInt64Bits(actualPulse.Transition));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedPulse.SyncLevel),
                BitConverter.DoubleToInt64Bits(actualPulse.SyncLevel));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedPulse.BlankLevel),
                BitConverter.DoubleToInt64Bits(actualPulse.BlankLevel));
        }

        var saturatedOverlapDetector = new VhsSyncDetector(
            double.MaxValue,
            152.0,
            LineLength,
            8.8,
            workerThreads: workers);
        Assert.Empty(saturatedOverlapDetector.Detect(
            new double[65_536],
            detectLevels: false,
            syncTipEstimate: -5.0,
            blankingEstimate: 100.0).Pulses);
    }

    [Fact(DisplayName = "Parallel precise sync crossing buffers preserve index zero and report overflow")]
    public void ParallelPreciseSyncCrossingBuffersPreserveIndexZeroAndReportOverflow()
    {
        double[] filtered = [1.0, -1.0, 1.0, -1.0, 1.0];
        var crossings = new List<int>();

        Assert.False(VhsSyncDetector.TryFillThresholdCrossingsPartition(
            filtered,
            start: 0,
            end: filtered.Length - 1,
            threshold: 0.0,
            maximumCrossings: 2,
            crossings));
        Assert.Equal([0, ~1], crossings);

        crossings.Clear();
        Assert.True(VhsSyncDetector.TryFillThresholdCrossingsPartition(
            filtered,
            start: 0,
            end: filtered.Length - 1,
            threshold: 0.0,
            maximumCrossings: 4,
            crossings));
        Assert.Equal([0, ~1, 2, ~3], crossings);
    }

    [Theory(DisplayName = "Current VHS boxcar convolution matches NumPy same mode")]
    [MemberData(nameof(ConvolutionCases))]
    public void CurrentVhsBoxcarConvolutionMatchesNumpySameMode(
        double[] input,
        int windowSize,
        long[] expectedBits)
    {
        double[] actual = VhsSyncDetector.ConvolveBoxcarSame(input, windowSize);

        Assert.Equal(
            expectedBits,
            actual.Select(BitConverter.DoubleToInt64Bits).ToArray());
    }

    [Theory(DisplayName = "Parallel nine-tap VHS boxcar matches serial output bit for bit")]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ParallelNineTapVhsBoxcarMatchesSerialOutputBitForBit(int workers)
    {
        foreach (int length in new[] { 9, 10, 10_003 })
        {
            var input = new double[length];
            for (int index = 0; index < input.Length; index++)
            {
                input[index] =
                    Math.Sin(index * 0.017)
                    + (Math.Cos(index * 0.031) * 0.25)
                    + (((index * 37) % 23) * 0.125);
            }

            AssertParallelNineTapMatchesScalar(input, workers);
        }

        AssertParallelNineTapMatchesScalar(
            [
                -0.0,
                0.0,
                double.Epsilon,
                -double.Epsilon,
                BitConverter.Int64BitsToDouble(0x000F_FFFF_FFFF_FFFF),
                BitConverter.Int64BitsToDouble(unchecked((long)0x800F_FFFF_FFFF_FFFFUL)),
                BitConverter.Int64BitsToDouble(0x0010_0000_0000_0000),
                BitConverter.Int64BitsToDouble(unchecked((long)0x8010_0000_0000_0000UL)),
                double.MaxValue,
                -double.MaxValue,
                double.PositiveInfinity,
                double.NegativeInfinity,
                BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8_0000_0000_1234UL)),
                BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_5678UL)),
                BitConverter.Int64BitsToDouble(unchecked((long)0x7FF0_0000_0000_0001UL)),
                1.0,
                -1.0,
                -0.0,
                0.0,
                double.Epsilon
            ],
            workers);
    }

    public static TheoryData<double[], int, long[]> ConvolutionCases => new()
    {
        {
            [1.0, 2.0, 3.0, 4.0, 5.0],
            3,
            [
                unchecked((long)0x3FF0000000000000UL),
                unchecked((long)0x4000000000000000UL),
                unchecked((long)0x4008000000000000UL),
                unchecked((long)0x400FFFFFFFFFFFFFUL),
                unchecked((long)0x4008000000000000UL)
            ]
        },
        {
            [1.0, 2.0],
            5,
            [
                unchecked((long)0x3FC999999999999AUL),
                unchecked((long)0x3FE3333333333334UL),
                unchecked((long)0x3FE3333333333334UL),
                unchecked((long)0x3FE3333333333334UL),
                unchecked((long)0x3FE3333333333334UL)
            ]
        },
        {
            [0.1, -0.2, 0.3, -0.4, 0.5],
            3,
            [
                unchecked((long)0xBFA1111111111111UL),
                unchecked((long)0x3FB1111111111110UL),
                unchecked((long)0xBFB999999999999AUL),
                unchecked((long)0x3FC1111111111110UL),
                unchecked((long)0x3FA1111111111110UL)
            ]
        }
    };

    private static double[] ConvolveBoxcarNineTapScalar(double[] input)
    {
        const int HalfWindow = 4;
        const double Scale = 1.0 / 9.0;
        var output = new double[input.Length];
        for (int outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            int sourceStart = Math.Max(0, outputIndex - HalfWindow);
            int sourceEnd = Math.Min(input.Length - 1, outputIndex + HalfWindow);
            double sum = 0.0;
            for (int sourceIndex = sourceStart; sourceIndex <= sourceEnd; sourceIndex++)
            {
                sum += input[sourceIndex] * Scale;
            }

            output[outputIndex] = sum;
        }

        return output;
    }

    private static void AssertParallelNineTapMatchesScalar(double[] input, int workers)
    {
        double[] expected = ConvolveBoxcarNineTapScalar(input);
        var actual = new double[expected.Length];
        VhsSyncDetector.ConvolveBoxcarSameParallel(
            input,
            windowSize: 9,
            actual,
            actual.Length,
            workers);

        Assert.Equal(
            expected.Select(BitConverter.DoubleToInt64Bits),
            actual.Select(BitConverter.DoubleToInt64Bits));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VhsSyncDetector.ConvolveBoxcarSameParallel(
                input,
                windowSize: 9,
                actual,
                actual.Length + 1,
                workers));
    }

    private static double[] BuildTwoGridSignal()
    {
        var signal = new double[7000];
        for (int index = 0; index < signal.Length; index++)
        {
            signal[index] = 100.0 + ((((index * 37) % 17) - 8) * 0.125);
        }

        int pulseIndex = 0;
        for (int start = 50; start <= 3250; start += 100)
        {
            PaintPulse(signal, start, 10, pulseIndex == 12 ? -45.0 : -2.0);
            pulseIndex++;
        }

        pulseIndex = 0;
        for (int start = 3390; start <= 6790; start += 100)
        {
            PaintPulse(signal, start, 10, pulseIndex == 7 ? 35.0 : 1.5);
            pulseIndex++;
        }

        PaintPulse(signal, 1200, 3, -10.0);
        PaintPulse(signal, 2500, 20, -5.0);
        PaintPulse(signal, 5100, 5, -20.0);
        return signal;
    }

    private static void PaintPulse(double[] signal, int start, int length, double level)
        => Array.Fill(signal, level, start, length);

    private static int[] CountGridSupportOrderedPairReference(
        IReadOnlyList<int> falls,
        double effectiveLineLength,
        double jitterTolerance)
    {
        var counts = new int[falls.Count];
        for (int first = 0; first < falls.Count; first++)
        {
            int connections = 1;
            for (int second = 0; second < falls.Count; second++)
            {
                if (first == second)
                {
                    continue;
                }

                int delta = second > first
                    ? falls[second] - falls[first]
                    : falls[first] - falls[second];
                double remainder = delta % effectiveLineLength;
                if (remainder < jitterTolerance
                    || remainder > effectiveLineLength - jitterTolerance)
                {
                    connections++;
                }
            }

            counts[first] = connections;
        }

        return counts;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double PoisonReductionStack()
    {
        Span<double> values = stackalloc double[32];
        values.Fill(double.NaN);
        return values[0];
    }

    private static string HashCoordinates(IReadOnlyList<VhsMeasuredSyncPulse> pulses)
    {
        var bytes = new byte[pulses.Count * 2 * sizeof(long)];
        for (int index = 0; index < pulses.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                bytes.AsSpan(index * 2 * sizeof(long), sizeof(long)),
                pulses[index].Start);
            BinaryPrimitives.WriteInt64LittleEndian(
                bytes.AsSpan((index * 2 * sizeof(long)) + sizeof(long), sizeof(long)),
                pulses[index].Length);
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
