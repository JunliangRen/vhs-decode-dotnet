using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp.CudaFast;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CudaFastRuntimeProvisionerTests
{
    [Fact(DisplayName = "CUDA-fast pins the official NVIDIA cuFFT redistributable")]
    public void PinnedPackageMatchesOfficialNvidiaManifest()
    {
        CudaFastRuntimePackage package = CudaFastRuntimeProvisioner.PinnedPackage;

        Assert.Equal("12.0.0.15", package.Version);
        Assert.Equal(
            "https://developer.download.nvidia.com/compute/cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-12.0.0.15-archive.zip",
            package.DownloadUri.AbsoluteUri);
        Assert.Equal(212_026_240, package.ArchiveSizeBytes);
        Assert.Equal(
            "4F0E0C019E1B53166BDEAF52C91C333450AE9DD05FBADC77CFFB70A702501B67",
            package.ArchiveSha256);
        Assert.Equal(284_331_040, package.LibrarySizeBytes);
        Assert.Equal(
            "611BA7E40DFAB64B9B5BD35F4AD3593E00A8E93785FBF53160D9398AACD5AC14",
            package.LibrarySha256);
    }

    [Fact(DisplayName = "CUDA-fast searches explicit and installed runtimes before its cache")]
    public void CandidatePathsPreferExplicitAndInstalledRuntimesBeforeCache()
    {
        string root = Path.Combine(Path.GetTempPath(), "cuda-search-" + Guid.NewGuid().ToString("N"));
        string explicitRuntime = Path.Combine(root, "explicit-runtime");
        string bridgePath = Path.Combine(root, "bridge", CudaFastNativeRuntime.NativeLibraryName);
        string app = Path.Combine(root, "app");
        string assembly = Path.Combine(root, "assembly");
        string cuda13 = Path.Combine(root, "cuda-13-env");
        string cuda = Path.Combine(root, "cuda-env");
        string toolkit = Path.Combine(root, "toolkit");
        string pathDirectory = Path.Combine(root, "path");
        string cache = Path.Combine(root, "cache", CudaFastRuntimeProvisioner.CuFftLibraryName);
        var environment = new CudaFastRuntimeSearchEnvironment(
            app,
            assembly,
            bridgePath,
            explicitRuntime,
            cuda13,
            cuda,
            [toolkit],
            [pathDirectory],
            cache);

        IReadOnlyList<string> paths = CudaFastRuntimeProvisioner.BuildCandidatePaths(environment);

        string[] expected =
        [
            Path.Combine(explicitRuntime, CudaFastRuntimeProvisioner.CuFftLibraryName),
            Path.Combine(Path.GetDirectoryName(bridgePath)!, CudaFastRuntimeProvisioner.CuFftLibraryName),
            Path.Combine(app, CudaFastRuntimeProvisioner.CuFftLibraryName),
            Path.Combine(assembly, CudaFastRuntimeProvisioner.CuFftLibraryName),
            Path.Combine(cuda13, "bin", "x64", CudaFastRuntimeProvisioner.CuFftLibraryName),
            Path.Combine(cuda, "bin", "x64", CudaFastRuntimeProvisioner.CuFftLibraryName),
            Path.Combine(toolkit, "bin", "x64", CudaFastRuntimeProvisioner.CuFftLibraryName),
            Path.Combine(pathDirectory, CudaFastRuntimeProvisioner.CuFftLibraryName),
            cache
        ];
        Assert.Equal(expected.Select(Path.GetFullPath), paths);
    }

    [Fact(DisplayName = "CUDA-fast auto-download recognizes explicit opt-out values")]
    public void AutoDownloadRecognizesOptOutValues()
    {
        Assert.True(CudaFastRuntimeProvisioner.IsAutoDownloadEnabled(null));
        Assert.True(CudaFastRuntimeProvisioner.IsAutoDownloadEnabled("1"));
        Assert.True(CudaFastRuntimeProvisioner.IsAutoDownloadEnabled("true"));
        Assert.False(CudaFastRuntimeProvisioner.IsAutoDownloadEnabled("0"));
        Assert.False(CudaFastRuntimeProvisioner.IsAutoDownloadEnabled("FALSE"));
        Assert.False(CudaFastRuntimeProvisioner.IsAutoDownloadEnabled("No"));
        Assert.False(CudaFastRuntimeProvisioner.IsAutoDownloadEnabled("off"));
    }

    [Theory(DisplayName = "CUDA preview preflight enforces driver, device, and compute capability")]
    [InlineData(12_900, 1, 8, 9, false, "CUDA 13")]
    [InlineData(13_000, 0, 8, 9, false, "no CUDA devices")]
    [InlineData(13_000, 1, 7, 4, false, "requires 7.5")]
    [InlineData(13_000, 1, 7, 5, true, "compute capability 7.5")]
    [InlineData(13_100, 2, 8, 9, true, "compute capability 8.9")]
    public void DriverCapabilityEvaluationRejectsUnsupportedDevices(
        int driverVersion,
        int deviceCount,
        int computeMajor,
        int computeMinor,
        bool expectedAvailable,
        string expectedDiagnostic)
    {
        CudaFastDriverProbeResult result =
            CudaFastRuntimeProvisioner.EvaluateCudaDriverCapabilities(
                driverVersion,
                deviceCount,
                computeMajor,
                computeMinor);

        Assert.Equal(expectedAvailable, result.IsAvailable);
        Assert.Contains(expectedDiagnostic, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Release publishing excludes cuFFT and enforces a lean size gate")]
    public void ReleasePublishingExcludesCuFft()
    {
        string repository = RepositoryRoot();
        string project = File.ReadAllText(
            Path.Combine(repository, "src", "VHSDecode.Core", "VHSDecode.Core.csproj"));
        string workflow = File.ReadAllText(
            Path.Combine(repository, ".github", "workflows", "release-build.yml"));
        string buildScript = File.ReadAllText(
            Path.Combine(repository, "tools", "build-cuda-fast-native.ps1"));

        Assert.DoesNotContain(
            "artifacts\\native\\Release\\win-x64\\cufft64_12.dll",
            project,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Verify lean CUDA runtime packaging", workflow, StringComparison.Ordinal);
        Assert.Contains("$maximumLeanBytes = 200MB", workflow, StringComparison.Ordinal);
        Assert.Contains("cuFFT is intentionally not staged", buildScript, StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item -LiteralPath $staleCuFftArtifactPath -Force",
            buildScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Destination (Join-Path $artifactDirectoryFullPath $cuFftName)",
            buildScript,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast reuses a verified cached cuFFT without network access")]
    public async Task VerifiedCacheAvoidsDriverProbeAndDownload()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] library = CreateLibraryBytes();
            CudaFastRuntimePackage package = CreatePackage([1], library);
            var handler = new BytesHandler([1]);
            int driverProbeCount = 0;
            using var client = new HttpClient(handler);
            var provisioner = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () =>
                {
                    driverProbeCount++;
                    return new(true, "test driver");
                });
            await File.WriteAllBytesAsync(
                provisioner.CacheLibraryPath,
                library,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                provisioner.CacheLicensePath,
                "test license",
                TestContext.Current.CancellationToken);

            string result = await provisioner.EnsureDownloadedAsync(
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(provisioner.CacheLibraryPath, result);
            Assert.Equal(0, handler.RequestCount);
            Assert.Equal(0, driverProbeCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast downloads validates and atomically installs cuFFT")]
    public async Task DownloadValidatesAndInstallsPinnedLibrary()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] library = CreateLibraryBytes();
            byte[] archive = CreateArchive(library);
            CudaFastRuntimePackage package = CreatePackage(archive, library);
            var handler = new BytesHandler(archive);
            using var client = new HttpClient(handler);
            var provisioner = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () => new(true, "test driver"));
            var output = new StringWriter();

            string result = await provisioner.EnsureDownloadedAsync(
                output,
                CancellationToken.None);

            Assert.Equal(provisioner.CacheLibraryPath, result);
            Assert.Equal(
                library,
                await File.ReadAllBytesAsync(result, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(directory, "install.json")));
            Assert.Equal(
                "test NVIDIA license",
                await File.ReadAllTextAsync(
                    provisioner.CacheLicensePath,
                    TestContext.Current.CancellationToken));
            Assert.Equal(1, handler.RequestCount);
            Assert.Contains("Downloading", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("100%", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("installed", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.download"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast rejects a cuFFT archive with the wrong SHA-256")]
    public async Task WrongArchiveHashIsRejectedWithoutPublishingLibrary()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] library = CreateLibraryBytes();
            byte[] archive = CreateArchive(library);
            CudaFastRuntimePackage package = CreatePackage(
                archive,
                library,
                archiveSha256: new string('0', 64));
            using var client = new HttpClient(new BytesHandler(archive));
            var provisioner = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () => new(true, "test driver"));

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => provisioner.EnsureDownloadedAsync(TextWriter.Null, CancellationToken.None));

            Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(provisioner.CacheLibraryPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast rejects an archive without exactly one cuFFT runtime")]
    public async Task ArchiveWithoutCuFftIsRejected()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] library = CreateLibraryBytes();
            byte[] archive = CreateArchive(library, "bin/not-cufft.dll");
            CudaFastRuntimePackage package = CreatePackage(archive, library);
            using var client = new HttpClient(new BytesHandler(archive));
            var provisioner = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () => new(true, "test driver"));

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => provisioner.EnsureDownloadedAsync(TextWriter.Null, CancellationToken.None));

            Assert.Contains("contained 0", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(provisioner.CacheLibraryPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast rejects an extracted cuFFT runtime with the wrong hash")]
    public async Task WrongLibraryHashIsRejectedWithoutPublishingLibrary()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] library = CreateLibraryBytes();
            byte[] archive = CreateArchive(library);
            CudaFastRuntimePackage package = CreatePackage(
                archive,
                library,
                librarySha256: new string('F', 64));
            using var client = new HttpClient(new BytesHandler(archive));
            var provisioner = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () => new(true, "test driver"));

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => provisioner.EnsureDownloadedAsync(TextWriter.Null, CancellationToken.None));

            Assert.Contains("extracted cuFFT runtime failed SHA-256", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(provisioner.CacheLibraryPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast does not download cuFFT when CUDA 13 driver preflight fails")]
    public async Task DriverFailurePreventsDownload()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] library = CreateLibraryBytes();
            byte[] archive = CreateArchive(library);
            CudaFastRuntimePackage package = CreatePackage(archive, library);
            var handler = new BytesHandler(archive);
            using var client = new HttpClient(handler);
            var provisioner = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () => new(false, "no compatible test GPU"));

            CudaFastBackendUnavailableException exception =
                await Assert.ThrowsAsync<CudaFastBackendUnavailableException>(
                    () => provisioner.EnsureDownloadedAsync(TextWriter.Null, CancellationToken.None));

            Assert.Contains("No runtime was downloaded", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.RequestCount);
            Assert.False(File.Exists(provisioner.CacheLibraryPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "Concurrent CUDA-fast provisioning performs one download")]
    public async Task ConcurrentProvisioningDownloadsOnce()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] library = CreateLibraryBytes();
            byte[] archive = CreateArchive(library);
            CudaFastRuntimePackage package = CreatePackage(archive, library);
            var handler = new BytesHandler(archive, TimeSpan.FromMilliseconds(100));
            using var client = new HttpClient(handler);
            var first = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () => new(true, "test driver"));
            var second = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () => new(true, "test driver"));

            string[] results = await Task.WhenAll(
                first.EnsureDownloadedAsync(TextWriter.Null, CancellationToken.None),
                second.EnsureDownloadedAsync(TextWriter.Null, CancellationToken.None));

            Assert.All(results, path => Assert.Equal(first.CacheLibraryPath, path));
            Assert.Equal(1, handler.RequestCount);
            Assert.True(first.IsPinnedLibraryValid(first.CacheLibraryPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast provisioning can be cancelled while waiting for another installer")]
    public async Task WaitingForInstallLockHonorsCancellation()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] library = CreateLibraryBytes();
            byte[] archive = CreateArchive(library);
            CudaFastRuntimePackage package = CreatePackage(archive, library);
            using var client = new HttpClient(new BytesHandler(archive));
            var provisioner = new CudaFastRuntimeProvisioner(
                client,
                package,
                directory,
                () => new(true, "test driver"));
            Directory.CreateDirectory(directory);
            await using FileStream heldLock = new(
                Path.Combine(directory, "install.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provisioner.EnsureDownloadedAsync(TextWriter.Null, cancellation.Token));

            Assert.False(File.Exists(provisioner.CacheLibraryPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-cuda-runtime-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "VHSDecodeDotNet.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static byte[] CreateLibraryBytes()
        => Enumerable.Range(0, 65_537).Select(index => (byte)(index * 31)).ToArray();

    private static byte[] CreateArchive(
        byte[] library,
        string entryName = "libcufft/bin/cufft64_12.dll")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using (Stream destination = entry.Open())
            {
                destination.Write(library);
            }

            ZipArchiveEntry license = archive.CreateEntry(
                "libcufft/LICENSE",
                CompressionLevel.Fastest);
            using (Stream licenseDestination = license.Open())
            using (var writer = new StreamWriter(licenseDestination))
            {
                writer.Write("test NVIDIA license");
            }
        }

        return stream.ToArray();
    }

    private static CudaFastRuntimePackage CreatePackage(
        byte[] archive,
        byte[] library,
        string? archiveSha256 = null,
        string? librarySha256 = null)
        => new(
            "test-version",
            new Uri("https://example.invalid/cufft.zip"),
            archive.LongLength,
            archiveSha256 ?? Convert.ToHexString(SHA256.HashData(archive)),
            library.LongLength,
            librarySha256 ?? Convert.ToHexString(SHA256.HashData(library)));

    private sealed class BytesHandler(byte[] content, TimeSpan? delay = null)
        : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
        }
    }
}
