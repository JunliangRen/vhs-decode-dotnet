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

    private static TbcFieldDecodePipeline CreatePipeline()
    {
        var converter = new VideoOutputConverter(
            ire0: 0.0,
            hzIre: 1.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 15.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 25.0);
        var spec = new TbcFrameSpec(
            "NTSC",
            OutputLineLength: 4,
            OutputLineCount: 12,
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
                Mode: TbcDropoutDetectionMode.LaserDiscDemod));
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
