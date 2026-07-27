using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ExactRfAnalyticCompatibilityTests
{
    private const int BlockLength = 32_768;

    [Fact(DisplayName = "Exact VHS profiles pin the full complex NumPy transform")]
    public void ExactVhsProfilesPinFullComplexNumpyTransform()
    {
        double[] input = BuildDeterministicRfBlock();
        RfDemodulatedBlock current = Decode(input, "current");
        RfDemodulatedBlock v040 = Decode(input, "v0.4.0");

        Assert.Equal(
            "478A227D20C7F9D960A672DBE76F02515062EA52568E072CD123C5E30A81E758",
            Hash(current.Analytic));
        Assert.Equal(
            "DC7B6A81577ACFA9B741A0D4BFB805F6E164597557F4DF6F4413C3C8E3232F87",
            Hash(current.DemodRaw));
        Assert.Equal(
            "3891828F76474EBCC7F1E00FDE5EC1107FE6D0B4209E03FCAFEE2AA28AD0E417",
            Hash(current.Video));
        Assert.Equal(
            "478A227D20C7F9D960A672DBE76F02515062EA52568E072CD123C5E30A81E758",
            Hash(v040.Analytic));
        Assert.Equal(
            "DC7B6A81577ACFA9B741A0D4BFB805F6E164597557F4DF6F4413C3C8E3232F87",
            Hash(v040.DemodRaw));
        Assert.Equal(
            "3891828F76474EBCC7F1E00FDE5EC1107FE6D0B4209E03FCAFEE2AA28AD0E417",
            Hash(v040.Video));
        Assert.Equal(Hash(current.Analytic), Hash(v040.Analytic));
    }

    private static RfDemodulatedBlock Decode(
        double[] input,
        string profile)
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            [
                "--system", "ntsc",
                "--tape_format", "VHS",
                "--frequency", "40",
                "--no_diff_demod",
                "--compat-version", profile,
                "input.s16",
                "output"
            ]);
        using DecodeSession session = DecodeSessionFactory.Create(
            command,
            blockLength: BlockLength);
        return session.Pipeline.DecodePreparedBlock(input).Demodulated;
    }

    private static double[] BuildDeterministicRfBlock()
    {
        var input = new double[BlockLength];
        uint state = 0x6D2B79F5U;
        double phase = 0.0;
        for (int i = 0; i < input.Length; i++)
        {
            state = unchecked((1_664_525U * state) + 1_013_904_223U);
            int linePosition = i % 2_540;
            double videoHz = linePosition switch
            {
                < 190 => 3_400_000.0,
                < 320 => 4_500_000.0,
                _ => 4_000_000.0 + (350_000.0 * Math.Sin(i * 0.0017))
            };
            phase += 2.0 * Math.PI * videoHz / 40_000_000.0;
            double noise = unchecked((short)(state >> 16)) * 0.015;
            input[i] = Math.Round((22_000.0 * Math.Cos(phase)) + noise);
        }

        return input;
    }

    private static string Hash<T>(T[] values)
        where T : unmanaged
        => Convert.ToHexString(SHA256.HashData(
            MemoryMarshal.AsBytes(values.AsSpan())));
}
