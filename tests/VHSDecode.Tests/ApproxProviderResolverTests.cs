using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ApproxProviderResolverTests
{
    public static TheoryData<Architecture> X86Architectures => new()
    {
        Architecture.X86,
        Architecture.X64
    };

    public static TheoryData<Architecture> ArmArchitectures => new()
    {
        Architecture.Arm,
        Architecture.Arm64
    };

    public static TheoryData<Architecture> SupportedProcessArchitectures => new()
    {
        Architecture.X86,
        Architecture.X64,
        Architecture.Arm,
        Architecture.Arm64
    };

    [Theory(DisplayName = "Automatic Approx selection prefers IPP on x86 when its probe succeeds")]
    [MemberData(nameof(X86Architectures))]
    public void AutomaticX86SelectionPrefersAvailableIpp(Architecture architecture)
    {
        int probeCount = 0;

        ApproxProviderSelection selection = ApproxProviderResolver.Resolve(
            requestedValue: null,
            isExplicit: false,
            architecture,
            () =>
            {
                probeCount++;
                return ApproxIppProbeResult.Available("IPP probe succeeded");
            });

        Assert.Equal(1, probeCount);
        Assert.Equal(ApproxProvider.Ipp, selection.Provider);
        Assert.False(selection.IsExplicit);
        Assert.False(selection.FellBackFromIpp);
        Assert.Equal(architecture, selection.ProcessArchitecture);
        Assert.Equal("IPP probe succeeded", selection.Diagnostic);
    }

    [Theory(DisplayName = "Automatic Approx selection falls back to managed on x86 when IPP is unavailable")]
    [MemberData(nameof(X86Architectures))]
    public void AutomaticX86SelectionFallsBackToManaged(Architecture architecture)
    {
        int probeCount = 0;

        ApproxProviderSelection selection = ApproxProviderResolver.Resolve(
            requestedValue: null,
            isExplicit: false,
            architecture,
            () =>
            {
                probeCount++;
                return ApproxIppProbeResult.Unavailable("IPP runtime missing");
            });

        Assert.Equal(1, probeCount);
        Assert.Equal(ApproxProvider.Managed, selection.Provider);
        Assert.False(selection.IsExplicit);
        Assert.True(selection.FellBackFromIpp);
        Assert.Equal(architecture, selection.ProcessArchitecture);
        Assert.Equal("IPP runtime missing", selection.Diagnostic);
    }

    [Theory(DisplayName = "Automatic Approx selection uses managed on ARM without probing IPP")]
    [MemberData(nameof(ArmArchitectures))]
    public void AutomaticArmSelectionUsesManagedWithoutProbe(Architecture architecture)
    {
        int probeCount = 0;

        ApproxProviderSelection selection = ApproxProviderResolver.Resolve(
            requestedValue: null,
            isExplicit: false,
            architecture,
            () =>
            {
                probeCount++;
                return ApproxIppProbeResult.Available();
            });

        Assert.Equal(0, probeCount);
        Assert.Equal(ApproxProvider.Managed, selection.Provider);
        Assert.False(selection.IsExplicit);
        Assert.False(selection.FellBackFromIpp);
        Assert.Equal(architecture, selection.ProcessArchitecture);
        Assert.Contains(architecture.ToString(), selection.Diagnostic, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Explicit managed selection never probes IPP")]
    [MemberData(nameof(SupportedProcessArchitectures))]
    public void ExplicitManagedNeverProbesIpp(Architecture architecture)
    {
        int probeCount = 0;

        ApproxProviderSelection selection = ApproxProviderResolver.Resolve(
            requestedValue: "MANAGED",
            isExplicit: true,
            architecture,
            () =>
            {
                probeCount++;
                return ApproxIppProbeResult.Available();
            });

        Assert.Equal(0, probeCount);
        Assert.Equal(ApproxProvider.Managed, selection.Provider);
        Assert.True(selection.IsExplicit);
        Assert.False(selection.FellBackFromIpp);
        Assert.Null(selection.Diagnostic);
    }

    [Theory(DisplayName = "Explicit IPP selection is honored when the probe succeeds")]
    [MemberData(nameof(X86Architectures))]
    public void ExplicitIppUsesAvailableIpp(Architecture architecture)
    {
        int probeCount = 0;

        ApproxProviderSelection selection = ApproxProviderResolver.Resolve(
            requestedValue: "IPP",
            isExplicit: true,
            architecture,
            () =>
            {
                probeCount++;
                return ApproxIppProbeResult.Available("IPP is ready");
            });

        Assert.Equal(1, probeCount);
        Assert.Equal(ApproxProvider.Ipp, selection.Provider);
        Assert.True(selection.IsExplicit);
        Assert.False(selection.FellBackFromIpp);
        Assert.Equal("IPP is ready", selection.Diagnostic);
    }

    [Theory(DisplayName = "Explicit IPP selection fails closed when the probe fails")]
    [MemberData(nameof(SupportedProcessArchitectures))]
    public void ExplicitIppFailsClosedWhenUnavailable(Architecture architecture)
    {
        int probeCount = 0;

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => ApproxProviderResolver.Resolve(
                requestedValue: "ipp",
                isExplicit: true,
                architecture,
                () =>
                {
                    probeCount++;
                    return ApproxIppProbeResult.Unavailable("IPP cannot load");
                }));

        Assert.Equal(1, probeCount);
        Assert.Contains("explicit '--approx-provider ipp' selection is unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("IPP cannot load", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--approx-provider managed", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Explicit provider is rejected for every non-Approx backend")]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    [InlineData(DspBackend.CudaFast)]
    public void ExplicitProviderRequiresApproxBackend(DspBackend backend)
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => ApproxProviderResolver.EnsureOptionCompatible(
                backend,
                approxProviderIsExplicit: true));

        Assert.Contains("valid only with '--dsp-backend approx-fast'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("was not ignored", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Omitted provider remains compatible with every backend")]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    [InlineData(DspBackend.CudaFast)]
    [InlineData(DspBackend.ApproxFast)]
    public void OmittedProviderDoesNotChangeBackendBehavior(DspBackend backend)
        => ApproxProviderResolver.EnsureOptionCompatible(
            backend,
            approxProviderIsExplicit: false);

    [Fact(DisplayName = "Explicit Approx provider rejects unknown and missing values")]
    public void ExplicitProviderRequiresKnownValue()
    {
        ArgumentException unknown = Assert.Throws<ArgumentException>(
            () => ApproxProviderResolver.Resolve(
                requestedValue: "auto",
                isExplicit: true,
                Architecture.X64,
                () => ApproxIppProbeResult.Available()));
        ArgumentException missing = Assert.Throws<ArgumentException>(
            () => ApproxProviderResolver.Resolve(
                requestedValue: null,
                isExplicit: true,
                Architecture.X64,
                () => ApproxIppProbeResult.Available()));

        Assert.Contains("Unknown Approx provider 'auto'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("explicit --approx-provider value is required", missing.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Omitted Approx resampler resolves to the v1 sinc16 contract")]
    public void OmittedResamplerResolvesToSinc16()
    {
        ApproxResampler resampler = ApproxResamplerResolver.Resolve(
            requestedValue: null,
            isExplicit: false);

        Assert.Equal(ApproxResampler.Sinc16, resampler);
        Assert.Equal(
            ApproximationContract.VhsRfTransformFloat32V1,
            ApproximationContract.ForResampler(resampler));
    }

    [Theory(DisplayName = "Explicit Approx resampler resolves supported values case-insensitively")]
    [InlineData("SINC16", ApproxResampler.Sinc16)]
    [InlineData("CATMULL-ROM4", ApproxResampler.CatmullRom4)]
    public void ExplicitResamplerResolvesSupportedValue(
        string value,
        ApproxResampler expected)
    {
        ApproxResampler resolved = ApproxResamplerResolver.Resolve(
            value,
            isExplicit: true);

        Assert.Equal(expected, resolved);
    }

    [Fact(DisplayName = "Catmull-Rom resampler selects the v2 approximation contract")]
    public void CatmullRomResamplerSelectsV2Contract()
    {
        Assert.Equal(
            ApproximationContract.VhsRfTransformFloat32CatmullRom4V2,
            ApproximationContract.ForResampler(ApproxResampler.CatmullRom4));
        Assert.Equal(
            "catmull-rom4",
            ApproxResamplerParser.ToCommandLineValue(ApproxResampler.CatmullRom4));
    }

    [Theory(DisplayName = "Explicit resampler is rejected for every non-Approx backend")]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    [InlineData(DspBackend.CudaFast)]
    public void ExplicitResamplerRequiresApproxBackend(DspBackend backend)
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => ApproxResamplerResolver.EnsureOptionCompatible(
                backend,
                approxResamplerIsExplicit: true));

        Assert.Contains("valid only with '--dsp-backend approx-fast'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("was not ignored", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Omitted resampler remains compatible with every backend")]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    [InlineData(DspBackend.CudaFast)]
    [InlineData(DspBackend.ApproxFast)]
    public void OmittedResamplerDoesNotChangeBackendBehavior(DspBackend backend)
        => ApproxResamplerResolver.EnsureOptionCompatible(
            backend,
            approxResamplerIsExplicit: false);

    [Fact(DisplayName = "Explicit Approx resampler rejects unknown and missing values")]
    public void ExplicitResamplerRequiresKnownValue()
    {
        ArgumentException unknown = Assert.Throws<ArgumentException>(
            () => ApproxResamplerResolver.Resolve(
                requestedValue: "catmull",
                isExplicit: true));
        ArgumentException missing = Assert.Throws<ArgumentException>(
            () => ApproxResamplerResolver.Resolve(
                requestedValue: null,
                isExplicit: true));

        Assert.Contains("Unknown Approx resampler 'catmull'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("explicit --approx-resampler value is required", missing.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Omitted Approx precision resolves to the balanced contract")]
    public void OmittedPrecisionResolvesToBalanced()
    {
        ApproxPrecision precision = ApproxPrecisionResolver.Resolve(
            requestedValue: null,
            isExplicit: false);

        Assert.Equal(ApproxPrecision.Balanced, precision);
        Assert.Equal(
            ApproximationContract.VhsRfTransformFloat32CatmullRom4V2,
            ApproximationContract.ForSelection(
                ApproxResampler.CatmullRom4,
                precision));
    }

    [Theory(DisplayName = "Explicit Approx precision resolves supported values case-insensitively")]
    [InlineData("BALANCED", ApproxPrecision.Balanced)]
    [InlineData("AGGRESSIVE", ApproxPrecision.Aggressive)]
    public void ExplicitPrecisionResolvesSupportedValue(
        string value,
        ApproxPrecision expected)
    {
        Assert.Equal(
            expected,
            ApproxPrecisionResolver.Resolve(value, isExplicit: true));
    }

    [Fact(DisplayName = "Aggressive precision selects the v6 frequency-domain chroma contract")]
    public void AggressivePrecisionSelectsV6Contract()
    {
        Assert.Equal(
            "vhs-rf-transform-field-f32-catmull-rom4-burst-iq-prefix-chroma-fft-v6",
            ApproximationContract.VhsRfTransformFieldFloat32CatmullRom4BurstIqPrefixChromaFftV6);
        Assert.Equal(
            ApproximationContract.VhsRfTransformFieldFloat32CatmullRom4BurstIqPrefixChromaFftV6,
            ApproximationContract.ForSelection(
                ApproxResampler.CatmullRom4,
                ApproxPrecision.Aggressive));
        Assert.Equal(
            "aggressive",
            ApproxPrecisionParser.ToCommandLineValue(ApproxPrecision.Aggressive));
    }

    [Theory(DisplayName = "Explicit precision is rejected for every non-Approx backend")]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    [InlineData(DspBackend.CudaFast)]
    public void ExplicitPrecisionRequiresApproxBackend(DspBackend backend)
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => ApproxPrecisionResolver.EnsureOptionCompatible(
                backend,
                approxPrecisionIsExplicit: true));

        Assert.Contains("valid only with '--dsp-backend approx-fast'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("was not ignored", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Aggressive precision fails closed without Catmull-Rom and current behavior")]
    public void AggressivePrecisionRequiresSupportedSelection()
    {
        NotSupportedException resampler = Assert.Throws<NotSupportedException>(
            () => ApproxPrecisionResolver.EnsureSelectionSupported(
                ApproxResampler.Sinc16,
                ApproxPrecision.Aggressive,
                UpstreamBehaviorProfile.Current));
        NotSupportedException behavior = Assert.Throws<NotSupportedException>(
            () => ApproxPrecisionResolver.EnsureSelectionSupported(
                ApproxResampler.CatmullRom4,
                ApproxPrecision.Aggressive,
                UpstreamBehaviorProfile.V040));

        Assert.Contains("--approx-resampler catmull-rom4", resampler.Message, StringComparison.Ordinal);
        Assert.Contains("--compat-version current", behavior.Message, StringComparison.Ordinal);
        Assert.Contains("no partial fallback", resampler.Message, StringComparison.Ordinal);
        Assert.Contains("no partial fallback", behavior.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Explicit Approx precision rejects unknown and missing values")]
    public void ExplicitPrecisionRequiresKnownValue()
    {
        ArgumentException unknown = Assert.Throws<ArgumentException>(
            () => ApproxPrecisionResolver.Resolve(
                requestedValue: "fast",
                isExplicit: true));
        ArgumentException missing = Assert.Throws<ArgumentException>(
            () => ApproxPrecisionResolver.Resolve(
                requestedValue: null,
                isExplicit: true));

        Assert.Contains("Unknown Approx precision 'fast'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("explicit --approx-precision value is required", missing.Message, StringComparison.Ordinal);
    }
}
