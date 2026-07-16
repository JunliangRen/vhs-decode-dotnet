using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Formats;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscVideoFilterCompatibilityTests
{
    [Fact(DisplayName = "LD half powers retain NumPy complex-zero signs")]
    public void HalfPowersRetainComplexZeroSigns()
    {
        Complex result = NumpyComplexMath.Pow(new Complex(-0.0, -0.0), 0.5);

        Assert.Equal(0x0000000000000000UL, BitConverter.DoubleToUInt64Bits(result.Real));
        Assert.Equal(0x8000000000000000UL, BitConverter.DoubleToUInt64Bits(result.Imaginary));
    }

    [Theory(DisplayName = "LD MTF powers match Release 4.0")]
    [InlineData(0.5, "A06D09C5250A5CE776B0091F6ECFEA9EBEB767881750961BF44A26FB474447EB")]
    [InlineData(2.0, "1DD420D11C574229619838390A7E0A449D6F4C00D7F795DD44CEDE47CBEAB7B8")]
    [InlineData(-2.0, "7CEC90D6538B741F95AAEBF871F2FB7F4C8937916BDEC3A271695988842FA4E4")]
    [InlineData(2.1, "505D8A03E891A1D409341FF72E525B2C944327C09B32F57D1E2E230BFAC73438")]
    public void MtfPowersMatchReleaseFour(double level, string expectedHash)
    {
        FormatParameterSet parameters = FormatCatalog.Default.GetLaserDiscParameters("PAL", lowBand: false);
        Complex[] response = DecodeFilterSetBuilder.BuildLaserDiscMtf(
            parameters,
            new DecodeFilterOptions(LdMtfLevel: level),
            targetMtf: 1.0,
            sampleRateHz: 40_000_000.0,
            blockLength: 32_768);

        Assert.Equal(expectedHash, ComplexBitsSha256(response));
    }

    [Fact(DisplayName = "LD custom deemphasis filters match Release 4.0")]
    public void CustomDeemphasisFiltersMatchReleaseFour()
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc,
        [
            "--PAL",
            "--noEFM",
            "--disable_analog_audio",
            "--deemp_low",
            "0.25",
            "--deemp_high",
            "1.5",
            "--deemp_strength",
            "0.75",
            "input.s16",
            "outbase"
        ]);

        using DecodeSession session = DecodeSessionFactory.Create(command, blockLength: 32_768);
        double[] timeConstants = session.Parameters.RfParams
            .GetProperty("video_deemp")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        Complex[] deemphasis = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.EmphasisIir(
                timeConstants[0],
                timeConstants[1],
                session.DecodeSampleRateHz),
            32_768);
        Complex[] poweredDeemphasis = deemphasis
            .Select(value => NumpyComplexMath.Pow(value, 0.75))
            .ToArray();
        Complex[] preGroupDelayVideo = session.Filters.VideoLowPass
            .Zip(poweredDeemphasis, NumpyVectorComplexMultiply)
            .ToArray();

        Assert.Equal(1.0 / 1_500_000.0, timeConstants[0]);
        Assert.Equal(1.0 / 250_000.0, timeConstants[1]);
        Assert.Equal(
            "1F429F9C96D0519F6E166F7BDD9CB273265E774FF097F775248DD58FD773722C",
            ComplexBitsSha256(deemphasis));
        Assert.Equal(
            "0914E9CAB94F9C9CEEA3FC628FBE8B27E6881F393C45636B8664BE6C52B51C84",
            ComplexBitsSha256(poweredDeemphasis));
        Assert.Equal(
            "117E917EFACCF2211FE4CE705D02D472530B8752CEF95FC462E7AC3F7D100C69",
            ComplexBitsSha256(session.Filters.VideoLowPass));
        Assert.Equal(
            "84982593560F77DABD49FE5455A1ECFDBEACA8B11BC23D4DEAA6D27A32DD2FA8",
            ComplexBitsSha256(preGroupDelayVideo));
        Assert.Equal(
            "FB5512BB4674D49E1D0932AAEFB13E09D277293519F361CEE1085803D6ECBC0C",
            ComplexBitsSha256(session.Filters.VideoLowPass05));

        var input = new double[32_768];
        for (ulong i = 0; i < (ulong)input.Length; i++)
        {
            input[(int)i] = (unchecked((i * 1_103_515_245UL) + 12_345UL) & 0xffffUL) - 32_768.0;
        }

        var references = new RfVideoReferenceFilterSet(
            session.Filters.LdVideoBurst,
            session.Filters.LdVideoBurstOffset,
            session.Filters.LdVideoPilot,
            session.FilterOptions.LdClipDemodForVideo);
        RfDemodulatedBlock block = new RfDemodulator(session.DecodeSampleRateHz).Demodulate(
            input,
            session.Filters.RfVideo,
            session.Filters.RfHighPass,
            session.Filters.RfMtf,
            session.Filters.Video,
            session.Filters.VideoLowPass05,
            session.Filters.VideoLowPass05Offset,
            session.FilterOptions.LdPalV4300DNotch,
            referenceFilters: references,
            fmDemodulatorMode: session.FilterOptions.FmDemodulatorMode);

        Assert.Equal(
            "5081BDE4C57623E1254A6FFB27E306215691829A1667BCF04FEC710A12B01C34",
            FloatBitsSha256(block.Video));
        Assert.Equal(
            "E4AC0D4BB8769F27761AFEF067E3F935919E53707AA0C06D08988FD24E1C8CD2",
            FloatBitsSha256(block.DemodRaw));
        Assert.Equal(
            "B24DE7F7A6DB3769580318592F21A421DCF5811FDC64AEB81382333EB52DED33",
            FloatBitsSha256(block.VideoLowPass));
        Assert.Equal(
            "7692477D87E9238C11FA71DF4E3546EBFE0418EA265DF14BC37AA67A3E2AB617",
            FloatBitsSha256(block.VideoBurst!));
        Assert.Equal(
            "8AA6600E865D36EECE881C6C8FB8D7DB01778D66C674A3C9B26F3A159E3F576D",
            FloatBitsSha256(block.VideoPilot!));
    }

    private static string ComplexBitsSha256(ReadOnlySpan<Complex> values)
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

    private static Complex NumpyVectorComplexMultiply(Complex left, Complex right)
        => new(
            Math.FusedMultiplyAdd(
                left.Real,
                right.Real,
                -(left.Imaginary * right.Imaginary)),
            Math.FusedMultiplyAdd(
                left.Real,
                right.Imaginary,
                left.Imaginary * right.Real));

    private static string FloatBitsSha256(ReadOnlySpan<double> values)
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
}
