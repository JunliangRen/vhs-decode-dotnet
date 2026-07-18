using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsIre0AdjustDiagnosticCompatibilityTests
{
    [Fact(DisplayName = "VHS IRE0 adjustment diagnostics match v0.4.0")]
    public void Ire0AdjustmentDiagnosticsMatch()
    {
        var spec = new TbcFrameSpec(
            "NTSC",
            OutputLineLength: 200,
            OutputLineCount: 3,
            OutputSampleRateHz: 14_318_180.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 100.0,
            hzIre: 10.0,
            outputZero: 0,
            vsyncIre: -40.0,
            outputScale: 1.0);
        var renderer = new TbcFieldRenderer(
            spec,
            converter,
            ire0Adjust: new Ire0AdjustOptions(
                BackPorch: true,
                HSync: true,
                BackPorchStart: 74,
                BackPorchEnd: 124));
        var diagnostics = new List<(string Level, string Message)>();
        _ = new TbcFieldDecodePipeline(
            new SyncAnalyzer(
                sampleRateHz: 1_000_000.0,
                linePeriodUs: 100.0,
                hsyncPulseUs: 10.0,
                equalizingPulseUs: 5.0,
                vsyncPulseUs: 20.0),
            renderer,
            converter,
            "NTSC",
            TbcDropoutDetectionOptions.Disabled,
            decodeType: "vhs",
            diagnosticLogger: (level, message) => diagnostics.Add((level, message)));
        double[] video = new double[spec.FieldSampleCount];
        for (int line = 0; line < spec.OutputLineCount; line++)
        {
            int lineBase = line * spec.OutputLineLength;
            Array.Fill(video, 90.0, lineBase + 4, 66);
            Array.Fill(video, 130.0, lineBase + 78, 42);
        }

        _ = renderer.RenderField(video, [0.0, 200.0, 400.0, 600.0]);

        Assert.Equal(
            [
                ("DEBUG", "calculated ire0: 130.00"),
                ("DEBUG", "calculated hz_ire: 1.00")
            ],
            diagnostics);

        diagnostics.Clear();
        var rawRenderer = new TbcFieldRenderer(
            spec,
            converter,
            ire0Adjust: renderer.Ire0Adjust,
            exportRawTbc: true);
        _ = new TbcFieldDecodePipeline(
            new SyncAnalyzer(
                sampleRateHz: 1_000_000.0,
                linePeriodUs: 100.0,
                hsyncPulseUs: 10.0,
                equalizingPulseUs: 5.0,
                vsyncPulseUs: 20.0),
            rawRenderer,
            converter,
            "NTSC",
            TbcDropoutDetectionOptions.Disabled,
            decodeType: "vhs",
            diagnosticLogger: (level, message) => diagnostics.Add((level, message)));

        _ = rawRenderer.RenderField(video, [0.0, 200.0, 400.0, 600.0]);

        Assert.Empty(diagnostics);
    }
}
