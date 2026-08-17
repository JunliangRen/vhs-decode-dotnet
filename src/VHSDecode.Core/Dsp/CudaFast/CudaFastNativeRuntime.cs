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

    private static readonly Lazy<CudaFastNativeRuntime> Shared = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly nint _libraryHandle;
    private readonly GetAbiVersionDelegate _getAbiVersion;
    private readonly GetRuntimeInfoDelegate _getRuntimeInfo;
    private readonly RunDelegate _run;
    private readonly GetLastErrorDelegate _getLastError;

    private CudaFastNativeRuntime(nint libraryHandle)
    {
        _libraryHandle = libraryHandle;
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

    internal static ICudaFastNativeRuntime RequireAvailable() => Shared.Value;

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

    private static CudaFastNativeRuntime Load()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CudaFastBackendUnavailableException(
                "the first CUDA-full implementation currently supports win-x64 only.");
        }

        IReadOnlyList<string> candidates = BuildCandidatePaths();
        List<string> loadFailures = [];
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                return new CudaFastNativeRuntime(NativeLibrary.Load(candidate));
            }
            catch (Exception ex) when (ex is DllNotFoundException
                or BadImageFormatException
                or EntryPointNotFoundException
                or CudaFastBackendUnavailableException)
            {
                loadFailures.Add($"{candidate}: {ex.Message}");
            }
        }

        string searched = string.Join(Environment.NewLine, candidates.Select(path => "  " + path));
        string failures = loadFailures.Count == 0
            ? string.Empty
            : Environment.NewLine + "Load failures:" + Environment.NewLine
                + string.Join(Environment.NewLine, loadFailures.Select(message => "  " + message));
        throw new CudaFastBackendUnavailableException(
            $"'{NativeLibraryName}' and its CUDA 13 runtime dependencies could not be loaded. Searched:{Environment.NewLine}{searched}{failures}");
    }

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetAbiVersionDelegate();

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
