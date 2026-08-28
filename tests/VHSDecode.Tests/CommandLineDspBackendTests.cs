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

    [Theory(DisplayName = "DSP backend accepts exact, ipp-fast, cuda-fast, and approx-fast case-insensitively")]
    [MemberData(nameof(Commands))]
    public void DspBackendAcceptsSupportedValues(DecodeCommandSpec spec)
    {
        Assert.Equal("exact", Parse(spec, "--dsp-backend", "EXACT").Get<string>("dsp_backend"));
        Assert.Equal("ipp-fast", Parse(spec, "--dsp-backend=IPP-FAST").Get<string>("dsp_backend"));
        Assert.Equal("cuda-fast", Parse(spec, "--dsp-backend", "CUDA-FAST").Get<string>("dsp_backend"));
        Assert.Equal("approx-fast", Parse(spec, "--dsp-backend=APPROX-FAST").Get<string>("dsp_backend"));
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
            "argument --dsp-backend: invalid choice: 'auto' (choose from exact, ipp-fast, cuda-fast, approx-fast)",
            auto.Message);
        Assert.Equal(
            "argument --dsp-backend: invalid choice: 'cuda' (choose from exact, ipp-fast, cuda-fast, approx-fast)",
            unknown.Message);
    }

    [Theory(DisplayName = "DSP backend is excluded from Python compatibility namespace")]
    [MemberData(nameof(Commands))]
    public void DspBackendIsExcludedFromPythonNamespace(DecodeCommandSpec spec)
    {
        string exact = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "exact"));
        string ippFast = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "ipp-fast"));
        string cudaFast = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "cuda-fast"));
        string approxFast = PythonNamespaceFormatter.Format(Parse(spec, "--dsp-backend", "approx-fast"));

        Assert.Equal(exact, ippFast);
        Assert.Equal(exact, cudaFast);
        Assert.Equal(exact, approxFast);
        Assert.DoesNotContain("dsp_backend", exact, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Approx provider is absent by default and records explicit values")]
    public void ApproxProviderTracksValueAndSource()
    {
        ParsedCommand automatic = Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast");
        ParsedCommand managed = Parse(
            CliSpecs.Vhs,
            "--dsp-backend",
            "approx-fast",
            "--approx-provider",
            "MANAGED");
        ParsedCommand ipp = Parse(
            CliSpecs.Vhs,
            "--dsp-backend=approx-fast",
            "--approx-provider=IPP");

        Assert.Null(automatic.Get<string?>("approx_provider"));
        Assert.Equal(ParsedOptionSource.Default, automatic.GetSource("approx_provider"));
        Assert.Equal("managed", managed.Get<string>("approx_provider"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, managed.GetSource("approx_provider"));
        Assert.Equal("ipp", ipp.Get<string>("approx_provider"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, ipp.GetSource("approx_provider"));
    }

    [Theory(DisplayName = "Approx provider rejects automatic aliases and unknown values")]
    [InlineData("auto")]
    [InlineData("native")]
    public void ApproxProviderRejectsUnsupportedValues(string value)
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast", "--approx-provider", value));

        Assert.Equal(
            $"argument --approx-provider: invalid choice: '{value}' (choose from managed, ipp)",
            exception.Message);
    }

    [Fact(DisplayName = "Approx provider is available only on the VHS command line")]
    public void ApproxProviderOptionIsVhsOnly()
    {
        foreach (DecodeCommandSpec spec in new[] { CliSpecs.Cvbs, CliSpecs.LaserDisc, CliSpecs.HiFi })
        {
            CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
                () => Parse(spec, "--approx-provider", "managed"));

            Assert.Contains("unrecognized arguments: --approx-provider", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "Approx provider is excluded from Python compatibility namespace")]
    public void ApproxProviderIsExcludedFromPythonNamespace()
    {
        string automatic = PythonNamespaceFormatter.Format(
            Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast"));
        string managed = PythonNamespaceFormatter.Format(
            Parse(
                CliSpecs.Vhs,
                "--dsp-backend",
                "approx-fast",
                "--approx-provider",
                "managed"));
        string ipp = PythonNamespaceFormatter.Format(
            Parse(
                CliSpecs.Vhs,
                "--dsp-backend",
                "approx-fast",
                "--approx-provider",
                "ipp"));

        Assert.Equal(automatic, managed);
        Assert.Equal(automatic, ipp);
        Assert.DoesNotContain("approx_provider", automatic, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Approx resampler is absent by default and records explicit values")]
    public void ApproxResamplerTracksValueAndSource()
    {
        ParsedCommand automatic = Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast");
        ParsedCommand sinc16 = Parse(
            CliSpecs.Vhs,
            "--dsp-backend",
            "approx-fast",
            "--approx-resampler",
            "SINC16");
        ParsedCommand catmullRom4 = Parse(
            CliSpecs.Vhs,
            "--dsp-backend=approx-fast",
            "--approx-resampler=CATMULL-ROM4");

        Assert.Null(automatic.Get<string?>("approx_resampler"));
        Assert.Equal(ParsedOptionSource.Default, automatic.GetSource("approx_resampler"));
        Assert.Equal("sinc16", sinc16.Get<string>("approx_resampler"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, sinc16.GetSource("approx_resampler"));
        Assert.Equal("catmull-rom4", catmullRom4.Get<string>("approx_resampler"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, catmullRom4.GetSource("approx_resampler"));
    }

    [Theory(DisplayName = "Approx resampler rejects aliases and unknown values")]
    [InlineData("catmull")]
    [InlineData("auto")]
    public void ApproxResamplerRejectsUnsupportedValues(string value)
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast", "--approx-resampler", value));

        Assert.Equal(
            $"argument --approx-resampler: invalid choice: '{value}' (choose from sinc16, catmull-rom4)",
            exception.Message);
    }

    [Fact(DisplayName = "Approx resampler is available only on the VHS command line")]
    public void ApproxResamplerOptionIsVhsOnly()
    {
        foreach (DecodeCommandSpec spec in new[] { CliSpecs.Cvbs, CliSpecs.LaserDisc, CliSpecs.HiFi })
        {
            CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
                () => Parse(spec, "--approx-resampler", "catmull-rom4"));

            Assert.Contains("unrecognized arguments: --approx-resampler", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "Approx resampler is excluded from Python compatibility namespace")]
    public void ApproxResamplerIsExcludedFromPythonNamespace()
    {
        string automatic = PythonNamespaceFormatter.Format(
            Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast"));
        string sinc16 = PythonNamespaceFormatter.Format(
            Parse(
                CliSpecs.Vhs,
                "--dsp-backend",
                "approx-fast",
                "--approx-resampler",
                "sinc16"));
        string catmullRom4 = PythonNamespaceFormatter.Format(
            Parse(
                CliSpecs.Vhs,
                "--dsp-backend",
                "approx-fast",
                "--approx-resampler",
                "catmull-rom4"));

        Assert.Equal(automatic, sinc16);
        Assert.Equal(automatic, catmullRom4);
        Assert.DoesNotContain("approx_resampler", automatic, StringComparison.Ordinal);

        string help = CommandHelpFormatter.Format(CliSpecs.Vhs, "decode.py");
        Assert.Contains("--approx-resampler {sinc16,catmull-rom4}", help, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Approx precision is absent by default and records explicit values")]
    public void ApproxPrecisionTracksValueAndSource()
    {
        ParsedCommand automatic = Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast");
        ParsedCommand balanced = Parse(
            CliSpecs.Vhs,
            "--dsp-backend",
            "approx-fast",
            "--approx-precision",
            "BALANCED");
        ParsedCommand aggressive = Parse(
            CliSpecs.Vhs,
            "--dsp-backend=approx-fast",
            "--approx-precision=AGGRESSIVE");

        Assert.Null(automatic.Get<string?>("approx_precision"));
        Assert.Equal(ParsedOptionSource.Default, automatic.GetSource("approx_precision"));
        Assert.Equal("balanced", balanced.Get<string>("approx_precision"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, balanced.GetSource("approx_precision"));
        Assert.Equal("aggressive", aggressive.Get<string>("approx_precision"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, aggressive.GetSource("approx_precision"));
    }

    [Theory(DisplayName = "Approx precision rejects aliases and unknown values")]
    [InlineData("fast")]
    [InlineData("auto")]
    public void ApproxPrecisionRejectsUnsupportedValues(string value)
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast", "--approx-precision", value));

        Assert.Equal(
            $"argument --approx-precision: invalid choice: '{value}' (choose from balanced, aggressive)",
            exception.Message);
    }

    [Fact(DisplayName = "Approx precision is available only on the VHS command line")]
    public void ApproxPrecisionOptionIsVhsOnly()
    {
        foreach (DecodeCommandSpec spec in new[] { CliSpecs.Cvbs, CliSpecs.LaserDisc, CliSpecs.HiFi })
        {
            CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
                () => Parse(spec, "--approx-precision", "aggressive"));

            Assert.Contains("unrecognized arguments: --approx-precision", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "Approx precision is excluded from Python compatibility namespace")]
    public void ApproxPrecisionIsExcludedFromPythonNamespace()
    {
        string automatic = PythonNamespaceFormatter.Format(
            Parse(CliSpecs.Vhs, "--dsp-backend", "approx-fast"));
        string balanced = PythonNamespaceFormatter.Format(
            Parse(
                CliSpecs.Vhs,
                "--dsp-backend",
                "approx-fast",
                "--approx-precision",
                "balanced"));
        string aggressive = PythonNamespaceFormatter.Format(
            Parse(
                CliSpecs.Vhs,
                "--dsp-backend",
                "approx-fast",
                "--approx-precision",
                "aggressive"));

        Assert.Equal(automatic, balanced);
        Assert.Equal(automatic, aggressive);
        Assert.DoesNotContain("approx_precision", automatic, StringComparison.Ordinal);

        string help = CommandHelpFormatter.Format(CliSpecs.Vhs, "decode.py");
        Assert.Contains("--approx-precision {balanced,aggressive}", help, StringComparison.Ordinal);
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

    [Theory(DisplayName = "Approx fast is isolated to the VHS command")]
    [InlineData("cvbs")]
    [InlineData("ld")]
    [InlineData("hifi")]
    public void ApproxFastRejectsOtherCommandsWithoutFallback(string commandName)
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => DspBackendSupport.EnsureCommandSupported(DspBackend.ApproxFast, commandName));

        Assert.Contains("supports only the 'vhs' command", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no silent Exact fallback", exception.Message, StringComparison.Ordinal);
        DspBackendSupport.EnsureCommandSupported(DspBackend.ApproxFast, "vhs");
    }

    private static ParsedCommand Parse(DecodeCommandSpec spec, params string[] options)
    {
        string[] arguments = spec.MinimumPositionals == 0
            ? options
            : [.. options, "input.s16", "output"];
        return new CommandLineParser().Parse(spec, arguments);
    }
}
