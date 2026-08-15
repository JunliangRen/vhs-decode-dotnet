using System.Diagnostics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Preview;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PreviewServerTests
{
    [Theory(DisplayName = "Preview server accepts one RF input only for VHS and LD")]
    [InlineData("vhs")]
    [InlineData("ld")]
    public void PreviewServerAcceptsOneRfInputOnlyForSupportedCommands(string commandName)
    {
        DecodeCommandSpec spec = commandName == "vhs" ? CliSpecs.Vhs : CliSpecs.LaserDisc;
        ParsedCommand parsed = new CommandLineParser().Parse(
            spec,
            ["--preview-server", "--preview-crf", "23", "capture.lds"]);

        Assert.True(parsed.Get<bool>("preview_server"));
        Assert.Equal(23, parsed.Get<int>("preview_crf"));
        Assert.Equal("capture.lds", parsed.InputFile);
        Assert.Empty(parsed.OutputBase);

        ParsedCommand defaultCrf = new CommandLineParser().Parse(
            spec,
            ["--preview-server", "capture.lds"]);
        Assert.Equal(31, defaultCrf.Get<int>("preview_crf"));

        Assert.Throws<CommandLineParseException>(() => new CommandLineParser().Parse(
            spec,
            ["--preview-server", "capture.lds", "unexpected-output"]));
    }

    [Fact(DisplayName = "Preview template keeps low-cost chroma and dropout detection enabled")]
    public void PreviewTemplateKeepsLowCostChromaAndDropoutDetectionEnabled()
    {
        ParsedCommand parsed = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "capture.lds"]);

        ParsedCommand template = PreviewDecodeCommandFactory.CreateFastTemplate(parsed);

        Assert.False(template.Get<bool>("skip_chroma"));
        Assert.False(template.Get<bool>("nodod"));
        Assert.True(template.Get<bool>("disable_comb"));
        Assert.Equal(0.0, template.Get<double>("cti_mix"));

        ParsedCommand laserDisc = new CommandLineParser().Parse(
            CliSpecs.LaserDisc,
            ["--preview-server", "--ntsc", "capture.lds"]);
        ParsedCommand laserDiscTemplate = PreviewDecodeCommandFactory.CreateFastTemplate(laserDisc);
        Assert.False(laserDiscTemplate.Get<bool>("nodod"));
        Assert.True(laserDiscTemplate.Get<bool>("noefm"));
    }

    [Fact(DisplayName = "Preview CRF validates the x264 range")]
    public void PreviewCrfValidatesTheX264Range()
    {
        new PreviewServerOptions { Crf = 0 }.Validate();
        new PreviewServerOptions { Crf = 51 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PreviewServerOptions { Crf = 52 }.Validate());
    }

    [Theory(DisplayName = "Preview dimensions follow the requested interlaced standards")]
    [InlineData("NTSC", 640, 480)]
    [InlineData("NTSC-J", 640, 480)]
    [InlineData("PAL_M", 640, 480)]
    [InlineData("PAL", 768, 576)]
    public void PreviewDimensionsFollowTheRequestedInterlacedStandards(
        string system,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            (expectedWidth, expectedHeight),
            DecodePreviewSegmentProvider.PreviewDimensions(system));
    }

    [Fact(DisplayName = "Preview encoder emits configurable top-field-first H.264")]
    public void PreviewEncoderEmitsConfigurableTopFieldFirstH264()
    {
        var timeline = new PreviewTimeline(2.0, 30_000.0 / 1_001.0, 2.0, 1);
        var encoder = new FfmpegHlsWindowEncoder(
            "ffmpeg",
            640,
            480,
            23,
            "NTSC",
            timeline);

        string[] arguments = encoder.BuildArguments(
            "index.m3u8",
            "init.mp4",
            "segment-%03d.m4s",
            timeline.FrameCountInWindow(0),
            timeline.FramesPerSegment,
            timeline.FramesPerSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "2.002");

        AssertOption(arguments, "-pixel_format", "yuv420p");
        AssertOption(arguments, "-crf", "23");
        AssertOption(arguments, "-vf", "setfield=tff");
        AssertOption(arguments, "-flags", "+ildct+ilme");
        AssertOption(arguments, "-top", "1");
        AssertOption(arguments, "-profile:v", "main");
        string x264Parameters = arguments[Array.IndexOf(arguments, "-x264-params") + 1];
        Assert.Contains("tff=1", x264Parameters);
        Assert.Contains("colorprim=smpte170m", x264Parameters);
        Assert.Contains("fullrange=off", x264Parameters);
        Assert.DoesNotContain("-output_ts_offset", arguments);

        AssertEncodedWindowStartsAtGlobalTimelinePosition();
    }

    [Fact(DisplayName = "Preview windows retain only the configured source-frame samples")]
    public void PreviewWindowsRetainOnlyTheConfiguredSourceFrameSamples()
    {
        const int width = 64;
        const int height = 48;
        const int outputFrameCount = 10;
        const int sampledFrameLimit = 4;
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--pal", "capture.lds", "output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        using var output = new MemoryStream();
        var assembler = new PreviewFrameAssembler(
            session,
            output,
            width,
            height,
            targetStartSample: 0,
            outputFrameCount,
            sampledFrameLimit);
        ushort neutralLuma = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(50.0));
        ushort[] luma = Enumerable.Repeat(
            neutralLuma,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        ushort[] chroma = Enumerable.Repeat(
            (ushort)32767,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        var writes = new List<(TbcDecodedField, TbcFieldOrderDecision)>();
        for (int fieldIndex = 0; fieldIndex < 12; fieldIndex++)
        {
            bool isFirstField = (fieldIndex & 1) == 0;
            writes.Add((
                new TbcDecodedField(
                    fieldIndex + 1,
                    luma,
                    null!,
                    null!,
                    0.0,
                    0.0,
                    0,
                    0,
                    FieldPhaseId: (fieldIndex % 4) + 1,
                    ChromaSamples: chroma),
                new TbcFieldOrderDecision(
                    fieldIndex + 1,
                    isFirstField,
                    isFirstField,
                    IsDuplicateField: false,
                    WriteField: true,
                    SyncConfidence: 100,
                    DecodeFaults: 0)));
        }

        assembler.Accept(writes);
        assembler.Complete();

        Assert.Equal(sampledFrameLimit, assembler.SampledFrameCount);
        Assert.Equal(outputFrameCount, assembler.WrittenFrameCount);
        Assert.Equal(outputFrameCount * width * height * 3 / 2, output.Length);
    }

    [Fact(DisplayName = "Preview colour renderer recovers PAL colour-under chroma")]
    public void PreviewColourRendererRecoversPalColourUnderChroma()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--pal", "capture.lds", "output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        var renderer = new PreviewFieldRenderer(session, 768, 576);
        ushort neutralLuma = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(50.0));
        ushort[] luma = Enumerable.Repeat(
            neutralLuma,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        ushort[] chroma = Enumerable.Repeat(
            (ushort)32767,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        AddFourFscCarrier(
            chroma,
            session.TbcFrameSpec.OutputLineLength,
            session.TbcFrameSpec.OutputLineCount,
            session.TbcFrameSpec.ColourBurstStart!.Value,
            session.TbcFrameSpec.ColourBurstEnd!.Value,
            session.TbcFrameSpec.ActiveVideoStart!.Value,
            session.TbcFrameSpec.ActiveVideoEnd!.Value,
            amplitude: 6000,
            centered: true);
        var field = new TbcDecodedField(
            0,
            luma,
            null!,
            null!,
            0.0,
            0.0,
            0,
            0,
            FieldPhaseId: 1,
            ChromaSamples: chroma);

        PreviewRenderedField rendered = renderer.Render(field, isFirstField: true);

        Assert.Contains(rendered.ChromaU, value => value < 120);
        Assert.Contains(rendered.ChromaV, value => value > 135);
    }

    [Fact(DisplayName = "Preview colour renderer recovers NTSC composite chroma")]
    public void PreviewColourRendererRecoversNtscCompositeChroma()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.LaserDisc,
            ["--ntsc", "capture.lds", "output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        var renderer = new PreviewFieldRenderer(session, 640, 480);
        ushort neutralLuma = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(50.0));
        ushort[] composite = Enumerable.Repeat(
            neutralLuma,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        AddFourFscCarrier(
            composite,
            session.TbcFrameSpec.OutputLineLength,
            session.TbcFrameSpec.OutputLineCount,
            session.TbcFrameSpec.ColourBurstStart!.Value,
            session.TbcFrameSpec.ColourBurstEnd!.Value,
            session.TbcFrameSpec.ActiveVideoStart!.Value,
            session.TbcFrameSpec.ActiveVideoEnd!.Value,
            amplitude: 6000,
            centered: false);
        var field = new TbcDecodedField(
            0,
            composite,
            null!,
            null!,
            0.0,
            0.0,
            0,
            0,
            FieldPhaseId: 1);

        PreviewRenderedField rendered = renderer.Render(field, isFirstField: true);

        Assert.Contains(rendered.ChromaU, value => value < 115);
        Assert.Contains(rendered.ChromaV, value => value is >= 124 and <= 132);
    }

    [Fact(DisplayName = "Preview dropout concealment borrows the paired field line")]
    public void PreviewDropoutConcealmentBorrowsThePairedFieldLine()
    {
        byte[] plane =
        [
            10, 10, 10, 10,
            90, 91, 92, 93,
            20, 20, 20, 20,
            80, 81, 82, 83
        ];
        var dropouts = new bool[plane.Length];
        dropouts[1] = true;

        int repaired = PreviewDropoutConcealer.Apply(plane, dropouts, 4, 4);

        Assert.Equal(1, repaired);
        Assert.Equal(91, plane[1]);
    }

    [Fact(DisplayName = "Normal LD parsing still requires input and output")]
    public void NormalLaserDiscParsingStillRequiresInputAndOutput()
    {
        CommandLineParseException error = Assert.Throws<CommandLineParseException>(() =>
            new CommandLineParser().Parse(CliSpecs.LaserDisc, ["capture.lds"]));

        Assert.Contains("infile, outfile", error.Message);
    }

    [Fact(DisplayName = "Preview sessions suppress every decode log write")]
    public void PreviewSessionsSuppressEveryDecodeLogWrite()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-preview-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.u8");
            string outputBase = Path.Combine(directory, "must-not-exist");
            File.WriteAllBytes(input, [128]);
            ParsedCommand parsed = new CommandLineParser().Parse(
                CliSpecs.Vhs,
                ["--preview-server", input]);
            var withSentinelOutput = new ParsedCommand(
                parsed.Spec,
                new Dictionary<string, object?>(parsed.Values),
                [input, outputBase],
                parsed.ProgramName,
                parsed.OptionSources);

            using DecodeSession session = DecodeSessionFactory.Create(withSentinelOutput);
            Assert.True(session.ExecutionOptions.SuppressFileOutputs);
            Assert.Equal(string.Empty, DecodeSessionLogWriter.Write(session));
            DecodeSessionLogWriter.Append(session, "INFO", "not written");
            DecodeSessionLogWriter.Status(session, "not written");

            Assert.False(File.Exists(outputBase + ".log"));
            Assert.False(File.Exists(outputBase + ".tbc"));
            Assert.False(File.Exists(outputBase + ".tbc.json"));
            Assert.False(File.Exists(outputBase + ".tbc.db"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "HLS VOD manifest exposes a complete seekable timeline")]
    public void HlsVodManifestExposesCompleteSeekableTimeline()
    {
        var timeline = new PreviewTimeline(
            sourceDurationSeconds: 13.0,
            framesPerSecond: 30_000.0 / 1_001.0,
            requestedSegmentSeconds: 2.0,
            segmentsPerWindow: 2);

        string playlist = HlsPlaylistBuilder.BuildMedia(timeline);

        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", playlist);
        Assert.Contains("#EXT-X-ENDLIST", playlist);
        Assert.Equal(timeline.WindowCount, Count(playlist, "#EXT-X-MAP:"));
        Assert.Equal(timeline.SegmentCount, Count(playlist, "#EXTINF:"));
        Assert.Equal(timeline.WindowCount - 1, Count(playlist, "#EXT-X-DISCONTINUITY"));
        Assert.Contains(
            $"window/{timeline.WindowCount - 1}/segment/",
            playlist);
    }

    [Fact(DisplayName = "HTTP preview serves a far seek window on demand and caches it")]
    public async Task HttpPreviewServesFarSeekWindowOnDemandAndCachesIt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var timeline = new PreviewTimeline(22.0, 25.0, 2.0, 2);
        var provider = new FakeProvider(timeline);
        var options = new PreviewServerOptions
        {
            Port = 0,
            CacheWindowCount = 2
        };
        await using PreviewHttpServer server = await PreviewHttpServer.StartAsync(
            provider,
            options,
            cancellationToken);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        string manifest = await client.GetStringAsync("hls/index.m3u8", cancellationToken);
        string master = await client.GetStringAsync("hls/master.m3u8", cancellationToken);
        int targetWindow = timeline.WindowCount - 1;
        Assert.Contains($"window/{targetWindow}/segment/0.m4s", manifest);
        Assert.Contains("CODECS=\"avc1.4d401f\"", master);
        Assert.Contains("RESOLUTION=640x480", master);
        Assert.Empty(provider.GeneratedWindows);

        byte[] segment = await client.GetByteArrayAsync(
            $"hls/window/{targetWindow}/segment/0.m4s",
            cancellationToken);
        byte[] init = await client.GetByteArrayAsync(
            $"hls/window/{targetWindow}/init.mp4",
            cancellationToken);

        Assert.Equal([(byte)targetWindow, 0x5A], segment);
        Assert.Equal([(byte)targetWindow, 0x49], init);
        Assert.Equal([targetWindow], provider.GeneratedWindows);
        Assert.Equal(
            "ok",
            (await client.GetStringAsync("health", cancellationToken)).Contains("ok")
                ? "ok"
                : string.Empty);

        string player = await client.GetStringAsync(string.Empty, cancellationToken);
        Assert.Contains("aria-label=\"Preview position\"", player);
        Assert.Contains("MediaSource", player);
        Assert.Contains("avc1.4d401f", player);
        Assert.Contains("new AbortController()", player);
        Assert.Contains("sourceBuffer.buffered", player);
        Assert.DoesNotContain("timestampOffset", player);
        Assert.DoesNotContain("requestedWindows", player);
        Assert.DoesNotContain("cdn.jsdelivr.net", player);
    }

    [Fact(DisplayName = "Concurrent HLS requests share one window generation")]
    public async Task ConcurrentHlsRequestsShareOneWindowGeneration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var timeline = new PreviewTimeline(8.0, 25.0, 2.0, 2);
        var provider = new FakeProvider(timeline, delay: TimeSpan.FromMilliseconds(50));
        var options = new PreviewServerOptions { Port = 0 };
        await using PreviewHttpServer server = await PreviewHttpServer.StartAsync(
            provider,
            options,
            cancellationToken);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        Task<byte[]> init = client.GetByteArrayAsync(
            "hls/window/0/init.mp4",
            cancellationToken);
        Task<byte[]> first = client.GetByteArrayAsync(
            "hls/window/0/segment/0.m4s",
            cancellationToken);
        Task<byte[]> second = client.GetByteArrayAsync(
            "hls/window/0/segment/1.m4s",
            cancellationToken);
        await Task.WhenAll(init, first, second);

        Assert.Equal([0], provider.GeneratedWindows);
    }

    [Fact(DisplayName = "Abandoned preview waiters cancel their shared window generation")]
    public async Task AbandonedPreviewWaitersCancelTheirSharedWindowGeneration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new BlockingProvider(
            new PreviewTimeline(8.0, 25.0, 2.0, 1),
            completeSubsequentGenerations: true);
        await using var cache = new PreviewSegmentCache(provider, capacity: 2);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        Task<PreviewSegmentWindow> first = cache.GetWindowAsync(
            1,
            firstCancellation.Token);
        Task<PreviewSegmentWindow> second = cache.GetWindowAsync(
            1,
            secondCancellation.Token);
        await provider.GenerationStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(provider.GenerationCancelled.Task.IsCompleted);

        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        int cancelledWindow = await provider.GenerationCancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);

        Assert.Equal(1, cancelledWindow);
        PreviewSegmentWindow retry = await cache.GetWindowAsync(1, cancellationToken);
        Assert.Equal(1, retry.WindowIndex);
        Assert.Equal(2, provider.GenerationCount);
    }

    [Fact(DisplayName = "Preview server shutdown cancels an active window build")]
    public async Task PreviewServerShutdownCancelsAnActiveWindowBuild()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var provider = new BlockingProvider(new PreviewTimeline(8.0, 25.0, 2.0, 1));
        PreviewHttpServer server = await PreviewHttpServer.StartAsync(
            provider,
            new PreviewServerOptions { Port = 0 },
            cancellationToken);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        Task<HttpResponseMessage> request = client.GetAsync(
            "hls/window/1/segment/0.m4s",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        try
        {
            await provider.GenerationStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken);
            Task shutdown = server.DisposeAsync().AsTask();
            Assert.Equal(
                1,
                await provider.GenerationCancelled.Task.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    cancellationToken));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            try
            {
                using HttpResponseMessage response = await request;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Either a closed connection or a cancelled response is valid during shutdown.
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static void AssertOption(string[] arguments, string option, string expectedValue)
    {
        int index = Array.IndexOf(arguments, option);
        Assert.True(index >= 0 && index + 1 < arguments.Length, $"Missing encoder option {option}.");
        Assert.Equal(expectedValue, arguments[index + 1]);
    }

    private static void AssertEncodedWindowStartsAtGlobalTimelinePosition()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        const int width = 64;
        const int height = 48;
        const int windowIndex = 3;
        var timeline = new PreviewTimeline(8.0, 25.0, 2.0, 1);
        var encoder = new FfmpegHlsWindowEncoder(
            "ffmpeg",
            width,
            height,
            31,
            "PAL",
            timeline);
        byte[] frame = new byte[width * height * 3 / 2];
        Array.Fill(frame, (byte)16, 0, width * height);
        Array.Fill(frame, (byte)128, width * height, frame.Length - (width * height));

        PreviewSegmentWindow encoded = EncodeSyntheticWindow(encoder, timeline, windowIndex, frame);
        PreviewSegmentWindow origin = EncodeSyntheticWindow(encoder, timeline, 0, frame);
        Assert.Equal(origin.InitializationSegment, encoded.InitializationSegment);

        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-preview-timestamp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mediaPath = Path.Combine(directory, "window.mp4");
            using (FileStream media = File.Create(mediaPath))
            {
                media.Write(encoded.InitializationSegment);
                foreach (PreviewMediaSegment segment in encoded.Segments)
                {
                    media.Write(segment.Data);
                }
            }

            double firstPacketTimestamp = ProbeFirstVideoPacketTimestamp(mediaPath);
            double expected = timeline.WindowStartSeconds(windowIndex);
            Assert.InRange(firstPacketTimestamp, expected, expected + 0.05);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PreviewSegmentWindow EncodeSyntheticWindow(
        FfmpegHlsWindowEncoder encoder,
        PreviewTimeline timeline,
        int windowIndex,
        byte[] frame)
        => encoder.Encode(
            windowIndex,
            stream =>
            {
                for (int frameIndex = 0;
                     frameIndex < timeline.FrameCountInWindow(windowIndex);
                     frameIndex++)
                {
                    stream.Write(frame);
                }
            },
            TestContext.Current.CancellationToken);

    private static double ProbeFirstVideoPacketTimestamp(string mediaPath)
    {
        var startInfo = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "packet=pts_time",
            "-of", "default=noprint_wrappers=1:nokey=1",
            mediaPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffprobe.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode == 0,
            $"ffprobe exited with {process.ExitCode}: {error.Result}");
        string firstTimestamp = output.Result.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries)[0];
        return double.Parse(
            firstTimestamp,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool CommandIsAvailable(string command)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-version");
        try
        {
            using Process process = Process.Start(startInfo)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void AddFourFscCarrier(
        ushort[] samples,
        int lineLength,
        int lineCount,
        int burstStart,
        int burstEnd,
        int activeStart,
        int activeEnd,
        int amplitude,
        bool centered)
    {
        for (int line = 0; line < lineCount; line++)
        {
            AddRange(burstStart, burstEnd);
            AddRange(activeStart, activeEnd);

            void AddRange(int start, int end)
            {
                for (int x = start; x < end; x++)
                {
                    int carrier = (x & 3) switch
                    {
                        0 => amplitude,
                        2 => -amplitude,
                        _ => 0
                    };
                    int index = (line * lineLength) + x;
                    int value = centered ? 32767 + carrier : samples[index] + carrier;
                    samples[index] = (ushort)Math.Clamp(value, 0, ushort.MaxValue);
                }
            }
        }
    }

    private sealed class FakeProvider : IPreviewSegmentProvider
    {
        private readonly TimeSpan _delay;
        private readonly object _gate = new();
        private readonly List<int> _generatedWindows = [];

        internal FakeProvider(PreviewTimeline timeline, TimeSpan delay = default)
        {
            Timeline = timeline;
            _delay = delay;
            MediaInfo = new PreviewMediaInfo(
                "VHS",
                "NTSC",
                timeline.FramesPerSecond,
                timeline.DurationSeconds,
                640,
                480,
                31,
                true,
                "test",
                "test");
        }

        public PreviewMediaInfo MediaInfo { get; }

        public PreviewTimeline Timeline { get; }

        internal int[] GeneratedWindows
        {
            get
            {
                lock (_gate)
                {
                    return [.. _generatedWindows];
                }
            }
        }

        public async Task<PreviewSegmentWindow> GenerateWindowAsync(
            int windowIndex,
            CancellationToken cancellationToken)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            lock (_gate)
            {
                _generatedWindows.Add(windowIndex);
            }

            int firstGlobal = Timeline.FirstSegmentInWindow(windowIndex);
            PreviewMediaSegment[] segments = Enumerable.Range(
                    0,
                    Timeline.SegmentCountInWindow(windowIndex))
                .Select(local => new PreviewMediaSegment(
                    firstGlobal + local,
                    local,
                    Timeline.SegmentDurationSeconds(firstGlobal + local),
                    [(byte)windowIndex, (byte)(0x5A + local)]))
                .ToArray();
            return new PreviewSegmentWindow(
                windowIndex,
                [(byte)windowIndex, 0x49],
                segments);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingProvider : IPreviewSegmentProvider
    {
        private readonly bool _completeSubsequentGenerations;
        private int _generationCount;

        internal BlockingProvider(
            PreviewTimeline timeline,
            bool completeSubsequentGenerations = false)
        {
            Timeline = timeline;
            _completeSubsequentGenerations = completeSubsequentGenerations;
            MediaInfo = new PreviewMediaInfo(
                "VHS",
                "PAL",
                timeline.FramesPerSecond,
                timeline.DurationSeconds,
                768,
                576,
                31,
                true,
                "test",
                "test");
        }

        internal TaskCompletionSource<int> GenerationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<int> GenerationCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int GenerationCount => Volatile.Read(ref _generationCount);

        public PreviewMediaInfo MediaInfo { get; }

        public PreviewTimeline Timeline { get; }

        public async Task<PreviewSegmentWindow> GenerateWindowAsync(
            int windowIndex,
            CancellationToken cancellationToken)
        {
            int generation = Interlocked.Increment(ref _generationCount);
            if (generation > 1 && _completeSubsequentGenerations)
            {
                return new PreviewSegmentWindow(
                    windowIndex,
                    [(byte)windowIndex, 0x49],
                    [new PreviewMediaSegment(
                        Timeline.FirstSegmentInWindow(windowIndex),
                        0,
                        Timeline.WindowDurationSeconds(windowIndex),
                        [(byte)windowIndex, 0x5A])]);
            }

            GenerationStarted.TrySetResult(windowIndex);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking provider unexpectedly resumed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                GenerationCancelled.TrySetResult(windowIndex);
                throw;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
