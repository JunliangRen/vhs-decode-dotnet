using System.Reflection;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsSavedLevelStateCompatibilityTests
{
    private const string SyncIssueDiagnostic =
        "Possible sync issues, re-running level detection on next field!";

    [Theory(DisplayName = "VHS saved-level retry threshold matches v0.4.0")]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void SavedLevelRetryThresholdMatches(int errorCount, bool expectedIssues)
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreatePipeline(
            diagnostics,
            decodeType: "vhs");
        var errors = new bool[40];
        Array.Fill(errors, true, 0, errorCount);

        pipeline.CompleteVhsLineLocationComputation(errors);

        Assert.Equal(expectedIssues, pipeline.CaptureState().VhsLineLocationIssues);
        if (expectedIssues)
        {
            Assert.Equal([("DEBUG", SyncIssueDiagnostic)], diagnostics);
        }
        else
        {
            Assert.Empty(diagnostics);
        }
    }

    [Fact(DisplayName = "VHS FieldState moving averages match v0.4.0 windows")]
    public void FieldStateMovingAveragesMatchReleaseFourWindows()
    {
        var pal = new VhsFieldLevelState(framesPerSecond: 25.0);
        Assert.False(pal.HasLevels);
        Assert.Null(pal.PullSyncLevel());
        Assert.Null(pal.PullLevels());

        pal.PushSyncLevel(-40.0);
        pal.PushLevels(-30.0, 10.0);
        Assert.True(pal.HasLevels);
        Assert.Equal(-35.0, pal.PullSyncLevel());
        Assert.Equal((-35.0, 10.0), pal.PullLevels());

        var ntsc = new VhsFieldLevelState(framesPerSecond: 30_000.0 / 1_001.0);
        for (int value = 1; value <= 14; value++)
        {
            ntsc.PushLevels(value, value * 10.0);
        }

        Assert.Equal((8.5, 85.0), ntsc.PullLevels());
    }

    [Fact(DisplayName = "VHS missing serration means reuse FieldState like v0.4.0")]
    public void MissingSerrationMeansReuseFieldState()
    {
        var fieldState = new VhsFieldLevelState(framesPerSecond: 25.0);
        fieldState.PushLevels(60.0, 100.0);
        var detector = new VsyncSerrationDetector(
            sampleRateHz: 4_000_000.0,
            framesPerSecond: 25.0,
            frameLines: 625.0,
            equalizingPulseUs: 2.35);
        detector.PushLevels(180.0, 280.0);
        detector.PushLevels(200.0, 300.0);
        _ = detector.PullLevels();
        var diagnostics = new List<(string Level, string Message)>();

        Assert.True(TbcFieldDecodePipeline.ApplyVhsSerrationRefinementFallback(
            SerrationLevelFailureKind.MissingLevels,
            fieldState,
            detector,
            (level, message) => diagnostics.Add((level, message))));

        Assert.Equal((130.0, 200.0), detector.PullLevels());
        Assert.Equal(
            [("DEBUG", "blacklevel or synclevel had a NaN!")],
            diagnostics);
        Assert.False(TbcFieldDecodePipeline.ApplyVhsSerrationRefinementFallback(
            SerrationLevelFailureKind.MissingLevels,
            fieldState: null,
            detector,
            diagnosticLogger: null));
    }

    [Fact(DisplayName = "VHS first fallback pass reuses FieldState without storing serration levels")]
    public void FirstFallbackPassDefersSerrationLevelStorage()
    {
        var fieldState = new VhsFieldLevelState(framesPerSecond: 25.0);
        fieldState.PushLevels(60.0, 100.0);
        var detector = new VsyncSerrationDetector(
            sampleRateHz: 4_000_000.0,
            framesPerSecond: 25.0,
            frameLines: 625.0,
            equalizingPulseUs: 2.35);
        detector.PushLevels(180.0, 280.0);
        var diagnostics = new List<(string Level, string Message)>();

        Assert.True(TbcFieldDecodePipeline.TryResolveVhsSerrationRefinementFallback(
            SerrationLevelFailureKind.NonFiniteLevels,
            fieldState,
            (level, message) => diagnostics.Add((level, message)),
            out (double SyncLevel, double BlankLevel)? levels));

        Assert.Equal((60.0, 100.0), levels);
        Assert.Equal((180.0, 280.0), detector.PullLevels());
        Assert.Equal(
            [("DEBUG", "blacklevel or synclevel had a NaN!")],
            diagnostics);
    }

    [Fact(DisplayName = "VHS serration fallback resolves FieldState after refinement updates")]
    public void SerrationFallbackUsesPostRefinementFieldState()
    {
        var fieldState = new VhsFieldLevelState(framesPerSecond: 25.0);
        for (int syncLevel = 1; syncLevel <= 10; syncLevel++)
        {
            fieldState.PushLevels(syncLevel, 100.0);
        }

        double[] syncReference = [0.0, 100.0];
        var rejectedSerrationLevels = (SyncLevel: 100.0, BlankLevel: 200.0);
        (double SyncLevel, double BlankLevel)? beforeRefinement =
            TbcFieldDecodePipeline.SelectUsableVhsLevels(
                syncReference,
                referenceSyncLevel: 60.0,
                rejectedSerrationLevels,
                hzIre: 1.0,
                fieldState,
                diagnosticLogger: null);
        Assert.Equal((5.5, 100.0), beforeRefinement);

        fieldState.PushSyncLevel(100.0);
        fieldState.PushSyncLevel(200.0);

        (double SyncLevel, double BlankLevel)? afterRefinement =
            TbcFieldDecodePipeline.SelectUsableVhsLevels(
                syncReference,
                referenceSyncLevel: 60.0,
                rejectedSerrationLevels,
                hzIre: 1.0,
                fieldState,
                diagnosticLogger: null);
        Assert.Equal((35.2, 100.0), afterRefinement);
    }

    [Fact(DisplayName = "VHS line-location issue state forces fresh level detection")]
    public void LineLocationIssueStateForcesFreshLevelDetection()
    {
        TbcFieldDecodePipeline pipeline = CreatePipeline([], decodeType: null);
        TbcFieldDecodeState initial = pipeline.CaptureState();
        pipeline.RestoreStateForRetry(initial with
        {
            LastDetectedSyncLevels = (-60.0, 0.0),
            VhsLineLocationIssues = false
        });
        TbcFieldDecodeState cleanState = pipeline.CaptureState();
        RfDecodedSpan span = BuildLevelDetectionSpan(syncLevel: -80.0);

        SyncPreparedSpan reused = PrepareSyncSpan(pipeline, span);
        Assert.True(reused.UsedSavedLevels);
        Assert.Equal(-30.0, reused.Threshold, 12);

        pipeline.RestoreStateForRetry(cleanState with { VhsLineLocationIssues = true });
        SyncPreparedSpan refreshed = PrepareSyncSpan(pipeline, span);
        Assert.False(refreshed.UsedSavedLevels);
        Assert.Equal(-40.0, refreshed.Threshold, 12);
        Assert.True(pipeline.CaptureState().VhsLineLocationIssues);

        pipeline.RestoreStateForRetry(cleanState);
        Assert.False(pipeline.CaptureState().VhsLineLocationIssues);
        Assert.True(PrepareSyncSpan(pipeline, span).UsedSavedLevels);
    }

    [Fact(DisplayName = "Successful VHS line locations clear saved-level retry state")]
    public void SuccessfulLineLocationsClearRetryState()
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreatePipeline(
            diagnostics,
            decodeType: "vhs");
        TbcFieldDecodeState initial = pipeline.CaptureState();
        pipeline.RestoreStateForRetry(initial with { VhsLineLocationIssues = true });
        double[] video = new double[2_500];
        PaintPulse(video, 10, 10, -40.0);
        PaintPulse(video, 110, 10, -40.0);
        PaintNtscFirstFieldVBlank(video, line0: 210);
        for (int line = 11; line <= 20; line++)
        {
            PaintPulse(video, 210 + (line * 100), 10, -40.0);
        }

        TbcDecodedField decoded = pipeline.Decode(
            new RfDecodedSpan(0, video, video, video),
            syncThresholdHz: -20.0);

        Assert.True(decoded.LineLocations.Filled.Count(error => error) < 30);
        Assert.False(pipeline.CaptureState().VhsLineLocationIssues);
        Assert.DoesNotContain(diagnostics, entry => entry.Message == SyncIssueDiagnostic);
    }

    [Fact(DisplayName = "Rejected short VHS fields retain sync history like v0.4.0")]
    public void RejectedShortFieldsRetainSyncHistory()
    {
        TbcFieldDecodePipeline pipeline = CreatePipeline([], decodeType: "vhs");
        double[] video = new double[1_400];
        PaintPulse(video, 10, 10, -40.0);
        PaintPulse(video, 110, 10, -40.0);
        PaintNtscFirstFieldVBlank(video, line0: 210);
        PaintPulse(video, 1_310, 10, -40.0);

        TbcFieldDecodeRecoveryException exception = Assert.Throws<TbcFieldDecodeRecoveryException>(() =>
            pipeline.Decode(
                new RfDecodedSpan(500_000, video, video, video),
                syncThresholdHz: -20.0));

        Assert.Equal(TbcFieldDecodeRecoveryKind.InsufficientData, exception.Kind);
        TbcFieldDecodeState state = pipeline.CaptureState();
        Assert.NotNull(state.PreviousFirstHSyncLocation);
        Assert.Equal(500_000, state.PreviousFirstHSyncReadLocation);
        Assert.NotNull(state.PreviousDetectedFirstField);
        Assert.Null(state.PreviousSyncConfidence);
    }

    private static TbcFieldDecodePipeline CreatePipeline(
        ICollection<(string Level, string Message)> diagnostics,
        string? decodeType)
    {
        var converter = new VideoOutputConverter(
            ire0: 0.0,
            hzIre: 1.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var renderer = new TbcFieldRenderer(
            new TbcFrameSpec(
                "NTSC",
                OutputLineLength: 4,
                OutputLineCount: 2,
                OutputSampleRateHz: 14_318_180.0,
                ColourBurstStart: null,
                ColourBurstEnd: null,
                ActiveVideoStart: null,
                ActiveVideoEnd: null),
            converter);
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0);
        return new TbcFieldDecodePipeline(
            analyzer,
            renderer,
            converter,
            "NTSC",
            TbcDropoutDetectionOptions.Disabled,
            syncDetectionOptions: new SyncDetectionOptions(
                DetectLevels: true,
                LevelDetectDivisor: 1,
                UseSavedLevels: true),
            decodeType: decodeType,
            diagnosticLogger: (level, message) => diagnostics.Add((level, message)));
    }

    private static SyncPreparedSpan PrepareSyncSpan(
        TbcFieldDecodePipeline pipeline,
        RfDecodedSpan span)
    {
        MethodInfo method = typeof(TbcFieldDecodePipeline).GetMethod(
            "PrepareSyncSpan",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(TbcFieldDecodePipeline), "PrepareSyncSpan");
        return Assert.IsType<SyncPreparedSpan>(method.Invoke(pipeline, [span, null, true, true]));
    }

    private static RfDecodedSpan BuildLevelDetectionSpan(double syncLevel)
    {
        double[] video = new double[320];
        PaintPulse(video, 10, 10, syncLevel);
        PaintPulse(video, 110, 10, syncLevel);
        PaintPulse(video, 210, 10, syncLevel);
        return new RfDecodedSpan(0, [], video, video, VideoLowPass: video);
    }

    private static void PaintPulse(double[] data, int start, int length, double value)
    {
        Array.Fill(data, value, start, length);
    }

    private static void PaintNtscFirstFieldVBlank(double[] data, int line0)
    {
        const int PulseCount = 6;
        const int HalfLineSamples = 50;
        PaintPulse(data, line0, 10, -40.0);
        int equalizing1Start = line0 + (2 * HalfLineSamples);
        for (int pulse = 0; pulse < PulseCount; pulse++)
        {
            PaintPulse(data, equalizing1Start + (pulse * HalfLineSamples), 5, -40.0);
        }

        int vSyncStart = equalizing1Start + (PulseCount * HalfLineSamples);
        for (int pulse = 0; pulse < PulseCount; pulse++)
        {
            PaintPulse(data, vSyncStart + (pulse * HalfLineSamples), 20, -40.0);
        }

        int equalizing2Start = vSyncStart + (PulseCount * HalfLineSamples);
        for (int pulse = 0; pulse < PulseCount; pulse++)
        {
            PaintPulse(data, equalizing2Start + (pulse * HalfLineSamples), 5, -40.0);
        }

        PaintPulse(data, equalizing2Start + (PulseCount * HalfLineSamples), 10, -40.0);
    }
}
