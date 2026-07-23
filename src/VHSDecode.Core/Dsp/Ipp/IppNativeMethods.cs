using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.Dsp.Ipp;

internal static partial class IppNativeMethods
{
    internal const string LibraryName = "vhsdecode_ipp";
    internal const uint RequiredAbiVersion = 0x0001_0000;

    internal static readonly string[] RequiredExports =
    [
        "vhsdecode_ipp_get_abi_version",
        "vhsdecode_ipp_get_runtime_info",
        "vhsdecode_ipp_fft64_create",
        "vhsdecode_ipp_fft64_destroy",
        "vhsdecode_ipp_fft64_forward_real",
        "vhsdecode_ipp_fft64_inverse_real",
        "vhsdecode_ipp_complex64_multiply",
        "vhsdecode_ipp_complex64_magnitude_phase",
        "vhsdecode_ipp_iir64_create",
        "vhsdecode_ipp_iir64_destroy",
        "vhsdecode_ipp_iir64_reset",
        "vhsdecode_ipp_iir64_get_state",
        "vhsdecode_ipp_iir64_set_state",
        "vhsdecode_ipp_iir64_process",
        "vhsdecode_ipp_sos64_create",
        "vhsdecode_ipp_sos64_destroy",
        "vhsdecode_ipp_sos64_reset",
        "vhsdecode_ipp_sos64_get_state",
        "vhsdecode_ipp_sos64_set_state",
        "vhsdecode_ipp_sos64_process",
        "vhsdecode_ipp_status_string"
    ];

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_get_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_get_runtime_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int GetRuntimeInfo(IppRuntimeInfoNative* info);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_fft64_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Fft64Create(int length, out nint context);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_fft64_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Fft64Destroy(nint context);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_fft64_forward_real")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Fft64ForwardReal(
        IppFft64SafeHandle context,
        double* input,
        int inputLength,
        IppComplex64* output,
        int outputLength);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_fft64_inverse_real")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Fft64InverseReal(
        IppFft64SafeHandle context,
        IppComplex64* input,
        int inputLength,
        double* output,
        int outputLength);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_complex64_multiply")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Complex64Multiply(
        IppComplex64* left,
        IppComplex64* right,
        IppComplex64* output,
        int length);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_complex64_magnitude_phase")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Complex64MagnitudePhase(
        IppComplex64* input,
        double* magnitude,
        double* phase,
        int length);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_iir64_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Iir64Create(
        double* numerator,
        int numeratorLength,
        double* denominator,
        int denominatorLength,
        double* initialState,
        int initialStateLength,
        out nint context);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_iir64_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Iir64Destroy(nint context);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_iir64_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Iir64Reset(IppIir64SafeHandle context);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_iir64_get_state")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Iir64GetState(
        IppIir64SafeHandle context,
        double* state,
        int stateLength);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_iir64_set_state")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Iir64SetState(
        IppIir64SafeHandle context,
        double* state,
        int stateLength);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_iir64_process")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Iir64Process(
        IppIir64SafeHandle context,
        double* input,
        double* output,
        int length);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_sos64_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Sos64Create(
        IppSos64Section* sections,
        int sectionCount,
        double* initialState,
        int initialStateLength,
        out nint context);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_sos64_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Sos64Destroy(nint context);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_sos64_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Sos64Reset(IppSos64SafeHandle context);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_sos64_get_state")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Sos64GetState(
        IppSos64SafeHandle context,
        double* state,
        int stateLength);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_sos64_set_state")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Sos64SetState(
        IppSos64SafeHandle context,
        double* state,
        int stateLength);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_sos64_process")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Sos64Process(
        IppSos64SafeHandle context,
        double* input,
        double* output,
        int length);

    [LibraryImport(LibraryName, EntryPoint = "vhsdecode_ipp_status_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint StatusString(int status);
}
