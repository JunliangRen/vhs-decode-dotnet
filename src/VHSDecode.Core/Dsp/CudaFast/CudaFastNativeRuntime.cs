using System.Runtime.InteropServices;
using System.Text;

namespace VHSDecode.Core.Dsp.CudaFast;

internal enum CudaFastProfile : uint
{
    Ntsc = 0,
    Pal = 1,
    PalM = 2
}

internal enum CudaFastTapeSpeed : uint
{
    Sp = 0,
    Lp = 1,
    Ep = 2
}

internal enum CudaFastInputSampleFormat : uint
{
    Float32 = 0,
    Int16 = 1
}

internal sealed record CudaFastRuntimeInfo(
    uint AbiVersion,
    int DeviceId,
    int ComputeMajor,
    int ComputeMinor,
    int MultiprocessorCount,
    ulong TotalVramBytes,
    ulong FreeVramBytes,
    string DeviceName);

internal sealed record CudaFastNativeResult(
    uint FieldsWritten,
    uint OutputLineLength,
    uint OutputFieldLines,
    double ElapsedSeconds);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate nuint CudaFastReadCallback(
    nint userData,
    nint destination,
    ulong sampleOffset,
    nuint sampleCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int CudaFastCancelCallback(nint userData);

internal sealed record CudaFastNativeRunConfiguration(
    CudaFastProfile Profile,
    CudaFastTapeSpeed TapeSpeed,
    int DeviceId,
    double SampleRateMhz,
    ulong TotalSamples,
    uint MaximumOutputFields,
    string OutputBase,
    bool Overwrite,
    CudaFastInputSampleFormat InputSampleFormat,
    CudaFastReadCallback ReadCallback,
    CudaFastCancelCallback CancelCallback,
    nint UserData);

internal interface ICudaFastNativeRuntime
{
    CudaFastRuntimeInfo GetRuntimeInfo(int deviceId = 0);

    CudaFastNativeResult Run(CudaFastNativeRunConfiguration configuration);
}

internal sealed class CudaFastBackendUnavailableException : NotSupportedException
{
    internal CudaFastBackendUnavailableException(string message, Exception? innerException = null)
        : base(
            $"The explicit '{DspBackendParser.CudaFastValue}' DSP backend is unavailable: {message}",
            innerException)
    {
    }
}

internal sealed class CudaFastNativeRuntime : ICudaFastNativeRuntime
{
    internal const uint AbiVersion = 0x00040000;
    internal const string NativeLibraryName = "vhsdecode_cuda_fast.dll";
    private const int StatusOk = 0;
    private const int StatusCudaUnavailable = -20002;
    private const int StatusCancelled = -20006;
    private const int DeviceNameCapacity = 128;
    private const int MinimumCuFftVersion = 12_000;
    private const int MaximumCuFftVersionExclusive = 13_000;

    private static readonly SemaphoreSlim LoadGate = new(1, 1);
    private static CudaFastNativeRuntime? _shared;

    private readonly nint _libraryHandle;
    private readonly nint _cuFftHandle;
    private readonly GetAbiVersionDelegate _getAbiVersion;
    private readonly GetRuntimeInfoDelegate _getRuntimeInfo;
    private readonly RunDelegate _run;
    private readonly GetLastErrorDelegate _getLastError;

    private CudaFastNativeRuntime(nint libraryHandle, nint cuFftHandle)
    {
        _libraryHandle = libraryHandle;
        _cuFftHandle = cuFftHandle;
        _getAbiVersion = GetExport<GetAbiVersionDelegate>(
            libraryHandle,
            "vhsdecode_cuda_fast_get_abi_version");
        _getRuntimeInfo = GetExport<GetRuntimeInfoDelegate>(
            libraryHandle,
            "vhsdecode_cuda_fast_get_runtime_info");
        _run = GetExport<RunDelegate>(libraryHandle, "vhsdecode_cuda_fast_run");
        _getLastError = GetExport<GetLastErrorDelegate>(
            libraryHandle,
            "vhsdecode_cuda_fast_get_last_error");

        uint abiVersion = _getAbiVersion();
        if (abiVersion != AbiVersion)
        {
            throw new CudaFastBackendUnavailableException(
                $"native bridge ABI 0x{abiVersion:X8} does not match managed ABI 0x{AbiVersion:X8}.");
        }
    }

    internal static ICudaFastNativeRuntime RequireAvailable()
        => RequireAvailableCore(
            TextWriter.Null,
            CancellationToken.None,
            allowDownload: false);

    internal static ICudaFastNativeRuntime RequireAvailable(
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        return RequireAvailableCore(output, cancellationToken, allowDownload: true);
    }

    internal static unsafe int RuntimeInfoStructureSize => sizeof(NativeRuntimeInfo);

    internal static unsafe int ConfigurationStructureSize => sizeof(NativeConfiguration);

    internal static unsafe int ResultStructureSize => sizeof(NativeResult);

    public unsafe CudaFastRuntimeInfo GetRuntimeInfo(int deviceId = 0)
    {
        var nativeInfo = new NativeRuntimeInfo
        {
            StructSize = checked((uint)sizeof(NativeRuntimeInfo))
        };
        int status = _getRuntimeInfo(deviceId, ref nativeInfo);
        ThrowForStatus(status, "CUDA device probe");

        byte* name = nativeInfo.DeviceName;
        int length = 0;
        while (length < DeviceNameCapacity && name[length] != 0)
        {
            length++;
        }
        string deviceName = Encoding.UTF8.GetString(name, length);

        return new CudaFastRuntimeInfo(
            nativeInfo.AbiVersion,
            nativeInfo.DeviceId,
            nativeInfo.ComputeMajor,
            nativeInfo.ComputeMinor,
            nativeInfo.MultiprocessorCount,
            nativeInfo.TotalVramBytes,
            nativeInfo.FreeVramBytes,
            deviceName);
    }

    public unsafe CudaFastNativeResult Run(CudaFastNativeRunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.OutputBase);
        ArgumentNullException.ThrowIfNull(configuration.ReadCallback);
        ArgumentNullException.ThrowIfNull(configuration.CancelCallback);

        nint outputBaseUtf8 = Marshal.StringToCoTaskMemUTF8(configuration.OutputBase);
        try
        {
            var nativeConfiguration = new NativeConfiguration
            {
                StructSize = checked((uint)sizeof(NativeConfiguration)),
                Profile = (uint)configuration.Profile,
                TapeSpeed = (uint)configuration.TapeSpeed,
                DeviceId = configuration.DeviceId,
                SampleRateMhz = configuration.SampleRateMhz,
                TotalSamples = configuration.TotalSamples,
                OutputBaseUtf8 = outputBaseUtf8,
                Overwrite = configuration.Overwrite ? 1 : 0,
                InputSampleFormat = (uint)configuration.InputSampleFormat,
                MaximumOutputFields = configuration.MaximumOutputFields,
                ReadCallback = Marshal.GetFunctionPointerForDelegate(configuration.ReadCallback),
                CancelCallback = Marshal.GetFunctionPointerForDelegate(configuration.CancelCallback),
                UserData = configuration.UserData
            };
            var nativeResult = new NativeResult
            {
                StructSize = checked((uint)sizeof(NativeResult))
            };

            int status = _run(ref nativeConfiguration, ref nativeResult);
            GC.KeepAlive(configuration.ReadCallback);
            GC.KeepAlive(configuration.CancelCallback);
            ThrowForStatus(status, "full CUDA signal decode");
            return new CudaFastNativeResult(
                nativeResult.FieldsWritten,
                nativeResult.OutputLineLength,
                nativeResult.OutputFieldLines,
                nativeResult.ElapsedSeconds);
        }
        finally
        {
            Marshal.FreeCoTaskMem(outputBaseUtf8);
        }
    }

    private static CudaFastNativeRuntime RequireAvailableCore(
        TextWriter output,
        CancellationToken cancellationToken,
        bool allowDownload)
    {
        CudaFastNativeRuntime? existing = Volatile.Read(ref _shared);
        if (existing is not null)
        {
            return existing;
        }

        LoadGate.Wait(cancellationToken);
        try
        {
            existing = _shared;
            if (existing is not null)
            {
                return existing;
            }

            existing = Load(output, cancellationToken, allowDownload);
            Volatile.Write(ref _shared, existing);
            return existing;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    private static CudaFastNativeRuntime Load(
        TextWriter output,
        CancellationToken cancellationToken,
        bool allowDownload)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CudaFastBackendUnavailableException(
                "the first CUDA-full implementation currently supports win-x64 only.");
        }

        IReadOnlyList<string> bridgeCandidates = BuildCandidatePaths();
        if (!bridgeCandidates.Any(File.Exists))
        {
            string searchedBridges = string.Join(
                Environment.NewLine,
                bridgeCandidates.Select(path => "  " + path));
            throw new CudaFastBackendUnavailableException(
                $"'{NativeLibraryName}' was not found. Searched:{Environment.NewLine}{searchedBridges}");
        }

        CudaFastRuntimeProvisioner provisioner = CudaFastRuntimeProvisioner.CreateProduction();
        IReadOnlyList<string> cuFftCandidates = CudaFastRuntimeProvisioner.BuildCandidatePaths(
            CudaFastRuntimeProvisioner.CaptureSearchEnvironment(
                provisioner.CacheLibraryPath));
        List<string> loadFailures = [];
        foreach (string candidate in cuFftCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (Path.GetFullPath(candidate).Equals(
                    Path.GetFullPath(provisioner.CacheLibraryPath),
                    StringComparison.OrdinalIgnoreCase)
                && !provisioner.IsPinnedLibraryValid(candidate))
            {
                loadFailures.Add($"{candidate}: cached runtime failed pinned size or SHA-256 validation.");
                continue;
            }

            if (TryLoadWithCuFft(
                    candidate,
                    bridgeCandidates,
                    out CudaFastNativeRuntime? runtime,
                    out string failure))
            {
                output.WriteLine(
                    $"CUDA-fast cuFFT {FormatCuFftVersion(runtime!.CuFftVersion)} loaded from '{runtime.CuFftPath}'.");
                return runtime;
            }

            loadFailures.Add($"{candidate}: {failure}");
        }

        bool downloadEnabled = allowDownload
            && CudaFastRuntimeProvisioner.IsAutoDownloadEnabled(
                Environment.GetEnvironmentVariable(
                    CudaFastRuntimeProvisioner.AutoDownloadEnvironmentVariable));
        if (downloadEnabled)
        {
            string downloadedPath;
            try
            {
                downloadedPath = provisioner.EnsureDownloadedAsync(output, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CudaFastBackendUnavailableException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException)
            {
                throw new CudaFastBackendUnavailableException(
                    $"the pinned cuFFT runtime could not be downloaded from NVIDIA. "
                    + $"Download '{CudaFastRuntimeProvisioner.PinnedPackage.DownloadUri}' manually or set "
                    + $"{CudaFastRuntimeProvisioner.RuntimePathEnvironmentVariable} to a compatible runtime directory. {ex.Message}",
                    ex);
            }

            if (TryLoadWithCuFft(
                    downloadedPath,
                    bridgeCandidates,
                    out CudaFastNativeRuntime? runtime,
                    out string failure))
            {
                output.WriteLine(
                    $"CUDA-fast cuFFT {FormatCuFftVersion(runtime!.CuFftVersion)} loaded from '{runtime.CuFftPath}'.");
                return runtime;
            }

            loadFailures.Add($"{downloadedPath}: {failure}");
        }

        string searched = string.Join(
            Environment.NewLine,
            cuFftCandidates.Select(path => "  " + path));
        string failures = loadFailures.Count == 0
            ? string.Empty
            : Environment.NewLine + "Load failures:" + Environment.NewLine
                + string.Join(Environment.NewLine, loadFailures.Select(message => "  " + message));
        string downloadStatus = allowDownload && !downloadEnabled
            ? Environment.NewLine
                + $"Automatic download is disabled by {CudaFastRuntimeProvisioner.AutoDownloadEnvironmentVariable}."
            : string.Empty;
        throw new CudaFastBackendUnavailableException(
            $"'{CudaFastRuntimeProvisioner.CuFftLibraryName}' could not be loaded for '{NativeLibraryName}'. "
            + $"Searched:{Environment.NewLine}{searched}{failures}{downloadStatus}{Environment.NewLine}"
            + $"Pinned NVIDIA package: {CudaFastRuntimeProvisioner.PinnedPackage.DownloadUri}");
    }

    private static bool TryLoadWithCuFft(
        string cuFftPath,
        IReadOnlyList<string> bridgeCandidates,
        out CudaFastNativeRuntime? runtime,
        out string failure)
    {
        runtime = null;
        nint cuFftHandle = 0;
        try
        {
            cuFftHandle = NativeLibrary.Load(cuFftPath);
            var getVersion = GetExport<CuFftGetVersionDelegate>(
                cuFftHandle,
                "cufftGetVersion");
            int status = getVersion(out int version);
            if (status != 0)
            {
                failure = $"cufftGetVersion failed with cuFFT status {status}.";
                return false;
            }
            if (version < MinimumCuFftVersion || version >= MaximumCuFftVersionExclusive)
            {
                failure = $"cuFFT version {version} is outside the supported 12.x ABI range.";
                return false;
            }

            var bridgeFailures = new List<string>();
            foreach (string bridgePath in bridgeCandidates)
            {
                if (!File.Exists(bridgePath))
                {
                    continue;
                }

                nint bridgeHandle = 0;
                try
                {
                    bridgeHandle = NativeLibrary.Load(bridgePath);
                    runtime = new CudaFastNativeRuntime(bridgeHandle, cuFftHandle)
                    {
                        CuFftPath = Path.GetFullPath(cuFftPath),
                        CuFftVersion = version
                    };
                    bridgeHandle = 0;
                    cuFftHandle = 0;
                    failure = string.Empty;
                    return true;
                }
                catch (Exception ex) when (ex is DllNotFoundException
                    or BadImageFormatException
                    or EntryPointNotFoundException
                    or CudaFastBackendUnavailableException)
                {
                    bridgeFailures.Add($"{bridgePath}: {ex.Message}");
                }
                finally
                {
                    if (bridgeHandle != 0)
                    {
                        NativeLibrary.Free(bridgeHandle);
                    }
                }
            }

            failure = bridgeFailures.Count == 0
                ? $"'{NativeLibraryName}' was not found."
                : string.Join(" | ", bridgeFailures);
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException)
        {
            failure = ex.Message;
            return false;
        }
        finally
        {
            if (cuFftHandle != 0)
            {
                NativeLibrary.Free(cuFftHandle);
            }
        }
    }

    private static string FormatCuFftVersion(int version)
        => $"{version / 1000}.{(version % 1000) / 100}.{version % 100}";

    internal static IReadOnlyList<string> BuildCandidatePaths()
    {
        var paths = new List<string>();
        string? configured = Environment.GetEnvironmentVariable("VHSDECODE_CUDA_FAST_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string fullConfiguredPath = Path.GetFullPath(configured);
            paths.Add(Directory.Exists(fullConfiguredPath)
                ? Path.Combine(fullConfiguredPath, NativeLibraryName)
                : fullConfiguredPath);
        }

        paths.Add(Path.Combine(AppContext.BaseDirectory, NativeLibraryName));
        string? assemblyDirectory = Path.GetDirectoryName(
            typeof(CudaFastNativeRuntime).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            paths.Add(Path.Combine(assemblyDirectory, NativeLibraryName));
        }

        return paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ThrowForStatus(int status, string operation)
    {
        if (status == StatusOk)
        {
            return;
        }

        string detail = Marshal.PtrToStringUTF8(_getLastError())
            ?? $"native status {status}";
        if (status == StatusCancelled)
        {
            throw new OperationCanceledException(detail);
        }

        if (status == StatusCudaUnavailable)
        {
            throw new CudaFastBackendUnavailableException(detail);
        }

        throw new InvalidOperationException(
            $"CUDA-fast {operation} failed (native status {status}): {detail}");
    }

    private static TDelegate GetExport<TDelegate>(nint libraryHandle, string name)
        where TDelegate : Delegate
        => Marshal.GetDelegateForFunctionPointer<TDelegate>(
            NativeLibrary.GetExport(libraryHandle, name));

    private string CuFftPath { get; init; } = string.Empty;

    private int CuFftVersion { get; init; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetAbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CuFftGetVersionDelegate(out int version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetRuntimeInfoDelegate(int deviceId, ref NativeRuntimeInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RunDelegate(
        ref NativeConfiguration configuration,
        ref NativeResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetLastErrorDelegate();

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeRuntimeInfo
    {
        public uint StructSize;
        public uint AbiVersion;
        public int DeviceId;
        public int ComputeMajor;
        public int ComputeMinor;
        public int MultiprocessorCount;
        public ulong TotalVramBytes;
        public ulong FreeVramBytes;
        public fixed byte DeviceName[DeviceNameCapacity];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeConfiguration
    {
        public uint StructSize;
        public uint Profile;
        public uint TapeSpeed;
        public int DeviceId;
        public double SampleRateMhz;
        public ulong TotalSamples;
        public nint OutputBaseUtf8;
        public int Overwrite;
        public uint InputSampleFormat;
        public uint MaximumOutputFields;
        public nint ReadCallback;
        public nint CancelCallback;
        public nint UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult
    {
        public uint StructSize;
        public uint FieldsWritten;
        public uint OutputLineLength;
        public uint OutputFieldLines;
        public double ElapsedSeconds;
    }
}
