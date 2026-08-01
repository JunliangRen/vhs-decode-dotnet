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

    [Fact(DisplayName = "VHS FieldState retains only the v0.4.0 moving-average window")]
    public void FieldStateRetainsOnlyReleaseFourMovingAverageWindow()
    {
        const int sampleCount = 100_000;
        var fieldState = new VhsFieldLevelState(framesPerSecond: 25.0);
        var expectedSync = new List<double>(sampleCount * 2);
        var expectedBlank = new List<double>(sampleCount);

        for (int index = 0; index < sampleCount; index++)
        {
            double measuredSync = (index * 0.125) - 10_000.0;
            double refinedSync = measuredSync + 0.03125;
            double refinedBlank = (index * -0.0625) + 2_000.0;
            fieldState.PushSyncLevel(measuredSync);
            fieldState.PushLevels(refinedSync, refinedBlank);
            expectedSync.Add(measuredSync);
            expectedSync.Add(refinedSync);
            expectedBlank.Add(refinedBlank);
        }

        Assert.Equal((10, 10), fieldState.RetainedSampleCounts);
        Assert.Equal(
            (
                expectedSync.GetRange(expectedSync.Count - 10, 10).Average(),
                expectedBlank.GetRange(expectedBlank.Count - 10, 10).Average()),
            fieldState.PullLevels());
        Assert.Equal((10, 10), fieldState.RetainedSampleCounts);
    }

    [Fact(DisplayName = "Bounded VHS FieldState matches delayed v0.4.0 trimming")]
    public void BoundedFieldStateMatchesReleaseFourDelayedTrimming()
    {
        const int window = 10;
        var fieldState = new VhsFieldLevelState(framesPerSecond: 25.0);
        var expectedSync = new List<double>();
        var expectedBlank = new List<double>();
        var random = new Random(0x5A17);

        static double? Pull(List<double> values)
        {
            if (values.Count == 0)
            {
                return null;
            }

            if (values.Count >= window)
            {
                values.RemoveRange(0, values.Count - window);
            }

            return values.Average();
        }

        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            double sync = (random.NextDouble() - 0.5) * 1e8;
            double blank = (random.NextDouble() - 0.5) * 1e8;
            switch (random.Next(4))
            {
                case 0:
                    fieldState.PushSyncLevel(sync);
                    expectedSync.Add(sync);
                    break;
                case 1:
                    fieldState.PushLevels(sync, blank);
                    expectedSync.Add(sync);
                    expectedBlank.Add(blank);
                    break;
                case 2:
                    Assert.Equal(Pull(expectedSync), fieldState.PullSyncLevel());
                    break;
                default:
                    double? expectedBlankLevel = Pull(expectedBlank);
                    (double SyncLevel, double BlankLevel)? expected = expectedBlankLevel.HasValue
                        ? (Pull(expectedSync)!.Value, expectedBlankLevel.Value)
                        : null;
                    Assert.Equal(expected, fieldState.PullLevels());
                    break;
            }

            (int retainedSync, int retainedBlank) = fieldState.RetainedSampleCounts;
            Assert.InRange(retainedSync, 0, window);
            Assert.InRange(retainedBlank, 0, window);
        }
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

        var syncOnlyState = new VhsFieldLevelState(framesPerSecond: 25.0);
        syncOnlyState.PushSyncLevel(80.0);
        diagnostics.Clear();
        Assert.True(TbcFieldDecodePipeline.TryResolveVhsSerrationRefinementFallback(
            SerrationLevelFailureKind.MissingLevels,
            syncOnlyState,
            (level, message) => diagnostics.Add((level, message)),
            out (double SyncLevel, double BlankLevel)? syncOnlyLevels));
        Assert.Null(syncOnlyLevels);
        Assert.Equal(
            [("DEBUG", "blacklevel or synclevel had a NaN!")],
            diagnostics);
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

    [Fact(DisplayName = "VHS terminal fallback includes rejected sync-only measurements")]
    public void TerminalFallbackIncludesRejectedSyncOnlyMeasurements()
    {
        var fieldState = new VhsFieldLevelState(
            framesPerSecond: 30_000.0 / 1_001.0);
        for (int syncLevel = 1; syncLevel <= 12; syncLevel++)
        {
            fieldState.PushLevels(syncLevel, 100.0);
        }

        (double SyncLevel, double BlankLevel)? savedLevels =
            fieldState.PullLevels();
        fieldState.PushSyncLevel(100.0);

        Assert.Equal(
            (14.75, 100.0),
            TbcFieldDecodePipeline.SelectTerminalVhsLevels(
                fieldState,
                savedLevels));
    }

    [Fact(DisplayName = "VHS fallback sync search preserves v0.4.0 rounding order")]
    public void FallbackSyncSearchPreservesReleaseFourRoundingOrder()
    {
        const double minimumSync = 3_452_817.2299598353;
        const double ire0 = 4_100_000.0;
        const double hzIre = 7_142.857142857143;

        double advanced = LevelDetection.AdvanceFallbackSyncSearchLevel(
            minimumSync,
            ire0,
            hzIre);

        Assert.Equal(4_704_745_975_732_870_158, BitConverter.DoubleToInt64Bits(advanced));
        Assert.NotEqual(
            BitConverter.DoubleToInt64Bits(minimumSync + (hzIre * 5.0)),
            BitConverter.DoubleToInt64Bits(advanced));
    }

    [Fact(DisplayName = "VHS single serration level accepts FieldState without a redundant check")]
    public void SingleSerrationLevelAcceptsFieldStateWithoutRedundantCheck()
    {
        var detector = new VsyncSerrationDetector(
            sampleRateHz: 4_000_000.0,
            framesPerSecond: 25.0,
            frameLines: 625.0,
            equalizingPulseUs: 2.35);
        detector.PushLevels(100.0, 200.0);
        Assert.False(detector.HasLevels);
        var fieldState = new VhsFieldLevelState(framesPerSecond: 25.0);
        fieldState.PushLevels(60.0, 100.0);
        var diagnostics = new List<(string Level, string Message)>();

        (double SyncLevel, double BlankLevel)? selected =
            TbcFieldDecodePipeline.SelectVhsLevelsAfterRefinement(
                [0.0, 100.0],
                referenceSyncLevel: 60.0,
                hzIre: 1.0,
                detector,
                fieldState,
                (level, message) => diagnostics.Add((level, message)));

        Assert.Equal((60.0, 100.0), selected);
        Assert.Empty(diagnostics);
    }

    [Fact(DisplayName = "v0.4.0 rejected serration levels use reversed defaults when FieldState is empty")]
    public void V040RejectedDetectorLevelsWithEmptyFieldStateUseReversedDefaults()
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreateSerrationPipeline(
            diagnostics,
            out _);
        RfDecodedSpan span = BuildSerrationFallbackSpan();

        SyncPreparedSpan prepared = PrepareSyncSpan(pipeline, span);

        Assert.Equal(120.0, prepared.Threshold);
        Assert.False(prepared.UsedSavedLevels);
        Assert.False(prepared.ExplicitThreshold);
        Assert.Same(span.Video, prepared.Span.Video);
        Assert.Equal([40.0, 140.0], prepared.Span.VideoLowPass!);
        Assert.Equal([0.0, 100.0], span.VideoLowPass!);
        Assert.Null(pipeline.CaptureState().LastDetectedSyncLevels);
        Assert.Equal(
            [
                (
                    "DEBUG",
                    "Level detection failed - sync or blank is None"),
                (
                    "DEBUG",
                    "Level check failed on serration measured levels, using defaults.")
            ],
            diagnostics);
    }

    [Fact(DisplayName = "v0.4.0 sync-only FieldState still uses reversed defaults")]
    public void V040SyncOnlyFieldStateUsesReversedDefaults()
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreateSerrationPipeline(
            diagnostics,
            out _);
        GetVhsFieldLevelState(pipeline).PushSyncLevel(75.0);

        SyncPreparedSpan prepared = PrepareSyncSpan(pipeline, BuildSerrationFallbackSpan());

        Assert.Equal(120.0, prepared.Threshold);
        Assert.Null(pipeline.CaptureState().LastDetectedSyncLevels);
        Assert.Contains(
            ("DEBUG", "Level check failed on serration measured levels, using defaults."),
            diagnostics);
    }

    [Fact(DisplayName = "v0.4.0 full FieldState takes precedence over reversed defaults")]
    public void V040PopulatedFieldStateTakesPrecedenceOverReversedDefaults()
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreateSerrationPipeline(
            diagnostics,
            out _);
        GetVhsFieldLevelState(pipeline).PushLevels(60.0, 100.0);

        SyncPreparedSpan prepared = PrepareSyncSpan(pipeline, BuildSerrationFallbackSpan());

        Assert.Equal(80.0, prepared.Threshold);
        Assert.Equal((60.0, 100.0), pipeline.CaptureState().LastDetectedSyncLevels);
        Assert.DoesNotContain(
            diagnostics,
            entry => entry.Message == "Level check failed on serration measured levels, using defaults.");
        Assert.Contains(
            diagnostics,
            entry => entry.Message.StartsWith(
                "Level check failed on serration measured levels [new_sync:",
                StringComparison.Ordinal));
    }

    [Fact(DisplayName = "v0.4.0 retained valid serration levels bypass reversed defaults")]
    public void V040RetainedValidDetectorLevelsBypassReversedDefaults()
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreateSerrationPipeline(
            diagnostics,
            out VsyncSerrationDetector detector);
        detector.PushLevels(60.0, 100.0);
        detector.PushLevels(60.0, 100.0);

        SyncPreparedSpan prepared = PrepareSyncSpan(pipeline, BuildSerrationFallbackSpan());

        Assert.Equal(80.0, prepared.Threshold);
        Assert.Equal((60.0, 100.0), pipeline.CaptureState().LastDetectedSyncLevels);
        Assert.DoesNotContain(
            diagnostics,
            entry => entry.Message == "Level check failed on serration measured levels, using defaults.");
    }

    [Fact(DisplayName = "v0.4.0 single rejected serration measurement does not use reversed defaults")]
    public void V040SingleRejectedSerrationMeasurementDoesNotUseReversedDefaults()
    {
        var diagnostics = new List<(string Level, string Message)>();
        (TbcFieldDecodePipeline pipeline, RfDecodedSpan span) =
            CreateSingleRejectedSerrationPipeline(diagnostics);

        SyncPreparedSpan prepared = PrepareSyncSpan(pipeline, span);

        Assert.Equal(80.0, prepared.Threshold);
        Assert.Same(span, prepared.Span);
        Assert.Null(pipeline.CaptureState().LastDetectedSyncLevels);
        Assert.DoesNotContain(
            diagnostics,
            entry => entry.Message == "Level check failed on serration measured levels, using defaults.");
        Assert.Contains(
            ("DEBUG", "Level detection had issues, so don't store anything in VsyncSerration."),
            diagnostics);
    }

    [Fact(DisplayName = "v0.4.0 rejected serration uses reversed defaults in immediate detector path")]
    public void V040RejectedSerrationUsesReversedDefaultsInImmediateDetectorPath()
    {
        var diagnostics = new List<(string Level, string Message)>();
        (TbcFieldDecodePipeline pipeline, RfDecodedSpan span) =
            CreateSingleRejectedSerrationPipeline(
                diagnostics,
                seedDetectorLevel: true);

        SyncPreparedSpan prepared = PrepareSyncSpan(pipeline, span);

        Assert.Equal(120.0, prepared.Threshold);
        Assert.False(prepared.UsedSavedLevels);
        Assert.False(prepared.ExplicitThreshold);
        Assert.Null(pipeline.CaptureState().LastDetectedSyncLevels);
        Assert.Equal(
            [
                (
                    "DEBUG",
                    "VBI serration levels 2 - Sync tip: 0.10 kHz, Blanking (ire0): 0.20 kHz"),
                (
                    "DEBUG",
                    "Level detection had issues, so don't store anything in VsyncSerration."),
                (
                    "DEBUG",
                    "Level check failed on serration measured levels, using defaults.")
            ],
            diagnostics);
    }

    [Fact(DisplayName = "current profile does not use v0.4.0 reversed serration defaults")]
    public void CurrentProfileDoesNotUseReversedSerrationDefaults()
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreateSerrationPipeline(
            diagnostics,
            out VsyncSerrationDetector detector,
            upstreamBehaviorProfile: UpstreamBehaviorProfile.Current);
        RfDecodedSpan span = BuildSerrationFallbackSpan();

        SyncPreparedSpan prepared = PrepareSyncSpan(pipeline, span);

        Assert.Equal(80.0, prepared.Threshold);
        Assert.Same(span, prepared.Span);
        Assert.Equal(1, detector.FieldCount);
        Assert.Empty(diagnostics);
    }

    [Fact(DisplayName = "v0.4.0 reversed defaults preserve clamp behavior")]
    public void V040ReversedDefaultsPreserveClampBehavior()
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreateSerrationPipeline(
            diagnostics,
            out _,
            clampDcOffset: true);
        RfDecodedSpan span = BuildSerrationFallbackSpan();

        SyncPreparedSpan prepared = PrepareSyncSpan(pipeline, span);

        Assert.Equal([50.0, 60.0], prepared.Span.Video);
        Assert.Equal([40.0, 140.0], prepared.Span.VideoLowPass!);
        Assert.Equal([10.0, 20.0], span.Video);
        Assert.Equal([0.0, 100.0], span.VideoLowPass!);
        Assert.Same(span.Input, prepared.Span.Input);
        Assert.Same(span.DemodRaw, prepared.Span.DemodRaw);
    }

    [Fact(DisplayName = "explicit sync threshold bypasses v0.4.0 reversed defaults")]
    public void ExplicitThresholdBypassesReversedDefaults()
    {
        var diagnostics = new List<(string Level, string Message)>();
        TbcFieldDecodePipeline pipeline = CreateSerrationPipeline(
            diagnostics,
            out VsyncSerrationDetector detector);
        RfDecodedSpan span = BuildSerrationFallbackSpan();

        SyncPreparedSpan prepared = PrepareSyncSpan(
            pipeline,
            span,
            explicitThreshold: 123.0);

        Assert.Equal(123.0, prepared.Threshold);
        Assert.True(prepared.ExplicitThreshold);
        Assert.Same(span, prepared.Span);
        Assert.Equal(1, detector.FieldCount);
        Assert.Empty(diagnostics);
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

    private static TbcFieldDecodePipeline CreateSerrationPipeline(
        ICollection<(string Level, string Message)> diagnostics,
        out VsyncSerrationDetector detector,
        UpstreamBehaviorProfile upstreamBehaviorProfile = UpstreamBehaviorProfile.V040,
        bool clampDcOffset = false)
    {
        var converter = new VideoOutputConverter(
            ire0: 100.0,
            hzIre: 1.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var renderer = new TbcFieldRenderer(
            new TbcFrameSpec(
                "PAL",
                OutputLineLength: 4,
                OutputLineCount: 2,
                OutputSampleRateHz: 1_000_000.0,
                ColourBurstStart: null,
                ColourBurstEnd: null,
                ActiveVideoStart: null,
                ActiveVideoEnd: null),
            converter);
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 64.0,
            hsyncPulseUs: 4.7,
            equalizingPulseUs: 2.35,
            vsyncPulseUs: 27.3);
        detector = new VsyncSerrationDetector(
            sampleRateHz: 1_000_000.0,
            framesPerSecond: 25.0,
            frameLines: 625.0,
            equalizingPulseUs: 2.35);
        _ = detector.Analyze([]);
        detector.PushLevels(100.0, 200.0);
        detector.PushLevels(100.0, 200.0);
        return new TbcFieldDecodePipeline(
            analyzer,
            renderer,
            converter,
            "PAL",
            TbcDropoutDetectionOptions.Disabled,
            syncDetectionOptions: new SyncDetectionOptions(
                DetectLevels: true,
                LevelDetectDivisor: 1,
                ClampDcOffset: clampDcOffset),
            decodeType: "vhs",
            vsyncSerrationDetector: detector,
            framesPerSecond: 25.0,
            diagnosticLogger: (level, message) => diagnostics.Add((level, message)),
            upstreamBehaviorProfile: upstreamBehaviorProfile,
            activeVideoStartUs: 12.0);
    }

    private static VhsFieldLevelState GetVhsFieldLevelState(TbcFieldDecodePipeline pipeline)
    {
        FieldInfo field = typeof(TbcFieldDecodePipeline).GetField(
            "_vhsFieldLevelState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(
                nameof(TbcFieldDecodePipeline),
                "_vhsFieldLevelState");
        return Assert.IsType<VhsFieldLevelState>(field.GetValue(pipeline));
    }

    private static (TbcFieldDecodePipeline Pipeline, RfDecodedSpan Span)
        CreateSingleRejectedSerrationPipeline(
            ICollection<(string Level, string Message)> diagnostics,
            bool seedDetectorLevel = false)
    {
        const double sampleRateHz = 4_000_000.0;
        var converter = new VideoOutputConverter(
            ire0: 100.0,
            hzIre: 1.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var analyzer = new SyncAnalyzer(
            sampleRateHz,
            linePeriodUs: 64.0,
            hsyncPulseUs: 4.7,
            equalizingPulseUs: 2.35,
            vsyncPulseUs: 27.3);
        var detector = new VsyncSerrationDetector(
            sampleRateHz,
            framesPerSecond: 25.0,
            frameLines: 625.0,
            equalizingPulseUs: 2.35);
        if (seedDetectorLevel)
        {
            detector.PushLevels(100.0, 200.0);
        }
        var renderer = new TbcFieldRenderer(
            new TbcFrameSpec(
                "PAL",
                OutputLineLength: 4,
                OutputLineCount: 2,
                OutputSampleRateHz: sampleRateHz,
                ColourBurstStart: null,
                ColourBurstEnd: null,
                ActiveVideoStart: null,
                ActiveVideoEnd: null),
            converter);
        var pipeline = new TbcFieldDecodePipeline(
            analyzer,
            renderer,
            converter,
            "PAL",
            TbcDropoutDetectionOptions.Disabled,
            syncDetectionOptions: new SyncDetectionOptions(
                DetectLevels: true,
                LevelDetectDivisor: 1),
            decodeType: "vhs",
            vsyncSerrationDetector: detector,
            framesPerSecond: 25.0,
            diagnosticLogger: (level, message) => diagnostics.Add((level, message)));
        double[] syncReference = Enumerable.Repeat(200.0, detector.LineLength * 400).ToArray();
        int firstPulse = detector.LineLength * 20;
        for (int pulse = 0; pulse < 11; pulse++)
        {
            int start = firstPulse + (pulse * detector.LineLength / 2);
            Array.Fill(syncReference, 100.0, start, detector.EqualizingPulseLength);
        }

        var span = new RfDecodedSpan(
            0,
            [],
            syncReference,
            syncReference,
            VideoLowPass: syncReference);
        return (pipeline, span);
    }

    private static SyncPreparedSpan PrepareSyncSpan(
        TbcFieldDecodePipeline pipeline,
        RfDecodedSpan span,
        double? explicitThreshold = null)
    {
        MethodInfo method = typeof(TbcFieldDecodePipeline).GetMethod(
            "PrepareSyncSpan",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(TbcFieldDecodePipeline), "PrepareSyncSpan");
        return Assert.IsType<SyncPreparedSpan>(method.Invoke(
            pipeline,
            [span, explicitThreshold, true, true]));
    }

    private static RfDecodedSpan BuildSerrationFallbackSpan()
        => new(
            0,
            [1.0, 2.0],
            [10.0, 20.0],
            [30.0, 40.0],
            VideoLowPass: [0.0, 100.0]);

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
