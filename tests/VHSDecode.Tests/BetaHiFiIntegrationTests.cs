using System.Globalization;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class BetaHiFiIntegrationTests
{
    [Fact(DisplayName = "NTSC Betamax HiFi defaults select the complete sample 16 processing")]
    public void DefaultsSelectTheCompleteSample16Processing()
    {
        HiFiDecodeOptions options = Options("--betamax");

        Assert.Equal("betamax", options.TapeFormat);
        Assert.Equal("n", options.Standard);
        Assert.Equal(40_000_000, options.InputRateHz);
        Assert.Equal(48_000, options.AudioRateHz);
        Assert.Equal("s", options.AudioMode);
        Assert.True(options.EnableDeemphasis);
        Assert.True(options.EnableExpander);
        Assert.Equal(0, options.BetaReferenceOffsetDb);
        Assert.Equal(1, options.Gain);
        Assert.False(options.Normalize);
        Assert.Equal(DspBackend.Exact, options.DspBackend);
    }

    [Theory(DisplayName = "Betamax HiFi accepts real 20 and 40 MSPS RF with explicit NTSC")]
    [InlineData("20", 20_000_000)]
    [InlineData("40MHz", 40_000_000)]
    public void AcceptsSupportedRealRfRates(string inputRate, int expectedRate)
    {
        HiFiDecodeOptions options = Options(
            "--betamax", "--ntsc", "-f", inputRate, "--audio_rate", "48000");

        Assert.Equal(expectedRate, options.InputRateHz);
        Assert.Equal(48_000, options.AudioRateHz);
        Assert.True(options.EnableDeemphasis);
        Assert.True(options.EnableExpander);
    }

    [Theory(DisplayName = "Betamax de-emphasis and dynamic expansion have independent explicit bypasses")]
    [InlineData("on", "on", true, true)]
    [InlineData("on", "off", true, false)]
    [InlineData("off", "on", false, true)]
    [InlineData("off", "off", false, false)]
    public void CompensationBypassesAreExplicitAndIndependent(
        string deemphasis,
        string expander,
        bool expectedDeemphasis,
        bool expectedExpander)
    {
        HiFiDecodeOptions options = Options(
            "--betamax", "--deemphasis", deemphasis, "--expander", expander);

        Assert.Equal(expectedDeemphasis, options.EnableDeemphasis);
        Assert.Equal(expectedExpander, options.EnableExpander);
        Assert.Equal(0, options.BetaReferenceOffsetDb);
    }

    [Theory(DisplayName = "Betamax retains the existing stereo and mono output modes")]
    [InlineData("s")]
    [InlineData("d")]
    [InlineData("ms")]
    [InlineData("dms")]
    [InlineData("l")]
    [InlineData("r")]
    [InlineData("sum")]
    public void AcceptsExistingAudioModes(string mode)
        => Assert.Equal(mode, Options("--betamax", "--audio_mode", mode).AudioMode);

    [Theory(DisplayName = "Betamax reference offset is an optional bounded work-point adjustment")]
    [InlineData("-12", -12)]
    [InlineData("0", 0)]
    [InlineData("3.5", 3.5)]
    [InlineData("12", 12)]
    public void AcceptsBoundedReferenceOffset(string value, double expected)
    {
        HiFiDecodeOptions options = Options("--betamax", "--beta-reference-offset=" + value);

        Assert.Equal(expected, options.BetaReferenceOffsetDb);
        Assert.True(options.EnableDeemphasis);
        Assert.True(options.EnableExpander);
        Assert.Equal(1, options.Gain);
    }

    [Theory(DisplayName = "Betamax final gain and normalize do not change dynamic-compensation options")]
    [InlineData("0", 0)]
    [InlineData("0.5", 0.5)]
    [InlineData("2", 2)]
    public void FinalGainAndNormalizeKeepCompensationOptions(string value, double expected)
    {
        HiFiDecodeOptions options = Options("--betamax", "--gain", value, "--normalize");

        Assert.Equal(expected, options.Gain);
        Assert.True(options.Normalize);
        Assert.True(options.EnableDeemphasis);
        Assert.True(options.EnableExpander);
        Assert.Equal(0, options.BetaReferenceOffsetDb);
    }

    [Theory(DisplayName = "VHS and 8mm keep their existing format-specific defaults")]
    [InlineData(false, "vhs", "s", 30, 56e-6, 6.5e-3)]
    [InlineData(true, "8mm", "ms", 6, 75e-6, 3e-3)]
    public void ExistingFormatsKeepTheirDefaults(
        bool is8Mm,
        string format,
        string audioMode,
        double gain,
        double deemphasis,
        double attack)
    {
        HiFiDecodeOptions options = is8Mm ? Options("--8mm") : Options();

        Assert.Equal(format, options.TapeFormat);
        Assert.Equal(audioMode, options.AudioMode);
        Assert.Equal(gain, options.ExpanderGain);
        Assert.Equal(deemphasis, options.DeemphasisLowTau);
        Assert.Equal(attack, options.ExpanderAttackTau);
        Assert.Equal(0, options.BetaReferenceOffsetDb);
        Assert.True(options.EnableDeemphasis);
        Assert.True(options.EnableExpander);
    }

    [Theory(DisplayName = "Zero Beta reference offset is harmless for VHS and 8mm")]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitZeroOffsetIsAcceptedForExistingFormats(bool is8Mm)
    {
        string[] arguments = is8Mm
            ? ["--8mm", "--beta-reference-offset", "0"]
            : ["--beta-reference-offset", "0"];

        Assert.Equal(0, Options(arguments).BetaReferenceOffsetDb);
    }

    [Theory(DisplayName = "Nonzero Beta reference offset is rejected for other formats")]
    [InlineData(false)]
    [InlineData(true)]
    public void NonzeroOffsetIsRejectedForExistingFormats(bool is8Mm)
    {
        string[] arguments = is8Mm
            ? ["--8mm", "--beta-reference-offset", "1"]
            : ["--beta-reference-offset", "1"];

        Assert.Throws<ArgumentException>(() => Options(arguments));
    }

    public static TheoryData<string[]> UnsupportedBetaOptions => new()
    {
        { new string[] { "--8mm" } },
        { new string[] { "--pal" } },
        { new string[] { "--pal", "--ntsc" } },
        { new string[] { "--audio_rate", "44100" } },
        { new string[] { "--audio_rate", "96000" } },
        { new string[] { "--preview" } },
        { new string[] { "--gnuradio" } },
        { new string[] { "--bias_guess" } },
        { new string[] { "--auto_fine_tune", "on" } },
        { new string[] { "--demod", "hilbert" } },
        { new string[] { "--NR_spectral_amount", "0.25" } },
        { new string[] { "--dsp-backend", "ipp-fast" } },
        { new string[] { "--frequency", "10" } },
        { new string[] { "--frequency", "0.04" } },
        { new string[] { "--frequency", "40.000001" } },
        { new string[] { "--threads", "0" } },
        { new string[] { "--threads", "-1" } },
        { new string[] { "--threads", "2147483648" } },
        { new string[] { "--gain=-1" } },
        { new string[] { "--gain=nan" } },
        { new string[] { "--gain=inf" } },
        { new string[] { "--beta-reference-offset=-12.01" } },
        { new string[] { "--beta-reference-offset=12.01" } },
        { new string[] { "--beta-reference-offset=nan" } },
        { new string[] { "--beta-reference-offset=inf" } },
        { new string[] { "--beta-reference-offset=-inf" } },
        { new string[] { "--audio_mode", "unknown" } },
        { new string[] { "--AFE_left_carrier", "1.4" } },
        { new string[] { "--AFE_right_carrier", "1.8" } },
        { new string[] { "--AFE_left_carrier_deviation", "0.1" } },
        { new string[] { "--AFE_right_carrier_deviation", "0.1" } },
        { new string[] { "--expander_gain", "30" } },
        { new string[] { "--expander_ratio", "2" } },
        { new string[] { "--expander_env_detection", "peak" } },
        { new string[] { "--expander_attack_tau", "0.005" } },
        { new string[] { "--expander_hold_tau", "0" } },
        { new string[] { "--expander_release_tau", "0.02" } },
        { new string[] { "--expander_weighting_low_tau", "0.001" } },
        { new string[] { "--expander_weighting_high_tau", "0.001" } },
        { new string[] { "--expander_weighting_low_pass", "15000" } },
        { new string[] { "--expander_weighting_low_pass_transition", "1000" } },
        { new string[] { "--deemphasis_low_tau", "0.000056" } },
        { new string[] { "--deemphasis_high_tau", "0.000020" } },
        { new string[] { "--nr_deemphasis_low_tau", "0.000056" } },
        { new string[] { "--nr_deemphasis_high_tau", "0.000020" } }
    };
    [Theory(DisplayName = "Unsupported Betamax options fail before reader or output creation")]
    [MemberData(nameof(UnsupportedBetaOptions))]
    public void UnsupportedOptionsFailBeforeInputOrOutputCreation(string[] unsupported)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "vhsdecode-beta-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "input.s16");
            string outputPath = Path.Combine(directory, "output.wav");
            File.WriteAllBytes(inputPath, []);
            int inputOpens = 0;
            int outputOpens = 0;
            var hiFiRunner = new HiFiDecodeRunner(
                new HiFiStreamingDecoder(),
                (_, _) =>
                {
                    inputOpens++;
                    throw new InvalidOperationException("Invalid options reached the RF reader.");
                },
                _ =>
                {
                    outputOpens++;
                    throw new InvalidOperationException("Invalid options reached output creation.");
                });
            var runner = new DecodeRunner(
                _ => throw new InvalidOperationException("Beta HiFi reached the video engine."),
                hiFiRunner);
            ParsedCommand command = Parse(["--betamax", .. unsupported, inputPath, outputPath]);
            var output = new StringWriter(CultureInfo.InvariantCulture);
            var error = new StringWriter(CultureInfo.InvariantCulture);

            int exitCode = runner.Run(
                command, output, error, TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.NotEmpty(error.ToString());
            Assert.Equal(0, inputOpens);
            Assert.Equal(0, outputOpens);
            Assert.False(File.Exists(outputPath));
            Assert.Equal(0, new FileInfo(inputPath).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HiFiDecodeOptions Options(params string[] options)
        => HiFiDecodeOptions.FromCommand(Parse([.. options, "input.s16", "output.wav"]));

    private static ParsedCommand Parse(string[] arguments)
        => new CommandLineParser().Parse(CliSpecs.HiFi, arguments);
}
