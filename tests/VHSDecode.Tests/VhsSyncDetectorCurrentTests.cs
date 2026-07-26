using System.Buffers.Binary;
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

    [Fact(DisplayName = "Current VHS sync detector reuses its full-field workspace")]
    public void CurrentVhsSyncDetectorReusesFullFieldWorkspace()
    {
        var signal = new double[1_000_000];
        Array.Fill(signal, 42.0);
        var detector = new VhsSyncDetector(188.0, 152.0, 2560, 8.8);
        _ = detector.Detect(
            signal,
            detectLevels: false,
            syncTipEstimate: 3_800_000.0,
            blankingEstimate: 4_100_000.0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        VhsSyncDetectionResult result = detector.Detect(
            signal,
            detectLevels: false,
            syncTipEstimate: 3_800_000.0,
            blankingEstimate: 4_100_000.0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(result.Pulses);
        Assert.InRange(allocated, 0, 64 * 1024);
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
