using VHSDecode.Core.CommandLine;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CommandLineOptionalValueCompatibilityTests
{
    [Fact(DisplayName = "ire0_adjust exact name preserves an invalid follower as positional")]
    public void Ire0AdjustExactNamePreservesInvalidFollowerAsPositional()
    {
        ParsedCommand command = Parse("--ire0_adjust", "capture.ldf", "output");

        Assert.Equal("backporch", command.Get<string>("ire0_adjust"));
        Assert.Equal("capture.ldf", command.InputFile);
        Assert.Equal("output", command.OutputBase);
    }

    [Fact(DisplayName = "ire0_adjust abbreviation follows argparse optional-value consumption")]
    public void Ire0AdjustAbbreviationFollowsArgparseOptionalValueConsumption()
    {
        CommandLineParseException invalid = Assert.Throws<CommandLineParseException>(
            () => Parse("--ire0_ad", "capture.ldf", "output"));
        Assert.Equal("argument --ire0_adjust: Allowed values: hsync, backporch", invalid.Message);

        ParsedCommand valid = Parse("--ire0_ad", "HSYNC", "capture.ldf", "output");
        Assert.Equal("hsync", valid.Get<string>("ire0_adjust"));
        Assert.Equal("capture.ldf", valid.InputFile);
        Assert.Equal("output", valid.OutputBase);
    }

    [Fact(DisplayName = "ire0_adjust values preserve Python lowercasing and empty-part validation")]
    public void Ire0AdjustValuesPreservePythonLowercasingAndEmptyPartValidation()
    {
        ParsedCommand spaced = Parse("--ire0_adjust= HSYNC , BACKPORCH ");
        Assert.Equal(" hsync , backporch ", spaced.Get<string>("ire0_adjust"));

        ParsedCommand pythonWhitespace = Parse("--ire0_adjust=\u001cHSYNC\u001c");
        Assert.Equal("\u001chsync\u001c", pythonWhitespace.Get<string>("ire0_adjust"));

        AssertInvalidValue(string.Empty);
        AssertInvalidValue("hsync,");
        AssertInvalidValue("hsync,,backporch");
    }

    private static ParsedCommand Parse(params string[] arguments)
        => new CommandLineParser().Parse(CliSpecs.Vhs, arguments);

    private static void AssertInvalidValue(string value)
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => Parse($"--ire0_adjust={value}"));
        Assert.Equal("argument --ire0_adjust: Allowed values: hsync, backporch", exception.Message);
    }
}
