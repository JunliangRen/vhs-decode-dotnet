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

    [Fact(DisplayName = "Only current profile adds CTI to the Python namespace")]
    public void OnlyCurrentProfileAddsCtiToPythonNamespace()
    {
        string v040 = PythonNamespaceFormatter.Format(
            Parse(CliSpecs.Vhs, "--compat-version", "v0.4.0"));
        string current = PythonNamespaceFormatter.Format(
            Parse(CliSpecs.Vhs, "--compat-version", "current"));
        string v040WithCti = PythonNamespaceFormatter.Format(
            Parse(
                CliSpecs.Vhs,
                "--cti_mix",
                "0.35",
                "--cti_width",
                "0"));

        Assert.DoesNotContain("compat_version", v040, StringComparison.Ordinal);
        Assert.DoesNotContain("cti_mix", v040, StringComparison.Ordinal);
        Assert.DoesNotContain("cti_width", v040, StringComparison.Ordinal);
        Assert.Equal(v040, v040WithCti);
        Assert.Contains(
            "detect_chroma_track_phase=False, cti_mix=1, cti_width=2, disable_phase_correction=False",
            current,
            StringComparison.Ordinal);
        Assert.Equal(
            v040,
            current.Replace(", cti_mix=1, cti_width=2", string.Empty, StringComparison.Ordinal));
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
        Assert.Equal("current", current.Algorithms["vhsVsyncLevels"]);
        Assert.Equal("current", current.Algorithms["chromaGroupDelay"]);
        Assert.Equal("current", current.Algorithms["chromaFinalFilter"]);
        Assert.Equal("current", current.Algorithms["cti"]);
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
        Assert.Equal(
            expected == UpstreamBehaviorProfile.Current,
            session.TbcFieldDecoder.ChromaFieldOptions!
                .SuperGaussianFinalFilter is not null);
        Assert.Equal(
            expected == UpstreamBehaviorProfile.Current,
            session.TbcFieldDecoder.ChromaFieldOptions!
                .UseCurrentChromaProcessing);
        Assert.Equal(
            expected == UpstreamBehaviorProfile.Current ? 71 : 70,
            session.TbcFieldDecoder.ChromaFieldOptions!.BurstStart);
        Assert.Equal(
            expected == UpstreamBehaviorProfile.Current ? 119 : 122,
            session.TbcFieldDecoder.ChromaFieldOptions!.BurstEnd);
        Assert.Equal(1.0, session.TbcFieldDecoder.ChromaFieldOptions!.CtiMix);
        Assert.Equal(2, session.TbcFieldDecoder.ChromaFieldOptions!.CtiWidth);
    }

    [Fact(DisplayName = "Current profile routes official CTI parameters")]
    public void CurrentProfileRoutesOfficialCtiParameters()
    {
        ParsedCommand command = Parse(
            CliSpecs.Vhs,
            "--compat-version",
            "current",
            "--cti_mix",
            "0.35",
            "--cti_width",
            "0",
            "input.s16",
            "output");

        using DecodeSession session = DecodeSessionFactory.Create(command);
        VhsChromaFieldOptions options = session.TbcFieldDecoder.ChromaFieldOptions!;

        Assert.Equal(0.35, command.Get<double>("cti_mix"));
        Assert.Equal(0, command.Get<int>("cti_width"));
        Assert.Equal(0.35, options.CtiMix);
        Assert.Equal(0, options.CtiWidth);
        Assert.True(options.UseCurrentChromaProcessing);
    }

    [Fact(DisplayName = "Non-VHS decode sessions retain the v0.4.0 behavior profile")]
    public void NonVhsDecodeSessionsRetainV040Profile()
    {
        using DecodeSession session = DecodeSessionFactory.Create(
            Parse(CliSpecs.Cvbs, "input.s16", "output"));

        Assert.Equal(UpstreamBehaviorProfile.V040, session.ExecutionOptions.UpstreamBehaviorProfile);
        Assert.Equal(UpstreamBehaviorProfile.V040, session.TbcFieldDecoder.UpstreamBehaviorProfile);
        Assert.Null(
            session.TbcFieldDecoder.ChromaFieldOptions?
                .SuperGaussianFinalFilter);
    }

    private static ParsedCommand Parse(DecodeCommandSpec spec, params string[] arguments)
        => new CommandLineParser().Parse(spec, arguments);
}
