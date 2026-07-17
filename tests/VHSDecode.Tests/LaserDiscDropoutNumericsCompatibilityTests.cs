using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscDropoutNumericsCompatibilityTests
{
    [Fact(DisplayName = "LD RFHPF dropout threshold uses Numba mixed-precision std")]
    public void LaserDiscRfHighPassDropoutThresholdUsesNumbaMixedPrecisionStandardDeviation()
    {
        double[] rfHighPass = BuildBoundaryRfHighPass();

        double standardDeviation =
            NumbaReduction.StandardDeviationFloat32InputToFloat64(rfHighPass);
        Assert.Equal(
            0x3FE247CD51EDA67BUL,
            BitConverter.DoubleToUInt64Bits(standardDeviation));

        float threshold = (float)(standardDeviation * 3.0);
        Assert.Equal(0x3FDB5DA0U, BitConverter.SingleToUInt32Bits(threshold));
        Assert.Equal(threshold, (float)rfHighPass[1_050]);
        Assert.True(rfHighPass[1_050] > LegacyStandardDeviation(rfHighPass) * 3.0);

        double[] video = new double[1_400];
        Array.Fill(video, -40.0, 10, 15);
        Array.Fill(video, -40.0, 110, 15);
        Array.Fill(video, -40.0, 210, 15);

        TbcDecodedField decoded = CreatePipeline().Decode(new RfDecodedSpan(
            0,
            [],
            video,
            new double[video.Length],
            RfHighPass: rfHighPass));

        Assert.NotNull(decoded.Dropouts);
        Assert.Empty(decoded.Dropouts.FieldLine);
    }

    [Fact(DisplayName = "LD demod dropout limits retain float32 array boundaries")]
    public void LaserDiscDemodDropoutLimitsRetainFloat32ArrayBoundaries()
    {
        var converter = new VideoOutputConverter(
            ire0: 8_100_000.0,
            hzIre: 1_700_000.0 / 140.0,
            outputZero: 1_024,
            vsyncIre: -40.0,
            outputScale: 358.4);
        float validMinimum = (float)converter.IreToHz(-50.0);
        Assert.Equal(0x4AE4A9F2U, BitConverter.SingleToUInt32Bits(validMinimum));
        Assert.True(validMinimum < converter.IreToHz(-50.0));

        double[] video = Enumerable.Repeat((double)(float)converter.IreToHz(0.0), 1_400).ToArray();
        Array.Fill(video, (float)converter.IreToHz(-40.0), 10, 15);
        Array.Fill(video, (float)converter.IreToHz(-40.0), 110, 15);
        Array.Fill(video, (float)converter.IreToHz(-40.0), 210, 15);
        video[1_050] = validMinimum;
        video[1_051] = validMinimum;

        TbcDecodedField decoded = CreatePipeline(converter).Decode(new RfDecodedSpan(
            0,
            [],
            video,
            new double[video.Length]));

        Assert.NotNull(decoded.Dropouts);
        Assert.Empty(decoded.Dropouts.FieldLine);
    }

    [Fact(DisplayName = "LD dropout limits use the active AGC converter")]
    public void LaserDiscDropoutLimitsUseActiveAgcConverter()
    {
        var converter = new VideoOutputConverter(
            ire0: 1_000.0,
            hzIre: 10.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var agcOptions = new LaserDiscAgcOptions(
            ColorBurstEndUsec: 10.0,
            ActiveVideoStartUsec: 20.0,
            WhiteSlices:
            [
                new LaserDiscVitsLevelSlice(
                    Line: 15,
                    StartUsec: 12.0,
                    LengthUsec: 8.0,
                    Percentile: 50.0)
            ]);
        TbcFieldDecodePipeline pipeline = CreatePipeline(
            converter,
            outputLineCount: 20,
            laserDiscAgcOptions: agcOptions,
            decodeLaserDiscVbi: true,
            hsyncPulseUsec: 10.0,
            vsyncPulseUsec: 20.0);

        double[] video = Enumerable.Repeat(1_100.0, 2_400).ToArray();
        double[] lowPass = Enumerable.Repeat(1_100.0, video.Length).ToArray();
        for (int line = 0; line < 23; line++)
        {
            int pulseLength = line is 3 or 4 ? 20 : 10;
            int pulseStart = 10 + (line * 100);
            Array.Fill(video, 700.0, pulseStart, pulseLength);
            Array.Fill(lowPass, 700.0, pulseStart, pulseLength);
        }

        Array.Fill(video, 2_050.0, 1_522, 8);
        video[1_860] = 600.0;
        video[1_861] = 600.0;

        TbcDecodedField decoded = pipeline.Decode(new RfDecodedSpan(
            0,
            [],
            video,
            video,
            VideoLowPass: lowPass));

        Assert.True(decoded.LaserDiscAgcAdjusted);
        Assert.NotNull(decoded.OutputConverter);
        Assert.Equal(1_100.0, decoded.OutputConverter.Ire0);
        Assert.Equal(9.5, decoded.OutputConverter.HzIre);
        Assert.NotNull(decoded.Dropouts);
        Assert.Single(decoded.Dropouts.FieldLine);
    }

    [Fact(DisplayName = "LD dropout field bounds use Python integer truncation")]
    public void LaserDiscDropoutFieldBoundsUsePythonIntegerTruncation()
    {
        (int start, int end) = TbcFieldDecodePipeline.GetLaserDiscDropoutSampleRange(
            [0.0, 100.75, 200.75, 300.75],
            startLine: 1,
            endLine: 3,
            sampleCount: 1_000);

        Assert.Equal(100, start);
        Assert.Equal(300, end);
    }

    private static double[] BuildBoundaryRfHighPass()
    {
        var values = new double[1_400];
        for (int i = 0; i < values.Length; i++)
        {
            int raw = ((i * 17) % 4_093) - 2_046;
            values[i] = (float)raw / 2_046.0f;
        }

        float boundary = BitConverter.UInt32BitsToSingle(0x3FDB5DA0U);
        values[1_050] = boundary;
        values[1_051] = boundary;
        return values;
    }

    private static TbcFieldDecodePipeline CreatePipeline(
        VideoOutputConverter? converter = null,
        int outputLineCount = 12,
        LaserDiscAgcOptions? laserDiscAgcOptions = null,
        bool decodeLaserDiscVbi = false,
        double hsyncPulseUsec = 15.0,
        double vsyncPulseUsec = 25.0)
    {
        converter ??= new VideoOutputConverter(
            ire0: 0.0,
            hzIre: 1.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: hsyncPulseUsec,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: vsyncPulseUsec);
        var spec = new TbcFrameSpec(
            "NTSC",
            OutputLineLength: 4,
            OutputLineCount: outputLineCount,
            OutputSampleRateHz: 14_318_180.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        return new TbcFieldDecodePipeline(
            analyzer,
            new TbcFieldRenderer(spec, converter),
            converter,
            "NTSC",
            new TbcDropoutDetectionOptions(
                Enabled: true,
                ThresholdFraction: 0.18,
                AbsoluteThreshold: null,
                Hysteresis: 2.0,
                Mode: TbcDropoutDetectionMode.LaserDiscDemod),
            laserDiscAgcOptions: laserDiscAgcOptions,
            decodeLaserDiscVbi: decodeLaserDiscVbi);
    }

    private static double LegacyStandardDeviation(ReadOnlySpan<double> values)
    {
        double mean = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            mean += values[i];
        }

        mean /= values.Length;
        double sumSquares = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            double distance = values[i] - mean;
            sumSquares += distance * distance;
        }

        return Math.Sqrt(sumSquares / values.Length);
    }
}
