using System.Numerics;
using VHSDecode.Core.CommandLine;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CommandLineNumericCompatibilityTests
{
    [Fact(DisplayName = "CLI integer parsing matches Python syntax and precision")]
    public void CliIntegerParsingMatchesPythonSyntaxAndPrecision()
    {
        Assert.Equal(1_000, ParseVhs("--threads", "1_000").Get<int>("threads"));
        Assert.Equal(12, ParseVhs("--threads", "１２").Get<int>("threads"));
        Assert.Equal(12, ParseVhs("--threads", "𝟙𝟚").Get<int>("threads"));
        Assert.Equal(-12, ParseVhs("--threads", "\t-１２\n").Get<int>("threads"));

        const string hugeText = "999999999999999999999999999";
        ParsedCommand huge = ParseVhs("--threads", hugeText);
        Assert.Equal(BigInteger.Parse(hugeText), Assert.IsType<BigInteger>(huge.Values["threads"]));
        Assert.Contains($"threads={hugeText}", PythonNamespaceFormatter.Format(huge));
        Assert.Throws<OverflowException>(() => huge.Get<int>("threads"));

        AssertInvalidInteger("1__0");
        AssertInvalidInteger("1_");
        AssertInvalidInteger("0x10");
    }

    [Fact(DisplayName = "Python numeric parsing is shared by every decode command")]
    public void PythonNumericParsingIsSharedByEveryDecodeCommand()
    {
        var parser = new CommandLineParser();

        Assert.Equal(10, parser.Parse(CliSpecs.Cvbs, ["--threads", "1_0"]).Get<int>("threads"));
        Assert.Equal(
            110_000,
            parser.Parse(CliSpecs.LaserDisc, ["--length", "1_1_0_0_0_0", "in", "out"])
                .Get<int>("length"));
        Assert.Equal(
            48_000,
            parser.Parse(CliSpecs.HiFi, ["--audio_rate", "4_8_0_0_0"])
                .Get<int>("rate"));
    }

    [Fact(DisplayName = "CLI float parsing matches Python syntax and special values")]
    public void CliFloatParsingMatchesPythonSyntaxAndSpecialValues()
    {
        Assert.Equal(1_000.5, ParseVhs("--level_adjust", "1_000.5").Get<double>("level_adjust"));
        Assert.Equal(0.1, ParseVhs("--level_adjust", ".1_0").Get<double>("level_adjust"));
        Assert.Equal(12.3, ParseVhs("--level_adjust", "𝟙𝟚.𝟛").Get<double>("level_adjust"));
        Assert.Equal(1e20, ParseVhs("--level_adjust", "1e+2_0").Get<double>("level_adjust"));
        Assert.True(double.IsPositiveInfinity(ParseVhs("--level_adjust", "inf").Get<double>("level_adjust")));
        Assert.True(double.IsPositiveInfinity(ParseVhs("--level_adjust", "1e309").Get<double>("level_adjust")));

        double negativeNaN = ParseVhs("--level_adjust=-NaN").Get<double>("level_adjust");
        Assert.True(double.IsNaN(negativeNaN));
        Assert.True(BitConverter.DoubleToInt64Bits(negativeNaN) < 0);
        Assert.True(double.IsNegativeInfinity(
            ParseVhs("--level_adjust=-Infinity").Get<double>("level_adjust")));

        AssertInvalidFloat("1__0.0");
        AssertInvalidFloat("1_.0");
        AssertInvalidFloat("1e_20");
    }

    [Fact(DisplayName = "CLI option-value boundaries match argparse negative-number detection")]
    public void CliOptionValueBoundariesMatchArgparseNegativeNumberDetection()
    {
        AssertParseError(
            ["--level_adjust", "-Infinity"],
            "argument -L/--level_adjust: expected one argument");
        AssertParseError(
            ["--level_adjust", "-1junk"],
            "argument -L/--level_adjust: invalid float value: '-1junk'");
        AssertParseError(
            ["--level_adjust", "-.junk"],
            "argument -L/--level_adjust: expected one argument");
        AssertParseError(
            ["--level_adjust", "-１２junk"],
            "argument -L/--level_adjust: invalid float value: '-１２junk'");
        AssertParseError(
            ["--fm_audio_notch", "-Infinity"],
            "unrecognized arguments: -Infinity");
    }

    [Fact(DisplayName = "CLI frequency parsing matches Python float and suffix boundaries")]
    public void CliFrequencyParsingMatchesPythonFloatAndSuffixBoundaries()
    {
        Assert.Equal(10.0, ParseVhs("--notch", "1_0MHz").Get<double>("notch"));
        Assert.Equal(0.0123, ParseVhs("--notch", "１２.３kHz").Get<double>("notch"), 12);
        Assert.True(double.IsPositiveInfinity(ParseVhs("--notch", "infMHz").Get<double>("notch")));

        CommandLineParseException trailingSpace = Assert.Throws<CommandLineParseException>(
            () => ParseVhs("--notch", "1MHz "));
        Assert.Equal("argument --notch: invalid parse_frequency value: '1MHz '", trailingSpace.Message);

        CommandLineParseException cxadcSpace = Assert.Throws<CommandLineParseException>(
            () => ParseVhs("--frequency", "cxadc "));
        Assert.Equal("argument -f/--frequency: invalid _parse_frequency value: 'cxadc '", cxadcSpace.Message);
    }

    private static ParsedCommand ParseVhs(params string[] arguments)
        => new CommandLineParser().Parse(CliSpecs.Vhs, arguments);

    private static void AssertInvalidInteger(string value)
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => ParseVhs("--threads", value));
        Assert.Equal($"argument -t/--threads: invalid int value: '{value}'", exception.Message);
    }

    private static void AssertInvalidFloat(string value)
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => ParseVhs("--level_adjust", value));
        Assert.Equal($"argument -L/--level_adjust: invalid float value: '{value}'", exception.Message);
    }

    private static void AssertParseError(string[] arguments, string expected)
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => ParseVhs(arguments));
        Assert.Equal(expected, exception.Message);
    }
}
