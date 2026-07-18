using System.Buffers.Binary;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class FfmpegStreamSampleLoaderTests
{
    [Fact(DisplayName = "FFmpeg stream rewind remains exact across circular wrap")]
    public void RewindRemainsExactAcrossCircularWrap()
    {
        using var decoded = new MemoryStream(BuildPcm16Bytes(Enumerable.Range(0, 16).Select(value => (short)value)));
        using var loader = new FfmpegStreamSampleLoader([], [], _ => decoded, rewindSize: 8);

        Assert.Equal([0.0, 1.0, 2.0], loader.Read(Stream.Null, 0, 3)!);
        Assert.Equal([2.0, 3.0, 4.0], loader.Read(Stream.Null, 2, 3)!);
        Assert.Equal([1.0, 2.0], loader.Read(Stream.Null, 1, 2)!);
        Assert.Equal([5.0, 6.0, 7.0, 8.0], loader.Read(Stream.Null, 5, 4)!);
        Assert.Equal([7.0, 8.0], loader.Read(Stream.Null, 7, 2)!);
        Assert.Throws<IOException>(() => loader.Read(Stream.Null, 4, 1));
    }

    [Fact(DisplayName = "FFmpeg stream forward skip keeps the configured rewind window")]
    public void ForwardSkipKeepsConfiguredRewindWindow()
    {
        using var decoded = new MemoryStream(BuildPcm16Bytes(Enumerable.Range(0, 24).Select(value => (short)value)));
        using var loader = new FfmpegStreamSampleLoader([], [], _ => decoded, rewindSize: 7);

        Assert.Equal([0.0], loader.Read(Stream.Null, 0, 1)!);
        Assert.Equal([10.0, 11.0], loader.Read(Stream.Null, 10, 2)!);
        Assert.Equal([9.0, 10.0], loader.Read(Stream.Null, 9, 2)!);
        Assert.Throws<IOException>(() => loader.Read(Stream.Null, 8, 1));
    }

    [Fact(DisplayName = "FFmpeg stream randomized overlap remains byte exact")]
    public void RandomizedOverlapRemainsByteExact()
    {
        const int rewindSize = 31;
        short[] source = Enumerable.Range(0, 512)
            .Select(index => unchecked((short)((index * 977) - 20_000)))
            .ToArray();
        using var decoded = new MemoryStream(BuildPcm16Bytes(source));
        using var loader = new FfmpegStreamSampleLoader([], [], _ => decoded, rewindSize);
        var random = new Random(0x4D534650);
        long positionBytes = 0;

        for (int iteration = 0; iteration < 300; iteration++)
        {
            long earliestByte = Math.Max(0, positionBytes - rewindSize);
            if (earliestByte > 0 && iteration % 13 == 0)
            {
                long tooOldSample = (earliestByte - 1) / sizeof(short);
                Assert.Throws<IOException>(() => loader.Read(Stream.Null, tooOldSample, 1));
                continue;
            }

            long earliestSample = (earliestByte + sizeof(short) - 1) / sizeof(short);
            long currentSample = positionBytes / sizeof(short);
            long startSample = iteration % 3 == 0
                ? ((positionBytes + sizeof(short) - 1) / sizeof(short)) + random.Next(0, 13)
                : random.NextInt64(earliestSample, currentSample + 1);
            if (startSample >= source.Length)
            {
                break;
            }

            int readLength = Math.Min(random.Next(1, 9), source.Length - checked((int)startSample));
            double[] expected = source
                .AsSpan(checked((int)startSample), readLength)
                .ToArray()
                .Select(value => (double)value)
                .ToArray();

            Assert.Equal(expected, loader.Read(Stream.Null, startSample, readLength)!);
            positionBytes = Math.Max(positionBytes, checked((startSample + readLength) * sizeof(short)));
        }
    }

    [Fact(DisplayName = "FFmpeg stream EOF retains bytes read before a short result")]
    public void EofRetainsBytesReadBeforeAShortResult()
    {
        using var decoded = new ChunkedReadStream(BuildPcm16Bytes([1, 2, 3, 4, 5]), maximumReadSize: 3);
        using var loader = new FfmpegStreamSampleLoader([], [], _ => decoded, rewindSize: 8);

        Assert.Equal([1.0, 2.0, 3.0], loader.Read(Stream.Null, 0, 3)!);
        Assert.Null(loader.Read(Stream.Null, 3, 3));
        Assert.Equal([3.0, 4.0], loader.Read(Stream.Null, 2, 2)!);
        Assert.Null(loader.Read(Stream.Null, 5, 1));
    }

    [Fact(DisplayName = "FFmpeg stream starts once and rewinds seekable input")]
    public void StartsOnceAndRewindsSeekableInput()
    {
        using var input = new MemoryStream(new byte[16]) { Position = 7 };
        using var decoded = new MemoryStream(BuildPcm16Bytes([11, 12, 13]));
        int opens = 0;
        using var loader = new FfmpegStreamSampleLoader([], [], openedInput =>
        {
            opens++;
            Assert.Same(input, openedInput);
            Assert.Equal(0, openedInput.Position);
            return decoded;
        }, rewindSize: 8);

        Assert.Equal([11.0, 12.0], loader.Read(input, 0, 2)!);
        Assert.Equal([11.0], loader.Read(input, 0, 1)!);
        Assert.Equal(1, opens);
    }

    [Fact(DisplayName = "FFmpeg stream overlapping reads keep managed allocation bounded")]
    public void OverlappingReadsKeepManagedAllocationBounded()
    {
        Assert.Equal(16 * 1024 * 1024, FfmpegStreamSampleLoader.DefaultRewindSize);
        ulong expectedHash = RunOverlappingReadWorkload(readCount: 96);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        ulong actualHash = RunOverlappingReadWorkload(readCount: 96);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(expectedHash, actualHash);
        Assert.True(
            allocated < 40_000_000,
            $"Overlapping FFmpeg reads allocated {allocated:N0} bytes.");
    }

    private static ulong RunOverlappingReadWorkload(int readCount)
    {
        const int blockLength = 32 * 1024;
        const int blockStride = 30 * 1024;
        long sampleCount = checked(((long)(readCount - 1) * blockStride) + blockLength);
        using var loader = new FfmpegStreamSampleLoader(
            [],
            [],
            _ => new PatternPcmStream(sampleCount),
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

    private sealed class PatternPcmStream(long sampleCount) : Stream
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
                short value = SampleValue(_position / 2);
                buffer[written++] = unchecked((byte)(value >> 8));
                _position++;
            }

            while (written + 1 < count)
            {
                short value = SampleValue(_position / 2);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(written, sizeof(short)), value);
                written += sizeof(short);
                _position += sizeof(short);
            }

            if (written < count)
            {
                short value = SampleValue(_position / 2);
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
