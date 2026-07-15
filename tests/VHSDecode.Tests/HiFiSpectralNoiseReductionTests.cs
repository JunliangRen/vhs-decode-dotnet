using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class HiFiSpectralNoiseReductionTests
{
    [Fact(DisplayName = "HiFi spectral NR stages match v0.4.0")]
    public void HiFiSpectralNoiseReductionStagesMatchV040()
    {
        var noiseReduction = new HiFiSpectralNoiseReduction(48_000, 0.5);
        float[] input = CreateInput(noiseReduction.ChunkSize, 0x12345678u);
        var chunk = new float[
            (2 * noiseReduction.ChunkSize)
            + input.Length
            + noiseReduction.EndPadding];
        input.CopyTo(chunk, 2 * noiseReduction.ChunkSize);

        Assert.Equal(
            "B5D989664CBBF16CF3EE56F373B57A65857AF399886B484425BFAAC55614C835",
            BinarySha256(chunk));
        Assert.Equal(
            "1F0E971AF59F6C758249E2FEFAC7479A16984255EAD2C5EC82715B2B613D311B",
            BinarySha256(noiseReduction.Window));
        Assert.Equal("3F85BAA8A10CB075", Bits(noiseReduction.SmoothingB));
        Assert.Equal(11, noiseReduction.SmoothingFilter.Rows);
        Assert.Equal(19, noiseReduction.SmoothingFilter.Columns);
        Assert.Equal(
            "42CCD9FB6093335CEFD874C5A3F3DBD2CB280CC4466BE8A19DF80A9EA1E5B695",
            BinarySha256(noiseReduction.SmoothingFilter.Values));

        float[] firstSignalFrame = HiFiSpectralNoiseReduction.CreateWindowedFrame(
            chunk,
            noiseReduction.Window,
            186);
        Assert.Equal(
            "BA083570F9A6F208AA5D9CF953DC8E879EAD5CAE5A28BD82E3F0A40C81753B36",
            BinarySha256(firstSignalFrame));

        HiFiSpectralMatrix<Complex32> spectrum =
            HiFiSpectralNoiseReduction.CreateStft(chunk, noiseReduction.Window);
        Assert.Equal(513, spectrum.Rows);
        Assert.Equal(293, spectrum.Columns);
        Assert.Equal("3A146DEE/00000000", Bits(spectrum[0, 186]));
        Assert.Equal("39E8AD7A/39B4F15C", Bits(spectrum[1, 186]));
        Assert.Equal("390BFE32/3A0C0E60", Bits(spectrum[2, 186]));
        Assert.Equal(
            "F1103F7AE5022B12E4BB100AF3BEA7B49A83BDAA1E8E1674B239D61F2A980CE1",
            BinarySha256(spectrum.Values));

        HiFiSpectralMatrix<float> absolute =
            HiFiSpectralNoiseReduction.Absolute(spectrum);
        Assert.Equal(
            "6A1988552494BC7BB8545B2FC567BC11437EBC0CA6EF102B3DB2333ECC82921D",
            BinarySha256(absolute.Values));

        HiFiSpectralMatrix<double> smooth =
            HiFiSpectralNoiseReduction.SmoothTime(
                absolute,
                noiseReduction.SmoothingB);
        Assert.Equal(
            "A5F84AFB1C52FEEF104D5AC21C739C86153DB671F7FEB36909EBFD7ADB4102C5",
            BinarySha256(smooth.Values));

        HiFiSpectralMatrix<double> rawMask =
            HiFiSpectralNoiseReduction.CreateMask(absolute, smooth);
        Assert.Equal(
            "CA3B0060663CEED972A8650A4611FDE094CEFF5BF0BF5594248E5EC964072E3F",
            BinarySha256(rawMask.Values));

        HiFiSpectralMatrix<double> smoothedMask =
            HiFiSpectralNoiseReduction.ConvolveSame(
                rawMask,
                noiseReduction.SmoothingFilter);

        HiFiSpectralNoiseReduction.ApplyMask(spectrum, smoothedMask, 0.5);
        Assert.Equal(
            "3D7D14956F3714CAAF92A87F0FB960D4995AE39D77175971D01F2CC111DE5BDB",
            BinarySha256(spectrum.Values));

        float[] denoised = HiFiSpectralNoiseReduction.InverseStft(
            spectrum,
            noiseReduction.Window);
        Assert.Equal(74_752, denoised.Length);
        Assert.Equal(
            "8A91830CDD6328BD25A2A46FC7425E745236855E524D723C2059831B08D5B1A2",
            BinarySha256(denoised));

        int outputStart = denoised.Length - input.Length - noiseReduction.EndPadding;
        Assert.Equal(
            "69D46E52685799F7C71D81FFE03E7C6EC19C286E95D652231AF5EC63550CA3C2",
            BinarySha256(denoised.AsSpan(outputStart, input.Length)));
    }

    [Theory(DisplayName = "HiFi spectral NR streaming matches v0.4.0")]
    [InlineData("48k-025")]
    [InlineData("48k-050")]
    [InlineData("48k-100")]
    [InlineData("44k-050")]
    public void HiFiSpectralNoiseReductionStreamingMatchesV040(
        string scenarioName)
    {
        SpectralScenario scenario = GetScenario(scenarioName);
        var noiseReduction = new HiFiSpectralNoiseReduction(
            scenario.SampleRateHz,
            scenario.Amount);

        for (int blockNumber = 0;
            blockNumber < scenario.OutputSha256.Length;
            blockNumber++)
        {
            float[] input = CreateInput(
                noiseReduction.ChunkSize,
                0x12345678u + (uint)blockNumber);
            var output = new float[input.Length];

            noiseReduction.Process(input, output);

            Assert.Equal(
                scenario.OutputSha256[blockNumber],
                BinarySha256(output));
        }
    }

    private static float[] CreateInput(int length, uint seed)
    {
        uint state = seed;
        var values = new float[length];
        for (int i = 0; i < values.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            float raw = ((int)(state >> 8) - 0x800000) / (float)0x800000;
            float scaled = raw * 0.35f;
            values[i] = scaled + 0.04f;
        }

        return values;
    }

    private static string BinarySha256<T>(ReadOnlySpan<T> values)
        where T : struct
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));

    private static string Bits(double value)
        => BitConverter.DoubleToUInt64Bits(value).ToString("X16");

    private static string Bits(Complex32 value)
        => $"{BitConverter.SingleToUInt32Bits(value.Real):X8}/"
            + $"{BitConverter.SingleToUInt32Bits(value.Imaginary):X8}";

    private static SpectralScenario GetScenario(string name)
        => name switch
        {
            "48k-025" => new SpectralScenario(
                48_000,
                0.25,
                [
                    "EA311BC8C85FD11412FBE3E68DE708B90E4BC021F0246A45C00CD5C5B9C62B9D",
                    "008F34026D77FE82E4BD5B7F3242AC5E939CAFA39A89852085429BEC47806EE7",
                    "00CC6E8D289E8301339CB73CAB8FB6DF929181C774C8A6616B66DA9E3162090D"
                ]),
            "48k-050" => new SpectralScenario(
                48_000,
                0.5,
                [
                    "69D46E52685799F7C71D81FFE03E7C6EC19C286E95D652231AF5EC63550CA3C2",
                    "644CBE6A5DBEB97883E706288E3D60C45DA43697F1742D9AAA16FB9DE81D1E68",
                    "28E90B7448CF7A65C215C72510E7FFA4AF6ACA698EDDC01724A022A1A322C47A"
                ]),
            "48k-100" => new SpectralScenario(
                48_000,
                1.0,
                [
                    "8F1DDFDC63CB82DAB8830BBA5A513943DD1B364495E51E595A9F2241C475887B",
                    "A7643CAFC61B44B6E1F1FF9FFAAE5972E297BCF05983CC74EE2FCA40A6AC858C",
                    "5BA41D2D214C8DD1440E6CDDFF3449DC58AADF7653E416B1C591698B17810316"
                ]),
            "44k-050" => new SpectralScenario(
                44_100,
                0.5,
                [
                    "C093ACF240ACFAA286E5955FDE65FA7C551698AB3BC8418B13F42C5922B480CA",
                    "DBE60A2F4A2E3F1E70A9DB56F6EF73C53F71D4DEE36747BF9B74E81F078AA897",
                    "3EDD435AA4468A19DAD9CA36778B5FB4F6EFA1711ECDCD458A0235F2578C634B"
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
        };

    private sealed record SpectralScenario(
        int SampleRateHz,
        double Amount,
        string[] OutputSha256);
}
