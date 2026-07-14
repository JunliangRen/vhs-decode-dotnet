using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class HiFiCompatibilityTests
{
    [Theory(DisplayName = "HiFi raw sample normalization matches v0.4.0 float32 bits")]
    [InlineData("u8", "00017F80FEFF", "BF800000,BF7DFDFE,BB808081,3B808081,3F7DFDFE,3F800000")]
    [InlineData("s8", "8081FF00017F", "BF800000,BF7E0000,BC000000,00000000,3C000000,3F7E0000")]
    [InlineData("u10le", "00000100FF010002FE03FF03", "BF800000,BF7F7FE0,BA802008,3A802008,3F7F7FE0,3F800000")]
    [InlineData("s10le", "00FE01FEFFFF00000100FF01", "BF800000,BF7F8000,BB000000,00000000,3B000000,3F7F8000")]
    [InlineData("u12le", "00000100FF070008FE0FFF0F", "BF800000,BF7FDFFE,B9800801,39800801,3F7FDFFE,3F800000")]
    [InlineData("s12le", "00F801F8FFFF00000100FF07", "BF800000,BF7FE000,BA000000,00000000,3A000000,3F7FE000")]
    [InlineData("u16le", "00000100FF7F0080FEFFFFFF", "BF800000,BF7FFE00,B7800080,37800080,3F7FFE00,3F800000")]
    [InlineData("s16le", "00800180FFFF00000100FF7F", "BF800000,BF7FFE00,B8000000,00000000,38000000,3F7FFE00")]
    [InlineData("f32le", "000080BF00000080000000000000003F0000803F0000C07F", "BF800000,80000000,00000000,3F000000,3F800000,7FC00000")]
    public void HiFiRawSampleNormalizationMatchesV040Float32Bits(
        string formatName,
        string inputHex,
        string expectedBitsText)
    {
        HiFiRawSampleFormat format = HiFiSampleNormalizer.ParseFormat(formatName);
        float[] normalized = HiFiSampleNormalizer.Normalize(Convert.FromHexString(inputHex), format);
        uint[] actualBits = normalized
            .Select(value => unchecked((uint)BitConverter.SingleToInt32Bits(value)))
            .ToArray();
        uint[] expectedBits = expectedBitsText
            .Split(',')
            .Select(value => uint.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(expectedBits, actualBits);
    }

    [Fact(DisplayName = "HiFi sample format routing matches v0.4.0")]
    public void HiFiSampleFormatRoutingMatchesV040()
    {
        Assert.Equal(HiFiRawSampleFormat.S16Le, HiFiSampleNormalizer.ParseFormat("RAW"));
        Assert.Equal(HiFiRawSampleFormat.S10Le, HiFiSampleNormalizer.ParseFormat("S10LE"));
        Assert.Equal(1, HiFiSampleNormalizer.BytesPerSample(HiFiRawSampleFormat.U8));
        Assert.Equal(2, HiFiSampleNormalizer.BytesPerSample(HiFiRawSampleFormat.S12Le));
        Assert.Equal(4, HiFiSampleNormalizer.BytesPerSample(HiFiRawSampleFormat.F32Le));
        Assert.Equal(
            "Unsupported format: packed",
            Assert.Throws<ArgumentException>(() => HiFiSampleNormalizer.ParseFormat("packed")).Message.Split(" (Parameter", StringSplitOptions.None)[0]);

        Span<float> output = stackalloc float[1];
        Assert.Equal(1, HiFiSampleNormalizer.Normalize([0x00, 0x80, 0xFF], output, HiFiRawSampleFormat.S16Le));
        Assert.Equal(-1.0f, output[0]);
        Assert.Throws<ArgumentException>(() => HiFiSampleNormalizer.Normalize([0, 1], [], HiFiRawSampleFormat.U8));
    }

    [Fact(DisplayName = "HiFi CLI spec covers all v0.4.0 options")]
    public void HiFiCliSpecCoversAllV040Options()
    {
        string[] expectedNames =
        [
            "-h", "--help", "--frequency", "-f", "--overwrite", "--threads", "-t",
            "--preview", "--gui", "--gnuradio", "--raw_format", "--pal", "-p", "--ntsc", "-n",
            "--8mm", "--demod", "--bias_guess", "--bg", "--auto_fine_tune",
            "--AFE_left_carrier", "--AFE_left_carrier_deviation", "--AFE_right_carrier",
            "--AFE_right_carrier_deviation", "--normalize", "--gain", "--audio_mode", "--audio_rate",
            "--ar", "--resampler_quality", "--head_switching_interpolation", "--doc",
            "--NR_spectral_amount", "--expander", "--expander_gain", "--expander_ratio",
            "--expander_env_detection", "--expander_attack_tau", "--expander_hold_tau",
            "--expander_release_tau", "--expander_weighting_low_tau", "--expander_weighting_high_tau",
            "--expander_weighting_low_pass", "--expander_weighting_low_pass_transition", "--deemphasis",
            "--deemphasis_low_tau", "--deemphasis_high_tau", "--nr_deemphasis_low_tau",
            "--nr_deemphasis_high_tau"
        ];

        Assert.Equal(42, CliSpecs.HiFi.Options.Length);
        Assert.Equal(expectedNames, CliSpecs.HiFi.Options.SelectMany(option => option.Names));
        Assert.Equal(0, CliSpecs.HiFi.MinimumPositionals);
        Assert.Equal(2, CliSpecs.HiFi.MaximumPositionals);
        Assert.Equal(["hifi-decode"], CliSpecs.HiFi.Aliases);
    }

    [Fact(DisplayName = "HiFi decode options match v0.4.0 defaults and preview overrides")]
    public void HiFiDecodeOptionsMatchV040DefaultsAndPreviewOverrides()
    {
        ParsedCommand defaults = new CommandLineParser().Parse(CliSpecs.HiFi, ["-", "out.flac"]);
        HiFiDecodeOptions defaultOptions = HiFiDecodeOptions.FromCommand(defaults);
        Assert.Equal(40_000_000.0, defaultOptions.InputRateHz);
        Assert.Equal("n", defaultOptions.Standard);
        Assert.Equal("vhs", defaultOptions.TapeFormat);
        Assert.Equal(HiFiConstants.AudioModeStereo, defaultOptions.AudioMode);
        Assert.Equal(48_000, defaultOptions.AudioRateHz);
        Assert.Equal("high", defaultOptions.ResamplerQuality);
        Assert.Equal(30.0, defaultOptions.ExpanderGain);
        Assert.Equal(6.5e-3, defaultOptions.ExpanderAttackTau);
        Assert.Equal(0.0, defaultOptions.ExpanderHoldTau);
        Assert.Equal(56e-6, defaultOptions.DeemphasisLowTau);
        Assert.Equal(20e-6, defaultOptions.DeemphasisHighTau);
        Assert.Equal(Environment.ProcessorCount, defaultOptions.Threads);

        ParsedCommand preview = new CommandLineParser().Parse(CliSpecs.HiFi,
        [
            "--pal", "--ntsc", "--8mm", "--preview", "--resampler_quality", "BOGUS",
            "--audio_mode", "DMS", "--auto_fine_tune", "on", "--NR_spectral_amount", "0.75",
            "--expander", "off", "--deemphasis", "off", "--expander_gain", "0",
            "--expander_env_detection", "", "--audio_rate", "96000", "--AFE_left_carrier", "1.5MHz",
            "--raw_format", "S10LE", "--head_switching_interpolation", "off", "--doc", "MUTE",
            "--gain", "1.25", "--bias_guess", "--normalize", "--gnuradio", "-", "out.wav"
        ]);
        HiFiDecodeOptions previewOptions = HiFiDecodeOptions.FromCommand(preview);
        Assert.Equal("p", previewOptions.Standard);
        Assert.Equal("8mm", previewOptions.TapeFormat);
        Assert.True(previewOptions.Preview);
        Assert.Equal("dms", previewOptions.AudioMode);
        Assert.Equal("low", previewOptions.ResamplerQuality);
        Assert.Equal(0.0, previewOptions.SpectralNoiseReductionAmount);
        Assert.False(previewOptions.AutoFineTune);
        Assert.False(previewOptions.EnableExpander);
        Assert.False(previewOptions.EnableDeemphasis);
        Assert.Equal(6.0, previewOptions.ExpanderGain);
        Assert.Equal("peak", previewOptions.ExpanderEnvelopeDetection);
        Assert.Equal(44_100, previewOptions.AudioRateHz);
        Assert.Equal(1_500_000.0, previewOptions.AfeLeftCarrierHz);
        Assert.Equal("S10LE", previewOptions.InputFormatOverride);
        Assert.False(previewOptions.HeadSwitchingInterpolation);
        Assert.Equal("mute", previewOptions.DropoutCompensation);
        Assert.Equal(1.25, previewOptions.Gain);
        Assert.True(previewOptions.BiasGuess);
        Assert.True(previewOptions.Normalize);
        Assert.True(previewOptions.GnuRadio);
        Assert.Equal(3e-3, previewOptions.ExpanderAttackTau);
        Assert.Equal(15e-3, previewOptions.ExpanderHoldTau);
        Assert.Equal(75e-6, previewOptions.DeemphasisLowTau);
        Assert.Equal(27e-6, previewOptions.DeemphasisHighTau);
    }

    [Fact(DisplayName = "HiFi help snapshots match v0.4.0 argparse")]
    public void HiFiHelpSnapshotsMatchV040Argparse()
    {
        string standalone = CommandHelpFormatter.Format(CliSpecs.HiFi, "hifi-decode");
        string facade = CommandHelpFormatter.Format(CliSpecs.HiFi, "decode.py");

        Assert.StartsWith("usage: hifi-decode ", standalone, StringComparison.Ordinal);
        Assert.StartsWith("usage: decode.py ", facade, StringComparison.Ordinal);
        Assert.Contains("Expander tuning options (advanced):", standalone, StringComparison.Ordinal);
        Assert.Contains("Deemphasis tuning options (advanced):", standalone, StringComparison.Ordinal);
        Assert.Equal("27E40DE774B9CD5E3A6E126A497B074AD239319AD7A79004D4594F74CB7ECC2B", Utf8LfSha256(standalone));
        Assert.Equal("14F69F3DD5869D3BAF3D44558ADF32DAC040F97F37CF00A2F3127FA101D7765E", Utf8LfSha256(facade));
    }

    private static string Utf8LfSha256(string value)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
