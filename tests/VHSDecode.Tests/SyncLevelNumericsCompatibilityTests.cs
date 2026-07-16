using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class SyncLevelNumericsCompatibilityTests
{
    [Fact(DisplayName = "VHS serration extraction uses Numba float64 fast-math mean")]
    public void VhsSerrationExtractionUsesNumbaFloat64FastMathMean()
    {
        double middle = BitConverter.Int64BitsToDouble(0x402CF3CF3CF3CF3B);
        var serration = new double[1_664];
        for (int block = 0; block < serration.Length; block += 8)
        {
            serration[block] = middle - 1.0;
            serration[block + 1] = middle + 1.0;
            Array.Fill(serration, middle, block + 2, 6);
        }

        (double syncLevel, double blankLevel) =
            VsyncSerrationDetector.GetSerrationSyncLevels(serration)
            ?? throw new InvalidOperationException("Expected serration levels.");

        Assert.Equal(0x402AF3CF3CF3CF3BUL, BitConverter.DoubleToUInt64Bits(syncLevel));
        Assert.Equal(0x402CF3CF3CF3CF3BUL, BitConverter.DoubleToUInt64Bits(blankLevel));
    }

    [Fact(DisplayName = "CVBS serration refinement uses NumPy float64 pulse means")]
    public void CvbsSerrationRefinementUsesNumpyFloat64PulseMeans()
    {
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 40_000_000.0,
            linePeriodUs: 64.0,
            hsyncPulseUs: 4.7,
            equalizingPulseUs: 2.35,
            vsyncPulseUs: 27.3);
        const int halfLine = 1_280;
        int equalizingLength = (int)Math.Round(
            analyzer.UsecToSamples(analyzer.EqualizingPulseUs),
            MidpointRounding.ToEven);
        int vsyncLength = (int)Math.Round(
            analyzer.UsecToSamples(analyzer.VSyncPulseUs),
            MidpointRounding.ToEven);
        double syncValue = BitConverter.Int64BitsToDouble(0x402CF3CF3CF3CF3B);
        double[] field = Enumerable.Repeat(100.0, 30 * halfLine).ToArray();
        const int pulseStart = 1_000;
        for (int pulse = 0; pulse < 18; pulse++)
        {
            int length = pulse is >= 6 and < 12 ? vsyncLength : equalizingLength;
            Array.Fill(field, syncValue, pulseStart + (pulse * halfLine), length);
        }

        SerrationLevelRefinement refinement = LevelDetection.RefineSerrationLevels(
            field,
            initialSyncLevel: syncValue,
            initialBlankLevel: 100.0,
            analyzer,
            referenceSyncLevel: syncValue,
            hzIre: 2.0)
            ?? throw new InvalidOperationException("Expected serration level refinement.");

        Assert.Equal(0x402CF3CF3CF3CF3DUL, BitConverter.DoubleToUInt64Bits(refinement.SyncLevel));
        Assert.Equal(100.0, refinement.BlankLevel);
        Assert.Equal(6, refinement.VsyncPulseCount);
        Assert.Equal(18, refinement.PulseCount);
    }
}
