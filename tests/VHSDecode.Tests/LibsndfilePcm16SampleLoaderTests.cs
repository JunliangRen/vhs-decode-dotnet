using System.Buffers.Binary;
using System.ComponentModel;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
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
            Assert.Equal(1, fallback.ReadCount);
            Assert.Equal(5, fallback.LastSample);
            Assert.Equal(2, fallback.LastReadLength);
        }

        Assert.True(source.Disposed);
        Assert.True(fallback.Disposed);
    }

    [Fact(DisplayName = "libsndfile reusable reads overwrite every decoded sample")]
    public void NativeReusableReadsOverwriteEveryDecodedSample()
    {
        var source = new RecordingSource([10, 20, 30, 40, 50, 60]);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            new RecordingFallback());

        double[] first = loader.ReadReusable(Stream.Null, 0, 3)!;
        Array.Fill(first, double.NaN);
        loader.ReturnReusable(first);
        double[] second = loader.ReadReusable(Stream.Null, 3, 3)!;

        Assert.Same(first, second);
        Assert.Equal([40.0, 50.0, 60.0], second);
        loader.ReturnReusable(second);
        Assert.Equal(1, loader.CachedReusableDecodedBufferCount);
    }

    [Fact(DisplayName = "libsndfile PCM16 conversion is exact for every value and vector tail")]
    public void Pcm16ConversionMatchesScalarForEveryValueAndTail()
    {
        const int ValueCount = 1 << 16;
        var source = new short[ValueCount + 7];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = unchecked((short)(short.MinValue + i));
        }

        for (int tail = 0; tail < 8; tail++)
        {
            int length = ValueCount + tail;
            var actual = new double[length];
            LibsndfilePcm16SampleLoader.ConvertPcm16ToDouble(
                source.AsSpan(0, length),
                actual);

            for (int i = 0; i < actual.Length; i++)
            {
                Assert.Equal((double)source[i], actual[i]);
            }
        }
    }

    [Fact(DisplayName = "libsndfile PCM16 conversion preserves short-span boundaries")]
    public void Pcm16ConversionPreservesShortSpanBoundaries()
    {
        const double Sentinel = 123456.75;
        for (int length = 0; length <= 15; length++)
        {
            var source = new short[length + 2];
            var destination = new double[length + 2];
            Array.Fill(destination, Sentinel);
            for (int i = 0; i < length; i++)
            {
                source[i + 1] = unchecked((short)(short.MinValue + (i * 4099)));
            }

            LibsndfilePcm16SampleLoader.ConvertPcm16ToDouble(
                source.AsSpan(1, length),
                destination.AsSpan(1, length));

            Assert.Equal(Sentinel, destination[0]);
            Assert.Equal(Sentinel, destination[^1]);
            for (int i = 0; i < length; i++)
            {
                Assert.Equal((double)source[i + 1], destination[i + 1]);
            }
        }
    }

    [Fact(DisplayName = "libsndfile PCM16 conversion rejects a short destination")]
    public void Pcm16ConversionRejectsShortDestination()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            LibsndfilePcm16SampleLoader.ConvertPcm16ToDouble(
                new short[] { short.MinValue, short.MaxValue },
                new double[1]));

        Assert.Equal("destination", exception.ParamName);
    }

    [Fact(DisplayName = "libsndfile reusable reads never alias active leases")]
    public void NativeReusableReadsNeverAliasActiveLeases()
    {
        var source = new RecordingSource([10, 20, 30, 40, 50, 60]);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            new RecordingFallback());

        double[] first = loader.ReadReusable(Stream.Null, 0, 3)!;
        double[] second = loader.ReadReusable(Stream.Null, 3, 3)!;

        Assert.NotSame(first, second);
        Assert.Equal([10.0, 20.0, 30.0], first);
        Assert.Equal([40.0, 50.0, 60.0], second);
        loader.ReturnReusable(first);
        loader.ReturnReusable(second);
    }

    [Fact(DisplayName = "libsndfile reusable fallback copies into loader-owned storage")]
    public void NativeReusableFallbackCopiesIntoLoaderOwnedStorage()
    {
        double[] fallbackSamples = [71.0, 72.0];
        var fallback = new RecordingFallback(fallbackSamples);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => throw new LibsndfilePcm16FallbackException("unavailable"),
            fallback);

        double[] first = loader.ReadReusable(Stream.Null, 0, 2)!;
        Assert.NotSame(fallbackSamples, first);
        Assert.Equal(fallbackSamples, first);
        loader.ReturnReusable(first);

        double[] second = loader.ReadReusable(Stream.Null, 2, 2)!;
        Assert.Same(first, second);
        Assert.Equal(fallbackSamples, second);
        Assert.Equal(2, fallback.ReadCount);
        loader.ReturnReusable(second);
    }

    [Fact(DisplayName = "libsndfile reusable reads allocate no decoded array after warmup")]
    public void NativeReusableReadsAllocateNoDecodedArrayAfterWarmup()
    {
        const int readLength = 32_768;
        short[] samples = CreateNativeSamples(readLength * 2);
        var source = new RecordingSource(samples);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            new RecordingFallback());
        double[] warm = loader.ReadReusable(Stream.Null, 0, readLength)!;
        loader.ReturnReusable(warm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        double[] actual = loader.ReadReusable(Stream.Null, readLength, readLength)!;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(warm, actual);
        Assert.Equal((double)samples[readLength], actual[0]);
        Assert.Equal((double)samples[^1], actual[^1]);
        loader.ReturnReusable(actual);
        Assert.True(
            allocated < 32 * 1_024,
            $"Warm reusable 32K libsndfile read allocated {allocated:N0} bytes.");
    }

    [Fact(DisplayName = "libsndfile reusable decoded buffer retention is concurrency-safe and bounded")]
    public void NativeReusableDecodedBufferRetentionIsConcurrencySafeAndBounded()
    {
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => new RecordingSource([]),
            new RecordingFallback());

        Parallel.For(
            0,
            LibsndfilePcm16SampleLoader.MaximumRetainedDecodedBufferCount * 2,
            i => loader.ReturnReusable(new double[8 + (i & 1)]));

        Assert.Equal(
            LibsndfilePcm16SampleLoader.MaximumRetainedDecodedBufferCount,
            loader.CachedReusableDecodedBufferCount);
        loader.Dispose();
        Assert.Equal(0, loader.CachedReusableDecodedBufferCount);

        using var oversizedLoader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => new RecordingSource([]),
            new RecordingFallback());
        oversizedLoader.ReturnReusable(
            new double[LibsndfilePcm16SampleLoader.MaximumRetainedDecodedBufferLength + 1]);
        Assert.Equal(0, oversizedLoader.CachedReusableDecodedBufferCount);
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

    [Fact(DisplayName = "libsndfile RF loader retries the same read after a native seek failure")]
    public void NativeSeekFailureActivatesFallback()
    {
        var source = new RecordingSource([1, 2, 3, 4])
        {
            SeekResultOverride = 0
        };
        var fallback = new RecordingFallback([91.0]);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback);
        using var input = new MemoryStream();

        double[]? actual = loader.Read(input, 2, 1);

        Assert.NotNull(actual);
        Assert.Equal([91.0], actual);
        Assert.True(source.Disposed);
        Assert.Equal(1, fallback.ReadCount);
        Assert.Equal(2, fallback.LastSample);
        Assert.Equal(1, fallback.LastReadLength);
    }

    [Fact(DisplayName = "libsndfile reusable reads return null before renting output on native short reads")]
    public void NativeReusableShortReadReturnsNullBeforeRentingOutput()
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
        double[] retained = new double[2];
        loader.ReturnReusable(retained);

        Assert.Null(loader.ReadReusable(input, 0, 2));
        Assert.Equal(0, fallback.ReadCount);
        Assert.Equal(1, loader.CachedReusableDecodedBufferCount);

        source.MaximumFramesPerRead = int.MaxValue;
        double[] complete = loader.ReadReusable(input, 0, 2)!;
        Assert.Same(retained, complete);
        Assert.Equal([1.0, 2.0], complete);
        loader.ReturnReusable(complete);
    }

    [Fact(DisplayName = "libsndfile reusable boundary fallback copies into loader-owned storage")]
    public void NativeReusableBoundaryFallbackCopiesIntoLoaderOwnedStorage()
    {
        var source = new RecordingSource([10, 20, 30, 40]);
        double[] fallbackSamples = [91.0, 92.0];
        var fallback = new RecordingFallback(fallbackSamples);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback);

        double[] first = loader.ReadReusable(Stream.Null, 3, 2)!;
        Assert.NotSame(fallbackSamples, first);
        Assert.Equal(fallbackSamples, first);
        Assert.True(source.Disposed);
        loader.ReturnReusable(first);

        double[] second = loader.ReadReusable(Stream.Null, 8, 2)!;
        Assert.Same(first, second);
        Assert.Equal(fallbackSamples, second);
        Assert.Equal(2, fallback.ReadCount);
        loader.ReturnReusable(second);
    }

    [Theory(DisplayName = "libsndfile RF loader retries invalid native read counts through fallback")]
    [InlineData(-1)]
    [InlineData(3)]
    public void InvalidNativeReadCountActivatesFallback(long framesRead)
    {
        var source = new RecordingSource([1, 2, 3, 4])
        {
            FramesReadOverride = framesRead
        };
        var fallback = new RecordingFallback([81.0, 82.0]);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback);

        double[]? actual = loader.Read(Stream.Null, 0, 2);

        Assert.NotNull(actual);
        Assert.Equal([81.0, 82.0], actual);
        Assert.True(source.Disposed);
        Assert.Equal(1, fallback.ReadCount);
    }

    [Fact(DisplayName = "libsndfile RF loader switches once after a native body read failure")]
    public void NativeBodyReadFailureActivatesPersistentFallback()
    {
        var source = new RecordingSource([1, 2, 3, 4])
        {
            ReadException = new LibsndfilePcm16FallbackException("damaged body")
        };
        var fallback = new RecordingFallback([71.0, 72.0]);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback);

        double[]? firstRead = loader.Read(Stream.Null, 0, 2);
        double[]? secondRead = loader.Read(Stream.Null, 2, 2);
        Assert.NotNull(firstRead);
        Assert.NotNull(secondRead);
        Assert.Equal([71.0, 72.0], firstRead);
        Assert.Equal([71.0, 72.0], secondRead);
        Assert.True(source.Disposed);
        Assert.Equal(2, fallback.ReadCount);
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

    [Theory(DisplayName = "raw FLAC STREAMINFO limits libsndfile random access to signed 32-bit totals")]
    [InlineData(2_147_483_647L, true)]
    [InlineData(2_147_483_648L, false)]
    public void StreamInfoGatesExactLibsndfileSeeking(long totalSamples, bool expected)
    {
        using var input = new MemoryStream(BuildFlacHeader(
            FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz,
            channels: 1,
            bitsPerSample: 16,
            totalSamples));

        Assert.True(RawFlacStreamInfo.TryRead(input, out RawFlacStreamInfo info));
        Assert.Equal(expected, info.SupportsExactLibsndfileSeeking);
    }

    [Theory(DisplayName = "raw FLAC mapped seeking requires a complete fixed-block stream without a seektable")]
    [InlineData(2_048, 2_048, false, 0xf8, 2_048, false, true)]
    [InlineData(192, 192, false, 0xf8, 192, false, true)]
    [InlineData(4_608, 4_608, false, 0xf8, 4_608, false, true)]
    [InlineData(2_048, 2_048, true, 0xf8, 2_048, false, false)]
    [InlineData(2_048, 4_096, false, 0xf8, 2_048, false, false)]
    [InlineData(2_048, 2_048, false, 0xf9, 2_048, false, false)]
    [InlineData(1, 1, false, 0xf8, 2_048, false, false)]
    [InlineData(2_048, 2_048, false, 0xf8, 4_096, false, false)]
    [InlineData(2_048, 2_048, false, 0xf8, 2_048, true, false)]
    public void StreamInfoGatesPyAvMappedSeeking(
        int minimumBlockSize,
        int maximumBlockSize,
        bool hasSeekTable,
        byte frameHeader,
        int frameBlockSize,
        bool corruptHeaderCrc,
        bool expected)
    {
        using var input = new MemoryStream(BuildMappedFlacHeader(
            minimumBlockSize,
            maximumBlockSize,
            hasSeekTable,
            frameHeader,
            frameBlockSize,
            corruptHeaderCrc));

        Assert.True(RawFlacStreamInfo.TryRead(input, out RawFlacStreamInfo info));
        Assert.Equal(minimumBlockSize, info.MinimumBlockSize);
        Assert.Equal(maximumBlockSize, info.MaximumBlockSize);
        Assert.Equal(hasSeekTable, info.HasSeekTable);
        Assert.True(info.MetadataChainComplete);
        bool expectedFixedBlockingStrategy = minimumBlockSize is >= 16 and <= 65_535
            && minimumBlockSize == maximumBlockSize
            && frameHeader == 0xf8
            && frameBlockSize == minimumBlockSize
            && !corruptHeaderCrc;
        Assert.Equal(expectedFixedBlockingStrategy, info.UsesFixedBlockingStrategy);
        Assert.Equal(expected, info.SupportsPyAvMappedLibsndfileSeeking);
    }

    [Theory(DisplayName = "raw FLAC mapped seeking rejects forbidden trailing metadata types")]
    [InlineData(0)]
    [InlineData(127)]
    public void StreamInfoRejectsForbiddenMappedMetadata(byte metadataBlockType)
    {
        using var input = new MemoryStream(BuildMappedFlacHeader(
            minimumBlockSize: 2_048,
            maximumBlockSize: 2_048,
            hasSeekTable: false,
            frameHeader: 0xf8,
            trailingMetadataBlockType: metadataBlockType));

        Assert.True(RawFlacStreamInfo.TryRead(input, out RawFlacStreamInfo info));
        Assert.False(info.MetadataChainComplete);
        Assert.False(info.SupportsPyAvMappedLibsndfileSeeking);
    }

    [Theory(DisplayName = "PyAV raw FLAC restart mapping preserves pinned FFmpeg frame starts")]
    [InlineData(0, 0)]
    [InlineData(160_788_480, 156_696_576)]
    [InlineData(500_000_000, 483_632_384)]
    [InlineData(1_500_000_000, 1_442_713_344)]
    [InlineData(2_147_516_415, 2_063_632_383)]
    [InlineData(3_700_000_000, 3_554_737_408)]
    public void PyAvRawFlacMapperMatchesPinnedFrameStarts(
        long logicalSample,
        long expectedPhysicalSample)
    {
        var mapper = new PyAvRawFlacSampleMapper(
            FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz,
            blockSize: 2_048);

        Assert.True(mapper.TryMapRestartSample(logicalSample, out long physicalSample));
        Assert.Equal(expectedPhysicalSample, physicalSample);
        Assert.False(mapper.TryMapRestartSample(-1, out _));
    }

    [Fact(DisplayName = "mapped libsndfile reads retain restart offsets across the rewind window")]
    public void MappedNativeReadsRetainRestartOffsetWithinWindow()
    {
        var source = new VirtualRecordingSource(4_000_000_000);
        var fallback = new RecordingFallback();
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.ldf",
            _ => source,
            fallback,
            new PyAvRawFlacSampleMapper(
                FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz,
                blockSize: 2_048));

        Assert.NotNull(loader.Read(Stream.Null, 500_000_000, 4));
        Assert.NotNull(loader.Read(Stream.Null, 500_000_004, 4));
        Assert.NotNull(loader.Read(Stream.Null, 499_999_000, 4));
        Assert.NotNull(loader.Read(Stream.Null, 1_500_000_000, 4));

        Assert.Equal(
            [483_632_384, 483_631_384, 1_442_713_344],
            source.SeekSamples);
        Assert.Equal(0, fallback.ReadCount);
    }

    [Fact(DisplayName = "mapped libsndfile boundary reads preserve the logical FFmpeg fallback position")]
    public void MappedNativeBoundaryReadUsesLogicalFallbackPosition()
    {
        const long LogicalSample = 500_000_000;
        const long PhysicalSample = 483_632_384;
        var source = new VirtualRecordingSource(PhysicalSample + 1);
        var fallback = new RecordingFallback([71.0, 72.0]);
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.ldf",
            _ => source,
            fallback,
            new PyAvRawFlacSampleMapper(
                FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz,
                blockSize: 2_048));

        double[]? actual = loader.Read(Stream.Null, LogicalSample, 2);

        Assert.NotNull(actual);
        Assert.Equal([71.0, 72.0], actual);
        Assert.Equal(LogicalSample, fallback.LastSample);
        Assert.Equal(2, fallback.LastReadLength);
        Assert.True(source.Disposed);
    }

    [Fact(DisplayName = "parallel raw FLAC routing opts into mapped seeking without changing serial routing")]
    public void RawFlacFactoryKeepsMappedSeekingOptIn()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "large fixed capture.ldf");
            File.WriteAllBytes(path, BuildMappedFlacHeader(
                minimumBlockSize: 2_048,
                maximumBlockSize: 2_048,
                hasSeekTable: false,
                frameHeader: 0xf8));

            using IDisposable serial = (IDisposable)RfLoaderFactory.CreateNative(path);
            using IDisposable parallel = (IDisposable)RfLoaderFactory.CreateNative(
                path,
                preferPyAvMappedRawFlacSeeking: true);

            Assert.IsType<FfmpegPcm16SampleLoader>(serial);
            var mapped = Assert.IsType<LibsndfilePcm16SampleLoader>(parallel);
            Assert.True(mapped.UsesPyAvMappedSeeking);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory(DisplayName = "VHS sessions map oversized raw FLAC only for ordinary parallel decode")]
    [InlineData(null, false, false, null, true)]
    [InlineData("0", false, false, null, false)]
    [InlineData("1", false, false, null, false)]
    [InlineData("2", false, false, null, true)]
    [InlineData("2", true, false, null, false)]
    [InlineData("2", false, true, null, false)]
    [InlineData("2", false, false, "25", false)]
    public void VhsSessionKeepsMappedRawFlacOnParallelDecodeOnly(
        string? threads,
        bool debugPlot,
        bool gnrcAfe,
        string? sharpness,
        bool expectMappedOnMulticore)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "large fixed capture.ldf");
            File.WriteAllBytes(inputPath, BuildMappedFlacHeader(
                minimumBlockSize: 2_048,
                maximumBlockSize: 2_048,
                hasSeekTable: false,
                frameHeader: 0xf8));
            var arguments = new List<string>
            {
                "--system",
                "pal",
                "--frequency",
                "40"
            };
            if (threads is not null)
            {
                arguments.Add("--threads");
                arguments.Add(threads);
            }

            if (debugPlot)
            {
                arguments.Add("--debug_plot");
                arguments.Add(Path.Combine(directory, "plot.json"));
            }

            if (gnrcAfe)
            {
                arguments.Add("--gnuradio_rf_afe");
            }

            if (sharpness is not null)
            {
                arguments.Add("--sharpness");
                arguments.Add(sharpness);
            }

            arguments.Add(inputPath);
            arguments.Add(Path.Combine(directory, "output"));
            ParsedCommand command = new CommandLineParser().Parse(
                CliSpecs.Vhs,
                arguments);
            using DecodeSession session = DecodeSessionFactory.Create(command);

            bool expectedMapped = expectMappedOnMulticore
                && Environment.ProcessorCount > 1;
            if (expectedMapped)
            {
                var mapped = Assert.IsType<LibsndfilePcm16SampleLoader>(session.Loader);
                Assert.True(mapped.UsesPyAvMappedSeeking);
            }
            else
            {
                Assert.IsType<FfmpegPcm16SampleLoader>(session.Loader);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory(DisplayName = "raw FLAC STREAMINFO rejects foreign and truncated headers")]
    [MemberData(nameof(InvalidFlacHeaders))]
    public void StreamInfoRejectsInvalidHeaders(byte[] header)
    {
        using var input = new MemoryStream(header);

        Assert.False(RawFlacStreamInfo.TryRead(input, out _));
    }

    [Fact(DisplayName = "bundled libsndfile reads direct raw FLAC without invoking FFmpeg fallback")]
    public void BundledLibsndfileReadsDirectRawFlacWithoutFallback()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The bundled sndfile.dll is a Windows runtime asset.");
        string directory = CreateTemporaryDirectory();
        try
        {
            short[] expected = CreateNativeSamples(32_768);
            string path = Path.Combine(directory, "native round trip.ldf");
            WriteDirectRawFlac(path, expected);
            var fallback = new RecordingFallback();
            using var loader = new LibsndfilePcm16SampleLoader(
                path,
                LibsndfilePcm16Source.Open,
                fallback);

            double[]? actual = loader.Read(Stream.Null, 0, expected.Length);

            Assert.NotNull(actual);
            Assert.Equal(expected.Select(static sample => (double)sample), actual);
            Assert.Equal(0, fallback.ReadCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "corrupted direct raw FLAC retries through the established fallback")]
    public void CorruptedDirectRawFlacActivatesFallback()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The bundled sndfile.dll is a Windows runtime asset.");
        string directory = CreateTemporaryDirectory();
        try
        {
            short[] source = CreateNativeSamples(65_536);
            string path = Path.Combine(directory, "damaged body.ldf");
            WriteDirectRawFlac(path, source);
            CorruptFlacAudioFrame(path);
            using (ILibsndfilePcm16Source probe = LibsndfilePcm16Source.Open(path))
            {
                Assert.Equal(source.Length, probe.Frames);
            }

            double[] fallbackSamples = Enumerable.Repeat(1234.0, source.Length).ToArray();
            var fallback = new RecordingFallback(fallbackSamples);
            using var loader = new LibsndfilePcm16SampleLoader(
                path,
                LibsndfilePcm16Source.Open,
                fallback);

            double[]? actual = loader.Read(Stream.Null, 0, source.Length);

            Assert.Same(fallbackSamples, actual);
            Assert.Equal(1, fallback.ReadCount);
            Assert.Equal(0, fallback.LastSample);
            Assert.Equal(source.Length, fallback.LastReadLength);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "underreported FLAC totals retry the boundary read through fallback")]
    public void UnderreportedFlacTotalActivatesFallback()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The bundled sndfile.dll is a Windows runtime asset.");
        string directory = CreateTemporaryDirectory();
        try
        {
            const int reportedSamples = 16_384;
            short[] source = CreateNativeSamples(65_536);
            string path = Path.Combine(directory, "underreported total.ldf");
            WriteDirectRawFlac(path, source);
            SetFlacTotalSamples(path, reportedSamples);
            Assert.True(RawFlacStreamInfo.TryRead(path, out RawFlacStreamInfo info));
            Assert.Equal(reportedSamples, info.TotalSamples);

            double[] fallbackSamples = Enumerable.Repeat(4321.0, 64).ToArray();
            var fallback = new RecordingFallback(fallbackSamples);
            using var loader = new LibsndfilePcm16SampleLoader(
                path,
                candidatePath =>
                {
                    ILibsndfilePcm16Source native = LibsndfilePcm16Source.Open(candidatePath);
                    Assert.Equal(reportedSamples, native.Frames);
                    return native;
                },
                fallback);

            double[]? actual = loader.Read(Stream.Null, reportedSamples, fallbackSamples.Length);

            Assert.Same(fallbackSamples, actual);
            Assert.Equal(1, fallback.ReadCount);
            Assert.Equal(reportedSamples, fallback.LastSample);
            Assert.Equal(fallbackSamples.Length, fallback.LastReadLength);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "clean native EOF remains EOF when FFmpeg is unavailable")]
    public void MissingFfmpegAtNativeLengthBoundaryRemainsEof()
    {
        var source = new RecordingSource([10, 20, 30, 40]);
        var fallback = new RecordingFallback
        {
            ReadException = new NotSupportedException(
                "FFmpeg is unavailable.",
                new Win32Exception(2))
        };
        using (var loader = new LibsndfilePcm16SampleLoader(
            "capture.flac",
            _ => source,
            fallback))
        {
            Assert.Null(loader.Read(Stream.Null, source.Frames, 1));
            Assert.False(source.Disposed);

            double[]? earlierRead = loader.Read(Stream.Null, 0, 2);
            Assert.NotNull(earlierRead);
            Assert.Equal([10.0, 20.0], earlierRead);
            Assert.Equal(1, fallback.ReadCount);
        }

        Assert.True(source.Disposed);
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

    private static byte[] BuildMappedFlacHeader(
        int minimumBlockSize,
        int maximumBlockSize,
        bool hasSeekTable,
        byte frameHeader,
        int frameBlockSize = 2_048,
        bool corruptHeaderCrc = false,
        byte? trailingMetadataBlockType = null)
    {
        const long TotalSamples = (long)int.MaxValue + 1;
        byte? metadataBlockType = trailingMetadataBlockType
            ?? (hasSeekTable ? (byte)3 : null);
        int metadataBytes = metadataBlockType.HasValue ? 4 : 0;
        const int FrameHeaderBytes = 7;
        var bytes = new byte[4 + 4 + 34 + metadataBytes + FrameHeaderBytes];
        "fLaC"u8.CopyTo(bytes);
        bytes[4] = metadataBlockType.HasValue ? (byte)0x00 : (byte)0x80;
        bytes[7] = 34;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), checked((ushort)minimumBlockSize));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10, 2), checked((ushort)maximumBlockSize));
        ulong packed = ((ulong)FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz << 44)
            | ((ulong)(1 - 1) << 41)
            | ((ulong)(16 - 1) << 36)
            | (ulong)TotalSamples;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), packed);
        int frameOffset = 42;
        if (metadataBlockType.HasValue)
        {
            bytes[frameOffset] = (byte)(0x80 | metadataBlockType.Value);
            frameOffset += 4;
        }

        bytes[frameOffset] = 0xff;
        bytes[frameOffset + 1] = frameHeader;
        bytes[frameOffset + 2] = (byte)((FixedBlockSizeCode(frameBlockSize) << 4) | 12);
        bytes[frameOffset + 3] = 0x08;
        bytes[frameOffset + 4] = 0;
        bytes[frameOffset + 5] = 40;
        byte crc = CalculateFlacHeaderCrc8(bytes.AsSpan(frameOffset, FrameHeaderBytes - 1));
        bytes[frameOffset + 6] = corruptHeaderCrc ? (byte)(crc ^ 0xff) : crc;
        return bytes;
    }

    private static int FixedBlockSizeCode(int blockSize)
        => blockSize switch
        {
            192 => 1,
            576 => 2,
            1_152 => 3,
            2_304 => 4,
            4_608 => 5,
            256 => 8,
            512 => 9,
            1_024 => 10,
            2_048 => 11,
            4_096 => 12,
            8_192 => 13,
            16_384 => 14,
            32_768 => 15,
            _ => throw new ArgumentOutOfRangeException(nameof(blockSize))
        };

    private static byte CalculateFlacHeaderCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0
                    ? (byte)((crc << 1) ^ 0x07)
                    : (byte)(crc << 1);
            }
        }

        return crc;
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-libsndfile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static short[] CreateNativeSamples(int count)
    {
        var samples = new short[count];
        uint state = 0x9e3779b9;
        for (int i = 0; i < samples.Length; i++)
        {
            state = (state * 1_664_525) + 1_013_904_223;
            samples[i] = unchecked((short)(state >> 16));
        }

        return samples;
    }

    private static void WriteDirectRawFlac(string path, ReadOnlySpan<short> samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(short))];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), samples[i]);
        }

        using var output = new LibsndfilePcm16FlacStream(
            path,
            LibsndfileLdTestLdfWriter.SampleRate,
            LibsndfileLdTestLdfWriter.CompressionLevel);
        output.Write(bytes);
    }

    private static void CorruptFlacAudioFrame(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("fLaC"u8));
        int offset = 4;
        bool lastBlock;
        do
        {
            Assert.True(offset <= bytes.Length - 4);
            lastBlock = (bytes[offset] & 0x80) != 0;
            int blockLength = (bytes[offset + 1] << 16)
                | (bytes[offset + 2] << 8)
                | bytes[offset + 3];
            offset = checked(offset + 4 + blockLength);
            Assert.InRange(offset, 0, bytes.Length);
        }
        while (!lastBlock);

        int encodedLength = bytes.Length - offset;
        Assert.True(encodedLength >= 32);
        bytes[offset + (encodedLength / 2)] ^= 0x5a;
        File.WriteAllBytes(path, bytes);
    }

    private static void SetFlacTotalSamples(string path, long totalSamples)
    {
        const ulong totalSamplesMask = 0x0000000FFFFFFFFFUL;
        Assert.InRange(totalSamples, 1, (long)totalSamplesMask);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("fLaC"u8));
        Assert.True(bytes.Length >= 26);
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18, sizeof(ulong)));
        packed = (packed & ~totalSamplesMask) | (ulong)totalSamples;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, sizeof(ulong)), packed);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class RecordingSource(short[] samples) : ILibsndfilePcm16Source
    {
        private long _position;

        public long Frames => samples.Length;

        public List<long> SeekSamples { get; } = [];

        public long? SeekResultOverride { get; init; }

        public int MaximumFramesPerRead { get; set; } = int.MaxValue;

        public long? FramesReadOverride { get; init; }

        public Exception? ReadException { get; init; }

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
            if (ReadException is not null)
            {
                throw ReadException;
            }

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

    private sealed class VirtualRecordingSource(long frames) : ILibsndfilePcm16Source
    {
        private long _position;

        public long Frames => frames;

        public List<long> SeekSamples { get; } = [];

        public bool Disposed { get; private set; }

        public long Seek(long sample)
        {
            SeekSamples.Add(sample);
            _position = sample;
            return sample;
        }

        public long ReadFrames(Span<short> destination)
        {
            int count = checked((int)Math.Min(destination.Length, Frames - _position));
            destination[..count].Clear();
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

        public long? LastSample { get; private set; }

        public int? LastReadLength { get; private set; }

        public Exception? ReadException { get; init; }

        public double[]? Read(Stream stream, long sample, int readLength)
        {
            ReadCount++;
            LastSample = sample;
            LastReadLength = readLength;
            if (ReadException is not null)
            {
                throw ReadException;
            }

            return result;
        }

        public void Dispose() => Disposed = true;
    }
}
