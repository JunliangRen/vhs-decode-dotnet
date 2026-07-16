using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscAgcMedianNumericsCompatibilityTests
{
    [Fact(DisplayName = "LD Numba float32 medians propagate NaN")]
    public void LaserDiscNumbaFloat32MediansPropagateNan()
    {
        double positiveNan = BitConverter.UInt64BitsToDouble(0x7FF8000000000000UL);
        double median = NumbaReduction.MedianFloat32([1.0, 2.0, positiveNan, 3.0, 4.0]);

        Assert.Equal(0x7FF8000000000000UL, BitConverter.DoubleToUInt64Bits(median));
        Assert.Equal(
            0x7FF8000000000000UL,
            BitConverter.DoubleToUInt64Bits(NumbaReduction.MedianFloat32([])));
        Assert.Equal(
            0x7FF8000000000000UL,
            BitConverter.DoubleToUInt64Bits(NumpyReduction.MedianFloat64([1.0, positiveNan, 2.0])));
        Assert.Equal(
            0xFFF8000000000000UL,
            BitConverter.DoubleToUInt64Bits(NumpyReduction.MedianFloat64([])));
    }

    [Fact(DisplayName = "LD AGC line levels use Numba float32 medians")]
    public void LaserDiscAgcLineLevelsUseNumbaFloat32Medians()
    {
        const int lineLength = 125;
        const int lineCount = 20;
        var spec = new TbcFrameSpec(
            "NTSC",
            OutputLineLength: lineLength,
            OutputLineCount: lineCount,
            OutputSampleRateHz: 1_250_000.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 8_000_000.0,
            hzIre: 10_000.0,
            outputZero: 1_024,
            vsyncIre: -40.0,
            outputScale: 358.4);
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_250_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0);
        var pipeline = new TbcFieldDecodePipeline(
            analyzer,
            new TbcFieldRenderer(spec, converter),
            converter,
            "NTSC",
            TbcDropoutDetectionOptions.Disabled,
            laserDiscAgcOptions: new LaserDiscAgcOptions(
                ColorBurstEndUsec: 10.0,
                ActiveVideoStartUsec: 14.5,
                WhiteSlices: []));

        var lowPass = new double[(lineCount + 1) * lineLength];
        for (int line = 12; line < lineCount; line++)
        {
            FillMedianBoundary(lowPass, (line * lineLength), 7_592_572.0);
            FillMedianBoundary(lowPass, (line * lineLength) + 12, 8_000_000.0);
        }

        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => line * (double)lineLength)
            .ToArray();
        (double syncHz, double ire0Hz, double ire100Hz) = pipeline.DetectLaserDiscAgcLevels(
            new RfDecodedSpan(0, [], lowPass, lowPass, VideoLowPass: lowPass),
            lineLocations,
            meanLineLength: lineLength,
            converter,
            isFirstField: true);

        Assert.Equal(0x415CF69F00000000UL, BitConverter.DoubleToUInt64Bits(syncHz));
        Assert.Equal(0x415E848000000000UL, BitConverter.DoubleToUInt64Bits(ire0Hz));
        Assert.Equal(converter.IreToHz(100.0), ire100Hz);
    }

    private static void FillMedianBoundary(double[] target, int start, double center)
    {
        target[start] = center - 1.0;
        target[start + 1] = center - 0.5;
        target[start + 2] = center;
        target[start + 3] = center + 0.5;
        target[start + 4] = center + 1.0;
        target[start + 5] = center + 1.5;
    }
}
