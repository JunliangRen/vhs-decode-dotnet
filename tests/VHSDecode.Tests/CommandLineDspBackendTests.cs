using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CommandLineDspBackendTests
{
    public static TheoryData<DecodeCommandSpec> Commands => new()
    {
        CliSpecs.Vhs,
        CliSpecs.Cvbs,
        CliSpecs.LaserDisc,
        CliSpecs.HiFi
    };

    [Theory(DisplayName = "DSP backend defaults to exact for every decoder")]
    [MemberData(nameof(Commands))]
    public void DspBackendDefaultsToExact(DecodeCommandSpec spec)
    {
        ParsedCommand command = Parse(spec);

        Assert.Equal("exact", command.Get<string>("dsp_backend"));
        Assert.Equal(ParsedOptionSource.Default, command.GetSource("dsp_backend"));
    }

    [Theory(DisplayName = "DSP backend accepts exact and ipp-fast case-insensitively")]
    [MemberData(nameof(Commands))]
    public void DspBackendAcceptsSupportedValues(DecodeCommandSpec spec)
    {
        Assert.Equal("exact", Parse(spec, "--dsp-backend", "EXACT").Get<string>("dsp_backend"));
        Assert.Equal("ipp-fast", Parse(spec, "--dsp-backend=IPP-FAST").Get<string>("dsp_backend"));
    }

    [Theory(DisplayName = "DSP backend rejects auto and unknown values")]
    [MemberData(nameof(Commands))]
    public void DspBackendRejectsUnsupportedValues(DecodeCommandSpec spec)
    {
        CommandLineParseException auto = Assert.Throws<CommandLineParseException>(
            () => Parse(spec, "--dsp-backend", "auto"));
        CommandLineParseException unknown = Assert.Throws<CommandLineParseException>(
            () => Parse(spec, "--dsp-backend", "cuda"));

        Assert.Equal(
            "argument --dsp-backend: invalid choice: 'auto' (choose from exact, ipp-fast)",
            auto.Message);
        Assert.Equal(
            "argument --dsp-backend: invalid choice: 'cuda' (choose from exact, ipp-fast)",
            unknown.Message);
    }

    [Theory(DisplayName = "DSP backend is excluded from Python compatibility namespace")]
    [MemberData(nameof(Commands))]
    public void DspBackendIsExcludedFromPythonNamespace(DecodeCommandSpec spec)
    {
        string exact = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "exact"));
        string ippFast = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "ipp-fast"));

        Assert.Equal(exact, ippFast);
        Assert.DoesNotContain("dsp_backend", exact, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "IPP fast rejects commands whose accelerated kernels are not implemented")]
    [InlineData("cvbs")]
    [InlineData("ld")]
    [InlineData("hifi")]
    public void IppFastRejectsCommandsWithoutAcceleratedKernels(string commandName)
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => DspBackendSupport.EnsureCommandSupported(DspBackend.IppFast, commandName));

        Assert.Contains("does not yet contain accelerated kernels", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no silent Exact fallback", exception.Message, StringComparison.Ordinal);
        DspBackendSupport.EnsureCommandSupported(DspBackend.Exact, commandName);
    }

    [Fact(DisplayName = "IPP fast is enabled only for the implemented VHS RF path")]
    public void IppFastSupportsVhsRfPath()
        => DspBackendSupport.EnsureCommandSupported(DspBackend.IppFast, "vhs");

    private static ParsedCommand Parse(DecodeCommandSpec spec, params string[] options)
    {
        string[] arguments = spec.MinimumPositionals == 0
            ? options
            : [.. options, "input.s16", "output"];
        return new CommandLineParser().Parse(spec, arguments);
    }
}
