using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppLinuxLoaderTests
{
    [Fact(DisplayName = "Required IPP test runs load and initialize the real native runtime")]
    public void RequiredRunsInitializeRealRuntime()
    {
        if (!IppTestRuntime.IsRequired)
        {
            return;
        }

        IppRuntimeInfo info = IppRuntime.ProbeRequired();
        Assert.Equal(0x0001_0004U, info.AbiVersion);
        Assert.NotEmpty(info.IppVersion);
        using var fft = new IppRealFft(8);
        Assert.Equal(5, fft.SpectrumLength);
    }

    [Theory(DisplayName = "Linux ELF IPP loader distinguishes export, ABI, layout, and initialization failures")]
    [InlineData("missing_export", IppBackendFailureKind.MissingExport, "vhsdecode_ipp_fft32_create")]
    [InlineData("wrong_abi", IppBackendFailureKind.AbiVersionMismatch, "0x00010005")]
    [InlineData("wrong_info_abi", IppBackendFailureKind.AbiVersionMismatch, "runtime-info reports ABI")]
    [InlineData("wrong_size", IppBackendFailureKind.AbiVersionMismatch, "239")]
    [InlineData("runtime_rejected", IppBackendFailureKind.RuntimeRejected, "fixture runtime rejected")]
    [InlineData("init_rejected", IppBackendFailureKind.RuntimeRejected, "initialization failed")]
    public void LoaderReportsSpecificFailure(string fixture, IppBackendFailureKind kind, string diagnostic)
    {
        RequireLinux();
        IppBackendUnavailableException error = Assert.Throws<IppBackendUnavailableException>(
            () => IppRuntime.ProbeRequiredForLibraryName(FixturePath(fixture)));
        Assert.Equal(kind, error.FailureKind);
        Assert.Contains(diagnostic, error.Detail, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Linux IPP probes accept success and nonfatal CPU warnings without poisoning the real probe")]
    [InlineData("valid")]
    [InlineData("warning")]
    public void LoaderAcceptsValidRuntimeAndWarnings(string fixture)
    {
        RequireLinux();
        IppRuntimeInfo info = IppRuntime.ProbeRequiredForLibraryName(FixturePath(fixture));
        Assert.Equal("loader-fixture", info.IppName);
        Assert.Equal(0x0001_0004U, info.AbiVersion);
        if (IppTestRuntime.IsRequired)
        {
            Assert.NotEqual(info.IppName, IppRuntime.ProbeRequired().IppName);
        }
    }

    [Fact(DisplayName = "An invalid Linux ELF IPP library produces a controlled loader failure")]
    public void InvalidLibraryFailsWithoutNativeCrash()
    {
        RequireLinux();
        string path = Path.Combine(Path.GetTempPath(), $"vhsdecode-invalid-ipp-{Guid.NewGuid():N}.so");
        try
        {
            File.WriteAllText(path, "This is not an ELF library.\r\n");
            IppBackendUnavailableException error = Assert.Throws<IppBackendUnavailableException>(
                () => IppRuntime.ProbeRequiredForLibraryName(path));
            Assert.Equal(IppBackendFailureKind.NativeLibraryNotFound, error.FailureKind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("ELF loader fixtures execute on Linux.");
        }
    }

    private static string FixturePath(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ipp-probe-fixtures", $"libipp_probe_{name}.so");
        Assert.True(File.Exists(path), $"Missing Linux loader fixture: {path}");
        return path;
    }
}
