using System.Buffers;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.Rf;

internal sealed class LibsndfilePcm16SampleLoader : IRfSampleLoader, IDisposable
{
    private readonly string _filename;
    private readonly Func<string, ILibsndfilePcm16Source> _openSource;
    private readonly IRfSampleLoader _fallback;
    private readonly object _gate = new();

    private ILibsndfilePcm16Source? _source;
    private long _positionFrames;
    private bool _fallbackActive;
    private bool _disposed;

    internal LibsndfilePcm16SampleLoader(string filename)
        : this(
            filename,
            LibsndfilePcm16Source.Open,
            new FfmpegPcm16SampleLoader(filename))
    {
    }

    internal LibsndfilePcm16SampleLoader(
        string filename,
        Func<string, ILibsndfilePcm16Source> openSource,
        IRfSampleLoader fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        _filename = filename;
        _openSource = openSource ?? throw new ArgumentNullException(nameof(openSource));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public double[]? Read(Stream stream, long sample, int readLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        ArgumentOutOfRangeException.ThrowIfNegative(readLength);
        if (readLength == 0)
        {
            return [];
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fallbackActive)
            {
                return _fallback.Read(stream, sample, readLength);
            }

            try
            {
                ILibsndfilePcm16Source source = _source ??= _openSource(_filename);
                return ReadNative(source, sample, readLength);
            }
            catch (LibsndfilePcm16FallbackException)
            {
                ActivateFallback();
                return _fallback.Read(stream, sample, readLength);
            }
        }
    }

    private double[]? ReadNative(
        ILibsndfilePcm16Source source,
        long sample,
        int readLength)
    {
        if (sample > source.Frames
            || readLength > source.Frames - sample)
        {
            return null;
        }

        if (sample != _positionFrames)
        {
            long position = source.Seek(sample);
            if (position != sample)
            {
                throw new LibsndfilePcm16FallbackException(
                    $"libsndfile sought to RF sample {position} instead of {sample}.");
            }

            _positionFrames = position;
        }

        short[] samples = ArrayPool<short>.Shared.Rent(readLength);
        try
        {
            long framesRead = source.ReadFrames(samples.AsSpan(0, readLength));
            if (framesRead < 0 || framesRead > readLength)
            {
                throw new LibsndfilePcm16FallbackException(
                    $"libsndfile returned an invalid RF frame count of {framesRead} for a {readLength}-frame read.");
            }

            _positionFrames += framesRead;
            if (framesRead != readLength)
            {
                return null;
            }

            double[] output = GC.AllocateUninitializedArray<double>(readLength);
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = samples[i];
            }

            return output;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(samples);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _source?.Dispose();
            }
            finally
            {
                if (_fallback is IDisposable disposableFallback)
                {
                    disposableFallback.Dispose();
                }
            }
        }
    }

    private void ActivateFallback()
    {
        _source?.Dispose();
        _source = null;
        _positionFrames = 0;
        _fallbackActive = true;
    }
}

internal interface ILibsndfilePcm16Source : IDisposable
{
    long Frames { get; }

    long Seek(long sample);

    long ReadFrames(Span<short> samples);
}

internal sealed class LibsndfilePcm16FallbackException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}

internal sealed unsafe partial class LibsndfilePcm16Source : ILibsndfilePcm16Source
{
    private const int FlacFormat = 0x170000;
    private const int Pcm16Format = 0x0002;
    private const int ReadMode = 0x10;
    private const int SeekSet = 0;
    private const int NoError = 0;
    private const int TypeMask = 0x0fff0000;
    private const int SubtypeMask = 0x0000ffff;

    private nint _file;
    private bool _disposed;

    private LibsndfilePcm16Source(nint file, long frames)
    {
        _file = file;
        Frames = frames;
    }

    public long Frames { get; }

    internal static ILibsndfilePcm16Source Open(string path)
    {
        var info = new SoundFileInfo();
        nint file;
        try
        {
            file = NativeMethods.Open(path, ReadMode, ref info);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw new LibsndfilePcm16FallbackException(
                "libsndfile is unavailable for raw FLAC RF input.",
                ex);
        }

        if (file == 0)
        {
            throw new LibsndfilePcm16FallbackException(
                $"libsndfile could not open raw FLAC RF input: {ErrorText(0)}");
        }

        bool supported = info.Frames > 0
            && info.SampleRate == FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz
            && info.Channels == 1
            && (info.Format & TypeMask) == FlacFormat
            && (info.Format & SubtypeMask) == Pcm16Format
            && info.Seekable != 0;
        if (!supported)
        {
            NativeMethods.Close(file);
            throw new LibsndfilePcm16FallbackException(
                "libsndfile raw FLAC RF input did not expose seekable 40 kHz mono PCM16 data.");
        }

        return new LibsndfilePcm16Source(file, info.Frames);
    }

    public long Seek(long sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeMethods.Seek(_file, sample, SeekSet);
    }

    public long ReadFrames(Span<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.IsEmpty)
        {
            return 0;
        }

        fixed (short* samplePointer = samples)
        {
            long framesRead = NativeMethods.ReadFramesShort(
                _file,
                samplePointer,
                samples.Length);
            if (NativeMethods.Error(_file) != NoError)
            {
                throw new LibsndfilePcm16FallbackException(
                    $"libsndfile failed while reading raw FLAC RF input: {ErrorText(_file)}");
            }

            return framesRead;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        nint file = _file;
        _file = 0;
        if (file != 0)
        {
            NativeMethods.Close(file);
        }
    }

    private static bool IsUnavailable(Exception exception)
        => exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

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

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_wchar_open",
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint Open(
            string path,
            int mode,
            ref SoundFileInfo info);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_seek")]
        internal static partial long Seek(
            nint file,
            long frames,
            int whence);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_readf_short")]
        internal static partial long ReadFramesShort(
            nint file,
            short* samples,
            long frames);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_error")]
        internal static partial int Error(nint file);

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
