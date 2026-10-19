using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class BetaHiFiFramingTests
{
    public static TheoryData<int, int, int> FramingCases
    {
        get
        {
            var cases = new TheoryData<int, int, int>();
            foreach (int rate in new[] { 8, 40 })
            {
                int context = rate / 4;
                int emit = rate * 2;
                int[] lengths = [0, 1, context - 1, context, context + 1,
                    emit - 1, emit, emit + 1, emit + context - 1,
                    emit + context, emit + context + 1, emit * 2 - 1,
                    emit * 2, emit * 2 + 1, emit * 3 + context + 1];
                foreach (int length in lengths.Distinct())
                foreach (int fragment in new[] { 1, 3, emit + context })
                    cases.Add(rate, length, fragment);
            }
            return cases;
        }
    }

    [Theory(DisplayName = "Beta RF framing retains every sample and real context through fragmented EOF")]
    [MemberData(nameof(FramingCases))]
    public void FramingRetainsEverySampleAndItsRealContext(int rate, int length, int fragment)
    {
        float[] source = Enumerable.Range(0, length).Select(index => 1000f + index).ToArray();
        using var reader = new FragmentingReader(source, fragment);
        var framer = new BetaHiFiBlockFramer(rate, reader);
        int context = rate / 4;
        int emit = rate * 2;
        var windows = new List<(float[] Samples, int EmitSamples, long StartSample)>();

        while (framer.ReadNext(TestContext.Current.CancellationToken) is { } window)
        {
            // The framer reuses its owned RF buffer on the next call.
            windows.Add((window.Samples.ToArray(), window.EmitSamples, window.StartSample));
            Assert.True(windows.Count <= (length + emit - 1) / emit);
        }

        Assert.Equal((length + emit - 1) / emit, windows.Count);
        Assert.Equal(length, framer.InputSamplesRead);
        Assert.Equal(length, reader.Position);
        Assert.Null(framer.ReadNext(TestContext.Current.CancellationToken));
        Assert.Null(framer.ReadNext(TestContext.Current.CancellationToken));

        var emitted = new List<float>();
        long expectedStart = 0;
        foreach ((float[] samples, int emitSamples, long startSample) in windows)
        {
            Assert.Equal(expectedStart, startSample);
            Assert.Equal(Math.Min(emit, length - expectedStart), emitSamples);
            Assert.Equal(emitSamples + context * 2, samples.Length);
            for (int index = 0; index < samples.Length; index++)
            {
                long sourceIndex = startSample - context + index;
                float expected = sourceIndex >= 0 && sourceIndex < source.Length
                    ? source[checked((int)sourceIndex)] : 0f;
                Assert.Equal(expected, samples[index]);
            }
            emitted.AddRange(samples.AsSpan(context, emitSamples).ToArray());
            expectedStart += emitSamples;
        }

        Assert.Equal(source, emitted.ToArray());
        Assert.Equal(length, expectedStart);
    }

    [Fact(DisplayName = "Beta RF framing follows real EOF rather than an advertised sample total")]
    public void FramingFollowsRealEofRatherThanAdvertisedTotal()
    {
        float[] source = Enumerable.Range(1, 35).Select(index => (float)index).ToArray();
        using var reader = new FragmentingReader(source, 3, advertisedTotal: 1);
        var framer = new BetaHiFiBlockFramer(8, reader);
        var emitted = new List<float>();

        while (framer.ReadNext(TestContext.Current.CancellationToken) is { } window)
            emitted.AddRange(window.Samples.Slice(2, window.EmitSamples).ToArray());

        Assert.Equal(source, emitted.ToArray());
        Assert.Equal(source.Length, framer.InputSamplesRead);
    }

    [Fact(DisplayName = "Canceled Beta RF framing does not read or consume input")]
    public void CanceledFramingDoesNotConsumeInput()
    {
        using var reader = new FragmentingReader([1, 2, 3], 1);
        var framer = new BetaHiFiBlockFramer(8, reader);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => framer.ReadNext(cancellation.Token));
        Assert.Equal(0, reader.ReadCalls);
        Assert.Equal(0, framer.InputSamplesRead);
        BetaHiFiRfWindow window = Assert.IsType<BetaHiFiRfWindow>(
            framer.ReadNext(TestContext.Current.CancellationToken));
        Assert.Equal(3, window.EmitSamples);
        Assert.Equal(new float[] { 0, 0, 1, 2, 3, 0, 0 }, window.Samples.ToArray());
    }

    [Theory(DisplayName = "Beta framing rejects invalid sample counts from the RF reader")]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void FramingRejectsInvalidReaderCounts(int returnedCount)
    {
        using var reader = new InvalidCountReader(returnedCount);
        var framer = new BetaHiFiBlockFramer(8, reader);

        Assert.Throws<InvalidDataException>(() => framer.ReadNext(TestContext.Current.CancellationToken));
        Assert.Equal(0, framer.InputSamplesRead);
    }

    [Theory(DisplayName = "Beta framing requires a positive integral quarter-second geometry")]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(10)]
    public void FramingRejectsInvalidGeometry(int rate)
    {
        using var reader = new FragmentingReader([], 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BetaHiFiBlockFramer(rate, reader));
    }

    public static TheoryData<string, float[], float[]> OutputModes => new()
    {
        { "s", new float[] { .3f, -.2f }, new float[] { .1f, .4f } },
        { "d", new float[] { .3f, -.2f }, new float[] { .1f, .4f } },
        { "ms", new float[] { .2f, .1f }, new float[] { .1f, -.3f } },
        { "dms", new float[] { .2f, .1f }, new float[] { .1f, -.3f } },
        { "l", new float[] { .3f, -.2f }, new float[] { .3f, -.2f } },
        { "r", new float[] { .1f, .4f }, new float[] { .1f, .4f } },
        { "sum", new float[] { .2f, .1f }, new float[] { .2f, .1f } }
    };

    [Theory(DisplayName = "Beta output maps already-compensated stereo before applying final gain")]
    [MemberData(nameof(OutputModes))]
    public void OutputMapsCompensatedStereoAndAppliesFinalGain(
        string mode, float[] expectedLeft, float[] expectedRight)
    {
        float[] left = [.6f, -.4f];
        float[] right = [.2f, .8f];
        float[] originalStereo = [99, 98, 97, 96];
        var input = new HiFiPostProcessedBlock(left, right, originalStereo, 123, 456);

        HiFiPostProcessedBlock output = BetaHiFiStreamingDecoder.PrepareOutput(input, mode, .5);

        Assert.NotSame(left, output.Left);
        Assert.NotSame(right, output.Right);
        Assert.NotSame(originalStereo, output.Stereo);
        Assert.Equal(new float[] { .6f, -.4f }, left);
        Assert.Equal(new float[] { .2f, .8f }, right);
        Assert.Equal(new float[] { 99, 98, 97, 96 }, originalStereo);
        Assert.Equal(123, input.LeftPeak);
        Assert.Equal(456, input.RightPeak);
        Assert.Equal(2, output.Left.Length);
        Assert.Equal(2, output.Right.Length);
        Assert.Equal(4, output.Stereo.Length);
        for (int index = 0; index < 2; index++)
        {
            Assert.InRange(Math.Abs(expectedLeft[index] - output.Left[index]), 0, 1e-7);
            Assert.InRange(Math.Abs(expectedRight[index] - output.Right[index]), 0, 1e-7);
            Assert.Equal(output.Left[index], output.Stereo[index * 2]);
            Assert.Equal(output.Right[index], output.Stereo[index * 2 + 1]);
        }
        Assert.Equal(output.Left.Max(MathF.Abs), output.LeftPeak);
        Assert.Equal(output.Right.Max(MathF.Abs), output.RightPeak);
    }

    [Fact(DisplayName = "Zero final Beta gain silences output without modifying compensated samples")]
    public void ZeroFinalGainDoesNotModifyCompensatedSamples()
    {
        float[] left = [.6f, -.4f];
        float[] right = [.2f, .8f];

        HiFiPostProcessedBlock output = BetaHiFiStreamingDecoder.PrepareOutput(
            new HiFiPostProcessedBlock(left, right, [], .6f, .8f), "s", 0);

        Assert.All(output.Stereo, sample => Assert.Equal(0, sample));
        Assert.Equal(0, output.LeftPeak);
        Assert.Equal(0, output.RightPeak);
        Assert.Equal(new float[] { .6f, -.4f }, left);
        Assert.Equal(new float[] { .2f, .8f }, right);
    }

    [Fact(DisplayName = "Beta final gain follows compensation and does not clip finite headroom")]
    public void FinalGainIsAppliedWithoutPrematureClipping()
    {
        HiFiPostProcessedBlock output = BetaHiFiStreamingDecoder.PrepareOutput(
            new HiFiPostProcessedBlock([.75f], [-.6f], [], .75f, .6f), "s", 2);

        Assert.Equal(1.5f, output.Left[0]);
        Assert.Equal(-1.2f, output.Right[0]);
        Assert.Equal(1.5f, output.LeftPeak);
        Assert.Equal(1.2f, output.RightPeak);
    }

    [Theory(DisplayName = "Beta output rejects invalid gain")]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void OutputRejectsInvalidGain(double gain)
        => Assert.Throws<ArgumentException>(() => BetaHiFiStreamingDecoder.PrepareOutput(
            new HiFiPostProcessedBlock([.1f], [.2f], [], .1f, .2f), "s", gain));

    [Fact(DisplayName = "Beta output rejects inconsistent channel lengths and unknown channel modes")]
    public void OutputRejectsInvalidChannelsOrMode()
    {
        Assert.Throws<ArgumentException>(() => BetaHiFiStreamingDecoder.PrepareOutput(
            new HiFiPostProcessedBlock([.1f], [], [], .1f, 0), "s", 1));
        Assert.Throws<ArgumentException>(() => BetaHiFiStreamingDecoder.PrepareOutput(
            new HiFiPostProcessedBlock([.1f], [.2f], [], .1f, .2f), "invalid", 1));
    }

    private sealed class FragmentingReader(
        float[] samples, int fragment, long? advertisedTotal = null) : IHiFiSampleReader
    {
        public long? TotalSamples => advertisedTotal;
        public int Position { get; private set; }
        public int ReadCalls { get; private set; }

        public int Read(Span<float> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            int count = Math.Min(Math.Min(fragment, destination.Length), samples.Length - Position);
            samples.AsSpan(Position, count).CopyTo(destination);
            Position += count;
            return count;
        }

        public void Dispose() { }
    }

    private sealed class InvalidCountReader(int returnedCount) : IHiFiSampleReader
    {
        public long? TotalSamples => null;
        public int Read(Span<float> destination, CancellationToken cancellationToken = default)
            => returnedCount;
        public void Dispose() { }
    }
}
