namespace VHSDecode.Core.CommandLine;

public static class CliSpecs
{
    public static readonly string[] SupportedTapeFormats =
    [
        "TYPEC",
        "UMATIC_SP",
        "HI8",
        "TYPEB",
        "UMATIC",
        "BETAMAX",
        "VCR_LP",
        "VHD",
        "VHS",
        "VIDEO2000",
        "UMATIC_HI",
        "SVHS",
        "EIAJ",
        "VCR",
        "QUADRUPLEX",
        "VHSHQ",
        "BETAMAX_HIFI",
        "VIDEO8",
        "SUPERBETA",
        "SVHS_ET"
    ];

    public static readonly string[] TapeSpeeds = ["sp", "lp", "ep", "slp", "vp"];

    public static readonly string[] VideoSystems = ["PAL", "PAL_M", "PALM", "NTSC", "MESECAM", "405", "819", "NLINHA"];

    public static DecodeCommandSpec Vhs { get; } = new(
        "vhs",
        "Extracts video from RAW RF captures of colour-under & composite modulated tapes",
        ["vhs-decode"],
        CommonOptions(defaultThreads: 5).Concat(VhsOptions()),
        minimumPositionals: 0,
        maximumPositionals: 2);

    public static DecodeCommandSpec Cvbs { get; } = new(
        "cvbs",
        "Extracts video from raw cvbs captures",
        ["cvbs-decode"],
        CommonOptions(defaultThreads: 5).Concat(CvbsOptions()),
        minimumPositionals: 0,
        maximumPositionals: 2);

    public static DecodeCommandSpec LaserDisc { get; } = new(
        "ld",
        "Extracts audio and video from raw RF laserdisc captures",
        ["ld-decode"],
        LaserDiscOptions(),
        minimumPositionals: 2,
        maximumPositionals: 2);

    public static DecodeCommandSpec[] AllCommands { get; } = [Vhs, Cvbs, LaserDisc];

    private static IEnumerable<OptionSpec> CommonOptions(int defaultThreads)
    {
        yield return Flag("help", ["-h", "--help"]);
        yield return Flag("noAGC", ["--noAGC"], hidden: true);
        yield return Flag("AGC", ["--AGC"], hidden: true);
        yield return Str("system", ["--system"], "NTSC", choices: VideoSystems, normalize: Upper);
        yield return Int("start", ["-s", "--start"], 0);
        yield return Dbl("start_fileloc", ["--start_fileloc"], -1.0, pythonDefaultValue: -1);
        yield return Int("length", ["-l", "--length"], 99999999);
        yield return Flag("overwrite", ["--overwrite"]);
        yield return Flag("write_db", ["--write_db"]);
        yield return Freq("inputfreq", ["-f", "--frequency"], null, ParseCommonInputFrequency, "_parse_frequency");
        yield return Flag("cxadc", ["--cxadc"], hidden: true);
        yield return Int("threads", ["-t", "--threads"], defaultThreads);
        yield return Flag("chroma_trap", ["--ct", "--chroma_trap"]);
        yield return Int("sharpness", ["--sl", "--sharpness"], 0);
        yield return Freq("notch", ["--notch"], null);
        yield return Dbl("notch_q", ["--notch_q"], 10.0);
        yield return Flag("pal", ["-p", "--pal"]);
        yield return Flag("ntsc", ["-n", "--ntsc"]);
        yield return Flag("palm", ["--pm", "--palm"]);
        yield return Flag("ntscj", ["--NTSCJ"]);
        yield return Flag("debug", ["--debug"]);
        yield return Flag("skip_hsync_refine", ["--skip_hsync_refine"]);
    }

    private static IEnumerable<OptionSpec> VhsOptions()
    {
        yield return Str("tape_format", ["--tf", "--tape_format"], "VHS", SupportedTapeFormats, Upper);
        yield return Str("tape_speed", ["--ts", "--tape_speed"], "sp", TapeSpeeds, Lower);
        yield return Str("params_file", ["--params_file"], null, validationError: ValidateParamsFile);
        yield return Flag("orc", ["--orc"]);
        yield return Dbl("level_adjust", ["-L", "--level_adjust"], 0.1);
        yield return OptionalStr("ire0_adjust", ["--ire0_adjust"], false, "backporch", Ire0AdjustParser.IsValidRaw, Ire0AdjustParser.Normalize);
        yield return Dbl("high_boost", ["--high_boost"], null);
        yield return Int("wow_level_adjust_smoothing", ["--wow_level_adjust_smoothing"], null);
        yield return Str("wow_interpolation_method", ["--wow_interpolation_method"], "linear", ["linear", "quadratic", "cubic"]);
        yield return Flag("disable_diff_demod", ["--nodd", "--no_diff_demod"]);
        yield return OptionalDbl(
            "fm_audio_notch",
            ["--fm_audio_notch"],
            0.0,
            10.0,
            pythonDefaultValue: 0,
            pythonConstValue: 10);
        yield return Flag("disable_dc_offset", ["--noclamp", "--no_clamping"], defaultValue: true, hidden: true);
        yield return Flag("enable_dc_offset", ["--clamp"]);
        yield return Flag("nldeemp", ["--nld", "--non_linear_deemphasis"]);
        yield return Flag("subdeemp", ["--sd", "--sub_deemphasis"]);
        yield return OptionalDbl("y_comb", ["--y_comb"], 0.0, 1.5, pythonDefaultValue: 0);
        yield return Flag("cafc", ["--cafc", "--chroma_AFC"]);
        yield return Int("track_phase", ["-T", "--track_phase"], null);
        yield return Flag("detect_chroma_track_phase", ["--dctp", "--detect_chroma_track_phase"]);
        yield return Flag("disable_phase_correction", ["--dpc", "--disable_phase_correction"]);
        yield return Flag("disable_burst_hsync", ["--dbh", "--disable_burst_hsync"]);
        yield return Flag("enable_color_killer", ["--ck", "--enable_color_killer"]);
        yield return Flag("disable_comb", ["--no_comb"]);
        yield return Flag("skip_chroma", ["--skip_chroma"]);
        yield return Str("debug_plot", ["--dp", "--debug_plot"], null);
        yield return Flag("disable_right_hsync", ["--drh", "--disable_right_hsync"]);
        yield return Int("level_detect_divisor", ["--level_detect_divisor"], 3);
        yield return Flag("no_resample", ["--no_resample"]);
        yield return Flag("fallback_vsync", ["--fallback_vsync"]);
        yield return Flag("relaxed_line0", ["--relaxed_line0"]);
        yield return Int("field_order_confidence", ["--field_order_confidence"], 100);
        yield return Str(
            "field_order_action",
            ["--field_order_action"],
            "detect",
            validationError: ValidateFieldOrderAction);
        yield return Flag("saved_levels", ["--use_saved_levels"]);
        yield return Flag("export_raw_tbc", ["--export_raw_tbc"]);
        yield return Flag("nodod", ["--noDOD"]);
        yield return Dbl("dod_threshold_p", ["-D", "--dod_t", "--dod_threshold_p"], null);
        yield return Dbl("dod_threshold_a", ["--dod_t_abs", "--dod_threshold_abs"], null);
        yield return Dbl("dod_hysteresis", ["--dod_h", "--dod_hysteresis"], 1.25);
        yield return Flag("gnrc_afe", ["--gnrc", "--gnuradio_rf_afe"]);
    }

    private static IEnumerable<OptionSpec> CvbsOptions()
    {
        yield return Int("seek", ["-S", "--seek"], -1);
        yield return Flag("auto_sync", ["-A", "--auto_sync"], hidden: true);
        yield return Flag("no_auto_sync", ["--no_auto_sync"]);
        yield return Flag("clamp_agc", ["-C", "--clamp_agc"]);
        yield return Dbl("agc_speed", ["--agc_speed"], 0.1);
        yield return Dbl("agc_gain_factor", ["--agc_gain_factor"], 1.0);
        yield return Dbl("agc_set_gain", ["--agc_set_gain"], 0.0);
        yield return Flag("rhs_hsync", ["--right_hand_hsync"]);
        yield return Dbl(
            "wow_level_adjust_smoothing",
            ["--wow_level_adjust_smoothing"],
            0.0,
            pythonDefaultValue: 0);
        yield return Str("wow_interpolation_method", ["--wow_interpolation_method"], "linear", ["linear", "quadratic", "cubic"]);
    }

    private static IEnumerable<OptionSpec> LaserDiscOptions()
    {
        yield return Flag("help", ["-h", "--help"]);
        yield return Flag("version", ["--version", "-v"], hidden: true);
        yield return Dbl("start", ["--start", "-s"], 0.0, pythonDefaultValue: 0);
        yield return Int("length", ["--length", "-l"], 110000);
        yield return Int("seek", ["--seek", "-S"], -1);
        yield return Flag("pal", ["--PAL", "-p", "--pal"]);
        yield return Flag("ntsc", ["--NTSC", "-n", "--ntsc"]);
        yield return Flag("ntscj", ["--NTSCJ", "-j"]);
        yield return Dbl("MTF", ["-m", "--MTF"], 1.0);
        yield return Dbl("MTF_offset", ["--MTF_offset"], 0.0, pythonDefaultValue: 0);
        yield return Flag("noAGC", ["--noAGC"]);
        yield return Flag("nodod", ["--noDOD"]);
        yield return Flag("noefm", ["--noEFM"]);
        yield return Flag("prefm", ["--preEFM"]);
        yield return Flag("daa", ["--disable_analog_audio", "--disable_analogue_audio", "--daa"]);
        yield return Flag("AC3", ["--AC3"]);
        yield return Dbl("start_fileloc", ["--start_fileloc"], -1.0, pythonDefaultValue: -1);
        yield return Flag("ignoreleadout", ["--ignoreleadout"]);
        yield return Flag("verboseVITS", ["--verboseVITS"]);
        yield return Flag("RF_TBC", ["--RF_TBC"]);
        yield return Flag("lowband", ["--lowband"]);
        yield return Flag("NTSC_color_notch_filter", ["--NTSC_color_notch_filter", "-N"]);
        yield return Flag("V4300D_notch_filter", ["--V4300D_notch_filter", "-V"]);
        yield return Dbl("deemp_low", ["--deemp_low"], 0.0, pythonDefaultValue: 0);
        yield return Dbl("deemp_high", ["--deemp_high"], 0.0, pythonDefaultValue: 0);
        yield return Dbl("deemp_strength", ["--deemp_strength"], 1.0, pythonDefaultValue: 1);
        yield return Dbl(
            "wow_level_adjust_smoothing",
            ["--wow_level_adjust_smoothing"],
            0.0,
            pythonDefaultValue: 0);
        yield return Str("wow_interpolation_method", ["--wow_interpolation_method"], "linear", ["linear", "quadratic", "cubic"]);
        yield return Int("threads", ["-t", "--threads"], 4);
        yield return Freq("inputfreq", ["-f", "--frequency"], null);
        yield return Int("analog_audio_freq", ["--analog_audio_frequency"], 44100);
        yield return Flag("ntsc_audio_rate", ["--ntsc_audio_rate"]);
        yield return Freq("vbpf_low", ["--video_bpf_low"], null);
        yield return Freq("vbpf_high", ["--video_bpf_high"], null);
        yield return Freq("vlpf", ["--video_lpf"], null);
        yield return Int("vlpf_order", ["--video_lpf_order"], -1);
        yield return Freq("audio_filterwidth", ["--audio_filterwidth"], null);
        yield return Flag("use_profiler", ["--use_profiler"]);
        yield return Str("write_test_ldf", ["--write-test-ldf"], null);
    }

    private static OptionSpec Flag(string dest, string[] names, bool defaultValue = false, bool hidden = false)
        => new()
        {
            Destination = dest,
            Names = names,
            Arity = OptionArity.Flag,
            ValueKind = OptionValueKind.Boolean,
            DefaultValue = defaultValue,
            PythonDefaultValue = defaultValue,
            Hidden = hidden
        };

    private static OptionSpec Str(
        string dest,
        string[] names,
        string? defaultValue,
        string[]? choices = null,
        Func<string, string>? normalize = null,
        Func<string, string?>? validationError = null)
        => new()
        {
            Destination = dest,
            Names = names,
            Arity = OptionArity.Value,
            ValueKind = OptionValueKind.String,
            DefaultValue = defaultValue,
            PythonDefaultValue = defaultValue,
            Choices = choices,
            NormalizeString = normalize,
            ParseErrorTypeName = "str",
            ValidationError = validationError
        };

    private static OptionSpec OptionalStr(
        string dest,
        string[] names,
        object? defaultValue,
        string constValue,
        Func<string, bool> isValidOptionalValue,
        Func<string, string> normalize)
        => new()
        {
            Destination = dest,
            Names = names,
            Arity = OptionArity.OptionalValue,
            ValueKind = OptionValueKind.String,
            DefaultValue = defaultValue,
            ConstValue = constValue,
            PythonDefaultValue = defaultValue,
            PythonConstValue = constValue,
            IsValidOptionalValue = isValidOptionalValue,
            NormalizeString = normalize,
            ParseErrorTypeName = "_parse_ire0_adjust"
        };

    private static OptionSpec Int(string dest, string[] names, int? defaultValue)
        => new()
        {
            Destination = dest,
            Names = names,
            Arity = OptionArity.Value,
            ValueKind = OptionValueKind.Integer,
            ParseErrorTypeName = "int",
            DefaultValue = defaultValue,
            PythonDefaultValue = defaultValue
        };

    private static OptionSpec Dbl(
        string dest,
        string[] names,
        double? defaultValue,
        object? pythonDefaultValue = null)
        => new()
        {
            Destination = dest,
            Names = names,
            Arity = OptionArity.Value,
            ValueKind = OptionValueKind.Double,
            ParseErrorTypeName = "float",
            DefaultValue = defaultValue,
            PythonDefaultValue = pythonDefaultValue ?? defaultValue
        };

    private static OptionSpec OptionalDbl(
        string dest,
        string[] names,
        double defaultValue,
        double constValue,
        object? pythonDefaultValue = null,
        object? pythonConstValue = null)
        => new()
        {
            Destination = dest,
            Names = names,
            Arity = OptionArity.OptionalValue,
            ValueKind = OptionValueKind.Double,
            ParseErrorTypeName = "float",
            DefaultValue = defaultValue,
            ConstValue = constValue,
            PythonDefaultValue = pythonDefaultValue ?? defaultValue,
            PythonConstValue = pythonConstValue ?? constValue
        };

    private static OptionSpec Freq(
        string dest,
        string[] names,
        double? defaultValue,
        Func<string, double>? parseFrequencyMHz = null,
        string parseErrorTypeName = "parse_frequency")
        => new()
        {
            Destination = dest,
            Names = names,
            Arity = OptionArity.Value,
            ValueKind = OptionValueKind.FrequencyMHz,
            ParseErrorTypeName = parseErrorTypeName,
            DefaultValue = defaultValue,
            PythonDefaultValue = defaultValue,
            ParseFrequencyMHz = parseFrequencyMHz
        };

    private static double ParseCommonInputFrequency(string value)
        => value == "cxadc"
            ? (8.0 * 315.0) / 88.0
            : FrequencyParser.ParseMHz(value);

    private static string? ValidateParamsFile(string value)
        => value == "-" || File.Exists(value)
            ? null
            : $"argument --params_file: can't open '{value}': [Errno 2] No such file or directory: '{value}'";

    private static string? ValidateFieldOrderAction(string value)
        => value is "detect" or "duplicate" or "drop" or "none"
            ? null
            : "--field_order_action must be one of [\"detect\", \"duplicate\", \"drop\", \"none\"]";

    private static string Upper(string value) => value.ToUpperInvariant();

    private static string Lower(string value) => value.ToLowerInvariant();

}
