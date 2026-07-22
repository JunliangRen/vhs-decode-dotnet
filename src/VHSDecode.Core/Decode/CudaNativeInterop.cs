using System.Runtime.InteropServices;
using System.Text;

namespace VHSDecode.Core.Compute.Cuda;

internal sealed class NativeCudaApiLoader : ICudaNativeApiLoader
{
    public CudaNativeApiLoadResult Load(string? componentDirectory)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return CudaNativeApiLoadResult.Failed(
                CudaBackendFailure.UnsupportedPlatform,
                "The CUDA component supports Windows x64 only.");
        }

        IReadOnlyList<string> directories;
        try
        {
            directories = ResolveComponentDirectories(
                componentDirectory,
                Environment.ProcessPath,
                AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            return CudaNativeApiLoadResult.Failed(
                CudaBackendFailure.ComponentLoadFailed,
                $"The CUDA component path is invalid: {ex.Message}");
        }

        string? componentPath = directories
            .Select(directory => Path.Combine(directory, CudaNativeAbi.ComponentFileName))
            .FirstOrDefault(File.Exists);
        if (componentPath is null)
        {
            string searchedPaths = string.Join(
                "', '",
                directories.Select(directory => Path.Combine(directory, CudaNativeAbi.ComponentFileName)));
            return CudaNativeApiLoadResult.Failed(
                CudaBackendFailure.ComponentMissing,
                $"Optional CUDA component was not found at '{searchedPaths}'.");
        }

        nint nativeLibrary;
        try
        {
            if (!NativeLibrary.TryLoad(componentPath, out nativeLibrary))
            {
                return CudaNativeApiLoadResult.Failed(
                    CudaBackendFailure.ComponentLoadFailed,
                    $"CUDA component '{componentPath}' could not be loaded; its CUDA runtime dependencies may be unavailable.");
            }
        }
        catch (Exception ex)
        {
            return CudaNativeApiLoadResult.Failed(
                CudaBackendFailure.ComponentLoadFailed,
                $"CUDA component '{componentPath}' could not be loaded: {ex.Message}");
        }

        var libraryHandle = new NativeLibrarySafeHandle(nativeLibrary);
        try
        {
            return CudaNativeApiLoadResult.Success(new NativeCudaApi(libraryHandle));
        }
        catch (EntryPointNotFoundException ex)
        {
            libraryHandle.Dispose();
            return CudaNativeApiLoadResult.Failed(
                CudaBackendFailure.RequiredExportMissing,
                $"CUDA component is missing a required ABI v{CudaNativeAbi.Version} export: {ex.Message}");
        }
        catch (Exception ex)
        {
            libraryHandle.Dispose();
            return CudaNativeApiLoadResult.Failed(
                CudaBackendFailure.ComponentLoadFailed,
                $"CUDA component exports could not be bound: {ex.Message}");
        }
    }

    internal static IReadOnlyList<string> ResolveComponentDirectories(
        string? componentDirectory,
        string? processPath,
        string appBaseDirectory)
    {
        if (componentDirectory is not null)
        {
            return [Path.GetFullPath(componentDirectory)];
        }

        var directories = new List<string>(capacity: 2);
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string? processDirectory = Path.GetDirectoryName(Path.GetFullPath(processPath));
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                directories.Add(processDirectory);
            }
        }

        string applicationBase = Path.GetFullPath(appBaseDirectory);
        if (!directories.Contains(applicationBase, StringComparer.OrdinalIgnoreCase))
        {
            directories.Add(applicationBase);
        }

        return directories;
    }
}

internal sealed unsafe class NativeCudaApi : ICudaNativeApi
{
    private const uint DeviceFlagFp64 = 1u << 0;
    private const uint DeviceFlagConcurrentCopyAndCompute = 1u << 1;
    private const uint DeviceFlagMemoryInfo = 1u << 2;
    private const int ErrorBufferSize = 4_096;

    private readonly NativeLibrarySafeHandle _library;
    private readonly GetAbiVersionDelegate _getAbiVersion;
    private readonly GetDeviceCountDelegate _getDeviceCount;
    private readonly GetDeviceInfoDelegate _getDeviceInfo;
    private readonly CreateContextDelegate _createContext;
    private readonly DestroyContextDelegate _destroyContext;
    private readonly SelfTestDelegate _selfTest;
    private readonly GetLastErrorDelegate _getLastError;
    private readonly GetCapabilitiesDelegate _getCapabilities;
    private readonly RfBatchExecuteDelegate _rfBatchExecute;

    internal NativeCudaApi(NativeLibrarySafeHandle library)
    {
        _library = library;
        nint handle = library.DangerousGetHandle();
        _getAbiVersion = Bind<GetAbiVersionDelegate>(handle, "vhsdecode_cuda_get_abi_version");
        _getDeviceCount = Bind<GetDeviceCountDelegate>(handle, "vhsdecode_cuda_get_device_count");
        _getDeviceInfo = Bind<GetDeviceInfoDelegate>(handle, "vhsdecode_cuda_get_device_info");
        _createContext = Bind<CreateContextDelegate>(handle, "vhsdecode_cuda_create");
        _destroyContext = Bind<DestroyContextDelegate>(handle, "vhsdecode_cuda_destroy");
        _selfTest = Bind<SelfTestDelegate>(handle, "vhsdecode_cuda_self_test");
        _getLastError = Bind<GetLastErrorDelegate>(handle, "vhsdecode_cuda_get_last_error");
        _getCapabilities = Bind<GetCapabilitiesDelegate>(handle, "vhsdecode_cuda_get_capabilities");
        _rfBatchExecute = Bind<RfBatchExecuteDelegate>(handle, "vhsdecode_cuda_rf_batch_execute");
    }

    internal static int DeviceInfoStructSize => sizeof(CudaNativeDeviceInfoV1);

    internal static int SelfTestMetricsStructSize => sizeof(CudaNativeSelfTestMetricsV1);

    internal static int RfBatchJobStructSize => sizeof(CudaNativeRfBatchJobV1);

    internal static int LdFrequencyOptionsStructSize => sizeof(CudaNativeLdFrequencyOptionsV1);

    public uint GetAbiVersion() => _getAbiVersion();

    public CudaNativeStatus GetDeviceCount(out int deviceCount) =>
        (CudaNativeStatus)_getDeviceCount(out deviceCount);

    public CudaNativeStatus GetDeviceInfo(int deviceIndex, out CudaDeviceInfo deviceInfo)
    {
        var native = new CudaNativeDeviceInfoV1
        {
            StructSize = (uint)sizeof(CudaNativeDeviceInfoV1)
        };

        CudaNativeStatus status = (CudaNativeStatus)_getDeviceInfo(deviceIndex, ref native);
        if (status != CudaNativeStatus.Success)
        {
            deviceInfo = null!;
            return status;
        }

        deviceInfo = new CudaDeviceInfo(
            native.Ordinal,
            ReadDeviceName(ref native),
            native.ComputeCapabilityMajor,
            native.ComputeCapabilityMinor,
            native.TotalGlobalMemoryBytes,
            native.DriverVersion,
            native.RuntimeVersion,
            native.CufftVersion,
            (native.Flags & DeviceFlagFp64) != 0,
            (native.Flags & DeviceFlagConcurrentCopyAndCompute) != 0,
            (native.Flags & DeviceFlagMemoryInfo) != 0
                ? ReadAvailableGlobalMemory(ref native)
                : null);
        return status;
    }

    public CudaNativeStatus CreateContext(int deviceIndex, out nint context) =>
        (CudaNativeStatus)_createContext(deviceIndex, out context);

    public void DestroyContext(nint context)
    {
        if (context != nint.Zero)
        {
            _destroyContext(context);
        }
    }

    public CudaNativeStatus RunSelfTest(nint context, out CudaSelfTestMetrics metrics)
    {
        var native = new CudaNativeSelfTestMetricsV1
        {
            StructSize = (uint)sizeof(CudaNativeSelfTestMetricsV1)
        };

        CudaNativeStatus status = (CudaNativeStatus)_selfTest(context, ref native);
        metrics = new CudaSelfTestMetrics(
            native.Passed != 0,
            native.MaximumAbsoluteError,
            native.Nrmse,
            native.SampleCount,
            (CudaNativeStatus)native.CudaStatus);
        return status;
    }

    public CudaNativeStatus GetCapabilities(
        nint context,
        out CudaBackendCapabilities capabilities)
    {
        CudaNativeStatus status = (CudaNativeStatus)_getCapabilities(context, out ulong nativeCapabilities);
        capabilities = (CudaBackendCapabilities)nativeCapabilities;
        return status;
    }

    public CudaNativeStatus ExecuteRfBatch(nint context, CudaRfBatchJob job)
    {
        using var pins = new PinnedArraySet();
        var native = new CudaNativeRfBatchJobV1
        {
            StructSize = (uint)sizeof(CudaNativeRfBatchJobV1),
            Flags = (uint)job.Flags,
            SampleCount = checked((uint)job.SampleCount),
            BatchCount = checked((uint)job.BatchCount),
            StreamIndex = job.StreamIndex,
            Mode = (uint)job.Mode,
            DemodPhaseScale = job.DemodPhaseScale,
            CvbsRawScale = job.CvbsRawScale,
            CvbsRawOffset = job.CvbsRawOffset,
            Input = pins.Pin(job.Input),
            RfVideoFilter = pins.Pin(job.RfVideoFilter),
            RfHighPassFilter = pins.Pin(job.RfHighPassFilter),
            MtfFilter = pins.Pin(job.MtfFilter),
            DemodVideoFilter = pins.Pin(job.DemodVideoFilter),
            DemodVideoLowPassFilter = pins.Pin(job.DemodVideoLowPassFilter),
            PreviousAnalyticPerBatch = pins.Pin(job.PreviousAnalyticPerBatch),
            LastAnalyticPerBatch = pins.Pin(job.LastAnalyticPerBatch),
            RfHighPassOutput = pins.Pin(job.RfHighPassOutput),
            AnalyticRealOutput = pins.Pin(job.AnalyticRealOutput),
            AnalyticImaginaryOutput = pins.Pin(job.AnalyticImaginaryOutput),
            EnvelopeOutput = pins.Pin(job.EnvelopeOutput),
            DemodRawOutput = pins.Pin(job.DemodRawOutput),
            VideoOutput = pins.Pin(job.VideoOutput),
            VideoLowPassOutput = pins.Pin(job.VideoLowPassOutput)
        };

        CudaNativeLdFrequencyOptionsV1 nativeLdOptions = default;
        if (job.LdFrequencyOptions is { } ldOptions)
        {
            nativeLdOptions = new CudaNativeLdFrequencyOptionsV1
            {
                StructSize = (uint)sizeof(CudaNativeLdFrequencyOptionsV1),
                Flags = 0,
                AudioLeftLowBin = checked((uint)ldOptions.AudioLeftLowBin),
                AudioLeftBinCount = checked((uint)ldOptions.AudioLeftBinCount),
                AudioRightLowBin = checked((uint)ldOptions.AudioRightLowBin),
                AudioRightBinCount = checked((uint)ldOptions.AudioRightBinCount),
                EfmFilter = pins.Pin(ldOptions.EfmFilter),
                AudioLeftFilter = pins.Pin(ldOptions.AudioLeftFilter),
                AudioRightFilter = pins.Pin(ldOptions.AudioRightFilter),
                EfmOutput = pins.Pin(ldOptions.EfmOutput),
                AudioLeftOutput = pins.Pin(ldOptions.AudioLeftOutput),
                AudioRightOutput = pins.Pin(ldOptions.AudioRightOutput)
            };
            ulong* reserved = native.Reserved;
            reserved[0] = unchecked((ulong)(nuint)(&nativeLdOptions));
        }

        return (CudaNativeStatus)_rfBatchExecute(context, ref native);
    }

    public string GetLastError(nint context)
    {
        Span<byte> buffer = stackalloc byte[ErrorBufferSize];
        buffer.Clear();
        fixed (byte* bufferPointer = buffer)
        {
            _ = _getLastError(context, bufferPointer, (ulong)buffer.Length);
        }

        int length = buffer.IndexOf((byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return Encoding.UTF8.GetString(buffer[..length]);
    }

    public void Dispose() => _library.Dispose();

    private static TDelegate Bind<TDelegate>(nint library, string exportName)
        where TDelegate : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, exportName, out nint export))
        {
            throw new EntryPointNotFoundException(exportName);
        }

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(export);
    }

    private static string ReadDeviceName(ref CudaNativeDeviceInfoV1 deviceInfo)
    {
        fixed (byte* name = deviceInfo.Name)
        {
            int length = 0;
            while (length < 256 && name[length] != 0)
            {
                length++;
            }

            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(name, length));
        }
    }

    private static ulong ReadAvailableGlobalMemory(ref CudaNativeDeviceInfoV1 deviceInfo)
    {
        fixed (uint* reserved = deviceInfo.Reserved)
        {
            return reserved[0] | ((ulong)reserved[1] << 32);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetAbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceCountDelegate(out int deviceCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceInfoDelegate(int deviceIndex, ref CudaNativeDeviceInfoV1 deviceInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateContextDelegate(int deviceIndex, out nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyContextDelegate(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SelfTestDelegate(nint context, ref CudaNativeSelfTestMetricsV1 metrics);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetLastErrorDelegate(nint context, byte* buffer, ulong bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetCapabilitiesDelegate(nint context, out ulong capabilities);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RfBatchExecuteDelegate(nint context, ref CudaNativeRfBatchJobV1 job);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct CudaNativeDeviceInfoV1
    {
        internal uint StructSize;
        internal int Ordinal;
        internal uint Flags;
        internal int ComputeCapabilityMajor;
        internal int ComputeCapabilityMinor;
        internal int DriverVersion;
        internal int RuntimeVersion;
        internal int CufftVersion;
        internal ulong TotalGlobalMemoryBytes;
        internal fixed byte Name[256];
        internal fixed uint Reserved[8];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct CudaNativeSelfTestMetricsV1
    {
        internal uint StructSize;
        internal uint Passed;
        internal double MaximumAbsoluteError;
        internal double Nrmse;
        internal ulong SampleCount;
        internal int CudaStatus;
        internal fixed uint Reserved[7];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct CudaNativeRfBatchJobV1
    {
        internal uint StructSize;
        internal uint Flags;
        internal uint SampleCount;
        internal uint BatchCount;
        internal uint StreamIndex;
        internal uint Mode;
        internal double DemodPhaseScale;
        internal double CvbsRawScale;
        internal double CvbsRawOffset;
        internal nint Input;
        internal nint RfVideoFilter;
        internal nint RfHighPassFilter;
        internal nint MtfFilter;
        internal nint DemodVideoFilter;
        internal nint DemodVideoLowPassFilter;
        internal nint PreviousAnalyticPerBatch;
        internal nint LastAnalyticPerBatch;
        internal nint RfHighPassOutput;
        internal nint AnalyticRealOutput;
        internal nint AnalyticImaginaryOutput;
        internal nint EnvelopeOutput;
        internal nint DemodRawOutput;
        internal nint VideoOutput;
        internal nint VideoLowPassOutput;
        internal fixed ulong Reserved[8];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct CudaNativeLdFrequencyOptionsV1
    {
        internal uint StructSize;
        internal uint Flags;
        internal uint AudioLeftLowBin;
        internal uint AudioLeftBinCount;
        internal uint AudioRightLowBin;
        internal uint AudioRightBinCount;
        internal fixed uint Reserved32[2];
        internal nint EfmFilter;
        internal nint AudioLeftFilter;
        internal nint AudioRightFilter;
        internal nint EfmOutput;
        internal nint AudioLeftOutput;
        internal nint AudioRightOutput;
        internal fixed ulong Reserved[8];
    }

    private sealed class PinnedArraySet : IDisposable
    {
        private readonly List<GCHandle> _handles = [];

        internal nint Pin(Array? values)
        {
            if (values is null)
            {
                return nint.Zero;
            }

            GCHandle handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            _handles.Add(handle);
            return handle.AddrOfPinnedObject();
        }

        public void Dispose()
        {
            for (int index = _handles.Count - 1; index >= 0; index--)
            {
                _handles[index].Free();
            }

            _handles.Clear();
        }
    }
}

internal sealed class NativeLibrarySafeHandle : SafeHandle
{
    internal NativeLibrarySafeHandle(nint nativeHandle)
        : base(nint.Zero, ownsHandle: true)
    {
        SetHandle(nativeHandle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeLibrary.Free(handle);
        return true;
    }
}
