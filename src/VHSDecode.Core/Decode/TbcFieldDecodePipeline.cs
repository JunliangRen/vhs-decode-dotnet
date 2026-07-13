using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Decode;

public sealed record TbcDecodedField(
    long StartSample,
    ushort[] Samples,
    LineLocationResult LineLocations,
    SyncTiming Timing,
    double SyncThresholdHz,
    double MeanLineLength,
    int RawPulseCount,
    int ClassifiedPulseCount,
    bool? DetectedFirstField = null,
    int DetectedFirstFieldConfidence = 0,
    TbcDropoutMap? Dropouts = null,
    TbcOutputPayload? OutputPayload = null,
    short[]? Efm = null,
    short[]? AudioPcm = null,
    short[]? RfTbc = null,
    int EfmTValueCount = 0,
    int AudioSampleCount = 0,
    double? DiskLocation = null,
    double? MedianBurstIre = null,
    int? FieldPhaseId = null,
    IReadOnlyDictionary<string, double>? VitsMetrics = null,
    int[]? VbiData = null,
    double[]? ChromaBurstSamples = null,
    ushort[]? ChromaSamples = null,
    int? BurstStartLine = null,
    double[]? RawInputSamples = null,
    double[]? PreTbcVideoSamples = null,
    double? NextFieldOffsetSamples = null,
    double? NominalFieldLengthSamples = null,
    int SyncConfidence = 100,
    VideoOutputConverter? OutputConverter = null,
    double? BlackToWhiteRfRatio = null,
    bool LaserDiscAgcAdjusted = false)
{
    internal TbcDeferredRenderSource? DeferredRenderSource { get; init; }
}

internal sealed record TbcDeferredRenderSource(
    double[] VideoHz,
    double[] LineLocations,
    int FirstLine,
    int FieldNumber);

public sealed record LaserDiscAnalogAudioOutputOptions(
    double LinePeriodUs,
    int LineCount,
    double OutputFrequency,
    double LeftCarrierHz,
    double RightCarrierHz,
    double FramesPerSecond = 0.0);

public sealed record LaserDiscRfTbcOptions(
    bool WriteRfTbc,
    double VideoWhiteOffsetSamples);

public sealed record LaserDiscVitsLevelSlice(
    int Line,
    double StartUsec,
    double LengthUsec,
    double Percentile);

public sealed record LaserDiscAgcOptions(
    double ColorBurstEndUsec,
    double ActiveVideoStartUsec,
    IReadOnlyList<LaserDiscVitsLevelSlice> WhiteSlices);

public sealed record LaserDiscRfMetricOptions(
    IReadOnlyList<LaserDiscVitsLevelSlice> WhiteSlices,
    LaserDiscVitsLevelSlice BlackSlice,
    int VideoWhiteDelaySamples,
    int VideoSyncDelaySamples);

public sealed record LaserDiscPilotRefineOptions(double PilotMHz);

public sealed record LaserDiscNtscBurstRefineOptions(double FscMHz);

public sealed record HSyncRefineOptions(bool Enabled, bool UseRightHSync)
{
    public static HSyncRefineOptions Default { get; } = new(true, true);

    public static HSyncRefineOptions LeftOnly { get; } = new(true, false);

    public static HSyncRefineOptions Disabled { get; } = new(false, false);
}

public sealed record SyncDetectionOptions(
    bool DetectLevels,
    int LevelDetectDivisor,
    bool UseSavedLevels = false,
    bool ClampDcOffset = false,
    bool UseFallbackVSync = false,
    bool RelaxedLine0 = false,
    bool CvbsAutoSync = false)
{
    public static SyncDetectionOptions Disabled { get; } = new(false, 1);
}

internal sealed record SyncPreparedSpan(
    RfDecodedSpan Span,
    double Threshold,
    bool UsedSavedLevels = false,
    VideoOutputConverter? ConverterOverride = null,
    bool ExplicitThreshold = false);

internal sealed record Line0Resolution(
    double Location,
    bool UsedFallback,
    bool? ExpectedFirstField,
    int ExpectedFirstFieldConfidence,
    bool UsedPreviousEstimate = false,
    double FirstHSyncLocation = double.NaN,
    double UnalignedFirstHSyncLocation = double.NaN);

internal sealed record Line0FallbackCandidate(
    double ExpectedLocation,
    double Location,
    bool? ExpectedFirstField,
    int ExpectedFirstFieldConfidence);

internal sealed record TbcFieldDecodeState(
    (double SyncLevel, double BlankLevel)? LastDetectedSyncLevels,
    (double SyncLevel, double BlankLevel)? DelayedCvbsSyncLevels,
    long? PreviousAnalogAudioStartSample,
    long PreviousAnalogAudioFieldNumber,
    int? ChromaRotationIndex,
    int PreviousBurstDetectedLine,
    double? ChromaAfcCarrierHz,
    double ChromaAfcPhaseRadians,
    double? ChromaAfcPhaseCarrierHz,
    double ChromaAfcPhaseCarrierRadians,
    VideoOutputConverter? LaserDiscAgcConverter,
    VideoOutputConverter? LaserDiscSyncConverter,
    double? PreviousFirstHSyncLocation,
    long? PreviousFirstHSyncReadLocation,
    bool? PreviousDetectedFirstField,
    double PreviousHSyncDifference,
    double LaserDiscNtscPhaseAdjustMedian,
    int? PreviousLaserDiscPalFieldPhaseId,
    IReadOnlyDictionary<int, double>? PreviousLaserDiscPalPhaseAdjustments,
    int PreviousLaserDiscSkipCheckScore);

public sealed class TbcFieldDecodePipeline
{
    private const int MaximumOutputFirstLine = 4;
    private const int LineLocationLookahead = 10;
    private readonly SyncAnalyzer _syncAnalyzer;
    private readonly TbcFieldRenderer _renderer;
    private readonly VideoOutputConverter _videoOutput;
    private readonly string _system;
    private readonly TbcDropoutDetectionOptions _dropoutOptions;
    private readonly int _rfHighPassOffset;
    private readonly int _fieldOrderConfidence;
    private readonly LaserDiscAnalogAudioOutputOptions? _analogAudioOptions;
    private readonly LaserDiscRfTbcOptions? _rfTbcOptions;
    private readonly LaserDiscAgcOptions? _laserDiscAgcOptions;
    private readonly LaserDiscRfMetricOptions? _laserDiscRfMetricOptions;
    private readonly LaserDiscPilotRefineOptions? _laserDiscPilotRefineOptions;
    private readonly LaserDiscNtscBurstRefineOptions? _laserDiscNtscBurstRefineOptions;
    private readonly HSyncRefineOptions _hSyncRefineOptions;
    private readonly SyncDetectionOptions _syncDetectionOptions;
    private readonly bool _decodeLaserDiscVbi;
    private readonly bool _decodeVbiData;
    private readonly bool _preserveRawMetricSources;
    private readonly VhsChromaFieldOptions? _chromaFieldOptions;
    private readonly VsyncSerrationDetector? _vsyncSerrationDetector;
    private readonly string? _decodeType;
    private readonly double? _framesPerSecond;
    private readonly Action<string, string>? _diagnosticLogger;
    private readonly bool _debug;
    private readonly int _inputBlockCutSamples;
    private (double SyncLevel, double BlankLevel)? _lastDetectedSyncLevels;
    private (double SyncLevel, double BlankLevel)? _delayedCvbsSyncLevels;
    private long? _previousAnalogAudioStartSample;
    private long _previousAnalogAudioFieldNumber;
    private int? _chromaRotationIndex;
    private int _previousBurstDetectedLine;
    private double? _chromaAfcCarrierHz;
    private double _chromaAfcPhaseRadians;
    private double? _chromaAfcPhaseCarrierHz;
    private double _chromaAfcPhaseCarrierRadians;
    private VideoOutputConverter? _laserDiscAgcConverter;
    private VideoOutputConverter? _laserDiscSyncConverter;
    private double? _previousFirstHSyncLocation;
    private long? _previousFirstHSyncReadLocation;
    private bool? _previousDetectedFirstField;
    private double _previousHSyncDifference = -1.0;
    private double _laserDiscNtscPhaseAdjustMedian;
    private int? _previousLaserDiscPalFieldPhaseId;
    private IReadOnlyDictionary<int, double>? _previousLaserDiscPalPhaseAdjustments;
    private int _previousLaserDiscSkipCheckScore;

    public TbcFieldDecodePipeline(
        SyncAnalyzer syncAnalyzer,
        TbcFieldRenderer renderer,
        VideoOutputConverter videoOutput,
        string system,
        TbcDropoutDetectionOptions? dropoutOptions = null,
        int rfHighPassOffset = 0,
        int fieldOrderConfidence = 100,
        LaserDiscAnalogAudioOutputOptions? analogAudioOptions = null,
        LaserDiscRfTbcOptions? rfTbcOptions = null,
        LaserDiscAgcOptions? laserDiscAgcOptions = null,
        HSyncRefineOptions? hSyncRefineOptions = null,
        SyncDetectionOptions? syncDetectionOptions = null,
        bool decodeLaserDiscVbi = false,
        bool preserveRawMetricSources = false,
        VhsChromaFieldOptions? chromaFieldOptions = null,
        LaserDiscPilotRefineOptions? laserDiscPilotRefineOptions = null,
        LaserDiscNtscBurstRefineOptions? laserDiscNtscBurstRefineOptions = null,
        string? decodeType = null,
        bool decodeVbiData = false,
        LaserDiscRfMetricOptions? laserDiscRfMetricOptions = null,
        VsyncSerrationDetector? vsyncSerrationDetector = null,
        double? framesPerSecond = null,
        Action<string, string>? diagnosticLogger = null,
        bool debug = false,
        int inputBlockCutSamples = 0)
    {
        _syncAnalyzer = syncAnalyzer;
        _renderer = renderer;
        _videoOutput = videoOutput;
        _system = system;
        _dropoutOptions = dropoutOptions ?? TbcDropoutDetectionOptions.DefaultDdd;
        _rfHighPassOffset = rfHighPassOffset;
        _fieldOrderConfidence = fieldOrderConfidence;
        _analogAudioOptions = analogAudioOptions;
        _rfTbcOptions = rfTbcOptions;
        _laserDiscAgcOptions = laserDiscAgcOptions;
        _laserDiscRfMetricOptions = laserDiscRfMetricOptions;
        _laserDiscPilotRefineOptions = laserDiscPilotRefineOptions;
        _hSyncRefineOptions = hSyncRefineOptions ?? HSyncRefineOptions.Disabled;
        _syncDetectionOptions = syncDetectionOptions ?? SyncDetectionOptions.Disabled;
        _delayedCvbsSyncLevels = renderer.LastCvbsSyncLevels;
        _decodeLaserDiscVbi = decodeLaserDiscVbi;
        _decodeVbiData = decodeLaserDiscVbi || decodeVbiData;
        _preserveRawMetricSources = preserveRawMetricSources;
        _chromaFieldOptions = chromaFieldOptions;
        _chromaRotationIndex = chromaFieldOptions?.InitialChromaRotationIndex;
        _laserDiscNtscBurstRefineOptions = laserDiscNtscBurstRefineOptions;
        _vsyncSerrationDetector = vsyncSerrationDetector;
        _decodeType = decodeType;
        _framesPerSecond = framesPerSecond is > 0.0 ? framesPerSecond : null;
        _diagnosticLogger = diagnosticLogger;
        _debug = debug;
        _inputBlockCutSamples = inputBlockCutSamples >= 0
            ? inputBlockCutSamples
            : throw new ArgumentOutOfRangeException(nameof(inputBlockCutSamples));
    }

    public bool HasChromaOutput => _chromaFieldOptions is not null;

    public VhsChromaFieldOptions? ChromaFieldOptions => _chromaFieldOptions;

    public bool DecodesVbiData => _decodeVbiData;

    internal TbcFieldDecodeState CaptureState()
    {
        return new TbcFieldDecodeState(
            _lastDetectedSyncLevels,
            _delayedCvbsSyncLevels,
            _previousAnalogAudioStartSample,
            _previousAnalogAudioFieldNumber,
            _chromaRotationIndex,
            _previousBurstDetectedLine,
            _chromaAfcCarrierHz,
            _chromaAfcPhaseRadians,
            _chromaAfcPhaseCarrierHz,
            _chromaAfcPhaseCarrierRadians,
            _laserDiscAgcConverter,
            _laserDiscSyncConverter,
            _previousFirstHSyncLocation,
            _previousFirstHSyncReadLocation,
            _previousDetectedFirstField,
            _previousHSyncDifference,
            _laserDiscNtscPhaseAdjustMedian,
            _previousLaserDiscPalFieldPhaseId,
            _previousLaserDiscPalPhaseAdjustments,
            _previousLaserDiscSkipCheckScore);
    }

    internal void RestoreStateForRetry(TbcFieldDecodeState state)
    {
        VideoOutputConverter? adjustedAgcConverter = _laserDiscAgcConverter;
        _lastDetectedSyncLevels = state.LastDetectedSyncLevels;
        _delayedCvbsSyncLevels = state.DelayedCvbsSyncLevels;
        _previousAnalogAudioStartSample = state.PreviousAnalogAudioStartSample;
        _previousAnalogAudioFieldNumber = state.PreviousAnalogAudioFieldNumber;
        _chromaRotationIndex = state.ChromaRotationIndex;
        _previousBurstDetectedLine = state.PreviousBurstDetectedLine;
        _chromaAfcCarrierHz = state.ChromaAfcCarrierHz;
        _chromaAfcPhaseRadians = state.ChromaAfcPhaseRadians;
        _chromaAfcPhaseCarrierHz = state.ChromaAfcPhaseCarrierHz;
        _chromaAfcPhaseCarrierRadians = state.ChromaAfcPhaseCarrierRadians;
        _laserDiscAgcConverter = adjustedAgcConverter ?? state.LaserDiscAgcConverter;
        _laserDiscSyncConverter = state.LaserDiscSyncConverter;
        _previousFirstHSyncLocation = state.PreviousFirstHSyncLocation;
        _previousFirstHSyncReadLocation = state.PreviousFirstHSyncReadLocation;
        _previousDetectedFirstField = state.PreviousDetectedFirstField;
        _previousHSyncDifference = state.PreviousHSyncDifference;
        _laserDiscNtscPhaseAdjustMedian = state.LaserDiscNtscPhaseAdjustMedian;
        _previousLaserDiscPalFieldPhaseId = state.PreviousLaserDiscPalFieldPhaseId;
        _previousLaserDiscPalPhaseAdjustments = state.PreviousLaserDiscPalPhaseAdjustments;
        _previousLaserDiscSkipCheckScore = state.PreviousLaserDiscSkipCheckScore;
    }

    internal void CommitLaserDiscAnalogAudioWrite(TbcDecodedField field, long writtenFieldNumber)
    {
        if (_analogAudioOptions is null || field.AudioPcm is null)
        {
            return;
        }

        _previousAnalogAudioStartSample = field.StartSample;
        _previousAnalogAudioFieldNumber = writtenFieldNumber;
    }

    public static TbcFieldDecodePipeline FromSession(DecodeSession session)
    {
        return new TbcFieldDecodePipeline(
            SyncAnalyzer.FromParameters(
                session.Parameters,
                session.DecodeSampleRateHz,
                hsyncToleranceUs: session.Spec.Name == "vhs" ? 0.7 : 0.5,
                equalizingToleranceUs: session.Spec.Name == "vhs" ? 0.9 : 0.5),
            session.TbcRenderer,
            session.VideoOutput,
            session.System,
            session.DropoutOptions,
            session.Filters.RfHighPassOffset,
            session.FieldOrderOptions.Confidence,
            BuildLaserDiscAnalogAudioOutputOptions(session),
            BuildLaserDiscRfTbcOptions(session),
            BuildLaserDiscAgcOptions(session),
            session.HSyncRefineOptions,
            session.SyncDetectionOptions,
            session.Spec.Name == "ld",
            session.Spec.Name == "ld" && session.ExecutionOptions.VerboseVits,
            BuildChromaFieldOptions(
                session.System,
                session.Parameters,
                session.TbcFrameSpec,
                session.ChromaOptions,
                session.FilterOptions,
                session.DecodeSampleRateHz,
                session.TbcRenderer.TrackPhaseIre0Offset?.TrackPhase),
            BuildLaserDiscPilotRefineOptions(session.Spec.Name, session.System, session.Parameters),
            BuildLaserDiscNtscBurstRefineOptions(session.Spec.Name, session.System, session.Parameters),
            session.Spec.Name,
            decodeVbiData: session.Spec.Name == "cvbs",
            laserDiscRfMetricOptions: BuildLaserDiscRfMetricOptions(
                session.Spec.Name,
                session.Parameters,
                session.Filters),
            vsyncSerrationDetector: BuildVsyncSerrationDetector(
                session.Spec.Name,
                session.System,
                session.Parameters,
                session.DecodeSampleRateHz,
                session.SyncDetectionOptions),
            framesPerSecond: JsonDouble(session.Parameters.SysParams, "FPS"),
            diagnosticLogger: (level, message) => DecodeSessionLogWriter.Append(session, level, message),
            debug: session.ExecutionOptions.Debug,
            inputBlockCutSamples: session.BlockCut);
    }

    public int EstimateReadSampleCount(int extraLines = 3)
    {
        if (extraLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraLines));
        }

        return checked((int)Math.Ceiling(
            (_renderer.FrameSpec.OutputLineCount + MaximumOutputFirstLine + extraLines)
            * _syncAnalyzer.NominalLineLength));
    }

    public int EstimateNominalFieldSampleCount()
    {
        if (_framesPerSecond.HasValue)
        {
            return checked((int)(_syncAnalyzer.SampleRateHz / (_framesPerSecond.Value * 2.0)) + 1);
        }

        return checked((int)Math.Ceiling(_renderer.FrameSpec.OutputLineCount * _syncAnalyzer.NominalLineLength));
    }

    private int[] FieldLinesForParityDetection()
    {
        return FormatCatalog.ParentSystem(_system) == "NTSC"
            ? [_renderer.FrameSpec.OutputLineCount, _renderer.FrameSpec.OutputLineCount - 1]
            : [_renderer.FrameSpec.OutputLineCount - 1, _renderer.FrameSpec.OutputLineCount];
    }

    private int CurrentFieldLineCount(bool? isFirstField)
    {
        if (!isFirstField.HasValue)
        {
            return _renderer.FrameSpec.OutputLineCount;
        }

        int[] fieldLines = FieldLinesForParityDetection();
        return fieldLines[isFirstField.Value ? 0 : 1];
    }


    internal static LaserDiscPilotRefineOptions? BuildLaserDiscPilotRefineOptions(
        string specName,
        string system,
        FormatParameterSet parameters)
    {
        return specName == "ld" && FormatCatalog.ParentSystem(system) == "PAL"
            ? new LaserDiscPilotRefineOptions(JsonDouble(parameters.SysParams, "pilot_mhz"))
            : null;
    }

    internal static LaserDiscNtscBurstRefineOptions? BuildLaserDiscNtscBurstRefineOptions(
        string specName,
        string system,
        FormatParameterSet parameters)
    {
        bool enabled = specName == "ld"
            ? FormatCatalog.ParentSystem(system) == "NTSC"
            : specName == "cvbs" && FormatCatalog.NormalizeSystem(system) == "NTSC";
        return enabled
            ? new LaserDiscNtscBurstRefineOptions(JsonDouble(parameters.SysParams, "fsc_mhz"))
            : null;
    }

    public static int? CvbsFallbackFieldPhaseId(string? decodeType, string system, int fieldNumber)
    {
        if (decodeType != "cvbs")
        {
            return null;
        }

        string normalized = FormatCatalog.NormalizeSystem(system);
        if (normalized is "PAL_M" or "NLINHA")
        {
            return 0;
        }

        return FormatCatalog.ParentSystem(normalized) == "PAL"
            ? 1 + (fieldNumber % 8)
            : null;
    }

    internal static VsyncSerrationDetector? BuildVsyncSerrationDetector(
        string specName,
        string system,
        FormatParameterSet parameters,
        double sampleRateHz,
        SyncDetectionOptions syncDetectionOptions)
    {
        if (specName != "vhs" || system is "405" or "819")
        {
            return null;
        }

        return new VsyncSerrationDetector(
            sampleRateHz,
            JsonDouble(parameters.SysParams, "FPS"),
            JsonDouble(parameters.SysParams, "frame_lines"),
            JsonDouble(parameters.SysParams, "eqPulseUS"),
            syncDetectionOptions.LevelDetectDivisor);
    }

    public TbcDecodedField Decode(RfDecodedSpan span, double? syncThresholdHz = null, int fieldNumber = 0)
        => DecodeCore(span, syncThresholdHz, fieldNumber, deferCvbsOutputConversion: false);

    internal TbcDecodedField DecodeForSequence(RfDecodedSpan span, int fieldNumber)
        => DecodeCore(span, syncThresholdHz: null, fieldNumber, deferCvbsOutputConversion: true);

    internal bool CanDeferCvbsOutputConversion
        => string.Equals(_decodeType, "cvbs", StringComparison.Ordinal)
            && _syncDetectionOptions.CvbsAutoSync
            && _renderer.CvbsClampAgc is null;

    private TbcDecodedField DecodeCore(
        RfDecodedSpan span,
        double? syncThresholdHz,
        int fieldNumber,
        bool deferCvbsOutputConversion)
    {
        (double SyncLevel, double BlankLevel)? previouslyRenderedCvbsLevels = _renderer.LastCvbsSyncLevels;
        SyncPreparedSpan prepared = PrepareSyncSpan(span, syncThresholdHz);
        TbcDecodedField decoded;
        try
        {
            decoded = DecodePrepared(
                prepared,
                fieldNumber,
                deferCvbsOutputConversion && CanDeferCvbsOutputConversion);
        }
        catch (InvalidOperationException ex) when (prepared.UsedSavedLevels && IsSyncLocationFailure(ex))
        {
            _diagnosticLogger?.Invoke("DEBUG", "Search for pulses failed, re-checking levels");
            SyncPreparedSpan retried = PrepareSyncSpan(
                span,
                syncThresholdHz,
                allowSavedLevels: false,
                fallbackToSavedLevels: false);
            decoded = DecodePrepared(
                retried,
                fieldNumber,
                deferCvbsOutputConversion && CanDeferCvbsOutputConversion);
        }

        if (_syncDetectionOptions.CvbsAutoSync && _renderer.CvbsClampAgc is not null)
        {
            // Upstream starts the next field before downscaling the current one,
            // so a clamp measurement is first visible to the following decode.
            _delayedCvbsSyncLevels = previouslyRenderedCvbsLevels;
        }

        return decoded;
    }

    private TbcDecodedField DecodePrepared(
        SyncPreparedSpan prepared,
        int fieldNumber,
        bool deferCvbsOutputConversion)
    {
        RfDecodedSpan span = prepared.Span;
        double threshold = prepared.Threshold;
        VideoOutputConverter activeVideoOutput = prepared.ConverterOverride
            ?? _laserDiscAgcConverter
            ?? _laserDiscSyncConverter
            ?? _videoOutput;
        ReadOnlySpan<double> pulseReference = span.VideoLowPass ?? span.Video;
        IReadOnlyList<Pulse> rawPulses = FindRawPulses(pulseReference, threshold);
        if (string.Equals(_decodeType, "ld", StringComparison.Ordinal)
            && rawPulses.Count == 0
            && fieldNumber == 0
            && !prepared.ExplicitThreshold)
        {
            double ire0 = Percentile(pulseReference, 15.0);
            _laserDiscSyncConverter = new VideoOutputConverter(
                ire0,
                activeVideoOutput.HzIre,
                activeVideoOutput.OutputZero,
                activeVideoOutput.VSyncIre,
                activeVideoOutput.OutputScale);
            activeVideoOutput = _laserDiscSyncConverter;
            threshold = activeVideoOutput.IreToHz(-20.0);
            rawPulses = FindRawPulses(pulseReference, threshold);
        }

        if (string.Equals(_decodeType, "cvbs", StringComparison.Ordinal))
        {
            CvbsPulseDetectionResult? cvbsPulses = CvbsPulseDetector.Refine(
                pulseReference,
                rawPulses,
                threshold,
                _syncAnalyzer,
                activeVideoOutput);
            rawPulses = cvbsPulses?.Pulses ?? [];
            threshold = cvbsPulses?.Threshold ?? threshold;
        }
        else if (string.Equals(_decodeType, "ld", StringComparison.Ordinal))
        {
            CvbsPulseDetectionResult? laserDiscPulses = CvbsPulseDetector.RefineLaserDisc(
                pulseReference,
                rawPulses,
                threshold,
                _syncAnalyzer,
                activeVideoOutput);
            rawPulses = laserDiscPulses?.Pulses ?? [];
            threshold = laserDiscPulses?.Threshold ?? threshold;
        }

        if (rawPulses.Count == 0)
        {
            throw BuildRecoveryException(
                TbcFieldDecodeRecoveryKind.NoSyncPulses,
                "No sync pulses were detected in the decoded span.");
        }

        SyncTiming timing = _syncAnalyzer.EstimateTiming(rawPulses);
        IReadOnlyList<ClassifiedSyncPulse> classified = _syncAnalyzer.ClassifyPulses(rawPulses, timing);
        if (classified.Count == 0)
        {
            throw BuildRecoveryException(
                TbcFieldDecodeRecoveryKind.NoFirstHSync,
                "No classified sync pulses were detected in the decoded span.");
        }

        IReadOnlyList<ClassifiedSyncPulse> refinedPulses = string.Equals(_decodeType, "vhs", StringComparison.Ordinal)
            ? _syncAnalyzer.RefinePulses(
                rawPulses,
                timing,
                span.VideoLowPass ?? span.Video,
                10.0 * _videoOutput.HzIre)
            : _syncAnalyzer.RefinePulses(rawPulses, timing);

        double meanLineLength = _syncAnalyzer.ComputeMeanLineLength(refinedPulses);
        Line0FallbackCandidate? fallback = TryResolveFallbackLine0(
            refinedPulses,
            rawPulses,
            span.VideoLowPass ?? span.Video,
            timing,
            span.StartSample,
            meanLineLength);
        FieldParityDetection parity = FieldParityDetector.ResolveCadence(
            refinedPulses,
            meanLineLength,
            _system,
            FieldLinesForParityDetection(),
            _previousDetectedFirstField,
            _fieldOrderConfidence,
            _previousFirstHSyncLocation.HasValue,
            fallback is not null,
            fallback?.ExpectedFirstField,
            fallback?.ExpectedFirstFieldConfidence ?? -1);
        // Upstream advances field cadence even when line-zero recovery later rejects this span.
        _previousDetectedFirstField = parity.IsFirstField;
        Line0Resolution line0 = ResolveLine0Location(
            refinedPulses,
            refinedPulses,
            rawPulses,
            timing,
            span.VideoLowPass ?? span.Video,
            span.StartSample,
            meanLineLength,
            parity.IsFirstField,
            fallback);
        UpdateSyncHistory(span.StartSample, line0, meanLineLength, parity);
        int currentFieldLineCount = CurrentFieldLineCount(parity.IsFirstField);
        int processedLines = _decodeLaserDiscVbi
            ? LaserDiscProcessedLineCount(parity.IsFirstField)
            : _renderer.FrameSpec.OutputLineCount + LineLocationLookahead;
        ThrowIfInsufficientFieldData(span, rawPulses, line0.Location, meanLineLength, processedLines);
        LineLocationResult lineLocations;
        if (_decodeLaserDiscVbi)
        {
            double? nextVBlankLocation = FindNextVBlankEqualizing1(refinedPulses, line0.Location, meanLineLength);
            lineLocations = LaserDiscLineLocationBuilder.Build(
                refinedPulses,
                line0.Location,
                nextVBlankLocation,
                meanLineLength,
                _syncAnalyzer.NominalLineLength,
                currentFieldLineCount,
                processedLines,
                _renderer.FrameSpec.OutputLineCount).LineLocations;
        }
        else
        {
            double firstHSyncLine = VBlankSyncResolver.FirstHSyncLine(
                _system,
                _syncAnalyzer.NumPulses,
                parity.IsFirstField);
            lineLocations = _syncAnalyzer.BuildUpstreamLineLocations(
                refinedPulses,
                referencePulse: Math.Truncate(line0.FirstHSyncLocation),
                referenceLine: checked((int)firstHSyncLine),
                meanLineLength,
                processedLines);
        }
        double nextFieldOffsetSamples = ComputeNextFieldOffsetSamples(
            refinedPulses,
            lineLocations.Locations,
            line0.Location,
            meanLineLength);
        if (_decodeLaserDiscVbi)
        {
            lineLocations = LaserDiscLineLocationRepair.MarkDerivativeErrors(lineLocations);
        }

        lineLocations = RefineLineLocationsFromHSync(span, lineLocations, activeVideoOutput);
        if (_decodeLaserDiscVbi)
        {
            lineLocations = LaserDiscLineLocationRepair.MarkDerivativeErrors(lineLocations);
        }

        LineLocationResult laserDiscHSyncLineLocations = lineLocations;
        lineLocations = RefineLaserDiscPalLineLocationsFromPilot(
            span,
            lineLocations,
            parity.IsFirstField);
        if (_decodeVbiData && FormatCatalog.ParentSystem(_system) == "PAL")
        {
            lineLocations = LaserDiscLineLocationRepair.FixBadLines(lineLocations, _system);
        }

        int outputFirstLine = OutputFirstLine(parity.IsFirstField);
        double[] renderLineLocations = RenderLineLocations(lineLocations, outputFirstLine);
        double[]? chromaBurstSamples = ResampleChromaBurst(span.Chroma, renderLineLocations, outputFirstLine);
        ChromaPhaseSequenceResult? chromaPhase = AnalyzeChromaPhase(
            chromaBurstSamples,
            lineLocations.Locations,
            _syncAnalyzer.NominalLineLength,
            outputFirstLine);
        if (CanRefineLineLocationsFromBurst(chromaPhase))
        {
            double[] refinedLocations = VhsChromaDecoder.RefineLineLocationsFromBurst(
                lineLocations.Locations,
                _renderer.FrameSpec.OutputLineLength,
                (_renderer.FrameSpec.OutputSampleRateHz / 1_000_000.0) / _chromaFieldOptions!.FscMHz,
                chromaPhase!,
                _chromaFieldOptions.ColorSystem);
            lineLocations = lineLocations with { Locations = refinedLocations };
            renderLineLocations = RenderLineLocations(lineLocations, outputFirstLine);
            chromaBurstSamples = ResampleChromaBurst(span.Chroma, renderLineLocations, outputFirstLine);
        }

        if (string.Equals(_decodeType, "vhs", StringComparison.Ordinal)
            && FormatCatalog.ParentSystem(_system) == "NTSC")
        {
            double fscMHz = _renderer.FrameSpec.OutputSampleRateHz / 4_000_000.0;
            lineLocations = ApplyNtscFscPhaseShiftCore(lineLocations, fscMHz);
            renderLineLocations = RenderLineLocations(lineLocations, outputFirstLine);
            chromaBurstSamples = ResampleChromaBurst(span.Chroma, renderLineLocations, outputFirstLine);
        }

        int? laserDiscFieldPhaseId = null;
        (LineLocationResult ntscBurstLineLocations, laserDiscFieldPhaseId) = RefineLaserDiscNtscLineLocationsFromBurst(
            span,
            lineLocations,
            parity.IsFirstField,
            activeVideoOutput);
        if (_decodeLaserDiscVbi
            && _laserDiscNtscBurstRefineOptions is not null
            && FormatCatalog.ParentSystem(_system) == "NTSC"
            && span.VideoBurst is { Length: > 0 })
        {
            LineLocationResult shiftedBackup = ApplyLaserDiscNtscFscPhaseShift(laserDiscHSyncLineLocations);
            ntscBurstLineLocations = LaserDiscLineLocationRepair.FixBadLines(
                ntscBurstLineLocations,
                _system,
                shiftedBackup);
        }
        if (!ReferenceEquals(ntscBurstLineLocations.Locations, lineLocations.Locations))
        {
            lineLocations = ntscBurstLineLocations;
            renderLineLocations = RenderLineLocations(lineLocations, outputFirstLine);
            chromaBurstSamples = ResampleChromaBurst(span.Chroma, renderLineLocations, outputFirstLine);
        }

        int syncConfidence = SyncConfidenceCalculator.Compute(
            lineLocations.Locations,
            currentFieldLineCount,
            lineOffset: Math.Max(0, outputFirstLine - 1));
        (VideoOutputConverter? agcConverter, bool laserDiscAgcAdjusted) = ResolveLaserDiscAgcConverter(
            span,
            lineLocations.Locations,
            meanLineLength,
            parity.IsFirstField,
            syncConfidence,
            activeVideoOutput,
            fieldNumber);
        VideoOutputConverter? fieldConverter = agcConverter
            ?? prepared.ConverterOverride
            ?? _laserDiscAgcConverter
            ?? _laserDiscSyncConverter;
        if (_decodeLaserDiscVbi)
        {
            _previousLaserDiscSkipCheckScore = LaserDiscPlayerSkipDetector.ScorePreviousFieldEnd(
                span.Video,
                lineLocations.Locations,
                _renderer.FrameSpec.OutputLineCount,
                LaserDiscLineOffset(parity.IsFirstField),
                _syncAnalyzer.NominalLineLength,
                fieldConverter ?? _videoOutput);
        }

        TbcDeferredRenderSource? deferredRenderSource = deferCvbsOutputConversion
            ? new TbcDeferredRenderSource(
                span.Video,
                renderLineLocations,
                outputFirstLine,
                fieldNumber)
            : null;
        TbcRenderedField rendered = deferredRenderSource is null
            ? _renderer.RenderFieldPayload(
                span.Video,
                renderLineLocations,
                firstLine: outputFirstLine,
                fieldNumber: fieldNumber,
                converterOverride: fieldConverter)
            : new TbcRenderedField([]);
        double? blackToWhiteRfRatio = ComputeLaserDiscBlackToWhiteRfRatio(
            span.Input,
            rendered.Samples,
            lineLocations.Locations,
            parity.IsFirstField == true,
            fieldConverter ?? _videoOutput);
        TbcDropoutMap? dropouts = DetectDropouts(span, lineLocations, timing, parity.IsFirstField);
        short[]? efm = SliceFieldEfm(span.Efm, lineLocations.Locations, currentFieldLineCount);
        short[]? audioPcm = DownscaleAnalogAudio(
            span.AnalogAudio,
            lineLocations.Locations,
            currentFieldLineCount,
            span.StartSample,
            fieldNumber,
            parity.IsFirstField);
        short[]? rfTbc = BuildRfTbc(
            span.Input,
            lineLocations.Locations,
            currentFieldLineCount);
        int[]? vbiData = DecodeLaserDiscVbiData(span.Video, lineLocations, parity.IsFirstField);
        IReadOnlyList<double> burstLevelLineLocations = _decodeLaserDiscVbi
            && FormatCatalog.ParentSystem(_system) == "NTSC"
                ? laserDiscHSyncLineLocations.Locations
                : lineLocations.Locations;
        double? medianBurstIre = ComputeLaserDiscMedianBurstIre(
            span.Video,
            burstLevelLineLocations,
            parity.IsFirstField,
            fieldConverter ?? _videoOutput);
        laserDiscFieldPhaseId ??= DetermineLaserDiscPalFieldPhase(
            span,
            lineLocations,
            parity.IsFirstField,
            medianBurstIre,
            fieldConverter ?? _videoOutput);
        laserDiscFieldPhaseId ??= CvbsFallbackFieldPhaseId(_decodeType, _system, fieldNumber);
        VhsChromaFieldResult? chroma = DecodeChromaField(
            chromaBurstSamples,
            chromaPhase,
            parity.IsFirstField,
            fieldNumber,
            outputFirstLine);
        if (chroma is not null)
        {
            CommitChromaState(chroma);
        }

        return new TbcDecodedField(
            span.StartSample,
            rendered.Samples,
            lineLocations,
            timing,
            threshold,
            meanLineLength,
            rawPulses.Count,
            classified.Count,
            parity.IsFirstField,
            parity.Confidence,
            dropouts,
            rendered.OutputPayload,
            efm,
            audioPcm,
            rfTbc,
            FieldPhaseId: chroma?.FieldPhaseId ?? laserDiscFieldPhaseId,
            MedianBurstIre: medianBurstIre,
            VbiData: vbiData,
            ChromaBurstSamples: chromaBurstSamples,
            ChromaSamples: chroma?.Samples,
            BurstStartLine: chroma?.BurstDetectedLine,
            RawInputSamples: _preserveRawMetricSources ? span.Input : null,
            PreTbcVideoSamples: _preserveRawMetricSources ? span.Video : null,
            NextFieldOffsetSamples: nextFieldOffsetSamples,
            NominalFieldLengthSamples: currentFieldLineCount * meanLineLength,
            SyncConfidence: syncConfidence,
            OutputConverter: fieldConverter,
            BlackToWhiteRfRatio: blackToWhiteRfRatio,
            LaserDiscAgcAdjusted: laserDiscAgcAdjusted)
        {
            DeferredRenderSource = deferredRenderSource
        };
    }

    private double? ComputeLaserDiscBlackToWhiteRfRatio(
        ReadOnlySpan<double> rawInput,
        ReadOnlySpan<ushort> output,
        IReadOnlyList<double> lineLocations,
        bool isFirstField,
        VideoOutputConverter converter)
    {
        if (_laserDiscRfMetricOptions is null || rawInput.IsEmpty || output.IsEmpty)
        {
            return null;
        }

        double? whiteRfLevel = null;
        foreach (LaserDiscVitsLevelSlice slice in _laserDiscRfMetricOptions.WhiteSlices)
        {
            if (!TryGetTbcMetricSlice(output, slice, out ReadOnlySpan<ushort> whiteOutput))
            {
                continue;
            }

            double whiteIre = 0.0;
            for (int i = 0; i < whiteOutput.Length; i++)
            {
                whiteIre += converter.OutputToIre(whiteOutput[i]);
            }

            whiteIre /= whiteOutput.Length;
            if (whiteIre < 90.0 || whiteIre > 110.0)
            {
                continue;
            }

            if (TryGetRawRfMetricSlice(
                    rawInput,
                    lineLocations,
                    slice,
                    isFirstField,
                    _laserDiscRfMetricOptions.VideoWhiteDelaySamples,
                    out ReadOnlySpan<double> whiteRaw))
            {
                whiteRfLevel = StandardDeviation(whiteRaw);
            }

            break;
        }

        if (!whiteRfLevel.HasValue || whiteRfLevel.Value == 0.0)
        {
            return null;
        }

        if (!TryGetRawRfMetricSlice(
                rawInput,
                lineLocations,
                _laserDiscRfMetricOptions.BlackSlice,
                isFirstField,
                _laserDiscRfMetricOptions.VideoSyncDelaySamples,
                out ReadOnlySpan<double> blackRaw))
        {
            return null;
        }

        double ratio = StandardDeviation(blackRaw) / whiteRfLevel.Value;
        return double.IsFinite(ratio)
            ? Math.Round(ratio, 4, MidpointRounding.ToEven)
            : null;
    }

    private bool TryGetTbcMetricSlice(
        ReadOnlySpan<ushort> output,
        LaserDiscVitsLevelSlice slice,
        out ReadOnlySpan<ushort> values)
    {
        double begin = ((slice.Line - 1) * _renderer.FrameSpec.OutputLineLength)
            + (slice.StartUsec * (_renderer.FrameSpec.OutputSampleRateHz / 1_000_000.0));
        int start = (int)Math.Round(begin, MidpointRounding.ToEven);
        int end = (int)Math.Round(
            begin + (slice.LengthUsec * (_renderer.FrameSpec.OutputSampleRateHz / 1_000_000.0)),
            MidpointRounding.ToEven);
        if (slice.Line <= 0 || start < 0 || end <= start || end > output.Length)
        {
            values = default;
            return false;
        }

        values = output.Slice(start, end - start);
        return true;
    }

    private bool TryGetRawRfMetricSlice(
        ReadOnlySpan<double> rawInput,
        IReadOnlyList<double> lineLocations,
        LaserDiscVitsLevelSlice slice,
        bool isFirstField,
        int delaySamples,
        out ReadOnlySpan<double> values)
    {
        int physicalLine = slice.Line + LaserDiscLineOffset(isFirstField);
        if (physicalLine <= 0 || physicalLine >= lineLocations.Count || slice.LengthUsec <= 0.0)
        {
            values = default;
            return false;
        }

        double begin = lineLocations[physicalLine]
            + (slice.StartUsec * LaserDiscLineSamplesPerUsec(lineLocations, physicalLine));
        int start = (int)Math.Floor(begin) - delaySamples;
        int end = (int)Math.Floor(begin + _syncAnalyzer.UsecToSamples(slice.LengthUsec) + 1.0) - delaySamples;
        if (start < 0 || end <= start || end > rawInput.Length)
        {
            values = default;
            return false;
        }

        values = rawInput.Slice(start, end - start);
        return true;
    }

    private double ComputeNextFieldOffsetSamples(
        IReadOnlyList<ClassifiedSyncPulse> classified,
        IReadOnlyList<double> lineLocations,
        double line0Location,
        double meanLineLength)
    {
        double? nextVBlankLine0 = FindNextVBlankLine0(classified, line0Location, meanLineLength);
        if (nextVBlankLine0.HasValue)
        {
            double fromVBlank = nextVBlankLine0.Value
                - (8.0 * _syncAnalyzer.NominalLineLength)
                + _inputBlockCutSamples;
            if (double.IsFinite(fromVBlank) && fromVBlank > 0.0)
            {
                return fromVBlank;
            }
        }

        int fallbackLine = Math.Max(0, _renderer.FrameSpec.OutputLineCount - 7);
        if (lineLocations.Count > fallbackLine)
        {
            double fromLineLocations = lineLocations[fallbackLine];
            if (double.IsFinite(fromLineLocations) && fromLineLocations > 0.0)
            {
                return fromLineLocations + _inputBlockCutSamples;
            }
        }

        return Math.Max(
            1.0,
            line0Location + (fallbackLine * meanLineLength) + _inputBlockCutSamples);
    }

    private double? FindNextVBlankLine0(
        IReadOnlyList<ClassifiedSyncPulse> classified,
        double line0Location,
        double meanLineLength)
    {
        VBlankPulseGroup? group = FindValidVBlankGroup(
            classified,
            meanLineLength,
            line0Location + (40.0 * meanLineLength));
        if (group is null)
        {
            return null;
        }

        double firstLocation;
        int pulseType;
        if (group.VSyncStart.HasValue)
        {
            firstLocation = group.VSyncStart.Value;
            pulseType = (int)SyncPulseKind.VSync;
        }
        else
        {
            firstLocation = group.Equalizing1Start;
            pulseType = (int)SyncPulseKind.Equalizing;
        }

        double inputLineLength = _syncAnalyzer.NominalLineLength;
        double distanceFromLine1 = ((pulseType - 1) * _syncAnalyzer.NumPulses) * 0.5;
        double distanceFromPreviousHSync = (firstLocation - group.PreviousHSync) / inputLineLength;
        long halfLineDistance = checked((long)Math.Round(
            distanceFromPreviousHSync * 2.0,
            MidpointRounding.ToEven));

        bool isPal = FormatCatalog.ParentSystem(_system) == "PAL";
        bool firstField = (halfLineDistance & 1L) == (isPal ? 1L : 0L);
        if ((((long)(distanceFromLine1 * 2.0)) & 1L) != 0L)
        {
            firstField = !firstField;
        }

        double equalizingGap = isPal
            ? firstField ? 0.5 : 1.0
            : firstField ? 1.0 : 0.5;
        return Math.Truncate(
            firstLocation - ((equalizingGap + distanceFromLine1) * inputLineLength));
    }

    private IReadOnlyList<Pulse> FindRawPulses(ReadOnlySpan<double> syncReference, double threshold)
    {
        if (string.Equals(_decodeType, "vhs", StringComparison.Ordinal))
        {
            return PulseDetection.FindPulses(
                syncReference,
                threshold,
                minimumSyncLength: Math.Max(0, (int)Math.Ceiling(_syncAnalyzer.UsecToSamples(_syncAnalyzer.EqualizingPulseUs) / 8.0)),
                maximumSyncLength: Math.Max(1, (int)(_syncAnalyzer.NominalLineLength * 5.0)));
        }

        if (_decodeType is "cvbs" or "ld")
        {
            return PulseDetection.FindPulses(
                syncReference,
                threshold,
                minimumSyncLength: 0,
                maximumSyncLength: 5000);
        }

        return _syncAnalyzer.FindRawPulses(
            syncReference,
            threshold,
            minimumPulseUs: Math.Max(0.1, _syncAnalyzer.EqualizingPulseUs - 1.0),
            maximumPulseUs: _syncAnalyzer.VSyncPulseUs + 2.0);
    }

    private double? FindNextVBlankEqualizing1(
        IReadOnlyList<ClassifiedSyncPulse> classified,
        double line0Location,
        double meanLineLength)
    {
        return FindValidVBlankGroup(
            classified,
            meanLineLength,
            line0Location + (40.0 * meanLineLength))?.Equalizing1Start;
    }

    private static bool IsSyncLocationFailure(InvalidOperationException ex)
    {
        string message = ex.Message;
        return message.Contains("No sync pulses", StringComparison.Ordinal)
            || message.Contains("No classified sync pulses", StringComparison.Ordinal)
            || message.Contains("No HSYNC pulse", StringComparison.Ordinal)
            || message.Contains("No valid line locations", StringComparison.Ordinal);
    }

    private int OutputFirstLine(bool? isFirstField)
    {
        if (FormatCatalog.ParentSystem(_system) == "NTSC")
        {
            return 1;
        }

        return isFirstField == false ? 4 : 3;
    }

    private double[] RenderLineLocations(LineLocationResult lineLocations, int outputFirstLine)
    {
        int requiredLocations = checked(outputFirstLine + _renderer.FrameSpec.OutputLineCount + 1);
        if (lineLocations.Locations.Length < requiredLocations)
        {
            throw new InvalidOperationException(
                $"Field has {lineLocations.Locations.Length} line locations but {requiredLocations} are required for TBC output.");
        }

        return lineLocations.Locations.ToArray();
    }

    private bool CanRefineLineLocationsFromBurst(ChromaPhaseSequenceResult? phase)
    {
        return phase is not null
            && _chromaFieldOptions is { DisableBurstHsync: false }
            && phase.BurstDetectedLine != -1;
    }

    private double[]? ResampleChromaBurst(
        double[]? chroma,
        IReadOnlyList<double> lineLocations,
        int outputFirstLine)
    {
        if (chroma is null || chroma.Length == 0)
        {
            return null;
        }

        return _renderer.ResampleField(chroma, lineLocations, firstLine: outputFirstLine);
    }

    private ChromaPhaseSequenceResult? AnalyzeChromaPhase(
        double[]? chromaBurstSamples,
        IReadOnlyList<double> lineLocations,
        double meanLineLength,
        int outputFirstLine)
    {
        if (_chromaFieldOptions is null || chromaBurstSamples is null)
        {
            return null;
        }

        return VhsChromaDecoder.AnalyzeFieldPhase(
            chromaBurstSamples,
            _chromaFieldOptions,
            lineLocations,
            Math.Max(1, (int)Math.Round(meanLineLength)),
            _chromaRotationIndex,
            _previousBurstDetectedLine,
            lineOffset: outputFirstLine,
            previousChromaAfcCarrierHz: _chromaAfcPhaseCarrierHz,
            previousChromaAfcPhaseRadians: _chromaAfcPhaseCarrierRadians);
    }

    private VhsChromaFieldResult? DecodeChromaField(
        double[]? chromaBurstSamples,
        ChromaPhaseSequenceResult? phase,
        bool? isFirstField,
        int fieldNumber,
        int outputFirstLine)
    {
        if (_chromaFieldOptions is null || chromaBurstSamples is null || phase is null)
        {
            return null;
        }

        return VhsChromaDecoder.DecodeFieldWithPhase(
            chromaBurstSamples,
            _chromaFieldOptions,
            phase,
            isFirstField,
            fieldNumber,
            lineOffset: outputFirstLine,
            previousChromaAfcCarrierHz: _chromaAfcCarrierHz,
            previousChromaAfcPhaseRadians: _chromaAfcPhaseRadians);
    }

    private void CommitChromaState(VhsChromaFieldResult chroma)
    {
        _chromaRotationIndex = chroma.NextChromaRotationIndex;
        _previousBurstDetectedLine = chroma.BurstDetectedLine;
        if (chroma.CarrierEstimate is { } carrier)
        {
            _chromaAfcPhaseCarrierHz = _chromaAfcCarrierHz;
            _chromaAfcPhaseCarrierRadians = _chromaAfcPhaseRadians;
            _chromaAfcCarrierHz = carrier.CarrierHz;
            _chromaAfcPhaseRadians = carrier.PhaseRadians;
        }
    }

    internal static VhsChromaFieldOptions? BuildChromaFieldOptions(
        string system,
        FormatParameterSet parameters,
        TbcFrameSpec frameSpec,
        ChromaDecodeOptions? chromaOptions,
        DecodeFilterOptions? filterOptions = null,
        double decodeSampleRateHz = 40_000_000.0,
        int? initialChromaRotationIndex = null)
    {
        if (chromaOptions?.WriteChroma != true
            || !parameters.SysParams.TryGetProperty("colorBurstUS", out JsonElement colorBurstRange)
            || colorBurstRange.ValueKind != JsonValueKind.Array
            || colorBurstRange.GetArrayLength() < 2)
        {
            return null;
        }

        double outputSamplesPerUsec = JsonDouble(parameters.SysParams, "outfreq");
        int burstStart = Math.Clamp(
            checked((int)Math.Floor(colorBurstRange[0].GetDouble() * outputSamplesPerUsec) - 5),
            0,
            frameSpec.OutputLineLength);
        int burstEnd = Math.Clamp(
            checked((int)Math.Ceiling(colorBurstRange[1].GetDouble() * outputSamplesPerUsec) + 10),
            burstStart,
            frameSpec.OutputLineLength);
        filterOptions ??= new DecodeFilterOptions();
        return new VhsChromaFieldOptions(
            system,
            frameSpec.OutputLineLength,
            frameSpec.OutputLineCount,
            frameSpec.OutputSampleRateHz,
            JsonDouble(parameters.SysParams, "fsc_mhz"),
            JsonDouble(parameters.RfParams, "color_under_carrier"),
            burstStart,
            burstEnd,
            JsonDouble(parameters.SysParams, "burst_abs_ref"),
            ReadIntArrayOrNull(parameters.RfParams, "chroma_rotation"),
            chromaOptions.DisableComb,
            chromaOptions.DisablePhaseCorrection,
            chromaOptions.EnableColorKiller,
            chromaOptions.DetectChromaTrackPhase)
        {
            FinalFilter = DecodeFilterSetBuilder.BuildChromaFinalFilter(parameters, frameSpec.OutputSampleRateHz, chromaOptions.IsColorUnder),
            FinalSosFilter = DecodeFilterSetBuilder.BuildChromaFinalSosFilter(parameters, frameSpec.OutputSampleRateHz, chromaOptions.IsColorUnder),
            ChromaDeemphasisFilter = chromaOptions.ChromaDeemphasisFilter
                ? DecodeFilterSetBuilder.BuildChromaDeemphasisFilter(parameters, frameSpec.OutputSampleRateHz)
                : null,
            ChromaPreFilter = chromaOptions.UseChromaAfc
                ? DecodeFilterSetBuilder.BuildChromaAfcBandPassFilter(parameters, decodeSampleRateHz)
                : null,
            ChromaPreSosFilter = chromaOptions.UseChromaAfc
                ? DecodeFilterSetBuilder.BuildChromaAfcBandPassSosFilter(parameters, decodeSampleRateHz)
                : null,
            ChromaAudioNotchFilter = chromaOptions.UseChromaAfc && chromaOptions.ChromaAudioNotch
                ? DecodeFilterSetBuilder.BuildChromaAudioNotchFilter(parameters, frameSpec.OutputSampleRateHz)
                : null,
            ChromaVideoNotchFilter = chromaOptions.UseChromaAfc
                ? DecodeFilterSetBuilder.BuildChromaVideoNotchFilter(filterOptions, frameSpec.OutputSampleRateHz)
                : null,
            ChromaPreFilterMoveSamples = chromaOptions.UseChromaAfc
                ? (int)(10.0 * (frameSpec.OutputSampleRateHz / 40_000_000.0))
                : 0,
            ChromaAfcTrackCarrier = chromaOptions.UseChromaAfc,
            ChromaAfcLineFrequencyHz = chromaOptions.UseChromaAfc
                ? JsonDouble(parameters.SysParams, "FPS") * JsonInt(parameters.SysParams, "frame_lines")
                : 0.0,
            ChromaAfcFineTuneStepHz = chromaOptions.UseChromaAfc
                ? ChromaAfcFineTuneStepHz(parameters)
                : 0.0,
            ChromaAfcMeasurementFilters = chromaOptions.UseChromaAfc
                ? DecodeFilterSetBuilder.BuildChromaAfcMeasurementFilters(parameters, frameSpec.OutputSampleRateHz)
                : null,
            ChromaAfcPreFilterLowHz = chromaOptions.UseChromaAfc
                ? JsonDoubleOrDefault(parameters.RfParams, "chroma_bpf_lower", 60_000.0)
                : 0.0,
            ChromaAfcPreFilterUpperRatio = chromaOptions.UseChromaAfc
                ? JsonDouble(parameters.RfParams, "chroma_bpf_upper") / JsonDouble(parameters.RfParams, "color_under_carrier")
                : 0.0,
            ChromaAfcPreFilterOrder = chromaOptions.UseChromaAfc
                ? JsonIntOrDefault(parameters.RfParams, "chroma_bpf_order", 4)
                : 0,
            ChromaAfcDecodeSampleRateHz = chromaOptions.UseChromaAfc ? decodeSampleRateHz : 0.0,
            DisableBurstHsync = chromaOptions.DisableBurstHsync,
            InitialChromaRotationIndex = initialChromaRotationIndex
        };
    }

    private static double ChromaAfcFineTuneStepHz(FormatParameterSet parameters)
    {
        double lineFrequency = JsonDouble(parameters.SysParams, "FPS") * JsonInt(parameters.SysParams, "frame_lines");
        return parameters.TapeFormat switch
        {
            "UMATIC" => lineFrequency,
            "BETAMAX" => lineFrequency / 2.0,
            _ => lineFrequency / 4.0
        };
    }

    private SyncPreparedSpan PrepareSyncSpan(
        RfDecodedSpan span,
        double? explicitThreshold,
        bool allowSavedLevels = true,
        bool fallbackToSavedLevels = true)
    {
        if (explicitThreshold.HasValue)
        {
            return new SyncPreparedSpan(span, explicitThreshold.Value, ExplicitThreshold: true);
        }

        VideoOutputConverter? converterOverride = null;
        if (_syncDetectionOptions.CvbsAutoSync)
        {
            ReadOnlySpan<double> cvbsSyncReference = span.VideoLowPass is { Length: > 0 } cvbsLowPass
                ? cvbsLowPass
                : span.Video;
            CvbsSyncLevels? cvbsLevels = _delayedCvbsSyncLevels is { } measured
                ? new CvbsSyncLevels(measured.SyncLevel, measured.BlankLevel)
                : CvbsSyncLevelDetector.Find(cvbsSyncReference, _syncAnalyzer);
            if (cvbsLevels.HasValue)
            {
                double hzIre = (cvbsLevels.Value.BlankLevel - cvbsLevels.Value.SyncLevel) / -_videoOutput.VSyncIre;
                if (double.IsFinite(hzIre) && hzIre != 0.0)
                {
                    converterOverride = new VideoOutputConverter(
                        cvbsLevels.Value.BlankLevel,
                        hzIre,
                        _videoOutput.OutputZero,
                        _videoOutput.VSyncIre,
                        _videoOutput.OutputScale);
                }
            }
        }

        VideoOutputConverter thresholdConverter = converterOverride
            ?? _laserDiscAgcConverter
            ?? _laserDiscSyncConverter
            ?? _videoOutput;
        double defaultThreshold = thresholdConverter.IreToHz(thresholdConverter.VSyncIre / 2.0);

        if (!_syncDetectionOptions.DetectLevels)
        {
            return new SyncPreparedSpan(span, defaultThreshold, ConverterOverride: converterOverride);
        }

        ReadOnlySpan<double> syncReference = span.VideoLowPass is { Length: > 0 } lowPass
            ? lowPass
            : span.Video;
        if (_debug && string.Equals(_decodeType, "vhs", StringComparison.Ordinal))
        {
            string hash = Convert.ToHexString(MD5.HashData(MemoryMarshal.AsBytes(syncReference))).ToLowerInvariant();
            _diagnosticLogger?.Invoke("DEBUG", "Hashed field sync reference " + hash);
        }

        if (allowSavedLevels && _syncDetectionOptions.UseSavedLevels && _lastDetectedSyncLevels.HasValue)
        {
            return PrepareSyncSpanFromLevels(span, _lastDetectedSyncLevels.Value, usedSavedLevels: true);
        }

        double referenceSyncLevel = _videoOutput.IreToHz(_videoOutput.VSyncIre);
        (double SyncLevel, double BlankLevel)? savedSerrationLevels = null;
        if (_vsyncSerrationDetector is not null)
        {
            int serrationFieldNumber = _vsyncSerrationDetector.FieldCount;
            VsyncSerrationResult serration = _vsyncSerrationDetector.Analyze(syncReference);
            if (serration.FoundSerration
                && serration.HasLevels
                && serration.SyncLevel.HasValue
                && serration.BlankLevel.HasValue)
            {
                _diagnosticLogger?.Invoke(
                    "DEBUG",
                    FormattableString.Invariant(
                        $"VBI serration levels {_vsyncSerrationDetector.LevelCount} - Sync tip: {serration.SyncLevel.Value / 1e3:F2} kHz, Blanking (ire0): {serration.BlankLevel.Value / 1e3:F2} kHz"));
            }
            else if (serrationFieldNumber % 10 == 0)
            {
                _diagnosticLogger?.Invoke(
                    "DEBUG",
                    "VBI EQ serration pulses search failed (using fallback logic)");
            }

            double? serrationSyncLevel = serration.SyncLevel;
            double? serrationBlankLevel = serration.BlankLevel;
            if (serration.FoundSerration
                && serrationSyncLevel.HasValue
                && serrationBlankLevel.HasValue)
            {
                SerrationLevelRefinement? refined = LevelDetection.RefineSerrationLevels(
                    syncReference,
                    serrationSyncLevel.Value,
                    serrationBlankLevel.Value,
                    _syncAnalyzer,
                    referenceSyncLevel,
                    _videoOutput.HzIre);
                if (refined is not null)
                {
                    _vsyncSerrationDetector.PushLevels(refined.SyncLevel, refined.BlankLevel);
                    (double SyncLevel, double BlankLevel)? averaged = _vsyncSerrationDetector.PullLevels();
                    serrationSyncLevel = averaged?.SyncLevel;
                    serrationBlankLevel = averaged?.BlankLevel;
                }
            }

            if ((serration.FoundSerration || serration.HasLevels)
                && serrationSyncLevel.HasValue
                && serrationBlankLevel.HasValue
                && VsyncSerrationDetector.CheckLevels(
                    syncReference,
                    referenceSyncLevel,
                    serrationSyncLevel.Value,
                    serrationBlankLevel.Value,
                    referenceSyncLevel,
                    _videoOutput.HzIre))
            {
                savedSerrationLevels = (serrationSyncLevel.Value, serrationBlankLevel.Value);
                if (serration.FoundSerration)
                {
                    _lastDetectedSyncLevels = savedSerrationLevels;
                    return PrepareSyncSpanFromLevels(span, _lastDetectedSyncLevels.Value, usedSavedLevels: false);
                }
            }
        }

        if (string.Equals(_decodeType, "vhs", StringComparison.Ordinal))
        {
            SerrationLevelRefinement? fallbackRefinement = LevelDetection.SearchFallbackSerrationLevels(
                syncReference,
                _syncAnalyzer,
                _syncDetectionOptions.LevelDetectDivisor,
                _videoOutput.IreToHz(0.0),
                referenceSyncLevel,
                _videoOutput.HzIre,
                _syncDetectionOptions.UseFallbackVSync,
                out SerrationLevelFailureKind failureKind);
            if (fallbackRefinement is not null)
            {
                _vsyncSerrationDetector?.PushLevels(
                    fallbackRefinement.SyncLevel,
                    fallbackRefinement.BlankLevel);
                _lastDetectedSyncLevels = (
                    fallbackRefinement.SyncLevel,
                    fallbackRefinement.BlankLevel);
                return PrepareSyncSpanFromLevels(span, _lastDetectedSyncLevels.Value, usedSavedLevels: false);
            }

            if (failureKind == SerrationLevelFailureKind.NonFiniteLevels)
            {
                _diagnosticLogger?.Invoke("DEBUG", "blacklevel or synclevel had a NaN!");
            }
            else if (failureKind == SerrationLevelFailureKind.LevelCheckFailed)
            {
                _diagnosticLogger?.Invoke("DEBUG", "level check failed in pulses_levels!");
            }

            _diagnosticLogger?.Invoke("DEBUG", "Level detection failed - sync or blank is None");

            if (savedSerrationLevels.HasValue)
            {
                _lastDetectedSyncLevels = savedSerrationLevels.Value;
                return PrepareSyncSpanFromLevels(span, savedSerrationLevels.Value, usedSavedLevels: false);
            }

            return fallbackToSavedLevels && _lastDetectedSyncLevels.HasValue
                ? PrepareSyncSpanFromLevels(span, _lastDetectedSyncLevels.Value, usedSavedLevels: true)
                : new SyncPreparedSpan(span, defaultThreshold);
        }

        (double syncLevel, double blankLevel)? levels = LevelDetection.FindSyncLevels(
            syncReference,
            _syncAnalyzer.NominalLineLength,
            _syncDetectionOptions.LevelDetectDivisor);
        if (levels.HasValue)
        {
            _lastDetectedSyncLevels = levels.Value;
            return PrepareSyncSpanFromLevels(span, levels.Value, usedSavedLevels: false);
        }

        return fallbackToSavedLevels && _lastDetectedSyncLevels.HasValue
            ? PrepareSyncSpanFromLevels(span, _lastDetectedSyncLevels.Value, usedSavedLevels: true)
            : new SyncPreparedSpan(span, defaultThreshold);
    }

    private SyncPreparedSpan PrepareSyncSpanFromLevels(
        RfDecodedSpan span,
        (double SyncLevel, double BlankLevel) levels,
        bool usedSavedLevels)
    {
        if (!_syncDetectionOptions.ClampDcOffset)
        {
            return new SyncPreparedSpan(span, SyncThresholdFromLevels(levels), usedSavedLevels);
        }

        double dcOffset = _videoOutput.Ire0 - levels.BlankLevel;
        if (!double.IsFinite(dcOffset) || dcOffset == 0.0)
        {
            return new SyncPreparedSpan(span, SyncThresholdFromLevels(levels), usedSavedLevels);
        }

        var adjustedLevels = (levels.SyncLevel + dcOffset, levels.BlankLevel + dcOffset);
        return new SyncPreparedSpan(
            span with
            {
                Video = AddDcOffset(span.Video, dcOffset),
                VideoLowPass = span.VideoLowPass is null ? null : AddDcOffset(span.VideoLowPass, dcOffset)
            },
            SyncThresholdFromLevels(adjustedLevels),
            usedSavedLevels);
    }

    private static double[] AddDcOffset(double[] input, double offset)
    {
        double[] output = new double[input.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = input[i] + offset;
        }

        return output;
    }

    private static double SyncThresholdFromLevels((double SyncLevel, double BlankLevel) levels)
        => (levels.SyncLevel + levels.BlankLevel) / 2.0;

    private (VideoOutputConverter? Converter, bool Adjusted) ResolveLaserDiscAgcConverter(
        RfDecodedSpan span,
        IReadOnlyList<double> lineLocations,
        double meanLineLength,
        bool isFirstField,
        int syncConfidence,
        VideoOutputConverter baseConverter,
        int fieldNumber)
    {
        if (_laserDiscAgcOptions is null)
        {
            return (null, false);
        }

        VideoOutputConverter current = _laserDiscAgcConverter ?? baseConverter;
        if (!isFirstField || syncConfidence <= 80)
        {
            return (_laserDiscAgcConverter, false);
        }

        (double syncHz, double ire0Hz, double ire100Hz) = DetectLaserDiscAgcLevels(
            span,
            lineLocations,
            meanLineLength,
            current,
            isFirstField);
        double syncIreDiff = Math.Abs(current.HzToIre(syncHz) - current.VSyncIre);
        double ire0Diff = Math.Abs(current.HzToIre(ire0Hz));
        double acceptableDifference = fieldNumber > 0 ? 2.0 : 0.5;
        if (Math.Max(syncIreDiff, ire0Diff) <= acceptableDifference)
        {
            return (current, false);
        }

        double hzIre = (ire100Hz - ire0Hz) / 100.0;
        if (!double.IsFinite(hzIre) || hzIre == 0.0)
        {
            return (current, false);
        }

        double vsyncIre = (syncHz - ire0Hz) / hzIre;
        if (!double.IsFinite(vsyncIre) || vsyncIre > -20.0)
        {
            return (current, false);
        }

        _laserDiscAgcConverter = new VideoOutputConverter(
            ire0Hz,
            hzIre,
            current.OutputZero,
            vsyncIre,
            current.OutputScale);
        return (_laserDiscAgcConverter, true);
    }

    private (double SyncHz, double Ire0Hz, double Ire100Hz) DetectLaserDiscAgcLevels(
        RfDecodedSpan span,
        IReadOnlyList<double> lineLocations,
        double meanLineLength,
        VideoOutputConverter current,
        bool isFirstField)
    {
        ReadOnlySpan<double> syncReference = span.VideoLowPass is { Length: > 0 } lowPass
            ? lowPass
            : span.Video;
        int lineOffset = LaserDiscLineOffset(isFirstField);
        var syncLevels = new List<double>();
        var blankLevels = new List<double>();
        var whiteLevels = new List<double>();

        foreach (LaserDiscVitsLevelSlice slice in _laserDiscAgcOptions!.WhiteSlices)
        {
            if (!TryGetLineSlice(span.Video, lineLocations, slice.Line + lineOffset, slice.StartUsec, slice.LengthUsec, out int start, out int length))
            {
                continue;
            }

            double whiteHz = Percentile(span.Video.AsSpan(start, length), slice.Percentile);
            double whiteIre = current.HzToIre(whiteHz);
            if (whiteIre >= 95.0 && whiteIre <= 110.0)
            {
                whiteLevels.Add(whiteHz);
            }
        }

        int outputLines = Math.Min(_renderer.FrameSpec.OutputLineCount, lineLocations.Count - 1 - lineOffset);
        for (int line = 12; line < outputLines; line++)
        {
            int physicalLine = line + lineOffset;
            if (!TryGetLineSlice(syncReference, lineLocations, physicalLine, 0.25, 4.0, out int syncStart, out int syncLength)
                || !TryGetLineSlice(
                    syncReference,
                    lineLocations,
                    physicalLine,
                    _laserDiscAgcOptions.ColorBurstEndUsec + 0.25,
                    _laserDiscAgcOptions.ActiveVideoStartUsec - _laserDiscAgcOptions.ColorBurstEndUsec - 0.5,
                    out int blankStart,
                    out int blankLength))
            {
                continue;
            }

            double thisLineLength = lineLocations[physicalLine] - lineLocations[physicalLine - 1];
            double adjustment = thisLineLength == 0.0 ? 0.0 : meanLineLength / thisLineLength;
            if (adjustment < 0.98 || adjustment > 1.02)
            {
                continue;
            }

            syncLevels.Add(Median(syncReference.Slice(syncStart, syncLength)) / adjustment);
            blankLevels.Add(Median(syncReference.Slice(blankStart, blankLength)) / adjustment);
        }

        double syncHz = syncLevels.Count > 0 ? Median(syncLevels) : current.IreToHz(current.VSyncIre);
        double ire0Hz = blankLevels.Count > 0 ? Median(blankLevels) : current.IreToHz(0.0);
        double ire100Hz = whiteLevels.Count > 0 ? Median(whiteLevels) : current.IreToHz(100.0);
        return (syncHz, ire0Hz, ire100Hz);
    }

    private int LaserDiscLineOffset(bool isFirstField)
    {
        return FormatCatalog.ParentSystem(_system) == "PAL"
            ? isFirstField ? 2 : 3
            : 0;
    }

    private LineLocationResult RefineLaserDiscPalLineLocationsFromPilot(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        bool? isFirstField)
    {
        if (_laserDiscPilotRefineOptions is null
            || FormatCatalog.ParentSystem(_system) != "PAL"
            || span.VideoPilot is not { Length: > 0 } pilot
            || pilot.Length != span.Video.Length)
        {
            return lineLocations;
        }

        int fieldLineBoundary = CurrentFieldLineCount(isFirstField)
            + LaserDiscLineOffset(isFirstField);
        double[] firstPass = RefineLaserDiscPalLineLocationsFromPilotPass(
            pilot,
            lineLocations.Locations,
            fieldLineBoundary);
        double[] secondPass = RefineLaserDiscPalLineLocationsFromPilotPass(
            pilot,
            firstPass,
            fieldLineBoundary);
        return lineLocations with { Locations = secondPass };
    }

    private double[] RefineLaserDiscPalLineLocationsFromPilotPass(
        ReadOnlySpan<double> pilot,
        IReadOnlyList<double> lineLocations,
        int fieldLineBoundary)
    {
        double[] output = lineLocations.ToArray();
        int lineCount = Math.Min(323, lineLocations.Count);
        if (lineCount <= 1 || _laserDiscPilotRefineOptions!.PilotMHz <= 0.0)
        {
            return output;
        }

        var zeroCrossingPhases = new double[lineCount];
        var pilotHalfPeriods = new double[lineCount];
        for (int line = 0; line < lineCount; line++)
        {
            pilotHalfPeriods[line] = LaserDiscPilotHalfPeriodSamples(lineLocations, line);
            zeroCrossingPhases[line] = TryMeasureLaserDiscPilotPhase(
                pilot,
                lineLocations,
                line,
                pilotHalfPeriods[line],
                fieldLineBoundary,
                out double phase)
                ? phase
                : line > 0 ? zeroCrossingPhases[line - 1] : 0.0;
        }

        double averagePhase = CircularAverageUnitPhase(zeroCrossingPhases);
        for (int line = 0; line < lineCount; line++)
        {
            if (double.IsFinite(output[line]))
            {
                output[line] += PhaseDistance(zeroCrossingPhases[line], averagePhase) * pilotHalfPeriods[line];
            }
        }

        return output;
    }

    private bool TryMeasureLaserDiscPilotPhase(
        ReadOnlySpan<double> pilot,
        IReadOnlyList<double> lineLocations,
        int line,
        double pilotHalfPeriodSamples,
        int fieldLineBoundary,
        out double phase)
    {
        phase = 0.0;
        if (pilotHalfPeriodSamples <= 0.0
            || !TryGetLaserDiscPilotSlice(
                pilot,
                lineLocations,
                line,
                fieldLineBoundary,
                out int start,
                out int length,
                out double lineOffset))
        {
            return false;
        }

        ReadOnlySpan<double> pilotSlice = pilot.Slice(start, length);
        int peak = MaxAbsIndex(pilotSlice);
        if (peak < 0 || peak >= pilotSlice.Length - 1)
        {
            return false;
        }

        double? zeroCrossing = PulseDetection.CalculateZeroCrossing(
            pilotSlice,
            peak,
            target: 0.0,
            count: Math.Min(16, pilotSlice.Length - peak - 1));
        if (!zeroCrossing.HasValue)
        {
            return false;
        }

        phase = (zeroCrossing.Value - lineOffset) / pilotHalfPeriodSamples;
        return double.IsFinite(phase);
    }

    private bool TryGetLaserDiscPilotSlice(
        ReadOnlySpan<double> source,
        IReadOnlyList<double> lineLocations,
        int line,
        int fieldLineBoundary,
        out int start,
        out int length,
        out double lineOffset)
    {
        start = 0;
        length = 0;
        lineOffset = 0.0;
        if (line < 0 || line >= lineLocations.Count)
        {
            return false;
        }

        double lineStart = lineLocations[line];
        if (!double.IsFinite(lineStart))
        {
            return false;
        }

        double lineSamplesPerUsec = LaserDiscLineSamplesPerUsec(
            lineLocations,
            line,
            fieldLineBoundary);
        start = Math.Clamp((int)Math.Floor(lineStart), 0, source.Length);
        int end = Math.Clamp((int)Math.Floor(lineStart + (6.0 * lineSamplesPerUsec) + 1.0), start, source.Length);
        length = end - start;
        lineOffset = lineStart - start;
        return length > 1;
    }

    private double LaserDiscPilotHalfPeriodSamples(IReadOnlyList<double> lineLocations, int line)
    {
        double lineSamplesPerUsec = line > 1
            ? LaserDiscLineSamplesPerUsecFromPrevious(lineLocations, line)
            : _syncAnalyzer.SampleRateMHz;
        return (lineSamplesPerUsec / _laserDiscPilotRefineOptions!.PilotMHz) / 2.0;
    }

    private double LaserDiscLineSamplesPerUsecFromPrevious(IReadOnlyList<double> lineLocations, int line)
    {
        double lineLength = line < lineLocations.Count
            ? lineLocations[line] - lineLocations[line - 1]
            : _syncAnalyzer.NominalLineLength;
        if (!double.IsFinite(lineLength) || lineLength <= 0.0 || _syncAnalyzer.NominalLineLength <= 0.0)
        {
            return _syncAnalyzer.SampleRateMHz;
        }

        return _syncAnalyzer.SampleRateMHz / (lineLength / _syncAnalyzer.NominalLineLength);
    }

    private static int MaxAbsIndex(ReadOnlySpan<double> values)
    {
        int index = -1;
        double best = double.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            double value = Math.Abs(values[i]);
            if (value > best)
            {
                best = value;
                index = i;
            }
        }

        return index;
    }

    private static double CircularAverageUnitPhase(ReadOnlySpan<double> phases)
    {
        double sumSin = 0.0;
        double sumCos = 0.0;
        for (int i = 0; i < phases.Length; i++)
        {
            double fraction = phases[i] - Math.Truncate(phases[i]);
            double angle = Math.Tau * fraction;
            sumCos += Math.Cos(angle);
            sumSin += Math.Sin(angle);
        }

        double average = Math.Atan2(sumSin, sumCos) / Math.Tau;
        return average < 0.0 ? average + 1.0 : average;
    }

    private static double PhaseDistance(double phase, double center)
    {
        double distance = (phase - Math.Floor(phase)) - center;
        if (distance < -0.5)
        {
            distance += 1.0;
        }
        else if (distance > 0.5)
        {
            distance -= 1.0;
        }

        return distance;
    }

    private int? DetermineLaserDiscPalFieldPhase(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        bool? isFirstField,
        double? medianBurstIre,
        VideoOutputConverter converter)
    {
        if (_decodeType is not ("ld" or "cvbs")
            || FormatCatalog.ParentSystem(_system) != "PAL"
            || !isFirstField.HasValue
            || span.VideoBurst is not { Length: > 0 } burst
            || burst.Length != span.Video.Length
            || converter.HzIre == 0.0)
        {
            return null;
        }

        int followingPhase = _previousLaserDiscPalFieldPhaseId.HasValue
            ? (_previousLaserDiscPalFieldPhaseId.Value % 8) + 1
            : 1;
        int Commit(int phase, IReadOnlyDictionary<int, double>? adjustments)
        {
            _previousLaserDiscPalFieldPhaseId = phase;
            _previousLaserDiscPalPhaseAdjustments = adjustments;
            return phase;
        }

        int lineOffset = isFirstField.Value ? 2 : 3;
        double? burstLevelHz = ComputeLaserDiscPalBurstLevel(
            span.Video,
            lineLocations.Locations,
            line: 6 + lineOffset,
            converter.HzIre);
        if (!burstLevelHz.HasValue || !medianBurstIre.HasValue)
        {
            return Commit(followingPhase, adjustments: null);
        }

        double burstLevelIre = burstLevelHz.Value / converter.HzIre;
        bool hasBurst;
        if (PulseDetection.InRange(
                burstLevelIre,
                medianBurstIre.Value * 0.8,
                medianBurstIre.Value * 1.2))
        {
            hasBurst = true;
        }
        else if (burstLevelIre < medianBurstIre.Value * 0.2)
        {
            hasBurst = false;
        }
        else
        {
            return Commit(followingPhase, adjustments: null);
        }

        int fourFieldPhase = (isFirstField.Value, hasBurst) switch
        {
            (true, false) => 1,
            (false, true) => 2,
            (true, true) => 3,
            (false, false) => 4
        };

        double fscMHz = _renderer.FrameSpec.OutputSampleRateHz / 4_000_000.0;
        int fieldLineBoundary = CurrentFieldLineCount(isFirstField) + lineOffset;
        var adjustments = new Dictionary<int, double>(4);
        int risingCount = 0;
        int count = 0;
        foreach (int baseLine in new[] { 7, 11, 15, 19 })
        {
            double previousAdjustment = _previousLaserDiscPalPhaseAdjustments is not null
                && _previousLaserDiscPalPhaseAdjustments.TryGetValue(baseLine, out double savedAdjustment)
                    ? savedAdjustment
                    : 0.0;
            if (!TryComputeLaserDiscLineBurst(
                    span.Video,
                    burst,
                    lineLocations.Locations,
                    baseLine + lineOffset,
                    previousAdjustment,
                    fieldLineBoundary,
                    fscMHz,
                    converter.HzIre,
                    out bool rising,
                    out double phaseAdjustment))
            {
                continue;
            }

            adjustments[baseLine] = phaseAdjustment;
            risingCount += rising ? 1 : 0;
            count++;
        }

        if (count == 0 || (risingCount * 2) == count)
        {
            return Commit(followingPhase, adjustments);
        }

        bool isFirstFour = (risingCount * 2) > count;
        if (fourFieldPhase == 2)
        {
            isFirstFour = !isFirstFour;
        }

        return Commit(fourFieldPhase + (isFirstFour ? 0 : 4), adjustments);
    }

    private double? ComputeLaserDiscPalBurstLevel(
        ReadOnlySpan<double> video,
        IReadOnlyList<double> lineLocations,
        int line,
        double hzIre)
    {
        if (!TryGetLaserDiscBurstSlice(video, lineLocations, line, out int start, out int length))
        {
            return null;
        }

        ReadOnlySpan<double> burstArea = video.Slice(start, length);
        double mean = Mean(burstArea);
        if (MaxCentered(burstArea, mean) > 30.0 * Math.Abs(hzIre))
        {
            return null;
        }

        return StandardDeviation(burstArea) * Math.Sqrt(2.0);
    }

    private (LineLocationResult LineLocations, int? FieldPhaseId) RefineLaserDiscNtscLineLocationsFromBurst(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        bool? isFirstField,
        VideoOutputConverter converter)
    {
        if (_laserDiscNtscBurstRefineOptions is null
            || FormatCatalog.ParentSystem(_system) != "NTSC"
            || span.VideoBurst is not { Length: > 0 } burst
            || burst.Length != span.Video.Length
            || _laserDiscNtscBurstRefineOptions.FscMHz <= 0.0)
        {
            return (lineLocations, null);
        }

        int lineLimit = Math.Min(266, lineLocations.Locations.Length);
        if (lineLimit == 0)
        {
            return (ApplyLaserDiscNtscFscPhaseShift(lineLocations), null);
        }

        var phaseAdjustments = new Dictionary<int, double>(lineLimit);
        int risingEvenLineCount = 0;
        int fieldLineBoundary = CurrentFieldLineCount(isFirstField);
        double previousPhaseAdjustment = _laserDiscNtscPhaseAdjustMedian;
        for (int line = 0; line < lineLimit; line++)
        {
            if (!TryComputeLaserDiscLineBurst(
                 span.Video,
                 burst,
                 lineLocations.Locations,
                 line,
                 previousPhaseAdjustment,
                 fieldLineBoundary,
                 _laserDiscNtscBurstRefineOptions.FscMHz,
                 converter.HzIre,
                 out bool rising,
                 out double phaseAdjustment))
            {
                continue;
            }

            phaseAdjustments[line] = phaseAdjustment / 2.0;
            if ((line & 1) == 0 && rising)
            {
                risingEvenLineCount++;
            }
        }

        bool[] burstErrors = lineLocations.Filled.ToArray();
        for (int line = 1; line < lineLimit; line++)
        {
            if (!phaseAdjustments.ContainsKey(line))
            {
                burstErrors[line] = true;
            }
        }

        LineLocationResult burstLineLocations = lineLocations with { Filled = burstErrors };

        if (phaseAdjustments.Count == 0)
        {
            return (ApplyLaserDiscNtscFscPhaseShift(burstLineLocations), 1);
        }

        bool field14 = risingEvenLineCount > (phaseAdjustments.Count / 4);
        _laserDiscNtscPhaseAdjustMedian = Median(phaseAdjustments.Values.ToArray()) * 2.0;

        var sampleAdjustments = new Dictionary<int, double>(phaseAdjustments.Count);
        double fscMHzInv = 1.0 / _laserDiscNtscBurstRefineOptions.FscMHz;
        for (int line = 0; line < lineLimit; line++)
        {
            if (!phaseAdjustments.TryGetValue(line, out double adjustment)
                || burstErrors[line]
                || !double.IsFinite(lineLocations.Locations[line]))
            {
                continue;
            }

            double lineSamplesPerUsec = LaserDiscLineSamplesPerUsec(
                lineLocations.Locations,
                line,
                fieldLineBoundary);
            double sampleAdjustment = adjustment * lineSamplesPerUsec * fscMHzInv;
            if (double.IsFinite(sampleAdjustment))
            {
                sampleAdjustments[line] = sampleAdjustment;
            }
        }

        if (sampleAdjustments.Count == 0)
        {
            return (ApplyLaserDiscNtscFscPhaseShift(burstLineLocations), 1);
        }

        double medianAdjustment = Median(sampleAdjustments.Values.ToArray());
        double lastValidAdjustment = medianAdjustment;
        double[] refined = lineLocations.Locations.ToArray();
        for (int line = 0; line < lineLimit; line++)
        {
            if (!double.IsFinite(refined[line]))
            {
                continue;
            }

            if (sampleAdjustments.TryGetValue(line, out double adjustment)
                && PulseDetection.InRange(adjustment - medianAdjustment, -2.0, 2.0))
            {
                refined[line] += adjustment;
                lastValidAdjustment = adjustment;
            }
            else
            {
                refined[line] += lastValidAdjustment;
            }
        }

        int? fieldPhaseId = isFirstField.HasValue
            ? LaserDiscNtscFieldPhaseId(isFirstField.Value, field14)
            : null;
        return (ApplyLaserDiscNtscFscPhaseShift(burstLineLocations with { Locations = refined }), fieldPhaseId);
    }

    private LineLocationResult ApplyLaserDiscNtscFscPhaseShift(LineLocationResult lineLocations)
        => ApplyNtscFscPhaseShiftCore(lineLocations, _laserDiscNtscBurstRefineOptions!.FscMHz);

    private LineLocationResult ApplyNtscFscPhaseShiftCore(
        LineLocationResult lineLocations,
        double fscMHz)
    {
        const double FscPhaseDegrees = 117.25;
        if (!double.IsFinite(fscMHz) || fscMHz <= 0.0)
        {
            return lineLocations;
        }

        double shiftSamples = ((FscPhaseDegrees / 360.0) / fscMHz)
            * _syncAnalyzer.SampleRateMHz;
        double[] shifted = lineLocations.Locations
            .Select(location => location - shiftSamples)
            .ToArray();
        return lineLocations with { Locations = shifted };
    }

    private bool TryComputeLaserDiscLineBurst(
        ReadOnlySpan<double> video,
        ReadOnlySpan<double> burst,
        IReadOnlyList<double> lineLocations,
        int line,
        double previousPhaseAdjustment,
        int fieldLineBoundary,
        double fscMHz,
        double hzIre,
        out bool rising,
        out double phaseAdjustment)
    {
        rising = false;
        phaseAdjustment = 0.0;
        if (fscMHz <= 0.0
            || line < 0
            || line >= lineLocations.Count)
        {
            return false;
        }

        double lineStart = lineLocations[line];
        if (!double.IsFinite(lineStart))
        {
            return false;
        }

        int start = (int)Math.Floor(lineStart);
        double startRemainder = lineStart - start;
        double lineSamplesPerUsec = LaserDiscLineSamplesPerUsec(
            lineLocations,
            line,
            fieldLineBoundary);
        double fscMHzInv = 1.0 / fscMHz;
        int burstStartOffset = (int)((21.0 * fscMHzInv) * lineSamplesPerUsec);
        int burstEndOffset = (int)((28.0 * fscMHzInv) * lineSamplesPerUsec);
        int windowStart = start + burstStartOffset;
        int windowEnd = start + burstEndOffset;
        if (windowStart < 0 || windowStart >= burst.Length)
        {
            return false;
        }

        windowEnd = Math.Clamp(windowEnd, windowStart, burst.Length);
        int length = windowEnd - windowStart;
        if (length <= 1)
        {
            return false;
        }

        float[] burstArea = CenterFloat32(burst.Slice(windowStart, length));
        float threshold = StandardDeviationFloat32(burstArea);
        if (!double.IsFinite(threshold) || threshold <= 0.0)
        {
            return false;
        }

        if (windowStart < video.Length)
        {
            int demodLength = Math.Min(length, video.Length - windowStart);
            if (demodLength > 0 && hzIre != 0.0)
            {
                float[] demodArea = CenterFloat32(video.Slice(windowStart, demodLength));
                if (MaxAbsFloat32(demodArea) > 30.0 * Math.Abs(hzIre))
                {
                    return false;
                }
            }
        }

        double zeroCrossingBurstDivisor = (lineSamplesPerUsec * fscMHzInv) / 2.0;
        if (!double.IsFinite(zeroCrossingBurstDivisor) || zeroCrossingBurstDivisor <= 0.0)
        {
            return false;
        }

        double adjustedPhase = -previousPhaseAdjustment;
        bool[] isRising = new bool[16];
        float[] zeroCrossings = new float[16];
        int zeroCrossingCount = 0;
        int risingCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            (zeroCrossingCount, adjustedPhase, risingCount) = FindLaserDiscNtscBurstZeroCrossings(
                isRising,
                zeroCrossings,
                burstArea,
                threshold,
                burstStartOffset,
                startRemainder,
                zeroCrossingBurstDivisor,
                adjustedPhase);
        }

        if (zeroCrossingCount == 0)
        {
            return false;
        }

        rising = risingCount > (zeroCrossingCount / 2.0);
        phaseAdjustment = -adjustedPhase;
        return double.IsFinite(phaseAdjustment);
    }

    private static (int ZeroCrossingCount, double PhaseAdjustment, int RisingCount) FindLaserDiscNtscBurstZeroCrossings(
        bool[] isRising,
        float[] zeroCrossings,
        ReadOnlySpan<float> burstArea,
        float threshold,
        int burstStart,
        double startRemainder,
        double zeroCrossingBurstDivisor,
        double phaseAdjustment)
    {
        Array.Clear(isRising);
        Array.Clear(zeroCrossings);
        int zeroCrossingCount = 0;
        int end = Math.Max(0, burstArea.Length - 1);
        int position = 0;
        while (position < end && zeroCrossingCount < zeroCrossings.Length)
        {
            float centered = burstArea[position];
            if (MathF.Abs(centered) > threshold)
            {
                double? zeroCrossing = CalculateZeroCrossingFloat32(
                    burstArea,
                    position,
                    target: 0.0f,
                    count: burstArea.Length - position - 1);
                if (zeroCrossing.HasValue)
                {
                    isRising[zeroCrossingCount] = centered < 0.0;
                    zeroCrossings[zeroCrossingCount] = (float)zeroCrossing.Value;
                    zeroCrossingCount++;
                    position = (int)zeroCrossing.Value + 1;
                    continue;
                }
            }

            position++;
        }

        int risingCount = 0;
        if (zeroCrossingCount > 0)
        {
            double[] phaseDistances = new double[zeroCrossings.Length];
            for (int i = 0; i < zeroCrossings.Length; i++)
            {
                double zeroCrossingCycles = ((burstStart + (double)zeroCrossings[i] - startRemainder) / zeroCrossingBurstDivisor)
                    + phaseAdjustment;
                int roundedCycles = (int)(zeroCrossingCycles + 0.5);
                phaseDistances[i] = roundedCycles - zeroCrossingCycles;
                if (isRising[i] ^ ((roundedCycles & 1) != 0))
                {
                    risingCount++;
                }
            }

            phaseAdjustment += Median(phaseDistances);
        }

        return (zeroCrossingCount, phaseAdjustment, risingCount);
    }

    private static double? CalculateZeroCrossingFloat32(
        ReadOnlySpan<float> data,
        int startOffset,
        float target,
        int count)
    {
        bool rising = data[startOffset] < target;
        int searchEnd = Math.Min(data.Length, startOffset + count + 1);
        int location = -1;
        for (int i = startOffset + 1; i < searchEnd; i++)
        {
            bool crossed = rising
                ? data[i - 1] < target && data[i] >= target
                : data[i - 1] > target && data[i] <= target;
            if (crossed)
            {
                location = i;
                break;
            }
        }

        if (location < 0)
        {
            return null;
        }

        double a = data[location - 1] - (double)target;
        double b = data[location] - (double)target;
        double fraction = b - a != 0.0 ? -a / (-a + b) : 0.0;
        return location - 1 + fraction;
    }

    private static float[] CenterFloat32(ReadOnlySpan<double> values)
    {
        var output = new float[values.Length];
        float sum = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            output[i] = (float)values[i];
            sum += output[i];
        }

        float mean = sum / values.Length;
        for (int i = 0; i < output.Length; i++)
        {
            output[i] -= mean;
        }

        return output;
    }

    private static float StandardDeviationFloat32(ReadOnlySpan<float> values)
    {
        float sum = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        float mean = sum / values.Length;
        float sumSquares = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            float distance = values[i] - mean;
            sumSquares += distance * distance;
        }

        return MathF.Sqrt(sumSquares / values.Length);
    }

    private static float MaxAbsFloat32(ReadOnlySpan<float> values)
    {
        float maximum = float.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            maximum = MathF.Max(maximum, MathF.Abs(values[i]));
        }

        return maximum;
    }

    private static int LaserDiscNtscFieldPhaseId(bool isFirstField, bool field14)
    {
        return (isFirstField, field14) switch
        {
            (true, true) => 1,
            (false, false) => 2,
            (true, false) => 3,
            (false, true) => 4
        };
    }

    private bool TryGetLineSlice(
        ReadOnlySpan<double> source,
        IReadOnlyList<double> lineLocations,
        int line,
        double startUsec,
        double lengthUsec,
        out int start,
        out int length)
    {
        start = 0;
        length = 0;
        if (line <= 0 || line >= lineLocations.Count || lengthUsec <= 0.0)
        {
            return false;
        }

        double lineSamplesPerUsec = LaserDiscLineSamplesPerUsec(lineLocations, line);
        double begin = lineLocations[line] + (startUsec * lineSamplesPerUsec);
        double end = begin + _syncAnalyzer.UsecToSamples(lengthUsec) + 1.0;
        start = Math.Clamp((int)begin, 0, source.Length);
        int endIndex = Math.Clamp((int)end, start, source.Length);
        length = endIndex - start;
        return length > 0;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        return MedianSorted(sorted);
    }

    private static double Median(ReadOnlySpan<double> values)
    {
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        return MedianSorted(sorted);
    }

    private static double Percentile(ReadOnlySpan<double> values, double percentile)
    {
        if (values.IsEmpty)
        {
            return 0.0;
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        double position = Math.Clamp(percentile, 0.0, 100.0) / 100.0 * (sorted.Length - 1);
        int left = (int)Math.Floor(position);
        int right = (int)Math.Ceiling(position);
        if (left == right)
        {
            return sorted[left];
        }

        double fraction = position - left;
        return sorted[left] + ((sorted[right] - sorted[left]) * fraction);
    }

    private static double MedianSorted(double[] sorted)
    {
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    private int[]? DecodeLaserDiscVbiData(
        ReadOnlySpan<double> video,
        LineLocationResult lineLocations,
        bool? isFirstField)
    {
        if (!_decodeVbiData || video.IsEmpty)
        {
            return null;
        }

        int lineOffset = FormatCatalog.ParentSystem(_system) == "PAL"
            ? isFirstField == false ? 3 : 2
            : 0;
        var codes = new List<int>(3);
        foreach (int baseLine in new[] { 16, 17, 18 })
        {
            int line = baseLine + lineOffset;
            if (line < 0 || line >= lineLocations.Locations.Length)
            {
                continue;
            }

            int? code = DecodeLaserDiscPhillipsCode(video, lineLocations.Locations[line]);
            if (code.HasValue)
            {
                codes.Add(code.Value);
            }
        }

        return codes.Count > 0 ? codes.ToArray() : null;
    }

    private double? ComputeLaserDiscMedianBurstIre(
        ReadOnlySpan<double> video,
        IReadOnlyList<double> lineLocations,
        bool? isFirstField,
        VideoOutputConverter converter)
    {
        if (!_decodeVbiData || video.IsEmpty || converter.HzIre == 0.0)
        {
            return null;
        }

        bool isPal = FormatCatalog.ParentSystem(_system) == "PAL";
        const int lineOffset = 0;
        int burstFieldLineBoundary = isFirstField == false ? 262 : 263;
        int endLineExclusive = isPal ? 313 : 264;
        var burstLevels = new List<double>(Math.Max(0, endLineExclusive - 11));
        for (int line = 11; line < endLineExclusive; line++)
        {
            if (!TryGetLaserDiscBurstSlice(
                    video,
                    lineLocations,
                    line + lineOffset,
                    out int start,
                    out int length,
                    burstFieldLineBoundary))
            {
                continue;
            }

            ReadOnlySpan<double> burstArea = video.Slice(start, length);
            double mean = Mean(burstArea);
            if (isPal && MaxCentered(burstArea, mean) > 30.0 * Math.Abs(converter.HzIre))
            {
                continue;
            }

            burstLevels.Add(StandardDeviation(burstArea) * Math.Sqrt(2.0));
        }

        return burstLevels.Count == 0
            ? 0.0
            : Median(burstLevels) / converter.HzIre;
    }

    private bool TryGetLaserDiscBurstSlice(
        ReadOnlySpan<double> source,
        IReadOnlyList<double> lineLocations,
        int line,
        out int start,
        out int length,
        int? fieldLineBoundary = null)
    {
        start = 0;
        length = 0;
        if (line <= 0 || line >= lineLocations.Count)
        {
            return false;
        }

        double lineSamplesPerUsec = LaserDiscLineSamplesPerUsec(lineLocations, line, fieldLineBoundary);
        double begin = lineLocations[line] + (5.5 * lineSamplesPerUsec);
        double end = begin + _syncAnalyzer.UsecToSamples(2.4) + 1.0;
        start = Math.Clamp((int)begin, 0, source.Length);
        int endIndex = Math.Clamp((int)end, start, source.Length);
        length = endIndex - start;
        return length > 0;
    }

    private double LaserDiscLineSamplesPerUsec(
        IReadOnlyList<double> lineLocations,
        int line,
        int? fieldLineBoundary = null)
    {
        double lineLength;
        int previousLineBoundary = Math.Min(
            fieldLineBoundary ?? lineLocations.Count - 1,
            lineLocations.Count - 1);
        if (line >= previousLineBoundary)
        {
            lineLength = lineLocations[line] - lineLocations[line - 1];
        }
        else if (line > 0)
        {
            lineLength = (lineLocations[line + 1] - lineLocations[line - 1]) / 2.0;
        }
        else
        {
            lineLength = lineLocations[line + 1] - lineLocations[line];
        }

        if (!double.IsFinite(lineLength) || lineLength <= 0.0 || _syncAnalyzer.NominalLineLength <= 0.0)
        {
            return _syncAnalyzer.SampleRateMHz;
        }

        return _syncAnalyzer.SampleRateMHz * lineLength / _syncAnalyzer.NominalLineLength;
    }

    private int? DecodeLaserDiscPhillipsCode(ReadOnlySpan<double> video, double lineStart)
    {
        if (!double.IsFinite(lineStart))
        {
            return null;
        }

        double threshold = _videoOutput.IreToHz(50.0);
        int firstSearchStart = (int)(lineStart + _syncAnalyzer.UsecToSamples(2.0));
        int firstSearchCount = Math.Max(0, (int)_syncAnalyzer.UsecToSamples(12.0));
        double? current = TryZeroCrossing(video, firstSearchStart, threshold, firstSearchCount);
        var crossings = new List<(double Location, bool Bit)>(24);
        while (current.HasValue)
        {
            int sampleBefore = (int)(current.Value - _syncAnalyzer.UsecToSamples(0.5));
            if (sampleBefore < 0 || sampleBefore >= video.Length)
            {
                return null;
            }

            crossings.Add((current.Value, video[sampleBefore] < threshold));
            int nextStart = (int)(current.Value + _syncAnalyzer.UsecToSamples(1.9));
            int nextCount = Math.Max(0, (int)_syncAnalyzer.UsecToSamples(0.2));
            current = TryZeroCrossing(video, nextStart, threshold, nextCount);
        }

        if (crossings.Count != 24)
        {
            return null;
        }

        for (int i = 1; i < crossings.Count; i++)
        {
            double gapUs = (crossings[i].Location - crossings[i - 1].Location) / _syncAnalyzer.SampleRateMHz;
            if (gapUs <= 1.85 || gapUs >= 2.15)
            {
                return null;
            }
        }

        int lineCode = 0;
        for (int b = 0; b < crossings.Count; b += 4)
        {
            int nibble = 0;
            for (int i = 0; i < 4; i++)
            {
                nibble = (nibble << 1) | (crossings[b + i].Bit ? 1 : 0);
            }

            lineCode = (lineCode << 4) | nibble;
        }

        return lineCode;
    }

    private static double? TryZeroCrossing(ReadOnlySpan<double> data, int start, double threshold, int count)
    {
        if (start < 0 || start >= data.Length)
        {
            return null;
        }

        return PulseDetection.CalculateZeroCrossing(data, start, threshold, count: count);
    }

    private LineLocationResult RefineLineLocationsFromHSync(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        VideoOutputConverter converter)
    {
        if (string.Equals(_decodeType, "ld", StringComparison.Ordinal))
        {
            return RefineLaserDiscLineLocationsFromHSync(span, lineLocations, converter);
        }

        if (!_hSyncRefineOptions.Enabled
            || span.VideoLowPass is not { Length: > 0 } syncReference
            || syncReference.Length != span.Video.Length)
        {
            return lineLocations;
        }

        double threshold = converter.IreToHz(converter.VSyncIre / 2.0);
        int oneMicrosecond = Math.Max(1, (int)_syncAnalyzer.SampleRateMHz);
        int normalHSyncLength = Math.Max(1, (int)_syncAnalyzer.UsecToSamples(_syncAnalyzer.HSyncPulseUs));
        int searchCount = Math.Max(1, oneMicrosecond * 2);
        double[] refined = lineLocations.Locations.ToArray();
        bool[] filled = lineLocations.Filled.ToArray();
        double previousPorchLevel = -1.0;

        for (int line = 0; line < refined.Length; line++)
        {
            if (IsVSyncLine(line))
            {
                filled[line] = true;
                continue;
            }

            if (!double.IsFinite(refined[line]))
            {
                continue;
            }

            int searchStart = (int)Math.Round(refined[line] - oneMicrosecond, MidpointRounding.AwayFromZero);
            if (searchStart < 0 || searchStart >= syncReference.Length - 1)
            {
                continue;
            }

            double? crossing = PulseDetection.CalculateZeroCrossing(
                syncReference,
                searchStart,
                threshold,
                count: Math.Min(searchCount, syncReference.Length - searchStart - 1));
            double originalLocation = refined[line];
            if (crossing.HasValue
                && !filled[line]
                && TryRefineLeftHSync(
                    syncReference,
                    searchStart,
                    crossing.Value,
                    oneMicrosecond,
                    previousPorchLevel,
                    converter,
                    out double refinedLocation,
                    out double porchLevel))
            {
                refined[line] = refinedLocation;
                filled[line] = lineLocations.Filled[line];
                if (porchLevel > 0.0)
                {
                    previousPorchLevel = porchLevel;
                }
            }
            else
            {
                filled[line] = true;
                refined[line] = originalLocation;
            }

            if (_hSyncRefineOptions.UseRightHSync
                && TryRefineFromRightHSync(
                    syncReference,
                    searchStart,
                    threshold,
                    normalHSyncLength,
                oneMicrosecond,
                refined[line],
                converter,
                out double rightRefinedLocation,
                    out double rightPorchLevel))
            {
                refined[line] = rightRefinedLocation;
                filled[line] = false;
                if (rightPorchLevel > 0.0)
                {
                    previousPorchLevel = rightPorchLevel;
                }
            }
        }

        return new LineLocationResult(refined, filled);
    }

    private LineLocationResult RefineLaserDiscLineLocationsFromHSync(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        VideoOutputConverter converter)
    {
        if (span.VideoLowPass is not { Length: > 0 } syncReference
            || syncReference.Length != span.Video.Length)
        {
            return lineLocations;
        }

        double sampleRateMHz = _syncAnalyzer.SampleRateMHz;
        double threshold = converter.IreToHz(converter.VSyncIre / 2.0);
        int searchCount = Math.Max(1, (int)(sampleRateMHz * 2.0));
        var refined = lineLocations.Locations.ToArray();
        var filled = lineLocations.Filled.ToArray();
        for (int line = 0; line < refined.Length; line++)
        {
            if (IsVSyncLine(line))
            {
                filled[line] = true;
                continue;
            }

            double originalLocation = refined[line];
            int searchStart = (int)(originalLocation - sampleRateMHz);
            double? crossing = searchStart >= 0 && searchStart < syncReference.Length
                ? PulseDetection.CalculateZeroCrossing(
                    syncReference,
                    searchStart,
                    threshold,
                    count: Math.Min(searchCount, syncReference.Length - searchStart - 1))
                : null;
            if (!crossing.HasValue || filled[line])
            {
                filled[line] = true;
                refined[line] = originalLocation;
                continue;
            }

            refined[line] = crossing.Value;
            if (!LaserDiscHSyncAreaLooksValid(syncReference, crossing.Value, sampleRateMHz, converter)
                || !TryTruncatedMedianRange(
                    syncReference,
                    crossing.Value + (8.0 * sampleRateMHz),
                    crossing.Value + (9.0 * sampleRateMHz),
                    out double porchLevel)
                || !TryTruncatedMedianRange(
                    syncReference,
                    crossing.Value + sampleRateMHz,
                    crossing.Value + (2.5 * sampleRateMHz),
                    out double syncLevel))
            {
                filled[line] = true;
                refined[line] = originalLocation;
                continue;
            }

            double? refinedCrossing = PulseDetection.CalculateZeroCrossing(
                syncReference,
                searchStart,
                (porchLevel + syncLevel) / 2.0,
                count: Math.Min(400, syncReference.Length - searchStart - 1));
            if (refinedCrossing.HasValue
                && Math.Abs(refinedCrossing.Value - crossing.Value) < sampleRateMHz / 2.0)
            {
                refined[line] = refinedCrossing.Value;
            }
            else
            {
                filled[line] = true;
                refined[line] = originalLocation;
            }
        }

        return new LineLocationResult(refined, filled);
    }

    private static bool LaserDiscHSyncAreaLooksValid(
        ReadOnlySpan<double> source,
        double crossing,
        double sampleRateMHz,
        VideoOutputConverter converter)
    {
        int start = Math.Clamp((int)(crossing - (0.75 * sampleRateMHz)), 0, source.Length);
        int end = Math.Clamp((int)(crossing + (8.0 * sampleRateMHz)), start, source.Length);
        if (end <= start)
        {
            return false;
        }

        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        for (int i = start; i < end; i++)
        {
            minimum = Math.Min(minimum, source[i]);
            maximum = Math.Max(maximum, source[i]);
        }

        return minimum >= converter.IreToHz(-55.0)
            && maximum <= converter.IreToHz(30.0);
    }

    private static bool TryTruncatedMedianRange(
        ReadOnlySpan<double> source,
        double start,
        double end,
        out double median)
    {
        int startIndex = Math.Clamp((int)start, 0, source.Length);
        int endIndex = Math.Clamp((int)end, startIndex, source.Length);
        if (endIndex <= startIndex)
        {
            median = 0.0;
            return false;
        }

        median = MedianFloat32(source[startIndex..endIndex]);
        return true;
    }

    private static double MedianFloat32(ReadOnlySpan<double> values)
    {
        var sorted = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            sorted[i] = (float)values[i];
        }

        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (float)((sorted[middle - 1] + sorted[middle]) / 2.0f)
            : sorted[middle];
    }

    private bool TryRefineFromRightHSync(
        ReadOnlySpan<double> syncReference,
        int leftSearchStart,
        double threshold,
        int normalHSyncLength,
        int oneMicrosecond,
        double referenceLocation,
        VideoOutputConverter converter,
        out double refinedLocation,
        out double porchLevel)
    {
        refinedLocation = 0.0;
        porchLevel = 0.0;
        int rightSearchStart = leftSearchStart + normalHSyncLength - oneMicrosecond;
        if (rightSearchStart < 0 || rightSearchStart >= syncReference.Length - 1)
        {
            return false;
        }

        int rightSearchCount = Math.Max(1, normalHSyncLength * 2);
        double? rightCrossing = PulseDetection.CalculateZeroCrossing(
            syncReference,
            rightSearchStart,
            threshold,
            edge: 1,
            count: Math.Min(rightSearchCount, syncReference.Length - rightSearchStart - 1));
        if (!rightCrossing.HasValue)
        {
            return false;
        }

        double sampleRateMHz = (float)_syncAnalyzer.SampleRateMHz;
        double candidate = rightCrossing.Value - normalHSyncLength + (2.25 * (sampleRateMHz / 40.0));
        if (!TryConfirmRightHSync(
                syncReference,
                rightSearchStart,
                rightCrossing.Value,
                normalHSyncLength,
                oneMicrosecond,
                converter,
                out porchLevel)
            || Math.Abs(candidate - referenceLocation) >= oneMicrosecond * 2.0)
        {
            return false;
        }

        refinedLocation = candidate;
        return true;
    }

    private bool TryRefineLeftHSync(
        ReadOnlySpan<double> syncReference,
        int searchStart,
        double crossing,
        int oneMicrosecond,
        double previousPorchLevel,
        VideoOutputConverter converter,
        out double refinedLocation,
        out double porchLevel)
    {
        refinedLocation = crossing;
        porchLevel = 0.0;
        if (!HSyncAreaLooksValid(
                syncReference,
                crossing,
                oneMicrosecond,
                endUsec: 3.5,
                minimumIre: -65.0,
                maximumIre: 110.0,
                converter)
            || !TryMeanRange(
                syncReference,
                crossing + oneMicrosecond,
                crossing + (2.5 * oneMicrosecond),
                out double syncLevel))
        {
            return false;
        }

        // v0.4.0's Cython c_max starts at NaN, so its back-porch branch is unreachable.
        if (previousPorchLevel > 0.0)
        {
            porchLevel = previousPorchLevel;
        }
        else if (!TryMeanRange(
            syncReference,
            crossing - oneMicrosecond,
            crossing - (0.5 * oneMicrosecond),
            out porchLevel))
        {
            return false;
        }

        double midpoint = (porchLevel + syncLevel) / 2.0;
        double? refinedCrossing = PulseDetection.CalculateZeroCrossing(
            syncReference,
            searchStart,
            midpoint,
            count: Math.Min(400, syncReference.Length - searchStart - 1));
        if (!refinedCrossing.HasValue || Math.Abs(refinedCrossing.Value - crossing) >= oneMicrosecond / 2.0)
        {
            if (previousPorchLevel <= 0.0)
            {
                return false;
            }

            refinedCrossing = PulseDetection.CalculateZeroCrossing(
                syncReference,
                searchStart,
                (previousPorchLevel + syncLevel) / 2.0,
                count: Math.Min(400, syncReference.Length - searchStart - 1));
            if (!refinedCrossing.HasValue
                || Math.Abs(refinedCrossing.Value - crossing) >= oneMicrosecond / 2.0)
            {
                return false;
            }

            porchLevel = previousPorchLevel;
        }

        refinedLocation = refinedCrossing.Value;
        return true;
    }

    private bool TryConfirmRightHSync(
        ReadOnlySpan<double> syncReference,
        int rightSearchStart,
        double rightCrossing,
        int normalHSyncLength,
        int oneMicrosecond,
        VideoOutputConverter converter,
        out double porchLevel)
    {
        porchLevel = 0.0;
        double rightDerivedStart = rightCrossing - normalHSyncLength;
        if (!HSyncAreaLooksValid(
                syncReference,
                rightDerivedStart,
                oneMicrosecond,
                endUsec: 8.0,
                minimumIre: -65.0,
                maximumIre: 30.0,
                converter)
            || !TryMeanRange(syncReference, rightDerivedStart + oneMicrosecond, rightDerivedStart + (2.5 * oneMicrosecond), out double syncLevel)
            || !TryMeanRange(
                syncReference,
                rightDerivedStart + normalHSyncLength + oneMicrosecond,
                rightDerivedStart + normalHSyncLength + (2.0 * oneMicrosecond),
                out porchLevel))
        {
            return false;
        }

        double midpoint = (porchLevel + syncLevel) / 2.0;
        double? refinedRightCrossing = PulseDetection.CalculateZeroCrossing(
            syncReference,
            rightSearchStart,
            midpoint,
            edge: 1,
            count: Math.Min(400, syncReference.Length - rightSearchStart - 1));
        return refinedRightCrossing.HasValue
            && Math.Abs(refinedRightCrossing.Value - rightCrossing) < oneMicrosecond / 2.0;
    }

    private bool IsVSyncLine(int line)
    {
        return (line >= 3 && line <= 6)
            || (FormatCatalog.ParentSystem(_system) == "PAL" && line >= 1 && line <= 2);
    }

    private bool HSyncAreaLooksValid(
        ReadOnlySpan<double> syncReference,
        double crossing,
        int oneMicrosecond,
        double endUsec,
        double minimumIre,
        double maximumIre,
        VideoOutputConverter converter)
    {
        int start = Math.Clamp(
            (int)Math.Round(crossing - (0.75 * oneMicrosecond), MidpointRounding.AwayFromZero),
            0,
            syncReference.Length);
        int end = Math.Clamp(
            (int)Math.Round(crossing + (endUsec * oneMicrosecond), MidpointRounding.AwayFromZero),
            start,
            syncReference.Length);
        if (end <= start)
        {
            return false;
        }

        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        for (int i = start; i < end; i++)
        {
            minimum = Math.Min(minimum, syncReference[i]);
            maximum = Math.Max(maximum, syncReference[i]);
        }

        return minimum >= converter.IreToHz(minimumIre)
            && maximum <= converter.IreToHz(maximumIre);
    }

    private static bool TryMeanRange(
        ReadOnlySpan<double> source,
        double start,
        double end,
        out double mean)
    {
        int startIndex = Math.Clamp((int)Math.Round(start, MidpointRounding.AwayFromZero), 0, source.Length);
        int endIndex = Math.Clamp((int)Math.Round(end, MidpointRounding.AwayFromZero), startIndex, source.Length);
        if (endIndex <= startIndex)
        {
            mean = 0.0;
            return false;
        }

        double sum = 0.0;
        for (int i = startIndex; i < endIndex; i++)
        {
            sum += source[i];
        }

        mean = sum / (endIndex - startIndex);
        return true;
    }

    private static LaserDiscAnalogAudioOutputOptions? BuildLaserDiscAnalogAudioOutputOptions(DecodeSession session)
    {
        LaserDiscAudioOptions? audioOptions = session.LaserDiscAudioOptions;
        if (session.Spec.Name != "ld"
            || audioOptions is null
            || !audioOptions.DecodeAnalogAudio
            || audioOptions.AnalogAudioFrequency == 0.0
            || session.Filters.LdAnalogAudio is null)
        {
            return null;
        }

        return new LaserDiscAnalogAudioOutputOptions(
            JsonDouble(session.Parameters.SysParams, "line_period"),
            session.TbcFrameSpec.OutputLineCount,
            audioOptions.AnalogAudioFrequency,
            JsonDouble(session.Parameters.SysParams, "audio_lfreq"),
            JsonDouble(session.Parameters.SysParams, "audio_rfreq"),
            JsonDouble(session.Parameters.SysParams, "FPS"));
    }

    private static LaserDiscRfTbcOptions? BuildLaserDiscRfTbcOptions(DecodeSession session)
    {
        LaserDiscAudioOptions? audioOptions = session.LaserDiscAudioOptions;
        if (session.Spec.Name != "ld" || audioOptions is null || (!audioOptions.WriteRfTbc && !audioOptions.Ac3))
        {
            return null;
        }

        return new LaserDiscRfTbcOptions(
            WriteRfTbc: true,
            VideoWhiteOffsetSamples: session.Filters.LdVideoWhiteOffset);
    }

    private static LaserDiscAgcOptions? BuildLaserDiscAgcOptions(DecodeSession session)
    {
        return BuildLaserDiscAgcOptions(session.Spec.Name, session.Parameters, session.LaserDiscAudioOptions);
    }

    internal static LaserDiscRfMetricOptions? BuildLaserDiscRfMetricOptions(
        string commandName,
        FormatParameterSet parameters,
        DecodeFilterSet filters)
    {
        if (commandName != "ld")
        {
            return null;
        }

        List<LaserDiscVitsLevelSlice> whiteSlices = ReadLaserDiscVitsLevelSlices(
            parameters.SysParams,
            "LD_VITS_whitelocs",
            defaultPercentile: 50.0).ToList();
        LaserDiscVitsLevelSlice? blackSlice = ReadLaserDiscVitsLevelSlice(
            parameters.SysParams,
            "blacksnr_slice",
            defaultPercentile: 50.0);
        return whiteSlices.Count == 0 || blackSlice is null
            ? null
            : new LaserDiscRfMetricOptions(
                whiteSlices,
                blackSlice,
                (int)filters.LdVideoWhiteOffset,
                (int)filters.LdVideoSyncOffset);
    }

    internal static LaserDiscAgcOptions? BuildLaserDiscAgcOptions(
        string commandName,
        FormatParameterSet parameters,
        LaserDiscAudioOptions? audioOptions)
    {
        if (commandName != "ld" || audioOptions?.UseAgc != true)
        {
            return null;
        }

        double colorBurstEnd = ReadDoubleArrayValue(parameters.SysParams, "colorBurstUS", 1, fallback: 0.0);
        double activeVideoStart = ReadDoubleArrayValue(parameters.SysParams, "activeVideoUS", 0, fallback: colorBurstEnd + 1.0);
        var slices = new List<LaserDiscVitsLevelSlice>();
        slices.AddRange(ReadLaserDiscVitsLevelSlices(parameters.SysParams, "LD_VITS_whitelocs", defaultPercentile: 50.0));
        slices.AddRange(ReadLaserDiscVitsLevelSlices(parameters.SysParams, "LD_VITS_code_slices", defaultPercentile: 50.0));
        return new LaserDiscAgcOptions(colorBurstEnd, activeVideoStart, slices);
    }

    private static IEnumerable<LaserDiscVitsLevelSlice> ReadLaserDiscVitsLevelSlices(
        JsonElement element,
        string propertyName,
        double defaultPercentile)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement tuple in property.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 3)
            {
                continue;
            }

            yield return new LaserDiscVitsLevelSlice(
                tuple[0].GetInt32(),
                tuple[1].GetDouble(),
                tuple[2].GetDouble(),
                tuple.GetArrayLength() >= 4 ? tuple[3].GetDouble() : defaultPercentile);
        }
    }

    private static LaserDiscVitsLevelSlice? ReadLaserDiscVitsLevelSlice(
        JsonElement element,
        string propertyName,
        double defaultPercentile)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement tuple)
            || tuple.ValueKind != JsonValueKind.Array
            || tuple.GetArrayLength() < 3)
        {
            return null;
        }

        return new LaserDiscVitsLevelSlice(
            tuple[0].GetInt32(),
            tuple[1].GetDouble(),
            tuple[2].GetDouble(),
            tuple.GetArrayLength() >= 4 ? tuple[3].GetDouble() : defaultPercentile);
    }

    private static double ReadDoubleArrayValue(JsonElement element, string propertyName, int index, double fallback)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Array
            && property.GetArrayLength() > index
            ? property[index].GetDouble()
            : fallback;
    }

    private static short[]? SliceFieldEfm(short[]? efm, IReadOnlyList<double> lineLocations, int lineCount)
    {
        if (efm is null)
        {
            return null;
        }

        if (lineLocations.Count < 2 || lineCount <= 0)
        {
            return [];
        }

        int endLine = Math.Min(lineCount + 1, lineLocations.Count - 1);
        if (endLine <= 1)
        {
            return [];
        }

        int start = Math.Clamp((int)lineLocations[1], 0, efm.Length);
        int end = Math.Clamp((int)lineLocations[endLine], start, efm.Length);
        var output = new short[end - start];
        Array.Copy(efm, start, output, 0, output.Length);
        return output;
    }

    private short[]? DownscaleAnalogAudio(
        LaserDiscAnalogAudioBlock? audio,
        IReadOnlyList<double> lineLocations,
        int fieldLineCount,
        long fieldStartSample,
        int decodedFieldNumber,
        bool? isFirstField)
    {
        if (audio is null || _analogAudioOptions is null || lineLocations.Count < 2)
        {
            return null;
        }

        double outputFrequency = _analogAudioOptions.OutputFrequency;
        double timeOffset = 0.0;
        long absoluteFieldNumber = decodedFieldNumber;
        if (_previousAnalogAudioStartSample.HasValue
            && _analogAudioOptions.FramesPerSecond > 0.0)
        {
            absoluteFieldNumber = LaserDiscAnalogAudioTiming.EstimateFieldNumber(
                _previousAnalogAudioFieldNumber,
                _previousAnalogAudioStartSample.Value,
                fieldStartSample,
                _syncAnalyzer.SampleRateHz,
                _analogAudioOptions.FramesPerSecond);
            if (isFirstField.HasValue)
            {
                int[] fieldLines = FieldLinesForParityDetection();
                timeOffset = LaserDiscAnalogAudioTiming.ComputeTimeOffset(
                    absoluteFieldNumber,
                    isFirstField.Value,
                    fieldLines[0],
                    fieldLines[1],
                    _analogAudioOptions.LinePeriodUs,
                    outputFrequency);
            }
        }

        if (outputFrequency < 0.0)
        {
            timeOffset = 0.0;
            outputFrequency = (1_000_000.0 / _analogAudioOptions.LinePeriodUs) * -outputFrequency;
        }

        double frameTime = fieldLineCount / (1_000_000.0 / _analogAudioOptions.LinePeriodUs);
        double soundGap = 1.0 / outputFrequency;
        double tickSpan = frameTime + (soundGap / 2.0) - timeOffset;
        int tickCount = tickSpan <= 0.0 ? 0 : checked((int)Math.Ceiling(tickSpan / soundGap));
        var ticks = new double[tickCount];
        for (int i = 0; i < ticks.Length; i++)
        {
            ticks[i] = timeOffset + (i * soundGap);
        }

        if (ticks.Length < 2)
        {
            return [];
        }

        double[] locations = new double[ticks.Length];
        double[] wowFactors = new double[ticks.Length];
        double nominalLineLength = _syncAnalyzer.NominalLineLength;
        for (int i = 0; i < ticks.Length; i++)
        {
            double lineNumber = ((ticks[i] * 1_000_000.0) / _analogAudioOptions.LinePeriodUs) + 1.0;
            int integerLine = (int)lineNumber;
            double lineLocationCurrent;
            double lineLocationNext;
            if (lineNumber < 0.0)
            {
                lineLocationCurrent = lineLocations[0] + (nominalLineLength * lineNumber);
                lineLocationNext = lineLocationCurrent + nominalLineLength;
            }
            else if (lineLocations.Count > lineNumber + 2.0)
            {
                lineLocationCurrent = lineLocations[integerLine];
                lineLocationNext = lineLocations[integerLine + 1];
            }
            else
            {
                lineLocationCurrent = lineLocations[^2];
                lineLocationNext = lineLocationCurrent + nominalLineLength;
            }

            double fraction = lineNumber - Math.Floor(lineNumber);
            double sampleLocation = lineLocationCurrent + ((lineLocationNext - lineLocationCurrent) * fraction);
            double wow = (lineLocationNext - lineLocationCurrent) / nominalLineLength;
            if (i > 0 && Math.Abs(wow - wowFactors[i - 1]) > 0.015)
            {
                wow = wowFactors[i - 1];
            }

            locations[i] = sampleLocation / audio.DecimationFactor;
            wowFactors[i] = wow;
        }

        var output = new short[(ticks.Length - 1) * 2];
        for (int i = 0; i < ticks.Length - 1; i++)
        {
            int start = (int)locations[i];
            int end = (int)locations[i + 1];
            if (end <= start || end >= audio.Left.Length || end >= audio.Right.Length)
            {
                continue;
            }

            double left = (Mean(audio.Left.AsSpan(start, end - start)) * wowFactors[i]) - _analogAudioOptions.LeftCarrierHz;
            double right = (Mean(audio.Right.AsSpan(start, end - start)) * wowFactors[i]) - _analogAudioOptions.RightCarrierHz;
            output[i * 2] = (short)-RescaleAndClipAudio(left);
            output[(i * 2) + 1] = (short)-RescaleAndClipAudio(right);
        }

        return output;
    }

    private short[]? BuildRfTbc(
        double[] input,
        IReadOnlyList<double> lineLocations,
        int fieldLineCount)
    {
        if (_rfTbcOptions is null || !_rfTbcOptions.WriteRfTbc)
        {
            return null;
        }

        if (input.Length == 0 || lineLocations.Count < 2)
        {
            return [];
        }

        int lineLength = Math.Max(
            1,
            (int)Math.Round(_syncAnalyzer.NominalLineLength, MidpointRounding.ToEven));
        int startLine = FormatCatalog.ParentSystem(_system) == "NTSC" ? 0 : 1;
        int lineCount = Math.Min(fieldLineCount, lineLocations.Count - startLine - 1);
        if (lineCount <= 0)
        {
            return [];
        }

        var output = new short[checked(lineLength * lineCount)];
        for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            int sourceLine = startLine + lineIndex;
            double begin = lineLocations[sourceLine] - _rfTbcOptions.VideoWhiteOffsetSamples;
            double end = lineLocations[sourceLine + 1] - _rfTbcOptions.VideoWhiteOffsetSamples;
            ScaleCubicToInt16(input, begin, end, output.AsSpan(lineIndex * lineLength, lineLength));
        }

        return output;
    }

    private static void ScaleCubicToInt16(ReadOnlySpan<double> source, double begin, double end, Span<short> destination)
    {
        if (destination.Length == 0)
        {
            return;
        }

        if (source.Length == 0)
        {
            destination.Clear();
            return;
        }

        double lineLength = end - begin;
        double factor = lineLength / destination.Length;
        for (int i = 0; i < destination.Length; i++)
        {
            double coordinate = (i * factor) + begin;
            int integerCoordinate = (int)Math.Truncate(coordinate);
            int start = integerCoordinate - 1;
            double p0 = SampleClamped(source, start);
            double p1 = SampleClamped(source, start + 1);
            double p2 = SampleClamped(source, start + 2);
            double p3 = SampleClamped(source, start + 3);
            double x = coordinate - integerCoordinate;
            double interpolated = p1 + (0.5 * x * (
                p2 - p0
                + (x * ((2.0 * p0) - (5.0 * p1) + (4.0 * p2) - p3
                + (x * ((3.0 * (p1 - p2)) + p3 - p0))))));
            destination[i] = RoundToInt16Wrapping(interpolated);
        }
    }

    private static double SampleClamped(ReadOnlySpan<double> source, int index)
    {
        return source[Math.Clamp(index, 0, source.Length - 1)];
    }

    private static short RoundToInt16Wrapping(double value)
    {
        if (!double.IsFinite(value))
        {
            return short.MinValue;
        }

        long rounded = checked((long)Math.Round(value, MidpointRounding.ToEven));
        return unchecked((short)rounded);
    }

    private static int RescaleAndClipAudio(double value)
    {
        int scaled = (int)Math.Round(value * 32767.0 / 371081.0, MidpointRounding.ToEven);
        return Math.Clamp(scaled, -32766, 32766);
    }

    private Line0Resolution ResolveLine0Location(
        IReadOnlyList<ClassifiedSyncPulse> classified,
        IReadOnlyList<ClassifiedSyncPulse> refinedPulses,
        IReadOnlyList<Pulse> rawPulses,
        SyncTiming timing,
        ReadOnlySpan<double> demodLowPass,
        long spanStartSample,
        double meanLineLength,
        bool? detectedFirstField,
        Line0FallbackCandidate? fallback)
    {
        bool estimatedFirstField = detectedFirstField
            ?? (_previousDetectedFirstField.HasValue ? !_previousDetectedFirstField.Value : true);
        int currentFieldLines = CurrentFieldLineCount(estimatedFirstField);
        VBlankPulseGroup? firstVBlank = FindValidVBlankGroup(refinedPulses, meanLineLength);
        if (firstVBlank is not null
            && _decodeLaserDiscVbi
            && !LaserDiscPlayerSkipDetector.IsFirstVBlankWithinPulseLimit(
                refinedPulses,
                firstVBlank.Equalizing1Start,
                _previousLaserDiscSkipCheckScore))
        {
            firstVBlank = null;
        }

        VBlankPulseGroup? lastVBlank = firstVBlank is null
            ? null
            : FindValidVBlankGroup(
                refinedPulses,
                meanLineLength,
                firstVBlank.Equalizing1Start + ((currentFieldLines - 2.0) * meanLineLength));
        VBlankSyncConsensus? vBlankConsensus = firstVBlank is null
            ? null
            : VBlankSyncResolver.EstimateLine0FromGroups(
                classified,
                firstVBlank,
                lastVBlank,
                meanLineLength,
                _system,
                _syncAnalyzer.NumPulses,
                estimatedFirstField,
                currentFieldLines);
        if (vBlankConsensus is null
            || (vBlankConsensus.First is null
                && vBlankConsensus.Last is null
                && vBlankConsensus.Combined is null))
        {
            int[] fieldLines = FieldLinesForParityDetection();
            vBlankConsensus = VBlankSyncResolver.EstimateLine0FromTransitions(
                refinedPulses,
                meanLineLength,
                _system,
                _syncAnalyzer.NumPulses,
                estimatedFirstField,
                currentFieldLines,
                fieldLines[0]);
        }
        if (vBlankConsensus?.Combined is { } combined)
        {
            return new Line0Resolution(
                combined.Line0Location,
                UsedFallback: false,
                fallback?.ExpectedFirstField,
                fallback?.ExpectedFirstFieldConfidence ?? 0,
                FirstHSyncLocation: combined.FirstHSyncLocation,
                UnalignedFirstHSyncLocation: combined.UnalignedFirstHSyncLocation);
        }

        if (fallback is not null)
        {
            return AlignLine0Anchor(
                classified,
                fallback.Location,
                meanLineLength,
                currentFieldLines,
                estimatedFirstField,
                usedFallback: true,
                fallback.ExpectedFirstField,
                fallback.ExpectedFirstFieldConfidence);
        }

        VBlankSyncEstimate? singleVBlank = SelectSingleVBlankEstimate(vBlankConsensus);
        if (singleVBlank is not null)
        {
            return new Line0Resolution(
                singleVBlank.Line0Location,
                UsedFallback: false,
                null,
                0,
                FirstHSyncLocation: singleVBlank.FirstHSyncLocation,
                UnalignedFirstHSyncLocation: singleVBlank.UnalignedFirstHSyncLocation);
        }

        Line0Resolution? previousEstimate = TryEstimateLine0FromPrevious(
            classified,
            spanStartSample,
            meanLineLength,
            currentFieldLines,
            estimatedFirstField);
        if (previousEstimate is not null)
        {
            return previousEstimate;
        }

        // The decode CLIs require a vblank, fallback, or previous-field anchor.
        // The untyped path is retained for small direct pipeline fixtures.
        double? firstHSync = _decodeType is null
            ? FindFirstHSyncLocation(classified)
            : null;
        if (firstHSync.HasValue)
        {
            return AlignLine0Anchor(
                classified,
                firstHSync.Value,
                meanLineLength,
                currentFieldLines,
                estimatedFirstField,
                usedFallback: false,
                expectedFirstField: null,
                expectedFirstFieldConfidence: 0);
        }

        throw BuildRecoveryException(
            TbcFieldDecodeRecoveryKind.NoFirstHSync,
            "No HSYNC pulse was detected in the decoded span.");
    }

    private InvalidOperationException BuildRecoveryException(
        TbcFieldDecodeRecoveryKind kind,
        string message,
        double? suggestedOffsetSamples = null)
    {
        if (_decodeType is null)
        {
            return new InvalidOperationException(message);
        }

        bool tape = string.Equals(_decodeType, "vhs", StringComparison.Ordinal);
        double suggested = suggestedOffsetSamples ?? kind switch
        {
            TbcFieldDecodeRecoveryKind.NoSyncPulses => _syncAnalyzer.SampleRateHz / (tape ? 10.0 : 1.0),
            TbcFieldDecodeRecoveryKind.NoFirstHSync => _syncAnalyzer.NominalLineLength * (tape ? 100.0 : 200.0),
            _ => _syncAnalyzer.NominalLineLength
        };
        long offset = checked((long)suggested);
        return new TbcFieldDecodeRecoveryException(
            kind,
            offset,
            message,
            stopAfterDecodedFields: !tape && kind == TbcFieldDecodeRecoveryKind.NoSyncPulses);
    }

    private void ThrowIfInsufficientFieldData(
        RfDecodedSpan span,
        IReadOnlyList<Pulse> rawPulses,
        double line0Location,
        double meanLineLength,
        int processedLines)
    {
        if (_decodeType is null || meanLineLength <= 0.0)
        {
            return;
        }

        bool tape = string.Equals(_decodeType, "vhs", StringComparison.Ordinal);
        double availableEnd = tape
            ? span.Input.Length
            : rawPulses[^1].Start;
        double lastLine = tape
            ? ((availableEnd - line0Location) / meanLineLength) - 1.0
            : (availableEnd - line0Location) / meanLineLength;
        if (lastLine >= processedLines)
        {
            return;
        }

        double suggested = line0Location - (meanLineLength * 20.0);
        if (!string.Equals(_decodeType, "ld", StringComparison.Ordinal))
        {
            suggested = Math.Max(suggested, _syncAnalyzer.NominalLineLength);
        }
        throw BuildRecoveryException(
            TbcFieldDecodeRecoveryKind.InsufficientData,
            "Missing data at the end of the decoded field.",
            suggested);
    }

    private VBlankSyncEstimate? SelectSingleVBlankEstimate(VBlankSyncConsensus? consensus)
    {
        if (consensus is null)
        {
            return null;
        }

        int firstCount = consensus.First?.ValidDistanceCount ?? 0;
        int lastCount = consensus.Last?.ValidDistanceCount ?? 0;
        bool hasPreviousSync = _previousFirstHSyncLocation is > 0.0;
        if (firstCount == 6 || (!hasPreviousSync && firstCount > lastCount))
        {
            return consensus.First;
        }

        if (lastCount == 6 || (!hasPreviousSync && lastCount > firstCount))
        {
            return consensus.Last;
        }

        return null;
    }

    private int LaserDiscLineOffset(bool? isFirstField)
    {
        if (FormatCatalog.ParentSystem(_system) != "PAL")
        {
            return 0;
        }

        return isFirstField == false ? 3 : 2;
    }

    private int LaserDiscProcessedLineCount(bool? isFirstField)
    {
        int palLookahead = FormatCatalog.ParentSystem(_system) == "PAL" ? 3 : 0;
        return checked(
            _renderer.FrameSpec.OutputLineCount
            + LaserDiscLineOffset(isFirstField)
            + LineLocationLookahead
            + palLookahead);
    }

    private void UpdateSyncHistory(
        long spanStartSample,
        Line0Resolution resolution,
        double meanLineLength,
        FieldParityDetection parity)
    {
        double? estimatedFirstHSync = EstimateFirstHSyncFromPrevious(
            spanStartSample,
            meanLineLength,
            parity.IsFirstField);
        if (!resolution.UsedPreviousEstimate
            && estimatedFirstHSync.HasValue
            && double.IsFinite(resolution.UnalignedFirstHSyncLocation))
        {
            _previousHSyncDifference =
                (resolution.UnalignedFirstHSyncLocation - estimatedFirstHSync.Value) / meanLineLength;
        }

        if (double.IsFinite(resolution.FirstHSyncLocation))
        {
            _previousFirstHSyncReadLocation = spanStartSample;
            _previousFirstHSyncLocation = resolution.FirstHSyncLocation;
        }

        _previousDetectedFirstField = parity.IsFirstField;
    }

    private double? EstimateFirstHSyncFromPrevious(
        long spanStartSample,
        double meanLineLength,
        bool isFirstField)
    {
        if (!_previousFirstHSyncLocation.HasValue
            || !_previousFirstHSyncReadLocation.HasValue
            || _previousFirstHSyncLocation.Value <= 0.0
            || meanLineLength <= 0.0)
        {
            return null;
        }

        int[] fieldLines = FieldLinesForParityDetection();
        double currentFieldLines = fieldLines[isFirstField ? 0 : 1];
        double previousFieldLines = fieldLines[isFirstField ? 1 : 0];
        double estimatedFieldLines = FormatCatalog.ParentSystem(_system) == "NTSC"
            ? previousFieldLines
            : currentFieldLines;
        double lastFieldOffsetLines =
            (_previousFirstHSyncReadLocation.Value - spanStartSample) / meanLineLength;
        return Math.Round(
            (lastFieldOffsetLines
                + estimatedFieldLines
                + (_previousFirstHSyncLocation.Value / meanLineLength))
            * meanLineLength,
            MidpointRounding.AwayFromZero);
    }

    private Line0Resolution AlignLine0Anchor(
        IReadOnlyList<ClassifiedSyncPulse> classified,
        double line0Location,
        double meanLineLength,
        int currentFieldLines,
        bool isFirstField,
        bool usedFallback,
        bool? expectedFirstField,
        int expectedFirstFieldConfidence)
    {
        double firstHSyncLine = VBlankSyncResolver.FirstHSyncLine(
            _system,
            _syncAnalyzer.NumPulses,
            isFirstField);
        double unalignedFirstHSync = Math.Round(
            line0Location + (meanLineLength * firstHSyncLine),
            MidpointRounding.AwayFromZero);
        double firstHSync = AlignFirstHSyncToValidPulses(
            classified,
            unalignedFirstHSync,
            meanLineLength,
            firstHSyncLine,
            currentFieldLines);
        return new Line0Resolution(
            firstHSync - (meanLineLength * firstHSyncLine),
            usedFallback,
            expectedFirstField,
            expectedFirstFieldConfidence,
            FirstHSyncLocation: firstHSync,
            UnalignedFirstHSyncLocation: unalignedFirstHSync);
    }

    private static double AlignFirstHSyncToValidPulses(
        IReadOnlyList<ClassifiedSyncPulse> classified,
        double firstHSyncLocation,
        double meanLineLength,
        double firstHSyncLine,
        int currentFieldLines)
    {
        double hSyncOffset = 0.0;
        int hSyncCount = 0;
        foreach (ClassifiedSyncPulse pulse in classified)
        {
            if (pulse.Kind != SyncPulseKind.HSync || !pulse.InOrder)
            {
                continue;
            }

            double line = ((pulse.Pulse.Start - firstHSyncLocation) / meanLineLength) + firstHSyncLine;
            int roundedLine = (int)Math.Round(line, MidpointRounding.AwayFromZero);
            if (roundedLine > currentFieldLines)
            {
                break;
            }

            if (roundedLine >= firstHSyncLine)
            {
                hSyncOffset += firstHSyncLocation
                    + (meanLineLength * (roundedLine - firstHSyncLine))
                    - pulse.Pulse.Start;
                hSyncCount++;
            }
        }

        return hSyncCount > 0
            ? firstHSyncLocation - (hSyncOffset / hSyncCount)
            : firstHSyncLocation;
    }

    private VBlankPulseGroup? FindValidVBlankGroup(
        IReadOnlyList<ClassifiedSyncPulse> classified,
        double meanLineLength,
        double? minimumEqualizing1Start = null)
    {
        double? minimum = minimumEqualizing1Start;
        while (true)
        {
            int blankLengthThreshold = string.Equals(_decodeType, "vhs", StringComparison.Ordinal)
                ? 9
                : 12;
            VBlankPulseGroup? group = VBlankSyncResolver.FindFirstGroup(
                classified,
                minimum,
                blankLengthThreshold);
            if (group is null)
            {
                return null;
            }

            if (VBlankSyncResolver.HasValidStateMachineTiming(
                group,
                meanLineLength,
                _syncAnalyzer.NumPulses))
            {
                return group;
            }

            minimum = group.Equalizing1Start;
        }
    }

    private Line0Resolution? TryEstimateLine0FromPrevious(
        IReadOnlyList<ClassifiedSyncPulse> classified,
        long spanStartSample,
        double meanLineLength,
        int currentFieldLines,
        bool isFirstField)
    {
        double? estimatedFirstHSyncValue = EstimateFirstHSyncFromPrevious(
            spanStartSample,
            meanLineLength,
            isFirstField);
        if (!estimatedFirstHSyncValue.HasValue)
        {
            return null;
        }

        double firstHSyncLine = VBlankSyncResolver.FirstHSyncLine(
            _system,
            _syncAnalyzer.NumPulses,
            isFirstField);
        double estimatedFirstHSync = estimatedFirstHSyncValue.Value;
        if (_previousHSyncDifference >= -0.5 && _previousHSyncDifference <= 0.5)
        {
            estimatedFirstHSync += meanLineLength * _previousHSyncDifference;
        }

        if (estimatedFirstHSync <= 0.0)
        {
            estimatedFirstHSync = classified.Count > 0 ? classified[0].Pulse.Start : 0.0;
        }

        double unalignedFirstHSync = Math.Round(
            estimatedFirstHSync,
            MidpointRounding.AwayFromZero);
        double firstHSync = AlignFirstHSyncToValidPulses(
            classified,
            unalignedFirstHSync,
            meanLineLength,
            firstHSyncLine,
            currentFieldLines);
        return new Line0Resolution(
            firstHSync - (firstHSyncLine * meanLineLength),
            UsedFallback: false,
            ExpectedFirstField: null,
            ExpectedFirstFieldConfidence: 0,
            UsedPreviousEstimate: true,
            FirstHSyncLocation: firstHSync,
            UnalignedFirstHSyncLocation: unalignedFirstHSync);
    }

    private Line0FallbackCandidate? TryResolveFallbackLine0(
        IReadOnlyList<ClassifiedSyncPulse> validPulses,
        IReadOnlyList<Pulse> rawPulses,
        ReadOnlySpan<double> demodLowPass,
        SyncTiming timing,
        long spanStartSample,
        double meanLineLength)
    {
        if (!_syncDetectionOptions.UseFallbackVSync || meanLineLength <= 0.0)
        {
            return null;
        }

        double? expectedLine0 = null;
        bool? expectedFirstField = null;
        if (_previousFirstHSyncLocation.HasValue
            && _previousFirstHSyncReadLocation.HasValue)
        {
            int[] historyFieldLines = FieldLinesForParityDetection();
            double linesPerField = (historyFieldLines[0] + historyFieldLines[1]) / 2.0;
            double previousAbsolute = _previousFirstHSyncReadLocation.Value
                + _previousFirstHSyncLocation.Value;
            double prediction = previousAbsolute
                + ((linesPerField - 8.0) * meanLineLength)
                - spanStartSample;
            if (double.IsFinite(prediction))
            {
                expectedLine0 = prediction;
                expectedFirstField = _previousDetectedFirstField.HasValue
                    ? !_previousDetectedFirstField.Value
                    : null;
            }
        }

        int[] fieldLines = FieldLinesForParityDetection();
        FallbackVSyncResolution? resolution = FallbackVSyncResolver.Resolve(
            validPulses,
            rawPulses,
            demodLowPass,
            timing.VSync,
            meanLineLength,
            _syncAnalyzer.NumPulses,
            fieldLines[0] + fieldLines[1],
            _syncDetectionOptions.RelaxedLine0,
            expectedLine0,
            expectedFirstField);
        return resolution is null
            ? null
            : new Line0FallbackCandidate(
                expectedLine0 ?? resolution.Line0Location,
                resolution.Line0Location,
                resolution.IsFirstField,
                resolution.FirstFieldConfidence);
    }

    private static double? FindFirstHSyncLocation(IReadOnlyList<ClassifiedSyncPulse> classified)
    {
        foreach (ClassifiedSyncPulse pulse in classified)
        {
            if (pulse.Kind == SyncPulseKind.HSync)
            {
                return pulse.Pulse.Start;
            }
        }

        return null;
    }

    private TbcDropoutMap? DetectDropouts(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        SyncTiming timing,
        bool? isFirstField)
    {
        if (!_dropoutOptions.Enabled)
        {
            return null;
        }

        if (_dropoutOptions.Mode == TbcDropoutDetectionMode.Disabled)
        {
            return null;
        }

        return _dropoutOptions.Mode == TbcDropoutDetectionMode.LaserDiscDemod
            ? DetectLaserDiscDropouts(span, lineLocations, timing, isFirstField)
            : DetectTapeDropouts(span, lineLocations, isFirstField);
    }

    private TbcDropoutMap DetectTapeDropouts(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        bool? isFirstField)
    {
        if (span.Envelope is not { Length: > 0 } envelope)
        {
            return TbcDropoutMap.Empty;
        }

        int lineOffset = LaserDiscLineOffset(isFirstField);
        int startLine = lineOffset + 1;
        int endLine = Math.Min(
            lineLocations.Locations.Length - 1,
            CurrentFieldLineCount(isFirstField) + startLine + 1);
        if (startLine < 0 || endLine <= startLine || startLine >= lineLocations.Locations.Length)
        {
            return TbcDropoutMap.Empty;
        }

        int startSample = Math.Clamp((int)Math.Floor(lineLocations.Locations[startLine]), 0, envelope.Length);
        int endSample = Math.Clamp((int)Math.Ceiling(lineLocations.Locations[endLine]), startSample, envelope.Length);
        if (endSample <= startSample)
        {
            return TbcDropoutMap.Empty;
        }

        double threshold = _dropoutOptions.AbsoluteThreshold
            ?? Mean(envelope) * _dropoutOptions.ThresholdFraction;
        IReadOnlyList<RfDropoutRange> ranges = RfDropoutDetector.FindDropouts(
            envelope,
            startSample,
            endSample,
            threshold,
            _dropoutOptions.Hysteresis);
        return TbcDropoutMapper.MapTapeRfToTbc(
            ranges,
            lineLocations.Locations,
            _renderer.FrameSpec.OutputLineLength,
            startLine,
            endLine,
            lineOffset);
    }

    private TbcDropoutMap DetectLaserDiscDropouts(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        SyncTiming timing,
        bool? isFirstField)
    {
        int lineOffset = LaserDiscLineOffset(isFirstField);
        int startLine = lineOffset + 1;
        int endLine = Math.Min(
            lineLocations.Locations.Length - 1,
            lineOffset + CurrentFieldLineCount(isFirstField) + 1);
        if (startLine < 0 || endLine <= startLine || startLine >= lineLocations.Locations.Length)
        {
            return TbcDropoutMap.Empty;
        }

        int startSample = Math.Clamp((int)Math.Floor(lineLocations.Locations[startLine]), 0, span.Video.Length);
        int endSample = Math.Clamp((int)Math.Ceiling(lineLocations.Locations[endLine]), startSample, span.Video.Length);
        if (endSample <= startSample)
        {
            return TbcDropoutMap.Empty;
        }

        IReadOnlyList<RfDropoutRange> ranges = DetectDemodDropoutRanges(
            span,
            lineLocations,
            timing,
            isFirstField,
            startSample,
            endSample);
        return TbcDropoutMapper.MapLaserDiscRfToTbc(
            ranges,
            lineLocations.Locations,
            _renderer.FrameSpec.OutputLineLength,
            lineOffset,
            CurrentFieldLineCount(isFirstField));
    }

    private IReadOnlyList<RfDropoutRange> DetectDemodDropoutRanges(
        RfDecodedSpan span,
        LineLocationResult lineLocations,
        SyncTiming timing,
        bool? isFirstField,
        int startSample,
        int endSample)
    {
        bool isPal = FormatCatalog.ParentSystem(_system) == "PAL";
        double validMin = _videoOutput.IreToHz(isPal ? -70.0 : -50.0);
        double validMax = _videoOutput.IreToHz(isPal ? 150.0 : 160.0);
        double syncMin = _videoOutput.IreToHz(_videoOutput.VSyncIre - (isPal ? 60.0 : 35.0));
        double validMin05 = _videoOutput.IreToHz(-30.0);
        double validMax05 = _videoOutput.IreToHz(115.0);
        double syncMin05 = _videoOutput.IreToHz(_videoOutput.VSyncIre - 10.0);
        bool[] syncArea = BuildSyncAreaMask(span.Video.Length, lineLocations, timing, isFirstField);
        bool[] errorMap = new bool[span.Video.Length];
        bool hasRawDemod = span.DemodRaw.Length == span.Video.Length;
        bool hasVideoLowPass = span.VideoLowPass?.Length == span.Video.Length;
        bool hasRfHighPass = span.RfHighPass?.Length == span.Video.Length;
        double rawDemodMaximum = _syncAnalyzer.SampleRateHz / 2.0;
        double rfHighPassMaximum = hasRfHighPass
            ? StandardDeviation(span.RfHighPass!) * 3.0
            : double.PositiveInfinity;

        for (int i = startSample; i < endSample; i++)
        {
            double minimum = syncArea[i] ? syncMin : validMin;
            double video = span.Video[i];
            bool videoOutOfRange = video < minimum || video > validMax;
            bool rawDemodOutOfRange = hasRawDemod && span.DemodRaw[i] > rawDemodMaximum;
            bool videoLowPassOutOfRange = false;
            if (hasVideoLowPass)
            {
                double lowPassMinimum = syncArea[i] ? syncMin05 : validMin05;
                double lowPassVideo = span.VideoLowPass![i];
                videoLowPassOutOfRange = lowPassVideo < lowPassMinimum || lowPassVideo > validMax05;
            }

            errorMap[i] = videoOutOfRange || rawDemodOutOfRange || videoLowPassOutOfRange;
        }

        if (hasRfHighPass)
        {
            for (int i = startSample; i < endSample; i++)
            {
                double value = span.RfHighPass![i];
                if (!(value < -rfHighPassMaximum || value > rfHighPassMaximum))
                {
                    continue;
                }

                int mapped = i + _rfHighPassOffset;
                if (mapped >= startSample && mapped < endSample)
                {
                    errorMap[mapped] = true;
                }
            }
        }

        return LaserDiscDropoutDetector.BuildErrorRanges(
            errorMap,
            startSample,
            endSample,
            _syncAnalyzer.SampleRateMHz);
    }

    private bool[] BuildSyncAreaMask(
        int length,
        LineLocationResult lineLocations,
        SyncTiming timing,
        bool? isFirstField)
    {
        bool[] syncArea = new bool[length];
        int endLine = lineLocations.Locations.Length - 1;
        int hsyncLength = Math.Max(1, (int)timing.HSync.Maximum);

        for (int line = 1; line < endLine; line++)
        {
            int lineStart = (int)Math.Floor(lineLocations.Locations[line]);
            int lineEnd = (int)Math.Ceiling(lineLocations.Locations[line + 1]);
            MarkRange(syncArea, lineStart, Math.Min(lineEnd, lineStart + hsyncLength));
        }

        if (isFirstField.HasValue)
        {
            MarkExpectedVSyncLines(syncArea, lineLocations.Locations, endLine, isFirstField.Value);
        }

        return syncArea;
    }

    private void MarkExpectedVSyncLines(bool[] syncArea, IReadOnlyList<double> lineLocations, int endLine, bool isFirstField)
    {
        int earlyEnd = isFirstField ? 10 : 9;
        for (int line = 1; line < earlyEnd && line < endLine; line++)
        {
            MarkLine(syncArea, lineLocations, line);
        }

        if (FormatCatalog.ParentSystem(_system) == "PAL")
        {
            int lateStart = isFirstField ? 311 : 310;
            for (int line = lateStart; line < 318 && line < endLine; line++)
            {
                MarkLine(syncArea, lineLocations, line);
            }
        }
    }

    private static void MarkLine(bool[] target, IReadOnlyList<double> lineLocations, int line)
    {
        MarkRange(
            target,
            (int)Math.Floor(lineLocations[line]),
            (int)Math.Ceiling(lineLocations[line + 1]));
    }

    private static void MarkRange(bool[] target, int start, int end)
    {
        int actualStart = Math.Clamp(start, 0, target.Length);
        int actualEnd = Math.Clamp(end, actualStart, target.Length);
        for (int i = actualStart; i < actualEnd; i++)
        {
            target[i] = true;
        }
    }

    private static double Mean(ReadOnlySpan<double> values)
    {
        double sum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    private static double StandardDeviation(ReadOnlySpan<double> values)
    {
        double mean = Mean(values);
        double sumSquares = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            double distance = values[i] - mean;
            sumSquares += distance * distance;
        }

        return Math.Sqrt(sumSquares / values.Length);
    }

    private static double MaxCentered(ReadOnlySpan<double> values, double mean)
    {
        double maximum = double.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            maximum = Math.Max(maximum, values[i] - mean);
        }

        return maximum;
    }

    private static double MaxAbsCentered(ReadOnlySpan<double> values, double mean)
    {
        double maximum = double.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            maximum = Math.Max(maximum, Math.Abs(values[i] - mean));
        }

        return maximum;
    }

    private static double JsonDouble(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetDouble();
    }

    private static int JsonInt(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetInt32();
    }

    private static double JsonDoubleOrDefault(JsonElement element, string propertyName, double defaultValue)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            ? property.GetDouble()
            : defaultValue;
    }

    private static int JsonIntOrDefault(JsonElement element, string propertyName, int defaultValue)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : defaultValue;
    }

    private static int[]? ReadIntArrayOrNull(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Expected {propertyName} to be an integer array.");
        }

        var values = new int[property.GetArrayLength()];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = property[i].GetInt32();
        }

        return values;
    }
}
