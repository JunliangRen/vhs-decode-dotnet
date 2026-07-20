using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            string hugeInteger = "1" + new string('0', 400);
            ParsedCommand command = ParseHiFi(
            [
                "--audio_rate", hugeInteger,
                "--threads", hugeInteger,
                "missing.s16",
                outputPath
            ]);
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

    [Theory(DisplayName = "HiFi output-rate CLI failures match v0.4.0 timing and artifacts")]
    [InlineData("zero", "Sample rate should be over 0")]
    [InlineData("negative", "Sample rate should be over 0")]
    [InlineData("three-billion", "division by zero")]
    [InlineData("float-overflow", "int too large to convert to float")]
    [InlineData("shared-memory-overflow", "Python int too large to convert to C ssize_t")]
    public void HiFiOutputRateCliFailuresMatchV040TimingAndArtifacts(
        string scenario,
        string expectedError)
    {
        string directory = CreateTempDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.s16");
            string outputPath = Path.Combine(directory, "output.wav");
            File.WriteAllBytes(inputPath, []);
            string rate = scenario switch
            {
                "zero" => "0",
                "negative" => "-1",
                "three-billion" => "3000000000",
                "float-overflow" => "1" + new string('0', 309),
                "shared-memory-overflow" => new string('9', 50),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
            ParsedCommand command = ParseHiFi(
            [
                "--pal",
                "--frequency", "6",
                "--raw_format", "s16le",
                "--audio_rate", rate,
                "--threads", "1",
                "--overwrite",
                inputPath,
                outputPath
            ]);
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Equal(
                Lines(
                    "Initializing ...",
                    "PAL VHS format selected, Audio mode is s"),
                output.ToString());
            Assert.Equal(expectedError + Environment.NewLine, error.ToString());
            Assert.False(File.Exists(outputPath));
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

    [Fact(DisplayName = "HiFi bias guess rejects stdin after its Release 4.0 heading")]
    public void HiFiBiasGuessRejectsStdinAfterRelease40Heading()
    {
        string directory = CreateTempDirectory();
        try
        {
            string outputPath = Path.Combine(directory, "stdin-bias.wav");
            ParsedCommand command = ParseHiFi(
                ["--bias_guess", "--raw_format", "s16le", "-", outputPath]);
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Equal(
                Lines(
                    "Initializing ...",
                    "NTSC VHS format selected, Audio mode is s",
                    "Measuring carrier bias ... "),
                output.ToString());
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

    [Fact(DisplayName = "HiFi progress reporting matches Release 4.0 formatting")]
    public void HiFiProgressReportingMatchesRelease40Formatting()
    {
        Assert.Equal(
            "Progress [####################                    ] 50.00%",
            HiFiProgressReporter.FormatProgressBar(16, 32));
        Assert.Equal(
            "Progress [##                                      ] 6.25%",
            HiFiProgressReporter.FormatProgressBar(1, 16));
        Assert.Equal(
            Lines(
                "- Decoding speed: 10000 kFrames/s (2.00x), 3 blocks enqueued",
                "- Input position: 0:00:01.000",
                "- Audio position: 0:00:01.000",
                "- Audio buffer  : 0:00:00.000",
                "- Wall time     : 0:00:00.500"),
            HiFiProgressReporter.FormatStatus(
                5_000_000,
                44_100,
                3,
                5_000_000,
                44_100,
                TimeSpan.FromSeconds(0.5)));
        Assert.Equal(
            Lines(
                "- Decoding speed: 1000 kFrames/s (0.03x), 7 blocks enqueued",
                "- Input position: 0:00:00.309",
                "- Audio position: 0:00:00.208",
                "- Audio buffer  : 0:00:00.100",
                "- Wall time     : 0:00:12.346"),
            HiFiProgressReporter.FormatStatus(
                12_345_678,
                10_000,
                7,
                40_000_000,
                48_000,
                TimeSpan.FromSeconds(12.3456784)));
        Assert.Equal(
            Lines(
                "- Decoding speed: 4042 kFrames/s (0.10x), 42 blocks enqueued",
                "- Input position: 0:06:10.000",
                "- Audio position: 0:05:33.333",
                "- Audio buffer  : 0:00:36.667",
                "- Wall time     : 1:01:01.235"),
            HiFiProgressReporter.FormatStatus(
                14_800_000_000,
                16_000_000,
                42,
                40_000_000,
                48_000,
                TimeSpan.FromSeconds(3661.2345678)));
        Assert.Equal("0:00:00.005", HiFiProgressReporter.FormatSeconds(264.0 / 48_000.0));
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

        float[] shortLeft = Enumerable.Range(0, 1_464).Select(value => (float)value).ToArray();
        float[] shortRight = shortLeft.Select(value => -value).ToArray();
        HiFiDecodedBlock wrapped = HiFiStreamingDecoder.TrimDecodedBlock(
            new HiFiDecodedBlock(shortLeft, shortRight, 0.25f, -0.5f),
            plan,
            finalJob);
        Assert.Equal(1_200.0f, wrapped.Left[0]);
        Assert.Equal(1_463.0f, wrapped.Left[263]);
        Assert.Equal(0.0f, wrapped.Left[264]);
        Assert.Equal(1_199.0f, wrapped.Left[^1]);
        Assert.Equal(-1_200.0f, wrapped.Right[0]);
    }

    [Fact(DisplayName = "HiFi runner matches Release 4.0 PAL VHS synthetic RF WAV")]
    public void HiFiRunnerMatchesRelease40PalVhsSyntheticRfWave()
    {
        string directory = CreateTempDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "pal-vhs-hifi.s16");
            string outputPath = Path.Combine(directory, "pal-vhs-hifi.wav");
            WritePalVhsHiFiRf(inputPath);
            Assert.Equal(
                "055D25D26C86D18F5390BA98DBA32FB65F28B1B186616F8AA52D53E14F940EC4",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))));
            ParsedCommand command = ParseHiFi(
            [
                "--pal", "--frequency", "6", "--threads", "1",
                "--raw_format", "s16le", "--audio_mode", "s",
                "--audio_rate", "48000", "--resampler_quality", "low",
                "--doc", "off", "--head_switching_interpolation", "off",
                "--expander", "off", "--deemphasis", "off",
                "--NR_spectral_amount", "0", "--overwrite",
                inputPath, outputPath
            ]);
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            byte[] wave = File.ReadAllBytes(outputPath);
            Assert.Equal(91_228, wave.Length);
            Assert.Equal(
                "325A4ABFB4922FE814338BAB377A94E6C2FD96277244813433A72F6ED5723553",
                Convert.ToHexString(SHA256.HashData(wave)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi runner matches Release 4.0 NTSC 8mm synthetic RF WAV")]
    public void HiFiRunnerMatchesRelease40Ntsc8mmSyntheticRfWave()
    {
        string directory = CreateTempDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "ntsc-8mm-hifi.s16");
            string outputPath = Path.Combine(directory, "ntsc-8mm-hifi.wav");
            WriteNtsc8mmHiFiRf(inputPath);
            Assert.Equal(
                "FCC01A6CDF4304C41011A02AA4F49BC692BF8B84DC99684B7FC6F7D9052D5820",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))));
            ParsedCommand command = ParseHiFi(
            [
                "--ntsc", "--8mm", "--frequency", "6", "--threads", "1",
                "--raw_format", "s16le", "--audio_mode", "s",
                "--audio_rate", "48000", "--resampler_quality", "low",
                "--doc", "off", "--head_switching_interpolation", "off",
                "--expander", "off", "--deemphasis", "off",
                "--NR_spectral_amount", "0", "--overwrite",
                inputPath, outputPath
            ]);
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            byte[] wave = File.ReadAllBytes(outputPath);
            Assert.Equal(91_228, wave.Length);
            Assert.Equal(
                "E1AAF3F68DF1392617BC28D162D2E3DD2AFE6251E91E06D4D3191540C3EFA83F",
                Convert.ToHexString(SHA256.HashData(wave)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
                ThreadsInteger = 2,
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
            var diagnostics = new StringWriter();

            HiFiStreamingDecodeResult result = decoder.Decode(
                options,
                reader,
                writer,
                diagnostics,
                TestContext.Current.CancellationToken,
                elapsedProvider: () => TimeSpan.FromSeconds(0.5));
            writer.Complete(result.LeftPeak, result.RightPeak, TextWriter.Null);

            Assert.Equal(10, result.InputSamples);
            Assert.Equal(264, result.AudioFrames);
            Assert.Equal(1, result.BlockCount);
            Assert.Equal(0.0f, result.LeftPeak);
            Assert.Equal(0.0f, result.RightPeak);
            byte[] wave = File.ReadAllBytes(path);
            Assert.Equal(1_584u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
            Assert.All(ReadPcm16(wave), value => Assert.Equal((short)0, value));
            Assert.Contains(
                "Progress [########################################] 100.00%",
                diagnostics.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "- Audio position: 0:00:00.005",
                diagnostics.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(
                2,
                diagnostics.ToString().Split(
                    "- Decoding speed:",
                    StringSplitOptions.None).Length - 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory(DisplayName = "HiFi zero-worker queues consume the v0.4.0 idle buffers then wait")]
    [InlineData("0", 2)]
    [InlineData("-1", 1)]
    public async Task HiFiZeroWorkerQueuesConsumeV040IdleBuffersThenWait(
        string threadsText,
        int expectedReads)
    {
        string directory = CreateTempDirectory();
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task<HiFiStreamingDecodeResult>? decodeTask = null;
        try
        {
            string path = Path.Combine(directory, "waiting.wav");
            HiFiDecodeOptions options = DefaultOptions(path) with
            {
                InputRateHz = 1_000_000.0,
                ThreadsInteger = BigInteger.Parse(threadsText)
            };
            using var reader = new FullBlockSampleReader(expectedReads);
            using (var writer = new HiFiOutputWriter(options))
            {
                var decoder = new HiFiStreamingDecoder(_ =>
                    throw new InvalidOperationException("A zero-worker decode created a worker."));
                using var diagnostics = new SignalingTextWriter(
                    $"{expectedReads} blocks enqueued");
                decodeTask = Task.Factory.StartNew(
                    () => decoder.Decode(
                        options,
                        reader,
                        writer,
                        diagnostics,
                        stopSource.Token,
                        elapsedProvider: () => TimeSpan.FromSeconds(1)),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                Assert.True(reader.ExpectedReads.Wait(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
                Assert.True(diagnostics.Signal.Wait(
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
                Assert.False(decodeTask.IsCompleted);
                Assert.Equal(expectedReads, reader.ReadCount);
                Assert.Contains(
                    $"{expectedReads} blocks enqueued",
                    diagnostics.ToString(),
                    StringComparison.Ordinal);

                stopSource.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await decodeTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
            }

            Assert.Equal(44, new FileInfo(path).Length);
        }
        finally
        {
            stopSource.Cancel();
            if (decodeTask is not null)
            {
                try
                {
                    await decodeTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                catch
                {
                    // Preserve the assertion or timeout that ended the test.
                }
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory(DisplayName = "HiFi thread counts below minus one wait before reading like v0.4.0")]
    [InlineData("-2")]
    [InlineData("-99999999999999999999999999999999999999999999999999")]
    public void HiFiThreadCountsBelowMinusOneWaitBeforeReadingLikeV040(string threadsText)
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "no-buffer.wav");
            HiFiDecodeOptions options = DefaultOptions(path) with
            {
                InputRateHz = 1_000_000.0,
                ThreadsInteger = BigInteger.Parse(threadsText)
            };
            using var reader = new FullBlockSampleReader(expectedReads: 1);
            using (var writer = new HiFiOutputWriter(options))
            using (var stopSource = new CancellationTokenSource())
            {
                stopSource.Cancel();
                var decoder = new HiFiStreamingDecoder(_ =>
                    throw new InvalidOperationException("A zero-worker decode created a worker."));

                Assert.ThrowsAny<OperationCanceledException>(() => decoder.Decode(
                    options,
                    reader,
                    writer,
                    TextWriter.Null,
                    stopSource.Token,
                    elapsedProvider: () => TimeSpan.FromSeconds(1)));
            }

            Assert.Equal(0, reader.ReadCount);
            Assert.Equal(44, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi zero threads retain v0.4.0 finishing wait and empty WAV artifact")]
    public async Task HiFiZeroThreadsRetainV040FinishingWaitAndEmptyWaveArtifact()
    {
        string directory = CreateTempDirectory();
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task<int>? runTask = null;
        try
        {
            string inputPath = Path.Combine(directory, "empty.s16");
            string outputPath = Path.Combine(directory, "waiting.wav");
            File.WriteAllBytes(inputPath, []);
            ParsedCommand command = ParseHiFi([
                "--pal",
                "--frequency", "6",
                "--raw_format", "s16le",
                "--audio_rate", "48000",
                "--threads", "0",
                "--overwrite",
                inputPath,
                outputPath
            ]);
            using var output = new SignalingTextWriter(
                "Decode finishing up. Emptying the queue");
            var error = new StringWriter();
            runTask = Task.Factory.StartNew(
                () => new DecodeRunner().Run(
                    command,
                    output,
                    error,
                    stopSource.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(output.Signal.Wait(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken));
            Assert.False(runTask.IsCompleted);
            stopSource.Cancel();

            Assert.Equal(
                1,
                await runTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
            string transcript = output.Snapshot();
            Assert.StartsWith(
                Lines(
                    "Initializing ...",
                    "PAL VHS format selected, Audio mode is s",
                    "Starting decode..."),
                transcript,
                StringComparison.Ordinal);
            Assert.Contains(
                Environment.NewLine + "Decode finishing up. Emptying the queue" + Environment.NewLine,
                transcript,
                StringComparison.Ordinal);
            Assert.EndsWith(
                "Ctrl-C was pressed, stopping decode..." + Environment.NewLine,
                transcript,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Decode finished successfully", transcript, StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
            byte[] wave = File.ReadAllBytes(outputPath);
            Assert.Equal(44, wave.Length);
            Assert.Equal("RIFF", ReadAscii(wave, 0));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)));
        }
        finally
        {
            stopSource.Cancel();
            if (runTask is not null)
            {
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                catch
                {
                    // Preserve the assertion or timeout that ended the test.
                }
            }

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
            HiFiDecodeOptions options = DefaultOptions(path) with { ThreadsInteger = 2 };
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
        int port = ReserveRandomNetMqPort();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var diagnostics = new StringWriter();
        using var sink = CreateGnuRadioSinkAfterPortRelease(
            port,
            cancellationToken,
            diagnostics);
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
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        int port;
        using (var blocker = new ResponseSocket())
        {
            blocker.Options.Linger = TimeSpan.Zero;
            port = blocker.BindRandomPort("tcp://*");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new HiFiGnuRadioSink(
                    TextWriter.Null,
                    cancellationToken,
                    port));

            Assert.Equal(
                "Unable to initialize the HiFi GNU Radio ZMQ output.",
                exception.Message);
            Assert.NotNull(exception.InnerException);
        }

        using HiFiGnuRadioSink recovered = CreateGnuRadioSinkAfterPortRelease(
            port,
            cancellationToken);
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

    [Fact(DisplayName = "HiFi carrier bias progress matches Release 4.0 running averages")]
    public void HiFiCarrierBiasProgressMatchesRelease40RunningAverages()
    {
        HiFiDecodeOptions options = DefaultOptions() with
        {
            InputRateHz = 5_000_000.0,
            DemodType = HiFiConstants.DemodHilbert,
            BiasGuess = true
        };
        var progress = new List<string>();

        HiFiBiasEstimate estimate = HiFiBiasEstimator.MeasureBlocks(
            options,
            [
                CreateDeterministicFloatInput(131_072, 0x12345678),
                CreateDeterministicFloatInput(131_072, 0x9ABCDEF0)
            ],
            TestContext.Current.CancellationToken,
            (current, total, currentEstimate) => progress.Add(
                HiFiBiasEstimator.FormatProgress(current, total, currentEstimate)));

        Assert.Equal(
            [
                "Carrier L 2.270868 MHz, R 2.679246 MHz "
                    + "[####################                    ] 50.00%",
                "Carrier L 2.270676 MHz, R 2.679689 MHz "
                    + "[########################################] 100.00%"
            ],
            progress);
        Assert.InRange(estimate.LeftCarrierHz, 2_270_675.0, 2_270_676.3);
        Assert.InRange(estimate.RightCarrierHz, 2_679_688.2, 2_679_689.5);
    }

    [Fact(DisplayName = "HiFi bias input ignores raw format overrides like Release 4.0")]
    public void HiFiBiasInputIgnoresRawFormatOverridesLikeRelease40()
    {
        string directory = CreateTempDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "capture.u8");
            string outputPath = Path.Combine(directory, "bias.wav");
            File.WriteAllBytes(inputPath, [0]);
            HiFiDecodeOptions? openedOptions = null;
            var runner = new HiFiDecodeRunner(
                new HiFiStreamingDecoder(),
                (activeOptions, _) =>
                {
                    openedOptions = activeOptions;
                    throw new IOException("stop after observing bias input options");
                },
                activeOptions => new HiFiOutputWriter(activeOptions));
            var output = new StringWriter();

            IOException exception = Assert.Throws<IOException>(() => runner.Run(
                ParseHiFi(
                    ["--bias_guess", "--raw_format", "s16le", inputPath, outputPath]),
                output,
                TextWriter.Null,
                TestContext.Current.CancellationToken));

            Assert.Equal("stop after observing bias input options", exception.Message);
            Assert.NotNull(openedOptions);
            Assert.Equal(inputPath, openedOptions.InputFile);
            Assert.Null(openedOptions.InputFormatOverride);
            Assert.EndsWith(
                "Measuring carrier bias ... " + Environment.NewLine,
                output.ToString(),
                StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi preview conversion matches Release 4.0 int16 wrapping")]
    public void HiFiPreviewConversionMatchesRelease40Int16Wrapping()
    {
        float[] input =
        [
            -2.0f,
            -1.1f,
            -1.0f,
            -0.99999f,
            -0.5f,
            -1.0f / 65_536.0f,
            0.0f,
            1.0f / 65_536.0f,
            0.5f,
            0.99999f,
            1.0f,
            1.1f,
            2.0f,
            float.NaN,
            float.PositiveInfinity,
            float.NegativeInfinity
        ];

        Assert.Equal(
            [0, 29_492, -32_768, -32_767, -16_384, 0, 0, 0,
                16_384, 32_767, -32_768, -29_492, 0, 0, 0, 0],
            WinMmHiFiPreviewSink.ConvertToPcm16(input));
        Assert.Equal(
            [16_384, 8_192],
            WinMmHiFiPreviewSink.ConvertToPcm16([0.5f, 0.25f, 1.0f]));
    }

    [Fact(DisplayName = "HiFi preview factory safely probes the host audio device")]
    public void HiFiPreviewFactorySafelyProbesTheHostAudioDevice()
    {
        using IHiFiPreviewSink? preview = HiFiPreviewSinkFactory.TryCreate(44_100);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(preview);
        }
        else
        {
            preview?.Write(
                new float[44 * 2],
                TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "HiFi preview routes post-processed stereo to a 44.1 kHz sink")]
    public void HiFiPreviewRoutesPostProcessedStereoTo44100HzSink()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "preview.wav");
            var preview = new RecordingPreviewSink();
            int requestedRate = 0;
            var streamingDecoder = new HiFiStreamingDecoder(
                activeOptions => new ZeroBlockDecoder(activeOptions));
            var runner = new HiFiDecodeRunner(
                streamingDecoder,
                (_, _) => new ArraySampleReader(new float[10]),
                activeOptions => new HiFiOutputWriter(activeOptions),
                sampleRate =>
                {
                    requestedRate = sampleRate;
                    return preview;
                });
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = runner.Run(
                ParseHiFi(["--preview", "--raw_format", "s16le", "-", path]),
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(44_100, requestedRate);
            Assert.Single(preview.Blocks);
            Assert.NotEmpty(preview.Blocks[0]);
            Assert.Equal(0, preview.Blocks[0].Length % 2);
            Assert.True(preview.Disposed);
            Assert.DoesNotContain(
                "preview is not available",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
            byte[] wave = File.ReadAllBytes(path);
            Assert.Equal(44_100u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(24, 4)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi preview falls back without suppressing file output")]
    public void HiFiPreviewFallsBackWithoutSuppressingFileOutput()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "fallback.wav");
            var streamingDecoder = new HiFiStreamingDecoder(
                activeOptions => new ZeroBlockDecoder(activeOptions));
            var runner = new HiFiDecodeRunner(
                streamingDecoder,
                (_, _) => new ArraySampleReader(new float[10]),
                activeOptions => new HiFiOutputWriter(activeOptions),
                _ => null);
            var output = new StringWriter();

            int exitCode = runner.Run(
                ParseHiFi(["--preview", "--raw_format", "s16le", "-", path]),
                output,
                TextWriter.Null,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains(
                "Import of sounddevice failed, preview is not available!",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 44);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi preview is disposed when input opening fails")]
    public void HiFiPreviewIsDisposedWhenInputOpeningFails()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "input-failure.wav");
            var preview = new RecordingPreviewSink();
            var runner = new HiFiDecodeRunner(
                new HiFiStreamingDecoder(
                    activeOptions => new ZeroBlockDecoder(activeOptions)),
                (_, _) => throw new IOException("synthetic preview input failure"),
                activeOptions => new HiFiOutputWriter(activeOptions),
                _ => preview);

            IOException exception = Assert.Throws<IOException>(() => runner.Run(
                ParseHiFi(["--preview", "--raw_format", "s16le", "-", path]),
                TextWriter.Null,
                TextWriter.Null,
                TestContext.Current.CancellationToken));

            Assert.Equal("synthetic preview input failure", exception.Message);
            Assert.True(preview.Disposed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    [Fact(DisplayName = "HiFi FLAC STREAMINFO exposes the Release 4.0 frame total")]
    public void HiFiFlacStreamInfoExposesRelease40FrameTotal()
    {
        string directory = CreateTempDirectory();
        try
        {
            const long ExpectedSamples = 0x0ABCDEF12;
            string path = Path.Combine(directory, "stream-info.flac");
            var bytes = new byte[42];
            "fLaC"u8.CopyTo(bytes);
            bytes[4] = 0x80;
            bytes[7] = 34;
            ulong packed = ((ulong)48_000 << 44)
                | ((ulong)(2 - 1) << 41)
                | ((ulong)(24 - 1) << 36)
                | ExpectedSamples;
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), packed);
            File.WriteAllBytes(path, bytes);

            Assert.Equal(ExpectedSamples, HiFiInputReader.TryReadFlacTotalSamples(path));
            Assert.Null(HiFiInputReader.TryReadFlacTotalSamples(Path.Combine(directory, "missing.flac")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi short 24-bit FLAC input matches Release 4.0 libsndfile samples")]
    public void HiFiShort24BitFlacInputMatchesRelease40LibsndfileSamples()
    {
        Assert.SkipUnless(CanRunFfmpeg(), "ffmpeg is not available on PATH.");
        string directory = CreateTempDirectory();
        try
        {
            const int SampleCount = 10_000;
            string outputPath = Path.Combine(directory, "source.flac");
            HiFiDecodeOptions writeOptions = DefaultOptions(outputPath) with
            {
                AudioMode = HiFiConstants.AudioModeDualMono
            };
            var expected = new float[SampleCount];
            for (int i = 0; i < expected.Length; i++)
            {
                short sample = unchecked((short)(i * 7_919));
                expected[i] = sample / 32_768.0f;
            }

            using (var writer = new HiFiOutputWriter(writeOptions))
            {
                writer.Write(new HiFiPostProcessedBlock(
                    expected,
                    expected,
                    [],
                    1.0f,
                    1.0f));
                writer.Complete(1.0f, 1.0f, TextWriter.Null);
            }

            string inputPath = HiFiOutputWriter.GetDualMonoFilename(
                outputPath,
                "channel_1");
            using IHiFiSampleReader reader = HiFiInputReader.Open(
                DefaultOptions() with { InputFile = inputPath },
                TextWriter.Null);
            var actual = new float[SampleCount];

            int read = reader.Read(actual, TestContext.Current.CancellationToken);

            Assert.Equal(SampleCount, reader.TotalSamples);
            Assert.Equal(SampleCount, read);
            Assert.Equal(expected, actual);
            Assert.Equal(
                0,
                reader.Read(new float[1], TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi LDF input prefers the Release 4.0 flac fallback")]
    public void HiFiLdfInputPrefersRelease40FlacFallback()
    {
        string directory = CreateTempDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "capture.ldf");
            File.WriteAllBytes(inputPath, "fLaC"u8.ToArray());
            var host = new RecordingInputProcessHost(
                new Dictionary<string, HiFiToolProbeResult>(StringComparer.Ordinal)
                {
                    ["flac"] = new(true, "flac 1.4.3")
                });
            var output = new StringWriter();

            using IHiFiSampleReader reader = HiFiInputReader.Open(
                DefaultOptions() with
                {
                    InputFile = inputPath,
                    InputFormatOverride = "s10le"
                },
                output,
                host);

            Assert.Equal(
                ["ld-ldf-reader", "ld-ldf-reader-py", "flac"],
                host.ProbeCalls.Select(call => call.FileName));
            Assert.Equal(["--help"], host.ProbeCalls[0].Arguments);
            Assert.Equal(["--help"], host.ProbeCalls[1].Arguments);
            Assert.Equal(["-version"], host.ProbeCalls[2].Arguments);
            HiFiInputProcessOpenCall opened = Assert.IsType<HiFiInputProcessOpenCall>(
                host.OpenCall);
            Assert.Equal("flac", opened.FileName);
            Assert.Equal(
                [
                    "-d", "-c", "-s", "-F", "--force-raw-format",
                    "--endian", "little", "--sign", "signed", inputPath
                ],
                opened.Arguments);
            Assert.Equal(HiFiRawSampleFormat.S10Le, opened.Format);
            Assert.Null(opened.TotalSamples);
            Assert.Equal(
                Lines(
                    "WARN: ld-ldf-reader not installed (or not in PATH)",
                    "WARN: ld-ldf-reader-py not installed (or not in PATH)",
                    "WARN: ld-ldf-reader/ld-ldf-reader-py not installed. "
                        + "LDF file format may not decode correctly",
                    "Found flac 1.4.3"),
                output.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HiFi LDF and unknown inputs retain Release 4.0 FFmpeg fallback flags")]
    public void HiFiContainerInputsRetainRelease40FfmpegFallbackFlags()
    {
        string directory = CreateTempDirectory();
        try
        {
            string ldfPath = Path.Combine(directory, "capture.ldf");
            string unknownPath = Path.Combine(directory, "capture.oga");
            File.WriteAllBytes(ldfPath, [0]);
            File.WriteAllBytes(unknownPath, [0]);
            var ldfHost = new RecordingInputProcessHost(
                new Dictionary<string, HiFiToolProbeResult>(StringComparer.Ordinal)
                {
                    ["ffmpeg"] = new(true, "ffmpeg version 7.1")
                });
            var ldfOutput = new StringWriter();

            using (HiFiInputReader.Open(
                DefaultOptions() with { InputFile = ldfPath },
                ldfOutput,
                ldfHost))
            {
            }

            Assert.Equal(
                ["ld-ldf-reader", "ld-ldf-reader-py", "flac", "ffmpeg"],
                ldfHost.ProbeCalls.Select(call => call.FileName));
            Assert.Equal(
                Lines(
                    "WARN: ld-ldf-reader not installed (or not in PATH)",
                    "WARN: ld-ldf-reader-py not installed (or not in PATH)",
                    "WARN: ld-ldf-reader/ld-ldf-reader-py not installed. "
                        + "LDF file format may not decode correctly",
                    "WARN: flac not installed (or not in PATH)",
                    "Found ffmpeg version 7.1"),
                ldfOutput.ToString());
            AssertFfmpegFallback(ldfHost.OpenCall, ldfPath);

            var unknownHost = new RecordingInputProcessHost(
                new Dictionary<string, HiFiToolProbeResult>(StringComparer.Ordinal)
                {
                    ["ffmpeg"] = new(true, "ffmpeg version 7.1")
                });
            var unknownOutput = new StringWriter();
            using (HiFiInputReader.Open(
                DefaultOptions() with { InputFile = unknownPath },
                unknownOutput,
                unknownHost))
            {
            }

            Assert.Equal(["ffmpeg"], unknownHost.ProbeCalls.Select(call => call.FileName));
            Assert.Equal(
                Lines(
                    "WARN: Unknown file format.",
                    "WARN: Attempting to decode with ffmpeg",
                    "Found ffmpeg version 7.1"),
                unknownOutput.ToString());
            AssertFfmpegFallback(unknownHost.OpenCall, unknownPath);
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

    private static float[] CreateDeterministicFloatInput(
        int length,
        uint state = 0x12345678)
    {
        var input = new float[length];
        for (int i = 0; i < input.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            input[i] = ((int)(state >> 8) - 0x800000) / (float)0x800000;
        }

        return input;
    }

    private static void WritePalVhsHiFiRf(string path)
        => WriteHiFiRf(path, 1_400_000.0, 150_000.0, 1_800_000.0, 150_000.0);

    private static void WriteNtsc8mmHiFiRf(string path)
        => WriteHiFiRf(path, 1_500_000.0, 100_000.0, 1_700_000.0, 50_000.0);

    private static void WriteHiFiRf(
        string path,
        double leftCarrier,
        double leftCarrierDeviation,
        double rightCarrier,
        double rightCarrierDeviation)
    {
        const int SampleRate = 6_000_000;
        const int SampleCount = 2_800_000;
        const int ChunkSamples = 262_144;
        const double LeftModulationHz = 997.0;
        const double RightModulationHz = 1_433.0;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var bytes = new byte[ChunkSamples * sizeof(short)];
        for (int start = 0; start < SampleCount; start += ChunkSamples)
        {
            int length = Math.Min(ChunkSamples, SampleCount - start);
            for (int offset = 0; offset < length; offset++)
            {
                int sample = start + offset;
                double time = (double)sample / SampleRate;
                double leftPhase = ((2.0 * Math.PI * leftCarrier) * time)
                    + (((leftCarrierDeviation * 0.40) / LeftModulationHz)
                        * (1.0 - Math.Cos((2.0 * Math.PI * LeftModulationHz) * time)));
                double rightPhase = ((2.0 * Math.PI * rightCarrier) * time)
                    + (((rightCarrierDeviation * 0.30) / RightModulationHz)
                        * (1.0 - Math.Cos((2.0 * Math.PI * RightModulationHz) * time)));
                double rf = (0.46 * Math.Sin(leftPhase)) + (0.46 * Math.Sin(rightPhase));
                short pcm = checked((short)Math.Round(
                    rf * short.MaxValue,
                    MidpointRounding.ToEven));
                BinaryPrimitives.WriteInt16LittleEndian(
                    bytes.AsSpan(offset * sizeof(short), sizeof(short)),
                    pcm);
            }

            stream.Write(bytes, 0, length * sizeof(short));
        }
    }

    private static string ReadAscii(byte[] bytes, int offset)
        => System.Text.Encoding.ASCII.GetString(bytes, offset, 4);

    private static string Lines(params string[] lines)
        => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static HiFiGnuRadioSink CreateGnuRadioSinkAfterPortRelease(
        int port,
        CancellationToken cancellationToken,
        TextWriter? output = null)
    {
        var timeout = Stopwatch.StartNew();
        InvalidOperationException? lastFailure = null;
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new HiFiGnuRadioSink(output ?? TextWriter.Null, cancellationToken, port);
            }
            catch (InvalidOperationException ex) when (
                ex.InnerException is AddressAlreadyInUseException)
            {
                lastFailure = ex;
                Thread.Sleep(25);
            }
        }

        throw new InvalidOperationException(
            $"ZMQ port {port} was not released within the test timeout.",
            lastFailure);
    }

    private static int ReserveRandomNetMqPort()
    {
        using var reservation = new ResponseSocket();
        reservation.Options.Linger = TimeSpan.Zero;
        return reservation.BindRandomPort("tcp://*");
    }

    private static void AssertFfmpegFallback(
        HiFiInputProcessOpenCall? openCall,
        string inputPath)
    {
        HiFiInputProcessOpenCall opened = Assert.IsType<HiFiInputProcessOpenCall>(openCall);
        Assert.Equal("ffmpeg", opened.FileName);
        Assert.Equal(
            [
                "-hide_banner", "-loglevel", "error", "-ignore_unknown",
                "-fflags", "nobuffer", "-i", inputPath,
                "-f", "s16le", "-acodec", "pcm_s16le",
                "-avoid_negative_ts", "disabled", "-"
            ],
            opened.Arguments);
        Assert.Equal(HiFiRawSampleFormat.S16Le, opened.Format);
        Assert.Null(opened.TotalSamples);
    }

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

    private sealed class FullBlockSampleReader(int expectedReads) : IHiFiSampleReader
    {
        private int _readCount;

        public ManualResetEventSlim ExpectedReads { get; } = new();

        public int ReadCount => Volatile.Read(ref _readCount);

        public long? TotalSamples => null;

        public int Read(Span<float> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int current = Interlocked.Increment(ref _readCount);
            if (current >= expectedReads)
            {
                ExpectedReads.Set();
            }

            return destination.Length;
        }

        public void Dispose() => ExpectedReads.Dispose();
    }

    private sealed class SignalingTextWriter(string signalLine) : StringWriter
    {
        private readonly object _gate = new();

        public ManualResetEventSlim Signal { get; } = new();

        public override void Write(char value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                base.Write(value);
                SignalIfMatched(value);
            }
        }

        public override void WriteLine()
        {
            lock (_gate)
            {
                base.WriteLine();
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                base.WriteLine(value);
                SignalIfMatched(value);
            }
        }

        private void SignalIfMatched(string? value)
        {
            if (value?.Contains(signalLine, StringComparison.Ordinal) == true)
            {
                Signal.Set();
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                return ToString();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Signal.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed record HiFiInputProcessProbeCall(
        string FileName,
        string[] Arguments);

    private sealed record HiFiInputProcessOpenCall(
        string FileName,
        string[] Arguments,
        HiFiRawSampleFormat Format,
        long? TotalSamples);

    private sealed class RecordingInputProcessHost(
        IReadOnlyDictionary<string, HiFiToolProbeResult> probes) : IHiFiInputProcessHost
    {
        public List<HiFiInputProcessProbeCall> ProbeCalls { get; } = [];

        public HiFiInputProcessOpenCall? OpenCall { get; private set; }

        public HiFiToolProbeResult Probe(
            string fileName,
            IReadOnlyList<string> arguments)
        {
            ProbeCalls.Add(new HiFiInputProcessProbeCall(fileName, [.. arguments]));
            return probes.TryGetValue(fileName, out HiFiToolProbeResult result)
                ? result
                : default;
        }

        public IHiFiSampleReader Open(
            string fileName,
            IReadOnlyList<string> arguments,
            HiFiRawSampleFormat format,
            long? totalSamples)
        {
            OpenCall = new HiFiInputProcessOpenCall(
                fileName,
                [.. arguments],
                format,
                totalSamples);
            return new ArraySampleReader([]);
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

    private sealed class RecordingPreviewSink : IHiFiPreviewSink
    {
        public List<float[]> Blocks { get; } = [];
        public bool Disposed { get; private set; }

        public void Write(
            ReadOnlySpan<float> stereoSamples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Blocks.Add(stereoSamples.ToArray());
        }

        public void Dispose() => Disposed = true;
    }
}
