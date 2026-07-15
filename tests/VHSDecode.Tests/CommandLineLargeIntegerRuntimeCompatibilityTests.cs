using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CommandLineLargeIntegerRuntimeCompatibilityTests
{
    [Fact(DisplayName = "Large field-order confidence values clamp like Python integers")]
    public void LargeFieldOrderConfidenceValuesClampLikePythonIntegers()
    {
        using DecodeSession negative = CreateVhs(
            "--field_order_confidence",
            "-999999999999999999999999999999");
        using DecodeSession positive = CreateVhs(
            "--field_order_confidence",
            "999999999999999999999999999999");

        Assert.Equal(0, negative.FieldOrderOptions.Confidence);
        Assert.Equal(100, positive.FieldOrderOptions.Confidence);
    }

    [Fact(DisplayName = "Large track phase values retain v0.4.0 validation semantics")]
    public void LargeTrackPhaseValuesRetainV040ValidationSemantics()
    {
        const string largeTrackPhase = "999999999999999999999999999999";

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => CreateVhs("--track_phase", largeTrackPhase));
        Assert.Equal("Track phase can only be 0, 1 or None", exception.Message);

        using DecodeSession meSecam = CreateVhs(
            "--system",
            "MESECAM",
            "--track_phase",
            largeTrackPhase);
        Assert.Null(meSecam.TbcRenderer.TrackPhaseIre0Offset);
    }

    [Fact(DisplayName = "Large sharpness values use Python integer true-division semantics")]
    public void LargeSharpnessValuesUsePythonIntegerTrueDivisionSemantics()
    {
        using DecodeSession beyondInt32 = CreateVhs("--sharpness", "3000000000");
        Assert.Equal(30_000_000.0, beyondInt32.FilterOptions.SharpnessEq?.Level);

        string finiteHugeValue = "1" + new string('0', 309);
        using DecodeSession finiteHuge = CreateVhs("--sharpness", finiteHugeValue);
        Assert.Equal(1e307, finiteHuge.FilterOptions.SharpnessEq?.Level);

        string overflowingValue = "1" + new string('0', 400);
        OverflowException exception = Assert.Throws<OverflowException>(
            () => CreateVhs("--sharpness", overflowingValue));
        Assert.Equal("integer division result too large for a float", exception.Message);
    }

    [Fact(DisplayName = "Large VHS wow smoothing values do not fall back to the default")]
    public void LargeVhsWowSmoothingValuesDoNotFallBackToTheDefault()
    {
        using DecodeSession session = CreateVhs(
            "--wow_level_adjust_smoothing",
            "3000000000");

        Assert.Equal(3_000_000_000.0, session.TbcRenderer.WowLevelAdjustSmoothing);
    }

    private static DecodeSession CreateVhs(params string[] options)
    {
        var arguments = new List<string>(options)
        {
            "input.u8",
            "out"
        };
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, arguments);
        return DecodeSessionFactory.Create(command);
    }
}
