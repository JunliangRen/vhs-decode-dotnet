using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NetMQ;
using NetMQ.Sockets;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Rf;
using VHSDecode.Core.Tbc;

using Xunit;

namespace VHSDecode.Tests;

public sealed class CompatibilityTests
{
[Fact(DisplayName = "frequency parser handles standard suffixes")]
public void FrequencyParserHandlesStandardSuffixes()
{
    AssertClose(40.0, FrequencyParser.ParseMHz("40"), 1e-12);
    AssertClose(40.0, FrequencyParser.ParseMHz("40MHz"), 1e-12);
    AssertClose(0.0441, FrequencyParser.ParseMHz("44.1kHz"), 1e-12);
    AssertClose(1000.0, FrequencyParser.ParseMHz("1GHz"), 1e-12);
    AssertThrows<FormatException>(() => FrequencyParser.ParseMHz("cxadc"));
}

[Fact(DisplayName = "frequency parser handles fsc suffixes")]
public void FrequencyParserHandlesFscSuffixes()
{
    AssertClose((315.0e6 / 88.0) / 1.0e6, FrequencyParser.ParseMHz("1fsc"), 1e-12);
    AssertClose(8.0 * (315.0e6 / 88.0) / 1.0e6, FrequencyParser.ParseMHz("8fsc"), 1e-12);
    AssertClose(((283.75 * 15625) + 25) / 1.0e6, FrequencyParser.ParseMHz("1fSCPAL"), 1e-12);
}

[Fact(DisplayName = "common input frequency accepts the cxadc alias")]
public void CommonInputFrequencyAcceptsCxadcAlias()
{
    AssertClose((8.0 * 315.0) / 88.0, Parse(CliSpecs.Vhs, ["-f", "cxadc"]).Get<double>("inputfreq"), 1e-12);
    AssertClose((8.0 * 315.0) / 88.0, Parse(CliSpecs.Cvbs, ["--frequency", "cxadc"]).Get<double>("inputfreq"), 1e-12);
    AssertThrows<CommandLineParseException>(() => Parse(CliSpecs.Vhs, ["-f", "CXADC"]));
    AssertThrows<CommandLineParseException>(() => Parse(CliSpecs.LaserDisc, ["-f", "cxadc", "in", "out"]));
}

[Fact(DisplayName = "CLI string choices preserve upstream case sensitivity")]
public void CliStringChoicesPreserveUpstreamCaseSensitivity()
{
    AssertThrows<ArgumentException>(() => Parse(CliSpecs.Vhs, ["--wow_interpolation_method", "Linear"]));
    AssertThrows<ArgumentException>(() => Parse(CliSpecs.Vhs, ["--field_order_action", "Detect"]));
    AssertThrows<ArgumentException>(() => Parse(CliSpecs.Cvbs, ["--wow_interpolation_method", "Cubic"]));
    AssertThrows<ArgumentException>(() => Parse(CliSpecs.LaserDisc, ["--wow_interpolation_method", "Quadratic", "in", "out"]));
}

[Fact(DisplayName = "CLI parser matches argparse errors and abbreviations")]
public void CliParserMatchesArgparseErrorsAndAbbreviations()
{
    AssertParseError(CliSpecs.Vhs, ["--tf"], "argument --tf/--tape_format: expected one argument");
    AssertParseError(
        CliSpecs.Vhs,
        ["--tf", "NOPE"],
        "argument --tf/--tape_format: invalid choice: 'NOPE' (choose from TYPEC, UMATIC_SP, HI8, TYPEB, UMATIC, BETAMAX, VCR_LP, VHD, VHS, VIDEO2000, UMATIC_HI, SVHS, EIAJ, VCR, QUADRUPLEX, VHSHQ, BETAMAX_HIFI, VIDEO8, SUPERBETA, SVHS_ET)");
    AssertParseError(CliSpecs.Vhs, ["-s", "abc"], "argument -s/--start: invalid int value: 'abc'");
    AssertParseError(CliSpecs.Vhs, ["--notch", "abc"], "argument --notch: invalid parse_frequency value: 'abc'");
    AssertParseError(CliSpecs.Vhs, ["--fm_audio_notch", "abc"], "argument --fm_audio_notch: invalid float value: 'abc'");
    AssertParseError(CliSpecs.Vhs, ["--ire0_adjust=abc"], "argument --ire0_adjust: Allowed values: hsync, backporch");
    AssertParseError(CliSpecs.Vhs, ["--pal=yes"], "argument -p/--pal: ignored explicit argument 'yes'");
    AssertParseError(CliSpecs.Vhs, ["--not"], "ambiguous option: --not could match --notch, --notch_q");
    AssertParseError(CliSpecs.LaserDisc, ["--wat"], "the following arguments are required: infile, outfile");
    AssertParseError(CliSpecs.LaserDisc, ["--wat", "in", "out"], "unrecognized arguments: --wat");

    ParsedCommand abbreviated = Parse(CliSpecs.Vhs, ["--overw"]);
    AssertTrue(abbreviated.Get<bool>("overwrite"));

    ParsedCommand attached = Parse(CliSpecs.Vhs, ["-s3", "-pn"]);
    AssertEqual(3, attached.Get<int>("start"));
    AssertTrue(attached.Get<bool>("pal"));
    AssertTrue(attached.Get<bool>("ntsc"));

    ParsedCommand optional = Parse(CliSpecs.Vhs, ["--fm_audio_notch", "-2"]);
    AssertClose(-2.0, optional.Get<double>("fm_audio_notch"), 0.0);

    var diagnostics = new StringWriter();
    AssertFalse(UpstreamIoArgumentValidator.ValidateInput(string.Empty, diagnostics));
    AssertFalse(UpstreamIoArgumentValidator.ValidateOutput(string.Empty, diagnostics));
    AssertEqual(
        "WARN: input file not specified" + Environment.NewLine
        + "WARN: output file '' not found" + Environment.NewLine,
        diagnostics.ToString());
}

[Fact(DisplayName = "dispatcher recognizes decode.py compatible subcommands")]
public void DispatcherRecognizesSubcommands()
{
    AssertTrue(DecodeDispatcher.TryDispatch(["vhs", "in", "out"], out DecodeCommandSpec? spec, out string[] rest));
    AssertEqual("vhs", spec!.Name);
    AssertEqual(2, rest.Length);

    AssertTrue(DecodeDispatcher.TryDispatch(["ld-decode", "in", "out"], out spec, out rest));
    AssertEqual("ld", spec!.Name);
    AssertEqual("in", rest[0]);

    AssertStringSequence(
        ["vhs", "--pal", "in", "out"],
        DecodeDispatcher.NormalizeInvocation(["--pal", "in", "out"], "vhs-decode.exe"));
    AssertStringSequence(
        ["cvbs", "in", "out"],
        DecodeDispatcher.NormalizeInvocation(["in", "out"], "CVBS-DECODE"));
    AssertStringSequence(
        ["ld", "--version"],
        DecodeDispatcher.NormalizeInvocation(["--version"], "ld-decode.exe"));
    AssertStringSequence(
        ["hifi", "--pal", "in.s16", "out.wav"],
        DecodeDispatcher.NormalizeInvocation(
            ["--pal", "in.s16", "out.wav"],
            "hifi-decode.exe"));
    AssertStringSequence(
        ["vhs", "in", "out"],
        DecodeDispatcher.NormalizeInvocation(["vhs", "in", "out"], "decode.exe"));
}

[Fact(DisplayName = "CLI specs cover upstream decode option names")]
public void CliSpecsCoverUpstreamDecodeOptionNames()
{
    string[] common =
    [
        "-h",
        "--help",
        "--system",
        "-s",
        "--start",
        "--start_fileloc",
        "-l",
        "--length",
        "--overwrite",
        "--write_db",
        "-f",
        "--frequency",
        "--cxadc",
        "-t",
        "--threads",
        "--ct",
        "--chroma_trap",
        "--sl",
        "--sharpness",
        "--notch",
        "--notch_q",
        "-p",
        "--pal",
        "-n",
        "--ntsc",
        "--pm",
        "--palm",
        "--NTSCJ",
        "--debug",
        "--skip_hsync_refine",
        "--noAGC",
        "--AGC"
    ];

    string[] vhs =
    [
        "--tf",
        "--tape_format",
        "--ts",
        "--tape_speed",
        "--params_file",
        "--orc",
        "-L",
        "--level_adjust",
        "--ire0_adjust",
        "--high_boost",
        "--wow_level_adjust_smoothing",
        "--wow_interpolation_method",
        "--nodd",
        "--no_diff_demod",
        "--fm_audio_notch",
        "--noclamp",
        "--no_clamping",
        "--clamp",
        "--nld",
        "--non_linear_deemphasis",
        "--sd",
        "--sub_deemphasis",
        "--y_comb",
        "--cafc",
        "--chroma_AFC",
        "-T",
        "--track_phase",
        "--dctp",
        "--detect_chroma_track_phase",
        "--dpc",
        "--disable_phase_correction",
        "--dbh",
        "--disable_burst_hsync",
        "--ck",
        "--enable_color_killer",
        "--no_comb",
        "--skip_chroma",
        "--dp",
        "--debug_plot",
        "--drh",
        "--disable_right_hsync",
        "--level_detect_divisor",
        "--no_resample",
        "--fallback_vsync",
        "--relaxed_line0",
        "--field_order_confidence",
        "--field_order_action",
        "--use_saved_levels",
        "--export_raw_tbc",
        "--noDOD",
        "-D",
        "--dod_t",
        "--dod_threshold_p",
        "--dod_t_abs",
        "--dod_threshold_abs",
        "--dod_h",
        "--dod_hysteresis",
        "--gnrc",
        "--gnuradio_rf_afe"
    ];

    string[] cvbs =
    [
        "-S",
        "--seek",
        "-A",
        "--auto_sync",
        "--no_auto_sync",
        "-C",
        "--clamp_agc",
        "--agc_speed",
        "--agc_gain_factor",
        "--agc_set_gain",
        "--right_hand_hsync",
        "--wow_level_adjust_smoothing",
        "--wow_interpolation_method"
    ];

    string[] ld =
    [
        "-h",
        "--help",
        "--version",
        "-v",
        "--start",
        "-s",
        "--length",
        "-l",
        "--seek",
        "-S",
        "--PAL",
        "-p",
        "--pal",
        "--NTSC",
        "-n",
        "--ntsc",
        "--NTSCJ",
        "-j",
        "-m",
        "--MTF",
        "--MTF_offset",
        "--noAGC",
        "--noDOD",
        "--noEFM",
        "--preEFM",
        "--disable_analog_audio",
        "--disable_analogue_audio",
        "--daa",
        "--AC3",
        "--start_fileloc",
        "--ignoreleadout",
        "--verboseVITS",
        "--RF_TBC",
        "--lowband",
        "--NTSC_color_notch_filter",
        "-N",
        "--V4300D_notch_filter",
        "-V",
        "--deemp_low",
        "--deemp_high",
        "--deemp_strength",
        "--wow_level_adjust_smoothing",
        "--wow_interpolation_method",
        "-t",
        "--threads",
        "-f",
        "--frequency",
        "--analog_audio_frequency",
        "--ntsc_audio_rate",
        "--video_bpf_low",
        "--video_bpf_high",
        "--video_lpf",
        "--video_lpf_order",
        "--audio_filterwidth",
        "--use_profiler",
        "--write-test-ldf"
    ];

    AssertSpecAcceptsOptionNames(CliSpecs.Vhs, common.Concat(vhs));
    AssertSpecAcceptsOptionNames(CliSpecs.Cvbs, common.Concat(cvbs));
    AssertSpecAcceptsOptionNames(CliSpecs.LaserDisc, ld);
}

[Fact(DisplayName = "decode runner prints command help before validation")]
public void DecodeRunnerPrintsCommandHelpBeforeValidation()
{
    var standaloneHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["vhs"] = "174450C2379D07D59109F289FF9F587BDA433E8E38C63EFE604535BBCD9077C4",
        ["cvbs"] = "7933C5EFFD23A835419449AA805D7110ABA4298E23FD46FF51805AF933712D15",
        ["ld"] = "E890F90169572401CF3C1565FF5EF44B23354824516E330250BD80BA136FB579",
        ["hifi"] = "27E40DE774B9CD5E3A6E126A497B074AD239319AD7A79004D4594F74CB7ECC2B"
    };
    var facadeHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["vhs"] = "9FE63E4A3C18927DE477D66BCF612085FB01ED85DA8BFD7AE2FACAE9E0E0EDA1",
        ["cvbs"] = "A19F45D777A92EDB940880F5953A9E2D2298AF850F7769A3615FB14CDC12877B",
        ["ld"] = "62D9A869755E04C5F7EC0ADCD682AC856554C1EE861B8D463DC9AA21BDDC2F68",
        ["hifi"] = "14F69F3DD5869D3BAF3D44558ADF32DAC040F97F37CF00A2F3127FA101D7765E"
    };

    foreach (DecodeCommandSpec spec in CliSpecs.AllCommands)
    {
        foreach (string helpOption in new[] { "-h", "--help" })
        {
            ParsedCommand command = Parse(spec, [helpOption]);
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            AssertEqual(0, exitCode);
            AssertContains(output.ToString(), $"usage: {spec.Aliases[0]}");
            AssertFalse(output.ToString().Contains($"usage: decode.py {spec.Name}", StringComparison.Ordinal));
            AssertContains(output.ToString(), "-h, --help");
            AssertContains(output.ToString(), "infile");
            AssertContains(output.ToString(), "outfile");
            AssertEqual(standaloneHashes[spec.Name], Utf8LfSha256(output.ToString()));
            AssertEqual(string.Empty, error.ToString());
        }

        ParsedCommand facade = new CommandLineParser().Parse(spec, ["--help"], "decode.py");
        var facadeOutput = new StringWriter();
        AssertEqual(0, new DecodeRunner().Run(
            facade,
            facadeOutput,
            TextWriter.Null,
            TestContext.Current.CancellationToken));
        AssertContains(facadeOutput.ToString(), "usage: decode.py ");
        AssertFalse(facadeOutput.ToString().Contains($"usage: decode.py {spec.Name}", StringComparison.Ordinal));
        AssertEqual(facadeHashes[spec.Name], Utf8LfSha256(facadeOutput.ToString()));
    }

    AssertEqual("vhs-decode", DecodeDispatcher.InvocationProgramName("C:\\tools\\vhs-decode.exe"));
    AssertEqual("decode.py", DecodeDispatcher.InvocationProgramName("C:\\tools\\decode.exe"));
}

[Fact(DisplayName = "vhs parser applies defaults and aliases")]
public void VhsParserAppliesDefaultsAndAliases()
{
    ParsedCommand command = Parse(CliSpecs.Vhs, ["--tf", "betamax", "--ts", "SLP", "-f", "8fsc", "-t", "2", "input.u8", "outbase"]);
    AssertEqual("BETAMAX", command.Get<string>("tape_format"));
    AssertEqual("slp", command.Get<string>("tape_speed"));
    AssertClose(8.0 * (315.0e6 / 88.0) / 1.0e6, command.Get<double>("inputfreq"), 1e-12);
    AssertEqual(2, command.Get<int>("threads"));
    AssertEqual("input.u8", command.InputFile);
    AssertEqual("outbase", command.OutputBase);
    AssertEqual("NTSC", VideoSystemSelector.Select(command));
}

[Fact(DisplayName = "vhs parser handles optional ire0_adjust normalization")]
public void VhsParserHandlesIre0Adjust()
{
    ParsedCommand flagOnly = Parse(CliSpecs.Vhs, ["--ire0_adjust", "in", "out"]);
    AssertEqual("backporch", flagOnly.Get<string>("ire0_adjust"));
    AssertEqual("in", flagOnly.InputFile);

    ParsedCommand explicitValue = Parse(CliSpecs.Vhs, ["--ire0_adjust", "hsync,backporch", "in", "out"]);
    AssertEqual("hsync,backporch", explicitValue.Get<string>("ire0_adjust"));
    AssertEqual("in", explicitValue.InputFile);
}

[Fact(DisplayName = "vhs parser keeps invalid ire0_adjust follower positional")]
public void VhsParserKeepsInvalidIre0FollowerPositional()
{
    ParsedCommand command = Parse(CliSpecs.Vhs, ["--ire0_adjust", "capture.ldf", "out"]);
    AssertEqual("backporch", command.Get<string>("ire0_adjust"));
    AssertEqual("capture.ldf", command.InputFile);
    AssertEqual("out", command.OutputBase);
}

[Fact(DisplayName = "vhs system selection rejects incompatible flags")]
public void VhsSystemSelectionRejectsConflicts()
{
    ParsedCommand command = Parse(CliSpecs.Vhs, ["--pal", "--ntsc"]);
    AssertThrows<ArgumentException>(() => VideoSystemSelector.Select(command));

    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.u8");
        File.WriteAllBytes(inputPath, [0]);
        var cases = new (DecodeCommandSpec Spec, string[] Options, string Message)[]
        {
            (CliSpecs.Vhs, ["--pal", "--ntsc"], "ERROR: Can only be PAL or NTSC"),
            (CliSpecs.Vhs, ["--palm", "--pal"], "ERROR: Can only be PAL-M or PAL"),
            (CliSpecs.Vhs, ["--palm", "--ntsc"], "ERROR: Can only be PAL-M or NTSC"),
            (CliSpecs.Cvbs, ["--pal", "--ntsc"], "ERROR: Can only be PAL or NTSC"),
            (CliSpecs.LaserDisc, ["--PAL", "--NTSC"], "ERROR: Can only be PAL or NTSC"),
            (CliSpecs.LaserDisc, ["--PAL", "--AC3"], "ERROR: AC3 audio decoding is only supported for NTSC")
        };

        for (int i = 0; i < cases.Length; i++)
        {
            (DecodeCommandSpec spec, string[] options, string message) = cases[i];
            ParsedCommand conflict = Parse(spec, [.. options, inputPath, Path.Combine(tempDirectory, $"out-{i}")]);
            var output = new StringWriter();
            var error = new StringWriter();
            AssertEqual(1, new DecodeRunner().Run(
                conflict,
                output,
                error,
                TestContext.Current.CancellationToken));
            AssertEqual(message + Environment.NewLine, output.ToString());
            AssertEqual(string.Empty, error.ToString());
        }
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "CVBS runner preserves v0.4.0 unsupported-system failures")]
public void CvbsRunnerPreservesReleaseUnsupportedSystemFailures()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.s16");
        File.WriteAllBytes(inputPath, [0, 0]);
        foreach (string system in new[] { "PAL_M", "PALM", "MESECAM", "405", "819", "NLINHA" })
        {
            ParsedCommand command = Parse(CliSpecs.Cvbs, [
                "--system",
                system,
                "--length",
                "0",
                inputPath,
                Path.Combine(tempDirectory, "out-" + system)
            ]);
            var output = new StringWriter();
            var error = new StringWriter();

            AssertEqual(1, new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken));
            AssertEqual(string.Empty, output.ToString());
            string normalized = system == "PALM" ? "PAL_M" : system;
            AssertEqual($"('Unknown video system!', '{normalized}')" + Environment.NewLine, error.ToString());
        }
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "cvbs parser handles agc options")]
public void CvbsParserHandlesAgcOptions()
{
    ParsedCommand command = Parse(CliSpecs.Cvbs, ["--clamp_agc", "--agc_speed", "0.25", "--right_hand_hsync", "in", "out"]);
    AssertTrue(command.Get<bool>("clamp_agc"));
    AssertTrue(command.Get<bool>("rhs_hsync"));
    AssertClose(0.25, command.Get<double>("agc_speed"), 1e-12);
}

[Fact(DisplayName = "CVBS runner preserves v0.4.0 chroma-trap constructor failure")]
public void CvbsRunnerPreservesReleaseChromaTrapConstructorFailure()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.s16");
        string outputBase = Path.Combine(tempDirectory, "capture");
        File.WriteAllBytes(inputPath, [0, 0]);
        var output = new StringWriter();
        var error = new StringWriter();

        AssertEqual(1, new DecodeRunner().Run(
            Parse(CliSpecs.Cvbs, [
                "--pal",
                "--chroma_trap",
                inputPath,
                outputBase
            ]),
            output,
            error,
            TestContext.Current.CancellationToken));

        AssertEqual(string.Empty, output.ToString());
        AssertEqual(
            "ChromaSepClass.__init__() missing 1 required positional argument: 'logger'"
            + Environment.NewLine,
            error.ToString());
        AssertTrue(File.Exists(outputBase + ".log"));
        AssertEqual(0L, new FileInfo(outputBase + ".log").Length);
        AssertFalse(File.Exists(outputBase + ".tbc"));
        AssertFalse(File.Exists(outputBase + ".tbc.json"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "ld parser handles audio and filter options")]
public void LdParserHandlesAudioAndFilterOptions()
{
    ParsedCommand command = Parse(CliSpecs.LaserDisc, ["--AC3", "--ntsc_audio_rate", "--video_lpf", "6.5MHz", "in.ldf", "out"]);
    AssertTrue(command.Get<bool>("AC3"));
    AssertTrue(command.Get<bool>("ntsc_audio_rate"));
    AssertClose(6.5, command.Get<double>("vlpf"), 1e-12);
    AssertEqual("in.ldf", command.InputFile);
}

[Fact(DisplayName = "ld parser allows version without positionals")]
public void LdParserAllowsVersionWithoutPositionals()
{
    ParsedCommand command = Parse(CliSpecs.LaserDisc, ["--unknown-before-version", "--version", "ignored"]);
    AssertTrue(command.Get<bool>("version"));
    AssertEqual(0, command.Positionals.Count);
    var output = new StringWriter();
    AssertEqual(0, new DecodeRunner().Run(
        command,
        output,
        TextWriter.Null,
        TestContext.Current.CancellationToken));
    AssertEqual(DecodeVersionInfo.Version + Environment.NewLine, output.ToString());
}

[Fact(DisplayName = "decode version info parses upstream git formats")]
public void DecodeVersionInfoParsesUpstreamGitFormats()
{
    AssertEqual(("main", "abcdef"), DecodeVersionInfo.ExtractGitVersionParts("main:abcdef:extra"));
    AssertEqual(("feature", "1234567"), DecodeVersionInfo.ExtractGitVersionParts("feature/1234567"));
    AssertEqual(("release", "beefcafe"), DecodeVersionInfo.ExtractGitVersionParts("1.2.3+git.beefcafe.dirty"));
    AssertEqual(("vhs_decode", "g4315520"), DecodeVersionInfo.ExtractGitVersionParts(DecodeVersionInfo.Version));
}

[Fact(DisplayName = "decode version OS info matches Python platform shape")]
public void DecodeVersionOsInfoMatchesPythonPlatformShape()
{
    string osInfo = DecodeVersionInfo.OsInfo();
    string[] parts = osInfo.Split(':', 3);
    AssertEqual(3, parts.Length);
    AssertTrue(parts.All(part => !string.IsNullOrWhiteSpace(part)));
    if (OperatingSystem.IsWindows())
    {
        System.Version version = Environment.OSVersion.Version;
        AssertEqual("Windows", parts[0]);
        AssertEqual(version.Build >= 22_000 ? "11" : "10", parts[1]);
        AssertEqual($"{version.Major}.{version.Minor}.{version.Build}", parts[2]);
    }
}

[Fact(DisplayName = "decode session logs match upstream entry points")]
public void DecodeSessionLogsMatchUpstreamEntryPoints()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        using DecodeSession vhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "input.u8",
            Path.Combine(tempDirectory, "vhs")
        ]));
        string vhsLog = File.ReadAllText(DecodeSessionLogWriter.Write(vhs));
        AssertContains(vhsLog, " - lddecode - DEBUG - Sys Parameters:");
        AssertContains(vhsLog, "Sys Parameters: " + Environment.NewLine + "{");
        AssertContains(vhsLog, "RF Parameters: " + Environment.NewLine + "{");
        AssertContains(vhsLog, Environment.NewLine + "    \"");
        AssertEqual(2, vhsLog.Split("\"hz_ire\":", StringSplitOptions.None).Length - 1);
        AssertEqual(2, vhsLog.Split("\"ire0\":", StringSplitOptions.None).Length - 1);
        AssertEqual(2, vhsLog.Split("\"vsync_ire\":", StringSplitOptions.None).Length - 1);
        AssertEqual(1, vhsLog.Split("\"track_ire0_offset\":", StringSplitOptions.None).Length - 1);
        AssertContains(
            vhsLog,
            "\"track_ire0_offset\": ["
                + Environment.NewLine
                + "        0,"
                + Environment.NewLine
                + "        0"
                + Environment.NewLine
                + "    ]");
        string[] sortedSysKeys = vhs.Parameters.SysParams.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        AssertTrue(vhsLog.IndexOf("\"" + sortedSysKeys[0] + "\"", StringComparison.Ordinal)
            < vhsLog.IndexOf("\"" + sortedSysKeys[1] + "\"", StringComparison.Ordinal));

        using DecodeSession ld = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "input.s16",
            Path.Combine(tempDirectory, "ld")
        ]), blockLength: 4096);
        string ldLog = File.ReadAllText(DecodeSessionLogWriter.Write(ld));
        AssertContains(ldLog, " - lddecode - DEBUG - ld-decode version vhs_decode:g4315520");
        AssertFalse(ldLog.Contains("Sys Parameters", StringComparison.Ordinal));
        AssertFalse(ldLog.Contains("RF Parameters", StringComparison.Ordinal));

        using DecodeSession cvbs = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--pal",
            "input.s16",
            Path.Combine(tempDirectory, "cvbs")
        ]), blockLength: 4096);
        string cvbsLogPath = DecodeSessionLogWriter.Write(cvbs);
        AssertEqual(string.Empty, File.ReadAllText(cvbsLogPath));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "decode session factory creates VHS raw sessions")]
public void DecodeSessionFactoryCreatesVhsRawSessions()
{
    ParsedCommand command = Parse(CliSpecs.Vhs, ["--pal", "--tf", "svhs", "input.u8", "outbase"]);
    DecodeSession session = DecodeSessionFactory.Create(command);
    AssertEqual("vhs", session.Spec.Name);
    AssertEqual("PAL", session.System);
    AssertEqual("SVHS", session.Parameters.TapeFormat);
    AssertEqual(32 * 1024, session.BlockLength);
    AssertEqual(1024, session.BlockCut);
    AssertEqual(1024, session.BlockCutEnd);
    AssertEqual(30 * 1024, session.StreamDecoder.BlockStride);
    AssertClose(40_000_000.0, session.DecodeSampleRateHz, 1e-12);
    AssertClose(0.18, session.DropoutOptions.ThresholdFraction, 1e-12);
    AssertEqual(TbcDropoutDetectionMode.TapeEnvelope, session.DropoutOptions.Mode);
    AssertTrue(session.HSyncRefineOptions.Enabled);
    AssertTrue(session.HSyncRefineOptions.UseRightHSync);
    AssertTrue(session.SyncDetectionOptions.DetectLevels);
    AssertEqual(3, session.SyncDetectionOptions.LevelDetectDivisor);
    AssertFalse(session.SyncDetectionOptions.UseSavedLevels);
    AssertFalse(session.SyncDetectionOptions.UseFallbackVSync);
    AssertFalse(session.SyncDetectionOptions.RelaxedLine0);
    AssertFalse(session.FilterOptions.LdClipDemodForVideo);
    var defaultVhsLoader = (FfmpegStreamSampleLoader)session.Loader;
    AssertStringSequence(["-f", "u8"], defaultVhsLoader.InputArguments.ToArray());
    AssertEqual(0, defaultVhsLoader.OutputArguments.Count);
    AssertEqual(32 * 1024, session.Filters.Video.Length);
    var vhsSyncAnalyzer = (SyncAnalyzer)PrivateFieldValue(session.TbcFieldDecoder, "_syncAnalyzer")!;
    AssertClose(0.7, vhsSyncAnalyzer.HSyncToleranceUs, 1e-12);
    AssertClose(0.9, vhsSyncAnalyzer.EqualizingToleranceUs, 1e-12);
    AssertType<VsyncSerrationDetector>(PrivateFieldValue(session.TbcFieldDecoder, "_vsyncSerrationDetector")!);

    DecodeSession savedLevels = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--use_saved_levels", "input.u8", "outbase"]));
    AssertTrue(savedLevels.SyncDetectionOptions.UseSavedLevels);
    AssertFalse(savedLevels.SyncDetectionOptions.ClampDcOffset);

    DecodeSession fallbackVsync = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--fallback_vsync", "--relaxed_line0", "input.u8", "outbase"]));
    AssertTrue(fallbackVsync.SyncDetectionOptions.UseFallbackVSync);
    AssertTrue(fallbackVsync.SyncDetectionOptions.RelaxedLine0);

    DecodeSession clampDcOffset = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--clamp", "input.u8", "outbase"]));
    AssertTrue(clampDcOffset.SyncDetectionOptions.ClampDcOffset);

    DecodeSession noClampDcOffset = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--noclamp", "input.u8", "outbase"]));
    AssertFalse(noClampDcOffset.SyncDetectionOptions.ClampDcOffset);

    DecodeSession noHSyncRefine = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--skip_hsync_refine", "input.u8", "outbase"]));
    AssertFalse(noHSyncRefine.HSyncRefineOptions.Enabled);
    AssertFalse(noHSyncRefine.HSyncRefineOptions.UseRightHSync);

    DecodeSession noRightHSync = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--disable_right_hsync", "input.u8", "outbase"]));
    AssertTrue(noRightHSync.HSyncRefineOptions.Enabled);
    AssertFalse(noRightHSync.HSyncRefineOptions.UseRightHSync);

    ParsedCommand resampling = Parse(CliSpecs.Vhs, ["-f", "8fsc", "input.u8", "outbase"]);
    DecodeSession resampledSession = DecodeSessionFactory.Create(resampling);
    AssertClose(40_000_000.0, resampledSession.DecodeSampleRateHz, 1e-12);
    AssertType<FfmpegStreamSampleLoader>(resampledSession.Loader);

    ParsedCommand noResample = Parse(CliSpecs.Vhs, ["-f", "8fsc", "--no_resample", "--level_detect_divisor", "10", "input.u8", "outbase"]);
    DecodeSession nativeRateSession = DecodeSessionFactory.Create(noResample);
    AssertClose(FrequencyParser.ParseMHz("8fsc") * 1_000_000.0, nativeRateSession.DecodeSampleRateHz, 1e-6);
    AssertType<UInt8SampleLoader>(nativeRateSession.Loader);
    AssertClose(0.35, nativeRateSession.DropoutOptions.ThresholdFraction, 1e-12);
    AssertEqual(7, nativeRateSession.SyncDetectionOptions.LevelDetectDivisor);

    DecodeSession nativeLds = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["capture.lds", "outbase"]));
    AssertType<PackedDdD4To40SampleLoader>(nativeLds.Loader);
    DecodeSession nativeLdf = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["capture.ldf", "outbase"]));
    AssertType<FfmpegPcm16SampleLoader>(nativeLdf.Loader);
    DecodeSession streamedWave = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["capture.wav", "outbase"]));
    AssertType<FfmpegStreamSampleLoader>(streamedWave.Loader);
    DecodeSession nativeWave = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--no_resample", "capture.wav", "outbase"]));
    AssertType<FfmpegPcm16SampleLoader>(nativeWave.Loader);
    AssertThrows<NotSupportedException>(() => DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["capture.r30", "outbase"])));
    DecodeSession nativeR30 = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--no_resample", "capture.r30", "outbase"]));
    AssertType<Packed3To32SampleLoader>(nativeR30.Loader);

    DecodeSession cxadcResampled = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--cxadc", "input.u8", "outbase"]));
    AssertClose(40_000_000.0, cxadcResampled.DecodeSampleRateHz, 1e-12);
    AssertClose(0.18, cxadcResampled.DropoutOptions.ThresholdFraction, 1e-12);

    DecodeSession cxadcNative = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--cxadc", "--no_resample", "input.u8", "outbase"]));
    AssertClose(FrequencyParser.CxAdcMHz * 1_000_000.0, cxadcNative.DecodeSampleRateHz, 1e-6);
    AssertClose(0.35, cxadcNative.DropoutOptions.ThresholdFraction, 1e-12);

    DecodeSession invalidDivisor = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--level_detect_divisor", "11", "input.u8", "outbase"]));
    AssertEqual(1, invalidDivisor.SyncDetectionOptions.LevelDetectDivisor);

    DecodeSession cvbsDefault = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["input.u8", "outbase"]));
    var cvbsDefaultLoader = (FfmpegStreamSampleLoader)cvbsDefault.Loader;
    AssertStringSequence(["-f", "u8"], cvbsDefaultLoader.InputArguments.ToArray());
    AssertEqual(0, cvbsDefaultLoader.OutputArguments.Count);
    AssertTrue(cvbsDefault.HSyncRefineOptions.Enabled);
    AssertFalse(cvbsDefault.HSyncRefineOptions.UseRightHSync);
    AssertFalse(cvbsDefault.SyncDetectionOptions.DetectLevels);
    AssertTrue(cvbsDefault.SyncDetectionOptions.CvbsAutoSync);
    AssertEqual(TbcDropoutDetectionMode.Disabled, cvbsDefault.DropoutOptions.Mode);
    AssertTrue(PrivateFieldValue(cvbsDefault.TbcFieldDecoder, "_vsyncSerrationDetector") is null);

    DecodeSession cvbsNoAutoSync = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["--no_auto_sync", "input.u8", "outbase"]));
    AssertFalse(cvbsNoAutoSync.SyncDetectionOptions.CvbsAutoSync);

    DecodeSession cvbsNoHSyncRefine = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["--skip_hsync_refine", "input.u8", "outbase"]));
    AssertTrue(cvbsNoHSyncRefine.HSyncRefineOptions.Enabled);
    AssertFalse(cvbsNoHSyncRefine.HSyncRefineOptions.UseRightHSync);
    DecodeSession cvbsRightHSync = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["--right_hand_hsync", "input.u8", "outbase"]));
    AssertTrue(cvbsRightHSync.HSyncRefineOptions.Enabled);
    AssertTrue(cvbsRightHSync.HSyncRefineOptions.UseRightHSync);

    DecodeSession cvbsResampled = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["-f", "8fsc", "input.u8", "outbase"]));
    AssertClose(40_000_000.0, cvbsResampled.DecodeSampleRateHz, 1e-12);
    var cvbsResampledLoader = (FfmpegStreamSampleLoader)cvbsResampled.Loader;
    AssertStringSequence(["-f", "u8"], cvbsResampledLoader.InputArguments.ToArray());
    AssertEqual("-filter:a", cvbsResampledLoader.OutputArguments[0]);
    AssertContains(cvbsResampledLoader.OutputArguments[1], "asetrate=28636363.636363");
    AssertContains(cvbsResampledLoader.OutputArguments[1], "aresample=40000000");
    AssertThrows<NotSupportedException>(() => DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["input.lds", "outbase"])));

    DecodeSession laserDisc = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "outbase"
    ]));
    AssertEqual(TbcDropoutDetectionMode.LaserDiscDemod, laserDisc.DropoutOptions.Mode);
    AssertTrue(PrivateFieldValue(laserDisc.TbcFieldDecoder, "_vsyncSerrationDetector") is null);
}

[Fact(DisplayName = "decode session factory applies common notch options")]
public void DecodeSessionFactoryAppliesCommonNotchOptions()
{
    DecodeSession vhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--notch",
        "2.5MHz",
        "--notch_q",
        "20",
        "input.u8",
        "out"
    ]), blockLength: 4096);

    DecodeSession vhsWithoutNotch = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "input.u8",
        "out"
    ]), blockLength: 4096);

    AssertClose(2_500_000.0, vhs.FilterOptions.VideoNotchHz!.Value, 1e-9);
    AssertClose(20.0, vhs.FilterOptions.VideoNotchQ, 1e-12);
    AssertTrue(vhs.Filters.RfVideoMagnitude[256] < vhsWithoutNotch.Filters.RfVideoMagnitude[256] * 1e-6);
    AssertClose(vhsWithoutNotch.Filters.VideoMagnitude[256], vhs.Filters.VideoMagnitude[256], 1e-12);
    AssertClose(vhsWithoutNotch.Filters.VideoLowPass05Magnitude[256], vhs.Filters.VideoLowPass05Magnitude[256], 1e-12);

    DecodeSession cvbs = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--notch",
        "2.5MHz",
        "--notch_q",
        "15",
        "input.u8",
        "out"
    ]), blockLength: 4096);
    DecodeSession cvbsWithoutNotch = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "input.u8",
        "out"
    ]), blockLength: 4096);

    AssertClose(2_500_000.0, cvbs.FilterOptions.VideoNotchHz!.Value, 1e-9);
    AssertClose(15.0, cvbs.FilterOptions.VideoNotchQ, 1e-12);
    AssertClose(cvbsWithoutNotch.Filters.VideoMagnitude[256], cvbs.Filters.VideoMagnitude[256], 1e-12);
}

[Fact(DisplayName = "decode session factory applies VHS FM audio notch options")]
public void DecodeSessionFactoryAppliesVhsFmAudioNotchOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "input.u8",
        "out"
    ]), blockLength: 20_000);

    DecodeSession explicitNotch = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--fm_audio_notch",
        "12",
        "input.u8",
        "out"
    ]), blockLength: 20_000);

    DecodeSession flagOnly = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "input.u8",
        "out",
        "--fm_audio_notch"
    ]), blockLength: 20_000);

    DecodeSession hi8Default = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--tf",
        "HI8",
        "input.u8",
        "out"
    ]), blockLength: 20_000);

    DecodeSession hi8Fractional = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--tf",
        "HI8",
        "--fm_audio_notch",
        "0.5",
        "input.u8",
        "out"
    ]), blockLength: 20_000);

    int leftBin = FrequencyBin(JsonDouble(explicitNotch.Parameters.RfParams, "fm_audio_channel_0_freq"), explicitNotch.DecodeSampleRateHz, explicitNotch.BlockLength);
    int rightBin = FrequencyBin(JsonDouble(explicitNotch.Parameters.RfParams, "fm_audio_channel_1_freq"), explicitNotch.DecodeSampleRateHz, explicitNotch.BlockLength);

    AssertClose(0.0, plain.FilterOptions.FmAudioNotchQ, 1e-12);
    AssertClose(12.0, explicitNotch.FilterOptions.FmAudioNotchQ, 1e-12);
    AssertClose(10.0, flagOnly.FilterOptions.FmAudioNotchQ, 1e-12);
    AssertTrue(explicitNotch.Filters.RfVideoMagnitude[leftBin] < plain.Filters.RfVideoMagnitude[leftBin] * 1e-6);
    AssertTrue(explicitNotch.Filters.RfVideoMagnitude[rightBin] < plain.Filters.RfVideoMagnitude[rightBin] * 1e-6);
    AssertTrue(flagOnly.Filters.RfVideoMagnitude[leftBin] < plain.Filters.RfVideoMagnitude[leftBin] * 1e-6);

    int hi8LeftBin = FrequencyBin(JsonDouble(hi8Default.Parameters.RfParams, "fm_audio_channel_0_freq"), hi8Default.DecodeSampleRateHz, hi8Default.BlockLength);
    AssertClose(1.0, hi8Default.FilterOptions.FmAudioNotchQ, 1e-12);
    AssertClose(0.5, hi8Fractional.FilterOptions.FmAudioNotchQ, 1e-12);
    AssertTrue(hi8Default.Filters.RfVideoMagnitude[hi8LeftBin] < hi8Fractional.Filters.RfVideoMagnitude[hi8LeftBin] * 1e-6);
}

[Fact(DisplayName = "decode session factory applies VHS high boost options")]
public void DecodeSessionFactoryAppliesVhsHighBoostOptions()
{
    DecodeSession plainVhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "input.u8",
        "out"
    ]));

    DecodeSession explicitBoost = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--high_boost",
        "2.5",
        "input.u8",
        "out"
    ]));

    DecodeSession catalogBoost = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--tf",
        "UMATIC",
        "--ts",
        "vp",
        "input.u8",
        "out"
    ]));

    AssertEqual(null, plainVhs.FilterOptions.RfHighBoost);
    AssertClose(2.5, explicitBoost.FilterOptions.RfHighBoost!.Multiplier, 1e-12);
    AssertClose(JsonDouble(explicitBoost.Parameters.RfParams, "boost_bpf_low"), explicitBoost.FilterOptions.RfHighBoost.LowHz, 1e-12);
    AssertClose(JsonDouble(explicitBoost.Parameters.RfParams, "boost_bpf_high"), explicitBoost.FilterOptions.RfHighBoost.HighHz, 1e-12);
    AssertClose(1.0, catalogBoost.FilterOptions.RfHighBoost!.Multiplier, 1e-12);
    AssertClose(JsonDouble(catalogBoost.Parameters.RfParams, "boost_bpf_low"), catalogBoost.FilterOptions.RfHighBoost.LowHz, 1e-12);
    AssertClose(JsonDouble(catalogBoost.Parameters.RfParams, "boost_bpf_high"), catalogBoost.FilterOptions.RfHighBoost.HighHz, 1e-12);
}

[Fact(DisplayName = "decode session factory applies diff demod repair options")]
public void DecodeSessionFactoryAppliesDiffDemodRepairOptions()
{
    DecodeSession enabled = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "input.u8",
        "out"
    ]));

    DecodeSession disabled = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--no_diff_demod",
        "input.u8",
        "out"
    ]));

    double expected = (JsonDouble(enabled.Parameters.SysParams, "ire0")
        + (JsonDouble(enabled.Parameters.SysParams, "hz_ire") * 100.0)) * 2.0;
    AssertClose(expected, enabled.FilterOptions.DiffDemodRepair!.MaxValue, 1e-6);
    AssertEqual(null, disabled.FilterOptions.DiffDemodRepair);
    AssertEqual(RfFmDemodulatorMode.VhsRustApproximation, enabled.FilterOptions.FmDemodulatorMode);
    AssertEqual(RfFmDemodulatorMode.VhsRustApproximation, disabled.FilterOptions.FmDemodulatorMode);
}

[Fact(DisplayName = "decode session factory applies Betamax fsc notch")]
public void DecodeSessionFactoryAppliesBetamaxFscNotch()
{
    DecodeSession vhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]), blockLength: 7040);

    DecodeSession betamax = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--tf",
        "BETAMAX",
        "input.u8",
        "out"
    ]), blockLength: 7040);

    double fscHz = JsonDouble(betamax.Parameters.SysParams, "fsc_mhz") * 1_000_000.0;
    int fscBin = FrequencyBin(fscHz, betamax.DecodeSampleRateHz, betamax.BlockLength);
    AssertEqual(null, vhs.FilterOptions.BetamaxFscNotchHz);
    AssertClose(fscHz, betamax.FilterOptions.BetamaxFscNotchHz!.Value, 1e-6);
    DecodeFilterSet betamaxWithoutPostNotch = DecodeFilterSetBuilder.BuildBasic(
        betamax.Parameters,
        betamax.DecodeSampleRateHz,
        betamax.BlockLength,
        betamax.FilterOptions with { BetamaxFscNotchHz = null });
    AssertClose(betamaxWithoutPostNotch.VideoMagnitude[fscBin], betamax.Filters.VideoMagnitude[fscBin], 1e-12);
    AssertTrue(betamax.Filters.VideoLowPass05Magnitude[fscBin] > 0.0);
}

[Fact(DisplayName = "decode session factory applies chroma trap options")]
public void DecodeSessionFactoryAppliesChromaTrapOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]));

    DecodeSession vhsTrap = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--chroma_trap",
        "input.u8",
        "out"
    ]));

    DecodeSession cvbsTrap = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--pal",
        "--chroma_trap",
        "input.u8",
        "out"
    ]));

    AssertEqual(null, plain.FilterOptions.ChromaTrap);
    AssertClose(JsonDouble(vhsTrap.Parameters.SysParams, "fsc_mhz") * 1_000_000.0, vhsTrap.FilterOptions.ChromaTrap!.FscHz, 1e-6);
    AssertClose(JsonDouble(cvbsTrap.Parameters.SysParams, "fsc_mhz") * 1_000_000.0, cvbsTrap.FilterOptions.ChromaTrap!.FscHz, 1e-6);
}

[Fact(DisplayName = "decode session factory applies chroma options")]
public void DecodeSessionFactoryAppliesChromaOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--ntsc",
        "input.u8",
        "out"
    ]));

    ChromaDecodeOptions plainChroma = plain.ChromaOptions!;
    AssertTrue(plainChroma.IsColorUnder);
    AssertTrue(plainChroma.WriteChroma);
    AssertFalse(plainChroma.SkipChroma);
    AssertFalse(plainChroma.UseChromaAfc);
    AssertFalse(plainChroma.DisableComb);
    AssertFalse(plainChroma.ChromaDeemphasisFilter);
    AssertFalse(plainChroma.ChromaAudioNotch);
    AssertEqual(5, plainChroma.ChromaOffsetSamples);
    AssertFalse(plainChroma.UseOldRawChromaOutput);
    AssertTrue(plain.TbcFieldDecoder.HasChromaOutput);
    AssertTrue(plain.TbcFieldDecoder.ChromaFieldOptions!.FinalFilter is not null);
    AssertTrue(plain.TbcFieldDecoder.ChromaFieldOptions.FinalSosFilter is not null);
    JsonElement colorBurstUs = plain.Parameters.SysParams.GetProperty("colorBurstUS");
    double outputSamplesPerUsec = JsonDouble(plain.Parameters.SysParams, "outfreq");
    AssertEqual(
        (int)Math.Floor(colorBurstUs[0].GetDouble() * outputSamplesPerUsec) - 5,
        plain.TbcFieldDecoder.ChromaFieldOptions.BurstStart);
    AssertEqual(
        (int)Math.Ceiling(colorBurstUs[1].GetDouble() * outputSamplesPerUsec) + 10,
        plain.TbcFieldDecoder.ChromaFieldOptions.BurstEnd);
    AssertFalse(plain.TbcFieldDecoder.ChromaFieldOptions.BurstStart == plain.TbcFrameSpec.ColourBurstStart - 5);
    AssertFalse(plain.TbcFieldDecoder.ChromaFieldOptions.BurstEnd == plain.TbcFrameSpec.ColourBurstEnd + 10);

    DecodeSession oldRawChroma = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--orc",
        "input.u8",
        "out"
    ]));
    AssertTrue(oldRawChroma.ChromaOptions!.UseOldRawChromaOutput);

    DecodeSession typeC = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--tf",
        "TYPEC",
        "input.u8",
        "out"
    ]));
    AssertFalse(typeC.ChromaOptions!.IsColorUnder);
    AssertFalse(typeC.ChromaOptions.WriteChroma);
    AssertFalse(typeC.TbcFieldDecoder.HasChromaOutput);

    DecodeSession skipped = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--skip_chroma",
        "input.u8",
        "out"
    ]));
    AssertTrue(skipped.ChromaOptions!.SkipChroma);
    AssertFalse(skipped.ChromaOptions.WriteChroma);

    DecodeSession rawTbc = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--export_raw_tbc",
        "input.u8",
        "out"
    ]));
    AssertFalse(rawTbc.ChromaOptions!.WriteChroma);
    AssertFalse(rawTbc.TbcFieldDecoder.HasChromaOutput);

    DecodeSession betamaxPal = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--tf",
        "BETAMAX",
        "input.u8",
        "out"
    ]));
    AssertTrue(betamaxPal.ChromaOptions!.UseChromaAfc);

    DecodeSession betamaxNtsc = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--ntsc",
        "--tf",
        "BETAMAX",
        "input.u8",
        "out"
    ]));
    AssertFalse(betamaxNtsc.ChromaOptions!.UseChromaAfc);

    DecodeSession explicitAfc = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--chroma_AFC",
        "input.u8",
        "out"
    ]));
    AssertTrue(explicitAfc.ChromaOptions!.UseChromaAfc);
    AssertTrue(explicitAfc.FilterOptions.UseChromaAfc);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions!.ChromaPreFilter is not null);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaPreSosFilter is not null);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaPreFilterMoveSamples > 0);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaAfcTrackCarrier);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaAfcLineFrequencyHz > 0);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaAfcFineTuneStepHz > 0);
    ChromaAfcMeasurementFilterSet afcMeasurementFilters = explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaAfcMeasurementFilters
        ?? throw new Exception("Expected chroma AFC measurement filters.");
    AssertTrue(afcMeasurementFilters.HighPass.Length > 0);
    AssertTrue(afcMeasurementFilters.LowPass.Length > 0);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaAfcPreFilterLowHz > 0.0);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaAfcPreFilterUpperRatio > 1.0);
    AssertTrue(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaAfcPreFilterOrder > 0);
    AssertClose(
        explicitAfc.DecodeSampleRateHz,
        explicitAfc.TbcFieldDecoder.ChromaFieldOptions.ChromaAfcDecodeSampleRateHz,
        1e-6);
    AssertFalse(explicitAfc.TbcFieldDecoder.ChromaFieldOptions.DisableBurstHsync);

    DecodeSession mesecam = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--system",
        "MESECAM",
        "input.u8",
        "out"
    ]));
    AssertTrue(mesecam.ChromaOptions!.DisableComb);

    DecodeSession hi8 = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--tf",
        "HI8",
        "input.u8",
        "out"
    ]));
    AssertTrue(hi8.ChromaOptions!.ChromaDeemphasisFilter);
    AssertTrue(hi8.ChromaOptions.ChromaAudioNotch);
    AssertTrue(hi8.ChromaOptions.WriteChroma);
    AssertTrue(hi8.TbcFieldDecoder.ChromaFieldOptions!.ChromaDeemphasisFilter is not null);
    AssertTrue(hi8.TbcFieldDecoder.ChromaFieldOptions.ChromaAudioNotchFilter is null);
    AssertTrue(plain.TbcFieldDecoder.ChromaFieldOptions.ChromaDeemphasisFilter is null);

    DecodeSession hi8Afc = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--tf",
        "HI8",
        "--chroma_AFC",
        "input.u8",
        "out"
    ]));
    AssertTrue(hi8Afc.TbcFieldDecoder.ChromaFieldOptions!.ChromaAudioNotchFilter is not null);

    DecodeSession flags = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--detect_chroma_track_phase",
        "--enable_color_killer",
        "--disable_burst_hsync",
        "--disable_phase_correction",
        "--no_comb",
        "input.u8",
        "out"
    ]));
    AssertTrue(flags.ChromaOptions!.DetectChromaTrackPhase);
    AssertTrue(flags.ChromaOptions.EnableColorKiller);
    AssertTrue(flags.ChromaOptions.DisableBurstHsync);
    AssertTrue(flags.ChromaOptions.DisablePhaseCorrection);
    AssertTrue(flags.ChromaOptions.DisableComb);
    AssertTrue(flags.TbcFieldDecoder.ChromaFieldOptions!.DisableBurstHsync);
}

[Fact(DisplayName = "decode session factory applies sharpness options")]
public void DecodeSessionFactoryAppliesSharpnessOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]));

    DecodeSession sharp = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--sharpness",
        "50",
        "input.u8",
        "out"
    ]));

    JsonElement lowBand = sharp.Parameters.RfParams.GetProperty("video_eq").GetProperty("loband");
    AssertEqual(null, plain.FilterOptions.SharpnessEq);
    AssertClose(0.5, sharp.FilterOptions.SharpnessEq!.Level, 1e-12);
    AssertClose(JsonDouble(lowBand, "corner"), sharp.FilterOptions.SharpnessEq.CornerHz, 1e-12);
    AssertClose(JsonDouble(lowBand, "transition"), sharp.FilterOptions.SharpnessEq.TransitionHz, 1e-12);
    AssertEqual(JsonInt(lowBand, "order_limit"), sharp.FilterOptions.SharpnessEq.OrderLimit);
}

[Fact(DisplayName = "decode session factory applies nonlinear deemphasis options")]
public void DecodeSessionFactoryAppliesNonlinearDeemphasisOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]));

    DecodeSession nld = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--nld",
        "input.u8",
        "out"
    ]));

    AssertEqual(null, plain.FilterOptions.NonlinearDeemphasis);
    AssertClose(JsonDouble(nld.Parameters.RfParams, "nonlinear_highpass_freq"), nld.FilterOptions.NonlinearDeemphasis!.HighPassHz, 1e-12);
    AssertClose(JsonDouble(nld.Parameters.RfParams, "nonlinear_highpass_limit_l"), nld.FilterOptions.NonlinearDeemphasis.LimitLow, 1e-12);
    AssertClose(JsonDouble(nld.Parameters.RfParams, "nonlinear_highpass_limit_h"), nld.FilterOptions.NonlinearDeemphasis.LimitHigh, 1e-12);
    AssertEqual(1, nld.FilterOptions.NonlinearDeemphasis.Order);
}

[Fact(DisplayName = "decode session factory applies sub-deemphasis options")]
public void DecodeSessionFactoryAppliesSubDeemphasisOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--ts",
        "sp",
        "input.u8",
        "out"
    ]));

    DecodeSession explicitSub = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--sd",
        "input.u8",
        "out"
    ]));

    DecodeSession formatEnabled = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--ts",
        "lp",
        "input.u8",
        "out"
    ]));

    AssertEqual(null, plain.FilterOptions.SubDeemphasis);
    AssertClose(JsonDouble(explicitSub.Parameters.RfParams, "nonlinear_highpass_freq"), explicitSub.FilterOptions.SubDeemphasis!.HighPassHz, 1e-12);
    AssertClose(700_000.0, explicitSub.FilterOptions.SubDeemphasis.AmplitudeLowPassHz, 1e-12);
    double expectedScaling = explicitSub.Parameters.RfParams.TryGetProperty("nonlinear_exp_scaling", out JsonElement scaling)
        ? scaling.GetDouble()
        : 0.25;
    AssertClose(expectedScaling, explicitSub.FilterOptions.SubDeemphasis.ExponentialScaling, 1e-12);
    AssertTrue(formatEnabled.FilterOptions.SubDeemphasis is not null);
}

[Fact(DisplayName = "decode session factory applies y-comb options")]
public void DecodeSessionFactoryAppliesYCombOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]));

    DecodeSession explicitComb = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--y_comb",
        "2.0",
        "input.u8",
        "out"
    ]));

    DecodeSession flagOnlyComb = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out",
        "--y_comb"
    ]));

    AssertClose(0.0, plain.TbcRenderer.YCombLimitHz, 1e-12);
    AssertClose(JsonDouble(explicitComb.Parameters.SysParams, "hz_ire") * 2.0, explicitComb.TbcRenderer.YCombLimitHz, 1e-12);
    AssertClose(JsonDouble(flagOnlyComb.Parameters.SysParams, "hz_ire") * 1.5, flagOnlyComb.TbcRenderer.YCombLimitHz, 1e-12);
}

[Fact(DisplayName = "decode session factory applies ire0 adjust options")]
public void DecodeSessionFactoryAppliesIre0AdjustOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]));
    DecodeSession flagOnly = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--ire0_adjust",
        "input.u8",
        "out"
    ]));
    DecodeSession explicitPal = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--ire0_adjust",
        "hsync,backporch",
        "input.u8",
        "out"
    ]));

    AssertEqual(null, plain.TbcRenderer.Ire0Adjust);
    AssertTrue(flagOnly.TbcRenderer.Ire0Adjust!.BackPorch);
    AssertFalse(flagOnly.TbcRenderer.Ire0Adjust.HSync);
    AssertEqual(74, flagOnly.TbcRenderer.Ire0Adjust.BackPorchStart);
    AssertEqual(124, flagOnly.TbcRenderer.Ire0Adjust.BackPorchEnd);
    AssertTrue(explicitPal.TbcRenderer.Ire0Adjust!.BackPorch);
    AssertTrue(explicitPal.TbcRenderer.Ire0Adjust.HSync);
    AssertEqual(96, explicitPal.TbcRenderer.Ire0Adjust.BackPorchStart);
    AssertEqual(160, explicitPal.TbcRenderer.Ire0Adjust.BackPorchEnd);
}

[Fact(DisplayName = "decode session factory applies export raw TBC options")]
public void DecodeSessionFactoryAppliesExportRawTbcOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]));
    DecodeSession raw = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--export_raw_tbc",
        "input.u8",
        "out"
    ]));

    AssertFalse(plain.FilterOptions.ExportRawTbc);
    AssertFalse(plain.TbcRenderer.ExportRawTbc);
    AssertTrue(raw.FilterOptions.ExportRawTbc);
    AssertTrue(raw.TbcRenderer.ExportRawTbc);
}

[Fact(DisplayName = "decode session factory applies track phase options")]
public void DecodeSessionFactoryAppliesTrackPhaseOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]));
    DecodeSession tracked = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--pal",
        "--tf",
        "VHSHQ",
        "--track_phase",
        "1",
        "input.u8",
        "out"
    ]));
    DecodeSession secamIgnored = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--system",
        "MESECAM",
        "--track_phase",
        "2",
        "input.u8",
        "out"
    ]));

    AssertEqual(null, plain.TbcRenderer.TrackPhaseIre0Offset);
    AssertEqual(1, tracked.TbcRenderer.TrackPhaseIre0Offset!.TrackPhase);
    AssertClose(7812.5, tracked.TbcRenderer.TrackPhaseIre0Offset.Offset0Hz, 1e-12);
    AssertClose(0.0, tracked.TbcRenderer.TrackPhaseIre0Offset.Offset1Hz, 1e-12);
    AssertEqual<int?>(1, tracked.TbcFieldDecoder.ChromaFieldOptions!.InitialChromaRotationIndex);
    AssertEqual<int?>(1, (int?)PrivateFieldValue(tracked.TbcFieldDecoder, "_chromaRotationIndex"));
    AssertEqual<int?>(null, plain.TbcFieldDecoder.ChromaFieldOptions!.InitialChromaRotationIndex);
    AssertEqual(null, secamIgnored.TbcRenderer.TrackPhaseIre0Offset);
    AssertThrows<ArgumentException>(() => DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--track_phase",
        "2",
        "input.u8",
        "out"
    ])));
}

[Fact(DisplayName = "decode session factory applies wow interpolation options")]
public void DecodeSessionFactoryAppliesWowInterpolationOptions()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "input.u8",
        "out"
    ]));
    DecodeSession vhsCubic = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--wow_interpolation_method",
        "cubic",
        "input.u8",
        "out"
    ]));
    DecodeSession cvbsQuadratic = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--wow_interpolation_method",
        "quadratic",
        "input.s16",
        "out"
    ]));
    DecodeSession ldCubic = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--wow_interpolation_method",
        "cubic",
        "input.s16",
        "out"
    ]));
    DecodeSession vhsSmoothing = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--wow_level_adjust_smoothing",
        "4",
        "input.u8",
        "out"
    ]));

    AssertEqual(TbcLineInterpolationMethod.Linear, plain.TbcRenderer.InterpolationMethod);
    AssertClose(JsonDouble(plain.Parameters.SysParams, "frame_lines") / 2.0, plain.TbcRenderer.WowLevelAdjustSmoothing, 1e-12);
    AssertEqual(TbcLineInterpolationMethod.Cubic, vhsCubic.TbcRenderer.InterpolationMethod);
    AssertEqual(TbcLineInterpolationMethod.Quadratic, cvbsQuadratic.TbcRenderer.InterpolationMethod);
    AssertClose(0.0, cvbsQuadratic.TbcRenderer.WowLevelAdjustSmoothing, 1e-12);
    AssertEqual(TbcLineInterpolationMethod.Cubic, ldCubic.TbcRenderer.InterpolationMethod);
    AssertClose(0.0, ldCubic.TbcRenderer.WowLevelAdjustSmoothing, 1e-12);
    AssertClose(4.0, vhsSmoothing.TbcRenderer.WowLevelAdjustSmoothing, 1e-12);
}

[Fact(DisplayName = "decode session factory applies field-order options")]
public void DecodeSessionFactoryAppliesFieldOrderOptions()
{
    DecodeSession explicitDrop = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--field_order_confidence",
        "125",
        "--field_order_action",
        "drop",
        "input.u8",
        "out"
    ]));

    AssertEqual(TbcFieldOrderAction.Drop, explicitDrop.FieldOrderOptions.Action);
    AssertEqual(100, explicitDrop.FieldOrderOptions.Confidence);
    AssertTrue(explicitDrop.FieldOrderOptions.AllowProgressiveFlip);

    DecodeSession negativeConfidence = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--field_order_confidence",
        "-10",
        "input.u8",
        "out"
    ]));
    AssertEqual(0, negativeConfidence.FieldOrderOptions.Confidence);

    DecodeSession typeC = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--tf",
        "TYPEC",
        "--field_order_action",
        "duplicate",
        "input.u8",
        "out"
    ]));

    AssertEqual(TbcFieldOrderAction.None, typeC.FieldOrderOptions.Action);
    AssertEqual(100, typeC.FieldOrderOptions.Confidence);
    AssertFalse(typeC.FieldOrderOptions.AllowProgressiveFlip);
}

[Fact(DisplayName = "decode session factory applies execution options")]
public void DecodeSessionFactoryAppliesExecutionOptions()
{
    DecodeSession vhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--threads",
        "7",
        "--write_db",
        "--debug",
        "--debug_plot",
        "plot.json",
        "input.u8",
        "outbase"
    ]));

    AssertEqual(7, vhs.ExecutionOptions.RequestedThreads);
    AssertEqual(0, vhs.ExecutionOptions.WorkerThreads);
    AssertEqual(0, vhs.StreamDecoder.WorkerThreads);
    AssertEqual(0, vhs.StreamDecoder.PrefetchBlocks);
    AssertEqual(-1, vhs.ExecutionOptions.SeekFrame);
    AssertTrue(vhs.ExecutionOptions.WriteDebugData);
    AssertTrue(vhs.ExecutionOptions.Debug);
    AssertEqual("plot.json", vhs.ExecutionOptions.DebugPlotPath);
    AssertFalse(vhs.ExecutionOptions.IgnoreLeadOut);
    AssertFalse(vhs.ExecutionOptions.VerboseVits);
    AssertFalse(vhs.ExecutionOptions.UseProfiler);
    AssertFalse(vhs.ExecutionOptions.CxAdcCompatibilityMode);

    DecodeSession cvbs = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--threads",
        "3",
        "--seek",
        "42",
        "input.u8",
        "outbase"
    ]));

    AssertEqual(3, cvbs.ExecutionOptions.RequestedThreads);
    AssertEqual(3, cvbs.ExecutionOptions.WorkerThreads);
    AssertEqual(3, cvbs.StreamDecoder.WorkerThreads);
    AssertEqual(0, cvbs.StreamDecoder.PrefetchBlocks);
    AssertEqual(42, cvbs.ExecutionOptions.SeekFrame);

    DecodeSession ld = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--threads",
        "2",
        "--seek",
        "123",
        "--ignoreleadout",
        "--verboseVITS",
        "--use_profiler",
        "input.s16",
        "outbase"
    ]));

    AssertEqual(2, ld.ExecutionOptions.RequestedThreads);
    AssertEqual(2, ld.ExecutionOptions.WorkerThreads);
    AssertEqual(2, ld.StreamDecoder.WorkerThreads);
    AssertEqual(0, ld.StreamDecoder.PrefetchBlocks);
    AssertEqual(123, ld.ExecutionOptions.SeekFrame);
    AssertTrue(ld.ExecutionOptions.IgnoreLeadOut);
    AssertTrue(ld.ExecutionOptions.VerboseVits);
    AssertTrue(ld.ExecutionOptions.UseProfiler);

    DecodeSession cxadc = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--cxadc",
        "--no_resample",
        "input.u8",
        "outbase"
    ]));
    AssertTrue(cxadc.ExecutionOptions.CxAdcCompatibilityMode);

    DecodeSession parallelVhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--threads",
        "7",
        "--no_resample",
        "input.s16",
        "outbase"
    ]));
    AssertEqual(
        Math.Min(7, Math.Min(Environment.ProcessorCount, RfBlockStreamDecoder.MaximumPrefetchBlocks)),
        parallelVhs.StreamDecoder.PrefetchBlocks);
}

[Fact(DisplayName = "decode session applies VHS params file overrides")]
public void DecodeSessionAppliesVhsParamsFileOverrides()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string paramsFile = Path.Combine(tempDirectory, "params.json");
        File.WriteAllText(paramsFile, """
            {
              "sys_params": {
                "outlinelen": 999,
                "hz_ire": 12345.0,
                "not_a_sys_param": 1
              },
              "rf_params": {
                "video_lpf_freq": 3000000.0,
                "not_a_rf_param": true
              }
            }
            """);

        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--params_file",
            paramsFile,
            "input.u8",
            "out"
        ]));

        AssertEqual(999, JsonInt(session.Parameters.SysParams, "outlinelen"));
        AssertClose(12_345.0, JsonDouble(session.Parameters.SysParams, "hz_ire"), 1e-12);
        AssertClose(12_345.0, JsonDouble(session.Parameters.RfParams, "hz_ire"), 1e-12);
        AssertClose(3_000_000.0, JsonDouble(session.Parameters.RfParams, "video_lpf_freq"), 1e-12);
        AssertFalse(session.Parameters.SysParams.TryGetProperty("not_a_sys_param", out _));
        AssertFalse(session.Parameters.RfParams.TryGetProperty("not_a_rf_param", out _));
        AssertEqual(999, session.TbcFrameSpec.OutputLineLength);
        AssertClose(12_345.0, session.VideoOutput.HzIre, 1e-12);

        AssertThrows<ArgumentException>(() => Parse(CliSpecs.Vhs, [
            "--params_file",
            Path.Combine(tempDirectory, "missing.json")
        ]));

        TextReader originalInput = Console.In;
        try
        {
            Console.SetIn(new StringReader("{\"sys_params\":{\"ire0\":123456.0}}"));
            DecodeSession stdinSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
                "--pal",
                "--params_file",
                "-",
                "input.u8",
                "out"
            ]));
            AssertClose(123_456.0, JsonDouble(stdinSession.Parameters.SysParams, "ire0"), 1e-12);
        }
        finally
        {
            Console.SetIn(originalInput);
        }
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "decode session factory applies LD overrides")]
public void DecodeSessionFactoryAppliesLdOverrides()
{
    ParsedCommand command = Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--lowband",
        "--video_bpf_low",
        "3MHz",
        "--video_bpf_high",
        "12MHz",
        "--video_lpf",
        "6.5MHz",
        "--video_lpf_order",
        "4",
        "--deemp_low",
        "0.25",
        "--deemp_high",
        "1.5",
        "--deemp_strength",
        "0.75",
        "input.s16",
        "outbase"
    ]);

    DecodeSession session = DecodeSessionFactory.Create(command);
    AssertEqual("ld", session.Spec.Name);
    AssertEqual("PAL", session.System);
    AssertEqual(1024, session.BlockCut);
    AssertEqual(32, session.BlockCutEnd);
    AssertType<Int16SampleLoader>(session.Loader);
    AssertTrue(session.FilterOptions.LdClipDemodForVideo);
    AssertEqual(RfFmDemodulatorMode.ConjugateProduct, session.FilterOptions.FmDemodulatorMode);
    AssertClose(3_000_000.0, JsonDouble(session.Parameters.RfParams, "video_bpf_low"), 1e-6);
    AssertClose(12_000_000.0, JsonDouble(session.Parameters.RfParams, "video_bpf_high"), 1e-6);
    AssertClose(6_500_000.0, JsonDouble(session.Parameters.RfParams, "video_lpf_freq"), 1e-6);
    AssertEqual(4, JsonInt(session.Parameters.RfParams, "video_lpf_order"));
    AssertClose(1.0 / (1.5 * 1_000_000.0), session.Parameters.RfParams.GetProperty("video_deemp")[0].GetDouble(), 1e-18);
    AssertClose(1.0 / (0.25 * 1_000_000.0), session.Parameters.RfParams.GetProperty("video_deemp")[1].GetDouble(), 1e-18);
    AssertClose(0.75, JsonDouble(session.Parameters.RfParams, "video_deemp_strength"), 1e-12);

    DecodeSession explicitFortyMHz = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "-f",
        "40",
        "input.u8",
        "outbase"
    ]));
    var explicitFortyLoader = (FfmpegStreamSampleLoader)explicitFortyMHz.Loader;
    AssertStringSequence(["-f", "u8"], explicitFortyLoader.InputArguments.ToArray());
    AssertEqual(0, explicitFortyLoader.OutputArguments.Count);
    AssertThrows<NotSupportedException>(() => DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "-f",
        "40",
        "input.lds",
        "outbase"
    ])));
}

[Fact(DisplayName = "decode session factory applies LD audio options")]
public void DecodeSessionFactoryAppliesLdAudioOptions()
{
    DecodeSession defaults = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "input.s16",
        "outbase"
    ]));

    LaserDiscAudioOptions defaultAudio = defaults.LaserDiscAudioOptions!;
    AssertTrue(defaults.LaserDiscAudioOptions is not null);
    AssertTrue(defaultAudio.DecodeDigitalAudio);
    AssertTrue(defaults.Filters.LdEfm is not null);
    AssertFalse(defaultAudio.WritePreEfm);
    AssertTrue(defaultAudio.DecodeAnalogAudio);
    AssertTrue(defaults.Filters.LdAnalogAudio is not null);
    AssertTrue(defaults.Filters.LdAnalogAudio!.DecimationFactor > 1);
    AssertClose(7.5, defaults.BlackIre, 1e-12);
    AssertClose(44_100.0, defaultAudio.AnalogAudioFrequency, 1e-12);
    AssertFalse(defaultAudio.UseNtscAudioRate);
    AssertFalse(defaultAudio.Ac3);
    AssertFalse(defaultAudio.WriteRfTbc);
    AssertEqual(null, defaults.Filters.LdAc3);
    AssertTrue(defaultAudio.UseAgc);
    AssertFalse(defaultAudio.AudioFilterWidthHz.HasValue);
    var analogOutput = (LaserDiscAnalogAudioOutputOptions)PrivateFieldValue(
        defaults.TbcFieldDecoder,
        "_analogAudioOptions")!;
    AssertClose(JsonDouble(defaults.Parameters.SysParams, "FPS"), analogOutput.FramesPerSecond, 1e-12);
    AssertEqual(
        "D3311591D460B00BBC7CA2401EE502637BCEBF2F666D7D124B2B3D1EE29F53DA",
        ComplexBitsSha256(defaults.Filters.LdAnalogAudio!.Left.Stage2Filter));

    DecodeSession customAnalog = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--analog_audio_frequency",
        "48000",
        "input.s16",
        "outbase"
    ]));
    AssertClose(48_000.0, customAnalog.LaserDiscAudioOptions!.AnalogAudioFrequency, 1e-12);

    DecodeSession ntscRate = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--ntsc_audio_rate",
        "input.s16",
        "outbase"
    ]));
    AssertTrue(ntscRate.LaserDiscAudioOptions!.UseNtscAudioRate);
    AssertClose(-2.8, ntscRate.LaserDiscAudioOptions.AnalogAudioFrequency, 1e-12);

    DecodeSession palRateIgnored = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--ntsc_audio_rate",
        "input.s16",
        "outbase"
    ]));
    AssertTrue(palRateIgnored.LaserDiscAudioOptions!.UseNtscAudioRate);
    AssertClose(44_100.0, palRateIgnored.LaserDiscAudioOptions.AnalogAudioFrequency, 1e-12);

    DecodeSession toggled = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--preEFM",
        "--disable_analog_audio",
        "--AC3",
        "--RF_TBC",
        "--noAGC",
        "--audio_filterwidth",
        "150kHz",
        "input.s16",
        "outbase"
    ]));

    LaserDiscAudioOptions toggledAudio = toggled.LaserDiscAudioOptions!;
    AssertFalse(toggledAudio.DecodeDigitalAudio);
    AssertEqual(null, toggled.Filters.LdEfm);
    AssertTrue(toggledAudio.WritePreEfm);
    AssertFalse(toggledAudio.DecodeAnalogAudio);
    AssertEqual(null, toggled.Filters.LdAnalogAudio);
    AssertClose(0.0, toggledAudio.AnalogAudioFrequency, 1e-12);
    AssertTrue(toggledAudio.Ac3);
    AssertTrue(toggledAudio.WriteRfTbc);
    AssertFalse(toggledAudio.UseAgc);
    AssertClose(0.15, toggledAudio.AudioFilterWidthHz!.Value, 1e-12);
    AssertClose(JsonDouble(toggled.Parameters.SysParams, "audio_rfreq_AC3"), JsonDouble(toggled.Parameters.SysParams, "audio_rfreq"), 1e-9);
    AssertTrue(toggled.Parameters.SysParams.GetProperty("AC3").GetBoolean());
    AssertClose(0.15, JsonDouble(toggled.Parameters.RfParams, "audio_filterwidth"), 1e-12);
    AssertTrue(toggled.Filters.LdAc3 is not null);
    AssertTrue(toggled.Filters.LdAc3Magnitude is not null);
    int ac3CarrierBin = FrequencyBin(JsonDouble(toggled.Parameters.SysParams, "audio_rfreq_AC3"), toggled.DecodeSampleRateHz, toggled.BlockLength);
    AssertTrue(toggled.Filters.LdAc3Magnitude![ac3CarrierBin] > 0.5);

    DecodeSession bareAudioFilterWidth = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--audio_filterwidth",
        "150000",
        "input.s16",
        "outbase"
    ]));
    AssertClose(150_000.0, bareAudioFilterWidth.LaserDiscAudioOptions!.AudioFilterWidthHz!.Value, 1e-9);
    AssertClose(150_000.0, JsonDouble(bareAudioFilterWidth.Parameters.RfParams, "audio_filterwidth"), 1e-9);

    DecodeSession ac3Only = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "--AC3",
        "input.s16",
        "outbase"
    ]));
    AssertTrue(ac3Only.LaserDiscAudioOptions!.Ac3);
    AssertFalse(ac3Only.LaserDiscAudioOptions.WriteRfTbc);
    AssertTrue(ac3Only.Filters.LdAc3 is not null);
}

[Fact(DisplayName = "decode session factory applies LD analog audio notch state")]
public void DecodeSessionFactoryAppliesLdAnalogAudioNotchState()
{
    DecodeSession ntsc = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "input.s16",
        "outbase"
    ]));
    AssertTrue(ntsc.Parameters.SysParams.GetProperty("analog_audio").GetBoolean());
    AssertFalse(ntsc.Parameters.SysParams.GetProperty("AC3").GetBoolean());
    AssertTrue(ntsc.FilterOptions.LdNtscAnalogAudioNotch);

    DecodeSession ntscDisabledAnalogOutput = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--disable_analog_audio",
        "input.s16",
        "outbase"
    ]));
    AssertFalse(ntscDisabledAnalogOutput.LaserDiscAudioOptions!.DecodeAnalogAudio);
    AssertTrue(ntscDisabledAnalogOutput.Parameters.SysParams.GetProperty("analog_audio").GetBoolean());
    AssertTrue(ntscDisabledAnalogOutput.FilterOptions.LdNtscAnalogAudioNotch);

    DecodeSession pal = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "input.s16",
        "outbase"
    ]));
    AssertTrue(pal.Parameters.SysParams.GetProperty("analog_audio").GetBoolean());
    AssertFalse(pal.FilterOptions.LdNtscAnalogAudioNotch);

    DecodeSession palDisabledAnalogOutput = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--disable_analog_audio",
        "input.s16",
        "outbase"
    ]));
    AssertFalse(palDisabledAnalogOutput.Parameters.SysParams.GetProperty("analog_audio").GetBoolean());
    AssertFalse(palDisabledAnalogOutput.FilterOptions.LdNtscAnalogAudioNotch);
}

[Fact(DisplayName = "decode session factory applies LD NTSC color notch")]
public void DecodeSessionFactoryAppliesLdNtscColorNotch()
{
    DecodeSession plain = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "input.s16",
        "outbase"
    ]), blockLength: 4096);

    DecodeSession notch = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--NTSC_color_notch_filter",
        "input.s16",
        "outbase"
    ]), blockLength: 4096);

    AssertFalse(plain.FilterOptions.LdNtscColorNotch);
    AssertTrue(notch.FilterOptions.LdNtscColorNotch);
    double notchCenterHz = Math.Sqrt(JsonDouble(notch.Parameters.RfParams, "video_lpf_freq") * 5_000_000.0);
    int colorWobbleBin = (int)Math.Round(notchCenterHz / notch.DecodeSampleRateHz * notch.BlockLength);
    AssertTrue(notch.Filters.VideoLowPassMagnitude[colorWobbleBin] < plain.Filters.VideoLowPassMagnitude[colorWobbleBin] * 0.1);
    AssertTrue(notch.Filters.VideoMagnitude[colorWobbleBin] < plain.Filters.VideoMagnitude[colorWobbleBin] * 0.1);

    DecodeSession palIgnored = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--NTSC_color_notch_filter",
        "input.s16",
        "outbase"
    ]), blockLength: 4096);

    AssertFalse(palIgnored.FilterOptions.LdNtscColorNotch);
}

[Fact(DisplayName = "decode session factory applies LD PAL V4300D notch")]
public void DecodeSessionFactoryAppliesLdPalV4300DNotch()
{
    DecodeSession pal = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--V4300D_notch_filter",
        "input.s16",
        "outbase"
    ]));

    AssertTrue(pal.FilterOptions.LdPalV4300DNotch);
    AssertFalse(pal.FilterOptions.LdNtscColorNotch);

    DecodeSession ntscIgnored = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--V4300D_notch_filter",
        "input.s16",
        "outbase"
    ]));

    AssertFalse(ntscIgnored.FilterOptions.LdPalV4300DNotch);
}

[Fact(DisplayName = "decode session factory applies LD MTF options")]
public void DecodeSessionFactoryAppliesLdMtfOptions()
{
    DecodeSession defaultMtf = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "input.s16",
        "outbase"
    ]), blockLength: 4096);

    DecodeSession disabledMtf = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--MTF",
        "0",
        "input.s16",
        "outbase"
    ]), blockLength: 4096);

    DecodeSession customMtf = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--MTF",
        "0.5",
        "--MTF_offset",
        "0.25",
        "input.s16",
        "outbase"
    ]), blockLength: 4096);

    AssertClose(1.0, defaultMtf.FilterOptions.LdMtfLevel!.Value, 1e-12);
    AssertClose(0.0, defaultMtf.FilterOptions.LdMtfOffset, 1e-12);
    AssertClose(0.0, disabledMtf.FilterOptions.LdMtfLevel!.Value, 1e-12);
    AssertClose(0.5, customMtf.FilterOptions.LdMtfLevel!.Value, 1e-12);
    AssertClose(0.25, customMtf.FilterOptions.LdMtfOffset, 1e-12);

    int mtfBin = (int)Math.Round(JsonDouble(defaultMtf.Parameters.RfParams, "MTF_freq") * 1_000_000.0 / defaultMtf.DecodeSampleRateHz * defaultMtf.BlockLength);
    AssertTrue(defaultMtf.Filters.RfMtfMagnitude[mtfBin] > disabledMtf.Filters.RfMtfMagnitude[mtfBin]);
    AssertClose(1.0, disabledMtf.Filters.RfMtfMagnitude[mtfBin], 1e-12);
    AssertTrue(customMtf.Filters.RfMtfMagnitude[mtfBin] > disabledMtf.Filters.RfMtfMagnitude[mtfBin]);
    AssertTrue(customMtf.Filters.RfMtfMagnitude[mtfBin] < defaultMtf.Filters.RfMtfMagnitude[mtfBin]);
}

[Fact(DisplayName = "decode session factory wires LD field refiners")]
public void DecodeSessionFactoryWiresLdFieldRefiners()
{
    DecodeSession ntsc = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "outbase"
    ]));

    object? ntscPilot = PrivateFieldValue(ntsc.TbcFieldDecoder, "_laserDiscPilotRefineOptions");
    object? ntscBurst = PrivateFieldValue(ntsc.TbcFieldDecoder, "_laserDiscNtscBurstRefineOptions");
    AssertTrue(ntscPilot is null);
    if (ntscBurst is not LaserDiscNtscBurstRefineOptions ntscBurstOptions)
    {
        throw new Exception($"Expected LD NTSC burst refine options, got {ntscBurst?.GetType().Name ?? "null"}.");
    }

    AssertClose(JsonDouble(ntsc.Parameters.SysParams, "fsc_mhz"), ntscBurstOptions.FscMHz, 1e-12);

    DecodeSession pal = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "outbase"
    ]));

    object? palPilot = PrivateFieldValue(pal.TbcFieldDecoder, "_laserDiscPilotRefineOptions");
    object? palBurst = PrivateFieldValue(pal.TbcFieldDecoder, "_laserDiscNtscBurstRefineOptions");
    AssertTrue(palBurst is null);
    if (palPilot is not LaserDiscPilotRefineOptions palPilotOptions)
    {
        throw new Exception($"Expected LD PAL pilot refine options, got {palPilot?.GetType().Name ?? "null"}.");
    }

    AssertClose(JsonDouble(pal.Parameters.SysParams, "pilot_mhz"), palPilotOptions.PilotMHz, 1e-12);

    DecodeSession cvbsNtsc = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--ntsc",
        "input.s16",
        "outbase"
    ]));
    AssertTrue(PrivateFieldValue(cvbsNtsc.TbcFieldDecoder, "_laserDiscPilotRefineOptions") is null);
    AssertType<LaserDiscNtscBurstRefineOptions>(
        PrivateFieldValue(cvbsNtsc.TbcFieldDecoder, "_laserDiscNtscBurstRefineOptions")!);
    AssertTrue(cvbsNtsc.Filters.CvbsVideoBurst is not null);

    DecodeSession cvbsPal = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--pal",
        "input.s16",
        "outbase"
    ]));
    AssertTrue(PrivateFieldValue(cvbsPal.TbcFieldDecoder, "_laserDiscPilotRefineOptions") is null);
    AssertTrue(PrivateFieldValue(cvbsPal.TbcFieldDecoder, "_laserDiscNtscBurstRefineOptions") is null);
    AssertTrue(cvbsPal.Filters.CvbsVideoBurst is not null);

    DecodeSession cvbsPalM = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--palm",
        "input.s16",
        "outbase"
    ]));
    AssertTrue(PrivateFieldValue(cvbsPalM.TbcFieldDecoder, "_laserDiscNtscBurstRefineOptions") is null);
    AssertEqual<int?>(1, TbcFieldDecodePipeline.CvbsFallbackFieldPhaseId("cvbs", "PAL", fieldNumber: 0));
    AssertEqual<int?>(8, TbcFieldDecodePipeline.CvbsFallbackFieldPhaseId("cvbs", "PAL", fieldNumber: 7));
    AssertEqual<int?>(1, TbcFieldDecodePipeline.CvbsFallbackFieldPhaseId("cvbs", "PAL", fieldNumber: 8));
    AssertEqual<int?>(0, TbcFieldDecodePipeline.CvbsFallbackFieldPhaseId("cvbs", "PAL_M", fieldNumber: 3));
    AssertEqual<int?>(0, TbcFieldDecodePipeline.CvbsFallbackFieldPhaseId("cvbs", "NLINHA", fieldNumber: 3));
    AssertEqual<int?>(null, TbcFieldDecodePipeline.CvbsFallbackFieldPhaseId("cvbs", "NTSC", fieldNumber: 3));
    AssertEqual<int?>(null, TbcFieldDecodePipeline.CvbsFallbackFieldPhaseId("ld", "PAL", fieldNumber: 3));
}

[Fact(DisplayName = "decode run bounds honor start length and fileloc")]
public void DecodeRunBoundsHonorStartLengthAndFileloc()
{
    DecodeSession vhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--pal", "--start", "3", "--length", "4", "input.u8", "out"]));
    int nominalVhsField = vhs.TbcFieldDecoder.EstimateNominalFieldSampleCount();
    AssertEqual(
        (int)(vhs.DecodeSampleRateHz / (JsonDouble(vhs.Parameters.SysParams, "FPS") * 2.0)) + 1,
        nominalVhsField);
    AssertEqual(3L * 2L * nominalVhsField, vhs.RunBounds.StartSample);
    AssertEqual(8, vhs.RunBounds.RequestedFieldCount);

    DecodeSession fileLoc = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--start", "9", "--start_fileloc", "12345.9", "--length", "1", "input.u8", "out"]));
    AssertEqual(12345L, fileLoc.RunBounds.StartSample);
    AssertEqual(2, fileLoc.RunBounds.RequestedFieldCount);

    DecodeSession negativeFileLoc = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--start", "9", "--start_fileloc", "-2", "input.u8", "out"]));
    AssertEqual(0L, negativeFileLoc.RunBounds.StartSample);

    DecodeSession ld = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, ["--PAL", "--start", "1.5", "--length", "2", "input.s16", "out"]));
    int nominalLdField = ld.TbcFieldDecoder.EstimateNominalFieldSampleCount();
    AssertEqual(
        (int)(ld.DecodeSampleRateHz / (JsonDouble(ld.Parameters.SysParams, "FPS") * 2.0)) + 1,
        nominalLdField);
    AssertEqual((long)Math.Floor(1.5 * 2.0 * nominalLdField), ld.RunBounds.StartSample);
    AssertEqual(4, ld.RunBounds.RequestedFieldCount);

    DecodeSession zeroLength = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--length", "0", "input.u8", "out"]));
    AssertEqual(0, new TbcFieldSequenceDecodeEngine().DecodeFields(zeroLength, Stream.Null).Count);
}

[Fact(DisplayName = "LD read windows match upstream block alignment")]
public void LaserDiscReadWindowsMatchUpstreamBlockAlignment()
{
    DecodeSession ntsc = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "out"
    ]));

    AssertEqual(1_024, ntsc.BlockCut);
    AssertEqual(32, ntsc.BlockCutEnd);
    AssertEqual(31_712, ntsc.StreamDecoder.BlockStride);

    int ntscRequest = DecodeReadWindowPlanner.EstimateReadSampleCount(ntsc, extraReadLines: 3);
    AssertEqual(950_272, ntscRequest);
    AssertEqual(new DecodeReadWindow(0, 951_360), DecodeReadWindowPlanner.Resolve(ntsc, 0, ntscRequest));
    AssertEqual(
        new DecodeReadWindow(665_952, 983_072),
        DecodeReadWindowPlanner.Resolve(ntsc, 700_000, ntscRequest));

    DecodeSession pal = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "out"
    ]));

    int palRequest = DecodeReadWindowPlanner.EstimateReadSampleCount(pal, extraReadLines: 3);
    AssertEqual(1_081_344, palRequest);
    AssertEqual(new DecodeReadWindow(0, 1_109_920), DecodeReadWindowPlanner.Resolve(pal, 0, palRequest));

    DecodeSession vhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["input.u8", "out"]));
    int vhsRequest = DecodeReadWindowPlanner.EstimateReadSampleCount(vhs, extraReadLines: 3);
    AssertEqual(950_272, vhsRequest);
    AssertEqual(new DecodeReadWindow(0, 952_320), DecodeReadWindowPlanner.Resolve(vhs, 123, vhsRequest));

    DecodeSession palVhs = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--pal", "input.u8", "out"]));
    AssertEqual(1_081_344, DecodeReadWindowPlanner.EstimateReadSampleCount(palVhs, extraReadLines: 0));

    DecodeSession system819 = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--system",
        "819",
        "--tape_format",
        "QUADRUPLEX",
        "input.u8",
        "out"
    ]));
    AssertEqual(1_015_808, DecodeReadWindowPlanner.EstimateReadSampleCount(system819, extraReadLines: 99));
    AssertTrue(PrivateFieldValue(system819.TbcFieldDecoder, "_vsyncSerrationDetector") is null);

    AssertThrows<ArgumentOutOfRangeException>(() => DecodeReadWindowPlanner.EstimateReadSampleCount(ntsc, -1));
    AssertThrows<ArgumentOutOfRangeException>(() => DecodeReadWindowPlanner.Resolve(ntsc, -1, ntscRequest));
    AssertThrows<ArgumentOutOfRangeException>(() => DecodeReadWindowPlanner.Resolve(ntsc, 0, -1));
}

[Fact(DisplayName = "decode output preflight rejects conflicting outputs")]
public void DecodeOutputPreflightRejectsConflictingOutputs()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.u8");
        string outputBase = Path.Combine(tempDirectory, "capture");
        File.WriteAllBytes(inputPath, [1, 2, 3]);
        File.WriteAllBytes(outputBase + "_chroma.tbc", []);

        ParsedCommand conflict = Parse(CliSpecs.Vhs, [inputPath, outputBase]);
        IReadOnlyList<string> conflicts = DecodeOutputPreflight.FindExistingOutputConflicts(conflict);
        AssertEqual(1, conflicts.Count);
        AssertEqual(outputBase + "_chroma.tbc", conflicts[0]);
        AssertThrows<ArgumentException>(() => DecodeOutputPreflight.Validate(conflict));
        var conflictOutput = new StringWriter();
        AssertEqual(1, new DecodeRunner().Run(
            conflict,
            conflictOutput,
            TextWriter.Null,
            TestContext.Current.CancellationToken));
        AssertEqual(
            "Existing decode files found, remove them or run command with --overwrite" + Environment.NewLine
            + "\t " + outputBase + "_chroma.tbc" + Environment.NewLine,
            conflictOutput.ToString());

        ParsedCommand overwrite = Parse(CliSpecs.Vhs, ["--overwrite", inputPath, outputBase]);
        AssertEqual(0, DecodeOutputPreflight.FindExistingOutputConflicts(overwrite).Count);
        DecodeOutputPreflight.Validate(overwrite);

        ParsedCommand sameLdf = Parse(CliSpecs.LaserDisc, ["--write-test-ldf", inputPath, inputPath, outputBase]);
        AssertThrows<ArgumentException>(() => DecodeOutputPreflight.Validate(sameLdf));

        string missingDirectoryOutput = Path.Combine(tempDirectory, "missing", "capture");
        ParsedCommand missingVhsOutputDirectory = Parse(CliSpecs.Vhs, [inputPath, missingDirectoryOutput]);
        AssertThrows<ArgumentException>(() => DecodeOutputPreflight.Validate(missingVhsOutputDirectory));
        ParsedCommand missingCvbsOutputDirectory = Parse(CliSpecs.Cvbs, [inputPath, missingDirectoryOutput]);
        AssertThrows<ArgumentException>(() => DecodeOutputPreflight.Validate(missingCvbsOutputDirectory));

        ParsedCommand missingInput = Parse(CliSpecs.Vhs, [Path.Combine(tempDirectory, "missing.u8"), outputBase]);
        AssertThrows<ArgumentException>(() => DecodeOutputPreflight.Validate(missingInput));

        ParsedCommand stdinInput = Parse(CliSpecs.Vhs, ["-", Path.Combine(tempDirectory, "stdin-capture")]);
        DecodeOutputPreflight.Validate(stdinInput);

        string ldOutputBase = Path.Combine(tempDirectory, "ld-capture");
        File.WriteAllBytes(ldOutputBase + ".pcm", []);
        ParsedCommand ldNoConflict = Parse(CliSpecs.LaserDisc, [inputPath, ldOutputBase]);
        AssertEqual(0, DecodeOutputPreflight.FindExistingOutputConflicts(ldNoConflict).Count);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "decode runner handles empty native decodes")]
public void DecodeRunnerHandlesEmptyNativeDecodes()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string nativeInput = Path.Combine(tempDirectory, "input.u8");
        File.WriteAllBytes(nativeInput, [1, 2, 3, 4]);
        string nativeBase = Path.Combine(tempDirectory, "native");
        ParsedCommand native = Parse(CliSpecs.Vhs, ["--pal", nativeInput, nativeBase]);
        var nativeError = new StringWriter();
        int nativeExit = new DecodeRunner().Run(
            native,
            TextWriter.Null,
            nativeError,
            TestContext.Current.CancellationToken);
        AssertEqual(0, nativeExit);
        AssertContains(nativeError.ToString(), "Completed without handling any frames.");
        AssertFalse(nativeError.ToString().Contains("Input ended before enough samples were available", StringComparison.Ordinal));
        AssertFalse(nativeError.ToString().Contains("Initialized vhs decode session", StringComparison.Ordinal));
        AssertFalse(nativeError.ToString().Contains("The .NET decode engine is not complete yet", StringComparison.Ordinal));
        AssertEqual(0L, new FileInfo(nativeBase + ".tbc").Length);
        AssertEqual(0L, new FileInfo(nativeBase + "_chroma.tbc").Length);
        AssertFalse(File.Exists(nativeBase + ".tbc.json"));
        AssertEqual("{", File.ReadAllText(nativeBase + ".tbc.json.tmp"));
        string nativeLog = File.ReadAllText(nativeBase + ".log");
        AssertFalse(nativeLog.Contains("vhs-decode-dotnet", StringComparison.Ordinal));
        AssertContains(nativeLog, " - lddecode - DEBUG - Sys Parameters:");
        AssertContains(nativeLog, " - lddecode - DEBUG - RF Parameters:");

        string cxadcBase = Path.Combine(tempDirectory, "cxadc");
        var cxadcError = new StringWriter();
        int cxadcExit = new DecodeRunner().Run(
            Parse(CliSpecs.Vhs, ["--pal", "--cxadc", "--no_resample", nativeInput, cxadcBase]),
            TextWriter.Null,
            cxadcError,
            TestContext.Current.CancellationToken);
        AssertEqual(0, cxadcExit);
        AssertContains(cxadcError.ToString(), "--cxadc is deprecated! use -f 8fsc instead!");
        string cxadcLog = File.ReadAllText(cxadcBase + ".log");
        int cxadcWarningIndex = cxadcLog.IndexOf(
            " - lddecode - WARNING - --cxadc is deprecated! use -f 8fsc instead!",
            StringComparison.Ordinal);
        int cxadcSysIndex = cxadcLog.IndexOf(" - lddecode - DEBUG - Sys Parameters:", StringComparison.Ordinal);
        AssertTrue(cxadcWarningIndex >= 0);
        AssertTrue(cxadcWarningIndex < cxadcSysIndex);

        string ldInput = Path.Combine(tempDirectory, "input.s16");
        File.WriteAllBytes(ldInput, [0, 0, 1, 0]);
        string ldBase = Path.Combine(tempDirectory, "ld-pal");
        var ldPalWarning = new StringWriter();
        int ldExit = new DecodeRunner().Run(
            Parse(CliSpecs.LaserDisc, ["--PAL", "--ntsc_audio_rate", ldInput, ldBase]),
            TextWriter.Null,
            ldPalWarning,
            TestContext.Current.CancellationToken);
        AssertEqual(0, ldExit);
        AssertContains(ldPalWarning.ToString(), "WARNING: --ntsc_audio_rate ignored for PAL (audio is already frame-locked at 44100hz)");
        AssertContains(ldPalWarning.ToString(), "Completed without handling any frames.");
        AssertEqual(0L, new FileInfo(ldBase + ".tbc").Length);
        AssertEqual(0L, new FileInfo(ldBase + ".efm").Length);
        AssertEqual(0L, new FileInfo(ldBase + ".pcm").Length);
        AssertFalse(File.Exists(ldBase + ".tbc.json"));
        AssertEqual("{", File.ReadAllText(ldBase + ".tbc.json.tmp"));
        string ldDbPath = ldBase + ".tbc.db";
        AssertTrue(File.Exists(ldDbPath));
        AssertEqual(0L, SqliteLong(ldDbPath, "SELECT COUNT(*) FROM capture"));
        AssertEqual(0L, SqliteLong(ldDbPath, "SELECT COUNT(*) FROM field_record"));
        AssertContains(
            File.ReadAllText(ldBase + ".log"),
            " - lddecode - DEBUG - ld-decode version vhs_decode:g4315520");

        string resamplingBase = Path.Combine(tempDirectory, "resampling");
        ParsedCommand resamplingMissingInput = Parse(CliSpecs.Vhs, ["-f", "8fsc", Path.Combine(tempDirectory, "missing.u8"), resamplingBase]);
        var missingInputOutput = new StringWriter();
        AssertEqual(1, new DecodeRunner().Run(
            resamplingMissingInput,
            missingInputOutput,
            TextWriter.Null,
            TestContext.Current.CancellationToken));
        AssertContains(missingInputOutput.ToString(), "ERROR: input file");
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "ported complex angle matches expected quadrants")]
public void PortedComplexAngleMatchesExpectedQuadrants()
{
    double[] result = PortedMath.ComplexAngle([new Complex(1, 0), new Complex(0, 1), new Complex(-1, 0), new Complex(0, -1)]);
    AssertClose(0.0, result[0], 1e-12);
    AssertClose(Math.PI / 2, result[1], 1e-12);
    AssertClose(Math.PI, result[2], 1e-12);
    AssertClose(-Math.PI / 2, result[3], 1e-12);
}

[Fact(DisplayName = "TBC field sequence engine omits empty CVBS metadata")]
public void TbcFieldSequenceEngineOmitsEmptyCvbsMetadata()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--pal",
            "input.s16",
            Path.Combine(tempDirectory, "empty-cvbs")
        ]));
        var error = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);
        DecodeSessionLogWriter.Write(session);
        int reads = 0;
        var engine = new TbcFieldSequenceDecodeEngine(
            readField: (_, _, _, _, _) => reads++ == 0
                ? throw new TbcFieldDecodeRecoveryException(
                    TbcFieldDecodeRecoveryKind.NoSyncPulses,
                    40_000_000,
                    "no sync",
                    stopAfterDecodedFields: true)
                : null);

        TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(session, Stream.Null);

        AssertTrue(result.Success);
        AssertEqual(2, reads);
        AssertEqual(0, result.WrittenFieldCount);
        AssertEqual(
            "Unable to find any sync pulses, skipping one second" + Environment.NewLine,
            error.ToString());
        AssertContains(
            File.ReadAllText(session.OutputBase + ".log"),
            " - lddecode - ERROR - Unable to find any sync pulses, skipping one second");
        AssertTrue(File.Exists(result.Paths!.TbcPath));
        AssertEqual(0L, new FileInfo(result.Paths.TbcPath).Length);
        AssertFalse(File.Exists(result.Paths.JsonPath));
        AssertEqual("{", File.ReadAllText(result.Paths.JsonPath + ".tmp"));
        AssertFalse(File.Exists(result.Paths.JsonPath + ".fields.tmp"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "FFT round-trips complex samples")]
public void FftRoundTripsComplexSamples()
{
    Complex[] input =
    [
        new Complex(1, 2),
        new Complex(3, -1),
        new Complex(0, 0.5),
        new Complex(-2, 4),
        new Complex(5, 0),
        new Complex(-1, -1),
        new Complex(2, 2),
        new Complex(0, -3)
    ];

    Complex[] spectrum = FastFourierTransform.Forward(input);
    Complex[] roundTrip = FastFourierTransform.Inverse(spectrum);
    AssertComplexSequence(input, roundTrip, 1e-12);
    AssertThrows<ArgumentException>(() => FastFourierTransform.Forward([1.0, 2.0, 3.0]));

    Complex[] arbitrary = FastFourierTransform.ForwardAnyLength([1.0, 2.0, 3.0]);
    AssertComplexSequence(
        [
            new Complex(6.0, 0.0),
            new Complex(-1.5, Math.Sqrt(3.0) / 2.0),
            new Complex(-1.5, -Math.Sqrt(3.0) / 2.0)
        ],
        arbitrary,
        1e-12);

    double[] pocketInput = Enumerable.Range(0, 32)
        .Select(i => ((((i * 37) % 101) - 50) / 7.0))
        .ToArray();
    Complex[] pocketSpectrum = PocketFftReal.Forward(pocketInput);
    ulong[] expectedSpectrumBits =
    [
        13835058055282163712UL, 0UL,
        13837488070422103640UL, 4617572491043400501UL,
        13835567031234699046UL, 4594495300033290048UL,
        13849200329768882624UL, 4627365117720238004UL,
        13839508760067471504UL, 4607849324652951936UL,
        13830409813963133184UL, 13833922836641819600UL,
        13850435346296224210UL, 4619803001473520042UL,
        13841527284487530352UL, 13829620100269872344UL,
        4611686018427387904UL, 13835058055282163712UL,
        13855010667504094478UL, 13845835037866143772UL,
        13850435346296224210UL, 13846057476987553055UL,
        13853482893817393680UL, 13851309221333289420UL,
        4630380693095538276UL, 4629982627366005415UL,
        4616673146621170666UL, 4614003683221632092UL,
        13835567031234699042UL, 13845529130137268088UL,
        4617183252080502104UL, 4624236675447915160UL,
        4611686018427387904UL, 0UL
    ];
    for (int i = 0; i < pocketSpectrum.Length; i++)
    {
        AssertEqual(expectedSpectrumBits[2 * i], BitConverter.DoubleToUInt64Bits(pocketSpectrum[i].Real));
        AssertEqual(expectedSpectrumBits[(2 * i) + 1], BitConverter.DoubleToUInt64Bits(pocketSpectrum[i].Imaginary));
    }

    double[] pocketInverse = PocketFftReal.Inverse(pocketSpectrum, pocketInput.Length);
    ulong[] expectedInverseBits =
    [
        13843100197473896740UL, 13834414683906825072UL,
        4614902875304081116UL, 13841491769035550134UL,
        13824764113276745424UL, 4617154675117766364UL,
        13839883340597203529UL, 4607182418800017408UL,
        4618763103556112968UL, 13836988169408179640UL,
        4612651075490395865UL, 13842617668942392758UL,
        13832484569780809142UL, 4615867932367089080UL,
        13841009240504046152UL, 13596367275031527424UL,
        4617637203649270344UL, 13839239969221864888UL,
        4609112532926033335UL, 4619245632087616950UL,
        13836023112345171676UL, 4613616132553403830UL,
        13842135140410888779UL, 13830554455654793216UL,
        4616511303742427720UL, 13840526711972542170UL,
        4601392076421969624UL, 4618119732180774326UL,
        13838274912158856923UL, 4611042647052049261UL,
        4619728160619120934UL, 13835058055282163711UL
    ];
    for (int i = 0; i < pocketInverse.Length; i++)
    {
        AssertEqual(expectedInverseBits[i], BitConverter.DoubleToUInt64Bits(pocketInverse[i]));
    }

    AssertThrows<ArgumentException>(() => PocketFftReal.Forward([1.0, 2.0, 3.0]));
    AssertThrows<ArgumentException>(() => PocketFftReal.Inverse([Complex.One], 4));
}

[Fact(DisplayName = "pocket complex FFT matches NumPy bit patterns")]
public void PocketComplexFftMatchesNumpyBitPatterns()
{
    Complex[] input = Enumerable.Range(0, 64)
        .Select(i => new Complex(
            ((((i * 37) % 101) - 50) / 8.0),
            ((((i * 19) % 67) - 30) / 16.0)))
        .ToArray();

    Complex[] spectrum = PocketFftComplex.Forward(input);
    AssertEqual(
        "A61BAE490D21D7C0FA7F931044D220A1CEF8C0B15F21852F4323B7A0E48EFA1B",
        ComplexBitsSha256(spectrum));
    AssertEqual(
        "FCB55A58B16247A7213A85788A7D0B21B2C9B2428AAED5826783C31C45E25EDE",
        ComplexBitsSha256(PocketFftComplex.Inverse(spectrum)));

    double[] real = input.Select(value => value.Real).ToArray();
    AssertComplexSequence(
        PocketFftComplex.Forward(real.Select(value => new Complex(value, 0.0)).ToArray()),
        PocketFftComplex.ForwardReal(real),
        0.0);
    AssertThrows<ArgumentException>(() => PocketFftComplex.Forward([Complex.One, Complex.Zero, Complex.One]));
    AssertThrows<ArgumentException>(() => PocketFftComplex.Inverse([Complex.One]));
}

[Fact(DisplayName = "analytic signal recovers sinusoid quadrature")]
public void AnalyticSignalRecoversSinusoidQuadrature()
{
    const int n = 16;
    double[] input = Enumerable.Range(0, n)
        .Select(i => Math.Cos(Math.Tau * 2.0 * i / n))
        .ToArray();

    Complex[] analytic = AnalyticSignal.FromReal(input);
    for (int i = 0; i < n; i++)
    {
        double expectedReal = Math.Cos(Math.Tau * 2.0 * i / n);
        double expectedImaginary = Math.Sin(Math.Tau * 2.0 * i / n);
        AssertClose(expectedReal, analytic[i].Real, 1e-12);
        AssertClose(expectedImaginary, analytic[i].Imaginary, 1e-12);
    }
}

[Fact(DisplayName = "analytic FM demod recovers generated frequency")]
public void AnalyticFmDemodRecoversGeneratedFrequency()
{
    const int n = 32;
    const double sampleRate = 32.0;
    const double frequency = 4.0;
    Complex[] analytic = Enumerable.Range(0, n)
        .Select(i => Complex.FromPolarCoordinates(1.0, Math.Tau * frequency * i / sampleRate))
        .ToArray();

    double[] demod = AnalyticSignal.FmDemodulateAnalytic(analytic, sampleRate);
    AssertClose(0.0, demod[0], 1e-12);
    for (int i = 1; i < demod.Length; i++)
    {
        AssertClose(frequency, demod[i], 1e-12);
    }

    double[] real = analytic.Select(value => value.Real).ToArray();
    double[] realDemod = AnalyticSignal.FmDemodulate(real, sampleRate);
    for (int i = 2; i < realDemod.Length - 2; i++)
    {
        AssertClose(frequency, realDemod[i], 1e-10);
    }
}

[Fact(DisplayName = "RF demodulator recovers FM block with frequency filters")]
public void RfDemodulatorRecoversFmBlockWithFrequencyFilters()
{
    const int n = 32;
    const double sampleRate = 32.0;
    const double carrier = 4.0;
    double[] raw = Enumerable.Range(0, n)
        .Select(i => Math.Cos(Math.Tau * carrier * i / sampleRate))
        .ToArray();

    var demodulator = new RfDemodulator(sampleRate);
    Complex[] identity = RfDemodulator.IdentityFilter(n);
    RfDemodulatedBlock block = demodulator.Demodulate(raw, identity, identity);
    Complex[] precomputedSpectrum = PocketFftComplex.ForwardDuccRealFull(raw);
    RfDemodulatedBlock precomputedBlock = demodulator.Demodulate(
        raw,
        identity,
        identity,
        Array.Empty<Complex>(),
        identity,
        identity,
        precomputedInputSpectrum: precomputedSpectrum);
    AssertSequence(block.Video, precomputedBlock.Video);
    AssertSequence(block.DemodRaw, precomputedBlock.DemodRaw);
    AssertComplexSequence(block.Analytic, precomputedBlock.Analytic, 0.0);
    AssertEqual(n, block.Envelope.Length);
    AssertEqual(n, block.VideoLowPass.Length);
    AssertEqual(n, block.RfHighPass.Length);
    for (int i = 0; i < block.Envelope.Length; i++)
    {
        AssertClose(1.0, block.Envelope[i], 1e-12);
    }

    for (int i = 2; i < block.DemodRaw.Length - 2; i++)
    {
        AssertClose(carrier, block.DemodRaw[i], 1e-10);
        AssertClose(carrier, block.Video[i], 1e-10);
        AssertClose(carrier, block.VideoLowPass[i], 1e-10);
        AssertClose(raw[i], block.RfHighPass[i], 1e-10);
    }

    Complex[] zeroVideo = new Complex[n];
    RfDemodulatedBlock muted = demodulator.Demodulate(raw, identity, zeroVideo);
    AssertSequence(new double[n], muted.Video);
    AssertSequence(new double[n], muted.VideoLowPass);
    RfDemodulatedBlock lowPassMuted = demodulator.Demodulate(raw, identity, identity, zeroVideo);
    AssertSequence(new double[n], lowPassMuted.VideoLowPass);
    double phase = 0.0;
    double[] variedFm = new double[n];
    for (int i = 0; i < variedFm.Length; i++)
    {
        double instantaneous = 3.0 + (0.75 * Math.Sin(Math.Tau * i / variedFm.Length));
        phase += Math.Tau * instantaneous / sampleRate;
        variedFm[i] = Math.Cos(phase);
    }

    RfDemodulatedBlock noOffset = demodulator.Demodulate(variedFm, identity, identity, identity, videoLowPassOffset: 0);
    RfDemodulatedBlock shifted = demodulator.Demodulate(variedFm, identity, identity, identity, videoLowPassOffset: 5);
    AssertSequence(FrequencyDomainFilter.Roll(noOffset.VideoLowPass, -5), shifted.VideoLowPass);
    AssertThrows<ArgumentException>(() => demodulator.Demodulate(raw, identity, [Complex.One]));
    AssertThrows<ArgumentException>(() => demodulator.Demodulate(
        raw,
        identity,
        identity,
        Array.Empty<Complex>(),
        identity,
        identity,
        precomputedInputSpectrum: new Complex[n / 2]));

    const int ldN = 128;
    const double ldSampleRate = 8_000_000.0;
    const double lowCarrier = 1_000_000.0;
    double[] lowFm = Enumerable.Range(0, ldN)
        .Select(i => Math.Cos(Math.Tau * lowCarrier * i / ldSampleRate))
        .ToArray();
    Complex[] ldIdentity = RfDemodulator.IdentityFilter(ldN);
    var ldDemodulator = new RfDemodulator(ldSampleRate);
    RfDemodulatedBlock clipped = ldDemodulator.Demodulate(
        lowFm,
        ldIdentity,
        ldIdentity,
        ldIdentity,
        ldIdentity,
        ldIdentity,
        referenceFilters: new RfVideoReferenceFilterSet(null, 0, null, ClipDemodForVideo: true));
    for (int i = 4; i < clipped.DemodRaw.Length - 4; i++)
    {
        AssertClose(lowCarrier, clipped.DemodRaw[i], 1e-3);
        AssertClose(1_500_000.0, clipped.Video[i], 1e-3);
        AssertClose(1_500_000.0, clipped.VideoLowPass[i], 1e-3);
    }
}

[Fact(DisplayName = "RF demodulator applies RF high boost")]
public void RfDemodulatorAppliesRfHighBoost()
{
    const int n = 64;
    const double sampleRate = 64.0;
    const double carrier = 8.0;
    double[] raw = Enumerable.Range(0, n)
        .Select(i => Math.Cos(Math.Tau * carrier * i / sampleRate))
        .ToArray();

    var demodulator = new RfDemodulator(sampleRate);
    Complex[] identity = RfDemodulator.IdentityFilter(n);
    RfDemodulatedBlock plain = demodulator.Demodulate(raw, identity, identity, identity, identity, identity);
    RfDemodulatedBlock zeroBoost = demodulator.Demodulate(
        raw,
        identity,
        identity,
        identity,
        identity,
        identity,
        rfHighBoost: new RfHighBoostOptions(0.0, 6.0, 10.0));
    RfDemodulatedBlock boosted = demodulator.Demodulate(
        raw,
        identity,
        identity,
        identity,
        identity,
        identity,
        rfHighBoost: new RfHighBoostOptions(2.0, 6.0, 10.0));

    AssertComplexSequence(plain.Analytic, zeroBoost.Analytic, 1e-12);
    AssertTrue(MeanMagnitude(boosted.Analytic) > MeanMagnitude(plain.Analytic) * 1.1);
    AssertThrows<ArgumentOutOfRangeException>(() => demodulator.Demodulate(
        raw,
        identity,
        identity,
        identity,
        identity,
        identity,
        rfHighBoost: new RfHighBoostOptions(1.0, 10.0, 6.0)));

    double[] syntheticRaw = Enumerable.Range(0, n)
        .Select(i => 8.0 + (((i * 37) % 101) / 16.0))
        .ToArray();
    SosSection[] identitySos = [new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)];
    RfDemodulatedBlock vhsBoosted = demodulator.Demodulate(
        syntheticRaw,
        identity,
        identity,
        ReadOnlySpan<Complex>.Empty,
        identity,
        identity,
        rfHighBoost: new RfHighBoostOptions(1.25, 8.0, 24.0),
        fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation,
        vhsEnvelopeFilter: identitySos,
        vhsRfTopFilter: identitySos);
    AssertEqual(
        "AB36B5F97FDEDD310957AD99B664A50A36CA6C602571B3C3FDE1FE719E3B1921",
        DoubleBitsSha256(vhsBoosted.Envelope));
    AssertEqual(
        "717670CF99BE6928A0DF9CA1097A275AA4EEF584ED5F0E1E2F69AE943B32FEDF",
        ComplexBitsSha256(vhsBoosted.Analytic));
}

[Fact(DisplayName = "RF demodulator replaces diff demod spikes")]
public void RfDemodulatorReplacesDiffDemodSpikes()
{
    double[] demod = Enumerable.Repeat(10.0, 50).ToArray();
    double[] demodDiffed = Enumerable.Repeat(1.0, 50).ToArray();
    demod[20] = 100.0;
    RfDemodulator.ReplaceSpikes(demod, demodDiffed, maxValue: 50.0);

    for (int i = 12; i < 49; i++)
    {
        AssertClose(1.0, demod[i], 1e-12);
    }

    AssertClose(10.0, demod[11], 1e-12);
    AssertClose(10.0, demod[49], 1e-12);

    double[] notImproved = Enumerable.Repeat(10.0, 50).ToArray();
    double[] worseDiffed = Enumerable.Repeat(200.0, 50).ToArray();
    notImproved[20] = 100.0;
    RfDemodulator.ReplaceSpikes(notImproved, worseDiffed, maxValue: 50.0);
    AssertClose(100.0, notImproved[20], 1e-12);

    double[] overlapping = Enumerable.Repeat(10.0, 80).ToArray();
    double[] overlappingDiffed = Enumerable.Repeat(1.0, 80).ToArray();
    overlapping[20] = 100.0;
    overlapping[40] = 100.0;
    RfDemodulator.ReplaceSpikes(overlapping, overlappingDiffed, maxValue: 50.0);
    for (int i = 12; i < 70; i++)
    {
        AssertClose(1.0, overlapping[i], 1e-12);
    }

    AssertClose(10.0, overlapping[11], 1e-12);
    AssertClose(10.0, overlapping[70], 1e-12);
    AssertThrows<ArgumentException>(() => RfDemodulator.ReplaceSpikes([1.0], [1.0, 2.0], maxValue: 0.0));
}

[Fact(DisplayName = "RF demodulator applies chroma trap comb")]
public void RfDemodulatorAppliesChromaTrapComb()
{
    const int n = 64;
    const double sampleRate = 40.0;
    const double fsc = 5.0;
    double[] chroma = Enumerable.Range(0, n)
        .Select(i => Math.Sin(Math.Tau * fsc * i / sampleRate))
        .ToArray();
    double[] trapped = RfDemodulator.ApplyChromaTrap(chroma, sampleRate, fsc);
    AssertTrue(MaxAbs(trapped) < 1e-12);

    double[] dc = Enumerable.Repeat(123.0, n).ToArray();
    AssertSequence(dc, RfDemodulator.ApplyChromaTrap(dc, sampleRate, fsc));
    AssertThrows<ArgumentOutOfRangeException>(() => RfDemodulator.ApplyChromaTrap(dc, sampleRate, sampleRate / 2.0));

    (int ntscNumerator, int ntscDenominator) = SoxrQuickResampler.ApproximateRatio(28_636_363.0 / 40_000_000.0);
    AssertEqual(63, ntscNumerator);
    AssertEqual(88, ntscDenominator);
    (int palNumerator, int palDenominator) = SoxrQuickResampler.ApproximateRatio(35_468_950.0 / 40_000_000.0);
    AssertEqual(728, palNumerator);
    AssertEqual(821, palDenominator);

    double[] vhsInput = Enumerable.Range(0, 32 * 1024)
        .Select(i => 2_000_000.0 + (((i * 37.0) % 101.0) * 1_000.0))
        .ToArray();
    AssertEqual(
        "2B4E91B7B81337523BF5D3060A7E21A4B95195A56A2A593830A78A493B88268C",
        DoubleBitsSha256(RfDemodulator.ApplyChromaTrap(
            vhsInput,
            40_000_000.0,
            3.5795454545454546 * 1_000_000.0)));
    AssertEqual(
        "1A036B6D4B2E9224423A87DFFC47140454C7B10805E39AF5483438E9FAE8A609",
        DoubleBitsSha256(RfDemodulator.ApplyChromaTrap(
            vhsInput,
            40_000_000.0,
            4.43361875 * 1_000_000.0)));
}

[Fact(DisplayName = "VHS chroma decoder applies upstream output helpers")]
public void VhsChromaDecoderAppliesUpstreamOutputHelpers()
{
    ushort[] mapped = VhsChromaDecoder.ChromaToU16([
        -32768.0,
        -32767.0,
        -1.0,
        0.0,
        1.0,
        32768.0,
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity
    ]);
    AssertIntSequence(
        [0, 0, 32766, 32767, 32768, 65535, 0, 0, 0],
        mapped.Select(value => (int)value).ToArray());
    AssertIntSequence(
        [65535, 0, 32767, 32767, 47964, 47964],
        VhsChromaDecoder.ChromaToU16([80_733.0, -50_339.0, 0.0, 0.0, 80_733.0, -50_339.0])
            .Select(value => (int)value)
            .ToArray());

    AssertSequence([1.5, -1.5, -0.5, 0.5], VhsChromaDecoder.ShiftChromaAndRemoveDc([1.0, 2.0, 3.0, 4.0], move: 1));
    AssertSequence([-0.5, 0.5, 1.5, -1.5], VhsChromaDecoder.ShiftChromaAndRemoveDc([1.0, 2.0, 3.0, 4.0], move: -1));
    var fastMeanProbe = new double[32];
    fastMeanProbe[0] = 1e20;
    fastMeanProbe[8] = 1.0;
    fastMeanProbe[16] = -1e20;
    fastMeanProbe[24] = 1.0;
    AssertSequence(
        fastMeanProbe.Select(value => (double)(float)value).ToArray(),
        VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32(fastMeanProbe, move: 0));

    double[] deemphasized = VhsChromaDecoder.ApplyBurstDeemphasis(
        Enumerable.Repeat(1.0, 16).ToArray(),
        lineOffset: 1,
        linesOut: 2,
        lineLength: 8,
        burstStart: 1,
        burstEnd: 2);
    AssertSequence([1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 2.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 2.0], deemphasized);

    double[] combInput = new double[20 * 2];
    for (int line = 0; line < 20; line++)
    {
        combInput[line * 2] = line * line;
        combInput[(line * 2) + 1] = (line * line) + 1;
    }

    double[] ntsc = VhsChromaDecoder.ApplyNtscComb(combInput, lineLength: 2);
    double[] pal = VhsChromaDecoder.ApplyPalComb(combInput, lineLength: 2);
    AssertClose(-0.5, ntsc[16 * 2], 1e-12);
    AssertClose(-0.5, ntsc[(16 * 2) + 1], 1e-12);
    AssertClose(-2.0, pal[16 * 2], 1e-12);
    AssertClose(-2.0, pal[(16 * 2) + 1], 1e-12);
    AssertClose(combInput[15 * 2], ntsc[15 * 2], 1e-12);
    AssertClose(combInput[18 * 2], ntsc[18 * 2], 1e-12);

    var preciseCombInput = new double[20];
    preciseCombInput[15] = -0.234567890123;
    preciseCombInput[16] = 1.000000000123;
    preciseCombInput[17] = 0.345678901234;
    double preciseCombined =
        ((preciseCombInput[16] * 2.0) - preciseCombInput[17] - preciseCombInput[15]) / 4.0;
    double[] float32Comb = VhsChromaDecoder.ApplyNtscComb(preciseCombInput, lineLength: 1);
    double[] float64Comb = VhsChromaDecoder.ApplyNtscComb(
        preciseCombInput,
        lineLength: 1,
        retainFloat32: false);
    AssertEqual((double)(float)preciseCombined, float32Comb[16]);
    AssertEqual(preciseCombined, float64Comb[16]);
    AssertFalse(float32Comb[16] == float64Comb[16]);

    double[] accInput = new double[18 * 4];
    new double[] { 100.0, 102.0, 98.0, 100.0 }.CopyTo(accInput, 16 * 4);
    new double[] { 200.0, 204.0, 196.0, 200.0 }.CopyTo(accInput, 17 * 4);
    AutomaticChromaGainResult acc = VhsChromaDecoder.ApplyAutomaticChromaGain(
        accInput,
        burstAbsRef: 10.0,
        burstStart: 1,
        burstEnd: 3,
        lineLength: 4,
        lines: 18,
        burstDetectedLine: 0);
    AssertClose(3.0, acc.MeanBurstRms, 1e-12);
    AssertSequence([500.0, 510.0, 490.0, 500.0], acc.Samples.Skip(16 * 4).Take(4).ToArray());
    AssertSequence([500.0, 510.0, 490.0, 500.0], acc.Samples.Skip(17 * 4).Take(4).ToArray());

    const int accProbeLineLength = 60;
    var accProbe = new double[18 * accProbeLineLength];
    for (int line = 16; line < 18; line++)
    {
        for (int i = 0; i < accProbeLineLength; i++)
        {
            accProbe[(line * accProbeLineLength) + i] = (float)(
                ((((i * 37) + (line * 11)) % 101) - 50) / 10.0f
                + (((i % 3) - 1) * 1000.0f));
        }
    }

    AutomaticChromaGainResult accFloat32 = VhsChromaDecoder.ApplyAutomaticChromaGain(
        accProbe,
        burstAbsRef: 4416.0,
        burstStart: 4,
        burstEnd: 56,
        lineLength: accProbeLineLength,
        lines: 18,
        burstDetectedLine: 0);
    AssertEqual(13886080547120341843UL, BitConverter.DoubleToUInt64Bits(accFloat32.Samples[960]));
    AssertEqual(13850057151597280334UL, BitConverter.DoubleToUInt64Bits(accFloat32.Samples[961]));
    AssertEqual(4662752321137288657UL, BitConverter.DoubleToUInt64Bits(accFloat32.Samples[977]));

    const int doubleAccLineLength = 52;
    var doubleAccProbe = new double[17 * doubleAccLineLength];
    for (int i = 0; i < doubleAccLineLength; i++)
    {
        doubleAccProbe[(16 * doubleAccLineLength) + i] =
            ((((i * 37) + 11) % 101) - 50) / 10.0
            + (((i % 3) - 1) * 1000.0)
            + ((i % 7) * 1e-9);
    }

    AutomaticChromaGainResult accFloat64 = VhsChromaDecoder.ApplyAutomaticChromaGain(
        doubleAccProbe,
        burstAbsRef: 4416.0,
        burstStart: 0,
        burstEnd: doubleAccLineLength,
        lineLength: doubleAccLineLength,
        lines: 17,
        burstDetectedLine: 0,
        useFloat32Rms: false);
    AssertEqual(4650426021891215527UL, BitConverter.DoubleToUInt64Bits(accFloat64.MeanBurstRms));

    AutomaticChromaGainResult killed = VhsChromaDecoder.ApplyAutomaticChromaGain(
        accInput,
        burstAbsRef: 10.0,
        burstStart: 1,
        burstEnd: 3,
        lineLength: 4,
        lines: 18,
        burstDetectedLine: 17);
    AssertClose(2.0, killed.MeanBurstRms, 1e-12);
    AssertSequence([0.0, 0.0, 0.0, 0.0], killed.Samples.Skip(16 * 4).Take(4).ToArray());
    AssertSequence([500.0, 510.0, 490.0, 500.0], killed.Samples.Skip(17 * 4).Take(4).ToArray());
}

[Fact(DisplayName = "VHS chroma decoder upconverts line phases")]
public void VhsChromaDecoderUpconvertsLinePhases()
{
    double[][] heterodyne = VhsChromaDecoder.BuildHeterodyneTable(
        sampleCount: 4,
        fscMHz: 3.0,
        colorUnderCarrierMHz: 1.0,
        outputSampleRateMHz: 16.0);
    AssertEqual(
        (double)(float)-Math.Cos(Math.Tau * (4.0 / 16.0) * 1.0),
        heterodyne[0][1]);
    AssertSequence([-1.0, 0.0, 1.0, 0.0], heterodyne[0]);
    AssertSequence([0.0, 1.0, 0.0, -1.0], heterodyne[1]);

    double[] chroma = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0];
    double[] phase0 = Enumerable.Repeat(2.0, chroma.Length).ToArray();
    double[] phase1 = Enumerable.Repeat(-1.0, chroma.Length).ToArray();
    double[] upconverted = VhsChromaDecoder.UpconvertChroma(
        chroma,
        lineOffset: 1,
        lineLength: 3,
        [
            new ChromaPhaseLine(LineNumber: 1, PhaseRotation: 0),
            new ChromaPhaseLine(LineNumber: 2, PhaseRotation: 1)
        ],
        [phase0, phase1]);
    AssertSequence([20.0, 40.0, 60.0, -40.0, -50.0, -60.0], upconverted);

    double[] compensated = VhsChromaDecoder.UpconvertChromaPhaseCompensated(
        [1.0, 1.0, 1.0, 1.0],
        lineOffset: 0,
        lineLength: 2,
        [
            new ChromaPhaseLine(LineNumber: 0, PhaseRotation: 0, BurstPhaseDegrees: 0.0),
            new ChromaPhaseLine(LineNumber: 1, PhaseRotation: 1, BurstPhaseDegrees: 90.0)
        ],
        colorUnderCarrierHz: 0.0,
        fscMHz: 1.0,
        targetPhaseEvenDegrees: 0.0,
        targetPhaseOddDegrees: 0.0);
    AssertSequence([-1.0, 0.0, -1.0, 0.0], compensated);

    double[] numbaFastMathCompensated = VhsChromaDecoder.UpconvertChromaPhaseCompensated(
        [0.5, -1.25, 2.0, -3.5, 4.25, -5.0, 6.5, -7.75, 8.0, -9.5],
        lineOffset: 1,
        lineLength: 10,
        [new ChromaPhaseLine(LineNumber: 1, PhaseRotation: 3, BurstPhaseDegrees: 55.0)],
        colorUnderCarrierHz: 629_370.0,
        fscMHz: 315.0 / 88.0,
        targetPhaseEvenDegrees: -33.0,
        targetPhaseOddDegrees: -33.0);
    uint[] expectedNumbaBits =
    [
        3191852143, 1065141330, 1070476877, 3214743597, 3230108355,
        3212239443, 1085893508, 1084778306, 3229968243, 3239209412
    ];
    for (int i = 0; i < expectedNumbaBits.Length; i++)
    {
        AssertEqual(
            expectedNumbaBits[i],
            unchecked((uint)BitConverter.SingleToInt32Bits((float)numbaFastMathCompensated[i])));
    }

    AssertThrows<ArgumentOutOfRangeException>(() => VhsChromaDecoder.UpconvertChroma(
        chroma,
        lineOffset: 1,
        lineLength: 3,
        [new ChromaPhaseLine(LineNumber: 0, PhaseRotation: 0)],
        [phase0]));
}

[Fact(DisplayName = "VHS chroma decoder detects burst phase sequence")]
public void VhsChromaDecoderDetectsBurstPhaseSequence()
{
    ChromaBurstDemodulationResult burst = VhsChromaDecoder.DemodBurst(
        [1.0, 2.0],
        lineScale: 0.5,
        lineStart: 0,
        burstStart: 2,
        burstSin: [0.0, 0.0, 0.0, 1.0],
        burstCos: [0.0, 0.0, 1.0, 0.0]);
    AssertClose(Math.Atan2(2.0, 1.0) * 180.0 / Math.PI, burst.PhaseDegrees, 1e-12);
    AssertClose(90.0, burst.PhaseOffsetDegrees, 1e-12);
    AssertClose(Math.Sqrt(5.0), burst.Magnitude, 1e-12);
    AssertClose(1.0, burst.I, 1e-12);
    AssertClose(2.0, burst.Q, 1e-12);

    double[] numbaBurst = Enumerable.Range(0, 70)
        .Select(i => (((i * 37) % 101 - 50) / 8.0) + Math.ScaleB(i + 1.0, -35))
        .ToArray();
    double[] numbaSin = Enumerable.Range(0, 96)
        .Select(i => (double)(float)(((i * 17) % 53 - 26) / 16.0))
        .ToArray();
    double[] numbaCos = Enumerable.Range(0, 96)
        .Select(i => (double)(float)(((i * 29) % 67 - 33) / 16.0))
        .ToArray();
    ChromaBurstDemodulationResult numbaBurstResult = VhsChromaDecoder.DemodBurst(
        numbaBurst,
        lineScale: 0.9989764459848242,
        lineStart: 0,
        burstStart: 7,
        numbaSin,
        numbaCos);
    AssertEqual(0xC038BA000003D400UL, BitConverter.DoubleToUInt64Bits(numbaBurstResult.I));
    AssertEqual(0x400CDFFFFF95D000UL, BitConverter.DoubleToUInt64Bits(numbaBurstResult.Q));
    AssertEqual(0x4038FD155992DCC5UL, BitConverter.DoubleToUInt64Bits(numbaBurstResult.Magnitude));
    AssertEqual(0x4065763E428A68F1UL, BitConverter.DoubleToUInt64Bits(numbaBurstResult.PhaseDegrees));
    AssertEqual(0x3FE4A28575E4BBDAUL, BitConverter.DoubleToUInt64Bits(numbaBurstResult.PhaseOffsetDegrees));

    double[] chroma = Enumerable.Range(0, 12).Select(value => (double)value).ToArray();
    double[] carrierSin = new double[12];
    double[] carrierCos = new double[12];
    carrierCos[8] = 1.0;
    carrierSin[9] = 1.0;
    ChromaBurstDemodulationResult probed = VhsChromaDecoder.ProbeUpconvertedBurst(
        chroma,
        [Enumerable.Repeat(1.0, chroma.Length).ToArray()],
        phaseRotation: 0,
        burstStart: 2,
        burstEnd: 4,
        burstSin: carrierSin,
        burstCos: carrierCos,
        lineScale: 0.75,
        lineNumber: 1,
        lineOffset: 0,
        lineLength: 6,
        burstFilter: padded => padded.Select(value => value * 2.0).ToArray());
    AssertClose(Math.Atan2(18.0, 16.0) * 180.0 / Math.PI, probed.PhaseDegrees, 1e-12);
    AssertClose(45.0, probed.PhaseOffsetDegrees, 1e-12);
    AssertClose(Math.Sqrt((16.0 * 16.0) + (18.0 * 18.0)), probed.Magnitude, 1e-12);
    AssertClose(16.0, probed.I, 1e-12);
    AssertClose(18.0, probed.Q, 1e-12);

    double[] lineLocations = Enumerable.Range(0, 41).Select(line => line * 100.0).ToArray();
    double line17Scale = 0.0;
    ChromaPhaseSequenceResult sequence = VhsChromaDecoder.GetPhaseRotationSequence(
        chromaRotation: null,
        chromaRotationIndex: 0,
        lineLocations,
        lineOffset: 0,
        linesOut: 40,
        inputLineLength: 100,
        burstProbe: (lineNumber, phaseRotation, lineScale) =>
        {
            if (lineNumber == 17)
            {
                line17Scale = lineScale;
            }

            return new ChromaBurstDemodulationResult(0.0, 0.0, 30_000.0, 30_000.0, 0.0);
        },
        detectChromaTrackPhase: false,
        rotationCheckStartLine: 24,
        enableColorKiller: true,
        prevBurstDetectedLine: -1,
        colorSystem: "NTSC");
    AssertEqual(0, sequence.NextChromaRotationIndex);
    AssertEqual(40, sequence.PhaseSequence.Length);
    AssertEqual(17, sequence.BurstDetectedLine);
    AssertClose(30_000.0, sequence.BurstMagnitudeAverage, 1e-12);
    AssertClose(0.0, sequence.BurstPhaseAverageDegrees, 1e-12);
    AssertClose(0.0, sequence.EvenBurstPhaseAverageDegrees, 1e-12);
    AssertClose(0.0, sequence.OddBurstPhaseAverageDegrees, 1e-12);
    AssertClose(1.0, line17Scale, 1e-12);

    ChromaPhaseSequenceResult lowBurst = VhsChromaDecoder.GetPhaseRotationSequence(
        chromaRotation: null,
        chromaRotationIndex: 0,
        lineLocations,
        lineOffset: 0,
        linesOut: 40,
        inputLineLength: 100,
        burstProbe: (_, _, _) => new ChromaBurstDemodulationResult(0.0, 0.0, 1_000.0, 1_000.0, 0.0),
        detectChromaTrackPhase: false,
        rotationCheckStartLine: 24,
        enableColorKiller: true,
        prevBurstDetectedLine: 0,
        colorSystem: "NTSC");
    AssertEqual(-1, lowBurst.BurstDetectedLine);
    AssertClose(1_000.0, lowBurst.BurstMagnitudeAverage, 1e-12);

    ChromaPhaseSequenceResult rotated = VhsChromaDecoder.GetPhaseRotationSequence(
        chromaRotation: [1, 3],
        chromaRotationIndex: 0,
        lineLocations,
        lineOffset: 0,
        linesOut: 40,
        inputLineLength: 100,
        burstProbe: (_, phaseRotation, _) =>
        {
            double phaseRadians = phaseRotation * Math.PI / 2.0;
            return new ChromaBurstDemodulationResult(
                phaseRotation * 90.0,
                0.0,
                30_000.0,
                Math.Cos(phaseRadians) * 30_000.0,
                Math.Sin(phaseRadians) * 30_000.0);
        },
        detectChromaTrackPhase: false,
        rotationCheckStartLine: 24,
        enableColorKiller: false,
        prevBurstDetectedLine: 0,
        colorSystem: "NTSC");
    AssertEqual(1, rotated.NextChromaRotationIndex);
    AssertEqual(1, rotated.PhaseSequence[0].PhaseRotation);
    AssertEqual(2, rotated.PhaseSequence[1].PhaseRotation);
    AssertEqual(3, rotated.PhaseSequence[2].PhaseRotation);
    AssertEqual(0, rotated.PhaseSequence[3].PhaseRotation);

    ChromaPhaseSequenceResult literalTwoTrackRotation = VhsChromaDecoder.GetPhaseRotationSequence(
        chromaRotation: [1, 3, 2],
        chromaRotationIndex: 1,
        lineLocations,
        lineOffset: 0,
        linesOut: 40,
        inputLineLength: 100,
        burstProbe: (_, phaseRotation, _) =>
        {
            double phaseRadians = phaseRotation * Math.PI / 2.0;
            return new ChromaBurstDemodulationResult(
                phaseRotation * 90.0,
                0.0,
                30_000.0,
                Math.Cos(phaseRadians) * 30_000.0,
                Math.Sin(phaseRadians) * 30_000.0);
        },
        detectChromaTrackPhase: false,
        rotationCheckStartLine: 24,
        enableColorKiller: false,
        prevBurstDetectedLine: 0,
        colorSystem: "NTSC");
    AssertEqual(0, literalTwoTrackRotation.NextChromaRotationIndex);

    var burstRefinePhase = new ChromaPhaseSequenceResult(
        NextChromaRotationIndex: 0,
        PhaseSequence: Enumerable.Range(3, 11)
            .Select(line => line switch
            {
                12 => new ChromaPhaseLine(LineNumber: line, PhaseRotation: 0, BurstPhaseDegrees: 350.0, BurstPhaseOffsetDegrees: 0.0),
                13 => new ChromaPhaseLine(LineNumber: line, PhaseRotation: 0, BurstPhaseDegrees: 30.0, BurstPhaseOffsetDegrees: 5.0),
                _ => new ChromaPhaseLine(LineNumber: line, PhaseRotation: 0, BurstPhaseDegrees: 0.0)
            })
            .ToArray(),
        BurstDetectedLine: 0,
        BurstMagnitudeAverage: 30_000.0,
        BurstPhaseAverageDegrees: 10.0,
        EvenBurstPhaseAverageDegrees: 40.0,
        OddBurstPhaseAverageDegrees: 20.0);
    double[] burstLineLocations = Enumerable.Range(0, 15).Select(line => line * 100.0).ToArray();
    double[] ntscBurstRefined = VhsChromaDecoder.RefineLineLocationsFromBurst(
        burstLineLocations,
        outputLineLength: 20,
        fscRatio: 4.0,
        burstRefinePhase,
        colorSystem: "NTSC");
    AssertClose(900.0, ntscBurstRefined[9], 1e-12);
    AssertClose(1200.0 + ((20.0 / 360.0) * 4.0 * 5.0), ntscBurstRefined[12], 1e-12);
    AssertClose(1300.0 + ((-15.0 / 360.0) * 4.0 * 5.0), ntscBurstRefined[13], 1e-12);

    double[] palBurstRefined = VhsChromaDecoder.RefineLineLocationsFromBurst(
        burstLineLocations,
        outputLineLength: 20,
        fscRatio: 4.0,
        burstRefinePhase,
        colorSystem: "PAL");
    AssertClose(900.0, palBurstRefined[9], 1e-12);
    AssertClose(1200.0 + ((50.0 / 360.0) * 4.0 * 5.0), palBurstRefined[12], 1e-12);
    AssertClose(1300.0 + ((-5.0 / 360.0) * 4.0 * 5.0), palBurstRefined[13], 1e-12);

    var killedBurstPhase = burstRefinePhase with { BurstDetectedLine = -1 };
    AssertSequence(burstLineLocations, VhsChromaDecoder.RefineLineLocationsFromBurst(
        burstLineLocations,
        outputLineLength: 20,
        fscRatio: 4.0,
        killedBurstPhase,
        colorSystem: "PAL"));
}

[Fact(DisplayName = "VHS chroma burst magnitude matches v0.4.0 Numba hypot")]
public void VhsChromaBurstMagnitudeMatchesReleaseHypot()
{
    ChromaBurstDemodulationResult burst = VhsChromaDecoder.DemodBurst(
        [-1937.88232421875, -110.7741928100586, 42.956520080566406, -197.7586212158203],
        lineScale: 1.0,
        lineStart: 0,
        burstStart: 0,
        burstSin: [0.0, 0.0, 1.0, 1.0],
        burstCos: [1.0, 1.0, 0.0, 0.0]);

    AssertEqual(0x40A00CFE60355380UL, BitConverter.DoubleToUInt64Bits(burst.Magnitude));
}

[Fact(DisplayName = "PAL VHS chroma burst probe matches v0.4.0 float64 bits")]
public void PalVhsChromaBurstProbeMatchesReleaseBits()
{
    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    TbcFrameSpec frameSpec = TbcFrameSpec.FromParameters(parameters);
    var decodeOptions = new ChromaDecodeOptions(
        IsColorUnder: true,
        WriteChroma: true,
        SkipChroma: false,
        UseChromaAfc: false,
        DisableComb: false,
        ChromaDeemphasisFilter: false,
        ChromaAudioNotch: false,
        ChromaOffsetSamples: 0,
        DetectChromaTrackPhase: false,
        EnableColorKiller: false,
        DisableBurstHsync: false,
        DisablePhaseCorrection: false,
        UseOldRawChromaOutput: false);
    VhsChromaFieldOptions options = TbcFieldDecodePipeline.BuildChromaFieldOptions(
        "PAL",
        parameters,
        frameSpec,
        decodeOptions)
        ?? throw new InvalidOperationException("Chroma options were not built for PAL VHS.");

    AssertEqual(94, options.BurstStart);
    AssertEqual(150, options.BurstEnd);
    AssertEqual(
        "E2A9F54E967697937EBE3D44357B8805D0740FBC6C7651C8B596CC4A69992E86",
        DoubleBitsSha256(options.FinalSosFilter!.SelectMany(section => new[]
        {
            section.B0,
            section.B1,
            section.B2,
            section.A0,
            section.A1,
            section.A2
        }).ToArray()));

    int sampleCount = 3 * frameSpec.OutputLineLength;
    double[] chroma = Enumerable.Range(0, sampleCount)
        .Select(index => (((index * 37) % 101 - 50) / 8.0) + Math.ScaleB(index + 1.0, -35))
        .ToArray();
    double[][] heterodyne = VhsChromaDecoder.BuildHeterodyneTable(
        sampleCount,
        options.FscMHz,
        options.ColorUnderCarrierHz / 1_000_000.0,
        options.FscMHz * 4.0);
    (double[] burstSin, double[] burstCos) = VhsChromaDecoder.BuildCarrierTables(
        sampleCount,
        options.FscMHz,
        options.FscMHz * 4.0);
    double[]? capturedMixed = null;
    double[]? capturedFiltered = null;
    ChromaBurstDemodulationResult result = VhsChromaDecoder.ProbeUpconvertedBurst(
        chroma,
        heterodyne,
        phaseRotation: 3,
        options.BurstStart,
        options.BurstEnd,
        burstSin,
        burstCos,
        lineScale: 0.9989764459848242,
        lineNumber: 2,
        lineOffset: 1,
        frameSpec.OutputLineLength,
        burstFilter: padded =>
        {
            capturedMixed = padded.ToArray();
            capturedFiltered = SosFilter.ApplyForwardBackward(options.FinalSosFilter!, padded);
            return capturedFiltered;
        });

    AssertEqual(
        "330869D467CD9D3DBB93F7CF5898D8565F75FFE0C646AA91FF19F272E7147C02",
        DoubleBitsSha256(capturedMixed!));
    AssertEqual(
        "25B6D459D47519976AB97BBBED41FFA88AA1E9F4038ACDF246959264BB827929",
        DoubleBitsSha256(capturedFiltered!));
    AssertEqual(0x4064C329592D3C5FUL, BitConverter.DoubleToUInt64Bits(result.PhaseDegrees));
    AssertEqual(0x4021518B70A91DA9UL, BitConverter.DoubleToUInt64Bits(result.PhaseOffsetDegrees));
    AssertEqual(0x402B4FBACBBD83FCUL, BitConverter.DoubleToUInt64Bits(result.Magnitude));
    AssertEqual(0xC02A82F3B467500FUL, BitConverter.DoubleToUInt64Bits(result.I));
    AssertEqual(0x400A3F02088DC1E6UL, BitConverter.DoubleToUInt64Bits(result.Q));
}

[Fact(DisplayName = "PAL VHS chroma phase sequence matches v0.4.0 float64 bits")]
public void PalVhsChromaPhaseSequenceMatchesReleaseBits()
{
    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    TbcFrameSpec frameSpec = TbcFrameSpec.FromParameters(parameters);
    var decodeOptions = new ChromaDecodeOptions(
        IsColorUnder: true,
        WriteChroma: true,
        SkipChroma: false,
        UseChromaAfc: false,
        DisableComb: false,
        ChromaDeemphasisFilter: false,
        ChromaAudioNotch: false,
        ChromaOffsetSamples: 0,
        DetectChromaTrackPhase: false,
        EnableColorKiller: false,
        DisableBurstHsync: false,
        DisablePhaseCorrection: false,
        UseOldRawChromaOutput: false);
    VhsChromaFieldOptions options = TbcFieldDecodePipeline.BuildChromaFieldOptions(
        "PAL",
        parameters,
        frameSpec,
        decodeOptions)
        ?? throw new InvalidOperationException("Chroma options were not built for PAL VHS.");

    double[] chroma = Enumerable.Range(0, frameSpec.FieldSampleCount)
        .Select(index => (((index * 37) % 101 - 50) / 8.0) + Math.ScaleB(index + 1.0, -35))
        .ToArray();
    const int LineOffset = 3;
    const int InputLineLength = 2_560;
    double[] lineLocations = Enumerable.Range(0, LineOffset + frameSpec.OutputLineCount + 2)
        .Select(line => line * (double)InputLineLength)
        .ToArray();
    ChromaPhaseSequenceResult result = VhsChromaDecoder.AnalyzeFieldPhase(
        chroma,
        options,
        lineLocations,
        InputLineLength,
        lineOffset: LineOffset);

    AssertEqual(1, result.NextChromaRotationIndex);
    AssertEqual(313, result.PhaseSequence.Length);
    AssertEqual(0, result.BurstDetectedLine);
    ChromaPhaseLine first = result.PhaseSequence[0];
    AssertEqual(3, first.LineNumber);
    AssertEqual(0, first.PhaseRotation);
    AssertEqual(0x4073B300258A63F3UL, BitConverter.DoubleToUInt64Bits(first.BurstPhaseDegrees));
    AssertEqual(0UL, BitConverter.DoubleToUInt64Bits(first.BurstPhaseOffsetDegrees));
    AssertEqual(0x403C5B2A5B4885C9UL, BitConverter.DoubleToUInt64Bits(first.BurstMagnitude));
    AssertEqual(0x40341DC74D25B16AUL, BitConverter.DoubleToUInt64Bits(first.I));
    AssertEqual(0xC033FC2D3DEDF498UL, BitConverter.DoubleToUInt64Bits(first.Q));
    ChromaPhaseLine last = result.PhaseSequence[^1];
    AssertEqual(315, last.LineNumber);
    AssertEqual(0, last.PhaseRotation);
    AssertEqual(0x405E9C37E95E4BDAUL, BitConverter.DoubleToUInt64Bits(last.BurstPhaseDegrees));
    AssertEqual(0UL, BitConverter.DoubleToUInt64Bits(last.BurstPhaseOffsetDegrees));
    AssertEqual(0xC02FDC176ECE9795UL, BitConverter.DoubleToUInt64Bits(last.I));
    AssertEqual(0x40390FD7498989E1UL, BitConverter.DoubleToUInt64Bits(last.Q));
    AssertEqual(0x403DB233AAC37DF0UL, BitConverter.DoubleToUInt64Bits(last.BurstMagnitude));
    double[] flattened = result.PhaseSequence
        .SelectMany(line => new[]
        {
            (double)line.LineNumber,
            line.PhaseRotation,
            line.BurstPhaseDegrees,
            line.BurstPhaseOffsetDegrees,
            line.BurstMagnitude,
            line.I,
            line.Q
        })
        .ToArray();
    AssertEqual(
        "8BFBCFFD123B67693FA6EC24B9EFD91A5D90A9BF38BA1BA8926B41F24B9754FB",
        DoubleBitsSha256(flattened));
    AssertEqual(0x4037E9036918DF9AUL, BitConverter.DoubleToUInt64Bits(result.BurstMagnitudeAverage));
    AssertEqual(0x40749879C9CA5F7FUL, BitConverter.DoubleToUInt64Bits(result.BurstPhaseAverageDegrees));
    AssertEqual(0x4074978FCAA356D3UL, BitConverter.DoubleToUInt64Bits(result.EvenBurstPhaseAverageDegrees));
    AssertEqual(0x4074997460E7FB3BUL, BitConverter.DoubleToUInt64Bits(result.OddBurstPhaseAverageDegrees));
}

[Fact(DisplayName = "PAL VHS chroma track-phase sequence matches v0.4.0 float64 bits")]
public void PalVhsChromaTrackPhaseSequenceMatchesReleaseBits()
{
    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    TbcFrameSpec frameSpec = TbcFrameSpec.FromParameters(parameters);
    var decodeOptions = new ChromaDecodeOptions(
        IsColorUnder: true,
        WriteChroma: true,
        SkipChroma: false,
        UseChromaAfc: false,
        DisableComb: false,
        ChromaDeemphasisFilter: false,
        ChromaAudioNotch: false,
        ChromaOffsetSamples: 0,
        DetectChromaTrackPhase: true,
        EnableColorKiller: false,
        DisableBurstHsync: false,
        DisablePhaseCorrection: false,
        UseOldRawChromaOutput: false);
    VhsChromaFieldOptions options = TbcFieldDecodePipeline.BuildChromaFieldOptions(
        "PAL",
        parameters,
        frameSpec,
        decodeOptions)
        ?? throw new InvalidOperationException("Chroma options were not built for PAL VHS.");

    double[] chroma = Enumerable.Range(0, frameSpec.FieldSampleCount)
        .Select(index => (((index * 37) % 101 - 50) / 8.0) + Math.ScaleB(index + 1.0, -35))
        .ToArray();
    const int LineOffset = 3;
    const int InputLineLength = 2_560;
    double[] lineLocations = Enumerable.Range(0, LineOffset + frameSpec.OutputLineCount + 2)
        .Select(line => line * (double)InputLineLength)
        .ToArray();
    ChromaPhaseSequenceResult result = VhsChromaDecoder.AnalyzeFieldPhase(
        chroma,
        options,
        lineLocations,
        InputLineLength,
        lineOffset: LineOffset);

    AssertEqual(1, result.NextChromaRotationIndex);
    AssertEqual(313, result.PhaseSequence.Length);
    AssertEqual(0, result.BurstDetectedLine);
    AssertIntSequence(
        [0, 3, 2, 1, 0, 3, 2, 1, 0, 3, 2, 1, 0, 3, 2, 1],
        result.PhaseSequence[^16..].Select(line => line.PhaseRotation).ToArray());
    ChromaPhaseLine last = result.PhaseSequence[^1];
    AssertEqual(315, last.LineNumber);
    AssertEqual(1, last.PhaseRotation);
    AssertEqual(0x406601C518E0EA83UL, BitConverter.DoubleToUInt64Bits(last.BurstPhaseDegrees));
    AssertEqual(0UL, BitConverter.DoubleToUInt64Bits(last.BurstPhaseOffsetDegrees));
    AssertEqual(0x403352BDFF350DC5UL, BitConverter.DoubleToUInt64Bits(last.BurstMagnitude));
    double[] flattened = result.PhaseSequence
        .SelectMany(line => new[]
        {
            (double)line.LineNumber,
            line.PhaseRotation,
            line.BurstPhaseDegrees,
            line.BurstPhaseOffsetDegrees,
            line.BurstMagnitude,
            line.I,
            line.Q
        })
        .ToArray();
    AssertEqual(
        "1D46B6D78C7F04A749AF47146EFB5A8075917825520B80047D6DF6904D02BAD4",
        DoubleBitsSha256(flattened));
    AssertEqual(0x4037E9036918DF9AUL, BitConverter.DoubleToUInt64Bits(result.BurstMagnitudeAverage));
    AssertEqual(0x40749879C9CA5F7FUL, BitConverter.DoubleToUInt64Bits(result.BurstPhaseAverageDegrees));
    AssertEqual(0x4074978FCAA356D3UL, BitConverter.DoubleToUInt64Bits(result.EvenBurstPhaseAverageDegrees));
    AssertEqual(0x4074997460E7FB3BUL, BitConverter.DoubleToUInt64Bits(result.OddBurstPhaseAverageDegrees));
}

[Fact(DisplayName = "VHS chroma carrier matches NumPy float32 trigonometry")]
public void VhsChromaCarrierMatchesNumpyFloat32Trigonometry()
{
    const int sampleCount = 22_032;
    double fscMHz = 315.0 / 88.0;
    (double[] sine, double[] cosine) = VhsChromaDecoder.BuildCarrierTables(
        sampleCount,
        fscMHz,
        outputSampleRateMHz: fscMHz * 4.0);

    (int Index, uint SinBits, uint CosBits)[] expected =
    [
        (21_866, 0x3AA170E3, 0xBF7FFFF3),
        (21_868, 0x3ADFBA02, 0x3F7FFFE8),
        (21_895, 0xBF7FFFFF, 0x39BDF68A),
        (21_897, 0x3F7FFFFD, 0x3A1EAEDC),
        (21_938, 0x3A6ECE96, 0xBF7FFFF9),
        (21_978, 0x3A401941, 0xBF7FFFFB),
        (22_031, 0xBF7FFFF8, 0x3A7DCA34)
    ];
    foreach ((int index, uint sinBits, uint cosBits) in expected)
    {
        AssertEqual(sinBits, BitConverter.SingleToUInt32Bits((float)sine[index]));
        AssertEqual(cosBits, BitConverter.SingleToUInt32Bits((float)cosine[index]));
    }
}

[Fact(DisplayName = "VHS chroma decoder emits field samples")]
public void VhsChromaDecoderEmitsFieldSamples()
{
    var options = new VhsChromaFieldOptions(
        ColorSystem: "PAL",
        OutputLineLength: 20,
        OutputLineCount: 40,
        OutputSampleRateHz: 4_000_000.0,
        FscMHz: 1.0,
        ColorUnderCarrierHz: 0.0,
        BurstStart: 5,
        BurstEnd: 10,
        BurstAbsRef: 10.0,
        ChromaRotation: null,
        DisableComb: true,
        DisablePhaseCorrection: true,
        EnableColorKiller: false,
        DetectChromaTrackPhase: false);
    double[] chroma = BuildOutputChromaCarrier(
        options.OutputLineLength,
        options.OutputLineCount,
        options.FscMHz,
        options.OutputSampleRateHz);
    double[] lineLocations = Enumerable.Range(0, options.OutputLineCount + 1).Select(line => line * 100.0).ToArray();

    VhsChromaFieldResult result = VhsChromaDecoder.DecodeField(
        chroma,
        options,
        lineLocations,
        inputLineLength: 100);

    AssertEqual(options.OutputLineLength * options.OutputLineCount, result.Samples.Length);
    AssertEqual(0, result.BurstDetectedLine);
    AssertEqual<int?>(null, result.FieldPhaseId);
    AssertEqual(32767, result.Samples[0]);
    int line16 = 16 * options.OutputLineLength;
    AssertEqual(32746, result.Samples[line16]);
    AssertEqual(32767, result.Samples[line16 + 1]);
    AssertEqual(32746, result.Samples[line16 + 2]);
    AssertEqual(32767, result.Samples[line16 + 3]);

    VhsChromaFieldOptions ntscOptions = options with
    {
        ColorSystem = "NTSC",
        ColorUnderCarrierHz = 1_000_000.0
    };
    double[]? capturedUpconverted = null;
    VhsChromaFieldResult ntscResult = VhsChromaDecoder.DecodeField(
        chroma,
        ntscOptions,
        lineLocations,
        inputLineLength: 100,
        finalFilter: values =>
        {
            capturedUpconverted = values.ToArray();
            return values;
        });
    double[] deemphasized = VhsChromaDecoder.ApplyBurstDeemphasis(
        chroma,
        lineOffset: 0,
        linesOut: ntscOptions.OutputLineCount,
        lineLength: ntscOptions.OutputLineLength,
        burstStart: ntscOptions.BurstStart,
        burstEnd: ntscOptions.BurstEnd);
    double[][] heterodyne = VhsChromaDecoder.BuildHeterodyneTable(
        chroma.Length,
        ntscOptions.FscMHz,
        ntscOptions.ColorUnderCarrierHz / 1_000_000.0,
        ntscOptions.OutputSampleRateHz / 1_000_000.0);
    double[] expectedUpconverted = VhsChromaDecoder.UpconvertChroma(
        deemphasized,
        lineOffset: 0,
        ntscOptions.OutputLineLength,
        ntscResult.Phase.PhaseSequence,
        heterodyne);
    AssertSequence(expectedUpconverted, capturedUpconverted!);

    VhsChromaFieldResult filteredBurst = VhsChromaDecoder.DecodeField(
        chroma,
        options with { FinalFilter = new TransferFunction([2.0], [1.0]) },
        lineLocations,
        inputLineLength: 100);
    AssertClose(
        result.Phase.BurstMagnitudeAverage * 4.0,
        filteredBurst.Phase.BurstMagnitudeAverage,
        1e-6);

    VhsChromaFieldOptions mutedOutputOptions = options with
    {
        ChromaPreFilter = new TransferFunction([0.0], [1.0])
    };
    ChromaPhaseSequenceResult rawPhase = VhsChromaDecoder.AnalyzeFieldPhase(
        chroma,
        mutedOutputOptions,
        lineLocations,
        inputLineLength: 100);
    AssertClose(result.Phase.BurstMagnitudeAverage, rawPhase.BurstMagnitudeAverage, 1e-6);
    VhsChromaFieldResult mutedOutput = VhsChromaDecoder.DecodeFieldWithPhase(
        chroma,
        mutedOutputOptions,
        rawPhase);
    AssertTrue(mutedOutput.Samples.All(sample => sample == 32767));

    double[] precisionChroma = Enumerable.Range(0, options.OutputLineLength * options.OutputLineCount)
        .Select(index => (double)(float)((((index * 37) % 1000) - 500) / 16.0f))
        .ToArray();
    var identitySos = new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);
    VhsChromaFieldOptions precisionOptions = options with
    {
        BurstAbsRef = 4416.0,
        DisableComb = false,
        FinalSosFilter = [identitySos],
        FinalFilter = new TransferFunction([1.0], [1.0])
    };
    var precisionPhase = new ChromaPhaseSequenceResult(
        NextChromaRotationIndex: 0,
        Enumerable.Range(0, options.OutputLineCount)
            .Select(line => new ChromaPhaseLine(line, PhaseRotation: 0))
            .ToArray(),
        BurstDetectedLine: 0,
        BurstMagnitudeAverage: 1.0,
        BurstPhaseAverageDegrees: 0.0,
        EvenBurstPhaseAverageDegrees: 0.0,
        OddBurstPhaseAverageDegrees: 0.0);
    VhsChromaFieldResult precisionResult = VhsChromaDecoder.DecodeFieldWithPhase(
        precisionChroma,
        precisionOptions,
        precisionPhase);
    double[] precisionMixed = VhsChromaDecoder.UpconvertChroma(
        precisionChroma,
        lineOffset: 0,
        precisionOptions.OutputLineLength,
        precisionPhase.PhaseSequence,
        VhsChromaDecoder.BuildHeterodyneTable(
            precisionChroma.Length,
            precisionOptions.FscMHz,
            precisionOptions.ColorUnderCarrierHz / 1_000_000.0,
            precisionOptions.OutputSampleRateHz / 1_000_000.0));
    double[] precisionFinal = SosFilter.ApplyForwardBackwardFloat32([identitySos], precisionMixed);
    double[] precisionComb = VhsChromaDecoder.ApplyPalComb(
        precisionFinal,
        precisionOptions.OutputLineLength,
        retainFloat32: true);
    ushort[] expectedPrecisionSamples = VhsChromaDecoder.ChromaToU16(
        VhsChromaDecoder.ApplyAutomaticChromaGain(
            precisionComb,
            precisionOptions.BurstAbsRef,
            precisionOptions.BurstStart,
            precisionOptions.BurstEnd,
            precisionOptions.OutputLineLength,
            precisionOptions.OutputLineCount,
            burstDetectedLine: 0,
            useFloat32Rms: true).Samples);
    AssertTrue(precisionResult.Samples.SequenceEqual(expectedPrecisionSamples));
    ushort[] wrongDoublePrecisionSamples = VhsChromaDecoder.ChromaToU16(
        VhsChromaDecoder.ApplyAutomaticChromaGain(
            VhsChromaDecoder.ApplyPalComb(
                precisionFinal,
                precisionOptions.OutputLineLength,
                retainFloat32: false),
            precisionOptions.BurstAbsRef,
            precisionOptions.BurstStart,
            precisionOptions.BurstEnd,
            precisionOptions.OutputLineLength,
            precisionOptions.OutputLineCount,
            burstDetectedLine: 0,
            useFloat32Rms: false).Samples);
    AssertFalse(expectedPrecisionSamples.SequenceEqual(wrongDoublePrecisionSamples));
}

[Theory(DisplayName = "format-specific chroma field matches v0.4.0 bits")]
[InlineData(0, "PAL_M", "VHS", "481FAD75D80528B5B24AC9DC0662CA93EDD193BB3C0CE9EE86A89BDB667756FE")]
[InlineData(1, "MESECAM", "VHS", "5F727C2341456CD7B9A77179A8C5E72FFFF5E01C84E6496AB4F84E3E49C68AE7")]
[InlineData(2, "PAL", "VIDEO8", "D0C90ACD8472376761F707FEE6800FC04003E530A403E0452001A65C854AE71A")]
[InlineData(3, "NTSC", "VIDEO8", "33830785F4E97F98C79792EC0AFB551BF54927E9DCCE15802D19CA8BDC5411D3")]
[InlineData(4, "PAL", "HI8", "C855C6FAC7B9104E90C5790F14D36DAE98095CB59A1A8CFA1D500FB665CE368A")]
[InlineData(5, "NTSC", "HI8", "355A497C272467AEA6A96D66F899755C62F96F23DF7A8D7F3A3D5F918F0E08E8")]
[InlineData(6, "PAL", "BETAMAX", "094AB253A2FBDD1178BBEB30EB08B29971DA05A829C49F6CB55887EDD72A63DB")]
[InlineData(7, "NTSC", "BETAMAX", "AF3927A3C4B0627C9524F5762477287C0BFBEA5BEA0515F3FBE6BF03B732032C")]
[InlineData(8, "NTSC", "BETAMAX_HIFI", "2D25B8CF323C2074B1BEC298DCD45326F56ADAD25FD5CA8563F35D7A5887E040")]
[InlineData(9, "NTSC", "SUPERBETA", "6C363EB432C1D0614E8E1B22F5C133B7C52C4EE55697A29B0C342347EB472DCB")]
[InlineData(10, "PAL", "UMATIC", "F5D0165ABFBEBE08D69CD46124AA464B112C7B8F00D571CF606404B043A093C4")]
[InlineData(11, "PAL", "UMATIC_HI", "E033E041B967E16853F156DE6D653A017F31ED4AAE4460CDB206D38B448A08A1")]
[InlineData(12, "PAL", "UMATIC_SP", "C1149028299896FC891360546FBA135F0F447F5F5F2EDADB35B824A717F60153")]
[InlineData(13, "PAL", "EIAJ", "6BAE892D9D5003069ECAC135630A79FCFD6AB1B55EC96DAB5D2151EF0423811A")]
[InlineData(14, "PAL", "VCR", "DCFD099B3D99C54B79C722F61C9CBA623F3646DA35A0CE73E6E710E4C767C002")]
[InlineData(15, "PAL", "VCR_LP", "9CBEE9C0BE2BC4A91FCC7930240CBE3EA85896352E6270AE0071288DEDB684DB")]
[InlineData(16, "PAL", "VIDEO2000", "E5F7E5A4A4F275B9027CF9BADB3A8723A0EBECFA77812FF3AAFB54ACACBE39B5")]
[InlineData(17, "PAL", "VHSHQ", "B4636C71044769326039D759C6B9F61AC7FFDE90964761879E161EA241DD405B")]
[InlineData(18, "PAL", "SVHS", "CEFA28AFCE2BF3A2A4FB6BCF7177AE43A77A6A707E4C0892DCD40CF3F8EFEE86")]
[InlineData(19, "PAL", "SVHS_ET", "F745052A75D0FEBAD2C95C0B3C3B85323076B6E899C9ECB6B3A3F853DC3DCA34")]
[InlineData(20, "NTSC", "UMATIC", "18834718B936D8DB2B1598831E38F29AE61ED4964A029D1EC564273EEFEC44E5")]
[InlineData(21, "NTSC", "VHSHQ", "1C503EAA76D4F968585F98CD3502B2CE365758E7E0DCD134AC05DD17FDA5994B")]
[InlineData(22, "NTSC", "SVHS", "97AC5FC2F8AADC8104A271C9B2E2FA961F246143C78FC754F0463F4E9E23225F")]
[InlineData(23, "NTSC", "SVHS_ET", "2BA7F57E26DC932AE19905D7654E1F30313E448EED995A3949AD601AC5CB1006")]
[InlineData(24, "NLINHA", "VHS", "B35A743D46E45AA1DF82BD4D3ADC0AC055454437849EC85515687DC23D730421")]
[InlineData(25, "PAL", "VHS", "61033C0998FF4B024670D5F0C4074CB8EF49613CA974621ABA38518A60CE3EEE")]
[InlineData(26, "NTSC", "VHS", "B4ACBFE26CE1DD6F2D69C53C30E922B8D287E1EAF4E40BAFB57F8CEDB1EC01C6")]
public void FormatSpecificChromaFieldMatchesReleaseBits(
    int caseIndex,
    string system,
    string format,
    string expectedHash)
{
    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters(system, format, "sp");
    TbcFrameSpec frameSpec = TbcFrameSpec.FromParameters(parameters);
    bool chromaDeemphasis = format is "VIDEO8" or "HI8";
    var decodeOptions = new ChromaDecodeOptions(
        IsColorUnder: true,
        WriteChroma: true,
        SkipChroma: false,
        UseChromaAfc: false,
        DisableComb: system == "MESECAM",
        ChromaDeemphasisFilter: chromaDeemphasis,
        ChromaAudioNotch: false,
        ChromaOffsetSamples: 0,
        DetectChromaTrackPhase: false,
        EnableColorKiller: false,
        DisableBurstHsync: false,
        DisablePhaseCorrection: false,
        UseOldRawChromaOutput: false);
    VhsChromaFieldOptions options = TbcFieldDecodePipeline.BuildChromaFieldOptions(
        system,
        parameters,
        frameSpec,
        decodeOptions)
        ?? throw new InvalidOperationException($"Chroma options were not built for {system} {format}.");

    var chroma = new double[frameSpec.FieldSampleCount];
    uint state = unchecked(0x12345678u + ((uint)caseIndex * 0x01020304u));
    for (int i = 0; i < chroma.Length; i++)
    {
        state = unchecked((state * 1_664_525u) + 1_013_904_223u);
        int signed = (int)((state >> 8) & 0xFFFFu) - 32_768;
        chroma[i] = (float)((signed / 8.0) + (((i % 17) - 8) * 0.03125));
    }

    int lineOffset = FormatCatalog.ParentSystem(system) == "NTSC" ? 1 : 3;
    ChromaPhaseLine[] phaseLines = Enumerable.Range(0, frameSpec.OutputLineCount)
        .Select(index =>
        {
            int line = lineOffset + index;
            return new ChromaPhaseLine(
                line,
                ((line * 3) + caseIndex + 1) % 4,
                ((line * 17) + (caseIndex * 23)) % 360 - 180.0,
                BurstPhaseOffsetDegrees: 0.0,
                BurstMagnitude: 30_000.0,
                I: 30_000.0,
                Q: 0.0);
        })
        .ToArray();
    var phase = new ChromaPhaseSequenceResult(
        NextChromaRotationIndex: 0,
        phaseLines,
        BurstDetectedLine: 0,
        BurstMagnitudeAverage: 30_000.0,
        BurstPhaseAverageDegrees: 0.0,
        EvenBurstPhaseAverageDegrees: 0.0,
        OddBurstPhaseAverageDegrees: 0.0);

    VhsChromaFieldResult result = VhsChromaDecoder.DecodeFieldWithPhase(
        chroma,
        options,
        phase,
        isFirstField: true,
        fieldNumber: 0,
        lineOffset: lineOffset);
    string actualHash = Convert.ToHexString(SHA256.HashData(
        TbcOutputWriter.ToLittleEndianBytes(result.Samples)));
    AssertEqual($"{system} {format}: {expectedHash}", $"{system} {format}: {actualHash}");
}

[Fact(DisplayName = "PAL Betamax chroma AFC field matches v0.4.0 bits")]
public void PalBetamaxChromaAfcFieldMatchesReleaseBits()
{
    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters("PAL", "BETAMAX", "sp");
    TbcFrameSpec frameSpec = TbcFrameSpec.FromParameters(parameters);
    var decodeOptions = new ChromaDecodeOptions(
        IsColorUnder: true,
        WriteChroma: true,
        SkipChroma: false,
        UseChromaAfc: true,
        DisableComb: false,
        ChromaDeemphasisFilter: false,
        ChromaAudioNotch: false,
        ChromaOffsetSamples: 5,
        DetectChromaTrackPhase: false,
        EnableColorKiller: false,
        DisableBurstHsync: false,
        DisablePhaseCorrection: false,
        UseOldRawChromaOutput: false);
    VhsChromaFieldOptions options = TbcFieldDecodePipeline.BuildChromaFieldOptions(
        "PAL",
        parameters,
        frameSpec,
        decodeOptions,
        decodeSampleRateHz: 40_000_000.0)
        ?? throw new InvalidOperationException("Chroma options were not built for PAL Betamax.");

    var chroma = new double[frameSpec.FieldSampleCount];
    uint state = 0xCAFEBABEu;
    for (int i = 0; i < chroma.Length; i++)
    {
        state = unchecked((state * 1_664_525u) + 1_013_904_223u);
        double noise = ((int)((state >> 16) & 0xFFFFu) - 32_768) / 256.0;
        double square = ((i * 40) & 1023) < 512 ? 3_000.0 : -3_000.0;
        chroma[i] = (float)(square + noise);
    }

    const int LineOffset = 3;
    ChromaPhaseLine[] phaseLines = Enumerable.Range(0, frameSpec.OutputLineCount)
        .Select(index =>
        {
            int line = LineOffset + index;
            return new ChromaPhaseLine(
                line,
                ((line * 3) + 2) % 4,
                ((line * 17) + 11) % 360 - 180.0,
                BurstPhaseOffsetDegrees: 0.0,
                BurstMagnitude: 30_000.0,
                I: 30_000.0,
                Q: 0.0);
        })
        .ToArray();
    var phase = new ChromaPhaseSequenceResult(
        NextChromaRotationIndex: 0,
        phaseLines,
        BurstDetectedLine: 0,
        BurstMagnitudeAverage: 30_000.0,
        BurstPhaseAverageDegrees: 0.0,
        EvenBurstPhaseAverageDegrees: 0.0,
        OddBurstPhaseAverageDegrees: 0.0);

    VhsChromaFieldResult result = VhsChromaDecoder.DecodeFieldWithPhase(
        chroma,
        options,
        phase,
        isFirstField: true,
        fieldNumber: 0,
        lineOffset: LineOffset);

    ChromaCarrierEstimate carrier = result.CarrierEstimate
        ?? throw new InvalidOperationException("PAL Betamax chroma AFC did not estimate a carrier.");
    AssertEqual(0x4124E70A0A0C8458UL, BitConverter.DoubleToUInt64Bits(carrier.CarrierHz));
    AssertEqual(0xC0A40DF5F37BA800UL, BitConverter.DoubleToUInt64Bits(carrier.OffsetHz));
    AssertEqual(0UL, BitConverter.DoubleToUInt64Bits(carrier.PhaseRadians));
    AssertEqual(
        "0A4872FDB67D33A999D8F74BA5701FDD6DC3D26FD9B94248D2D6DBC7178A300E",
        DoubleBitsSha256(options.ChromaPreSosFilter!.SelectMany(section => new[]
        {
            section.B0,
            section.B1,
            section.B2,
            section.A0,
            section.A1,
            section.A2
        }).ToArray()));
    double[] rawPrefiltered = SosFilter.ApplyForwardBackward(options.ChromaPreSosFilter!, chroma);
    AssertEqual(
        "B41DA0F37284E6271D41790BED83A69DA89FBE13E8A28D83419607124A36EC6B",
        DoubleBitsSha256(rawPrefiltered));
    double[] prefiltered = VhsChromaDecoder.ApplyChromaPreFilter(chroma, options);
    AssertEqual(
        "7030D3DF464EFB398474BA43ECB1B1BF224FBC299CA09922F1F51762D969A357",
        DoubleBitsSha256(prefiltered));
    double[] mixed = VhsChromaDecoder.UpconvertChroma(
        prefiltered,
        LineOffset,
        frameSpec.OutputLineLength,
        phase.PhaseSequence,
        VhsChromaDecoder.BuildHeterodyneTable(
            prefiltered.Length,
            options.FscMHz,
            carrier.CarrierHz / 1_000_000.0,
            options.FscMHz * 4.0,
            carrier.PhaseRadians));
    AssertEqual(
        "89DA11D0A4277D7B5532133D0FFE59D7B0CB27EA2F1E25CFDF3344A8FE3EAFBE",
        FloatBitsSha256(mixed));
    double[] filtered = SosFilter.ApplyForwardBackwardFloat32(options.FinalSosFilter!, mixed);
    AssertEqual(
        "01287D5AE385CD82DBFFEB4E3003411C7ECA84442E4B2EDED5100D6588483EA0",
        FloatBitsSha256(filtered));
    double[] combined = VhsChromaDecoder.ApplyPalComb(
        filtered,
        frameSpec.OutputLineLength,
        retainFloat32: true);
    AssertEqual(
        "E20514D67A9066387E0C5EA09E80A42AE6319DEEEDB0447A183CFA39640C03E6",
        FloatBitsSha256(combined));
    double[] gained = VhsChromaDecoder.ApplyAutomaticChromaGain(
        combined,
        options.BurstAbsRef,
        options.BurstStart,
        options.BurstEnd,
        options.OutputLineLength,
        options.OutputLineCount,
        burstDetectedLine: 0,
        useFloat32Rms: true).Samples;
    AssertEqual(
        "18A0B3EB6B99CE174B1D0643C65C86C7BDD749268C0433EFA5AFA0BE86A6070A",
        DoubleBitsSha256(gained));
    AssertEqual(
        "9EA84B99076A817D74D9D4C7A55A5D5E5FC385104DB8E5F9B5B866AB772917BE",
        Convert.ToHexString(SHA256.HashData(TbcOutputWriter.ToLittleEndianBytes(result.Samples))));
}

[Fact(DisplayName = "PAL Video8 chroma AFC notch stages match v0.4.0 bits")]
public void PalVideo8ChromaAfcNotchStagesMatchReleaseBits()
{
    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters("PAL", "VIDEO8", "sp");
    TbcFrameSpec frameSpec = TbcFrameSpec.FromParameters(parameters);
    var decodeOptions = new ChromaDecodeOptions(
        IsColorUnder: true,
        WriteChroma: true,
        SkipChroma: false,
        UseChromaAfc: true,
        DisableComb: false,
        ChromaDeemphasisFilter: true,
        ChromaAudioNotch: true,
        ChromaOffsetSamples: 5,
        DetectChromaTrackPhase: false,
        EnableColorKiller: false,
        DisableBurstHsync: false,
        DisablePhaseCorrection: false,
        UseOldRawChromaOutput: false);
    var filterOptions = new DecodeFilterOptions(
        VideoNotchHz: 2_500_000.0,
        VideoNotchQ: 20.0,
        UseChromaAfc: true);
    VhsChromaFieldOptions options = TbcFieldDecodePipeline.BuildChromaFieldOptions(
        "PAL",
        parameters,
        frameSpec,
        decodeOptions,
        filterOptions,
        decodeSampleRateHz: 40_000_000.0)
        ?? throw new InvalidOperationException("Chroma options were not built for PAL Video8.");

    double[] chroma = Enumerable.Range(0, frameSpec.FieldSampleCount)
        .Select(index => (((index * 37) % 1009 - 504) / 16.0) + Math.ScaleB(index + 1.0, -38))
        .ToArray();
    AssertEqual(
        "B6E7F9D11E7590251DF679134348A993A965100237785106073111D30426D661",
        DoubleBitsSha256(chroma));
    AssertEqual(4, options.ChromaPreFilterMoveSamples);
    AssertEqual(
        "C1B4827F13A540C821CE52B1B7949ED0CC8DD353948F23202AFF291CAD14DEAE",
        DoubleBitsSha256(options.ChromaPreSosFilter!.SelectMany(section => new[]
        {
            section.B0,
            section.B1,
            section.B2,
            section.A0,
            section.A1,
            section.A2
        }).ToArray()));
    double[] prefiltered = SosFilter.ApplyForwardBackward(options.ChromaPreSosFilter!, chroma);
    AssertEqual(
        "8DCADDC1D99E48EF097E27C2A6ABDE576716E2E4CB6B242311C8C5BB9E216BED",
        DoubleBitsSha256(prefiltered));

    TransferFunction audioNotch = options.ChromaAudioNotchFilter
        ?? throw new InvalidOperationException("PAL Video8 chroma audio notch was not built.");
    AssertEqual(
        "EBD8C993F4A13029975F960A48757CDB900D794B5C480AC552753A362F50D164",
        DoubleBitsSha256(audioNotch.Numerator));
    AssertEqual(
        "DD55346011633A862B26189FEDFAC818E3C7F6DBB5918E7530FDC93FA676B2C6",
        DoubleBitsSha256(audioNotch.Denominator));
    double[] afterAudio = IirFilter.ApplyForwardBackward(audioNotch, prefiltered);
    AssertEqual(
        "3A000F5F90DF82BDBEE492186E79316E09D9D910DCD41F94CA6D56F7B49304EE",
        DoubleBitsSha256(afterAudio));

    TransferFunction videoNotch = options.ChromaVideoNotchFilter
        ?? throw new InvalidOperationException("PAL Video8 chroma video notch was not built.");
    AssertEqual(
        "B430632801F26DE0FE7A47C64AD1F21BA473E4AB6804623E9F780D7B1E05D284",
        DoubleBitsSha256(videoNotch.Numerator));
    AssertEqual(
        "D9361FA6F897C3E657C2F2949E847FAA1B66FCC03A1415CA37CB08F666D16084",
        DoubleBitsSha256(videoNotch.Denominator));
    double[] afterVideo = IirFilter.ApplyForwardBackward(videoNotch, afterAudio);
    AssertEqual(
        "D610B01E5B9063DA016A038154DEBB2F7C623498EAE71512515A18F9CE42A1DC",
        DoubleBitsSha256(afterVideo));

    double[] shifted = VhsChromaDecoder.ApplyChromaPreFilter(chroma, options);
    AssertEqual(
        "8850F30E8DCCA202ACE4FF9ED1FB27E3A84625979B39FED8575D6724C1D43231",
        DoubleBitsSha256(shifted));

    const int LineOffset = 3;
    ChromaPhaseLine[] phaseLines = Enumerable.Range(0, frameSpec.OutputLineCount)
        .Select(index =>
        {
            int line = LineOffset + index;
            return new ChromaPhaseLine(
                line,
                ((line * 3) + 2) % 4,
                ((line * 17) + 11) % 360 - 180.0,
                BurstPhaseOffsetDegrees: 0.0,
                BurstMagnitude: 30_000.0,
                I: 30_000.0,
                Q: 0.0);
        })
        .ToArray();
    var phase = new ChromaPhaseSequenceResult(
        NextChromaRotationIndex: 0,
        phaseLines,
        BurstDetectedLine: 0,
        BurstMagnitudeAverage: 30_000.0,
        BurstPhaseAverageDegrees: 0.0,
        EvenBurstPhaseAverageDegrees: 0.0,
        OddBurstPhaseAverageDegrees: 0.0);
    VhsChromaFieldResult result = VhsChromaDecoder.DecodeFieldWithPhase(
        chroma,
        options,
        phase,
        isFirstField: true,
        fieldNumber: 0,
        lineOffset: LineOffset);
    ChromaCarrierEstimate carrier = result.CarrierEstimate
        ?? throw new InvalidOperationException("PAL Video8 chroma AFC did not estimate a carrier.");
    AssertEqual(0x41265A0BC0000000UL, BitConverter.DoubleToUInt64Bits(carrier.NominalCarrierHz));
    AssertEqual(0x41265970D7EDC111UL, BitConverter.DoubleToUInt64Bits(carrier.CarrierHz));
    AssertEqual(0xC0535D0247DDE000UL, BitConverter.DoubleToUInt64Bits(carrier.OffsetHz));
    AssertEqual(0UL, BitConverter.DoubleToUInt64Bits(carrier.PhaseRadians));

    double[] upconverted = VhsChromaDecoder.UpconvertChroma(
        shifted,
        LineOffset,
        frameSpec.OutputLineLength,
        phase.PhaseSequence,
        VhsChromaDecoder.BuildHeterodyneTable(
            shifted.Length,
            options.FscMHz,
            carrier.CarrierHz / 1_000_000.0,
            options.FscMHz * 4.0,
            carrier.PhaseRadians));
    AssertEqual(
        "041DF1E00CF5B76A566AD045AF96BF0E266782BE583AAC6583116BCFDB0D0EEE",
        FloatBitsSha256(upconverted));
    double[] final = SosFilter.ApplyForwardBackwardFloat32(options.FinalSosFilter!, upconverted);
    AssertEqual(
        "2D98CAD5F0FC9798C48F7C46B988CF352568BE5646D9287912900F7DD12D9B81",
        FloatBitsSha256(final));

    TransferFunction deemphasis = options.ChromaDeemphasisFilter
        ?? throw new InvalidOperationException("PAL Video8 chroma deemphasis was not built.");
    AssertEqual(
        "EE08B71006DADC75E379AF7D3BDE25DA4CB5260F31A68642DB3D6552B784D708",
        DoubleBitsSha256(deemphasis.Numerator));
    AssertEqual(
        "A8E9E38D1AB7F9FCAA428AF95168A8A1C5C9576681DE8F5A34D415E27572CD47",
        DoubleBitsSha256(deemphasis.Denominator));
    double[] deemphasized = IirFilter.ApplyForward(deemphasis, final);
    AssertEqual(
        "DA42D875C0FBDFE4E7E390103F9B3F647C3FD2908EDC82EB8A931B6E0893AAB0",
        DoubleBitsSha256(deemphasized));
    double[] combined = VhsChromaDecoder.ApplyPalComb(
        deemphasized,
        frameSpec.OutputLineLength,
        retainFloat32: false);
    AssertEqual(
        "C884AA2F0882D5A3FB6697766C5AE1622D29C33473BA93641E1E4DAC4D39D97F",
        DoubleBitsSha256(combined));
    AutomaticChromaGainResult gained = VhsChromaDecoder.ApplyAutomaticChromaGain(
        combined,
        options.BurstAbsRef,
        options.BurstStart,
        options.BurstEnd,
        options.OutputLineLength,
        options.OutputLineCount,
        burstDetectedLine: 0,
        useFloat32Rms: false);
    int line16Start = 16 * options.OutputLineLength;
    AssertEqual(0x3FB55C4B60C8F0D5UL, BitConverter.DoubleToUInt64Bits(combined[line16Start]));
    AssertEqual(0x40B181A0E988718EUL, BitConverter.DoubleToUInt64Bits(gained.Samples[line16Start]));
    AssertEqual(0x3FC431611A5BC9C4UL, BitConverter.DoubleToUInt64Bits(gained.MeanBurstRms));
    AssertEqual(
        "9BA09CDA0429425FA616D47ADDEE36420305E77ACB1C7590B3EE810A5F665482",
        DoubleBitsSha256(gained.Samples));
    AssertEqual(
        "7E2DC277018708D982DF3C518807118D8E30CF0597661B2B16D0DB042A2CDED0",
        Convert.ToHexString(SHA256.HashData(TbcOutputWriter.ToLittleEndianBytes(result.Samples))));
}

[Fact(DisplayName = "NTSC Video8 chroma AFC notch field matches v0.4.0 bits")]
public void NtscVideo8ChromaAfcNotchFieldMatchesReleaseBits()
{
    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters("NTSC", "VIDEO8", "sp");
    TbcFrameSpec frameSpec = TbcFrameSpec.FromParameters(parameters);
    var decodeOptions = new ChromaDecodeOptions(
        IsColorUnder: true,
        WriteChroma: true,
        SkipChroma: false,
        UseChromaAfc: true,
        DisableComb: false,
        ChromaDeemphasisFilter: true,
        ChromaAudioNotch: true,
        ChromaOffsetSamples: 5,
        DetectChromaTrackPhase: false,
        EnableColorKiller: false,
        DisableBurstHsync: false,
        DisablePhaseCorrection: false,
        UseOldRawChromaOutput: false);
    var filterOptions = new DecodeFilterOptions(
        VideoNotchHz: 2_500_000.0,
        VideoNotchQ: 20.0,
        UseChromaAfc: true);
    VhsChromaFieldOptions options = TbcFieldDecodePipeline.BuildChromaFieldOptions(
        "NTSC",
        parameters,
        frameSpec,
        decodeOptions,
        filterOptions,
        decodeSampleRateHz: 40_000_000.0)
        ?? throw new InvalidOperationException("Chroma options were not built for NTSC Video8.");

    AssertEqual(70, options.BurstStart);
    AssertEqual(122, options.BurstEnd);
    AssertEqual(3, options.ChromaPreFilterMoveSamples);
    AssertEqual(4_100.0, options.BurstAbsRef);
    double[] chroma = Enumerable.Range(0, frameSpec.FieldSampleCount)
        .Select(index => (((index * 37) % 1009 - 504) / 16.0) + Math.ScaleB(index + 1.0, -38))
        .ToArray();
    AssertEqual(
        "0DEC95390F9EA9D4C5D107BE5735DB6D4F388A328DE359000BC9B23535111F26",
        DoubleBitsSha256(chroma));
    AssertEqual(
        "C1B4827F13A540C821CE52B1B7949ED0CC8DD353948F23202AFF291CAD14DEAE",
        DoubleBitsSha256(options.ChromaPreSosFilter!.SelectMany(section => new[]
        {
            section.B0,
            section.B1,
            section.B2,
            section.A0,
            section.A1,
            section.A2
        }).ToArray()));
    double[] rawPrefiltered = SosFilter.ApplyForwardBackward(options.ChromaPreSosFilter!, chroma);
    AssertEqual(
        "7B1DF5C0A54B4E19AC3358B433DA17090F0B087AD5E871C478EFE4E645206DEC",
        DoubleBitsSha256(rawPrefiltered));

    TransferFunction audioNotch = options.ChromaAudioNotchFilter
        ?? throw new InvalidOperationException("NTSC Video8 chroma audio notch was not built.");
    AssertEqual(
        "536717C5CDBBD6D3156A8F6AE9206E84A83AB7D7BDC52BD683E263646C5BBF07",
        DoubleBitsSha256(audioNotch.Numerator));
    AssertEqual(
        "0D879E20592216C09AC2C00CDC93ACAF1F7E0FC72B7B12EFD77AB54984F72B53",
        DoubleBitsSha256(audioNotch.Denominator));
    TransferFunction videoNotch = options.ChromaVideoNotchFilter
        ?? throw new InvalidOperationException("NTSC Video8 chroma video notch was not built.");
    AssertEqual(
        "7BB4EEB22A771B14DDA878BA3DE8FC6C58FF5B85BD2006633380BC4C983CC8AA",
        DoubleBitsSha256(videoNotch.Numerator));
    AssertEqual(
        "7E240C5672C10A594698EF75390D2C61AB541AC278E620241FED5189849D9703",
        DoubleBitsSha256(videoNotch.Denominator));
    double[] shifted = VhsChromaDecoder.ApplyChromaPreFilter(chroma, options);
    AssertEqual(
        "5A58E5EBCB401031D876E98D7C17F475CE32D217304F2556E0EBDEC0939C511A",
        DoubleBitsSha256(shifted));

    const int LineOffset = 1;
    ChromaPhaseLine[] phaseLines = Enumerable.Range(0, frameSpec.OutputLineCount)
        .Select(index =>
        {
            int line = LineOffset + index;
            return new ChromaPhaseLine(
                line,
                ((line * 3) + 2) % 4,
                ((line * 17) + 11) % 360 - 180.0,
                BurstPhaseOffsetDegrees: 0.0,
                BurstMagnitude: 30_000.0,
                I: 30_000.0,
                Q: 0.0);
        })
        .ToArray();
    var phase = new ChromaPhaseSequenceResult(
        NextChromaRotationIndex: 0,
        phaseLines,
        BurstDetectedLine: 0,
        BurstMagnitudeAverage: 30_000.0,
        BurstPhaseAverageDegrees: 0.0,
        EvenBurstPhaseAverageDegrees: 0.0,
        OddBurstPhaseAverageDegrees: 0.0);
    VhsChromaFieldResult result = VhsChromaDecoder.DecodeFieldWithPhase(
        chroma,
        options,
        phase,
        isFirstField: true,
        fieldNumber: 0,
        lineOffset: LineOffset);
    AssertEqual(1, result.FieldPhaseId);
    ChromaCarrierEstimate carrier = result.CarrierEstimate
        ?? throw new InvalidOperationException("NTSC Video8 chroma AFC did not estimate a carrier.");
    AssertEqual(0x4126A389808FC2E8UL, BitConverter.DoubleToUInt64Bits(carrier.CarrierHz));
    AssertEqual(0xC0993D3829E03C00UL, BitConverter.DoubleToUInt64Bits(carrier.OffsetHz));
    AssertEqual(0UL, BitConverter.DoubleToUInt64Bits(carrier.PhaseRadians));

    double[] burstDeemphasized = VhsChromaDecoder.ApplyBurstDeemphasis(
        shifted,
        LineOffset,
        frameSpec.OutputLineCount,
        frameSpec.OutputLineLength,
        options.BurstStart,
        options.BurstEnd);
    AssertEqual(
        "7402948922F352B6F278BC5C3A2AAC75F3BC0DAF05C7D844C1AAFF93C1A0218D",
        DoubleBitsSha256(burstDeemphasized));
    double[] upconverted = VhsChromaDecoder.UpconvertChromaPhaseCompensated(
        burstDeemphasized,
        LineOffset,
        frameSpec.OutputLineLength,
        phase.PhaseSequence,
        options.ColorUnderCarrierHz,
        options.FscMHz,
        targetPhaseEvenDegrees: -33.0,
        targetPhaseOddDegrees: -33.0);
    AssertEqual(
        "A4A6D4BB874F1581F783562FB3A5158859A3A1EF126E0694B14CE97CCB938109",
        FloatBitsSha256(upconverted));
    double[] final = SosFilter.ApplyForwardBackwardFloat32(options.FinalSosFilter!, upconverted);
    AssertEqual(
        "4298B29BBCBEFAD3FA65827DBE0F64CE307C9DFB04BCE0EEF74D47799B0FD9D5",
        FloatBitsSha256(final));

    TransferFunction deemphasis = options.ChromaDeemphasisFilter
        ?? throw new InvalidOperationException("NTSC Video8 chroma deemphasis was not built.");
    AssertEqual(
        "CB8C8CE40001180E6B0FC224AB24715F674BD9352567822238CDC26B1C449FB4",
        DoubleBitsSha256(deemphasis.Numerator));
    AssertEqual(
        "13D6AA3FC2CA709B0BE2F47F5852C312E62ED577151DA9049925A4F2C5E4CB36",
        DoubleBitsSha256(deemphasis.Denominator));
    double[] deemphasized = IirFilter.ApplyForward(deemphasis, final);
    AssertEqual(
        "312339E85BACF17572EF28914B49C8AEE43DA03CCD2E49B530AA367673B77391",
        DoubleBitsSha256(deemphasized));
    double[] combined = VhsChromaDecoder.ApplyNtscComb(
        deemphasized,
        frameSpec.OutputLineLength,
        retainFloat32: false);
    AssertEqual(
        "0BBCE9403D67AAEF2C9463192EEB8245AAEDF106D21FA3FC37A32BD0984175DC",
        DoubleBitsSha256(combined));
    AutomaticChromaGainResult gained = VhsChromaDecoder.ApplyAutomaticChromaGain(
        combined,
        options.BurstAbsRef,
        options.BurstStart,
        options.BurstEnd,
        options.OutputLineLength,
        options.OutputLineCount,
        burstDetectedLine: 0,
        useFloat32Rms: false);
    AssertEqual(0x3FC4DCC0311381E5UL, BitConverter.DoubleToUInt64Bits(gained.MeanBurstRms));
    AssertEqual(
        "30817DCD29D76CFE8C3BB4024519DB6EF3FB80F0B9626925F9FA2796B009BF67",
        DoubleBitsSha256(gained.Samples));
    AssertEqual(
        "A639D0AD8D38590162FDF8D5345A5F06FE5DB90BAD5B4B36A9CE5E195A74C183",
        Convert.ToHexString(SHA256.HashData(TbcOutputWriter.ToLittleEndianBytes(result.Samples))));
}

[Fact(DisplayName = "VHS chroma AFC tracks carrier offset")]
public void VhsChromaAfcTracksCarrierOffset()
{
    const double sampleRateHz = 4_000_000.0;
    const double nominalHz = 1_000_000.0;
    const double carrierHz = 1_007_812.5;
    double[] chroma = Enumerable.Range(0, 4096)
        .Select(i => Math.Cos(Math.Tau * carrierHz * i / sampleRateHz))
        .ToArray();

    ChromaCarrierEstimate estimate = VhsChromaDecoder.EstimateChromaCarrier(
        chroma,
        sampleRateHz,
        nominalHz,
        lineFrequencyHz: 20_000.0,
        fineTuneStepHz: 20_000.0)
        ?? throw new InvalidOperationException("Carrier estimate was not produced.");

    AssertClose(carrierHz, estimate.PeakCarrierHz, 1e-6);
    AssertClose(carrierHz, estimate.CarrierHz, 1e-6);
    AssertClose(carrierHz - nominalHz, estimate.OffsetHz, 1e-6);

    double[] competingPeaks = Enumerable.Range(0, 4096)
        .Select(i =>
            (0.7 * Math.Cos(Math.Tau * nominalHz * i / sampleRateHz))
            + Math.Cos(Math.Tau * carrierHz * i / sampleRateHz))
        .ToArray();
    ChromaCarrierEstimate closest = VhsChromaDecoder.EstimateChromaCarrier(
        competingPeaks,
        sampleRateHz,
        nominalHz,
        lineFrequencyHz: 20_000.0,
        fineTuneStepHz: 20_000.0)
        ?? throw new InvalidOperationException("Competing carrier estimate was not produced.");
    AssertClose(nominalHz, closest.PeakCarrierHz, 1e-6);
    AssertClose(nominalHz, closest.CarrierHz, 1e-6);

    const double outOfBandPeakHz = 1_250_000.0;
    double[] outOfBand = Enumerable.Range(0, 4096)
        .Select(i => Math.Cos(Math.Tau * outOfBandPeakHz * i / sampleRateHz))
        .ToArray();
    ChromaCarrierEstimate clipped = VhsChromaDecoder.EstimateChromaCarrier(
        outOfBand,
        sampleRateHz,
        nominalHz,
        lineFrequencyHz: 20_000.0,
        fineTuneStepHz: 20_000.0)
        ?? throw new InvalidOperationException("Out-of-band carrier estimate was not produced.");
    AssertClose(outOfBandPeakHz, clipped.PeakCarrierHz, 1e-6);
    AssertTrue(clipped.CarrierHz >= 960_000.0 && clipped.CarrierHz <= 1_040_000.0);

    const double ntscOutputRateHz = (315_000_000.0 / 88.0) * 4.0;
    const int precisionBin = 179;
    double precisionCarrierHz = BitConverter.Int64BitsToDouble(unchecked((long)0x41231872a62e8ba4UL));
    double[] precisionCarrier = Enumerable.Range(0, 4096)
        .Select(i => Math.Cos(Math.Tau * precisionCarrierHz * i / ntscOutputRateHz))
        .ToArray();
    ChromaCarrierEstimate precisionEstimate = VhsChromaDecoder.EstimateChromaCarrier(
        precisionCarrier,
        ntscOutputRateHz,
        precisionCarrierHz,
        lineFrequencyHz: 15_734.0,
        fineTuneStepHz: 4_000.0)
        ?? throw new InvalidOperationException("Precision carrier estimate was not produced.");
    AssertEqual(
        BitConverter.DoubleToInt64Bits(precisionCarrierHz),
        BitConverter.DoubleToInt64Bits(precisionEstimate.PeakCarrierHz));
    AssertEqual(precisionBin, (int)Math.Round(precisionEstimate.PeakCarrierHz / (1.0 / (4096 * (1.0 / ntscOutputRateHz)))));
}

[Fact(DisplayName = "RF demodulator applies sharpness EQ")]
public void RfDemodulatorAppliesSharpnessEq()
{
    const int n = 128;
    const double sampleRate = 128.0;
    double[] mixed = Enumerable.Range(0, n)
        .Select(i => 10.0
            + Math.Sin(Math.Tau * 4.0 * i / sampleRate)
            + (0.25 * Math.Sin(Math.Tau * 32.0 * i / sampleRate)))
        .ToArray();
    double[] sharpened = RfDemodulator.ApplySharpnessEq(
        mixed,
        sampleRate,
        new SharpnessEqOptions(Level: 0.5, CornerHz: 16.0, TransitionHz: 4.0, OrderLimit: 2));

    double[] dc = Enumerable.Repeat(10.0, n).ToArray();
    double[] dcSharpened = RfDemodulator.ApplySharpnessEq(
        dc,
        sampleRate,
        new SharpnessEqOptions(Level: 0.5, CornerHz: 16.0, TransitionHz: 4.0, OrderLimit: 2));
    for (int i = 10; i < dcSharpened.Length; i++)
    {
        AssertClose(10.0, dcSharpened[i], 1e-9);
    }

    AssertTrue(AmplitudeAtBin(sharpened, bin: 32) > AmplitudeAtBin(mixed, bin: 32) * 1.5);
    AssertTrue(AmplitudeAtBin(sharpened, bin: 4) < AmplitudeAtBin(mixed, bin: 4) * 1.25);
    AssertThrows<ArgumentOutOfRangeException>(() => RfDemodulator.ApplySharpnessEq(
        mixed,
        sampleRate,
        new SharpnessEqOptions(Level: 0.5, CornerHz: sampleRate / 2.0, TransitionHz: 4.0, OrderLimit: 2)));

    var statefulDemodulator = new RfDemodulator(sampleRate);
    var statefulOptions = new SharpnessEqOptions(Level: 0.5, CornerHz: 16.0, TransitionHz: 4.0, OrderLimit: 2);
    double[] firstStateful = statefulDemodulator.ApplySharpnessEqStateful(mixed, statefulOptions);
    double[] secondStateful = statefulDemodulator.ApplySharpnessEqStateful(mixed, statefulOptions);
    AssertTrue(firstStateful[..10].Where((value, index) =>
        BitConverter.DoubleToInt64Bits(value) != BitConverter.DoubleToInt64Bits(secondStateful[index])).Any());
    for (int i = 10; i < mixed.Length; i++)
    {
        AssertEqual(
            BitConverter.DoubleToInt64Bits(firstStateful[i]),
            BitConverter.DoubleToInt64Bits(secondStateful[i]));
    }

    const double vhs17Point9SampleRate = 17_900_000.0;
    const double vhs17Point9Nyquist = vhs17Point9SampleRate / 2.0;
    (int vhs17Point9Order, double vhs17Point9Cutoff) = IirFilterDesign.ButterworthLowPassOrder(
        2_620_000.0 / vhs17Point9Nyquist,
        3_120_000.0 / vhs17Point9Nyquist,
        3.0,
        30.0);
    AssertEqual(17, vhs17Point9Order);
    AssertEqual(4598945745717935080L, BitConverter.DoubleToInt64Bits(vhs17Point9Cutoff));
    TransferFunction vhs17Point9Filter = IirFilterDesign.ButterworthHighPassTransferFunction(
        vhs17Point9Order,
        vhs17Point9Cutoff);
    AssertEqual(
        "36079D664FA54271CD212A1CE5C8297565B6CB1713D2EE18977FEF15CEADEB74",
        DoubleBitsSha256(vhs17Point9Filter.Numerator));
    AssertEqual(
        "C93A519F4E268ABB2E9155B67093F001A2360F3C9DBB3726F5F1960E21266572",
        DoubleBitsSha256(vhs17Point9Filter.Denominator));
    AssertEqual(
        "CB44D5BE6BFC51F6B3EE6F192A995724A766111574C16CE70E9BF9FAB9F0A3D0",
        DoubleBitsSha256(IirFilter.SteadyStateInitialConditions(vhs17Point9Filter)));

    const double vhsSampleRate = 40_000_000.0;
    const double vhsNyquist = vhsSampleRate / 2.0;
    (int vhsOrder, double vhsCutoff) = IirFilterDesign.ButterworthLowPassOrder(
        2_620_000.0 / vhsNyquist,
        3_120_000.0 / vhsNyquist,
        3.0,
        30.0);
    TransferFunction vhsFilter = IirFilterDesign.ButterworthHighPassTransferFunction(vhsOrder, vhsCutoff);
    AssertEqual(
        "D341D4551C29058C87B5EA724290B02B44DEDBCAC39573DEA73D003757979401",
        DoubleBitsSha256(vhsFilter.Numerator));
    AssertEqual(
        "697B2DAF9C1DBDE7DB4B15E2EFA9CB177214A37369F6250CAC9C84F9DAF3A158",
        DoubleBitsSha256(vhsFilter.Denominator));
    AssertEqual(
        "CF47B3A6CCA5F62A2DEA8D5037E2250775AD778630C00A8779059FA6DA599F30",
        DoubleBitsSha256(IirFilter.SteadyStateInitialConditions(vhsFilter)));

    double[] vhsInput = Enumerable.Range(0, 32 * 1024)
        .Select(i => 3_000_000.0 + (((i * 37.0) % 101.0) * 1_000.0))
        .ToArray();
    double[] vhsSharpened = RfDemodulator.ApplySharpnessEq(
        vhsInput,
        vhsSampleRate,
        new SharpnessEqOptions(0.5, 2_620_000.0, 500_000.0, 20));
    AssertEqual(
        "CC66AFB0124BAFA71F6606042456182217602B640B095F6C5D36A5E24EBF9A63",
        DoubleBitsSha256(vhsSharpened));
}

[Fact(DisplayName = "RF demodulator applies nonlinear deemphasis")]
public void RfDemodulatorAppliesNonlinearDeemphasis()
{
    const int n = 128;
    const double sampleRate = 128.0;
    double[] video = Enumerable.Range(0, n)
        .Select(i => Math.Sin(Math.Tau * 4.0 * i / sampleRate)
            + Math.Sin(Math.Tau * 32.0 * i / sampleRate))
        .ToArray();
    Complex[] videoSpectrum = FastFourierTransform.Forward(video);
    var options = new NonlinearDeemphasisOptions(
        HighPassHz: 16.0,
        BandPassUpperHz: null,
        Order: 2,
        LimitLow: -0.25,
        LimitHigh: 0.25);
    double[] processed = RfDemodulator.ApplyNonlinearDeemphasis(video, videoSpectrum, sampleRate, options);

    AssertTrue(AmplitudeAtBin(processed, bin: 32) < AmplitudeAtBin(video, bin: 32));
    AssertTrue(AmplitudeAtBin(processed, bin: 4) > AmplitudeAtBin(video, bin: 4) * 0.95);
    for (int i = 0; i < video.Length; i++)
    {
        double removed = video[i] - processed[i];
        AssertTrue(removed >= options.LimitLow - 1e-9);
        AssertTrue(removed <= options.LimitHigh + 1e-9);
    }

    AssertThrows<ArgumentException>(() => RfDemodulator.ApplyNonlinearDeemphasis(
        video,
        videoSpectrum.AsSpan(0, videoSpectrum.Length - 1).ToArray(),
        sampleRate,
        options));
    AssertThrows<ArgumentOutOfRangeException>(() => RfDemodulator.ApplyNonlinearDeemphasis(
        video,
        videoSpectrum,
        sampleRate,
        options with { HighPassHz = sampleRate / 2.0 }));
}

[Fact(DisplayName = "RF demodulator applies sub-deemphasis")]
public void RfDemodulatorAppliesSubDeemphasis()
{
    const int n = 128;
    const double sampleRate = 128.0;
    double[] video = Enumerable.Range(0, n)
        .Select(i => Math.Sin(Math.Tau * 4.0 * i / sampleRate)
            + Math.Sin(Math.Tau * 32.0 * i / sampleRate))
        .ToArray();
    Complex[] videoSpectrum = FastFourierTransform.Forward(video);
    var options = new SubDeemphasisOptions(
        HighPassHz: 16.0,
        BandPassUpperHz: null,
        Order: 2,
        AmplitudeLowPassHz: 8.0,
        Deviation: 4.0,
        ExponentialScaling: 1.0,
        Scaling1: null,
        Scaling2: null,
        LogisticMid: null,
        LogisticRate: null,
        StaticFactor: null);

    double[] processed = RfDemodulator.ApplySubDeemphasis(video, videoSpectrum, sampleRate, options);

    AssertTrue(AmplitudeAtBin(processed, bin: 32) < AmplitudeAtBin(video, bin: 32));
    AssertTrue(AmplitudeAtBin(processed, bin: 4) > AmplitudeAtBin(video, bin: 4) * 0.90);
    AssertTrue(processed.Zip(video, (a, b) => Math.Abs(a - b)).Average() > 0.01);

    double[] logistic = RfDemodulator.ApplySubDeemphasis(
        video,
        videoSpectrum,
        sampleRate,
        options with { LogisticMid = 0.2, LogisticRate = 14.0, Scaling1 = 0.8, Scaling2 = 1.2, StaticFactor = 0.1 });
    AssertTrue(AmplitudeAtBin(logistic, bin: 32) < AmplitudeAtBin(video, bin: 32));

    AssertThrows<ArgumentException>(() => RfDemodulator.ApplySubDeemphasis(
        video,
        videoSpectrum.AsSpan(0, videoSpectrum.Length - 1).ToArray(),
        sampleRate,
        options));
    AssertThrows<ArgumentOutOfRangeException>(() => RfDemodulator.ApplySubDeemphasis(
        video,
        videoSpectrum,
        sampleRate,
        options with { Deviation = 0.0 }));

    const int fixtureLength = 32768;
    const double fixtureSampleRate = 40_000_000.0;
    double[] fixture = Enumerable.Range(0, fixtureLength)
        .Select(index =>
            ((((index * 7919.0) % 65521.0) - 32760.0) * 32.0)
            + ((((index * 37.0) % 101.0) - 50.0) * 4096.0))
        .ToArray();
    Complex[] fixtureSpectrum = PocketFftReal.Forward(fixture);

    double[] svhs = RfDemodulator.ApplySubDeemphasisReal(
        fixture,
        fixtureSpectrum,
        fixtureSampleRate,
        new SubDeemphasisOptions(
            HighPassHz: 320_000.0,
            BandPassUpperHz: null,
            Order: 1,
            AmplitudeLowPassHz: 590_000.0,
            Deviation: 1_600_000.0,
            ExponentialScaling: 0.23,
            Scaling1: null,
            Scaling2: 0.72,
            LogisticMid: 0.2,
            LogisticRate: 14.0,
            StaticFactor: null));
    AssertClose(-1_017_798.6896995121, svhs[0], 1e-8);
    AssertClose(281_293.7336982782, svhs[1024], 1e-8);
    AssertClose(-886_929.7006694467, svhs[32533], 1e-8);
    AssertClose(-238_660.32260580471, svhs[^1], 1e-8);

    double[] betamax = RfDemodulator.ApplySubDeemphasisReal(
        fixture,
        fixtureSpectrum,
        fixtureSampleRate,
        new SubDeemphasisOptions(
            HighPassHz: 662_300.0,
            BandPassUpperHz: 4_500_000.0,
            Order: 1,
            AmplitudeLowPassHz: 700_000.0,
            Deviation: 1_400_000.0,
            ExponentialScaling: 0.25,
            Scaling1: 0.7,
            Scaling2: null,
            LogisticMid: null,
            LogisticRate: null,
            StaticFactor: null));
    AssertClose(-1_183_587.7581180683, betamax[0], 1e-8);
    AssertClose(-534_215.2407867257, betamax[10], 1e-8);
    AssertClose(1_104_548.9812005796, betamax[27734], 1e-8);
    AssertClose(772_657.0510404956, betamax[32599], 1e-8);
    AssertClose(-275_657.39673635113, betamax[^1], 1e-8);
}

[Fact(DisplayName = "RF demodulator removes LD PAL V4300D spur")]
public void RfDemodulatorRemovesLdPalV4300DSpur()
{
    const int length = 4096;
    const double sampleRateHz = 40_000_000.0;
    var spectrum = new Complex[length];
    int start = (int)(length * (8_420_000.0 / sampleRateHz));
    int end = (int)(1.0 + (length * (8_600_000.0 / sampleRateHz)));
    for (int i = start; i < end; i++)
    {
        spectrum[i] = Complex.One;
        spectrum[length - i] = Complex.One;
    }

    int spur = (int)Math.Round(length * (8_500_000.0 / sampleRateHz));
    spectrum[spur] = new Complex(100.0, 0.0);
    spectrum[length - spur] = new Complex(100.0, 0.0);

    Complex[] cleaned = RfDemodulator.RemoveLdPalV4300DSpur(spectrum, sampleRateHz);

    AssertEqual(new Complex(100.0, 0.0), spectrum[spur]);
    AssertEqual(Complex.Zero, cleaned[spur - 1]);
    AssertEqual(Complex.Zero, cleaned[spur]);
    AssertEqual(Complex.Zero, cleaned[spur + 1]);
    AssertEqual(Complex.Zero, cleaned[length - (spur - 1)]);
    AssertEqual(Complex.Zero, cleaned[length - spur]);
    AssertEqual(Complex.Zero, cleaned[length - (spur + 1)]);
    AssertEqual(Complex.One, cleaned[start]);
    AssertEqual(Complex.One, cleaned[end - 1]);
    AssertThrows<ArgumentOutOfRangeException>(() => RfDemodulator.RemoveLdPalV4300DSpur(spectrum, 0.0));
}

[Fact(DisplayName = "ported unwrap hilbert recovers instantaneous frequency")]
public void PortedUnwrapHilbertRecoversInstantaneousFrequency()
{
    double sampleRate = 8.0;
    Complex[] analytic =
    [
        Complex.FromPolarCoordinates(1.0, 0.0),
        Complex.FromPolarCoordinates(1.0, Math.PI / 2),
        Complex.FromPolarCoordinates(1.0, Math.PI),
        Complex.FromPolarCoordinates(1.0, -Math.PI / 2),
        Complex.FromPolarCoordinates(1.0, 0.0)
    ];
    double[] result = PortedMath.UnwrapHilbert(analytic, sampleRate);
    AssertSequence([0.0, 2.0, 2.0, 2.0, 2.0], result);

    Complex[] wrapped =
    [
        Complex.FromPolarCoordinates(1.0, 7.0 * Math.PI / 4.0),
        Complex.FromPolarCoordinates(1.0, Math.PI / 4.0)
    ];
    AssertSequence([0.0, 2.0], PortedMath.UnwrapHilbert(wrapped, sampleRate));

    Complex[] nonUnit = [new(1, 2), new(-3, 4), new(-2, -5), new(7, -1)];
    double[] conjugateExpected = [0.0, 1.409665529398267, 2.6961931748400927, 2.3038068251599078];
    AssertSequence(conjugateExpected, PortedMath.UnwrapHilbertConjugateProduct(nonUnit, sampleRate));
    AssertSequence(conjugateExpected, PortedMath.UnwrapHilbert(nonUnit, sampleRate));

    Complex[] vhsRustInput =
    [
        new(0.0, 0.0),
        new(1.0, 0.0),
        new(0.0, 1.0),
        new(-1.0, 0.0),
        new(0.0, -1.0),
        new(1.0, 2.0),
        new(-3.0, 4.0),
        new(-2.0, -5.0),
        new(7.0, -1.0),
        new(0.125, -0.75),
        new(-0.25, 0.5),
        new(3.25, 1.5),
        new(-4.5, -2.25),
        new(1e-20, 2e-20),
        new(-1e-20, 3e-20),
        new(12345.678, -9876.543)
    ];
    ulong[] expectedVhsRustBits =
    [
        0UL,
        0UL,
        4706563005637197824UL,
        4706563005637197824UL,
        4706563006174068736UL,
        4709949726936530944UL,
        4704027848577384448UL,
        4708235600656859136UL,
        4707292908485607424UL,
        4713938717541138432UL,
        4711523152771940352UL,
        4713421323232083968UL,
        4711114384628252672UL,
        4712050829232701440UL,
        4702059408694181888UL,
        4711944988353626112UL
    ];
    double[] vhsRust = PortedMath.UnwrapHilbertVhsRustApproximation(vhsRustInput, 17_900_000.0);
    for (int i = 0; i < vhsRust.Length; i++)
    {
        AssertEqual(expectedVhsRustBits[i], BitConverter.DoubleToUInt64Bits(vhsRust[i]));
    }
}

[Fact(DisplayName = "ported unwrap matches numpy-style behavior")]
public void PortedUnwrapMatchesExpectedBehavior()
{
    double[] result = PortedMath.UnwrapAngles([0.0, 0.5 * Math.PI, -0.75 * Math.PI, -0.5 * Math.PI]);
    AssertClose(0.0, result[0], 1e-12);
    AssertClose(0.5 * Math.PI, result[1], 1e-12);
    AssertClose(1.25 * Math.PI, result[2], 1e-12);
    AssertClose(1.5 * Math.PI, result[3], 1e-12);
}

[Fact(DisplayName = "ported diff forward matches ediff1d to_begin zero")]
public void PortedDiffForwardMatchesExpectedBehavior()
{
    double[] data = [3.0, 7.0, 2.0, 10.0];
    PortedMath.DiffForwardInPlace(data);
    AssertSequence([0.0, 4.0, -5.0, 8.0], data);
}

[Fact(DisplayName = "ported Hilbert and super-Gaussian helpers match expected shape")]
public void PortedFilterHelpersMatchExpectedShape()
{
    AssertSequence([1, 2, 2, 2, 1, 0, 0, 0], PortedMath.BuildHilbertMultiplier(8));
    AssertThrows<ArgumentException>(() => PortedMath.BuildHilbertMultiplier(7));

    double center = PortedMath.SuperGaussian(5.0, frequency: 4.0, order: 2, centerFrequency: 5.0);
    double edge = PortedMath.SuperGaussian(1.0, frequency: 4.0, order: 2, centerFrequency: 5.0);
    AssertClose(1.0, center, 1e-12);
    AssertTrue(edge < center);

    double[] envelope = PortedMath.GenerateBandPassSuperGaussian(
        frequencyLow: 2.0,
        frequencyHigh: 6.0,
        order: 2,
        nyquistHz: 8.0,
        blockLength: 8);
    AssertEqual(8, envelope.Length);
    AssertClose(envelope[0], envelope[7], 1e-12);
    AssertClose(envelope[1], envelope[6], 1e-12);
    AssertClose(envelope[2], envelope[5], 1e-12);
    AssertClose(envelope[3], envelope[4], 1e-12);
    AssertTrue(envelope[2] > envelope[0]);
}

[Fact(DisplayName = "frequency-domain filter helpers build masks and apply responses")]
public void FrequencyDomainFilterHelpersBuildMasksAndApplyResponses()
{
    double[] lowPass = FrequencyDomainFilter.LowPassSuperGaussianHalf(
        cornerFrequency: 4.0,
        order: 2,
        nyquistHz: 8.0,
        blockLength: 8);
    AssertEqual(5, lowPass.Length);
    AssertClose(1.0, lowPass[0], 1e-12);
    AssertTrue(lowPass[4] < lowPass[2]);

    double[] bandPass = FrequencyDomainFilter.BandPassSuperGaussianHalf(
        lowFrequency: 2.0,
        highFrequency: 6.0,
        order: 2,
        nyquistHz: 8.0,
        blockLength: 8);
    AssertClose(1.0, bandPass[2], 1e-12);
    AssertTrue(bandPass[0] < bandPass[2]);

    AssertSequence([1, 2, 3, 4, 3, 2], FrequencyDomainFilter.MirrorHalfToFull([1, 2, 3, 4]));
    AssertSequence([0, 0, 1.0, 2.0, 2.0, 1.0, 0, 0], FrequencyDomainFilter.RampFilter(
        startFrequencyHz: 10_000_000.0,
        boostStart: 1.0,
        boostMax: 2.0,
        nyquistHz: 20_000_000.0,
        blockLength: 8));

    Complex[] spectrum = [new Complex(1, 1), new Complex(2, 0)];
    Complex[] filtered = FrequencyDomainFilter.Apply(spectrum, [2.0, 0.5]);
    AssertComplexSequence([new Complex(2, 2), new Complex(1, 0)], filtered, 1e-12);
    AssertSequence([3, 4, 1, 2], FrequencyDomainFilter.Roll([1, 2, 3, 4], 2));
    AssertSequence([2, 3, 4, 1], FrequencyDomainFilter.Roll([1, 2, 3, 4], -1));
}

[Fact(DisplayName = "IIR filter design builds Butterworth and notch filters")]
public void IirFilterDesignBuildsButterworthAndNotchFilters()
{
    (int ldAudioOrder, double ldAudioCutoff) = IirFilterDesign.ButterworthLowPassOrder(
        normalizedPassFrequency: 0.032,
        normalizedStopFrequency: 0.0384,
        passRippleDb: 1.0,
        stopAttenuationDb: 9.0);
    AssertEqual(10, ldAudioOrder);
    AssertClose(0.03423247992877569, ldAudioCutoff, 1e-15);

    SosSection[] firstLow = IirFilterDesign.ButterworthLowPass(order: 1, normalizedCutoff: 0.5);
    AssertEqual(1, firstLow.Length);
    AssertClose(0.5, firstLow[0].B0, 1e-12);
    AssertClose(0.5, firstLow[0].B1, 1e-12);
    AssertClose(0.0, firstLow[0].A1, 1e-12);

    SosSection[] firstHigh = IirFilterDesign.ButterworthHighPass(order: 1, normalizedCutoff: 0.5);
    AssertClose(0.5, firstHigh[0].B0, 1e-12);
    AssertClose(-0.5, firstHigh[0].B1, 1e-12);

    SosSection rfTop = IirFilterDesign.ButterworthBandPassSos(
        order: 1,
        normalizedLowCutoff: 4_500_000.0 / 8_950_000.0,
        normalizedHighCutoff: 5_400_000.0 / 8_950_000.0)[0];
    AssertEqual(
        "B7948BB50B0C9B2535A4FF62BACE4BBC98D4DFD66225B0A148A213677BA7DA0D",
        DoubleBitsSha256(
        [
            rfTop.B0,
            rfTop.B1,
            rfTop.B2,
            rfTop.A0,
            rfTop.A1,
            rfTop.A2
        ]));

    SosSection[] secondLow = IirFilterDesign.ButterworthLowPass(order: 2, normalizedCutoff: 0.5);
    AssertEqual(1, secondLow.Length);
    AssertClose(0.2928932188134525, secondLow[0].B0, 1e-12);
    AssertClose(0.585786437626905, secondLow[0].B1, 1e-12);
    AssertClose(0.1715728752538099, secondLow[0].A2, 1e-12);

    Complex[] lowResponse = IirFilterDesign.FrequencyResponse(secondLow, 8, whole: true);
    AssertClose(1.0, lowResponse[0].Magnitude, 1e-12);
    AssertTrue(lowResponse[2].Magnitude < lowResponse[1].Magnitude);

    Complex[] highResponse = IirFilterDesign.FrequencyResponse(
        IirFilterDesign.ButterworthHighPass(order: 2, normalizedCutoff: 0.5),
        8,
        whole: true);
    AssertClose(0.0, highResponse[0].Magnitude, 1e-12);
    AssertTrue(highResponse[2].Magnitude > highResponse[1].Magnitude);

    TransferFunction notch = IirFilterDesign.Notch(normalizedFrequency: 0.5, q: 10.0);
    AssertEqual(
        "E8C77271026DA202C2709B1429D6DF71128FB1D69AE150EC58E5D8E53AC27282",
        DoubleBitsSha256(notch.Numerator));
    AssertEqual(
        "F1003E750661821F95167DD2756A32171AFF2625AC56264F56BD8AF1C249625E",
        DoubleBitsSha256(notch.Denominator));
    Complex[] notchResponse = IirFilterDesign.FrequencyResponse(notch, 8, whole: true);
    AssertTrue(notchResponse[2].Magnitude < 1e-10);
    AssertTrue(notchResponse[0].Magnitude > 0.99);

    TransferFunction bandStop = IirFilterDesign.ButterworthBandStop(order: 3, normalizedLowCutoff: 0.2, normalizedHighCutoff: 0.3);
    Complex[] bandStopResponse = IirFilterDesign.FrequencyResponse(bandStop, 1024, whole: true);
    AssertTrue(bandStopResponse[0].Magnitude > 0.99);
    AssertTrue(bandStopResponse[128].Magnitude < 0.05);
    AssertTrue(bandStopResponse[260].Magnitude > 0.95);

    TransferFunction bandPass = IirFilterDesign.ButterworthBandPass(order: 3, normalizedLowCutoff: 0.2, normalizedHighCutoff: 0.3);
    Complex[] bandPassResponse = IirFilterDesign.FrequencyResponse(bandPass, 1024, whole: true);
    SosSection[] bandPassSos = IirFilterDesign.ButterworthBandPassSos(order: 3, normalizedLowCutoff: 0.2, normalizedHighCutoff: 0.3);
    Complex[] bandPassSosResponse = IirFilterDesign.FrequencyResponse(bandPassSos, 1024, whole: true);
    AssertEqual(3, bandPassSos.Length);
    AssertTrue(bandPassResponse[0].Magnitude < 1e-8);
    AssertTrue(bandPassResponse[128].Magnitude > 0.95);
    AssertTrue(bandPassResponse[260].Magnitude < 0.05);
    for (int i = 0; i < bandPassResponse.Length; i++)
    {
        AssertClose(bandPassResponse[i].Magnitude, bandPassSosResponse[i].Magnitude, 1e-8);
    }

    TransferFunction ntscVhsBandPass = IirFilterDesign.ButterworthBandPass(
        order: 8,
        normalizedLowCutoff: 500_000.0 / 20_000_000.0,
        normalizedHighCutoff: 6_500_000.0 / 20_000_000.0);
    AssertEqual(
        "5A14DA56D71AFABC38CE2C0270D32F5B05D163D195E1C1EC8983EAE692BA679E",
        DoubleBitsSha256(ntscVhsBandPass.Numerator));
    AssertEqual(
        "4DA6E81E8DA9095AFC90D374CBEE670ABE4F315DDA9E67BC5ABB28B23017C598",
        DoubleBitsSha256(ntscVhsBandPass.Denominator));

    AssertThrows<ArgumentOutOfRangeException>(() => IirFilterDesign.ButterworthLowPass(0, 0.5));
    AssertThrows<ArgumentOutOfRangeException>(() => IirFilterDesign.Notch(1.0, 10.0));
    AssertThrows<ArgumentException>(() => IirFilterDesign.ButterworthBandStop(3, 0.3, 0.2));
    AssertThrows<ArgumentException>(() => IirFilterDesign.ButterworthBandPass(3, 0.3, 0.2));
}

[Fact(DisplayName = "IIR filter design builds constant-Q peaking filters")]
public void IirFilterDesignBuildsConstantQPeakingFilters()
{
    TransferFunction boost = IirFilterDesign.PeakingConstantQ(
        normalizedFrequency: 0.5,
        gainDb: 6.0,
        bandwidthOctaves: 0.5);
    Complex[] boostResponse = IirFilterDesign.FrequencyResponse(boost, 8, whole: true);
    AssertClose(1.0, boostResponse[0].Magnitude, 1e-12);
    AssertClose(Math.Pow(10.0, 6.0 / 20.0), boostResponse[2].Magnitude, 1e-12);
    AssertClose(1.0, boostResponse[4].Magnitude, 1e-12);

    TransferFunction cut = IirFilterDesign.PeakingConstantQ(
        normalizedFrequency: 0.5,
        gainDb: -6.0,
        bandwidthOctaves: 0.5);
    Complex[] cutResponse = IirFilterDesign.FrequencyResponse(cut, 8, whole: true);
    AssertClose(Math.Pow(10.0, -6.0 / 20.0), cutResponse[2].Magnitude, 1e-12);
    AssertThrows<ArgumentOutOfRangeException>(() => IirFilterDesign.PeakingConstantQ(0.5, 6.0, 0.0));
}

[Fact(DisplayName = "IIR filter design builds video deemphasis shelves")]
public void IirFilterDesignBuildsVideoDeemphasisShelves()
{
    TransferFunction highShelf = IirFilterDesign.Shelf(
        centerFrequencyHz: 2.0,
        gainDb: 6.0,
        kind: ShelfKind.High,
        sampleRateHz: 8.0,
        q: 0.5);
    Complex[] highShelfResponse = IirFilterDesign.FrequencyResponse(highShelf, 8, whole: true);
    AssertTrue(highShelfResponse[0].Magnitude < highShelfResponse[3].Magnitude);

    TransferFunction deemp = IirFilterDesign.VideoDeEmphasisShelf(
        sampleRateHz: 8.0,
        gainDb: 6.0,
        midpointHz: 2.0,
        q: 0.5);
    Complex[] deempResponse = IirFilterDesign.FrequencyResponse(deemp, 8, whole: true);
    AssertTrue(deempResponse[0].Magnitude > deempResponse[3].Magnitude);
    AssertThrows<ArgumentOutOfRangeException>(() => IirFilterDesign.Shelf(5.0, 6.0, ShelfKind.High, 8.0, 0.5));
}

[Fact(DisplayName = "FIR frequency response matches SciPy DUCC packet FFT")]
public void FirFrequencyResponseMatchesScipyDuccPacketFft()
{
    Complex[] response = IirFilterDesign.FrequencyResponse(
        new TransferFunction([0.25, 0.75], [1.0]),
        32_768,
        whole: true);
    AssertEqual(
        "7E82F70A283DFB70224AFDB2BF844EF2B0DB667966511650CD7406E3DA97B001",
        ComplexBitsSha256(response.AsSpan(0, 16_385)));
}

[Fact(DisplayName = "VHS video low-pass matches SciPy SOS response bits")]
public void VhsVideoLowPassMatchesScipySosResponseBits()
{
    const int blockLength = 32_768;
    SosSection[] sections = IirFilterDesign.ButterworthLowPassScipySos(
        order: 6,
        normalizedCutoff: 5_200_000.0 / 20_000_000.0);
    AssertEqual(
        "030CEA3602790BB439DCD6FBE34EB5F35C770BA152D959C0A7825653BD71A87E",
        DoubleBitsSha256(sections.SelectMany(section => new[]
        {
            section.B0,
            section.B1,
            section.B2,
            section.A0,
            section.A1,
            section.A2
        }).ToArray()));

    Complex[] response = IirFilterDesign.FrequencyResponse(sections, blockLength);
    AssertEqual(
        "02A0BB695FE1B7A001012161FC502DBB4E80E9B79DA4C1B558E28A5DD570598E",
        ComplexBitsSha256(response.AsSpan(0, (blockLength / 2) + 1)));

    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters("PAL", "QUADRUPLEX", "sp");
    DecodeFilterSet filters = DecodeFilterSetBuilder.BuildBasic(
        parameters,
        sampleRateHz: 40_000_000.0,
        blockLength);
    double[] magnitude = filters.VideoLowPass
        .AsSpan(0, (blockLength / 2) + 1)
        .ToArray()
        .Select(value => value.Real)
        .ToArray();
    AssertEqual(
        "448F993297D9C20DDCB15802F79A0CC9536926EEFD7954A2D60B38B0AE27B8DE",
        DoubleBitsSha256(magnitude));
}

[Fact(DisplayName = "sub-deemphasis analytic magnitude matches SciPy hilbert bits")]
public void SubDeemphasisAnalyticMagnitudeMatchesScipyHilbertBits()
{
    const int length = 32_768;
    var input = new double[length];
    for (ulong i = 0; i < (ulong)input.Length; i++)
    {
        input[(int)i] = (unchecked((i * 1_103_515_245UL) + 12_345UL) & 0xffffUL) - 32_768.0;
    }

    AssertEqual(
        "2AD6816EF4E085A2C65950897ACD97863248F3152377DE6F17322CB38AA9ED13",
        DoubleBitsSha256(RfDemodulator.BuildAnalyticMagnitude(input)));
}

[Fact(DisplayName = "complex FFT matches SciPy DUCC packet transforms")]
public void ComplexFftMatchesScipyDuccPacketTransforms()
{
    const int length = 32_768;
    var input = new Complex[length];
    for (ulong i = 0; i < (ulong)input.Length; i++)
    {
        double real = (unchecked((i * 1_103_515_245UL) + 12_345UL) & 0xffffUL) - 32_768.0;
        double imaginary = ((unchecked((i * 2_654_435_761UL) + 1_013_904_223UL) & 0xffffUL) - 32_768.0) * 0.25;
        input[(int)i] = new Complex(real, imaginary);
    }

    Complex[] forward = PocketFftComplex.ForwardDucc(input);
    AssertEqual(
        "FF623DBBEAEEFE8422AC5DA3281A3D4C6CC954E7C0ED73A0F160FF1F7C812134",
        ComplexBitsSha256(forward));
    AssertEqual(
        "F3B5C7DCE8BF5BC11B087BD2930FFC3D6EA656DC1196344B995EC192218E6B96",
        ComplexBitsSha256(PocketFftComplex.InverseDucc(forward)));
}

[Fact(DisplayName = "real full FFT matches SciPy DUCC packet transform")]
public void RealFullFftMatchesScipyDuccPacketTransform()
{
    const int length = 32_768;
    var input = new double[length];
    for (ulong i = 0; i < (ulong)input.Length; i++)
    {
        input[(int)i] = (unchecked((i * 1_103_515_245UL) + 12_345UL) & 0xffffUL) - 32_768.0;
    }

    Complex[] spectrum = PocketFftComplex.ForwardDuccRealFull(input);
    AssertEqual(
        "5ED16316814D498111E0A6EE33EDED7E0862CBD7ACBACD9D00DA13C8125FACF4",
        ComplexBitsSha256(spectrum));
    AssertEqual(
        BitConverter.DoubleToInt64Bits(-0.0),
        BitConverter.DoubleToInt64Bits(spectrum[0].Imaginary));
    AssertEqual(
        BitConverter.DoubleToInt64Bits(-0.0),
        BitConverter.DoubleToInt64Bits(spectrum[length / 2].Imaginary));
}

[Fact(DisplayName = "LD IIR filters match SciPy 1.18 bits")]
public void LaserDiscIirFiltersMatchScipy18Bits()
{
    TransferFunction videoLowPass = IirFilterDesign.ButterworthLowPassTransferFunction(
        order: 7,
        normalizedCutoff: 5_800_000.0 / 20_000_000.0);
    AssertEqual(
        "330DABE20E63227AC920B082C47AD60D9510E58FCEFADC2C3BC40635D82E2D76",
        DoubleBitsSha256(videoLowPass.Numerator));
    AssertEqual(
        "43B80CA9955D7FD32F468AF225F1775A2C4874DDA74BA71B653BAE2D93155E48",
        DoubleBitsSha256(videoLowPass.Denominator));
    AssertEqual(
        "117E917EFACCF2211FE4CE705D02D472530B8752CEF95FC462E7AC3F7D100C69",
        ComplexBitsSha256(IirFilterDesign.FrequencyResponse(videoLowPass, 32_768)));

    TransferFunction rfLowPass = IirFilterDesign.ButterworthLowPassTransferFunction(
        order: 3,
        normalizedCutoff: 14_000_000.0 / 20_000_000.0);
    AssertEqual(
        "D5D55F5E68E61BEE04034B426E2DDC86C297D1B2D50C78BC835DCFDF4E2FD001",
        DoubleBitsSha256(rfLowPass.Numerator));
    AssertEqual(
        "E35C5B36DE2A4261E4E4D6082395D489F79689B5CC833F2CC83A907D303EF035",
        DoubleBitsSha256(rfLowPass.Denominator));
    AssertEqual(
        "5E8B76A504F7484FB0A04016F3993C8AB8E94E3446C8E333ECF1D27C6F891D1C",
        ComplexBitsSha256(IirFilterDesign.FrequencyResponse(rfLowPass, 32_768)));

    TransferFunction deemphasis = IirFilterDesign.EmphasisIir(
        zeroTimeConstant: 100e-9,
        poleTimeConstant: 400e-9,
        sampleRateHz: 40_000_000.0);
    AssertEqual(
        "E2EF9503A35F287431765D364614BC90D90BDBE09F3B0813B47C479AA07A54FC",
        DoubleBitsSha256(deemphasis.Numerator));
    AssertEqual(
        "AFC5EFD261856C97E7ED87FBF4759C50ED89B27EBFD40322B3B67F0FC86E9461",
        DoubleBitsSha256(deemphasis.Denominator));
    AssertEqual(
        "BF1845F29E85D81BCA34BC0069536F58BADFDC40AE5AB1A2DE06F1A55CE3561D",
        ComplexBitsSha256(IirFilterDesign.FrequencyResponse(deemphasis, 32_768)));
}

[Fact(DisplayName = "IIR filter design builds LD emphasis filters")]
public void IirFilterDesignBuildsLdEmphasisFilters()
{
    TransferFunction deemp = IirFilterDesign.EmphasisIir(
        zeroTimeConstant: 1.0e-6,
        poleTimeConstant: 3.0e-6,
        sampleRateHz: 40_000_000.0);
    Complex[] response = IirFilterDesign.FrequencyResponse(deemp, 1024, whole: true);
    AssertClose(1.0, response[0].Magnitude, 1e-12);
    AssertTrue(response[100].Magnitude < response[1].Magnitude);
    AssertThrows<ArgumentOutOfRangeException>(() => IirFilterDesign.EmphasisIir(0.0, 3.0e-6, 40_000_000.0));
}

[Fact(DisplayName = "IIR filter applies forward-backward filtering")]
public void IirFilterAppliesForwardBackwardFiltering()
{
    var gain = new TransferFunction([0.5], [1.0]);
    AssertSequence([0.25, 0.5, 0.75, 1.0], IirFilter.ApplyForwardBackward(gain, [1.0, 2.0, 3.0, 4.0], padLength: 0));

    var smoother = new TransferFunction([0.5, 0.5], [1.0]);
    double[] alternating = Enumerable.Range(0, 64).Select(i => (i & 1) == 0 ? 1.0 : -1.0).ToArray();
    double[] smoothed = IirFilter.ApplyForwardBackward(smoother, alternating, padLength: 0);
    AssertTrue(MaxAbs(smoothed) < MaxAbs(alternating));
    AssertThrows<ArgumentException>(() => IirFilter.ApplyForward(new TransferFunction([], [1.0]), [1.0]));
}

[Fact(DisplayName = "decode filter-set builder creates RF and video responses")]
public void DecodeFilterSetBuilderCreatesRfAndVideoResponses()
{
    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    DecodeFilterSet palFilters = DecodeFilterSetBuilder.BuildBasic(palVhs, sampleRateHz: 40_000_000.0, blockLength: 1024);
    AssertEqual(1024, palFilters.RfVideo.Length);
    AssertEqual(1024, palFilters.RfHighPass.Length);
    AssertEqual(1024, palFilters.Video.Length);
    AssertEqual(1024, palFilters.VideoLowPass.Length);
    AssertEqual(1024, palFilters.VideoLowPass05.Length);
    AssertEqual(DecodeFilterSet.DefaultVideoLowPass05Offset, palFilters.VideoLowPass05Offset);
    AssertEqual(0, palFilters.RfHighPassOffset);
    AssertTrue(palFilters.RfVideoMagnitude[0] < 0.05);
    AssertTrue(palFilters.RfHighPassMagnitude[0] < 1e-6);
    AssertClose(Math.Sin(Math.PI / 1024.0), palFilters.RfHighPassMagnitude[1], 1e-12);
    AssertClose(Math.Sqrt(0.5), palFilters.RfHighPassMagnitude[256], 1e-12);
    AssertClose(1.0, palFilters.RfHighPassMagnitude[512], 1e-12);
    AssertTrue(Max(palFilters.RfVideoMagnitude) > 0.1);
    AssertTrue(palFilters.RfVideoMagnitude[110] > palFilters.RfVideoMagnitude[0]);
    AssertTrue(palFilters.RfVideo.All(value => value.Imaginary == 0.0));
    AssertTrue(palFilters.VideoMagnitude[10] < palFilters.VideoLowPassMagnitude[10]);
    AssertTrue(palFilters.VideoLowPass.All(value => value.Imaginary == 0.0));
    AssertTrue(palFilters.VideoLowPassMagnitude[0] > 0.99);
    AssertTrue(palFilters.VideoLowPassMagnitude[400] < palFilters.VideoLowPassMagnitude[10]);
    AssertTrue(palFilters.VideoLowPass05Magnitude[0] > 0.99);
    AssertTrue(palFilters.VideoLowPass05Magnitude[100] < palFilters.VideoLowPassMagnitude[100]);

    FormatParameterSet ntscVhs = FormatCatalog.Default.GetTapeParameters("NTSC", "VHS", "sp");
    DecodeFilterSet ntscFilters = DecodeFilterSetBuilder.BuildBasic(ntscVhs, sampleRateHz: 40_000_000.0, blockLength: 1024);
    AssertTrue(ntscFilters.VideoLowPassMagnitude[0] > 0.99);
    AssertTrue(ntscFilters.VideoLowPassMagnitude[400] < 0.05);
    DecodeFilterSet ntscReferenceFilters = DecodeFilterSetBuilder.BuildBasic(
        ntscVhs,
        sampleRateHz: 17_900_000.0,
        blockLength: 32768);
    (int Bin, double Magnitude)[] upstreamRfVideoProbes =
    [
        (2000, 0.0036885238249112291),
        (4000, 0.096185267421832601),
        (6000, 0.20772545707194212),
        (8000, 0.30408426676313194),
        (10000, 0.31567465623546165),
        (12000, 0.00049813742392739),
        (30000, 0.050278803398900694)
    ];
    foreach ((int bin, double magnitude) in upstreamRfVideoProbes)
    {
        AssertClose(magnitude, ntscReferenceFilters.RfVideoMagnitude[bin], 2e-12);
    }

    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    DecodeFilterSet ldFilters = DecodeFilterSetBuilder.BuildBasic(ldNtsc, sampleRateHz: 40_000_000.0, blockLength: 1024);
    AssertTrue(ldFilters.RfVideoMagnitude[0] < 0.05);
    AssertTrue(Max(ldFilters.RfVideoMagnitude) > 0.9);
    AssertTrue(ldFilters.RfVideo.Any(value => Math.Abs(value.Imaginary) > 1e-12));
    AssertTrue(ldFilters.VideoLowPass.Any(value => Math.Abs(value.Imaginary) > 1e-12));
    AssertTrue(ldFilters.VideoMagnitude[100] != ldFilters.VideoLowPassMagnitude[100]);

    FormatParameterSet palSvhs = FormatCatalog.Default.GetTapeParameters("PAL", "SVHS", "sp");
    DecodeFilterSet svhsFilters = DecodeFilterSetBuilder.BuildBasic(palSvhs, sampleRateHz: 40_000_000.0, blockLength: 32768);
    JsonObject svhsRfWithoutCustomNode = JsonNode.Parse(palSvhs.RfParams.GetRawText())!.AsObject();
    AssertTrue(svhsRfWithoutCustomNode.Remove("video_custom_luma_filters"));
    var palSvhsWithoutCustom = new FormatParameterSet(
        palSvhs.System,
        palSvhs.TapeFormat,
        palSvhs.TapeSpeed,
        palSvhs.SysParams,
        JsonSerializer.SerializeToElement(svhsRfWithoutCustomNode),
        palSvhs.Warnings);
    DecodeFilterSet svhsWithoutCustomFilters = DecodeFilterSetBuilder.BuildBasic(
        palSvhsWithoutCustom,
        sampleRateHz: 40_000_000.0,
        blockLength: 32768);
    AssertEqual(32768, svhsFilters.Video.Length);
    AssertEqual(0, svhsFilters.RfHighPassOffset);
    AssertTrue(svhsFilters.VideoMagnitude[200] != svhsFilters.VideoLowPassMagnitude[200]);
    AssertTrue(svhsFilters.Video.Where((value, index) => value != svhsWithoutCustomFilters.Video[index]).Any());
    AssertComplexSequence(svhsWithoutCustomFilters.VideoLowPass05, svhsFilters.VideoLowPass05, 0.0);
}

[Fact(DisplayName = "decode delay estimator applies LD fake-video filtering")]
public void DecodeDelayEstimatorAppliesLdFakeVideoFiltering()
{
    const int blockLength = 8192;
    const double sampleRateHz = 40_000_000.0;
    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    Complex[] identity = Enumerable.Repeat(Complex.One, blockLength).ToArray();
    DecodeFilterSet baselineFilters = BuildProbeFilters(identity);
    DecodeFilterSet suppressedFilters = BuildProbeFilters(new Complex[blockLength]);

    double baseline = DecodeDelayEstimator.EstimateVideoWhiteOffset(ldNtsc, baselineFilters, sampleRateHz, blockLength);
    double suppressed = DecodeDelayEstimator.EstimateVideoWhiteOffset(ldNtsc, suppressedFilters, sampleRateHz, blockLength);
    DecodeDelayEstimates combined = DecodeDelayEstimator.EstimateLaserDiscDelays(ldNtsc, baselineFilters, sampleRateHz, blockLength);

    AssertTrue(Math.Abs(baseline) < 128);
    AssertTrue(baseline != Math.Truncate(baseline));
    AssertClose(baseline, combined.VideoWhiteOffset, 0.0);
    AssertClose(
        DecodeDelayEstimator.EstimateVideoSyncOffset(ldNtsc, baselineFilters, sampleRateHz, blockLength),
        combined.VideoSyncOffset,
        0.0);
    AssertEqual(
        DecodeDelayEstimator.EstimateRfHighPassOffset(ldNtsc, baselineFilters, sampleRateHz, blockLength),
        combined.RfHighPassOffset);
    AssertTrue(double.IsFinite(suppressed));
    AssertTrue(Math.Abs(baseline - suppressed) > 1e-6);

    static DecodeFilterSet BuildProbeFilters(Complex[] videoLowPass)
    {
        Complex[] identity = Enumerable.Repeat(Complex.One, videoLowPass.Length).ToArray();
        double[] identityMagnitude = Enumerable.Repeat(1.0, videoLowPass.Length).ToArray();
        return new DecodeFilterSet(
            identity,
            identity,
            identity,
            identity,
            videoLowPass,
            identity,
            null,
            identityMagnitude,
            identityMagnitude,
            identityMagnitude,
            identityMagnitude,
            videoLowPass.Select(value => value.Magnitude).ToArray(),
            identityMagnitude,
            null);
    }

}

[Fact(DisplayName = "decode filter-set builder applies video notch options")]
public void DecodeFilterSetBuilderAppliesVideoNotchOptions()
{
    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    DecodeFilterSet withoutNotch = DecodeFilterSetBuilder.BuildBasic(palVhs, sampleRateHz: 40_000_000.0, blockLength: 1024);
    DecodeFilterSet withNotch = DecodeFilterSetBuilder.BuildBasic(
        palVhs,
        sampleRateHz: 40_000_000.0,
        blockLength: 1024,
        new DecodeFilterOptions(VideoNotchHz: 2_500_000.0, VideoNotchQ: 20.0));

    AssertTrue(withNotch.RfVideoMagnitude[64] < withoutNotch.RfVideoMagnitude[64] * 1e-6);
    const int passBandProbeBin = 100;
    AssertTrue(withNotch.RfVideoMagnitude[passBandProbeBin] > withoutNotch.RfVideoMagnitude[passBandProbeBin] * 0.95);
    AssertClose(withoutNotch.VideoMagnitude[64], withNotch.VideoMagnitude[64], 1e-12);
    AssertClose(withoutNotch.VideoLowPass05Magnitude[64], withNotch.VideoLowPass05Magnitude[64], 1e-12);
    AssertThrows<ArgumentOutOfRangeException>(() => DecodeFilterSetBuilder.BuildBasic(
        palVhs,
        sampleRateHz: 40_000_000.0,
        blockLength: 1024,
        new DecodeFilterOptions(VideoNotchHz: 20_000_000.0, VideoNotchQ: 20.0)));
}

[Fact(DisplayName = "decode filter-set builder applies chroma burst options")]
public void DecodeFilterSetBuilderAppliesChromaBurstOptions()
{
    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    const double sampleRateHz = 40_000_000.0;
    const int blockLength = 8192;
    DecodeFilterSet plain = DecodeFilterSetBuilder.BuildBasic(palVhs, sampleRateHz, blockLength);
    DecodeFilterSet notched = DecodeFilterSetBuilder.BuildBasic(
        palVhs,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(VideoNotchHz: JsonDouble(palVhs.RfParams, "color_under_carrier"), VideoNotchQ: 10.0));

    AssertTrue(plain.ChromaBurst is not null);
    AssertTrue(plain.ChromaBurstMagnitude is not null);
    AssertTrue(plain.ChromaBurstSos is not null);
    AssertFalse(plain.ChromaBurstUsesDemodulatedVideo);
    AssertEqual(4, plain.ChromaBurstSos!.Length);
    AssertEqual(null, plain.ChromaBurstAudioNotch);
    AssertEqual(null, plain.ChromaBurstVideoNotch);
    AssertTrue(notched.ChromaBurstVideoNotch is not null);
    AssertEqual(blockLength, plain.ChromaBurst!.Length);
    AssertEqual(5, plain.ChromaOffsetSamples);
    int carrierBin = FrequencyBin(JsonDouble(palVhs.RfParams, "color_under_carrier"), sampleRateHz, blockLength);
    int stopBin = FrequencyBin(5_000_000.0, sampleRateHz, blockLength);
    AssertTrue(plain.ChromaBurstMagnitude![carrierBin] > 0.5);
    AssertTrue(plain.ChromaBurstMagnitude[0] < 1e-6);
    AssertTrue(plain.ChromaBurstMagnitude[stopBin] < plain.ChromaBurstMagnitude[carrierBin] * 0.01);
    AssertClose(plain.ChromaBurstMagnitude[carrierBin], notched.ChromaBurstMagnitude![carrierBin], 1e-12);
    Complex[] chromaVideoNotch = IirFilterDesign.FrequencyResponse(notched.ChromaBurstVideoNotch!, blockLength);
    AssertTrue(chromaVideoNotch[carrierBin].Magnitude < 0.1);

    double outputRateHz = JsonDouble(palVhs.SysParams, "outfreq") * 1_000_000.0;
    TransferFunction chromaFinal = DecodeFilterSetBuilder.BuildChromaFinalFilter(palVhs, outputRateHz)!;
    SosSection[] chromaFinalSos = DecodeFilterSetBuilder.BuildChromaFinalSosFilter(palVhs, outputRateHz)!;
    Complex[] chromaFinalResponse = IirFilterDesign.FrequencyResponse(chromaFinal, blockLength);
    Complex[] chromaFinalSosResponse = IirFilterDesign.FrequencyResponse(chromaFinalSos, blockLength);
    int fscBin = FrequencyBin(JsonDouble(palVhs.SysParams, "fsc_mhz") * 1_000_000.0, outputRateHz, blockLength);
    int nyquistBin = blockLength / 2;
    AssertEqual(4, chromaFinalSos.Length);
    AssertTrue(chromaFinalResponse[fscBin].Magnitude > 0.9);
    AssertClose(chromaFinalResponse[fscBin].Magnitude, chromaFinalSosResponse[fscBin].Magnitude, 1e-8);
    AssertTrue(chromaFinalResponse[0].Magnitude < 1e-6);
    AssertTrue(chromaFinalResponse[nyquistBin].Magnitude < 0.05);
    TransferFunction chromaDeemphasis = DecodeFilterSetBuilder.BuildChromaDeemphasisFilter(palVhs, outputRateHz);
    Complex[] deemphasisResponse = IirFilterDesign.FrequencyResponse(chromaDeemphasis, blockLength);
    AssertTrue(deemphasisResponse[fscBin].Magnitude > deemphasisResponse[0].Magnitude * 1.3);
    AssertTrue(deemphasisResponse[0].Magnitude > 0.99);
    TransferFunction chromaAfcBandPass = DecodeFilterSetBuilder.BuildChromaAfcBandPassFilter(palVhs, sampleRateHz);
    Complex[] chromaAfcResponse = IirFilterDesign.FrequencyResponse(chromaAfcBandPass, blockLength);
    AssertTrue(chromaAfcResponse[carrierBin].Magnitude > 0.5);
    AssertTrue(chromaAfcResponse[0].Magnitude < 1e-6);
    AssertTrue(chromaAfcResponse[stopBin].Magnitude < chromaAfcResponse[carrierBin].Magnitude * 0.01);

    FormatParameterSet hi8 = FormatCatalog.Default.GetTapeParameters("NTSC", "HI8", "sp");
    double hi8OutputRateHz = JsonDouble(hi8.SysParams, "outfreq") * 1_000_000.0;
    TransferFunction chromaAudioNotch = DecodeFilterSetBuilder.BuildChromaAudioNotchFilter(hi8, hi8OutputRateHz)!;
    Complex[] chromaAudioNotchResponse = IirFilterDesign.FrequencyResponse(chromaAudioNotch, blockLength);
    int chromaAudioBin = FrequencyBin(JsonDouble(hi8.RfParams, "chroma_audio_notch_freq"), hi8OutputRateHz, blockLength);
    AssertTrue(chromaAudioNotchResponse[chromaAudioBin].Magnitude < 0.2);
    AssertEqual(null, DecodeFilterSetBuilder.BuildChromaAudioNotchFilter(palVhs, outputRateHz));

    FormatParameterSet typeC = FormatCatalog.Default.GetTapeParameters("PAL", "TYPEC", "sp");
    DecodeFilterSet typeCFilters = DecodeFilterSetBuilder.BuildBasic(typeC, sampleRateHz, blockLength);
    AssertTrue(typeCFilters.ChromaBurst is not null);
    AssertTrue(typeCFilters.ChromaBurstMagnitude is not null);
    AssertTrue(typeCFilters.ChromaBurstSos is not null);
    AssertTrue(typeCFilters.ChromaBurstUsesDemodulatedVideo);
    AssertEqual(4, typeCFilters.ChromaBurstSos!.Length);
    AssertEqual(
        "47F567112E4A2993642C76AF409033A479A096C79974263D4E83AEBB4098C41D",
        DoubleBitsSha256(typeCFilters.ChromaBurstSos.SelectMany(section => new[]
        {
            section.B0,
            section.B1,
            section.B2,
            section.A0,
            section.A1,
            section.A2
        }).ToArray()));
    AssertEqual(null, DecodeFilterSetBuilder.BuildChromaFinalFilter(typeC, outputRateHz));
    AssertEqual(null, DecodeFilterSetBuilder.BuildChromaFinalSosFilter(typeC, outputRateHz));

    FormatParameterSet ntscTypeC = FormatCatalog.Default.GetTapeParameters("NTSC", "TYPEC", "sp");
    DecodeFilterSet ntscTypeCFilters = DecodeFilterSetBuilder.BuildBasic(ntscTypeC, sampleRateHz, blockLength);
    AssertEqual(
        "50AB4CCCB3B5DFC004246B3E984DDB5E1FF87132A57F876F8A578E6F35FC8DA8",
        DoubleBitsSha256(ntscTypeCFilters.ChromaBurstSos!.SelectMany(section => new[]
        {
            section.B0,
            section.B1,
            section.B2,
            section.A0,
            section.A1,
            section.A2
        }).ToArray()));

    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    DecodeFilterSet ldFilters = DecodeFilterSetBuilder.BuildBasic(ldNtsc, sampleRateHz, blockLength);
    AssertEqual(null, ldFilters.ChromaBurst);
    AssertEqual(null, ldFilters.ChromaBurstMagnitude);
    AssertEqual(null, ldFilters.ChromaBurstSos);
    AssertFalse(ldFilters.ChromaBurstUsesDemodulatedVideo);
    AssertEqual(null, DecodeFilterSetBuilder.BuildChromaFinalSosFilter(ldNtsc, outputRateHz));
}

[Fact(DisplayName = "decode filter-set builder applies FM audio notch options")]
public void DecodeFilterSetBuilderAppliesFmAudioNotchOptions()
{
    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    const double sampleRateHz = 40_000_000.0;
    const int blockLength = 20_000;
    DecodeFilterSet withoutNotch = DecodeFilterSetBuilder.BuildBasic(palVhs, sampleRateHz, blockLength);
    DecodeFilterSet withNotch = DecodeFilterSetBuilder.BuildBasic(
        palVhs,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(FmAudioNotchQ: 10.0));
    DecodeFilterSet fractionalDisabled = DecodeFilterSetBuilder.BuildBasic(
        palVhs,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(FmAudioNotchQ: 0.5));

    int leftBin = FrequencyBin(JsonDouble(palVhs.RfParams, "fm_audio_channel_0_freq"), sampleRateHz, blockLength);
    int rightBin = FrequencyBin(JsonDouble(palVhs.RfParams, "fm_audio_channel_1_freq"), sampleRateHz, blockLength);

    AssertTrue(withNotch.RfVideoMagnitude[leftBin] < withoutNotch.RfVideoMagnitude[leftBin] * 1e-6);
    AssertTrue(withNotch.RfVideoMagnitude[rightBin] < withoutNotch.RfVideoMagnitude[rightBin] * 1e-6);
    AssertClose(withoutNotch.RfVideoMagnitude[leftBin], fractionalDisabled.RfVideoMagnitude[leftBin], 1e-12);
    AssertClose(withoutNotch.RfVideoMagnitude[rightBin], fractionalDisabled.RfVideoMagnitude[rightBin], 1e-12);

    FormatParameterSet ntscVhs = FormatCatalog.Default.GetTapeParameters("NTSC", "VHS", "sp");
    double[] vhs17Point9Magnitude = DecodeFilterSetBuilder.BuildFmAudioNotchMagnitude(
        JsonDouble(ntscVhs.RfParams, "fm_audio_channel_0_freq"),
        JsonDouble(ntscVhs.RfParams, "fm_audio_channel_1_freq"),
        q: 10.0,
        nyquistHz: 17_900_000.0 / 2.0,
        blockLength: 32_768);
    AssertEqual(
        "E03D50C2898303E53A1A5870578AACFE38F0838CFF3A613285B5C8BFB6A46ADB",
        DoubleBitsSha256(vhs17Point9Magnitude));
}

[Fact(DisplayName = "decode filter-set builder applies Betamax fsc notch")]
public void DecodeFilterSetBuilderAppliesBetamaxFscNotch()
{
    FormatParameterSet betamax = FormatCatalog.Default.GetTapeParameters("NTSC", "BETAMAX", "sp");
    const double sampleRateHz = 40_000_000.0;
    const int blockLength = 8192;
    double fscHz = JsonDouble(betamax.SysParams, "fsc_mhz") * 1_000_000.0;
    DecodeFilterSet plain = DecodeFilterSetBuilder.BuildBasic(betamax, sampleRateHz, blockLength);
    DecodeFilterSet notched = DecodeFilterSetBuilder.BuildBasic(
        betamax,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(BetamaxFscNotchHz: fscHz));

    int fscBin = FrequencyBin(fscHz, sampleRateHz, blockLength);
    AssertClose(plain.VideoMagnitude[fscBin], notched.VideoMagnitude[fscBin], 1e-12);
    AssertClose(plain.VideoLowPass05Magnitude[fscBin], notched.VideoLowPass05Magnitude[fscBin], 1e-12);

    double[] input = Enumerable.Range(0, blockLength)
        .Select(index =>
            Math.Sin(Math.Tau * fscHz * index / sampleRateHz)
            + Math.Sin(Math.Tau * 1_000_000.0 * index / sampleRateHz))
        .ToArray();
    double[] filtered = RfDemodulator.ApplyBetamaxFscNotch(input, sampleRateHz, fscHz);
    int lowBin = FrequencyBin(1_000_000.0, sampleRateHz, blockLength);
    AssertTrue(AmplitudeAtBin(filtered, fscBin) < AmplitudeAtBin(input, fscBin) * 0.01);
    AssertTrue(AmplitudeAtBin(filtered, lowBin) > AmplitudeAtBin(input, lowBin) * 0.95);
    AssertThrows<ArgumentOutOfRangeException>(() => RfDemodulator.ApplyBetamaxFscNotch(
        input,
        sampleRateHz,
        sampleRateHz / 2.0));
}

[Fact(DisplayName = "decode filter-set builder applies LD MTF options")]
public void DecodeFilterSetBuilderAppliesLdMtfOptions()
{
    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    DecodeFilterSet enabled = DecodeFilterSetBuilder.BuildBasic(
        ldNtsc,
        sampleRateHz: 40_000_000.0,
        blockLength: 4096,
        new DecodeFilterOptions(LdMtfLevel: 1.0));
    DecodeFilterSet disabled = DecodeFilterSetBuilder.BuildBasic(
        ldNtsc,
        sampleRateHz: 40_000_000.0,
        blockLength: 4096,
        new DecodeFilterOptions(LdMtfLevel: 0.0));

    int mtfBin = (int)Math.Round(JsonDouble(ldNtsc.RfParams, "MTF_freq") * 1_000_000.0 / 40_000_000.0 * 4096);
    AssertEqual(4096, enabled.RfMtf.Length);
    AssertTrue(enabled.RfMtfMagnitude[mtfBin] > enabled.RfMtfMagnitude[0]);
    AssertTrue(enabled.RfMtfMagnitude[mtfBin] > disabled.RfMtfMagnitude[mtfBin]);
    AssertClose(1.0, disabled.RfMtfMagnitude[0], 1e-12);
    AssertClose(1.0, disabled.RfMtfMagnitude[mtfBin], 1e-12);

    DecodeFilterOptions scaledOptions = new(LdMtfLevel: 2.0, LdMtfOffset: 0.1);
    Complex[] dynamicHalf = DecodeFilterSetBuilder.BuildLaserDiscMtf(
        ldNtsc,
        scaledOptions,
        targetMtf: 0.5,
        sampleRateHz: 40_000_000.0,
        blockLength: 4096);
    DecodeFilterSet equivalentStatic = DecodeFilterSetBuilder.BuildBasic(
        ldNtsc,
        sampleRateHz: 40_000_000.0,
        blockLength: 4096,
        scaledOptions with { LdMtfLevel = 1.0 });
    AssertSequence(equivalentStatic.RfMtf.Select(value => value.Real).ToArray(), dynamicHalf.Select(value => value.Real).ToArray());
    AssertSequence(equivalentStatic.RfMtf.Select(value => value.Imaginary).ToArray(), dynamicHalf.Select(value => value.Imaginary).ToArray());

    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    DecodeFilterSet ignoredForVhs = DecodeFilterSetBuilder.BuildBasic(
        palVhs,
        sampleRateHz: 40_000_000.0,
        blockLength: 1024,
        new DecodeFilterOptions(LdMtfLevel: 1.0));
    AssertClose(1.0, ignoredForVhs.RfMtfMagnitude[0], 1e-12);
    AssertClose(1.0, ignoredForVhs.RfMtfMagnitude[128], 1e-12);
}

[Fact(DisplayName = "decode filter-set builder applies LD video group delay equalizer")]
public void DecodeFilterSetBuilderAppliesLdVideoGroupDelayEqualizer()
{
    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    const double sampleRateHz = 40_000_000.0;
    const int blockLength = 32768;
    DecodeFilterOptions enabledOptions = new(LdMtfLevel: 1.0);
    DecodeFilterOptions disabledOptions = enabledOptions with { LdVideoGroupDelayEqualizer = false };
    DecodeFilterSet enabled = DecodeFilterSetBuilder.BuildBasic(ldNtsc, sampleRateHz, blockLength, enabledOptions);
    DecodeFilterSet disabled = DecodeFilterSetBuilder.BuildBasic(ldNtsc, sampleRateHz, blockLength, disabledOptions);

    int chromaBin = FrequencyBin(JsonDouble(ldNtsc.SysParams, "fsc_mhz") * 1_000_000.0, sampleRateHz, blockLength);
    Complex ratio = enabled.Video[chromaBin] / disabled.Video[chromaBin];
    AssertClose(1.0, ratio.Magnitude, 1e-9);
    AssertTrue(Math.Abs(ratio.Phase) > 0.01);
    AssertClose(disabled.VideoMagnitude[chromaBin], enabled.VideoMagnitude[chromaBin], 1e-9);
    AssertClose(disabled.VideoLowPass05[chromaBin].Real, enabled.VideoLowPass05[chromaBin].Real, 1e-12);
    AssertClose(disabled.VideoLowPass05[chromaBin].Imaginary, enabled.VideoLowPass05[chromaBin].Imaginary, 1e-12);

    DecodeSession defaultDeemp = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "input.s16",
        "outbase"
    ]), blockLength);
    DecodeSession weakerDeemp = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--deemp_strength",
        "0.5",
        "input.s16",
        "outbase"
    ]), blockLength);
    AssertTrue(Math.Abs(defaultDeemp.Filters.Video[chromaBin].Magnitude - weakerDeemp.Filters.Video[chromaBin].Magnitude) > 1e-6);
    AssertClose(defaultDeemp.Filters.VideoLowPass05[chromaBin].Real, weakerDeemp.Filters.VideoLowPass05[chromaBin].Real, 1e-12);
    AssertClose(defaultDeemp.Filters.VideoLowPass05[chromaBin].Imaginary, weakerDeemp.Filters.VideoLowPass05[chromaBin].Imaginary, 1e-12);

    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    DecodeFilterSet vhsEnabled = DecodeFilterSetBuilder.BuildBasic(palVhs, sampleRateHz, blockLength, enabledOptions);
    DecodeFilterSet vhsDisabled = DecodeFilterSetBuilder.BuildBasic(palVhs, sampleRateHz, blockLength, disabledOptions);
    int vhsBin = FrequencyBin(3_000_000.0, sampleRateHz, blockLength);
    AssertClose(vhsDisabled.Video[vhsBin].Real, vhsEnabled.Video[vhsBin].Real, 1e-12);
    AssertClose(vhsDisabled.Video[vhsBin].Imaginary, vhsEnabled.Video[vhsBin].Imaginary, 1e-12);
}

[Fact(DisplayName = "decode filter-set builder applies LD video burst and pilot references")]
public void DecodeFilterSetBuilderAppliesLdVideoBurstAndPilotReferences()
{
    const double sampleRateHz = 40_000_000.0;
    const int blockLength = 32768;
    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    FormatParameterSet ldPal = FormatCatalog.Default.GetLaserDiscParameters("PAL", lowBand: false);

    DecodeFilterSet ntsc = DecodeFilterSetBuilder.BuildBasic(ldNtsc, sampleRateHz, blockLength);
    AssertTrue(ntsc.LdVideoBurst is not null);
    AssertTrue(ntsc.LdVideoBurstMagnitude is not null);
    AssertEqual(blockLength, ntsc.LdVideoBurst!.Length);
    AssertEqual(DecodeFilterSet.DefaultLdVideoBurstOffset, ntsc.LdVideoBurstOffset);
    AssertEqual(null, ntsc.LdVideoPilot);
    AssertEqual(null, ntsc.LdVideoPilotMagnitude);

    int ntscFscBin = FrequencyBin(JsonDouble(ldNtsc.SysParams, "fsc_mhz") * 1_000_000.0, sampleRateHz, blockLength);
    int ntscOffBin = FrequencyBin((JsonDouble(ldNtsc.SysParams, "fsc_mhz") * 1_000_000.0) + 900_000.0, sampleRateHz, blockLength);
    AssertTrue(ntsc.LdVideoBurstMagnitude![ntscFscBin] > 0.2);
    AssertTrue(ntsc.LdVideoBurstMagnitude[0] < ntsc.LdVideoBurstMagnitude[ntscFscBin] * 0.1);
    AssertTrue(ntsc.LdVideoBurstMagnitude[ntscOffBin] < ntsc.LdVideoBurstMagnitude[ntscFscBin] * 0.2);

    DecodeSession defaultDeemp = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "input.s16",
        "outbase"
    ]), blockLength);
    DecodeSession weakerDeemp = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--deemp_strength",
        "0.5",
        "input.s16",
        "outbase"
    ]), blockLength);
    AssertClose(defaultDeemp.Filters.LdVideoBurst![ntscFscBin].Real, weakerDeemp.Filters.LdVideoBurst![ntscFscBin].Real, 1e-12);
    AssertClose(defaultDeemp.Filters.LdVideoBurst[ntscFscBin].Imaginary, weakerDeemp.Filters.LdVideoBurst[ntscFscBin].Imaginary, 1e-12);

    DecodeFilterSet pal = DecodeFilterSetBuilder.BuildBasic(ldPal, sampleRateHz, blockLength);
    AssertTrue(pal.LdVideoBurst is not null);
    AssertTrue(pal.LdVideoPilot is not null);
    AssertTrue(pal.LdVideoPilotMagnitude is not null);
    int palPilotBin = FrequencyBin(JsonDouble(ldPal.SysParams, "pilot_mhz") * 1_000_000.0, sampleRateHz, blockLength);
    int palPilotOffBin = FrequencyBin((JsonDouble(ldPal.SysParams, "pilot_mhz") * 1_000_000.0) + 600_000.0, sampleRateHz, blockLength);
    AssertTrue(pal.LdVideoPilotMagnitude![palPilotBin] > 0.2);
    AssertTrue(pal.LdVideoPilotMagnitude[0] < pal.LdVideoPilotMagnitude[palPilotBin] * 0.01);
    AssertTrue(pal.LdVideoPilotMagnitude[palPilotOffBin] < pal.LdVideoPilotMagnitude[palPilotBin] * 0.3);

    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    DecodeFilterSet vhs = DecodeFilterSetBuilder.BuildBasic(palVhs, sampleRateHz, blockLength);
    AssertEqual(null, vhs.LdVideoBurst);
    AssertEqual(null, vhs.LdVideoPilot);
}

[Fact(DisplayName = "decode filter-set builder applies LD EFM options")]
public void DecodeFilterSetBuilderAppliesLdEfmOptions()
{
    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    const double sampleRateHz = 40_000_000.0;
    const int blockLength = 32768;
    DecodeFilterSet enabled = DecodeFilterSetBuilder.BuildBasic(
        ldNtsc,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(LdDecodeDigitalAudio: true));
    DecodeFilterSet disabled = DecodeFilterSetBuilder.BuildBasic(
        ldNtsc,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(LdDecodeDigitalAudio: false));

    AssertTrue(enabled.LdEfm is not null);
    AssertTrue(enabled.LdEfmMagnitude is not null);
    AssertEqual(blockLength, enabled.LdEfm!.Length);
    AssertEqual(null, disabled.LdEfm);
    AssertEqual(null, disabled.LdEfmMagnitude);

    int centerBin = FrequencyBin(950_000.0, sampleRateHz, blockLength);
    int upperStopBin = FrequencyBin(1_900_000.0, sampleRateHz, blockLength);
    AssertTrue(enabled.LdEfmMagnitude![centerBin] > 4.0);
    AssertClose(0.0, enabled.LdEfmMagnitude[0], 1e-12);
    AssertTrue(enabled.LdEfmMagnitude[upperStopBin] < enabled.LdEfmMagnitude[centerBin] * 0.01);

    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    DecodeFilterSet ignoredForVhs = DecodeFilterSetBuilder.BuildBasic(
        palVhs,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(LdDecodeDigitalAudio: true));
    AssertEqual(null, ignoredForVhs.LdEfm);
}

[Fact(DisplayName = "decode filter-set builder applies LD analog audio options")]
public void DecodeFilterSetBuilderAppliesLdAnalogAudioOptions()
{
    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    const double sampleRateHz = 40_000_000.0;
    const int blockLength = 32768;
    DecodeFilterSet enabled = DecodeFilterSetBuilder.BuildBasic(
        ldNtsc,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(LdDecodeAnalogAudio: true));
    DecodeFilterSet disabled = DecodeFilterSetBuilder.BuildBasic(
        ldNtsc,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(LdDecodeAnalogAudio: false));

    AssertTrue(enabled.LdAnalogAudio is not null);
    LaserDiscAnalogAudioFilterSet audio = enabled.LdAnalogAudio!;
    AssertEqual(blockLength / audio.Left.BinCount, audio.DecimationFactor);
    AssertEqual(audio.Left.BinCount, audio.Left.Stage1Filter.Length);
    AssertEqual(audio.Right.BinCount, audio.Right.Stage1Filter.Length);
    AssertEqual(blockLength, audio.Left.Stage2Filter.Length);
    AssertEqual(blockLength, audio.Right.Stage2Filter.Length);
    AssertTrue(audio.Left.LowBin > 0);
    AssertTrue(audio.Right.LowBin > audio.Left.LowBin);
    AssertClose(JsonDouble(ldNtsc.SysParams, "audio_lfreq"), audio.Left.CenterFrequencyHz, 1e-9);
    AssertClose(JsonDouble(ldNtsc.SysParams, "audio_rfreq"), audio.Right.CenterFrequencyHz, 1e-9);
    AssertEqual(null, disabled.LdAnalogAudio);

    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    DecodeFilterSet ignoredForVhs = DecodeFilterSetBuilder.BuildBasic(
        palVhs,
        sampleRateHz,
        blockLength,
        new DecodeFilterOptions(LdDecodeAnalogAudio: true));
    AssertEqual(null, ignoredForVhs.LdAnalogAudio);
}

[Fact(DisplayName = "decode filter-set builder applies LD NTSC analog audio notch")]
public void DecodeFilterSetBuilderAppliesLdNtscAnalogAudioNotch()
{
    DecodeSession ntsc = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--disable_analog_audio",
        "input.s16",
        "outbase"
    ]), blockLength: 32768);
    DecodeFilterSet noNotch = DecodeFilterSetBuilder.BuildBasic(
        ntsc.Parameters,
        ntsc.DecodeSampleRateHz,
        ntsc.BlockLength,
        ntsc.FilterOptions with { LdNtscAnalogAudioNotch = false });

    int leftBin = FrequencyBin(JsonDouble(ntsc.Parameters.SysParams, "audio_lfreq"), ntsc.DecodeSampleRateHz, ntsc.BlockLength);
    int rightBin = FrequencyBin(JsonDouble(ntsc.Parameters.SysParams, "audio_rfreq"), ntsc.DecodeSampleRateHz, ntsc.BlockLength);
    AssertTrue(ntsc.FilterOptions.LdNtscAnalogAudioNotch);
    AssertTrue(ntsc.Filters.RfVideoMagnitude[leftBin] < noNotch.RfVideoMagnitude[leftBin] * 1e-2);
    AssertTrue(ntsc.Filters.RfVideoMagnitude[rightBin] < noNotch.RfVideoMagnitude[rightBin] * 1e-2);
    double nyquistHz = ntsc.DecodeSampleRateHz / 2.0;
    int order = JsonInt(ntsc.Parameters.RfParams, "audio_notchorder");
    double width = JsonDouble(ntsc.Parameters.RfParams, "audio_notchwidth");
    Complex[] leftNotch = IirFilterDesign.FrequencyResponse(
        IirFilterDesign.ButterworthBandStop(
            order,
            (JsonDouble(ntsc.Parameters.SysParams, "audio_lfreq") - width) / nyquistHz,
            (JsonDouble(ntsc.Parameters.SysParams, "audio_lfreq") + width) / nyquistHz),
        ntsc.BlockLength);
    Complex[] rightNotch = IirFilterDesign.FrequencyResponse(
        IirFilterDesign.ButterworthBandStop(
            order,
            (JsonDouble(ntsc.Parameters.SysParams, "audio_rfreq") - width) / nyquistHz,
            (JsonDouble(ntsc.Parameters.SysParams, "audio_rfreq") + width) / nyquistHz),
        ntsc.BlockLength);
    int phaseProbeBin = FrequencyBin(2_500_000.0, ntsc.DecodeSampleRateHz, ntsc.BlockLength);
    Complex expected = noNotch.RfVideo[phaseProbeBin]
        * (leftNotch[phaseProbeBin] * rightNotch[phaseProbeBin]);
    AssertClose(expected.Real, ntsc.Filters.RfVideo[phaseProbeBin].Real, 1e-12);
    AssertClose(expected.Imaginary, ntsc.Filters.RfVideo[phaseProbeBin].Imaginary, 1e-12);

    DecodeSession pal = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "input.s16",
        "outbase"
    ]), blockLength: 32768);
    AssertFalse(pal.FilterOptions.LdNtscAnalogAudioNotch);
}

[Fact(DisplayName = "LD v0.4 block demodulation matches upstream float32 hashes")]
public void LaserDiscV04BlockDemodulationMatchesUpstreamFloat32Hashes()
{
    const int blockLength = 32768;
    const double sampleRateHz = 40_000_000.0;
    FormatParameterSet parameters = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    var options = new DecodeFilterOptions(
        LdNtscAnalogAudioNotch: true,
        LdDecodeDigitalAudio: false,
        LdDecodeAnalogAudio: false,
        LdMtfLevel: 1.0,
        LdMtfOffset: 0.0,
        LdClipDemodForVideo: true);
    DecodeFilterSet filters = DecodeFilterSetBuilder.BuildBasic(
        parameters,
        sampleRateHz,
        blockLength,
        options);
    var references = new RfVideoReferenceFilterSet(
        filters.LdVideoBurst,
        filters.LdVideoBurstOffset,
        filters.LdVideoPilot,
        ClipDemodForVideo: true);

    uint state = 0x12345678;
    var input = new double[blockLength];
    for (int i = 0; i < input.Length; i++)
    {
        state = unchecked((state * 1664525) + 1013904223);
        input[i] = (int)((state >> 8) & 0xFFFF) - 32768;
    }

    RfDemodulatedBlock block = new RfDemodulator(sampleRateHz).Demodulate(
        input,
        filters.RfVideo,
        filters.RfHighPass,
        filters.RfMtf,
        filters.Video,
        filters.VideoLowPass05,
        filters.VideoLowPass05Offset,
        referenceFilters: references,
        fmDemodulatorMode: RfFmDemodulatorMode.ConjugateProduct);

    AssertEqual(
        "F26441091BA02C2CCB084630A23D4BEC3FF0373715CBFA28C6EEB6FA09B69AAD",
        FloatBitsSha256(block.Video));
    AssertEqual(
        "F56DFD321AB864EBFA3000D305034995FDA12401D94FAB6E0E4B600AA456D556",
        FloatBitsSha256(block.DemodRaw));
    AssertEqual(
        "F007BBA52141A095BC814E41B9AAFEACF4E41D9FE03D779BDE98D349A4DB23DE",
        FloatBitsSha256(block.VideoLowPass));
    AssertEqual(
        "B59CC4E9A487CF06482B97793B61124ACADA0FD1C7B6B6327D0CDDA1F35939C0",
        FloatBitsSha256(block.VideoBurst!));
}

[Fact(DisplayName = "PAL LD v0.4 block demodulation matches upstream bits")]
public void PalLaserDiscV04BlockDemodulationMatchesUpstreamBits()
{
    const int blockLength = 32_768;
    const double sampleRateHz = 40_000_000.0;
    FormatParameterSet parameters = FormatCatalog.Default.GetLaserDiscParameters("PAL", lowBand: false);
    var options = new DecodeFilterOptions(
        LdDecodeDigitalAudio: false,
        LdDecodeAnalogAudio: false,
        LdMtfLevel: 1.0,
        LdMtfOffset: 0.0,
        LdClipDemodForVideo: true);
    DecodeFilterSet filters = DecodeFilterSetBuilder.BuildBasic(
        parameters,
        sampleRateHz,
        blockLength,
        options);
    var references = new RfVideoReferenceFilterSet(
        filters.LdVideoBurst,
        filters.LdVideoBurstOffset,
        filters.LdVideoPilot,
        ClipDemodForVideo: true);

    var input = new double[blockLength];
    for (ulong i = 0; i < (ulong)input.Length; i++)
    {
        input[(int)i] = (unchecked((i * 1_103_515_245UL) + 12_345UL) & 0xffffUL) - 32_768.0;
    }

    AssertEqual(
        "F57895C848D823B791F4A015D3A8B1FDF4FF16E9A83E8FEE72C1D5FF4BB1AC77",
        ComplexBitsSha256(filters.RfMtf));
    AssertEqual(
        "117E917EFACCF2211FE4CE705D02D472530B8752CEF95FC462E7AC3F7D100C69",
        ComplexBitsSha256(filters.VideoLowPass));
    AssertEqual(
        "6474816519E6AA38B324A16B7271584621C1AFC6B909AFA1100C6AE0B5B8C459",
        ComplexBitsSha256(filters.VideoLowPass05));
    AssertEqual(
        "8A4706CD55C8BE77C2382B63C59FE01C512C418AED506AEE912AB17C5C7FF8CF",
        ComplexBitsSha256(filters.LdVideoBurst!));
    AssertEqual(
        "F63020FE0A0AB422BA3D384B47464B0924B9EC0C3C79DD65F0319A8297DF403D",
        ComplexBitsSha256(filters.LdVideoPilot!));

    RfDemodulatedBlock block = new RfDemodulator(sampleRateHz).Demodulate(
        input,
        filters.RfVideo,
        filters.RfHighPass,
        filters.RfMtf,
        filters.Video,
        filters.VideoLowPass05,
        filters.VideoLowPass05Offset,
        referenceFilters: references,
        fmDemodulatorMode: RfFmDemodulatorMode.ConjugateProduct);

    AssertEqual(
        "20525ABB338B1640D387EAE935FCBB0D1D0729A38008819AAD0ADB900C4D8C33",
        ComplexBitsSha256(block.Analytic));
    AssertEqual(
        "FEDE6AC5EFBEE32513A9247CA4F89491B73769E046BC708ABD781BFBCC09FEF6",
        DoubleBitsSha256(block.DemodRaw));
    AssertEqual(
        "F55C50873424A2A56706BC360792B8706ABFE596C43A0E6973E72FE6BAA64507",
        DoubleBitsSha256(block.VideoPilot!));
    AssertEqual(
        "ADFFFDAEEDDE27DC86E34A26CED2C7D6B424592769BA212881C4ECC558744C08",
        FloatBitsSha256(block.Video));
    AssertEqual(
        "E4AC0D4BB8769F27761AFEF067E3F935919E53707AA0C06D08988FD24E1C8CD2",
        FloatBitsSha256(block.DemodRaw));
    AssertEqual(
        "8488CE08A2AA742547BD4A959319AE40D928EE4F060391C479E2B4BEDA969810",
        FloatBitsSha256(block.VideoLowPass));
    AssertEqual(
        "15B13EB9267EF8AFF44F8C0AEB0E66214981019B7BD3078E80D7966F2AD23154",
        FloatBitsSha256(block.VideoBurst!));
    AssertEqual(
        "23B44A32BD6CEF0BBD8D957390856AD1D29330E9849546FE18027A7CB636E950",
        FloatBitsSha256(block.VideoPilot!));
}

[Fact(DisplayName = "LD analog audio phase 2 matches upstream overlap and peak suppression")]
public void LaserDiscAnalogAudioPhase2MatchesUpstreamOverlapAndPeakSuppression()
{
    const int blockLength = 1024;
    Complex[] identity = Enumerable.Repeat(Complex.One, blockLength).ToArray();
    var leftFilter = new LaserDiscAnalogAudioChannelFilter(
        LowBin: 0,
        BinCount: blockLength,
        SliceSampleRateHz: 1_000_000.0,
        LowFrequencyHz: 0.0,
        CenterFrequencyHz: 1_000.0,
        Stage1Filter: identity,
        Stage2Filter: identity);
    var rightFilter = leftFilter with { CenterFrequencyHz = 2_000.0 };
    var filters = new LaserDiscAnalogAudioFilterSet(leftFilter, rightFilter, DecimationFactor: 1);

    double[] shortLeft = Enumerable.Range(0, 8).Select(value => 1_000.0 + value).ToArray();
    double[] shortRight = Enumerable.Range(0, 8).Select(value => 2_000.0 + (2 * value)).ToArray();
    LaserDiscAnalogAudioBlock shortResult = LaserDiscAnalogAudioPhase2.Apply(
        new LaserDiscAnalogAudioBlock(shortLeft, shortRight, 1),
        filters);
    for (int i = 0; i < shortLeft.Length; i++)
    {
        AssertClose(shortLeft[i], shortResult.Left[i], 1e-8);
        AssertClose(shortRight[i], shortResult.Right[i], 1e-8);
    }

    const double ntscLeftCarrier = 2_301_136.3636363638;
    LaserDiscAnalogAudioChannelFilter float32CenterFilter = leftFilter with
    {
        CenterFrequencyHz = ntscLeftCarrier
    };
    double[] float32Source = [2_313_564.0, 2_310_882.0, 2_308_120.0];
    LaserDiscAnalogAudioBlock float32Centered = LaserDiscAnalogAudioPhase2.Apply(
        new LaserDiscAnalogAudioBlock(float32Source, float32Source, 1),
        new LaserDiscAnalogAudioFilterSet(float32CenterFilter, float32CenterFilter, 1));
    for (int i = 0; i < float32Source.Length; i++)
    {
        double expected = ((float)float32Source[i] - (float)ntscLeftCarrier) + ntscLeftCarrier;
        AssertClose(expected, float32Centered.Left[i], 1e-8);
    }

    double[] longLeft = Enumerable.Range(0, 1500).Select(value => 1_000.0 + value).ToArray();
    double[] longRight = Enumerable.Range(0, 1500).Select(value => 2_000.0 + value).ToArray();
    LaserDiscAnalogAudioBlock overlapResult = LaserDiscAnalogAudioPhase2.Apply(
        new LaserDiscAnalogAudioBlock(longLeft, longRight, 1),
        filters);
    AssertClose(longLeft[987], overlapResult.Left[988], 1e-8);
    AssertClose(longRight[1498], overlapResult.Right[1499], 1e-8);

    LaserDiscAnalogAudioBlock suppressed = LaserDiscAnalogAudioPhase2.Apply(
        new LaserDiscAnalogAudioBlock(
            [1_000.0, 601_001.0, 701_001.0, 1_000.0],
            [2_000.0, 2_010.0, 2_020.0, 2_030.0],
            1),
        filters);
    AssertTrue(suppressed.Left.All(value => Math.Abs(value - 1_000.0) < 1e-8));
    AssertTrue(suppressed.Right.All(value => Math.Abs(value - 2_000.0) < 1e-8));
}

[Fact(DisplayName = "LD AC3 filter processes RF TBC blocks")]
public void LaserDiscAc3FilterProcessesRfTbcBlocks()
{
    Complex[] identity = Enumerable.Repeat(Complex.One, 2048).ToArray();
    var filter = new LaserDiscAc3Filter(identity);
    short[] first = Enumerable.Repeat((short)640, 2048).ToArray();
    byte[] firstOutput = filter.Process(first);

    AssertEqual(1024, firstOutput.Length);
    AssertTrue(firstOutput.All(value => value == 10));

    short[] second = Enumerable.Repeat((short)-640, 1024).ToArray();
    byte[] secondOutput = filter.Process(second);
    AssertEqual(1024, secondOutput.Length);
    AssertTrue(secondOutput.All(value =>
    {
        sbyte signed = unchecked((sbyte)value);
        return signed >= -10 && signed <= -9;
    }));

    byte[] clipped = new LaserDiscAc3Filter(identity).Process(Enumerable.Repeat(short.MaxValue, 2048).Select(value => (short)value).ToArray());
    AssertTrue(clipped.All(value => value == 100));

    DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "--AC3",
        "input.s16",
        "out"
    ]));
    Complex[] scipyReferenceFilter = session.Filters.LdAc3!;
    var expectedResponse = new Dictionary<int, Complex>
    {
        [0] = Complex.Zero,
        [1949] = new(0.012475326115833124, 0.003276734500210017),
        [2241] = new(-0.6603543508442894, 0.24387858420918887),
        [2359] = new(0.97967330642849015, -0.20059897361900103),
        [2477] = new(-0.70741946891498231, -0.040971756241497063),
        [2769] = new(0.020188156105713204, -0.0032826894262266231),
        [32767] = new(-2.9652555526707439e-25, -1.1560457639309072e-28)
    };
    foreach ((int bin, Complex expected) in expectedResponse)
    {
        AssertClose(expected.Real, scipyReferenceFilter[bin].Real, 1e-8);
        AssertClose(expected.Imaginary, scipyReferenceFilter[bin].Imaginary, 1e-8);
    }

    short[] referenceInput = new short[32_768];
    for (int i = 0; i < referenceInput.Length; i++)
    {
        double sample =
            (4_000.0 * Math.Sin(Math.Tau * 2_880_000.0 * i / 40_000_000.0)) +
            (2_000.0 * Math.Cos(Math.Tau * 1_100_000.0 * i / 40_000_000.0)) +
            (1_500.0 * Math.Sin(Math.Tau * 6_500_000.0 * i / 40_000_000.0));
        referenceInput[i] = checked((short)Math.Round(sample, MidpointRounding.ToEven));
    }

    AssertEqual(
        "F7A933E489422EE4C51DEC1128A62688FA08CFEF210DAB8D070AB8A559F2504E",
        Convert.ToHexString(SHA256.HashData(BuildPcm16Bytes(referenceInput))));
    byte[] referenceOutput = new LaserDiscAc3Filter(scipyReferenceFilter).Process(referenceInput);
    AssertEqual(31_744, referenceOutput.Length);
    AssertEqual(
        "9638AF6FD0F818A9CACFEF29B37F1EDB76A69E7002692FFE1B578886C03D3DAA",
        Convert.ToHexString(SHA256.HashData(referenceOutput)));
}

[Fact(DisplayName = "LD AC3 process specs match upstream pipeline")]
public void LaserDiscAc3ProcessSpecsMatchUpstreamPipeline()
{
    IReadOnlyList<LaserDiscAc3ProcessSpec> specs = LaserDiscAc3Pipe.BuildProcessSpecs("capture.ac3");
    AssertEqual(3, specs.Count);

    AssertEqual("ld-ac3-decode", specs[0].FileName);
    AssertEqual("-|capture.ac3", string.Join('|', specs[0].Arguments));
    AssertTrue(specs[0].RedirectInput);
    AssertTrue(specs[0].RedirectOutput);
    AssertTrue(specs[0].RedirectErrorToLog);

    AssertEqual("ld-ac3-demodulate", specs[1].FileName);
    AssertEqual("-v|3|-|-", string.Join('|', specs[1].Arguments));
    AssertTrue(specs[1].RedirectInput);
    AssertTrue(specs[1].RedirectOutput);
    AssertFalse(specs[1].RedirectErrorToLog);

    AssertEqual("sox", specs[2].FileName);
    AssertEqual(
        "-r|40000000|-b|8|-c|1|-e|signed|-t|raw|-|-b|8|-r|46080000|-e|unsigned|-c|1|-t|raw|-",
        string.Join('|', specs[2].Arguments));
    AssertTrue(specs[2].RedirectInput);
    AssertTrue(specs[2].RedirectOutput);
    AssertFalse(specs[2].RedirectErrorToLog);
    AssertThrows<ArgumentException>(() => LaserDiscAc3Pipe.BuildProcessSpecs(""));
}

[Fact(DisplayName = "LD EFM PLL preserves state across buffers")]
public void LaserDiscEfmPllPreservesStateAcrossBuffers()
{
    short[] signal = BuildEfmSquareWave(2048, halfPeriodSamples: 9, amplitude: 12_000);
    var single = new LaserDiscEfmPll();
    byte[] singlePass = single.Process(signal);

    var split = new LaserDiscEfmPll();
    byte[] splitA = split.Process(signal.AsSpan(0, 777));
    byte[] splitB = split.Process(signal.AsSpan(777));
    byte[] splitPass = splitA.Concat(splitB).ToArray();

    AssertTrue(singlePass.Length > 20);
    AssertIntSequence(singlePass.Select(value => (int)value).ToArray(), splitPass.Select(value => (int)value).ToArray());
    foreach (byte value in singlePass)
    {
        AssertTrue(value >= 3 && value <= 11);
    }

    byte[] silent = new LaserDiscEfmPll().Process(Enumerable.Repeat((short)1000, 256).ToArray());
    AssertEqual(0, silent.Length);
}

[Fact(DisplayName = "RF block decode pipeline connects loader filters and demodulator")]
public void RfBlockDecodePipelineConnectsLoaderFiltersAndDemodulator()
{
    const int n = 32;
    const double sampleRate = 32.0;
    const double carrier = 4.0;
    short[] samples = Enumerable.Range(0, n)
        .Select(i => (short)Math.Round(10000.0 * Math.Cos(Math.Tau * carrier * i / sampleRate)))
        .ToArray();
    byte[] bytes = new byte[n * 2];
    for (int i = 0; i < samples.Length; i++)
    {
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
    }

    Complex[] identity = RfDemodulator.IdentityFilter(n);
    double[] ones = Enumerable.Repeat(1.0, n).ToArray();
    var filters = new DecodeFilterSet(identity, identity, identity, identity, identity, identity, null, ones, ones, ones, ones, ones, ones, null);
    var pipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), filters, sampleRate);

    RfDemodulatedBlock? block = pipeline.DecodeBlock(new MemoryStream(bytes), 0, n);
    if (block is null)
    {
        throw new Exception("Expected decoded RF block.");
    }

    for (int i = 2; i < block.DemodRaw.Length - 2; i++)
    {
        AssertClose(carrier, block.DemodRaw[i], 1e-3);
    }
    AssertEqual(n, block.VideoLowPass.Length);
    AssertEqual(n, block.RfHighPass.Length);
    AssertEqual(null, block.Efm);

    var vhsRustPipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        filters,
        sampleRate,
        new DecodeFilterOptions(FmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation));
    RfDemodulatedBlock vhsRustBlock = vhsRustPipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    AssertSequence(
        PortedMath.UnwrapHilbertVhsRustApproximation(vhsRustBlock.Analytic, sampleRate),
        vhsRustBlock.DemodRaw);

    Complex[] zeroFilter = new Complex[n];
    var zeroVideoFilters = new DecodeFilterSet(identity, identity, identity, zeroFilter, identity, identity, null, ones, ones, ones, ones, ones, ones, null);
    var filteredPipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), zeroVideoFilters, sampleRate);
    RfDemodulatedBlock filteredBlock = filteredPipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    AssertClose(0.0, MaxAbs(filteredBlock.Video), 1e-12);

    var rawPipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        zeroVideoFilters,
        sampleRate,
        new DecodeFilterOptions(ExportRawTbc: true));
    RfDemodulatedBlock rawBlock = rawPipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    AssertSequence(rawBlock.DemodRaw, rawBlock.Video);
    AssertTrue(MaxAbs(rawBlock.Video) > 0.1);

    short[] chromaSamples = [1, 2, 3, 4, 5, 6, 7, 8];
    byte[] chromaBytes = BuildPcm16Bytes(chromaSamples);
    Complex[] chromaIdentity = RfDemodulator.IdentityFilter(chromaSamples.Length);
    double[] chromaOnes = Enumerable.Repeat(1.0, chromaSamples.Length).ToArray();
    var chromaFilters = new DecodeFilterSet(
        chromaIdentity,
        chromaIdentity,
        chromaIdentity,
        chromaIdentity,
        chromaIdentity,
        chromaIdentity,
        null,
        chromaOnes,
        chromaOnes,
        chromaOnes,
        chromaOnes,
        chromaOnes,
        chromaOnes,
        null,
        ChromaBurst: chromaIdentity,
        ChromaBurstMagnitude: chromaOnes,
        ChromaOffsetSamples: 2);
    var chromaPipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), chromaFilters, sampleRate);
    RfDemodulatedBlock chromaBlock = chromaPipeline.DecodeBlock(new MemoryStream(chromaBytes), 0, chromaSamples.Length)!;
    double[] expectedChroma = FrequencyDomainFilter.Roll(chromaSamples.Select(value => (double)value).ToArray(), 2);
    double chromaMean = expectedChroma.Average();
    for (int i = 0; i < expectedChroma.Length; i++)
    {
        expectedChroma[i] -= chromaMean;
    }

    AssertTrue(chromaBlock.Chroma is not null);
    AssertSequence(expectedChroma, chromaBlock.Chroma!);

    SosSection[] chromaSos = [new SosSection(0.5, 0.5, 0.0, 1.0, 0.0, 0.0)];
    DecodeFilterSet sosChromaFilters = filters with
    {
        ChromaBurst = identity,
        ChromaBurstMagnitude = ones,
        ChromaBurstSos = chromaSos,
        ChromaOffsetSamples = 1
    };
    var sosChromaPipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), sosChromaFilters, sampleRate);
    RfDemodulatedBlock sosChromaBlock = sosChromaPipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    double[] expectedSosChroma = SosFilter.ApplyForwardBackwardFloat32(
        chromaSos,
        samples.Select(value => (double)value).ToArray());
    expectedSosChroma = VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32(expectedSosChroma, 1);

    AssertSequence(expectedSosChroma, sosChromaBlock.Chroma!);
    AssertTrue(expectedSosChroma.Where((value, i) => Math.Abs(value - samples[i]) > 1e-6).Any());

    var chromaAfcPipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        chromaFilters,
        sampleRate,
        new DecodeFilterOptions(UseChromaAfc: true));
    RfDemodulatedBlock chromaAfcBlock = chromaAfcPipeline.DecodeBlock(new MemoryStream(chromaBytes), 0, chromaSamples.Length)!;
    AssertSequence(chromaSamples.Select(value => (double)value).ToArray(), chromaAfcBlock.Chroma!);

    var referenceFilters = new DecodeFilterSet(
        identity,
        identity,
        identity,
        identity,
        identity,
        identity,
        null,
        ones,
        ones,
        ones,
        ones,
        ones,
        ones,
        null,
        LdVideoBurst: identity,
        LdVideoBurstMagnitude: ones,
        LdVideoPilot: identity,
        LdVideoPilotMagnitude: ones)
    {
        LdVideoBurstOffset = 3
    };
    var referencePipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), referenceFilters, sampleRate);
    RfDemodulatedBlock referenceBlock = referencePipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    AssertTrue(referenceBlock.VideoBurst is not null);
    AssertTrue(referenceBlock.VideoPilot is not null);
    AssertSequence(FrequencyDomainFilter.Roll(referenceBlock.DemodRaw, -3), referenceBlock.VideoBurst!);
    AssertSequence(referenceBlock.DemodRaw, referenceBlock.VideoPilot!);

    var ldQuantizedPipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        referenceFilters,
        40_000_000.0,
        new DecodeFilterOptions(LdClipDemodForVideo: true));
    RfDemodulatedBlock ldQuantized = ldQuantizedPipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    AssertSequence(ldQuantized.Video.Select(value => (double)(float)value).ToArray(), ldQuantized.Video);
    AssertSequence(ldQuantized.DemodRaw.Select(value => (double)(float)value).ToArray(), ldQuantized.DemodRaw);
    AssertSequence(ldQuantized.VideoLowPass.Select(value => (double)(float)value).ToArray(), ldQuantized.VideoLowPass);
    AssertSequence(ldQuantized.RfHighPass.Select(value => (double)(float)value).ToArray(), ldQuantized.RfHighPass);
    AssertSequence(ldQuantized.VideoBurst!.Select(value => (double)(float)value).ToArray(), ldQuantized.VideoBurst!);
    AssertSequence(ldQuantized.VideoPilot!.Select(value => (double)(float)value).ToArray(), ldQuantized.VideoPilot!);

    AssertEqual(null, pipeline.DecodeBlock(new MemoryStream(bytes), n - 1, n));
}

[Fact(DisplayName = "GNU Radio RF AFE bridge matches upstream ZMQ protocol")]
public async Task GnuRadioRfAfeBridgeMatchesUpstreamZmqProtocol()
{
    int sendPort = GnuRadioRfAfeBridge.FindAvailablePort(20_000, 24_999)
        ?? throw new InvalidOperationException("No test ZMQ send port was available.");
    int receivePort = GnuRadioRfAfeBridge.FindAvailablePort(sendPort + 1, 25_999)
        ?? throw new InvalidOperationException("No test ZMQ receive port was available.");
    var log = new StringWriter();
    using var bridge = new GnuRadioRfAfeBridge(sendPort, receivePort, log);

    double[] input = [1.25, -2.5, 16_777_217.0, Math.PI];
    double[] firstResponse = [10.5, 20.25];
    double[] secondResponse = [-30.75, 40.125];
    using var companionReady = new ManualResetEventSlim();
    Task companion = Task.Factory.StartNew(
        () =>
        {
            using var source = new RequestSocket();
            using var sink = new ResponseSocket();
            source.Options.Linger = TimeSpan.Zero;
            sink.Options.Linger = TimeSpan.Zero;
            source.Connect($"tcp://localhost:{sendPort}");
            sink.Bind($"tcp://*:{receivePort}");
            companionReady.Set();

            source.SendFrame(Array.Empty<byte>());
            double[] sentInput = ReadFloat32Samples(source.ReceiveFrameBytes());
            AssertSequence(input.Select(value => (double)(float)value).ToArray(), sentInput);

            AssertTrue(sink.ReceiveFrameBytes().SequenceEqual([(byte)'0']));
            sink.SendFrame(BuildFloat32Bytes(firstResponse));
            AssertTrue(sink.ReceiveFrameBytes().SequenceEqual([(byte)'0']));
            sink.SendFrame(BuildFloat32Bytes(secondResponse));
        },
        TestContext.Current.CancellationToken,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

    AssertTrue(companionReady.Wait(
        TimeSpan.FromSeconds(5),
        TestContext.Current.CancellationToken));
    double[] processed = bridge.Process(input);
    await companion.WaitAsync(
        TimeSpan.FromSeconds(5),
        TestContext.Current.CancellationToken);
    AssertSequence(firstResponse.Concat(secondResponse).ToArray(), processed);
    AssertTrue(log.ToString().Contains($"tcp://localhost:{sendPort}", StringComparison.Ordinal));
    AssertTrue(log.ToString().Contains($"tcp://*:{receivePort}", StringComparison.Ordinal));

    AssertEqual<int?>(sendPort + 1, GnuRadioRfAfeBridge.FindAvailablePort(sendPort, sendPort + 1, sendPort));
    AssertThrows<ArgumentOutOfRangeException>(() => GnuRadioRfAfeBridge.FindAvailablePort(10, 9));

    const int blockLength = 8;
    Complex[] identity = RfDemodulator.IdentityFilter(blockLength);
    double[] ones = Enumerable.Repeat(1.0, blockLength).ToArray();
    var filters = new DecodeFilterSet(
        identity, identity, identity, identity, identity, identity, null,
        ones, ones, ones, ones, ones, ones, null);
    var processor = new TestRfInputProcessor(values => values.Select(value => value + 100.0).ToArray());
    var pipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        filters,
        sampleRateHz: blockLength,
        inputProcessor: processor);
    RfPipelineBlock pipelineBlock = pipeline.DecodeBlockWithInput(
        new MemoryStream(BuildPcm16Bytes([1, 2, 3, 4, 5, 6, 7, 8])),
        sample: 0,
        blockLength)!;
    AssertSequence([101, 102, 103, 104, 105, 106, 107, 108], pipelineBlock.Input);
    pipeline.Dispose();
    AssertTrue(processor.Disposed);
}

[Fact(DisplayName = "RF block decode pipeline emits LD EFM blocks")]
public void RfBlockDecodePipelineEmitsLdEfmBlocks()
{
    const int n = 32;
    const double sampleRate = 32.0;
    byte[] bytes = BuildPcm16Bytes(Enumerable.Repeat((short)20000, n).ToArray());
    Complex[] identity = RfDemodulator.IdentityFilter(n);
    Complex[] doubleGain = Enumerable.Repeat(new Complex(2.0, 0.0), n).ToArray();
    double[] ones = Enumerable.Repeat(1.0, n).ToArray();
    double[] twos = Enumerable.Repeat(2.0, n).ToArray();
    var filters = new DecodeFilterSet(identity, identity, identity, identity, identity, identity, doubleGain, ones, ones, ones, ones, ones, ones, twos);
    var pipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), filters, sampleRate);

    RfDemodulatedBlock block = pipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    AssertTrue(block.Efm is not null);
    AssertEqual(n, block.Efm!.Length);
    AssertIntSequence(Enumerable.Repeat((int)short.MaxValue, n).ToArray(), block.Efm.Select(value => (int)value).ToArray());

    const int streamBlockLength = 16;
    const int blockCut = 2;
    const int blockCutEnd = 2;
    Complex[] streamIdentity = RfDemodulator.IdentityFilter(streamBlockLength);
    double[] streamOnes = Enumerable.Repeat(1.0, streamBlockLength).ToArray();
    var streamFilters = new DecodeFilterSet(
        streamIdentity,
        streamIdentity,
        streamIdentity,
        streamIdentity,
        streamIdentity,
        streamIdentity,
        streamIdentity,
        streamOnes,
        streamOnes,
        streamOnes,
        streamOnes,
        streamOnes,
        streamOnes,
        streamOnes,
        ChromaBurst: streamIdentity,
        ChromaBurstMagnitude: streamOnes,
        LdVideoBurst: streamIdentity,
        LdVideoBurstMagnitude: streamOnes,
        LdVideoPilot: streamIdentity,
        LdVideoPilotMagnitude: streamOnes)
    {
        LdVideoBurstOffset = 3
    };
    byte[] streamBytes = BuildPcm16Bytes(Enumerable.Repeat((short)123, 40).ToArray());
    var streamPipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), streamFilters, streamBlockLength);
    var streamDecoder = new RfBlockStreamDecoder(streamPipeline, streamBlockLength, blockCut, blockCutEnd);

    RfDecodedSpan span = streamDecoder.Read(new MemoryStream(streamBytes), begin: 5, length: 15)!;
    AssertTrue(span.Efm is not null);
    AssertIntSequence(Enumerable.Repeat(123, 15).ToArray(), span.Efm!.Select(value => (int)value).ToArray());
    AssertTrue(span.Chroma is not null);
    AssertEqual(15, span.Chroma!.Length);
    AssertClose(0.0, MaxAbs(span.Chroma), 1e-12);
    AssertTrue(span.VideoBurst is not null);
    AssertTrue(span.VideoPilot is not null);
    AssertEqual(15, span.VideoBurst!.Length);
    AssertEqual(15, span.VideoPilot!.Length);
}

[Fact(DisplayName = "RF block decode pipeline handles CVBS direct luma")]
public void RfBlockDecodePipelineHandlesCvbsDirectLuma()
{
    const int n = 32;
    const double sampleRate = 32.0;
    short[] samples = Enumerable.Range(0, n).Select(value => (short)(value * 10)).ToArray();
    byte[] bytes = BuildPcm16Bytes(samples);
    Complex[] identity = RfDemodulator.IdentityFilter(n);
    Complex[] zero = new Complex[n];
    double[] ones = Enumerable.Repeat(1.0, n).ToArray();
    var filters = new DecodeFilterSet(
        identity,
        identity,
        identity,
        zero,
        identity,
        identity,
        null,
        ones,
        ones,
        ones,
        new double[n],
        ones,
        ones,
        null,
        CvbsVideoBurst: identity,
        CvbsVideoBurstMagnitude: ones);
    var converter = new VideoOutputConverter(
        ire0: 100.0,
        hzIre: 10.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 1.0);
    var autoSyncPipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        filters,
        sampleRate,
        cvbsOptions: new CvbsDecodeOptions(AutoSync: true, converter));

    RfDemodulatedBlock autoSync = autoSyncPipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    double[] reconstructed = CvbsFftRoundTrip(samples.Select(value => (double)value).ToArray());
    AssertSequence(reconstructed, autoSync.DemodRaw);
    AssertSequence(reconstructed, autoSync.Video);
    AssertSequence(
        CvbsFftRoundTrip(reconstructed).Select(value => (double)(float)value).ToArray(),
        autoSync.VideoBurst!);
    AssertEqual(0, autoSync.Analytic.Length);

    var noAutoSyncPipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        filters,
        sampleRate,
        cvbsOptions: new CvbsDecodeOptions(AutoSync: false, converter));
    RfDemodulatedBlock noAutoSync = noAutoSyncPipeline.DecodeBlock(new MemoryStream(bytes), 0, n)!;
    double[] expectedMapped = RfBlockDecodePipeline.ConvertCvbsRawToHz(reconstructed, converter);
    AssertSequence(expectedMapped, noAutoSync.DemodRaw);
    AssertSequence(expectedMapped, noAutoSync.Video);
    AssertSequence(
        CvbsFftRoundTrip(expectedMapped).Select(value => (double)(float)value).ToArray(),
        noAutoSync.VideoBurst!);

    DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "input.s16",
        "out"
    ]), blockLength: 4096);
    byte[] sessionBytes = BuildPcm16Bytes(Enumerable.Range(0, session.BlockLength).Select(value => (short)(value % 100)).ToArray());
    RfDemodulatedBlock sessionBlock = session.Pipeline.DecodeBlock(new MemoryStream(sessionBytes), 0, session.BlockLength)!;
    AssertClose(0.0, sessionBlock.DemodRaw[0], 1e-9);
    AssertClose(1.0, sessionBlock.DemodRaw[1], 1e-9);
    AssertSequence(sessionBlock.DemodRaw, sessionBlock.Video);
    AssertTrue(sessionBlock.VideoBurst is not null);
}

[Fact(DisplayName = "CVBS v0.4 block filters match upstream float32 hashes")]
public void CvbsV04BlockFiltersMatchUpstreamFloat32Hashes()
{
    const int blockLength = 32768;
    FormatParameterSet parameters = FormatCatalog.Default.GetCvbsParameters("NTSC");
    uint state = 0x12345678;
    var input = new double[blockLength];
    for (int i = 0; i < input.Length; i++)
    {
        state = unchecked((state * 1664525) + 1013904223);
        int value = (int)((state >> 8) & 0xFFFF) - 32768;
        input[i] = value == 0 ? 1 : value;
    }

    AssertCvbsV04Hashes(
        parameters,
        input,
        new DecodeFilterOptions(),
        autoSync: true,
        "3DAE4DFDEA2CB35381BAB7FA78905D9AB0DFD534CABD834F7805724CA69441D0",
        "36AC5FFB7704AF4B8004CF08D3B5DE8EFCB2161EB65593C8FBDEBB28DAEBF5CB",
        "7977594BEB52A0C6BA1C1CE96A62F3C036FCDF81B9F6FAC467E1BADB380FD0B1",
        "45F1F20764D84B3950C3E216FB827C78736DC31DC210EF418C18BC3F4B024159",
        "EBB605C0FD21EE5499202CE9456DB5B28538DA421215F66BEC1FC4CDF620B80A");
    AssertCvbsV04Hashes(
        parameters,
        input,
        new DecodeFilterOptions(),
        autoSync: false,
        "81D70D24493E54E3094D9CBD62F9AE2B7975A8DC8FCCE044C865C5643E629EBF",
        "EF971B10B1B630C3E55D4A9C0100DB30CB5034F9FE80AD6A179A152634EC31D0",
        "2AB184BA686E60AFC61737E665CD23A5914B895B8DF05F417CE575A3D7A368E3",
        "BE92347A04CAFCDF0D71409EE64918F6C7F4647EA0434211AF00BEB4B7A95662",
        "3D9B7A1A7403727941C4F8C0E758F1E458C842D38E385C15DE51DCA92352A986");
    AssertCvbsV04Hashes(
        parameters,
        input,
        new DecodeFilterOptions(VideoNotchHz: 2_500_000.0, VideoNotchQ: 15.0),
        autoSync: true,
        "9064E2552859B8CD15CEED9141B382E6458B85955E0CB36D96008CBE2A349222",
        "CF856DDC49407AB9F37873D218B7F3A35AC73F570C6B28E6C70880D45183E11D",
        "FD22A43C118D6BBEA7B357FB9535DC338F09047688BC7701FFC9355B3C7FE5A7",
        "99D7FC86631EAB0A8B275425359D059E619115D6D52EBA8DD994B426CD4922C4",
        "A62D9F7CBB1F31813D1B52BE6B83C42EB3D4C341529027C7A22805301C975F74");
}

static void AssertCvbsV04Hashes(
    FormatParameterSet parameters,
    double[] input,
    DecodeFilterOptions options,
    bool autoSync,
    string demodDoubleHash,
    string video05DoubleHash,
    string demodHash,
    string video05Hash,
    string burstHash)
{
    DecodeFilterSet filters = DecodeFilterSetBuilder.BuildBasic(
        parameters,
        40_000_000.0,
        input.Length,
        options);
    using var pipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        filters,
        40_000_000.0,
        options,
        new CvbsDecodeOptions(autoSync, VideoOutputConverter.FromParameters(parameters)));
    RfDemodulatedBlock block = pipeline.DecodePreparedBlock(input).Demodulated;
    AssertEqual(demodDoubleHash, DoubleBitsSha256(block.DemodRaw));
    AssertEqual(video05DoubleHash, DoubleBitsSha256(block.VideoLowPass));
    AssertEqual(demodHash, FloatBitsSha256(block.DemodRaw));
    AssertEqual(video05Hash, FloatBitsSha256(block.VideoLowPass));
    AssertEqual(burstHash, FloatBitsSha256(block.VideoBurst!));
}

[Fact(DisplayName = "CVBS DUCC real FFT filtering matches SciPy double bits")]
public void CvbsDuccRealFftFilteringMatchesScipyDoubleBits()
{
    const int blockLength = 32_768;
    uint state = 0x12345678;
    var input = new double[blockLength];
    for (int i = 0; i < input.Length; i++)
    {
        state = unchecked((state * 1664525) + 1013904223);
        int value = (int)((state >> 8) & 0xFFFF) - 32768;
        input[i] = value == 0 ? 1 : value;
    }

    AssertEqual(
        "A8B3FFBA42B93344094A5BD9B3056EE8E1358FC589EAB1AB0F9C47C634E71560",
        DoubleBitsSha256(input));

    DecodeFilterSet filters = DecodeFilterSetBuilder.BuildBasic(
        FormatCatalog.Default.GetCvbsParameters("NTSC"),
        40_000_000.0,
        blockLength);
    AssertEqual(
        "7E0AA1AB29320D3DFDB5C8C5843274C57611EB30434335A32EE61775DDCF1E35",
        ComplexBitsSha256(filters.VideoLowPass05));

    Complex[] halfSpectrum = PocketFftComplex.ForwardDuccReal(input);
    AssertEqual(
        "14467469A931E878E066F4E9F7CB04950D951A32DD5FFB7A6BD9B4BF693F1768",
        ComplexBitsSha256(halfSpectrum));
    double[] reconstructed = PocketFftComplex.InverseDuccReal(halfSpectrum, blockLength);
    AssertEqual(
        "3DAE4DFDEA2CB35381BAB7FA78905D9AB0DFD534CABD834F7805724CA69441D0",
        DoubleBitsSha256(reconstructed));
    Complex[] reconstructedSpectrum = PocketFftComplex.ForwardDuccReal(reconstructed);
    AssertEqual(
        "4536090FA1058E7761561A0A2377188ED1A9F96167F6C746C5D26E48740F1D01",
        ComplexBitsSha256(reconstructedSpectrum));

    var filteredSpectrum = new Complex[reconstructedSpectrum.Length];
    for (int i = 0; i < filteredSpectrum.Length; i++)
    {
        Complex value = reconstructedSpectrum[i];
        Complex coefficient = filters.VideoLowPass05[i];
        filteredSpectrum[i] = new Complex(
            Math.FusedMultiplyAdd(
                value.Real,
                coefficient.Real,
                -(value.Imaginary * coefficient.Imaginary)),
            Math.FusedMultiplyAdd(
                value.Real,
                coefficient.Imaginary,
                value.Imaginary * coefficient.Real));
    }

    AssertEqual(
        "8B98C81888AC03D255815CA8043790BD502890A4CD68541DCC9FDE80E5922339",
        ComplexBitsSha256(filteredSpectrum));
    AssertEqual(
        "40B996E074F1A1CD7A8F9989864CD61F4457CCFCFAC5C1B4B62629A307D83593",
        DoubleBitsSha256(PocketFftComplex.InverseDuccReal(filteredSpectrum, blockLength)));

    double[] secondRoundTrip = PocketFftComplex.InverseDuccReal(
        reconstructedSpectrum,
        blockLength);
    AssertEqual(
        "5347913DE0967DB8EB0605C4603769AA2150CBB8CF03103BA3A7334C39CE5855",
        DoubleBitsSha256(secondRoundTrip));
    AssertThrows<ArgumentException>(() => PocketFftComplex.InverseDuccReal(halfSpectrum, 1024));
}

static double[] CvbsFftRoundTrip(ReadOnlySpan<double> input)
{
    Complex[] halfSpectrum = PocketFftComplex.ForwardDuccReal(input);
    return PocketFftComplex.InverseDuccReal(halfSpectrum, input.Length);
}

[Fact(DisplayName = "RF block stream decoder stitches overlap-save blocks")]
public void RfBlockStreamDecoderStitchesOverlapSaveBlocks()
{
    const int blockLength = 16;
    const int blockCut = 2;
    const int blockCutEnd = 2;
    const double sampleRate = 16.0;
    byte[] bytes = BuildPcm16Bytes(Enumerable.Range(0, 64).Select(value => (short)value).ToArray());
    Complex[] identity = RfDemodulator.IdentityFilter(blockLength);
    double[] ones = Enumerable.Repeat(1.0, blockLength).ToArray();
    var filters = new DecodeFilterSet(identity, identity, identity, identity, identity, identity, null, ones, ones, ones, ones, ones, ones, null);
    var pipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), filters, sampleRate);
    var streamDecoder = new RfBlockStreamDecoder(pipeline, blockLength, blockCut, blockCutEnd);

    AssertEqual(12, streamDecoder.BlockStride);
    RfDecodedSpan? span = streamDecoder.Read(new MemoryStream(bytes), begin: 5, length: 15);
    if (span is null)
    {
        throw new Exception("Expected decoded span.");
    }

    AssertEqual(5L, span.StartSample);
    AssertSequence(Enumerable.Range(7, 15).Select(value => (double)value).ToArray(), span.Input);
    AssertEqual(15, span.Video.Length);
    AssertEqual(15, span.DemodRaw.Length);
    AssertEqual(15, span.Envelope!.Length);
    AssertEqual(15, span.VideoLowPass!.Length);
    AssertEqual(15, span.RfHighPass!.Length);

    var parallelPipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), filters, sampleRate);
    var parallelDecoder = new RfBlockStreamDecoder(
        parallelPipeline,
        blockLength,
        blockCut,
        blockCutEnd,
        workerThreads: 4);
    RfDecodedSpan parallel = parallelDecoder.Read(new MemoryStream(bytes), begin: 5, length: 15)
        ?? throw new Exception("Expected parallel decoded span.");
    AssertEqual(4, parallelDecoder.WorkerThreads);
    AssertSequence(span.Input, parallel.Input);
    AssertSequence(span.Video, parallel.Video);
    AssertSequence(span.DemodRaw, parallel.DemodRaw);
    AssertSequence(span.Envelope!, parallel.Envelope!);
    AssertSequence(span.VideoLowPass!, parallel.VideoLowPass!);
    AssertSequence(span.RfHighPass!, parallel.RfHighPass!);

    DecodeFilterSet delayedFilters = filters with { RfHighPassOffset = 1 };
    var delayedPipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), delayedFilters, sampleRate);
    var delayedDecoder = new RfBlockStreamDecoder(delayedPipeline, blockLength, blockCut, blockCutEnd);
    RfDecodedSpan delayed = delayedDecoder.Read(new MemoryStream(bytes), begin: 5, length: 15)
        ?? throw new Exception("Expected delay-aligned decoded span.");
    AssertSequence(
        Enumerable.Range(6, 15).Select(value => (double)value).ToArray(),
        delayed.RfHighPass!);
    AssertSequence(span.Input, delayed.Input);
    AssertSequence(span.Video, delayed.Video);

    var statefulPipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        filters,
        sampleRate,
        new DecodeFilterOptions(SharpnessEq: new SharpnessEqOptions(
            Level: 0.5,
            CornerHz: 2.0,
            TransitionHz: 1.0,
            OrderLimit: 2)));
    var statefulDecoder = new RfBlockStreamDecoder(
        statefulPipeline,
        blockLength,
        blockCut,
        blockCutEnd,
        workerThreads: 4);
    RfDecodedSpan statefulFirst = statefulDecoder.Read(new MemoryStream(bytes), begin: 0, length: 24)
        ?? throw new Exception("Expected first stateful decoded span.");
    RfDecodedSpan statefulOverlap = statefulDecoder.Read(new MemoryStream(bytes), begin: 12, length: 24)
        ?? throw new Exception("Expected overlapping stateful decoded span.");
    AssertSequence(statefulFirst.DemodRaw[12..24], statefulOverlap.DemodRaw[..12]);
    AssertSequence(statefulFirst.Video[12..24], statefulOverlap.Video[..12]);

    var statefulGapPipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        filters,
        sampleRate,
        new DecodeFilterOptions(SharpnessEq: new SharpnessEqOptions(0.5, 2.0, 1.0, 2)));
    var statefulGapDecoder = new RfBlockStreamDecoder(
        statefulGapPipeline,
        blockLength,
        blockCut,
        blockCutEnd);
    _ = statefulGapDecoder.Read(new MemoryStream(bytes), begin: 0, length: 24);
    _ = statefulGapDecoder.Read(new MemoryStream(bytes), begin: 36, length: 12);
    RfDecodedSpan warmedGap = statefulGapDecoder.Read(new MemoryStream(bytes), begin: 24, length: 24)
        ?? throw new Exception("Expected cached stateful gap span.");

    var statefulReferencePipeline = new RfBlockDecodePipeline(
        new Pcm16StreamSampleLoader(),
        filters,
        sampleRate,
        new DecodeFilterOptions(SharpnessEq: new SharpnessEqOptions(0.5, 2.0, 1.0, 2)));
    var statefulReferenceDecoder = new RfBlockStreamDecoder(
        statefulReferencePipeline,
        blockLength,
        blockCut,
        blockCutEnd);
    RfDecodedSpan statefulReference = statefulReferenceDecoder.Read(new MemoryStream(bytes), begin: 0, length: 48)
        ?? throw new Exception("Expected stateful reference span.");
    AssertSequence(statefulReference.DemodRaw[24..48], warmedGap.DemodRaw);
    AssertSequence(statefulReference.Video[24..48], warmedGap.Video);

    var delayedParallelPipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), delayedFilters, sampleRate);
    var delayedParallelDecoder = new RfBlockStreamDecoder(
        delayedParallelPipeline,
        blockLength,
        blockCut,
        blockCutEnd,
        workerThreads: 4);
    RfDecodedSpan delayedParallel = delayedParallelDecoder.Read(new MemoryStream(bytes), begin: 5, length: 15)
        ?? throw new Exception("Expected parallel delay-aligned decoded span.");
    AssertSequence(delayed.RfHighPass!, delayedParallel.RfHighPass!);

    DecodeFilterSet advancedFilters = filters with { RfHighPassOffset = -1 };
    var advancedPipeline = new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), advancedFilters, sampleRate);
    var advancedDecoder = new RfBlockStreamDecoder(advancedPipeline, blockLength, blockCut, blockCutEnd);
    RfDecodedSpan advanced = advancedDecoder.Read(new MemoryStream(bytes), begin: 5, length: 15)
        ?? throw new Exception("Expected advance-aligned decoded span.");
    AssertSequence(
        Enumerable.Range(8, 15).Select(value => (double)value).ToArray(),
        advanced.RfHighPass!);

    RfDecodedSpan empty = streamDecoder.Read(new MemoryStream(bytes), begin: 4, length: 0)!;
    AssertEqual(0, empty.Input.Length);

    byte[] shortBytes = BuildPcm16Bytes(Enumerable.Range(0, 28).Select(value => (short)value).ToArray());
    AssertEqual(null, streamDecoder.Read(new MemoryStream(shortBytes), begin: 20, length: 10));
    AssertThrows<ArgumentException>(() => new RfBlockStreamDecoder(pipeline, blockLength: 8, blockCut: 4, blockCutEnd: 4));
    AssertThrows<ArgumentOutOfRangeException>(() => new RfBlockStreamDecoder(pipeline, blockLength, blockCut, blockCutEnd, workerThreads: -1));
    AssertThrows<ArgumentOutOfRangeException>(() => new RfBlockStreamDecoder(pipeline, blockLength, blockCut, blockCutEnd, prefetchBlocks: -1));
}

[Fact(DisplayName = "SOS filter applies forward and forward-backward filtering")]
public void SosFilterAppliesForwardAndForwardBackwardFiltering()
{
    SosSection[] identity = SosFilter.FromScipyArray(1, [1, 0, 0, 1, 0, 0]);
    AssertSequence([1, 2, 3, 4], SosFilter.ApplyForward(identity, [1, 2, 3, 4]));
    AssertSequence([1, 2, 3, 4], SosFilter.ApplyForwardBackward(identity, [1, 2, 3, 4], padLength: 0));

    SosSection[] lowPass = SosFilter.FromScipyArray(1, [0.5, 0, 0, 1, -0.5, 0]);
    AssertSequence([0.5, 0.75, 0.875], SosFilter.ApplyForward(lowPass, [1, 1, 1]));
    AssertSequence([1, 1, 1, 1, 1, 1, 1, 1], SosFilter.ApplyForwardBackward(lowPass, [1, 1, 1, 1, 1, 1, 1, 1]));
    AssertEqual(6, SosFilter.DefaultPadLength(lowPass));

    double[,] zi = SosFilter.SteadyStateInitialConditions(lowPass);
    AssertClose(0.5, zi[0, 0], 1e-12);
    AssertClose(0.0, zi[0, 1], 1e-12);

    SosSection[] nonBinary = SosFilter.FromScipyArray(
        1,
        [0.123456789, 0.234567891, 0.123456789, 1.0, -0.345678912, 0.123456789]);
    double[] signal = Enumerable.Range(0, 32).Select(i => Math.Sin(i * 0.37) * 1000.0).ToArray();
    double[] float32 = SosFilter.ApplyForwardBackwardFloat32(nonBinary, signal);
    AssertSequence(float32.Select(value => (double)(float)value).ToArray(), float32);
    double[] float64 = SosFilter.ApplyForwardBackward(nonBinary, signal);
    AssertTrue(float32.Where((value, i) => value != float64[i]).Any());

    double[] rustInput = Enumerable.Range(0, 32)
        .Select(index => (double)((((index * 37) % 19) - 9) * 17))
        .ToArray();
    uint[] rustBits =
    [
        3261761440, 1092793341, 1110893777, 1111851246, 1109703267, 1107514260, 1104092684, 1100718532,
        1095782194, 1087418848, 975120384, 3234896008, 3243261489, 3248209863, 3251616036, 3255022434,
        3257094770, 3258878218, 3257845104, 3247751310, 1100267661, 1110361455, 1111394570, 1109611124,
        1107538790, 1104132378, 1100726126, 1095777494, 1087413279, 995687424, 3234886930, 3243277327
    ];
    double[] rustCompatible = SosFilter.ApplyForwardBackwardFloat32(nonBinary, rustInput);
    for (int i = 0; i < rustBits.Length; i++)
    {
        AssertEqual(rustBits[i], BitConverter.SingleToUInt32Bits((float)rustCompatible[i]));
    }
}

[Fact(DisplayName = "pulse detection calculates zero crossings")]
public void PulseDetectionCalculatesZeroCrossings()
{
    double[] data = [8.0, 4.0, 1.0, 8.0, 1.0, 4.0, 8.0];
    AssertClose(4.0 / 3.0, PulseDetection.CalculateZeroCrossing(data, 0, 3.0, edge: -1, count: data.Length)!.Value, 1e-12);
    AssertClose(2.0 + (2.0 / 7.0), PulseDetection.CalculateZeroCrossing(data, 2, 3.0, edge: 1, count: data.Length)!.Value, 1e-12);
    AssertClose(4.0 + (2.0 / 3.0), PulseDetection.CalculateZeroCrossing(data, 6, 3.0, edge: -1, count: data.Length, reverse: true)!.Value, 1e-12);

    double[] noCrossing = [8.0, 4.0, 4.0, 8.0, 30.0, 99.0, 8.0];
    AssertEqual<double?>(null, PulseDetection.CalculateZeroCrossing(noCrossing, 0, 3.0, edge: 1, count: noCrossing.Length));
}

[Fact(DisplayName = "pulse detection finds threshold pulses")]
public void PulseDetectionFindsThresholdPulses()
{
    double[] sync = [0, 0, 5, 4, 1, 1, 1, 5, 5, 1, 1, 5, 1, 1];
    IReadOnlyList<Pulse> pulses = PulseDetection.FindPulses(sync, high: 2.0, minimumSyncLength: 2, maximumSyncLength: 3);
    AssertEqual(2, pulses.Count);
    AssertEqual(new Pulse(4, 3), pulses[0]);
    AssertEqual(new Pulse(9, 2), pulses[1]);
    AssertTrue(PulseDetection.InRange(2, 2, 3));
    AssertFalse(PulseDetection.InRange(4, 2, 3));
}

[Fact(DisplayName = "level detection computes fallback vsync means")]
public void LevelDetectionComputesFallbackVsyncMeans()
{
    double[] demod = Enumerable.Range(0, 40).Select(i => (double)i).ToArray();
    Pulse[] pulses = [new Pulse(0, 2), new Pulse(10, 10), new Pulse(20, 20)];
    (int[] locations, double[] means) = LevelDetection.FallbackVsyncLocationMeans(
        demod,
        pulses,
        sampleFrequencyMHz: 2.0,
        minimumLength: 5.0,
        maximumLength: 15.0);

    AssertEqual(1, locations.Length);
    AssertEqual(1, locations[0]);
    AssertSequence([14.5], means);
    AssertThrows<ArgumentException>(() => LevelDetection.FallbackVsyncLocationMeans(
        demod,
        [new Pulse(38, 4)],
        sampleFrequencyMHz: 2.0,
        minimumLength: 1.0,
        maximumLength: 10.0));
}

[Fact(DisplayName = "level detection finds sync and blank levels")]
public void LevelDetectionFindsSyncAndBlankLevels()
{
    double[] data = [10, 10, 0, 0, 10, 10, 10, 10, 10, 10];
    (double sync, double blank)? levels = LevelDetection.FindSyncLevels(data, lineFrequency: 2.0);
    if (!levels.HasValue)
    {
        throw new Exception("Expected sync levels.");
    }

    (double sync, double blank) = levels.Value;
    AssertClose(0.0, sync, 1e-12);
    AssertClose(10.0, blank, 1e-12);
    AssertEqual<(double, double)?>(null, LevelDetection.FindSyncLevels([1, 1, 1], lineFrequency: 1.0));

    double[] decimated = [100, 10, 0, 0, 10, 10, 50, 10, 10, 10, 10, 10];
    (double sync, double blank)? reducedLevels = LevelDetection.FindSyncLevels(decimated, lineFrequency: 2.0, divisor: 2);
    if (!reducedLevels.HasValue)
    {
        throw new Exception("Expected decimated sync levels.");
    }

    AssertClose(0.0, reducedLevels.Value.sync, 1e-12);
    AssertClose(50.0, reducedLevels.Value.blank, 1e-12);
    AssertThrows<ArgumentOutOfRangeException>(() => LevelDetection.FindSyncLevels(decimated, lineFrequency: 2.0, divisor: 0));
}

[Fact(DisplayName = "VBI serration detector matches v0.4.0 rules")]
public void VsyncSerrationDetectorMatchesV04Rules()
{
    AssertIntSequence([1, 4], VsyncSerrationDetector.LocalMinimaIndices([3.0, 1.0, 1.0, 2.0, 0.0, 1.0]));
    AssertIntSequence(
        [10, 30],
        VsyncSerrationDetector.ArbitrateVsync(
            vsyncLength: 20,
            envelopeMinima: [10],
            serrations: [],
            dataLength: 100));
    AssertIntSequence(
        [40, 20],
        VsyncSerrationDetector.ArbitrateVsync(
            vsyncLength: 20,
            envelopeMinima: [40],
            serrations: [],
            dataLength: 50));
    AssertIntSequence(
        [20, 20, 60],
        VsyncSerrationDetector.ArbitrateVsync(
            vsyncLength: 10,
            envelopeMinima: [25, 35, 65],
            serrations: [20, 60, 90],
            dataLength: 120));

    var detector = new VsyncSerrationDetector(
        sampleRateHz: 4_000_000.0,
        framesPerSecond: 25.0,
        frameLines: 625.0,
        equalizingPulseUs: 2.35);
    AssertEqual(9, detector.EqualizingPulseLength);
    AssertEqual(256, detector.LineLength);
    AssertEqual(80_000, detector.VsyncLength);
    AssertEqual(9, detector.OriginalRateEqualizingPulseLength);
    AssertEqual(256, detector.OriginalRateLineLength);

    double[] field = Enumerable.Repeat(100.0, detector.LineLength * 400).ToArray();
    int firstPulse = detector.LineLength * 20;
    for (int pulse = 0; pulse < 11; pulse++)
    {
        int start = firstPulse + (pulse * detector.LineLength / 2);
        Array.Fill(field, 60.0, start, detector.EqualizingPulseLength);
    }

    AssertTrue(VsyncSerrationDetector.TryMeasureSerration(
        field,
        firstPulse + (detector.LineLength * 2),
        detector.LineLength,
        detector.EqualizingPulseLength,
        detector.MinimumVbiLength,
        detector.MaximumVbiLength,
        out VsyncSerrationMeasurement? measurement));
    AssertTrue(measurement is not null);
    AssertClose(60.0, measurement!.SyncLevel, 1e-12);
    AssertClose(100.0, measurement.BlankLevel, 1e-12);
    AssertTrue(measurement.End - measurement.Start > detector.MinimumVbiLength);
    AssertTrue(measurement.End - measurement.Start < detector.MaximumVbiLength);

    var automatic = new VsyncSerrationDetector(
        sampleRateHz: 4_000_000.0,
        framesPerSecond: 25.0,
        frameLines: 625.0,
        equalizingPulseUs: 2.35);
    VsyncSerrationResult automaticResult = automatic.Analyze(field);
    if (!automaticResult.FoundSerration)
    {
        throw new Exception(
            $"Serration search failed: envelope minima={automaticResult.EnvelopeMinima.Count}, "
            + $"harmonic minima={automaticResult.HarmonicMinima.Count}, candidates={automaticResult.Candidates.Count}; "
            + $"envelope=[{string.Join(',', automaticResult.EnvelopeMinima)}], "
            + $"harmonic=[{string.Join(',', automaticResult.HarmonicMinima.Take(40))}].");
    }
    AssertTrue(automaticResult.Measurements.Count > 0);
    AssertClose(60.0, automaticResult.Measurements[0].SyncLevel, 1e-12);
    AssertClose(100.0, automaticResult.Measurements[0].BlankLevel, 1e-12);
    VsyncSerrationResult tooShort = new VsyncSerrationDetector(
        4_000_000.0,
        25.0,
        625.0,
        2.35).Analyze([60.0, 100.0, 60.0]);
    AssertFalse(tooShort.FoundSerration);
    AssertEqual(0, tooShort.Candidates.Count);

    detector.PushLevels(60.0, 100.0);
    detector.PushLevels(62.0, 102.0);
    AssertTrue(detector.HasLevels);
    (double sync, double blank) = detector.PullLevels()!.Value;
    AssertClose(61.0, sync, 1e-12);
    AssertClose(101.0, blank, 1e-12);

    AssertTrue(VsyncSerrationDetector.CheckLevels(
        field,
        oldSync: 60.0,
        newSync: 62.0,
        newBlank: 102.0,
        referenceSync: 60.0,
        hzIre: 1.0));
    AssertFalse(VsyncSerrationDetector.CheckLevels(
        field,
        oldSync: 60.0,
        newSync: 20.0,
        newBlank: 100.0,
        referenceSync: 60.0,
        hzIre: 1.0));

    var fortyMegahertzAnalyzer = new SyncAnalyzer(
        sampleRateHz: 40_000_000.0,
        linePeriodUs: 64.0,
        hsyncPulseUs: 4.7,
        equalizingPulseUs: 2.35,
        vsyncPulseUs: 27.3);
    const int halfLine = 1280;
    int eqLength = (int)Math.Round(fortyMegahertzAnalyzer.UsecToSamples(2.35), MidpointRounding.ToEven);
    int vsyncLength = (int)Math.Round(fortyMegahertzAnalyzer.UsecToSamples(27.3), MidpointRounding.ToEven);
    double[] refinementField = Enumerable.Repeat(100.0, 30 * halfLine).ToArray();
    int pulseStart = 1000;
    for (int pulse = 0; pulse < 18; pulse++)
    {
        int length = pulse is >= 6 and < 12 ? vsyncLength : eqLength;
        Array.Fill(refinementField, 60.0, pulseStart + (pulse * halfLine), length);
    }

    SerrationLevelRefinement refinement = LevelDetection.RefineSerrationLevels(
        refinementField,
        initialSyncLevel: 60.0,
        initialBlankLevel: 100.0,
        fortyMegahertzAnalyzer,
        referenceSyncLevel: 60.0,
        hzIre: 1.0,
        out SerrationLevelFailureKind refinementFailure)
        ?? throw new Exception("Expected VBI pulse-level refinement.");
    AssertEqual(SerrationLevelFailureKind.None, refinementFailure);
    AssertClose(60.0, refinement.SyncLevel, 1e-12);
    AssertClose(100.0, refinement.BlankLevel, 1e-12);
    AssertEqual(6, refinement.VsyncPulseCount);
    AssertEqual(18, refinement.PulseCount);

    AssertEqual<SerrationLevelRefinement?>(null, LevelDetection.RefineSerrationLevels(
        [],
        initialSyncLevel: 60.0,
        initialBlankLevel: 100.0,
        fortyMegahertzAnalyzer,
        referenceSyncLevel: 60.0,
        hzIre: 1.0,
        out SerrationLevelFailureKind missingFailure));
    AssertEqual(SerrationLevelFailureKind.MissingLevels, missingFailure);

    double[] vsyncOnly = Enumerable.Repeat(100.0, 12 * halfLine).ToArray();
    for (int pulse = 0; pulse < 6; pulse++)
    {
        Array.Fill(vsyncOnly, 60.0, 1000 + (pulse * halfLine), vsyncLength);
    }

    AssertEqual<SerrationLevelRefinement?>(null, LevelDetection.RefineSerrationLevels(
        vsyncOnly,
        initialSyncLevel: 60.0,
        initialBlankLevel: 100.0,
        fortyMegahertzAnalyzer,
        referenceSyncLevel: 60.0,
        hzIre: 1.0,
        out SerrationLevelFailureKind nonFiniteFailure));
    AssertEqual(SerrationLevelFailureKind.NonFiniteLevels, nonFiniteFailure);

    AssertEqual<SerrationLevelRefinement?>(null, LevelDetection.RefineSerrationLevels(
        refinementField,
        initialSyncLevel: 60.0,
        initialBlankLevel: 100.0,
        fortyMegahertzAnalyzer,
        referenceSyncLevel: 60.0,
        hzIre: 0.5,
        out SerrationLevelFailureKind checkFailure));
    AssertEqual(SerrationLevelFailureKind.LevelCheckFailed, checkFailure);

    double[] fallbackField = Enumerable.Repeat(100.0, 322 * 2560).ToArray();
    for (int line = 0; line < 240; line++)
    {
        Array.Fill(fallbackField, 60.0, 100 + (line * 2560), 188);
    }

    int fallbackVbiStart = 250 * 2560;
    for (int pulse = 0; pulse < 18; pulse++)
    {
        int length = pulse is >= 6 and < 12 ? vsyncLength : eqLength;
        Array.Fill(fallbackField, 60.0, fallbackVbiStart + (pulse * halfLine), length);
    }

    SerrationLevelRefinement fallbackRefinement = LevelDetection.SearchFallbackSerrationLevels(
        fallbackField,
        fortyMegahertzAnalyzer,
        divisor: 3,
        blankLevel: 100.0,
        referenceSyncLevel: 60.0,
        hzIre: 1.0,
        checkLongPulses: false,
        out SerrationLevelFailureKind fallbackFailure)
        ?? throw new Exception("Expected iterative VBI level search.");
    AssertEqual(SerrationLevelFailureKind.None, fallbackFailure);
    AssertClose(60.0, fallbackRefinement.SyncLevel, 1e-12);
    AssertClose(100.0, fallbackRefinement.BlankLevel, 1e-12);
    AssertEqual(6, fallbackRefinement.VsyncPulseCount);
    AssertTrue(fallbackRefinement.PulseCount > 200);

    var syncAnalyzer = new SyncAnalyzer(
        sampleRateHz: 4_000_000.0,
        linePeriodUs: 64.0,
        hsyncPulseUs: 4.7,
        equalizingPulseUs: 2.35,
        vsyncPulseUs: 27.3);
    var frameSpec = new TbcFrameSpec(
        "PAL",
        OutputLineLength: 4,
        OutputLineCount: 2,
        OutputSampleRateHz: 4_000_000.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var converter = new VideoOutputConverter(
        ire0: 100.0,
        hzIre: 1.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 10.0);
    var syncPipeline = new TbcFieldDecodePipeline(
        syncAnalyzer,
        new TbcFieldRenderer(frameSpec, converter),
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(DetectLevels: true, LevelDetectDivisor: 1),
        decodeType: "vhs",
        vsyncSerrationDetector: new VsyncSerrationDetector(
            4_000_000.0,
            25.0,
            625.0,
            2.35));
    object prepared = InvokePrivateMethod(
        syncPipeline,
        "PrepareSyncSpan",
        new RfDecodedSpan(0, field, field, field, VideoLowPass: field),
        null,
        true,
        true)!;
    AssertClose(80.0, Convert.ToDouble(PrivatePropertyValue(prepared, "Threshold")), 1e-12);
}

[Fact(DisplayName = "VHS debug mode hashes sync references")]
public void VhsDebugModeHashesSyncReferences()
{
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 4_000_000.0,
        linePeriodUs: 64.0,
        hsyncPulseUs: 4.7,
        equalizingPulseUs: 2.35,
        vsyncPulseUs: 27.3);
    var frameSpec = new TbcFrameSpec(
        "PAL",
        OutputLineLength: 4,
        OutputLineCount: 2,
        OutputSampleRateHz: 4_000_000.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var converter = new VideoOutputConverter(
        ire0: 100.0,
        hzIre: 1.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 10.0);
    var diagnostics = new List<(string Level, string Message)>();
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(frameSpec, converter),
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(DetectLevels: true, LevelDetectDivisor: 1),
        decodeType: "vhs",
        diagnosticLogger: (level, message) => diagnostics.Add((level, message)),
        debug: true);

    _ = CaptureException<TbcFieldDecodeRecoveryException>(() => pipeline.Decode(
        new RfDecodedSpan(0, [], [1.0, 2.0], [], VideoLowPass: [1.0, 2.0])));

    AssertEqual("DEBUG", diagnostics[0].Level);
    AssertEqual(
        "Hashed field sync reference a0e3efbd7ed4640a5e23bba9321a926d",
        diagnostics[0].Message);
}

[Fact(DisplayName = "CVBS auto-sync detects and applies field levels")]
public void CvbsAutoSyncDetectsAndAppliesFieldLevels()
{
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    double[] video = Enumerable.Repeat(100.0, 320).ToArray();
    PaintPulse(video, 10, 10, -40.0);
    PaintPulse(video, 110, 10, -40.0);
    PaintPulse(video, 210, 10, -40.0);

    CvbsSyncLevels levels = CvbsSyncLevelDetector.Find(video, analyzer)
        ?? throw new InvalidOperationException("CVBS sync levels were not detected.");
    AssertClose(-40.0, levels.SyncLevel, 1e-12);
    AssertClose(100.0, levels.BlankLevel, 1e-12);

    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 2,
        OutputSampleRateHz: 4_000_000.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var staticConverter = new VideoOutputConverter(
        ire0: 0.0,
        hzIre: 1.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 10.0);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, staticConverter),
        staticConverter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(
            DetectLevels: false,
            LevelDetectDivisor: 1,
            CvbsAutoSync: true));
    TbcDecodedField field = pipeline.Decode(new RfDecodedSpan(
        0,
        video,
        video,
        video,
        VideoLowPass: video));
    AssertTrue(field.OutputConverter is not null);
    AssertClose(100.0, field.OutputConverter!.Ire0, 1e-12);
    AssertClose(3.5, field.OutputConverter.HzIre, 1e-12);
    AssertEqual((ushort)256, field.Samples[0]);

    var noAutoPipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, staticConverter),
        staticConverter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: SyncDetectionOptions.Disabled);
    TbcDecodedField staticField = noAutoPipeline.Decode(new RfDecodedSpan(0, video, video, video));
    AssertTrue(staticField.OutputConverter is null);

    double[] shiftedSync = new double[180];
    PaintPulse(shiftedSync, 20, 2, -20.0);
    PaintPulse(shiftedSync, 40, 2, -20.0);
    PaintPulse(shiftedSync, 60, 20, -20.0);
    PaintPulse(shiftedSync, 100, 2, -20.0);
    PaintPulse(shiftedSync, 120, 2, -20.0);
    IReadOnlyList<Pulse> initialPulses = PulseDetection.FindPulses(
        shiftedSync,
        high: -20.0,
        minimumSyncLength: 0,
        maximumSyncLength: 5000);
    CvbsPulseDetectionResult recalibrated = CvbsPulseDetector.Refine(
        shiftedSync,
        initialPulses,
        initialThreshold: -20.0,
        analyzer,
        staticConverter)
        ?? throw new InvalidOperationException("CVBS pulses were not recalibrated.");
    AssertTrue(recalibrated.Recalibrated);
    AssertClose(-10.0, recalibrated.Threshold, 1e-12);
    AssertEqual(5, recalibrated.Pulses.Count);

    const int clampLineLength = 200;
    const int clampLineCount = 20;
    var clampSpec = new TbcFrameSpec(
        "NTSC",
        clampLineLength,
        clampLineCount,
        OutputSampleRateHz: 4_000_000.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var measuredRenderer = new TbcFieldRenderer(
        clampSpec,
        staticConverter,
        cvbsClampAgc: new CvbsClampAgcOptions(Speed: 1.0, GainFactor: 1.0, SetGain: 1.0));
    double[] measuredVideo = Enumerable.Repeat(200.0, clampLineLength * clampLineCount).ToArray();
    for (int line = 0; line < clampLineCount; line++)
    {
        int lineBase = line * clampLineLength;
        for (int sample = lineBase + 12; sample < lineBase + 72; sample++)
        {
            measuredVideo[sample] = 60.0;
        }

        for (int sample = lineBase + 96; sample < lineBase + 164; sample++)
        {
            measuredVideo[sample] = 100.0;
        }
    }

    double[] measuredLocations = Enumerable.Range(0, clampLineCount + 1)
        .Select(line => line * (double)clampLineLength)
        .ToArray();
    measuredRenderer.RenderField(measuredVideo, measuredLocations);
    AssertTrue(measuredRenderer.LastCvbsSyncLevels.HasValue);
    AssertClose(60.0, measuredRenderer.LastCvbsSyncLevels!.Value.SyncLevel, 1e-12);
    AssertClose(100.0, measuredRenderer.LastCvbsSyncLevels.Value.BlankLevel, 1e-12);

    var measuredPipeline = new TbcFieldDecodePipeline(
        analyzer,
        measuredRenderer,
        staticConverter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(
            DetectLevels: false,
            LevelDetectDivisor: 1,
            CvbsAutoSync: true),
        decodeType: "cvbs");
    double[] nextField = Enumerable.Repeat(100.0, 5_000).ToArray();
    PaintPulse(nextField, 10, 10, 0.0);
    PaintPulse(nextField, 110, 10, 60.0);
    PaintPulse(nextField, 210, 10, 60.0);
    PaintTestVBlank(nextField, line0: 310, isFirstField: true, system: "NTSC");
    for (int sample = 0; sample < nextField.Length; sample++)
    {
        if (nextField[sample] == -40.0)
        {
            nextField[sample] = 60.0;
        }
    }

    for (int line = 11; line <= 34; line++)
    {
        PaintPulse(nextField, 310 + (line * 100), 10, 60.0);
    }

    CvbsSyncLevels crudeNextLevels = CvbsSyncLevelDetector.Find(nextField, analyzer)
        ?? throw new InvalidOperationException("Expected crude CVBS levels for the next field.");
    AssertClose(0.0, crudeNextLevels.SyncLevel, 1e-12);
    TbcDecodedField measuredField = measuredPipeline.Decode(new RfDecodedSpan(
        0,
        nextField,
        nextField,
        nextField,
        VideoLowPass: nextField));
    AssertTrue(measuredField.OutputConverter is not null);
    AssertClose(100.0, measuredField.OutputConverter!.Ire0, 1e-12);
    AssertClose(1.0, measuredField.OutputConverter.HzIre, 1e-12);
    AssertEqual(90, measuredField.SyncConfidence);
    AssertEqual<int?>(90, measuredPipeline.CaptureState().PreviousSyncConfidence);
}

[Fact(DisplayName = "CVBS clamp AGC levels follow upstream speculative decode delay")]
public void CvbsClampAgcLevelsFollowSpeculativeDecodeDelay()
{
    const int lineLength = 200;
    const int lineCount = 20;
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var frameSpec = new TbcFrameSpec(
        "NTSC",
        lineLength,
        lineCount,
        OutputSampleRateHz: 4_000_000.0,
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
    var renderer = new TbcFieldRenderer(
        frameSpec,
        converter,
        cvbsClampAgc: new CvbsClampAgcOptions(Speed: 1.0, GainFactor: 1.0, SetGain: 1.0));
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(
            DetectLevels: false,
            LevelDetectDivisor: 1,
            CvbsAutoSync: true),
        decodeType: "cvbs");
    double[] lineLocations = Enumerable.Range(0, lineCount + 1)
        .Select(line => line * (double)lineLength)
        .ToArray();

    static double[] BuildClampField(double syncLevel, double blankLevel)
    {
        double[] samples = Enumerable.Repeat(200.0, lineLength * lineCount).ToArray();
        for (int line = 0; line < lineCount; line++)
        {
            int lineBase = line * lineLength;
            Array.Fill(samples, syncLevel, lineBase + 12, 60);
            Array.Fill(samples, blankLevel, lineBase + 96, 68);
        }

        return samples;
    }

    double[] decodeVideo = Enumerable.Repeat(100.0, 5_000).ToArray();
    PaintPulse(decodeVideo, 10, 10, -40.0);
    PaintPulse(decodeVideo, 110, 10, -40.0);
    PaintPulse(decodeVideo, 210, 10, -40.0);
    PaintTestVBlank(decodeVideo, line0: 310, isFirstField: true, system: "NTSC");
    for (int line = 11; line <= 34; line++)
    {
        PaintPulse(decodeVideo, 310 + (line * 100), 10, -40.0);
    }

    CvbsSyncLevels detected = CvbsSyncLevelDetector.Find(decodeVideo, analyzer)
        ?? throw new InvalidOperationException("Expected CVBS levels for the speculative field.");
    renderer.RenderField(BuildClampField(syncLevel: 60.0, blankLevel: 100.0), lineLocations);

    TbcDecodedField first = pipeline.Decode(new RfDecodedSpan(
        0,
        decodeVideo,
        decodeVideo,
        decodeVideo,
        VideoLowPass: decodeVideo));
    AssertTrue(first.OutputConverter is not null);
    AssertClose(detected.BlankLevel, first.OutputConverter!.Ire0, 1e-12);
    AssertClose(
        (detected.BlankLevel - detected.SyncLevel) / 40.0,
        first.OutputConverter.HzIre,
        1e-12);
    TbcFieldDecodeState afterFirst = pipeline.CaptureState();
    AssertClose(60.0, afterFirst.DelayedCvbsSyncLevels!.Value.SyncLevel, 1e-12);
    AssertClose(100.0, afterFirst.DelayedCvbsSyncLevels.Value.BlankLevel, 1e-12);

    renderer.RenderField(BuildClampField(syncLevel: 20.0, blankLevel: 180.0), lineLocations);
    object preparedSecond = InvokePrivateMethod(
        pipeline,
        "PrepareSyncSpan",
        new RfDecodedSpan(
            0,
            decodeVideo,
            decodeVideo,
            decodeVideo,
            VideoLowPass: decodeVideo),
        null,
        true,
        true)!;
    var delayedConverter = (VideoOutputConverter?)PrivatePropertyValue(preparedSecond, "ConverterOverride");
    AssertTrue(delayedConverter is not null);
    AssertClose(100.0, delayedConverter!.Ire0, 1e-12);
    AssertClose(1.0, delayedConverter.HzIre, 1e-12);
    AssertClose(20.0, renderer.LastCvbsSyncLevels!.Value.SyncLevel, 1e-12);
    AssertEqual(afterFirst.DelayedCvbsSyncLevels, pipeline.CaptureState().DelayedCvbsSyncLevels);

    SetPrivateFieldValue(
        pipeline,
        "_delayedCvbsSyncLevels",
        ((double SyncLevel, double BlankLevel)?)(20.0, 180.0));
    SetPrivateFieldValue(pipeline, "_previousSyncConfidence", 12);
    pipeline.RestoreStateForRetry(afterFirst);
    AssertEqual(afterFirst.DelayedCvbsSyncLevels, pipeline.CaptureState().DelayedCvbsSyncLevels);
    AssertEqual(afterFirst.PreviousSyncConfidence, pipeline.CaptureState().PreviousSyncConfidence);
}

[Fact(DisplayName = "LD sync calibration matches upstream retry and pulse windows")]
public void LaserDiscSyncCalibrationMatchesUpstream()
{
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 2.0,
        vsyncPulseUs: 20.0);
    var converter = new VideoOutputConverter(
        ire0: 0.0,
        hzIre: 1.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 10.0);

    double[] shiftedSync = new double[180];
    PaintPulse(shiftedSync, 20, 2, -20.0);
    PaintPulse(shiftedSync, 40, 2, -20.0);
    PaintPulse(shiftedSync, 60, 20, -20.0);
    PaintPulse(shiftedSync, 100, 2, -20.0);
    PaintPulse(shiftedSync, 120, 2, -20.0);
    IReadOnlyList<Pulse> initialPulses = PulseDetection.FindPulses(
        shiftedSync,
        high: -20.0,
        minimumSyncLength: 0,
        maximumSyncLength: 5000);
    CvbsPulseDetectionResult laserDiscPulses = CvbsPulseDetector.RefineLaserDisc(
        shiftedSync,
        initialPulses,
        initialThreshold: -20.0,
        analyzer,
        converter)
        ?? throw new InvalidOperationException("LD pulses were not recalibrated.");
    AssertTrue(laserDiscPulses.Recalibrated);
    AssertClose(-10.0, laserDiscPulses.Threshold, 1e-12);
    AssertEqual(5, laserDiscPulses.Pulses.Count);

    double[] threeMicrosecondEqualizing = new double[180];
    PaintPulse(threeMicrosecondEqualizing, 20, 3, -20.0);
    PaintPulse(threeMicrosecondEqualizing, 40, 3, -20.0);
    PaintPulse(threeMicrosecondEqualizing, 60, 20, -20.0);
    PaintPulse(threeMicrosecondEqualizing, 100, 3, -20.0);
    PaintPulse(threeMicrosecondEqualizing, 120, 3, -20.0);
    IReadOnlyList<Pulse> wideEqualizingPulses = PulseDetection.FindPulses(
        threeMicrosecondEqualizing,
        high: -20.0,
        minimumSyncLength: 0,
        maximumSyncLength: 5000);
    CvbsPulseDetectionResult cvbsWide = CvbsPulseDetector.Refine(
        threeMicrosecondEqualizing,
        wideEqualizingPulses,
        initialThreshold: -20.0,
        analyzer,
        converter)!;
    CvbsPulseDetectionResult ldWide = CvbsPulseDetector.RefineLaserDisc(
        threeMicrosecondEqualizing,
        wideEqualizingPulses,
        initialThreshold: -20.0,
        analyzer,
        converter)!;
    AssertEqual(5, cvbsWide.Pulses.Count);
    AssertTrue(double.IsNaN(ldWide.Threshold));
    AssertEqual(0, ldWide.Pulses.Count);

    var retryConverter = new VideoOutputConverter(
        ire0: 100.0,
        hzIre: 1.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 10.0);
    var pipelineAnalyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 20,
        OutputSampleRateHz: 4_000_000.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var pipeline = new TbcFieldDecodePipeline(
        pipelineAnalyzer,
        new TbcFieldRenderer(spec, retryConverter),
        retryConverter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true,
        decodeType: "ld");
    double[] video = new double[4_000];
    PaintPulse(video, 10, 10, -40.0);
    PaintPulse(video, 110, 10, -40.0);
    PaintTestVBlank(video, line0: 210, isFirstField: true, system: "NTSC");
    for (int line = 11; line <= 34; line++)
    {
        PaintPulse(video, 210 + (line * 100), 10, -40.0);
    }

    TbcDecodedField field = pipeline.Decode(new RfDecodedSpan(
        0,
        video,
        video,
        video,
        VideoLowPass: video));
    AssertTrue(field.OutputConverter is not null);
    AssertClose(0.0, field.OutputConverter!.Ire0, 1e-12);
    AssertClose(-20.0, field.SyncThresholdHz, 1e-12);
}

[Fact(DisplayName = "LD HSync refinement matches the legacy v0.4.0 field path")]
public void LaserDiscHSyncRefinementMatchesLegacyFieldPath()
{
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 4.0,
        equalizingPulseUs: 2.0,
        vsyncPulseUs: 20.0);
    var converter = new VideoOutputConverter(
        ire0: 0.0,
        hzIre: 1.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 10.0);
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 7,
        OutputSampleRateHz: 4_000_000.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, converter),
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        hSyncRefineOptions: HSyncRefineOptions.Disabled,
        decodeLaserDiscVbi: true,
        decodeType: "ld");

    double[] lowPass = new double[800];
    for (int line = 0; line < 8; line++)
    {
        PaintPulse(lowPass, 20 + (line * 100), 4, -40.0);
    }

    lowPass[225] = 100.0;
    var locations = new LineLocationResult(
        Enumerable.Range(0, 8).Select(line => 20.0 + (line * 100.0)).ToArray(),
        new bool[8]);
    var span = new RfDecodedSpan(0, [], lowPass, lowPass, VideoLowPass: lowPass);
    var refined = (LineLocationResult)InvokePrivateMethod(
        pipeline,
        "RefineLineLocationsFromHSync",
        span,
        locations,
        converter)!;

    AssertClose(19.5, refined.Locations[0], 1e-12);
    AssertClose(119.5, refined.Locations[1], 1e-12);
    AssertClose(220.0, refined.Locations[2], 1e-12);
    AssertTrue(refined.Filled[2]);
    for (int line = 3; line <= 6; line++)
    {
        AssertClose(locations.Locations[line], refined.Locations[line], 1e-12);
        AssertTrue(refined.Filled[line]);
    }

    AssertClose(719.5, refined.Locations[7], 1e-12);
}

[Fact(DisplayName = "sync analyzer classifies pulses and builds line locations")]
public void SyncAnalyzerClassifiesPulsesAndBuildsLineLocations()
{
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 10_000_000.0,
        linePeriodUs: 10.0,
        hsyncPulseUs: 4.0,
        equalizingPulseUs: 2.0,
        vsyncPulseUs: 8.0);
    AssertClose(100.0, analyzer.NominalLineLength, 1e-12);

    double[] reference = Enumerable.Repeat(10.0, 800).ToArray();
    PaintPulse(reference, 100, 40, -10.0);
    PaintPulse(reference, 200, 40, -10.0);
    PaintPulse(reference, 300, 20, -10.0);
    PaintPulse(reference, 350, 80, -10.0);
    PaintPulse(reference, 500, 40, -10.0);
    PaintPulse(reference, 600, 40, -10.0);
    PaintPulse(reference, 700, 40, -10.0);

    IReadOnlyList<Pulse> rawPulses = analyzer.FindRawPulses(reference, threshold: 0.0, minimumPulseUs: 1.0, maximumPulseUs: 10.0);
    AssertEqual(7, rawPulses.Count);
    AssertEqual(new Pulse(100, 40), rawPulses[0]);
    AssertEqual(new Pulse(350, 80), rawPulses[3]);

    SyncTiming timing = analyzer.EstimateTiming(rawPulses);
    AssertClose(40.0, timing.HSyncMedian, 1e-12);
    AssertTrue(timing.HSync.Contains(40));
    AssertTrue(timing.Equalizing.Contains(20));
    AssertTrue(timing.VSync.Contains(80));

    var tapeAnalyzer = new SyncAnalyzer(
        sampleRateHz: 10_000_000.0,
        linePeriodUs: 10.0,
        hsyncPulseUs: 4.0,
        equalizingPulseUs: 2.0,
        vsyncPulseUs: 8.0,
        hsyncToleranceUs: 0.7,
        equalizingToleranceUs: 0.9);
    SyncTiming tapeTiming = tapeAnalyzer.EstimateTiming(rawPulses);
    AssertFalse(timing.HSync.Contains(34));
    AssertTrue(tapeTiming.HSync.Contains(34));
    AssertFalse(timing.Equalizing.Contains(12));
    AssertTrue(tapeTiming.Equalizing.Contains(12));
    AssertClose(0.7, tapeAnalyzer.HSyncToleranceUs, 1e-12);
    AssertClose(0.9, tapeAnalyzer.EqualizingToleranceUs, 1e-12);

    IReadOnlyList<ClassifiedSyncPulse> classified = analyzer.ClassifyPulses(rawPulses, timing);
    AssertEqual(7, classified.Count);
    AssertEqual(SyncPulseKind.HSync, classified[0].Kind);
    AssertEqual(SyncPulseKind.Equalizing, classified[2].Kind);
    AssertEqual(SyncPulseKind.VSync, classified[3].Kind);
    AssertClose(100.0, analyzer.ComputeMeanLineLength(classified), 1e-12);

    ClassifiedSyncPulse[] sparse =
    [
        new(SyncPulseKind.HSync, new Pulse(100, 40), false),
        new(SyncPulseKind.HSync, new Pulse(200, 40), true),
        new(SyncPulseKind.HSync, new Pulse(500, 40), true),
        new(SyncPulseKind.HSync, new Pulse(700, 40), true)
    ];
    LineLocationResult locations = analyzer.BuildLineLocations(
        sparse,
        line0Location: 100.0,
        meanLineLength: 100.0,
        processedLines: 7,
        hsyncToleranceLines: 0.1);
    AssertSequence([100, 200, 300, 400, 500, 600, 700], locations.Locations);
    AssertFalse(locations.Filled[0]);
    AssertTrue(locations.Filled[2]);
    AssertTrue(locations.Filled[5]);

    ClassifiedSyncPulse[] nearestFit =
    [
        new(SyncPulseKind.HSync, new Pulse(100, 40), false),
        new(SyncPulseKind.HSync, new Pulse(190, 40), true),
        new(SyncPulseKind.HSync, new Pulse(310, 40), true),
        new(SyncPulseKind.HSync, new Pulse(410, 40), true)
    ];
    LineLocationResult upstreamLocations = analyzer.BuildUpstreamLineLocations(
        nearestFit,
        referencePulse: 100.0,
        referenceLine: 0,
        meanLineLength: 100.0,
        processedLines: 4);
    AssertSequence([100, 190, 310, 400], upstreamLocations.Locations);
    AssertTrue(upstreamLocations.Filled.All(filled => !filled));

    ClassifiedSyncPulse[] equalDistancePulses =
    [
        new(SyncPulseKind.HSync, new Pulse(90, 40), true),
        new(SyncPulseKind.HSync, new Pulse(110, 40), true),
        new(SyncPulseKind.HSync, new Pulse(300, 40), true)
    ];
    LineLocationResult equalDistanceLocations = analyzer.BuildUpstreamLineLocations(
        equalDistancePulses,
        referencePulse: 100.0,
        referenceLine: 0,
        meanLineLength: 100.0,
        processedLines: 1,
        preferEarlierPulseOnEqualDistance: true);
    AssertSequence([90], equalDistanceLocations.Locations);

    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    SyncAnalyzer fromParams = SyncAnalyzer.FromParameters(palVhs, sampleRateHz: 40_000_000.0);
    AssertClose(2560.0, fromParams.NominalLineLength, 1e-12);
    AssertEqual(5, fromParams.NumPulses);
    FormatParameterSet ntscLd = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    SyncAnalyzer ntscLdFromParams = SyncAnalyzer.FromParameters(ntscLd, sampleRateHz: 40_000_000.0);
    AssertClose(2542.0, ntscLdFromParams.NominalLineLength, 1e-12);
    AssertClose(2542.0 / 40.0, ntscLdFromParams.LinePeriodUs, 1e-12);
    SyncAnalyzer tapeFromParams = SyncAnalyzer.FromParameters(
        palVhs,
        sampleRateHz: 40_000_000.0,
        hsyncToleranceUs: 0.7,
        equalizingToleranceUs: 0.9);
    AssertClose(0.7, tapeFromParams.HSyncToleranceUs, 1e-12);
    AssertClose(0.9, tapeFromParams.EqualizingToleranceUs, 1e-12);
}

[Fact(DisplayName = "vblank sync resolver matches upstream distance consensus")]
public void VBlankSyncResolverMatchesUpstreamDistanceConsensus()
{
    var pulses = new List<ClassifiedSyncPulse>
    {
        Sync(SyncPulseKind.HSync, 0),
        Sync(SyncPulseKind.HSync, 100)
    };
    for (int sample = 200; sample <= 450; sample += 50)
    {
        pulses.Add(Sync(SyncPulseKind.Equalizing, sample));
    }

    for (int sample = 500; sample <= 750; sample += 50)
    {
        pulses.Add(Sync(SyncPulseKind.VSync, sample));
    }

    for (int sample = 800; sample <= 1_050; sample += 50)
    {
        pulses.Add(Sync(SyncPulseKind.Equalizing, sample));
    }

    pulses.Add(Sync(SyncPulseKind.HSync, 1_100));
    pulses.Add(Sync(SyncPulseKind.HSync, 1_200));

    VBlankPulseGroup group = VBlankSyncResolver.FindFirstGroup(pulses)!;
    AssertClose(100.0, group.PreviousHSync, 1e-12);
    AssertClose(200.0, group.Equalizing1Start, 1e-12);
    AssertClose(500.0, group.VSyncStart!.Value, 1e-12);
    AssertClose(800.0, group.Equalizing2Start, 1e-12);
    AssertClose(1_050.0, group.Equalizing2End, 1e-12);
    AssertClose(1_100.0, group.FollowingHSync, 1e-12);
    AssertTrue(VBlankSyncResolver.HasValidStateMachineTiming(group, 100.0, 6));

    VBlankSyncEstimate estimate = VBlankSyncResolver.EstimateLine0(
        pulses,
        group,
        meanLineLength: 100.0,
        system: "NTSC",
        numEqualizingPulses: 6,
        isFirstField: true,
        currentFieldLines: 263)!;
    AssertClose(100.0, estimate.Line0Location, 1e-12);
    AssertClose(1_100.0, estimate.FirstHSyncLocation, 1e-12);
    AssertClose(10.0, estimate.FirstHSyncLine, 1e-12);
    AssertEqual(6, estimate.ValidDistanceCount);

    VBlankPulseGroup lastGroup = new(
        PreviousHSync: 26_400,
        Equalizing1Start: 26_450,
        VSyncStart: 26_750,
        Equalizing2Start: 27_050,
        Equalizing2End: 27_300,
        FollowingHSync: 27_400);
    VBlankSyncConsensus consensus = VBlankSyncResolver.EstimateLine0FromGroups(
        pulses,
        group,
        lastGroup,
        meanLineLength: 100.0,
        system: "NTSC",
        numEqualizingPulses: 6,
        isFirstField: true,
        currentFieldLines: 263);
    AssertTrue(consensus.First is not null);
    AssertTrue(consensus.Last is not null);
    AssertTrue(consensus.Combined is not null);
    AssertClose(100.0, consensus.Combined!.Line0Location, 1e-12);
    AssertClose(1_100.0, consensus.Combined.FirstHSyncLocation, 1e-12);
    AssertEqual(28, consensus.Combined.ValidDistanceCount);

    VBlankPulseGroup conflictingLast = lastGroup with
    {
        PreviousHSync = lastGroup.PreviousHSync + 60,
        Equalizing1Start = lastGroup.Equalizing1Start + 60,
        VSyncStart = lastGroup.VSyncStart + 60,
        Equalizing2Start = lastGroup.Equalizing2Start + 60,
        Equalizing2End = lastGroup.Equalizing2End + 60,
        FollowingHSync = lastGroup.FollowingHSync + 60
    };
    VBlankSyncConsensus conflict = VBlankSyncResolver.EstimateLine0FromGroups(
        pulses,
        group,
        conflictingLast,
        meanLineLength: 100.0,
        system: "NTSC",
        numEqualizingPulses: 6,
        isFirstField: true,
        currentFieldLines: 263);
    AssertTrue(conflict.First is not null);
    AssertTrue(conflict.Last is not null);
    AssertEqual<VBlankSyncEstimate?>(null, conflict.Combined);

    ClassifiedSyncPulse[] partialLastVBlank =
    [
        Sync(SyncPulseKind.HSync, 0),
        Sync(SyncPulseKind.Equalizing, 26_350, inOrder: false),
        Sync(SyncPulseKind.VSync, 26_650),
        Sync(SyncPulseKind.EqualizingSecond, 26_950, inOrder: false),
        Sync(SyncPulseKind.EqualizingSecond, 27_200, inOrder: false),
        Sync(SyncPulseKind.HSync, 27_250)
    ];
    VBlankSyncConsensus partialConsensus = VBlankSyncResolver.EstimateLine0FromTransitions(
        partialLastVBlank,
        meanLineLength: 100.0,
        system: "NTSC",
        numEqualizingPulses: 6,
        isFirstField: true,
        currentFieldLines: 263,
        firstFieldLines: 263);
    AssertEqual<VBlankSyncEstimate?>(null, partialConsensus.First);
    AssertTrue(partialConsensus.Last is not null);
    AssertClose(0.0, partialConsensus.Last!.Line0Location, 1e-12);
    AssertClose(1_000.0, partialConsensus.Last.FirstHSyncLocation, 1e-12);
    AssertEqual(1, partialConsensus.Last.ValidDistanceCount);

    ClassifiedSyncPulse[] incomplete =
    [
        Sync(SyncPulseKind.HSync, 0),
        Sync(SyncPulseKind.Equalizing, 50),
        Sync(SyncPulseKind.VSync, 100),
        Sync(SyncPulseKind.HSync, 150)
    ];
    AssertEqual<VBlankPulseGroup?>(null, VBlankSyncResolver.FindFirstGroup(incomplete));

    var shortTapeBlank = new List<ClassifiedSyncPulse> { Sync(SyncPulseKind.HSync, 0) };
    for (int pulse = 0; pulse < 5; pulse++)
    {
        shortTapeBlank.Add(Sync(SyncPulseKind.Equalizing, 50 + (pulse * 50)));
    }

    shortTapeBlank.Add(Sync(SyncPulseKind.VSync, 300));
    for (int pulse = 0; pulse < 5; pulse++)
    {
        shortTapeBlank.Add(Sync(SyncPulseKind.Equalizing, 350 + (pulse * 50)));
    }

    shortTapeBlank.Add(Sync(SyncPulseKind.HSync, 600));
    AssertTrue(VBlankSyncResolver.FindFirstGroup(shortTapeBlank, blankLengthThreshold: 9) is not null);
    AssertEqual<VBlankPulseGroup?>(null, VBlankSyncResolver.FindFirstGroup(shortTapeBlank, blankLengthThreshold: 12));
    AssertThrows<ArgumentOutOfRangeException>(() => VBlankSyncResolver.FindFirstGroup(shortTapeBlank, blankLengthThreshold: -1));

    VBlankPulseGroup compressed = new(
        PreviousHSync: 0,
        Equalizing1Start: 50,
        VSyncStart: 100,
        Equalizing2Start: 150,
        Equalizing2End: 150,
        FollowingHSync: 200);
    AssertFalse(VBlankSyncResolver.HasValidStateMachineTiming(compressed, 100.0, 6));
}

[Fact(DisplayName = "PAL CVBS initial second field uses Release 4.0 local vblank anchor")]
public void PalCvbsInitialSecondFieldUsesReleaseFourLocalVblankAnchor()
{
    var group = new VBlankPulseGroup(
        PreviousHSync: 88_577,
        Equalizing1Start: 89_857,
        VSyncStart: 96_257,
        Equalizing2Start: 102_657,
        Equalizing2End: 107_777,
        FollowingHSync: 110_337);

    Line0Resolution resolution = TbcFieldDecodePipeline.TryResolveInitialPalCvbsSecondFieldLine0(
        decodeType: "cvbs",
        system: "PAL",
        hasPreviousSync: false,
        firstVBlank: group,
        numEqualizingPulses: 5,
        nominalLineLength: 2_560.0,
        estimatedFirstField: false)!;
    AssertClose(87_297.0, resolution.Location, 0.0);
    AssertClose(110_337.0, resolution.FirstHSyncLocation, 0.0);
    AssertEqual(90, resolution.InitialSyncConfidence);

    VBlankSyncEstimate firstField = TbcFieldDecodePipeline.TryResolvePalDirectLocalVBlankEstimate(
        decodeType: "cvbs",
        system: "PAL",
        firstVBlank: group with { VSyncStart = 32_256 },
        numEqualizingPulses: 5,
        nominalLineLength: 2_560.0,
        isFirstField: true)!;
    AssertClose(24_576.0, firstField.Line0Location, 0.0);
    AssertClose(45_056.0, firstField.FirstHSyncLocation, 0.0);
    AssertEqual<Line0Resolution?>(
        null,
        TbcFieldDecodePipeline.TryResolveInitialPalCvbsSecondFieldLine0(
            decodeType: "cvbs",
            system: "PAL",
            hasPreviousSync: true,
            firstVBlank: group,
            numEqualizingPulses: 5,
            nominalLineLength: 2_560.0,
            estimatedFirstField: false));
}

[Fact(DisplayName = "PAL LD initial second field uses Release 4.0 firstFieldH anchor")]
public void PalLaserDiscInitialSecondFieldUsesReleaseFourFirstFieldHAnchor()
{
    var group = new VBlankPulseGroup(
        PreviousHSync: 88_577,
        Equalizing1Start: 89_857,
        VSyncStart: 96_257,
        Equalizing2Start: 102_657,
        Equalizing2End: 107_777,
        FollowingHSync: 110_337);

    Line0Resolution resolution = TbcFieldDecodePipeline.TryResolveInitialPalLaserDiscSecondFieldLine0(
        decodeType: "ld",
        system: "PAL",
        hasPreviousSync: false,
        firstVBlank: group,
        numEqualizingPulses: 5,
        nominalLineLength: 2_560.0,
        estimatedFirstField: false)!;

    AssertClose(87_297.0, resolution.Location, 0.0);
    AssertClose(110_337.0, resolution.FirstHSyncLocation, 0.0);
    AssertEqual(90, resolution.InitialSyncConfidence);

    VBlankSyncEstimate projected = TbcFieldDecodePipeline.TryResolvePalLaserDiscNextVBlankEstimate(
        decodeType: "ld",
        system: "PAL",
        nextVBlank: group with { VSyncStart = 879_662 },
        numEqualizingPulses: 5,
        nominalLineLength: 2_560.0,
        meanLineLength: 2_560.0,
        isFirstField: true,
        currentFieldLines: 312)!;
    AssertClose(71_982.0, projected.Line0Location, 0.0);
    AssertClose(92_462.0, projected.FirstHSyncLocation, 0.0);

    double? previousProjection = TbcFieldDecodePipeline.TryProjectPalLaserDiscPreviousLine0(
        decodeType: "ld",
        system: "PAL",
        previousEndLineAbsoluteSample: 1_553_888 + 870_718.2733659535,
        spanStartSample: 2_378_400);
    AssertClose(46_206.27336595347, previousProjection!.Value, 1e-9);

    AssertEqual<Line0Resolution?>(
        null,
        TbcFieldDecodePipeline.TryResolveInitialPalLaserDiscSecondFieldLine0(
            decodeType: "ld",
            system: "PAL",
            hasPreviousSync: true,
            firstVBlank: group,
            numEqualizingPulses: 5,
            nominalLineLength: 2_560.0,
            estimatedFirstField: false));
}

[Fact(DisplayName = "LD line-zero consensus matches Release 4.0 three-way median")]
public void LaserDiscLineZeroConsensusMatchesRelease40ThreeWayMedian()
{
    var first = new VBlankSyncEstimate(
        Line0Location: 100.0,
        FirstHSyncLocation: 1_100.0,
        FirstHSyncLine: 10.0,
        ValidDistanceCount: 6,
        UnalignedFirstHSyncLocation: 1_099.0);
    var next = new VBlankSyncEstimate(
        Line0Location: 300.0,
        FirstHSyncLocation: 1_300.0,
        FirstHSyncLine: 10.0,
        ValidDistanceCount: 6,
        UnalignedFirstHSyncLocation: 1_299.0);
    var previous = new Line0Resolution(
        Location: 305.0,
        UsedFallback: false,
        ExpectedFirstField: null,
        ExpectedFirstFieldConfidence: 0,
        UsedPreviousEstimate: true,
        FirstHSyncLocation: 1_305.0,
        UnalignedFirstHSyncLocation: 1_304.0,
        InitialSyncConfidence: 90);

    Line0Resolution selected = TbcFieldDecodePipeline.SelectLegacyThreeWayLine0Consensus(
        first,
        next,
        previous);

    AssertClose(300.0, selected.Location, 1e-12);
    AssertClose(1_300.0, selected.FirstHSyncLocation, 1e-12);
    AssertClose(1_299.0, selected.UnalignedFirstHSyncLocation, 1e-12);
    AssertFalse(selected.UsedFallback);
    AssertFalse(selected.UsedPreviousEstimate);
    AssertEqual(100, selected.InitialSyncConfidence);

    Line0Resolution tied = TbcFieldDecodePipeline.SelectLegacyThreeWayLine0Consensus(
        first,
        next with { Line0Location = first.Line0Location },
        previous);
    AssertClose(first.Line0Location, tied.Location, 1e-12);
    AssertClose(next.FirstHSyncLocation, tied.FirstHSyncLocation, 1e-12);
}

[Fact(DisplayName = "fallback vsync resolver matches upstream pulse priorities")]
public void FallbackVSyncResolverMatchesUpstreamPulsePriorities()
{
    AssertEqual(2, Convert.ToInt32(InvokePrivateStaticMethod(
        typeof(FallbackVSyncResolver),
        "DominantPhase",
        1,
        1,
        3)));
    AssertEqual(0, Convert.ToInt32(InvokePrivateStaticMethod(
        typeof(FallbackVSyncResolver),
        "DominantPhase",
        2,
        2,
        1)));
    AssertEqual(8, Convert.ToInt32(InvokePrivateStaticMethod(
        typeof(FallbackVSyncResolver),
        "NormalizeNumpySliceIndex",
        -2,
        10)));
    AssertEqual(0, Convert.ToInt32(InvokePrivateStaticMethod(
        typeof(FallbackVSyncResolver),
        "NormalizeNumpySliceIndex",
        -20,
        10)));

    Pulse[] beginningOfBlanking =
    [
        new(100, 10),
        new(200, 10),
        new(300, 4),
        new(350, 4),
        new(400, 4),
        new(450, 4)
    ];
    ClassifiedSyncPulse[] beginningValid = beginningOfBlanking
        .Select((pulse, index) => new ClassifiedSyncPulse(
            index < 2 ? SyncPulseKind.HSync : SyncPulseKind.Equalizing,
            pulse,
            index != 0))
        .ToArray();
    FallbackVSyncResolution beginning = FallbackVSyncResolver.Resolve(
        beginningValid,
        beginningOfBlanking,
        new double[600],
        new SyncRange(35.0, 60.0),
        meanLineLength: 100.0,
        numEqualizingPulses: 6,
        frameLines: 525)!;
    AssertClose(200.0, beginning.Line0Location, 1e-12);
    AssertEqual<bool?>(true, beginning.IsFirstField);
    AssertEqual(60, beginning.FirstFieldConfidence);
    AssertEqual<double?>(null, beginning.LastLineLocation);

    Pulse[] predictionPulses = [new(100, 10), new(260, 10), new(400, 10)];
    ClassifiedSyncPulse[] predictionValid = predictionPulses
        .Select((pulse, index) => new ClassifiedSyncPulse(SyncPulseKind.HSync, pulse, index != 0))
        .ToArray();
    FallbackVSyncResolution predicted = FallbackVSyncResolver.Resolve(
        predictionValid,
        predictionPulses,
        new double[500],
        new SyncRange(35.0, 60.0),
        meanLineLength: 100.0,
        numEqualizingPulses: 6,
        frameLines: 525,
        expectedLine0: 250.0,
        expectedFirstField: false)!;
    AssertClose(260.0, predicted.Line0Location, 1e-12);
    AssertEqual<bool?>(false, predicted.IsFirstField);
    AssertEqual(50, predicted.FirstFieldConfidence);

    Pulse[] longBlockPulses = [new(100, 10), new(200, 40)];
    ClassifiedSyncPulse[] longBlockValid =
    [
        new(SyncPulseKind.HSync, longBlockPulses[0], false),
        new(SyncPulseKind.VSync, longBlockPulses[1], true)
    ];
    FallbackVSyncResolution longBlock = FallbackVSyncResolver.Resolve(
        longBlockValid,
        longBlockPulses,
        new double[300],
        new SyncRange(35.0, 60.0),
        meanLineLength: 100.0,
        numEqualizingPulses: 6,
        frameLines: 525)!;
    AssertClose(100.0, longBlock.Line0Location, 1e-12);
    AssertEqual<bool?>(null, longBlock.IsFirstField);
    AssertEqual(-1, longBlock.FirstFieldConfidence);
}

[Fact(DisplayName = "sync analyzer refines upstream vblank states")]
public void SyncAnalyzerRefinesUpstreamVBlankStates()
{
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0,
        numPulses: 6);
    var raw = new List<Pulse>
    {
        new(0, 10),
        new(100, 10),
        new(200, 10)
    };
    for (int sample = 300; sample <= 550; sample += 50)
    {
        raw.Add(new Pulse(sample, 5));
    }

    for (int sample = 600; sample <= 850; sample += 50)
    {
        raw.Add(new Pulse(sample, 20));
    }

    for (int sample = 900; sample <= 1_150; sample += 50)
    {
        raw.Add(new Pulse(sample, 5));
    }

    raw.Add(new Pulse(1_200, 10));
    raw.Add(new Pulse(1_300, 10));
    SyncTiming timing = analyzer.EstimateTiming(raw);
    IReadOnlyList<ClassifiedSyncPulse> refined = analyzer.RefinePulses(raw, timing);
    AssertEqual(23, refined.Count);
    AssertEqual(SyncPulseKind.Equalizing, refined[3].Kind);
    AssertEqual(SyncPulseKind.VSync, refined[9].Kind);
    AssertEqual(SyncPulseKind.EqualizingSecond, refined[15].Kind);
    AssertEqual(SyncPulseKind.HSync, refined[21].Kind);
    AssertTrue(refined[21].InOrder);

    Pulse[] compressed =
    [
        new(0, 10),
        new(100, 10),
        new(200, 10),
        new(300, 5),
        new(350, 20),
        new(400, 5),
        new(450, 10)
    ];
    IReadOnlyList<ClassifiedSyncPulse> rejected = analyzer.RefinePulses(compressed, timing);
    AssertTrue(rejected.All(pulse => pulse.Kind == SyncPulseKind.HSync));

    Pulse[] porchMerged = [new(0, 10), new(100, 20), new(200, 10)];
    double[] porchMergedSignal = new double[240];
    Array.Fill(porchMergedSignal, -20.0, 100, 20);
    Array.Fill(porchMergedSignal, -40.0, 104, 10);
    IReadOnlyList<ClassifiedSyncPulse> rescued = analyzer.RefinePulses(
        porchMerged,
        timing,
        porchMergedSignal,
        hsyncRescueStepHz: 10.0);
    AssertEqual(3, rescued.Count);
    AssertEqual(104, rescued[1].Pulse.Start);
    AssertEqual(10, rescued[1].Pulse.Length);

    Pulse[] earlyFinalHSync =
    [
        new(0, 10),
        new(100, 10),
        new(150, 5),
        new(200, 5),
        new(250, 5),
        new(300, 20),
        new(450, 20),
        new(500, 20),
        new(550, 20),
        new(600, 20),
        new(650, 20),
        new(700, 20),
        new(750, 5),
        new(800, 5),
        new(850, 5),
        new(900, 10),
        new(1_000, 10)
    ];
    IReadOnlyList<ClassifiedSyncPulse> reprocessed = analyzer.RefinePulses(earlyFinalHSync, timing);
    AssertTrue(reprocessed.Any(pulse => pulse.Kind == SyncPulseKind.HSync && pulse.Pulse.Start == 900));
}

[Fact(DisplayName = "sync confidence calculator matches upstream line-difference rule")]
public void SyncConfidenceCalculatorMatchesUpstreamRule()
{
    AssertEqual(100, SyncConfidenceCalculator.Compute([0.0, 10.0, 20.0, 30.0], 4));
    AssertEqual(45, SyncConfidenceCalculator.Compute([0.0, 10.0, 25.0, 40.0], 4));
    AssertEqual(100, SyncConfidenceCalculator.Compute([0.0, 15.0, 25.0, 35.0], 4));
    AssertEqual(10, SyncConfidenceCalculator.Compute([0.0, 10.0, 25.0, 40.0], 4, initialConfidence: 10));
    double[] palLocations = Enumerable.Range(0, 316)
        .Select(line => (line * 100.0) + (line >= 312 ? 50.0 : 0.0))
        .ToArray();
    AssertEqual(100, SyncConfidenceCalculator.Compute(palLocations, 312));
    AssertEqual(45, SyncConfidenceCalculator.Compute(palLocations, 312, lineOffset: 2));
    AssertThrows<ArgumentOutOfRangeException>(() => SyncConfidenceCalculator.Compute(palLocations, 312, lineOffset: -1));
}

[Fact(DisplayName = "LD and CVBS line-zero sync confidence follows upstream anchor priority")]
public void LegacyLineZeroSyncConfidenceFollowsUpstreamAnchorPriority()
{
    AssertEqual(100, TbcFieldDecodePipeline.ResolveLegacyLine0SyncConfidence(
        hasStrongLocalEstimate: true,
        hasNextEstimate: true,
        hasPreviousEstimate: true,
        previousConfidence: 45));
    AssertEqual(90, TbcFieldDecodePipeline.ResolveLegacyLine0SyncConfidence(
        hasStrongLocalEstimate: true,
        hasNextEstimate: true,
        hasPreviousEstimate: false,
        previousConfidence: 100));
    AssertEqual(90, TbcFieldDecodePipeline.ResolveLegacyLine0SyncConfidence(
        hasStrongLocalEstimate: true,
        hasNextEstimate: false,
        hasPreviousEstimate: true,
        previousConfidence: 45));
    AssertEqual(35, TbcFieldDecodePipeline.ResolveLegacyLine0SyncConfidence(
        hasStrongLocalEstimate: false,
        hasNextEstimate: false,
        hasPreviousEstimate: true,
        previousConfidence: 45));
    AssertEqual(10, TbcFieldDecodePipeline.ResolveLegacyLine0SyncConfidence(
        hasStrongLocalEstimate: false,
        hasNextEstimate: false,
        hasPreviousEstimate: true,
        previousConfidence: 5));
    AssertEqual(50, TbcFieldDecodePipeline.ResolveLegacyLine0SyncConfidence(
        hasStrongLocalEstimate: false,
        hasNextEstimate: true,
        hasPreviousEstimate: false,
        previousConfidence: 100,
        nextConfidence: 50));
    AssertEqual(100, TbcFieldDecodePipeline.ResolveLegacyLine0SyncConfidence(
        hasStrongLocalEstimate: false,
        hasNextEstimate: false,
        hasPreviousEstimate: false,
        previousConfidence: 100));
}

[Fact(DisplayName = "LD line location repair matches upstream bad-line rules")]
public void LaserDiscLineLocationRepairMatchesUpstreamRules()
{
    var source = new LineLocationResult(
        [0.0, 100.0, 205.0, 305.0, 405.0, 505.0],
        new bool[6]);
    LineLocationResult marked = LaserDiscLineLocationRepair.MarkDerivativeErrors(source);
    bool[] expectedErrors = [false, true, true, true, false, false];
    AssertEqual(expectedErrors.Length, marked.Filled.Length);
    for (int line = 0; line < expectedErrors.Length; line++)
    {
        AssertEqual(expectedErrors[line], marked.Filled[line]);
    }

    var repairSource = new LineLocationResult(
        [0.0, 100.0, 200.0, 350.0, 500.0, 650.0],
        new bool[6]);
    LineLocationResult repaired = LaserDiscLineLocationRepair.FixBadLines(
        repairSource,
        system: "NTSC");
    AssertClose(700.0 / 3.0, repaired.Locations[2], 1e-12);
    AssertClose(1100.0 / 3.0, repaired.Locations[3], 1e-12);

    var noNtscLineZeroAnchor = new LineLocationResult(
        [0.0, 150.0, 300.0],
        [false, true, false]);
    LineLocationResult notRepaired = LaserDiscLineLocationRepair.FixBadLines(
        noNtscLineZeroAnchor,
        system: "NTSC");
    AssertClose(150.0, notRepaired.Locations[1], 1e-12);
}

[Fact(DisplayName = "CVBS line location repair preserves its overridden derivative mask")]
public void CvbsLineLocationRepairPreservesOverriddenDerivativeMask()
{
    var source = new LineLocationResult(
        [0.0, 100.0, 205.0, 360.0, 405.0, 505.0],
        [false, false, false, true, false, false]);

    LineLocationResult repaired = LaserDiscLineLocationRepair.FixBadLines(
        source,
        system: "PAL",
        markDerivativeErrors: false);

    bool[] expectedErrors = [false, false, false, true, false, false];
    for (int line = 0; line < expectedErrors.Length; line++)
    {
        AssertEqual(expectedErrors[line], repaired.Filled[line]);
    }

    AssertClose(305.0, repaired.Locations[3], 1e-12);
    AssertClose(205.0, repaired.Locations[2], 1e-12);
    AssertClose(405.0, repaired.Locations[4], 1e-12);
}

[Fact(DisplayName = "PAL CVBS retains Release 4.0 cubic interpolation lookahead")]
public void PalCvbsRetainsRelease40CubicInterpolationLookahead()
{
    AssertEqual(326, TbcFieldDecodePipeline.NonLaserDiscProcessedLineCount("cvbs", "PAL", 313));
    AssertEqual(323, TbcFieldDecodePipeline.NonLaserDiscProcessedLineCount("vhs", "PAL", 313));
    AssertEqual(273, TbcFieldDecodePipeline.NonLaserDiscProcessedLineCount("cvbs", "NTSC", 263));
}

[Fact(DisplayName = "LD line location builder repairs player skips from field end")]
public void LaserDiscLineLocationBuilderRepairsPlayerSkipsFromFieldEnd()
{
    ClassifiedSyncPulse[] pulses =
    [
        new(SyncPulseKind.HSync, new Pulse(1_000, 100), true),
        new(SyncPulseKind.HSync, new Pulse(11_035, 100), true),
        new(SyncPulseKind.HSync, new Pulse(27_200, 100), true)
    ];

    LaserDiscLineLocationBuildResult normal = LaserDiscLineLocationBuilder.Build(
        pulses,
        line0Location: 1_000.0,
        nextVBlankLocation: null,
        meanLineLength: 100.0,
        nominalLineLength: 100.0,
        fieldLineCount: 263,
        processedLines: 274,
        outputLineCount: 263);
    AssertFalse(normal.SkipDetected);
    AssertClose(11_035.0, normal.LineLocations.Locations[100], 1e-12);
    AssertFalse(normal.LineLocations.Filled[100]);

    LaserDiscLineLocationBuildResult skipped = LaserDiscLineLocationBuilder.Build(
        pulses,
        line0Location: 1_000.0,
        nextVBlankLocation: 26_350.0,
        meanLineLength: 100.0,
        nominalLineLength: 100.0,
        fieldLineCount: 263,
        processedLines: 274,
        outputLineCount: 263);
    AssertTrue(skipped.SkipDetected);
    AssertClose(11_035.0, skipped.LineLocations.Locations[110], 1e-12);
    AssertFalse(skipped.LineLocations.Filled[110]);
    AssertTrue(skipped.LineLocations.Filled[100]);
    AssertClose(10_122.727272727272, skipped.LineLocations.Locations[100], 1e-9);

    AssertThrows<InvalidOperationException>(() => LaserDiscLineLocationBuilder.Build(
        [new(SyncPulseKind.HSync, new Pulse(50, 10), true)],
        line0Location: 0.0,
        nextVBlankLocation: null,
        meanLineLength: 100.0,
        nominalLineLength: 100.0,
        fieldLineCount: 263,
        processedLines: 274,
        outputLineCount: 263));
}

[Fact(DisplayName = "LD player skip detector scores field ends and limits vblank")]
public void LaserDiscPlayerSkipDetectorScoresFieldEndsAndLimitsVBlank()
{
    var converter = new VideoOutputConverter(
        ire0: 1_000_000.0,
        hzIre: 10_000.0,
        outputZero: 1024,
        vsyncIre: -40.0,
        outputScale: 1.0);
    double[] lineLocations = Enumerable.Range(0, 13).Select(line => line * 100.0).ToArray();

    double[] twoVSyncLines = Enumerable.Repeat(converter.IreToHz(0.0), 1_300).ToArray();
    Array.Fill(twoVSyncLines, converter.IreToHz(-40.0), 300, 201);
    AssertEqual(100, LaserDiscPlayerSkipDetector.ScorePreviousFieldEnd(
        twoVSyncLines,
        lineLocations,
        outputLineCount: 3,
        lineOffset: 0,
        nominalLineLength: 100.0,
        converter));

    double[] oneVSyncLine = Enumerable.Repeat(converter.IreToHz(0.0), 1_300).ToArray();
    Array.Fill(oneVSyncLine, converter.IreToHz(-40.0), 300, 101);
    AssertEqual(50, LaserDiscPlayerSkipDetector.ScorePreviousFieldEnd(
        oneVSyncLine,
        lineLocations,
        outputLineCount: 3,
        lineOffset: 0,
        nominalLineLength: 100.0,
        converter));

    double[] mostlyBlank = Enumerable.Repeat(converter.IreToHz(100.0), 1_300).ToArray();
    Array.Fill(mostlyBlank, converter.IreToHz(0.0), 300, 501);
    AssertEqual(25, LaserDiscPlayerSkipDetector.ScorePreviousFieldEnd(
        mostlyBlank,
        lineLocations,
        outputLineCount: 3,
        lineOffset: 0,
        nominalLineLength: 100.0,
        converter));

    double[] splitBlank = Enumerable.Repeat(converter.IreToHz(100.0), 1_300).ToArray();
    Array.Fill(splitBlank, converter.IreToHz(0.0), 300, 401);
    AssertEqual(0, LaserDiscPlayerSkipDetector.ScorePreviousFieldEnd(
        splitBlank,
        lineLocations,
        outputLineCount: 3,
        lineOffset: 0,
        nominalLineLength: 100.0,
        converter));

    ClassifiedSyncPulse[] pulses = Enumerable.Range(0, 102)
        .Select(index => new ClassifiedSyncPulse(SyncPulseKind.HSync, new Pulse(index * 100, 10), true))
        .ToArray();
    AssertTrue(LaserDiscPlayerSkipDetector.IsFirstVBlankWithinPulseLimit(pulses, pulses[100].Pulse.Start, 50));
    AssertFalse(LaserDiscPlayerSkipDetector.IsFirstVBlankWithinPulseLimit(pulses, pulses[101].Pulse.Start, 50));
    AssertTrue(LaserDiscPlayerSkipDetector.IsFirstVBlankWithinPulseLimit(pulses, pulses[101].Pulse.Start, 25));
}

[Fact(DisplayName = "field parity detector uses vsync boundary gaps")]
public void FieldParityDetectorUsesVsyncBoundaryGaps()
{
    ClassifiedSyncPulse[] palFirst =
    [
        new(SyncPulseKind.Equalizing, new Pulse(0, 5), true),
        new(SyncPulseKind.VSync, new Pulse(50, 20), true),
        new(SyncPulseKind.VSync, new Pulse(100, 20), true),
        new(SyncPulseKind.Equalizing, new Pulse(150, 5), true)
    ];
    FieldParityDetection palFirstResult = FieldParityDetector.Detect(palFirst, meanLineLength: 100.0, "PAL")!;
    AssertTrue(palFirstResult.IsFirstField);
    AssertEqual("vsync-boundary-gaps", palFirstResult.Method);
    AssertTrue(palFirstResult.Confidence > 80);

    ClassifiedSyncPulse[] palSecond =
    [
        new(SyncPulseKind.Equalizing, new Pulse(0, 5), true),
        new(SyncPulseKind.VSync, new Pulse(100, 20), true),
        new(SyncPulseKind.VSync, new Pulse(200, 20), true),
        new(SyncPulseKind.Equalizing, new Pulse(300, 5), true)
    ];
    FieldParityDetection palSecondResult = FieldParityDetector.Detect(palSecond, meanLineLength: 100.0, "PAL")!;
    AssertFalse(palSecondResult.IsFirstField);

    FieldParityDetection ntscFirstResult = FieldParityDetector.Detect(palSecond, meanLineLength: 100.0, "NTSC")!;
    AssertTrue(ntscFirstResult.IsFirstField);

    ClassifiedSyncPulse[] ambiguous =
    [
        new(SyncPulseKind.Equalizing, new Pulse(0, 5), true),
        new(SyncPulseKind.VSync, new Pulse(75, 20), true),
        new(SyncPulseKind.Equalizing, new Pulse(150, 5), true)
    ];
    AssertEqual<FieldParityDetection?>(null, FieldParityDetector.Detect(ambiguous, meanLineLength: 100.0, "PAL"));
}

[Fact(DisplayName = "field parity detector uses vblank boundary consensus")]
public void FieldParityDetectorUsesVBlankBoundaryConsensus()
{
    ClassifiedSyncPulse[] palFirst =
    [
        Sync(SyncPulseKind.HSync, 0),
        Sync(SyncPulseKind.Equalizing, 50),
        Sync(SyncPulseKind.VSync, 200),
        Sync(SyncPulseKind.Equalizing, 350),
        Sync(SyncPulseKind.HSync, 400),
        Sync(SyncPulseKind.HSync, 500),
        Sync(SyncPulseKind.Equalizing, 600),
        Sync(SyncPulseKind.VSync, 750),
        Sync(SyncPulseKind.Equalizing, 900),
        Sync(SyncPulseKind.HSync, 1000)
    ];
    FieldParityDetection palFirstResult = FieldParityDetector.Detect(
        palFirst,
        meanLineLength: 100.0,
        "PAL",
        minimumConfidence: 100,
        fieldLines: [4, 5])!;
    AssertTrue(palFirstResult.IsFirstField);
    AssertEqual("vblank-boundary-consensus", palFirstResult.Method);
    AssertEqual(100, palFirstResult.Confidence);

    ClassifiedSyncPulse[] palSecond =
    [
        Sync(SyncPulseKind.HSync, 0),
        Sync(SyncPulseKind.Equalizing, 100),
        Sync(SyncPulseKind.VSync, 200),
        Sync(SyncPulseKind.Equalizing, 300),
        Sync(SyncPulseKind.HSync, 400),
        Sync(SyncPulseKind.HSync, 500),
        Sync(SyncPulseKind.Equalizing, 550),
        Sync(SyncPulseKind.VSync, 700),
        Sync(SyncPulseKind.Equalizing, 850),
        Sync(SyncPulseKind.HSync, 900)
    ];
    FieldParityDetection palSecondResult = FieldParityDetector.Detect(
        palSecond,
        meanLineLength: 100.0,
        "PAL",
        minimumConfidence: 100,
        fieldLines: [4, 5])!;
    AssertFalse(palSecondResult.IsFirstField);
    AssertEqual(100, palSecondResult.Confidence);
}

[Fact(DisplayName = "field parity detector honors confidence threshold")]
public void FieldParityDetectorHonorsConfidenceThreshold()
{
    ClassifiedSyncPulse[] slightlyNoisyPalFirst =
    [
        new(SyncPulseKind.Equalizing, new Pulse(0, 5), true),
        new(SyncPulseKind.VSync, new Pulse(55, 20), true),
        new(SyncPulseKind.VSync, new Pulse(105, 20), true),
        new(SyncPulseKind.Equalizing, new Pulse(155, 5), true)
    ];

    FieldParityDetection relaxed = FieldParityDetector.Detect(
        slightlyNoisyPalFirst,
        meanLineLength: 100.0,
        "PAL",
        minimumConfidence: 95)!;
    AssertTrue(relaxed.IsFirstField);
    AssertEqual(95, relaxed.Confidence);

    AssertEqual<FieldParityDetection?>(null, FieldParityDetector.Detect(
        slightlyNoisyPalFirst,
        meanLineLength: 100.0,
        "PAL",
        minimumConfidence: 96));
    AssertEqual<FieldParityDetection?>(null, FieldParityDetector.Detect(
        slightlyNoisyPalFirst,
        meanLineLength: 100.0,
        "PAL",
        minimumConfidence: 101));
    AssertTrue(FieldParityDetector.Detect(
        slightlyNoisyPalFirst,
        meanLineLength: 100.0,
        "PAL",
        minimumConfidence: -1) is not null);
}

[Fact(DisplayName = "field parity resolver matches v0.4.0 cadence priorities")]
public void FieldParityResolverMatchesV04CadencePriorities()
{
    ClassifiedSyncPulse[] fullPalFirst =
    [
        Sync(SyncPulseKind.HSync, 0),
        Sync(SyncPulseKind.Equalizing, 50),
        Sync(SyncPulseKind.VSync, 200),
        Sync(SyncPulseKind.EqualizingSecond, 350),
        Sync(SyncPulseKind.HSync, 400),
        Sync(SyncPulseKind.HSync, 500),
        Sync(SyncPulseKind.Equalizing, 600),
        Sync(SyncPulseKind.VSync, 750),
        Sync(SyncPulseKind.EqualizingSecond, 900),
        Sync(SyncPulseKind.HSync, 1000)
    ];

    FieldParityDetection retainedCadence = FieldParityDetector.ResolveCadence(
        fullPalFirst,
        100.0,
        "PAL",
        [4, 5],
        previousFirstField: true,
        minimumConfidence: 125,
        hasPreviousHSync: true,
        fallbackFirstField: true,
        fallbackConfidence: 80);
    AssertFalse(retainedCadence.IsFirstField);
    AssertEqual("previous-cadence", retainedCadence.Method);

    FieldParityDetection strongFallback = FieldParityDetector.ResolveCadence(
        fullPalFirst,
        100.0,
        "PAL",
        [4, 5],
        previousFirstField: true,
        minimumConfidence: 125,
        hasPreviousHSync: true,
        fallbackFirstField: true,
        fallbackConfidence: 101);
    AssertTrue(strongFallback.IsFirstField);
    AssertEqual("fallback-vsync", strongFallback.Method);

    FieldParityDetection firstFieldThresholdReduction = FieldParityDetector.ResolveCadence(
        fullPalFirst,
        100.0,
        "PAL",
        [4, 5],
        previousFirstField: null,
        minimumConfidence: 125,
        hasPreviousHSync: false);
    AssertTrue(firstFieldThresholdReduction.IsFirstField);
    AssertEqual(100, firstFieldThresholdReduction.Confidence);

    FieldParityDetection fallbackLinePreventsThresholdReduction = FieldParityDetector.ResolveCadence(
        fullPalFirst,
        100.0,
        "PAL",
        [4, 5],
        previousFirstField: true,
        minimumConfidence: 125,
        hasPreviousHSync: false,
        hasFallbackLine: true);
    AssertFalse(fallbackLinePreventsThresholdReduction.IsFirstField);
    AssertEqual("previous-cadence", fallbackLinePreventsThresholdReduction.Method);

    ClassifiedSyncPulse[] tiedBoundaries =
    [
        Sync(SyncPulseKind.HSync, 0),
        Sync(SyncPulseKind.Equalizing, 50),
        Sync(SyncPulseKind.VSync, 175),
        Sync(SyncPulseKind.EqualizingSecond, 300),
        Sync(SyncPulseKind.HSync, 400)
    ];
    FieldParityDetection initialTie = FieldParityDetector.ResolveCadence(
        tiedBoundaries,
        100.0,
        "PAL",
        [4, 5],
        previousFirstField: null,
        minimumConfidence: 100,
        hasPreviousHSync: true);
    AssertTrue(initialTie.IsFirstField);
    AssertEqual("initial-cadence", initialTie.Method);

    FieldParityDetection negativeOneFallback = FieldParityDetector.ResolveCadence(
        tiedBoundaries,
        100.0,
        "PAL",
        [4, 5],
        previousFirstField: false,
        minimumConfidence: 100,
        hasPreviousHSync: true,
        hasFallbackLine: true,
        fallbackFirstField: null,
        fallbackConfidence: 101);
    AssertFalse(negativeOneFallback.IsFirstField);
    AssertEqual("fallback-vsync", negativeOneFallback.Method);

    ClassifiedSyncPulse[] quarterLineBoundary =
    [
        Sync(SyncPulseKind.HSync, 0),
        Sync(SyncPulseKind.Equalizing, 25),
        Sync(SyncPulseKind.VSync, 175),
        Sync(SyncPulseKind.EqualizingSecond, 350),
        Sync(SyncPulseKind.HSync, 400)
    ];
    FieldParityDetection quarterRoundedAway = FieldParityDetector.ResolveCadence(
        quarterLineBoundary,
        100.0,
        "PAL",
        [4, 5],
        previousFirstField: true,
        minimumConfidence: 50,
        hasPreviousHSync: true);
    AssertTrue(quarterRoundedAway.IsFirstField);
    AssertEqual(50, quarterRoundedAway.Confidence);
}

[Fact(DisplayName = "TBC line resampler extracts fixed-width lines")]
public void TbcLineResamplerExtractsFixedWidthLines()
{
    double[] source = Enumerable.Range(0, 64).Select(value => (double)value).ToArray();
    double[] lineLocations = [10.0, 30.0, 50.0];
    var resampler = new TbcLineResampler(outputLineLength: 4);

    AssertSequence([10.0, 15.0, 20.0, 25.0], resampler.ResampleLine(source, lineLocations, line: 0));
    AssertSequence(
        [10.0, 15.0, 20.0, 25.0, 30.0, 35.0, 40.0, 45.0],
        resampler.ResampleLines(source, lineLocations, firstLine: 0, lineCount: 2));

    double[] halfSampleRamp = resampler.ResampleLine(source, [20.5, 24.5], line: 0);
    AssertClose(20.5, halfSampleRamp[0], 1e-7);
    double[] halfSampleImpulse = new double[64];
    halfSampleImpulse[20] = 1.0;
    float halfSample = (float)resampler.ResampleLine(halfSampleImpulse, [20.5, 36.5], line: 0)[0];
    AssertEqual(unchecked((int)0x3f21c07b), BitConverter.SingleToInt32Bits(halfSample));

    double[] constantEdgeSource = Enumerable.Repeat(7.0, 64).ToArray();
    AssertSequence(
        [(float)7.0, (float)7.0, (float)7.0, (float)7.0],
        resampler.ResampleLine(constantEdgeSource, [-10.0, 10.0], line: 0));
    AssertSequence(
        [(float)7.0, (float)7.0, (float)7.0, (float)7.0],
        resampler.ResampleLine(constantEdgeSource, [60.0, 80.0], line: 0));

    double[] identitySource = Enumerable.Range(0, 80).Select(value => (double)value).ToArray();
    double[] curvedLocations = [20.0, 24.0, 30.0, 38.0];
    var quadratic = new TbcLineResampler(2, TbcLineInterpolationMethod.Quadratic);
    var cubic = new TbcLineResampler(2, TbcLineInterpolationMethod.Cubic);
    double[] quadraticLine = quadratic.ResampleLine(identitySource, curvedLocations, line: 1);
    AssertClose((float)20.0, quadraticLine[0], 1e-7);
    AssertClose((float)26.74681398808025, quadraticLine[1], 1e-7);
    double[] cubicLine = cubic.ResampleLine(identitySource, curvedLocations, line: 1);
    AssertClose((float)19.2, cubicLine[0], 1e-7);
    AssertClose((float)26.697085807449184, cubicLine[1], 1e-7);

    double[] scipyQuadraticLocations = [10.0, 20.0, 35.0, 50.0, 80.0];
    var scipyQuadratic = new TbcLineResampler(
        outputLineLength: 4,
        TbcLineInterpolationMethod.Quadratic,
        nominalInputLineLength: 10.0);
    double[] quadraticWow = scipyQuadratic.ResampleLines(
        Enumerable.Repeat(1.0, 128).ToArray(),
        scipyQuadraticLocations,
        firstLine: 1,
        lineCount: 1);
    AssertClose(13.142857142857142 / 10.0, quadraticWow[0], 2e-7);
    AssertClose(14.714285714285714 / 10.0, quadraticWow[1], 2e-7);
    AssertClose(16.285714285714285 / 10.0, quadraticWow[2], 2e-7);
    AssertClose(15.285714285714286 / 10.0, quadraticWow[3], 2e-7);
    AssertIntSequence(
        [0x3fa83a84, 0x3fbc57c5, 0x3fd07508, 0x3fc3a83b],
        quadraticWow.Select(value => BitConverter.SingleToInt32Bits((float)value)).ToArray());
    var scipyCubic = new TbcLineResampler(
        outputLineLength: 4,
        TbcLineInterpolationMethod.Cubic,
        nominalInputLineLength: 10.0);
    double[] cubicWow = scipyCubic.ResampleLines(
        Enumerable.Repeat(1.0, 128).ToArray(),
        scipyQuadraticLocations,
        firstLine: 1,
        lineCount: 1);
    AssertIntSequence(
        [0x3fa92492, 0x3fc0b6db, 0x3fc9b6db, 0x3fc42493],
        cubicWow.Select(value => BitConverter.SingleToInt32Bits((float)value)).ToArray());
    double[] quadraticRamp = scipyQuadratic.ResampleLines(
        Enumerable.Range(0, 128).Select(value => (double)value).ToArray(),
        scipyQuadraticLocations,
        firstLine: 1,
        lineCount: 1);
    AssertClose(26.285715, quadraticRamp[0], 1e-5);
    AssertClose(34.55276, quadraticRamp[1], 1e-5);
    AssertClose(44.556835, quadraticRamp[2], 1e-5);
    AssertClose(47.85416, quadraticRamp[3], 1e-5);

    var negativeWowQuadratic = new TbcLineResampler(
        outputLineLength: 8,
        TbcLineInterpolationMethod.Quadratic,
        nominalInputLineLength: 1.0);
    double[] negativeWow = negativeWowQuadratic.ResampleLines(
        Enumerable.Repeat(1.0, 256).ToArray(),
        [20.0, 120.0, 121.0, 122.0],
        firstLine: 1,
        lineCount: 1);
    AssertClose(42.25, negativeWow[0], 1e-5);
    AssertClose(13.375, negativeWow[2], 1e-5);
    AssertClose(-1.0625, negativeWow[3], 1e-5);
    AssertClose(-15.5, negativeWow[4], 1e-5);
    AssertClose(-9.3125, negativeWow[7], 1e-5);

    double[] constantSource = Enumerable.Repeat(1.0, 80).ToArray();
    var smoothed = new TbcLineResampler(2, TbcLineInterpolationMethod.Linear, wowLevelAdjustSmoothing: 1.0);
    AssertSequence([(float)(2.0 / 3.0), (float)(2.0 / 3.0), (float)(5.0 / 6.0), (float)(11.0 / 12.0)], smoothed.ResampleLines(
        constantSource,
        curvedLocations,
        firstLine: 0,
        lineCount: 2));

    var upstreamPrefixSmoothing = new TbcLineResampler(
        outputLineLength: 4,
        TbcLineInterpolationMethod.Linear,
        wowLevelAdjustSmoothing: 1.0,
        nominalInputLineLength: 10.0);
    AssertSequence(
        [(float)1.125, (float)1.21875, (float)1.2890625, (float)1.341796875],
        upstreamPrefixSmoothing.ResampleLines(
            constantSource,
            [10.0, 20.0, 35.0],
            firstLine: 1,
            lineCount: 1));

    var numbaFastMathSmoothing = new TbcLineResampler(
        outputLineLength: 910,
        TbcLineInterpolationMethod.Linear,
        wowLevelAdjustSmoothing: 262.5,
        nominalInputLineLength: 1138.0);
    var fastMathLevels = (double[])InvokePrivateMethod(
        numbaFastMathSmoothing,
        "BuildLevelAdjusts",
        new double[] { 0.99995, 0.999987 })!;
    AssertEqual(0x3FEFFF9724899D5DUL, BitConverter.DoubleToUInt64Bits(fastMathLevels[1]));

    Range secondLine = TbcLineResampler.GetOutputLineRange(oneBasedLine: 2, outputLineLength: 4);
    AssertEqual(4, secondLine.Start.Value);
    AssertEqual(8, secondLine.End.Value);

    using DecodeSession splineSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
        "--wow_interpolation_method",
        "quadratic",
        "input.s16",
        "output"
    ]));
    double[] lookaheadLocations = Enumerable.Range(0, 273).Select(value => value * 1138.0).ToArray();
    var lookaheadResult = new LineLocationResult(lookaheadLocations, new bool[lookaheadLocations.Length]);
    double[] retainedLookahead = (double[])InvokePrivateMethod(
        splineSession.TbcFieldDecoder,
        "RenderLineLocations",
        lookaheadResult,
        1)!;
    AssertEqual(273, retainedLookahead.Length);
    AssertEqual(lookaheadLocations[^1], retainedLookahead[^1]);

    AssertThrows<ArgumentOutOfRangeException>(() => resampler.ResampleLine(source, lineLocations, line: 2));
    AssertThrows<ArgumentException>(() => resampler.ResampleLine(source, [10.0, 10.0], line: 0));
}

[Fact(DisplayName = "video output converter maps Hz to TBC samples")]
public void VideoOutputConverterMapsHzToTbcSamples()
{
    var converter = new VideoOutputConverter(
        ire0: 1000.0,
        hzIre: 10.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 10.0);

    AssertEqual((ushort)256, converter.ConvertHz(converter.IreToHz(-40.0)));
    AssertEqual((ushort)656, converter.ConvertHz(converter.IreToHz(0.0)));
    AssertEqual((ushort)1656, converter.ConvertHz(converter.IreToHz(100.0)));
    AssertClose(0.0, converter.HzToIre(converter.IreToHz(0.0)), 1e-12);
    AssertClose(0.0, converter.OutputToIre(656), 1e-12);
    AssertEqual(ushort.MaxValue, converter.ConvertHz(1_000_000_000.0));
    AssertEqual((ushort)0, converter.ConvertHz(-1_000_000_000.0));

    ushort[] converted = converter.ConvertHz([converter.IreToHz(-40.0), converter.IreToHz(0.0)]);
    AssertEqual((ushort)256, converted[0]);
    AssertEqual((ushort)656, converted[1]);

    FormatParameterSet palVhs = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    VideoOutputConverter palConverter = VideoOutputConverter.FromParameters(palVhs);
    double vsyncIre = JsonDouble(palVhs.SysParams, "vsync_ire");
    AssertClose(VideoOutputConverter.ComputeOutputScale(isPal: true, vsyncIre), palConverter.OutputScale, 1e-12);
    ushort expectedBlank = (ushort)(JsonInt(palVhs.SysParams, "outputZero") - (vsyncIre * palConverter.OutputScale) + 0.5);
    AssertEqual(expectedBlank, palConverter.ConvertHz(palConverter.IreToHz(0.0)));
}

[Fact(DisplayName = "video output array conversion matches Numba fastmath boundaries")]
public void VideoOutputArrayConversionMatchesNumbaFastMathBoundaries()
{
    var converter = new VideoOutputConverter(
        ire0: 7_100_000.0,
        hzIre: 8_000.0,
        outputZero: 256,
        vsyncIre: -42.857142857142854,
        outputScale: 376.32);

    ushort[] converted = converter.ConvertHz(
        [6_760_937.5, 6_789_062.5, 6_754_687.5, 6_767_187.5]);
    AssertEqual((ushort)434, converted[0]);
    AssertEqual((ushort)1_757, converted[1]);
    AssertEqual((ushort)140, converted[2]);
    AssertEqual((ushort)728, converted[3]);
}

[Fact(DisplayName = "LD sync pulse search uses the upstream minus 20 IRE threshold")]
public void LaserDiscSyncPulseSearchUsesMinusTwentyIreThreshold()
{
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 40_000_000.0,
        linePeriodUs: 64.0,
        hsyncPulseUs: 4.7,
        equalizingPulseUs: 2.35,
        vsyncPulseUs: 27.3);
    var frameSpec = new TbcFrameSpec(
        "PAL",
        OutputLineLength: 4,
        OutputLineCount: 2,
        OutputSampleRateHz: 17_734_475.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var converter = new VideoOutputConverter(
        ire0: 7_100_000.0,
        hzIre: 8_000.0,
        outputZero: 256,
        vsyncIre: -42.857142857142854,
        outputScale: 376.32);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(frameSpec, converter),
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: SyncDetectionOptions.Disabled,
        decodeType: "ld");
    double[] samples = [7_100_000.0];
    object prepared = InvokePrivateMethod(
        pipeline,
        "PrepareSyncSpan",
        new RfDecodedSpan(0, samples, samples, samples, VideoLowPass: samples),
        null,
        true,
        true)!;

    AssertClose(6_940_000.0, Convert.ToDouble(PrivatePropertyValue(prepared, "Threshold")), 0.0);
}

[Fact(DisplayName = "TBC field renderer emits upstream-shaped little-endian fields")]
public void TbcFieldRendererEmitsUpstreamShapedFields()
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
    var renderer = new TbcFieldRenderer(spec, converter);

    double[] videoHz = Enumerable.Range(0, 64).Select(value => (double)value).ToArray();
    double[] lineLocations = [0.0, 20.0, 40.0];
    double[] rawField = renderer.ResampleField(videoHz, lineLocations);
    ushort[] field = renderer.RenderField(videoHz, lineLocations);

    AssertSequence([0.0, 5.0, 10.0, 15.0, 20.0, 25.0, 30.0, 35.0], rawField);
    AssertEqual(8, field.Length);
    AssertEqual((ushort)656, field[0]);
    AssertEqual((ushort)706, field[1]);
    AssertEqual((ushort)856, field[4]);
    AssertEqual((ushort)1006, field[7]);

    byte[] bytes = TbcOutputWriter.ToLittleEndianBytes(field);
    AssertEqual(16, bytes.Length);
    AssertEqual((byte)0x90, bytes[0]);
    AssertEqual((byte)0x02, bytes[1]);

    using var output = new MemoryStream();
    TbcOutputWriter.WriteFrame(output, field, spec);
    AssertEqual(16L, output.Length);
    AssertThrows<ArgumentException>(() => TbcOutputWriter.WriteFrame(Stream.Null, [1, 2, 3], spec));
    AssertThrows<ArgumentException>(() => renderer.ResampleField(videoHz, lineLocations, lineCount: 1));
    AssertThrows<ArgumentException>(() => renderer.RenderField(videoHz, lineLocations, lineCount: 1));
}

[Fact(DisplayName = "TBC field renderer emits raw float payloads")]
public void TbcFieldRendererEmitsRawFloatPayloads()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 3,
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
    var renderer = new TbcFieldRenderer(spec, converter, exportRawTbc: true);

    TbcRenderedField rendered = renderer.RenderFieldPayload([1.0, 2.0, 3.0, 4.0, 5.0, 6.0], [0.0, 3.0, 6.0]);

    AssertTrue(rendered.OutputPayload is not null);
    AssertEqual(TbcOutputSampleFormat.Float32, rendered.OutputPayload!.SampleFormat);
    AssertEqual(24, rendered.OutputPayload.Bytes.Length);
    AssertSequence([1.0, 2.0, 3.0, 4.0, 5.0, 6.0], ReadFloat32Samples(rendered.OutputPayload.Bytes));
    AssertSequence(ReadFloat32Samples(rendered.OutputPayload.Bytes), ReadFloat32Samples(renderer.RenderFieldBytes(
        [1.0, 2.0, 3.0, 4.0, 5.0, 6.0],
        [0.0, 3.0, 6.0])));
    AssertEqual((ushort)666, rendered.Samples[0]);
}

[Fact(DisplayName = "TBC field renderer snapshots a dynamic converter after resampling")]
public void TbcFieldRendererSnapshotsDynamicConverterAfterResampling()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 3,
        OutputLineCount: 2,
        OutputSampleRateHz: 14_318_180.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var fallback = new VideoOutputConverter(0.0, 1.0, 256, -40.0, 10.0);
    var selected = new VideoOutputConverter(5.0, 2.0, 256, -40.0, 10.0);
    var renderer = new TbcFieldRenderer(spec, fallback);
    double[] videoHz = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0];
    int providerCalls = 0;

    TbcRenderedField rendered = renderer.RenderFieldPayloadWithConverterProvider(
        videoHz,
        [0.0, 3.0, 6.0],
        firstLine: 0,
        lineCount: null,
        fieldNumber: 0,
        converterFallback: fallback,
        converterProvider: () =>
        {
            providerCalls++;
            Array.Fill(videoHz, 1_000.0);
            return selected;
        });

    AssertEqual(1, providerCalls);
    AssertEqual(selected.ConvertHz(10.0), rendered.Samples[0]);
    AssertTrue(rendered.Samples[0] != selected.ConvertHz(1_000.0));
    AssertTrue(ReferenceEquals(selected, rendered.OutputConverter));
}

[Fact(DisplayName = "TBC field renderer applies y-comb")]
public void TbcFieldRendererAppliesYComb()
{
    double[] data = [0.0, 0.0, 10.0, 10.0, 0.0, 0.0];
    TbcFieldRenderer.ApplyYCombInPlace(data, lineLength: 2, limit: 4.0);
    AssertSequence([2.0, 2.0, 8.0, 8.0, 2.0, 2.0], data);

    double[] float32Data = [1_000_000.1, -2.3, 1_000_000.7, -2.8, 999_999.2, -1.1];
    TbcFieldRenderer.ApplyYCombInPlace(float32Data, lineLength: 2, limit: 0.37);
    AssertIntSequence(
        [
            0x497423ff,
            unchecked((int)0xc0075c29),
            0x49742408,
            unchecked((int)0xc0275c29),
            0x497423f6,
            unchecked((int)0xbfa47ae2)
        ],
        float32Data.Select(value => BitConverter.SingleToInt32Bits((float)value)).ToArray());

    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 2,
        OutputLineCount: 3,
        OutputSampleRateHz: 14_318_180.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var converter = new VideoOutputConverter(
        ire0: 0.0,
        hzIre: 1.0,
        outputZero: 0,
        vsyncIre: 0.0,
        outputScale: 1.0);
    var renderer = new TbcFieldRenderer(spec, converter, yCombLimitHz: 4.0);
    ushort[] samples = renderer.RenderField([0.0, 0.0, 10.0, 10.0, 0.0, 0.0], [0.0, 2.0, 4.0, 6.0]);
    AssertIntSequence([2, 2, 8, 8, 2, 2], samples.Select(value => (int)value).ToArray());

    AssertThrows<ArgumentException>(() => TbcFieldRenderer.ApplyYCombInPlace([1.0, 2.0, 3.0], lineLength: 2, limit: 1.0));
    AssertThrows<ArgumentOutOfRangeException>(() => TbcFieldRenderer.ApplyYCombInPlace([1.0, 2.0], lineLength: 2, limit: -1.0));
}

[Fact(DisplayName = "TBC field renderer applies ire0 adjust")]
public void TbcFieldRendererAppliesIre0Adjust()
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

    double[] video = Enumerable.Repeat(230.0, 600).ToArray();
    for (int line = 0; line < 3; line++)
    {
        int lineBase = line * 200;
        for (int i = lineBase + 4; i < lineBase + 70; i++)
        {
            video[i] = 90.0;
        }

        for (int i = lineBase + 78; i < lineBase + 120; i++)
        {
            video[i] = 130.0;
        }
    }

    double[] lineLocations = [0.0, 200.0, 400.0, 600.0];
    var adjustedRenderer = new TbcFieldRenderer(
        spec,
        converter,
        ire0Adjust: new Ire0AdjustOptions(BackPorch: true, HSync: true, BackPorchStart: 74, BackPorchEnd: 124));
    var plainRenderer = new TbcFieldRenderer(spec, converter);

    ushort[] adjusted = adjustedRenderer.RenderField(video, lineLocations);
    ushort[] plain = plainRenderer.RenderField(video, lineLocations);

    AssertEqual((ushort)0, adjusted[10]);
    AssertEqual((ushort)40, adjusted[90]);
    AssertEqual((ushort)140, adjusted[150]);
    AssertTrue(adjusted[150] > plain[150] * 2);

    var backPorchOnly = new TbcFieldRenderer(
        spec,
        converter,
        ire0Adjust: new Ire0AdjustOptions(BackPorch: true, HSync: false, BackPorchStart: 74, BackPorchEnd: 124));
    ushort[] backPorchAdjusted = backPorchOnly.RenderField(video, lineLocations);
    AssertEqual((ushort)40, backPorchAdjusted[90]);
    AssertTrue(backPorchAdjusted[150] < adjusted[150]);

    var precisionSpec = spec with { OutputLineCount = 263 };
    var precisionRenderer = new TbcFieldRenderer(
        precisionSpec,
        converter,
        ire0Adjust: new Ire0AdjustOptions(BackPorch: true, HSync: false, BackPorchStart: 74, BackPorchEnd: 124));
    var precisionField = new double[precisionSpec.FieldSampleCount];
    for (int line = 0; line < precisionSpec.OutputLineCount; line++)
    {
        float level = BitConverter.Int32BitsToSingle(unchecked((int)(0x49700000u + (uint)line)));
        Array.Fill(precisionField, (double)level, (line * precisionSpec.OutputLineLength) + 78, 42);
    }

    var measuredConverter = (VideoOutputConverter)InvokePrivateMethod(
        precisionRenderer,
        "BuildFieldConverter",
        precisionField,
        0,
        null,
        null)!;
    AssertEqual(0x49700083, BitConverter.SingleToInt32Bits((float)measuredConverter.Ire0));

    var hsyncPrecisionConverter = new VideoOutputConverter(
        ire0: 100.1,
        hzIre: 10.0,
        outputZero: 0,
        vsyncIre: -40.0,
        outputScale: 1.0);
    var hsyncPrecisionRenderer = new TbcFieldRenderer(
        precisionSpec,
        hsyncPrecisionConverter,
        ire0Adjust: new Ire0AdjustOptions(BackPorch: false, HSync: true, BackPorchStart: 74, BackPorchEnd: 124));
    var hsyncPrecisionField = new double[precisionSpec.FieldSampleCount];
    for (int line = 0; line < precisionSpec.OutputLineCount; line++)
    {
        Array.Fill(
            hsyncPrecisionField,
            (double)(float)13.37,
            (line * precisionSpec.OutputLineLength) + 4,
            66);
    }

    var hsyncMeasuredConverter = (VideoOutputConverter)InvokePrivateMethod(
        hsyncPrecisionRenderer,
        "BuildFieldConverter",
        hsyncPrecisionField,
        0,
        null,
        null)!;
    AssertEqual(
        (double)(float)(((float)hsyncPrecisionConverter.Ire0 - (float)13.37) / 40.0f),
        hsyncMeasuredConverter.HzIre);
}

[Fact(DisplayName = "TBC field renderer applies track phase offsets")]
public void TbcFieldRendererAppliesTrackPhaseOffsets()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 2,
        OutputLineCount: 2,
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
    double[] video = [200.0, 200.0, 200.0, 200.0];
    double[] lineLocations = [0.0, 2.0, 4.0];

    var phase0 = new TbcFieldRenderer(
        spec,
        converter,
        trackPhaseIre0Offset: new TrackPhaseIre0OffsetOptions(TrackPhase: 0, Offset0Hz: 20.0, Offset1Hz: 0.0));
    var phase1 = new TbcFieldRenderer(
        spec,
        converter,
        trackPhaseIre0Offset: new TrackPhaseIre0OffsetOptions(TrackPhase: 1, Offset0Hz: 20.0, Offset1Hz: 0.0));

    AssertEqual((ushort)48, phase0.RenderField(video, lineLocations, fieldNumber: 0)[0]);
    AssertEqual((ushort)50, phase0.RenderField(video, lineLocations, fieldNumber: 1)[0]);
    AssertEqual((ushort)50, phase1.RenderField(video, lineLocations, fieldNumber: 0)[0]);
    AssertEqual((ushort)48, phase1.RenderField(video, lineLocations, fieldNumber: 1)[0]);
    AssertEqual((ushort)50, phase0.RenderField(video, lineLocations, fieldNumber: 0, trackPhaseOverride: 1)[0]);
    AssertEqual((ushort)48, phase1.RenderField(video, lineLocations, fieldNumber: 0, trackPhaseOverride: 0)[0]);
}

[Fact(DisplayName = "TBC field renderer applies CVBS clamp AGC")]
public void TbcFieldRendererAppliesCvbsClampAgc()
{
    const int lineLength = 200;
    const int lineCount = 20;
    var spec = new TbcFrameSpec(
        "NTSC",
        lineLength,
        lineCount,
        OutputSampleRateHz: 14_318_180.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var converter = new VideoOutputConverter(
        ire0: 100.0,
        hzIre: 1.0,
        outputZero: 0,
        vsyncIre: -40.0,
        outputScale: 1.0);
    double[] video = Enumerable.Repeat(200.0, lineLength * lineCount).ToArray();
    for (int line = 0; line < lineCount; line++)
    {
        int lineBase = line * lineLength;
        for (int i = lineBase + 12; i < lineBase + 72; i++)
        {
            video[i] = 60.0;
        }

        for (int i = lineBase + 96; i < lineBase + 164; i++)
        {
            video[i] = 100.0;
        }
    }

    double[] lineLocations = Enumerable.Range(0, lineCount + 1).Select(line => line * (double)lineLength).ToArray();
    var renderer = new TbcFieldRenderer(
        spec,
        converter,
        cvbsClampAgc: new CvbsClampAgcOptions(Speed: 1.0, GainFactor: 1.0, SetGain: 0.0));
    ushort[] samples = renderer.RenderField(video, lineLocations);

    AssertEqual((ushort)0, samples[20]);
    AssertEqual((ushort)40, samples[120]);
    AssertEqual((ushort)140, samples[180]);
    AssertTrue(renderer.CvbsAgcStatistics is not null);
    AssertClose(1.0, renderer.CvbsAgcStatistics!.LowestDetectedGain, 1e-12);
    double[] higherGainVideo = video.ToArray();
    for (int line = 0; line < lineCount; line++)
    {
        int lineBase = line * lineLength;
        Array.Fill(higherGainVideo, 20.0, lineBase + 12, 60);
    }

    renderer.RenderField(higherGainVideo, lineLocations);
    AssertClose(1.0, renderer.CvbsAgcStatistics.LowestDetectedGain, 1e-12);
    AssertClose(2.0, renderer.CvbsAgcStatistics.HighestDetectedGain, 1e-12);
    AssertClose(1.0, renderer.CvbsAgcStatistics.LowestUsedGain, 1e-12);
    AssertClose(2.0, renderer.CvbsAgcStatistics.HighestUsedGain, 1e-12);

    var setGainRenderer = new TbcFieldRenderer(
        spec,
        converter,
        cvbsClampAgc: new CvbsClampAgcOptions(Speed: 1.0, GainFactor: 1.0, SetGain: 2.0));
    ushort[] setGainSamples = setGainRenderer.RenderField(video, lineLocations);
    AssertEqual((ushort)20, setGainSamples[20]);
    AssertEqual((ushort)40, setGainSamples[120]);
    AssertEqual((ushort)90, setGainSamples[180]);
    AssertEqual<CvbsAgcStatistics?>(null, setGainRenderer.CvbsAgcStatistics);

    double[] orderedVideo = Enumerable.Repeat(400.0, lineLength * lineCount).ToArray();
    for (int line = 0; line < lineCount; line++)
    {
        double blank = line is >= 7 and <= 11 ? 300.0 : 100.0;
        int lineBase = line * lineLength;
        for (int i = lineBase + 12; i < lineBase + 72; i++)
        {
            orderedVideo[i] = blank - 40.0;
        }

        for (int i = lineBase + 96; i < lineBase + 164; i++)
        {
            orderedVideo[i] = blank;
        }
    }

    var orderedRenderer = new TbcFieldRenderer(
        spec,
        converter,
        cvbsClampAgc: new CvbsClampAgcOptions(Speed: 1.0, GainFactor: 1.0, SetGain: 1.0));
    ushort[] orderedSamples = orderedRenderer.RenderField(orderedVideo, lineLocations);
    AssertEqual((ushort)140, orderedSamples[180]);
    AssertTrue(orderedRenderer.LastCvbsSyncLevels.HasValue);
    AssertClose(60.0, orderedRenderer.LastCvbsSyncLevels!.Value.SyncLevel, 1e-12);
    AssertClose(100.0, orderedRenderer.LastCvbsSyncLevels.Value.BlankLevel, 1e-12);

    DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--clamp_agc",
        "--agc_speed",
        "0.25",
        "--agc_gain_factor",
        "1.5",
        "--agc_set_gain",
        "2",
        "input.s16",
        "out"
    ]));
    AssertClose(0.25, session.TbcRenderer.CvbsClampAgc!.Speed, 1e-12);
    AssertClose(1.5, session.TbcRenderer.CvbsClampAgc.GainFactor, 1e-12);
    AssertClose(2.0, session.TbcRenderer.CvbsClampAgc.SetGain, 1e-12);
}

[Fact(DisplayName = "CVBS clamp AGC matches NumPy float32 staging")]
public void CvbsClampAgcMatchesNumpyFloat32Staging()
{
    const int lineLength = 200;
    const int lineCount = 20;
    var spec = new TbcFrameSpec(
        "PAL",
        lineLength,
        lineCount,
        OutputSampleRateHz: 17_734_475.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    VideoOutputConverter converter = VideoOutputConverter.FromParameters(
        FormatCatalog.Default.GetCvbsParameters("PAL"));
    var options = new CvbsClampAgcOptions(Speed: 1.0, GainFactor: 1.0, SetGain: 0.0);
    var renderer = new TbcFieldRenderer(spec, converter, cvbsClampAgc: options);
    var field = new double[lineLength * lineCount];
    for (int line = 0; line < lineCount; line++)
    {
        float blank = 10_000.0f + (line * 17.25f);
        int lineStart = line * lineLength;
        for (int x = 0; x < lineLength; x++)
        {
            field[lineStart + x] = blank + (2_000.0f + (((x % 37) - 18) * 13.125f));
        }

        for (int x = 12; x < 72; x++)
        {
            field[lineStart + x] = blank + (-8_000.0f + (((x % 5) - 2) * 0.125f));
        }

        for (int x = 96; x < 164; x++)
        {
            field[lineStart + x] = blank + (((x % 7) - 3) * 0.0625f);
        }
    }

    int roundingBoundary = (6 * lineLength) + 180;
    field[roundingBoundary] = 12_906.9931640625f;
    ushort[] samples = (ushort[])InvokePrivateMethod(
        renderer,
        "ConvertCvbsClampAgc",
        field,
        options)!;

    AssertEqual((ushort)42_405, samples[roundingBoundary]);
    AssertEqual(
        "31B045161C504B79C5491F6A80B0E48C46D7B03705F0E4AD2666F035CE34D039",
        Convert.ToHexString(SHA256.HashData(TbcOutputWriter.ToLittleEndianBytes(samples))));
}

[Fact(DisplayName = "CVBS AGC statistics match upstream reporting")]
public void CvbsAgcStatisticsMatchUpstreamReporting()
{
    var statistics = new CvbsAgcStatistics(
        LowestDetectedGain: 0.75,
        HighestDetectedGain: 1.25,
        LowestUsedGain: 0.9,
        HighestUsedGain: 1.1);
    var error = new StringWriter();

    DecodeRunner.WriteCvbsAgcStatistics(statistics, error);

    AssertEqual(
        "Automatic gain control statistics:" + Environment.NewLine
        + " Lowest detected gain:   0.75" + Environment.NewLine
        + " Highest detected gain:  1.25" + Environment.NewLine
        + " Lowest used gain:       0.9" + Environment.NewLine
        + " Highest used gain:      1.1" + Environment.NewLine,
        error.ToString());
    var empty = new StringWriter();
    DecodeRunner.WriteCvbsAgcStatistics(null, empty);
    AssertEqual(string.Empty, empty.ToString());

    var completed = new StringWriter();
    DecodeRunner.WriteCompletionMessage(2, completed);
    AssertEqual(
        Environment.NewLine + "Completed: saving JSON and exiting." + Environment.NewLine,
        completed.ToString());
    var noFrames = new StringWriter();
    DecodeRunner.WriteCompletionMessage(0, noFrames);
    AssertEqual(
        Environment.NewLine + "Completed without handling any frames." + Environment.NewLine,
        noFrames.ToString());

    var testLdfReport = new StringWriter();
    DecodeRunner.WriteTestLdfReport(
        new LdTestLdfWriteResult(
            false,
            "Short read at sample 123.",
            SamplesWritten: 23,
            StartSample: 100,
            EndSample: 1_100_100,
            OutputPath: "bug.ldf",
            ShortReadSample: 123),
        testLdfReport);
    AssertEqual(
        Environment.NewLine
        + "Writing input samples to bug.ldf..." + Environment.NewLine
        + "  Start sample: 100" + Environment.NewLine
        + "  End sample: 1100100" + Environment.NewLine
        + "WARNING: Short read at sample 123" + Environment.NewLine
        + "  Samples written: 23" + Environment.NewLine
        + "Successfully wrote bug.ldf" + Environment.NewLine,
        testLdfReport.ToString());
}

[Fact(DisplayName = "decode runtime reporter matches upstream stream protocol")]
public void DecodeRuntimeReporterMatchesUpstreamStreamProtocol()
{
    var output = new StringWriter();
    var error = new StringWriter();
    double elapsedSeconds = 0.0;
    var reporter = new DecodeRuntimeReporter(output, error, () => elapsedSeconds);

    reporter.Status("Frame status");
    reporter.Log("DEBUG", "log-only detail");
    reporter.Log("WARNING", "visible warning");
    AssertEqual(
        "Frame status" + new string(' ', 80 - "Frame status".Length) + '\r' + Environment.NewLine,
        output.ToString());
    AssertEqual("visible warning" + Environment.NewLine, error.ToString());

    elapsedSeconds = 1.0;
    reporter.FieldsWritten(1);
    elapsedSeconds = 2.0;
    reporter.FieldsWritten(1);
    elapsedSeconds = 5.0;
    reporter.WriteStatistics();
    reporter.WriteStatistics();
    AssertEqual(
        "visible warning" + Environment.NewLine
        + "Took 5.00 seconds to decode 1 frames (0.25 FPS post-setup)" + Environment.NewLine,
        error.ToString());
}

[Fact(DisplayName = "VHS disk guard matches upstream cadence and pause protocol")]
public void VhsDiskGuardMatchesUpstreamCadenceAndPauseProtocol()
{
    AssertTrue(VhsDiskSpaceGuard.ShouldCheck(0));
    AssertTrue(VhsDiskSpaceGuard.ShouldCheck(99));
    AssertFalse(VhsDiskSpaceGuard.ShouldCheck(100));
    AssertFalse(VhsDiskSpaceGuard.ShouldCheck(499));
    AssertTrue(VhsDiskSpaceGuard.ShouldCheck(500));
    AssertFalse(VhsDiskSpaceGuard.ShouldCheck(501));
    AssertTrue(VhsDiskSpaceGuard.ShouldCheck(1_000));

    var output = new StringWriter();
    var error = new StringWriter();
    var reporter = new DecodeRuntimeReporter(output, error, () => 0.0);
    reporter.Status("active status");
    var freeBytes = new Queue<long>(
        [VhsDiskSpaceGuard.MinimumFreeBytes - 1, VhsDiskSpaceGuard.MinimumFreeBytes - 1, VhsDiskSpaceGuard.MinimumFreeBytes]);
    var waits = new List<TimeSpan>();
    string? checkedDirectory = null;
    var guard = new VhsDiskSpaceGuard(
        directory =>
        {
            checkedDirectory = directory;
            return freeBytes.Dequeue();
        },
        waits.Add);
    string outputBase = Path.Combine(Path.GetTempPath(), "vhs-disk-guard", "capture");

    guard.Check(
        outputBase,
        fieldsWritten: 1,
        reporter,
        TestContext.Current.CancellationToken);

    string status = "active status";
    AssertEqual(status + new string(' ', 80 - status.Length) + '\r', output.ToString());
    AssertEqual(Path.GetDirectoryName(Path.GetFullPath(outputBase)), checkedDirectory);
    AssertEqual(2, waits.Count);
    AssertEqual(TimeSpan.FromSeconds(1.0), waits[0]);
    AssertEqual(TimeSpan.FromSeconds(1.0), waits[1]);
    AssertEqual(
        Environment.NewLine
        + "Less than 10GB of free disk space is remaining, decoding paused. "
        + "Decoding will resume once there is more space, or press Ctrl+C to exit."
        + Environment.NewLine
        + Environment.NewLine
        + "Disk space available, resuming decode."
        + Environment.NewLine,
        error.ToString());

    int skippedQueries = 0;
    new VhsDiskSpaceGuard(_ =>
    {
        skippedQueries++;
        return 0;
    }).Check(
        outputBase,
        fieldsWritten: 100,
        reporter,
        TestContext.Current.CancellationToken);
    AssertEqual(0, skippedQueries);

    var ignoredError = new StringWriter();
    var ignoredReporter = new DecodeRuntimeReporter(TextWriter.Null, ignoredError, () => 0.0);
    new VhsDiskSpaceGuard(_ => throw new IOException("unavailable"))
        .Check(
            outputBase,
            fieldsWritten: 1,
            ignoredReporter,
            TestContext.Current.CancellationToken);
    AssertEqual(string.Empty, ignoredError.ToString());

    using var cancellationSource = new CancellationTokenSource();
    var cancellationError = new StringWriter();
    var cancellationReporter = new DecodeRuntimeReporter(
        TextWriter.Null,
        cancellationError,
        () => 0.0);
    var cancellationGuard = new VhsDiskSpaceGuard(
        _ => VhsDiskSpaceGuard.MinimumFreeBytes - 1,
        _ => cancellationSource.Cancel());
    AssertThrows<OperationCanceledException>(() => cancellationGuard.Check(
        outputBase,
        fieldsWritten: 1,
        cancellationReporter,
        cancellationSource.Token));
    AssertEqual(
        Environment.NewLine
        + "Less than 10GB of free disk space is remaining, decoding paused. "
        + "Decoding will resume once there is more space, or press Ctrl+C to exit."
        + Environment.NewLine,
        cancellationError.ToString());
}

[Fact(DisplayName = "decode cancellation finalizes partial output and matches upstream streams")]
public void DecodeCancellationFinalizesPartialOutputAndMatchesUpstreamStreams()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string partialBase = Path.Combine(tempDirectory, "partial");
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--write_db",
            "input.u8",
            partialBase
        ]));
        using var fieldCancellation = new CancellationTokenSource();
        int reads = 0;
        TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int ___)
        {
            reads++;
            if (reads == 2)
            {
                fieldCancellation.Cancel();
                fieldCancellation.Token.ThrowIfCancellationRequested();
            }

            return BuildSyntheticTbcField(
                    begin,
                    new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                    detectedFirstField: true)
                with
                {
                    NextFieldOffsetSamples = 100.0,
                    ChromaSamples = new ushort[activeSession.TbcFrameSpec.FieldSampleCount]
                };
        }

        var engine = new TbcFieldSequenceDecodeEngine(
            readField: ReadField,
            cancellationToken: fieldCancellation.Token);
        AssertThrows<OperationCanceledException>(() => engine.TryDecodeAndWrite(session, Stream.Null));
        AssertEqual(2, reads);
        AssertEqual(
            session.TbcFrameSpec.FieldSampleCount * sizeof(ushort),
            new FileInfo(partialBase + ".tbc").Length);
        AssertEqual(
            session.TbcFrameSpec.FieldSampleCount * sizeof(ushort),
            new FileInfo(partialBase + "_chroma.tbc").Length);
        using (JsonDocument partialJson = JsonDocument.Parse(File.ReadAllText(partialBase + ".tbc.json")))
        {
            AssertEqual(1, partialJson.RootElement.GetProperty("fields").GetArrayLength());
        }

        AssertFalse(File.Exists(partialBase + ".tbc.json.tmp"));
        AssertFalse(File.Exists(partialBase + ".tbc.json.fields.tmp"));
        AssertEqual(1L, SqliteLong(partialBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
        AssertEqual(
            1L,
            SqliteLong(partialBase + ".tbc.db", "SELECT number_of_sequential_fields FROM capture"));

        string inputPath = Path.Combine(tempDirectory, "empty.u8");
        File.WriteAllBytes(inputPath, []);
        string runnerBase = Path.Combine(tempDirectory, "runner");
        ParsedCommand command = Parse(CliSpecs.Vhs, [
            "--pal",
            "--no_resample",
            "--length", "1",
            inputPath,
            runnerBase
        ]);
        using var runnerCancellation = new CancellationTokenSource();
        runnerCancellation.Cancel();
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = new DecodeRunner().Run(
            command,
            output,
            error,
            runnerCancellation.Token);

        string termination = Environment.NewLine
            + "Terminated, saving JSON and exiting"
            + Environment.NewLine;
        AssertEqual(1, exitCode);
        AssertEqual(termination, output.ToString());
        AssertEqual(string.Empty, error.ToString());
        AssertFalse(File.Exists(runnerBase + ".tbc.json"));
        AssertEqual("{", File.ReadAllText(runnerBase + ".tbc.json.tmp"));

        var ldOutput = new StringWriter();
        var ldError = new StringWriter();
        DecodeRunner.WriteTerminationMessage(CliSpecs.LaserDisc, ldOutput, ldError);
        AssertEqual(string.Empty, ldOutput.ToString());
        AssertEqual(termination, ldError.ToString());

        var cvbsOutput = new StringWriter();
        var cvbsError = new StringWriter();
        DecodeRunner.WriteTerminationMessage(CliSpecs.Cvbs, cvbsOutput, cvbsError);
        AssertEqual(termination, cvbsOutput.ToString());
        AssertEqual(string.Empty, cvbsError.ToString());
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "VHS runtime Namespace matches Release 4.0 argparse repr")]
public void VhsRuntimeNamespaceMatchesRelease40ArgparseRepr()
{
    ParsedCommand command = Parse(CliSpecs.Vhs, [
        "-",
        "namespace-out.tmp",
        "--noAGC",
        "--frequency", "0.00001",
        "--fm_audio_notch",
        "--y_comb"
    ]);

    AssertEqual(
        "Namespace(infile='-', outfile='namespace-out.tmp', AGC=False, system='NTSC', start=0, "
        + "start_fileloc=-1, length=99999999, overwrite=False, write_db=False, inputfreq=1e-05, "
        + "cxadc=False, threads=5, chroma_trap=False, sharpness=0, notch=None, notch_q=10.0, "
        + "pal=False, ntsc=False, palm=False, ntscj=False, debug=False, skip_hsync_refine=False, "
        + "tape_format='VHS', tape_speed='sp', params_file=None, orc=False, level_adjust=0.1, "
        + "ire0_adjust=False, high_boost=None, wow_level_adjust_smoothing=None, "
        + "wow_interpolation_method='linear', disable_diff_demod=False, fm_audio_notch=10, "
        + "disable_dc_offset=True, enable_dc_offset=False, nldeemp=False, subdeemp=False, "
        + "y_comb=1.5, cafc=False, track_phase=None, detect_chroma_track_phase=False, "
        + "disable_phase_correction=False, disable_burst_hsync=False, enable_color_killer=False, "
        + "disable_comb=False, skip_chroma=False, debug_plot=None, disable_right_hsync=False, "
        + "level_detect_divisor=3, no_resample=False, fallback_vsync=False, relaxed_line0=False, "
        + "field_order_confidence=100, field_order_action='detect', saved_levels=False, "
        + "export_raw_tbc=False, nodod=False, dod_threshold_p=None, dod_threshold_a=None, "
        + "dod_hysteresis=1.25, gnrc_afe=False, noAGC=True)",
        PythonNamespaceFormatter.Format(command));

    string paramsPath = typeof(CompatibilityTests).Assembly.Location;
    ParsedCommand paramsCommand = Parse(CliSpecs.Vhs, ["--params_file", paramsPath, "-", "out"]);
    string paramsNamespace = PythonNamespaceFormatter.Format(paramsCommand);
    AssertContains(
        paramsNamespace,
        $"params_file=<_io.TextIOWrapper name={PythonNamespaceFormatter.FormatString(paramsPath)} mode='r' encoding=");
}

[Fact(DisplayName = "CVBS runtime Namespace matches Release 4.0 argparse repr")]
public void CvbsRuntimeNamespaceMatchesRelease40ArgparseRepr()
{
    ParsedCommand command = Parse(CliSpecs.Cvbs, [
        "--noAGC",
        "-A",
        "--clamp_agc",
        "--agc_speed", "0.25",
        "--frequency", "0.00001",
        "-",
        "namespace-out.tmp"
    ]);

    AssertEqual(
        "Namespace(infile='-', outfile='namespace-out.tmp', AGC=False, system='NTSC', start=0, "
        + "start_fileloc=-1, length=99999999, overwrite=False, write_db=False, inputfreq=1e-05, "
        + "cxadc=False, threads=5, chroma_trap=False, sharpness=0, notch=None, notch_q=10.0, "
        + "pal=False, ntsc=False, palm=False, ntscj=False, debug=False, skip_hsync_refine=False, "
        + "seek=-1, auto_sync=True, no_auto_sync=False, clamp_agc=True, agc_speed=0.25, "
        + "agc_gain_factor=1.0, agc_set_gain=0.0, rhs_hsync=False, wow_level_adjust_smoothing=0, "
        + "wow_interpolation_method='linear', noAGC=True)",
        PythonNamespaceFormatter.Format(command));
}

[Fact(DisplayName = "LD runtime Namespace matches Release 4.0 argparse repr")]
public void LaserDiscRuntimeNamespaceMatchesRelease40ArgparseRepr()
{
    ParsedCommand command = Parse(CliSpecs.LaserDisc, [
        "in'put.s16",
        "out\"base",
        "--start_fileloc", "-1",
        "--deemp_strength", "1",
        "--frequency", "0.00001"
    ]);

    AssertEqual(
        "Namespace(infile=\"in'put.s16\", outfile='out\"base', start=0, length=110000, seek=-1, "
        + "pal=False, ntsc=False, ntscj=False, MTF=1.0, MTF_offset=0, noAGC=False, nodod=False, "
        + "noefm=False, prefm=False, daa=False, AC3=False, start_fileloc=-1.0, ignoreleadout=False, "
        + "verboseVITS=False, RF_TBC=False, lowband=False, NTSC_color_notch_filter=False, "
        + "V4300D_notch_filter=False, deemp_low=0, deemp_high=0, deemp_strength=1.0, "
        + "wow_level_adjust_smoothing=0, wow_interpolation_method='linear', threads=4, "
        + "inputfreq=1e-05, analog_audio_freq=44100, ntsc_audio_rate=False, vbpf_low=None, "
        + "vbpf_high=None, vlpf=None, vlpf_order=-1, audio_filterwidth=None, use_profiler=False, "
        + "write_test_ldf=None)",
        PythonNamespaceFormatter.Format(command));
    AssertEqual("1e+16", PythonNamespaceFormatter.FormatValue(1e16));
    AssertEqual("-0.0", PythonNamespaceFormatter.FormatValue(-0.0));
    AssertEqual("'line\\n\\x00'", PythonNamespaceFormatter.FormatValue("line\n\0"));
}

[Fact(DisplayName = "decode read errors report context and finalize partial output")]
public void DecodeReadErrorsReportContextAndFinalizePartialOutput()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "empty.u8");
        string outputBase = Path.Combine(tempDirectory, "partial-error");
        File.WriteAllBytes(inputPath, []);
        ParsedCommand command = Parse(CliSpecs.Vhs, [
            "--pal",
            "--no_resample",
            "--write_db",
            "--length", "1",
            inputPath,
            outputBase
        ]);
        int reads = 0;
        int fieldSampleCount = 0;
        TbcDecodedField? ReadField(
            DecodeSession activeSession,
            Stream _,
            long begin,
            int __,
            int ___)
        {
            reads++;
            if (reads == 2)
            {
                throw new InvalidOperationException("synthetic field failure");
            }

            fieldSampleCount = activeSession.TbcFrameSpec.FieldSampleCount;
            return BuildSyntheticTbcField(
                    begin,
                    new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                    detectedFirstField: true)
                with
                {
                    NextFieldOffsetSamples = 100.0,
                    ChromaSamples = new ushort[activeSession.TbcFrameSpec.FieldSampleCount]
                };
        }

        var runner = new DecodeRunner(cancellationToken => new TbcFieldSequenceDecodeEngine(
            readField: ReadField,
            vhsDiskSpaceGuard: new VhsDiskSpaceGuard(
                _ => VhsDiskSpaceGuard.MinimumFreeBytes + 1),
            cancellationToken: cancellationToken));
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = runner.Run(
            command,
            output,
            error,
            TestContext.Current.CancellationToken);

        string errorText = error.ToString();
        AssertEqual(1, exitCode);
        AssertEqual(2, reads);
        AssertEqual(string.Empty, output.ToString());
        AssertTrue(errorText.StartsWith(
            Environment.NewLine
            + "ERROR - please paste the following into a bug report:"
            + Environment.NewLine,
            StringComparison.Ordinal));
        AssertContains(errorText, "current sample: 100" + Environment.NewLine);
        AssertContains(
            errorText,
            "arguments: " + PythonNamespaceFormatter.Format(command) + Environment.NewLine);
        AssertContains(errorText, "Exception: synthetic field failure  Traceback:" + Environment.NewLine);
        AssertContains(errorText, "  File \"");
        AssertContains(errorText, "g__ReadField");
        AssertContains(errorText, "Took ");

        AssertEqual(fieldSampleCount * (long)sizeof(ushort), new FileInfo(outputBase + ".tbc").Length);
        AssertEqual(
            fieldSampleCount * (long)sizeof(ushort),
            new FileInfo(outputBase + "_chroma.tbc").Length);
        using (JsonDocument partialJson = JsonDocument.Parse(File.ReadAllText(outputBase + ".tbc.json")))
        {
            AssertEqual(1, partialJson.RootElement.GetProperty("fields").GetArrayLength());
        }

        AssertEqual(1L, SqliteLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
        AssertEqual(
            1L,
            SqliteLong(outputBase + ".tbc.db", "SELECT number_of_sequential_fields FROM capture"));
        AssertFalse(File.Exists(outputBase + ".tbc.json.tmp"));
        AssertFalse(File.Exists(outputBase + ".tbc.json.fields.tmp"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field decode pipeline detects sync and renders a field")]
public void TbcFieldDecodePipelineDetectsSyncAndRendersField()
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
    var renderer = new TbcFieldRenderer(spec, converter);
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        new TbcDropoutDetectionOptions(Enabled: true, ThresholdFraction: 0.18, AbsoluteThreshold: 2.0, Hysteresis: 2.0));

    double[] video = Enumerable.Repeat(0.0, 320).ToArray();
    PaintPulse(video, 10, 10, -40.0);
    PaintPulse(video, 110, 10, -40.0);
    PaintPulse(video, 210, 10, -40.0);
    double[] envelope = Enumerable.Repeat(10.0, video.Length).ToArray();
    for (int i = 125; i <= 140; i++)
    {
        envelope[i] = 1.0;
    }

    short[] efm = Enumerable.Range(0, 400).Select(value => (short)value).ToArray();
    double[] chroma = Enumerable.Range(0, video.Length).Select(value => 1000.0 + value).ToArray();
    var span = new RfDecodedSpan(StartSample: 1234, Input: [], Video: video, DemodRaw: video, Envelope: envelope, Efm: efm, Chroma: chroma);

    TbcDecodedField decoded = pipeline.Decode(span);

    AssertEqual(1234L, decoded.StartSample);
    AssertEqual(3, decoded.RawPulseCount);
    AssertEqual(3, decoded.ClassifiedPulseCount);
    AssertEqual<bool?>(true, decoded.DetectedFirstField);
    AssertEqual(0, decoded.DetectedFirstFieldConfidence);
    AssertClose(-20.0, decoded.SyncThresholdHz, 1e-12);
    AssertClose(100.0, decoded.MeanLineLength, 1e-12);
    AssertSequence([10.0, 110.0, 210.0, 310.0], decoded.LineLocations.Locations.Take(4).ToArray());
    AssertEqual(8, decoded.Samples.Length);
    AssertEqual((ushort)256, decoded.Samples[0]);
    AssertEqual((ushort)656, decoded.Samples[1]);
    AssertEqual((ushort)656, decoded.Samples[3]);
    AssertEqual((ushort)256, decoded.Samples[4]);
    AssertTrue(decoded.ChromaBurstSamples is not null);
    AssertEqual(spec.FieldSampleCount, decoded.ChromaBurstSamples!.Length);
    AssertSequence([1110.0, 1135.0, 1160.0, 1185.0, 1210.0, 1235.0, 1260.0, 1285.0], decoded.ChromaBurstSamples);
    AssertTrue(decoded.ChromaSamples is null);
    AssertTrue(decoded.Efm is not null);
    AssertEqual(200, decoded.Efm!.Length);
    AssertEqual((short)110, decoded.Efm[0]);
    AssertEqual((short)309, decoded.Efm[^1]);
    AssertEqual(1, decoded.Dropouts!.Count);
    AssertIntSequence([1], decoded.Dropouts.FieldLine);
    AssertIntSequence([0], decoded.Dropouts.StartX);
    AssertIntSequence([2], decoded.Dropouts.EndX);
    AssertTrue(decoded.RawInputSamples is null);
    AssertTrue(decoded.PreTbcVideoSamples is null);

    var rawMetricPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        preserveRawMetricSources: true);
    double[] rawInput = Enumerable.Range(0, video.Length).Select(value => 10_000.0 + value).ToArray();
    TbcDecodedField rawMetricDecoded = rawMetricPipeline.Decode(span with { Input = rawInput });
    AssertSequence(rawInput, rawMetricDecoded.RawInputSamples!);
    AssertSequence(video, rawMetricDecoded.PreTbcVideoSamples!);

    var levelDetectPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(DetectLevels: true, LevelDetectDivisor: 1));
    double[] levelDetectLowPass = Enumerable.Repeat(0.0, video.Length).ToArray();
    PaintPulse(levelDetectLowPass, 10, 10, -60.0);
    PaintPulse(levelDetectLowPass, 110, 10, -60.0);
    PaintPulse(levelDetectLowPass, 210, 10, -60.0);
    TbcDecodedField levelDetected = levelDetectPipeline.Decode(new RfDecodedSpan(
        0,
        [],
        video,
        video,
        VideoLowPass: levelDetectLowPass));
    AssertClose(-30.0, levelDetected.SyncThresholdHz, 1e-12);

    var clampPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(DetectLevels: true, LevelDetectDivisor: 1, ClampDcOffset: true));
    double[] shiftedVideo = video.Select(value => value + 15.0).ToArray();
    double[] shiftedLowPass = levelDetectLowPass.Select(value => value + 15.0).ToArray();
    TbcDecodedField clampDetected = clampPipeline.Decode(new RfDecodedSpan(
        0,
        [],
        shiftedVideo,
        shiftedVideo,
        VideoLowPass: shiftedLowPass));
    AssertClose(-30.0, clampDetected.SyncThresholdHz, 1e-12);
    AssertEqual(decoded.Samples[0], clampDetected.Samples[0]);
    AssertEqual(decoded.Samples[1], clampDetected.Samples[1]);

    var savedLevelPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(DetectLevels: true, LevelDetectDivisor: 1, UseSavedLevels: true));
    TbcDecodedField savedFirst = savedLevelPipeline.Decode(new RfDecodedSpan(
        0,
        [],
        video,
        video,
        VideoLowPass: levelDetectLowPass));
    double[] changedLowPass = Enumerable.Repeat(0.0, video.Length).ToArray();
    PaintPulse(changedLowPass, 10, 10, -80.0);
    PaintPulse(changedLowPass, 110, 10, -80.0);
    PaintPulse(changedLowPass, 210, 10, -80.0);
    TbcDecodedField savedSecond = savedLevelPipeline.Decode(new RfDecodedSpan(
        0,
        [],
        video,
        video,
        VideoLowPass: changedLowPass));
    AssertClose(-30.0, savedFirst.SyncThresholdHz, 1e-12);
    AssertClose(-30.0, savedSecond.SyncThresholdHz, 1e-12);

    double[] shallowVideo = Enumerable.Repeat(0.0, video.Length).ToArray();
    PaintPulse(shallowVideo, 10, 10, -10.0);
    PaintPulse(shallowVideo, 110, 10, -10.0);
    PaintPulse(shallowVideo, 210, 10, -10.0);
    TbcDecodedField savedRetry = savedLevelPipeline.Decode(new RfDecodedSpan(
        0,
        [],
        shallowVideo,
        shallowVideo,
        VideoLowPass: shallowVideo));
    AssertClose(-5.0, savedRetry.SyncThresholdHz, 1e-12);
    AssertEqual(3, savedRetry.RawPulseCount);

    var ldVbiAnalyzer = new SyncAnalyzer(
        sampleRateHz: 40_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var ldVbiSpec = spec with { OutputLineCount = 20 };
    var ldVbiRenderer = new TbcFieldRenderer(ldVbiSpec, converter);
    var ldVbiPipeline = new TbcFieldDecodePipeline(
        ldVbiAnalyzer,
        ldVbiRenderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true);
    const int VbiLineLength = 4_000;
    double[] ldVbiVideo = Enumerable.Repeat(100.0, 35 * VbiLineLength).ToArray();
    for (int line = 0; line < 34; line++)
    {
        if (line is 16 or 17 or 18)
        {
            continue;
        }

        PaintPulse(ldVbiVideo, 10 + (line * VbiLineLength), 400, -40.0);
    }

    PaintPhilipsCode(ldVbiVideo, lineStart: 10 + (16 * VbiLineLength), code: 0xAAAAAA, sampleRateMHz: 40.0);
    foreach (int line in Enumerable.Range(11, 23).Where(line => line is not (16 or 17 or 18)))
    {
        int burstStart = 10 + (line * VbiLineLength) + 220;
        Array.Fill(ldVbiVideo, 100.0, burstStart, 97);
        ldVbiVideo[burstStart + 1] = 95.0;
        ldVbiVideo[burstStart + 2] = 105.0;
    }

    TbcDecodedField ldVbiDecoded = ldVbiPipeline.Decode(new RfDecodedSpan(
        0,
        [],
        ldVbiVideo,
        ldVbiVideo));
    AssertEqual(ldVbiSpec.OutputLineCount + 10, ldVbiDecoded.LineLocations.Locations.Length);
    AssertIntSequence([0xAAAAAA], ldVbiDecoded.VbiData!);
    const double NumbaBurstRms = 1.0153461561907278;
    AssertClose(NumbaBurstRms, ldVbiDecoded.MedianBurstIre!.Value, 1e-12);

    var cvbsVbiPipeline = new TbcFieldDecodePipeline(
        ldVbiAnalyzer,
        ldVbiRenderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        decodeVbiData: true);
    TbcDecodedField cvbsVbiDecoded = cvbsVbiPipeline.Decode(new RfDecodedSpan(
        0,
        [],
        ldVbiVideo,
        ldVbiVideo));
    AssertTrue(cvbsVbiPipeline.DecodesVbiData);
    AssertIntSequence([0xAAAAAA], cvbsVbiDecoded.VbiData!);
    AssertClose(NumbaBurstRms, cvbsVbiDecoded.MedianBurstIre!.Value, 1e-12);

    var refineAnalyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 7.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var refinePipeline = new TbcFieldDecodePipeline(
        refineAnalyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        hSyncRefineOptions: HSyncRefineOptions.LeftOnly);
    double[] lowPass = Enumerable.Repeat(0.0, video.Length).ToArray();
    PaintPulse(lowPass, 11, 7, -40.0);
    PaintPulse(lowPass, 111, 7, -40.0);
    PaintPulse(lowPass, 211, 7, -40.0);
    TbcDecodedField refined = refinePipeline.Decode(new RfDecodedSpan(
        0,
        [],
        video,
        video,
        VideoLowPass: lowPass));
    AssertSequence([11.0, 111.0, 211.0, 311.0], refined.LineLocations.Locations.Take(4).ToArray());

    var highRateAnalyzer = new SyncAnalyzer(
        sampleRateHz: 10_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 5.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var highRatePipeline = new TbcFieldDecodePipeline(
        highRateAnalyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        hSyncRefineOptions: HSyncRefineOptions.Default);
    double[] highRateVideo = Enumerable.Repeat(0.0, 3_200).ToArray();
    PaintPulse(highRateVideo, 100, 50, -40.0);
    PaintPulse(highRateVideo, 1_100, 50, -40.0);
    PaintPulse(highRateVideo, 2_100, 50, -40.0);
    double[] highRateLowPass = Enumerable.Repeat(0.0, highRateVideo.Length).ToArray();
    PaintPulse(highRateLowPass, 101, 50, -40.0);
    PaintPulse(highRateLowPass, 1_101, 50, -40.0);
    PaintPulse(highRateLowPass, 2_101, 50, -40.0);
    TbcDecodedField rightRefined = highRatePipeline.Decode(new RfDecodedSpan(
        0,
        [],
        highRateVideo,
        highRateVideo,
        VideoLowPass: highRateLowPass));
    AssertSequence([101.0625, 1101.0625, 2101.0625, 3101.0], rightRefined.LineLocations.Locations.Take(4).ToArray());

    double[] derivativeBadVideo = Enumerable.Repeat(0.0, 3_200).ToArray();
    PaintPulse(derivativeBadVideo, 100, 50, -40.0);
    PaintPulse(derivativeBadVideo, 1_110, 50, -40.0);
    PaintPulse(derivativeBadVideo, 2_100, 50, -40.0);
    var rightRescuePipeline = new TbcFieldDecodePipeline(
        highRateAnalyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        hSyncRefineOptions: HSyncRefineOptions.Default,
        decodeLaserDiscVbi: true);
    TbcDecodedField rightRescued = rightRescuePipeline.Decode(new RfDecodedSpan(
        0,
        [],
        derivativeBadVideo,
        derivativeBadVideo,
        VideoLowPass: highRateLowPass));
    AssertClose(1_101.0625, rightRescued.LineLocations.Locations[1], 1e-12);
    AssertClose(2_101.0625, rightRescued.LineLocations.Locations[2], 1e-12);
    AssertFalse(rightRescued.LineLocations.Filled[1]);
    AssertFalse(rightRescued.LineLocations.Filled[2]);

    double[] porchStateLowPass = Enumerable.Repeat(20.0, highRateVideo.Length).ToArray();
    PaintPulse(porchStateLowPass, 101, 50, -40.0);
    PaintPulse(porchStateLowPass, 1_101, 50, -40.0);
    PaintPulse(porchStateLowPass, 2_101, 50, -40.0);
    for (int sample = 1_091; sample < 1_097; sample++)
    {
        porchStateLowPass[sample] = 80.0;
    }

    porchStateLowPass[1_120] = 50.0;
    var porchStatePipeline = new TbcFieldDecodePipeline(
        highRateAnalyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        hSyncRefineOptions: HSyncRefineOptions.LeftOnly);
    TbcDecodedField porchStateField = porchStatePipeline.Decode(new RfDecodedSpan(
        0,
        [],
        highRateVideo,
        highRateVideo,
        VideoLowPass: porchStateLowPass));
    AssertFalse(porchStateField.LineLocations.Filled[1]);
    AssertTrue(PulseDetection.InRange(porchStateField.LineLocations.Locations[1], 1_100.0, 1_102.0));

    var trackedRenderer = new TbcFieldRenderer(
        spec,
        converter,
        trackPhaseIre0Offset: new TrackPhaseIre0OffsetOptions(TrackPhase: 0, Offset0Hz: 10.0, Offset1Hz: 0.0));
    var trackedPipeline = new TbcFieldDecodePipeline(analyzer, trackedRenderer, converter, "NTSC", TbcDropoutDetectionOptions.Disabled);
    TbcDecodedField trackedField0 = trackedPipeline.Decode(span, fieldNumber: 0);
    TbcDecodedField trackedField1 = trackedPipeline.Decode(span, fieldNumber: 1);
    AssertTrue(trackedField0.Samples[1] < trackedField1.Samples[1]);

    AssertThrows<InvalidOperationException>(() => pipeline.Decode(new RfDecodedSpan(0, [], [0.0, 0.0, 0.0], [])));

    var palParitySpec = spec with { OutputLineCount = 12 };
    var palParityRenderer = new TbcFieldRenderer(palParitySpec, converter);
    var palAnalyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0,
        numPulses: 5);
    var palParityPipeline = new TbcFieldDecodePipeline(palAnalyzer, palParityRenderer, converter, "PAL");
    double[] palParityVideo = Enumerable.Repeat(0.0, 2_500).ToArray();
    PaintPulse(palParityVideo, 10, 10, -40.0);
    PaintPulse(palParityVideo, 110, 10, -40.0);
    PaintTestVBlank(palParityVideo, line0: 210, isFirstField: true, system: "PAL");
    PaintPulse(palParityVideo, 1_010, 10, -40.0);
    PaintPulse(palParityVideo, 1_110, 10, -40.0);
    TbcDecodedField palParityDecoded = palParityPipeline.Decode(new RfDecodedSpan(0, [], palParityVideo, palParityVideo));
    AssertClose(210.0, palParityDecoded.LineLocations.Locations[0], 1e-12);
    AssertEqual<bool?>(true, palParityDecoded.DetectedFirstField);
    AssertEqual((ushort)256, palParityDecoded.Samples[0]);

    double[] palSecondVideo = Enumerable.Repeat(0.0, 2_800).ToArray();
    PaintPulse(palSecondVideo, 10, 10, -40.0);
    PaintPulse(palSecondVideo, 110, 10, -40.0);
    PaintTestVBlank(palSecondVideo, line0: 210, isFirstField: false, system: "PAL");
    PaintPulse(palSecondVideo, 1_110, 10, -40.0);
    PaintPulse(palSecondVideo, 1_210, 10, -40.0);
    double[] palSecondChroma = Enumerable.Range(0, palSecondVideo.Length).Select(value => (double)value).ToArray();
    TbcDecodedField palSecondDecoded = new TbcFieldDecodePipeline(
        palAnalyzer,
        palParityRenderer,
        converter,
        "PAL").Decode(new RfDecodedSpan(
            0,
            [],
            palSecondVideo,
            palSecondVideo,
            Chroma: palSecondChroma));
    AssertEqual<bool?>(false, palSecondDecoded.DetectedFirstField);
    AssertClose(
        (float)palSecondDecoded.LineLocations.Locations[4],
        palSecondDecoded.ChromaBurstSamples![0],
        1e-5);
}

[Fact(DisplayName = "TBC field decode pipeline applies fallback vsync line0")]
public void TbcFieldDecodePipelineAppliesFallbackVsyncLine0()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 12,
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
    var renderer = new TbcFieldRenderer(spec, converter);
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);

    double[] firstVideo = BuildLine0FallbackVideo(
        length: 2_200,
        hsyncStarts: Enumerable.Range(0, 14).Select(line => 10 + (line * 100)));
    double[] lateSecondVideo = BuildLine0FallbackVideo(
        length: 2_200,
        hsyncStarts: Enumerable.Range(0, 9).Select(line => 510 + (line * 100)));

    var defaultPipeline = new TbcFieldDecodePipeline(analyzer, renderer, converter, "NTSC", TbcDropoutDetectionOptions.Disabled);
    defaultPipeline.Decode(new RfDecodedSpan(1000, [], firstVideo, firstVideo));
    TbcDecodedField defaultSecond = defaultPipeline.Decode(new RfDecodedSpan(2100, [], lateSecondVideo, lateSecondVideo));
    AssertClose(110.0, defaultSecond.LineLocations.Locations[0], 1e-12);

    double[] shiftedSecondVideo = BuildLine0FallbackVideo(
        length: 2_200,
        hsyncStarts: Enumerable.Range(0, 9).Select(line => 512 + (line * 100)));
    var alignedPredictionPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled);
    alignedPredictionPipeline.Decode(new RfDecodedSpan(1000, [], firstVideo, firstVideo));
    TbcDecodedField alignedPrediction = alignedPredictionPipeline.Decode(new RfDecodedSpan(
        2100,
        [],
        shiftedSecondVideo,
        shiftedSecondVideo));
    AssertClose(112.0, alignedPrediction.LineLocations.Locations[0], 1e-12);

    double[] snappedSecondVideo = BuildLine0FallbackVideo(
        length: 2_200,
        hsyncStarts: Enumerable.Range(0, 9).Select(line => 510 + (line * 100)));
    PaintPulse(snappedSecondVideo, 112, 20, -40.0);
    var fallbackPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(
            DetectLevels: true,
            LevelDetectDivisor: 1,
            UseFallbackVSync: true));
    fallbackPipeline.Decode(new RfDecodedSpan(1000, [], firstVideo, firstVideo));
    TbcDecodedField snappedSecond = fallbackPipeline.Decode(new RfDecodedSpan(2100, [], snappedSecondVideo, snappedSecondVideo));
    AssertClose(-190.0, snappedSecond.LineLocations.Locations[0], 1e-12);

    var relaxedPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(
            DetectLevels: true,
            LevelDetectDivisor: 1,
            UseFallbackVSync: true,
            RelaxedLine0: true));
    relaxedPipeline.Decode(new RfDecodedSpan(1000, [], firstVideo, firstVideo));
    TbcDecodedField relaxedSecond = relaxedPipeline.Decode(new RfDecodedSpan(2100, [], lateSecondVideo, lateSecondVideo));
    AssertClose(210.0, relaxedSecond.LineLocations.Locations[0], 1e-12);

    double[] detectedSecondField = Enumerable.Repeat(0.0, 2_800).ToArray();
    PaintPulse(detectedSecondField, 10, 10, -40.0);
    PaintPulse(detectedSecondField, 110, 10, -40.0);
    PaintTestVBlank(detectedSecondField, line0: 210, isFirstField: false, system: "NTSC");
    foreach (int line in Enumerable.Range(0, 14))
    {
        PaintPulse(detectedSecondField, 1_210 + (line * 100), 10, -40.0);
    }
    var parityAwarePipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(
            DetectLevels: true,
            LevelDetectDivisor: 1,
            UseFallbackVSync: true,
            RelaxedLine0: true));
    TbcDecodedField detectedSecond = parityAwarePipeline.Decode(new RfDecodedSpan(1000, [], detectedSecondField, detectedSecondField));
    AssertEqual<bool?>(false, detectedSecond.DetectedFirstField);
    TbcDecodedField parityAwareNext = parityAwarePipeline.Decode(new RfDecodedSpan(2300, [], lateSecondVideo, lateSecondVideo));
    AssertClose(210.0, parityAwareNext.LineLocations.Locations[0], 1e-12);

    var driftPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        syncDetectionOptions: new SyncDetectionOptions(
            DetectLevels: true,
            LevelDetectDivisor: 1,
            UseFallbackVSync: true));
    driftPipeline.Decode(new RfDecodedSpan(1000, [], firstVideo, firstVideo));
    double[] driftObservedVideo = Enumerable.Repeat(0.0, 2_200).ToArray();
    PaintPulse(driftObservedVideo, 12, 10, -40.0);
    PaintTestVBlank(driftObservedVideo, line0: 112, isFirstField: false, system: "NTSC");
    foreach (int line in Enumerable.Range(0, 10))
    {
        PaintPulse(driftObservedVideo, 1_112 + (line * 100), 10, -40.0);
    }

    TbcDecodedField driftObserved = driftPipeline.Decode(new RfDecodedSpan(
        2100,
        [],
        driftObservedVideo,
        driftObservedVideo));
    AssertClose(112.0, driftObserved.LineLocations.Locations[0], 1e-12);
    AssertClose(
        0.02,
        Convert.ToDouble(PrivateFieldValue(driftPipeline, "_previousHSyncDifference")),
        1e-12);

    double[] driftEstimatedVideo = Enumerable.Repeat(0.0, 2_200).ToArray();
    for (int pulse = 0; pulse < 6; pulse++)
    {
        PaintPulse(driftEstimatedVideo, 500 + (pulse * 50), 5, -40.0);
    }

    TbcDecodedField driftEstimated = driftPipeline.Decode(new RfDecodedSpan(
        3200,
        [],
        driftEstimatedVideo,
        driftEstimatedVideo));
    AssertClose(114.0, driftEstimated.LineLocations.Locations[0], 1e-12);

    var noHSyncPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled);
    noHSyncPipeline.Decode(new RfDecodedSpan(1000, [], firstVideo, firstVideo));
    double[] equalizingOnlyVideo = Enumerable.Repeat(0.0, 2_200).ToArray();
    for (int pulse = 0; pulse < 6; pulse++)
    {
        PaintPulse(equalizingOnlyVideo, 50 + (pulse * 50), 5, -40.0);
    }

    TbcDecodedField noHSyncEstimate = noHSyncPipeline.Decode(new RfDecodedSpan(
        3300,
        [],
        equalizingOnlyVideo,
        equalizingOnlyVideo));
    AssertClose(-1_000.0, noHSyncEstimate.LineLocations.Locations[0], 1e-12);
}

[Fact(DisplayName = "TBC field decode delays CAFC carrier used for burst phase by one field")]
public void TbcFieldDecodeDelaysCafcBurstPhaseCarrier()
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
    var converter = new VideoOutputConverter(0.0, 1.0, 256, -40.0, 10.0);
    var pipeline = new TbcFieldDecodePipeline(
        new SyncAnalyzer(1_000_000.0, 100.0, 10.0, 5.0, 20.0),
        new TbcFieldRenderer(spec, converter),
        converter,
        "NTSC");
    var phase = new ChromaPhaseSequenceResult(0, [], 0, 0.0, 0.0, 0.0, 0.0);
    var commit = typeof(TbcFieldDecodePipeline).GetMethod(
        "CommitChromaState",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new MissingMethodException("CommitChromaState");

    commit.Invoke(pipeline, [new VhsChromaFieldResult([], 0, null, 0, phase)
    {
        CarrierEstimate = new ChromaCarrierEstimate(629_370.0, 629_355.0, 629_355.0, -15.0, 0.125)
    }]);
    TbcFieldDecodeState afterFirstField = pipeline.CaptureState();
    AssertClose(629_355.0, afterFirstField.ChromaAfcCarrierHz!.Value, 0.0);
    AssertEqual<double?>(null, afterFirstField.ChromaAfcPhaseCarrierHz);
    AssertClose(0.0, afterFirstField.ChromaAfcPhaseCarrierRadians, 0.0);

    commit.Invoke(pipeline, [new VhsChromaFieldResult([], 0, null, 0, phase)
    {
        CarrierEstimate = new ChromaCarrierEstimate(629_370.0, 629_386.0, 629_386.0, 16.0, 0.25)
    }]);
    TbcFieldDecodeState afterSecondField = pipeline.CaptureState();
    AssertClose(629_386.0, afterSecondField.ChromaAfcCarrierHz!.Value, 0.0);
    AssertClose(629_355.0, afterSecondField.ChromaAfcPhaseCarrierHz!.Value, 0.0);
    AssertClose(0.125, afterSecondField.ChromaAfcPhaseCarrierRadians, 0.0);
}

[Fact(DisplayName = "TBC field decode pipeline computes upstream next-field offsets")]
public void TbcFieldDecodePipelineComputesNextFieldOffsets()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 50,
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
        TbcDropoutDetectionOptions.Disabled);

    double[] withNextVBlank = Enumerable.Repeat(0.0, 8_000).ToArray();
    foreach (int line in Enumerable.Range(0, 9))
    {
        PaintPulse(withNextVBlank, 10 + (line * 100), 10, -40.0);
    }

    PaintTestVBlank(withNextVBlank, line0: 810, isFirstField: true, system: "NTSC");
    foreach (int line in Enumerable.Range(0, 41))
    {
        PaintPulse(withNextVBlank, 1_810 + (line * 100), 10, -40.0);
    }

    PaintTestVBlank(withNextVBlank, line0: 5_810, isFirstField: false, system: "NTSC");
    PaintPulse(withNextVBlank, 6_910, 10, -40.0);
    PaintPulse(withNextVBlank, 7_010, 10, -40.0);

    TbcDecodedField vblankField = pipeline.Decode(new RfDecodedSpan(
        1_000,
        [],
        withNextVBlank,
        withNextVBlank));
    AssertClose(810.0, vblankField.LineLocations.Locations[0], 1e-12);
    AssertClose(5_010.0, vblankField.NextFieldOffsetSamples!.Value, 1e-12);
    AssertEqual(6_010L, TbcFieldSequenceDecodeEngine.EstimateNextFieldStart(
        DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["input.u8", "out"])),
        vblankField));

    var blockCutPipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, converter),
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        inputBlockCutSamples: 1_024);
    TbcDecodedField blockCutField = blockCutPipeline.Decode(new RfDecodedSpan(
        1_000,
        [],
        withNextVBlank,
        withNextVBlank));
    AssertClose(6_034.0, blockCutField.NextFieldOffsetSamples!.Value, 1e-12);

    double[] withoutNextVBlank = Enumerable.Repeat(0.0, 8_000).ToArray();
    foreach (int line in Enumerable.Range(0, 9))
    {
        PaintPulse(withoutNextVBlank, 10 + (line * 100), 10, -40.0);
    }

    PaintTestVBlank(withoutNextVBlank, line0: 810, isFirstField: true, system: "NTSC");
    foreach (int line in Enumerable.Range(0, 54))
    {
        PaintPulse(withoutNextVBlank, 1_810 + (line * 100), 10, -40.0);
    }

    TbcDecodedField fallbackField = pipeline.Decode(new RfDecodedSpan(
        0,
        [],
        withoutNextVBlank,
        withoutNextVBlank));
    AssertClose(810.0, fallbackField.LineLocations.Locations[0], 1e-12);
    AssertClose(5_110.0, fallbackField.NextFieldOffsetSamples!.Value, 1e-12);
    double expectedNominalFieldLength = fallbackField.DetectedFirstField == false ? 4_900.0 : 5_000.0;
    AssertClose(expectedNominalFieldLength, fallbackField.NominalFieldLengthSamples!.Value, 1e-12);
}

[Fact(DisplayName = "TBC history line0 prediction matches C rounding")]
public void TbcHistoryLine0PredictionMatchesCRounding()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 12,
        OutputSampleRateHz: 4_000_000.0,
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
        TbcDropoutDetectionOptions.Disabled,
        decodeType: "vhs");
    SetPrivateFieldValue(pipeline, "_previousFirstHSyncLocation", 1_800.5);
    SetPrivateFieldValue(pipeline, "_previousFirstHSyncReadLocation", 0L);
    SetPrivateFieldValue(pipeline, "_previousDetectedFirstField", true);
    SetPrivateFieldValue(pipeline, "_previousHSyncDifference", -1.0);

    double[] equalizingOnly = new double[5_000];
    for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
    {
        PaintPulse(equalizingOnly, 100 + (pulse * 50), 5, -40.0);
    }

    TbcDecodedField field = pipeline.Decode(new RfDecodedSpan(
        1_000,
        equalizingOnly,
        equalizingOnly,
        equalizingOnly));
    AssertClose(1_000.6743055555555, field.LineLocations.Locations[0], 1e-12);

    var palSpec = spec with { System = "PAL", OutputLineCount = 313 };
    var palAnalyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0,
        numPulses: 5);
    var palPipeline = new TbcFieldDecodePipeline(
        palAnalyzer,
        new TbcFieldRenderer(palSpec, converter),
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        decodeType: "vhs");
    SetPrivateFieldValue(palPipeline, "_previousFirstHSyncLocation", 900.0);
    SetPrivateFieldValue(palPipeline, "_previousFirstHSyncReadLocation", 1_000L);
    SetPrivateFieldValue(palPipeline, "_previousDetectedFirstField", false);
    SetPrivateFieldValue(palPipeline, "_previousHSyncDifference", -1.0);

    ClassifiedSyncPulse[] palPulses =
    [
        Sync(SyncPulseKind.HSync, 30_820),
        Sync(SyncPulseKind.HSync, 31_102),
        Sync(SyncPulseKind.HSync, 31_202),
        Sync(SyncPulseKind.HSync, 31_280, inOrder: false)
    ];
    object palEstimate = InvokePrivateMethod(
        palPipeline,
        "TryEstimateLine0FromPrevious",
        palPulses,
        2_000L,
        100.0,
        312,
        true)!;
    AssertClose(30_302.0, Convert.ToDouble(PrivatePropertyValue(palEstimate, "Location")), 1e-12);
    AssertClose(31_102.0, Convert.ToDouble(PrivatePropertyValue(palEstimate, "FirstHSyncLocation")), 1e-12);

    object sameReadLocationEstimate = InvokePrivateMethod(
        palPipeline,
        "TryEstimateLine0FromPrevious",
        Array.Empty<ClassifiedSyncPulse>(),
        1_000L,
        100.0,
        312,
        true)!;
    AssertClose(31_300.0, Convert.ToDouble(PrivatePropertyValue(sameReadLocationEstimate, "Location")), 1e-12);
}

[Fact(DisplayName = "TBC field decode pipeline applies LD AGC")]
public void TbcFieldDecodePipelineAppliesLdAgc()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 20,
        OutputSampleRateHz: 14_318_180.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var converter = new VideoOutputConverter(
        ire0: 1000.0,
        hzIre: 10.0,
        outputZero: 256,
        vsyncIre: -40.0,
        outputScale: 10.0);
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var renderer = new TbcFieldRenderer(spec, converter);
    var agcOptions = new LaserDiscAgcOptions(
        ColorBurstEndUsec: 10.0,
        ActiveVideoStartUsec: 20.0,
        WhiteSlices: [new LaserDiscVitsLevelSlice(Line: 15, StartUsec: 12.0, LengthUsec: 8.0, Percentile: 50.0)]);
    var noAgcPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true);
    var agcPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        laserDiscAgcOptions: agcOptions,
        decodeLaserDiscVbi: true);

    double[] video = Enumerable.Repeat(1100.0, 2_400).ToArray();
    double[] lowPass = Enumerable.Repeat(1100.0, video.Length).ToArray();
    for (int line = 0; line < 23; line++)
    {
        int pulseLength = line is 3 or 4 ? 20 : 10;
        int pulseStart = 10 + (line * 100);
        PaintPulse(video, pulseStart, pulseLength, 700.0);
        PaintPulse(lowPass, pulseStart, pulseLength, 700.0);
    }

    for (int i = 0; i < 8; i++)
    {
        video[10 + (15 * 100) + 12 + i] = 2050.0;
    }

    var span = new RfDecodedSpan(0, [], video, video, VideoLowPass: lowPass);
    TbcDecodedField noAgc = noAgcPipeline.Decode(span);
    TbcDecodedField agc = agcPipeline.Decode(span);

    AssertEqual<bool?>(true, agc.DetectedFirstField);
    AssertEqual(0, agc.DetectedFirstFieldConfidence);
    AssertTrue(agc.SyncConfidence > 80);
    AssertFalse(noAgc.LaserDiscAgcAdjusted);
    AssertTrue(agc.LaserDiscAgcAdjusted);
    AssertEqual((ushort)756, noAgc.Samples[1]);
    AssertEqual((ushort)677, agc.Samples[1]);
    AssertTrue(noAgc.MedianBurstIre.HasValue);
    AssertTrue(agc.MedianBurstIre.HasValue);
    AssertTrue(agc.OutputConverter is not null);
    double noAgcMedianBurstIre = noAgc.MedianBurstIre
        ?? throw new InvalidOperationException("The non-AGC LD field did not report a burst level.");
    double agcMedianBurstIre = agc.MedianBurstIre
        ?? throw new InvalidOperationException("The AGC LD field did not report a burst level.");
    VideoOutputConverter agcOutputConverter = agc.OutputConverter
        ?? throw new InvalidOperationException("The AGC LD field did not retain its field-level converter.");
    AssertClose(
        noAgcMedianBurstIre * converter.HzIre / agcOutputConverter.HzIre,
        agcMedianBurstIre,
        1e-12);

    var diagnostics = new List<(string Level, string Message)>();
    var warningPipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        laserDiscAgcOptions: agcOptions,
        decodeLaserDiscVbi: true,
        diagnosticLogger: (level, message) => diagnostics.Add((level, message)));
    double[] malformedVideo = Enumerable.Repeat(1100.0, 2_400).ToArray();
    double[] malformedLowPass = Enumerable.Repeat(1100.0, malformedVideo.Length).ToArray();
    for (int line = 0; line < 23; line++)
    {
        PaintPulse(malformedVideo, line * 100, 10, 950.0);
        PaintPulse(malformedLowPass, line * 100, 10, 950.0);
    }

    for (int i = 0; i < 8; i++)
    {
        malformedVideo[(15 * 100) + 12 + i] = 2050.0;
    }

    double[] agcLineLocations = Enumerable.Range(0, 24).Select(line => line * 100.0).ToArray();
    InvokePrivateMethod(
        warningPipeline,
        "ResolveLaserDiscAgcConverter",
        new RfDecodedSpan(
            0,
            [],
            malformedVideo,
            malformedVideo,
            VideoLowPass: malformedLowPass),
        agcLineLocations,
        100.0,
        true,
        100,
        converter,
        0);

    AssertEqual(1, diagnostics.Count);
    AssertEqual("WARNING", diagnostics[0].Level);
    AssertEqual(
        "At field #0, Auto-level detection malfunction "
            + "(vsync IRE computed at -15.79, nominal ~= -40), possible disk skipping",
        diagnostics[0].Message);
}

[Fact(DisplayName = "TBC field decode pipeline computes LD RF ratio")]
public void TbcFieldDecodePipelineComputesLdRfRatio()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 100,
        OutputLineCount: 20,
        OutputSampleRateHz: 1_000_000.0,
        ColourBurstStart: null,
        ColourBurstEnd: null,
        ActiveVideoStart: null,
        ActiveVideoEnd: null);
    var converter = new VideoOutputConverter(
        ire0: 1_000.0,
        hzIre: 10.0,
        outputZero: 10_000,
        vsyncIre: -40.0,
        outputScale: 100.0);
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var metricOptions = new LaserDiscRfMetricOptions(
        WhiteSlices: [new LaserDiscVitsLevelSlice(Line: 15, StartUsec: 12.0, LengthUsec: 8.0, Percentile: 50.0)],
        BlackSlice: new LaserDiscVitsLevelSlice(Line: 16, StartUsec: 12.0, LengthUsec: 8.0, Percentile: 50.0),
        VideoWhiteDelaySamples: 0,
        VideoSyncDelaySamples: 0);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, converter),
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        laserDiscRfMetricOptions: metricOptions);

    double[] video = Enumerable.Repeat(1_000.0, 2_400).ToArray();
    for (int line = 0; line < 23; line++)
    {
        int pulseLength = line is 3 or 4 ? 20 : 10;
        PaintPulse(video, 10 + (line * 100), pulseLength, 600.0);
    }

    for (int i = 0; i < 30; i++)
    {
        video[10 + (15 * 100) + 5 + i] = 2_000.0;
    }

    double[] raw = new double[video.Length];
    double[] whitePattern = [-1.0, 0.0, 1.0, -1.0, 0.0, 1.0, -1.0, 0.0, 1.0];
    double[] blackPattern = whitePattern.Select(value => value * 2.0).ToArray();
    Array.Copy(whitePattern, 0, raw, 10 + (15 * 100) + 12, whitePattern.Length);
    Array.Copy(blackPattern, 0, raw, 10 + (16 * 100) + 12, blackPattern.Length);

    TbcDecodedField decoded = pipeline.Decode(new RfDecodedSpan(0, raw, video, video));
    AssertEqual<bool?>(true, decoded.DetectedFirstField);
    AssertClose(2.0, decoded.BlackToWhiteRfRatio ?? double.NaN, 0.0);
}

[Fact(DisplayName = "TBC field decode pipeline refines LD PAL pilot line locations")]
public void TbcFieldDecodePipelineRefinesLdPalPilotLineLocations()
{
    var spec = new TbcFrameSpec(
        "PAL",
        OutputLineLength: 4,
        OutputLineCount: 4,
        OutputSampleRateHz: 14_187_500.0,
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
    var renderer = new TbcFieldRenderer(spec, converter);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true,
        laserDiscPilotRefineOptions: new LaserDiscPilotRefineOptions(PilotMHz: 0.125));

    double[] video = Enumerable.Repeat(0.0, 760).ToArray();
    for (int line = 0; line < 7; line++)
    {
        PaintPulse(video, 10 + (line * 100), 10, -40.0);
    }

    double[] pilot = new double[video.Length];
    for (int line = 0; line < 6; line++)
    {
        int start = 10 + (line * 100);
        if (line == 3)
        {
            pilot[start] = 1.0;
            pilot[start + 1] = 1.0;
            pilot[start + 2] = 1.0;
            pilot[start + 3] = 0.0;
            pilot[start + 4] = -1.0;
            pilot[start + 5] = -1.0;
        }
        else
        {
            pilot[start] = 1.0;
            pilot[start + 1] = 1.0;
            pilot[start + 2] = 0.0;
            pilot[start + 3] = -1.0;
            pilot[start + 4] = -1.0;
            pilot[start + 5] = -1.0;
        }
    }

    TbcDecodedField plain = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true).Decode(new RfDecodedSpan(0, [], video, video));
    TbcDecodedField refined = pipeline.Decode(new RfDecodedSpan(0, [], video, video, VideoPilot: pilot));

    int palLineOffset = refined.DetectedFirstField == false ? 3 : 2;
    AssertEqual(spec.OutputLineCount + palLineOffset + 10 + 3, refined.LineLocations.Locations.Length);
    AssertClose(310.0, plain.LineLocations.Locations[3], 1e-12);
    if (refined.LineLocations.Locations[3] <= plain.LineLocations.Locations[3] + 0.25)
    {
        throw new Exception($"Expected PAL pilot refinement to move line 3; plain={plain.LineLocations.Locations[3]}, refined={refined.LineLocations.Locations[3]}.");
    }

    double boundaryLineRate = (double)InvokePrivateMethod(
        pipeline,
        "LaserDiscLineSamplesPerUsec",
        new double[] { 0.0, 100.0, 200.0, 300.0, 420.0, 500.0 },
        4,
        (int?)4)!;
    AssertClose(1.2, boundaryLineRate, 1e-12);
}

[Fact(DisplayName = "LD PAL pilot slice uses nominal input frequency")]
public void LaserDiscPalPilotSliceUsesNominalInputFrequency()
{
    (int start, int length, double lineOffset) = TbcFieldDecodePipeline.LaserDiscPilotSliceBounds(
        lineStart: 100.25,
        sampleRateMHz: 1.0,
        sourceLength: 1_000);
    AssertEqual(100, start);
    AssertEqual(7, length);
    AssertClose(0.25, lineOffset, 1e-12);
}

[Fact(DisplayName = "LD line sample rate uses Release 4.0 rounded nominal line length")]
public void LaserDiscLineSampleRateUsesRelease40RoundedNominalLineLength()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 4,
        OutputSampleRateHz: 14_318_181.818181818,
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
        linePeriodUs: 100.4,
        hsyncPulseUs: 10.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 20.0);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, converter),
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true);

    double samplesPerUsec = (double)InvokePrivateMethod(
        pipeline,
        "LaserDiscLineSamplesPerUsec",
        new double[] { 0.0, 120.0, 240.0 },
        0,
        (int?)2)!;

    AssertClose(1.2, samplesPerUsec, 1e-12);
}

[Fact(DisplayName = "TBC field decode determines PAL eight-field burst phase transactionally")]
public void TbcFieldDecodeDeterminesPalBurstPhaseTransactionally()
{
    const double fscMHz = 4.43361875;
    const double sampleRateHz = 40_000_000.0;
    const int lineSamples = 2_560;
    const int lineCount = 326;
    var spec = new TbcFrameSpec(
        "PAL",
        OutputLineLength: 1_135,
        OutputLineCount: 313,
        OutputSampleRateHz: fscMHz * 4_000_000.0,
        ColourBurstStart: 98,
        ColourBurstEnd: 138,
        ActiveVideoStart: 185,
        ActiveVideoEnd: 1_107);
    var converter = new VideoOutputConverter(
        ire0: 10_000.0,
        hzIre: 100.0,
        outputZero: 256,
        vsyncIre: -42.857142857142854,
        outputScale: 1.0);
    var analyzer = new SyncAnalyzer(
        sampleRateHz,
        linePeriodUs: 64.0,
        hsyncPulseUs: 4.7,
        equalizingPulseUs: 2.35,
        vsyncPulseUs: 27.3,
        numPulses: 5);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, converter),
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        decodeType: "cvbs",
        decodeVbiData: true);
    var locations = new LineLocationResult(
        Enumerable.Range(0, lineCount)
            .Select(line => (line * (double)lineSamples) - 0.46964792488)
            .ToArray(),
        new bool[lineCount]);

    static RfDecodedSpan BuildBurstSpan(int regularLineOrigin, double fsc)
    {
        double[] video = Enumerable.Repeat(10_000.0, lineCount * lineSamples).ToArray();
        double[] burst = new double[video.Length];
        for (int line = 0; line < lineCount; line++)
        {
            int phaseIndex = ((line - regularLineOrigin) % 4 + 4) % 4;
            double phase = phaseIndex * Math.PI / 2.0;
            int lineStart = line * lineSamples;
            const int burstStart = 224;
            const int burstEnd = 314;
            for (int offset = burstStart; offset < burstEnd; offset++)
            {
                double value = 900.0 * Math.Sin(
                    (Math.Tau * fsc * (offset - burstStart) / 40.0) + phase);
                video[lineStart + offset] += value;
                burst[lineStart + offset] = value;
            }
        }

        return new RfDecodedSpan(0, [], video, video, VideoBurst: burst);
    }

    RfDecodedSpan firstSpan = BuildBurstSpan(regularLineOrigin: 8, fscMHz);
    RfDecodedSpan secondSpan = BuildBurstSpan(regularLineOrigin: 9, fscMHz);
    int? Analyze(RfDecodedSpan span, bool firstField) => (int?)InvokePrivateMethod(
        pipeline,
        "DetermineLaserDiscPalFieldPhase",
        span,
        locations,
        (bool?)firstField,
        (double?)9.0,
        converter);

    AssertEqual<int?>(7, Analyze(firstSpan, firstField: true));
    TbcFieldDecodeState afterFirst = pipeline.CaptureState();
    AssertEqual<int?>(2, Analyze(secondSpan, firstField: false));
    TbcFieldDecodeState afterSecond = pipeline.CaptureState();
    AssertEqual<int?>(2, afterSecond.PreviousLaserDiscPalFieldPhaseId);
    AssertEqual(4, afterSecond.PreviousLaserDiscPalPhaseAdjustments!.Count);

    pipeline.RestoreStateForRetry(afterFirst);
    AssertEqual<int?>(2, Analyze(secondSpan, firstField: false));
    RfDecodedSpan missingPhase = secondSpan with { VideoBurst = new double[secondSpan.Video.Length] };
    AssertEqual<int?>(3, Analyze(missingPhase, firstField: true));
}

[Fact(DisplayName = "TBC field decode pipeline refines LD NTSC burst line locations")]
public void TbcFieldDecodePipelineRefinesLdNtscBurstLineLocations()
{
    const double fscMHz = 0.4;
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 20,
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
    var renderer = new TbcFieldRenderer(spec, converter);
    TbcFieldDecodePipeline CreatePipeline() => new(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true,
        laserDiscNtscBurstRefineOptions: new LaserDiscNtscBurstRefineOptions(FscMHz: fscMHz));

    double[] video = Enumerable.Repeat(0.0, 2_400).ToArray();
    for (int line = 0; line < 23; line++)
    {
        int pulseLength = line is 3 or 4 ? 20 : 10;
        PaintPulse(video, 10 + (line * 100), pulseLength, -40.0);
    }

    double[] burst = new double[video.Length];
    for (int line = 0; line < 22; line++)
    {
        if (line is 3 or 4)
        {
            continue;
        }

        int lineStart = 10 + (line * 100);
        for (int offset = 45; offset < 80; offset++)
        {
            int index = lineStart + offset;
            if (index < burst.Length)
            {
                burst[index] = 5.0 * Math.Sin(Math.Tau * ((fscMHz * offset) + 0.17));
            }
        }
    }

    TbcDecodedField plain = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled).Decode(new RfDecodedSpan(0, [], video, video));
    TbcDecodedField preBurst = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true).Decode(new RfDecodedSpan(0, [], video, video));
    TbcDecodedField shiftOnly = CreatePipeline().Decode(new RfDecodedSpan(
        0,
        [],
        video,
        video,
        VideoBurst: new double[video.Length]));
    TbcDecodedField refined = CreatePipeline().Decode(new RfDecodedSpan(0, [], video, video, VideoBurst: burst));

    AssertEqual(spec.OutputLineCount + 10, refined.LineLocations.Locations.Length);
    AssertClose(1210.0, plain.LineLocations.Locations[12], 1e-12);
    double expectedFscPhaseShift = ((117.25 / 360.0) / fscMHz) * analyzer.SampleRateMHz;
    AssertClose(
        plain.LineLocations.Locations[12] - expectedFscPhaseShift,
        shiftOnly.LineLocations.Locations[12],
        1e-12);
    AssertTrue(Math.Abs(refined.LineLocations.Locations[12] - plain.LineLocations.Locations[12]) > 0.05);
    AssertClose(preBurst.MedianBurstIre!.Value, refined.MedianBurstIre!.Value, 1e-12);
    AssertEqual<bool?>(true, refined.DetectedFirstField);
    AssertEqual(0, refined.DetectedFirstFieldConfidence);
    AssertEqual<int?>(1, refined.FieldPhaseId);
    var probeLocations = new LineLocationResult([10.0, 20.0], [false, false]);
    var shiftedProbe = (LineLocationResult)InvokePrivateMethod(
        CreatePipeline(),
        "ApplyNtscFscPhaseShiftCore",
        probeLocations,
        fscMHz)!;
    AssertSequence(
        [10.0 - expectedFscPhaseShift, 20.0 - expectedFscPhaseShift],
        shiftedProbe.Locations);
}

[Fact(DisplayName = "LD NTSC burst search matches Release 4.0 zero-crossing limit")]
public void LaserDiscNtscBurstSearchMatchesRelease40ZeroCrossingLimit()
{
    const double fscMHz = 0.25;
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
        hzIre: 1_000.0,
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
        TbcDropoutDetectionOptions.Disabled,
        decodeLaserDiscVbi: true,
        laserDiscNtscBurstRefineOptions: new LaserDiscNtscBurstRefineOptions(fscMHz));

    var burst = new double[300];
    for (int offset = 0; offset < 28; offset++)
    {
        burst[84 + offset] = offset switch
        {
            0 => -10.0,
            >= 20 and <= 24 => 3.0,
            _ => -1.0
        };
    }

    double[] video = burst.ToArray();
    bool found = pipeline.TryComputeLaserDiscLineBurst(
        video,
        burst,
        [0.0, 100.0, 200.0],
        line: 0,
        previousPhaseAdjustment: 0.125,
        fieldLineBoundary: 2,
        fscMHz,
        converter.HzIre,
        out bool rising,
        out double phaseAdjustment);

    AssertTrue(found);
    AssertFalse(rising);
    AssertClose(0.125, phaseAdjustment, 0.0);
}

[Fact(DisplayName = "TBC field decode pipeline emits chroma samples")]
public void TbcFieldDecodePipelineEmitsChromaSamples()
{
    var spec = new TbcFrameSpec(
        "PAL",
        OutputLineLength: 20,
        OutputLineCount: 40,
        OutputSampleRateHz: 4_000_000.0,
        ColourBurstStart: 5,
        ColourBurstEnd: 10,
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
    var chromaOptions = new VhsChromaFieldOptions(
        ColorSystem: "PAL",
        OutputLineLength: spec.OutputLineLength,
        OutputLineCount: spec.OutputLineCount,
        OutputSampleRateHz: spec.OutputSampleRateHz,
        FscMHz: 1.0,
        ColorUnderCarrierHz: 0.0,
        BurstStart: 5,
        BurstEnd: 10,
        BurstAbsRef: 10.0,
        ChromaRotation: null,
        DisableComb: true,
        DisablePhaseCorrection: true,
        EnableColorKiller: false,
        DetectChromaTrackPhase: false);
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, converter),
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        chromaFieldOptions: chromaOptions);

    double[] video = Enumerable.Repeat(0.0, 4_500).ToArray();
    for (int line = 0; line < 43; line++)
    {
        PaintPulse(video, 10 + (line * 100), 10, -40.0);
    }

    double[] chroma = BuildRawChromaCarrier(
        video.Length,
        firstLineStart: 10,
        lineLength: 100,
        lineCount: 43,
        outputLineLength: spec.OutputLineLength,
        fscMHz: chromaOptions.FscMHz,
        outputSampleRateHz: spec.OutputSampleRateHz);
    TbcDecodedField decoded = pipeline.Decode(new RfDecodedSpan(0, [], video, video, Chroma: chroma));

    AssertTrue(decoded.ChromaBurstSamples is not null);
    AssertTrue(decoded.ChromaSamples is not null);
    AssertEqual(spec.FieldSampleCount, decoded.ChromaSamples!.Length);
    int line16 = 16 * spec.OutputLineLength;
    AssertEqual(32746, decoded.ChromaSamples[line16]);
    AssertEqual(32746, decoded.ChromaSamples[line16 + 2]);
}

[Theory(DisplayName = "TBC field decode pipeline applies analyzed VHS track phase to luma")]
[InlineData(0, 0, 456, 0, 656)]
[InlineData(1, 0, 456, 0, 656)]
public void TbcFieldDecodePipelineAppliesAnalyzedVhsTrackPhaseToLuma(
    int initialTrackPhase,
    int firstNextTrackPhase,
    ushort firstExpectedSample,
    int secondNextTrackPhase,
    ushort secondExpectedSample)
{
    var spec = new TbcFrameSpec(
        "PAL",
        OutputLineLength: 20,
        OutputLineCount: 40,
        OutputSampleRateHz: 4_000_000.0,
        ColourBurstStart: 5,
        ColourBurstEnd: 10,
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
        vsyncPulseUs: 20.0,
        numPulses: 5);
    var chromaOptions = new VhsChromaFieldOptions(
        ColorSystem: "PAL",
        OutputLineLength: spec.OutputLineLength,
        OutputLineCount: spec.OutputLineCount,
        OutputSampleRateHz: spec.OutputSampleRateHz,
        FscMHz: 1.0,
        ColorUnderCarrierHz: 0.0,
        BurstStart: 5,
        BurstEnd: 10,
        BurstAbsRef: 10.0,
        ChromaRotation: [0, 3],
        DisableComb: true,
        DisablePhaseCorrection: true,
        EnableColorKiller: false,
        DetectChromaTrackPhase: false)
    {
        DisableBurstHsync = true,
        InitialChromaRotationIndex = initialTrackPhase
    };
    var renderer = new TbcFieldRenderer(
        spec,
        converter,
        trackPhaseIre0Offset: new TrackPhaseIre0OffsetOptions(
            initialTrackPhase,
            Offset0Hz: 20.0,
            Offset1Hz: 0.0));
    var pipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        chromaFieldOptions: chromaOptions,
        decodeType: "vhs");

    double[] video = Enumerable.Repeat(0.0, 6_500).ToArray();
    PaintPulse(video, 10, 10, -40.0);
    PaintPulse(video, 110, 10, -40.0);
    PaintTestVBlank(video, line0: 210, isFirstField: true, system: "PAL");
    for (int line = 11; line <= 60; line++)
    {
        PaintPulse(video, 210 + (line * 100), 10, -40.0);
    }

    double[] chroma = BuildRawChromaCarrier(
        video.Length,
        firstLineStart: 210,
        lineLength: 100,
        lineCount: 61,
        outputLineLength: spec.OutputLineLength,
        fscMHz: chromaOptions.FscMHz,
        outputSampleRateHz: spec.OutputSampleRateHz);
    var firstSpan = new RfDecodedSpan(0, video, video, video, VideoLowPass: video, Chroma: chroma);
    var secondSpan = firstSpan with { StartSample = video.Length };
    int stableLumaSample = (16 * spec.OutputLineLength) + 5;

    TbcDecodedField first = pipeline.Decode(firstSpan, fieldNumber: 0);
    AssertEqual<int?>(firstNextTrackPhase, pipeline.CaptureState().ChromaRotationIndex);
    AssertEqual(firstExpectedSample, first.Samples[stableLumaSample]);

    TbcDecodedField second = pipeline.Decode(secondSpan, fieldNumber: 1);
    AssertEqual<int?>(secondNextTrackPhase, pipeline.CaptureState().ChromaRotationIndex);
    AssertEqual(secondExpectedSample, second.Samples[stableLumaSample]);
}

static double[] BuildOutputChromaCarrier(int lineLength, int lineCount, double fscMHz, double outputSampleRateHz)
{
    var chroma = new double[checked(lineLength * lineCount)];
    double cyclesPerSample = fscMHz / (outputSampleRateHz / 1_000_000.0);
    for (int line = 0; line < lineCount; line++)
    {
        int lineStart = line * lineLength;
        for (int sample = 0; sample < lineLength; sample++)
        {
            chroma[lineStart + sample] = Math.Cos(Math.Tau * cyclesPerSample * sample);
        }
    }

    return chroma;
}

static double[] BuildRawChromaCarrier(
    int sampleCount,
    int firstLineStart,
    int lineLength,
    int lineCount,
    int outputLineLength,
    double fscMHz,
    double outputSampleRateHz)
{
    var chroma = new double[sampleCount];
    double cyclesPerOutputSample = fscMHz / (outputSampleRateHz / 1_000_000.0);
    for (int line = 0; line < lineCount; line++)
    {
        int start = firstLineStart + (line * lineLength);
        int end = Math.Min(sampleCount, start + lineLength);
        for (int sample = Math.Max(0, start); sample < end; sample++)
        {
            double outputSample = (sample - start) * (double)outputLineLength / lineLength;
            chroma[sample] = Math.Cos(Math.Tau * cyclesPerOutputSample * outputSample);
        }
    }

    return chroma;
}

[Fact(DisplayName = "TBC field decode pipeline downscales LD analog audio")]
public void TbcFieldDecodePipelineDownscalesLdAnalogAudio()
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
    var pipeline = new TbcFieldDecodePipeline(
        new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0),
        new TbcFieldRenderer(spec, converter),
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        analogAudioOptions: new LaserDiscAnalogAudioOutputOptions(
            LinePeriodUs: 100.0,
            LineCount: 2,
            OutputFrequency: 10_000.0,
            LeftCarrierHz: 1_000.0,
            RightCarrierHz: 2_000.0));

    double[] video = Enumerable.Repeat(0.0, 360).ToArray();
    PaintPulse(video, 10, 10, -40.0);
    PaintPulse(video, 110, 10, -40.0);
    PaintPulse(video, 210, 10, -40.0);
    double leftOffset = -371_081.0 * 1000.0 / 32767.0;
    double rightOffset = 371_081.0 * 500.0 / 32767.0;
    double[] left = Enumerable.Repeat(1_000.0 + leftOffset, 40).ToArray();
    double[] right = Enumerable.Repeat(2_000.0 + rightOffset, 40).ToArray();
    var span = new RfDecodedSpan(
        StartSample: 0,
        Input: [],
        Video: video,
        DemodRaw: video,
        AnalogAudio: new LaserDiscAnalogAudioBlock(left, right, DecimationFactor: 10));

    TbcDecodedField decoded = pipeline.Decode(span);
    AssertTrue(decoded.AudioPcm is not null);
    AssertEqual(4, decoded.AudioPcm!.Length);
    AssertEqual((short)1000, decoded.AudioPcm[0]);
    AssertEqual((short)-500, decoded.AudioPcm[1]);

    var paritySpec = spec with { OutputLineCount = 12 };
    var parityPipeline = new TbcFieldDecodePipeline(
        new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0,
            numPulses: 6),
        new TbcFieldRenderer(paritySpec, converter),
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        analogAudioOptions: new LaserDiscAnalogAudioOutputOptions(
            LinePeriodUs: 100.0,
            LineCount: 12,
            OutputFrequency: 10_000.0,
            LeftCarrierHz: 1_000.0,
            RightCarrierHz: 2_000.0));
    double[] secondFieldVideo = Enumerable.Repeat(0.0, 3_000).ToArray();
    PaintPulse(secondFieldVideo, 10, 10, -40.0);
    PaintPulse(secondFieldVideo, 110, 10, -40.0);
    PaintTestVBlank(secondFieldVideo, line0: 210, isFirstField: false, system: "NTSC");
    foreach (int line in Enumerable.Range(0, 14))
    {
        PaintPulse(secondFieldVideo, 1_210 + (line * 100), 10, -40.0);
    }

    double[] parityLeft = Enumerable.Repeat(1_000.0, 300).ToArray();
    double[] parityRight = Enumerable.Repeat(2_000.0, 300).ToArray();
    TbcDecodedField secondAudioField = parityPipeline.Decode(new RfDecodedSpan(
        StartSample: 0,
        Input: [],
        Video: secondFieldVideo,
        DemodRaw: secondFieldVideo,
        Efm: Enumerable.Range(0, secondFieldVideo.Length).Select(value => (short)value).ToArray(),
        AnalogAudio: new LaserDiscAnalogAudioBlock(parityLeft, parityRight, DecimationFactor: 10)));
    AssertEqual<bool?>(false, secondAudioField.DetectedFirstField);
    AssertEqual(22, secondAudioField.AudioPcm!.Length);
    int secondEfmStart = (int)secondAudioField.LineLocations.Locations[1];
    int secondEfmEnd = (int)secondAudioField.LineLocations.Locations[12];
    AssertEqual(secondEfmEnd - secondEfmStart, secondAudioField.Efm!.Length);
    AssertEqual((short)(secondEfmEnd - 1), secondAudioField.Efm[^1]);
}

[Fact(DisplayName = "LD analog audio timing matches upstream field alignment")]
public void LaserDiscAnalogAudioTimingMatchesUpstreamFieldAlignment()
{
    long skippedFieldNumber = LaserDiscAnalogAudioTiming.EstimateFieldNumber(
        previousFieldNumber: 0,
        previousReadLocation: 1_000,
        currentReadLocation: 11_000,
        sampleRateHz: 1_000_000.0,
        framesPerSecond: 100.0);
    AssertEqual(2L, skippedFieldNumber);

    long tieToEven = LaserDiscAnalogAudioTiming.EstimateFieldNumber(
        previousFieldNumber: 0,
        previousReadLocation: 1_000,
        currentReadLocation: 3_500,
        sampleRateHz: 1_000_000.0,
        framesPerSecond: 100.0);
    AssertEqual(0L, tieToEven);

    double secondFieldOffset = LaserDiscAnalogAudioTiming.ComputeTimeOffset(
        fieldNumber: 1,
        isFirstField: false,
        firstFieldLines: 12,
        secondFieldLines: 11,
        linePeriodUs: 100.0,
        outputFrequency: 44_100.0);
    AssertClose(0.08 / 44_100.0, secondFieldOffset, 1e-15);

    double nextFirstFieldOffset = LaserDiscAnalogAudioTiming.ComputeTimeOffset(
        fieldNumber: 2,
        isFirstField: true,
        firstFieldLines: 12,
        secondFieldLines: 11,
        linePeriodUs: 100.0,
        outputFrequency: 44_100.0);
    AssertClose(-0.43 / 44_100.0, nextFirstFieldOffset, 1e-15);
    AssertClose(0.0, LaserDiscAnalogAudioTiming.ComputeTimeOffset(
        fieldNumber: 3,
        isFirstField: false,
        firstFieldLines: 12,
        secondFieldLines: 11,
        linePeriodUs: 100.0,
        outputFrequency: 10_000.0), 1e-15);
}

[Fact(DisplayName = "TBC field decode pipeline builds LD RF TBC")]
public void TbcFieldDecodePipelineBuildsLdRfTbc()
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
        TbcDropoutDetectionOptions.Disabled,
        rfTbcOptions: new LaserDiscRfTbcOptions(WriteRfTbc: true, VideoWhiteOffsetSamples: 5));

    double[] video = Enumerable.Repeat(0.0, 700).ToArray();
    PaintPulse(video, 10, 10, -40.0);
    PaintPulse(video, 110, 10, -40.0);
    PaintPulse(video, 210, 10, -40.0);
    double[] input = Enumerable.Range(0, video.Length).Select(value => (double)value).ToArray();

    TbcDecodedField decoded = pipeline.Decode(new RfDecodedSpan(0, input, video, video));
    AssertTrue(decoded.RfTbc is not null);
    AssertEqual(200, decoded.RfTbc!.Length);
    AssertEqual((short)5, decoded.RfTbc[0]);
    AssertEqual((short)104, decoded.RfTbc[99]);
    AssertEqual((short)105, decoded.RfTbc[100]);

    short[] nominalLength = (short[])InvokePrivateMethod(
        pipeline,
        "BuildRfTbc",
        input,
        new double[] { 10.0, 130.0, 250.0 },
        2)!;
    AssertEqual(200, nominalLength.Length);
    AssertEqual((short)5, nominalLength[0]);
    AssertEqual((short)124, nominalLength[99]);
    AssertEqual((short)125, nominalLength[100]);

    TbcDecodedField fractionalDelay = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, converter),
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        rfTbcOptions: new LaserDiscRfTbcOptions(WriteRfTbc: true, VideoWhiteOffsetSamples: 5.5))
        .Decode(new RfDecodedSpan(0, input, video, video));
    AssertEqual((short)4, fractionalDelay.RfTbc![0]);

    var palPipeline = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec with { System = "PAL" }, converter),
        converter,
        "PAL",
        TbcDropoutDetectionOptions.Disabled,
        rfTbcOptions: new LaserDiscRfTbcOptions(WriteRfTbc: true, VideoWhiteOffsetSamples: 0));
    TbcDecodedField palDecoded = palPipeline.Decode(new RfDecodedSpan(0, input, video, video));
    AssertEqual(100, palDecoded.RfTbc!.Length);
    AssertEqual((short)160, palDecoded.RfTbc![0]);

    double[] overflowingInput = Enumerable.Repeat(40_000.0, video.Length).ToArray();
    TbcDecodedField wrapped = new TbcFieldDecodePipeline(
        analyzer,
        new TbcFieldRenderer(spec, converter),
        converter,
        "NTSC",
        TbcDropoutDetectionOptions.Disabled,
        rfTbcOptions: new LaserDiscRfTbcOptions(WriteRfTbc: true, VideoWhiteOffsetSamples: 0))
        .Decode(new RfDecodedSpan(0, overflowingInput, video, video));
    AssertEqual(unchecked((short)40_000), wrapped.RfTbc![0]);
}

[Fact(DisplayName = "TBC field decode pipeline detects demod-level dropouts")]
public void TbcFieldDecodePipelineDetectsDemodLevelDropouts()
{
    var spec = new TbcFrameSpec(
        "NTSC",
        OutputLineLength: 4,
        OutputLineCount: 12,
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
    var renderer = new TbcFieldRenderer(spec, converter);
    var analyzer = new SyncAnalyzer(
        sampleRateHz: 1_000_000.0,
        linePeriodUs: 100.0,
        hsyncPulseUs: 15.0,
        equalizingPulseUs: 5.0,
        vsyncPulseUs: 25.0);
    TbcFieldDecodePipeline CreateLaserDiscPipeline(int rfHighPassOffset = 0) => new(
        analyzer,
        renderer,
        converter,
        "NTSC",
        new TbcDropoutDetectionOptions(
            Enabled: true,
            ThresholdFraction: 0.18,
            AbsoluteThreshold: null,
            Hysteresis: 2.0,
            Mode: TbcDropoutDetectionMode.LaserDiscDemod),
        rfHighPassOffset);

    double[] baseVideo = Enumerable.Repeat(0.0, 1_400).ToArray();
    PaintPulse(baseVideo, 10, 15, -40.0);
    PaintPulse(baseVideo, 110, 15, -40.0);
    PaintPulse(baseVideo, 210, 15, -40.0);

    double[] syncDipVideo = baseVideo.ToArray();
    for (int i = 112; i < 124; i++)
    {
        syncDipVideo[i] = -70.0;
    }

    TbcDecodedField syncDipDecoded = CreateLaserDiscPipeline().Decode(
        new RfDecodedSpan(0, [], syncDipVideo, new double[syncDipVideo.Length]));
    AssertEqual(0, syncDipDecoded.Dropouts!.Count);

    double[] activeLowVideo = baseVideo.ToArray();
    for (int i = 1_050; i < 1_066; i++)
    {
        activeLowVideo[i] = -60.0;
    }

    TbcDecodedField activeLowDecoded = CreateLaserDiscPipeline().Decode(
        new RfDecodedSpan(0, [], activeLowVideo, new double[activeLowVideo.Length]));
    AssertEqual(1, activeLowDecoded.Dropouts!.Count);
    AssertIntSequence([10], activeLowDecoded.Dropouts.FieldLine);
    AssertIntSequence([1], activeLowDecoded.Dropouts.StartX);
    AssertIntSequence([2], activeLowDecoded.Dropouts.EndX);

    double[] rawDemod = new double[baseVideo.Length];
    for (int i = 1_050; i < 1_066; i++)
    {
        rawDemod[i] = 600_000.0;
    }

    TbcDecodedField rawDemodDecoded = CreateLaserDiscPipeline().Decode(
        new RfDecodedSpan(0, [], baseVideo, rawDemod));
    AssertEqual(1, rawDemodDecoded.Dropouts!.Count);
    AssertIntSequence([10], rawDemodDecoded.Dropouts.FieldLine);
    AssertIntSequence([1], rawDemodDecoded.Dropouts.StartX);
    AssertIntSequence([2], rawDemodDecoded.Dropouts.EndX);

    double[] videoLowPass = baseVideo.ToArray();
    for (int i = 1_050; i < 1_066; i++)
    {
        videoLowPass[i] = 120.0;
    }

    TbcDecodedField lowPassDecoded = CreateLaserDiscPipeline().Decode(new RfDecodedSpan(
        0,
        [],
        baseVideo,
        new double[baseVideo.Length],
        VideoLowPass: videoLowPass));
    AssertEqual(1, lowPassDecoded.Dropouts!.Count);
    AssertIntSequence([10], lowPassDecoded.Dropouts.FieldLine);
    AssertIntSequence([1], lowPassDecoded.Dropouts.StartX);
    AssertIntSequence([2], lowPassDecoded.Dropouts.EndX);

    double[] rfHighPass = new double[baseVideo.Length];
    for (int i = 1_050; i < 1_066; i++)
    {
        rfHighPass[i] = 100.0;
    }

    TbcDecodedField rfHighPassDecoded = CreateLaserDiscPipeline().Decode(new RfDecodedSpan(
        0,
        [],
        baseVideo,
        new double[baseVideo.Length],
        RfHighPass: rfHighPass));
    AssertEqual(1, rfHighPassDecoded.Dropouts!.Count);
    AssertIntSequence([10], rfHighPassDecoded.Dropouts.FieldLine);
    AssertIntSequence([1], rfHighPassDecoded.Dropouts.StartX);
    AssertIntSequence([2], rfHighPassDecoded.Dropouts.EndX);

    TbcFieldDecodePipeline offsetPipeline = CreateLaserDiscPipeline(rfHighPassOffset: 5);
    double[] shiftedRfHighPass = new double[baseVideo.Length];
    for (int i = 1_045; i < 1_061; i++)
    {
        shiftedRfHighPass[i] = 100.0;
    }

    TbcDecodedField shiftedRfHighPassDecoded = offsetPipeline.Decode(new RfDecodedSpan(
        0,
        [],
        baseVideo,
        new double[baseVideo.Length],
        RfHighPass: shiftedRfHighPass));
    AssertEqual(1, shiftedRfHighPassDecoded.Dropouts!.Count);
    AssertIntSequence([10], shiftedRfHighPassDecoded.Dropouts.FieldLine);
    AssertIntSequence([1], shiftedRfHighPassDecoded.Dropouts.StartX);
    AssertIntSequence([2], shiftedRfHighPassDecoded.Dropouts.EndX);

    double[] lowEnvelope = Enumerable.Repeat(10.0, baseVideo.Length).ToArray();
    for (int i = 1_050; i < 1_066; i++)
    {
        lowEnvelope[i] = 0.0;
    }

    TbcDecodedField ldIgnoresEnvelope = CreateLaserDiscPipeline().Decode(new RfDecodedSpan(
        0,
        [],
        baseVideo,
        new double[baseVideo.Length],
        Envelope: lowEnvelope));
    AssertEqual(0, ldIgnoresEnvelope.Dropouts!.Count);

    var tapePipeline = new TbcFieldDecodePipeline(
        analyzer,
        renderer,
        converter,
        "NTSC",
        new TbcDropoutDetectionOptions(
            Enabled: true,
            ThresholdFraction: 0.18,
            AbsoluteThreshold: null,
            Hysteresis: 2.0,
            Mode: TbcDropoutDetectionMode.TapeEnvelope));
    TbcDecodedField tapeIgnoresDemod = tapePipeline.Decode(new RfDecodedSpan(
        0,
        [],
        baseVideo,
        rawDemod,
        Envelope: Enumerable.Repeat(10.0, baseVideo.Length).ToArray()));
    AssertEqual(0, tapeIgnoresDemod.Dropouts!.Count);

    double[] nanDemod = Enumerable.Repeat(double.NaN, baseVideo.Length).ToArray();
    double[] nanRfHighPass = Enumerable.Repeat(double.NaN, baseVideo.Length).ToArray();
    double[] nanLowPass = baseVideo.ToArray();
    for (int i = 1_050; i < 1_066; i++)
    {
        nanLowPass[i] = double.NaN;
    }

    TbcDecodedField ldKeepsNumpyNanComparisonSemantics = CreateLaserDiscPipeline().Decode(new RfDecodedSpan(
        0,
        [],
        baseVideo,
        nanDemod,
        VideoLowPass: nanLowPass,
        RfHighPass: nanRfHighPass));
    AssertEqual(0, ldKeepsNumpyNanComparisonSemantics.Dropouts!.Count);
}

[Fact(DisplayName = "TBC first-field engine writes output artifacts")]
public void TbcFirstFieldEngineWritesOutputArtifacts()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.u8");
        string outputBase = Path.Combine(tempDirectory, "capture");
        File.WriteAllBytes(inputPath, []);
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--pal", "--level_adjust", "0.25", inputPath, outputBase]));

        ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        samples[0] = 0x1234;
        samples[^1] = 0xABCD;
        (int whiteLine, double whiteStart, double whiteLength) = FirstVitsSlice(session.Parameters.SysParams, "LD_VITS_whitelocs");
        ushort whiteOutput = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(100.0));
        PaintTbcMetricSlice(samples, session, whiteLine, whiteStart, whiteLength, i => (ushort)(whiteOutput + (i % 2 == 0 ? -8 : 8)));
        (int blackLine, double blackStart, double blackLength) = FirstVitsSlice(session.Parameters.SysParams, "blacksnr_slice");
        ushort blackOutput = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(0.0));
        PaintTbcMetricSlice(samples, session, blackLine, blackStart, blackLength, i => (ushort)(blackOutput + (i % 2 == 0 ? -6 : 6)));
        TbcDecodedField field = BuildSyntheticTbcField(startSample: 42, samples: samples, rawPulseCount: 7, classifiedPulseCount: 6)
            with { SyncConfidence = 45 };

        var engine = new TbcFirstFieldDecodeEngine();
        TbcFirstFieldDecodeResult result = engine.WriteDecodedField(session, field);

        AssertTrue(result.Success);
        AssertEqual(outputBase + ".tbc", result.Paths!.TbcPath);
        AssertEqual(outputBase + ".tbc.json", result.Paths.JsonPath);
        AssertEqual(outputBase + "_chroma.tbc", result.Paths.ChromaPath);
        AssertFalse(File.Exists(result.Paths.ChromaPath!));
        string compactJson = File.ReadAllText(result.Paths.JsonPath);
        AssertTrue(compactJson.StartsWith("{\"pcmAudioParameters\":", StringComparison.Ordinal));
        AssertFalse(compactJson.Contains(Environment.NewLine + " ", StringComparison.Ordinal));
        AssertTrue(compactJson.Contains("\"sampleRate\":0", StringComparison.Ordinal));
        AssertTrue(compactJson.Contains("\"diskLoc\":0.0", StringComparison.Ordinal));
        AssertTrue(compactJson.Contains(
            "\"fields\":[{\"isFirstField\":true,\"detectedFirstField\":true,\"isDuplicateField\":false,\"burstStartLine\":0,\"syncConf\":45,\"seqNo\":1",
            StringComparison.Ordinal));
        AssertEqual(samples.Length * 2L, new FileInfo(result.Paths.TbcPath).Length);
        byte[] bytes = File.ReadAllBytes(result.Paths.TbcPath);
        AssertEqual((byte)0x34, bytes[0]);
        AssertEqual((byte)0x12, bytes[1]);
        AssertEqual((byte)0xCD, bytes[^2]);
        AssertEqual((byte)0xAB, bytes[^1]);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths.JsonPath));
        JsonElement root = document.RootElement;
        JsonElement pcmAudio = root.GetProperty("pcmAudioParameters");
        AssertEqual(16, JsonInt(pcmAudio, "bits"));
        AssertTrue(pcmAudio.GetProperty("isLittleEndian").GetBoolean());
        AssertTrue(pcmAudio.GetProperty("isSigned").GetBoolean());
        AssertClose(0.0, JsonDouble(pcmAudio, "sampleRate"), 1e-12);
        JsonElement video = root.GetProperty("videoParameters");
        AssertEqual(1, JsonInt(video, "numberOfSequentialFields"));
        AssertEqual(DecodeVersionInfo.Version, video.GetProperty("version").GetString());
        AssertFalse(string.IsNullOrWhiteSpace(video.GetProperty("osInfo").GetString()));
        AssertEqual("vhs_decode", video.GetProperty("gitBranch").GetString());
        AssertEqual("g4315520", video.GetProperty("gitCommit").GetString());
        AssertEqual("PAL", video.GetProperty("system").GetString());
        AssertFalse(video.TryGetProperty("decoder", out _));
        AssertEqual("VHS", video.GetProperty("tapeFormat").GetString());
        AssertEqual(session.TbcFrameSpec.OutputLineLength, JsonInt(video, "fieldWidth"));
        AssertEqual(session.TbcFrameSpec.OutputLineCount, JsonInt(video, "fieldHeight"));
        AssertClose(session.TbcFrameSpec.OutputSampleRateHz, JsonDouble(video, "sampleRate"), 1e-6);
        AssertEqual(0.0, session.BlackIre);
        double unadjustedBlack = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(session.BlackIre));
        double unadjustedWhite = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(100.0));
        double unadjustedBlanking = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(0.0));
        AssertClose(0.25, session.LevelAdjust, 1e-12);
        AssertClose(unadjustedBlack * 0.75, JsonDouble(video, "black16bIre"), 1e-6);
        AssertClose(unadjustedWhite * 1.25, JsonDouble(video, "white16bIre"), 1e-6);
        AssertClose(unadjustedBlanking, JsonDouble(video, "blanking16bIre"), 1e-6);

        JsonElement fieldInfo = root.GetProperty("fields")[0];
        AssertEqual(42, JsonInt(fieldInfo, "fileLoc"));
        AssertEqual(45, JsonInt(fieldInfo, "syncConf"));
        AssertEqual(0, JsonInt(fieldInfo, "burstStartLine"));
        AssertClose(0.0, JsonDouble(fieldInfo, "diskLoc"), 1e-12);
        AssertEqual(1, JsonInt(fieldInfo, "fieldPhaseID"));
        JsonElement vitsMetrics = fieldInfo.GetProperty("vitsMetrics");
        AssertEqual(JsonValueKind.Object, vitsMetrics.ValueKind);
        AssertTrue(JsonDouble(vitsMetrics, "wSNR") > 0.0);
        AssertTrue(JsonDouble(vitsMetrics, "bPSNR") > 0.0);
        double quantizedBlackPsnr = JsonDouble(vitsMetrics, "bPSNR");
        var rawMetricSamples = Enumerable.Repeat(3_600_000.0, samples.Length).ToArray();
        PaintTbcMetricSlice(
            rawMetricSamples,
            session,
            blackLine,
            blackStart,
            blackLength,
            i => i % 2 == 0 ? 2_600_000.0 : 4_600_000.0);
        string rawMetricJsonPath = Path.Combine(tempDirectory, "raw-metrics.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            session,
            [field with
            {
                OutputPayload = new TbcOutputPayload(
                    TbcOutputWriter.ToLittleEndianFloat32Bytes(rawMetricSamples),
                    TbcOutputSampleFormat.Float32)
            }],
            rawMetricJsonPath);
        using JsonDocument rawMetricDocument = JsonDocument.Parse(File.ReadAllText(rawMetricJsonPath));
        double rawBlackPsnr = JsonDouble(
            rawMetricDocument.RootElement.GetProperty("fields")[0].GetProperty("vitsMetrics"),
            "bPSNR");
        AssertTrue(rawBlackPsnr < quantizedBlackPsnr);
        AssertFalse(fieldInfo.TryGetProperty("rawPulseCount", out _));
        AssertFalse(fieldInfo.TryGetProperty("classifiedPulseCount", out _));
        AssertFalse(fieldInfo.TryGetProperty("meanLineLength", out _));
        AssertFalse(fieldInfo.TryGetProperty("syncThresholdHz", out _));

        double framesPerSecond = session.Parameters.SysParams.GetProperty("FPS").GetDouble();
        long upstreamFieldSamples = ((long)(session.DecodeSampleRateHz / (framesPerSecond * 2.0))) + 1;
        string longLocationJsonPath = Path.Combine(tempDirectory, "long-location.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(
                startSample: upstreamFieldSamples * 100,
                samples: Array.Empty<ushort>())],
            longLocationJsonPath);
        using JsonDocument longLocationDocument = JsonDocument.Parse(File.ReadAllText(longLocationJsonPath));
        AssertClose(
            100.0,
            JsonDouble(longLocationDocument.RootElement.GetProperty("fields")[0], "diskLoc"),
            1e-12);

        TbcOutputPaths explicitTbc = TbcFirstFieldDecodeEngine.BuildOutputPaths(Path.Combine(tempDirectory, "explicit.tbc"));
        AssertEqual(Path.Combine(tempDirectory, "explicit.tbc.tbc"), explicitTbc.TbcPath);
        AssertEqual(Path.Combine(tempDirectory, "explicit.tbc.tbc.json"), explicitTbc.JsonPath);
        AssertEqual(Path.Combine(tempDirectory, "explicit.tbc_chroma.tbc"), explicitTbc.ChromaPath);

        DecodeSession oldRawChromaSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--orc",
            "input.u8",
            Path.Combine(tempDirectory, "oldraw")
        ]));
        TbcOutputPaths oldRawChroma = TbcFirstFieldDecodeEngine.BuildOutputPaths(oldRawChromaSession);
        AssertEqual(Path.Combine(tempDirectory, "oldraw.tbcy"), oldRawChroma.TbcPath);
        AssertEqual(Path.Combine(tempDirectory, "oldraw.tbc.json"), oldRawChroma.JsonPath);
        AssertEqual(Path.Combine(tempDirectory, "oldraw.tbcc"), oldRawChroma.ChromaPath);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC first-field engine writes raw float payloads")]
public void TbcFirstFieldEngineWritesRawFloatPayloads()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "capture");
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--export_raw_tbc",
            "input.u8",
            outputBase
        ]));

        ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        double[] raw = new double[session.TbcFrameSpec.FieldSampleCount];
        raw[0] = 1.5;
        raw[^1] = -2.25;
        TbcDecodedField field = BuildSyntheticTbcField(startSample: 42, samples: samples)
            with
            {
                OutputPayload = new TbcOutputPayload(
                    TbcOutputWriter.ToLittleEndianFloat32Bytes(raw),
                    TbcOutputSampleFormat.Float32)
            };

        var engine = new TbcFirstFieldDecodeEngine();
        TbcFirstFieldDecodeResult result = engine.WriteDecodedField(session, field);

        AssertTrue(result.Success);
        AssertEqual(samples.Length * 4L, new FileInfo(result.Paths!.TbcPath).Length);
        double[] written = ReadFloat32Samples(File.ReadAllBytes(result.Paths.TbcPath));
        AssertClose(1.5, written[0], 1e-12);
        AssertClose(-2.25, written[^1], 1e-12);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths.JsonPath));
        JsonElement video = document.RootElement.GetProperty("videoParameters");
        AssertClose((float)session.VideoOutput.IreToHz(session.BlackIre) * 0.9, JsonDouble(video, "black16bIre"), 1e-6);
        AssertClose((float)session.VideoOutput.IreToHz(100.0) * 1.1, JsonDouble(video, "white16bIre"), 1e-6);
        AssertClose((float)session.VideoOutput.IreToHz(0.0), JsonDouble(video, "blanking16bIre"), 1e-6);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine writes multiple fields")]
public void TbcFieldSequenceEngineWritesMultipleFields()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.u8");
        string outputBase = Path.Combine(tempDirectory, "capture.tbc");
        File.WriteAllBytes(inputPath, []);
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--pal", inputPath, outputBase]));

        ushort[] firstSamples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        ushort[] secondSamples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        ushort[] firstChroma = new ushort[session.TbcFrameSpec.FieldSampleCount];
        ushort[] secondChroma = new ushort[session.TbcFrameSpec.FieldSampleCount];
        firstSamples[0] = 0x1111;
        secondSamples[0] = 0x2222;
        firstChroma[0] = 0x3333;
        secondChroma[0] = 0x4444;
        TbcDecodedField first = BuildSyntheticTbcField(
            startSample: 0,
            samples: firstSamples,
            lineLocations: BuildLineLocationsForAdvance(session, 300.0),
            rawPulseCount: 3,
            classifiedPulseCount: 3)
            with
            {
                ChromaSamples = firstChroma
            };
        TbcDecodedField second = BuildSyntheticTbcField(startSample: 360, samples: secondSamples, rawPulseCount: 4, classifiedPulseCount: 4)
            with
            {
                ChromaSamples = secondChroma,
                BurstStartLine = 12
            };
        second = second with { DetectedFirstField = false, DetectedFirstFieldConfidence = 100 };

        var engine = new TbcFieldSequenceDecodeEngine();
        TbcFieldSequenceDecodeResult result = engine.WriteDecodedFields(session, [first, second]);

        AssertTrue(result.Success);
        AssertEqual(outputBase + ".tbc", result.Paths!.TbcPath);
        AssertEqual(outputBase + ".tbc.json", result.Paths.JsonPath);
        AssertEqual(outputBase + "_chroma.tbc", result.Paths.ChromaPath);
        AssertEqual(outputBase + ".tbc.db", result.Paths.DbPath);
        AssertFalse(File.Exists(result.Paths.DbPath!));
        AssertEqual(2, result.Fields.Count);
        AssertEqual(session.TbcFrameSpec.FieldSampleCount * 4L, new FileInfo(result.Paths.TbcPath).Length);
        byte[] bytes = File.ReadAllBytes(result.Paths.TbcPath);
        AssertEqual((byte)0x11, bytes[0]);
        AssertEqual((byte)0x11, bytes[1]);
        AssertEqual((byte)0x22, bytes[session.TbcFrameSpec.FieldSampleCount * 2]);
        AssertEqual((byte)0x22, bytes[(session.TbcFrameSpec.FieldSampleCount * 2) + 1]);
        byte[] chromaBytes = File.ReadAllBytes(result.Paths.ChromaPath!);
        AssertEqual(session.TbcFrameSpec.FieldSampleCount * 4L, chromaBytes.Length);
        AssertEqual((byte)0x33, chromaBytes[0]);
        AssertEqual((byte)0x33, chromaBytes[1]);
        AssertEqual((byte)0x44, chromaBytes[session.TbcFrameSpec.FieldSampleCount * 2]);
        AssertEqual((byte)0x44, chromaBytes[(session.TbcFrameSpec.FieldSampleCount * 2) + 1]);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths.JsonPath));
        JsonElement fields = document.RootElement.GetProperty("fields");
        AssertEqual(2, fields.GetArrayLength());
        AssertEqual(1, JsonInt(fields[0], "seqNo"));
        AssertTrue(fields[0].GetProperty("isFirstField").GetBoolean());
        AssertEqual(2, JsonInt(fields[1], "seqNo"));
        AssertEqual(1, JsonInt(fields[0], "fieldPhaseID"));
        AssertEqual(1, JsonInt(fields[1], "fieldPhaseID"));
        AssertEqual(0, JsonInt(fields[0], "burstStartLine"));
        AssertEqual(12, JsonInt(fields[1], "burstStartLine"));
        AssertFalse(fields[1].GetProperty("isFirstField").GetBoolean());
        AssertFalse(fields[1].GetProperty("detectedFirstField").GetBoolean());
        AssertFalse(fields[1].GetProperty("isDuplicateField").GetBoolean());
        AssertFalse(fields[1].TryGetProperty("decodeFaults", out _));
        AssertEqual(100, JsonInt(fields[1], "syncConf"));
        AssertEqual(360, JsonInt(fields[1], "fileLoc"));

        AssertEqual(300L, TbcFieldSequenceDecodeEngine.EstimateNextFieldStart(session, first));
        TbcFieldSequenceDecodeResult empty = engine.WriteDecodedFields(session, []);
        AssertTrue(empty.Success);
        AssertEqual(0, empty.WrittenFieldCount);
        AssertEqual(0L, new FileInfo(empty.Paths!.TbcPath).Length);
        AssertEqual(0L, new FileInfo(empty.Paths.ChromaPath!).Length);
        AssertFalse(File.Exists(empty.Paths.JsonPath));
        AssertEqual("{", File.ReadAllText(empty.Paths.JsonPath + ".tmp"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine accepts stdin streams")]
public void TbcFieldSequenceEngineAcceptsStdinStreams()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "stdin-capture");
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--no_resample",
            "-",
            outputBase
        ]));
        var engine = new TbcFieldSequenceDecodeEngine(readField: (_, _, _, _, _) => null);
        using var input = new MemoryStream([1, 2, 3, 4]);

        TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(session, input);

        AssertTrue(result.Success);
        AssertEqual(0, result.WrittenFieldCount);
        AssertEqual(0L, new FileInfo(outputBase + ".tbc").Length);
        AssertEqual(0L, new FileInfo(outputBase + "_chroma.tbc").Length);
        AssertFalse(File.Exists(outputBase + ".tbc.json"));
        AssertEqual("{", File.ReadAllText(outputBase + ".tbc.json.tmp"));
        AssertTrue(input.CanRead);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine performs VHS terminal lookahead")]
public void TbcFieldSequenceEnginePerformsVhsTerminalLookahead()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "lookahead");
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--length",
            "1",
            "input.u8",
            outputBase
        ]));
        var statusOutput = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(
            statusOutput,
            new StringWriter(),
            () => 0.0);
        DecodeSessionLogWriter.Write(session);

        int reads = 0;
        int diskChecks = 0;
        var begins = new List<long>();
        TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int ___)
        {
            reads++;
            begins.Add(begin);
            if (reads == 3)
            {
                DecodeSessionLogWriter.Append(activeSession, "DEBUG", "terminal lookahead marker");
            }

            return BuildSyntheticTbcField(
                    begin,
                    new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                    detectedFirstField: (reads & 1) != 0)
                with
                {
                    NextFieldOffsetSamples = 100.0,
                    ChromaSamples = new ushort[activeSession.TbcFrameSpec.FieldSampleCount]
                };
        }

        var diskGuard = new VhsDiskSpaceGuard(
            _ =>
            {
                diskChecks++;
                return long.MaxValue;
            },
            _ => throw new Exception("The disk guard should not wait when space is available."));
        TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine(
            readField: ReadField,
            vhsDiskSpaceGuard: diskGuard).TryDecodeAndWrite(session, Stream.Null);

        if (!result.Success)
        {
            throw new Exception(result.Message);
        }
        AssertEqual(2, result.WrittenFieldCount);
        AssertEqual(3, reads);
        AssertEqual(2, diskChecks);
        AssertTrue(begins.SequenceEqual([0L, 100L, 200L]));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths!.JsonPath));
        AssertEqual(2, document.RootElement.GetProperty("fields").GetArrayLength());

        string log = File.ReadAllText(outputBase + ".log");
        int lookaheadIndex = log.IndexOf("terminal lookahead marker", StringComparison.Ordinal);
        int statusIndex = log.IndexOf("File Frame 0: VHS ", StringComparison.Ordinal);
        AssertTrue(lookaheadIndex >= 0);
        AssertTrue(statusIndex > lookaheadIndex);
        string status = "File Frame 0: VHS ";
        AssertEqual(
            status + new string(' ', 80 - status.Length) + '\r',
            statusOutput.ToString());
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine emits LD frame status")]
public void TbcFieldSequenceEngineEmitsLdFrameStatus()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "ld-status");
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--length", "2",
            "--noEFM",
            "--disable_analog_audio",
            "input.s16",
            outputBase
        ]));
        var statusOutput = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(
            statusOutput,
            new StringWriter(),
            () => 0.0);

        TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int fieldNumber)
        {
            if (fieldNumber >= 4)
            {
                return null;
            }

            int[] vbiData = fieldNumber == 3
                ? [EncodeLaserDiscCavFrameCode(123)]
                : [];
            return BuildSyntheticTbcField(
                    begin,
                    new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                    detectedFirstField: (fieldNumber & 1) == 0)
                with
                {
                    DiskLocation = fieldNumber,
                    FieldPhaseId = fieldNumber + 1,
                    NextFieldOffsetSamples = 100.0,
                    VbiData = vbiData
                };
        }

        var diskGuard = new VhsDiskSpaceGuard(
            _ => throw new Exception("LD recovery snapshots must not query free disk space."));
        TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine(
            readField: ReadField,
            vhsDiskSpaceGuard: diskGuard).TryDecodeAndWrite(session, Stream.Null);

        AssertTrue(result.Success);
        string log = File.ReadAllText(outputBase + ".log");
        AssertTrue(log.Contains(
            "DEBUG - Frame 1/2: File Frame 0: CAV Pulldown/Telecine Frame",
            StringComparison.Ordinal));
        AssertTrue(log.Contains(
            "DEBUG - Frame 2/2: File Frame 1: CAV Frame #123",
            StringComparison.Ordinal));
        string firstStatus = "Frame 1/2: File Frame 0: CAV Pulldown/Telecine Frame";
        string secondStatus = "Frame 2/2: File Frame 1: CAV Frame #123";
        AssertEqual(
            firstStatus + new string(' ', 80 - firstStatus.Length) + '\r'
            + secondStatus + new string(' ', 80 - secondStatus.Length) + '\r',
            statusOutput.ToString());
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "LD frame status formats CLV and persistent lead states")]
public void LaserDiscFrameStatusFormatsClvAndPersistentLeadStates()
{
    var earlyClv = new LaserDiscVbiInterpretation(
        FrameNumber: 4_980,
        IsClv: true,
        IsEarlyClv: true,
        ClvMinutes: 83,
        ClvSeconds: null,
        ClvFrameNumber: null,
        LeadIn: false,
        LeadOut: false);
    AssertEqual(
        "Frame 1/10: File Frame 4: CLV Timecode 83:xx ",
        TbcFieldSequenceDecodeEngine.FormatLaserDiscFrameStatus(1, 10, 4, earlyClv, false, false));
    AssertEqual(
        "Frame 1/10: File Frame 4: CLV Timecode 83:xx ",
        TbcFieldSequenceDecodeEngine.FormatLaserDiscFrameStatus(0, 10, 4, earlyClv, false, false));

    var clv = earlyClv with
    {
        FrameNumber = 149_862,
        IsEarlyClv = false,
        ClvSeconds = 15,
        ClvFrameNumber = 12
    };
    AssertEqual(
        "Frame 2/10: File Frame 5: CLV Timecode 83:15.12 Frame #149862 ",
        TbcFieldSequenceDecodeEngine.FormatLaserDiscFrameStatus(3, 10, 5, clv, false, false));

    var noCode = new LaserDiscVbiInterpretation(null, false, false, null, null, null, false, false);
    AssertEqual(
        "Frame 3/10: File Frame 6: CAV Frame #123 ",
        TbcFieldSequenceDecodeEngine.FormatLaserDiscFrameStatus(
            5,
            10,
            6,
            noCode with { FrameNumber = 123 },
            false,
            false));
    AssertEqual(
        "Frame 3/10: File Frame 6: CAV Lead In",
        TbcFieldSequenceDecodeEngine.FormatLaserDiscFrameStatus(5, 10, 6, noCode, true, true));
    AssertEqual(
        "Frame 3/10: File Frame 6: CAV Lead Out",
        TbcFieldSequenceDecodeEngine.FormatLaserDiscFrameStatus(5, 10, 6, noCode, false, true));
    AssertEqual(
        "Frame 3/10: File Frame 6: CAV Pulldown/Telecine Frame",
        TbcFieldSequenceDecodeEngine.FormatLaserDiscFrameStatus(
            5,
            10,
            6,
            noCode with { FrameNumber = 0 },
            false,
            false));
}

[Fact(DisplayName = "TBC field sequence engine streams fields before decode completes")]
public void TbcFieldSequenceEngineStreamsFieldsBeforeDecodeCompletes()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string batchBase = Path.Combine(tempDirectory, "batch");
        string streamingBase = Path.Combine(tempDirectory, "streaming");
        using DecodeSession batchSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "input.u8",
            batchBase
        ]));
        using DecodeSession streamingSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "input.u8",
            streamingBase
        ]));

        int fieldSampleCount = batchSession.TbcFrameSpec.FieldSampleCount;
        TbcDecodedField[] fields =
        [
            BuildSyntheticTbcField(
                    startSample: 0,
                    samples: Enumerable.Repeat((ushort)0x1111, fieldSampleCount).ToArray(),
                    lineLocations: BuildLineLocationsForAdvance(batchSession, 300.0),
                    detectedFirstField: true)
                with { ChromaSamples = Enumerable.Repeat((ushort)0x3333, fieldSampleCount).ToArray() },
            BuildSyntheticTbcField(
                    startSample: 300,
                    samples: Enumerable.Repeat((ushort)0x2222, fieldSampleCount).ToArray(),
                    lineLocations: BuildLineLocationsForAdvance(batchSession, 300.0),
                    detectedFirstField: false)
                with { ChromaSamples = Enumerable.Repeat((ushort)0x4444, fieldSampleCount).ToArray() }
        ];

        TbcFieldSequenceDecodeResult batch = new TbcFieldSequenceDecodeEngine().WriteDecodedFields(batchSession, fields);
        int reads = 0;
        TbcDecodedField? ReadField(DecodeSession _, Stream __, long ___, int ____, int fieldNumber)
        {
            if (fieldNumber == 1)
            {
                AssertTrue(File.Exists(streamingBase + ".tbc"));
                AssertTrue(File.Exists(streamingBase + ".tbc.json.fields.tmp"));
                AssertEqual(fieldSampleCount * 2L, new FileInfo(streamingBase + ".tbc").Length);
                AssertEqual(fieldSampleCount * 2L, new FileInfo(streamingBase + "_chroma.tbc").Length);
            }

            reads++;
            return fields[fieldNumber];
        }

        var engine = new TbcFieldSequenceDecodeEngine(readField: ReadField);
        TbcFieldSequenceDecodeResult streaming = engine.TryDecodeAndWrite(streamingSession, Stream.Null, maxFields: 2);

        AssertTrue(streaming.Success);
        AssertEqual(2, reads);
        AssertEqual(2, streaming.WrittenFieldCount);
        AssertEqual(0, streaming.Fields.Count);
        AssertTrue(File.ReadAllBytes(batch.Paths!.TbcPath).SequenceEqual(File.ReadAllBytes(streaming.Paths!.TbcPath)));
        AssertTrue(File.ReadAllBytes(batch.Paths.ChromaPath!).SequenceEqual(File.ReadAllBytes(streaming.Paths.ChromaPath!)));
        AssertEqual(File.ReadAllText(batch.Paths.JsonPath), File.ReadAllText(streaming.Paths.JsonPath));
        AssertFalse(File.Exists(streaming.Paths.JsonPath + ".fields.tmp"));
        AssertFalse(File.Exists(streaming.Paths.JsonPath + ".tmp"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "LD auto MTF controller matches upstream scaling")]
public void LaserDiscAutoMtfControllerMatchesUpstreamScaling()
{
    var gentle = new LaserDiscAutoMtfController();
    LaserDiscMtfUpdate gentleUpdate = gentle.Observe(1.4486);
    AssertClose(0.97, gentleUpdate.Level, 1e-12);
    AssertFalse(gentleUpdate.RequiresRetry);

    var controller = new LaserDiscAutoMtfController();
    LaserDiscMtfUpdate first = controller.Observe(1.08);
    AssertClose(1.0, first.PreviousLevel, 0.0);
    AssertClose(0.0, first.Level, 0.0);
    AssertTrue(first.RequiresRetry);
    AssertEqual(1, first.RatioCount);

    LaserDiscMtfUpdate stable = controller.Observe(1.08);
    AssertClose(0.0, stable.Level, 0.0);
    AssertFalse(stable.RequiresRetry);
    for (int i = 0; i < 35; i++)
    {
        controller.Observe(1.46);
    }

    AssertEqual(30, controller.BlackToWhiteRatios.Count);

    var clv = new LaserDiscAutoMtfController();
    clv.ObserveAcceptedField(
        BuildSyntheticTbcField(0, [], detectedFirstField: true) with { VbiData = [0x8AE001] },
        "NTSC");
    clv.ObserveAcceptedField(
        BuildSyntheticTbcField(100, [], detectedFirstField: false) with { VbiData = [0xF0DD01] },
        "NTSC");
    AssertTrue(clv.IsClv);
    for (int i = 0; i < 35; i++)
    {
        clv.Observe(1.46);
    }

    AssertEqual(35, clv.BlackToWhiteRatios.Count);
}

[Fact(DisplayName = "TBC field sequence engine delays gentle LD MTF by one speculative field")]
public void TbcFieldSequenceEngineDelaysGentleLdMtfByOneSpeculativeField()
{
    using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "out"
    ]), blockLength: 4096);
    int mtfBin = FrequencyBin(
        JsonDouble(session.Parameters.RfParams, "MTF_freq") * 1_000_000.0,
        session.DecodeSampleRateHz,
        session.BlockLength);
    Complex[] initialMtf = DecodeFilterSetBuilder.BuildLaserDiscMtf(
        session.Parameters,
        session.FilterOptions,
        targetMtf: 1.0,
        session.DecodeSampleRateHz,
        session.BlockLength);
    Complex[] gentleMtf = DecodeFilterSetBuilder.BuildLaserDiscMtf(
        session.Parameters,
        session.FilterOptions,
        targetMtf: 0.97,
        session.DecodeSampleRateHz,
        session.BlockLength);
    var observedMtf = new List<double>();

    TbcDecodedField? ReadField(
        DecodeSession activeSession,
        Stream _,
        long begin,
        int __,
        int fieldNumber)
    {
        observedMtf.Add(activeSession.Filters.RfMtfMagnitude[mtfBin]);
        return BuildSyntheticTbcField(
                begin,
                new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                detectedFirstField: (fieldNumber & 1) == 0)
            with
            {
                BlackToWhiteRfRatio = 1.4486,
                NextFieldOffsetSamples = 100.0,
                NominalFieldLengthSamples = 100.0
            };
    }

    IReadOnlyList<TbcDecodedField> fields = new TbcFieldSequenceDecodeEngine(
        readField: ReadField).DecodeFields(session, Stream.Null, maxFields: 3);

    AssertEqual(3, fields.Count);
    AssertEqual(3, observedMtf.Count);
    AssertClose(initialMtf[mtfBin].Magnitude, observedMtf[0], 1e-12);
    AssertClose(initialMtf[mtfBin].Magnitude, observedMtf[1], 1e-12);
    AssertClose(gentleMtf[mtfBin].Magnitude, observedMtf[2], 1e-12);
}

[Fact(DisplayName = "TBC field sequence engine retries dynamic LD MTF")]
public void TbcFieldSequenceEngineRetriesDynamicLdMtf()
{
    using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "input.s16",
        "out"
    ]), blockLength: 4096);
    int mtfBin = FrequencyBin(
        JsonDouble(session.Parameters.RfParams, "MTF_freq") * 1_000_000.0,
        session.DecodeSampleRateHz,
        session.BlockLength);
    AssertTrue(session.Filters.RfMtfMagnitude[mtfBin] > 1.0);

    int reads = 0;
    var readBegins = new List<long>();
    TbcDecodedField? ReadField(
        DecodeSession activeSession,
        Stream _,
        long begin,
        int __,
        int ___)
    {
        reads++;
        readBegins.Add(begin);
        if (reads == 1)
        {
            SetPrivateFieldValue(activeSession.TbcFieldDecoder, "_previousFirstHSyncReadLocation", 123L);
            SetPrivateFieldValue(activeSession.TbcFieldDecoder, "_chromaRotationIndex", 3);
        }
        else
        {
            AssertEqual<long?>(null, (long?)PrivateFieldValue(activeSession.TbcFieldDecoder, "_previousFirstHSyncReadLocation"));
            AssertEqual<int?>(null, (int?)PrivateFieldValue(activeSession.TbcFieldDecoder, "_chromaRotationIndex"));
        }

        return BuildSyntheticTbcField(
                begin,
                new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                detectedFirstField: true)
            with
            {
                BlackToWhiteRfRatio = 1.08,
                NextFieldOffsetSamples = 100.0,
                NominalFieldLengthSamples = 100.0
            };
    }

    IReadOnlyList<TbcDecodedField> fields = new TbcFieldSequenceDecodeEngine(
        readField: ReadField).DecodeFields(session, Stream.Null, maxFields: 1);
    AssertEqual(1, fields.Count);
    AssertEqual(2, reads);
    AssertSequence([0.0, 0.0], readBegins.Select(value => (double)value).ToArray());
    AssertTrue(session.Filters.RfMtf.All(value => value == Complex.One));
    AssertTrue(session.Filters.RfMtfMagnitude.All(value => value == 1.0));
}

[Fact(DisplayName = "TBC field sequence engine retries LD AGC")]
public void TbcFieldSequenceEngineRetriesLdAgc()
{
    using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "input.s16",
        "out"
    ]), blockLength: 4096);
    int reads = 0;
    TbcDecodedField? ReadField(
        DecodeSession activeSession,
        Stream _,
        long begin,
        int __,
        int ___)
    {
        reads++;
        return BuildSyntheticTbcField(
                begin,
                new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                detectedFirstField: true)
            with
            {
                LaserDiscAgcAdjusted = reads == 1,
                NextFieldOffsetSamples = 100.0,
                NominalFieldLengthSamples = 100.0
            };
    }

    IReadOnlyList<TbcDecodedField> fields = new TbcFieldSequenceDecodeEngine(
        readField: ReadField).DecodeFields(session, Stream.Null, maxFields: 1);
    AssertEqual(1, fields.Count);
    AssertEqual(2, reads);
    AssertFalse(fields[0].LaserDiscAgcAdjusted);
}

[Fact(DisplayName = "TBC field sequence engine writes SQLite debug DB")]
public void TbcFieldSequenceEngineWritesSqliteDebugDb()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "capture");
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--write_db",
            "input.u8",
            outputBase
        ]));

        TbcDecodedField first = BuildSyntheticTbcField(
                startSample: 0,
                samples: new ushort[session.TbcFrameSpec.FieldSampleCount],
                lineLocations: BuildLineLocationsForAdvance(session, 300.0),
                detectedFirstField: true,
                dropouts: new TbcDropoutMap([10], [20], [30]))
            with
            {
                VitsMetrics = new Dictionary<string, double>
                {
                    ["wSNR"] = 12.5,
                    ["bPSNR"] = 9.75
                }
            };
        TbcDecodedField second = BuildSyntheticTbcField(
            startSample: 300,
            samples: new ushort[session.TbcFrameSpec.FieldSampleCount],
            lineLocations: BuildLineLocationsForAdvance(session, 300.0),
            detectedFirstField: false);

        TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine().WriteDecodedFields(session, [first, second]);

        AssertTrue(result.Success);
        AssertEqual(outputBase + ".tbc.db", result.Paths!.DbPath);
        AssertTrue(File.Exists(result.Paths.DbPath!));
        AssertEqual(1L, SqliteLong(result.Paths.DbPath!, "SELECT user_version FROM pragma_user_version"));
        AssertEqual(8L, SqliteLong(result.Paths.DbPath!, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"));
        byte[] sqliteBytes = File.ReadAllBytes(result.Paths.DbPath!);
        AssertEqual(12u, BinaryPrimitives.ReadUInt32BigEndian(sqliteBytes.AsSpan(24, sizeof(uint))));
        AssertEqual(12u, BinaryPrimitives.ReadUInt32BigEndian(sqliteBytes.AsSpan(92, sizeof(uint))));
        AssertEqual(3_050_004u, BinaryPrimitives.ReadUInt32BigEndian(sqliteBytes.AsSpan(96, sizeof(uint))));
        AssertTrue(SqliteString(
            result.Paths.DbPath!,
            "SELECT sql FROM sqlite_master WHERE name = 'vits_metrics'")
            .Contains("FOREIGN KEY (capture_id, field_id) \n        REFERENCES", StringComparison.Ordinal));
        AssertEqual("PAL", SqliteString(result.Paths.DbPath!, "SELECT system FROM capture"));
        AssertEqual("ld-decode", SqliteString(result.Paths.DbPath!, "SELECT decoder FROM capture"));
        AssertEqual("vhs_decode", SqliteString(result.Paths.DbPath!, "SELECT git_branch FROM capture"));
        AssertEqual("g4315520", SqliteString(result.Paths.DbPath!, "SELECT git_commit FROM capture"));
        AssertEqual(2L, SqliteLong(result.Paths.DbPath!, "SELECT number_of_sequential_fields FROM capture"));
        AssertEqual(2L, SqliteLong(result.Paths.DbPath!, "SELECT COUNT(*) FROM field_record"));
        AssertEqual(1L, SqliteLong(result.Paths.DbPath!, "SELECT capture_id FROM field_record WHERE field_id = 0"));
        AssertEqual(1L, SqliteLong(result.Paths.DbPath!, "SELECT is_first_field FROM field_record WHERE field_id = 0"));
        AssertEqual(0L, SqliteLong(result.Paths.DbPath!, "SELECT is_first_field FROM field_record WHERE field_id = 1"));
        AssertEqual(300L, SqliteLong(result.Paths.DbPath!, "SELECT file_loc FROM field_record WHERE field_id = 1"));
        AssertEqual(2L, SqliteLong(result.Paths.DbPath!, "SELECT COUNT(*) FROM vits_metrics"));
        AssertClose(12.5, SqliteDouble(result.Paths.DbPath!, "SELECT w_snr FROM vits_metrics WHERE field_id = 0"), 1e-12);
        AssertClose(9.75, SqliteDouble(result.Paths.DbPath!, "SELECT b_psnr FROM vits_metrics WHERE field_id = 0"), 1e-12);
        AssertEqual(1L, SqliteLong(result.Paths.DbPath!, "SELECT COUNT(*) FROM drop_outs"));
        AssertEqual(20L, SqliteLong(result.Paths.DbPath!, "SELECT startx FROM drop_outs WHERE field_id = 0"));
        AssertEqual(0L, SqliteLong(result.Paths.DbPath!, "SELECT COUNT(*) FROM vbi"));

        string emptyMetricsJsonPath = Path.Combine(tempDirectory, "empty-metrics.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(startSample: 0, samples: Array.Empty<ushort>())],
            emptyMetricsJsonPath);
        using JsonDocument emptyMetricsDocument = JsonDocument.Parse(File.ReadAllText(emptyMetricsJsonPath));
        JsonElement emptyMetrics = emptyMetricsDocument.RootElement.GetProperty("fields")[0].GetProperty("vitsMetrics");
        AssertEqual(JsonValueKind.Object, emptyMetrics.ValueKind);
        AssertEqual(0, JsonPropertyCount(emptyMetrics));

        string zeroNoiseJsonPath = Path.Combine(tempDirectory, "zero-noise.tbc.json");
        ushort tapeBlack = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(0.0));
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(
                startSample: 0,
                samples: Enumerable.Repeat(tapeBlack, session.TbcFrameSpec.FieldSampleCount).ToArray())],
            zeroNoiseJsonPath);
        using JsonDocument zeroNoiseDocument = JsonDocument.Parse(File.ReadAllText(zeroNoiseJsonPath));
        JsonElement zeroNoiseMetrics = zeroNoiseDocument.RootElement.GetProperty("fields")[0].GetProperty("vitsMetrics");
        AssertClose(0.0, JsonDouble(zeroNoiseMetrics, "bPSNR"), 0.0);

        DecodeSession noDodSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--noDOD",
            "input.u8",
            Path.Combine(tempDirectory, "no-dod")
        ]));
        string noDodDbPath = Path.Combine(tempDirectory, "no-dod.tbc.db");
        TbcSqliteMetadataWriter.Write(
            noDodSession,
            [BuildSyntheticTbcField(startSample: 0, samples: Array.Empty<ushort>())],
            noDodDbPath);
        AssertEqual(1L, SqliteLong(noDodDbPath, "SELECT capture_id FROM capture"));
        AssertEqual(0L, SqliteLong(noDodDbPath, "SELECT capture_id FROM field_record WHERE field_id = 0"));
        AssertEqual(0L, SqliteLong(noDodDbPath, "SELECT capture_id FROM vits_metrics WHERE field_id = 0"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC SQLite schema is checkout-line-ending independent")]
public void TbcSqliteSchemaIsCheckoutLineEndingIndependent()
{
    const string checkoutSchema = "CREATE TABLE first (id INTEGER);\r\nCREATE TABLE second (id INTEGER);\r\n";
    const string upstreamSchema = "CREATE TABLE first (id INTEGER);\nCREATE TABLE second (id INTEGER);\n";

    AssertEqual(upstreamSchema, TbcSqliteMetadataWriter.NormalizeSchemaSql(checkoutSchema));
}

[Fact(DisplayName = "TBC field sequence engine honors decoded next-field offsets")]
public void TbcFieldSequenceEngineHonorsDecodedNextFieldOffsets()
{
    DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["input.u8", "out"]));
    var readBegins = new List<long>();
    ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];

    TbcDecodedField? ReadSyntheticField(
        DecodeSession _,
        Stream __,
        long begin,
        int ___,
        int fieldNumber)
    {
        readBegins.Add(begin);
        return BuildSyntheticTbcField(
                startSample: begin,
                samples: samples,
                detectedFirstField: (fieldNumber & 1) == 0)
            with
            {
                NextFieldOffsetSamples = 125.0,
                NominalFieldLengthSamples = 125.0
            };
    }

    var engine = new TbcFieldSequenceDecodeEngine(readField: ReadSyntheticField);
    IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(session, Stream.Null, maxFields: 3);

    AssertEqual(3, fields.Count);
    AssertSequence([0.0, 125.0, 250.0], readBegins.Select(value => (double)value).ToArray());
    AssertEqual(375L, TbcFieldSequenceDecodeEngine.EstimateNextFieldStart(session, fields[^1]));
}

[Fact(DisplayName = "CVBS output deferral covers serial and worker auto-sync paths")]
public void CvbsOutputDeferralCoversSerialAndWorkerAutoSyncPaths()
{
    using DecodeSession serial = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--threads", "0", "input.s16", "serial"
    ]));
    using DecodeSession worker = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--threads", "1", "input.s16", "worker"
    ]));
    using DecodeSession staticLevels = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--threads", "0", "--no_auto_sync", "input.s16", "static"
    ]));
    using DecodeSession clamp = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--threads", "0", "--clamp_agc", "input.s16", "clamp"
    ]));

    AssertTrue(TbcFieldSequenceDecodeEngine.ShouldDeferCvbsOutputConversion(serial));
    AssertTrue(TbcFieldSequenceDecodeEngine.ShouldDeferCvbsOutputConversion(worker));
    AssertFalse(TbcFieldSequenceDecodeEngine.ShouldDeferCvbsOutputConversion(staticLevels));
    AssertFalse(TbcFieldSequenceDecodeEngine.ShouldDeferCvbsOutputConversion(clamp));
}

[Fact(DisplayName = "CVBS and LD session reads use Release 4.0 fields-written numbering")]
public void CvbsAndLdSessionReadsUseReleaseFourFieldsWrittenNumbering()
{
    int[] writtenFieldCounts = [0, 0, 0, 1];
    int[] sessionFieldNumbers = writtenFieldCounts
        .Select((writtenFieldCount, decodedFieldNumber) =>
            TbcFieldSequenceDecodeEngine.ResolveReadFieldNumber(
                usesSessionReader: true,
                decoderName: "cvbs",
                decodedFieldNumber,
                writtenFieldCount))
        .ToArray();

    AssertTrue(sessionFieldNumbers.SequenceEqual([0, 0, 0, 1]));
    AssertEqual(
        1,
        TbcFieldSequenceDecodeEngine.ResolveReadFieldNumber(
            usesSessionReader: true,
            decoderName: "ld",
            decodedFieldNumber: 3,
            writtenFieldCount: 1));
    AssertEqual(
        3,
        TbcFieldSequenceDecodeEngine.ResolveReadFieldNumber(
            usesSessionReader: false,
            decoderName: "cvbs",
            decodedFieldNumber: 3,
            writtenFieldCount: 1));
    AssertEqual(
        3,
        TbcFieldSequenceDecodeEngine.ResolveReadFieldNumber(
            usesSessionReader: true,
            decoderName: "vhs",
            decodedFieldNumber: 3,
            writtenFieldCount: 1));
}

[Fact(DisplayName = "CVBS worker prefetch exposes next-field levels before current conversion")]
public void CvbsWorkerPrefetchExposesNextFieldLevelsBeforeCurrentConversion()
{
    using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--pal", "--threads", "1", "input.s16", "out"
    ]));
    VideoOutputConverter firstConverter = BuildCvbsTestConverter(session, ire0: 10.0);
    VideoOutputConverter secondConverter = BuildCvbsTestConverter(session, ire0: 20.0);
    TbcDecodedField[] sourceFields =
    [
        BuildDeferredCvbsField(session, 0, 0, 50.0, firstConverter, true),
        BuildDeferredCvbsField(session, 100, 1, 60.0, secondConverter, false)
    ];
    ushort firstOwnLevel = RenderDeferredCvbsFirstSample(session, sourceFields[0], firstConverter);
    ushort firstNextLevel = RenderDeferredCvbsFirstSample(session, sourceFields[0], secondConverter);
    int callingThread = Environment.CurrentManagedThreadId;
    int secondReaderThread = 0;
    int reads = 0;

    TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int fieldNumber)
    {
        Interlocked.Increment(ref reads);
        VideoOutputConverter converter = fieldNumber == 0 ? firstConverter : secondConverter;
        activeSession.TbcFieldDecoder.CurrentCvbsOutputConverter = converter;
        if (fieldNumber == 1)
        {
            Volatile.Write(ref secondReaderThread, Environment.CurrentManagedThreadId);
        }

        return fieldNumber < sourceFields.Length
            ? sourceFields[fieldNumber] with { StartSample = begin }
            : null;
    }

    var engine = new TbcFieldSequenceDecodeEngine(readField: ReadField)
    {
        EnableWorkerPrefetchForCustomReader = true
    };
    IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(session, Stream.Null, maxFields: 2);

    AssertEqual(2, reads);
    AssertEqual(2, fields.Count);
    AssertEqual(firstNextLevel, fields[0].Samples[0]);
    AssertTrue(firstOwnLevel != fields[0].Samples[0]);
    AssertClose(secondConverter.Ire0, fields[0].OutputConverter!.Ire0, 0.0);
    AssertClose(secondConverter.Ire0, fields[1].OutputConverter!.Ire0, 0.0);
    AssertTrue(Volatile.Read(ref secondReaderThread) != callingThread);
    AssertTrue(fields.All(field => field.DeferredRenderSource is null));
}

[Fact(DisplayName = "CVBS serial max-fields conversion uses the next field and flushes the tail")]
public void CvbsSerialMaxFieldsConversionUsesNextFieldAndFlushesTail()
{
    using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
        "--pal", "--threads", "0", "input.s16", "out"
    ]));
    VideoOutputConverter firstConverter = BuildCvbsTestConverter(session, ire0: 10.0);
    VideoOutputConverter secondConverter = BuildCvbsTestConverter(session, ire0: 20.0);
    TbcDecodedField[] sourceFields =
    [
        BuildDeferredCvbsField(session, 0, 0, 50.0, firstConverter, true),
        BuildDeferredCvbsField(session, 100, 1, 60.0, secondConverter, false)
    ];
    ushort firstOwnLevel = RenderDeferredCvbsFirstSample(session, sourceFields[0], firstConverter);
    ushort firstNextLevel = RenderDeferredCvbsFirstSample(session, sourceFields[0], secondConverter);
    ushort secondOwnLevel = RenderDeferredCvbsFirstSample(session, sourceFields[1], secondConverter);
    int reads = 0;

    TbcDecodedField? ReadField(DecodeSession _, Stream __, long begin, int ___, int fieldNumber)
    {
        reads++;
        return fieldNumber < sourceFields.Length
            ? sourceFields[fieldNumber] with { StartSample = begin }
            : null;
    }

    IReadOnlyList<TbcDecodedField> fields = new TbcFieldSequenceDecodeEngine(
        readField: ReadField).DecodeFields(session, Stream.Null, maxFields: 2);

    AssertEqual(2, reads);
    AssertEqual(2, fields.Count);
    AssertEqual(firstNextLevel, fields[0].Samples[0]);
    AssertEqual(secondOwnLevel, fields[1].Samples[0]);
    AssertTrue(firstOwnLevel != fields[0].Samples[0]);
    AssertClose(secondConverter.Ire0, fields[0].OutputConverter!.Ire0, 0.0);
    AssertClose(secondConverter.Ire0, fields[1].OutputConverter!.Ire0, 0.0);
    AssertTrue(fields.All(field => field.DeferredRenderSource is null));
}

[Fact(DisplayName = "CVBS serial length lookahead is decoded but not written")]
public void CvbsSerialLengthLookaheadIsDecodedButNotWritten()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "cvbs-prefetch");
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--pal",
            "--threads", "0",
            "--length", "1",
            "input.s16",
            outputBase
        ]));
        session.RuntimeReporter = new DecodeRuntimeReporter(
            TextWriter.Null,
            TextWriter.Null,
            () => 0.0);
        VideoOutputConverter[] converters =
        [
            BuildCvbsTestConverter(session, ire0: 10.0),
            BuildCvbsTestConverter(session, ire0: 20.0),
            BuildCvbsTestConverter(session, ire0: 30.0)
        ];
        TbcDecodedField[] sourceFields = converters
            .Select((converter, fieldNumber) => BuildDeferredCvbsField(
                session,
                fieldNumber * 100L,
                fieldNumber,
                50.0 + (fieldNumber * 10.0),
                converter,
                detectedFirstField: (fieldNumber & 1) == 0))
            .ToArray();
        sourceFields[0] = sourceFields[0] with { FieldPhaseId = 5 };
        sourceFields[1] = sourceFields[1] with { FieldPhaseId = 8 };
        sourceFields[2] = sourceFields[2] with { FieldPhaseId = 1 };
        ushort firstExpected = RenderDeferredCvbsFirstSample(session, sourceFields[0], converters[1]);
        ushort secondExpected = RenderDeferredCvbsFirstSample(session, sourceFields[1], converters[2]);
        int reads = 0;

        TbcDecodedField? ReadField(DecodeSession _, Stream __, long begin, int ___, int fieldNumber)
        {
            reads++;
            return fieldNumber < sourceFields.Length
                ? sourceFields[fieldNumber] with { StartSample = begin }
                : null;
        }

        var diskGuard = new VhsDiskSpaceGuard(
            _ => throw new Exception("CVBS recovery snapshots must not query free disk space."));
        TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine(
            readField: ReadField,
            vhsDiskSpaceGuard: diskGuard).TryDecodeAndWrite(session, Stream.Null);

        AssertTrue(result.Success);
        AssertEqual(3, reads);
        AssertEqual(2, result.WrittenFieldCount);
        byte[] bytes = File.ReadAllBytes(result.Paths!.TbcPath);
        AssertEqual(session.TbcFrameSpec.FieldSampleCount * 2 * sizeof(ushort), bytes.Length);
        AssertFieldFirstSample(
            bytes,
            session.TbcFrameSpec.FieldSampleCount,
            fieldIndex: 0,
            firstExpected);
        AssertFieldFirstSample(
            bytes,
            session.TbcFrameSpec.FieldSampleCount,
            fieldIndex: 1,
            secondExpected);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths.JsonPath));
        AssertEqual(2, document.RootElement.GetProperty("fields").GetArrayLength());
        string[] logLines = File.ReadAllLines(session.OutputBase + ".log");
        AssertEqual(2, logLines.Length);
        AssertTrue(logLines[0].Contains(
            "WARNING - At field #1, Field phaseID sequence mismatch (5->8)",
            StringComparison.Ordinal));
        AssertTrue(logLines[1].EndsWith(
            "DEBUG - File Frame 0: CAV Pulldown/Telecine Frame",
            StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "CVBS dropped initial second field does not report a file frame")]
public void CvbsDroppedInitialSecondFieldDoesNotReportAFileFrame()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "cvbs-start-fileloc");
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--pal",
            "--threads", "0",
            "--length", "1",
            "input.s16",
            outputBase
        ]));
        session.RuntimeReporter = new DecodeRuntimeReporter(
            TextWriter.Null,
            TextWriter.Null,
            () => 0.0);
        int sampleCount = session.TbcFrameSpec.FieldSampleCount;
        TbcDecodedField[] sourceFields =
        [
            BuildSyntheticTbcField(
                    0,
                    new ushort[sampleCount],
                    BuildLineLocationsForAdvance(session, 300.0),
                    detectedFirstField: false)
                with { DiskLocation = 1.0, FieldPhaseId = 8 },
            BuildSyntheticTbcField(
                    300,
                    new ushort[sampleCount],
                    BuildLineLocationsForAdvance(session, 300.0),
                    detectedFirstField: true)
                with { DiskLocation = 2.0, FieldPhaseId = 1 },
            BuildSyntheticTbcField(
                    600,
                    new ushort[sampleCount],
                    BuildLineLocationsForAdvance(session, 300.0),
                    detectedFirstField: false)
                with { DiskLocation = 3.0, FieldPhaseId = 8 }
        ];
        int reads = 0;

        TbcDecodedField? ReadField(DecodeSession _, Stream __, long ___, int ____, int fieldNumber)
        {
            reads++;
            return fieldNumber < sourceFields.Length ? sourceFields[fieldNumber] : null;
        }

        TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine(readField: ReadField)
            .TryDecodeAndWrite(session, Stream.Null);

        AssertTrue(result.Success);
        AssertEqual(3, reads);
        AssertEqual(2, result.WrittenFieldCount);
        string log = File.ReadAllText(outputBase + ".log");
        AssertFalse(log.Contains("File Frame 0:", StringComparison.Ordinal));
        AssertTrue(log.Contains("File Frame 1: CAV Pulldown/Telecine Frame", StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "CVBS worker length lookahead completes without being written")]
public void CvbsWorkerLengthLookaheadCompletesWithoutBeingWritten()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--pal",
            "--threads", "1",
            "--length", "1",
            "input.s16",
            Path.Combine(tempDirectory, "cvbs-worker-prefetch")
        ]));
        VideoOutputConverter[] converters =
        [
            BuildCvbsTestConverter(session, ire0: 10.0),
            BuildCvbsTestConverter(session, ire0: 20.0),
            BuildCvbsTestConverter(session, ire0: 30.0)
        ];
        TbcDecodedField[] sourceFields = converters
            .Select((converter, fieldNumber) => BuildDeferredCvbsField(
                session,
                fieldNumber * 100L,
                fieldNumber,
                50.0 + (fieldNumber * 10.0),
                converter,
                detectedFirstField: (fieldNumber & 1) == 0))
            .ToArray();
        int reads = 0;

        TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int fieldNumber)
        {
            Interlocked.Increment(ref reads);
            if (fieldNumber >= sourceFields.Length)
            {
                return null;
            }

            activeSession.TbcFieldDecoder.CurrentCvbsOutputConverter = converters[fieldNumber];
            return sourceFields[fieldNumber] with { StartSample = begin };
        }

        var engine = new TbcFieldSequenceDecodeEngine(readField: ReadField)
        {
            EnableWorkerPrefetchForCustomReader = true
        };
        TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(session, Stream.Null);

        AssertTrue(result.Success);
        AssertEqual(3, reads);
        AssertEqual(2, result.WrittenFieldCount);
        AssertEqual(
            session.TbcFrameSpec.FieldSampleCount * 2 * sizeof(ushort),
            File.ReadAllBytes(result.Paths!.TbcPath).Length);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths.JsonPath));
        AssertEqual(2, document.RootElement.GetProperty("fields").GetArrayLength());
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine recovers with upstream field advances")]
public void TbcFieldSequenceEngineRecoversWithUpstreamFieldAdvances()
{
    DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["input.u8", "out"]));
    var readBegins = new List<long>();
    int attempts = 0;
    TbcDecodedField? ReadRecoveringField(
        DecodeSession activeSession,
        Stream _,
        long begin,
        int __,
        int fieldNumber)
    {
        readBegins.Add(begin);
        attempts++;
        return attempts switch
        {
            1 => throw new TbcFieldDecodeRecoveryException(
                TbcFieldDecodeRecoveryKind.NoSyncPulses,
                4_000_000,
                "jump 100 ms"),
            3 => throw new TbcFieldDecodeRecoveryException(
                TbcFieldDecodeRecoveryKind.InsufficientData,
                2_542,
                "skip a tiny bit"),
            _ => BuildSyntheticTbcField(
                    begin,
                    new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                    detectedFirstField: (fieldNumber & 1) == 0)
                with
                {
                    NextFieldOffsetSamples = 100.0,
                    NominalFieldLengthSamples = 100.0
                }
        };
    }

    IReadOnlyList<TbcDecodedField> fields = new TbcFieldSequenceDecodeEngine(
        readField: ReadRecoveringField).DecodeFields(session, Stream.Null, maxFields: 2);
    AssertEqual(2, fields.Count);
    AssertSequence([0.0, 4_000_000.0, 4_000_100.0, 4_002_642.0], readBegins.Select(value => (double)value).ToArray());

    DecodeSession cvbs = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["input.u8", "out"]));
    int cvbsAttempts = 0;
    TbcDecodedField? ReadCvbsField(DecodeSession activeSession, Stream _, long begin, int __, int fieldNumber)
    {
        cvbsAttempts++;
        if (cvbsAttempts == 1)
        {
            return BuildSyntheticTbcField(
                    begin,
                    new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                    detectedFirstField: true)
                with { NextFieldOffsetSamples = 100.0 };
        }

        if (cvbsAttempts == 2)
        {
            throw new TbcFieldDecodeRecoveryException(
                TbcFieldDecodeRecoveryKind.NoSyncPulses,
                40_000_000,
                "recover after written fields",
                stopAfterDecodedFields: true);
        }

        return null;
    }

    IReadOnlyList<TbcDecodedField> cvbsFields = new TbcFieldSequenceDecodeEngine(
        readField: ReadCvbsField).DecodeFields(cvbs, Stream.Null, maxFields: 2);
    AssertEqual(1, cvbsFields.Count);
    AssertEqual(3, cvbsAttempts);

    TbcFieldDecodeRecoveryException noPulses = CaptureException<TbcFieldDecodeRecoveryException>(() =>
        session.TbcFieldDecoder.Decode(new RfDecodedSpan(0, [], [0.0, 0.0, 0.0], [])));
    AssertEqual(TbcFieldDecodeRecoveryKind.NoSyncPulses, noPulses.Kind);
    AssertEqual(4_000_000L, noPulses.SuggestedOffsetSamples);
    AssertFalse(noPulses.StopAfterDecodedFields);

    TbcFieldDecodeRecoveryException cvbsNoPulses = CaptureException<TbcFieldDecodeRecoveryException>(() =>
        cvbs.TbcFieldDecoder.Decode(new RfDecodedSpan(0, [], [0.0, 0.0, 0.0], [])));
    AssertEqual(40_000_000L, cvbsNoPulses.SuggestedOffsetSamples);
    AssertTrue(cvbsNoPulses.StopAfterDecodedFields);

    var analyzer = (SyncAnalyzer)PrivateFieldValue(session.TbcFieldDecoder, "_syncAnalyzer")!;
    int equalizingLength = (int)Math.Round(analyzer.UsecToSamples(analyzer.EqualizingPulseUs));
    double[] equalizingOnly = new double[10_000];
    for (int pulse = 0; pulse < 6; pulse++)
    {
        PaintPulse(equalizingOnly, 100 + (pulse * 1_271), equalizingLength, -40.0);
    }

    TbcFieldDecodeRecoveryException noFirstHSync = CaptureException<TbcFieldDecodeRecoveryException>(() =>
        session.TbcFieldDecoder.Decode(
            new RfDecodedSpan(0, equalizingOnly, equalizingOnly, equalizingOnly),
            syncThresholdHz: -20.0));
    AssertEqual(TbcFieldDecodeRecoveryKind.NoFirstHSync, noFirstHSync.Kind);
    AssertEqual((long)(analyzer.NominalLineLength * 100.0), noFirstHSync.SuggestedOffsetSamples);

    int lineLength = (int)Math.Round(analyzer.NominalLineLength);
    int hsyncLength = (int)Math.Round(analyzer.UsecToSamples(analyzer.HSyncPulseUs));
    double[] hsyncOnly = new double[lineLength * 16];
    for (int line = 0; line < 15; line++)
    {
        PaintPulse(hsyncOnly, 10 + (line * lineLength), hsyncLength, -40.0);
    }

    TbcFieldDecodeRecoveryException unanchoredHSync = CaptureException<TbcFieldDecodeRecoveryException>(() =>
        session.TbcFieldDecoder.Decode(
            new RfDecodedSpan(0, hsyncOnly, hsyncOnly, hsyncOnly),
            syncThresholdHz: -20.0));
    AssertEqual(TbcFieldDecodeRecoveryKind.NoFirstHSync, unanchoredHSync.Kind);
    AssertEqual((long)(analyzer.NominalLineLength * 100.0), unanchoredHSync.SuggestedOffsetSamples);

    int equalizingPulseLength = (int)Math.Round(analyzer.UsecToSamples(analyzer.EqualizingPulseUs));
    int vSyncPulseLength = (int)Math.Round(analyzer.UsecToSamples(analyzer.VSyncPulseUs));
    int halfLineLength = (int)Math.Round(analyzer.NominalLineLength / 2.0);
    int line0 = (2 * lineLength) + 10;
    double[] shortField = new double[lineLength * 18];
    PaintPulse(shortField, line0 - (2 * lineLength), hsyncLength, -40.0);
    PaintPulse(shortField, line0 - lineLength, hsyncLength, -40.0);
    PaintPulse(shortField, line0, hsyncLength, -40.0);
    int equalizing1Start = line0 + lineLength;
    for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
    {
        PaintPulse(shortField, equalizing1Start + (pulse * halfLineLength), equalizingPulseLength, -40.0);
    }

    int vSyncStart = equalizing1Start + (3 * lineLength);
    for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
    {
        PaintPulse(shortField, vSyncStart + (pulse * halfLineLength), vSyncPulseLength, -40.0);
    }

    int equalizing2Start = vSyncStart + (3 * lineLength);
    for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
    {
        PaintPulse(shortField, equalizing2Start + (pulse * halfLineLength), equalizingPulseLength, -40.0);
    }

    PaintPulse(shortField, line0 + (10 * lineLength), hsyncLength, -40.0);
    TbcFieldDecodeRecoveryException insufficient = CaptureException<TbcFieldDecodeRecoveryException>(() =>
        session.TbcFieldDecoder.Decode(
            new RfDecodedSpan(0, shortField, shortField, shortField),
            syncThresholdHz: -20.0));
    AssertEqual(TbcFieldDecodeRecoveryKind.InsufficientData, insufficient.Kind);
    AssertEqual((long)analyzer.NominalLineLength, insufficient.SuggestedOffsetSamples);

    DecodeSession ld = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "out"
    ]));
    var ldAnalyzer = (SyncAnalyzer)PrivateFieldValue(ld.TbcFieldDecoder, "_syncAnalyzer")!;
    int ldLineLength = (int)Math.Round(ldAnalyzer.NominalLineLength);
    int ldHsyncLength = (int)Math.Round(ldAnalyzer.UsecToSamples(ldAnalyzer.HSyncPulseUs));
    double[] ldHSyncOnly = new double[ldLineLength * 4];
    for (int line = 0; line < 4; line++)
    {
        PaintPulse(ldHSyncOnly, 10 + (line * ldLineLength), ldHsyncLength, -40.0);
    }

    TbcFieldDecodeRecoveryException ldNoVSync = CaptureException<TbcFieldDecodeRecoveryException>(() =>
        ld.TbcFieldDecoder.Decode(
            new RfDecodedSpan(0, ldHSyncOnly, ldHSyncOnly, ldHSyncOnly),
            syncThresholdHz: -20.0));
    AssertEqual(TbcFieldDecodeRecoveryKind.NoSyncPulses, ldNoVSync.Kind);

    int ldLine0 = (2 * ldLineLength) + 10;
    double[] shortLdField = new double[ldLineLength * 18];
    PaintPulse(shortLdField, ldLine0 - (2 * ldLineLength), ldHsyncLength, -40.0);
    PaintPulse(shortLdField, ldLine0 - ldLineLength, ldHsyncLength, -40.0);
    PaintNativeVBlank(shortLdField, ldAnalyzer, ldLine0, isFirstField: true, system: "NTSC");
    TbcFieldDecodeRecoveryException ldInsufficient = CaptureException<TbcFieldDecodeRecoveryException>(() =>
        ld.TbcFieldDecoder.Decode(
            new RfDecodedSpan(0, shortLdField, shortLdField, shortLdField),
            syncThresholdHz: -20.0));
    AssertEqual(TbcFieldDecodeRecoveryKind.InsufficientData, ldInsufficient.Kind);
    AssertTrue(ldInsufficient.SuggestedOffsetSamples < 0);
}

[Fact(DisplayName = "TBC field sequence engine writes LD EFM sidecars")]
public void TbcFieldSequenceEngineWritesLdEfmSidecars()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "capture");
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--preEFM",
            "--disable_analog_audio",
            "input.s16",
            outputBase
        ]));

        ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        samples[0] = 0x3456;
        short[] efm = BuildEfmSquareWave(2048, halfPeriodSamples: 9, amplitude: 12_000);
        TbcDecodedField field = BuildSyntheticTbcField(
                startSample: 0,
                samples: samples,
                lineLocations: BuildLineLocationsForAdvance(session, 300.0),
                rawPulseCount: 3,
                classifiedPulseCount: 3)
            with
            {
                DetectedFirstField = true,
                DetectedFirstFieldConfidence = 100,
                Efm = efm
            };

        var engine = new TbcFieldSequenceDecodeEngine();
        TbcFieldSequenceDecodeResult result = engine.WriteDecodedFields(session, [field]);

        AssertTrue(result.Success);
        string efmPath = outputBase + ".efm";
        string preEfmPath = outputBase + ".prefm";
        AssertTrue(File.Exists(efmPath));
        AssertTrue(File.Exists(preEfmPath));
        byte[] efmBytes = File.ReadAllBytes(efmPath);
        AssertTrue(efmBytes.Length > 20);
        foreach (byte value in efmBytes)
        {
            AssertTrue(value >= 3 && value <= 11);
        }

        AssertEqual(efm.Length * 2L, new FileInfo(preEfmPath).Length);
        AssertSequence(efm.Select(value => (double)value).ToArray(), ReadInt16Samples(File.ReadAllBytes(preEfmPath)));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputBase + ".tbc.json"));
        JsonElement fieldInfo = document.RootElement.GetProperty("fields")[0];
        AssertEqual(0, JsonInt(fieldInfo, "audioSamples"));
        AssertEqual(efmBytes.Length, JsonInt(fieldInfo, "efmTValues"));

        string noEfmBase = Path.Combine(tempDirectory, "noefm");
        DecodeSession noEfmSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--noEFM",
            "--preEFM",
            "--disable_analog_audio",
            "input.s16",
            noEfmBase
        ]));
        TbcDecodedField noEfmField = field with { Efm = null };
        TbcFieldSequenceDecodeResult noEfmResult = engine.WriteDecodedFields(noEfmSession, [noEfmField]);
        AssertTrue(noEfmResult.Success);
        AssertFalse(File.Exists(noEfmBase + ".efm"));
        AssertFalse(File.Exists(noEfmBase + ".prefm"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine streams LD sidecars with preserved state")]
public void TbcFieldSequenceEngineStreamsLdSidecarsWithPreservedState()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string batchBase = Path.Combine(tempDirectory, "batch");
        string streamingBase = Path.Combine(tempDirectory, "streaming");
        using DecodeSession batchSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--preEFM",
            "--verboseVITS",
            "--disable_analog_audio",
            "input.s16",
            batchBase
        ]));
        using DecodeSession streamingSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--preEFM",
            "--verboseVITS",
            "--disable_analog_audio",
            "input.s16",
            streamingBase
        ]));

        int sampleCount = batchSession.TbcFrameSpec.FieldSampleCount;
        TbcDecodedField[] fields =
        [
            BuildSyntheticTbcField(
                    0,
                    new ushort[sampleCount],
                    BuildLineLocationsForAdvance(batchSession, 300.0),
                    detectedFirstField: true)
                with
                {
                    Efm = BuildEfmSquareWave(2048, halfPeriodSamples: 9, amplitude: 12_000),
                    FieldPhaseId = 1
                },
            BuildSyntheticTbcField(
                    300,
                    new ushort[sampleCount],
                    BuildLineLocationsForAdvance(batchSession, 300.0),
                    detectedFirstField: false)
                with
                {
                    Efm = BuildEfmSquareWave(2048, halfPeriodSamples: 11, amplitude: 10_000),
                    FieldPhaseId = 2
                }
        ];

        var batchEngine = new TbcFieldSequenceDecodeEngine();
        TbcFieldSequenceDecodeResult batch = batchEngine.WriteDecodedFields(batchSession, fields);
        TbcDecodedField? ReadField(DecodeSession _, Stream __, long ___, int ____, int fieldNumber)
            => fields[fieldNumber];
        var streamingEngine = new TbcFieldSequenceDecodeEngine(readField: ReadField);
        TbcFieldSequenceDecodeResult streaming = streamingEngine.TryDecodeAndWrite(
            streamingSession,
            Stream.Null,
            maxFields: fields.Length);

        AssertTrue(streaming.Success);
        AssertTrue(File.ReadAllBytes(batch.Paths!.TbcPath).SequenceEqual(File.ReadAllBytes(streaming.Paths!.TbcPath)));
        AssertTrue(File.ReadAllBytes(batchBase + ".efm").SequenceEqual(File.ReadAllBytes(streamingBase + ".efm")));
        AssertTrue(File.ReadAllBytes(batchBase + ".prefm").SequenceEqual(File.ReadAllBytes(streamingBase + ".prefm")));
        AssertEqual(File.ReadAllText(batch.Paths.JsonPath), File.ReadAllText(streaming.Paths.JsonPath));
        AssertFalse(File.Exists(streaming.Paths.JsonPath + ".fields.tmp"));
        AssertFalse(File.Exists(streaming.Paths.JsonPath + ".tmp"));
        AssertEqual(
            SqliteLong(batch.Paths.DbPath!, "SELECT SUM(efm_t_values) FROM field_record"),
            SqliteLong(streaming.Paths.DbPath!, "SELECT SUM(efm_t_values) FROM field_record"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine writes LD analog audio sidecars")]
public void TbcFieldSequenceEngineWritesLdAnalogAudioSidecars()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "capture");
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--noEFM",
            "input.s16",
            outputBase
        ]));

        short[] audio = [100, -100, 200, -200, 300, -300];
        TbcDecodedField field = BuildSyntheticTbcField(
                startSample: 0,
                samples: new ushort[session.TbcFrameSpec.FieldSampleCount],
                lineLocations: BuildLineLocationsForAdvance(session, 300.0),
                rawPulseCount: 3,
                classifiedPulseCount: 3)
            with
            {
                DetectedFirstField = true,
                DetectedFirstFieldConfidence = 100,
                AudioPcm = audio
            };

        var engine = new TbcFieldSequenceDecodeEngine();
        TbcFieldSequenceDecodeResult result = engine.WriteDecodedFields(session, [field]);

        AssertTrue(result.Success);
        string pcmPath = outputBase + ".pcm";
        AssertTrue(File.Exists(pcmPath));
        AssertFalse(File.Exists(outputBase + ".efm"));
        AssertSequence(audio.Select(value => (double)value).ToArray(), ReadInt16Samples(File.ReadAllBytes(pcmPath)));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputBase + ".tbc.json"));
        JsonElement fieldInfo = document.RootElement.GetProperty("fields")[0];
        AssertEqual(audio.Length / 2, JsonInt(fieldInfo, "audioSamples"));
        AssertEqual(0, JsonInt(fieldInfo, "efmTValues"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine writes LD RF TBC sidecars")]
public void TbcFieldSequenceEngineWritesLdRfTbcSidecars()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "capture");
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--noEFM",
            "--disable_analog_audio",
            "--RF_TBC",
            "input.s16",
            outputBase
        ]));

        short[] rfTbc = [1, -2, 3, -4];
        TbcDecodedField field = BuildSyntheticTbcField(
                startSample: 0,
                samples: new ushort[session.TbcFrameSpec.FieldSampleCount],
                lineLocations: BuildLineLocationsForAdvance(session, 300.0),
                rawPulseCount: 3,
                classifiedPulseCount: 3)
            with
            {
                DetectedFirstField = true,
                DetectedFirstFieldConfidence = 100,
                RfTbc = rfTbc
            };

        var engine = new TbcFieldSequenceDecodeEngine(
            efmOutputWriter: new LaserDiscEfmOutputWriter(path => File.Create(path)));
        TbcFieldSequenceDecodeResult result = engine.WriteDecodedFields(session, [field]);

        AssertTrue(result.Success);
        string rfTbcPath = outputBase + ".tbc.ldf";
        AssertTrue(File.Exists(rfTbcPath));
        AssertFalse(File.Exists(outputBase + ".efm"));
        AssertFalse(File.Exists(outputBase + ".pcm"));
        AssertSequence(rfTbc.Select(value => (double)value).ToArray(), ReadInt16Samples(File.ReadAllBytes(rfTbcPath)));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine writes LD AC3 sidecars")]
public void TbcFieldSequenceEngineWritesLdAc3Sidecars()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string outputBase = Path.Combine(tempDirectory, "capture");
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "--AC3",
            "input.s16",
            outputBase
        ]));

        short[] rfTbc = new short[session.Filters.LdAc3!.Length];
        TbcDecodedField field = BuildSyntheticTbcField(
                startSample: 0,
                samples: new ushort[session.TbcFrameSpec.FieldSampleCount],
                lineLocations: BuildLineLocationsForAdvance(session, 300.0),
                rawPulseCount: 3,
                classifiedPulseCount: 3)
            with
            {
                DetectedFirstField = true,
                DetectedFirstFieldConfidence = 100,
                RfTbc = rfTbc
            };

        var engine = new TbcFieldSequenceDecodeEngine(
            efmOutputWriter: new LaserDiscEfmOutputWriter(path => File.Create(path)));
        TbcFieldSequenceDecodeResult result = engine.WriteDecodedFields(session, [field]);

        AssertTrue(result.Success);
        string ac3Path = outputBase + ".ac3";
        AssertTrue(File.Exists(ac3Path));
        AssertEqual(session.Filters.LdAc3.Length - 1024L, new FileInfo(ac3Path).Length);
        AssertFalse(File.Exists(outputBase + ".efm"));
        AssertFalse(File.Exists(outputBase + ".pcm"));
        AssertFalse(File.Exists(outputBase + ".tbc.ldf"));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine honors LD lead-out")]
public void TbcFieldSequenceEngineHonorsLdLeadOut()
{
    const int leadOutCode = 0x80EEEE;
    int cavCode = EncodeLaserDiscCavFrameCode(12);
    LaserDiscVbiInterpretation cavBeforeLeadOut = LaserDiscVbiInterpreter.Interpret(
        [cavCode, leadOutCode, leadOutCode],
        30);
    AssertEqual<int?>(12, cavBeforeLeadOut.FrameNumber);
    AssertFalse(cavBeforeLeadOut.LeadOut);
    AssertTrue(LaserDiscVbiInterpreter.Interpret([leadOutCode, leadOutCode], 30).LeadOut);

    int[]?[] vbiByField =
    [
        [cavCode, leadOutCode, leadOutCode],
        [],
        null,
        [leadOutCode],
        [leadOutCode],
        [leadOutCode],
        [],
        []
    ];

    using DecodeSession ld = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "out"
    ]));

    (IReadOnlyList<TbcDecodedField> stoppedFields, int stoppedReads) = Decode(ld);
    AssertEqual(6, stoppedReads);
    AssertEqual(6, stoppedFields.Count);

    using DecodeSession ignoreLeadOut = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--ignoreleadout",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "out"
    ]));
    (IReadOnlyList<TbcDecodedField> ignoredFields, int ignoredReads) = Decode(ignoreLeadOut);
    AssertEqual(vbiByField.Length, ignoredReads);
    AssertEqual(vbiByField.Length, ignoredFields.Count);

    (IReadOnlyList<TbcDecodedField> Fields, int Reads) Decode(DecodeSession session)
    {
        int reads = 0;
        TbcDecodedField? ReadField(DecodeSession _, Stream __, long begin, int ___, int fieldNumber)
        {
            reads++;
            return BuildSyntheticTbcField(
                    startSample: begin,
                    samples: [],
                    detectedFirstField: (fieldNumber & 1) == 0)
                with
                {
                    NextFieldOffsetSamples = 100.0,
                    VbiData = vbiByField[fieldNumber]
                };
        }

        IReadOnlyList<TbcDecodedField> fields = new TbcFieldSequenceDecodeEngine(
            readField: ReadField).DecodeFields(
                session,
                Stream.Null,
                maxFields: vbiByField.Length);
        return (fields, reads);
    }
}

[Fact(DisplayName = "TBC field sequence engine ignores LD VBI state from skipped fields")]
public void TbcFieldSequenceEngineIgnoresLdVbiStateFromSkippedFields()
{
    const int leadOutCode = 0x80EEEE;
    double[] diskLocations = [0.0, 1.0, 4.0, 5.0];
    bool[] firstFields = [true, false, false, true];
    int reads = 0;
    var statusOutput = new StringWriter();
    using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "out"
    ]));
    session.RuntimeReporter = new DecodeRuntimeReporter(
        statusOutput,
        new StringWriter(),
        () => 0.0);

    TbcDecodedField? ReadField(
        DecodeSession _,
        Stream __,
        long begin,
        int ___,
        int fieldNumber)
    {
        reads++;
        return BuildSyntheticTbcField(
                begin,
                [],
                detectedFirstField: firstFields[fieldNumber])
            with
            {
                DiskLocation = diskLocations[fieldNumber],
                NextFieldOffsetSamples = 100.0,
                VbiData = fieldNumber == 2 ? [leadOutCode, leadOutCode] : []
            };
    }

    IReadOnlyList<TbcDecodedField> fields = new TbcFieldSequenceDecodeEngine(
        readField: ReadField).DecodeFields(
            session,
            Stream.Null,
            maxFields: diskLocations.Length);

    AssertEqual(diskLocations.Length, reads);
    AssertEqual(diskLocations.Length, fields.Count);
    AssertFalse(statusOutput.ToString().Contains("Lead Out", StringComparison.Ordinal));
}

[Fact(DisplayName = "TBC field sequence engine rejects CVBS seek")]
public void TbcFieldSequenceEngineRejectsCvbsSeek()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.u8");
        File.WriteAllBytes(inputPath, [0, 1, 2, 3]);
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--seek",
            "12",
            "--length",
            "1",
            inputPath,
            Path.Combine(tempDirectory, "out")
        ]));

        AssertThrows<InvalidOperationException>(() => new TbcFieldSequenceDecodeEngine().DecodeFields(session, Stream.Null));
        TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine().TryDecodeAndWrite(session);
        AssertFalse(result.Success);
        AssertEqual("ERROR: Seeking failed", result.Message);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine applies LD seek")]
public void TbcFieldSequenceEngineAppliesLdSeek()
{
    DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--NTSC",
        "--seek",
        "12",
        "--length",
        "1",
        "--noEFM",
        "--disable_analog_audio",
        "input.s16",
        "out"
    ]));
    long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
    long initialProbe = 12L * 2L * nominalFieldSamples;
    long targetProbe = initialProbe + (3L * nominalFieldSamples);
    long targetDecodeStart = targetProbe + nominalFieldSamples;
    var readBegins = new List<long>();
    TbcDecodedField? ReadSyntheticField(DecodeSession activeSession, Stream _, long begin, int __, int fieldNumber)
    {
        readBegins.Add(begin);
        int frameNumber = begin >= targetProbe ? 12 : 10;
        int[] vbi = fieldNumber == 1 ? [EncodeLaserDiscCavFrameCode(frameNumber)] : [];
        ushort[] samples = new ushort[activeSession.TbcFrameSpec.FieldSampleCount];
        samples[0] = (ushort)(begin == targetDecodeStart ? 0x2222 : 0x1111);
        return BuildSyntheticTbcField(
                begin,
                samples,
                lineLocations: BuildLineLocationsForAdvance(activeSession, nominalFieldSamples),
                detectedFirstField: fieldNumber == 0)
            with
            {
                VbiData = vbi
            };
    }

    var engine = new TbcFieldSequenceDecodeEngine(readField: ReadSyntheticField);
    IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(session, Stream.Null, maxFields: 1);

    AssertEqual(1, fields.Count);
    AssertEqual(targetDecodeStart, fields[0].StartSample);
    AssertEqual((ushort)0x2222, fields[0].Samples[0]);
    AssertSequence(
        [initialProbe, initialProbe + nominalFieldSamples, targetProbe, targetDecodeStart, targetDecodeStart],
        readBegins.Select(value => (double)value).ToArray());
}

[Fact(DisplayName = "TBC metadata writer emits LD upstream fields")]
public void TbcMetadataWriterEmitsLdUpstreamFields()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "input.s16",
            Path.Combine(tempDirectory, "capture")
        ]));
        TbcDecodedField explicitField = BuildSyntheticTbcField(
                startSample: 0,
                samples: new ushort[session.TbcFrameSpec.FieldSampleCount])
            with
            {
                DiskLocation = 12.3,
                MedianBurstIre = 27.5126,
                FieldPhaseId = 1,
                VitsMetrics = new Dictionary<string, double>
                {
                    ["wSNR"] = 41.2,
                    ["bPSNR"] = 38.8,
                    ["ignoredNaN"] = double.NaN
                },
                VbiData = [0x12345, 0x80E001]
            };
        long oneFieldStart = (long)((int)(session.DecodeSampleRateHz / (JsonDouble(session.Parameters.SysParams, "FPS") * 2.0)) + 1);
        TbcDecodedField defaultField = BuildSyntheticTbcField(
            startSample: oneFieldStart,
            samples: new ushort[session.TbcFrameSpec.FieldSampleCount]);
        string jsonPath = Path.Combine(tempDirectory, "capture.tbc.json");

        TbcOutputMetadataWriter.WriteJson(session, [explicitField, defaultField], jsonPath);

        string defaultJson = File.ReadAllText(jsonPath);
        AssertFalse(defaultJson.Contains(Environment.NewLine + " ", StringComparison.Ordinal));
        AssertContains(defaultJson, "\"isSigned\":true,\"sampleRate\":0}");
        using JsonDocument document = JsonDocument.Parse(defaultJson);
        JsonElement pcmAudio = document.RootElement.GetProperty("pcmAudioParameters");
        AssertEqual(16, JsonInt(pcmAudio, "bits"));
        AssertTrue(pcmAudio.GetProperty("isLittleEndian").GetBoolean());
        AssertTrue(pcmAudio.GetProperty("isSigned").GetBoolean());
        AssertClose(0.0, JsonDouble(pcmAudio, "sampleRate"), 1e-12);
        JsonElement videoParameters = document.RootElement.GetProperty("videoParameters");
        AssertEqual(2, JsonInt(videoParameters, "numberOfSequentialFields"));
        AssertEqual(DecodeVersionInfo.Version, videoParameters.GetProperty("version").GetString());
        AssertFalse(string.IsNullOrWhiteSpace(videoParameters.GetProperty("osInfo").GetString()));
        AssertEqual("vhs_decode", videoParameters.GetProperty("gitBranch").GetString());
        AssertEqual("g4315520", videoParameters.GetProperty("gitCommit").GetString());
        AssertClose(session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(session.BlackIre)), JsonDouble(videoParameters, "black16bIre"), 1e-12);
        JsonElement fields = document.RootElement.GetProperty("fields");
        JsonElement first = fields[0];
        AssertFalse(first.TryGetProperty("detectedFirstField", out _));
        AssertFalse(first.TryGetProperty("isDuplicateField", out _));
        AssertClose(12.3, JsonDouble(first, "diskLoc"), 1e-12);
        AssertClose(27.513, JsonDouble(first, "medianBurstIRE"), 1e-12);
        AssertEqual(1, JsonInt(first, "fieldPhaseID"));
        JsonElement metrics = first.GetProperty("vitsMetrics");
        AssertClose(41.2, JsonDouble(metrics, "wSNR"), 1e-12);
        AssertClose(38.8, JsonDouble(metrics, "bPSNR"), 1e-12);
        AssertFalse(metrics.TryGetProperty("ignoredNaN", out _));
        JsonElement vbiData = first.GetProperty("vbi").GetProperty("vbiData");
        AssertEqual(2, vbiData.GetArrayLength());
        AssertEqual(0x12345, vbiData[0].GetInt32());
        AssertEqual(0x80E001, vbiData[1].GetInt32());

        JsonElement second = fields[1];
        AssertFalse(second.TryGetProperty("detectedFirstField", out _));
        AssertFalse(second.TryGetProperty("isDuplicateField", out _));
        AssertClose(1.0, JsonDouble(second, "diskLoc"), 1e-12);
        AssertClose(0.0, JsonDouble(second, "medianBurstIRE"), 1e-12);
        AssertEqual(1, JsonInt(second, "fieldPhaseID"));
        AssertEqual(2, JsonInt(second, "decodeFaults"));
        AssertTrue(second.TryGetProperty("vitsMetrics", out JsonElement secondMetrics));
        AssertEqual(JsonValueKind.Object, secondMetrics.ValueKind);
        AssertEqual(0, second.GetProperty("vbi").GetProperty("vbiData").GetArrayLength());

        string emptyMetricsJsonPath = Path.Combine(tempDirectory, "ld-empty-metrics.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(startSample: 0, samples: Array.Empty<ushort>())],
            emptyMetricsJsonPath);
        using JsonDocument emptyMetricsDocument = JsonDocument.Parse(File.ReadAllText(emptyMetricsJsonPath));
        JsonElement emptyMetrics = emptyMetricsDocument.RootElement.GetProperty("fields")[0].GetProperty("vitsMetrics");
        AssertEqual(JsonValueKind.Object, emptyMetrics.ValueKind);
        AssertEqual(0, JsonPropertyCount(emptyMetrics));
        string ldZeroNoiseJsonPath = Path.Combine(tempDirectory, "ld-zero-noise.tbc.json");
        ushort ldBlack = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(0.0));
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(
                startSample: 0,
                samples: Enumerable.Repeat(ldBlack, session.TbcFrameSpec.FieldSampleCount).ToArray())],
            ldZeroNoiseJsonPath);
        using JsonDocument ldZeroNoiseDocument = JsonDocument.Parse(File.ReadAllText(ldZeroNoiseJsonPath));
        AssertFalse(ldZeroNoiseDocument.RootElement.GetProperty("fields")[0]
            .GetProperty("vitsMetrics").TryGetProperty("bPSNR", out _));
        string sqliteEmptyDbPath = Path.Combine(tempDirectory, "ld-empty.tbc.db");
        TbcSqliteMetadataWriter.Write(
            session,
            [BuildSyntheticTbcField(startSample: 0, samples: Array.Empty<ushort>())],
            sqliteEmptyDbPath);
        AssertEqual(0L, SqliteLong(sqliteEmptyDbPath, "SELECT COUNT(*) FROM vits_metrics"));
        string roundedBurstDbPath = Path.Combine(tempDirectory, "ld-rounded-burst.tbc.db");
        TbcSqliteMetadataWriter.Write(session, [explicitField], roundedBurstDbPath);
        AssertClose(
            27.513,
            SqliteDouble(roundedBurstDbPath, "SELECT median_burst_ire FROM field_record WHERE field_id = 0"),
            1e-12);

        ushort[] metricSamples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        (int whiteLine, double whiteStart, double whiteLength) = FirstVitsSlice(session.Parameters.SysParams, "LD_VITS_whitelocs");
        ushort whiteOutput = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(100.0));
        PaintTbcMetricSlice(metricSamples, session, whiteLine, whiteStart, whiteLength, i => (ushort)(whiteOutput + (i % 2 == 0 ? -8 : 8)));
        (int blackLine, double blackStart, double blackLength) = FirstVitsSlice(session.Parameters.SysParams, "blacksnr_slice");
        ushort blackOutput = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(0.0));
        PaintTbcMetricSlice(metricSamples, session, blackLine, blackStart, blackLength, i => (ushort)(blackOutput + (i % 2 == 0 ? -6 : 6)));
        string computedMetricsJsonPath = Path.Combine(tempDirectory, "computed-metrics.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(startSample: 0, samples: metricSamples)],
            computedMetricsJsonPath);
        using JsonDocument computedMetricsDocument = JsonDocument.Parse(File.ReadAllText(computedMetricsJsonPath));
        JsonElement computedMetrics = computedMetricsDocument.RootElement.GetProperty("fields")[0].GetProperty("vitsMetrics");
        AssertTrue(JsonDouble(computedMetrics, "wSNR") > 0.0);
        AssertTrue(JsonDouble(computedMetrics, "bPSNR") > 0.0);
        AssertFalse(computedMetrics.TryGetProperty("whiteIRE", out _));

        string phaseFaultJsonPath = Path.Combine(tempDirectory, "phase-fault.tbc.json");
        TbcDecodedField phaseOne = BuildSyntheticTbcField(
                startSample: 0,
                samples: new ushort[session.TbcFrameSpec.FieldSampleCount])
            with
            {
                FieldPhaseId = 1
            };
        TbcDecodedField phaseThree = BuildSyntheticTbcField(
                startSample: oneFieldStart,
                samples: new ushort[session.TbcFrameSpec.FieldSampleCount])
            with
            {
                FieldPhaseId = 3
            };
        TbcOutputMetadataWriter.WriteJson(session, [phaseOne, phaseThree], phaseFaultJsonPath);
        using JsonDocument phaseFaultDocument = JsonDocument.Parse(File.ReadAllText(phaseFaultJsonPath));
        JsonElement phaseFaultFields = phaseFaultDocument.RootElement.GetProperty("fields");
        AssertEqual(0, JsonInt(phaseFaultFields[0], "decodeFaults"));
        AssertEqual(2, JsonInt(phaseFaultFields[1], "decodeFaults"));

        DecodeSession ntscAudioSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--noEFM",
            "--ntsc_audio_rate",
            "input.s16",
            Path.Combine(tempDirectory, "ntsc-audio")
        ]));
        string ntscAudioJsonPath = Path.Combine(tempDirectory, "ntsc-audio.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            ntscAudioSession,
            [BuildSyntheticTbcField(startSample: 0, samples: new ushort[ntscAudioSession.TbcFrameSpec.FieldSampleCount])],
            ntscAudioJsonPath);
        using JsonDocument ntscAudioDocument = JsonDocument.Parse(File.ReadAllText(ntscAudioJsonPath));
        double expectedNtscAudioRate = (1_000_000.0 / JsonDouble(ntscAudioSession.Parameters.SysParams, "line_period")) * 2.8;
        AssertClose(
            expectedNtscAudioRate,
            JsonDouble(ntscAudioDocument.RootElement.GetProperty("pcmAudioParameters"), "sampleRate"),
            1e-9);

        DecodeSession verboseSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "--verboseVITS",
            "input.s16",
            Path.Combine(tempDirectory, "verbose")
        ]));
        ushort normalLine19Code = verboseSession.VideoOutput.ConvertHz(verboseSession.VideoOutput.IreToHz(70.0));
        AssertFalse((bool)InvokePrivateStaticMethod(
            typeof(TbcOutputMetadataWriter),
            "IsNtscLine19ColorGateValid",
            Enumerable.Repeat(normalLine19Code, 32).ToArray(),
            0,
            32)!);
        AssertTrue((bool)InvokePrivateStaticMethod(
            typeof(TbcOutputMetadataWriter),
            "IsNtscLine19ColorGateValid",
            Enumerable.Repeat((ushort)70, 32).ToArray(),
            0,
            32)!);
        string verboseJsonPath = Path.Combine(tempDirectory, "verbose.tbc.json");
        ushort[] verboseFirstSamples = new ushort[verboseSession.TbcFrameSpec.FieldSampleCount];
        (int verboseWhiteLine, double verboseWhiteStart, double verboseWhiteLength) = FirstVitsSlice(verboseSession.Parameters.SysParams, "LD_VITS_whitelocs");
        ushort verboseWhiteOutput = verboseSession.VideoOutput.ConvertHz(verboseSession.VideoOutput.IreToHz(100.0));
        PaintTbcMetricSlice(verboseFirstSamples, verboseSession, verboseWhiteLine, verboseWhiteStart, verboseWhiteLength, i => (ushort)(verboseWhiteOutput + (i % 2 == 0 ? -8 : 8)));
        (int verboseBlackLine, double verboseBlackStart, double verboseBlackLength) = FirstVitsSlice(verboseSession.Parameters.SysParams, "blacksnr_slice");
        ushort verboseBlackOutput = verboseSession.VideoOutput.ConvertHz(verboseSession.VideoOutput.IreToHz(0.0));
        PaintTbcMetricSlice(verboseFirstSamples, verboseSession, verboseBlackLine, verboseBlackStart, verboseBlackLength, i => (ushort)(verboseBlackOutput + (i % 2 == 0 ? -6 : 6)));
        PaintTbcMetricSlice(verboseFirstSamples, verboseSession, 19, 0.0, 40.0, i => BuildNtscLine19ColorSample(verboseSession, i, phaseShift: 0));
        ushort verboseGreyOutput = verboseSession.VideoOutput.ConvertHz(verboseSession.VideoOutput.IreToHz(50.0));
        PaintTbcMetricSlice(verboseFirstSamples, verboseSession, 19, 36.0, 10.0, i => (ushort)(verboseGreyOutput + (i % 2 == 0 ? -7 : 7)));
        PaintTbcMetricSlice(verboseFirstSamples, verboseSession, 11, 15.0, 40.0, i => (ushort)(verboseWhiteOutput + (i % 2 == 0 ? -5 : 5)));
        ushort verboseBurstPrevious = verboseSession.VideoOutput.ConvertHz(verboseSession.VideoOutput.IreToHz(10.0));
        PaintTbcMetricSlice(verboseFirstSamples, verboseSession, 19, 5.5, 2.4, _ => verboseBurstPrevious);
        double[] verboseLineLocations = BuildPreTbcLineLocations(verboseSession);
        double[] verboseRawInput = new double[(int)Math.Ceiling(verboseLineLocations[^1] + JsonDouble(verboseSession.Parameters.SysParams, "line_period") * (verboseSession.DecodeSampleRateHz / 1_000_000.0))];
        double[] verbosePreTbcVideo = new double[verboseRawInput.Length];
        PaintPreTbcMetricSlice(
            verboseRawInput,
            verboseSession,
            verboseLineLocations,
            isFirstField: true,
            line: 19,
            startUsec: 36.0,
            lengthUsec: 10.0,
            delaySamples: (int)verboseSession.Filters.LdVideoWhiteOffset,
            sampleAt: i => i % 2 == 0 ? 30.0 : 36.0);
        PaintPreTbcMetricSlice(
            verboseRawInput,
            verboseSession,
            verboseLineLocations,
            isFirstField: true,
            line: verboseWhiteLine,
            startUsec: verboseWhiteStart,
            lengthUsec: verboseWhiteLength,
            delaySamples: (int)verboseSession.Filters.LdVideoWhiteOffset,
            sampleAt: i => i % 2 == 0 ? 20.0 : 24.48);
        PaintPreTbcMetricSlice(
            verboseRawInput,
            verboseSession,
            verboseLineLocations,
            isFirstField: true,
            line: verboseBlackLine,
            startUsec: verboseBlackStart,
            lengthUsec: verboseBlackLength,
            delaySamples: (int)verboseSession.Filters.LdVideoSyncOffset,
            sampleAt: i => i % 2 == 0 ? 8.0 : 14.52);
        PaintPreTbcMetricSlice(
            verbosePreTbcVideo,
            verboseSession,
            verboseLineLocations,
            isFirstField: true,
            line: verboseBlackLine,
            startUsec: verboseBlackStart,
            lengthUsec: verboseBlackLength,
            delaySamples: 0,
            sampleAt: _ => verboseSession.VideoOutput.IreToHz(7.5));

        ushort[] verboseSecondSamples = new ushort[verboseSession.TbcFrameSpec.FieldSampleCount];
        PaintTbcMetricSlice(verboseSecondSamples, verboseSession, 19, 0.0, 40.0, i => BuildNtscLine19ColorSample(verboseSession, i, phaseShift: 2));
        ushort verboseBurstCurrent = verboseSession.VideoOutput.ConvertHz(verboseSession.VideoOutput.IreToHz(30.0));
        PaintTbcMetricSlice(verboseSecondSamples, verboseSession, 19, 5.5, 2.4, _ => verboseBurstCurrent);
        TbcDecodedField cavFirst = BuildSyntheticTbcField(
                startSample: 0,
                samples: verboseFirstSamples,
                lineLocations: verboseLineLocations)
            with
            {
                RawInputSamples = verboseRawInput,
                PreTbcVideoSamples = verbosePreTbcVideo,
                VbiData = [0xF00123]
            };
        TbcDecodedField cavSecond = BuildSyntheticTbcField(
            startSample: oneFieldStart,
            samples: verboseSecondSamples);
        TbcDecodedField clvFirst = BuildSyntheticTbcField(
                startSample: oneFieldStart * 2,
                samples: new ushort[verboseSession.TbcFrameSpec.FieldSampleCount])
            with
            {
                VbiData = [0x8BE512, 0xF1DD23]
            };
        TbcDecodedField clvSecond = BuildSyntheticTbcField(
            startSample: oneFieldStart * 3,
            samples: new ushort[verboseSession.TbcFrameSpec.FieldSampleCount]);
        TbcOutputMetadataWriter.WriteJson(verboseSession, [cavFirst, cavSecond, clvFirst, clvSecond], verboseJsonPath);
        string verboseJson = File.ReadAllText(verboseJsonPath);
        AssertTrue(verboseJson.StartsWith(
            "{" + Environment.NewLine + "\"pcmAudioParameters\":{" + Environment.NewLine + "    \"bits\": 16",
            StringComparison.Ordinal));
        AssertTrue(verboseJson.Contains(Environment.NewLine + "\"videoParameters\":{", StringComparison.Ordinal));
        AssertTrue(verboseJson.Contains(
            Environment.NewLine + "\"fields\":[" + Environment.NewLine + "{" + Environment.NewLine + "    \"isFirstField\": true",
            StringComparison.Ordinal));
        AssertTrue(verboseJson.Contains("\"sampleRate\": 0", StringComparison.Ordinal));

        var floatMeanPreTbcVideo = new double[verboseRawInput.Length];
        PaintPreTbcMetricSlice(
            floatMeanPreTbcVideo,
            verboseSession,
            verboseLineLocations,
            isFirstField: true,
            line: verboseBlackLine,
            startUsec: verboseBlackStart,
            lengthUsec: verboseBlackLength,
            delaySamples: 0,
            sampleAt: i => i == 0
                ? verboseSession.VideoOutput.Ire0 - 0.5
                : verboseSession.VideoOutput.Ire0);
        string floatMeanJsonPath = Path.Combine(tempDirectory, "float-mean.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            verboseSession,
            [cavFirst with { PreTbcVideoSamples = floatMeanPreTbcVideo }, cavSecond],
            floatMeanJsonPath);
        string floatMeanJson = File.ReadAllText(floatMeanJsonPath);
        AssertTrue(floatMeanJson.Contains("\"blackLinePreTBCIRE\": 0.0", StringComparison.Ordinal));
        AssertFalse(floatMeanJson.Contains("\"blackLinePreTBCIRE\": -0.0", StringComparison.Ordinal));

        string pythonFloatJsonPath = Path.Combine(tempDirectory, "python-float.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(startSample: 0, samples: Array.Empty<ushort>()) with
            {
                VitsMetrics = new Dictionary<string, double>
                {
                    ["positiveZero"] = 0.0,
                    ["negativeZero"] = -0.0,
                    ["integerFloat"] = 1.0,
                    ["smallExponent"] = 1e-7
                }
            }],
            pythonFloatJsonPath);
        string pythonFloatJson = File.ReadAllText(pythonFloatJsonPath);
        AssertTrue(pythonFloatJson.Contains("\"positiveZero\":0.0", StringComparison.Ordinal));
        AssertTrue(pythonFloatJson.Contains("\"negativeZero\":-0.0", StringComparison.Ordinal));
        AssertTrue(pythonFloatJson.Contains("\"integerFloat\":1.0", StringComparison.Ordinal));
        AssertTrue(pythonFloatJson.Contains("\"smallExponent\":1e-07", StringComparison.Ordinal));
        using JsonDocument verboseDocument = JsonDocument.Parse(verboseJson);
        AssertEqual(4, JsonInt(verboseDocument.RootElement.GetProperty("videoParameters"), "numberOfSequentialFields"));
        JsonElement verboseFields = verboseDocument.RootElement.GetProperty("fields");
        AssertFalse(verboseFields[0].TryGetProperty("cavFrameNr", out _));
        AssertEqual(123, JsonInt(verboseFields[1], "cavFrameNr"));
        AssertEqual(((83 * 60) + 15) * 30 + 12, JsonInt(verboseFields[3], "cavFrameNr"));
        AssertEqual(83, JsonInt(verboseFields[3], "clvMinutes"));
        AssertEqual(15, JsonInt(verboseFields[3], "clvSeconds"));
        AssertEqual(12, JsonInt(verboseFields[3], "clvFrameNr"));
        JsonElement verboseFirstMetrics = verboseFields[0].GetProperty("vitsMetrics");
        AssertTrue(JsonDouble(verboseFirstMetrics, "ntscWhiteFlagSNR") > 0.0);
        double line19Phase = JsonDouble(verboseFirstMetrics, "ntscLine19ColorPhase");
        AssertTrue(line19Phase >= 0.0 && line19Phase < 360.0);
        AssertTrue(double.IsFinite(JsonDouble(verboseFirstMetrics, "ntscLine19ColorRawSNR")));
        AssertTrue(JsonDouble(verboseFirstMetrics, "greyPSNR") > 0.0);
        AssertClose(50.0, JsonDouble(verboseFirstMetrics, "greyIRE"), 0.2);
        AssertClose(3.0, JsonDouble(verboseFirstMetrics, "greyRFLevel"), 1e-12);
        AssertTrue(JsonDouble(verboseFirstMetrics, "wSNR") > 0.0);
        AssertClose(100.0, JsonDouble(verboseFirstMetrics, "whiteIRE"), 0.2);
        AssertClose(2.2, JsonDouble(verboseFirstMetrics, "whiteRFLevel"), 1e-12);
        AssertClose(7.5, JsonDouble(verboseFirstMetrics, "blackLinePreTBCIRE"), 1e-12);
        AssertClose(3.3, JsonDouble(verboseFirstMetrics, "blackLineRFLevel"), 1e-12);
        AssertClose(1.4554, JsonDouble(verboseFirstMetrics, "blackToWhiteRFRatio"), 1e-12);
        AssertClose(0.0, JsonDouble(verboseFirstMetrics, "blackLinePostTBCIRE"), 0.2);
        AssertTrue(JsonDouble(verboseFirstMetrics, "bPSNR") > 0.0);
        AssertStringSequence(
            [
                "ntscWhiteFlagSNR",
                "ntscLine19ColorPhase",
                "ntscLine19ColorRawSNR",
                "greyPSNR",
                "greyIRE",
                "greyRFLevel",
                "wSNR",
                "whiteIRE",
                "whiteRFLevel",
                "blackLineRFLevel",
                "blackLinePreTBCIRE",
                "blackLinePostTBCIRE",
                "bPSNR",
                "blackToWhiteRFRatio"
            ],
            verboseFirstMetrics.EnumerateObject().Select(property => property.Name).ToArray());
        JsonElement verboseSecondMetrics = verboseFields[1].GetProperty("vitsMetrics");
        AssertTrue(JsonDouble(verboseSecondMetrics, "ntscLine19Burst70IRE") > 0.0);
        AssertTrue(double.IsFinite(JsonDouble(verboseSecondMetrics, "ntscLine19Color3DRawSNR")));
        AssertClose(0.0, JsonDouble(verboseSecondMetrics, "ntscLine19Burst0IRE"), 0.0);
        AssertStringSequence(
            [
                "ntscLine19ColorPhase",
                "ntscLine19ColorRawSNR",
                "greyPSNR",
                "greyIRE",
                "ntscLine19Burst70IRE",
                "ntscLine19Color3DRawSNR",
                "ntscLine19Burst0IRE",
                "blackLinePostTBCIRE",
                "bPSNR"
            ],
            verboseSecondMetrics.EnumerateObject().Select(property => property.Name).ToArray());
        AssertFalse(verboseFields[3].GetProperty("vitsMetrics").TryGetProperty("ntscLine19Burst0IRE", out _));

        DecodeSession verbosePalSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--noEFM",
            "--disable_analog_audio",
            "--verboseVITS",
            "input.s16",
            Path.Combine(tempDirectory, "verbose-pal")
        ]));
        ushort[] verbosePalFirstSamples = new ushort[verbosePalSession.TbcFrameSpec.FieldSampleCount];
        ushort verbosePalGreyOutput = verbosePalSession.VideoOutput.ConvertHz(verbosePalSession.VideoOutput.IreToHz(50.0));
        PaintTbcMetricSlice(verbosePalFirstSamples, verbosePalSession, 13, 20.2, 3.0, i => (ushort)(verbosePalGreyOutput + (i % 2 == 0 ? -5 : 5)));
        ushort[] verbosePalSecondSamples = new ushort[verbosePalSession.TbcFrameSpec.FieldSampleCount];
        PaintTbcMetricSlice(verbosePalSecondSamples, verbosePalSession, 13, 36.0, 20.0, i => (ushort)(verbosePalGreyOutput + (i % 2 == 0 ? -5 : 5)));
        string verbosePalJsonPath = Path.Combine(tempDirectory, "verbose-pal.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            verbosePalSession,
            [
                BuildSyntheticTbcField(startSample: 0, samples: verbosePalFirstSamples),
                BuildSyntheticTbcField(startSample: 1, samples: verbosePalSecondSamples)
            ],
            verbosePalJsonPath);
        using JsonDocument verbosePalDocument = JsonDocument.Parse(File.ReadAllText(verbosePalJsonPath));
        JsonElement verbosePalFields = verbosePalDocument.RootElement.GetProperty("fields");
        JsonElement verbosePalFirstMetrics = verbosePalFields[0].GetProperty("vitsMetrics");
        AssertTrue(JsonDouble(verbosePalFirstMetrics, "greyPSNR") > 0.0);
        AssertClose(50.0, JsonDouble(verbosePalFirstMetrics, "greyIRE"), 0.2);
        JsonElement verbosePalSecondMetrics = verbosePalFields[1].GetProperty("vitsMetrics");
        double expectedPalBurst50Level = Math.Round(
            5.0 / verbosePalSession.VideoOutput.OutputScale,
            3,
            MidpointRounding.ToEven);
        AssertClose(expectedPalBurst50Level, JsonDouble(verbosePalSecondMetrics, "palVITSBurst50Level"), 0.0);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC metadata writer matches NumPy float64 standard deviation")]
public void TbcMetadataWriterMatchesNumpyFloat64StandardDeviation()
{
    double repeatedValue = BitConverter.UInt64BitsToDouble(0x40605381BDD2B899UL);
    double[] repeatedValues = Enumerable.Repeat(repeatedValue, 887).ToArray();
    double standardDeviation = TbcOutputMetadataWriter.StandardDeviationNumpyFloat64(repeatedValues);
    AssertEqual(0x3D20000000000000UL, BitConverter.DoubleToUInt64Bits(standardDeviation));

    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--pal",
            "--clamp_agc",
            "--agc_set_gain",
            "1.25",
            "input.s16",
            Path.Combine(tempDirectory, "clamped")
        ]));
        string jsonPath = Path.Combine(tempDirectory, "clamped.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(
                startSample: 0,
                samples: Enumerable.Repeat(ushort.MaxValue, session.TbcFrameSpec.FieldSampleCount).ToArray())],
            jsonPath);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement metrics = document.RootElement.GetProperty("fields")[0].GetProperty("vitsMetrics");
        AssertClose(310.9, JsonDouble(metrics, "bPSNR"), 0.0);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC metadata writer emits CVBS upstream fields")]
public void TbcMetadataWriterEmitsCvbsUpstreamFields()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--palm",
            "input.u8",
            Path.Combine(tempDirectory, "capture")
        ]));

        ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        (int whiteLine, double whiteStart, double whiteLength) = FirstVitsSlice(session.Parameters.SysParams, "LD_VITS_whitelocs");
        ushort whiteOutput = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(100.0));
        PaintTbcMetricSlice(samples, session, whiteLine, whiteStart, whiteLength, i => (ushort)(whiteOutput + (i % 2 == 0 ? -8 : 8)));
        (int blackLine, double blackStart, double blackLength) = FirstVitsSlice(session.Parameters.SysParams, "blacksnr_slice");
        ushort blackOutput = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(0.0));
        PaintTbcMetricSlice(samples, session, blackLine, blackStart, blackLength, i => (ushort)(blackOutput + (i % 2 == 0 ? -6 : 6)));

        TbcDecodedField field = BuildSyntheticTbcField(startSample: 0, samples: samples)
            with
            {
                MedianBurstIre = double.NaN,
                VbiData = [0x123456]
            };
        string jsonPath = Path.Combine(tempDirectory, "capture.tbc.json");

        TbcOutputMetadataWriter.WriteJson(session, [field], jsonPath);

        string compactJson = File.ReadAllText(jsonPath);
        AssertTrue(compactJson.StartsWith("{\"pcmAudioParameters\":", StringComparison.Ordinal));
        AssertFalse(compactJson.Contains(Environment.NewLine + " ", StringComparison.Ordinal));
        AssertTrue(compactJson.Contains(
            "\"fields\":[{\"isFirstField\":true,\"syncConf\":100,\"seqNo\":1,\"diskLoc\":0.0,\"fileLoc\":0,\"medianBurstIRE\":0.0",
            StringComparison.Ordinal));

        using JsonDocument document = JsonDocument.Parse(compactJson);
        JsonElement pcmAudio = document.RootElement.GetProperty("pcmAudioParameters");
        AssertEqual(16, JsonInt(pcmAudio, "bits"));
        AssertTrue(pcmAudio.GetProperty("isLittleEndian").GetBoolean());
        AssertTrue(pcmAudio.GetProperty("isSigned").GetBoolean());
        AssertClose(0.0, JsonDouble(pcmAudio, "sampleRate"), 1e-12);
        JsonElement video = document.RootElement.GetProperty("videoParameters");
        AssertEqual(1, JsonInt(video, "numberOfSequentialFields"));
        AssertEqual(DecodeVersionInfo.Version, video.GetProperty("version").GetString());
        AssertFalse(string.IsNullOrWhiteSpace(video.GetProperty("osInfo").GetString()));
        AssertEqual("vhs_decode", video.GetProperty("gitBranch").GetString());
        AssertEqual("g4315520", video.GetProperty("gitCommit").GetString());
        AssertEqual("PAL-M", video.GetProperty("system").GetString());
        AssertFalse(video.TryGetProperty("decoder", out _));
        AssertFalse(video.TryGetProperty("tapeFormat", out _));
        AssertClose(
            session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(session.BlackIre)),
            JsonDouble(video, "black16bIre"),
            1e-12);
        AssertClose(
            session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(100.0)),
            JsonDouble(video, "white16bIre"),
            1e-12);

        JsonElement fieldInfo = document.RootElement.GetProperty("fields")[0];
        AssertClose(0.0, JsonDouble(fieldInfo, "diskLoc"), 1e-12);
        AssertClose(0.0, JsonDouble(fieldInfo, "medianBurstIRE"), 1e-12);
        AssertEqual(0, JsonInt(fieldInfo, "fieldPhaseID"));
        AssertEqual(0, JsonInt(fieldInfo, "decodeFaults"));
        AssertFalse(fieldInfo.TryGetProperty("audioSamples", out _));
        AssertFalse(fieldInfo.TryGetProperty("detectedFirstField", out _));
        AssertFalse(fieldInfo.TryGetProperty("isDuplicateField", out _));
        JsonElement metrics = fieldInfo.GetProperty("vitsMetrics");
        AssertTrue(JsonDouble(metrics, "wSNR") > 0.0);
        AssertTrue(JsonDouble(metrics, "bPSNR") > 0.0);
        JsonElement vbiData = fieldInfo.GetProperty("vbi").GetProperty("vbiData");
        AssertEqual(1, vbiData.GetArrayLength());
        AssertEqual(0x123456, vbiData[0].GetInt32());

        string palmDbPath = Path.Combine(tempDirectory, "palm.tbc.db");
        AssertThrows<SqliteException>(() => TbcSqliteMetadataWriter.Write(session, [field], palmDbPath));

        DecodeSession dbSession = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--ntsc",
            "input.u8",
            Path.Combine(tempDirectory, "db-capture")
        ]));
        TbcDecodedField dbField = BuildSyntheticTbcField(
                startSample: 0,
                samples: new ushort[dbSession.TbcFrameSpec.FieldSampleCount])
            with
            {
                MedianBurstIre = 12.345,
                VbiData = [0x123456],
                VitsMetrics = new Dictionary<string, double>
                {
                    ["wSNR"] = 12.5,
                    ["bPSNR"] = 9.75
                }
            };
        string dbPath = Path.Combine(tempDirectory, "capture.tbc.db");
        TbcSqliteMetadataWriter.Write(dbSession, [dbField], dbPath);
        AssertEqual(1L, SqliteLong(dbPath, "SELECT capture_id FROM capture"));
        AssertEqual(0L, SqliteLong(dbPath, "SELECT capture_id FROM field_record WHERE field_id = 0"));
        AssertEqual(0L, SqliteLong(dbPath, "SELECT capture_id FROM vits_metrics WHERE field_id = 0"));
        AssertEqual(0L, SqliteLong(dbPath, "SELECT capture_id FROM vbi WHERE field_id = 0"));
        AssertEqual(1L, SqliteLong(dbPath, "SELECT audio_samples IS NULL FROM field_record WHERE field_id = 0"));
        AssertEqual(1L, SqliteLong(dbPath, "SELECT efm_t_values IS NULL FROM field_record WHERE field_id = 0"));
        AssertEqual(1L, SqliteLong(dbPath, "SELECT median_burst_ire IS NULL FROM field_record WHERE field_id = 0"));

        long cvbsFieldSamples = ((long)(dbSession.DecodeSampleRateHz
            / (JsonDouble(dbSession.Parameters.SysParams, "FPS") * 2.0))) + 1;
        string ntscPhasePath = Path.Combine(tempDirectory, "ntsc-phase.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            dbSession,
            [
                BuildSyntheticTbcField(0, Array.Empty<ushort>()),
                BuildSyntheticTbcField(cvbsFieldSamples, Array.Empty<ushort>())
            ],
            ntscPhasePath);
        using JsonDocument ntscPhaseDocument = JsonDocument.Parse(File.ReadAllText(ntscPhasePath));
        JsonElement ntscPhaseFields = ntscPhaseDocument.RootElement.GetProperty("fields");
        AssertEqual(1, JsonInt(ntscPhaseFields[0], "fieldPhaseID"));
        AssertEqual(1, JsonInt(ntscPhaseFields[1], "fieldPhaseID"));
        AssertEqual(2, JsonInt(ntscPhaseFields[1], "decodeFaults"));

        DecodeSession palSession = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--pal",
            "input.u8",
            Path.Combine(tempDirectory, "pal-phase")
        ]));
        string palPhasePath = Path.Combine(tempDirectory, "pal-phase.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            palSession,
            [
                BuildSyntheticTbcField(0, Array.Empty<ushort>()),
                BuildSyntheticTbcField(1, Array.Empty<ushort>())
            ],
            palPhasePath);
        using JsonDocument palPhaseDocument = JsonDocument.Parse(File.ReadAllText(palPhasePath));
        JsonElement palPhaseFields = palPhaseDocument.RootElement.GetProperty("fields");
        AssertEqual(1, JsonInt(palPhaseFields[0], "fieldPhaseID"));
        AssertEqual(2, JsonInt(palPhaseFields[1], "fieldPhaseID"));
        AssertEqual(0, JsonInt(palPhaseFields[1], "decodeFaults"));

        string explicitPalPhasePath = Path.Combine(tempDirectory, "pal-explicit-phase.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            palSession,
            [
                BuildSyntheticTbcField(0, Array.Empty<ushort>()) with { FieldPhaseId = 7 },
                BuildSyntheticTbcField(1, Array.Empty<ushort>()) with { FieldPhaseId = 2 }
            ],
            explicitPalPhasePath);
        using JsonDocument explicitPalPhaseDocument = JsonDocument.Parse(File.ReadAllText(explicitPalPhasePath));
        JsonElement explicitPalPhaseFields = explicitPalPhaseDocument.RootElement.GetProperty("fields");
        AssertEqual(7, JsonInt(explicitPalPhaseFields[0], "fieldPhaseID"));
        AssertEqual(2, JsonInt(explicitPalPhaseFields[1], "fieldPhaseID"));
        AssertEqual(2, JsonInt(explicitPalPhaseFields[1], "decodeFaults"));
        AssertContains(
            File.ReadAllText(Path.Combine(tempDirectory, "pal-phase.log")),
            " - lddecode - WARNING - At field #1, Field phaseID sequence mismatch (7->2) (player may be paused)");

        string sequencedDbPath = Path.Combine(tempDirectory, "sequenced.tbc.db");
        TbcSqliteMetadataWriter.Write(
            dbSession,
            [dbField],
            sequencedDbPath,
            [new TbcFieldOrderDecision(
                SeqNo: 7,
                IsFirstField: true,
                DetectedFirstField: true,
                IsDuplicateField: false,
                WriteField: true,
                SyncConfidence: 100,
                DecodeFaults: 0)]);
        AssertEqual(6L, SqliteLong(sequencedDbPath, "SELECT field_id FROM field_record"));

        string emptyMetricsJsonPath = Path.Combine(tempDirectory, "empty-metrics.tbc.json");
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(startSample: 0, samples: Array.Empty<ushort>())],
            emptyMetricsJsonPath);
        using JsonDocument emptyMetricsDocument = JsonDocument.Parse(File.ReadAllText(emptyMetricsJsonPath));
        JsonElement emptyMetrics = emptyMetricsDocument.RootElement.GetProperty("fields")[0].GetProperty("vitsMetrics");
        AssertEqual(JsonValueKind.Object, emptyMetrics.ValueKind);
        AssertEqual(0, JsonPropertyCount(emptyMetrics));
        string cvbsZeroNoiseJsonPath = Path.Combine(tempDirectory, "cvbs-zero-noise.tbc.json");
        ushort cvbsBlack = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(0.0));
        TbcOutputMetadataWriter.WriteJson(
            session,
            [BuildSyntheticTbcField(
                startSample: 0,
                samples: Enumerable.Repeat(cvbsBlack, session.TbcFrameSpec.FieldSampleCount).ToArray())],
            cvbsZeroNoiseJsonPath);
        using JsonDocument cvbsZeroNoiseDocument = JsonDocument.Parse(File.ReadAllText(cvbsZeroNoiseJsonPath));
        AssertClose(
            0.0,
            JsonDouble(cvbsZeroNoiseDocument.RootElement.GetProperty("fields")[0].GetProperty("vitsMetrics"), "bPSNR"),
            0.0);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine applies field-order writes")]
public void TbcFieldSequenceEngineAppliesFieldOrderWrites()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        DecodeSession duplicateSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--orc",
            "--field_order_action",
            "duplicate",
            "input.u8",
            Path.Combine(tempDirectory, "duplicate.tbc")
        ]));
        DecodeSession dropSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--field_order_action",
            "drop",
            "input.u8",
            Path.Combine(tempDirectory, "drop.tbc")
        ]));

        TbcDecodedField[] fields =
        [
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 0.0), 0x1111, true, 0xA111),
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 1.0), 0x2222, false, 0xA222),
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 2.0), 0x3333, true, 0xA333),
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 3.0), 0x4444, true, 0xA444),
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 4.0), 0x5555, false, 0xA555)
        ];

        var engine = new TbcFieldSequenceDecodeEngine();
        TbcFieldSequenceDecodeResult duplicateResult = engine.WriteDecodedFields(duplicateSession, fields);
        AssertTrue(duplicateResult.Success);
        AssertEqual(duplicateSession.TbcFrameSpec.FieldSampleCount * 12L, new FileInfo(duplicateResult.Paths!.TbcPath).Length);
        byte[] duplicateBytes = File.ReadAllBytes(duplicateResult.Paths.TbcPath);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0x1111);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0x2222);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 2, expected: 0x3333);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 3, expected: 0x2222);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 4, expected: 0x4444);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 5, expected: 0x5555);
        AssertEqual(Path.Combine(tempDirectory, "duplicate.tbc.tbcc"), duplicateResult.Paths.ChromaPath);
        AssertEqual(duplicateSession.TbcFrameSpec.FieldSampleCount * 12L, new FileInfo(duplicateResult.Paths.ChromaPath!).Length);
        byte[] duplicateChroma = File.ReadAllBytes(duplicateResult.Paths.ChromaPath!);
        AssertFieldFirstSample(duplicateChroma, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0xA111);
        AssertFieldFirstSample(duplicateChroma, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0xA222);
        AssertFieldFirstSample(duplicateChroma, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 2, expected: 0xA333);
        AssertFieldFirstSample(duplicateChroma, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 3, expected: 0xA222);
        AssertFieldFirstSample(duplicateChroma, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 4, expected: 0xA444);
        AssertFieldFirstSample(duplicateChroma, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 5, expected: 0xA555);

        using JsonDocument duplicateJson = JsonDocument.Parse(File.ReadAllText(duplicateResult.Paths.JsonPath));
        JsonElement duplicateFields = duplicateJson.RootElement.GetProperty("fields");
        AssertEqual(6, duplicateFields.GetArrayLength());
        AssertEqual(2, JsonInt(duplicateFields[3], "seqNo"));
        AssertEqual(4, JsonInt(duplicateFields[4], "seqNo"));
        AssertTrue(duplicateFields[4].GetProperty("isDuplicateField").GetBoolean());
        AssertEqual(6, JsonInt(duplicateFields[5], "seqNo"));

        TbcFieldSequenceDecodeResult dropResult = engine.WriteDecodedFields(dropSession, fields);
        AssertTrue(dropResult.Success);
        AssertEqual(dropSession.TbcFrameSpec.FieldSampleCount * 8L, new FileInfo(dropResult.Paths!.TbcPath).Length);
        byte[] dropBytes = File.ReadAllBytes(dropResult.Paths.TbcPath);
        AssertFieldFirstSample(dropBytes, dropSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0x1111);
        AssertFieldFirstSample(dropBytes, dropSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0x2222);
        AssertFieldFirstSample(dropBytes, dropSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 2, expected: 0x3333);
        AssertFieldFirstSample(dropBytes, dropSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 3, expected: 0x5555);

        using JsonDocument dropJson = JsonDocument.Parse(File.ReadAllText(dropResult.Paths.JsonPath));
        JsonElement dropFields = dropJson.RootElement.GetProperty("fields");
        AssertEqual(4, dropFields.GetArrayLength());
        AssertEqual(3, JsonInt(dropFields[2], "seqNo"));
        AssertEqual(4, JsonInt(dropFields[3], "seqNo"));

        DecodeSession ldSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "input.s16",
            Path.Combine(tempDirectory, "ld-close.tbc")
        ]));
        TbcDecodedField[] ldCloseFields =
        [
            BuildSequenceField(ldSession, FieldStart(ldSession, 0.0), 0x1111, true),
            BuildSequenceField(ldSession, FieldStart(ldSession, 1.0), 0x2222, true)
        ];

        TbcFieldSequenceDecodeResult ldCloseResult = engine.WriteDecodedFields(ldSession, ldCloseFields);
        AssertTrue(ldCloseResult.Success);
        AssertEqual(ldSession.TbcFrameSpec.FieldSampleCount * 4L, new FileInfo(ldCloseResult.Paths!.TbcPath).Length);
        using JsonDocument ldCloseJson = JsonDocument.Parse(File.ReadAllText(ldCloseResult.Paths.JsonPath));
        JsonElement ldCloseJsonFields = ldCloseJson.RootElement.GetProperty("fields");
        AssertEqual(2, ldCloseJsonFields.GetArrayLength());
        AssertTrue(ldCloseJsonFields[0].GetProperty("isFirstField").GetBoolean());
        AssertFalse(ldCloseJsonFields[1].GetProperty("isFirstField").GetBoolean());
        AssertEqual(10, JsonInt(ldCloseJsonFields[1], "syncConf"));
        AssertEqual(3, JsonInt(ldCloseJsonFields[1], "decodeFaults"));

        DecodeSession ldSkippedSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "input.s16",
            Path.Combine(tempDirectory, "ld-skipped.tbc")
        ]));
        TbcDecodedField[] ldSkippedFields =
        [
            BuildSequenceField(ldSkippedSession, FieldStart(ldSkippedSession, 0.0), 0x1111, true) with { FieldPhaseId = 1 },
            BuildSequenceField(ldSkippedSession, FieldStart(ldSkippedSession, 1.0), 0x2222, false) with { FieldPhaseId = 2 },
            BuildSequenceField(ldSkippedSession, FieldStart(ldSkippedSession, 2.0), 0x3333, true) with { FieldPhaseId = 3 },
            BuildSequenceField(ldSkippedSession, FieldStart(ldSkippedSession, 3.4), 0x4444, true) with { FieldPhaseId = 4 }
        ];

        TbcFieldSequenceDecodeResult ldSkippedResult = engine.WriteDecodedFields(ldSkippedSession, ldSkippedFields);
        AssertTrue(ldSkippedResult.Success);
        AssertEqual(ldSkippedSession.TbcFrameSpec.FieldSampleCount * 10L, new FileInfo(ldSkippedResult.Paths!.TbcPath).Length);
        byte[] ldSkippedBytes = File.ReadAllBytes(ldSkippedResult.Paths.TbcPath);
        AssertFieldFirstSample(ldSkippedBytes, ldSkippedSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0x1111);
        AssertFieldFirstSample(ldSkippedBytes, ldSkippedSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0x2222);
        AssertFieldFirstSample(ldSkippedBytes, ldSkippedSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 2, expected: 0x3333);
        AssertFieldFirstSample(ldSkippedBytes, ldSkippedSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 3, expected: 0x2222);
        AssertFieldFirstSample(ldSkippedBytes, ldSkippedSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 4, expected: 0x4444);
        using JsonDocument ldSkippedJson = JsonDocument.Parse(File.ReadAllText(ldSkippedResult.Paths.JsonPath));
        JsonElement ldSkippedJsonFields = ldSkippedJson.RootElement.GetProperty("fields");
        AssertEqual(5, ldSkippedJsonFields.GetArrayLength());
        AssertEqual(ldSkippedJsonFields[1].GetRawText(), ldSkippedJsonFields[3].GetRawText());
        AssertEqual(4, JsonInt(ldSkippedJsonFields[4], "seqNo"));
        AssertEqual(0, JsonInt(ldSkippedJsonFields[4], "syncConf"));
        AssertFalse(ldSkippedJsonFields[4].TryGetProperty("decodeFaults", out _));
        AssertFalse(ldSkippedJsonFields[4].TryGetProperty("vitsMetrics", out _));
        AssertFalse(ldSkippedJsonFields[4].TryGetProperty("vbi", out _));
        AssertTrue(ldSkippedJsonFields[4].TryGetProperty("audioSamples", out _));
        AssertTrue(ldSkippedJsonFields[4].TryGetProperty("efmTValues", out _));
        AssertEqual(1L, SqliteLong(ldSkippedResult.Paths.DbPath!, "SELECT decode_faults IS NULL FROM field_record WHERE field_id = 3"));
        AssertEqual(1L, SqliteLong(ldSkippedResult.Paths.DbPath!, "SELECT decode_faults IS NULL FROM field_record WHERE field_id = 4"));
        AssertEqual(0L, SqliteLong(ldSkippedResult.Paths.DbPath!, "SELECT COUNT(*) FROM vits_metrics WHERE field_id = 4"));
        AssertEqual(0L, SqliteLong(ldSkippedResult.Paths.DbPath!, "SELECT COUNT(*) FROM vbi WHERE field_id = 4"));

        DecodeSession detectSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "input.u8",
            Path.Combine(tempDirectory, "detect-state")
        ]));
        TbcDecodedField[] detectFields =
        [
            BuildSequenceField(detectSession, FieldStart(detectSession, 0.0), 0x1000, false),
            BuildSequenceField(detectSession, FieldStart(detectSession, 1.0), 0x1111, true),
            BuildSequenceField(detectSession, FieldStart(detectSession, 2.0), 0x2222, true),
            BuildSequenceField(detectSession, FieldStart(detectSession, 3.0), 0x3333, true),
            BuildSequenceField(detectSession, FieldStart(detectSession, 4.0), 0x4444, false),
            BuildSequenceField(detectSession, FieldStart(detectSession, 5.5), 0x5555, false)
        ];

        TbcFieldSequenceDecodeResult detectResult = engine.WriteDecodedFields(detectSession, detectFields);
        AssertEqual(4, detectResult.WrittenFieldCount);
        byte[] detectBytes = File.ReadAllBytes(detectResult.Paths!.TbcPath);
        AssertFieldFirstSample(detectBytes, detectSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0x1111);
        AssertFieldFirstSample(detectBytes, detectSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0x4444);
        AssertFieldFirstSample(detectBytes, detectSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 2, expected: 0x3333);
        AssertFieldFirstSample(detectBytes, detectSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 3, expected: 0x5555);
        using JsonDocument detectJson = JsonDocument.Parse(File.ReadAllText(detectResult.Paths.JsonPath));
        JsonElement detectJsonFields = detectJson.RootElement.GetProperty("fields");
        AssertEqual(4, detectJsonFields.GetArrayLength());
        AssertEqual(2, JsonInt(detectJsonFields[2], "seqNo"));
        AssertEqual(3, JsonInt(detectJsonFields[3], "seqNo"));

        DecodeSession ldRawOrderSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "input.s16",
            Path.Combine(tempDirectory, "ld-raw-order")
        ]));
        TbcDecodedField[] ldRawOrderFields =
        [
            BuildSequenceField(ldRawOrderSession, FieldStart(ldRawOrderSession, 0.0), 0x1111, true),
            BuildSequenceField(ldRawOrderSession, FieldStart(ldRawOrderSession, 1.0), 0x2222, false),
            BuildSequenceField(ldRawOrderSession, FieldStart(ldRawOrderSession, 2.0), 0x3333, true),
            BuildSequenceField(ldRawOrderSession, FieldStart(ldRawOrderSession, 3.0), 0x4444, true),
            BuildSequenceField(ldRawOrderSession, FieldStart(ldRawOrderSession, 4.4), 0x5555, false)
        ];

        TbcFieldSequenceDecodeResult ldRawOrderResult = engine.WriteDecodedFields(ldRawOrderSession, ldRawOrderFields);
        AssertEqual(6, ldRawOrderResult.WrittenFieldCount);
        byte[] ldRawOrderBytes = File.ReadAllBytes(ldRawOrderResult.Paths!.TbcPath);
        AssertFieldFirstSample(ldRawOrderBytes, ldRawOrderSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0x1111);
        AssertFieldFirstSample(ldRawOrderBytes, ldRawOrderSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0x2222);
        AssertFieldFirstSample(ldRawOrderBytes, ldRawOrderSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 2, expected: 0x3333);
        AssertFieldFirstSample(ldRawOrderBytes, ldRawOrderSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 3, expected: 0x4444);
        AssertFieldFirstSample(ldRawOrderBytes, ldRawOrderSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 4, expected: 0x4444);
        AssertFieldFirstSample(ldRawOrderBytes, ldRawOrderSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 5, expected: 0x5555);
        using JsonDocument ldRawOrderJson = JsonDocument.Parse(File.ReadAllText(ldRawOrderResult.Paths.JsonPath));
        JsonElement ldRawOrderJsonFields = ldRawOrderJson.RootElement.GetProperty("fields");
        AssertFalse(ldRawOrderJsonFields[5].TryGetProperty("decodeFaults", out _));
        AssertFalse(ldRawOrderJsonFields[5].TryGetProperty("vitsMetrics", out _));
        AssertFalse(ldRawOrderJsonFields[5].TryGetProperty("vbi", out _));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC streaming metadata reuses LD filler field info")]
public void TbcStreamingMetadataReusesLdFillerFieldInfo()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "input.s16",
            Path.Combine(tempDirectory, "ld-streaming")
        ]));
        TbcDecodedField[] fields =
        [
            BuildSequenceField(session, FieldStart(session, 0.0), 0x1111, true) with { FieldPhaseId = 1 },
            BuildSequenceField(session, FieldStart(session, 1.0), 0x2222, false) with { FieldPhaseId = 2 },
            BuildSequenceField(session, FieldStart(session, 2.0), 0x3333, true) with { FieldPhaseId = 3 },
            BuildSequenceField(session, FieldStart(session, 3.4), 0x4444, true) with { FieldPhaseId = 4 }
        ];

        TbcDecodedField? ReadField(DecodeSession _, Stream __, long ___, int ____, int fieldNumber)
            => fieldNumber < fields.Length ? fields[fieldNumber] : null;

        TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine(readField: ReadField)
            .TryDecodeAndWrite(session, Stream.Null);

        AssertTrue(result.Success);
        AssertEqual(5, result.WrittenFieldCount);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths!.JsonPath));
        JsonElement outputFields = document.RootElement.GetProperty("fields");
        AssertEqual(5, outputFields.GetArrayLength());
        AssertEqual(outputFields[1].GetRawText(), outputFields[3].GetRawText());
        AssertEqual(
            1L,
            SqliteLong(result.Paths.DbPath!, "SELECT decode_faults IS NULL FROM field_record WHERE field_id = 3"));
        AssertFalse(
            File.ReadAllText(session.OutputBase + ".log")
                .Contains("Field phaseID sequence mismatch (3->2)", StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine applies CVBS LD-style field-order writes")]
public void TbcFieldSequenceEngineAppliesCvbsLdStyleFieldOrderWrites()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, [
            "--pal",
            "input.s16",
            Path.Combine(tempDirectory, "cvbs-order")
        ]));
        TbcDecodedField[] fields =
        [
            BuildSequenceField(session, FieldStart(session, 0.0), 0x1111, true) with { FieldPhaseId = 1 },
            BuildSequenceField(session, FieldStart(session, 1.0), 0x2222, true) with { FieldPhaseId = 2 },
            BuildSequenceField(session, FieldStart(session, 2.4), 0x3333, false) with { FieldPhaseId = 3 }
        ];

        var engine = new TbcFieldSequenceDecodeEngine();
        TbcFieldSequenceDecodeResult result = engine.WriteDecodedFields(session, fields);

        AssertTrue(result.Success);
        AssertEqual(4, result.WrittenFieldCount);
        byte[] bytes = File.ReadAllBytes(result.Paths!.TbcPath);
        AssertFieldFirstSample(bytes, session.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0x1111);
        AssertFieldFirstSample(bytes, session.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0x2222);
        AssertFieldFirstSample(bytes, session.TbcFrameSpec.FieldSampleCount, fieldIndex: 2, expected: 0x2222);
        AssertFieldFirstSample(bytes, session.TbcFrameSpec.FieldSampleCount, fieldIndex: 3, expected: 0x3333);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths.JsonPath));
        JsonElement outputFields = document.RootElement.GetProperty("fields");
        AssertEqual(4, outputFields.GetArrayLength());
        AssertFalse(outputFields[1].GetProperty("isFirstField").GetBoolean());
        AssertEqual(10, JsonInt(outputFields[1], "syncConf"));
        AssertEqual(1, JsonInt(outputFields[1], "decodeFaults"));
        AssertEqual(2, JsonInt(outputFields[2], "seqNo"));
        AssertEqual(outputFields[1].GetRawText(), outputFields[2].GetRawText());
        AssertFalse(outputFields[3].TryGetProperty("decodeFaults", out _));
        AssertFalse(outputFields[3].TryGetProperty("vitsMetrics", out _));
        AssertFalse(outputFields[3].TryGetProperty("vbi", out _));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine counts written fields for length")]
public void TbcFieldSequenceEngineCountsWrittenFieldsForLength()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        DecodeSession dropSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--length",
            "1",
            "--field_order_action",
            "drop",
            "input.u8",
            Path.Combine(tempDirectory, "drop-length")
        ]));
        TbcDecodedField[] dropFields =
        [
            BuildSequenceField(dropSession, FieldStart(dropSession, 0.0), 0x1111, true),
            BuildSequenceField(dropSession, FieldStart(dropSession, 1.0), 0x2222, true),
            BuildSequenceField(dropSession, FieldStart(dropSession, 2.0), 0x3333, false)
        ];
        int dropReads = 0;
        TbcDecodedField? ReadDropField(DecodeSession _, Stream __, long ___, int ____, int fieldNumber)
        {
            dropReads++;
            return fieldNumber < dropFields.Length ? dropFields[fieldNumber] : null;
        }

        var dropEngine = new TbcFieldSequenceDecodeEngine(readField: ReadDropField);
        IReadOnlyList<TbcDecodedField> decodedDropFields = dropEngine.DecodeFields(dropSession, Stream.Null);
        AssertEqual(3, decodedDropFields.Count);
        AssertEqual(4, dropReads);
        TbcFieldSequenceDecodeResult dropResult = dropEngine.WriteDecodedFields(dropSession, decodedDropFields);
        AssertEqual(2, dropResult.WrittenFieldCount);
        byte[] dropBytes = File.ReadAllBytes(dropResult.Paths!.TbcPath);
        AssertFieldFirstSample(dropBytes, dropSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0x1111);
        AssertFieldFirstSample(dropBytes, dropSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0x3333);

        dropReads = 0;
        IReadOnlyList<TbcDecodedField> explicitlyCapped = dropEngine.DecodeFields(dropSession, Stream.Null, maxFields: 2);
        AssertEqual(2, explicitlyCapped.Count);
        AssertEqual(2, dropReads);

        DecodeSession duplicateSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, [
            "--pal",
            "--length",
            "2",
            "--field_order_action",
            "duplicate",
            "input.u8",
            Path.Combine(tempDirectory, "duplicate-length")
        ]));
        TbcDecodedField[] duplicateFields =
        [
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 0.0), 0x1111, true),
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 1.0), 0x2222, false),
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 2.4), 0x3333, false),
            BuildSequenceField(duplicateSession, FieldStart(duplicateSession, 3.4), 0x4444, true)
        ];
        int duplicateReads = 0;
        TbcDecodedField? ReadDuplicateField(DecodeSession _, Stream __, long ___, int ____, int fieldNumber)
        {
            duplicateReads++;
            return fieldNumber < duplicateFields.Length ? duplicateFields[fieldNumber] : null;
        }

        var duplicateEngine = new TbcFieldSequenceDecodeEngine(readField: ReadDuplicateField);
        IReadOnlyList<TbcDecodedField> decodedDuplicateFields = duplicateEngine.DecodeFields(duplicateSession, Stream.Null);
        AssertEqual(3, decodedDuplicateFields.Count);
        AssertEqual(4, duplicateReads);
        TbcFieldSequenceDecodeResult duplicateResult = duplicateEngine.WriteDecodedFields(duplicateSession, decodedDuplicateFields);
        AssertEqual(4, duplicateResult.WrittenFieldCount);
        byte[] duplicateBytes = File.ReadAllBytes(duplicateResult.Paths!.TbcPath);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 0, expected: 0x1111);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 1, expected: 0x2222);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 2, expected: 0x1111);
        AssertFieldFirstSample(duplicateBytes, duplicateSession.TbcFrameSpec.FieldSampleCount, fieldIndex: 3, expected: 0x3333);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field sequence engine computes LD test LDF range")]
public void TbcFieldSequenceEngineComputesLdTestLdfRange()
{
    DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--start_fileloc",
        "100",
        "--write-test-ldf",
        "sample.ldf",
        "input.s16",
        "out"
    ]));
    TbcDecodedField field = BuildSyntheticTbcField(
        startSample: 100,
        samples: new ushort[session.TbcFrameSpec.FieldSampleCount],
        lineLocations: BuildLineLocationsForAdvance(session, 300.0));

    (long StartSample, long EndSample)? range = TbcFieldSequenceDecodeEngine.ComputeTestLdfSampleRange(session, [field]);
    AssertEqual(100L, range!.Value.StartSample);
    AssertEqual(1_100_400L, range.Value.EndSample);

    DecodeSession seekSession = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, [
        "--PAL",
        "--seek",
        "42",
        "--write-test-ldf",
        "sample.ldf",
        "input.s16",
        "out"
    ]));
    TbcDecodedField seekedField = BuildSyntheticTbcField(
        startSample: 55_000,
        samples: new ushort[seekSession.TbcFrameSpec.FieldSampleCount],
        lineLocations: BuildLineLocationsForAdvance(seekSession, 400.0));
    (long StartSample, long EndSample)? seekRange = TbcFieldSequenceDecodeEngine.ComputeTestLdfSampleRange(seekSession, [seekedField]);
    AssertEqual(55_000L, seekRange!.Value.StartSample);
    AssertEqual(1_155_400L, seekRange.Value.EndSample);

    (long StartSample, long EndSample)? emptyRange = TbcFieldSequenceDecodeEngine.ComputeTestLdfSampleRange(session, []);
    AssertEqual(100L, emptyRange!.Value.StartSample);
    AssertEqual(1_100_100L, emptyRange.Value.EndSample);

    DecodeSession noOutput = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, ["--PAL", "input.s16", "out"]));
    AssertEqual<(long StartSample, long EndSample)?>(null, TbcFieldSequenceDecodeEngine.ComputeTestLdfSampleRange(noOutput, [field]));
}

[Fact(DisplayName = "LD test LDF writer copies input samples")]
public void LdTestLdfWriterCopiesInputSamples()
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.s16");
        string ldfPath = Path.Combine(tempDirectory, "bug.ldf");
        string outputBase = Path.Combine(tempDirectory, "capture");
        File.WriteAllBytes(inputPath, BuildPcm16Bytes([short.MinValue, -1, 0, short.MaxValue, 123]));
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, ["--write-test-ldf", ldfPath, inputPath, outputBase]));
        using var captured = new MemoryStream();
        var writer = new FfmpegLdTestLdfWriter(_ => captured, chunkSamples: 2);
        using FileStream input = File.OpenRead(inputPath);

        LdTestLdfWriteResult result = writer.Write(session, startSample: 1, endSample: 4, input);

        AssertTrue(result.Success);
        AssertEqual(3L, result.SamplesWritten);
        AssertEqual(1L, result.StartSample);
        AssertEqual(4L, result.EndSample);
        AssertEqual(ldfPath, result.OutputPath);
        AssertEqual<long?>(null, result.ShortReadSample);
        AssertSequence([-1, 0, short.MaxValue], ReadInt16Samples(captured.ToArray()));

        IReadOnlyList<string> args = FfmpegLdTestLdfWriter.BuildFfmpegArguments(ldfPath);
        string joined = string.Join(" ", args);
        AssertContains(joined, "-f s16le -ar 40k -ac 1 -i -");
        AssertContains(joined, "-acodec flac -f ogg -compression_level 6");
        AssertEqual(ldfPath, args[^1]);
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "TBC field order planner handles duplicate drop and none actions")]
public void TbcFieldOrderPlannerHandlesDuplicateDropAndNoneActions()
{
    var duplicate = new TbcFieldOrderPlanner(TbcFieldOrderAction.Duplicate);
    TbcFieldOrderDecision duplicateFirst = duplicate.Plan(new TbcFieldOrderInput(0, true));
    TbcFieldOrderDecision duplicateSecond = duplicate.Plan(new TbcFieldOrderInput(100, true), distanceFromPreviousField: 1.2);
    AssertTrue(duplicateFirst.WriteField);
    AssertTrue(duplicateSecond.WriteField);
    AssertTrue(duplicateSecond.IsDuplicateField);
    AssertEqual(4, duplicateSecond.DecodeFaults);
    AssertEqual(0, duplicateSecond.SyncConfidence);

    var drop = new TbcFieldOrderPlanner(TbcFieldOrderAction.Drop);
    drop.Plan(new TbcFieldOrderInput(0, true));
    TbcFieldOrderDecision dropped = drop.Plan(new TbcFieldOrderInput(100, true), distanceFromPreviousField: 0.8);
    AssertFalse(dropped.WriteField);
    AssertFalse(dropped.IsDuplicateField);
    AssertEqual(4, dropped.DecodeFaults);

    var none = new TbcFieldOrderPlanner(TbcFieldOrderAction.None);
    none.Plan(new TbcFieldOrderInput(0, true));
    TbcFieldOrderDecision flipped = none.Plan(new TbcFieldOrderInput(100, true), distanceFromPreviousField: 1.2);
    AssertTrue(flipped.WriteField);
    AssertFalse(flipped.IsFirstField);
    AssertTrue(flipped.DetectedFirstField);
    AssertEqual(4, flipped.DecodeFaults);

    var progressive = new TbcFieldOrderPlanner(TbcFieldOrderAction.Detect);
    progressive.Plan(new TbcFieldOrderInput(0, true));
    progressive.Plan(new TbcFieldOrderInput(100, true), distanceFromPreviousField: 1.3);
    TbcFieldOrderDecision progressiveFlip = progressive.Plan(new TbcFieldOrderInput(200, true), distanceFromPreviousField: 1.0);
    AssertFalse(progressiveFlip.IsFirstField);
    AssertEqual(1, progressiveFlip.DecodeFaults);
    AssertEqual(10, progressiveFlip.SyncConfidence);

    var noProgressiveFlip = new TbcFieldOrderPlanner(TbcFieldOrderAction.Detect, allowProgressiveFlip: false);
    noProgressiveFlip.Plan(new TbcFieldOrderInput(0, true));
    noProgressiveFlip.Plan(new TbcFieldOrderInput(100, true), distanceFromPreviousField: 1.3);
    TbcFieldOrderDecision notFlipped = noProgressiveFlip.Plan(new TbcFieldOrderInput(200, true), distanceFromPreviousField: 1.0);
    AssertTrue(notFlipped.IsFirstField);
    AssertEqual(4, notFlipped.DecodeFaults);
    AssertEqual(0, notFlipped.SyncConfidence);

    AssertEqual(TbcFieldOrderAction.Drop, TbcFieldOrderPlanner.ParseAction("drop"));
    AssertThrows<ArgumentException>(() => TbcFieldOrderPlanner.ParseAction("bogus"));
}

[Fact(DisplayName = "TBC dropout detector maps RF ranges to JSON coordinates")]
public void TbcDropoutDetectorMapsRfRangesToJsonCoordinates()
{
    double[] envelope = Enumerable.Repeat(10.0, 80).ToArray();
    for (int i = 10; i <= 22; i++)
    {
        envelope[i] = 1.0;
    }

    for (int i = 25; i <= 35; i++)
    {
        envelope[i] = 1.0;
    }

    for (int i = 60; i <= 65; i++)
    {
        envelope[i] = 1.0;
    }

    IReadOnlyList<RfDropoutRange> ranges = RfDropoutDetector.FindDropouts(
        envelope,
        start: 0,
        end: envelope.Length,
        threshold: 2.0,
        hysteresis: 2.0,
        mergeThreshold: 5,
        minimumLength: 10);

    AssertEqual(1, ranges.Count);
    AssertClose(10.0, ranges[0].Start, 1e-12);
    AssertClose(23.0, ranges[0].End, 1e-12);

    bool[] laserDiscErrors = new bool[300];
    laserDiscErrors[100] = true;
    laserDiscErrors[101] = true;
    laserDiscErrors[102] = true;
    laserDiscErrors[200] = true;
    IReadOnlyList<RfDropoutRange> laserDiscRanges = LaserDiscDropoutDetector.BuildErrorRanges(
        laserDiscErrors,
        start: 0,
        end: laserDiscErrors.Length,
        sampleRateMHz: 1.0);
    AssertEqual(2, laserDiscRanges.Count);
    AssertClose(92.0, laserDiscRanges[0].Start, 1e-12);
    AssertClose(107.4, laserDiscRanges[0].End, 1e-12);
    AssertClose(200.0, laserDiscRanges[1].Start, 1e-12);
    AssertClose(200.0, laserDiscRanges[1].End, 1e-12);

    TbcDropoutMap laserDiscMapped = TbcDropoutMapper.MapLaserDiscRfToTbc(
        [new RfDropoutRange(110, 135), new RfDropoutRange(195, 250)],
        lineLocations: [100.0, 200.0, 300.0],
        outputLineLength: 100,
        lineOffset: 0,
        lineCount: 2);
    AssertIntSequence([0, 0, 1], laserDiscMapped.FieldLine);
    AssertIntSequence([10, 95, 0], laserDiscMapped.StartX);
    AssertIntSequence([35, 100, 50], laserDiscMapped.EndX);

    TbcDropoutMap tapeBoundaryMapped = TbcDropoutMapper.MapTapeRfToTbc(
        [new RfDropoutRange(10, 20)],
        lineLocations: [0.0, 20.0, 40.0],
        outputLineLength: 100,
        startLine: 0,
        endLine: 2,
        lineOffset: 0);
    AssertIntSequence([0, 1], tapeBoundaryMapped.FieldLine);
    AssertIntSequence([50, 0], tapeBoundaryMapped.StartX);
    AssertIntSequence([100, 0], tapeBoundaryMapped.EndX);

    TbcDropoutMap mapped = TbcDropoutMapper.MapRfToTbc(
        [new RfDropoutRange(15, 35), new RfDropoutRange(45, 95), new RfDropoutRange(-5, 5)],
        lineLocations: [0.0, 20.0, 40.0, 60.0, 80.0],
        outputLineLength: 100,
        startLine: 0,
        endLine: 4);

    AssertEqual(5, mapped.Count);
    AssertIntSequence([0, 1, 2, 3, 0], mapped.FieldLine);
    AssertIntSequence([75, 0, 25, 0, 0], mapped.StartX);
    AssertIntSequence([100, 75, 100, 100, 25], mapped.EndX);

    string tempDirectory = Path.Combine(Path.GetTempPath(), "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);
    try
    {
        string inputPath = Path.Combine(tempDirectory, "input.u8");
        string outputBase = Path.Combine(tempDirectory, "capture");
        File.WriteAllBytes(inputPath, []);
        DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--pal", inputPath, outputBase]));

        ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        TbcDecodedField field = BuildSyntheticTbcField(startSample: 0, samples: samples, dropouts: mapped);
        TbcFirstFieldDecodeResult result = new TbcFirstFieldDecodeEngine().WriteDecodedField(session, field);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Paths!.JsonPath));
        JsonElement dropOuts = document.RootElement.GetProperty("fields")[0].GetProperty("dropOuts");
        AssertEqual(5, dropOuts.GetProperty("fieldLine").GetArrayLength());
        AssertEqual(75, dropOuts.GetProperty("startx")[0].GetInt32());
        AssertEqual(100, dropOuts.GetProperty("endx")[3].GetInt32());

        DecodeSession noDodSession = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--noDOD", "--pal", inputPath, Path.Combine(tempDirectory, "no-dod")]));
        TbcFirstFieldDecodeResult noDodResult = new TbcFirstFieldDecodeEngine().WriteDecodedField(noDodSession, field);
        using JsonDocument noDodDocument = JsonDocument.Parse(File.ReadAllText(noDodResult.Paths!.JsonPath));
        AssertFalse(noDodDocument.RootElement.GetProperty("fields")[0].TryGetProperty("dropOuts", out _));

        DecodeSession cvbsSession = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["--pal", inputPath, Path.Combine(tempDirectory, "cvbs")]));
        TbcOutputMetadataWriter.WriteJson(cvbsSession, [field], cvbsSession.OutputBase + ".tbc.json");
        using JsonDocument cvbsDocument = JsonDocument.Parse(File.ReadAllText(cvbsSession.OutputBase + ".tbc.json"));
        AssertFalse(cvbsDocument.RootElement.GetProperty("fields")[0].TryGetProperty("dropOuts", out _));
    }
    finally
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

[Fact(DisplayName = "decode session exposes TBC output components")]
public void DecodeSessionExposesTbcOutputComponents()
{
    ParsedCommand command = Parse(CliSpecs.Vhs, ["--pal", "input.u8", "outbase"]);
    DecodeSession session = DecodeSessionFactory.Create(command);

    AssertEqual(1135, session.TbcFrameSpec.OutputLineLength);
    AssertEqual(313, session.TbcFrameSpec.OutputLineCount);
    AssertEqual(1135 * 313, session.TbcFrameSpec.FieldSampleCount);
    AssertClose(17_734_475.0, session.TbcFrameSpec.OutputSampleRateHz, 1e-6);
    AssertEqual(256, session.VideoOutput.OutputZero);
    AssertTrue(session.TbcFrameSpec.ActiveVideoStart.HasValue);
    AssertTrue(session.TbcFrameSpec.ActiveVideoEnd.HasValue);
    AssertEqual(session.TbcFrameSpec, session.TbcRenderer.FrameSpec);
    AssertTrue(session.TbcFieldDecoder is not null);
    AssertClose(0.0, session.BlackIre, 1e-12);
    AssertClose(0.1, session.LevelAdjust, 1e-12);
    AssertTrue(session.DropoutOptions.Enabled);
    AssertClose(0.18, session.DropoutOptions.ThresholdFraction, 1e-12);
    AssertTrue(Math.Abs(session.Filters.RfHighPassOffset) < 512);

    DecodeSession ntsc = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--ntsc", "input.u8", "outbase"]));
    AssertClose(7.5, ntsc.BlackIre, 1e-12);

    DecodeSession ntscJapan = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--ntsc", "--NTSCJ", "input.u8", "outbase"]));
    AssertClose(0.0, ntscJapan.BlackIre, 1e-12);

    DecodeSession palM = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--palm", "input.u8", "outbase"]));
    AssertClose(0.0, palM.BlackIre, 1e-12);

    DecodeSession cvbsNtsc = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["--ntsc", "input.u8", "outbase"]));
    AssertClose(7.5, cvbsNtsc.BlackIre, 1e-12);
    AssertClose(0.2, cvbsNtsc.LevelAdjust, 1e-12);
    AssertFalse(cvbsNtsc.DropoutOptions.Enabled);
    AssertTrue(cvbsNtsc.TbcFieldDecoder.DecodesVbiData);

    DecodeSession cvbsPalM = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, ["--palm", "input.u8", "outbase"]));
    AssertClose(0.0, cvbsPalM.BlackIre, 1e-12);

    DecodeSession ldNtsc = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, ["--NTSC", "input.s16", "outbase"]));
    AssertClose(7.5, ldNtsc.BlackIre, 1e-12);
    AssertClose(0.0, ldNtsc.LevelAdjust, 1e-12);
    AssertEqual(3, ldNtsc.Filters.RfHighPassOffset);
    AssertTrue(ldNtsc.TbcFieldDecoder.DecodesVbiData);
    AssertFalse(session.TbcFieldDecoder!.DecodesVbiData);

    DecodeSession ldNtscJapan = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, ["--NTSCJ", "input.s16", "outbase"]));
    AssertClose(0.0, ldNtscJapan.BlackIre, 1e-12);

    DecodeSession ldPal = DecodeSessionFactory.Create(Parse(CliSpecs.LaserDisc, ["--PAL", "input.s16", "outbase"]));
    AssertClose(0.0, ldPal.BlackIre, 1e-12);

    DecodeSession customLevelAdjust = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--level_adjust", "0.25", "input.u8", "outbase"]));
    AssertClose(0.25, customLevelAdjust.LevelAdjust, 1e-12);

    DecodeSession disabledDod = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--noDOD", "input.u8", "outbase"]));
    AssertFalse(disabledDod.DropoutOptions.Enabled);

    DecodeSession customDod = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["--dod_threshold_p", "0.25", "--dod_threshold_abs", "123", "--dod_hysteresis", "1.5", "input.u8", "outbase"]));
    AssertClose(0.25, customDod.DropoutOptions.ThresholdFraction, 1e-12);
    AssertClose(123.0, customDod.DropoutOptions.AbsoluteThreshold!.Value, 1e-12);
    AssertClose(1.5, customDod.DropoutOptions.Hysteresis, 1e-12);
}

[Fact(DisplayName = "decode session disposes stateful loader")]
public void DecodeSessionDisposesStatefulLoader()
{
    DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Vhs, ["input.u8", "out"]));
    var loader = new FfmpegPcm16SampleLoader("capture.ldf", (_, _) => new MemoryStream(BuildPcm16Bytes([1])));
    DecodeSession disposableSession = session with { Loader = loader };

    disposableSession.Dispose();

    AssertThrows<ObjectDisposedException>(() => loader.Read(Stream.Null, 0, 1));
}

[Fact(DisplayName = "format catalog exposes upstream snapshot metadata")]
public void FormatCatalogExposesSnapshotMetadata()
{
    FormatCatalog catalog = FormatCatalog.Default;
    AssertEqual("oyvindln/vhs-decode", catalog.Source);
    AssertEqual("43155200", catalog.Commit);
    AssertEqual(20, catalog.TapeFormats.Count);
    AssertEqual(7, catalog.Systems.Count);
    AssertEqual(2, FormatCatalog.ParseTapeSpeed("slp"));
    AssertEqual("PAL_M", FormatCatalog.NormalizeSystem("PALM"));
    AssertEqual("NTSC", FormatCatalog.ParentSystem("NLINHA"));
    AssertTrue(FormatCatalog.IsColorUnder("BETAMAX"));
    AssertFalse(FormatCatalog.IsColorUnder("TYPEC"));
}

[Fact(DisplayName = "format catalog covers every upstream parameter combination")]
public void FormatCatalogCoversEveryUpstreamParameterCombination()
{
    FormatCatalog catalog = FormatCatalog.Default;
    AssertEqual(7, catalog.Systems.Count);
    AssertEqual(20, catalog.TapeFormats.Count);
    AssertEqual(4, catalog.TapeSpeeds.Count);

    int tapeCases = 0;
    int tapeSuccesses = 0;
    int tapeErrors = 0;
    foreach (string system in catalog.Systems)
    {
        foreach (string tapeFormat in catalog.TapeFormats)
        {
            foreach (string tapeSpeed in catalog.TapeSpeeds.Keys)
            {
                tapeCases++;
                try
                {
                    FormatParameterSet parameters = catalog.GetTapeParameters(system, tapeFormat, tapeSpeed);
                    AssertEqual(system, parameters.System);
                    AssertEqual(tapeFormat, parameters.TapeFormat);
                    AssertEqual(tapeSpeed, parameters.TapeSpeed);
                    tapeSuccesses++;
                }
                catch (FormatParameterException ex)
                {
                    AssertFalse(ex.Message.StartsWith("Unknown tape format parameter case:", StringComparison.Ordinal));
                    tapeErrors++;
                }
            }
        }
    }

    AssertEqual(560, tapeCases);
    AssertEqual(tapeCases, tapeSuccesses + tapeErrors);
    AssertTrue(tapeSuccesses > 0);
    AssertTrue(tapeErrors > 0);

    foreach (string system in catalog.Systems)
    {
        try
        {
            FormatParameterSet parameters = catalog.GetCvbsParameters(system);
            AssertEqual(system, parameters.System);
        }
        catch (FormatParameterException ex)
        {
            AssertFalse(ex.Message.StartsWith("Unknown CVBS parameter case:", StringComparison.Ordinal));
        }
    }

    foreach (string system in new[] { "PAL", "NTSC" })
    {
        foreach (bool lowBand in new[] { false, true })
        {
            FormatParameterSet parameters = catalog.GetLaserDiscParameters(system, lowBand);
            AssertEqual(system, parameters.System);
            AssertEqual("LD", parameters.TapeFormat);
        }
    }
}

[Fact(DisplayName = "all CVBS and LD variants build and demodulate a block")]
public void AllCvbsAndLdVariantsBuildAndDemodulateBlock()
{
    const int blockLength = 4096;
    FormatCatalog catalog = FormatCatalog.Default;
    byte[] signedInput = BuildCatalogSignedRf(blockLength);

    int cvbsDecoded = 0;
    foreach (string system in catalog.Systems)
    {
        ParsedCommand command = Parse(CliSpecs.Cvbs, ["--system", system, "input.s16", "out"]);
        using DecodeSession session = DecodeSessionFactory.Create(command, blockLength);
        using var input = new MemoryStream(signedInput, writable: false);
        AssertCompleteDemodulatedBlock(
            session.Pipeline.DecodeBlock(input, 0, blockLength),
            blockLength,
            $"CVBS/{system}");
        cvbsDecoded++;
    }

    int laserDiscDecoded = 0;
    foreach (string system in new[] { "NTSC", "PAL" })
    {
        foreach (bool lowBand in new[] { false, true })
        {
            var commandArgs = new List<string>
            {
                system == "PAL" ? "--PAL" : "--NTSC",
                "--noEFM",
                "--disable_analog_audio"
            };
            if (lowBand)
            {
                commandArgs.Add("--lowband");
            }

            commandArgs.Add("input.s16");
            commandArgs.Add("out");
            using DecodeSession session = DecodeSessionFactory.Create(
                new CommandLineParser().Parse(CliSpecs.LaserDisc, commandArgs),
                blockLength);
            using var input = new MemoryStream(signedInput, writable: false);
            AssertCompleteDemodulatedBlock(
                session.Pipeline.DecodeBlock(input, 0, blockLength),
                blockLength,
                $"LD/{system}/lowband={lowBand}");
            laserDiscDecoded++;
        }
    }

    AssertEqual(7, cvbsDecoded);
    AssertEqual(4, laserDiscDecoded);
}

[Fact(DisplayName = "v0.4 tape-family blocks match upstream channels")]
public void TapeFamilyBlocksMatchUpstreamChannels()
{
    const int blockLength = 32_768;
    const string resourceName = "VHSDecode.Tests.Fixtures.tape-family-v0.4.0.json";
    using Stream resource = System.Reflection.Assembly.GetExecutingAssembly()
        .GetManifestResourceStream(resourceName)
        ?? throw new Exception($"Missing embedded tape-family baseline '{resourceName}'.");
    using JsonDocument baseline = JsonDocument.Parse(resource);
    AssertEqual(blockLength, baseline.RootElement.GetProperty("block_length").GetInt32());

    var inputBytes = new byte[blockLength];
    uint state = 0x12345678;
    for (int i = 0; i < inputBytes.Length; i++)
    {
        state = unchecked((state * 1664525) + 1013904223);
        inputBytes[i] = (byte)(state >> 24);
    }

    int cases = 0;
    int comparedChannels = 0;
    var burstMismatches = new List<string>();
    foreach (JsonElement item in baseline.RootElement.GetProperty("cases").EnumerateArray())
    {
        string system = item.GetProperty("system").GetString()!;
        string tapeFormat = item.GetProperty("tape_format").GetString()!;
        string tapeSpeed = item.GetProperty("tape_speed").GetString()!;
        string label = $"{system}/{tapeFormat}/{tapeSpeed}";
        ParsedCommand command = Parse(CliSpecs.Vhs,
        [
            "--system", system,
            "--tf", tapeFormat,
            "--ts", tapeSpeed,
            "--no_resample",
            "input.u8",
            "out"
        ]);
        using DecodeSession session = DecodeSessionFactory.CreateForRfParameterProbe(command, blockLength);
        using var input = new MemoryStream(inputBytes, writable: false);
        RfDemodulatedBlock block = session.Pipeline.DecodeBlock(input, 0, blockLength)
            ?? throw new Exception($"{label}: no RF block was decoded.");

        AssertNamedHash(label, "demod", item, FloatBitsSha256(block.Video));
        AssertNamedHash(label, "demod_05", item, FloatBitsSha256(block.VideoLowPass));
        try
        {
            AssertNamedHash(label, "demod_burst", item, FloatBitsSha256(block.Chroma!));
        }
        catch (Exception ex)
        {
            burstMismatches.Add(ex.Message);
        }
        AssertNamedHash(label, "envelope", item, FloatBitsSha256(block.Envelope));
        comparedChannels += 4;

        cases++;
    }

    AssertEqual(357, cases);
    AssertEqual(1_428, comparedChannels);
    if (burstMismatches.Count != 0)
    {
        throw new Exception(string.Join(Environment.NewLine, burstMismatches));
    }
}

static void AssertNamedHash(string label, string channel, JsonElement expectedCase, string actual)
{
    string expected = expectedCase.GetProperty(channel).GetString()!;
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new Exception($"{label} {channel}: expected {expected}, got {actual}.");
    }
}

static void AssertCompleteDemodulatedBlock(RfDemodulatedBlock? block, int length, string label)
{
    if (block is null)
    {
        throw new Exception($"{label} returned no demodulated block.");
    }

    AssertEqual(length, block.Video.Length);
    AssertEqual(length, block.DemodRaw.Length);
    AssertEqual(length, block.VideoLowPass.Length);
    AssertTrue(block.Video.All(double.IsFinite));
    AssertTrue(block.DemodRaw.All(double.IsFinite));
    AssertTrue(block.VideoLowPass.All(double.IsFinite));
}

static byte[] BuildCatalogUnsignedRf(int length)
{
    var output = new byte[length];
    uint state = 0x12345678;
    for (int i = 0; i < output.Length; i++)
    {
        state = unchecked((state * 1664525) + 1013904223);
        output[i] = (byte)(state >> 24);
    }

    return output;
}

static byte[] BuildCatalogSignedRf(int length)
{
    byte[] output = BuildCatalogUnsignedRf(length * sizeof(short));
    for (int i = 1; i < output.Length; i += sizeof(short))
    {
        output[i] ^= 0x80;
    }

    return output;
}

[Fact(DisplayName = "format catalog returns VHS PAL parameters")]
public void FormatCatalogReturnsVhsPalParameters()
{
    FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters("PAL", "VHS", "sp");
    AssertClose(4_100_000.0, JsonDouble(parameters.SysParams, "ire0"), 1e-6);
    AssertClose(1_300_000.0, JsonDouble(parameters.RfParams, "video_bpf_low"), 1e-6);
    AssertClose(626_953.0, JsonDouble(parameters.RfParams, "color_under_carrier"), 1e-6);
    AssertEqual(0, parameters.Warnings.Count);
}

[Fact(DisplayName = "format catalog maps SLP to EP tape speed")]
public void FormatCatalogMapsSlpToEpTapeSpeed()
{
    FormatParameterSet ep = FormatCatalog.Default.GetTapeParameters("NTSC", "VHS", "ep");
    FormatParameterSet slp = FormatCatalog.Default.GetTapeParameters("NTSC", "VHS", "slp");
    AssertEqual("ep", slp.TapeSpeed);
    AssertClose(JsonDouble(ep.RfParams, "video_bpf_high"), JsonDouble(slp.RfParams, "video_bpf_high"), 1e-12);
    AssertClose(6_400_000.0, JsonDouble(slp.RfParams, "video_bpf_high"), 1e-6);
}

[Fact(DisplayName = "format catalog returns special line standard parameters")]
public void FormatCatalogReturnsSpecialLineStandardParameters()
{
    FormatParameterSet betamax405 = FormatCatalog.Default.GetTapeParameters("405", "BETAMAX", "sp");
    AssertEqual(405, JsonInt(betamax405.SysParams, "frame_lines"));
    AssertClose(98.765, JsonDouble(betamax405.SysParams, "line_period"), 1e-12);

    FormatParameterSet quad819 = FormatCatalog.Default.GetTapeParameters("819", "QUADRUPLEX", "sp");
    AssertEqual(819, JsonInt(quad819.SysParams, "frame_lines"));
    AssertClose(1_500_000.0, JsonDouble(quad819.RfParams, "video_bpf_low"), 1e-6);
}

[Fact(DisplayName = "format catalog preserves upstream unsupported-format errors")]
public void FormatCatalogPreservesUnsupportedFormatErrors()
{
    AssertThrows<FormatParameterException>(() => FormatCatalog.Default.GetTapeParameters("405", "VHS", "sp"));

    FormatParameterSet fallback = FormatCatalog.Default.GetTapeParameters("PAL", "BETAMAX_HIFI", "sp");
    AssertEqual(1, fallback.Warnings.Count);
    AssertEqual("Tape format \"BETAMAX_HIFI\" not supported for PAL yet", fallback.Warnings[0]);
    AssertClose(1_300_000.0, JsonDouble(fallback.RfParams, "video_bpf_low"), 1e-6);
}

[Fact(DisplayName = "format catalog returns CVBS and LD parameters")]
public void FormatCatalogReturnsCvbsAndLdParameters()
{
    FormatParameterSet cvbsPalm = FormatCatalog.Default.GetCvbsParameters("PALM");
    AssertEqual(525, JsonInt(cvbsPalm.SysParams, "frame_lines"));
    AssertClose(4_500_000.0, JsonDouble(cvbsPalm.RfParams, "video_lpf_freq"), 1e-6);

    FormatParameterSet ldNtsc = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: false);
    FormatParameterSet ldNtscLow = FormatCatalog.Default.GetLaserDiscParameters("NTSC", lowBand: true);
    AssertClose(13_800_000.0, JsonDouble(ldNtsc.RfParams, "video_bpf_high"), 1e-6);
    AssertClose(12_500_000.0, JsonDouble(ldNtscLow.RfParams, "video_bpf_high"), 1e-6);
}

[Fact(DisplayName = "raw RF loaders read numeric samples")]
public void RawRfLoadersReadNumericSamples()
{
    AssertSequence([20, 30, 40], new UInt8SampleLoader().Read(new MemoryStream([10, 20, 30, 40]), 1, 3)!);
    AssertEqual(null, new UInt8SampleLoader().Read(new MemoryStream([10, 20]), 0, 3));
    AssertSequence([-128, -1, 0, 127], new Int8SampleLoader().Read(new MemoryStream([0x80, 0xFF, 0x00, 0x7F]), 0, 4)!);

    byte[] s16 = new byte[6];
    BinaryPrimitives.WriteInt16LittleEndian(s16.AsSpan(0, 2), -1);
    BinaryPrimitives.WriteInt16LittleEndian(s16.AsSpan(2, 2), 256);
    BinaryPrimitives.WriteInt16LittleEndian(s16.AsSpan(4, 2), short.MinValue);
    AssertSequence([-1, 256, short.MinValue], new Int16SampleLoader().Read(new MemoryStream(s16), 0, 3)!);

    byte[] u16 = new byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(u16.AsSpan(0, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(u16.AsSpan(2, 2), ushort.MaxValue);
    AssertSequence([0, ushort.MaxValue], new UInt16SampleLoader().Read(new MemoryStream(u16), 0, 2)!);

    byte[] f32 = new byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(f32.AsSpan(0, 4), BitConverter.SingleToInt32Bits(0.5f));
    BinaryPrimitives.WriteInt32LittleEndian(f32.AsSpan(4, 4), BitConverter.SingleToInt32Bits(-1.0f));
    AssertSequence([16384, -32768], new Float32SampleLoader().Read(new MemoryStream(f32), 0, 2)!);
}

[Fact(DisplayName = "PCM16 stream loader reads with data offsets")]
public void Pcm16StreamLoaderReadsWithDataOffsets()
{
    byte[] bytes = [0xAA, 0xBB, 0x00, 0x80, 0x34, 0x12, 0xFF, 0x7F];
    var loader = new Pcm16StreamSampleLoader(dataOffset: 2, dataLengthBytes: 6);
    AssertSequence([0x1234, short.MaxValue], loader.Read(new MemoryStream(bytes), 1, 2)!);
    AssertEqual(null, loader.Read(new MemoryStream(bytes), 2, 2));
}

[Fact(DisplayName = "WAV PCM16 loader reads RIFF data chunk")]
public void WavePcm16LoaderReadsRiffDataChunk()
{
    byte[] wave = BuildPcm16Wave([1, -2, 300, short.MinValue], sampleRate: 40000);
    var stream = new MemoryStream(wave);
    WaveDataInfo info = WavePcm16SampleLoader.ReadWaveInfo(stream);
    AssertEqual(40000u, info.SampleRate);
    AssertEqual(8L, info.DataLengthBytes);

    var loader = new WavePcm16SampleLoader();
    AssertSequence([-2, 300], loader.Read(new MemoryStream(wave), 1, 2)!);
    AssertEqual(null, loader.Read(new MemoryStream(wave), 3, 2));

    byte[] stereoWave = BuildPcm16Wave([1, 2], sampleRate: 40000, channels: 2);
    AssertThrows<NotSupportedException>(() => WavePcm16SampleLoader.ReadWaveInfo(new MemoryStream(stereoWave)));
}

[Fact(DisplayName = "FFmpeg PCM16 loader reads container segments")]
public void FfmpegPcm16LoaderReadsContainerSegments()
{
    string? seenFilename = null;
    long seenSample = -1;
    int seenReadLength = -1;
    var loader = new FfmpegPcm16SampleLoader("capture.ldf", (filename, sample, readLength) =>
    {
        seenFilename = filename;
        seenSample = sample;
        seenReadLength = readLength;
        byte[] bytes = new byte[readLength * 2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(0, 2), -123);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2, 2), 456);
        return bytes;
    });

    AssertSequence([-123, 456], loader.Read(Stream.Null, 12345, 2)!);
    AssertEqual("capture.ldf", seenFilename);
    AssertEqual(12345L, seenSample);
    AssertEqual(2, seenReadLength);

    var shortReader = new FfmpegPcm16SampleLoader("capture.flac", (_, _, _) => [0x01]);
    AssertEqual(null, shortReader.Read(Stream.Null, 0, 1));
}

[Fact(DisplayName = "FFmpeg PCM16 loader builds upstream LDF seek arguments")]
public void FfmpegPcm16LoaderBuildsUpstreamLdfSeekArguments()
{
    AssertEqual("25.025", FfmpegPcm16SampleLoader.FormatSeekSeconds(1_001_000));
    AssertEqual("61.783240223", FfmpegPcm16SampleLoader.FormatSeekSeconds(1_105_920, 17_900));
    IReadOnlyList<string> args = FfmpegPcm16SampleLoader.BuildFfmpegArguments("capture.ldf", 80_000);
    AssertContains(string.Join(" ", args), "-i capture.ldf -ss 2");
    IReadOnlyList<string> native179Args = FfmpegPcm16SampleLoader.BuildFfmpegArguments(
        "capture.flac",
        1_105_920,
        17_900);
    AssertContains(string.Join(" ", native179Args), "-i capture.flac -ss 61.783240223");
    AssertContains(string.Join(" ", args), "-f s16le");
    AssertContains(string.Join(" ", args), "-acodec pcm_s16le");
    AssertContains(string.Join(" ", args), "-ac 1 -");
}

[Fact(DisplayName = "FFmpeg PCM16 loader streams and rewinds container output")]
public void FfmpegPcm16LoaderStreamsAndRewindsContainerOutput()
{
    AssertEqual(2 * 1024 * 1024, FfmpegPcm16SampleLoader.DefaultRewindSize);
    AssertEqual(40 * 1024 * 1024, FfmpegPcm16SampleLoader.DefaultSeekThreshold);
    byte[] pcm = BuildPcm16Bytes([1, 2, 3, 4, 5, 6, 7, 8]);
    var opens = new List<long>();
    using var loader = new FfmpegPcm16SampleLoader("capture.ldf", (_, startSample) =>
    {
        opens.Add(startSample);
        int byteOffset = checked((int)Math.Min(pcm.Length, startSample * 2));
        return new MemoryStream(pcm[byteOffset..]);
    }, rewindSize: 8);

    AssertSequence([1, 2], loader.Read(Stream.Null, 0, 2)!);
    AssertSequence([3, 4], loader.Read(Stream.Null, 2, 2)!);
    AssertSequence([2, 3], loader.Read(Stream.Null, 1, 2)!);
    AssertSequence([8], loader.Read(Stream.Null, 7, 1)!);
    AssertSequence([1], loader.Read(Stream.Null, 0, 1)!);
    AssertEqual(2, opens.Count);
    AssertEqual(0L, opens[0]);
    AssertEqual(0L, opens[1]);

    var seekOpens = new List<long>();
    using var seekingLoader = new FfmpegPcm16SampleLoader("capture.flac", (_, startSample) =>
    {
        seekOpens.Add(startSample);
        int byteOffset = checked((int)Math.Min(pcm.Length, startSample * 2));
        return new MemoryStream(pcm[byteOffset..]);
    }, rewindSize: 4, seekThreshold: 4);

    AssertSequence([1], seekingLoader.Read(Stream.Null, 0, 1)!);
    AssertSequence([6], seekingLoader.Read(Stream.Null, 5, 1)!);
    AssertEqual(2, seekOpens.Count);
    AssertEqual(5L, seekOpens[1]);

    using var failingLoader = new FfmpegPcm16SampleLoader(
        "broken.ldf",
        (_, _) => new MemoryStream([]),
        rewindSize: 8,
        exitCodeAfterOutputEnd: () => 1,
        stderrProvider: () => "container decode failed");
    try
    {
        _ = failingLoader.Read(Stream.Null, 0, 1);
        throw new Exception("Expected FFmpeg streaming failure.");
    }
    catch (InvalidOperationException ex)
    {
        AssertContains(ex.Message, "FFmpeg failed while streaming 'broken.ldf'");
        AssertContains(ex.Message, "container decode failed");
    }
}

[Fact(DisplayName = "FFmpeg stream loader reads forward and rewind samples")]
public void FfmpegStreamLoaderReadsForwardAndRewindSamples()
{
    byte[] pcm = new byte[8];
    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(0, 2), 1);
    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2, 2), 2);
    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(4, 2), 3);
    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(6, 2), 4);
    using var decoded = new MemoryStream(pcm);
    using var loader = new FfmpegStreamSampleLoader([], [], _ => decoded, rewindSize: 8);

    AssertSequence([2, 3], loader.Read(Stream.Null, 1, 2)!);
    AssertSequence([1, 2], loader.Read(Stream.Null, 0, 2)!);
    AssertEqual(null, loader.Read(Stream.Null, 4, 1));

    using var failingLoader = new FfmpegStreamSampleLoader(
        [],
        [],
        _ => new MemoryStream([]),
        rewindSize: 8,
        exitCodeAfterOutputEnd: () => 1,
        stderrProvider: () => "resample failed");
    try
    {
        _ = failingLoader.Read(Stream.Null, 0, 1);
        throw new Exception("Expected FFmpeg resampling failure.");
    }
    catch (InvalidOperationException ex)
    {
        AssertContains(ex.Message, "FFmpeg failed while resampling RF input");
        AssertContains(ex.Message, "resample failed");
    }
}

[Fact(DisplayName = "RF loader factory maps resampling extensions")]
public void RfLoaderFactoryMapsResamplingExtensions()
{
    AssertStringSequence(["-f", "u8"], RfLoaderFactory.BuildResamplingInputArguments("capture.u8").ToArray());
    AssertStringSequence(["-f", "s16le"], RfLoaderFactory.BuildResamplingInputArguments("capture.raw").ToArray());
    AssertStringSequence(["-f", "f32le"], RfLoaderFactory.BuildResamplingInputArguments("capture.rf").ToArray());
    AssertEqual(0, RfLoaderFactory.BuildResamplingInputArguments("capture.U8").Count);
    IReadOnlyList<string> outputArgs = RfLoaderFactory.BuildResamplingOutputArguments(FrequencyParser.ParseMHz("8fsc"));
    AssertEqual("-filter:a", outputArgs[0]);
    AssertContains(outputArgs[1], "asetrate=28636363.636363");
    AssertContains(outputArgs[1], "aresample=40000000");
    AssertEqual(0, RfLoaderFactory.BuildResamplingOutputArguments(40.0).Count);
    AssertStringSequence(
        ["-filter:a", "asetrate=17900000.0,aresample=40000000.0"],
        RfLoaderFactory.BuildResamplingOutputArguments(17.9).ToArray());
    AssertStringSequence(
        ["-filter:a", "asetrate=40000001.0,aresample=40000000.0"],
        RfLoaderFactory.BuildResamplingOutputArguments(40.000001).ToArray());
    AssertStringSequence(
        ["-filter:a", "asetrate=0.0,aresample=40000000.0"],
        RfLoaderFactory.BuildResamplingOutputArguments(0.0).ToArray());
    AssertStringSequence(
        ["-filter:a", "asetrate=nan,aresample=40000000.0"],
        RfLoaderFactory.BuildResamplingOutputArguments(double.NaN).ToArray());

    var loader = (FfmpegStreamSampleLoader)RfLoaderFactory.CreateResampling("capture.r8", FrequencyParser.CxAdcMHz);
    AssertStringSequence(["-f", "u8"], loader.InputArguments.ToArray());
    AssertThrows<NotSupportedException>(() => RfLoaderFactory.CreateResampling("capture.lds", 28.0));
}

[Fact(DisplayName = "packed LDS loader unpacks 4x10 bit samples")]
public void PackedLdsLoaderUnpacksSamples()
{
    byte[] packed = Pack4x10([0, 512, 1023, 513, 1, 2, 3, 4, 5, 6, 7, 8]);
    double[]? actual = new PackedDdD4To40SampleLoader().Read(new MemoryStream(packed), 1, 5);
    AssertSequence([0, 32704, 64, -32704, -32640], actual!);
}

[Fact(DisplayName = "packed r30 loader unpacks 3x10 bit samples")]
public void PackedR30LoaderUnpacksSamples()
{
    byte[] packed = Pack3x10([1, 2, 3, 4, 5, 6]);
    double[]? actual = new Packed3To32SampleLoader().Read(new MemoryStream(packed), 2, 3);
    AssertSequence([3, 4, 5], actual!);
}

[Fact(DisplayName = "RF loader factory maps native extensions")]
public void RfLoaderFactoryMapsNativeExtensions()
{
    AssertType<UInt8SampleLoader>(RfLoaderFactory.CreateNative("capture.u8"));
    AssertType<UInt8SampleLoader>(RfLoaderFactory.CreateNative("capture.r8"));
    AssertType<Int16SampleLoader>(RfLoaderFactory.CreateNative("capture.s16"));
    AssertType<UInt16SampleLoader>(RfLoaderFactory.CreateNative("capture.u16"));
    AssertType<UInt16SampleLoader>(RfLoaderFactory.CreateNative("capture.r16"));
    AssertType<Float32SampleLoader>(RfLoaderFactory.CreateNative("capture.rf"));
    AssertType<PackedDdD4To40SampleLoader>(RfLoaderFactory.CreateNative("capture.lds"));
    AssertType<Packed3To32SampleLoader>(RfLoaderFactory.CreateNative("capture.r30"));
    AssertType<FfmpegStreamSampleLoader>(RfLoaderFactory.CreateNative("capture.s8"));
    AssertType<FfmpegPcm16SampleLoader>(RfLoaderFactory.CreateNative("capture.wav"));
    AssertType<FfmpegPcm16SampleLoader>(RfLoaderFactory.CreateNative("capture.ldf"));
    AssertType<FfmpegPcm16SampleLoader>(RfLoaderFactory.CreateNative("capture.flac"));
    AssertType<FfmpegPcm16SampleLoader>(RfLoaderFactory.CreateNative("capture.vhs"));
    AssertType<FfmpegPcm16SampleLoader>(RfLoaderFactory.CreateNative("capture.raw.oga"));
    var fallback = (FfmpegStreamSampleLoader)RfLoaderFactory.CreateNative("capture.mka");
    AssertEqual(0, fallback.InputArguments.Count);
    AssertEqual(0, fallback.OutputArguments.Count);
    AssertType<FfmpegStreamSampleLoader>(RfLoaderFactory.CreateNative("capture.LDS"));
}

static ParsedCommand Parse(DecodeCommandSpec spec, string[] args) => new CommandLineParser().Parse(spec, args);

static void AssertSpecAcceptsOptionNames(DecodeCommandSpec spec, IEnumerable<string> optionNames)
{
    foreach (string optionName in optionNames.Distinct(StringComparer.Ordinal))
    {
        if (!spec.TryGetOption(optionName, out OptionSpec option))
        {
            throw new Exception($"{spec.Name} is missing upstream option {optionName}.");
        }

        var args = new List<string> { optionName };
        if (option.Arity != OptionArity.Flag)
        {
            args.Add(SampleValueFor(option));
        }

        for (int positional = 0; positional < spec.MinimumPositionals; positional++)
        {
            args.Add(positional == 0 ? "input.s16" : "output");
        }

        Parse(spec, args.ToArray());
    }
}

static string SampleValueFor(OptionSpec option)
{
    if (option.Choices is { Length: > 0 })
    {
        return option.Choices[0];
    }

    if (option.Destination == "ire0_adjust")
    {
        return "hsync";
    }

    if (option.Destination == "field_order_action")
    {
        return "detect";
    }

    return option.ValueKind switch
    {
        OptionValueKind.String => option.Destination switch
        {
            "write_test_ldf" => "sample.ldf",
            "params_file" => "-",
            _ => "value"
        },
        OptionValueKind.Integer => "1",
        OptionValueKind.Double => "1.0",
        OptionValueKind.FrequencyMHz => "1MHz",
        _ => "true"
    };
}

static void AssertTrue(bool value)
{
    if (!value)
    {
        throw new Exception("Expected true.");
    }
}

static void AssertFalse(bool value)
{
    if (value)
    {
        throw new Exception("Expected false.");
    }
}

static ClassifiedSyncPulse Sync(SyncPulseKind kind, int start, int length = 5, bool inOrder = true)
{
    return new ClassifiedSyncPulse(kind, new Pulse(start, length), inOrder);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"Expected {expected}, got {actual}.");
    }
}

static void AssertContains(string haystack, string needle)
{
    if (!haystack.Contains(needle, StringComparison.Ordinal))
    {
        throw new Exception($"Expected text to contain '{needle}', got '{haystack}'.");
    }
}

static void AssertType<T>(object value)
{
    if (value is not T)
    {
        throw new Exception($"Expected type {typeof(T).Name}, got {value.GetType().Name}.");
    }
}

static object? PrivateFieldValue(object target, string fieldName)
{
    var field = target.GetType().GetField(
        fieldName,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (field is null)
    {
        throw new Exception($"Expected private field {fieldName} on {target.GetType().Name}.");
    }

    return field.GetValue(target);
}

static void SetPrivateFieldValue(object target, string fieldName, object? value)
{
    var field = target.GetType().GetField(
        fieldName,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (field is null)
    {
        throw new Exception($"Expected private field {fieldName} on {target.GetType().Name}.");
    }

    field.SetValue(target, value);
}

static object? PrivatePropertyValue(object target, string propertyName)
{
    var property = target.GetType().GetProperty(
        propertyName,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
    if (property is null)
    {
        throw new Exception($"Expected property {propertyName} on {target.GetType().Name}.");
    }

    return property.GetValue(target);
}

static object? InvokePrivateMethod(object target, string methodName, params object?[] arguments)
{
    var method = target.GetType().GetMethod(
        methodName,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (method is null)
    {
        throw new Exception($"Expected private method {methodName} on {target.GetType().Name}.");
    }

    return method.Invoke(target, arguments);
}

static object? InvokePrivateStaticMethod(Type type, string methodName, params object?[] arguments)
{
    var method = type.GetMethod(
        methodName,
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    if (method is null)
    {
        throw new Exception($"Expected private static method {methodName} on {type.Name}.");
    }

    return method.Invoke(null, arguments);
}

static void AssertStringSequence(string[] expected, string[] actual)
{
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
    {
        throw new Exception(
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    for (int i = 0; i < expected.Length; i++)
    {
        AssertEqual(expected[i], actual[i]);
    }
}

static void AssertClose(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new Exception($"Expected {expected:R}, got {actual:R}.");
    }
}

static void AssertSequence(double[] expected, double[] actual)
{
    AssertEqual(expected.Length, actual.Length);
    for (int i = 0; i < expected.Length; i++)
    {
        AssertClose(expected[i], actual[i], 1e-12);
    }
}

static void AssertIntSequence(int[] expected, int[] actual)
{
    AssertEqual(expected.Length, actual.Length);
    for (int i = 0; i < expected.Length; i++)
    {
        AssertEqual(expected[i], actual[i]);
    }
}

static double Max(double[] values)
{
    double max = double.NegativeInfinity;
    foreach (double value in values)
    {
        max = Math.Max(max, value);
    }

    return max;
}

static double MaxAbs(double[] values)
{
    double max = 0.0;
    foreach (double value in values)
    {
        max = Math.Max(max, Math.Abs(value));
    }

    return max;
}

static int FrequencyBin(double frequencyHz, double sampleRateHz, int blockLength)
{
    return (int)Math.Round(frequencyHz / sampleRateHz * blockLength);
}

static double MeanMagnitude(Complex[] values)
{
    double sum = 0.0;
    foreach (Complex value in values)
    {
        sum += value.Magnitude;
    }

    return sum / values.Length;
}

static double AmplitudeAtBin(double[] values, int bin)
{
    Complex[] spectrum = FastFourierTransform.Forward(values);
    return spectrum[bin].Magnitude;
}

static (int Line, double StartUsec, double LengthUsec) FirstVitsSlice(JsonElement sysParams, string propertyName)
{
    JsonElement property = sysParams.GetProperty(propertyName);
    JsonElement tuple = property.ValueKind == JsonValueKind.Array && property.GetArrayLength() > 0 && property[0].ValueKind == JsonValueKind.Array
        ? property[0]
        : property;
    return (tuple[0].GetInt32(), tuple[1].GetDouble(), tuple[2].GetDouble());
}

static void PaintTbcMetricSlice(
    ushort[] samples,
    DecodeSession session,
    int line,
    double startUsec,
    double lengthUsec,
    Func<int, ushort> sampleAt)
{
    double begin = ((line - 1) * session.TbcFrameSpec.OutputLineLength)
        + (startUsec * JsonDouble(session.Parameters.SysParams, "outfreq"));
    int start = (int)Math.Round(begin, MidpointRounding.ToEven);
    int end = (int)Math.Round(
        begin + (lengthUsec * JsonDouble(session.Parameters.SysParams, "outfreq")),
        MidpointRounding.ToEven);
    for (int i = start; i < end && i < samples.Length; i++)
    {
        samples[i] = sampleAt(i - start);
    }
}

static void PaintTbcMetricSlice(
    double[] samples,
    DecodeSession session,
    int line,
    double startUsec,
    double lengthUsec,
    Func<int, double> sampleAt)
{
    double begin = ((line - 1) * session.TbcFrameSpec.OutputLineLength)
        + (startUsec * JsonDouble(session.Parameters.SysParams, "outfreq"));
    int start = (int)Math.Round(begin, MidpointRounding.ToEven);
    int end = (int)Math.Round(
        begin + (lengthUsec * JsonDouble(session.Parameters.SysParams, "outfreq")),
        MidpointRounding.ToEven);
    for (int i = start; i < end && i < samples.Length; i++)
    {
        samples[i] = sampleAt(i - start);
    }
}

static ushort BuildNtscLine19ColorSample(DecodeSession session, int sampleIndex, int phaseShift)
{
    int phase = (sampleIndex + phaseShift) & 3;
    double carrier = phase switch
    {
        1 => 1.0,
        3 => -1.0,
        _ => 0.0
    };
    double amplitude = 6.0 + (((sampleIndex / 4) % 7) * 0.35);
    double ire = 70.0 + (carrier * amplitude);
    return (ushort)Math.Round(ire, MidpointRounding.ToEven);
}

static double[] BuildPreTbcLineLocations(DecodeSession session)
{
    double lineLength = JsonDouble(session.Parameters.SysParams, "line_period") * (session.DecodeSampleRateHz / 1_000_000.0);
    double[] locations = new double[session.TbcFrameSpec.OutputLineCount + 4];
    for (int i = 0; i < locations.Length; i++)
    {
        locations[i] = i * lineLength;
    }

    return locations;
}

static void PaintPreTbcMetricSlice(
    double[] samples,
    DecodeSession session,
    double[] lineLocations,
    bool isFirstField,
    int line,
    double startUsec,
    double lengthUsec,
    int delaySamples,
    Func<int, double> sampleAt)
{
    int physicalLine = string.Equals(session.System, "PAL", StringComparison.OrdinalIgnoreCase)
        ? isFirstField ? line + 2 : line + 3
        : line;
    double samplesPerUsec = session.DecodeSampleRateHz / 1_000_000.0;
    double begin = lineLocations[physicalLine] + (startUsec * samplesPerUsec) - delaySamples;
    int start = (int)Math.Floor(begin);
    int end = (int)Math.Floor(begin + (lengthUsec * samplesPerUsec) + 1.0);
    for (int i = start; i < end && i < samples.Length; i++)
    {
        if (i >= 0)
        {
            samples[i] = sampleAt(i - start);
        }
    }
}

static TbcDecodedField BuildSyntheticTbcField(
    long startSample,
    ushort[] samples,
    double[]? lineLocations = null,
    int rawPulseCount = 7,
    int classifiedPulseCount = 6,
    bool? detectedFirstField = null,
    TbcDropoutMap? dropouts = null)
{
    double[] locations = lineLocations ?? [];
    return new TbcDecodedField(
        StartSample: startSample,
        Samples: samples,
        LineLocations: new LineLocationResult(locations, new bool[locations.Length]),
        Timing: new SyncTiming(0, 0, 0, new SyncRange(0, 0), new SyncRange(0, 0), new SyncRange(0, 0)),
        SyncThresholdHz: 123.0,
        MeanLineLength: 456.0,
        RawPulseCount: rawPulseCount,
        ClassifiedPulseCount: classifiedPulseCount,
        DetectedFirstField: detectedFirstField,
        DetectedFirstFieldConfidence: detectedFirstField.HasValue ? 100 : 0,
        Dropouts: dropouts);
}

static VideoOutputConverter BuildCvbsTestConverter(DecodeSession session, double ire0)
{
    return new VideoOutputConverter(
        ire0,
        hzIre: 1.0,
        session.VideoOutput.OutputZero,
        session.VideoOutput.VSyncIre,
        session.VideoOutput.OutputScale);
}

static TbcDecodedField BuildDeferredCvbsField(
    DecodeSession session,
    long startSample,
    int fieldNumber,
    double videoLevel,
    VideoOutputConverter converter,
    bool detectedFirstField)
{
    int lineLength = session.TbcFrameSpec.OutputLineLength;
    var lineLocations = new double[session.TbcFrameSpec.OutputLineCount + 1];
    for (int i = 0; i < lineLocations.Length; i++)
    {
        lineLocations[i] = (double)i * lineLength;
    }

    double[] video = Enumerable.Repeat(
        videoLevel,
        checked((lineLocations.Length * lineLength) + 16)).ToArray();
    return BuildSyntheticTbcField(
            startSample,
            [],
            lineLocations,
            detectedFirstField: detectedFirstField)
        with
        {
            NextFieldOffsetSamples = 100.0,
            NominalFieldLengthSamples = 100.0,
            OutputConverter = converter,
            DeferredRenderSource = new TbcDeferredRenderSource(
                video,
                lineLocations,
                FirstLine: 0,
                fieldNumber)
        };
}

static ushort RenderDeferredCvbsFirstSample(
    DecodeSession session,
    TbcDecodedField field,
    VideoOutputConverter converter)
{
    TbcDeferredRenderSource source = field.DeferredRenderSource
        ?? throw new Exception("Expected a deferred CVBS render source.");
    return session.TbcRenderer.RenderFieldPayload(
        source.VideoHz,
        source.LineLocations,
        firstLine: source.FirstLine,
        fieldNumber: source.FieldNumber,
        converterOverride: converter).Samples[0];
}

static TbcDecodedField BuildSequenceField(
    DecodeSession session,
    long startSample,
    ushort firstSample,
    bool detectedFirstField,
    ushort? chromaFirstSample = null)
{
    ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
    samples[0] = firstSample;
    TbcDecodedField field = BuildSyntheticTbcField(
        startSample,
        samples,
        lineLocations: BuildLineLocationsForAdvance(session, 300.0),
        detectedFirstField: detectedFirstField);
    if (!chromaFirstSample.HasValue)
    {
        return field;
    }

    ushort[] chroma = new ushort[session.TbcFrameSpec.FieldSampleCount];
    chroma[0] = chromaFirstSample.Value;
    return field with { ChromaSamples = chroma };
}

static long FieldStart(DecodeSession session, double fieldLocation)
{
    double framesPerSecond = JsonDouble(session.Parameters.SysParams, "FPS");
    long samplesPerField = ((long)(session.DecodeSampleRateHz / (framesPerSecond * 2.0))) + 1;
    return checked((long)Math.Round(samplesPerField * fieldLocation, MidpointRounding.AwayFromZero));
}

static double[] BuildLine0FallbackVideo(int length, IEnumerable<int> hsyncStarts)
{
    double[] video = Enumerable.Repeat(0.0, length).ToArray();
    foreach (int start in hsyncStarts)
    {
        PaintPulse(video, start, 10, -40.0);
    }

    return video;
}

static int EncodeLaserDiscCavFrameCode(int frameNumber)
{
    int value = frameNumber;
    int bcd = 0;
    int shift = 0;
    do
    {
        bcd |= (value % 10) << shift;
        value /= 10;
        shift += 4;
    }
    while (value > 0);

    return 0xF00000 | bcd;
}

static void AssertFieldFirstSample(byte[] bytes, int fieldSampleCount, int fieldIndex, ushort expected)
{
    int offset = checked(fieldIndex * fieldSampleCount * 2);
    AssertEqual((byte)(expected & 0xFF), bytes[offset]);
    AssertEqual((byte)(expected >> 8), bytes[offset + 1]);
}

static double[] BuildLineLocationsForAdvance(DecodeSession session, double nextFieldStart)
{
    double[] locations = new double[session.TbcFrameSpec.OutputLineCount + 1];
    locations[^1] = nextFieldStart;
    return locations;
}

static void AssertComplexSequence(Complex[] expected, Complex[] actual, double tolerance)
{
    AssertEqual(expected.Length, actual.Length);
    for (int i = 0; i < expected.Length; i++)
    {
        AssertClose(expected[i].Real, actual[i].Real, tolerance);
        AssertClose(expected[i].Imaginary, actual[i].Imaginary, tolerance);
    }
}

static void AssertParseError(DecodeCommandSpec spec, string[] args, string expected)
{
    CommandLineParseException exception = CaptureException<CommandLineParseException>(() => Parse(spec, args));
    AssertEqual(expected, exception.Message);
}

static void AssertThrows<T>(Action action) where T : Exception
{
    _ = CaptureException<T>(action);
}

static T CaptureException<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T ex)
    {
        return ex;
    }

    throw new Exception($"Expected exception {typeof(T).Name}.");
}

static long SqliteLong(string dbPath, string sql)
{
    return Convert.ToInt64(SqliteScalar(dbPath, sql));
}

static double SqliteDouble(string dbPath, string sql)
{
    return Convert.ToDouble(SqliteScalar(dbPath, sql));
}

static string SqliteString(string dbPath, string sql)
{
    return Convert.ToString(SqliteScalar(dbPath, sql)) ?? "";
}

static object SqliteScalar(string dbPath, string sql)
{
    SQLitePCL.Batteries_V2.Init();
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    object? value = command.ExecuteScalar();
    return value is null || value is DBNull
        ? throw new Exception($"SQLite query returned null: {sql}")
        : value;
}

static double JsonDouble(JsonElement element, string propertyName)
{
    return element.GetProperty(propertyName).GetDouble();
}

static int JsonInt(JsonElement element, string propertyName)
{
    return element.GetProperty(propertyName).GetInt32();
}

static int JsonPropertyCount(JsonElement element)
{
    int count = 0;
    foreach (JsonProperty _ in element.EnumerateObject())
    {
        count++;
    }

    return count;
}

static byte[] Pack4x10(int[] samples)
{
    if (samples.Length % 4 != 0)
    {
        throw new ArgumentException("Sample count must be divisible by four.");
    }

    byte[] output = new byte[(samples.Length / 4) * 5];
    for (int group = 0; group < samples.Length / 4; group++)
    {
        int s0 = samples[group * 4] & 0x3FF;
        int s1 = samples[(group * 4) + 1] & 0x3FF;
        int s2 = samples[(group * 4) + 2] & 0x3FF;
        int s3 = samples[(group * 4) + 3] & 0x3FF;
        int i = group * 5;
        output[i] = (byte)(s0 >> 2);
        output[i + 1] = (byte)(((s0 & 0x03) << 6) | (s1 >> 4));
        output[i + 2] = (byte)(((s1 & 0x0F) << 4) | (s2 >> 6));
        output[i + 3] = (byte)(((s2 & 0x3F) << 2) | (s3 >> 8));
        output[i + 4] = (byte)(s3 & 0xFF);
    }

    return output;
}

static byte[] Pack3x10(int[] samples)
{
    if (samples.Length % 3 != 0)
    {
        throw new ArgumentException("Sample count must be divisible by three.");
    }

    byte[] output = new byte[(samples.Length / 3) * 4];
    for (int group = 0; group < samples.Length / 3; group++)
    {
        uint word = (uint)(
            (samples[group * 3] & 0x3FF)
            | ((samples[(group * 3) + 1] & 0x3FF) << 10)
            | ((samples[(group * 3) + 2] & 0x3FF) << 20));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(group * 4, 4), word);
    }

    return output;
}

static byte[] BuildPcm16Wave(short[] samples, uint sampleRate, ushort channels = 1)
{
    ushort blockAlign = (ushort)(channels * 2);
    uint byteRate = sampleRate * blockAlign;
    uint dataSize = (uint)(samples.Length * 2);
    byte[] output = new byte[44 + dataSize];

    WriteAscii(output.AsSpan(0, 4), "RIFF");
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), (uint)(36 + dataSize));
    WriteAscii(output.AsSpan(8, 4), "WAVE");
    WriteAscii(output.AsSpan(12, 4), "fmt ");
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16, 4), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(20, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(22, 2), channels);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(28, 4), byteRate);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(32, 2), blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(34, 2), 16);
    WriteAscii(output.AsSpan(36, 4), "data");
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(40, 4), dataSize);
    for (int i = 0; i < samples.Length; i++)
    {
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(44 + (i * 2), 2), samples[i]);
    }

    return output;
}

static byte[] BuildPcm16Bytes(short[] samples)
{
    byte[] output = new byte[samples.Length * 2];
    for (int i = 0; i < samples.Length; i++)
    {
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2, 2), samples[i]);
    }

    return output;
}

static byte[] BuildFloat32Bytes(IEnumerable<double> samples)
{
    double[] values = samples.ToArray();
    var output = new byte[values.Length * sizeof(float)];
    for (int i = 0; i < values.Length; i++)
    {
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(i * sizeof(float), sizeof(float)), (float)values[i]);
    }

    return output;
}

static string ComplexBitsSha256(ReadOnlySpan<Complex> values)
{
    var bytes = new byte[values.Length * sizeof(double) * 2];
    for (int i = 0; i < values.Length; i++)
    {
        int offset = i * sizeof(double) * 2;
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(offset, sizeof(double)),
            BitConverter.DoubleToUInt64Bits(values[i].Real));
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(offset + sizeof(double), sizeof(double)),
            BitConverter.DoubleToUInt64Bits(values[i].Imaginary));
    }

    return Convert.ToHexString(SHA256.HashData(bytes));
}

static string DoubleBitsSha256(ReadOnlySpan<double> values)
{
    var bytes = new byte[values.Length * sizeof(double)];
    for (int i = 0; i < values.Length; i++)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(i * sizeof(double), sizeof(double)),
            BitConverter.DoubleToUInt64Bits(values[i]));
    }

    return Convert.ToHexString(SHA256.HashData(bytes));
}

static string FloatBitsSha256(ReadOnlySpan<double> values)
{
    var bytes = new byte[values.Length * sizeof(float)];
    for (int i = 0; i < values.Length; i++)
    {
        BinaryPrimitives.WriteSingleLittleEndian(
            bytes.AsSpan(i * sizeof(float), sizeof(float)),
            (float)values[i]);
    }

    return Convert.ToHexString(SHA256.HashData(bytes));
}

static string Utf8LfSha256(string value)
{
    string normalized = value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
}

static short[] BuildEfmSquareWave(int length, int halfPeriodSamples, short amplitude)
{
    var output = new short[length];
    for (int i = 0; i < output.Length; i++)
    {
        output[i] = ((i / halfPeriodSamples) & 1) == 0 ? amplitude : (short)-amplitude;
    }

    return output;
}

static double[] ReadInt16Samples(byte[] bytes)
{
    var output = new double[bytes.Length / 2];
    for (int i = 0; i < output.Length; i++)
    {
        output[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));
    }

    return output;
}

static double[] ReadFloat32Samples(byte[] bytes)
{
    var output = new double[bytes.Length / 4];
    for (int i = 0; i < output.Length; i++)
    {
        int bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        output[i] = BitConverter.Int32BitsToSingle(bits);
    }

    return output;
}

static void PaintTestVBlank(double[] data, int line0, bool isFirstField, string system)
{
    int numPulses = system == "PAL" ? 5 : 6;
    int firstHSyncHalfLines = (system, isFirstField) switch
    {
        ("NTSC", true) => 2,
        ("NTSC", false) => 1,
        ("PAL", true) => 1,
        ("PAL", false) => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(system))
    };
    int secondEqualizingHalfLines = (system, isFirstField) switch
    {
        ("NTSC", true) => 1,
        ("NTSC", false) => 2,
        ("PAL", true) => 1,
        ("PAL", false) => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(system))
    };

    const int HalfLineSamples = 50;
    PaintPulse(data, line0, 10, -40.0);
    int equalizing1Start = line0 + (firstHSyncHalfLines * HalfLineSamples);
    for (int pulse = 0; pulse < numPulses; pulse++)
    {
        PaintPulse(data, equalizing1Start + (pulse * HalfLineSamples), 5, -40.0);
    }

    int vSyncStart = equalizing1Start + (numPulses * HalfLineSamples);
    for (int pulse = 0; pulse < numPulses; pulse++)
    {
        PaintPulse(data, vSyncStart + (pulse * HalfLineSamples), 20, -40.0);
    }

    int equalizing2Start = vSyncStart + (numPulses * HalfLineSamples);
    for (int pulse = 0; pulse < numPulses; pulse++)
    {
        PaintPulse(data, equalizing2Start + (pulse * HalfLineSamples), 5, -40.0);
    }

    int followingHSync = equalizing2Start
        + ((numPulses - 1 + secondEqualizingHalfLines) * HalfLineSamples);
    PaintPulse(data, followingHSync, 10, -40.0);
}

static void PaintNativeVBlank(
    double[] data,
    SyncAnalyzer analyzer,
    int line0,
    bool isFirstField,
    string system)
{
    int firstHSyncHalfLines = (system, isFirstField) switch
    {
        ("NTSC", true) => 2,
        ("NTSC", false) => 1,
        ("PAL", true) => 1,
        ("PAL", false) => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(system))
    };
    int secondEqualizingHalfLines = (system, isFirstField) switch
    {
        ("NTSC", true) => 1,
        ("NTSC", false) => 2,
        ("PAL", true) => 1,
        ("PAL", false) => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(system))
    };
    double halfLine = analyzer.NominalLineLength / 2.0;
    int hSyncLength = (int)Math.Round(analyzer.UsecToSamples(analyzer.HSyncPulseUs));
    int equalizingLength = (int)Math.Round(analyzer.UsecToSamples(analyzer.EqualizingPulseUs));
    int vSyncLength = (int)Math.Round(analyzer.UsecToSamples(analyzer.VSyncPulseUs));
    int AtHalfLine(double halfLines) => line0 + (int)Math.Round(halfLines * halfLine);

    PaintPulse(data, line0, hSyncLength, -40.0);
    int equalizing1Start = AtHalfLine(firstHSyncHalfLines);
    for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
    {
        PaintPulse(data, equalizing1Start + (int)Math.Round(pulse * halfLine), equalizingLength, -40.0);
    }

    int vSyncStart = equalizing1Start + (int)Math.Round(analyzer.NumPulses * halfLine);
    for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
    {
        PaintPulse(data, vSyncStart + (int)Math.Round(pulse * halfLine), vSyncLength, -40.0);
    }

    int equalizing2Start = vSyncStart + (int)Math.Round(analyzer.NumPulses * halfLine);
    for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
    {
        PaintPulse(data, equalizing2Start + (int)Math.Round(pulse * halfLine), equalizingLength, -40.0);
    }

    int followingHSync = equalizing2Start
        + (int)Math.Round((analyzer.NumPulses - 1 + secondEqualizingHalfLines) * halfLine);
    PaintPulse(data, followingHSync, hSyncLength, -40.0);
}

static void PaintPulse(double[] data, int start, int length, double value)
{
    for (int i = start; i < start + length && i < data.Length; i++)
    {
        data[i] = value;
    }
}

static void PaintPhilipsCode(double[] data, int lineStart, int code, double sampleRateMHz)
{
    int firstCrossing = lineStart + (int)Math.Round(4.0 * sampleRateMHz, MidpointRounding.AwayFromZero);
    int bitSpacing = Math.Max(1, (int)Math.Round(2.0 * sampleRateMHz, MidpointRounding.AwayFromZero));
    int previous = lineStart;
    for (int bit = 23; bit >= 0; bit--)
    {
        bool lowBeforeCrossing = ((code >> bit) & 1) != 0;
        int crossing = firstCrossing + ((23 - bit) * bitSpacing);
        for (int i = previous; i < crossing && i < data.Length; i++)
        {
            data[i] = lowBeforeCrossing ? 0.0 : 100.0;
        }

        previous = crossing;
    }

    if (previous < data.Length)
    {
        data[previous] = ((code & 1) != 0) ? 100.0 : 0.0;
    }
}

static void WriteAscii(Span<byte> destination, string value)
{
    for (int i = 0; i < value.Length; i++)
    {
        destination[i] = (byte)value[i];
    }
}

sealed class TestRfInputProcessor(Func<double[], double[]> process) : IRfInputProcessor
{
    public bool Disposed { get; private set; }

    public double[] Process(ReadOnlySpan<double> input) => process(input.ToArray());

    public void Dispose()
    {
        Disposed = true;
    }
}
}
