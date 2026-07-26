using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CommandLineUpstreamBehaviorProfileTests
{
    [Fact(DisplayName = "VHS upstream behavior defaults to v0.4.0")]
    public void VhsUpstreamBehaviorDefaultsToV040()
    {
        ParsedCommand command = Parse(CliSpecs.Vhs);

        Assert.Equal(UpstreamBehaviorProfileParser.V040Value, command.Get<string>("compat_version"));
        Assert.Equal(ParsedOptionSource.Default, command.GetSource("compat_version"));
    }

    [Theory(DisplayName = "VHS upstream behavior accepts supported values case-insensitively")]
    [InlineData("V0.4.0", UpstreamBehaviorProfile.V040)]
    [InlineData("CURRENT", UpstreamBehaviorProfile.Current)]
    public void VhsUpstreamBehaviorAcceptsSupportedValues(
        string value,
        UpstreamBehaviorProfile expected)
    {
        ParsedCommand command = Parse(CliSpecs.Vhs, "--compat-version", value);

        Assert.Equal(
            UpstreamBehaviorProfileParser.ToCommandLineValue(expected),
            command.Get<string>("compat_version"));
    }

    [Fact(DisplayName = "VHS upstream behavior rejects unsupported values")]
    public void VhsUpstreamBehaviorRejectsUnsupportedValues()
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => Parse(CliSpecs.Vhs, "--compat-version", "next"));

        Assert.Equal(
            "argument --compat-version: invalid choice: 'next' (choose from v0.4.0, current)",
            exception.Message);
    }

    [Fact(DisplayName = "Upstream behavior is excluded from the v0.4.0 Python namespace")]
    public void UpstreamBehaviorIsExcludedFromPythonNamespace()
    {
        string v040 = PythonNamespaceFormatter.Format(
            Parse(CliSpecs.Vhs, "--compat-version", "v0.4.0"));
        string current = PythonNamespaceFormatter.Format(
            Parse(CliSpecs.Vhs, "--compat-version", "current"));

        Assert.Equal(v040, current);
        Assert.DoesNotContain("compat_version", v040, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Upstream behavior parser round-trips supported profiles")]
    [InlineData(UpstreamBehaviorProfile.V040)]
    [InlineData(UpstreamBehaviorProfile.Current)]
    public void UpstreamBehaviorParserRoundTrips(UpstreamBehaviorProfile profile)
    {
        string value = UpstreamBehaviorProfileParser.ToCommandLineValue(profile);

        Assert.True(UpstreamBehaviorProfileParser.TryParse(value, out UpstreamBehaviorProfile parsed));
        Assert.Equal(profile, parsed);
        Assert.Equal(profile, UpstreamBehaviorProfileParser.Parse(value));
    }

    [Fact(DisplayName = "Embedded upstream behavior baselines pin exact source commits")]
    public void EmbeddedUpstreamBehaviorBaselinesPinExactSourceCommits()
    {
        UpstreamBaselineCatalog catalog = UpstreamBaselineCatalog.Default;
        UpstreamBehaviorBaseline v040 = catalog.Get(UpstreamBehaviorProfile.V040);
        UpstreamBehaviorBaseline current = catalog.Get(UpstreamBehaviorProfile.Current);

        Assert.Equal("oyvindln/vhs-decode", catalog.Repository);
        Assert.Equal("43155200da87c0d49eb37d8ec09b1372075ee8e4", v040.Commit);
        Assert.Equal("v0.4.0", v040.Algorithms["vhsSync"]);
        Assert.Equal("2f21e8ed6018b14561396cc95f1f6828054470b8", current.Commit);
        Assert.Equal("pull/341", current.Source);
        Assert.Equal("current", current.Algorithms["vhsSync"]);
        Assert.Equal("pending", current.Algorithms["vhsVsyncLevels"]);
        Assert.Equal("pending", current.Algorithms["chromaGroupDelay"]);
        Assert.Equal("pending", current.Algorithms["chromaFinalFilter"]);
        Assert.Equal("pending", current.Algorithms["cti"]);
    }

    [Theory(DisplayName = "Decode session routes the selected upstream behavior profile")]
    [InlineData("v0.4.0", UpstreamBehaviorProfile.V040)]
    [InlineData("current", UpstreamBehaviorProfile.Current)]
    public void DecodeSessionRoutesSelectedUpstreamBehaviorProfile(
        string value,
        UpstreamBehaviorProfile expected)
    {
        ParsedCommand command = Parse(
            CliSpecs.Vhs,
            "--compat-version",
            value,
            "input.s16",
            "output");

        using DecodeSession session = DecodeSessionFactory.Create(command);

        Assert.Equal(expected, session.ExecutionOptions.UpstreamBehaviorProfile);
        Assert.Equal(expected, session.TbcFieldDecoder.UpstreamBehaviorProfile);
    }

    [Fact(DisplayName = "Non-VHS decode sessions retain the v0.4.0 behavior profile")]
    public void NonVhsDecodeSessionsRetainV040Profile()
    {
        using DecodeSession session = DecodeSessionFactory.Create(
            Parse(CliSpecs.Cvbs, "input.s16", "output"));

        Assert.Equal(UpstreamBehaviorProfile.V040, session.ExecutionOptions.UpstreamBehaviorProfile);
        Assert.Equal(UpstreamBehaviorProfile.V040, session.TbcFieldDecoder.UpstreamBehaviorProfile);
    }

    private static ParsedCommand Parse(DecodeCommandSpec spec, params string[] arguments)
        => new CommandLineParser().Parse(spec, arguments);
}
