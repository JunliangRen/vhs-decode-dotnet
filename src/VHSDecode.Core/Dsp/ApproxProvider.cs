using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.Dsp;

public enum ApproxProvider
{
    Managed = 0,
    Ipp = 1
}

public enum ApproxResampler
{
    Sinc16 = 0,
    CatmullRom4 = 1
}

public enum ApproxPrecision
{
    Balanced = 0,
    Aggressive = 1
}

public static class ApproximationContract
{
    public const string VhsRfTransformFloat32V1 = "vhs-rf-transform-f32-v1";

    public const string VhsRfTransformFloat32CatmullRom4V2 =
        "vhs-rf-transform-f32-catmull-rom4-v2";

    public const string VhsRfTransformFieldFloat32CatmullRom4BurstIqPrefixV5 =
        "vhs-rf-transform-field-f32-catmull-rom4-burst-iq-prefix-v5";

    public const string VhsRfTransformFieldFloat32CatmullRom4BurstIqPrefixChromaFftV6 =
        "vhs-rf-transform-field-f32-catmull-rom4-burst-iq-prefix-chroma-fft-v6";

    public static string ForResampler(ApproxResampler resampler)
        => resampler switch
        {
            ApproxResampler.Sinc16 => VhsRfTransformFloat32V1,
            ApproxResampler.CatmullRom4 => VhsRfTransformFloat32CatmullRom4V2,
            _ => throw new ArgumentOutOfRangeException(nameof(resampler))
        };

    public static string ForSelection(
        ApproxResampler resampler,
        ApproxPrecision precision)
        => precision switch
        {
            ApproxPrecision.Balanced => ForResampler(resampler),
            ApproxPrecision.Aggressive when resampler == ApproxResampler.CatmullRom4
                => VhsRfTransformFieldFloat32CatmullRom4BurstIqPrefixChromaFftV6,
            ApproxPrecision.Aggressive => throw new NotSupportedException(
                "The aggressive Approx precision contract requires the catmull-rom4 resampler."),
            _ => throw new ArgumentOutOfRangeException(nameof(precision))
        };
}

public static class ApproxProviderParser
{
    public const string ManagedValue = "managed";
    public const string IppValue = "ipp";

    public static ApproxProvider Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Equals(ManagedValue, StringComparison.OrdinalIgnoreCase))
        {
            return ApproxProvider.Managed;
        }

        if (value.Equals(IppValue, StringComparison.OrdinalIgnoreCase))
        {
            return ApproxProvider.Ipp;
        }

        throw new ArgumentException(
            $"Unknown Approx provider '{value}'. Expected '{ManagedValue}' or '{IppValue}'.",
            nameof(value));
    }

    public static string ToCommandLineValue(ApproxProvider provider)
        => provider switch
        {
            ApproxProvider.Managed => ManagedValue,
            ApproxProvider.Ipp => IppValue,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
}

public static class ApproxResamplerParser
{
    public const string Sinc16Value = "sinc16";
    public const string CatmullRom4Value = "catmull-rom4";

    public static ApproxResampler Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Equals(Sinc16Value, StringComparison.OrdinalIgnoreCase))
        {
            return ApproxResampler.Sinc16;
        }

        if (value.Equals(CatmullRom4Value, StringComparison.OrdinalIgnoreCase))
        {
            return ApproxResampler.CatmullRom4;
        }

        throw new ArgumentException(
            $"Unknown Approx resampler '{value}'. Expected '{Sinc16Value}' or '{CatmullRom4Value}'.",
            nameof(value));
    }

    public static string ToCommandLineValue(ApproxResampler resampler)
        => resampler switch
        {
            ApproxResampler.Sinc16 => Sinc16Value,
            ApproxResampler.CatmullRom4 => CatmullRom4Value,
            _ => throw new ArgumentOutOfRangeException(nameof(resampler))
        };
}

public static class ApproxPrecisionParser
{
    public const string BalancedValue = "balanced";
    public const string AggressiveValue = "aggressive";

    public static ApproxPrecision Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Equals(BalancedValue, StringComparison.OrdinalIgnoreCase))
        {
            return ApproxPrecision.Balanced;
        }

        if (value.Equals(AggressiveValue, StringComparison.OrdinalIgnoreCase))
        {
            return ApproxPrecision.Aggressive;
        }

        throw new ArgumentException(
            $"Unknown Approx precision '{value}'. Expected '{BalancedValue}' or '{AggressiveValue}'.",
            nameof(value));
    }

    public static string ToCommandLineValue(ApproxPrecision precision)
        => precision switch
        {
            ApproxPrecision.Balanced => BalancedValue,
            ApproxPrecision.Aggressive => AggressiveValue,
            _ => throw new ArgumentOutOfRangeException(nameof(precision))
        };
}

internal static class ApproxResamplerResolver
{
    internal static ApproxResampler Resolve(
        string? requestedValue,
        bool isExplicit)
    {
        if (!isExplicit)
        {
            return ApproxResampler.Sinc16;
        }

        return ApproxResamplerParser.Parse(
            requestedValue
                ?? throw new ArgumentException(
                    "An explicit --approx-resampler value is required.",
                    nameof(requestedValue)));
    }

    internal static void EnsureOptionCompatible(
        DspBackend backend,
        bool approxResamplerIsExplicit)
    {
        if (approxResamplerIsExplicit && backend != DspBackend.ApproxFast)
        {
            throw new NotSupportedException(
                "--approx-resampler is valid only with '--dsp-backend approx-fast'; the option was not ignored.");
        }
    }
}

internal static class ApproxPrecisionResolver
{
    internal static ApproxPrecision Resolve(
        string? requestedValue,
        bool isExplicit)
    {
        if (!isExplicit)
        {
            return ApproxPrecision.Balanced;
        }

        return ApproxPrecisionParser.Parse(
            requestedValue
                ?? throw new ArgumentException(
                    "An explicit --approx-precision value is required.",
                    nameof(requestedValue)));
    }

    internal static void EnsureOptionCompatible(
        DspBackend backend,
        bool approxPrecisionIsExplicit)
    {
        if (approxPrecisionIsExplicit && backend != DspBackend.ApproxFast)
        {
            throw new NotSupportedException(
                "--approx-precision is valid only with '--dsp-backend approx-fast'; the option was not ignored.");
        }
    }

    internal static void EnsureSelectionSupported(
        ApproxResampler resampler,
        ApproxPrecision precision,
        UpstreamBehaviorProfile behaviorProfile)
    {
        if (precision != ApproxPrecision.Aggressive)
        {
            return;
        }

        if (resampler != ApproxResampler.CatmullRom4)
        {
            throw new NotSupportedException(
                "The aggressive Approx precision contract requires '--approx-resampler catmull-rom4'; no partial fallback was performed.");
        }

        if (behaviorProfile != UpstreamBehaviorProfile.Current)
        {
            throw new NotSupportedException(
                "The aggressive Approx precision contract requires '--compat-version current'; no partial fallback was performed.");
        }
    }
}

internal readonly record struct ApproxIppProbeResult(
    bool IsAvailable,
    string? Diagnostic)
{
    internal static ApproxIppProbeResult Available(string? diagnostic = null)
        => new(true, diagnostic);

    internal static ApproxIppProbeResult Unavailable(string diagnostic)
        => new(false, diagnostic);
}

internal readonly record struct ApproxProviderSelection(
    ApproxProvider Provider,
    bool IsExplicit,
    bool FellBackFromIpp,
    Architecture ProcessArchitecture,
    string? Diagnostic);

internal static class ApproxProviderResolver
{
    private const int VhsRfTransformLength = 32_768;

    internal static ApproxProviderSelection Resolve(
        string? requestedValue,
        bool isExplicit,
        Architecture processArchitecture,
        Func<ApproxIppProbeResult>? ippProbe = null)
    {
        ippProbe ??= ProbeIppRuntime;
        if (isExplicit)
        {
            ApproxProvider requested = ApproxProviderParser.Parse(
                requestedValue
                    ?? throw new ArgumentException(
                        "An explicit --approx-provider value is required.",
                        nameof(requestedValue)));
            if (requested == ApproxProvider.Managed)
            {
                return new ApproxProviderSelection(
                    ApproxProvider.Managed,
                    IsExplicit: true,
                    FellBackFromIpp: false,
                    processArchitecture,
                    Diagnostic: null);
            }

            ApproxIppProbeResult explicitProbe = ippProbe();
            if (!explicitProbe.IsAvailable)
            {
                throw new NotSupportedException(
                    "The explicit '--approx-provider ipp' selection is unavailable: "
                    + (explicitProbe.Diagnostic ?? "the Intel IPP runtime probe failed.")
                    + " Select '--approx-provider managed' or omit the option to allow automatic fallback.");
            }

            return new ApproxProviderSelection(
                ApproxProvider.Ipp,
                IsExplicit: true,
                FellBackFromIpp: false,
                processArchitecture,
                explicitProbe.Diagnostic);
        }

        if (processArchitecture is not (Architecture.X86 or Architecture.X64))
        {
            return new ApproxProviderSelection(
                ApproxProvider.Managed,
                IsExplicit: false,
                FellBackFromIpp: false,
                processArchitecture,
                $"Intel IPP automatic selection is not attempted on {processArchitecture}.");
        }

        ApproxIppProbeResult automaticProbe = ippProbe();
        if (automaticProbe.IsAvailable)
        {
            return new ApproxProviderSelection(
                ApproxProvider.Ipp,
                IsExplicit: false,
                FellBackFromIpp: false,
                processArchitecture,
                automaticProbe.Diagnostic);
        }

        return new ApproxProviderSelection(
            ApproxProvider.Managed,
            IsExplicit: false,
            FellBackFromIpp: true,
            processArchitecture,
            automaticProbe.Diagnostic
                ?? "the Intel IPP runtime probe failed.");
    }

    internal static void EnsureOptionCompatible(
        DspBackend backend,
        bool approxProviderIsExplicit)
    {
        if (approxProviderIsExplicit && backend != DspBackend.ApproxFast)
        {
            throw new NotSupportedException(
                "--approx-provider is valid only with '--dsp-backend approx-fast'; the option was not ignored.");
        }
    }

    private static ApproxIppProbeResult ProbeIppRuntime()
    {
        try
        {
            IppRuntimeInfo info = IppRuntime.ProbeRequired();
            using var capabilityProbe = new IppRealFft32(VhsRfTransformLength);
            return ApproxIppProbeResult.Available(
                $"Intel IPP {info.IppVersion}, target {info.IppTargetCpu}");
        }
        catch (IppBackendUnavailableException exception)
        {
            return ApproxIppProbeResult.Unavailable(exception.Detail);
        }
        catch (IppNativeException exception)
        {
            return ApproxIppProbeResult.Unavailable(
                $"the Approx float32 FFT capability probe failed: {exception.Message}");
        }
    }
}
