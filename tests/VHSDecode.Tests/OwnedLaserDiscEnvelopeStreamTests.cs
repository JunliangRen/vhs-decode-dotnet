using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

[Collection(RfBlockCacheConcurrencyCollection.Name)]
public sealed class OwnedLaserDiscEnvelopeStreamTests
{
    private const int BlockLength = 16;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void FreshDomainRejectsEveryOtherReaderBeforeLoaderWork(int workers)
    {
        using var fixture = new Fixture(workers);
        var reader = Activate(fixture);
        Assert.True(reader.IsFor(fixture.Decoder));
        Assert.Null(fixture.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        Assert.Empty(fixture.Loader.Calls);

        Assert.Throws<InvalidOperationException>(() => fixture.Decoder.Read(fixture.Stream, 0, 25));
        Assert.Throws<InvalidOperationException>(() => fixture.Decoder.ReadLeased(fixture.Stream, 0, 25));
        Assert.Throws<InvalidOperationException>(() => fixture.Decoder.ReadLeasedForImmediateLaserDiscField(fixture.Stream, 0, 25));
        Assert.Throws<InvalidOperationException>(() => fixture.Decoder.ReadVhsStagedLeased(fixture.Stream, 0, 25));
        Assert.Throws<InvalidOperationException>(() => fixture.Decoder.Read(fixture.Stream, -1, 0));
        var forged = new RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader(fixture.Decoder, new double[BlockLength]);
        Assert.Throws<InvalidOperationException>(() => forged.Read(fixture.Stream, 0, 25));
        Assert.Empty(fixture.Loader.Calls);

        using var other = new Fixture(workers);
        Assert.False(reader.IsFor(other.Decoder));
        using var span = Required(reader.Read(fixture.Stream, 0, 25));
        Assert.Null(span.Span.Envelope);
        Assert.NotEmpty(fixture.Loader.Calls);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("invalid")]
    [InlineData("eof")]
    [InlineData("success")]
    public void AnEarlierOrdinaryAttemptPermanentlyPreventsActivation(string prior)
    {
        using var fixture = new Fixture(1, eofAt: 40);
        switch (prior)
        {
            case "zero": Assert.NotNull(fixture.Decoder.Read(fixture.Stream, 0, 0)); break;
            case "invalid": Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Decoder.Read(fixture.Stream, -1, 9)); break;
            case "eof": Assert.Null(fixture.Decoder.Read(fixture.Stream, 48, 9)); break;
            default: Assert.NotNull(fixture.Decoder.Read(fixture.Stream, 0, 9)); break;
        }
        ReadCall[] before = fixture.Loader.Calls;
        Assert.Null(fixture.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        fixture.Decoder.InvalidateCachedBlocks();
        Assert.Null(fixture.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        Assert.Equal(before, fixture.Loader.Calls);
    }

    [Fact]
    public void ConfiguredGeneralPrefetchDeclinesWithoutReadingOrStartingWorkers()
    {
        using var fixture = new Fixture(4, prefetch: 2);
        Assert.Equal(2, fixture.Decoder.PrefetchBlocks);
        Assert.Null(fixture.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        Assert.Empty(fixture.Loader.Calls);
    }

    [Fact]
    public async Task ActiveOrdinaryReadCannotBeConvertedToThePrivateDomain()
    {
        using var fixture = new Fixture(4, blockFirstRead: true);
        Task<RfDecodedSpan?> read = Task.Run(() => fixture.Decoder.Read(fixture.Stream, 0, 25), TestContext.Current.CancellationToken);
        RfDecodedSpan? completed = null;
        try
        {
            Assert.True(fixture.Loader.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Null(fixture.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());
            Assert.Single(fixture.Loader.Calls);
        }
        finally
        {
            fixture.Loader.Release.Set();
            completed = await read.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        Assert.NotNull(completed);
        Assert.Null(fixture.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());
    }

    [Fact]
    public void OrdinaryImmediateProjectionStillProducesFullReusableDiagnosticBlocks()
    {
        using var ordinary = new Fixture(4);
        using (var projected = Required(ordinary.Decoder.ReadLeasedForImmediateLaserDiscField(ordinary.Stream, 1, 25)))
            Assert.Null(projected.Span.Envelope);

        RfPipelineBlock[] blocks = CachedBlocks(ordinary.Decoder);
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block =>
        {
            Assert.Equal(BlockLength, block.Demodulated.Envelope.Length);
            Assert.Contains(block.Demodulated.Envelope, value => value > 0.0);
        });
        using var fresh = new Fixture(4);
        RfDecodedSpan expected = Assert.IsType<RfDecodedSpan>(fresh.Decoder.Read(fresh.Stream, 1, 25));
        using var full = Required(ordinary.Decoder.ReadLeased(ordinary.Stream, 1, 25));
        Bits(expected.Envelope!, full.Span.Envelope!);
        Assert.Null(ordinary.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public void PoisonedDomainPreservesFullReadHistoryAcrossWarmupRewindMtfAndStreamChanges(int workers, bool sequential)
    {
        using var full = new Fixture(workers, sequential: sequential, eofAt: 160);
        using var owned = new Fixture(workers, sequential: sequential, eofAt: 160);
        var reader = Activate(owned);
        Poison(reader.EnvelopeShape);
        double[] shapeBefore = reader.EnvelopeShape.ToArray();
        using var fullOther = new MemoryStream();
        using var ownedOther = new MemoryStream();

        Compare(0, 25, full.Stream, owned.Stream);
        Compare(12, 25, full.Stream, owned.Stream);
        // Sequential mode warms the intervening3/4 blocks; parallel mode fills prepared workers.
        Compare(60, 25, full.Stream, owned.Stream);
        Compare(11, 9, full.Stream, owned.Stream);
        Complex[] updated = Enumerable.Repeat(new Complex(1.125, 0.0), BlockLength).ToArray();
        full.Decoder.UpdateLaserDiscCompatibilityMtf(updated);
        owned.Decoder.UpdateLaserDiscCompatibilityMtf(updated);
        full.Decoder.InvalidateCachedBlocks();
        owned.Decoder.InvalidateCachedBlocks();
        Assert.Null(owned.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        Compare(24, 25, full.Stream, owned.Stream);
        Compare(0, 25, fullOther, ownedOther);
        Compare(156, 9, fullOther, ownedOther); // one actual padded block, with a published tail
        Compare(144, 25, fullOther, ownedOther); // requested range reaches a null later block
        Compare(180, 25, fullOther, ownedOther); // EOF, then recovery/rewind on the original stream
        Compare(1, 17, full.Stream, owned.Stream);
        Bits(shapeBefore, reader.EnvelopeShape);

        void Compare(long begin, int length, Stream expectedStream, Stream actualStream)
        {
            using var expected = full.Decoder.ReadLeased(expectedStream, begin, length);
            using var actual = reader.Read(actualStream, begin, length);
            Assert.Equal(expected is null, actual is null);
            if (expected is not null && actual is not null)
            {
                EqualExceptEnvelope(expected.Span, actual.Span);
                Assert.Null(actual.Span.Envelope);
                Assert.NotSame(reader.EnvelopeShape, expected.Span.Envelope);
                Assert.NotEmpty(CachedBlocks(owned.Decoder));
            }
            Assert.Equal(full.Loader.Calls, owned.Loader.Calls);
            Bits(shapeBefore, reader.EnvelopeShape);
            Assert.All(CachedBlocks(owned.Decoder), block => Assert.Same(reader.EnvelopeShape, block.Demodulated.Envelope));
        }
    }

    [Fact]
    public void AStillHeldLeaseKeepsMaterializedChannelsIndependentOfLaterLeases()
    {
        using var fixture = new Fixture(4);
        var reader = Activate(fixture);
        Poison(reader.EnvelopeShape);
        double[] shapeBefore = reader.EnvelopeShape.ToArray();
        using var first = Required(reader.Read(fixture.Stream, 1, 25));
        RfDecodedSpan saved = Snapshot(first.Span);
        using (var second = Required(reader.Read(fixture.Stream, 37, 25)))
        {
            Assert.NotSame(first.Span.Video, second.Span.Video);
            Assert.NotSame(first.Span.DemodRaw, second.Span.DemodRaw);
            EqualExceptEnvelope(saved, first.Span);
        }
        using var third = Required(reader.Read(fixture.Stream, 11, 25));
        EqualExceptEnvelope(saved, first.Span);
        Bits(shapeBefore, reader.EnvelopeShape);
        Assert.Null(first.Span.Envelope);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void InvalidArgumentsAndLoaderFailureKeepOldPrecedenceAndRecoveryHistory(int workers)
    {
        using var full = new Fixture(workers);
        using var owned = new Fixture(workers);
        var reader = Activate(owned);
        Poison(reader.EnvelopeShape);
        double[] before = reader.EnvelopeShape.ToArray();
        foreach ((long begin, int length) in new[] { (-1L, 25), (0L, -1), (long.MaxValue, 1) })
        {
            Exception? expected = Record.Exception(() => full.Decoder.ReadLeased(full.Stream, begin, length)?.Dispose());
            Exception? actual = Record.Exception(() => reader.Read(owned.Stream, begin, length)?.Dispose());
            Assert.NotNull(expected);
            Assert.Equal(Describe(expected), Describe(actual));
        }
        Assert.Empty(full.Loader.Calls);
        Assert.Empty(owned.Loader.Calls);
        full.Loader.ThrowAt = owned.Loader.ThrowAt = 12;
        Exception? expectedFailure = Record.Exception(() => full.Decoder.ReadLeased(full.Stream, 0, 25)?.Dispose());
        Exception? actualFailure = Record.Exception(() => reader.Read(owned.Stream, 0, 25)?.Dispose());
        Assert.IsType<IOException>(expectedFailure);
        Assert.Equal(Describe(expectedFailure), Describe(actualFailure));
        Assert.Equal(full.Loader.Calls, owned.Loader.Calls);
        full.Loader.ThrowAt = owned.Loader.ThrowAt = null;
        using var recoveredFull = Required(full.Decoder.ReadLeased(full.Stream, 0, 25));
        using var recoveredOwned = Required(reader.Read(owned.Stream, 0, 25));
        EqualExceptEnvelope(recoveredFull.Span, recoveredOwned.Span);
        Assert.Equal(full.Loader.Calls, owned.Loader.Calls);
        Bits(before, reader.EnvelopeShape);
        Assert.Throws<InvalidOperationException>(() => owned.Decoder.Read(owned.Stream, 0, 25));
        Assert.Equal(full.Loader.Calls, owned.Loader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelOwned32KReadReloadsBothBlocksAfterLaterEofOrFailure(bool throws)
    {
        const int blockLength = 32_768;
        using var full = new Fixture(4, blockLength: blockLength);
        using var owned = new Fixture(4, blockLength: blockLength);
        var reader = Activate(owned);
        Poison(reader.EnvelopeShape);
        double[] shapeBefore = reader.EnvelopeShape.ToArray();
        int nextSample = owned.Decoder.BlockStride;
        int length = nextSample + 1;

        if (throws)
        {
            full.Loader.ThrowAt = owned.Loader.ThrowAt = nextSample;
            IOException expected = Assert.Throws<IOException>(() => full.Decoder.ReadLeased(full.Stream, 0, length)?.Dispose());
            IOException actual = Assert.Throws<IOException>(() => reader.Read(owned.Stream, 0, length)?.Dispose());
            Assert.Equal(expected.Message, actual.Message);
        }
        else
        {
            full.Loader.EofAt = owned.Loader.EofAt = nextSample;
            Assert.Null(full.Decoder.ReadLeased(full.Stream, 0, length));
            Assert.Null(reader.Read(owned.Stream, 0, length));
        }

        ReadCall[] failedCalls =
        [
            new(0, 0, blockLength, "data"),
            new(0, nextSample, blockLength, throws ? "throw" : "eof")
        ];
        Assert.Equal(failedCalls, full.Loader.Calls);
        Assert.Equal(failedCalls, owned.Loader.Calls);
        Assert.Empty(CachedBlocks(full.Decoder));
        Assert.Empty(CachedBlocks(owned.Decoder));
        Bits(shapeBefore, reader.EnvelopeShape);

        full.Loader.ThrowAt = owned.Loader.ThrowAt = null;
        full.Loader.EofAt = owned.Loader.EofAt = long.MaxValue;
        // A different active MTF must not replace the compatibility response already captured for these blocks.
        Complex[] updatedMtf = new Complex[blockLength];
        full.Decoder.UpdateLaserDiscCompatibilityMtf(updatedMtf);
        owned.Decoder.UpdateLaserDiscCompatibilityMtf(updatedMtf);
        using var expectedRetry = Required(full.Decoder.ReadLeased(full.Stream, 0, length));
        using var actualRetry = Required(reader.Read(owned.Stream, 0, length));
        Assert.IsType<double[]>(expectedRetry.Span.Envelope);
        Assert.Null(actualRetry.Span.Envelope);
        EqualExceptEnvelope(expectedRetry.Span, actualRetry.Span);

        ReadCall[] allCalls = [.. failedCalls, new(0, 0, blockLength, "data"), new(0, nextSample, blockLength, "data")];
        Assert.Equal(allCalls, full.Loader.Calls);
        Assert.Equal(allCalls, owned.Loader.Calls);
        RfPipelineBlock[] blocks = CachedBlocks(owned.Decoder);
        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, block => Assert.Same(reader.EnvelopeShape, block.Demodulated.Envelope));
        Bits(shapeBefore, reader.EnvelopeShape);
    }

    [Theory]
    [InlineData(DspBackend.Exact, 1)]
    [InlineData(DspBackend.IppFast, 0)]
    public void ParallelOwned32KReadOnlySeedsExactBeforeLaterEof(DspBackend backend, int expectedPlanBuilds)
    {
        if (backend == DspBackend.IppFast)
        {
            Assert.SkipUnless(IppTestRuntime.TryProbe(out _), "Intel IPP runtime is unavailable.");
        }

        using var fixture = new Fixture(4, blockLength: 32_768, backend: backend,
            nonlinear: new NonlinearDeemphasisOptions(2_000_000.0, null, 2, -250_000.0, 250_000.0));
        var demodulator = Assert.IsType<RfDemodulator>(typeof(RfBlockDecodePipeline)
            .GetField("_demodulator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Pipeline));
        var reader = Activate(fixture);
        int nextSample = fixture.Decoder.BlockStride;
        int length = nextSample + 1;
        Assert.Equal(0, demodulator.NonlinearDeemphasisFilterPlanBuildCount);

        fixture.Loader.EofAt = nextSample;
        Assert.Null(reader.Read(fixture.Stream, 0, length));
        Assert.Empty(CachedBlocks(fixture.Decoder));
        Assert.Equal(expectedPlanBuilds, demodulator.NonlinearDeemphasisFilterPlanBuildCount);

        fixture.Loader.EofAt = long.MaxValue;
        using var retry = Required(reader.Read(fixture.Stream, 0, length));
        Assert.Equal(1, demodulator.NonlinearDeemphasisFilterPlanBuildCount);
    }

    [Fact]
    public void ParallelOwned32KReadPreservesLaterLoaderExceptionOverRfFilterFailure()
    {
        const int blockLength = 32_768;
        using var full = new Fixture(4, blockLength: blockLength, malformedRfVideo: true);
        using var owned = new Fixture(4, blockLength: blockLength, malformedRfVideo: true);
        var reader = Activate(owned);
        int nextSample = owned.Decoder.BlockStride;
        int length = nextSample + 1;
        full.Loader.ThrowAt = owned.Loader.ThrowAt = nextSample;

        IOException expected = Assert.Throws<IOException>(() => full.Decoder.ReadLeased(full.Stream, 0, length)?.Dispose());
        IOException actual = Assert.Throws<IOException>(() => reader.Read(owned.Stream, 0, length)?.Dispose());
        Assert.Equal(expected.Message, actual.Message);
        ReadCall[] failedCalls = [new(0, 0, blockLength, "data"), new(0, nextSample, blockLength, "throw")];
        Assert.Equal(failedCalls, full.Loader.Calls);
        Assert.Equal(failedCalls, owned.Loader.Calls);
        Assert.Empty(CachedBlocks(full.Decoder));
        Assert.Empty(CachedBlocks(owned.Decoder));

        // Let both loads complete to prove the RF error is real and still reaches the caller.
        full.Loader.ThrowAt = owned.Loader.ThrowAt = null;
        AggregateException expectedRf = Assert.Throws<AggregateException>(() => full.Decoder.ReadLeased(full.Stream, 0, length)?.Dispose());
        AggregateException actualRf = Assert.Throws<AggregateException>(() => reader.Read(owned.Stream, 0, length)?.Dispose());
        var expectedErrors = expectedRf.Flatten().InnerExceptions;
        var actualErrors = actualRf.Flatten().InnerExceptions;
        Assert.NotEmpty(expectedErrors);
        Assert.NotEmpty(actualErrors);
        ArgumentException expectedFilter = Assert.IsType<ArgumentException>(expectedErrors[0]);
        Assert.Equal("filter", expectedFilter.ParamName);
        Assert.All(actualErrors, error => Assert.Equal(Describe(expectedFilter), Describe(error)));

        ReadCall[] allCalls = [.. failedCalls, new(0, 0, blockLength, "data"), new(0, nextSample, blockLength, "data")];
        Assert.Equal(allCalls, full.Loader.Calls);
        Assert.Equal(allCalls, owned.Loader.Calls);
        Assert.Empty(CachedBlocks(full.Decoder));
        Assert.Empty(CachedBlocks(owned.Decoder));
    }

    [Theory]
    [InlineData(DspBackend.Exact, false)]
    [InlineData(DspBackend.IppFast, false)]
    [InlineData(DspBackend.IppFast, true)]
    public void CpuVideoStorageCompactsOnlyIppAndPreservesBits(DspBackend backend, bool noEfm)
    {
        Assert.SkipUnless(Avx2.IsSupported && Fma.IsSupported, "Compact LD video requires AVX2 and FMA.");
        if (backend == DspBackend.IppFast)
            Assert.SkipUnless(IppTestRuntime.TryProbe(out _), "Intel IPP runtime is unavailable.");

        var arguments = new List<string> { "--NTSC", "--threads", "4", "--dsp-backend",
            backend == DspBackend.Exact ? "exact" : "ipp-fast" };
        if (noEfm) arguments.Add("--noEFM");
        arguments.AddRange(["capture.s16", "unused"]);
        var command = new CommandLineParser().Parse(CliSpecs.LaserDisc, arguments.ToArray());
        using DecodeSession wide = DecodeSessionFactory.Create(command);
        using DecodeSession candidate = DecodeSessionFactory.Create(command);
        Assert.Equal(noEfm, candidate.Filters.LdEfm is null);
        var wideReader = Assert.IsType<RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader>(
            wide.StreamDecoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        var compactReader = Assert.IsType<RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader>(
            candidate.StreamDecoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        bool expectedCompact = backend == DspBackend.IppFast;
        Assert.Equal(expectedCompact, candidate.Pipeline.CanCompactOwnedLaserDiscPostFmVideo);
        compactReader.EnableCompactCpuPostFmVideoIfSupported();
        Assert.Equal(expectedCompact, compactReader.CompactCpuPostFmVideo);
        Assert.False(wideReader.CompactCpuPostFmVideo);

        int stride = candidate.StreamDecoder.BlockStride;
        int blockLength = candidate.StreamDecoder.BlockLength;
        Assert.Equal(31_712, stride);
        Assert.Equal(32_768, blockLength);
        var samples = new short[(2 * stride) + blockLength];
        for (int i = 0; i < samples.Length; i++)
        {
            double phase = Math.Tau * 8_300_000.0 * i / candidate.DecodeSampleRateHz + 3 * Math.Sin(i * 0.003);
            samples[i] = (short)Math.Round(19_000 * Math.Cos(phase) + 2_000 * Math.Sin(i * 0.173));
        }
        byte[] bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        using var wideStream = new MemoryStream(bytes, writable: false);
        using var compactStream = new LaterReadFailureStream(bytes);

        if (expectedCompact && noEfm)
        {
            // The first miss can start before the second read fails. Retry on the same reader.
            compactStream.FailureOffset = (long)stride * sizeof(short);
            IOException failure = Assert.Throws<IOException>(() => compactReader.Read(compactStream, 0, stride + 1)?.Dispose());
            Assert.Equal("Synthetic later block read failure.", failure.Message);
            Assert.Empty(CachedBlocks(candidate.StreamDecoder));
            compactStream.FailureOffset = null;
        }

        Compare(11, 17); // Serial miss caches a wide block.
        RfDemodulatedBlock initial = Assert.Single(CachedBlocks(candidate.StreamDecoder)).Demodulated;
        Assert.Equal(blockLength, initial.Video.Length);
        Assert.Null(initial.VideoFloat32);

        // Cached wide block plus two parallel misses. No-EFM IPP can seed the first compact miss.
        Compare(stride - 7, stride + 23);
        RfPipelineBlock[] mixed = CachedBlocks(candidate.StreamDecoder);
        Assert.Equal(3, mixed.Length);
        Assert.Contains(mixed, block => ReferenceEquals(initial, block.Demodulated));
        Assert.Equal(expectedCompact ? 1 : 3, mixed.Count(block => block.Demodulated.VideoFloat32 is null));
        var storage = new HashSet<float[]>(ReferenceEqualityComparer.Instance);
        foreach (RfPipelineBlock block in mixed.Where(block => block.Demodulated.VideoFloat32 is not null))
        {
            RfDemodulatedBlock compact = block.Demodulated;
            Assert.Empty(compact.Video);
            Assert.Empty(compact.VideoLowPass);
            Assert.Null(compact.VideoBurst);
            foreach (float[]? channel in new[] { compact.VideoFloat32, compact.VideoLowPassFloat32, compact.LdVideoBurstFloat32 })
            {
                float[] owned = Assert.IsType<float[]>(channel);
                Assert.Equal(blockLength, owned.Length);
                Assert.True(storage.Add(owned), "A compact channel is shared with another channel or block.");
            }
        }
        Assert.Equal(expectedCompact ? 6 : 0, storage.Count);

        Compare(stride + 11, 17); // The serial append path reads a cached block.
        Assert.Throws<InvalidOperationException>(compactReader.EnableCompactCpuPostFmVideoIfSupported);

        void Compare(long begin, int length)
        {
            using var expected = Required(wideReader.Read(wideStream, begin, length));
            using var actual = Required(compactReader.Read(compactStream, begin, length));
            EqualExceptEnvelope(expected.Span, actual.Span);
            Assert.Equal(length, actual.Span.SampleCount);
            Assert.Equal(noEfm, actual.Span.Efm is null);
            Assert.Equal(length, Assert.IsType<double[]>(actual.Span.VideoLowPass).Length);
            Assert.Equal(length, Assert.IsType<double[]>(actual.Span.VideoBurst).Length);
            LaserDiscAnalogAudioBlock expectedAudio = Assert.IsType<LaserDiscAnalogAudioBlock>(expected.Span.AnalogAudio);
            LaserDiscAnalogAudioBlock actualAudio = Assert.IsType<LaserDiscAnalogAudioBlock>(actual.Span.AnalogAudio);
            Assert.Equal(expectedAudio.DecimationFactor, actualAudio.DecimationFactor);
            Bits(expectedAudio.Left, actualAudio.Left);
            Bits(expectedAudio.Right, actualAudio.Right);
        }
    }

    private static RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader Activate(Fixture fixture)
        => Assert.IsType<RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader>(fixture.Decoder.TryCreateOwnedImmediateLaserDiscFieldReader());

    private static RfBlockStreamDecoder.RfDecodedSpanLease Required(RfBlockStreamDecoder.RfDecodedSpanLease? lease)
        => Assert.IsType<RfBlockStreamDecoder.RfDecodedSpanLease>(lease);

    // Inspect only the two existing block caches to prove actual producer omission, not just field projection.
    private static RfPipelineBlock[] CachedBlocks(RfBlockStreamDecoder decoder)
        => new[] { "_decodedBlockCache", "_sequentialBlockCache" }
            .SelectMany(name => ((Dictionary<long, RfPipelineBlock>)typeof(RfBlockStreamDecoder)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decoder)!).Values)
            .ToArray();

    private static void Poison(double[] values)
    {
        long[] patterns = [0x7FF8_0123_4567_89AB, unchecked((long)0xFFF8_FEDC_BA98_7654UL),
            long.MinValue, 0x7FF0_0000_0000_0000, 0x3FF1_2345_6789_ABCD];
        for (int index = 0; index < values.Length; index++)
            values[index] = BitConverter.Int64BitsToDouble(patterns[index % patterns.Length]);
    }

    private static RfDecodedSpan Snapshot(RfDecodedSpan span)
        => span with { Input = span.Input.ToArray(), Video = span.Video.ToArray(), DemodRaw = span.DemodRaw.ToArray(),
            VideoLowPass = span.VideoLowPass?.ToArray(), RfHighPass = span.RfHighPass?.ToArray(),
            Chroma = span.Chroma?.ToArray(), VideoBurst = span.VideoBurst?.ToArray(), VideoPilot = span.VideoPilot?.ToArray(),
            Efm = span.Efm?.ToArray() };

    private static void EqualExceptEnvelope(RfDecodedSpan expected, RfDecodedSpan actual)
    {
        Assert.Equal(expected.StartSample, actual.StartSample);
        Assert.Equal(expected.SampleCount, actual.SampleCount);
        Bits(expected.Input, actual.Input); Bits(expected.Video, actual.Video); Bits(expected.DemodRaw, actual.DemodRaw);
        NullableBits(expected.VideoLowPass, actual.VideoLowPass); NullableBits(expected.RfHighPass, actual.RfHighPass);
        NullableBits(expected.Chroma, actual.Chroma); NullableBits(expected.VideoBurst, actual.VideoBurst);
        NullableBits(expected.VideoPilot, actual.VideoPilot); Assert.Equal(expected.Efm, actual.Efm);
    }

    private static void NullableBits(double[]? expected, double[]? actual)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is not null && actual is not null) Bits(expected, actual);
    }

    private static void Bits(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual)
        => Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)), "Double bits differ.");

    private static string? Describe(Exception? error)
        => error is null ? null : $"{error.GetType().FullName}|{(error as ArgumentException)?.ParamName}|{error.Message}|"
            + (error is AggregateException aggregate
                ? string.Join(";", aggregate.InnerExceptions.Select(Describe)) : Describe(error.InnerException));

    private sealed class LaterReadFailureStream(byte[] data) : MemoryStream(data, writable: false)
    {
        internal long? FailureOffset { get; set; }

        public override long Seek(long offset, SeekOrigin loc)
        {
            if (loc == SeekOrigin.Begin && offset == FailureOffset)
            {
                throw new IOException("Synthetic later block read failure.");
            }

            return base.Seek(offset, loc);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly RecordingLoader Loader;
        internal readonly MemoryStream Stream = new();
        internal readonly RfBlockDecodePipeline Pipeline;
        internal readonly RfBlockStreamDecoder Decoder;

        internal Fixture(int workers, bool sequential = false, long eofAt = long.MaxValue, int prefetch = 0,
            bool blockFirstRead = false, int blockLength = BlockLength, bool malformedRfVideo = false,
            DspBackend backend = DspBackend.Exact, NonlinearDeemphasisOptions? nonlinear = null)
        {
            Loader = new RecordingLoader(eofAt, blockFirstRead);
            Complex[] identity = RfDemodulator.IdentityFilter(blockLength);
            double[] ones = Enumerable.Repeat(1.0, blockLength).ToArray();
            Complex[] rfVideo = malformedRfVideo ? new Complex[blockLength - 1] : identity;
            var filters = new DecodeFilterSet(rfVideo, identity, identity, identity, identity, identity,
                identity, ones, ones, ones, ones, ones, ones, ones,
                ChromaBurst: identity, ChromaBurstMagnitude: ones, LdVideoBurst: identity,
                LdVideoBurstMagnitude: ones, LdVideoPilot: identity, LdVideoPilotMagnitude: ones);
            var options = new DecodeFilterOptions(LdClipDemodForVideo: true,
                SharpnessEq: sequential ? new SharpnessEqOptions(0.0, 2.0, 1.0, 2) : null,
                NonlinearDeemphasis: nonlinear);
            Pipeline = new RfBlockDecodePipeline(Loader, filters, sampleRateHz: 40_000_000.0,
                filterOptions: options, dspBackend: backend);
            Decoder = new RfBlockStreamDecoder(Pipeline, blockLength, 2, 2,
                workerThreads: workers, prefetchBlocks: prefetch, laserDiscCompatibilityPrefetchBlocks: 2);
            Decoder.UpdateLaserDiscCompatibilityMtf(identity);
        }

        public void Dispose() { Decoder.Dispose(); Pipeline.Dispose(); Stream.Dispose(); Loader.Dispose(); }
    }

    private sealed record ReadCall(int StreamId, long Sample, int Length, string Outcome);

    // Same monotone full-block/padded-EOF pattern as LaserDiscEnvelopeProjectionTests.TestLoader;
    // records actual stream identities and outcomes rather than predicting the cache's read schedule.
    private sealed class RecordingLoader(long eofAt, bool blockFirstRead) : IRfSampleLoader, IDisposable
    {
        private readonly List<ReadCall> _calls = [];
        private readonly Dictionary<Stream, int> _streams = new(ReferenceEqualityComparer.Instance);
        private readonly object _gate = new();
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        internal long? ThrowAt;
        internal long EofAt = eofAt;
        internal ReadCall[] Calls { get { lock (_gate) return _calls.ToArray(); } }

        public double[]? Read(Stream stream, long sample, int readLength)
        {
            int sequence;
            lock (_gate)
            {
                if (!_streams.TryGetValue(stream, out int id)) _streams.Add(stream, id = _streams.Count);
                sequence = _calls.Count;
                _calls.Add(new ReadCall(id, sample, readLength, sample == ThrowAt ? "throw" : sample >= EofAt ? "eof" : "data"));
            }
            if (blockFirstRead && sequence == 0)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test loader was not released.");
            }
            if (sample == ThrowAt) throw new IOException("Synthetic loader failure at sample " + sample + ".");
            if (sample >= EofAt) return null;
            var result = new double[readLength];
            for (int index = 0; index < readLength && sample + index < EofAt; index++) result[index] = sample + index + 1.0;
            return result;
        }

        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
