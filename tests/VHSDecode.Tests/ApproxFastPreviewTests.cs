using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using System.Runtime.InteropServices;
using VHSDecode.Preview;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ApproxFastPreviewTests
{
    [Fact(DisplayName = "Preview templates and windows preserve an explicit Approx provider")]
    public void TemplatesAndWindowsPreserveProvider()
    {
        ParsedCommand command = Parse(
            "--preview-server",
            "--dsp-backend",
            "approx-fast",
            "--approx-provider",
            "managed",
            "input.ldf");

        ParsedCommand template = PreviewDecodeCommandFactory.CreateFastTemplate(command);
        ParsedCommand window = PreviewDecodeCommandFactory.ForWindow(
            template,
            startSeconds: 1.25,
            sourceSampleRateHz: 40_000_000.0,
            requestedFrames: 3);

        Assert.Equal("approx-fast", template.Get<string>("dsp_backend"));
        Assert.Equal("managed", template.Get<string>("approx_provider"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, template.GetSource("dsp_backend"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, template.GetSource("approx_provider"));
        Assert.Equal("managed", window.Get<string>("approx_provider"));
        Assert.Equal(ParsedOptionSource.ExplicitValue, window.GetSource("approx_provider"));
    }

    [Fact(DisplayName = "Preview backend selection pins explicit Approx without CPU or CUDA fallback")]
    public async Task BackendSelectionPinsExplicitApprox()
    {
        ParsedCommand command = Parse(
            "--preview-server",
            "--dsp-backend",
            "approx-fast",
            "input.ldf");
        var attempted = new List<DspBackend>();

        DspBackend selected = await PreviewBackendSelector.SelectAsync(
            command,
            (backend, _) =>
            {
                attempted.Add(backend);
                return Task.FromResult(backend);
            },
            TextWriter.Null,
            CancellationToken.None,
            cudaPreflight: () => throw new InvalidOperationException(
                "Explicit Approx must not probe CUDA."),
            ippProbe: () => throw new InvalidOperationException(
                "Provider selection belongs to DecodeSessionFactory."));

        Assert.Equal(DspBackend.ApproxFast, selected);
        Assert.Equal([DspBackend.ApproxFast], attempted);
    }

    [Fact(DisplayName = "Preview window sessions reuse the initially resolved Approx provider selection")]
    public void WindowSessionReusesResolvedProviderSelection()
    {
        ParsedCommand command = Parse(
            "--preview-server",
            "--dsp-backend",
            "approx-fast",
            "input.ldf");
        var selection = new ApproxProviderSelection(
            ApproxProvider.Managed,
            IsExplicit: false,
            FellBackFromIpp: true,
            Architecture.X64,
            "pinned preview fallback");

        using DecodeSession session = DecodeSessionFactory.CreateForPreview(
            command,
            selection);

        Assert.Equal(ApproxProvider.Managed, session.ExecutionOptions.ApproxProvider);
        Assert.False(session.ExecutionOptions.ApproxProviderIsExplicit);
        Assert.True(session.ExecutionOptions.ApproxProviderFellBackFromIpp);
        Assert.Equal(Architecture.X64, session.ExecutionOptions.ApproxProviderProcessArchitecture);
        Assert.Equal("pinned preview fallback", session.ExecutionOptions.ApproxProviderDiagnostic);
    }

    private static ParsedCommand Parse(params string[] arguments)
        => new CommandLineParser().Parse(CliSpecs.Vhs, arguments);
}
