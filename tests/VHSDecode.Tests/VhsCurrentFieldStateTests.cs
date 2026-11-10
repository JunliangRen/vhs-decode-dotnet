using System.Reflection;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsCurrentFieldStateTests
{
    [Theory(DisplayName = "Current VHS HSync measurements keep fractional sample windows")]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionalRateHSyncIncludesOutlierAtExtendedWindow(bool right)
    {
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 10_750_000.0,
            linePeriodUs: 10.0,
            hsyncPulseUs: 5.0,
            equalizingPulseUs: 2.5,
            vsyncPulseUs: 8.0);
        var converter = new VideoOutputConverter(0.0, 1.0, 256, -40.0, 10.0);
        var spec = new TbcFrameSpec("NTSC", 4, 7, 4_000_000.0, null, null, null, null);
        var waveform = new double[300];
        Array.Fill(waveform, -40.0, 101, 53);
        waveform[right ? 183 : 136] = 200.0;
        var lines = new LineLocationResult([101.0], [right]);
        var span = new RfDecodedSpan(0, [], waveform, waveform, VideoLowPass: waveform);

        LineLocationResult release = Refine(UpstreamBehaviorProfile.V040);
        LineLocationResult current = Refine(UpstreamBehaviorProfile.Current);

        Assert.Equal(right ? 101.1046875 : 100.5, release.Locations[0], precision: 10);
        Assert.False(release.Filled[0]);
        Assert.Equal(101.0, current.Locations[0]);
        Assert.True(current.Filled[0]);

        LineLocationResult Refine(UpstreamBehaviorProfile profile)
        {
            var pipeline = new TbcFieldDecodePipeline(
                analyzer,
                new TbcFieldRenderer(spec, converter),
                converter,
                "NTSC",
                TbcDropoutDetectionOptions.Disabled,
                hSyncRefineOptions: right ? HSyncRefineOptions.Default : HSyncRefineOptions.LeftOnly,
                decodeType: "vhs",
                upstreamBehaviorProfile: profile,
                activeVideoStartUs: 10.5);
            return (LineLocationResult)typeof(TbcFieldDecodePipeline)
                .GetMethod("RefineLineLocationsFromHSync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pipeline, [span, lines, converter, -20.0])!;
        }
    }

    [Theory(DisplayName = "Current VHS latches AFC before the next field even without a retune")]
    [InlineData("v0.4.0", 625_000.0, 0.125)]
    [InlineData("current", 630_000.0, 0.25)]
    public void AfcPhaseLatchAdvancesAcrossFieldWithoutRetune(string profile, double expectedHz, double expectedPhase)
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs, ["--pal", "--compat-version", profile, "--chroma_AFC", "input.s16", "output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        TbcFieldDecodePipeline pipeline = session.TbcFieldDecoder;
        pipeline.RestoreStateForRetry(pipeline.CaptureState() with
        {
            ChromaAfcCarrierHz = 630_000.0,
            ChromaAfcPhaseRadians = 0.25,
            ChromaAfcPhaseCarrierHz = 625_000.0,
            ChromaAfcPhaseCarrierRadians = 0.125
        });

        typeof(TbcFieldDecodePipeline)
            .GetMethod("CommitChromaStateCore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pipeline, [0, 7, null]);

        TbcFieldDecodeState state = pipeline.CaptureState();
        Assert.Equal(expectedHz, state.ChromaAfcPhaseCarrierHz);
        Assert.Equal(expectedPhase, state.ChromaAfcPhaseCarrierRadians);
        Assert.Equal(630_000.0, state.ChromaAfcCarrierHz);
        Assert.Equal(0.25, state.ChromaAfcPhaseRadians);
    }
}
