using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace VHSDecode.Core.Dsp.CudaFast;

internal sealed record CudaFastRuntimePackage(
    string Version,
    Uri DownloadUri,
    long ArchiveSizeBytes,
    string ArchiveSha256,
    long LibrarySizeBytes,
    string LibrarySha256);

internal sealed record CudaFastDriverProbeResult(bool IsAvailable, string Diagnostic);

internal sealed record CudaFastRuntimeSearchEnvironment(
    string AppBaseDirectory,
    string? AssemblyDirectory,
    string? ConfiguredBridgePath,
    string? ConfiguredRuntimePath,
    string? CudaPathV13,
    string? CudaPath,
    IReadOnlyList<string> ToolkitRoots,
    IReadOnlyList<string> PathDirectories,
    string CacheLibraryPath);

internal sealed class CudaFastRuntimeProvisioner
{
    internal const string CuFftLibraryName = "cufft64_12.dll";
    internal const string CuFftLicenseName = "LICENSE.txt";
    internal const string AutoDownloadEnvironmentVariable = "VHSDECODE_CUDA_AUTO_DOWNLOAD";
    internal const string CachePathEnvironmentVariable = "VHSDECODE_CUDA_CACHE_PATH";
    internal const string RuntimePathEnvironmentVariable = "VHSDECODE_CUDA_RUNTIME_PATH";

    internal static readonly CudaFastRuntimePackage PinnedPackage = new(
        Version: "12.0.0.15",
        DownloadUri: new Uri(
            "https://developer.download.nvidia.com/compute/cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-12.0.0.15-archive.zip"),
        ArchiveSizeBytes: 212_026_240,
        ArchiveSha256: "4F0E0C019E1B53166BDEAF52C91C333450AE9DD05FBADC77CFFB70A702501B67",
        LibrarySizeBytes: 284_331_040,
        LibrarySha256: "611BA7E40DFAB64B9B5BD35F4AD3593E00A8E93785FBF53160D9398AACD5AC14");

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _httpClient;
    private readonly CudaFastRuntimePackage _package;
    private readonly string _cacheDirectory;
    private readonly Func<CudaFastDriverProbeResult> _driverProbe;

    internal CudaFastRuntimeProvisioner(
        HttpClient httpClient,
        CudaFastRuntimePackage package,
        string cacheDirectory,
        Func<CudaFastDriverProbeResult> driverProbe)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _package = package ?? throw new ArgumentNullException(nameof(package));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _driverProbe = driverProbe ?? throw new ArgumentNullException(nameof(driverProbe));
    }

    internal string CacheLibraryPath => Path.Combine(_cacheDirectory, CuFftLibraryName);

    internal string CacheLicensePath => Path.Combine(_cacheDirectory, CuFftLicenseName);

    internal static CudaFastRuntimeProvisioner CreateProduction()
        => new(
            SharedHttpClient,
            PinnedPackage,
            GetPinnedCacheDirectory(),
            ProbeCuda13Driver);

    internal static bool IsAutoDownloadEnabled(string? value)
        => value is null
            || !(value.Equals("0", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase));

    internal static string GetPinnedCacheDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable(CachePathEnvironmentVariable);
        string root;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            root = Path.GetFullPath(configured);
        }
        else
        {
            string localApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new CudaFastBackendUnavailableException(
                    $"the per-user CUDA runtime cache could not be located; set {CachePathEnvironmentVariable} to a writable directory.");
            }

            root = Path.Combine(localApplicationData, "vhs-decode-dotnet", "cuda");
        }

        return Path.Combine(root, "cufft", PinnedPackage.Version);
    }

    internal static CudaFastRuntimeSearchEnvironment CaptureSearchEnvironment(
        string cacheLibraryPath)
    {
        var toolkitRoots = new List<string>();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            string cudaRoot = Path.Combine(
                programFiles,
                "NVIDIA GPU Computing Toolkit",
                "CUDA");
            try
            {
                toolkitRoots.AddRange(
                    Directory.EnumerateDirectories(cudaRoot, "v13.*")
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException)
            {
                // Environment variables and explicit paths remain available.
            }
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        string[] pathDirectories = string.IsNullOrWhiteSpace(pathValue)
            ? []
            : pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new CudaFastRuntimeSearchEnvironment(
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(CudaFastNativeRuntime).Assembly.Location),
            Environment.GetEnvironmentVariable("VHSDECODE_CUDA_FAST_PATH"),
            Environment.GetEnvironmentVariable(RuntimePathEnvironmentVariable),
            Environment.GetEnvironmentVariable("CUDA_PATH_V13_0"),
            Environment.GetEnvironmentVariable("CUDA_PATH"),
            toolkitRoots,
            pathDirectories,
            cacheLibraryPath);
    }

    internal static IReadOnlyList<string> BuildCandidatePaths(
        CudaFastRuntimeSearchEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var paths = new List<string>();

        AddConfiguredRuntimePath(paths, environment.ConfiguredRuntimePath);
        AddBridgeSiblingPath(paths, environment.ConfiguredBridgePath);
        AddDirectoryCandidate(paths, environment.AppBaseDirectory);
        AddDirectoryCandidate(paths, environment.AssemblyDirectory);
        AddToolkitRoot(paths, environment.CudaPathV13);
        AddToolkitRoot(paths, environment.CudaPath);
        foreach (string root in environment.ToolkitRoots)
        {
            AddToolkitRoot(paths, root);
        }
        foreach (string directory in environment.PathDirectories)
        {
            AddDirectoryCandidate(paths, directory);
        }
        AddFileCandidate(paths, environment.CacheLibraryPath);

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal bool IsPinnedLibraryValid(string path)
    {
        try
        {
            if (!File.Exists(path) || !File.Exists(CacheLicensePath))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length != _package.LibrarySizeBytes)
            {
                return false;
            }

            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream))
                .Equals(_package.LibrarySha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal async Task<string> EnsureDownloadedAsync(
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_cacheDirectory);

        string lockPath = Path.Combine(_cacheDirectory, "install.lock");
        await using FileStream installLock = await AcquireInstallLockAsync(
            lockPath,
            cancellationToken).ConfigureAwait(false);

        if (await IsPinnedLibraryValidAsync(CacheLibraryPath, cancellationToken)
            .ConfigureAwait(false))
        {
            return CacheLibraryPath;
        }

        CudaFastDriverProbeResult driver = _driverProbe();
        if (!driver.IsAvailable)
        {
            throw new CudaFastBackendUnavailableException(
                $"cuFFT was not found and the CUDA 13 driver preflight failed: {driver.Diagnostic} No runtime was downloaded.");
        }

        string uniqueSuffix = Guid.NewGuid().ToString("N");
        string archivePath = Path.Combine(_cacheDirectory, $"cufft-{uniqueSuffix}.download");
        string libraryPath = Path.Combine(_cacheDirectory, $"{CuFftLibraryName}.{uniqueSuffix}.tmp");
        string licensePath = Path.Combine(_cacheDirectory, $"{CuFftLicenseName}.{uniqueSuffix}.tmp");
        string metadataPath = Path.Combine(_cacheDirectory, $"install.{uniqueSuffix}.tmp");
        try
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"CUDA-fast cuFFT runtime was not found. Downloading {_package.ArchiveSizeBytes / (1024.0 * 1024.0):F1} MiB from NVIDIA..."));
            await DownloadArchiveAsync(archivePath, output, cancellationToken)
                .ConfigureAwait(false);
            await ValidateFileAsync(
                    archivePath,
                    _package.ArchiveSizeBytes,
                    _package.ArchiveSha256,
                    "downloaded cuFFT archive",
                    cancellationToken)
                .ConfigureAwait(false);
            await ExtractPackageAsync(
                    archivePath,
                    libraryPath,
                    licensePath,
                    cancellationToken)
                .ConfigureAwait(false);
            await ValidateFileAsync(
                    libraryPath,
                    _package.LibrarySizeBytes,
                    _package.LibrarySha256,
                    "extracted cuFFT runtime",
                    cancellationToken)
                .ConfigureAwait(false);

            File.Move(licensePath, CacheLicensePath, overwrite: true);
            File.Move(libraryPath, CacheLibraryPath, overwrite: true);
            var metadata = new
            {
                packageVersion = _package.Version,
                source = _package.DownloadUri.AbsoluteUri,
                archiveSha256 = _package.ArchiveSha256,
                librarySha256 = _package.LibrarySha256,
                installedAtUtc = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(
                    metadataPath,
                    JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(
                metadataPath,
                Path.Combine(_cacheDirectory, "install.json"),
                overwrite: true);
            output.WriteLine($"CUDA-fast cuFFT runtime installed in '{_cacheDirectory}'.");
            return CacheLibraryPath;
        }
        finally
        {
            DeleteTemporaryFile(archivePath);
            DeleteTemporaryFile(libraryPath);
            DeleteTemporaryFile(licensePath);
            DeleteTemporaryFile(metadataPath);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("vhs-decode-dotnet", "2"));
        return client;
    }

    private static async Task<FileStream> AcquireInstallLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> IsPinnedLibraryValidAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || !File.Exists(CacheLicensePath))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length != _package.LibrarySizeBytes)
            {
                return false;
            }

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToHexString(hash)
                .Equals(_package.LibrarySha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task DownloadArchiveAsync(
        string destinationPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _package.DownloadUri);
        using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength != _package.ArchiveSizeBytes)
        {
            throw new InvalidDataException(
                $"NVIDIA cuFFT response length {contentLength} did not match the pinned {_package.ArchiveSizeBytes} bytes.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long total = 0;
        int lastReportedPercent = -10;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                total = checked(total + read);
                int percent = _package.ArchiveSizeBytes == 0
                    ? 100
                    : checked((int)Math.Min(100, (total * 100) / _package.ArchiveSizeBytes));
                if (percent >= lastReportedPercent + 10 || percent == 100)
                {
                    output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"CUDA-fast cuFFT download: {percent}% ({total / (1024.0 * 1024.0):F1}/{_package.ArchiveSizeBytes / (1024.0 * 1024.0):F1} MiB)"));
                    lastReportedPercent = percent;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExtractPackageAsync(
        string archivePath,
        string destinationPath,
        string licenseDestinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream archiveStream = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry[] entries = archive.Entries
            .Where(entry => entry.Name.Equals(CuFftLibraryName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException(
                $"The pinned NVIDIA archive contained {entries.Length} '{CuFftLibraryName}' entries instead of one.");
        }
        if (entries[0].Length != _package.LibrarySizeBytes)
        {
            throw new InvalidDataException(
                $"The archived cuFFT runtime length {entries[0].Length} did not match the pinned {_package.LibrarySizeBytes} bytes.");
        }

        ZipArchiveEntry[] licenseEntries = archive.Entries
            .Where(entry => entry.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (licenseEntries.Length != 1
            || licenseEntries[0].Length <= 0
            || licenseEntries[0].Length > 1024 * 1024)
        {
            throw new InvalidDataException(
                "The pinned NVIDIA archive did not contain exactly one bounded LICENSE file.");
        }

        await CopyArchiveEntryAsync(entries[0], destinationPath, cancellationToken)
            .ConfigureAwait(false);
        await CopyArchiveEntryAsync(
                licenseEntries[0],
                licenseDestinationPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CopyArchiveEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using Stream source = entry.Open();
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateFileAsync(
        string path,
        long expectedLength,
        string expectedSha256,
        string description,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"The {description} was {info.Length} bytes; expected {expectedLength} bytes.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The {description} failed SHA-256 validation ({actual}); expected {expectedSha256}.");
        }
    }

    internal static CudaFastDriverProbeResult ProbeCuda13Driver()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(false, "CUDA-fast currently supports Windows x64 only.");
        }

        if (!Environment.Is64BitProcess)
        {
            return new(false, "CUDA-fast requires a 64-bit process.");
        }

        string driverPath = Path.Combine(Environment.SystemDirectory, "nvcuda.dll");
        if (!File.Exists(driverPath))
        {
            return new(false, "the NVIDIA CUDA driver library nvcuda.dll was not found.");
        }

        nint handle = 0;
        try
        {
            handle = NativeLibrary.Load(driverPath);
            var initialize = GetDriverExport<CuInitDelegate>(handle, "cuInit");
            var getVersion = GetDriverExport<CuDriverGetVersionDelegate>(
                handle,
                "cuDriverGetVersion");
            var getDeviceCount = GetDriverExport<CuDeviceGetCountDelegate>(
                handle,
                "cuDeviceGetCount");
            var getDevice = GetDriverExport<CuDeviceGetDelegate>(handle, "cuDeviceGet");
            var getDeviceAttribute = GetDriverExport<CuDeviceGetAttributeDelegate>(
                handle,
                "cuDeviceGetAttribute");
            int status = initialize(0);
            if (status != 0)
            {
                return new(false, $"cuInit failed with CUDA status {status}.");
            }
            status = getVersion(out int version);
            if (status != 0)
            {
                return new(false, $"cuDriverGetVersion failed with CUDA status {status}.");
            }
            if (version < 13_000)
            {
                return new(
                    false,
                    $"the installed NVIDIA driver exposes CUDA {version / 1000}.{(version % 1000) / 10}, but CUDA 13 or newer is required.");
            }
            status = getDeviceCount(out int count);
            if (status != 0 || count <= 0)
            {
                return new(
                    false,
                    status == 0
                        ? "the NVIDIA driver reported no CUDA devices."
                        : $"cuDeviceGetCount failed with CUDA status {status}.");
            }

            status = getDevice(out int device, 0);
            if (status != 0)
            {
                return new(false, $"cuDeviceGet(0) failed with CUDA status {status}.");
            }

            const int ComputeCapabilityMajorAttribute = 75;
            const int ComputeCapabilityMinorAttribute = 76;
            status = getDeviceAttribute(
                out int computeMajor,
                ComputeCapabilityMajorAttribute,
                device);
            if (status != 0)
            {
                return new(
                    false,
                    $"reading CUDA device 0 compute-capability major failed with status {status}.");
            }
            status = getDeviceAttribute(
                out int computeMinor,
                ComputeCapabilityMinorAttribute,
                device);
            if (status != 0)
            {
                return new(
                    false,
                    $"reading CUDA device 0 compute-capability minor failed with status {status}.");
            }

            return EvaluateCudaDriverCapabilities(
                version,
                count,
                computeMajor,
                computeMinor);
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException)
        {
            return new(false, ex.Message);
        }
        finally
        {
            if (handle != 0)
            {
                NativeLibrary.Free(handle);
            }
        }
    }

    internal static CudaFastDriverProbeResult EvaluateCudaDriverCapabilities(
        int driverVersion,
        int deviceCount,
        int computeMajor,
        int computeMinor)
    {
        if (driverVersion < 13_000)
        {
            return new(
                false,
                $"the installed NVIDIA driver exposes CUDA {driverVersion / 1000}.{(driverVersion % 1000) / 10}, but CUDA 13 or newer is required.");
        }

        if (deviceCount <= 0)
        {
            return new(false, "the NVIDIA driver reported no CUDA devices.");
        }

        if (computeMajor < 7 || (computeMajor == 7 && computeMinor < 5))
        {
            return new(
                false,
                $"CUDA device 0 has compute capability {computeMajor}.{computeMinor}; CUDA-fast requires 7.5 or newer.");
        }

        return new(
            true,
            $"CUDA driver {driverVersion / 1000}.{(driverVersion % 1000) / 10} reported {deviceCount} device(s); device 0 has compute capability {computeMajor}.{computeMinor}.");
    }

    private static TDelegate GetDriverExport<TDelegate>(nint handle, string name)
        where TDelegate : Delegate
        => Marshal.GetDelegateForFunctionPointer<TDelegate>(NativeLibrary.GetExport(handle, name));

    private static void AddConfiguredRuntimePath(List<string> paths, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (Path.GetExtension(value).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            AddFileCandidate(paths, value);
        }
        else
        {
            AddDirectoryCandidate(paths, value);
        }
    }

    private static void AddBridgeSiblingPath(List<string> paths, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string? directory = Path.GetExtension(value).Equals(".dll", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(value)
            : value;
        AddDirectoryCandidate(paths, directory);
    }

    private static void AddToolkitRoot(List<string> paths, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        AddDirectoryCandidate(paths, Path.Combine(root, "bin", "x64"));
    }

    private static void AddDirectoryCandidate(List<string> paths, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        AddFileCandidate(paths, Path.Combine(directory, CuFftLibraryName));
    }

    private static void AddFileCandidate(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            paths.Add(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            // Ignore malformed optional environment entries.
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next versioned installation uses unique temporary names.
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CuInitDelegate(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CuDriverGetVersionDelegate(out int version);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CuDeviceGetCountDelegate(out int count);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CuDeviceGetDelegate(out int device, int ordinal);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CuDeviceGetAttributeDelegate(
        out int value,
        int attribute,
        int device);
}
