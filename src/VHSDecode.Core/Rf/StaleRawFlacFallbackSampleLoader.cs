using System.ComponentModel;

namespace VHSDecode.Core.Rf;

// Keep the native prefix and its existing FFmpeg transition. A wrapped 32-bit
// STREAMINFO total must not turn physical native reads into PyAV restart reads.
internal sealed class StaleRawFlacFallbackSampleLoader : IRfSampleLoader, IDisposable
{
    private const int MaximumTailBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly Lazy<IRfSampleLoader> _loader;
    private bool _disposed;
    private CancellationToken? _readAheadToken;

    internal StaleRawFlacFallbackSampleLoader(string filename)
        : this(() => CreateFallback(filename))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
    }

    internal StaleRawFlacFallbackSampleLoader(Func<IRfSampleLoader> createLoader)
    {
        ArgumentNullException.ThrowIfNull(createLoader);
        _loader = new Lazy<IRfSampleLoader>(
            createLoader,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal FfmpegPcm16SeekMode? SelectedSeekMode
    {
        get
        {
            lock (_gate)
            {
                return _loader.IsValueCreated && _loader.Value is FfmpegPcm16SampleLoader ffmpeg
                    ? ffmpeg.SeekMode
                    : null;
            }
        }
    }

    internal void EnableMappedReadAhead(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loader.IsValueCreated)
            {
                throw new InvalidOperationException("Mapped PCM read-ahead must be selected before opening the fallback.");
            }

            _readAheadToken = cancellationToken;
        }
    }

    internal static StaleRawFlacFallbackSampleLoader? GetMappedFallback(IRfSampleLoader loader)
        => loader switch
        {
            LibsndfilePcm16SampleLoader native => native.MappedReadAheadFallback,
            StaleRawFlacFallbackSampleLoader mapped => mapped,
            _ => null
        };

    internal FfmpegPcm16SampleLoader? ActiveMappedReadAhead
    {
        get
        {
            lock (_gate)
            {
                return _loader.IsValueCreated && _loader.Value is FfmpegPcm16SampleLoader { HasActiveMappedReadAhead: true } mapped
                    ? mapped
                    : null;
            }
        }
    }

    public double[]? Read(Stream stream, long sample, int readLength)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(sample);
            ArgumentOutOfRangeException.ThrowIfNegative(readLength);
            if (readLength == 0)
            {
                return [];
            }

            IRfSampleLoader loader = _loader.Value;
            if (_readAheadToken is { } cancellationToken)
            {
                _readAheadToken = null;
                if (loader is FfmpegPcm16SampleLoader { SeekMode: FfmpegPcm16SeekMode.MappedIndexedRawFlac } mapped)
                {
                    mapped.EnableMappedReadAhead(cancellationToken);
                }
            }

            return loader.Read(stream, sample, readLength);
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
            if (_loader.IsValueCreated && _loader.Value is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    internal static bool HasWrappedSampleCount(RawFlacStreamInfo info, long indexedTotal)
        => IsFixedNativeFlac(info)
            && indexedTotal > uint.MaxValue
            && indexedTotal > info.TotalSamples
            && (indexedTotal & uint.MaxValue) == info.TotalSamples;

    private static bool IsFixedNativeFlac(RawFlacStreamInfo info)
        => info.IsNativeRfPcm16
            && info.FixedBlockSize.HasValue
            && info.MetadataChainComplete
            && info.UsesFixedBlockingStrategy
            && !info.HasSeekTable;

    private static IRfSampleLoader CreateFallback(string filename)
    {
        if (RawFlacStreamInfo.TryRead(filename, out RawFlacStreamInfo info)
            && IsFixedNativeFlac(info)
            && RawFlacFrameIndex.TryOpen(filename, out RawFlacFrameIndex? index)
            && index is not null
            && HasWrappedSampleCount(info, index.TotalSamples)
            && HasDecodableTail(filename, index))
        {
            return new FfmpegPcm16SampleLoader(
                filename,
                index,
                new PyAvRawFlacSampleMapper(info.SampleRateHz, info.FixedBlockSize!.Value));
        }

        return new FfmpegPcm16SampleLoader(filename);
    }

    internal static bool HasDecodableTail(string filename, RawFlacFrameIndex index)
    {
        if (index.Metadata.Length > MaximumTailBytes || !HasValidTailChecksum(filename, index))
        {
            return false;
        }

        try
        {
            // Exercise the same indexed decoder used by the replacement, through
            // EOF. A zero CRC residue alone can miss a truncated final zero byte.
            using var probe = new FfmpegPcm16SampleLoader(filename, fastInputSeek: true);
            int length = index.LastFrame.BlockSize;
            long sample = index.LastFrame.StartSample;
            if (probe.Read(Stream.Null, sample, length)?.Length != length)
            {
                return false;
            }

            // The index can end at the last full block before a short final one.
            // Reading one more full block drains that short tail and its errors.
            return probe.Read(Stream.Null, checked(sample + length), length) is null;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            || exception is NotSupportedException { InnerException: Win32Exception })
        {
            return false;
        }
    }

    internal static bool HasValidTailChecksum(string filename, RawFlacFrameIndex index)
    {
        long tailLength = index.FileLength - index.LastFrame.ByteOffset;
        if (tailLength < 2 || tailLength > MaximumTailBytes)
        {
            return false;
        }

        try
        {
            using var input = new FileStream(
                filename,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (input.Length != index.FileLength)
            {
                return false;
            }

            var tail = new byte[(int)tailLength];
            input.Position = index.LastFrame.ByteOffset;
            input.ReadExactly(tail);
            // Complete FLAC frames, including their CRC16, have a zero residue.
            // This checksum screen declines ordinary damaged or partial tails.
            ushort crc = 0;
            foreach (byte value in tail)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x8005 : 0));
                }
            }

            return crc == 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }
}
