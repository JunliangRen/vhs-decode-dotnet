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

    [Theory(DisplayName = "DSP backend accepts exact, ipp-fast, and cuda-fast case-insensitively")]
    [MemberData(nameof(Commands))]
    public void DspBackendAcceptsSupportedValues(DecodeCommandSpec spec)
    {
        Assert.Equal("exact", Parse(spec, "--dsp-backend", "EXACT").Get<string>("dsp_backend"));
        Assert.Equal("ipp-fast", Parse(spec, "--dsp-backend=IPP-FAST").Get<string>("dsp_backend"));
        Assert.Equal("cuda-fast", Parse(spec, "--dsp-backend", "CUDA-FAST").Get<string>("dsp_backend"));
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
            "argument --dsp-backend: invalid choice: 'auto' (choose from exact, ipp-fast, cuda-fast)",
            auto.Message);
        Assert.Equal(
            "argument --dsp-backend: invalid choice: 'cuda' (choose from exact, ipp-fast, cuda-fast)",
            unknown.Message);
    }

    [Theory(DisplayName = "DSP backend is excluded from Python compatibility namespace")]
    [MemberData(nameof(Commands))]
    public void DspBackendIsExcludedFromPythonNamespace(DecodeCommandSpec spec)
    {
        string exact = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "exact"));
        string ippFast = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "ipp-fast"));
        string cudaFast = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "cuda-fast"));

        Assert.Equal(exact, ippFast);
        Assert.Equal(exact, cudaFast);
        Assert.DoesNotContain("dsp_backend", exact, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "20 MSPS decode switch accepts both spellings and stays out of Python namespaces")]
    public void TwentyMspsDecodeSwitchIsDotNetOnly()
    {
        ParsedCommand dashed = Parse(CliSpecs.Vhs, "--decode-at-20msps");
        ParsedCommand underscored = Parse(CliSpecs.Vhs, "--decode_at_20msps");
        ParsedCommand disabled = Parse(CliSpecs.Vhs);

        Assert.True(dashed.Get<bool>(CliSpecs.DecodeAt20MspsDestination));
        Assert.True(underscored.Get<bool>(CliSpecs.DecodeAt20MspsDestination));
        Assert.False(disabled.Get<bool>(CliSpecs.DecodeAt20MspsDestination));
        Assert.Equal(
            PythonNamespaceFormatter.Format(disabled),
            PythonNamespaceFormatter.Format(dashed));
        Assert.DoesNotContain(
            CliSpecs.DecodeAt20MspsDestination,
            PythonNamespaceFormatter.Format(dashed),
            StringComparison.Ordinal);
        string help = CommandHelpFormatter.Format(CliSpecs.Vhs, "decode.py");
        Assert.Contains("--decode-at-20msps", help, StringComparison.Ordinal);
        Assert.Contains("--decode_at_20msps", help, StringComparison.Ordinal);
        Assert.Contains("preview enables this automatically", help, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "IPP fast rejects commands whose accelerated kernels are not implemented")]
    [InlineData("cvbs")]
    [InlineData("hifi")]
    public void IppFastRejectsCommandsWithoutAcceleratedKernels(string commandName)
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => DspBackendSupport.EnsureCommandSupported(DspBackend.IppFast, commandName));

        Assert.Contains("does not yet contain accelerated kernels", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no silent Exact fallback", exception.Message, StringComparison.Ordinal);
        DspBackendSupport.EnsureCommandSupported(DspBackend.Exact, commandName);
    }

    [Theory(DisplayName = "IPP fast is enabled for implemented RF paths")]
    [InlineData("vhs")]
    [InlineData("ld")]
    public void IppFastSupportsImplementedRfPaths(string commandName)
        => DspBackendSupport.EnsureCommandSupported(DspBackend.IppFast, commandName);

    [Theory(DisplayName = "CUDA fast is isolated to the VHS command")]
    [InlineData("cvbs")]
    [InlineData("ld")]
    [InlineData("hifi")]
    public void CudaFastRejectsOtherCommandsWithoutFallback(string commandName)
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => DspBackendSupport.EnsureCommandSupported(DspBackend.CudaFast, commandName));

        Assert.Contains("supports only the 'vhs' command", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no silent fallback", exception.Message, StringComparison.OrdinalIgnoreCase);
        DspBackendSupport.EnsureCommandSupported(DspBackend.CudaFast, "vhs");
    }

    private static ParsedCommand Parse(DecodeCommandSpec spec, params string[] options)
    {
        string[] arguments = spec.MinimumPositionals == 0
            ? options
            : [.. options, "input.s16", "output"];
        return new CommandLineParser().Parse(spec, arguments);
    }
}
