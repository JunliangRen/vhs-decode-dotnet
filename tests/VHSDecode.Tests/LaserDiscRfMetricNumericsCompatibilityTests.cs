using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscRfMetricNumericsCompatibilityTests
{
    [Fact(DisplayName = "LD RF ratio uses NumPy float64 standard deviations before rounding")]
    public void LaserDiscRfRatioUsesNumpyFloat64StandardDeviations()
    {
        const int lineLength = 300;
        var spec = new TbcFrameSpec(
            "NTSC",
            OutputLineLength: lineLength,
            OutputLineCount: 20,
            OutputSampleRateHz: 1_000_000.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 1_000.0,
            hzIre: 10.0,
            outputZero: 10_000,
            vsyncIre: -40.0,
            outputScale: 100.0);
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: lineLength,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0);
        var metricOptions = new LaserDiscRfMetricOptions(
            WhiteSlices: [new LaserDiscVitsLevelSlice(Line: 15, StartUsec: 12.0, LengthUsec: 256.0, Percentile: 50.0)],
            BlackSlice: new LaserDiscVitsLevelSlice(Line: 16, StartUsec: 12.0, LengthUsec: 256.0, Percentile: 50.0),
            VideoWhiteDelaySamples: 0,
            VideoSyncDelaySamples: 0);
        var pipeline = new TbcFieldDecodePipeline(
            analyzer,
            new TbcFieldRenderer(spec, converter),
            converter,
            "NTSC",
            TbcDropoutDetectionOptions.Disabled,
            laserDiscRfMetricOptions: metricOptions);

        double[] video = Enumerable.Repeat(1_000.0, 24 * lineLength).ToArray();
        for (int line = 0; line < 23; line++)
        {
            int pulseLength = line is 3 or 4 ? 20 : 10;
            PaintPulse(video, 10 + (line * lineLength), pulseLength, 600.0);
        }

        Array.Fill(video, 2_000.0, 10 + (15 * lineLength) + 5, 280);

        double[] raw = new double[video.Length];
        double[] whitePattern = GeneratePattern(seed: 1, length: 257);
        double[] blackPattern = GeneratePattern(seed: 31, length: 257);
        double blackScale = BitConverter.Int64BitsToDouble(0x3FF00F59DD952037L);
        for (int i = 0; i < blackPattern.Length; i++)
        {
            blackPattern[i] *= blackScale;
        }

        Array.Copy(whitePattern, 0, raw, 10 + (15 * lineLength) + 12, whitePattern.Length);
        Array.Copy(blackPattern, 0, raw, 10 + (16 * lineLength) + 12, blackPattern.Length);

        TbcDecodedField decoded = pipeline.Decode(new RfDecodedSpan(0, raw, video, video));

        Assert.True(decoded.DetectedFirstField);
        Assert.Equal(1.0, decoded.BlackToWhiteRfRatio);
    }

    private static double[] GeneratePattern(ulong seed, int length)
    {
        var values = new double[length];
        ulong state = seed;
        for (int i = 0; i < values.Length; i++)
        {
            state = unchecked((state * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL);
            double unit = (state >> 11) / 9_007_199_254_740_992.0;
            values[i] = (unit - 0.5) * 2_048.0;
        }

        return values;
    }

    private static void PaintPulse(double[] samples, int start, int length, double value)
        => Array.Fill(samples, value, start, length);
}
