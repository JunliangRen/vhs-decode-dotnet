namespace VHSDecode.Core.Dsp;

public enum DspBackend
{
    Exact = 0,
    IppFast = 1,
    CudaFast = 2,
    ApproxFast = 3
}

public static class DspBackendParser
{
    public const string ExactValue = "exact";
    public const string IppFastValue = "ipp-fast";
    public const string CudaFastValue = "cuda-fast";
    public const string ApproxFastValue = "approx-fast";

    public static DspBackend Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Equals(ExactValue, StringComparison.OrdinalIgnoreCase))
        {
            return DspBackend.Exact;
        }

        if (value.Equals(IppFastValue, StringComparison.OrdinalIgnoreCase))
        {
            return DspBackend.IppFast;
        }

        if (value.Equals(CudaFastValue, StringComparison.OrdinalIgnoreCase))
        {
            return DspBackend.CudaFast;
        }

        if (value.Equals(ApproxFastValue, StringComparison.OrdinalIgnoreCase))
        {
            return DspBackend.ApproxFast;
        }

        throw new ArgumentException(
            $"Unknown DSP backend '{value}'. Expected '{ExactValue}', '{IppFastValue}', '{CudaFastValue}', or '{ApproxFastValue}'.",
            nameof(value));
    }

    public static bool TryParse(string? value, out DspBackend backend)
    {
        if (value is not null)
        {
            if (value.Equals(ExactValue, StringComparison.OrdinalIgnoreCase))
            {
                backend = DspBackend.Exact;
                return true;
            }

            if (value.Equals(IppFastValue, StringComparison.OrdinalIgnoreCase))
            {
                backend = DspBackend.IppFast;
                return true;
            }

            if (value.Equals(CudaFastValue, StringComparison.OrdinalIgnoreCase))
            {
                backend = DspBackend.CudaFast;
                return true;
            }

            if (value.Equals(ApproxFastValue, StringComparison.OrdinalIgnoreCase))
            {
                backend = DspBackend.ApproxFast;
                return true;
            }
        }

        backend = default;
        return false;
    }

    public static string ToCommandLineValue(DspBackend backend)
        => backend switch
        {
            DspBackend.Exact => ExactValue,
            DspBackend.IppFast => IppFastValue,
            DspBackend.CudaFast => CudaFastValue,
            DspBackend.ApproxFast => ApproxFastValue,
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };
}

public static class DspBackendSupport
{
    public static void EnsureCommandSupported(DspBackend backend, string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend));
        }

        if (backend == DspBackend.IppFast
            && !commandName.Equals("vhs", StringComparison.Ordinal)
            && !commandName.Equals("ld", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The explicit '{DspBackendParser.IppFastValue}' DSP backend does not yet contain accelerated kernels for the '{commandName}' command. "
                + "Use '--dsp-backend exact'; no silent Exact fallback was performed.");
        }

        if (backend == DspBackend.CudaFast
            && !commandName.Equals("vhs", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The explicit '{DspBackendParser.CudaFastValue}' DSP backend currently supports only the 'vhs' command, not '{commandName}'. "
                + "Use '--dsp-backend exact' explicitly if CPU Exact decoding is required; no silent fallback was performed.");
        }

        if (backend == DspBackend.ApproxFast
            && !commandName.Equals("vhs", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The experimental '{DspBackendParser.ApproxFastValue}' DSP backend currently supports only the 'vhs' command, not '{commandName}'. "
                + "Use '--dsp-backend exact' explicitly for other decoders; no silent Exact fallback was performed.");
        }
    }
}

public static class DspBackendKernelPolicy
{
    public static bool UsesIpp(
        DspBackend backend,
        ApproxProvider? approxProvider = null)
        => backend == DspBackend.IppFast
            || (backend == DspBackend.ApproxFast
                && approxProvider == ApproxProvider.Ipp);
}
