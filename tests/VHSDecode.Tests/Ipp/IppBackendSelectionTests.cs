using System.IO;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Preview;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppBackendSelectionTests
{
    [Fact]
    public void InstallingIppPreservesOrdinaryExactDefault()
    {
        if (!IppTestRuntime.TryProbe(out _)) return;
        using DecodeSession session = DecodeSessionFactory.Create(Parse([]));
        Assert.Equal(DspBackend.Exact, session.ExecutionOptions.DspBackend);
    }

    [Fact]
    public void AutomaticApproxUsesRealInstalledIpp()
    {
        if (!IppTestRuntime.TryProbe(out _)) return;
        using DecodeSession session = DecodeSessionFactory.Create(Parse(["--dsp-backend", "approx-fast"]));
        Assert.Equal(ApproxProvider.Ipp, session.ExecutionOptions.ApproxProvider);
        Assert.False(session.ExecutionOptions.ApproxProviderIsExplicit);
        Assert.False(session.ExecutionOptions.ApproxProviderFellBackFromIpp);
    }

    [Theory]
    [InlineData("managed", ApproxProvider.Managed)]
    [InlineData("ipp", ApproxProvider.Ipp)]
    public void ExplicitApproxProviderIsRespectedWithRealIpp(string option, ApproxProvider expected)
    {
        if (!IppTestRuntime.TryProbe(out _)) return;
        using DecodeSession session = DecodeSessionFactory.Create(Parse(
            ["--dsp-backend", "approx-fast", "--approx-provider", option]));
        Assert.Equal(expected, session.ExecutionOptions.ApproxProvider);
        Assert.True(session.ExecutionOptions.ApproxProviderIsExplicit);
        Assert.False(session.ExecutionOptions.ApproxProviderFellBackFromIpp);
    }

    [Fact]
    public async Task AutomaticCpuPreviewUsesRealInstalledIpp()
    {
        if (!IppTestRuntime.TryProbe(out _)) return;
        // write_db excludes automatic CUDA selection and exercises the actual CPU probe.
        DspBackend selected = await PreviewBackendSelector.SelectAsync(
            Parse(["--preview-server", "--write_db"]),
            (backend, _) => Task.FromResult(backend), TextWriter.Null, CancellationToken.None);
        Assert.Equal(DspBackend.IppFast, selected);
    }

    [Fact]
    public async Task ExplicitExactPreviewIsRespectedWithRealIpp()
    {
        if (!IppTestRuntime.TryProbe(out _)) return;
        DspBackend selected = await PreviewBackendSelector.SelectAsync(
            Parse(["--preview-server", "--dsp-backend", "exact"]),
            (backend, _) => Task.FromResult(backend), TextWriter.Null, CancellationToken.None);
        Assert.Equal(DspBackend.Exact, selected);
    }

    private static ParsedCommand Parse(string[] options)
        => new CommandLineParser().Parse(CliSpecs.Vhs,
            options.Contains("--preview-server")
                ? ["--pal", .. options, "capture.s16"]
                : ["--pal", .. options, "capture.s16", "output"]);
}
