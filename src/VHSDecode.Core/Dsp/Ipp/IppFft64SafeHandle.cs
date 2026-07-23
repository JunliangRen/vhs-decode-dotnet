using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Core.Dsp.Ipp;

internal sealed class IppFft64SafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private IppFft64SafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static IppFft64SafeHandle FromNativeHandle(nint nativeHandle)
    {
        var result = new IppFft64SafeHandle();
        result.SetHandle(nativeHandle);
        return result;
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return IppNativeMethods.Fft64Destroy(handle) >= IppStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
