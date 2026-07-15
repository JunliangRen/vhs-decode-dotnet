using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Rf;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Decode;

public sealed record DecodeSession(
    DecodeCommandSpec Spec,
    string InputFile,
    string OutputBase,
    string System,
    FormatParameterSet Parameters,
    double BlackIre,
    double LevelAdjust,
    TbcDropoutDetectionOptions DropoutOptions,
    double DecodeSampleRateHz,
    int BlockLength,
    int BlockCut,
    int BlockCutEnd,
    IRfSampleLoader Loader,
    DecodeFilterSet Filters,
    DecodeFilterOptions FilterOptions,
    RfBlockDecodePipeline Pipeline,
    RfBlockStreamDecoder StreamDecoder,
    TbcFrameSpec TbcFrameSpec,
    VideoOutputConverter VideoOutput,
    TbcFieldRenderer TbcRenderer,
    HSyncRefineOptions HSyncRefineOptions,
    SyncDetectionOptions SyncDetectionOptions,
    TbcFieldDecodePipeline TbcFieldDecoder,
    TbcFieldOrderOptions FieldOrderOptions,
    DecodeExecutionOptions ExecutionOptions,
    DecodeRunBounds RunBounds,
    ChromaDecodeOptions? ChromaOptions,
    LaserDiscAudioOptions? LaserDiscAudioOptions,
    string? TestLdfOutputPath) : IDisposable
{
    internal DecodeRuntimeReporter? RuntimeReporter { get; set; }

    public void Dispose()
    {
        Pipeline.Dispose();
        if (Loader is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public sealed record ChromaDecodeOptions(
    bool IsColorUnder,
    bool WriteChroma,
    bool SkipChroma,
    bool UseChromaAfc,
    bool DisableComb,
    bool ChromaDeemphasisFilter,
    bool ChromaAudioNotch,
    int ChromaOffsetSamples,
    bool DetectChromaTrackPhase,
    bool EnableColorKiller,
    bool DisableBurstHsync,
    bool DisablePhaseCorrection,
    bool UseOldRawChromaOutput);

public sealed record DecodeExecutionOptions(
    int RequestedThreads,
    int WorkerThreads,
    int SeekFrame,
    bool WriteDebugData,
    bool Debug,
    string? DebugPlotPath,
    bool IgnoreLeadOut,
    bool VerboseVits,
    bool UseProfiler,
    bool CxAdcCompatibilityMode);

public sealed record LaserDiscAudioOptions(
    bool DecodeDigitalAudio,
    bool WritePreEfm,
    bool DecodeAnalogAudio,
    double AnalogAudioFrequency,
    bool UseNtscAudioRate,
    bool Ac3,
    bool WriteRfTbc,
    bool UseAgc,
    double? AudioFilterWidthHz);

public static class DecodeSessionFactory
{
    public const int DefaultBlockLength = 32 * 1024;

    public static DecodeSession Create(ParsedCommand command, int blockLength = DefaultBlockLength)
    {
        if (command.Positionals.Count < 2)
        {
            throw new ArgumentException("the following arguments are required: infile, outfile");
        }

        if (blockLength <= 0 || blockLength % 2 != 0)
        {
            throw new ArgumentException("Block length must be a positive even value.", nameof(blockLength));
        }

        return command.Spec.Name switch
        {
            "vhs" => CreateVhs(command, blockLength),
            "cvbs" => CreateCvbs(command, blockLength),
            "ld" => CreateLaserDisc(command, blockLength),
            _ => throw new NotSupportedException($"Unsupported decode command '{command.Spec.Name}'.")
        };
    }

    private static DecodeSession CreateVhs(ParsedCommand command, int blockLength)
    {
        string system = VideoSystemSelector.Select(command);
        double selectedSampleRateMHz = SelectCommonSampleFrequencyMHz(command);
        bool noResample = command.Get<bool>("no_resample");
        double decodeSampleRateMHz = noResample ? selectedSampleRateMHz : FrequencyParser.DddMHz;
        bool nativeFortyMegahertzContainer = Math.Abs(selectedSampleRateMHz - FrequencyParser.DddMHz) <= 1e-9
            && (command.InputFile.EndsWith(".lds", StringComparison.Ordinal)
                || command.InputFile.EndsWith(".ldf", StringComparison.Ordinal));
        bool useFfmpegInputPath = !noResample && !nativeFortyMegahertzContainer;
        IRfSampleLoader loader = CreateLoader(command.InputFile, useFfmpegInputPath, selectedSampleRateMHz);
        FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters(
            system,
            command.Get<string>("tape_format"),
            command.Get<string>("tape_speed"));
        parameters = VhsParamsFileOverride.Apply(parameters, NullableString(command, "params_file"));
        return Build(command, system, parameters, decodeSampleRateMHz, blockLength, blockCut: 1024, blockCutEnd: 1024, loader);
    }

    private static DecodeSession CreateCvbs(ParsedCommand command, int blockLength)
    {
        string system = VideoSystemSelector.Select(command);
        double selectedSampleRateMHz = SelectCommonSampleFrequencyMHz(command);
        IRfSampleLoader loader = RfLoaderFactory.CreateResampling(command.InputFile, selectedSampleRateMHz);
        FormatParameterSet parameters = FormatCatalog.Default.GetCvbsParameters(system);
        return Build(command, system, parameters, FrequencyParser.DddMHz, blockLength, blockCut: 1024, blockCutEnd: 1024, loader);
    }

    private static DecodeSession CreateLaserDisc(ParsedCommand command, int blockLength)
    {
        bool pal = command.Get<bool>("pal");
        bool ntsc = command.Get<bool>("ntsc");
        bool ntscj = command.Get<bool>("ntscj");
        if (pal && (ntsc || ntscj))
        {
            throw new ArgumentException("ERROR: Can only be PAL or NTSC");
        }

        if (pal && command.Get<bool>("AC3"))
        {
            throw new ArgumentException("ERROR: AC3 audio decoding is only supported for NTSC");
        }

        double? inputFrequencyMHz = NullableDouble(command, "inputfreq");
        IRfSampleLoader loader = CreateLoader(command.InputFile, inputFrequencyMHz.HasValue, inputFrequencyMHz);
        string system = pal ? "PAL" : "NTSC";
        FormatParameterSet parameters = FormatCatalog.Default.GetLaserDiscParameters(system, command.Get<bool>("lowband"));
        parameters = ApplyLaserDiscOverrides(parameters, command);
        return Build(command, system, parameters, FrequencyParser.DddMHz, blockLength, blockCut: 1024, blockCutEnd: 32, loader);
    }

    private static DecodeSession Build(
        ParsedCommand command,
        string system,
        FormatParameterSet parameters,
        double sampleRateMHz,
        int blockLength,
        int blockCut,
        int blockCutEnd,
        IRfSampleLoader loader)
    {
        double sampleRateHz = sampleRateMHz * 1_000_000.0;
        ChromaDecodeOptions? chromaOptions = BuildChromaOptions(command, system, parameters, sampleRateMHz);
        DecodeFilterOptions filterOptions = BuildFilterOptions(command, system, parameters, chromaOptions);
        DecodeFilterSet filters = DecodeFilterSetBuilder.BuildBasic(parameters, sampleRateHz, blockLength, filterOptions);
        VideoOutputConverter videoOutput = VideoOutputConverter.FromParameters(parameters);
        DecodeExecutionOptions executionOptions = BuildExecutionOptions(command);
        var pipeline = new RfBlockDecodePipeline(
            loader,
            filters,
            sampleRateHz,
            filterOptions,
            BuildCvbsDecodeOptions(command, videoOutput),
            BuildRfInputProcessor(command));
        var streamDecoder = new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut,
            blockCutEnd,
            executionOptions.WorkerThreads);
        TbcFrameSpec tbcFrameSpec = TbcFrameSpec.FromParameters(parameters);
        var tbcRenderer = new TbcFieldRenderer(
            tbcFrameSpec,
            videoOutput,
            BuildYCombLimitHz(command, parameters),
            BuildIre0AdjustOptions(command, parameters),
            BuildCvbsClampAgcOptions(command),
            IsExportRawTbc(command),
            BuildTrackPhaseIre0OffsetOptions(command, system, parameters),
            BuildTbcInterpolationMethod(command),
            BuildWowLevelAdjustSmoothing(command, parameters),
            nominalInputLineLength: Math.Round(
                JsonRequiredDouble(parameters.SysParams, "line_period") * sampleRateMHz,
                MidpointRounding.ToEven));
        TbcDropoutDetectionOptions dropoutOptions = BuildDropoutOptions(command, sampleRateMHz);
        TbcFieldOrderOptions fieldOrderOptions = BuildFieldOrderOptions(command, parameters);
        LaserDiscAudioOptions? laserDiscAudioOptions = BuildLaserDiscAudioOptions(command, system);
        HSyncRefineOptions hSyncRefineOptions = BuildHSyncRefineOptions(command);
        SyncDetectionOptions syncDetectionOptions = BuildSyncDetectionOptions(
            command,
            system,
            parameters,
            sampleRateMHz);
        var tbcFieldDecoder = new TbcFieldDecodePipeline(
            SyncAnalyzer.FromParameters(
                parameters,
                sampleRateHz,
                hsyncToleranceUs: command.Spec.Name == "vhs" ? 0.7 : 0.5,
                equalizingToleranceUs: command.Spec.Name == "vhs" ? 0.9 : 0.5),
            tbcRenderer,
            videoOutput,
            system,
            dropoutOptions,
            filters.RfHighPassOffset,
            fieldOrderOptions.Confidence,
            BuildLaserDiscAnalogAudioOutputOptions(command, parameters, filters, tbcFrameSpec, laserDiscAudioOptions),
            BuildLaserDiscRfTbcOptions(command, filters, laserDiscAudioOptions),
            TbcFieldDecodePipeline.BuildLaserDiscAgcOptions(command.Spec.Name, parameters, laserDiscAudioOptions),
            hSyncRefineOptions,
            syncDetectionOptions,
            command.Spec.Name == "ld",
            command.Spec.Name == "ld" && command.Get<bool>("verboseVITS"),
            TbcFieldDecodePipeline.BuildChromaFieldOptions(
                system,
                parameters,
                tbcFrameSpec,
                chromaOptions,
                filterOptions,
                sampleRateHz,
                tbcRenderer.TrackPhaseIre0Offset?.TrackPhase),
            TbcFieldDecodePipeline.BuildLaserDiscPilotRefineOptions(command.Spec.Name, system, parameters),
            TbcFieldDecodePipeline.BuildLaserDiscNtscBurstRefineOptions(command.Spec.Name, system, parameters),
            command.Spec.Name,
            decodeVbiData: command.Spec.Name == "cvbs",
            laserDiscRfMetricOptions: TbcFieldDecodePipeline.BuildLaserDiscRfMetricOptions(
                command.Spec.Name,
                parameters,
                filters),
            vsyncSerrationDetector: TbcFieldDecodePipeline.BuildVsyncSerrationDetector(
                command.Spec.Name,
                system,
                parameters,
                sampleRateHz,
                syncDetectionOptions),
            framesPerSecond: JsonRequiredDouble(parameters.SysParams, "FPS"),
            diagnosticLogger: (level, message) => DecodeSessionLogWriter.Append(
                command.OutputBase + ".log",
                level,
                message),
            debug: executionOptions.Debug,
            inputBlockCutSamples: blockCut);
        DecodeRunBounds runBounds = DecodeRunBounds.FromCommand(
            command,
            tbcFieldDecoder.EstimateNominalFieldSampleCount());
        return new DecodeSession(
            command.Spec,
            command.InputFile,
            command.OutputBase,
            system,
            parameters,
            ComputeBlackIre(command, system),
            BuildLevelAdjust(command),
            dropoutOptions,
            sampleRateHz,
            blockLength,
            blockCut,
            blockCutEnd,
            loader,
            filters,
            filterOptions,
            pipeline,
            streamDecoder,
            tbcFrameSpec,
            videoOutput,
            tbcRenderer,
            hSyncRefineOptions,
            syncDetectionOptions,
            tbcFieldDecoder,
            fieldOrderOptions,
            executionOptions,
            runBounds,
            chromaOptions,
            laserDiscAudioOptions,
            NullableString(command, "write_test_ldf"));
    }

    private static double BuildLevelAdjust(ParsedCommand command)
    {
        return command.Spec.Name switch
        {
            "vhs" => NullableDouble(command, "level_adjust") ?? 0.1,
            "cvbs" => 0.2,
            _ => 0.0
        };
    }

    private static HSyncRefineOptions BuildHSyncRefineOptions(ParsedCommand command)
    {
        if (command.Spec.Name is not ("vhs" or "cvbs"))
        {
            return HSyncRefineOptions.Disabled;
        }

        if (command.Spec.Name == "vhs" && BoolValueOrDefault(command, "skip_hsync_refine"))
        {
            return HSyncRefineOptions.Disabled;
        }

        return command.Spec.Name == "cvbs"
            ? new HSyncRefineOptions(Enabled: true, UseRightHSync: BoolValueOrDefault(command, "rhs_hsync"))
            : new HSyncRefineOptions(Enabled: true, UseRightHSync: !BoolValueOrDefault(command, "disable_right_hsync"));
    }

    private static SyncDetectionOptions BuildSyncDetectionOptions(
        ParsedCommand command,
        string system,
        FormatParameterSet parameters,
        double sampleRateMHz)
    {
        if (command.Spec.Name == "cvbs")
        {
            return new SyncDetectionOptions(
                DetectLevels: false,
                LevelDetectDivisor: 1,
                CvbsAutoSync: !BoolValueOrDefault(command, "no_auto_sync"));
        }

        if (command.Spec.Name != "vhs")
        {
            return SyncDetectionOptions.Disabled;
        }

        int divisor = IntValueOrDefault(command, "level_detect_divisor", 1);
        if (divisor < 1 || divisor > 10)
        {
            divisor = 1;
        }
        else if (sampleRateMHz / divisor < 4.0)
        {
            divisor = Math.Max(1, (int)Math.Floor(sampleRateMHz / 4.0));
        }

        bool useImplicitFallbackVSync = parameters.TapeFormat is "TYPEC" or "EIAJ"
            || system is "405" or "819";
        return new SyncDetectionOptions(
            DetectLevels: true,
            LevelDetectDivisor: divisor,
            UseSavedLevels: BoolValueOrDefault(command, "saved_levels"),
            ClampDcOffset: BoolValueOrDefault(command, "enable_dc_offset"),
            UseFallbackVSync: BoolValueOrDefault(command, "fallback_vsync") || useImplicitFallbackVSync,
            RelaxedLine0: BoolValueOrDefault(command, "relaxed_line0"));
    }

    private static LaserDiscAnalogAudioOutputOptions? BuildLaserDiscAnalogAudioOutputOptions(
        ParsedCommand command,
        FormatParameterSet parameters,
        DecodeFilterSet filters,
        TbcFrameSpec tbcFrameSpec,
        LaserDiscAudioOptions? audioOptions)
    {
        if (command.Spec.Name != "ld"
            || audioOptions is null
            || !audioOptions.DecodeAnalogAudio
            || audioOptions.AnalogAudioFrequency == 0.0
            || filters.LdAnalogAudio is null)
        {
            return null;
        }

        return new LaserDiscAnalogAudioOutputOptions(
            JsonRequiredDouble(parameters.SysParams, "line_period"),
            tbcFrameSpec.OutputLineCount,
            audioOptions.AnalogAudioFrequency,
            JsonRequiredDouble(parameters.SysParams, "audio_lfreq"),
            JsonRequiredDouble(parameters.SysParams, "audio_rfreq"),
            JsonRequiredDouble(parameters.SysParams, "FPS"));
    }

    private static LaserDiscRfTbcOptions? BuildLaserDiscRfTbcOptions(
        ParsedCommand command,
        DecodeFilterSet filters,
        LaserDiscAudioOptions? audioOptions)
    {
        if (command.Spec.Name != "ld" || audioOptions is null || (!audioOptions.WriteRfTbc && !audioOptions.Ac3))
        {
            return null;
        }

        return new LaserDiscRfTbcOptions(
            WriteRfTbc: true,
            VideoWhiteOffsetSamples: filters.LdVideoWhiteOffset);
    }

    private static ChromaDecodeOptions? BuildChromaOptions(
        ParsedCommand command,
        string system,
        FormatParameterSet parameters,
        double sampleRateMHz)
    {
        if (command.Spec.Name != "vhs")
        {
            return null;
        }

        bool isColorUnder = FormatCatalog.IsColorUnder(parameters.TapeFormat);
        bool exportRawTbc = IsExportRawTbc(command);
        bool skipChroma = command.Get<bool>("skip_chroma");
        bool writeChroma = isColorUnder
            && !exportRawTbc
            && !skipChroma
            && !string.Equals(system, "405", StringComparison.OrdinalIgnoreCase);
        bool useChromaAfc = parameters.TapeFormat == "BETAMAX" && !string.Equals(system, "NTSC", StringComparison.OrdinalIgnoreCase)
            ? true
            : command.Get<bool>("cafc");
        bool disableComb = command.Get<bool>("disable_comb") || IsSecam(system);
        double chromaOffset = JsonNullableDouble(parameters.RfParams, "chroma_offset") ?? 5.0;
        double chromaAudioNotchFreq = JsonNullableDouble(parameters.RfParams, "chroma_audio_notch_freq") ?? 0.0;

        return new ChromaDecodeOptions(
            IsColorUnder: isColorUnder,
            WriteChroma: writeChroma,
            SkipChroma: skipChroma,
            UseChromaAfc: useChromaAfc,
            DisableComb: disableComb,
            ChromaDeemphasisFilter: parameters.TapeFormat is "VIDEO8" or "HI8",
            ChromaAudioNotch: chromaAudioNotchFreq > 0.0,
            ChromaOffsetSamples: (int)(chromaOffset * (sampleRateMHz / FrequencyParser.DddMHz)),
            DetectChromaTrackPhase: command.Get<bool>("detect_chroma_track_phase"),
            EnableColorKiller: command.Get<bool>("enable_color_killer"),
            DisableBurstHsync: command.Get<bool>("disable_burst_hsync"),
            DisablePhaseCorrection: command.Get<bool>("disable_phase_correction"),
            UseOldRawChromaOutput: command.Get<bool>("orc"));
    }

    private static DecodeExecutionOptions BuildExecutionOptions(ParsedCommand command)
    {
        int requestedThreads = command.Get<int>("threads");
        string? debugPlotPath = NullableString(command, "debug_plot");
        int workerThreads = command.Spec.Name == "vhs" && debugPlotPath is not null
            ? 0
            : requestedThreads;

        return new DecodeExecutionOptions(
            RequestedThreads: requestedThreads,
            WorkerThreads: workerThreads,
            SeekFrame: IntValueOrDefault(command, "seek", -1),
            WriteDebugData: BoolValueOrDefault(command, "write_db"),
            Debug: BoolValueOrDefault(command, "debug"),
            DebugPlotPath: debugPlotPath,
            IgnoreLeadOut: BoolValueOrDefault(command, "ignoreleadout"),
            VerboseVits: BoolValueOrDefault(command, "verboseVITS"),
            UseProfiler: BoolValueOrDefault(command, "use_profiler"),
            CxAdcCompatibilityMode: command.Spec.Name == "vhs" && command.Get<bool>("cxadc"));
    }

    private static LaserDiscAudioOptions? BuildLaserDiscAudioOptions(ParsedCommand command, string system)
    {
        if (command.Spec.Name != "ld")
        {
            return null;
        }

        double analogAudioFrequency = command.Get<int>("analog_audio_freq");
        bool useNtscAudioRate = command.Get<bool>("ntsc_audio_rate");
        if (useNtscAudioRate && string.Equals(system, "NTSC", StringComparison.OrdinalIgnoreCase))
        {
            analogAudioFrequency = -2.8;
        }

        bool decodeAnalogAudio = !command.Get<bool>("daa");
        double? parsedAudioFilterWidth = NullableDouble(command, "audio_filterwidth");
        double? audioFilterWidthHz = parsedAudioFilterWidth.HasValue && parsedAudioFilterWidth.Value > 0.0
            ? parsedAudioFilterWidth.Value
            : null;

        return new LaserDiscAudioOptions(
            DecodeDigitalAudio: !command.Get<bool>("noefm"),
            WritePreEfm: command.Get<bool>("prefm"),
            DecodeAnalogAudio: decodeAnalogAudio,
            AnalogAudioFrequency: decodeAnalogAudio ? analogAudioFrequency : 0.0,
            UseNtscAudioRate: useNtscAudioRate,
            Ac3: command.Get<bool>("AC3"),
            WriteRfTbc: command.Get<bool>("RF_TBC"),
            UseAgc: !command.Get<bool>("noAGC"),
            AudioFilterWidthHz: audioFilterWidthHz);
    }

    private static CvbsDecodeOptions? BuildCvbsDecodeOptions(ParsedCommand command, VideoOutputConverter videoOutput)
    {
        if (command.Spec.Name != "cvbs")
        {
            return null;
        }

        bool noAutoSync = command.Values.TryGetValue("no_auto_sync", out object? noAutoSyncValue)
            && noAutoSyncValue is bool disabled
            && disabled;
        return new CvbsDecodeOptions(!noAutoSync, videoOutput);
    }

    private static IRfInputProcessor? BuildRfInputProcessor(ParsedCommand command)
    {
        return command.Spec.Name == "vhs" && BoolValueOrDefault(command, "gnrc_afe")
            ? new GnuRadioRfAfeBridge()
            : null;
    }

    private static CvbsClampAgcOptions? BuildCvbsClampAgcOptions(ParsedCommand command)
    {
        if (command.Spec.Name != "cvbs"
            || !command.Values.TryGetValue("clamp_agc", out object? clampValue)
            || clampValue is not bool enabled
            || !enabled)
        {
            return null;
        }

        return new CvbsClampAgcOptions(
            NullableDouble(command, "agc_speed") ?? 0.1,
            NullableDouble(command, "agc_gain_factor") ?? 1.0,
            NullableDouble(command, "agc_set_gain") ?? 0.0);
    }

    private static IRfSampleLoader CreateLoader(string inputFile, bool needsResampling, double? inputFrequencyMHz = null)
    {
        if (needsResampling)
        {
            return RfLoaderFactory.CreateResampling(inputFile, inputFrequencyMHz ?? FrequencyParser.DddMHz);
        }

        return RfLoaderFactory.CreateNative(inputFile);
    }

    private static double SelectCommonSampleFrequencyMHz(ParsedCommand command)
    {
        if (command.Get<bool>("cxadc"))
        {
            return FrequencyParser.CxAdcMHz;
        }

        return NullableDouble(command, "inputfreq") ?? FrequencyParser.DddMHz;
    }

    private static double BuildYCombLimitHz(ParsedCommand command, FormatParameterSet parameters)
    {
        if (command.Spec.Name != "vhs")
        {
            return 0.0;
        }

        double levelIre = command.Get<double>("y_comb");
        if (levelIre == 0.0)
        {
            return 0.0;
        }

        return levelIre * JsonRequiredDouble(parameters.SysParams, "hz_ire");
    }

    private static Ire0AdjustOptions? BuildIre0AdjustOptions(ParsedCommand command, FormatParameterSet parameters)
    {
        if (command.Spec.Name != "vhs"
            || !command.Values.TryGetValue("ire0_adjust", out object? value)
            || value is not string text
            || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] modes = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool backPorch = modes.Contains("backporch", StringComparer.Ordinal);
        bool hsync = modes.Contains("hsync", StringComparer.Ordinal);
        if (!backPorch && !hsync)
        {
            return null;
        }

        (int start, int end) = FormatCatalog.ParentSystem(parameters.System) == "PAL"
            ? (96, 160)
            : (74, 124);
        return new Ire0AdjustOptions(backPorch, hsync, start, end);
    }

    private static TrackPhaseIre0OffsetOptions? BuildTrackPhaseIre0OffsetOptions(
        ParsedCommand command,
        string system,
        FormatParameterSet parameters)
    {
        if (command.Spec.Name != "vhs")
        {
            return null;
        }

        BigInteger? trackPhaseValue = NullableInteger(command, "track_phase");
        if (!trackPhaseValue.HasValue)
        {
            return null;
        }

        string normalizedSystem = FormatCatalog.NormalizeSystem(system);
        if (normalizedSystem is "SECAM" or "MESECAM")
        {
            return null;
        }

        if (trackPhaseValue.Value != BigInteger.Zero
            && trackPhaseValue.Value != BigInteger.One)
        {
            throw new ArgumentException("Track phase can only be 0, 1 or None");
        }

        int trackPhase = (int)trackPhaseValue.Value;

        double offset0 = 0.0;
        double offset1 = 0.0;
        if (parameters.SysParams.TryGetProperty("track_ire0_offset", out JsonElement offsets)
            && offsets.ValueKind == JsonValueKind.Array
            && offsets.GetArrayLength() >= 2)
        {
            offset0 = offsets[0].GetDouble();
            offset1 = offsets[1].GetDouble();
        }

        return new TrackPhaseIre0OffsetOptions(trackPhase, offset0, offset1);
    }

    private static TbcLineInterpolationMethod BuildTbcInterpolationMethod(ParsedCommand command)
    {
        string method = command.Values.TryGetValue("wow_interpolation_method", out object? value) && value is string text
            ? text
            : "linear";
        return method switch
        {
            "quadratic" => TbcLineInterpolationMethod.Quadratic,
            "cubic" => TbcLineInterpolationMethod.Cubic,
            _ => TbcLineInterpolationMethod.Linear
        };
    }

    private static double BuildWowLevelAdjustSmoothing(ParsedCommand command, FormatParameterSet parameters)
    {
        if (command.Values.TryGetValue("wow_level_adjust_smoothing", out object? value))
        {
            if (value is int intValue)
            {
                return Math.Max(0.0, intValue);
            }

            if (value is BigInteger integerValue)
            {
                return integerValue.Sign <= 0 ? 0.0 : (double)integerValue;
            }

            if (value is double doubleValue)
            {
                return Math.Max(0.0, doubleValue);
            }
        }

        return command.Spec.Name == "vhs"
            ? JsonRequiredDouble(parameters.SysParams, "frame_lines") / 2.0
            : 0.0;
    }

    private static TbcFieldOrderOptions BuildFieldOrderOptions(ParsedCommand command, FormatParameterSet parameters)
    {
        TbcFieldOrderAction action = TbcFieldOrderOptions.Default.Action;
        int confidence = TbcFieldOrderOptions.Default.Confidence;
        if (command.Values.TryGetValue("field_order_action", out object? actionValue) && actionValue is string actionText)
        {
            action = TbcFieldOrderPlanner.ParseAction(actionText);
        }

        BigInteger? parsedConfidence = NullableInteger(command, "field_order_confidence");
        if (parsedConfidence.HasValue)
        {
            confidence = parsedConfidence.Value <= BigInteger.Zero
                ? 0
                : parsedConfidence.Value >= new BigInteger(100)
                    ? 100
                    : (int)parsedConfidence.Value;
        }

        bool allowProgressiveFlip = parameters.TapeFormat != "TYPEC";
        if (!allowProgressiveFlip)
        {
            action = TbcFieldOrderAction.None;
        }

        return TbcFieldOrderOptions.Create(action, confidence, allowProgressiveFlip);
    }

    private static DecodeFilterOptions BuildFilterOptions(
        ParsedCommand command,
        string system,
        FormatParameterSet parameters,
        ChromaDecodeOptions? chromaOptions)
    {
        double? notchMHz = NullableDouble(command, "notch");
        bool isLaserDisc = command.Spec.Name == "ld";
        string parentSystem = FormatCatalog.ParentSystem(system);
        return new DecodeFilterOptions(
            VideoNotchHz: notchMHz.HasValue ? notchMHz.Value * 1_000_000.0 : null,
            VideoNotchQ: NullableDouble(command, "notch_q") ?? 10.0,
            LdNtscColorNotch: isLaserDisc
                && parentSystem == "NTSC"
                && command.Values.TryGetValue("NTSC_color_notch_filter", out object? ntscNotch)
                && ntscNotch is bool ntscNotchEnabled
                && ntscNotchEnabled,
            LdPalV4300DNotch: isLaserDisc
                && parentSystem == "PAL"
                && command.Values.TryGetValue("V4300D_notch_filter", out object? v4300dNotch)
                && v4300dNotch is bool v4300dNotchEnabled
                && v4300dNotchEnabled,
            LdNtscAnalogAudioNotch: isLaserDisc && parentSystem == "NTSC",
            LdDecodeDigitalAudio: !isLaserDisc || !command.Get<bool>("noefm"),
            LdDecodeAnalogAudio: isLaserDisc && !command.Get<bool>("daa"),
            LdAudioFilterWidthHz: isLaserDisc ? JsonNullableDouble(parameters.RfParams, "audio_filterwidth") : null,
            LdMtfLevel: isLaserDisc ? command.Get<double>("MTF") : null,
            LdMtfOffset: isLaserDisc ? command.Get<double>("MTF_offset") : 0.0,
            LdClipDemodForVideo: isLaserDisc,
            FmAudioNotchQ: BuildFmAudioNotchQ(command, parameters),
            RfHighBoost: BuildRfHighBoostOptions(command, parameters),
            DiffDemodRepair: BuildDiffDemodRepairOptions(command, parameters),
            BetamaxFscNotchHz: BuildBetamaxFscNotchHz(command, parameters),
            ChromaTrap: BuildChromaTrapOptions(command, parameters),
            SharpnessEq: BuildSharpnessEqOptions(command, parameters),
            NonlinearDeemphasis: BuildNonlinearDeemphasisOptions(command, parameters),
            SubDeemphasis: BuildSubDeemphasisOptions(command, parameters),
            ExportRawTbc: IsExportRawTbc(command),
            UseChromaAfc: chromaOptions?.UseChromaAfc == true,
            FmDemodulatorMode: command.Spec.Name == "vhs"
                ? RfFmDemodulatorMode.VhsRustApproximation
                : RfFmDemodulatorMode.ConjugateProduct);
    }

    private static bool IsExportRawTbc(ParsedCommand command)
    {
        return command.Spec.Name == "vhs"
            && command.Values.TryGetValue("export_raw_tbc", out object? value)
            && value is bool enabled
            && enabled;
    }

    private static double BuildFmAudioNotchQ(ParsedCommand command, FormatParameterSet parameters)
    {
        if (command.Spec.Name != "vhs")
        {
            return 0.0;
        }

        double configured = command.Get<double>("fm_audio_notch");
        if (configured != 0.0)
        {
            return configured;
        }

        return parameters.TapeFormat == "HI8" ? 1.0 : 0.0;
    }

    private static RfHighBoostOptions? BuildRfHighBoostOptions(ParsedCommand command, FormatParameterSet parameters)
    {
        if (command.Spec.Name != "vhs")
        {
            return null;
        }

        double? multiplier = NullableDouble(command, "high_boost")
            ?? JsonNullableDouble(parameters.RfParams, "boost_bpf_mult");
        if (!multiplier.HasValue)
        {
            return null;
        }

        double low = JsonNullableDouble(parameters.RfParams, "boost_bpf_low")
            ?? throw new ArgumentException("RF high boost requires boost_bpf_low in format parameters.");
        double high = JsonNullableDouble(parameters.RfParams, "boost_bpf_high")
            ?? throw new ArgumentException("RF high boost requires boost_bpf_high in format parameters.");
        return new RfHighBoostOptions(multiplier.Value, low, high);
    }

    private static DiffDemodRepairOptions? BuildDiffDemodRepairOptions(ParsedCommand command, FormatParameterSet parameters)
    {
        if (command.Spec.Name != "vhs" || command.Get<bool>("disable_diff_demod"))
        {
            return null;
        }

        double ire0 = JsonNullableDouble(parameters.SysParams, "ire0")
            ?? throw new ArgumentException("Diff demod repair requires ire0 in system parameters.");
        double hzIre = JsonNullableDouble(parameters.SysParams, "hz_ire")
            ?? throw new ArgumentException("Diff demod repair requires hz_ire in system parameters.");
        return new DiffDemodRepairOptions((ire0 + (hzIre * 100.0)) * 2.0);
    }

    private static double? BuildBetamaxFscNotchHz(ParsedCommand command, FormatParameterSet parameters)
    {
        if (command.Spec.Name != "vhs"
            || (parameters.TapeFormat != "BETAMAX" && parameters.TapeFormat != "BETAMAX_HIFI"))
        {
            return null;
        }

        double fscMHz = JsonNullableDouble(parameters.SysParams, "fsc_mhz")
            ?? throw new ArgumentException("Betamax fsc notch requires fsc_mhz in system parameters.");
        return fscMHz * 1_000_000.0;
    }

    private static ChromaTrapOptions? BuildChromaTrapOptions(ParsedCommand command, FormatParameterSet parameters)
    {
        if (!command.Values.TryGetValue("chroma_trap", out object? chromaTrap)
            || chromaTrap is not bool chromaTrapEnabled
            || !chromaTrapEnabled)
        {
            return null;
        }

        double fscMHz = JsonNullableDouble(parameters.SysParams, "fsc_mhz")
            ?? throw new ArgumentException("Chroma trap requires fsc_mhz in system parameters.");
        return new ChromaTrapOptions(fscMHz * 1_000_000.0);
    }

    private static SharpnessEqOptions? BuildSharpnessEqOptions(ParsedCommand command, FormatParameterSet parameters)
    {
        BigInteger? sharpness = NullableInteger(command, "sharpness");
        if (!sharpness.HasValue || sharpness.Value.IsZero)
        {
            return null;
        }

        if (!parameters.RfParams.TryGetProperty("video_eq", out JsonElement videoEq)
            || !videoEq.TryGetProperty("loband", out JsonElement lowBand))
        {
            return null;
        }

        return new SharpnessEqOptions(
            PythonNumericParser.DivideIntegerByPowerOfTen(sharpness.Value, 2),
            JsonRequiredDouble(lowBand, "corner"),
            JsonRequiredDouble(lowBand, "transition"),
            JsonRequiredInt(lowBand, "order_limit"));
    }

    private static NonlinearDeemphasisOptions? BuildNonlinearDeemphasisOptions(ParsedCommand command, FormatParameterSet parameters)
    {
        if (!command.Values.TryGetValue("nldeemp", out object? nldeemp)
            || nldeemp is not bool enabled
            || !enabled)
        {
            return null;
        }

        return new NonlinearDeemphasisOptions(
            JsonRequiredDouble(parameters.RfParams, "nonlinear_highpass_freq"),
            JsonNullableDouble(parameters.RfParams, "nonlinear_bandpass_upper"),
            JsonNullableInt(parameters.RfParams, "nonlinear_bandpass_order") ?? 1,
            JsonRequiredDouble(parameters.RfParams, "nonlinear_highpass_limit_l"),
            JsonRequiredDouble(parameters.RfParams, "nonlinear_highpass_limit_h"));
    }

    private static SubDeemphasisOptions? BuildSubDeemphasisOptions(ParsedCommand command, FormatParameterSet parameters)
    {
        bool enabledByCommand = command.Values.TryGetValue("subdeemp", out object? subdeemp)
            && subdeemp is bool commandEnabled
            && commandEnabled;
        bool enabledByFormat = JsonBool(parameters.RfParams, "use_sub_deemphasis", defaultValue: false);
        if (!enabledByCommand && !enabledByFormat)
        {
            return null;
        }

        double hzIre = JsonRequiredDouble(parameters.SysParams, "hz_ire");
        double vsyncIre = JsonRequiredDouble(parameters.SysParams, "vsync_ire");
        double deviation = JsonNullableDouble(parameters.SysParams, "nonlinear_deviation")
            ?? hzIre * (100.0 + -vsyncIre);

        return new SubDeemphasisOptions(
            JsonRequiredDouble(parameters.RfParams, "nonlinear_highpass_freq"),
            JsonNullableDouble(parameters.RfParams, "nonlinear_bandpass_upper"),
            JsonNullableInt(parameters.RfParams, "nonlinear_bandpass_order") ?? 1,
            JsonNullableDouble(parameters.RfParams, "nonlinear_amp_lpf_freq") ?? 700_000.0,
            deviation,
            JsonNullableDouble(parameters.RfParams, "nonlinear_exp_scaling") ?? 0.25,
            JsonNullableDouble(parameters.RfParams, "nonlinear_scaling_1"),
            JsonNullableDouble(parameters.RfParams, "nonlinear_scaling_2"),
            JsonNullableDouble(parameters.RfParams, "nonlinear_logistic_mid"),
            JsonNullableDouble(parameters.RfParams, "nonlinear_logistic_rate"),
            JsonNullableDouble(parameters.RfParams, "nonlinear_static_factor"));
    }

    private static double ComputeBlackIre(ParsedCommand command, string system)
    {
        bool ntscJapan = command.Values.TryGetValue("ntscj", out object? value) && value is bool boolValue && boolValue;
        return string.Equals(system, "NTSC", StringComparison.Ordinal) && !ntscJapan ? 7.5 : 0.0;
    }

    private static TbcDropoutDetectionOptions BuildDropoutOptions(ParsedCommand command, double sampleRateMHz)
    {
        if (command.Spec.Name == "cvbs")
        {
            return TbcDropoutDetectionOptions.Disabled;
        }

        if (command.Values.TryGetValue("nodod", out object? noDodValue) && noDodValue is bool noDod && noDod)
        {
            return TbcDropoutDetectionOptions.Disabled;
        }

        if (command.Spec.Name == "ld")
        {
            return TbcDropoutDetectionOptions.LaserDisc;
        }

        double defaultFraction = Math.Floor(sampleRateMHz) == 28.0
            ? TbcDropoutDetectionOptions.DefaultCxAdc.ThresholdFraction
            : TbcDropoutDetectionOptions.DefaultDdd.ThresholdFraction;
        double thresholdFraction = NullableDouble(command, "dod_threshold_p") ?? defaultFraction;
        double? absoluteThreshold = NullableDouble(command, "dod_threshold_a");
        double hysteresis = NullableDouble(command, "dod_hysteresis") ?? TbcDropoutDetectionOptions.DefaultDdd.Hysteresis;
        return new TbcDropoutDetectionOptions(
            true,
            thresholdFraction,
            absoluteThreshold,
            hysteresis,
            TbcDropoutDetectionMode.TapeEnvelope);
    }

    private static FormatParameterSet ApplyLaserDiscOverrides(FormatParameterSet parameters, ParsedCommand command)
    {
        JsonObject sysParams = JsonNode.Parse(parameters.SysParams.GetRawText())!.AsObject();
        JsonObject rfParams = JsonNode.Parse(parameters.RfParams.GetRawText())!.AsObject();
        SetFrequencyOverride(rfParams, command, "vbpf_low", "video_bpf_low");
        SetFrequencyOverride(rfParams, command, "vbpf_high", "video_bpf_high");
        SetFrequencyOverride(rfParams, command, "vlpf", "video_lpf_freq");

        bool ac3 = command.Get<bool>("AC3");
        bool decodeAnalogAudio = !command.Get<bool>("daa");
        sysParams["analog_audio"] = parameters.System == "PAL" ? decodeAnalogAudio : true;
        sysParams["AC3"] = ac3;

        if (ac3)
        {
            double ac3RightCarrier = sysParams["audio_rfreq_AC3"]?.GetValue<double>()
                ?? throw new FormatParameterException("LaserDisc parameters did not contain audio_rfreq_AC3.");
            sysParams["audio_rfreq"] = ac3RightCarrier;
            sysParams["AC3"] = true;
        }

        double? parsedAudioFilterWidth = NullableDouble(command, "audio_filterwidth");
        if (parsedAudioFilterWidth.HasValue && parsedAudioFilterWidth.Value > 0.0)
        {
            rfParams["audio_filterwidth"] = parsedAudioFilterWidth.Value;
        }

        int vlpfOrder = command.Get<int>("vlpf_order");
        if (vlpfOrder >= 1)
        {
            rfParams["video_lpf_order"] = vlpfOrder;
        }

        double deempLow = command.Get<double>("deemp_low");
        double deempHigh = command.Get<double>("deemp_high");
        if (deempLow > 0 || deempHigh > 0)
        {
            JsonArray deemp = rfParams["video_deemp"]?.AsArray()
                ?? throw new FormatParameterException("LaserDisc parameters did not contain video_deemp.");
            double highTimeConstant = deemp[0]!.GetValue<double>();
            double lowTimeConstant = deemp[1]!.GetValue<double>();
            if (deempLow > 0)
            {
                lowTimeConstant = 1.0 / (deempLow * 1_000_000.0);
            }

            if (deempHigh > 0)
            {
                highTimeConstant = 1.0 / (deempHigh * 1_000_000.0);
            }

            rfParams["video_deemp"] = new JsonArray(highTimeConstant, lowTimeConstant);
        }

        double strength = command.Get<double>("deemp_strength");
        rfParams["video_deemp_strength"] = strength;

        return new FormatParameterSet(
            parameters.System,
            parameters.TapeFormat,
            parameters.TapeSpeed,
            ToJsonElement(sysParams),
            ToJsonElement(rfParams),
            parameters.Warnings);
    }

    private static JsonElement ToJsonElement(JsonObject value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void SetFrequencyOverride(JsonObject rfParams, ParsedCommand command, string cliName, string parameterName)
    {
        double? valueMHz = NullableDouble(command, cliName);
        if (valueMHz.HasValue)
        {
            rfParams[parameterName] = valueMHz.Value * 1_000_000.0;
        }
    }

    private static double? NullableDouble(ParsedCommand command, string name)
    {
        return command.Values.TryGetValue(name, out object? value) && value is double doubleValue
            ? doubleValue
            : null;
    }

    private static BigInteger? NullableInteger(ParsedCommand command, string name)
    {
        if (!command.Values.TryGetValue(name, out object? value))
        {
            return null;
        }

        return value switch
        {
            int intValue => new BigInteger(intValue),
            BigInteger integerValue => integerValue,
            _ => null
        };
    }

    private static int IntValueOrDefault(ParsedCommand command, string name, int defaultValue)
    {
        return command.Values.TryGetValue(name, out object? value) && value is int intValue
            ? intValue
            : defaultValue;
    }

    private static bool BoolValueOrDefault(ParsedCommand command, string name, bool defaultValue = false)
    {
        return command.Values.TryGetValue(name, out object? value) && value is bool boolValue
            ? boolValue
            : defaultValue;
    }

    private static bool IsSecam(string system)
    {
        return string.Equals(system, "SECAM", StringComparison.OrdinalIgnoreCase)
            || string.Equals(system, "MESECAM", StringComparison.OrdinalIgnoreCase);
    }

    private static double? JsonNullableDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind != JsonValueKind.Null
            ? property.GetDouble()
            : null;
    }

    private static int? JsonNullableInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind != JsonValueKind.Null
            ? property.GetInt32()
            : null;
    }

    private static bool JsonBool(JsonElement element, string propertyName, bool defaultValue)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : defaultValue;
    }

    private static double JsonRequiredDouble(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetDouble();
    }

    private static int JsonRequiredInt(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetInt32();
    }

    private static string? NullableString(ParsedCommand command, string name)
    {
        return command.Values.TryGetValue(name, out object? value) && value is string text && text.Length > 0
            ? text
            : null;
    }
}
