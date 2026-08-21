using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.CudaFast;

namespace VHSDecode.Preview;

internal sealed class AutomaticCudaPreviewUnavailableException : Exception
{
    internal AutomaticCudaPreviewUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class PreviewBackendSelector
{
    private static readonly HashSet<string> AutomaticCudaOptions = new(
        StringComparer.Ordinal)
    {
        CliSpecs.DecodeAt20MspsDestination,
        "inputfreq",
        "length",
        "ntsc",
        "overwrite",
        "pal",
        "preview_crf",
        "preview_port",
        "preview_server",
        "start",
        "start_fileloc",
        "system",
        "tape_format",
        "tape_speed",
        "threads"
    };

    internal static async Task<T> SelectAsync<T>(
        ParsedCommand command,
        Func<DspBackend, CancellationToken, Task<T>> createBackend,
        TextWriter diagnosticOutput,
        CancellationToken cancellationToken,
        Func<CudaFastDriverProbeResult>? cudaPreflight = null,
        Func<bool>? ippProbe = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(createBackend);
        ArgumentNullException.ThrowIfNull(diagnosticOutput);

        if (command.GetSource("dsp_backend") != ParsedOptionSource.Default)
        {
            DspBackend explicitBackend = DspBackendParser.Parse(
                command.Get<string>("dsp_backend"));
            return await createBackend(explicitBackend, cancellationToken).ConfigureAwait(false);
        }

        if (!IsAutomaticCudaCandidate(command))
        {
            DspBackend cpuBackend = ResolveCpuBackend(ippProbe);
            return await createBackend(cpuBackend, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        CudaFastDriverProbeResult probe = (cudaPreflight
            ?? CudaFastRuntimeProvisioner.ProbeCuda13Driver)();
        if (!probe.IsAvailable)
        {
            DspBackend cpuBackend = ResolveCpuBackend(ippProbe);
            diagnosticOutput.WriteLine(
                $"CUDA-fast preview preflight unavailable ({probe.Diagnostic}); falling back to {DisplayName(cpuBackend)}.");
            return await createBackend(cpuBackend, cancellationToken).ConfigureAwait(false);
        }

        diagnosticOutput.WriteLine(
            $"CUDA-fast preview preflight passed ({probe.Diagnostic}); initializing CUDA, cuFFT, and NVENC.");
        try
        {
            return await createBackend(DspBackend.CudaFast, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AutomaticCudaPreviewUnavailableException ex)
        {
            DspBackend cpuBackend = ResolveCpuBackend(ippProbe);
            diagnosticOutput.WriteLine(
                $"CUDA-fast preview initialization was unavailable ({ex.Message}); falling back to {DisplayName(cpuBackend)}.");
            return await createBackend(cpuBackend, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsAutomaticCudaCandidate(ParsedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Spec.Name != "vhs"
            || command.GetSource("dsp_backend") != ParsedOptionSource.Default
            || command.Get<bool>("cxadc")
            || !string.Equals(
                command.Get<string>("tape_format"),
                "VHS",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach ((string destination, ParsedOptionSource source) in command.OptionSources)
        {
            if (source != ParsedOptionSource.Default
                && !AutomaticCudaOptions.Contains(destination))
            {
                return false;
            }
        }

        double inputSampleRateMhz = command.Values.TryGetValue(
                "inputfreq",
                out object? inputFrequency)
            && inputFrequency is double parsedInputFrequency
            && parsedInputFrequency > 0.0
                ? parsedInputFrequency
                : FrequencyParser.DddMHz;
        if (Math.Abs(inputSampleRateMhz - FrequencyParser.DddMHz) > 1e-9)
        {
            return false;
        }

        try
        {
            _ = CudaFastDecodeRunner.ResolveProfile(VideoSystemSelector.Select(command));
            _ = CudaFastDecodeRunner.ResolveTapeSpeed(command.Get<string>("tape_speed"));
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsIppAvailable()
        => IppRuntime.TryProbe(out _);

    private static DspBackend ResolveCpuBackend(Func<bool>? ippProbe)
        => (ippProbe ?? IsIppAvailable)()
            ? DspBackend.IppFast
            : DspBackend.Exact;

    private static string DisplayName(DspBackend backend)
        => backend == DspBackend.IppFast ? "IPP-fast" : "Exact";
}
