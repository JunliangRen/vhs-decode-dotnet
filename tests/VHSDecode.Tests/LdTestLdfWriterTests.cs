using System.Buffers.Binary;
using System.Text;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LdTestLdfWriterTests
{
    [Fact(DisplayName = "LD test LDF uses bounded libsndfile PCM16 FLAC with exact samples")]
    public void NativeWriterPreservesPcm16Samples()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            short[] source =
            [
                short.MinValue,
                -12_345,
                -1,
                0,
                1,
                12_345,
                short.MaxValue
            ];
            string inputPath = Path.Combine(tempDirectory, "input.s16");
            WritePcm16(inputPath, source);
            string ldfPath = Path.Combine(tempDirectory, "sample.ldf");
            using DecodeSession session = CreateSession(inputPath, ldfPath);
            using FileStream input = File.OpenRead(inputPath);
            var writer = new LdTestLdfWriter(chunkSamples: 2);

            LdTestLdfWriteResult result = writer.Write(
                session,
                startSample: 1,
                endSample: 6,
                input);

            Assert.True(result.Success);
            Assert.Equal(5, result.SamplesWritten);
            Assert.Equal("fLaC", Encoding.ASCII.GetString(File.ReadAllBytes(ldfPath), 0, 4));
            LibsndfileReadResult decoded = LibsndfileTestReader.ReadPcm32(ldfPath);
            Assert.Equal(5, decoded.Frames);
            Assert.Equal(LibsndfileLdTestLdfWriter.SampleRate, decoded.SampleRate);
            Assert.Equal(1, decoded.Channels);
            Assert.Equal(LibsndfilePcm16FlacStream.FlacPcm16Format, decoded.Format);
            Assert.Equal(
                source[1..6].Select(sample => (int)sample << 16),
                decoded.Samples);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD test LDF falls back only when libsndfile is unavailable")]
    public void BackendUnavailableUsesFallback()
    {
        using DecodeSession session = CreateSession("input.s16", "sample.ldf");
        using var input = new MemoryStream();
        var unavailable = new RecordingWriter(
            _ => throw new LdTestLdfBackendUnavailableException(
                "unavailable",
                new DllNotFoundException()));
        var expected = new LdTestLdfWriteResult(
            true,
            "fallback",
            SamplesWritten: 4,
            StartSample: 2,
            EndSample: 6,
            OutputPath: "sample.ldf");
        var fallback = new RecordingWriter(_ => expected);
        var writer = new LdTestLdfWriter(unavailable, fallback);

        LdTestLdfWriteResult actual = writer.Write(session, 2, 6, input);

        Assert.Equal(expected, actual);
        Assert.Equal(1, unavailable.CallCount);
        Assert.Equal(1, fallback.CallCount);
        Assert.Same(session, fallback.LastCall!.Value.Session);
        Assert.Same(input, fallback.LastCall.Value.Input);
        Assert.Equal(2, fallback.LastCall.Value.StartSample);
        Assert.Equal(6, fallback.LastCall.Value.EndSample);
    }

    [Fact(DisplayName = "LD test LDF does not hide native output failures behind FFmpeg")]
    public void OutputFailureDoesNotUseFallback()
    {
        using DecodeSession session = CreateSession("input.s16", "sample.ldf");
        using var input = new MemoryStream();
        var preferred = new RecordingWriter(
            _ => throw new InvalidDataException("write failed"));
        var fallback = new RecordingWriter(
            _ => throw new InvalidOperationException("fallback must not run"));
        var writer = new LdTestLdfWriter(preferred, fallback);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => writer.Write(session, 0, 1, input));

        Assert.Equal("write failed", exception.Message);
        Assert.Equal(1, preferred.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    private static DecodeSession CreateSession(string inputPath, string ldfPath)
    {
        string outputBase = Path.Combine(
            Path.GetDirectoryName(ldfPath) ?? string.Empty,
            "decoded");
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc,
        [
            "--NTSC",
            "--noEFM",
            "--disable_analog_audio",
            "--write-test-ldf",
            ldfPath,
            inputPath,
            outputBase
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static void WritePcm16(string path, IReadOnlyList<short> samples)
    {
        byte[] bytes = new byte[checked(samples.Count * sizeof(short))];
        for (int i = 0; i < samples.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), samples[i]);
        }

        File.WriteAllBytes(path, bytes);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private readonly record struct WriterCall(
        DecodeSession Session,
        long StartSample,
        long EndSample,
        Stream Input);

    private sealed class RecordingWriter(Func<WriterCall, LdTestLdfWriteResult> write)
        : ILdTestLdfWriter
    {
        public int CallCount { get; private set; }

        public WriterCall? LastCall { get; private set; }

        public LdTestLdfWriteResult Write(
            DecodeSession session,
            long startSample,
            long endSample,
            Stream input)
        {
            CallCount++;
            LastCall = new WriterCall(session, startSample, endSample, input);
            return write(LastCall.Value);
        }
    }
}
