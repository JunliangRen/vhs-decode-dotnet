using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Rf;
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

    [Fact(DisplayName = "LD test LDF round-trips through the production RF loader")]
    public void NativeWriterRoundTripsThroughProductionLoader()
    {
        Assert.SkipUnless(CanRunFfmpeg(), "ffmpeg is not available on PATH.");
        string tempDirectory = CreateTempDirectory();
        try
        {
            const int StartLength = 4_097;
            const long SeekStart = 40_001_003;
            const int SeekLength = 4_099;
            long sampleCount = SeekStart + SeekLength;
            short[] startExpected = CreatePattern(StartLength, seed: 17);
            short[] seekExpected = CreatePattern(SeekLength, seed: 29);

            string inputPath = Path.Combine(tempDirectory, "input.s16");
            WriteSparsePcm16(
                inputPath,
                sampleCount,
                (StartSample: 0, Samples: startExpected),
                (StartSample: SeekStart, Samples: seekExpected));
            string ldfPath = Path.Combine(tempDirectory, "sample.ldf");
            using DecodeSession session = CreateSession(inputPath, ldfPath);
            using FileStream input = File.OpenRead(inputPath);
            var writer = new LdTestLdfWriter();

            LdTestLdfWriteResult result = writer.Write(
                session,
                startSample: 0,
                endSample: sampleCount,
                input);

            Assert.True(result.Success);
            Assert.Equal(sampleCount, result.SamplesWritten);
            Assert.Contains(
                "-ss",
                FfmpegPcm16SampleLoader.BuildPyAvFramedFfmpegArguments(
                    ldfPath,
                    SeekStart,
                    FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz));
            Assert.Equal(
                startExpected.Select(static sample => (double)sample),
                ReadWithProductLoader(ldfPath, startSample: 0, readLength: StartLength));
            Assert.Equal(
                seekExpected.Select(static sample => (double)sample),
                ReadWithProductLoader(ldfPath, startSample: SeekStart, readLength: SeekLength));
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
        => File.WriteAllBytes(path, Pcm16Bytes(samples));

    private static void WriteSparsePcm16(
        string path,
        long sampleCount,
        params (long StartSample, short[] Samples)[] segments)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        output.SetLength(checked(sampleCount * sizeof(short)));
        foreach ((long startSample, short[] samples) in segments)
        {
            output.Position = checked(startSample * sizeof(short));
            output.Write(Pcm16Bytes(samples));
        }
    }

    private static byte[] Pcm16Bytes(IReadOnlyList<short> samples)
    {
        byte[] bytes = new byte[checked(samples.Count * sizeof(short))];
        for (int i = 0; i < samples.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), samples[i]);
        }

        return bytes;
    }

    private static short[] CreatePattern(int sampleCount, int seed)
    {
        var samples = new short[sampleCount];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = unchecked((short)((i * 7_919) + (seed * 1_003) + (i / 17)));
        }

        return samples;
    }

    private static double[] ReadWithProductLoader(
        string path,
        long startSample,
        int readLength)
    {
        IRfSampleLoader loader = RfLoaderFactory.CreateNative(path);
        using IDisposable? disposableLoader = loader as IDisposable;
        using FileStream input = File.OpenRead(path);
        double[]? samples = loader.Read(input, startSample, readLength);
        Assert.NotNull(samples);
        return samples;
    }

    private static bool CanRunFfmpeg()
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-version");
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
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
