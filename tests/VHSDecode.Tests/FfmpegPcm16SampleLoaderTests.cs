using System.Buffers.Binary;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class FfmpegPcm16SampleLoaderTests
{
    [Fact(DisplayName = "Preview fast seek moves FFmpeg seek before the input only when requested")]
    public void PreviewFastSeekMovesSeekBeforeInputOnlyWhenRequested()
    {
        List<string> normal = FfmpegPcm16SampleLoader.BuildFfmpegArguments(
                "capture.ldf",
                80_000)
            .ToList();
        List<string> fast = FfmpegPcm16SampleLoader.BuildFfmpegArguments(
                "capture.ldf",
                80_000,
                fastInputSeek: true)
            .ToList();

        Assert.True(normal.IndexOf("-ss") > normal.IndexOf("-i"));
        Assert.True(fast.IndexOf("-ss") < fast.IndexOf("-i"));
        Assert.Equal("2", normal[normal.IndexOf("-ss") + 1]);
        Assert.Equal("2", fast[fast.IndexOf("-ss") + 1]);
    }

    [Fact(DisplayName = "FFmpeg PCM16 rewind remains exact across circular wrap and restart")]
    public void RewindRemainsExactAcrossCircularWrapAndRestart()
    {
        short[] source = Enumerable.Range(0, 32).Select(value => (short)value).ToArray();
        byte[] pcm = BuildPcm16Bytes(source);
        var opens = new List<long>();
        using var loader = new FfmpegPcm16SampleLoader(
            "capture.ldf",
            (_, startSample) =>
            {
                opens.Add(startSample);
                int byteOffset = checked((int)(startSample * sizeof(short)));
                return new MemoryStream(pcm[byteOffset..]);
            },
            rewindSize: 8);

        Assert.Equal([0.0, 1.0, 2.0], loader.Read(Stream.Null, 0, 3)!);
        Assert.Equal([2.0, 3.0, 4.0], loader.Read(Stream.Null, 2, 3)!);
        Assert.Equal([1.0, 2.0], loader.Read(Stream.Null, 1, 2)!);
        Assert.Equal([5.0, 6.0, 7.0, 8.0], loader.Read(Stream.Null, 5, 4)!);
        Assert.Equal([7.0, 8.0], loader.Read(Stream.Null, 7, 2)!);
        Assert.Equal([4.0], loader.Read(Stream.Null, 4, 1)!);
        Assert.Equal([0L, 4L], opens);
    }

    [Fact(DisplayName = "CUDA-fast FFmpeg PCM16 reads preserve native samples and rewind")]
    public void DirectInt16ReadsPreserveNativeSamplesAndRewind()
    {
        short[] source =
        [
            short.MinValue, -20_000, -1, 0, 1, 12_345, short.MaxValue, 77, 88, 99
        ];
        byte[] pcm = BuildPcm16Bytes(source);
        using var loader = new FfmpegPcm16SampleLoader(
            "capture.ldf",
            (_, startSample) =>
            {
                int byteOffset = checked((int)(startSample * sizeof(short)));
                return new MemoryStream(pcm[byteOffset..]);
            },
            rewindSize: 8);
        var first = new short[6];
        var rewind = new short[4];

        Assert.True(loader.TryReadInt16(Stream.Null, 0, first, out int firstRead));
        Assert.True(loader.TryReadInt16(Stream.Null, 2, rewind, out int rewindRead));

        Assert.Equal(6, firstRead);
        Assert.Equal(4, rewindRead);
        Assert.Equal(source.AsSpan(0, 6).ToArray(), first);
        Assert.Equal(source.AsSpan(2, 4).ToArray(), rewind);
    }

    [Fact(DisplayName = "FFmpeg PCM16 forward skip keeps an odd-byte rewind window exact")]
    public void ForwardSkipKeepsAnOddByteRewindWindowExact()
    {
        short[] source = Enumerable.Range(0, 24).Select(value => (short)value).ToArray();
        byte[] pcm = BuildPcm16Bytes(source);
        var opens = new List<long>();
        using var loader = new FfmpegPcm16SampleLoader(
            "capture.ldf",
            (_, startSample) =>
            {
                opens.Add(startSample);
                int byteOffset = checked((int)(startSample * sizeof(short)));
                return new MemoryStream(pcm[byteOffset..]);
            },
            rewindSize: 7,
            seekThreshold: 64);

        Assert.Equal([0.0], loader.Read(Stream.Null, 0, 1)!);
        Assert.Equal([10.0, 11.0], loader.Read(Stream.Null, 10, 2)!);
        Assert.Equal([9.0, 10.0], loader.Read(Stream.Null, 9, 2)!);
        Assert.Equal([8.0], loader.Read(Stream.Null, 8, 1)!);
        Assert.Equal([0L, 8L], opens);
    }

    [Fact(DisplayName = "FFmpeg PCM16 EOF retains bytes read before a short result")]
    public void EofRetainsBytesReadBeforeAShortResult()
    {
        byte[] pcm = BuildPcm16Bytes([1, 2, 3, 4, 5]);
        using var loader = new FfmpegPcm16SampleLoader(
            "capture.ldf",
            (_, startSample) =>
            {
                int byteOffset = checked((int)(startSample * sizeof(short)));
                return new ChunkedReadStream(pcm[byteOffset..], maximumReadSize: 3);
            },
            rewindSize: 8);

        Assert.Equal([1.0, 2.0, 3.0], loader.Read(Stream.Null, 0, 3)!);
        Assert.Null(loader.Read(Stream.Null, 3, 3));
        Assert.Equal([3.0, 4.0], loader.Read(Stream.Null, 2, 2)!);
        Assert.Null(loader.Read(Stream.Null, 5, 1));
    }

    [Fact(DisplayName = "FFmpeg PCM16 overlapping reads keep managed allocation bounded")]
    public void OverlappingReadsKeepManagedAllocationBounded()
    {
        ulong expectedHash = RunOverlappingReadWorkload(readCount: 96);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        ulong actualHash = RunOverlappingReadWorkload(readCount: 96);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(expectedHash, actualHash);
        Assert.True(
            allocated < 40_000_000,
            $"Overlapping FFmpeg PCM16 reads allocated {allocated:N0} bytes.");
    }

    private static ulong RunOverlappingReadWorkload(int readCount)
    {
        const int blockLength = 32 * 1024;
        const int blockStride = 30 * 1024;
        long sampleCount = checked(((long)(readCount - 1) * blockStride) + blockLength);
        using var loader = new FfmpegPcm16SampleLoader(
            "capture.ldf",
            (_, startSample) => new PatternPcmStream(startSample, sampleCount - startSample),
            rewindSize: 1024 * 1024);

        ulong hash = 14695981039346656037UL;
        for (int readIndex = 0; readIndex < readCount; readIndex++)
        {
            double[] data = loader.Read(Stream.Null, (long)readIndex * blockStride, blockLength)
                ?? throw new InvalidOperationException($"Read {readIndex} unexpectedly reached EOF.");
            hash = AddHash(hash, data[0]);
            hash = AddHash(hash, data[data.Length / 2]);
            hash = AddHash(hash, data[^1]);
        }

        return hash;
    }

    private static ulong AddHash(ulong hash, double value)
    {
        hash ^= unchecked((ushort)(short)value);
        return hash * 1099511628211UL;
    }

    private static byte[] BuildPcm16Bytes(IEnumerable<short> samples)
    {
        short[] values = samples.ToArray();
        var bytes = new byte[values.Length * sizeof(short)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), values[i]);
        }

        return bytes;
    }

    private sealed class ChunkedReadStream(byte[] bytes, int maximumReadSize) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, Math.Min(count, maximumReadSize));

        public override int Read(Span<byte> buffer)
            => _inner.Read(buffer[..Math.Min(buffer.Length, maximumReadSize)]);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class PatternPcmStream(long startSample, long sampleCount) : Stream
    {
        private readonly long _byteLength = checked(sampleCount * sizeof(short));
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _byteLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = checked((int)Math.Min(buffer.Length, _byteLength - _position));
            int written = 0;
            if ((_position & 1) != 0 && count > 0)
            {
                short value = SampleValue(startSample + (_position / sizeof(short)));
                buffer[written++] = unchecked((byte)(value >> 8));
                _position++;
            }

            while (written + 1 < count)
            {
                short value = SampleValue(startSample + (_position / sizeof(short)));
                BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(written, sizeof(short)), value);
                written += sizeof(short);
                _position += sizeof(short);
            }

            if (written < count)
            {
                short value = SampleValue(startSample + (_position / sizeof(short)));
                buffer[written++] = unchecked((byte)value);
                _position++;
            }

            return written;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static short SampleValue(long sample)
            => unchecked((short)((sample * 73) + 19));
    }
}
