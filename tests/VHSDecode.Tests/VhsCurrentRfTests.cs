using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsCurrentRfTests
{
    [Fact]
    public void BlockChromaMatchesUpstreamFloat32Output()
    {
        double[] input = [20, -10, 15, 0, -11, 4, 7, -2];
        Complex[] gain = [0.0, 0.25, 1.0, 0.5, 0.0];
        float[] expected =
        [
            1.2196699380874634f, -0.39146602153778076f, 3.269606828689575f, -5.348572731018066f,
            5.280330181121826f, 2.391465902328491f, -9.769606590270996f, 3.3485727310180664f
        ];

        Assert.Equal(expected, RfBlockDecodePipeline.DecodeCurrentChromaBlock(input, gain, offset: 2));
    }

    [Theory]
    [InlineData("current", 0.18)]
    [InlineData("v0.4.0", 0.35)]
    public void TwentyEightMHzDropoutDefaultDependsOnCompatibility(string profile, double expected)
    {
        using DecodeSession session = Create(profile, "--frequency", "28.636", "input.s16", "output");
        Assert.Equal(expected, session.DropoutOptions.ThresholdFraction);
    }

    [Fact]
    public void CurrentUsesExplicitDropoutThresholdAtTwentyEightMHz()
    {
        using DecodeSession session = Create("current", "--frequency", "28.636",
            "--dod_threshold_p", "0.29", "input.s16", "output");
        Assert.Equal(0.29, session.DropoutOptions.ThresholdFraction);
    }

    [Fact]
    public void CurrentSignedEightBitNativeInputMatchesPcm16Scale()
    {
        using DecodeSession session = Create("current", "input.s8", "output");
        using var input = new MemoryStream([0x80, 0xFF, 0x00, 0x7F]);
        Assert.Equal(new double[] { -32768, -256, 0, 32512 }, session.Loader.Read(input, 0, 4));
    }

    [Theory]
    [InlineData("current", "analog")]
    [InlineData("v0.4.0", "file")]
    public void SuperVhsLinearDeemphasisUsesTheSelectedSource(string profile, string expected)
    {
        using DecodeSession session = Create(profile, "--pal", "--tf", "SVHS", "input.s16", "output");
        Assert.Equal(expected, session.Parameters.RfParams.GetProperty("video_custom_luma_filters")[0]
            .GetProperty("type").GetString());
    }

    private static DecodeSession Create(string profile, params string[] arguments)
        => DecodeSessionFactory.Create(new CommandLineParser().Parse(
            CliSpecs.Vhs, ["--compat-version", profile, "--no_resample", .. arguments]));
}
