using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class BetaHiFiAudioProcessorTests
{
    private static readonly GoldenFixture Golden = ReadGolden();

    [Theory(DisplayName = "Beta empirical backend matches the frozen Python/SciPy oracle")]
    [InlineData("default")]
    [InlineData("reference-plus-six")]
    [InlineData("deemphasis-only")]
    [InlineData("dynamics-only")]
    [InlineData("bypass-quantized")]
    [InlineData("silence")]
    [InlineData("left-only")]
    [InlineData("short-1")]
    [InlineData("short-2")]
    [InlineData("short-9")]
    [InlineData("short-10")]
    [InlineData("short-47")]
    [InlineData("short-48")]
    [InlineData("short-49")]
    public void MatchesIndependentOracle(string name)
    {
        GoldenCase test = Golden.Cases.Single(x => x.Name == name);
        float[] actual = Run(test, blockFrames: 137, fragmented: true);
        Assert.Equal(test.Expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            // Double-precision filtering is exposed through the existing float32
            // block contract. Allow its final rounding, not a relaxed DSP error.
            double tolerance = Math.Max(2e-10, Math.Abs(test.Expected[i]) * 8e-8);
            double error = Math.Abs(actual[i] - test.Expected[i]);
            Assert.True(error <= tolerance,
                $"{name} sample {i}: expected {test.Expected[i]:R}, actual {actual[i]:R}, error {error:R}, tolerance {tolerance:R}.");
        }
    }

    [Fact(DisplayName = "Beta oracle identifies frozen source provenance")]
    public void OracleHasFrozenSourceProvenance()
    {
        using JsonDocument meta = JsonDocument.Parse(Golden.Metadata);
        Assert.Equal("bf003a507b96b5fadebd35d980f0352fe949aab72d94add7a7d9ed0ad266e633", meta.RootElement.GetProperty("model_sha256").GetString());
        Assert.Equal("06c9fc5a90f02b0be408682cf535e25a13c1a47c0776d93d6016bb6c538b21db", meta.RootElement.GetProperty("profile_sha256").GetString());
        Assert.Equal(48_000, meta.RootElement.GetProperty("rate").GetInt32());
        Assert.Equal(14, Golden.Cases.Count);
    }

    [Fact(DisplayName = "Beta filtering and linked controls survive arbitrary chunk boundaries")]
    public void ChunkingIsBitExact()
    {
        GoldenCase test = Golden.Cases.Single(x => x.Name == "default");
        float[] full = Run(test, blockFrames: 32_779);
        Assert.Equal(full, Run(test, blockFrames: 4800));
        Assert.Equal(full, Run(test, blockFrames: 17, fragmented: true));
    }

    [Fact(DisplayName = "Beta silence stays silent and one input channel cannot leak into the other")]
    public void HasNoAdditiveOrCrossChannelAudio()
    {
        GoldenCase silence = Golden.Cases.Single(x => x.Name == "silence");
        Assert.All(Run(silence, 17), sample => Assert.Equal(0, sample));
        GoldenCase mono = Golden.Cases.Single(x => x.Name == "left-only");
        float[] output = Run(mono, 17);
        for (int i = 1; i < output.Length; i += 2)
        {
            Assert.Equal(0, output[i]);
        }
    }

    [Fact(DisplayName = "Beta reads only the declared frames at the current input position")]
    public void UsesCurrentPositionAndLeavesInputOpen()
    {
        GoldenCase test = Golden.Cases.Single(x => x.Name == "short-49");
        byte[] bytes = new byte[test.Input.Length + 18];
        test.Input.CopyTo(bytes, 7);
        using var stream = new MemoryStream(bytes);
        stream.Position = 7;
        long outputFrames = 0;
        BetaHiFiAudioProcessor.ProcessFile(stream, test.Frames, block => outputFrames += block.Left.Length,
            token: TestContext.Current.CancellationToken);
        Assert.Equal(test.Frames, outputFrames);
        Assert.Equal(7 + test.Input.Length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact(DisplayName = "Beta accepts an empty file without emitting synthetic frames")]
    public void EmptyIsEmpty()
    {
        using var stream = new MemoryStream();
        BetaHiFiAudioProcessor.ProcessFile(stream, 0, _ => Assert.Fail("Empty input emitted audio."),
            token: TestContext.Current.CancellationToken);
        Assert.Equal(0, stream.Position);
    }

    [Fact(DisplayName = "Beta fails on truncated input and nonfinite or clipping interstages")]
    public void RejectsInvalidInput()
    {
        using var shortInput = new MemoryStream(new byte[7]);
        Assert.Throws<EndOfStreamException>(() => BetaHiFiAudioProcessor.ProcessFile(shortInput, 1, _ => { },
            token: TestContext.Current.CancellationToken));
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, -1f, 1f, 1.1f })
        {
            using var stream = new MemoryStream(StereoBytes(value, 0));
            Assert.Throws<InvalidDataException>(() => BetaHiFiAudioProcessor.ProcessFile(stream, 1, _ => { },
                token: TestContext.Current.CancellationToken));
        }

        // Raw values are valid but the fixed low-frequency EQ would exceed the
        // PCM24 interstage. Later output normalization must not conceal clipping.
        using var eqClip = new MemoryStream(StereoBytes(.8f, .8f));
        Assert.Throws<InvalidDataException>(() => BetaHiFiAudioProcessor.ProcessFile(eqClip, 1, _ => { },
            token: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "Beta validates bounded buffer and calibration options")]
    public void RejectsInvalidOptions()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaHiFiAudioProcessor.ProcessFile(stream, -1, _ => { },
            token: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaHiFiAudioProcessor.ProcessFile(stream, 0, _ => { },
            token: TestContext.Current.CancellationToken, outputBlockFrames: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaHiFiAudioProcessor.ProcessFile(stream, 0, _ => { },
            token: TestContext.Current.CancellationToken, outputBlockFrames: 65_537));
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaHiFiAudioProcessor.ProcessFile(stream, 0, _ => { },
            referenceOffsetDb: double.NaN, token: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => BetaHiFiAudioProcessor.ProcessFile(stream, long.MaxValue, _ => { },
            token: TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "Beta cancellation and callback failure leave the input owned by the caller")]
    public void CancellationAndCallbackFailure()
    {
        GoldenCase test = Golden.Cases.Single(x => x.Name == "default");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var input = new MemoryStream(test.Input);
        Assert.Throws<OperationCanceledException>(() => BetaHiFiAudioProcessor.ProcessFile(input, test.Frames, _ => { }, token: cancelled.Token));
        Assert.Equal(0, input.Position);
        Assert.True(input.CanRead);

        using var midway = new CancellationTokenSource();
        int blocks = 0;
        Assert.Throws<OperationCanceledException>(() => BetaHiFiAudioProcessor.ProcessFile(input, test.Frames,
            _ => { blocks++; midway.Cancel(); }, token: midway.Token, outputBlockFrames: 137));
        Assert.Equal(1, blocks);
        Assert.True(input.CanRead);

        input.Position = 0;
        Assert.Throws<IOException>(() => BetaHiFiAudioProcessor.ProcessFile(input, test.Frames,
            _ => throw new IOException("Consumer failed."), token: TestContext.Current.CancellationToken));
        Assert.True(input.CanRead);
    }

    private static byte[] StereoBytes(float left, float right)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, left);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), right);
        return bytes;
    }

    private static float[] Run(GoldenCase test, int blockFrames, bool fragmented = false)
    {
        using Stream input = fragmented
            ? new FragmentedStream(test.Input)
            : new MemoryStream(test.Input, writable: false);
        var output = new List<float>(test.Frames * 2);
        BetaHiFiAudioProcessor.ProcessFile(input, test.Frames, block =>
        {
            Assert.Equal(block.Left.Length, block.Right.Length);
            Assert.Equal(block.Left.Length * 2, block.Stereo.Length);
            Assert.InRange(block.Left.Length, 1, blockFrames);
            float lp = 0, rp = 0;
            for (int i = 0; i < block.Left.Length; i++)
            {
                Assert.Equal(block.Left[i], block.Stereo[i * 2]);
                Assert.Equal(block.Right[i], block.Stereo[(i * 2) + 1]);
                lp = MathF.Max(lp, MathF.Abs(block.Left[i]));
                rp = MathF.Max(rp, MathF.Abs(block.Right[i]));
            }

            Assert.Equal(lp, block.LeftPeak);
            Assert.Equal(rp, block.RightPeak);
            output.AddRange(block.Stereo);
        }, (test.Flags & 1) != 0, (test.Flags & 2) != 0, test.ReferenceOffset,
            token: TestContext.Current.CancellationToken,
            outputBlockFrames: blockFrames);
        Assert.True(input.CanRead);
        return output.ToArray();
    }

    private static GoldenFixture ReadGolden()
    {
        Stream resource = typeof(BetaHiFiAudioProcessorTests).Assembly.GetManifestResourceStream(
            "VHSDecode.Tests.Fixtures.beta-hifi-backend-v1.bin.gz")
            ?? throw new InvalidOperationException("Missing embedded Beta golden fixture.");
        using (resource)
        using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
        using (var reader = new BinaryReader(gzip, Encoding.UTF8))
        {
            Assert.Equal("BETA16G1", Encoding.ASCII.GetString(reader.ReadBytes(8)));
            string metadata = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            int count = reader.ReadInt32();
            var cases = new List<GoldenCase>(count);
            for (int i = 0; i < count; i++)
            {
                string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                int flags = reader.ReadInt32();
                double offset = reader.ReadDouble();
                int frames = reader.ReadInt32();
                byte[] input = reader.ReadBytes(frames * 8);
                var expected = new double[frames * 2];
                for (int j = 0; j < expected.Length; j++)
                {
                    expected[j] = reader.ReadDouble();
                }

                cases.Add(new GoldenCase(name, flags, offset, frames, input, expected));
            }

            Assert.Equal(-1, reader.BaseStream.ReadByte());
            return new GoldenFixture(metadata, cases);
        }
    }

    private sealed record GoldenFixture(string Metadata, List<GoldenCase> Cases);
    private sealed record GoldenCase(string Name, int Flags, double ReferenceOffset, int Frames, byte[] Input, double[] Expected);

    private sealed class FragmentedStream(byte[] input) : Stream
    {
        private readonly MemoryStream _inner = new(input, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(13, count));
        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(13, buffer.Length)]);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
