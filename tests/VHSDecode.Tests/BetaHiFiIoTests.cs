using System.Buffers.Binary;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class BetaHiFiIoTests
{
    [Theory(DisplayName = "Betamax zero gain remains digital silence through stereo and dual-mono normalization")]
    [InlineData("s", 2)]
    [InlineData("d", 1)]
    public void ZeroGainAndNormalizePreserveDigitalSilence(string mode, int channels)
    {
        using var directory = new TemporaryDirectory();
        string outputPath = Path.Combine(directory.Path, "normalized.wav");
        HiFiDecodeOptions options = Options(
            "input.s16", outputPath, "--gain", "0", "--normalize", "--audio_mode", mode);
        string[] paths = mode == "d"
            ? [HiFiOutputWriter.GetDualMonoFilename(outputPath, "channel_1"),
               HiFiOutputWriter.GetDualMonoFilename(outputPath, "channel_2")]
            : [outputPath];
        using (var writer = new HiFiOutputWriter(options))
        {
            writer.Write(new HiFiPostProcessedBlock(new float[100], new float[100], new float[200], 0, 0));
            foreach (string path in paths)
                Assert.True(File.Exists(HiFiOutputWriter.GetNormalizeFilename(path, 48_000)));

            var diagnostic = new StringWriter();
            writer.Complete(0, 0, diagnostic);

            Assert.DoesNotContain("Infinity", diagnostic.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", diagnostic.ToString(), StringComparison.Ordinal);
            foreach (string path in paths)
            {
                byte[] bytes = File.ReadAllBytes(path);
                Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
                Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
                Assert.Equal("data"u8.ToArray(), bytes[36..40]);
                Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2)));
                Assert.Equal(channels, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)));
                Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
                Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2)));
                Assert.Equal(100 * channels * sizeof(short), bytes.Length - 44);
                Assert.Equal((uint)(100 * channels * sizeof(short)),
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
                Assert.All(bytes[44..], sampleByte => Assert.Equal(0, sampleByte));
                Assert.False(File.Exists(HiFiOutputWriter.GetNormalizeFilename(path, 48_000)));
            }
        }
        Assert.Empty(Directory.GetFiles(directory.Path, "*tmp_normalize.raw"));
    }

    [Theory(DisplayName = "Native Beta RF FLAC bypasses video LDF tools and FFmpeg nobuffer regardless of suffix")]
    [InlineData("capture.flac", 1_912_602_624L)]
    [InlineData("capture.flac.ldf", 1_912_602_624L)]
    [InlineData("capture.ldf", 0L)]
    public void NativeFlacUsesLosslessFfmpegRouteWithoutDiscardingItsStart(string name, long samples)
    {
        using var directory = new TemporaryDirectory();
        string inputPath = Path.Combine(directory.Path, name);
        byte[] original = WriteFlacHeader(inputPath, channels: 1, samples);
        var host = new RecordingProcessHost();

        using IHiFiSampleReader reader = HiFiInputReader.Open(
            Options(inputPath, Path.Combine(directory.Path, "output.wav")), TextWriter.Null, host);

        Assert.Equal(0, host.ProbeCalls);
        ProcessOpen call = Assert.Single(host.Opens);
        Assert.Equal("ffmpeg", call.FileName);
        Assert.Equal(HiFiRawSampleFormat.S16Le, call.Format);
        Assert.Equal(samples == 0 ? null : (long?)samples, call.TotalSamples);
        Assert.Equal(call.TotalSamples, reader.TotalSamples);
        Assert.Equal(new[]
        {
            "-hide_banner", "-loglevel", "error", "-ignore_unknown", "-i", inputPath,
            "-f", "s16le", "-acodec", "pcm_s16le", "-avoid_negative_ts", "disabled", "-"
        }, call.Arguments);
        Assert.DoesNotContain("nobuffer", call.Arguments);
        Assert.DoesNotContain("-fflags", call.Arguments);
        Assert.Equal(original, File.ReadAllBytes(inputPath));
    }

    [Theory(DisplayName = "Beta RF FLAC rejects multichannel programme audio before launching tools")]
    [InlineData(2)]
    [InlineData(8)]
    public void NativeFlacRejectsMultichannelInput(int channels)
    {
        using var directory = new TemporaryDirectory();
        string inputPath = Path.Combine(directory.Path, "stereo.flac.ldf");
        byte[] original = WriteFlacHeader(inputPath, channels, 1024);
        var host = new RecordingProcessHost();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HiFiInputReader.Open(
            Options(inputPath, Path.Combine(directory.Path, "output.wav")), TextWriter.Null, host));

        Assert.Contains("one RF channel", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.ProbeCalls);
        Assert.Empty(host.Opens);
        Assert.Equal(original, File.ReadAllBytes(inputPath));
    }

    [Theory(DisplayName = "Beta RF FLAC rejects raw overrides incompatible with signed PCM16 storage")]
    [InlineData("s8")]
    [InlineData("u8")]
    [InlineData("u10le")]
    [InlineData("u12le")]
    [InlineData("u16le")]
    [InlineData("f32le")]
    public void NativeFlacRejectsIncompatiblePrecisionOverrides(string format)
    {
        using var directory = new TemporaryDirectory();
        string inputPath = Path.Combine(directory.Path, "capture.flac.ldf");
        WriteFlacHeader(inputPath, 1, 1024);
        var host = new RecordingProcessHost();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => HiFiInputReader.Open(
            Options(inputPath, Path.Combine(directory.Path, "output.wav"), "--raw_format", format),
            TextWriter.Null, host));

        Assert.Contains("signed 16-bit storage", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.ProbeCalls);
        Assert.Empty(host.Opens);
    }

    [Theory(DisplayName = "Beta RF FLAC accepts signed 10 12 and 16 bit precision in PCM16 storage")]
    [InlineData("s10le", HiFiRawSampleFormat.S10Le)]
    [InlineData("s12le", HiFiRawSampleFormat.S12Le)]
    [InlineData("s16le", HiFiRawSampleFormat.S16Le)]
    public void NativeFlacAcceptsSignedPcm16StorageOverrides(string format, HiFiRawSampleFormat expected)
    {
        using var directory = new TemporaryDirectory();
        string inputPath = Path.Combine(directory.Path, "capture.flac.ldf");
        WriteFlacHeader(inputPath, 1, 1024);
        var host = new RecordingProcessHost();

        using IHiFiSampleReader reader = HiFiInputReader.Open(
            Options(inputPath, Path.Combine(directory.Path, "output.wav"), "--raw_format", format),
            TextWriter.Null, host);

        ProcessOpen call = Assert.Single(host.Opens);
        Assert.Equal("ffmpeg", call.FileName);
        Assert.Equal(expected, call.Format);
        Assert.Equal(1024, call.TotalSamples);
        Assert.Equal(0, host.ProbeCalls);
        Assert.Contains("pcm_s16le", call.Arguments);
        Assert.DoesNotContain("nobuffer", call.Arguments);
    }

    private static HiFiDecodeOptions Options(string inputPath, string outputPath, params string[] arguments)
        => HiFiDecodeOptions.FromCommand(new CommandLineParser().Parse(
            CliSpecs.HiFi, ["--betamax", .. arguments, inputPath, outputPath]));

    private static byte[] WriteFlacHeader(string path, int channels, long samples)
    {
        var bytes = new byte[42];
        "fLaC"u8.CopyTo(bytes);
        bytes[4] = 0x80; // Final metadata block, STREAMINFO.
        bytes[7] = 34;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), 2048);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10, 2), 2048);
        ulong packed = (40_000UL << 44) | ((ulong)(channels - 1) << 41)
            | (15UL << 36) | checked((ulong)samples);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), packed);
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    private sealed record ProcessOpen(
        string FileName, string[] Arguments, HiFiRawSampleFormat Format, long? TotalSamples);

    private sealed class RecordingProcessHost : IHiFiInputProcessHost
    {
        public int ProbeCalls { get; private set; }
        public List<ProcessOpen> Opens { get; } = [];

        public HiFiToolProbeResult Probe(string fileName, IReadOnlyList<string> arguments)
        {
            ProbeCalls++;
            return new HiFiToolProbeResult(true, "unexpected tool probe");
        }

        public IHiFiSampleReader Open(
            string fileName, IReadOnlyList<string> arguments, HiFiRawSampleFormat format, long? totalSamples)
        {
            Opens.Add(new ProcessOpen(fileName, arguments.ToArray(), format, totalSamples));
            return new EmptySampleReader(totalSamples);
        }
    }

    private sealed class EmptySampleReader(long? totalSamples) : IHiFiSampleReader
    {
        public long? TotalSamples => totalSamples;
        public int Read(Span<float> destination, CancellationToken cancellationToken = default) => 0;
        public void Dispose() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "vhsdecode-beta-io-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
