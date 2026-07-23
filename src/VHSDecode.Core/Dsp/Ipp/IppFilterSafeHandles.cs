using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Core.Dsp.Ipp;

internal sealed class IppIir64SafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private IppIir64SafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static IppIir64SafeHandle FromNativeHandle(nint nativeHandle)
    {
        var result = new IppIir64SafeHandle();
        result.SetHandle(nativeHandle);
        return result;
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return IppNativeMethods.Iir64Destroy(handle) >= IppStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class IppSos64SafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private IppSos64SafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static IppSos64SafeHandle FromNativeHandle(nint nativeHandle)
    {
        var result = new IppSos64SafeHandle();
        result.SetHandle(nativeHandle);
        return result;
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return IppNativeMethods.Sos64Destroy(handle) >= IppStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
