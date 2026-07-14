using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NetMQ;
using NetMQ.Sockets;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class HiFiRunnerTests
{
    [Fact(DisplayName = "HiFi command is registered for facade and standalone dispatch")]
    public void HiFiCommandIsRegisteredForFacadeAndStandaloneDispatch()
    {
        Assert.Contains(CliSpecs.HiFi, CliSpecs.AllCommands);
        Assert.True(DecodeDispatcher.TryDispatch(
            ["hifi", "in.s16", "out.wav"],
            out DecodeCommandSpec? facade,
            out string[] facadeArguments));
        Assert.Same(CliSpecs.HiFi, facade);
        Assert.Equal(["in.s16", "out.wav"], facadeArguments);
        Assert.True(DecodeDispatcher.TryDispatch(
            ["hifi-decode", "in.s16", "out.wav"],
            out DecodeCommandSpec? standalone,
            out string[] standaloneArguments));
        Assert.Same(CliSpecs.HiFi, standalone);
        Assert.Equal(["in.s16", "out.wav"], standaloneArguments);
        Assert.Equal(
            ["hifi", "--pal", "in.s16", "out.wav"],
            DecodeDispatcher.NormalizeInvocation(
                ["--pal", "in.s16", "out.wav"],
                "hifi-decode.exe"));
        Assert.Equal("hifi-decode", DecodeDispatcher.InvocationProgramName("hifi-decode.exe"));
    }

    [Fact(DisplayName = "Decode runner routes HiFi before video session creation")]
    public void DecodeRunnerRoutesHiFiBeforeVideoSessionCreation()
    {
        var hiFiRunner = new RecordingHiFiRunner(37);
        var runner = new DecodeRunner(
            _ => throw new InvalidOperationException("Video engine must not be created."),
            hiFiRunner);
        ParsedCommand command = ParseHiFi(["in.s16", "out.wav"]);
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = runner.Run(
            command,
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(37, exitCode);
        Assert.Same(command, hiFiRunner.Command);
        Assert.Same(output, hiFiRunner.Output);
        Assert.Same(error, hiFiRunner.Error);
    }

    [Fact(DisplayName = "HiFi runner preserves existing-output conflict behavior")]
    public void HiFiRunnerPreservesExistingOutputConflictBehavior()
    {
        string directory = CreateTempDirectory();
        try
        {
            string outputPath = Path.Combine(directory, "existing.wav");
            File.WriteAllText(outputPath, "existing");
            ParsedCommand command = ParseHiFi(["missing.s16", outputPath]);
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Equal(
                "Existing decode files found, remove them or run command with --overwrite"
                + Environment.NewLine
                + "\t " + outputPath + Environment.NewLine,
                output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi runner requires a raw format for stdin")]
    public void HiFiRunnerRequiresRawFormatForStdin()
    {
        string directory = CreateTempDirectory();
        try
        {
            string outputPath = Path.Combine(directory, "stdin.wav");
            ParsedCommand command = ParseHiFi(["-", outputPath]);
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains("Starting decode...", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(
                "`--raw_format <format>` is required for stdin input" + Environment.NewLine,
                error.ToString());
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi block framer matches Release 4.0 first middle and final overlap")]
    public void HiFiBlockFramerMatchesRelease40FirstMiddleAndFinalOverlap()
    {
        HiFiDecodePlan plan = SmallFramingPlan();
        using var reader = new ArraySampleReader(
            Enumerable.Range(0, 37).Select(value => (float)value).ToArray());
        var framer = new HiFiBlockFramer(plan, reader);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        HiFiDecodeJob first = Assert.IsType<HiFiDecodeJob>(framer.ReadNext(cancellationToken));
        HiFiDecodeJob middle = Assert.IsType<HiFiDecodeJob>(framer.ReadNext(cancellationToken));
        HiFiDecodeJob last = Assert.IsType<HiFiDecodeJob>(framer.ReadNext(cancellationToken));

        Assert.Equal(0, first.BlockNumber);
        Assert.Equal(16, first.FramesRead);
        Assert.False(first.IsLastBlock);
        Assert.Equal(18, first.PlanInputSamples);
        Assert.Equal(
            [0, 1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
            first.Samples);

        Assert.Equal(1, middle.BlockNumber);
        Assert.Equal(16, middle.FramesRead);
        Assert.False(middle.IsLastBlock);
        Assert.Equal(20, middle.PlanInputSamples);
        Assert.Equal(
            [12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31],
            middle.Samples);

        Assert.Equal(2, last.BlockNumber);
        Assert.Equal(5, last.FramesRead);
        Assert.True(last.IsLastBlock);
        Assert.Equal(20, last.PlanInputSamples);
        Assert.Equal(
            [19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 0, 0],
            last.Samples);
        Assert.Null(framer.ReadNext(cancellationToken));
        Assert.Equal(3, framer.BlockCount);
        Assert.Equal(37, framer.InputSamplesRead);
    }

    [Fact(DisplayName = "HiFi block framer emits Release 4.0 terminal block at exact EOF")]
    public void HiFiBlockFramerEmitsRelease40TerminalBlockAtExactEof()
    {
        using var reader = new ArraySampleReader(
            Enumerable.Range(0, 32).Select(value => (float)value).ToArray());
        var framer = new HiFiBlockFramer(SmallFramingPlan(), reader);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        _ = framer.ReadNext(cancellationToken);
        _ = framer.ReadNext(cancellationToken);
        HiFiDecodeJob terminal = Assert.IsType<HiFiDecodeJob>(framer.ReadNext(cancellationToken));

        Assert.True(terminal.IsLastBlock);
        Assert.Equal(0, terminal.FramesRead);
        Assert.Equal([28, 29, 30, 31], terminal.Samples[..4]);
        Assert.All(terminal.Samples[4..], value => Assert.Equal(0.0f, value));
    }

    [Fact(DisplayName = "HiFi decoded block trimming matches Release 4.0 worker offsets")]
    public void HiFiDecodedBlockTrimmingMatchesRelease40WorkerOffsets()
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions());
        float[] left = Enumerable.Range(0, 24_000).Select(value => (float)value).ToArray();
        float[] right = left.Select(value => -value).ToArray();
        var decoded = new HiFiDecodedBlock(left, right, 0.25f, -0.5f);
        var middleJob = new HiFiDecodeJob(
            1,
            [],
            20_000_000,
            false,
            20_000_000);

        HiFiDecodedBlock middle = HiFiStreamingDecoder.TrimDecodedBlock(
            decoded,
            plan,
            middleJob);

        Assert.Equal(23_472, middle.Left.Length);
        Assert.Equal(264.0f, middle.Left[0]);
        Assert.Equal(23_735.0f, middle.Left[^1]);
        Assert.Equal(-264.0f, middle.Right[0]);

        var finalJob = new HiFiDecodeJob(
            2,
            [],
            1_000_000,
            true,
            20_000_000);
        HiFiDecodedBlock final = HiFiStreamingDecoder.TrimDecodedBlock(
            decoded,
            plan,
            finalJob);
        Assert.Equal(1_464, final.Left.Length);
        Assert.Equal(22_272.0f, final.Left[0]);
        Assert.Equal(23_735.0f, final.Left[^1]);
    }

    [Fact(DisplayName = "HiFi parallel streaming pipeline completes and preserves block order")]
    public void HiFiParallelStreamingPipelineCompletesAndPreservesBlockOrder()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "stream.wav");
            HiFiDecodeOptions options = DefaultOptions(path) with
            {
                Threads = 2,
                EnableDeemphasis = false,
                EnableExpander = false,
                HeadSwitchingInterpolation = false,
                DropoutCompensation = HiFiConstants.DropoutCompensationDisabled,
                SpectralNoiseReductionAmount = 0.0
            };
            using var reader = new ArraySampleReader(new float[10]);
            using var writer = new HiFiOutputWriter(options);
            var decoder = new HiFiStreamingDecoder(
                activeOptions => new ZeroBlockDecoder(activeOptions));

            HiFiStreamingDecodeResult result = decoder.Decode(
                options,
                reader,
                writer,
                TextWriter.Null,
                TestContext.Current.CancellationToken);
            writer.Complete(result.LeftPeak, result.RightPeak, TextWriter.Null);

            Assert.Equal(10, result.InputSamples);
            Assert.Equal(264, result.AudioFrames);
            Assert.Equal(1, result.BlockCount);
            Assert.Equal(0.0f, result.LeftPeak);
            Assert.Equal(0.0f, result.RightPeak);
            byte[] wave = File.ReadAllBytes(path);
            Assert.Equal(1_584u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
            Assert.All(ReadPcm16(wave), value => Assert.Equal((short)0, value));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi worker failures remain diagnostic and finalize partial output")]
    public void HiFiWorkerFailuresRemainDiagnosticAndFinalizePartialOutput()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "failed.wav");
            HiFiDecodeOptions options = DefaultOptions(path) with { Threads = 2 };
            using var reader = new ArraySampleReader(new float[10]);
            var decoder = new HiFiStreamingDecoder(
                activeOptions => new ThrowingBlockDecoder(activeOptions));
            using (var writer = new HiFiOutputWriter(options))
            {
                InvalidDataException exception = Assert.Throws<InvalidDataException>(() => decoder.Decode(
                    options,
                    reader,
                    writer,
                    TextWriter.Null,
                    TestContext.Current.CancellationToken));
                Assert.Equal("synthetic HiFi worker failure", exception.Message);
            }

            byte[] wave = File.ReadAllBytes(path);
            Assert.Equal("RIFF", ReadAscii(wave, 0));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi GNU Radio sink matches Release 4.0 REP float32 protocol")]
    public async Task HiFiGnuRadioSinkMatchesRelease40RepFloat32Protocol()
    {
        int port = GnuRadioRfAfeBridge.FindAvailablePort(5_700, 5_800)
            ?? throw new InvalidOperationException("No test ZMQ port is available.");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var diagnostics = new StringWriter();
        using var sink = new HiFiGnuRadioSink(diagnostics, cancellationToken, port);
        using var requester = new RequestSocket();
        requester.Options.Linger = TimeSpan.Zero;
        requester.Connect($"tcp://localhost:{port}");
        float[] expected = [1.25f, -2.5f, 0.0f, float.NaN];

        Task sender = Task.Run(() => sink.Send(expected), cancellationToken);
        requester.SendFrame(Array.Empty<byte>());
        byte[] bytes = requester.ReceiveFrameBytes();
        await sender.WaitAsync(cancellationToken);

        Assert.Equal(expected, MemoryMarshal.Cast<byte, float>(bytes).ToArray());
        Assert.Equal(
            $"Initializing ZMQSend (REP) at pid {Environment.ProcessId}, port {port}"
            + Environment.NewLine,
            diagnostics.ToString());
    }

    [Fact(DisplayName = "HiFi GNU Radio startup failures release their worker resources")]
    public void HiFiGnuRadioStartupFailuresReleaseTheirWorkerResources()
    {
        int port = GnuRadioRfAfeBridge.FindAvailablePort(5_800, 5_900)
            ?? throw new InvalidOperationException("No test ZMQ port is available.");
        using (var blocker = new ResponseSocket())
        {
            blocker.Options.Linger = TimeSpan.Zero;
            blocker.Bind($"tcp://*:{port}");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new HiFiGnuRadioSink(
                    TextWriter.Null,
                    TestContext.Current.CancellationToken,
                    port));

            Assert.Equal(
                "Unable to initialize the HiFi GNU Radio ZMQ output.",
                exception.Message);
            Assert.NotNull(exception.InnerException);
        }

        using var recovered = new HiFiGnuRadioSink(
            TextWriter.Null,
            TestContext.Current.CancellationToken,
            port);
    }

    [Fact(DisplayName = "HiFi carrier bias measurement matches Release 4.0")]
    public void HiFiCarrierBiasMeasurementMatchesRelease40()
    {
        HiFiDecodeOptions options = DefaultOptions() with
        {
            InputRateHz = 5_000_000.0,
            DemodType = HiFiConstants.DemodHilbert,
            BiasGuess = true
        };

        HiFiBiasEstimate estimate = HiFiBiasEstimator.MeasureBlocks(
            options,
            [CreateDeterministicFloatInput(131_072)],
            TestContext.Current.CancellationToken);

        Assert.InRange(estimate.LeftCarrierHz, 2_270_867.5, 2_270_868.8);
        Assert.InRange(estimate.RightCarrierHz, 2_679_245.5, 2_679_246.8);
    }

    [Fact(DisplayName = "HiFi WAV output matches libsndfile PCM16 quantization and padding")]
    public void HiFiWaveOutputMatchesLibsndfilePcm16QuantizationAndPadding()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "decode.wav");
            HiFiDecodeOptions options = DefaultOptions(path);
            var diagnostics = new StringWriter();
            using (var writer = new HiFiOutputWriter(options))
            {
                writer.WriteInitialPadding(5);
                writer.Write(new HiFiPostProcessedBlock(
                    [],
                    [],
                    [-2.0f, -1.0f / 65536.0f, 1.0f / 65536.0f, 0.99999f, float.NaN, float.PositiveInfinity],
                    0.0f,
                    0.0f));
                writer.Complete(0.75f, 0.5f, diagnostics);
            }

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF", ReadAscii(bytes, 0));
            Assert.Equal("WAVE", ReadAscii(bytes, 8));
            Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)));
            Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
            Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
            Assert.Equal(
                [0, 0, 0, 0, -32768, -1, 0, 32767, -32768, 32767],
                ReadPcm16(bytes));
            Assert.Equal(
                Environment.NewLine + "Peak gain is 75.00%.",
                diagnostics.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi default output is 24-bit stereo FLAC")]
    public void HiFiDefaultOutputIs24BitStereoFlac()
    {
        Assert.SkipUnless(CanRunFfmpeg(), "ffmpeg is not available on PATH.");
        Assert.Equal(0, HiFiOutputWriter.QuantizeFlacPcm24(-1.0f / 16_777_216.0f));
        Assert.Equal(0, HiFiOutputWriter.QuantizeFlacPcm24(1.0f / 16_777_216.0f));
        Assert.Equal(469_889, HiFiOutputWriter.QuantizeFlacPcm24(0.05601511150598526f));
        Assert.Equal(-3_652_894, HiFiOutputWriter.QuantizeFlacPcm24(-0.4354589283466339f));
        Assert.Throws<InvalidDataException>(
            () => HiFiOutputWriter.QuantizeFlacPcm24(float.NaN));
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "decode.flac");
            HiFiDecodeOptions options = DefaultOptions(path);
            using (var writer = new HiFiOutputWriter(options))
            {
                writer.Write(new HiFiPostProcessedBlock(
                    [],
                    [],
                    [
                        -1.0f / 16_777_216.0f,
                        1.0f / 16_777_216.0f,
                        0.05601511150598526f,
                        -0.4354589283466339f
                    ],
                    0.5f,
                    0.5f));
                writer.Complete(0.5f, 0.5f, TextWriter.Null);
            }

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal("fLaC", ReadAscii(bytes, 0));
            Assert.Equal(0, bytes[4] & 0x7f);
            Assert.Equal(34, (bytes[5] << 16) | (bytes[6] << 8) | bytes[7]);
            ulong streamInfo = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18, 8));
            Assert.Equal(48_000ul, streamInfo >> 44);
            Assert.Equal(2ul, ((streamInfo >> 41) & 0x7) + 1);
            Assert.Equal(24ul, ((streamInfo >> 36) & 0x1f) + 1);
            Assert.Equal(2ul, streamInfo & 0x0f_ffff_ffff);
            Assert.Equal(
                [0, 0, 120_291_584, -935_140_864],
                ReadFlacPcm32(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi dual mono output preserves Release 4.0 names and padding quirk")]
    public void HiFiDualMonoOutputPreservesRelease40NamesAndPaddingQuirk()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "decode.test.wav");
            HiFiDecodeOptions options = DefaultOptions(path) with
            {
                AudioMode = HiFiConstants.AudioModeDualMono
            };
            using (var writer = new HiFiOutputWriter(options))
            {
                writer.WriteInitialPadding(5);
                writer.Write(new HiFiPostProcessedBlock(
                    [0.5f, -0.5f],
                    [0.25f, -0.25f],
                    [],
                    0.5f,
                    0.25f));
                writer.Complete(0.5f, 0.25f, TextWriter.Null);
            }

            string leftPath = Path.Combine(directory, "decode.test_channel_1.wav");
            string rightPath = Path.Combine(directory, "decode.test_channel_2.wav");
            Assert.True(File.Exists(leftPath));
            Assert.True(File.Exists(rightPath));
            Assert.Equal([0, 0, 0, 0, 16384, -16384], ReadPcm16(File.ReadAllBytes(leftPath)));
            Assert.Equal([0, 0, 0, 0, 8192, -8192], ReadPcm16(File.ReadAllBytes(rightPath)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi normalization applies Release 4.0 float16 epsilon gain")]
    public void HiFiNormalizationAppliesRelease40Float16EpsilonGain()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "normalized.wav");
            HiFiDecodeOptions options = DefaultOptions(path) with { Normalize = true };
            using (var writer = new HiFiOutputWriter(options))
            {
                writer.Write(new HiFiPostProcessedBlock(
                    [],
                    [],
                    [0.25f, -0.5f, 0.5f, -0.25f],
                    0.5f,
                    0.5f));
                writer.Complete(0.5f, 0.5f, TextWriter.Null);
            }

            Assert.Equal([16376, -32752, 32752, -16376], ReadPcm16(File.ReadAllBytes(path)));
            Assert.False(File.Exists(HiFiOutputWriter.GetNormalizeFilename(path, 48_000)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi raw input reader follows Release 4.0 extension routing")]
    public void HiFiRawInputReaderFollowsRelease40ExtensionRouting()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "capture.s10le");
            File.WriteAllBytes(path, [0x00, 0xFE, 0x01, 0x00, 0xFF, 0x01]);
            HiFiDecodeOptions options = DefaultOptions() with { InputFile = path };
            using IHiFiSampleReader reader = HiFiInputReader.Open(options, TextWriter.Null);
            var samples = new float[8];

            int count = reader.Read(samples, TestContext.Current.CancellationToken);

            Assert.Equal(3, count);
            Assert.Equal([-1.0f, 1.0f / 512.0f, 511.0f / 512.0f], samples[..count]);
            Assert.Equal(3, reader.TotalSamples);
            Assert.Equal("s16le", HiFiInputReader.NormalizeRawExtension("capture.s16"));
            Assert.Equal("s16lele", HiFiInputReader.NormalizeRawExtension("capture.s16le"));
            Assert.Equal("f32le", HiFiInputReader.NormalizeRawExtension("capture.f32"));

            Assert.Throws<ArgumentException>(() => HiFiInputReader.Open(
                DefaultOptions() with { InputFile = "-", InputFormatOverride = null },
                TextWriter.Null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HiFiDecodePlan SmallFramingPlan()
    {
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(DefaultOptions());
        return plan with
        {
            InitialBlockSizes = new HiFiBlockSizes(0.5, 20, 20, 10, 10),
            BlockOverlap = new HiFiBlockOverlap(4, 2, 2, 1, false)
        };
    }

    private static ParsedCommand ParseHiFi(string[] arguments)
        => new CommandLineParser().Parse(CliSpecs.HiFi, arguments);

    private static HiFiDecodeOptions DefaultOptions(string? outputPath = null)
    {
        ParsedCommand command = ParseHiFi(["-", outputPath ?? "out.wav"]);
        return HiFiDecodeOptions.FromCommand(command);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-hifi-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
            using var process = Process.Start(startInfo);
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

    private static int[] ReadFlacPcm32(string path)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-i", path,
            "-f", "s32le", "-acodec", "pcm_s32le", "pipe:1"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        string error = standardError.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, error);

        byte[] bytes = output.ToArray();
        Assert.Equal(0, bytes.Length % sizeof(int));
        var samples = new int[bytes.Length / sizeof(int)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(i * sizeof(int), sizeof(int)));
        }

        return samples;
    }

    private static float[] CreateDeterministicFloatInput(int length)
    {
        uint state = 0x12345678;
        var input = new float[length];
        for (int i = 0; i < input.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            input[i] = ((int)(state >> 8) - 0x800000) / (float)0x800000;
        }

        return input;
    }

    private static string ReadAscii(byte[] bytes, int offset)
        => System.Text.Encoding.ASCII.GetString(bytes, offset, 4);

    private static short[] ReadPcm16(byte[] wave)
    {
        Assert.Equal("data", ReadAscii(wave, 36));
        int dataLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
        var samples = new short[dataLength / sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(
                wave.AsSpan(44 + (i * sizeof(short)), sizeof(short)));
        }

        return samples;
    }

    private sealed class RecordingHiFiRunner(int exitCode) : IHiFiCommandRunner
    {
        public ParsedCommand? Command { get; private set; }
        public TextWriter? Output { get; private set; }
        public TextWriter? Error { get; private set; }

        public int Run(
            ParsedCommand command,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            Command = command;
            Output = output;
            Error = error;
            return exitCode;
        }
    }

    private sealed class ArraySampleReader(float[] samples) : IHiFiSampleReader
    {
        private int _position;

        public long? TotalSamples => samples.Length;

        public int Read(Span<float> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(destination.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ZeroBlockDecoder : IHiFiBlockDecoder
    {
        public ZeroBlockDecoder(HiFiDecodeOptions options)
        {
            Plan = HiFiDecodePlan.FromOptions(options);
        }

        public HiFiDecodePlan Plan { get; }

        public HiFiDecodedBlock Decode(ReadOnlySpan<float> rfData)
            => new(
                new float[Plan.InitialBlockSizes.FinalAudioSamples],
                new float[Plan.InitialBlockSizes.FinalAudioSamples],
                0.0f,
                0.0f);

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingBlockDecoder : IHiFiBlockDecoder
    {
        public ThrowingBlockDecoder(HiFiDecodeOptions options)
        {
            Plan = HiFiDecodePlan.FromOptions(options);
        }

        public HiFiDecodePlan Plan { get; }

        public HiFiDecodedBlock Decode(ReadOnlySpan<float> rfData)
            => throw new InvalidDataException("synthetic HiFi worker failure");

        public void Dispose()
        {
        }
    }
}
