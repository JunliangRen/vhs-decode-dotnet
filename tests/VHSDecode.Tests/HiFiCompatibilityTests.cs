using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Dsp;
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

    [Theory(DisplayName = "HiFi AFE standards match v0.4.0")]
    [InlineData("vhs", "n", 59.94, 15_750.0, 150_000.0, 150_000.0, 371_506.25, 371_506.25, 1_300_000.0, 1_700_000.0)]
    [InlineData("vhs", "p", 50.0, null, 150_000.0, 150_000.0, 371_506.25, 371_506.25, 1_400_000.0, 1_800_000.0)]
    [InlineData("8mm", "n", 59.94, 15_750.0, 100_000.0, 50_000.0, 240_000.0, 75_000.0, 1_500_000.0, 1_700_000.0)]
    [InlineData("8mm", "p", 50.0, 15_625.0, 100_000.0, 50_000.0, 240_000.0, 75_000.0, 1_500_000.0, 1_700_000.0)]
    public void HiFiAfeStandardsMatchV040(
        string tapeFormat,
        string standard,
        double fieldRateHz,
        double? horizontalFrequencyHz,
        double leftDeviationHz,
        double rightDeviationHz,
        double leftNotchWidthHz,
        double rightNotchWidthHz,
        double leftCarrierHz,
        double rightCarrierHz)
    {
        HiFiDecodeOptions options = DefaultOptions() with
        {
            TapeFormat = tapeFormat,
            Standard = standard
        };
        HiFiAfeParameters actual = HiFiAfeParameters.FromOptions(options);

        Assert.Equal(fieldRateHz, actual.FieldRateHz);
        Assert.Equal(horizontalFrequencyHz, actual.HorizontalFrequencyHz);
        Assert.Equal(leftDeviationHz, actual.LeftCarrierDeviationHz);
        Assert.Equal(rightDeviationHz, actual.RightCarrierDeviationHz);
        Assert.Equal(leftNotchWidthHz, actual.LeftNotchWidthHz);
        Assert.Equal(rightNotchWidthHz, actual.RightNotchWidthHz);
        Assert.Equal(leftCarrierHz, actual.LeftCarrierHz);
        Assert.Equal(rightCarrierHz, actual.RightCarrierHz);
        Assert.Equal(leftCarrierHz - leftNotchWidthHz, actual.LeftBandPassLowHz);
        Assert.Equal(rightCarrierHz + rightNotchWidthHz, actual.RightBandPassHighHz);
    }

    [Fact(DisplayName = "HiFi AFE overrides preserve v0.4.0 notch widths")]
    public void HiFiAfeOverridesPreserveV040NotchWidths()
    {
        HiFiDecodeOptions options = DefaultOptions() with
        {
            AfeLeftCarrierDeviationHz = 123_456.0,
            AfeRightCarrierDeviationHz = 234_567.0,
            AfeLeftCarrierHz = 1_456_789.0,
            AfeRightCarrierHz = 1_765_432.0
        };
        HiFiAfeParameters actual = HiFiAfeParameters.FromOptions(options);

        Assert.Equal(123_456.0, actual.LeftCarrierDeviationHz);
        Assert.Equal(234_567.0, actual.RightCarrierDeviationHz);
        Assert.Equal(1_456_789.0, actual.LeftCarrierHz);
        Assert.Equal(1_765_432.0, actual.RightCarrierHz);
        Assert.Equal(371_506.25, actual.LeftNotchWidthHz);
        Assert.Equal(371_506.25, actual.RightNotchWidthHz);
    }

    [Theory(DisplayName = "HiFi AFE Chebyshev-II filters match v0.4.0 float bits")]
    [InlineData(40_000_000, 1_300_000.0, 371_506.25, "013DD3F9C36B5EB32E8D30492C8C3399D07CCA80DA65DBECE4C6E9A54A785D59", "68E787344B944B50302EDCB8DE13C51915E21D6686D0174146BE6BBD115344B5")]
    [InlineData(40_000_000, 1_700_000.0, 371_506.25, "2724B050D8C33E44F8C957A007F2DF214FF169AFC8D50872765100C9E6419541", "47B6C958B4DC3775C784C0ED42E66B23C66906463A19FE087A2399781C5F2672")]
    [InlineData(40_000_000, 1_400_000.0, 371_506.25, "BC9934C999496F968091F375E493EEE44FEFBA48243A1E3EEB2353253C338B3D", "DFEBB44A9F59676A3AE97F4680286A41BDC41AD347838DDCDF16CDA1DD069DBA")]
    [InlineData(40_000_000, 1_800_000.0, 371_506.25, "F259E8F9866E5F57348EFB68ABBEE77DDF47143619CFD07C4998425759A3D5F4", "AFD5CED3CDCD97A1A2B27E215B84AEE20A8468F466F5E9AD33BEFAD793C8E7E3")]
    [InlineData(40_000_000, 1_500_000.0, 240_000.0, "D05789CF147962A83F9270C797754C7AEBC291E5BD05A3E5494ABA99D28BC435", "59F30845739FDF2F5670802A3D8B569E9995D2C9AB30E51463FEF71D8BA60AC7")]
    [InlineData(40_000_000, 1_700_000.0, 75_000.0, "65B44189091CEC193669D5994E1EB9048A8D946B37500BA51D2207AC7F2D657E", "E280782EEE0B87ED122464196FDE2C88F4F9248E5FDE742AA2DEB7E7C269575D")]
    [InlineData(8_388_608, 1_300_000.0, 371_506.25, "63AF471BB1FD22CCCC6CA1D7A59DF622B34A1B34A33427718F06525BCDEE9603", "CBE5AE6280D37D3B6BC706CC138C0B2964C8DCE9989158325ACCCD77BC822685")]
    [InlineData(8_388_608, 1_700_000.0, 371_506.25, "A3E5376A2BC2CE94A5365B1FB3DDDF78016BD59055BC65BCDD7B15A88180E9DE", "57A2896302EF6D42CE5EEDF7A7991B1F31311BAB6DCB00CE780691363ED8B771")]
    [InlineData(8_388_608, 1_400_000.0, 371_506.25, "0A880586ABF4DBDCFF03512BDD937801ACEE6BF1E5E99E5C7CB8D6B66B52EEDE", "AB972B13E48CAE6DDF2DBD2199B12AFBB57D81E64DEFD03DF657A837B827C863")]
    [InlineData(8_388_608, 1_800_000.0, 371_506.25, "847073D192D1B141B848CDC200FA2807138A6F14A1A2B3D9632585FA349A830D", "15010F0BCBFF79CCA74B462D9981D74A9E25A6943BF05CA1424FC577F9BDCC14")]
    [InlineData(8_388_608, 1_500_000.0, 240_000.0, "BAF9FA4890B48EC2D60AF8CBB5D9DD7622EB22929DF1130BB191B3B30280DBEB", "B31DCA42595F44CDDA94C273D9179CCA3263BAFF7DEB736E04A0D3D8D88A6F27")]
    [InlineData(8_388_608, 1_700_000.0, 75_000.0, "A0B14E389944B65F560ABB3A662A9D4BAA3B6C9F1E6D5818951160629652AC57", "06481F9EA0EEB4CF5334337E147417BB6B3FAA019DD8345307CF34E170C8436D")]
    [InlineData(40_000_000, 1_456_789.0, 371_506.25, "EDD5304DE99A4A9FEB87EF3B9BD119387B5D2B001E0A4F2BDDA6EC6E71ACC61D", "E8DBEC146CBE01A90353961217E6B717A03B5D53CF4EED51B3A18C6A1AE6DD2B")]
    public void HiFiAfeChebyshevTypeIIFiltersMatchV040FloatBits(
        int sampleRateHz,
        double carrierHz,
        double widthHz,
        string expectedSosHash,
        string expectedOutputHash)
    {
        var filter = new HiFiAfeFilter(sampleRateHz, carrierHz, widthHz);
        SosSection[] sections = filter.Sections.ToArray();
        float[] flattened = sections
            .SelectMany(section => new[]
            {
                (float)section.B0,
                (float)section.B1,
                (float)section.B2,
                (float)section.A0,
                (float)section.A1,
                (float)section.A2
            })
            .ToArray();
        float[] input = CreateDeterministicFloatInput(2048);
        float[] output = filter.Apply(input);

        Assert.Equal(22, sections.Length);
        Assert.Equal(expectedSosHash, BinarySha256(flattened));
        Assert.Equal(expectedOutputHash, BinarySha256(output));
    }

    [Fact(DisplayName = "HiFi quadrature block plan matches v0.4.0")]
    public void HiFiQuadratureBlockPlanMatchesV040()
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions());

        Assert.Equal(40_000_000, plan.InputRateHz);
        Assert.Equal(40_000_000, plan.IfRateHz);
        Assert.Equal(192_000, plan.AudioRateHz);
        Assert.Equal(48_000, plan.FinalAudioRateHz);
        AssertRatio(plan.ResamplingRatios.InputToIf, "1", "1");
        AssertRatio(plan.ResamplingRatios.IfToAudio, "5534023222112865", "1152921504606846976");
        AssertRatio(plan.ResamplingRatios.AudioToFinal, "1", "4");
        Assert.Equal(new HiFiBlockSizes(0.5, 20_000_000, 20_000_000, 96_000, 24_000), plan.InitialBlockSizes);
        Assert.Equal(new HiFiBlockOverlap(440_000, 220_000, 220_000, 264, false), plan.BlockOverlap);
        Assert.Equal(1_000, plan.PreTrimSamples);
    }

    [Fact(DisplayName = "HiFi Hilbert IF plan matches v0.4.0")]
    public void HiFiHilbertIfPlanMatchesV040()
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions() with
        {
            DemodType = HiFiConstants.DemodHilbert
        });

        Assert.Equal(8_388_608, plan.IfRateHz);
        AssertRatio(plan.ResamplingRatios.InputToIf, "944473296573929", "4503599627370496");
        AssertRatio(plan.ResamplingRatios.IfToAudio, "375", "16384");
        Assert.Equal(4_194_304, plan.InitialBlockSizes.IfSamples);
        Assert.Equal(new HiFiBlockOverlap(440_000, 220_000, 220_000, 264, false), plan.BlockOverlap);
    }

    [Fact(DisplayName = "HiFi 44.1 kHz overlap plan matches v0.4.0")]
    public void HiFi44100OverlapPlanMatchesV040()
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions() with
        {
            AudioRateHz = 44_100
        });

        AssertRatio(plan.ResamplingRatios.AudioToFinal, "4137682157646643", "18014398509481984");
        Assert.Equal(new HiFiBlockSizes(0.5, 20_000_000, 20_000_000, 96_000, 22_050), plan.InitialBlockSizes);
        Assert.Equal(new HiFiBlockOverlap(3_482_994, 1_741_497, 1_741_497, 1_920, false), plan.BlockOverlap);
    }

    [Fact(DisplayName = "HiFi fractional input rate truncation matches v0.4.0")]
    public void HiFiFractionalInputRateTruncationMatchesV040()
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions() with
        {
            InputRateHz = 28_636_363.636363637,
            DemodType = HiFiConstants.DemodHilbert,
            AudioRateHz = 96_000
        });

        Assert.Equal(28_636_363, plan.InputRateHz);
        AssertRatio(plan.ResamplingRatios.InputToIf, "659632158297427", "2251799813685248");
        AssertRatio(plan.ResamplingRatios.IfToAudio, "375", "16384");
        AssertRatio(plan.ResamplingRatios.AudioToFinal, "1", "2");
        Assert.Equal(new HiFiBlockSizes(0.5, 14_318_182, 4_194_304, 96_000, 48_000), plan.InitialBlockSizes);
        Assert.Equal(new HiFiBlockOverlap(313_210, 156_605, 156_606, 525, true), plan.BlockOverlap);
    }

    [Fact(DisplayName = "HiFi custom block sizing keeps initial overlap like v0.4.0")]
    public void HiFiCustomBlockSizingKeepsInitialOverlapLikeV040()
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions());
        HiFiBlockSizes custom = plan.CalculateBlockSizes(1_234_567);

        Assert.Equal(0.030864175, custom.BlocksPerSecondRatio);
        Assert.Equal(1_234_567, custom.InputSamples);
        Assert.Equal(1_234_567, custom.IfSamples);
        Assert.Equal(5_926, custom.AudioSamples);
        Assert.Equal(1_482, custom.FinalAudioSamples);
        Assert.Equal(new HiFiBlockOverlap(440_000, 220_000, 220_000, 264, false), plan.BlockOverlap);
        Assert.Equal(23_472, plan.CalculateFinalAudioLength(20_000_000, false));
        Assert.Equal(1_464, plan.CalculateFinalAudioLength(1_000_000, true));
    }

    [Fact(DisplayName = "HiFi irregular final rate plan matches v0.4.0")]
    public void HiFiIrregularFinalRatePlanMatchesV040()
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions() with
        {
            InputRateHz = 40_000_000.75,
            AudioRateHz = 47_999
        });

        Assert.Equal(40_000_000, plan.InputRateHz);
        AssertRatio(plan.ResamplingRatios.AudioToFinal, "4503505802378259", "18014398509481984");
        Assert.Equal(new HiFiBlockSizes(0.5, 20_000_000, 20_000_000, 96_000, 24_000), plan.InitialBlockSizes);
        Assert.Equal(new HiFiBlockOverlap(440_010, 220_005, 220_005, 264, false), plan.BlockOverlap);
    }

    [Theory(DisplayName = "HiFi resampler profiles match v0.4.0")]
    [InlineData("high", "VHQ", "VHQ", "VHQ")]
    [InlineData("medium", "LQ", "MQ", "HQ")]
    [InlineData("low", "LQ", "LQ", "LQ")]
    [InlineData("unexpected", "LQ", "LQ", "LQ")]
    public void HiFiResamplerProfilesMatchV040(
        string quality,
        string inputToIf,
        string ifToAudio,
        string audioToFinal)
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions() with
        {
            ResamplerQuality = quality
        });

        Assert.Equal(new HiFiResamplerConverters(inputToIf, ifToAudio, audioToFinal), plan.ResamplerConverters);
    }

    [Fact(DisplayName = "HiFi plan rejects unsupported decode configuration")]
    public void HiFiPlanRejectsUnsupportedDecodeConfiguration()
    {
        Assert.Throws<ArgumentException>(() => HiFiDecodePlan.FromOptions(DefaultOptions() with
        {
            DemodType = "phase-locked-loop"
        }));
        Assert.Throws<ArgumentException>(() => HiFiAfeParameters.FromOptions(DefaultOptions() with
        {
            TapeFormat = "betamax"
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HiFiDecodePlan.FromOptions(DefaultOptions() with
        {
            InputRateHz = 0.0
        }));
    }

    [Theory(DisplayName = "HiFi quadrature oscillator geometry matches v0.4.0")]
    [InlineData(40_000_000, 1_300_000, 32_768, 200)]
    [InlineData(40_000_000, 1_700_000, 32_768, 200)]
    [InlineData(40_000_000, 1_456_789, 17, 17)]
    [InlineData(8_388_608, 1_500_000, 10_000, 10_000)]
    public void HiFiQuadratureOscillatorGeometryMatchesV040(
        int sampleRateHz,
        int carrierHz,
        int maximumLength,
        int expectedLength)
    {
        Assert.Equal(
            expectedLength,
            HiFiQuadratureDiscriminator.MinimumOscillatorLength(
                sampleRateHz,
                carrierHz,
                maximumLength));
    }

    [Theory(DisplayName = "HiFi quadrature discriminator matches v0.4.0 float bits")]
    [InlineData(40_000_000, 1_300_000.0, 150_000.0, 32_768, "7C353913EDD6D7650DA78FF1FA142C63CDC68365F2B3C0E8367B49738427E228", "F7D9FAEF0F5E0C78A06DD0FB79EECF4F24C4A0DFBEFD38DC0A456CA78E671A07", "88B39B7C06C65AD9844E55C42BF04B6FB8F1EDDB620851F4D09437FE1C8E2317")]
    [InlineData(40_000_000, 1_700_000.0, 150_000.0, 32_768, "878CE5D98C3526AE1FF605B6460A7868F80B2FA104BD2E4DCC1C732C2ED8983F", "31FABABD49F014A43212EE99F3A26209F03AA611C1A361CD93743EF2C2FD9ACD", "B2F98EA7E20B39A5664B6FD9BD174629F02C3C4FE9D7E03D33D38BF7D0EFC612")]
    [InlineData(40_000_000, 1_456_789.4, 123_456.0, 17, "8F39D52F8E6691BCAFBD6637BE064C7D2EFDEE8C69930F370FB00534388C0322", "86C330B23A7B929F4A3D6E8A76CBA6854A54F6A432B14844F3318AD5912E2631", "D47D60EFAC9E83A51E68870FA699F9159F421DCB28D5E84C569ECA338ADB9AB1")]
    [InlineData(8_388_608, 1_500_000.0, 100_000.0, 10_000, "4E43D97815EE52B832AC6F1925F6CD2B985A9464FC25A0DFD661A293545B84C3", "F6DD58BE92DD1E5EE6A3950B8B3AFEC60E872397BA22FC132FDEF1A59EACEC88", "27CEA9439DA412E60087D8D4C9D1A12631BAD8669BB5676DD748724CE3FD1126")]
    public void HiFiQuadratureDiscriminatorMatchesV040FloatBits(
        int sampleRateHz,
        double carrierHz,
        double deviationHz,
        int maximumOscillatorLength,
        string expectedInPhaseHash,
        string expectedQuadratureHash,
        string expectedOutputHash)
    {
        var discriminator = new HiFiQuadratureDiscriminator(
            sampleRateHz,
            carrierHz,
            deviationHz,
            maximumOscillatorLength);
        float[] input = Enumerable.Range(0, 257)
            .Select(index => (float)(((index * 37) % 251) - 125) / 128.0f)
            .ToArray();
        float[] output = Enumerable.Repeat(123.25f, input.Length).ToArray();

        discriminator.Demodulate(input, output);

        Assert.Equal(expectedInPhaseHash, BinarySha256(discriminator.InPhaseOscillator.Span));
        Assert.Equal(expectedQuadratureHash, BinarySha256(discriminator.QuadratureOscillator.Span));
        Assert.Equal(expectedOutputHash, BinarySha256(output));
        Assert.Equal(123.25f, output[^1]);
    }

    [Fact(DisplayName = "HiFi quadrature carrier rounding matches v0.4.0")]
    public void HiFiQuadratureCarrierRoundingMatchesV040()
    {
        Assert.Equal(
            1_456_788,
            new HiFiQuadratureDiscriminator(40_000_000, 1_456_788.5, 123_456.9, 17).CarrierHz);
        Assert.Equal(
            123_456,
            new HiFiQuadratureDiscriminator(40_000_000, 1_456_788.5, 123_456.9, 17).DeviationHz);
    }

    [Fact(DisplayName = "HiFi Hilbert phase kernel matches v0.4.0 float bits")]
    public void HiFiHilbertPhaseKernelMatchesV040FloatBits()
    {
        float[] phase =
        [
            0.0f, 0.25f, 3.0f, -3.0f, -2.5f, 2.9f,
            -3.1f, 3.1f, -3.14f, 3.14f, 0.0f
        ];
        float[] output = Enumerable.Repeat(123.25f, phase.Length).ToArray();

        HiFiHilbertDiscriminator.DemodulatePhase(
            phase,
            output,
            8_388_608,
            1_500_000,
            100_000);

        Assert.Equal(
            "99FD09AB9488B7DFD95166DEA2D4753FFD9CA6D1DCFEF1FFCBC75BFE0C02FE98",
            BinarySha256(output));
        Assert.Equal(123.25f, output[^1]);
    }

    [Theory(DisplayName = "HiFi Hilbert FFT discriminator matches v0.4.0 float bits")]
    [InlineData(64, "4CCCB11215C35B4CF6E3BBBA99AC2DCC0763C394D81D0A633B749144F48B0B01")]
    [InlineData(128, "C82C86BA3413B0BC2CBDB53845D6CA478F4166DE5964A2C7C2E656E8210E8AE6")]
    [InlineData(255, "81D40AF0ABE64834A0D153B6F43B8C2C4D38E3D5F058000C1531FD796975C1F8")]
    [InlineData(256, "38C55E64B4E5E0AD9CD0160AB0DBCA59DE265043618C27CAA00A940DE0F89F45")]
    [InlineData(257, "941DCA6294586C8B99583D35FB1CDBD1324C453DEC89573589A963B82F787CD1")]
    [InlineData(512, "7AEA0D18364F6C89AF1F764816AB6416B24793756E339C20DF67A7470E789E6D")]
    [InlineData(1000, "9489484FE45539857F30E34041E1A5DAED841230CB9BEC8FEAB10DFCB549EEDB")]
    [InlineData(1024, "8F99555022C09B162EB9362AA13845B2B3C4250AFE3954393C328ABB9B6450B9")]
    public void HiFiHilbertFftDiscriminatorMatchesV040FloatBits(
        int length,
        string expectedOutputHash)
    {
        float[] input = Enumerable.Range(0, length)
            .Select(index => (float)(((index * 37) % 251) - 125) / 128.0f)
            .ToArray();
        float[] output = Enumerable.Repeat(123.25f, input.Length).ToArray();
        var discriminator = new HiFiHilbertDiscriminator(
            8_388_608,
            1_500_000,
            100_000);

        discriminator.Demodulate(input, output);

        Assert.Equal(expectedOutputHash, BinarySha256(output));
        Assert.Equal(123.25f, output[^1]);
    }

    [Theory(DisplayName = "HiFi Hilbert FFT random discriminator matches v0.4.0 float bits")]
    [InlineData(64, "97E25C6FA9C1C1602A162A0364A10DBF5AD22AF068B08497F28682F96D59DA22")]
    [InlineData(128, "51AA963F25C574E013CA3446036336635A1935F2872838CB015B59F8A3CE902B")]
    [InlineData(255, "8F62CB790355C4328907DFC1D3547F1343B186790019CDEDA82BC82782ABD8EE")]
    [InlineData(256, "9A512755DA76439087EE6A2B77B1D880B1EC15EFABD891A80A41F81B4713C7A5")]
    [InlineData(257, "CFDB7727E2A94F9C2A6B2C4270EB1E7B59AE2C4790B9330A25FA0F164273BE28")]
    [InlineData(512, "8F5D781234FC28EF7D8CBFE1A5FB802AC70380B3943FD393B8659D274D496230")]
    [InlineData(1000, "A318DB9BA05C1FF8B244C2CFA7C2FFAA098A8FCB1552F1A1647631FEC74D6B7A")]
    [InlineData(1024, "F1599DFA9591AC0A168F0BEE7F87E46B68B3CEB5CD9156F7D0FA83CE3C2C0395")]
    public void HiFiHilbertFftRandomDiscriminatorMatchesV040FloatBits(
        int length,
        string expectedOutputHash)
    {
        uint state = 0x12345678;
        var input = new float[length];
        for (int i = 0; i < input.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            input[i] = ((int)(state >> 8) - 0x800000) / (float)0x800000;
        }

        float[] output = Enumerable.Repeat(123.25f, input.Length).ToArray();
        var discriminator = new HiFiHilbertDiscriminator(
            8_388_608,
            1_500_000,
            100_000);

        discriminator.Demodulate(input, output);

        Assert.Equal(expectedOutputHash, BinarySha256(output));
        Assert.Equal(123.25f, output[^1]);
    }

    [Theory(DisplayName = "HiFi Hilbert FFT phase stays within v0.4.0 float tolerance")]
    [InlineData(256, "4030CE4C,C0227025,C01247B9,BFDDE3CA,BF945D7B,BF42D2ED,3F3E9D52,401BD78E,C01C99D2,C0190FE4,BFD13EC0,BF69439E")]
    [InlineData(255, "40330D4E,C01A3B09,C0151F04,BFDC2619,BF8CD2AE,BF58AF02,3F450069,401E9A18,C01E8D37,C014CFB0,BFD09424,BF7BA9AB")]
    public void HiFiHilbertFftPhaseStaysWithinV040FloatTolerance(
        int length,
        string expectedBits)
    {
        float[] input = Enumerable.Range(0, length)
            .Select(index => (float)(((index * 37) % 251) - 125) / 128.0f)
            .ToArray();
        float[] expected = expectedBits
            .Split(',')
            .Select(value => BitConverter.Int32BitsToSingle(unchecked((int)uint.Parse(
                value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture))))
            .ToArray();

        HiFiHilbertDiscriminator.ComputeAnalyticPhase(input);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.InRange(MathF.Abs(input[i] - expected[i]), 0.0f, 2e-5f);
        }
    }

    [Theory(DisplayName = "HiFi libsoxr block resampling matches v0.4.0 float bits")]
    [InlineData("4503599627370496", "944473296573929", SoxrQuality.Low, 4096, 859, "C303FCD7B275FA770BBD5D57EF603E097E7472C390943485C4F1FD8E5C7E1EC6")]
    [InlineData("4503599627370496", "944473296573929", SoxrQuality.VeryHigh, 4096, 859, "F4A195634E48A9375360F8787025685EDE30DEE4687CEDC4579035AE85CD3359")]
    [InlineData("16384", "375", SoxrQuality.Low, 8192, 188, "394E5F9C7A9C2B5574A98CD9A3DF5879968A6B347821FC2C9EA6CB4BDB6AE144")]
    [InlineData("16384", "375", SoxrQuality.Medium, 8192, 188, "2B5E0CE1D99BE8DA9B42074BAA9CF58B4A34683F8187CD60A1547564952C7DA4")]
    [InlineData("16384", "375", SoxrQuality.VeryHigh, 8192, 188, "5EA39F504393026DC7D77D821E277413880DEBCEEB8EE099DDA65150407683C2")]
    [InlineData("4", "1", SoxrQuality.Low, 4096, 1024, "7E5949D573BF3780BA0B280846C58730770703FBC663768EC95D9E0A50C848C8")]
    [InlineData("4", "1", SoxrQuality.High, 4096, 1024, "6BC1C30C85BBF029D111C23EA21D9327C0E8374283C0F8B6A7AC957BE30428CE")]
    [InlineData("4", "1", SoxrQuality.VeryHigh, 4096, 1024, "8885417FC24AABB2686703F8BD300B5DA9207583A18C0AA953875F1B636B29FB")]
    [InlineData("18014398509481984", "4137682157646643", SoxrQuality.Low, 4096, 941, "A1D4C4A7875E03D4E98627BFD6CDF8B9446516FC4FD186B23F083A446179F0AD")]
    [InlineData("18014398509481984", "4137682157646643", SoxrQuality.High, 4096, 941, "98AF5A51C2ED158E7257D2156C2B0AED7F6008C2655B470CDEB53B645A8173BF")]
    [InlineData("18014398509481984", "4137682157646643", SoxrQuality.VeryHigh, 4096, 941, "5484495BC955DDB6F9ADEE5C28BDCDB53E0125C33C0AB35E0AAF7BDAD47CA5E8")]
    public void HiFiLibsoxrBlockResamplingMatchesV040FloatBits(
        string inputRateValue,
        string outputRateValue,
        SoxrQuality quality,
        int inputLength,
        int expectedOutputLength,
        string expectedOutputHash)
    {
        double inputRate = (double)BigInteger.Parse(inputRateValue, CultureInfo.InvariantCulture);
        double outputRate = (double)BigInteger.Parse(outputRateValue, CultureInfo.InvariantCulture);
        float[] input = CreateDeterministicFloatInput(inputLength);
        using var resampler = new SoxrFloat32Resampler(inputRate, outputRate, quality);

        float[] firstOutput = resampler.Process(input, last: true);

        Assert.True(resampler.Ended);
        Assert.Equal(expectedOutputLength, firstOutput.Length);
        Assert.Equal(expectedOutputHash, BinarySha256(firstOutput));
        Assert.Throws<InvalidOperationException>(() => resampler.Process(input));

        resampler.Clear();
        float[] repeatedOutput = resampler.Process(input, last: true);

        Assert.Equal(firstOutput, repeatedOutput);
        Assert.Equal(expectedOutputHash, BinarySha256(repeatedOutput));
    }

    [Fact(DisplayName = "HiFi bundled libsoxr version matches v0.4.0")]
    public void HiFiBundledLibsoxrVersionMatchesV040()
    {
        Assert.Equal("libsoxr-0.1.3", SoxrFloat32Resampler.LibraryVersion);
    }

    [Theory(DisplayName = "HiFi block resampler quality routing matches v0.4.0")]
    [InlineData("low", SoxrQuality.Low, SoxrQuality.Low, SoxrQuality.Low)]
    [InlineData("medium", SoxrQuality.Low, SoxrQuality.Medium, SoxrQuality.High)]
    [InlineData("high", SoxrQuality.VeryHigh, SoxrQuality.VeryHigh, SoxrQuality.VeryHigh)]
    public void HiFiBlockResamplerQualityRoutingMatchesV040(
        string quality,
        SoxrQuality expectedInputToIf,
        SoxrQuality expectedIfToAudio,
        SoxrQuality expectedAudioToFinal)
    {
        HiFiDecodeOptions options = DefaultOptions() with
        {
            DemodType = HiFiConstants.DemodHilbert,
            ResamplerQuality = quality
        };
        using var resamplers = new HiFiBlockResamplers(HiFiDecodePlan.FromOptions(options));

        Assert.Equal(expectedInputToIf, resamplers.InputToIfQuality);
        Assert.Equal(expectedIfToAudio, resamplers.IfToAudioQuality);
        Assert.Equal(expectedAudioToFinal, resamplers.AudioToFinalQuality);
    }

    [Fact(DisplayName = "HiFi block resampler bypasses unchanged v0.4.0 rates")]
    public void HiFiBlockResamplerBypassesUnchangedV040Rates()
    {
        HiFiDecodeOptions options = DefaultOptions() with
        {
            DemodType = HiFiConstants.DemodQuadrature,
            AudioRateHz = HiFiConstants.IntermediateAudioRate
        };
        using var resamplers = new HiFiBlockResamplers(HiFiDecodePlan.FromOptions(options));
        float[] input = CreateDeterministicFloatInput(257);

        float[] ifOutput = resamplers.ResampleInputToIf(input);
        float[] finalOutput = resamplers.ResampleLeftAudioToFinal(input);

        Assert.Equal(input, ifOutput);
        Assert.Equal(input, finalOutput);
        Assert.NotSame(input, ifOutput);
        Assert.NotSame(input, finalOutput);
    }

    [Theory(DisplayName = "HiFi AFE/FM/audio block chain matches v0.4.0 float bits")]
    [InlineData("quadrature", "BE838838", "BE09159B", "7EFD9C87BF46CB6A144BF6477FF7734D89CBA36511A1F9680FA016D2C49A7FCD", "B9DD71B88D94E838F68AC47CD81E6683ABBC1B4EA813EC97170EDC2CA70C571C")]
    [InlineData("hilbert", "BE66626D", "BDC46843", "50C1401C2DECE67C50791412FAA8FE274C1147B792A44C509DB7E0B751B149E9", "F125DA33072028149CED2BB4F2F0DE668C6348F870ADB34AF8DC5DEDF9E55B6D")]
    public void HiFiAfeFmAudioBlockChainMatchesV040FloatBits(
        string demodType,
        string expectedLeftDcBits,
        string expectedRightDcBits,
        string expectedLeftHash,
        string expectedRightHash)
    {
        HiFiDecodeOptions options = DefaultOptions() with
        {
            DemodType = demodType,
            ResamplerQuality = "high",
            AudioMode = HiFiConstants.AudioModeStereo
        };
        using var decoder = new HiFiBlockDecoder(options);
        float[] input = CreateDeterministicFloatInput(524_288);

        HiFiDemodulatedBlock block = decoder.Decode(input);

        Assert.NotNull(block.Left);
        Assert.NotNull(block.Right);
        Assert.Equal(2517, block.Left.Length);
        Assert.Equal(2517, block.Right.Length);
        Assert.Equal(expectedLeftDcBits, BitConverter.SingleToUInt32Bits(block.LeftDc).ToString("X8"));
        Assert.Equal(expectedRightDcBits, BitConverter.SingleToUInt32Bits(block.RightDc).ToString("X8"));
        Assert.Equal(expectedLeftHash, BinarySha256(block.Left));
        Assert.Equal(expectedRightHash, BinarySha256(block.Right));
    }

    [Theory(DisplayName = "HiFi Numba fast mean reduction matches v0.4.0 float bits")]
    [InlineData(1, "BDABCD90")]
    [InlineData(2, "3E84E70A")]
    [InlineData(3, "BD7CB00B")]
    [InlineData(4, "BE18A7D8")]
    [InlineData(5, "BDE4AC60")]
    [InlineData(7, "3E184DD0")]
    [InlineData(8, "3E78149B")]
    [InlineData(15, "3D2D8BA0")]
    [InlineData(31, "3DA15834")]
    [InlineData(32, "3DCA08BA")]
    [InlineData(33, "3DCF74F6")]
    [InlineData(35, "3D97072A")]
    [InlineData(36, "3D8B1850")]
    [InlineData(63, "BAF7E410")]
    [InlineData(64, "BC64B85E")]
    [InlineData(65, "BABBDC10")]
    [InlineData(517, "BD2BAEF9")]
    [InlineData(94_000, "3B33FA9D")]
    public void HiFiNumbaFastMeanReductionMatchesV040FloatBits(
        int length,
        string expectedBits)
    {
        float actual = HiFiAudioProcessing.NumbaFastMean(CreateDeterministicFloatInput(length));

        Assert.Equal(expectedBits, BitConverter.SingleToUInt32Bits(actual).ToString("X8"));
    }

    [Fact(DisplayName = "HiFi DOC FFT statistics match v0.4.0 float bits")]
    public void HiFiDocFftStatisticsMatchV040FloatBits()
    {
        float[] input = CreateDeterministicFloatInput(128);
        for (int i = 0; i < input.Length; i++)
        {
            input[i] *= 4.0f;
        }

        Complex32[] spectrum = NumpyComplex64Fft.ForwardReal(input);
        (float mean, float standardDeviation) = HiFiDropoutCompensator.AnalyzeWindow(input);
        float[] magnitude = HiFiDropoutCompensator.AnalyzeWindowMagnitude(input);

        Assert.Equal(
            "9B3DEA37E677027C1F81ACBC1D90E48D03779BB7B340E0892ECC8E35DB3D141F",
            BinarySha256(spectrum));
        Assert.Equal(
            "BE4A8DFE3659D0E3EFFEAAED4CAFB447FC5FBAAFF2222C0698AC6B8A79A80928",
            BinarySha256(magnitude));
        Assert.Equal("41A1682B", BitConverter.SingleToUInt32Bits(mean).ToString("X8"));
        Assert.Equal("41288938", BitConverter.SingleToUInt32Bits(standardDeviation).ToString("X8"));
    }

    [Fact(DisplayName = "HiFi DOC raised-cosine DC interpolation matches v0.4.0 float bits")]
    public void HiFiDocRaisedCosineDcInterpolationMatchesV040FloatBits()
    {
        float[] source = CreateDeterministicFloatInput(1024);
        Assert.Equal(
            "3B1AEAC0",
            BitConverter.SingleToUInt32Bits(HiFiAudioProcessing.NumbaFastMean(source[..256])).ToString("X8"));
        Assert.Equal(
            "3D42B4CC",
            BitConverter.SingleToUInt32Bits(HiFiAudioProcessing.NumbaFastMean(source[768..])).ToString("X8"));
        float before = BitConverter.UInt32BitsToSingle(0x3B1AEAC0);
        float after = BitConverter.UInt32BitsToSingle(0x3D42B4CC);

        float[] actual = HiFiDropoutCompensator.InterpolateDc(before, after, 768);

        Assert.Equal(
            "691BB95900C3651B1C13D6A8C2CAA834AB446E209AB853D836986C46F850EBA8",
            BinarySha256(actual));
    }

    [Fact(DisplayName = "HiFi DOC window detection matches v0.4.0 boundaries")]
    public void HiFiDocWindowDetectionMatchesV040Boundaries()
    {
        var zeros = new float[1024];
        float[] broad = CreateScaledDeterministicFloatInput(1024, 0x12345678, 4.0f);
        var mixed = new float[1024];
        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (float)Math.Sin(i * 2.0 * Math.PI * 1000.0 / 192_000.0) * 0.02f;
        }

        CreateScaledDeterministicFloatInput(512, 0x89ABCDEF, 4.0f)
            .CopyTo(mixed, 256);

        Assert.Empty(HiFiDropoutCompensator.Detect(zeros));
        Assert.Equal(
            [new HiFiSampleRange(0, 1024)],
            HiFiDropoutCompensator.Detect(broad));
        Assert.Equal(
            [new HiFiSampleRange(256, 896)],
            HiFiDropoutCompensator.Detect(mixed));
    }

    [Fact(DisplayName = "HiFi DOC cross-channel ranges match v0.4.0")]
    public void HiFiDocCrossChannelRangesMatchV040()
    {
        List<HiFiDropoutAction> actual = HiFiDropoutCompensator.CheckOtherChannel(
            [new HiFiSampleRange(100, 500), new HiFiSampleRange(700, 900)],
            [
                new HiFiSampleRange(50, 150),
                new HiFiSampleRange(250, 350),
                new HiFiSampleRange(450, 750),
                new HiFiSampleRange(800, 850)
            ]);

        HiFiDropoutAction[] expected =
        [
            new(null, new HiFiSampleRange(100, 150)),
            new(new HiFiSampleRange(150, 250), null),
            new(null, new HiFiSampleRange(250, 350)),
            new(new HiFiSampleRange(350, 450), null),
            new(null, new HiFiSampleRange(450, 500)),
            new(null, new HiFiSampleRange(700, 750)),
            new(new HiFiSampleRange(750, 800), null),
            new(null, new HiFiSampleRange(800, 850)),
            new(new HiFiSampleRange(850, 900), null)
        ];
        Assert.Equal(expected, actual);
    }

    [Theory(DisplayName = "HiFi DOC fill and mute fades match v0.4.0 float bits")]
    [InlineData(256, 768, false, "E2AF4B5D18F2068A7D159A82AC2D69CCCB6B41C73C3089D419A2B4EB212063F8")]
    [InlineData(256, 768, true, "AE1B6E97DC6BA3D807110B4EA178F323E1089738C68F3D1507258973665F2266")]
    [InlineData(0, 192, false, "A42C5AD0724A2D9FD909BC1C95C439F93A4656D4ED5EEE2B80726FFDB47B5028")]
    [InlineData(832, 1024, true, "B235D23172065F5D7DAD0B87A40356583B0612BFBFF6E74489F7CD4EA37B7C91")]
    public void HiFiDocFillAndMuteFadesMatchV040FloatBits(
        int start,
        int end,
        bool mute,
        string expectedHash)
    {
        float[] outer = CreateDeterministicFloatInput(1024);
        float[] inner = CreateDeterministicFloatInput(1024, 0x89ABCDEF);

        HiFiDropoutCompensator.Fill(start, end, outer, inner, mute);

        Assert.Equal(expectedHash, BinarySha256(outer));
    }

    [Theory(DisplayName = "HiFi DOC stereo and mono processing matches v0.4.0 float bits")]
    [InlineData("s", "full", "FDDB655A235FFC66E67AB10342D2F443EEAAF327C530D9D9D0B556253E612454", "94EC7A02B6CD457123453B0EB65F9CE9666DCEA501376517DAF81A8698B657BC")]
    [InlineData("s", "mute", "D376429A282712B9833FA9A5EF764BA6D2CC64D9E322C7ACB7F80BD790CF705C", "120CA997BC15F26CDD4A0DA052E6F8D7CF735A537741A891830037578B1F4DD5")]
    [InlineData("d", "full", "D376429A282712B9833FA9A5EF764BA6D2CC64D9E322C7ACB7F80BD790CF705C", "120CA997BC15F26CDD4A0DA052E6F8D7CF735A537741A891830037578B1F4DD5")]
    [InlineData("l", "full", "D376429A282712B9833FA9A5EF764BA6D2CC64D9E322C7ACB7F80BD790CF705C", "090D7754D6AFEC7357C2FB491A8F620A79A7A12BA136D9642C0013E50EB5F3A8")]
    [InlineData("r", "full", "1751BFBABCB4B76E9885062C5225F0AE1C1E47DC3C4D767F5DD0DD3D4D54EEA8", "120CA997BC15F26CDD4A0DA052E6F8D7CF735A537741A891830037578B1F4DD5")]
    public void HiFiDocStereoAndMonoProcessingMatchesV040FloatBits(
        string decodeMode,
        string compensationMode,
        string expectedLeftHash,
        string expectedRightHash)
    {
        float[] left = CreateScaledDeterministicFloatInput(4096, 0x12345678, 0.02f);
        float[] right = CreateScaledDeterministicFloatInput(4096, 0x89ABCDEF, 0.02f);
        CreateScaledDeterministicFloatInput(768, 0x31415926, 4.0f).CopyTo(left, 1024);
        CreateScaledDeterministicFloatInput(896, 0x27182818, 4.0f).CopyTo(right, 1408);

        HiFiDropoutCompensator.Compensate(left, right, decodeMode, compensationMode);

        Assert.Equal(expectedLeftHash, BinarySha256(left));
        Assert.Equal(expectedRightHash, BinarySha256(right));
    }

    [Fact(DisplayName = "HiFi head-switch Chebyshev-II filter matches v0.4.0 float bits")]
    public void HiFiHeadSwitchChebyshevTypeIIFilterMatchesV040FloatBits()
    {
        SosSection[] sections = IirFilterDesign.ChebyshevTypeIIHighPassSos(
            22,
            200.0,
            28_000.0,
            192_000.0);
        float[] flattened = sections
            .SelectMany(section => new[]
            {
                (float)section.B0,
                (float)section.B1,
                (float)section.B2,
                (float)section.A0,
                (float)section.A1,
                (float)section.A2
            })
            .ToArray();

        Assert.Equal(11, sections.Length);
        Assert.Equal(
            "5A31F030E244ADAA18B4416142B0AC5DF66537F6A9B38550F69DFDA850F3D868",
            BinarySha256(flattened));
    }

    [Fact(DisplayName = "HiFi head-switch peak detection matches v0.4.0 float bits")]
    public void HiFiHeadSwitchPeakDetectionMatchesV040FloatBits()
    {
        var processor = new HiFiHeadSwitchProcessor(192_000, 59.94);
        float[] audio = CreateHeadSwitchAudio();

        HiFiHeadSwitchDetection detection = processor.Detect(audio);
        List<HiFiSampleRange> boundaries = processor.CalculateBoundaries(detection.Peaks);
        double[] flattenedPeaks = detection.Peaks
            .SelectMany(peak => new[]
            {
                (double)peak.Center,
                peak.Start,
                peak.End,
                peak.Prominence
            })
            .ToArray();

        Assert.Equal(
            "5D981DACDD3D6B95FFBC1571BDF6FEE4C28F2D92608A7669981370AFFA6C2493",
            BinarySha256(audio));
        Assert.Equal(
            "4A62B4337CE43F57B712985AD198E37F479E4CF53C7DA2B5602C5082F20932CF",
            BinarySha256(detection.Filtered));
        Assert.Equal(
            "A1E75D095FA747C85017EFCC684F9ABB7CCFB7B362270CA93252B9064B9F13B3",
            BinarySha256(detection.Absolute));
        Assert.Equal("00000000", BitConverter.SingleToUInt32Bits(detection.Mean).ToString("X8"));
        Assert.Equal("3C50E90D", BitConverter.SingleToUInt32Bits(detection.StandardDeviation).ToString("X8"));
        Assert.Equal(16, detection.Peaks.Count);
        Assert.Equal(
            "B769A8412BBFB084480E01D1278680220F6D52D4AF89B42081129BD45B156969",
            BinarySha256(flattenedPeaks));
        Assert.Equal(
            [
                new HiFiSampleRange(2813, 2826),
                new HiFiSampleRange(5883, 5885),
                new HiFiSampleRange(9202, 9208),
                new HiFiSampleRange(9217, 9232),
                new HiFiSampleRange(12392, 12394),
                new HiFiSampleRange(15606, 15614),
                new HiFiSampleRange(15621, 15637),
                new HiFiSampleRange(18541, 18544),
                new HiFiSampleRange(22039, 22047)
            ],
            boundaries);
    }

    [Fact(DisplayName = "HiFi head-switch NumPy AVX2 peak priority order matches v0.4.0")]
    public void HiFiHeadSwitchPeakPriorityOrderMatchesV040()
    {
        var processor = new HiFiHeadSwitchProcessor(192_000, 59.94);
        HiFiHeadSwitchDetection detection = processor.Detect(CreateHeadSwitchAudio());
        List<int> maxima = SciPyPeakFinder.FindLocalMaxima(detection.Absolute);
        double[] priorities = maxima.Select(index => (double)detection.Absolute[index]).ToArray();
        long[] order = NumpyAvx2ArgSort.SortIndices(priorities)
            .Select(index => (long)index)
            .ToArray();

        Assert.Equal(9_928, maxima.Count);
        Assert.Equal(
            "85AC89536CCD1C857D1260C0EE4309275E73D21D2D4B24632754B2FEEC949F6D",
            BinarySha256(order));
    }

    [Fact(DisplayName = "NumPy AVX2 256-value argsort network matches v0.4.0")]
    public void NumpyAvx2ArgSortNetworkMatchesV040()
    {
        uint state = 0x12345678;
        var values = new double[256];
        for (int i = 0; i < values.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            values[i] = (state >> 16) % 13;
        }

        long[] order = NumpyAvx2ArgSort.SortIndices(values)
            .Select(index => (long)index)
            .ToArray();

        Assert.Equal(
            "74199A7D8CF44E7AF89A32C88CAE3BA44022F6F99D20F702FDE2DE51EDAE7D28",
            BinarySha256(order));
    }

    [Fact(DisplayName = "HiFi head-switch interpolation matches v0.4.0 float bits")]
    public void HiFiHeadSwitchInterpolationMatchesV040FloatBits()
    {
        var processor = new HiFiHeadSwitchProcessor(192_000, 59.94);
        float[] audio = CreateHeadSwitchAudio();
        HiFiHeadSwitchDetection detection = processor.Detect(audio);
        List<HiFiSampleRange> boundaries = processor.CalculateBoundaries(detection.Peaks);

        float[] interpolated = HiFiHeadSwitchProcessor.InterpolateBoundaries(audio, boundaries);
        float[] removed = processor.RemoveNoise(audio);
        float[] simple = Enumerable.Range(0, 64).Select(index => index / 10.0f).ToArray();
        float[] simpleInterpolated = HiFiHeadSwitchProcessor.InterpolateBoundaries(
            simple,
            [
                new HiFiSampleRange(8, 13),
                new HiFiSampleRange(27, 35),
                new HiFiSampleRange(-2, 3),
                new HiFiSampleRange(60, 67)
            ]);

        Assert.Equal(
            "C2B6BE3504B284C13D8B81AA056D3FB4614221F0FE1D9C404BEA0030F863DDA7",
            BinarySha256(interpolated));
        Assert.Equal(BinarySha256(interpolated), BinarySha256(removed));
        Assert.Equal(
            "AF6123FD82AD552CF6BA8DF2C56C30131CB911D181B9A585BE5D364E15FFCF78",
            BinarySha256(simpleInterpolated));
    }

    [Theory(DisplayName = "HiFi mono block channel routing matches v0.4.0")]
    [InlineData("l", true, false)]
    [InlineData("r", false, true)]
    public void HiFiMonoBlockChannelRoutingMatchesV040(
        string audioMode,
        bool expectsLeft,
        bool expectsRight)
    {
        HiFiDecodeOptions options = DefaultOptions() with
        {
            AudioMode = audioMode,
            ResamplerQuality = "high"
        };
        using var decoder = new HiFiBlockDecoder(options);

        HiFiDemodulatedBlock block = decoder.Decode(CreateDeterministicFloatInput(524_288));

        Assert.Equal(expectsLeft, block.Left is not null);
        Assert.Equal(expectsRight, block.Right is not null);
        Assert.Equal(expectsLeft ? "BE838838" : "00000000", BitConverter.SingleToUInt32Bits(block.LeftDc).ToString("X8"));
        Assert.Equal(expectsRight ? "BE09159B" : "00000000", BitConverter.SingleToUInt32Bits(block.RightDc).ToString("X8"));
    }

    private static HiFiDecodeOptions DefaultOptions()
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.HiFi, ["-", "out.flac"]);
        return HiFiDecodeOptions.FromCommand(command);
    }

    private static void AssertRatio(HiFiRateRatio actual, string numerator, string denominator)
    {
        Assert.Equal(BigInteger.Parse(numerator, CultureInfo.InvariantCulture), actual.Numerator);
        Assert.Equal(BigInteger.Parse(denominator, CultureInfo.InvariantCulture), actual.Denominator);
    }

    private static string BinarySha256(ReadOnlySpan<float> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));

    private static string BinarySha256(ReadOnlySpan<double> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));

    private static string BinarySha256(ReadOnlySpan<long> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));

    private static string BinarySha256(ReadOnlySpan<Complex32> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));

    private static float[] CreateDeterministicFloatInput(int length)
        => CreateDeterministicFloatInput(length, 0x12345678);

    private static float[] CreateDeterministicFloatInput(int length, uint seed)
    {
        uint state = seed;
        var input = new float[length];
        for (int i = 0; i < input.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            input[i] = ((int)(state >> 8) - 0x800000) / (float)0x800000;
        }

        return input;
    }

    private static float[] CreateScaledDeterministicFloatInput(
        int length,
        uint seed,
        float scale)
    {
        float[] input = CreateDeterministicFloatInput(length, seed);
        for (int i = 0; i < input.Length; i++)
        {
            input[i] *= scale;
        }

        return input;
    }

    private static float[] CreateHeadSwitchAudio()
    {
        var audio = new float[26_000];
        float[] shape = [0.08f, 0.3f, 0.72f, 1.0f, 0.64f, 0.25f, 0.06f];
        (int Center, double Amplitude)[] pulses =
        [
            (2800, 1.8),
            (9205, -2.4),
            (15610, 3.2),
            (22025, -1.5)
        ];
        foreach ((int center, double amplitude) in pulses)
        {
            float amplitudeFloat = (float)amplitude;
            for (int i = 0; i < shape.Length; i++)
            {
                audio[center - 3 + i] += shape[i] * amplitudeFloat;
            }

            audio[center + 18] += (float)(amplitude * -0.42);
            audio[center + 23] += (float)(amplitude * 0.31);
        }

        return audio;
    }

    private static string Utf8LfSha256(string value)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
