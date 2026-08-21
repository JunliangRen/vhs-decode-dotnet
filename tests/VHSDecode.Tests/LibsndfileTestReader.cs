using System.Runtime.InteropServices;

namespace VHSDecode.Tests;

internal sealed record LibsndfileReadResult(
    long Frames,
    int SampleRate,
    int Channels,
    int Format,
    int[] Samples);

internal static unsafe partial class LibsndfileTestReader
{
    private const int ReadMode = 0x10;

    public static LibsndfileReadResult ReadPcm32(string path)
    {
        var info = new SoundFileInfo();
        nint file = NativeMethods.Open(path, ReadMode, ref info);
        if (file == 0)
        {
            throw new InvalidDataException(
                $"libsndfile failed to open test input: {ErrorText(0)}");
        }

        try
        {
            int sampleCount = checked((int)(info.Frames * info.Channels));
            var samples = new int[sampleCount];
            fixed (int* samplePointer = samples)
            {
                long framesRead = NativeMethods.ReadFramesInt(
                    file,
                    samplePointer,
                    info.Frames);
                if (framesRead != info.Frames)
                {
                    throw new InvalidDataException(
                        $"libsndfile returned {framesRead} of {info.Frames} test frames: "
                        + ErrorText(file));
                }
            }

            return new LibsndfileReadResult(
                info.Frames,
                info.SampleRate,
                info.Channels,
                info.Format,
                samples);
        }
        finally
        {
            NativeMethods.Close(file);
        }
    }

    private static string ErrorText(nint file)
        => Marshal.PtrToStringUTF8(NativeMethods.StrError(file))
            ?? "unknown libsndfile error";

    [StructLayout(LayoutKind.Sequential)]
    private struct SoundFileInfo
    {
        public long Frames;
        public int SampleRate;
        public int Channels;
        public int Format;
        public int Sections;
        public int Seekable;
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "sndfile";

        internal static nint Open(
            string path,
            int mode,
            ref SoundFileInfo info)
            => OperatingSystem.IsWindows()
                ? OpenWindows(path, mode, ref info)
                : OpenUnix(path, mode, ref info);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_wchar_open",
            StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint OpenWindows(
            string path,
            int mode,
            ref SoundFileInfo info);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_open",
            StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint OpenUnix(
            string path,
            int mode,
            ref SoundFileInfo info);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_readf_int")]
        internal static partial long ReadFramesInt(
            nint file,
            int* samples,
            long frames);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_close")]
        internal static partial int Close(nint file);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_strerror")]
        internal static partial nint StrError(nint file);
    }
}
