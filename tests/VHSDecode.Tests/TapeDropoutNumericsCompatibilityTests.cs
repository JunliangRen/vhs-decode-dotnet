using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TapeDropoutNumericsCompatibilityTests
{
    [Fact(DisplayName = "VHS dropout threshold uses NumPy float32 envelope mean")]
    public void VhsDropoutThresholdUsesNumpyFloat32EnvelopeMean()
    {
        var spec = new TbcFrameSpec(
            "NTSC",
            OutputLineLength: 4,
            OutputLineCount: 2,
            OutputSampleRateHz: 14_318_180.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 0.0,
            hzIre: 1.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0);
        var pipeline = new TbcFieldDecodePipeline(
            analyzer,
            new TbcFieldRenderer(spec, converter),
            converter,
            "NTSC",
            new TbcDropoutDetectionOptions(
                Enabled: true,
                ThresholdFraction: 0.18,
                AbsoluteThreshold: null,
                Hysteresis: 2.0,
                Mode: TbcDropoutDetectionMode.TapeEnvelope));

        double[] video = new double[320];
        PaintPulse(video, 10, 10, -40.0);
        PaintPulse(video, 110, 10, -40.0);
        PaintPulse(video, 210, 10, -40.0);

        float high = BitConverter.Int32BitsToSingle(0x411FFC18);
        float low = BitConverter.Int32BitsToSingle(0x3FDCD8C5);
        double[] envelope = Enumerable.Repeat((double)high, video.Length).ToArray();
        Array.Fill(envelope, (double)low, 125, 16);

        float mean = NumpyReduction.MeanFloat32(envelope);
        Assert.Equal(0x41195DA5, BitConverter.SingleToInt32Bits(mean));
        Assert.Equal(0x3FDCD8C5, BitConverter.SingleToInt32Bits(mean * 0.18f));

        TbcDecodedField decoded = pipeline.Decode(
            new RfDecodedSpan(0, [], video, video, Envelope: envelope));

        Assert.NotNull(decoded.Dropouts);
        Assert.Equal(1, decoded.Dropouts.Count);
        Assert.Equal([1], decoded.Dropouts.FieldLine);
        Assert.Equal([0], decoded.Dropouts.StartX);
        Assert.Equal([2], decoded.Dropouts.EndX);
    }

    private static void PaintPulse(double[] samples, int start, int length, double value)
        => Array.Fill(samples, value, start, length);
}
