using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class SyncLevelNumericsCompatibilityTests
{
    [Theory(DisplayName = "CVBS and LD pulse refinement uses NumPy float64 means")]
    [InlineData(false)]
    [InlineData(true)]
    public void PulseRefinementUsesNumpyFloat64Means(bool laserDisc)
    {
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 40_000_000.0,
            linePeriodUs: 64.0,
            hsyncPulseUs: 4.7,
            equalizingPulseUs: 2.35,
            vsyncPulseUs: 27.3);
        const int pulseStart = 100;
        const int pulseLength = 1_092;
        double pulseValue = BitConverter.Int64BitsToDouble(0x402CF3CF3CF3CF3B);
        var syncReference = new double[pulseStart + pulseLength + 100];
        Array.Fill(syncReference, pulseValue, pulseStart + 40, pulseLength - 80);
        Pulse[] pulses = [new Pulse(pulseStart, pulseLength)];
        var converter = new VideoOutputConverter(
            ire0: BitConverter.Int64BitsToDouble(0x4022F3CF3CF3CF7D),
            hzIre: 1.0,
            outputZero: 0,
            vsyncIre: 0.0,
            outputScale: 1.0);

        CvbsPulseDetectionResult result = (laserDisc
            ? CvbsPulseDetector.RefineLaserDisc(
                syncReference,
                pulses,
                initialThreshold: -20.0,
                analyzer,
                converter)
            : CvbsPulseDetector.Refine(
                syncReference,
                pulses,
                initialThreshold: -20.0,
                analyzer,
                converter))
            ?? throw new InvalidOperationException("Expected pulse refinement result.");

        Assert.False(result.Recalibrated);
        Assert.Equal(-20.0, result.Threshold);
        Assert.Equal(pulses, result.Pulses);
    }

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
