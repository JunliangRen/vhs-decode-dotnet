using System.Buffers;
using System.ComponentModel;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.Rf;

internal sealed class LibsndfilePcm16SampleLoader : IReusableRfSampleLoader, IDisposable
{
    private readonly record struct MappedReadPlan(
        long PhysicalSample,
        long PhysicalOffset,
        bool Restart);

    internal const int MaximumRetainedDecodedBufferLength = 32 * 1024;
    internal const int MaximumRetainedDecodedBufferCount = 48;
    private const int RewindCapacityFrames = FfmpegPcm16SampleLoader.DefaultRewindSize / sizeof(short);
    private const int SeekThresholdFrames = FfmpegPcm16SampleLoader.DefaultSeekThreshold / sizeof(short);
    private readonly string _filename;
    private readonly Func<string, ILibsndfilePcm16Source> _openSource;
    private readonly IRfSampleLoader _fallback;
    private readonly PyAvRawFlacSampleMapper? _sampleMapper;
    private readonly object _gate = new();
    private readonly object _decodedBufferLock = new();
    private readonly double[]?[] _decodedBuffers =
        new double[]?[MaximumRetainedDecodedBufferCount];

    private ILibsndfilePcm16Source? _source;
    private long _positionFrames;
    private long _logicalPositionFrames;
    private long _rewindStartFrames;
    private long _physicalOffsetFrames;
    private int _decodedBufferCount;
    private bool _mappedDecoderStarted;
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
        PyAvRawFlacSampleMapper sampleMapper)
        : this(
            filename,
            LibsndfilePcm16Source.Open,
            new FfmpegPcm16SampleLoader(filename),
            sampleMapper)
    {
    }

    internal LibsndfilePcm16SampleLoader(
        string filename,
        Func<string, ILibsndfilePcm16Source> openSource,
        IRfSampleLoader fallback,
        PyAvRawFlacSampleMapper? sampleMapper = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        _filename = filename;
        _openSource = openSource ?? throw new ArgumentNullException(nameof(openSource));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _sampleMapper = sampleMapper;
    }

    public double[]? Read(Stream stream, long sample, int readLength)
        => ReadCore(stream, sample, readLength, reuseDecodedBuffer: false);

    bool IReusableRfSampleLoader.ReuseForSequentialDecode => false;

    internal double[]? ReadReusable(Stream stream, long sample, int readLength)
        => ReadCore(stream, sample, readLength, reuseDecodedBuffer: true);

    double[]? IReusableRfSampleLoader.ReadReusable(
        Stream stream,
        long sample,
        int readLength)
        => ReadReusable(stream, sample, readLength);

    internal int CachedReusableDecodedBufferCount
    {
        get
        {
            lock (_decodedBufferLock)
            {
                return _decodedBufferCount;
            }
        }
    }

    internal bool UsesPyAvMappedSeeking => _sampleMapper is not null;

    internal void ReturnReusable(double[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_decodedBufferLock)
        {
            if (!_disposed
                && buffer.Length <= MaximumRetainedDecodedBufferLength
                && _decodedBufferCount < _decodedBuffers.Length)
            {
                _decodedBuffers[_decodedBufferCount++] = buffer;
            }
        }
    }

    void IReusableRfSampleLoader.ReturnReusable(double[] buffer)
        => ReturnReusable(buffer);

    private double[]? ReadCore(
        Stream stream,
        long sample,
        int readLength,
        bool reuseDecodedBuffer)
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
                return ReadFallback(stream, sample, readLength, reuseDecodedBuffer);
            }

            try
            {
                ILibsndfilePcm16Source source = _source ??= _openSource(_filename);
                if (_sampleMapper is null)
                {
                    if (sample > source.Frames
                        || readLength > source.Frames - sample)
                    {
                        return ReadAtNativeLengthBoundary(
                            stream,
                            sample,
                            readLength,
                            reuseDecodedBuffer);
                    }

                    return ReadNative(source, sample, readLength, reuseDecodedBuffer);
                }

                if (!TryResolveMappedRead(sample, readLength, out MappedReadPlan mappedRead))
                {
                    throw new LibsndfilePcm16FallbackException(
                        $"Could not map PyAV RF sample {sample} to a native FLAC position.");
                }

                if (mappedRead.PhysicalSample > source.Frames
                    || readLength > source.Frames - mappedRead.PhysicalSample)
                {
                    return ReadAtNativeLengthBoundary(
                        stream,
                        sample,
                        readLength,
                        reuseDecodedBuffer);
                }

                return ReadMappedNative(
                    source,
                    sample,
                    readLength,
                    reuseDecodedBuffer,
                    mappedRead);
            }
            catch (LibsndfilePcm16FallbackException)
            {
                ActivateFallback();
                return ReadFallback(stream, sample, readLength, reuseDecodedBuffer);
            }
        }
    }

    private bool TryResolveMappedRead(
        long logicalSample,
        int readLength,
        out MappedReadPlan mappedRead)
    {
        mappedRead = default;
        if (logicalSample > long.MaxValue - readLength)
        {
            return false;
        }

        bool restart = !_mappedDecoderStarted
            || logicalSample < _rewindStartFrames
            || (logicalSample > _logicalPositionFrames
                && logicalSample - _logicalPositionFrames > SeekThresholdFrames);
        try
        {
            long physicalSample;
            long physicalOffset;
            if (restart)
            {
                if (!(_sampleMapper
                        ?? throw new InvalidOperationException(
                            "Mapped FLAC seeking is not configured."))
                    .TryMapRestartSample(logicalSample, out physicalSample))
                {
                    return false;
                }

                physicalOffset = checked(physicalSample - logicalSample);
            }
            else
            {
                physicalOffset = _physicalOffsetFrames;
                physicalSample = checked(logicalSample + physicalOffset);
            }

            if (physicalSample < 0)
            {
                return false;
            }

            mappedRead = new MappedReadPlan(
                physicalSample,
                physicalOffset,
                restart);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private double[]? ReadNative(
        ILibsndfilePcm16Source source,
        long sample,
        int readLength,
        bool reuseDecodedBuffer)
    {
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

            double[] output = reuseDecodedBuffer
                ? TakeDecodedBuffer(readLength)
                : GC.AllocateUninitializedArray<double>(readLength);
            ConvertPcm16ToDouble(samples.AsSpan(0, readLength), output);

            return output;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(samples);
        }
    }

    private double[]? ReadMappedNative(
        ILibsndfilePcm16Source source,
        long logicalSample,
        int readLength,
        bool reuseDecodedBuffer,
        MappedReadPlan mappedRead)
    {
        if (mappedRead.PhysicalSample != _positionFrames)
        {
            long position = source.Seek(mappedRead.PhysicalSample);
            if (position != mappedRead.PhysicalSample)
            {
                throw new LibsndfilePcm16FallbackException(
                    $"libsndfile sought to FLAC sample {position} instead of {mappedRead.PhysicalSample}.");
            }

            _positionFrames = position;
        }

        if (mappedRead.Restart)
        {
            _mappedDecoderStarted = true;
            _physicalOffsetFrames = mappedRead.PhysicalOffset;
            _logicalPositionFrames = logicalSample;
            _rewindStartFrames = logicalSample;
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
            UpdateMappedDecoderWindow(logicalSample, framesRead);
            if (framesRead != readLength)
            {
                return null;
            }

            double[] output = reuseDecodedBuffer
                ? TakeDecodedBuffer(readLength)
                : GC.AllocateUninitializedArray<double>(readLength);
            ConvertPcm16ToDouble(samples.AsSpan(0, readLength), output);
            return output;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(samples);
        }
    }

    private void UpdateMappedDecoderWindow(long logicalSample, long framesRead)
    {
        long end = checked(logicalSample + framesRead);
        if (end > _logicalPositionFrames)
        {
            _logicalPositionFrames = end;
            _rewindStartFrames = Math.Max(
                _rewindStartFrames,
                _logicalPositionFrames - RewindCapacityFrames);
        }
    }

    internal static unsafe void ConvertPcm16ToDouble(
        ReadOnlySpan<short> source,
        Span<double> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException(
                "PCM16 conversion destination is shorter than the source.",
                nameof(destination));
        }

        int index = 0;
        if (Avx2.IsSupported)
        {
            fixed (short* sourcePointer = source)
            fixed (double* destinationPointer = destination)
            {
                int vectorizedEnd = source.Length - (source.Length % 8);
                for (; index < vectorizedEnd; index += 8)
                {
                    var integers = Avx2.ConvertToVector256Int32(
                        Sse2.LoadVector128(sourcePointer + index));
                    Avx.Store(
                        destinationPointer + index,
                        Avx.ConvertToVector256Double(integers.GetLower()));
                    Avx.Store(
                        destinationPointer + index + 4,
                        Avx.ConvertToVector256Double(integers.GetUpper()));
                }
            }
        }

        for (; index < source.Length; index++)
        {
            destination[index] = source[index];
        }
    }

    private double[] TakeDecodedBuffer(int length)
    {
        lock (_decodedBufferLock)
        {
            for (int i = _decodedBufferCount - 1; i >= 0; i--)
            {
                double[] candidate = _decodedBuffers[i]!;
                if (candidate.Length == length)
                {
                    int last = --_decodedBufferCount;
                    _decodedBuffers[i] = _decodedBuffers[last];
                    _decodedBuffers[last] = null;
                    return candidate;
                }
            }
        }

        return GC.AllocateUninitializedArray<double>(length);
    }

    private double[]? ReadFallback(
        Stream stream,
        long sample,
        int readLength,
        bool reuseDecodedBuffer)
    {
        double[]? result = _fallback.Read(stream, sample, readLength);
        if (!reuseDecodedBuffer || result is null)
        {
            return result;
        }

        double[] output = TakeDecodedBuffer(result.Length);
        result.AsSpan().CopyTo(output);
        return output;
    }

    private double[]? ReadAtNativeLengthBoundary(
        Stream stream,
        long sample,
        int readLength,
        bool reuseDecodedBuffer)
    {
        try
        {
            double[]? result = _fallback.Read(stream, sample, readLength);
            ActivateFallback();
            if (!reuseDecodedBuffer || result is null)
            {
                return result;
            }

            double[] output = TakeDecodedBuffer(result.Length);
            result.AsSpan().CopyTo(output);
            return output;
        }
        catch (NotSupportedException ex) when (ex.InnerException is Win32Exception)
        {
            // A clean native file must still reach EOF when FFmpeg is not installed.
            return null;
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

                lock (_decodedBufferLock)
                {
                    Array.Clear(_decodedBuffers);
                    _decodedBufferCount = 0;
                }
            }
        }
    }

    private void ActivateFallback()
    {
        _source?.Dispose();
        _source = null;
        _positionFrames = 0;
        _logicalPositionFrames = 0;
        _rewindStartFrames = 0;
        _physicalOffsetFrames = 0;
        _mappedDecoderStarted = false;
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
