using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Core.Dsp.Ipp;

internal sealed class IppDft32SafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private IppDft32SafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static IppDft32SafeHandle FromNativeHandle(nint nativeHandle)
    {
        var result = new IppDft32SafeHandle();
        result.SetHandle(nativeHandle);
        return result;
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return IppNativeMethods.Dft32Destroy(handle) >= IppStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
