using VHSDecode.Core.Compute.Cuda;

namespace VHSDecode.Core.Decode;

internal readonly record struct RfComputeBackendSelection(
    IRfBlockComputeBackend? Backend,
    DecodeInitializationDiagnostic Diagnostic);

/// <summary>
/// Selects the RF compute backend before any decode output is opened. A CUDA
/// backend returned from this method owns its native context; callers must
/// either transfer it to <see cref="RfBlockStreamDecoder"/> or dispose it.
/// </summary>
internal static class RfComputeBackendSelector
{
    // Release policy: this is promoted only after representative VHS and LD
    // captures both demonstrate at least 1.25x end-to-end speedup while passing
    // the full field/output compatibility gates, including CPU recomputation
    // for threshold-guarded decisions. Explicit --compute-backend cuda remains
    // available for validation before that promotion.
    internal const bool AutomaticCudaPromotionEnabled = false;
    internal const string AutomaticCpuDiagnostic =
        "RF compute backend selected: cpu (automatic CUDA promotion is disabled until the 1.25x end-to-end compatibility release gate is validated).";

    internal static RfComputeBackendSelection Select(
        DecodeExecutionOptions options,
        RfBlockDecodePipeline pipeline,
        int blockLength,
        CudaBackendProbe? probe = null,
        bool automaticCudaPromotionEnabled = AutomaticCudaPromotionEnabled)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pipeline);

        if (options.ComputeBackend == RfComputeBackend.Cpu)
        {
            return Cpu("RF compute backend selected: cpu (explicit request).");
        }

        if (options.ComputeBackend == RfComputeBackend.Auto
            && !automaticCudaPromotionEnabled)
        {
            return Cpu(AutomaticCpuDiagnostic);
        }

        probe ??= new CudaBackendProbe();
        using CudaBackendProbeResult probeResult = probe.Probe(
            CudaBackendProbeRequest.ForBackend(
                options.ComputeBackend,
                options.CudaDevice,
                blockLength));

        if (!probeResult.IsReady)
        {
            if (probeResult.ShouldFallbackToCpu)
            {
                return Cpu(
                    $"RF compute backend selected: cpu (CUDA preflight failed: {probeResult.Message})");
            }

            throw new NotSupportedException(
                $"CUDA RF backend was requested but preflight failed: {probeResult.Message}");
        }

        CudaBackendContext context = probeResult.TakeContext();
        try
        {
            if (!CudaRfBlockComputeBackend.TryCreate(
                    context,
                    pipeline,
                    out CudaRfBlockComputeBackend? backend,
                    out string unsupportedReason))
            {
                if (options.ComputeBackend == RfComputeBackend.Auto)
                {
                    context.Dispose();
                    return Cpu(
                        $"RF compute backend selected: cpu (CUDA does not support this decode configuration: {unsupportedReason})");
                }

                throw new NotSupportedException(
                    $"CUDA RF backend does not support this decode configuration: {unsupportedReason}");
            }

            return new RfComputeBackendSelection(
                backend,
                new DecodeInitializationDiagnostic(
                    "INFO",
                    $"RF compute backend selected: cuda device {context.Device.Ordinal} ({context.Device.Name})."));
        }
        catch
        {
            // TryCreate transfers ownership only on success. Dispose here for
            // every unsupported or unexpected startup failure.
            context.Dispose();
            throw;
        }
    }

    private static RfComputeBackendSelection Cpu(string message) =>
        new(null, new DecodeInitializationDiagnostic("INFO", message));
}
