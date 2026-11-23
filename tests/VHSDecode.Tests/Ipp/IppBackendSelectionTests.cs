using System.IO;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.HiFi;
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

    [Theory]
    [InlineData(8_000_000, 1_300_000, 150_000.0, 8192)]
    [InlineData(8_000_000, 1_700_000, 150_000.0, 8192)]
    [InlineData(8_000_000, 1_456_789, 123_456.0, 17)]
    [InlineData(8_000_000, 1_456_789, 123_456.0, 8192)]
    [InlineData(8_000_000, 4_000_000, 100_000.0, 8192)]
    [InlineData(8_000_000, 1_300_000, 0.25, 8192)]
    public void HiFiIppQuadratureMatchesManagedAtBatchBoundariesAndSignedZeros(
        int sampleRateHz, int carrierHz, double deviationHz, int maximumOscillatorLength)
    {
        if (!IppTestRuntime.TryProbe(out var info)) return;
        if (!IppTestRuntime.IsRequired && !info.SupportsAtan2) return;
        Assert.True(info.SupportsAtan2);

        var discriminator = new HiFiQuadratureDiscriminator(sampleRateHz, carrierHz, deviationHz, maximumOscillatorLength);
        float[] input = Enumerable.Range(0, 1550)
            .Select(index => ((index * 37) % 251 - 125) / 128.0f)
            .ToArray();
        input[512] = -0.0f;
        input[513] = 0.0f;
        input[1024] = -0.0f;
        input[1025] = 0.0f;
        input[1026] = float.Epsilon;
        input[1027] = -float.Epsilon;
        input[1028] = float.MaxValue;
        input[1029] = float.MinValue;
        float[] exact = new float[input.Length];
        float[] ipp = new float[input.Length];
        exact[^1] = ipp[^1] = 123.25f;

        discriminator.Demodulate(input, exact);
        discriminator.Demodulate(input, ipp, useIpp: true);

        Assert.Equal(exact.Select(BitConverter.SingleToInt32Bits), ipp.Select(BitConverter.SingleToInt32Bits));
    }

    private static ParsedCommand Parse(string[] options)
        => new CommandLineParser().Parse(CliSpecs.Vhs,
            options.Contains("--preview-server")
                ? ["--pal", .. options, "capture.s16"]
                : ["--pal", .. options, "capture.s16", "output"]);
}
