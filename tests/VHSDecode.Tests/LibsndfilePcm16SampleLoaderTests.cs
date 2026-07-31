using System.Buffers.Binary;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LibsndfilePcm16SampleLoaderTests
{
    [Fact(DisplayName = "libsndfile RF loader keeps sequential reads seek-free and random reads exact")]
    public void NativeReadsPreserveSequentialAndRandomPositions()
    {
        var source = new RecordingSource([10, 20, 30, 40, 50, 60]);
        var fallback = new RecordingFallback();
        using (var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback))
        {
            using var input = new MemoryStream();
            double[]? firstRead = loader.Read(input, 0, 3);
            double[]? sequentialRead = loader.Read(input, 3, 2);
            Assert.NotNull(firstRead);
            Assert.NotNull(sequentialRead);
            Assert.Equal([10.0, 20.0, 30.0], firstRead);
            Assert.Equal([40.0, 50.0], sequentialRead);
            Assert.Empty(source.SeekSamples);

            double[]? randomRead = loader.Read(input, 1, 3);
            Assert.NotNull(randomRead);
            Assert.Equal([20.0, 30.0, 40.0], randomRead);
            Assert.Equal([1], source.SeekSamples);
            Assert.Null(loader.Read(input, 5, 2));
            Assert.Equal(0, fallback.ReadCount);
        }

        Assert.True(source.Disposed);
        Assert.True(fallback.Disposed);
    }

    [Fact(DisplayName = "libsndfile RF loader switches to FFmpeg only once when native open is unavailable")]
    public void NativeOpenUnavailableUsesPersistentFallback()
    {
        int openCount = 0;
        var fallback = new RecordingFallback([71.0, 72.0]);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ =>
            {
                openCount++;
                throw new LibsndfilePcm16FallbackException("unavailable");
            },
            fallback);
        using var input = new MemoryStream();

        double[]? firstRead = loader.Read(input, 4, 2);
        double[]? secondRead = loader.Read(input, 8, 2);
        Assert.NotNull(firstRead);
        Assert.NotNull(secondRead);
        Assert.Equal([71.0, 72.0], firstRead);
        Assert.Equal([71.0, 72.0], secondRead);
        Assert.Equal(1, openCount);
        Assert.Equal(2, fallback.ReadCount);
    }

    [Fact(DisplayName = "libsndfile RF loader does not hide native seek failures behind FFmpeg")]
    public void NativeSeekFailureDoesNotUseFallback()
    {
        var source = new RecordingSource([1, 2, 3, 4])
        {
            SeekResultOverride = 0
        };
        var fallback = new RecordingFallback();
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback);
        using var input = new MemoryStream();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => loader.Read(input, 2, 1));

        Assert.Contains("instead of 2", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fallback.ReadCount);
    }

    [Fact(DisplayName = "libsndfile RF loader returns null on native short reads without fallback")]
    public void NativeShortReadReturnsNullWithoutFallback()
    {
        var source = new RecordingSource([1, 2, 3, 4])
        {
            MaximumFramesPerRead = 1
        };
        var fallback = new RecordingFallback();
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback);
        using var input = new MemoryStream();

        Assert.Null(loader.Read(input, 0, 2));
        Assert.Equal(0, fallback.ReadCount);
    }

    [Theory(DisplayName = "libsndfile RF loader rejects invalid native read counts")]
    [InlineData(-1)]
    [InlineData(3)]
    public void InvalidNativeReadCountThrows(long framesRead)
    {
        var source = new RecordingSource([1, 2, 3, 4])
        {
            FramesReadOverride = framesRead
        };
        var fallback = new RecordingFallback();
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => loader.Read(Stream.Null, 0, 2));

        Assert.Contains("invalid RF frame count", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fallback.ReadCount);
    }

    [Fact(DisplayName = "libsndfile RF loader zero-length reads do not open either backend")]
    public void ZeroLengthReadDoesNotOpenBackend()
    {
        int openCount = 0;
        var fallback = new RecordingFallback();
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ =>
            {
                openCount++;
                return new RecordingSource([]);
            },
            fallback);

        Assert.Empty(loader.Read(Stream.Null, 123, 0)!);
        Assert.Equal(0, openCount);
        Assert.Equal(0, fallback.ReadCount);
    }

    [Theory(DisplayName = "raw FLAC STREAMINFO gates only 40 kHz mono PCM16")]
    [InlineData(40_000, 1, 16, 12_345, true)]
    [InlineData(48_000, 1, 16, 12_345, false)]
    [InlineData(40_000, 2, 16, 12_345, false)]
    [InlineData(40_000, 1, 24, 12_345, false)]
    [InlineData(40_000, 1, 16, 0, false)]
    public void StreamInfoEligibilityIsNarrow(
        int sampleRate,
        int channels,
        int bitsPerSample,
        long totalSamples,
        bool expectedNative)
    {
        using var input = new MemoryStream(BuildFlacHeader(
            sampleRate,
            channels,
            bitsPerSample,
            totalSamples));

        Assert.True(RawFlacStreamInfo.TryRead(input, out RawFlacStreamInfo info));
        Assert.Equal(sampleRate, info.SampleRateHz);
        Assert.Equal(channels, info.Channels);
        Assert.Equal(bitsPerSample, info.BitsPerSample);
        Assert.Equal(totalSamples == 0 ? null : totalSamples, info.TotalSamples);
        Assert.Equal(expectedNative, info.IsNativeRfPcm16);
    }

    [Theory(DisplayName = "raw FLAC STREAMINFO rejects foreign and truncated headers")]
    [MemberData(nameof(InvalidFlacHeaders))]
    public void StreamInfoRejectsInvalidHeaders(byte[] header)
    {
        using var input = new MemoryStream(header);

        Assert.False(RawFlacStreamInfo.TryRead(input, out _));
    }

    public static TheoryData<byte[]> InvalidFlacHeaders
        => new()
        {
            "OggS"u8.ToArray(),
            "fLaC"u8.ToArray(),
            BuildInvalidFlacHeader(0x81, 0x22, payloadLength: 34),
            BuildInvalidFlacHeader(0x80, 0x22, payloadLength: 33),
            BuildInvalidFlacHeader(0x80, 0x21, payloadLength: 33)
        };

    private static byte[] BuildInvalidFlacHeader(
        byte blockType,
        byte blockLength,
        int payloadLength)
    {
        var bytes = new byte[8 + payloadLength];
        "fLaC"u8.CopyTo(bytes);
        bytes[4] = blockType;
        bytes[7] = blockLength;
        return bytes;
    }

    private static byte[] BuildFlacHeader(
        int sampleRate,
        int channels,
        int bitsPerSample,
        long totalSamples)
    {
        var bytes = new byte[4 + 4 + 34];
        "fLaC"u8.CopyTo(bytes);
        bytes[4] = 0x80;
        bytes[7] = 34;
        ulong packed = ((ulong)sampleRate << 44)
            | ((ulong)(channels - 1) << 41)
            | ((ulong)(bitsPerSample - 1) << 36)
            | (ulong)totalSamples;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), packed);
        return bytes;
    }

    private sealed class RecordingSource(short[] samples) : ILibsndfilePcm16Source
    {
        private long _position;

        public long Frames => samples.Length;

        public List<long> SeekSamples { get; } = [];

        public long? SeekResultOverride { get; init; }

        public int MaximumFramesPerRead { get; init; } = int.MaxValue;

        public long? FramesReadOverride { get; init; }

        public bool Disposed { get; private set; }

        public long Seek(long sample)
        {
            SeekSamples.Add(sample);
            long position = SeekResultOverride ?? sample;
            _position = position;
            return position;
        }

        public long ReadFrames(Span<short> destination)
        {
            if (FramesReadOverride is long framesRead)
            {
                return framesRead;
            }

            int available = checked((int)Math.Max(0, Frames - _position));
            int count = Math.Min(
                destination.Length,
                Math.Min(available, MaximumFramesPerRead));
            samples.AsSpan(checked((int)_position), count).CopyTo(destination);
            _position += count;
            return count;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingFallback(double[]? result = null)
        : IRfSampleLoader, IDisposable
    {
        public int ReadCount { get; private set; }

        public bool Disposed { get; private set; }

        public double[]? Read(Stream stream, long sample, int readLength)
        {
            ReadCount++;
            return result;
        }

        public void Dispose() => Disposed = true;
    }
}
