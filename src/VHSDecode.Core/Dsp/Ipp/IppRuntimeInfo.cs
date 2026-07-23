using System.Runtime.InteropServices;

namespace VHSDecode.Core.Dsp.Ipp;

public sealed record IppRuntimeInfo
{
    internal IppRuntimeInfo(string nativeLibrary, in IppRuntimeInfoNative native)
    {
        NativeLibrary = nativeLibrary;
        AbiVersion = native.AbiVersion;
        IppInitStatus = native.IppInitStatus;
        IppMajor = native.IppMajor;
        IppMinor = native.IppMinor;
        IppUpdate = native.IppUpdate;
        IppBuild = native.IppBuild;
        CpuFeatures = native.CpuFeatures;
        EnabledCpuFeatures = native.EnabledCpuFeatures;
        IppName = native.GetIppName();
        IppVersion = native.GetIppVersion();
        IppBuildDate = native.GetIppBuildDate();
        IppTargetCpu = native.GetIppTargetCpu();
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture;
    }

    public string NativeLibrary { get; }
    public uint AbiVersion { get; }
    public int IppInitStatus { get; }
    public int IppMajor { get; }
    public int IppMinor { get; }
    public int IppUpdate { get; }
    public int IppBuild { get; }
    public ulong CpuFeatures { get; }
    public ulong EnabledCpuFeatures { get; }
    public string IppName { get; }
    public string IppVersion { get; }
    public string IppBuildDate { get; }
    public string IppTargetCpu { get; }
    public Architecture ProcessArchitecture { get; }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IppRuntimeInfoNative
{
    internal const int IppNameLength = 64;
    internal const int IppVersionLength = 64;
    internal const int IppBuildDateLength = 32;
    internal const int IppTargetCpuLength = 32;
    internal const int ExpectedSize = 240;

    internal uint StructSize;
    internal uint AbiVersion;
    internal int IppInitStatus;
    internal int IppMajor;
    internal int IppMinor;
    internal int IppUpdate;
    internal int IppBuild;
    internal uint Reserved0;
    internal ulong CpuFeatures;
    internal ulong EnabledCpuFeatures;
    internal fixed byte IppName[IppNameLength];
    internal fixed byte IppVersion[IppVersionLength];
    internal fixed byte IppBuildDate[IppBuildDateLength];
    internal fixed byte IppTargetCpu[IppTargetCpuLength];

    internal static IppRuntimeInfoNative Create()
        => new()
        {
            StructSize = ExpectedSize,
            AbiVersion = IppNativeMethods.RequiredAbiVersion
        };

    internal readonly string GetIppName()
    {
        fixed (byte* pointer = IppName)
        {
            return ReadUtf8(pointer, IppNameLength);
        }
    }

    internal readonly string GetIppVersion()
    {
        fixed (byte* pointer = IppVersion)
        {
            return ReadUtf8(pointer, IppVersionLength);
        }
    }

    internal readonly string GetIppBuildDate()
    {
        fixed (byte* pointer = IppBuildDate)
        {
            return ReadUtf8(pointer, IppBuildDateLength);
        }
    }

    internal readonly string GetIppTargetCpu()
    {
        fixed (byte* pointer = IppTargetCpu)
        {
            return ReadUtf8(pointer, IppTargetCpuLength);
        }
    }

    private static string ReadUtf8(byte* pointer, int capacity)
    {
        int length = 0;
        while (length < capacity && pointer[length] != 0)
        {
            length++;
        }

        return Marshal.PtrToStringUTF8((nint)pointer, length) ?? string.Empty;
    }
}
