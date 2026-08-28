using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Core.Dsp.Ipp;

internal sealed class IppFft32SafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private IppFft32SafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static IppFft32SafeHandle FromNativeHandle(nint nativeHandle)
    {
        var result = new IppFft32SafeHandle();
        result.SetHandle(nativeHandle);
        return result;
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return IppNativeMethods.Fft32Destroy(handle) >= IppStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
