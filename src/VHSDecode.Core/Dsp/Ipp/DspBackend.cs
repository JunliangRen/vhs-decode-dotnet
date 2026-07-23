namespace VHSDecode.Core.Dsp;

public enum DspBackend
{
    Exact = 0,
    IppFast = 1
}

public static class DspBackendParser
{
    public const string ExactValue = "exact";
    public const string IppFastValue = "ipp-fast";

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

        throw new ArgumentException(
            $"Unknown DSP backend '{value}'. Expected '{ExactValue}' or '{IppFastValue}'.",
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
        }

        backend = default;
        return false;
    }

    public static string ToCommandLineValue(DspBackend backend)
        => backend switch
        {
            DspBackend.Exact => ExactValue,
            DspBackend.IppFast => IppFastValue,
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
            && !commandName.Equals("vhs", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The explicit '{DspBackendParser.IppFastValue}' DSP backend does not yet contain accelerated kernels for the '{commandName}' command. "
                + "Use '--dsp-backend exact'; no silent Exact fallback was performed.");
        }
    }
}
