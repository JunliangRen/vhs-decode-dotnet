using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

[Collection(RfBlockCacheConcurrencyCollection.Name)]
public sealed class LaserDiscEnvelopeProjectionTests
{
    private const int BlockLength = 16;
    private const long CanaryBits = 0x7FF8_0123_4567_89AB;
    private static readonly double Canary = BitConverter.Int64BitsToDouble(CanaryBits);

    [Theory]
    [InlineData("ld", false, true, TbcDropoutDetectionMode.LaserDiscDemod, true)]
    [InlineData("ld", true, true, TbcDropoutDetectionMode.LaserDiscDemod, false)]
    [InlineData("ld", false, false, TbcDropoutDetectionMode.LaserDiscDemod, false)]
    [InlineData("ld", false, true, TbcDropoutDetectionMode.TapeEnvelope, false)]
    [InlineData("ld", false, true, TbcDropoutDetectionMode.Disabled, false)]
    [InlineData("ld", false, true, (TbcDropoutDetectionMode)1234, false)]
    [InlineData("LD", false, true, TbcDropoutDetectionMode.LaserDiscDemod, false)]
    [InlineData("vhs", false, true, TbcDropoutDetectionMode.LaserDiscDemod, false)]
    [InlineData("cvbs", false, true, TbcDropoutDetectionMode.LaserDiscDemod, false)]
    [InlineData(null, false, true, TbcDropoutDetectionMode.LaserDiscDemod, false)]
    public void CapabilityUsesActualImmutableFieldOptions(string? decodeType, bool preserve,
        bool enabled, TbcDropoutDetectionMode mode, bool expected)
    {
        var options = TbcDropoutDetectionOptions.LaserDisc with { Enabled = enabled, Mode = mode };
        TbcFieldDecodePipeline decoder = FieldDecoder(decodeType, preserve, options);
        Assert.Equal(expected, decoder.CanOmitEnvelopeForImmediateLaserDiscField);
        options = options with { Enabled = !enabled, Mode = TbcDropoutDetectionMode.TapeEnvelope };
        Assert.Equal(expected, decoder.CanOmitEnvelopeForImmediateLaserDiscField);
    }

    [Fact]
    public void SessionRecordMetadataCannotAuthorizeADifferentActualDecoder()
    {
        using DecodeSession session = DecodeSessionFactory.Create(new CommandLineParser().Parse(
            CliSpecs.LaserDisc, ["--PAL", "--noAGC", "input.s16", "unused-envelope-projection-test"]));
        DecodeSession metadataSaysTape = session with { DropoutOptions = TbcDropoutDetectionOptions.DefaultDdd };
        Assert.True(metadataSaysTape.TbcFieldDecoder.CanOmitEnvelopeForImmediateLaserDiscField);
        DecodeSession metadataSaysLd = session with
        {
            DropoutOptions = TbcDropoutDetectionOptions.LaserDisc,
            TbcFieldDecoder = FieldDecoder("ld", false, TbcDropoutDetectionOptions.DefaultDdd)
        };
        Assert.False(metadataSaysLd.TbcFieldDecoder.CanOmitEnvelopeForImmediateLaserDiscField);
        Assert.False((session with { TbcFieldDecoder = FieldDecoder("vhs", false,
            TbcDropoutDetectionOptions.LaserDisc) }).TbcFieldDecoder.CanOmitEnvelopeForImmediateLaserDiscField);
        // Do not Dispose record clones: they intentionally share the original session's resources.
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OmittedReadLeavesCanaryAndEveryLaterFullReadRefreshesItsEntireEnvelope(int workers)
    {
        foreach (int length in new[] { 17, 25, 9, 49 })
        {
            // Each length gets an empty pool, so A, omitted B and C must reuse
            // the exact set. This makes B's untouched Envelope canary meaningful.
            using var baseline = new Fixture(workers);
            using var projected = new Fixture(workers);
            Assert.True(CheckOmittedHistory(baseline, projected, workers, length, requireReuse: true));
            Assert.Equal(baseline.Loader.ReadCount, projected.Loader.ReadCount);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void PersistentGrowShrinkAndCapacityOverflowKeepEveryPublishedEnvelopeDefined(int workers)
    {
        using var baseline = new Fixture(workers);
        using var projected = new Fixture(workers);
        int reusedHistories = 0, discardedHistories = 0;
        foreach (int length in new[] { 17, 25, 9, 17, 49, 9 })
        {
            if (CheckOmittedHistory(baseline, projected, workers, length, requireReuse: false)) reusedHistories++;
            else discardedHistories++;
        }
        // The bounded pool retains 17 and 25. Later sizes exercise discard,
        // not an undocumented eviction/reuse promise for every distinct size.
        Assert.True(reusedHistories > 0);
        Assert.True(discardedHistories > 0);
        Assert.Equal(2, projected.Decoder.CachedReusableSpanBufferSetCount);
        Assert.Equal(baseline.Loader.ReadCount, projected.Loader.ReadCount);
    }

    private static bool CheckOmittedHistory(Fixture baseline, Fixture projected, int workers,
        int length, bool requireReuse)
    {
        // Baseline: full(A), full(B), full(C). Candidate: full(A), omit(B), full(C).
        double[] candidatePool, candidateVideo;
        using (var expectedA = baseline.Decoder.ReadLeased(baseline.Stream, 1, length)!)
            Array.Fill(expectedA.Span.Envelope!, Canary);
        using (var actualA = projected.Decoder.ReadLeased(projected.Stream, 1, length)!)
        {
            candidatePool = actualA.Span.Envelope!;
            candidateVideo = actualA.Span.Video;
            Array.Fill(candidatePool, Canary);
        }
        using (var expectedB = baseline.Decoder.ReadLeased(baseline.Stream, 13, length)!)
            Assert.All(expectedB.Span.Envelope!, value => Assert.NotEqual(CanaryBits, BitConverter.DoubleToInt64Bits(value)));
        bool reused;
        using (var omittedB = projected.Decoder.ReadLeasedForImmediateLaserDiscField(projected.Stream, 13, length)!)
        {
            reused = ReferenceEquals(candidateVideo, omittedB.Span.Video);
            if (requireReuse) Assert.Same(candidateVideo, omittedB.Span.Video);
            Assert.Null(omittedB.Span.Envelope);
            Assert.All(candidatePool, value => Assert.Equal(CanaryBits, BitConverter.DoubleToInt64Bits(value)));
        }
        using var expectedC = baseline.Decoder.ReadLeased(baseline.Stream, 11, length)!;
        using var actualC = projected.Decoder.ReadLeased(projected.Stream, 11, length)!;
        using var fresh = new Fixture(workers);
        RfDecodedSpan freshC = fresh.Decoder.Read(fresh.Stream, 11, length)!;
        if (reused) Assert.Same(candidatePool, actualC.Span.Envelope);
        Bits(expectedC.Span.Envelope!, actualC.Span.Envelope!);
        Bits(freshC.Envelope!, actualC.Span.Envelope!);
        Assert.All(actualC.Span.Envelope!, value => Assert.NotEqual(CanaryBits, BitConverter.DoubleToInt64Bits(value)));
        Bits(expectedC.Span.Input, actualC.Span.Input);
        Bits(expectedC.Span.Video, actualC.Span.Video);
        Bits(expectedC.Span.DemodRaw, actualC.Span.DemodRaw);
        Bits(expectedC.Span.VideoLowPass!, actualC.Span.VideoLowPass!);
        Bits(expectedC.Span.RfHighPass!, actualC.Span.RfHighPass!);
        return reused;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void PaddedTailAndFailedEofReadCannotLeaveHistoryInTheNextPublishedEnvelope(int workers)
    {
        using var baseline = new Fixture(workers, eofAt: 40);
        using var projected = new Fixture(workers, eofAt: 40);
        using (var a = baseline.Decoder.ReadLeased(baseline.Stream, 0, 24)!)
            Array.Fill(a.Span.Envelope!, Canary);
        double[] destination;
        using (var a = projected.Decoder.ReadLeased(projected.Stream, 0, 24)!)
        {
            destination = a.Span.Envelope!;
            Array.Fill(destination, Canary);
        }
        using (var b = baseline.Decoder.ReadLeased(baseline.Stream, 24, 24)!)
            Assert.NotNull(b.Span.Envelope);
        using (var b = projected.Decoder.ReadLeasedForImmediateLaserDiscField(projected.Stream, 24, 24)!)
            Assert.Null(b.Span.Envelope);
        Assert.All(destination, value => Assert.Equal(CanaryBits, BitConverter.DoubleToInt64Bits(value)));
        using (var expected = baseline.Decoder.ReadLeased(baseline.Stream, 24, 24)!)
        using (var actual = projected.Decoder.ReadLeased(projected.Stream, 24, 24)!)
        {
            Bits(expected.Span.Envelope!, actual.Span.Envelope!);
            Assert.All(actual.Span.Envelope!, value => Assert.NotEqual(CanaryBits, BitConverter.DoubleToInt64Bits(value)));
        }
        Assert.Null(baseline.Decoder.ReadLeased(baseline.Stream, 48, 24));
        Assert.Null(projected.Decoder.ReadLeasedForImmediateLaserDiscField(projected.Stream, 48, 24));
        using var expectedRecovery = baseline.Decoder.ReadLeased(baseline.Stream, 1, 24)!;
        using var actualRecovery = projected.Decoder.ReadLeased(projected.Stream, 1, 24)!;
        Bits(expectedRecovery.Span.Envelope!, actualRecovery.Span.Envelope!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void PublicOwnedReadAndDefaultLeaseRemainCompleteAndIndependent(int workers)
    {
        using var fixture = new Fixture(workers);
        RfDecodedSpan owned = fixture.Decoder.Read(fixture.Stream, 3, 25)!;
        double[] snapshot = owned.Envelope!.ToArray();
        using (var omitted = fixture.Decoder.ReadLeasedForImmediateLaserDiscField(fixture.Stream, 11, 25)!)
            Assert.Null(omitted.Span.Envelope);
        using var full = fixture.Decoder.ReadLeased(fixture.Stream, 3, 25)!;
        Assert.NotSame(owned.Envelope, full.Span.Envelope);
        Bits(snapshot, full.Span.Envelope!);
        Bits(snapshot, owned.Envelope);
        Array.Fill(full.Span.Envelope!, Canary);
        Bits(snapshot, owned.Envelope);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public void InvalidBlockEnvelopeStillFailsAtAppendWithTheOriginalParallelWrapping(int workers, bool nullEnvelope)
    {
        using var baseline = new Fixture(workers);
        using var projected = new Fixture(workers);
        _ = baseline.Decoder.Read(baseline.Stream, 0, 24);
        _ = projected.Decoder.Read(projected.Stream, 0, 24);
        CorruptEnvelope(baseline.Decoder, nullEnvelope);
        CorruptEnvelope(projected.Decoder, nullEnvelope);
        Exception expected = Capture(() => baseline.Decoder.ReadLeased(baseline.Stream, 0, 24)?.Dispose())!;
        Exception actual = Capture(() => projected.Decoder.ReadLeasedForImmediateLaserDiscField(projected.Stream, 0, 24)?.Dispose())!;
        Assert.NotNull(expected);
        Assert.Equal(Describe(expected), Describe(actual));
        if (workers > 1) Assert.IsType<AggregateException>(actual);
        else if (nullEnvelope) Assert.IsType<NullReferenceException>(actual);
        else Assert.IsType<ArgumentException>(actual);
        // Bad Envelope must not suppress loader/block processing or leave a rented set lost.
        Assert.Equal(baseline.Loader.ReadCount, projected.Loader.ReadCount);
        Assert.Equal(baseline.Decoder.CachedReusableSpanBufferSetCount,
            projected.Decoder.CachedReusableSpanBufferSetCount);
    }

    [Theory]
    [InlineData(16, 16, 0, 0, null)]
    [InlineData(16, 17, 0, 1, null)]
    [InlineData(15, 16, int.MaxValue - 1, int.MaxValue - 1, null)]
    [InlineData(16, 16, 0, 0, -1)]
    [InlineData(16, 16, 0, 0, int.MaxValue)]
    [InlineData(16, 16, int.MaxValue - 5, 0, null)]
    [InlineData(16, 16, 0, int.MaxValue - 7, null)]
    [InlineData(15, 16, 100, 0, null)]
    public void SkipCopyRetainsFrozenValidationArithmeticAndArrayCopyErrors(int sourceLength, int destinationLength,
        int blockOffset, int windowOffset, int? sourceOffset)
    {
        using var fixture = new Fixture(1);
        double[] source = Enumerable.Range(0, sourceLength).Select(index => (double)index).ToArray();
        double[] destination = Enumerable.Repeat(Canary, destinationLength).ToArray();
        Exception? expected = Capture(() => FrozenCopy(source, destination.ToArray(), blockOffset, windowOffset, sourceOffset));
        Exception? full = Capture(() => InvokeCopy(fixture.Decoder, source, destination.ToArray(), blockOffset,
            windowOffset, sourceOffset, copyOutput: true));
        Exception? omitted = Capture(() => InvokeCopy(fixture.Decoder, source, destination, blockOffset,
            windowOffset, sourceOffset, copyOutput: false));
        Assert.Equal(Describe(expected), Describe(full));
        Assert.Equal(Describe(expected), Describe(omitted));
        Assert.All(destination, value => Assert.Equal(CanaryBits, BitConverter.DoubleToInt64Bits(value)));
    }

    private static void InvokeCopy(RfBlockStreamDecoder decoder, double[] source, double[] destination,
        int blockOffset, int windowOffset, int? sourceOffset, bool copyOutput)
    {
        MethodInfo method = typeof(RfBlockStreamDecoder).GetMethod("CopyTrimmedWindow",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(double[]), typeof(double[]), typeof(int), typeof(int), typeof(int?), typeof(bool)], null)!;
        try { method.Invoke(decoder, [source, destination, blockOffset, windowOffset, sourceOffset, copyOutput]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    // Literal pre-candidate double[] overload anchored to 7737897; no new bounds semantics.
    private static void FrozenCopy(double[] source, double[] destination, int blockDestinationOffset,
        int windowOffset, int? sourceOffset = null)
    {
        if (source.Length != BlockLength)
            throw new ArgumentException("Decoded block length did not match the configured block length.", nameof(source));
        int actualSourceOffset = sourceOffset ?? 2;
        if (actualSourceOffset < 0 || actualSourceOffset + 12 > source.Length)
            throw new InvalidOperationException("RF high-pass delay exceeds the overlap-save block cuts.");
        int copyStart = Math.Max(blockDestinationOffset, windowOffset);
        int copyEnd = Math.Min(checked(blockDestinationOffset + 12), checked(windowOffset + destination.Length));
        if (copyStart >= copyEnd) return;
        Array.Copy(source, actualSourceOffset + (copyStart - blockDestinationOffset),
            destination, copyStart - windowOffset, copyEnd - copyStart);
    }

    private static void CorruptEnvelope(RfBlockStreamDecoder decoder, bool nullEnvelope)
    {
        var cache = (Dictionary<long, RfPipelineBlock>)typeof(RfBlockStreamDecoder)
            .GetField("_decodedBlockCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decoder)!;
        RfPipelineBlock block = cache[0];
        cache[0] = block with { Demodulated = block.Demodulated with { Envelope = nullEnvelope ? null! : new double[BlockLength - 1] } };
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }
    private static string? Describe(Exception? error)
        => error is null ? null : $"{error.GetType().FullName}|{(error as ArgumentException)?.ParamName}|{error.Message}|"
            + (error is AggregateException aggregate
                ? string.Join(";", aggregate.InnerExceptions.Select(Describe)) : Describe(error.InnerException));
    private static void Bits(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual)
        => Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)));
    private static TbcFieldDecodePipeline FieldDecoder(string? decodeType, bool preserve, TbcDropoutDetectionOptions options)
    {
        var converter = new VideoOutputConverter(7_100_000.0, 10_000.0, 1024, -40.0, 256.0);
        var renderer = new TbcFieldRenderer(new TbcFrameSpec("PAL", 16, 2, 40_000_000.0, null, null, null, null), converter);
        return new TbcFieldDecodePipeline(new SyncAnalyzer(40_000_000.0, 64.0, 4.7, 2.35, 27.3), renderer,
            converter, "PAL", options, preserveRawMetricSources: preserve, decodeType: decodeType);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly TestLoader Loader;
        internal readonly MemoryStream Stream = new();
        internal readonly RfBlockDecodePipeline Pipeline;
        internal readonly RfBlockStreamDecoder Decoder;
        internal Fixture(int workers, long eofAt = long.MaxValue)
        {
            Loader = new TestLoader(eofAt);
            Complex[] identity = RfDemodulator.IdentityFilter(BlockLength);
            double[] ones = Enumerable.Repeat(1.0, BlockLength).ToArray();
            var filters = new DecodeFilterSet(identity, identity, identity, identity, identity, identity,
                null, ones, ones, ones, ones, ones, ones, null);
            Pipeline = new RfBlockDecodePipeline(Loader, filters, sampleRateHz: 16.0);
            Decoder = new RfBlockStreamDecoder(Pipeline, BlockLength, 2, 2, workerThreads: workers);
        }
        public void Dispose() { Decoder.Dispose(); Pipeline.Dispose(); Stream.Dispose(); }
    }
    private sealed class TestLoader(long eofAt) : IRfSampleLoader
    {
        internal int ReadCount;
        public double[]? Read(Stream stream, long sample, int readLength)
        {
            ReadCount++;
            if (sample >= eofAt) return null;
            var result = new double[readLength];
            for (int index = 0; index < readLength && sample + index < eofAt; index++)
                result[index] = sample + index + 1.0;
            return result;
        }
    }
}