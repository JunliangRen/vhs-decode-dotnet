using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Core.Dsp.Ipp;

internal sealed class IppComplexFft64SafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private IppComplexFft64SafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static IppComplexFft64SafeHandle FromNativeHandle(nint nativeHandle)
    {
        var result = new IppComplexFft64SafeHandle();
        result.SetHandle(nativeHandle);
        return result;
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return IppNativeMethods.ComplexFft64Destroy(handle) >= IppStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
