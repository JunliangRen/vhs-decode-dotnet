using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.Dsp;

public static unsafe class IppRuntime
{
    private static readonly Lazy<ProbeResult> DefaultProbe = new(
        static () => ProbeCore(IppNativeMethods.LibraryName),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IppRuntimeInfo ProbeRequired()
    {
        ProbeResult result = DefaultProbe.Value;
        if (result.Info is not null)
        {
            return result.Info;
        }

        throw result.CreateException();
    }

    public static IppRuntimeInfo RequireAvailable() => ProbeRequired();

    public static bool TryProbe(out IppRuntimeInfo? info)
    {
        ProbeResult result = DefaultProbe.Value;
        info = result.Info;
        return info is not null;
    }

    internal static IppRuntimeInfo ProbeRequiredForLibraryName(string nativeLibrary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeLibrary);
        ProbeResult result = ProbeCore(nativeLibrary);
        return result.Info ?? throw result.CreateException();
    }

    internal static IppBackendUnavailableException CreateLoaderException(Exception exception)
        => new(
            exception is BadImageFormatException
                ? IppBackendFailureKind.NativeLibraryInvalid
                : IppBackendFailureKind.NativeLibraryNotFound,
            $"native library '{IppNativeMethods.LibraryName}' could not be loaded ({exception.Message}).",
            exception);

    private static ProbeResult ProbeCore(string nativeLibrary)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return ProbeResult.Failed(
                IppBackendFailureKind.UnsupportedProcessArchitecture,
                $"the v1 native backend requires an x64 process, but this process is {RuntimeInformation.ProcessArchitecture}.");
        }

        nint libraryHandle = 0;
        try
        {
            if (!NativeLibrary.TryLoad(
                    nativeLibrary,
                    typeof(IppRuntime).Assembly,
                    DllImportSearchPath.SafeDirectories | DllImportSearchPath.AssemblyDirectory,
                    out libraryHandle))
            {
                return ProbeResult.Failed(
                    IppBackendFailureKind.NativeLibraryNotFound,
                    $"native library '{nativeLibrary}', or one of its IPP dependencies, could not be loaded. Install or deploy the matching x64 vhsdecode IPP runtime.");
            }

            foreach (string export in IppNativeMethods.RequiredExports)
            {
                if (!NativeLibrary.TryGetExport(libraryHandle, export, out _))
                {
                    return ProbeResult.Failed(
                        IppBackendFailureKind.MissingExport,
                        $"native library '{nativeLibrary}' does not export required ABI symbol '{export}'.");
                }
            }

            NativeLibrary.TryGetExport(libraryHandle, "vhsdecode_ipp_get_abi_version", out nint abiAddress);
            var getAbiVersion = (delegate* unmanaged[Cdecl]<uint>)abiAddress;
            uint abiVersion = getAbiVersion();
            if (abiVersion != IppNativeMethods.RequiredAbiVersion)
            {
                return ProbeResult.Failed(
                    IppBackendFailureKind.AbiVersionMismatch,
                    $"native library '{nativeLibrary}' reports ABI 0x{abiVersion:X8}; ABI 0x{IppNativeMethods.RequiredAbiVersion:X8} is required.");
            }

            NativeLibrary.TryGetExport(libraryHandle, "vhsdecode_ipp_get_runtime_info", out nint infoAddress);
            var getRuntimeInfo = (delegate* unmanaged[Cdecl]<IppRuntimeInfoNative*, int>)infoAddress;
            IppRuntimeInfoNative nativeInfo = IppRuntimeInfoNative.Create();
            int status = getRuntimeInfo(&nativeInfo);
            if (status < IppStatus.Success)
            {
                string statusText = ReadStatus(libraryHandle, status);
                return ProbeResult.Failed(
                    IppBackendFailureKind.RuntimeRejected,
                    $"native runtime initialization failed with status {status} ({statusText}). The CPU may not be supported by this IPP build.");
            }

            if (nativeInfo.StructSize != IppRuntimeInfoNative.ExpectedSize)
            {
                return ProbeResult.Failed(
                    IppBackendFailureKind.AbiVersionMismatch,
                    $"native runtime-info size is {nativeInfo.StructSize}; {IppRuntimeInfoNative.ExpectedSize} bytes are required.");
            }

            if (nativeInfo.AbiVersion != IppNativeMethods.RequiredAbiVersion)
            {
                return ProbeResult.Failed(
                    IppBackendFailureKind.AbiVersionMismatch,
                    $"native runtime-info reports ABI 0x{nativeInfo.AbiVersion:X8}; ABI 0x{IppNativeMethods.RequiredAbiVersion:X8} is required.");
            }

            if (nativeInfo.IppInitStatus < IppStatus.Success)
            {
                return ProbeResult.Failed(
                    IppBackendFailureKind.RuntimeRejected,
                    $"Intel IPP initialization failed with status {nativeInfo.IppInitStatus}. The CPU may not be supported by this IPP build.");
            }

            return ProbeResult.Succeeded(new IppRuntimeInfo(nativeLibrary, in nativeInfo));
        }
        catch (Exception exception) when (exception is BadImageFormatException
            or DllNotFoundException
            or EntryPointNotFoundException
            or TypeInitializationException)
        {
            return ProbeResult.Failed(
                exception is BadImageFormatException
                    ? IppBackendFailureKind.NativeLibraryInvalid
                    : IppBackendFailureKind.NativeLibraryNotFound,
                $"native library '{nativeLibrary}' could not be loaded ({exception.Message}).",
                exception);
        }
        finally
        {
            if (libraryHandle != 0)
            {
                NativeLibrary.Free(libraryHandle);
            }
        }
    }

    private static string ReadStatus(nint libraryHandle, int status)
    {
        if (!NativeLibrary.TryGetExport(libraryHandle, "vhsdecode_ipp_status_string", out nint statusAddress))
        {
            return "unknown native status";
        }

        var getStatusString = (delegate* unmanaged[Cdecl]<int, nint>)statusAddress;
        return Marshal.PtrToStringUTF8(getStatusString(status)) ?? "unknown native status";
    }

    private sealed record ProbeResult(
        IppRuntimeInfo? Info,
        IppBackendFailureKind FailureKind,
        string? FailureDetail,
        Exception? InnerException)
    {
        internal static ProbeResult Succeeded(IppRuntimeInfo info)
            => new(info, default, null, null);

        internal static ProbeResult Failed(
            IppBackendFailureKind failureKind,
            string failureDetail,
            Exception? innerException = null)
            => new(null, failureKind, failureDetail, innerException);

        internal IppBackendUnavailableException CreateException()
            => new(
                FailureKind,
                FailureDetail ?? "the native runtime probe failed.",
                InnerException);
    }
}
