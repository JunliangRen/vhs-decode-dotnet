using System.Runtime.InteropServices;

namespace VHSDecode.Core.Dsp.Ipp;

public sealed class IppNativeException : InvalidOperationException
{
    internal IppNativeException(string operation, int statusCode, string statusText)
        : base($"Native IPP operation '{operation}' failed with status {statusCode}: {statusText}")
    {
        Operation = operation;
        StatusCode = statusCode;
        StatusText = statusText;
    }

    public string Operation { get; }
    public int StatusCode { get; }
    public string StatusText { get; }
}

internal static class IppStatus
{
    internal const int Success = 0;

    internal static void ThrowIfFailed(int statusCode, string operation)
    {
        if (statusCode >= Success)
        {
            return;
        }

        string statusText;
        try
        {
            nint pointer = IppNativeMethods.StatusString(statusCode);
            statusText = Marshal.PtrToStringUTF8(pointer) ?? "unknown native status";
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            statusText = "native status text is unavailable";
        }

        throw new IppNativeException(operation, statusCode, statusText);
    }
}
