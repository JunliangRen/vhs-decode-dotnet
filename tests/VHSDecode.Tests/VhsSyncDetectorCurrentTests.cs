using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsSyncDetectorCurrentTests
{
    private const string CoordinateHash =
        "D09201E5DA03460E830F3302A088524CEB3A1BDC7666B9252EE1D11946DAC37B";

    [Theory(DisplayName = "Current VHS sync worker policy widens only eligible high-thread runs")]
    [InlineData(0, true, 1)]
    [InlineData(5, true, 4)]
    [InlineData(8, true, 8)]
    [InlineData(20, true, 8)]
    [InlineData(20, false, 4)]
    public void CurrentVhsSyncWorkerPolicyWidensOnlyEligibleHighThreadRuns(
        int requestedWorkers,
        bool useWideParallelPreprocessing,
        int expectedWorkers)
    {
        Assert.Equal(
            expectedWorkers,
            VhsSyncDetector.ResolveParallelWorkerCount(
                requestedWorkers,
                useWideParallelPreprocessing));
    }

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

    [Theory(DisplayName = "Approx float32 VHS sync detection preserves widened-input structure")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ApproxFloat32VhsSyncDetectionPreservesWidenedInputStructure(
        bool detectLevels,
        bool allowAvx)
    {
        float[] signal = BuildPeriodicSignal(300_000, 2_560, 188)
            .Select(static value => (float)value)
            .ToArray();
        double[] widened = signal.Select(static value => (double)value).ToArray();
        var expectedDetector = new VhsSyncDetector(188.0, 152.0, 2_560, 8.8);
        var actualDetector = new VhsSyncDetector(188.0, 152.0, 2_560, 8.8);

        VhsSyncDetectionResult expected = expectedDetector.Detect(
            widened,
            detectLevels,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0);
        VhsSyncDetectionResult actual = actualDetector.DetectApproxFloat32(
            signal,
            detectLevels,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0,
            allowAvx);

        Assert.NotEmpty(expected.Pulses);
        Assert.Equal(expected.Pulses.Count, actual.Pulses.Count);
        Assert.InRange(Math.Abs(expected.SyncTipLevel - actual.SyncTipLevel), 0.0, 0.001);
        Assert.InRange(Math.Abs(expected.BlankLevel - actual.BlankLevel), 0.0, 0.001);
        for (int index = 0; index < expected.Pulses.Count; index++)
        {
            VhsMeasuredSyncPulse expectedPulse = expected.Pulses[index];
            VhsMeasuredSyncPulse actualPulse = actual.Pulses[index];
            Assert.Equal(expectedPulse.Start, actualPulse.Start);
            Assert.Equal(expectedPulse.Length, actualPulse.Length);
            Assert.InRange(
                Math.Abs(expectedPulse.Transition - actualPulse.Transition),
                0.0,
                0.001);
            Assert.InRange(
                Math.Abs(expectedPulse.SyncLevel - actualPulse.SyncLevel),
                0.0,
                0.001);
            Assert.InRange(
                Math.Abs(expectedPulse.BlankLevel - actualPulse.BlankLevel),
                0.0,
                0.001);
        }
    }

    [Theory(DisplayName = "Parallel Approx float32 VHS sync detection matches serial bits")]
    [InlineData(false, 2, true, true, true)]
    [InlineData(false, 20, true, true, true)]
    [InlineData(false, 20, true, false, false)]
    [InlineData(true, 20, true, true, true)]
    [InlineData(true, 20, false, true, true)]
    [InlineData(true, 20, true, false, false)]
    public void ParallelApproxFloat32VhsSyncDetectionMatchesSerialBits(
        bool detectLevels,
        int workerThreads,
        bool allowAvx,
        bool parallelizePreciseEdgeScan,
        bool useCompactParallelRadix)
    {
        int signalLength = detectLevels ? 600_000 : 300_000;
        float[] signal = BuildPeriodicSignal(signalLength, 2_560, 188)
            .Select(static value => (float)value)
            .ToArray();
        var serial = new VhsSyncDetector(
            188.0,
            152.0,
            2_560,
            8.8,
            workerThreads: 1);
        var parallel = new VhsSyncDetector(
            188.0,
            152.0,
            2_560,
            8.8,
            workerThreads,
            parallelizePreciseEdgeScan,
            useCompactParallelRadix);

        VhsSyncDetectionResult expected = serial.DetectApproxFloat32(
            signal.AsSpan(),
            detectLevels,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0,
            allowAvx);
        VhsSyncDetectionResult first = parallel.DetectApproxFloat32(
            signal,
            detectLevels,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0,
            allowAvx);
        VhsSyncDetectionResult second = parallel.DetectApproxFloat32(
            signal,
            detectLevels,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0,
            allowAvx);

        Assert.NotEmpty(expected.Pulses);
        AssertDetectionBitsEqual(expected, first);
        AssertDetectionBitsEqual(first, second);
    }

    [Fact(DisplayName = "Approx float32 radix quantiles match exact widening bit for bit")]
    public void ApproxFloat32RadixQuantilesMatchExactWideningBitForBit()
    {
        var random = new Random(431_552);
        int[] highHistogram = new int[VhsSyncDetector.RadixHistogramWidth];
        int[] middleHistograms = new int[VhsSyncDetector.RadixHistogramWidth * 2];
        foreach (int length in new[] { 33, 257, 1_024, 8_193 })
        {
            for (int iteration = 0; iteration < 32; iteration++)
            {
                var source = new float[length];
                for (int index = 0; index < source.Length; index++)
                {
                    source[index] = index % 17 == 0
                        ? 4_100_000.0f
                        : (random.Next(2) == 0 ? -1.0f : 1.0f)
                            * (1.0f + (random.NextSingle() * 10_000_000.0f));
                }

                double[] widened = Array.ConvertAll(
                    source,
                    static value => (double)value);
                int syncTarget = (int)(length * 0.05);
                int blankingTarget = (int)(length * 0.25);
                (double expectedSync, double expectedBlanking) =
                    VhsSyncDetector.SelectLevelQuantilesRadix(
                        widened,
                        new double[length],
                        highHistogram,
                        middleHistograms,
                        syncTarget,
                        blankingTarget);
                (double actualSync, double actualBlanking) =
                    VhsSyncDetector.SelectLevelQuantilesRadixFloat32(
                        source,
                        new double[length],
                        new double[length],
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
            }
        }
    }

    [Fact(DisplayName = "Approx float32 radix quantiles preserve signed parent and child boundaries bit for bit")]
    public void ApproxFloat32RadixQuantilesPreserveSignedParentAndChildBoundariesBitForBit()
    {
        const int Length = 4_096;
        uint[] bitPatterns =
        [
            0xFF7F_FFFFU,
            0xC080_0800U,
            0xC080_07FFU,
            0xC080_0400U,
            0xC080_03FFU,
            0xBF80_0800U,
            0xBF80_07FFU,
            0xBF80_0400U,
            0xBF80_03FFU,
            0xBF80_0000U,
            0x8080_0000U,
            0x807F_FFFFU,
            0x8000_0001U,
            0x0000_0001U,
            0x007F_FFFFU,
            0x0080_0000U,
            0x3F7F_FFFFU,
            0x3F80_0000U,
            0x3F80_03FFU,
            0x3F80_0400U,
            0x3F80_07FFU,
            0x3F80_0800U,
            0x4080_03FFU,
            0x4080_0400U,
            0x4080_07FFU,
            0x4080_0800U,
            0x7F7F_FFFFU
        ];
        float[] source = Enumerable.Range(0, Length)
            .Select(index => BitConverter.UInt32BitsToSingle(
                bitPatterns[(index * 17) % bitPatterns.Length]))
            .ToArray();
        double[] widened = Array.ConvertAll(source, static value => (double)value);

        foreach ((int syncTarget, int blankingTarget) in new[]
        {
            (0, 1),
            (Length / 8, (Length / 8) + 1),
            (Length / 4, (Length * 3) / 4),
            ((Length / 2) - 1, Length / 2),
            (Length - 2, Length - 1)
        })
        {
            (double expectedSync, double expectedBlanking) =
                VhsSyncDetector.SelectLevelQuantilesRadix(
                    widened,
                    new double[Length],
                    new int[VhsSyncDetector.RadixHistogramWidth],
                    new int[VhsSyncDetector.RadixHistogramWidth * 2],
                    syncTarget,
                    blankingTarget);
            (double actualSync, double actualBlanking) =
                VhsSyncDetector.SelectLevelQuantilesRadixFloat32(
                    source,
                    new double[Length],
                    new double[Length],
                    new int[VhsSyncDetector.RadixHistogramWidth],
                    new int[VhsSyncDetector.RadixHistogramWidth * 2],
                    syncTarget,
                    blankingTarget);

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedSync),
                BitConverter.DoubleToInt64Bits(actualSync));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedBlanking),
                BitConverter.DoubleToInt64Bits(actualBlanking));
        }
    }

    [Fact(DisplayName = "Parallel Approx float32 radix quantiles match exact widening and remain deterministic")]
    public void ParallelApproxFloat32RadixQuantilesMatchExactWideningAndRemainDeterministic()
    {
        const int Length = 600_013;
        const int MaximumWorkers = 20;
        var random = new Random(34_104_315);
        float[] source = Enumerable.Range(0, Length)
            .Select(index => index % 19 == 0
                ? 4_100_000.0f
                : (random.Next(2) == 0 ? -1.0f : 1.0f)
                    * (1.0f + (random.NextSingle() * 10_000_000.0f)))
            .ToArray();
        double[] widened = Array.ConvertAll(source, static value => (double)value);
        int syncTarget = (int)(Length * 0.05);
        int blankingTarget = (int)(Length * 0.25);
        (double expectedSync, double expectedBlanking) =
            VhsSyncDetector.SelectLevelQuantilesRadix(
                widened,
                new double[Length],
                new int[VhsSyncDetector.RadixHistogramWidth],
                new int[VhsSyncDetector.RadixHistogramWidth * 2],
                syncTarget,
                blankingTarget);
        var workerHistograms = new int[MaximumWorkers * 4_096];
        var workerFlags = new int[MaximumWorkers];

        foreach (int workers in new[] { 2, 4, 8, 20 })
        {
            (double firstSync, double firstBlanking) =
                VhsSyncDetector.SelectLevelQuantilesRadixFloat32Parallel(
                    source,
                    source.Length,
                    new double[Length],
                    new double[Length],
                    new int[VhsSyncDetector.RadixHistogramWidth],
                    new int[VhsSyncDetector.RadixHistogramWidth * 2],
                    workerHistograms,
                    workerFlags,
                    syncTarget,
                    blankingTarget,
                    workers);
            (double secondSync, double secondBlanking) =
                VhsSyncDetector.SelectLevelQuantilesRadixFloat32Parallel(
                    source,
                    source.Length,
                    new double[Length],
                    new double[Length],
                    new int[VhsSyncDetector.RadixHistogramWidth],
                    new int[VhsSyncDetector.RadixHistogramWidth * 2],
                    workerHistograms,
                    workerFlags,
                    syncTarget,
                    blankingTarget,
                    workers);

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedSync),
                BitConverter.DoubleToInt64Bits(firstSync));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expectedBlanking),
                BitConverter.DoubleToInt64Bits(firstBlanking));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(firstSync),
                BitConverter.DoubleToInt64Bits(secondSync));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(firstBlanking),
                BitConverter.DoubleToInt64Bits(secondBlanking));
            Assert.All(
                workerFlags.AsSpan(0, workers).ToArray(),
                flag => Assert.Equal(0, flag));
        }
    }

    [Theory(DisplayName = "Approx float32 radix quantiles preserve exceptional-value fallback")]
    [InlineData(0x00000000U)]
    [InlineData(0x80000000U)]
    [InlineData(0x7F800000U)]
    [InlineData(0xFF800000U)]
    [InlineData(0x7FC00042U)]
    [InlineData(0xFFC00042U)]
    public void ApproxFloat32RadixQuantilesPreserveExceptionalValueFallback(
        uint exceptionalBits)
    {
        const int Length = 2_048;
        float[] source = Enumerable.Range(0, Length)
            .Select(index => 3_700_000.0f + (index * 0.125f))
            .ToArray();
        source[1_023] = BitConverter.UInt32BitsToSingle(exceptionalBits);
        double[] widened = Array.ConvertAll(source, static value => (double)value);
        int syncTarget = (int)(Length * 0.05);
        int blankingTarget = (int)(Length * 0.25);
        (double expectedSync, double expectedBlanking) =
            VhsSyncDetector.SelectLevelQuantilesRadix(
                widened,
                new double[Length],
                new int[VhsSyncDetector.RadixHistogramWidth],
                new int[VhsSyncDetector.RadixHistogramWidth * 2],
                syncTarget,
                blankingTarget);
        (double sequentialSync, double sequentialBlanking) =
            VhsSyncDetector.SelectLevelQuantilesRadixFloat32(
                source,
                new double[Length],
                new double[Length],
                new int[VhsSyncDetector.RadixHistogramWidth],
                new int[VhsSyncDetector.RadixHistogramWidth * 2],
                syncTarget,
                blankingTarget);
        (double parallelSync, double parallelBlanking) =
            VhsSyncDetector.SelectLevelQuantilesRadixFloat32Parallel(
                source,
                source.Length,
                new double[Length],
                new double[Length],
                new int[VhsSyncDetector.RadixHistogramWidth],
                new int[VhsSyncDetector.RadixHistogramWidth * 2],
                new int[4 * 4_096],
                new int[4],
                syncTarget,
                blankingTarget,
                workerThreads: 4);

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expectedSync),
            BitConverter.DoubleToInt64Bits(sequentialSync));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expectedBlanking),
            BitConverter.DoubleToInt64Bits(sequentialBlanking));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expectedSync),
            BitConverter.DoubleToInt64Bits(parallelSync));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expectedBlanking),
            BitConverter.DoubleToInt64Bits(parallelBlanking));
    }

    [Theory(DisplayName = "Parallel Approx float32 nine-tap VHS boxcar matches serial bits")]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    public void ParallelApproxFloat32NineTapVhsBoxcarMatchesSerialBits(
        int workers,
        bool allowAvx)
    {
        const int Length = 100_003;
        var input = new float[Length];
        for (int index = 0; index < input.Length; index++)
        {
            input[index] =
                MathF.Sin(index * 0.017f)
                + (MathF.Cos(index * 0.031f) * 0.25f)
                + (((index * 37) % 23) * 0.125f);
        }

        float[] expected = VhsSyncDetector.ConvolveBoxcarSameFloat32(
            input,
            windowSize: 9,
            allowAvx);
        var actual = new float[expected.Length];
        VhsSyncDetector.ConvolveBoxcarSameParallelFloat32(
            input,
            windowSize: 9,
            actual,
            actual.Length,
            workers,
            allowAvx);

        Assert.Equal(
            expected.Select(BitConverter.SingleToInt32Bits),
            actual.Select(BitConverter.SingleToInt32Bits));
    }

    [Theory(DisplayName = "Approx float32 nine-tap VHS boxcar SIMD matches scalar bits")]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(4_099)]
    public void ApproxFloat32NineTapVhsBoxcarSimdMatchesScalarBits(int length)
    {
        var random = new Random(4_315_520 + length);
        var input = new float[length];
        for (int index = 0; index < input.Length; index++)
        {
            input[index] = index % 97 switch
            {
                0 => -0.0f,
                1 => 0.0f,
                2 => float.NaN,
                3 => float.PositiveInfinity,
                4 => float.NegativeInfinity,
                _ => (random.NextSingle() * 8.0f) - 4.0f
            };
        }

        float[] expected = VhsSyncDetector.ConvolveBoxcarSameFloat32(
            input,
            windowSize: 9,
            allowAvx: false);
        float[] actual = VhsSyncDetector.ConvolveBoxcarSameFloat32(
            input,
            windowSize: 9,
            allowAvx: true);

        Assert.Equal(
            expected.Select(BitConverter.SingleToInt32Bits),
            actual.Select(BitConverter.SingleToInt32Bits));
    }

    [Theory(DisplayName = "Approx float32 VHS edge scan matches widened threshold semantics")]
    [InlineData(false)]
    [InlineData(true)]
    public void ApproxFloat32VhsEdgeScanMatchesWidenedThresholdSemantics(bool allowAvx)
    {
        const int Length = 4_099;
        var random = new Random(4_315_520);
        var filtered = new float[Length];
        for (int index = 0; index < filtered.Length; index++)
        {
            filtered[index] = index % 71 switch
            {
                0 => float.NaN,
                1 => float.PositiveInfinity,
                2 => float.NegativeInfinity,
                _ => (random.NextSingle() * 8.0f) - 4.0f
            };
        }

        int[] falls = Enumerable.Range(0, 64)
            .Select(index => 17 + (index * 97))
            .ToArray();
        bool[] finalMask = Enumerable.Range(0, falls.Length)
            .Select(index => index % 5 != 1)
            .ToArray();
        double[] widened = filtered.Select(static value => (double)value).ToArray();
        double positiveBoundary =
            ((double)BitConverter.Int32BitsToSingle(0x3EAAAAAA)
             + BitConverter.Int32BitsToSingle(0x3EAAAAAB))
            * 0.5;
        double negativeBoundary =
            ((double)BitConverter.Int32BitsToSingle(unchecked((int)0xBEAAAAAB))
             + BitConverter.Int32BitsToSingle(unchecked((int)0xBEAAAAAA)))
            * 0.5;
        foreach (double preciseMidpoint in new[]
                 {
                     positiveBoundary,
                     negativeBoundary,
                     0.5,
                     -0.0,
                     double.PositiveInfinity,
                     double.NegativeInfinity,
                     double.NaN
                 })
        {
            var expectedFalls = new List<int>();
            var expectedRises = new List<int>();
            var actualFalls = new List<int>();
            var actualRises = new List<int>();

            VhsSyncDetector.FindPreciseEdgesOnValidGrid(
                widened,
                preciseMidpoint,
                falls,
                finalMask,
                falls.Length,
                effectiveLineLength: 111.55,
                jitterTolerance: 10.75,
                expectedFalls,
                expectedRises,
                allowAvx: false);
            VhsSyncDetector.FindPreciseEdgesOnValidGridFloat32(
                filtered,
                preciseMidpoint,
                falls,
                finalMask,
                falls.Length,
                effectiveLineLength: 111.55,
                jitterTolerance: 10.75,
                actualFalls,
                actualRises,
                allowAvx);

            Assert.Equal(expectedFalls, actualFalls);
            Assert.Equal(expectedRises, actualRises);
        }
    }

    [Theory(DisplayName = "Approx initial float32 VHS edge SIMD matches scalar ordering")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(4_099)]
    public void ApproxInitialFloat32VhsEdgeSimdMatchesScalarOrdering(int length)
    {
        var random = new Random(4_315_520 + length);
        var filtered = new float[length];
        for (int index = 0; index < filtered.Length; index++)
        {
            filtered[index] = index % 71 switch
            {
                0 => -0.0f,
                1 => 0.0f,
                2 => float.NaN,
                3 => float.PositiveInfinity,
                4 => float.NegativeInfinity,
                _ => (random.NextSingle() * 8.0f) - 4.0f
            };
        }

        double positiveBoundary =
            ((double)BitConverter.Int32BitsToSingle(0x3EAAAAAA)
             + BitConverter.Int32BitsToSingle(0x3EAAAAAB))
            * 0.5;
        double negativeBoundary =
            ((double)BitConverter.Int32BitsToSingle(unchecked((int)0xBEAAAAAB))
             + BitConverter.Int32BitsToSingle(unchecked((int)0xBEAAAAAA)))
            * 0.5;
        foreach (double slicerLevel in new[]
                 {
                     positiveBoundary,
                     negativeBoundary,
                     0.5,
                     -0.0,
                     double.PositiveInfinity,
                     double.NegativeInfinity,
                     double.NaN
                 })
        {
            (int[] expectedFalls, int[] expectedRises) =
                VhsSyncDetector.FindInitialEdgesSequentialFloat32(
                    filtered,
                    slicerLevel,
                    minimumWidth: 2.25,
                    maximumWidth: 45.75,
                    initialCapacity: 4,
                    allowAvx: false);
            (int[] actualFalls, int[] actualRises) =
                VhsSyncDetector.FindInitialEdgesSequentialFloat32(
                    filtered,
                    slicerLevel,
                    minimumWidth: 2.25,
                    maximumWidth: 45.75,
                    initialCapacity: 4,
                    allowAvx: true);

            Assert.Equal(expectedFalls, actualFalls);
            Assert.Equal(expectedRises, actualRises);
        }
    }

    [Fact(DisplayName = "Approx initial edge SIMD preserves full serial detection bits")]
    public void ApproxInitialEdgeSimdPreservesFullSerialDetectionBits()
    {
        float[] signal = BuildPeriodicSignal(300_003, 2_560, 188)
            .Select(static value => (float)value)
            .ToArray();
        var detector = new VhsSyncDetector(
            188.0,
            152.0,
            2_560,
            8.8,
            workerThreads: 1);

        VhsSyncDetectionResult expected = detector.DetectApproxFloat32(
            signal,
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0,
            allowAvx: false);
        VhsSyncDetectionResult first = detector.DetectApproxFloat32(
            signal,
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0,
            allowAvx: true);
        VhsSyncDetectionResult second = detector.DetectApproxFloat32(
            signal,
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0,
            allowAvx: true);

        Assert.NotEmpty(expected.Pulses);
        AssertDetectionBitsEqual(expected, first);
        AssertDetectionBitsEqual(first, second);
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

    [Theory(DisplayName = "AVX current VHS precise edge scanning matches the scalar oracle")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(4_099)]
    public void AvxCurrentVhsPreciseEdgeScanningMatchesScalarOracle(int length)
    {
        var random = new Random(4_315_520 + length);
        var filtered = new double[length];
        for (int index = 0; index < filtered.Length; index++)
        {
            filtered[index] = (index + random.Next(11)) % 13 switch
            {
                0 => double.NaN,
                1 => double.PositiveInfinity,
                2 => double.NegativeInfinity,
                3 => -0.0,
                4 => 0.0,
                5 => -2.0,
                6 => 2.0,
                7 => BitConverter.Int64BitsToDouble(
                    unchecked((long)0x7FF0_0000_0000_0001UL)),
                8 => BitConverter.Int64BitsToDouble(
                    unchecked((long)0xFFF0_0000_0000_0001UL)),
                _ => (random.NextDouble() * 8.0) - 4.0
            };
        }

        int[] falls = Enumerable.Range(0, 64)
            .Select(index => 17 + (index * 97))
            .ToArray();
        bool[] finalMask = Enumerable.Range(0, falls.Length)
            .Select(index => index % 5 != 1)
            .ToArray();
        var scalarFalls = new List<int> { int.MinValue };
        var scalarRises = new List<int> { int.MaxValue };
        var avxFalls = new List<int> { int.MinValue };
        var avxRises = new List<int> { int.MaxValue };

        VhsSyncDetector.FindPreciseEdgesOnValidGrid(
            filtered,
            preciseMidpoint: 0.0,
            falls,
            finalMask,
            falls.Length,
            effectiveLineLength: 111.55,
            jitterTolerance: 10.75,
            scalarFalls,
            scalarRises,
            allowAvx: false);
        VhsSyncDetector.FindPreciseEdgesOnValidGrid(
            filtered,
            preciseMidpoint: 0.0,
            falls,
            finalMask,
            falls.Length,
            effectiveLineLength: 111.55,
            jitterTolerance: 10.75,
            avxFalls,
            avxRises,
            allowAvx: true);

        Assert.Equal(scalarFalls, avxFalls);
        Assert.Equal(scalarRises, avxRises);
    }

    [Fact(DisplayName = "AVX current VHS precise edge scanning handles special values and its scalar tail")]
    public void AvxCurrentVhsPreciseEdgeScanningHandlesSpecialValuesAndScalarTail()
    {
        Assert.SkipUnless(Avx.IsSupported, "AVX is unavailable on this host.");
        double positiveSignalingNaN = BitConverter.Int64BitsToDouble(
            unchecked((long)0x7FF0_0000_0000_0001UL));
        double negativeSignalingNaN = BitConverter.Int64BitsToDouble(
            unchecked((long)0xFFF0_0000_0000_0001UL));
        double[] filtered =
        [
            double.PositiveInfinity,
            double.NegativeInfinity,
            -1.0,
            -0.0,
            double.NaN,
            0.0,
            -1.0,
            negativeSignalingNaN,
            double.NegativeInfinity,
            double.PositiveInfinity,
            positiveSignalingNaN,
            0.0,
            -1.0,
            -1.0,
            double.NaN,
            -1.0,
            -1.0,
            1.0
        ];
        var fallingEdges = new List<int>();
        var risingEdges = new List<int>();

        VhsSyncDetector.FindPreciseEdgesOnValidGrid(
            filtered,
            preciseMidpoint: 0.0,
            falls: [0],
            finalMask: [true],
            amplitudeCount: 1,
            effectiveLineLength: 1_000.0,
            jitterTolerance: 100.0,
            fallingEdges,
            risingEdges,
            allowAvx: true);

        Assert.Equal([0, 5, 11], fallingEdges);
        Assert.Equal([2, 8, 16], risingEdges);
    }

    [Fact(DisplayName = "Current VHS sync detector reuses its full-field workspace")]
    public void CurrentVhsSyncDetectorReusesFullFieldWorkspace()
    {
        const int MeasurementIterations = 8;
        double[] signal = BuildPeriodicSignal(1_000_000, 2_560, 188);
        var detector = new VhsSyncDetector(188.0, 152.0, 2_560, 8.8);
        for (int iteration = 0; iteration < 3; iteration++)
        {
            _ = detector.Detect(
                signal,
                detectLevels: false,
                syncTipEstimate: -2.0,
                blankingEstimate: 100.0);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        VhsSyncDetectionResult? result = null;
        for (int iteration = 0; iteration < MeasurementIterations; iteration++)
        {
            result = detector.Detect(
                signal,
                detectLevels: false,
                syncTipEstimate: -2.0,
                blankingEstimate: 100.0);
        }

        long allocatedPerCall =
            (GC.GetAllocatedBytesForCurrentThread() - before) / MeasurementIterations;

        Assert.NotNull(result);
        Assert.NotEmpty(result.Pulses);
        Assert.InRange(allocatedPerCall, 0, 40 * 1024);
    }

    [Fact(DisplayName = "Current VHS sync workspace survives dirty large-small-large reuse")]
    public void CurrentVhsSyncWorkspaceSurvivesDirtyLargeSmallLargeReuse()
    {
        double[] large = BuildPeriodicSignal(1_000_000, 2_560, 188);
        double[] small = BuildPeriodicSignal(150_000, 2_560, 188);
        var shared = new VhsSyncDetector(188.0, 152.0, 2_560, 8.8);
        VhsSyncDetectionResult expectedLarge = new VhsSyncDetector(
            188.0,
            152.0,
            2_560,
            8.8).Detect(
            large,
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0);
        VhsSyncDetectionResult expectedSmall = new VhsSyncDetector(
            188.0,
            152.0,
            2_560,
            8.8).Detect(
            small,
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0);

        AssertDetectionBitsEqual(expectedLarge, shared.Detect(
            large,
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0));
        AssertDetectionBitsEqual(expectedSmall, shared.Detect(
            small,
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0));
        AssertDetectionBitsEqual(expectedLarge, shared.Detect(
            large,
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0));
    }

    [Fact(DisplayName = "Current VHS sync workspaces remain call-local under concurrency")]
    public async Task CurrentVhsSyncWorkspacesRemainCallLocalUnderConcurrency()
    {
        double[][] signals = Enumerable.Range(0, 6)
            .Select(index => BuildPeriodicSignal(
                300_000 + (index * 47_123),
                2_560,
                180 + (index * 3),
                phaseOffset: index * 29,
                noiseMultiplier: 37 + (index * 2)))
            .ToArray();
        VhsSyncDetectionResult[] expected = signals
            .Select(signal => new VhsSyncDetector(
                188.0,
                152.0,
                2_560,
                8.8).Detect(
                    signal,
                    detectLevels: false,
                    syncTipEstimate: -2.0,
                    blankingEstimate: 100.0))
            .ToArray();
        var shared = new VhsSyncDetector(188.0, 152.0, 2_560, 8.8);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        _ = shared.Detect(
            signals[^1],
            detectLevels: false,
            syncTipEstimate: -2.0,
            blankingEstimate: 100.0);

        for (int round = 0; round < 2; round++)
        {
            using var startBarrier = new Barrier(signals.Length + 1);
            Task<VhsSyncDetectionResult>[] tasks = signals
                .Select(signal => Task.Factory.StartNew(
                    () =>
                    {
                        startBarrier.SignalAndWait(cancellationToken);
                        return shared.Detect(
                            signal,
                            detectLevels: false,
                            syncTipEstimate: -2.0,
                            blankingEstimate: 100.0);
                    },
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            startBarrier.SignalAndWait(cancellationToken);
            VhsSyncDetectionResult[] actual = await Task.WhenAll(tasks);

            for (int index = 0; index < actual.Length; index++)
            {
                AssertDetectionBitsEqual(expected[index], actual[index]);
            }
        }
    }

    [Fact(DisplayName = "Parallel current VHS sync detector reuses radix workspaces without caller allocation")]
    public void ParallelCurrentVhsSyncDetectorReusesRadixWorkspacesWithoutCallerAllocation()
    {
        double[] signal = BuildPeriodicSignal(1_000_000, 2_560, 188);
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

        Assert.NotEmpty(result.Pulses);
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
        static uint SortablePrefix(double value)
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(value);
            ulong key = (bits & 0x8000_0000_0000_0000UL) != 0
                ? ~bits
                : bits ^ 0x8000_0000_0000_0000UL;
            return (uint)(key >> 32);
        }

        const int length = 600_013;
        var random = new Random(34_104_315);
        double[] sharedSecondPrefixSource = Enumerable.Range(0, length)
            .Select(index =>
            {
                uint prefix = (index % 10) switch
                {
                    0 => 0xBFF0_0040U,
                    1 or 2 => 0xBFF0_0180U,
                    _ => 0xBFF0_0800U
                };
                ulong bits = ((ulong)(prefix ^ 0x8000_0000U) << 32) | (uint)index;
                return BitConverter.UInt64BitsToDouble(bits);
            })
            .ToArray();
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
                .ToArray(),
            sharedSecondPrefixSource
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

            if (ReferenceEquals(source, sharedSecondPrefixSource))
            {
                uint syncPrefix = SortablePrefix(expectedSync);
                uint blankingPrefix = SortablePrefix(expectedBlanking);
                Assert.NotEqual(syncPrefix, blankingPrefix);
                Assert.Equal(syncPrefix >> 10, blankingPrefix >> 10);
            }

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
                Assert.Equal(
                    expectedScratch.Select(BitConverter.DoubleToInt64Bits),
                    actualScratch.Select(BitConverter.DoubleToInt64Bits));
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
    [InlineData(8)]
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
    [InlineData(8)]
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
        double[] serial = VhsSyncDetector.ConvolveBoxcarSame(input, windowSize: 9);
        var actual = new double[expected.Length];
        VhsSyncDetector.ConvolveBoxcarSameParallel(
            input,
            windowSize: 9,
            actual,
            actual.Length,
            workers);

        Assert.Equal(
            expected.Select(BitConverter.DoubleToInt64Bits),
            serial.Select(BitConverter.DoubleToInt64Bits));
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

    private static double[] BuildPeriodicSignal(
        int length,
        int lineLength,
        int pulseLength,
        int phaseOffset = 0,
        int noiseMultiplier = 37)
    {
        var signal = new double[length];
        for (int index = 0; index < signal.Length; index++)
        {
            signal[index] =
                100.0 + ((((index * noiseMultiplier) % 17) - 8) * 0.125);
        }

        for (int start = lineLength - 123 + phaseOffset;
            start + pulseLength < signal.Length;
            start += lineLength)
        {
            PaintPulse(signal, start, pulseLength, -2.0);
        }

        return signal;
    }

    private static void PaintPulse(double[] signal, int start, int length, double level)
        => Array.Fill(signal, level, start, length);

    private static void AssertDetectionBitsEqual(
        VhsSyncDetectionResult expected,
        VhsSyncDetectionResult actual)
    {
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.SyncTipLevel),
            BitConverter.DoubleToInt64Bits(actual.SyncTipLevel));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.BlankLevel),
            BitConverter.DoubleToInt64Bits(actual.BlankLevel));
        Assert.Equal(expected.Pulses.Count, actual.Pulses.Count);
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
    }

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
