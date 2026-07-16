using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class HiFiPostProcessorTests
{
    [Fact(DisplayName = "HiFi post-filter coefficients match v0.4.0")]
    public void HiFiPostFilterCoefficientsMatchV040()
    {
        Assert.Equal("3FEFFDE59DB8F54D", Bits(new HiFiDcBlocker(48_000, 1.0).R));
        Assert.Equal("3FEFFDB602B505E8", Bits(new HiFiDcBlocker(44_100, 1.0).R));

        AssertFilter(
            HiFiShelfFilterDesign.High(240e-6, 24e-6, 48_000),
            "3FE74885E37C973F",
            "BFE558A3A442F1BF",
            "BFD942530F7F11FB");
        AssertFilter(
            HiFiShelfFilterDesign.Low(56e-6, 20e-6, 48_000),
            "3FDD4F56676D87DF",
            "BFC2782167BFC257",
            "BFE5F65D26392CA6");
        AssertFilter(
            HiFiShelfFilterDesign.Low(240e-6, 56e-6, 48_000),
            "3FD0F970B5498527",
            "BFC74CC1E53FFA8D",
            "BFED56781EAB3C10");
        AssertFilter(
            HiFiShelfFilterDesign.High(240e-6, 24e-6, 44_100),
            "3FE6C27F3624CBF6",
            "BFE4B4D45E9EC065",
            "BFD6EEA7298718B5");
        AssertFilter(
            HiFiShelfFilterDesign.Low(56e-6, 20e-6, 44_100),
            "3FDDC8D3DCB08D3E",
            "BFC0772C234F72C5",
            "BFE539611A7B9612");
        AssertFilter(
            HiFiShelfFilterDesign.Low(240e-6, 56e-6, 44_100),
            "3FD125905C88823A",
            "BFC6BECB5BD97E67",
            "BFED1CEAA8B21E7C");
        AssertFilter(
            HiFiShelfFilterDesign.High(75e-6, 27e-6, 48_000),
            "3FEA4C66A0C8B1E3",
            "BFE3E25A15AA7A03",
            "BFDC5D816CE657CD");
        AssertFilter(
            HiFiShelfFilterDesign.Low(75e-6, 27e-6, 48_000),
            "3FDC08FDC08FDC0A",
            "BFC8D9C98D9C98DC",
            "BFE831F3831F3831");
        AssertFilter(
            HiFiShelfFilterDesign.Low(75e-6, 19e-6, 48_000),
            "3FD60A7D60A7D60C",
            "BFB9B9919B9919BC",
            "BFE831F3831F3831");

        var vhs = new HiFiExpander(
            48_000,
            30.0,
            2.0,
            HiFiConstants.EnvelopeDetectionPeak,
            6.5e-3,
            0.0,
            70e-3,
            240e-6,
            24e-6);
        Assert.Equal("3FEFE5C91E8CCA66", Bits(vhs.AttackCoefficient));
        Assert.Equal("3FEFFD8FF0C3EE0C", Bits(vhs.ReleaseCoefficient));
        Assert.Equal(0, vhs.HoldSamples);

        var eightMillimeter = new HiFiExpander(
            48_000,
            6.0,
            2.0,
            HiFiConstants.EnvelopeDetectionPeak,
            3e-3,
            15e-3,
            40e-3,
            75e-6,
            27e-6);
        Assert.Equal("3FEFC74EE53F0C60", Bits(eightMillimeter.AttackCoefficient));
        Assert.Equal("3FEFFBBC0489D939", Bits(eightMillimeter.ReleaseCoefficient));
        Assert.Equal(720, eightMillimeter.HoldSamples);
    }

    [Theory(DisplayName = "HiFi stateful post-processing matches v0.4.0")]
    [InlineData("vhs-default-48k")]
    [InlineData("vhs-rms-44k")]
    [InlineData("vhs-deemphasis-48k")]
    [InlineData("vhs-disabled-48k")]
    [InlineData("8mm-default-48k")]
    public void HiFiStatefulPostProcessingMatchesV040(string scenarioName)
    {
        PostProcessorScenario scenario = GetScenario(scenarioName);
        var processor = new HiFiAudioPostProcessor(CreateOptions(scenario));
        float cumulativeLeftPeak = 0.0f;
        float cumulativeRightPeak = 0.0f;

        for (int blockNumber = 0; blockNumber < scenario.Blocks.Length; blockNumber++)
        {
            ExpectedBlock expected = scenario.Blocks[blockNumber];
            var decoded = new HiFiDecodedBlock(
                CreateInput(4096, 0x12345678u + (uint)blockNumber, 0.42f, 0.075f),
                CreateInput(4096, 0x87654321u + (uint)blockNumber, 0.31f, -0.055f),
                0.0f,
                0.0f);

            HiFiPostProcessedBlock actual = processor.Process(decoded, blockNumber);

            Assert.Equal(expected.LeftSha256, BinarySha256(actual.Left));
            Assert.Equal(expected.RightSha256, BinarySha256(actual.Right));
            Assert.Equal(expected.StereoSha256, BinarySha256(actual.Stereo));
            Assert.Equal(expected.LeftPeakBits, Bits(actual.LeftPeak));
            Assert.Equal(expected.RightPeakBits, Bits(actual.RightPeak));
            Assert.Equal(expected.DcX1Bits, Bits(processor.LeftChannel.DcBlocker.X1));
            Assert.Equal(expected.DcY1Bits, Bits(processor.LeftChannel.DcBlocker.Y1));
            Assert.Equal(expected.EnvelopeBits, Bits(processor.LeftChannel.Expander.Envelope));
            Assert.Equal(expected.HoldState, processor.LeftChannel.Expander.HoldState);

            cumulativeLeftPeak = MathF.Max(cumulativeLeftPeak, actual.LeftPeak);
            cumulativeRightPeak = MathF.Max(cumulativeRightPeak, actual.RightPeak);
            Assert.Equal(Bits(cumulativeLeftPeak), Bits(processor.PeakLeft));
            Assert.Equal(Bits(cumulativeRightPeak), Bits(processor.PeakRight));
            Assert.Equal(blockNumber + 1, processor.NextBlockNumber);
        }
    }

    [Fact(DisplayName = "HiFi spectral NR post-processing matches v0.4.0")]
    public void HiFiSpectralNoiseReductionPostProcessingMatchesV040()
    {
        HiFiDecodeOptions options = CreateOptions(GetScenario("vhs-disabled-48k")) with
        {
            SpectralNoiseReductionAmount = 0.5
        };
        var processor = new HiFiAudioPostProcessor(options);
        int blockLength = options.AudioRateHz / HiFiConstants.BlocksPerSecond;
        PostProcessorExpected[] expected =
        [
            new PostProcessorExpected(
                "D24BBCAB55730F4E4FF3691E2809AA103CA2EF3DB42B85DE49C89442A0D77F84",
                "CEE4DDCF45103D3A868A1B4D1D2749D9E1629C6EDE7443148C1C22CC75D59BA8",
                "22D83662498190EE805DBC55774796348E76A4ED003299F450DF730C615D448F",
                "3E8D29D9",
                "3E4FF56C"),
            new PostProcessorExpected(
                "C68361A5820FC925F69B1E161B97DD612A5027BC76AA66EDB79FE1E3B9422E1F",
                "882C622EC13CAA01242C76FA376FBCCD370ED20D96B3606D37B86676722EFD44",
                "E8D41F083F46759D91392A8B069C161F83442FBF89455134B9A1E61932FAF665",
                "3E63B9F5",
                "3E27DDC6")
        ];

        for (int blockNumber = 0; blockNumber < expected.Length; blockNumber++)
        {
            var decoded = new HiFiDecodedBlock(
                CreateInput(
                    blockLength,
                    0x12345678u + (uint)blockNumber,
                    0.42f,
                    0.075f),
                CreateInput(
                    blockLength,
                    0x87654321u + (uint)blockNumber,
                    0.31f,
                    -0.055f),
                0.0f,
                0.0f);

            HiFiPostProcessedBlock actual = processor.Process(
                decoded,
                blockNumber);

            Assert.Equal(expected[blockNumber].LeftSha256, BinarySha256(actual.Left));
            Assert.Equal(expected[blockNumber].RightSha256, BinarySha256(actual.Right));
            Assert.Equal(expected[blockNumber].StereoSha256, BinarySha256(actual.Stereo));
            Assert.Equal(expected[blockNumber].LeftPeakBits, Bits(actual.LeftPeak));
            Assert.Equal(expected[blockNumber].RightPeakBits, Bits(actual.RightPeak));
        }
    }

    [Fact(DisplayName = "HiFi post-processor requires ordered blocks")]
    public void HiFiPostProcessorRequiresOrderedBlocks()
    {
        var processor = new HiFiAudioPostProcessor(
            CreateOptions(GetScenario("vhs-disabled-48k")));
        var decoded = new HiFiDecodedBlock(new float[4096], new float[4096], 0.0f, 0.0f);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => processor.Process(decoded, 1));

        Assert.Contains("Expected HiFi block 0", exception.Message, StringComparison.Ordinal);
    }

    private static HiFiDecodeOptions CreateOptions(PostProcessorScenario scenario)
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.HiFi, ["-", "out.flac"]);
        HiFiDecodeOptions options = HiFiDecodeOptions.FromCommand(command) with
        {
            AudioRateInteger = scenario.SampleRateHz,
            TapeFormat = scenario.TapeFormat,
            EnableDeemphasis = scenario.EnableDeemphasis,
            EnableExpander = scenario.EnableExpander,
            ExpanderEnvelopeDetection = scenario.EnvelopeDetection,
            SpectralNoiseReductionAmount = 0.0
        };

        return scenario.TapeFormat == "8mm"
            ? options with
            {
                ExpanderGain = HiFiConstants.Default8MmExpanderGain,
                ExpanderRatio = HiFiConstants.Default8MmExpanderRatio,
                ExpanderAttackTau = HiFiConstants.Default8MmExpanderAttackTau,
                ExpanderHoldTau = HiFiConstants.Default8MmExpanderHoldTau,
                ExpanderReleaseTau = HiFiConstants.Default8MmExpanderReleaseTau,
                ExpanderWeightingLowTau = HiFiConstants.Default8MmExpanderWeightingLowTau,
                ExpanderWeightingHighTau = HiFiConstants.Default8MmExpanderWeightingHighTau,
                DeemphasisLowTau = HiFiConstants.Default8MmDeemphasisLowTau,
                DeemphasisHighTau = HiFiConstants.Default8MmDeemphasisHighTau,
                NoiseReductionDeemphasisLowTau = HiFiConstants.Default8MmNoiseReductionDeemphasisLowTau,
                NoiseReductionDeemphasisHighTau = HiFiConstants.Default8MmNoiseReductionDeemphasisHighTau
            }
            : options;
    }

    private static float[] CreateInput(
        int length,
        uint seed,
        float scale,
        float offset)
    {
        uint state = seed;
        var values = new float[length];
        for (int i = 0; i < values.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            float raw = ((int)(state >> 8) - 0x800000) / (float)0x800000;
            float scaled = raw * scale;
            values[i] = scaled + offset;
        }

        return values;
    }

    private static void AssertFilter(
        HiFiFirstOrderCoefficients actual,
        string expectedB0,
        string expectedB1,
        string expectedA1)
    {
        Assert.Equal(expectedB0, Bits(actual.B0));
        Assert.Equal(expectedB1, Bits(actual.B1));
        Assert.Equal(expectedA1, Bits(actual.A1));
    }

    private static string BinarySha256(ReadOnlySpan<float> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));

    private static string Bits(double value)
        => BitConverter.DoubleToUInt64Bits(value).ToString("X16");

    private static string Bits(float value)
        => BitConverter.SingleToUInt32Bits(value).ToString("X8");

    private static PostProcessorScenario GetScenario(string name)
        => name switch
        {
            "vhs-default-48k" => new PostProcessorScenario(
                "vhs",
                48_000,
                true,
                true,
                HiFiConstants.EnvelopeDetectionPeak,
                [
                    new ExpectedBlock(
                        "233FB42AF7F72090D3F772BC7A3C7430AA5E4CB539D0039BCB44688D0BDED4A1",
                        "45388975986F03E508C97AA9603CF34425BF9C7997154A2BCB6246B8720C73E4",
                        "B23E3737199C18E1FC3D153A8995B9844B7C31A1C94348C5AC1F3F6C1FAEECB5",
                        "3F1CCF37",
                        "3EA648B9",
                        "3F91F4BD80000000",
                        "BFA8DBECEEFE856B",
                        "3FBA00156D58F39E",
                        0),
                    new ExpectedBlock(
                        "1B1D2F81A9704BEDAA72EA9E720032EA6BA647D6D6923C07CDB27774C9B3C4AD",
                        "5B75E7E1E16C141EBB357B2068BC02A207A5531E1B3035C4BAC83962F32D7FD4",
                        "55627A7E5181962710B1D979CF7B12F885E51080474F87863BF00F2FB6383415",
                        "3F04D2F8",
                        "3E9E78D7",
                        "3FDB5E8240000000",
                        "3FD6C4B41491DBD4",
                        "3FBA0890E044461F",
                        0)
                ]),
            "vhs-rms-44k" => new PostProcessorScenario(
                "vhs",
                44_100,
                true,
                true,
                HiFiConstants.EnvelopeDetectionRms,
                [
                    new ExpectedBlock(
                        "ED12B5A9408D6EF23237CBCAF902954F636AC0DD69ACA1916D350BE4FBC20883",
                        "35143BAC6BD0AD5231B7AB03E148FBF4459EA950716B4DB53A01C047CC7AD6F5",
                        "909A41DE28C4BABAC848BE356FE0E5005F844259F3EB777404277BBD42B77759",
                        "3F361E67",
                        "3EC1418D",
                        "3F91F4BD80000000",
                        "BFA9B1322FF46908",
                        "3F8A846EAC63FC3D",
                        0),
                    new ExpectedBlock(
                        "4063ED3EE21AD43A29A883A9EFEA210317F338E0605C3F40BA2E2CF9FD2E38EF",
                        "E8155DAD98A5232808642EA990F22D7A29D8C7F70769C0D8F61032C72A2A9D27",
                        "75FAD7F5BA87546B1BCA76C41F9D76D8E003B906481393631AB27BD49A9295B8",
                        "3F1704AB",
                        "3EB8A592",
                        "3FDB5E8240000000",
                        "3FD6B7C7308B1587",
                        "3F8AE2DC36F34D6E",
                        0)
                ]),
            "vhs-deemphasis-48k" => new PostProcessorScenario(
                "vhs",
                48_000,
                true,
                false,
                HiFiConstants.EnvelopeDetectionPeak,
                [
                    new ExpectedBlock(
                        "CF94769DB7C4B8ADB9F8B6DF886337B4B5BA5523104599FB4761F67F3A51BBD5",
                        "654AB78EED5C7FCDB6759F24C02293201BC09C5A7571D7514BD804D50F4AA817",
                        "92ADE4F194B9548B8F7F13349E8639AFFB220B6ABF8C08A0DABC92CC39B184BF",
                        "3E44D2DD",
                        "3E0C6D72",
                        "3F91F4BD80000000",
                        "BFA8DBECEEFE856B",
                        "0000000000000000",
                        0),
                    new ExpectedBlock(
                        "8E6FF1BD53AB681D85A8248F09147B7A79FC1769D5088BFAC6310E03E9C3B38B",
                        "3CB1DC397D519EE4A6E44C2985DA723AF9475A6D78D51182E218D847445AD54B",
                        "D62AB8A0C192462A46793D5A030BD9FDE2FB94C8AC043EF901AACFCA2BFDB870",
                        "3E27EAA1",
                        "3E05A16C",
                        "3FDB5E8240000000",
                        "3FD6C4B41491DBD4",
                        "0000000000000000",
                        0)
                ]),
            "vhs-disabled-48k" => new PostProcessorScenario(
                "vhs",
                48_000,
                false,
                false,
                HiFiConstants.EnvelopeDetectionPeak,
                [
                    new ExpectedBlock(
                        "EEBFDDA037453650D3F6F8DB0B9DB5E33EB113BB560141F22B5C401EEF9BD94E",
                        "36FAD22318A306AF4851F798B5C85DC646E66F6D707194798E821CEE0E40B937",
                        "BCD36BC3536FF98308A4860AE92F62EBEFA2D2B3540272F93768806BAE4BFCD6",
                        "3EDFC5B9",
                        "3EA59ACB",
                        "3F91F4BD80000000",
                        "BFA8DBECEEFE856B",
                        "0000000000000000",
                        0),
                    new ExpectedBlock(
                        "3A988EB83F6000932901C466812C9F0B9B40B61E38EB6D00AE3293837F8B4F84",
                        "EED2190BEDF13E52D8E0249EFA6A9EB3139BAD124B69F0BD56DAF4702B82E29C",
                        "B1EB4FBE205E471D75E0BBFC23E571C915C1AED7D666C7EAD6C0023318C5AEB4",
                        "3EDCB283",
                        "3EA20279",
                        "3FDB5E8240000000",
                        "3FD6C4B41491DBD4",
                        "0000000000000000",
                        0)
                ]),
            "8mm-default-48k" => new PostProcessorScenario(
                "8mm",
                48_000,
                true,
                true,
                HiFiConstants.EnvelopeDetectionPeak,
                [
                    new ExpectedBlock(
                        "B25C189A8A9F8C7D4061CF798724236A5BEB50519CA9ADDF36281ECA4D8F081C",
                        "F41F4602F2997D3A86A1DB1796228738007829F044ECFB921B8219FF53CF4C12",
                        "0D2AD80060D0CBEDB0392EDEA866F9915044E9AD608B269824BF74CF3462C8B8",
                        "3E2ED90A",
                        "3DC0A880",
                        "3F91F4BD80000000",
                        "BFA8DBECEEFE856B",
                        "3FC10B5DDCD5E801",
                        720),
                    new ExpectedBlock(
                        "AAC6D396F4C1E21BA15436F952DC02EB6E12514ED8A3ED5BB147FDA7A44B739E",
                        "A047782905B11608D4F0A9DDFA4B1C39C9C852A2AC71D7C547F549F3C03BAA3B",
                        "36931B4449D2870671064326EDEDE60F024F211B72E41F9FFD52193B1FFE8DF1",
                        "3E1D9484",
                        "3DC3C0D4",
                        "3FDB5E8240000000",
                        "3FD6C4B41491DBD4",
                        "3FD7E9AB13CDA929",
                        668)
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
        };

    private sealed record PostProcessorScenario(
        string TapeFormat,
        int SampleRateHz,
        bool EnableDeemphasis,
        bool EnableExpander,
        string EnvelopeDetection,
        ExpectedBlock[] Blocks);

    private sealed record ExpectedBlock(
        string LeftSha256,
        string RightSha256,
        string StereoSha256,
        string LeftPeakBits,
        string RightPeakBits,
        string DcX1Bits,
        string DcY1Bits,
        string EnvelopeBits,
        int HoldState);

    private sealed record PostProcessorExpected(
        string LeftSha256,
        string RightSha256,
        string StereoSha256,
        string LeftPeakBits,
        string RightPeakBits);
}
