using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.CudaFast;
using VHSDecode.Core.Rf;
using VHSDecode.Preview;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PreviewServerTests
{
    [Fact(DisplayName = "Preview and CUDA recover wrapped FLAC STREAMINFO sample totals")]
    public async Task PreviewAndCudaRecoverWrappedFlacSampleTotals()
    {
        const long HeaderSamples = 3_783_262_208;
        const long ExpectedSamples = HeaderSamples + (5L * 4_294_967_296L);
        const ulong LastFrameNumber = (ulong)(ExpectedSamples / 4_096L) - 1UL;
        string path = Path.Combine(
            Path.GetTempPath(),
            $"vhsdecode-preview-wrapped-streaminfo-{Guid.NewGuid():N}.ldf");
        try
        {
            byte[] firstFrame = BuildFixedFlacFrameHeader(0);
            byte[][] tailFrames =
            [
                BuildFixedFlacFrameHeader(LastFrameNumber - 2),
                BuildFixedFlacFrameHeader(LastFrameNumber - 1),
                BuildFixedFlacFrameHeader(LastFrameNumber),
                BuildFixedFlacFrameHeader(LastFrameNumber + 1_000_000)
            ];
            int tailBytes = tailFrames.Sum(static frame => frame.Length);
            var bytes = new byte[42 + firstFrame.Length + 32 + tailBytes];
            "fLaC"u8.CopyTo(bytes);
            bytes[4] = 0x80;
            bytes[7] = 34;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), 4_096);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10, 2), 4_096);
            ulong packed = (40_000UL << 44)
                | (15UL << 36)
                | (ulong)HeaderSamples;
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), packed);
            firstFrame.CopyTo(bytes, 42);
            int tailOffset = 42 + firstFrame.Length + 32;
            foreach (byte[] frame in tailFrames)
            {
                frame.CopyTo(bytes, tailOffset);
                tailOffset += frame.Length;
            }
            File.WriteAllBytes(path, bytes);

            double previewDuration = await PreviewSourceProbe.GetDurationSecondsAsync(
                path,
                40_000_000.0,
                "ffprobe-must-not-run",
                CancellationToken.None);
            Assert.True(CudaFastDecodeRunner.TryGetInputSampleCount(
                path,
                out long cudaSamples));
            Assert.Equal(
                ExpectedSamples,
                checked((long)Math.Round(previewDuration * 40_000_000.0)));
            Assert.Equal(ExpectedSamples, cudaSamples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildFixedFlacFrameHeader(ulong frameNumber)
    {
        byte[] codedNumber = EncodeFlacUtf8Integer(frameNumber);
        var header = new byte[4 + codedNumber.Length + 1];
        header[0] = 0xff;
        header[1] = 0xf8;
        header[2] = 0xc0;
        header[3] = 0x08;
        codedNumber.CopyTo(header, 4);
        header[^1] = CalculateFlacHeaderCrc8(header.AsSpan(0, header.Length - 1));
        return header;
    }

    private static byte[] EncodeFlacUtf8Integer(ulong value)
    {
        int length = value switch
        {
            <= 0x7fUL => 1,
            <= 0x7ffUL => 2,
            <= 0xffffUL => 3,
            <= 0x1fffffUL => 4,
            <= 0x3ffffffUL => 5,
            <= 0x7fffffffUL => 6,
            _ => 7
        };
        if (length == 1)
        {
            return [(byte)value];
        }

        var result = new byte[length];
        ulong remaining = value;
        for (int index = length - 1; index > 0; index--)
        {
            result[index] = (byte)(0x80 | (remaining & 0x3f));
            remaining >>= 6;
        }
        int payloadBits = 7 - length;
        byte prefix = (byte)(0xff << (8 - length));
        result[0] = (byte)(prefix
            | (payloadBits == 0 ? 0UL : remaining & ((1UL << payloadBits) - 1UL)));
        return result;
    }

    private static byte CalculateFlacHeaderCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0
                    ? (byte)((crc << 1) ^ 0x07)
                    : (byte)(crc << 1);
            }
        }
        return crc;
    }

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
        Assert.Equal(PreviewServerOptions.DefaultPort, parsed.Get<int>("preview_port"));
        Assert.Equal("capture.lds", parsed.InputFile);
        Assert.Empty(parsed.OutputBase);

        ParsedCommand defaultCrf = new CommandLineParser().Parse(
            spec,
            ["--preview-server", "capture.lds"]);
        Assert.Equal(31, defaultCrf.Get<int>("preview_crf"));

        ParsedCommand dynamicPort = new CommandLineParser().Parse(
            spec,
            ["--preview-server", "--preview-port", "0", "capture.lds"]);
        Assert.Equal(0, dynamicPort.Get<int>("preview_port"));
        Assert.NotEqual(
            ParsedOptionSource.Default,
            dynamicPort.GetSource("preview_port"));

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

    [Fact(DisplayName = "Preview template routes CUDA-fast only for native-rate VHS")]
    public void PreviewTemplateRoutesCudaFastOnlyForVhs()
    {
        ParsedCommand vhs = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--dsp-backend", "cuda-fast", "capture.ldf"]);
        ParsedCommand template = PreviewDecodeCommandFactory.CreateFastTemplate(vhs);

        Assert.Equal("cuda-fast", template.Get<string>("dsp_backend"));
        Assert.True(template.Get<bool>(PreviewDecodeCommandFactory.DecodeAt20MspsOption));

        ParsedCommand laserDisc = new CommandLineParser().Parse(
            CliSpecs.LaserDisc,
            ["--preview-server", "--dsp-backend", "cuda-fast", "capture.ldf"]);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => PreviewDecodeCommandFactory.CreateFastTemplate(laserDisc));

        Assert.Contains("VHS command only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no CPU preview fallback", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Default VHS preview preflights CUDA before full initialization")]
    public async Task DefaultPreviewPreflightsCudaBeforeInitialization()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--pal", "capture.ldf"]);
        var attempted = new List<DspBackend>();
        int preflightCalls = 0;
        int ippProbeCalls = 0;

        DspBackend selected = await PreviewBackendSelector.SelectAsync(
            command,
            (backend, _) =>
            {
                attempted.Add(backend);
                return Task.FromResult(backend);
            },
            TextWriter.Null,
            CancellationToken.None,
            () =>
            {
                preflightCalls++;
                return new(true, "test CUDA device 8.9");
            },
            () =>
            {
                ippProbeCalls++;
                return true;
            });

        Assert.Equal(DspBackend.CudaFast, selected);
        Assert.Equal([DspBackend.CudaFast], attempted);
        Assert.Equal(1, preflightCalls);
        Assert.Equal(0, ippProbeCalls);

        ParsedCommand compatibilityPinned = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--compat-version", "current", "capture.ldf"]);
        Assert.False(PreviewBackendSelector.IsAutomaticCudaCandidate(compatibilityPinned));
    }

    [Fact(DisplayName = "Default VHS preview falls back to IPP after lightweight CUDA rejection")]
    public async Task DefaultPreviewFallsBackToIppAfterPreflightRejection()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--ntsc", "capture.ldf"]);
        var attempted = new List<DspBackend>();
        using var output = new StringWriter();

        DspBackend selected = await PreviewBackendSelector.SelectAsync(
            command,
            (backend, _) =>
            {
                attempted.Add(backend);
                return Task.FromResult(backend);
            },
            output,
            CancellationToken.None,
            () => new(false, "no compatible CUDA device"),
            () => true);

        Assert.Equal(DspBackend.IppFast, selected);
        Assert.Equal([DspBackend.IppFast], attempted);
        Assert.Contains("preflight unavailable", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("falling back to IPP-fast", output.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Default VHS preview falls back after CUDA full initialization failure")]
    public async Task DefaultPreviewFallsBackAfterCudaInitializationFailure()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--pal", "capture.ldf"]);
        var attempted = new List<DspBackend>();
        using var output = new StringWriter();

        DspBackend selected = await PreviewBackendSelector.SelectAsync(
            command,
            (backend, _) =>
            {
                attempted.Add(backend);
                return backend == DspBackend.CudaFast
                    ? Task.FromException<DspBackend>(
                        new AutomaticCudaPreviewUnavailableException("NVENC unavailable"))
                    : Task.FromResult(backend);
            },
            output,
            CancellationToken.None,
            () => new(true, "test CUDA device 8.9"),
            () => true);

        Assert.Equal(DspBackend.IppFast, selected);
        Assert.Equal([DspBackend.CudaFast, DspBackend.IppFast], attempted);
        Assert.Contains("initialization was unavailable", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("NVENC unavailable", output.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Default VHS preview falls back to Exact when IPP is unavailable")]
    public async Task DefaultPreviewFallsBackToExactWhenIppIsUnavailable()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--pal", "capture.ldf"]);

        DspBackend selected = await PreviewBackendSelector.SelectAsync(
            command,
            (backend, _) => Task.FromResult(backend),
            TextWriter.Null,
            CancellationToken.None,
            () => new(false, "no CUDA device"),
            () => false);

        Assert.Equal(DspBackend.Exact, selected);
    }

    [Fact(DisplayName = "Explicit CUDA preview remains fail-closed without fallback")]
    public async Task ExplicitCudaPreviewRemainsFailClosed()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--dsp-backend", "cuda-fast", "capture.ldf"]);
        int preflightCalls = 0;
        int ippProbeCalls = 0;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PreviewBackendSelector.SelectAsync(
                command,
                (backend, _) => Task.FromException<DspBackend>(
                    new InvalidOperationException($"{backend} failed")),
                TextWriter.Null,
                CancellationToken.None,
                () =>
                {
                    preflightCalls++;
                    return new(true, "unused");
                },
                () =>
                {
                    ippProbeCalls++;
                    return true;
                }));

        Assert.Contains("CudaFast failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, preflightCalls);
        Assert.Equal(0, ippProbeCalls);
    }

    [Theory(DisplayName = "Automatic CUDA preview ignores unsupported command surfaces")]
    [InlineData("ld", "VHS", "40")]
    [InlineData("vhs", "SVHS", "40")]
    [InlineData("vhs", "VHS", "20")]
    public async Task AutomaticCudaPreviewIgnoresUnsupportedSurfaces(
        string commandName,
        string tapeFormat,
        string inputRate)
    {
        DecodeCommandSpec spec = commandName == "vhs" ? CliSpecs.Vhs : CliSpecs.LaserDisc;
        var arguments = new List<string> { "--preview-server" };
        if (commandName == "vhs")
        {
            arguments.AddRange(["--tape_format", tapeFormat, "--frequency", inputRate]);
        }
        arguments.Add("capture.ldf");
        ParsedCommand command = new CommandLineParser().Parse(spec, arguments);
        int preflightCalls = 0;

        DspBackend selected = await PreviewBackendSelector.SelectAsync(
            command,
            (backend, _) => Task.FromResult(backend),
            TextWriter.Null,
            CancellationToken.None,
            () =>
            {
                preflightCalls++;
                return new(true, "unused");
            },
            () => true);

        Assert.Equal(DspBackend.IppFast, selected);
        Assert.Equal(0, preflightCalls);
    }

    [Fact(DisplayName = "Preview template forces 20 MSPS decode for supported VHS RF")]
    public void PreviewTemplateForcesTwentyMspsForSupportedVhsRf()
    {
        ParsedCommand defaultVhs = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "capture.lds"]);
        ParsedCommand defaultTemplate = PreviewDecodeCommandFactory.CreateFastTemplate(defaultVhs);
        Assert.True(defaultTemplate.Get<bool>(PreviewDecodeCommandFactory.DecodeAt20MspsOption));

        ParsedCommand nativeTwenty = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--frequency", "20", "capture.s16"]);
        ParsedCommand nativeTwentyTemplate = PreviewDecodeCommandFactory.CreateFastTemplate(nativeTwenty);
        Assert.True(nativeTwentyTemplate.Get<bool>(
            PreviewDecodeCommandFactory.DecodeAt20MspsOption));
        Assert.True(nativeTwentyTemplate.Get<bool>("no_resample"));
        using (DecodeSession nativeTwentySession = DecodeSessionFactory.Create(nativeTwentyTemplate))
        {
            Assert.Equal(20_000_000.0, nativeTwentySession.DecodeSampleRateHz);
            Assert.IsType<Int16SampleLoader>(nativeTwentySession.Loader);
        }

        ParsedCommand superVhs = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--tape_format", "SVHS", "capture.lds"]);
        ParsedCommand superVhsTemplate = PreviewDecodeCommandFactory.CreateFastTemplate(superVhs);
        Assert.False(superVhsTemplate.Get<bool>(
            PreviewDecodeCommandFactory.DecodeAt20MspsOption));

        ParsedCommand laserDisc = new CommandLineParser().Parse(
            CliSpecs.LaserDisc,
            ["--preview-server", "--pal", "capture.ldf"]);
        ParsedCommand laserDiscTemplate = PreviewDecodeCommandFactory.CreateFastTemplate(laserDisc);
        Assert.False(laserDiscTemplate.Values.ContainsKey(
            PreviewDecodeCommandFactory.DecodeAt20MspsOption));
    }

    [Fact(DisplayName = "Half-rate RF loader filters aliases and maps source positions")]
    public void HalfRateRfLoaderFiltersAliasesAndMapsSourcePositions()
    {
        using var stream = new MemoryStream([0]);
        var constantSource = new GeneratedSampleLoader((_) => 1_000.0);
        using var constantLoader = new HalfRateSampleLoader(constantSource);

        double[] constant = Assert.IsType<double[]>(constantLoader.Read(stream, 100, 16));

        Assert.Equal(185, constantSource.LastSample);
        Assert.Equal(61, constantSource.LastReadLength);
        Assert.All(constant, value => Assert.InRange(value, 999.99, 1_000.01));

        var nyquistSource = new GeneratedSampleLoader(
            sample => (sample & 1L) == 0L ? 1_000.0 : -1_000.0);
        using var nyquistLoader = new HalfRateSampleLoader(nyquistSource);

        double[] suppressed = Assert.IsType<double[]>(nyquistLoader.Read(stream, 100, 16));

        Assert.All(suppressed, value => Assert.InRange(value, -0.02, 0.02));

        const double sourceSampleRateHz = 40_000_000.0;
        var vhsPassbandSource = new GeneratedSampleLoader(sample =>
            Math.Sin(2.0 * Math.PI * 5_780_000.0 * sample / sourceSampleRateHz));
        using var vhsPassbandLoader = new HalfRateSampleLoader(vhsPassbandSource);
        double[] vhsPassband = Assert.IsType<double[]>(
            vhsPassbandLoader.Read(stream, 200, 512));
        double vhsPassbandAmplitude = Math.Sqrt(
            2.0 * vhsPassband.Average(value => value * value));
        Assert.InRange(vhsPassbandAmplitude, 0.995, 1.005);

        var aliasBandSource = new GeneratedSampleLoader(sample =>
            Math.Sin(2.0 * Math.PI * 15_000_000.0 * sample / sourceSampleRateHz));
        using var aliasBandLoader = new HalfRateSampleLoader(aliasBandSource);
        double[] aliasBand = Assert.IsType<double[]>(
            aliasBandLoader.Read(stream, 200, 512));
        double aliasBandAmplitude = Math.Sqrt(
            2.0 * aliasBand.Average(value => value * value));
        Assert.InRange(aliasBandAmplitude, 0.0, 0.001);

        static double ComparisonWave(long sample)
            => Math.Sin(sample * 0.017)
                + (0.25 * Math.Cos(sample * 0.071));
        using var scalarComparisonLoader = new HalfRateSampleLoader(
            new GeneratedSampleLoader(ComparisonWave));
        using var vectorComparisonLoader = new HalfRateSampleLoader(
            new GeneratedSampleLoader(ComparisonWave));
        double[] scalarComparison = Assert.IsType<double[]>(
            scalarComparisonLoader.Read(stream, 100, 63));
        double[] vectorComparison = Assert.IsType<double[]>(
            vectorComparisonLoader.Read(stream, 100, 128));
        for (int index = 0; index < scalarComparison.Length; index++)
        {
            Assert.InRange(
                Math.Abs(scalarComparison[index] - vectorComparison[index]),
                0.0,
                1e-12);
        }

        using var pooledLoader = new HalfRateSampleLoader(
            new GeneratedSampleLoader(sample => sample));
        IReusableRfSampleLoader reusableLoader = pooledLoader;
        double[] firstBuffer = Assert.IsType<double[]>(
            reusableLoader.ReadReusable(stream, 300, 32));
        reusableLoader.ReturnReusable(firstBuffer);
        double[] secondBuffer = Assert.IsType<double[]>(
            reusableLoader.ReadReusable(stream, 400, 32));
        Assert.Same(firstBuffer, secondBuffer);
        reusableLoader.ReturnReusable(secondBuffer);

        var finiteSamples = new short[100];
        for (short index = 0; index < finiteSamples.Length; index++)
        {
            finiteSamples[index] = index;
        }
        using var finiteStream = new MemoryStream(
            MemoryMarshal.AsBytes(finiteSamples.AsSpan()).ToArray());
        using var finiteLoader = new HalfRateSampleLoader(new Int16SampleLoader());
        using var zeroPaddedReference = new HalfRateSampleLoader(
            new GeneratedSampleLoader(sample => sample < finiteSamples.Length ? sample : 0.0));

        double[] expectedFinalBlock = Assert.IsType<double[]>(
            zeroPaddedReference.Read(stream, 0, 50));
        double[] actualFinalBlock = Assert.IsType<double[]>(
            finiteLoader.Read(finiteStream, 0, 50));

        Assert.Equal(expectedFinalBlock, actualFinalBlock);
        Assert.NotEqual(0.0, actualFinalBlock[^1]);
        Assert.Null(finiteLoader.Read(finiteStream, 1, 50));

        int[] packedSamples = Enumerable.Range(0, 100)
            .Select(index => (index * 73) & 0x3FF)
            .ToArray();
        using var packedStream = new MemoryStream(Pack4x10(packedSamples));
        using var packedLoader = new HalfRateSampleLoader(
            new PackedDdD4To40SampleLoader());
        using var packedReference = new HalfRateSampleLoader(
            new GeneratedSampleLoader(sample => sample < packedSamples.Length
                ? (short)((packedSamples[sample] - 512) << 6)
                : 0.0));

        double[] expectedPackedFinalBlock = Assert.IsType<double[]>(
            packedReference.Read(stream, 0, 50));
        double[] actualPackedFinalBlock = Assert.IsType<double[]>(
            packedLoader.Read(packedStream, 0, 50));

        Assert.Equal(expectedPackedFinalBlock, actualPackedFinalBlock);
        Assert.NotEqual(0.0, actualPackedFinalBlock[^1]);
        Assert.Null(packedLoader.Read(packedStream, 1, 50));
    }

    [Fact(DisplayName = "Preview 20 MSPS routing cannot alter normal VHS sessions")]
    public void PreviewTwentyMspsRoutingCannotAlterNormalVhsSessions()
    {
        ParsedCommand preview = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--pal", "capture.lds"]);
        ParsedCommand previewTemplate = PreviewDecodeCommandFactory.CreateFastTemplate(preview);
        using DecodeSession previewSession = DecodeSessionFactory.Create(previewTemplate);

        Assert.Equal(20_000_000.0, previewSession.DecodeSampleRateHz);
        HalfRateSampleLoader previewLoader = Assert.IsType<HalfRateSampleLoader>(
            previewSession.Loader);
        Assert.IsType<PackedDdD4To40SampleLoader>(previewLoader.Source);

        ParsedCommand rawPreview = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--pal", "capture.raw"]);
        using DecodeSession rawPreviewSession = DecodeSessionFactory.Create(
            PreviewDecodeCommandFactory.CreateFastTemplate(rawPreview));
        HalfRateSampleLoader rawPreviewLoader = Assert.IsType<HalfRateSampleLoader>(
            rawPreviewSession.Loader);
        Assert.IsType<Int16SampleLoader>(rawPreviewLoader.Source);

        ParsedCommand signedBytePreview = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--pal", "capture.s8"]);
        using DecodeSession signedBytePreviewSession = DecodeSessionFactory.Create(
            PreviewDecodeCommandFactory.CreateFastTemplate(signedBytePreview));
        HalfRateSampleLoader signedBytePreviewLoader = Assert.IsType<HalfRateSampleLoader>(
            signedBytePreviewSession.Loader);
        Assert.IsType<Int8SampleLoader>(signedBytePreviewLoader.Source);

        ParsedCommand normal = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--pal", "capture.lds", "normal-output"]);
        using DecodeSession normalSession = DecodeSessionFactory.Create(normal);

        Assert.Equal(40_000_000.0, normalSession.DecodeSampleRateHz);
        Assert.IsType<PackedDdD4To40SampleLoader>(normalSession.Loader);

        ParsedCommand sourcePositionedPreview = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--pal", "--start_fileloc", "40000000", "capture.lds"]);
        Assert.Equal(
            1.0,
            DecodePreviewSegmentProvider.ResolveBaseStartSeconds(
                sourcePositionedPreview,
                framesPerSecond: 25.0,
                sourceSampleRateHz: 40_000_000.0));

        ParsedCommand fortyMspsWindow = PreviewDecodeCommandFactory.ForWindow(
            previewTemplate,
            startSeconds: 1.88,
            sourceSampleRateHz: 40_000_000.0,
            requestedFrames: 58);
        Assert.Equal(75_200_000.0, fortyMspsWindow.Get<double>("start_fileloc"));
        using (DecodeSession fortyMspsWindowSession = DecodeSessionFactory.Create(
            fortyMspsWindow))
        {
            Assert.Equal(37_600_000L, fortyMspsWindowSession.RunBounds.StartSample);
            Assert.Equal(75_200_000L, fortyMspsWindowSession.ToSourceSampleLocation(
                fortyMspsWindowSession.RunBounds.StartSample));
        }

        ParsedCommand nativeTwentyPreview = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--preview-server", "--frequency", "20", "capture.s16"]);
        ParsedCommand nativeTwentyWindowTemplate =
            PreviewDecodeCommandFactory.CreateFastTemplate(nativeTwentyPreview);
        ParsedCommand nativeTwentyWindow = PreviewDecodeCommandFactory.ForWindow(
            nativeTwentyWindowTemplate,
            startSeconds: 1.88,
            sourceSampleRateHz: 20_000_000.0,
            requestedFrames: 58);
        Assert.Equal(37_600_000.0, nativeTwentyWindow.Get<double>("start_fileloc"));
        using DecodeSession nativeTwentyWindowSession = DecodeSessionFactory.Create(
            nativeTwentyWindow);
        Assert.Equal(37_600_000L, nativeTwentyWindowSession.RunBounds.StartSample);
        Assert.Equal(37_600_000L, nativeTwentyWindowSession.ToSourceSampleLocation(
            nativeTwentyWindowSession.RunBounds.StartSample));
    }

    [Fact(DisplayName = "IPP complete decode can opt into 20 MSPS with source-coordinate metadata")]
    public void IppCompleteDecodeCanOptIntoTwentyMsps()
    {
        Assert.SkipUnless(
            IppRuntime.TryProbe(out _),
            "The optional IPP runtime was not staged for this test build.");

        ParsedCommand fortyMsps = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            [
                "--dsp-backend", "ipp-fast",
                "--decode-at-20msps",
                "--pal",
                "--start_fileloc", "40000001",
                "capture.s16",
                "output"
            ]);
        using (DecodeSession session = DecodeSessionFactory.Create(fortyMsps))
        {
            Assert.Equal(20_000_000.0, session.DecodeSampleRateHz);
            Assert.Equal(2, session.SourceSamplesPerDecodeSample);
            Assert.Equal(20_000_000L, session.RunBounds.StartSample);
            Assert.Equal(40_000_000L, session.ToSourceSampleLocation(
                session.RunBounds.StartSample));
            HalfRateSampleLoader loader = Assert.IsType<HalfRateSampleLoader>(session.Loader);
            Assert.IsType<Int16SampleLoader>(loader.Source);
        }

        ParsedCommand nativeTwenty = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            [
                "--dsp-backend", "ipp-fast",
                "--decode_at_20msps",
                "--frequency", "20",
                "capture.RAW",
                "output"
            ]);
        using DecodeSession nativeSession = DecodeSessionFactory.Create(nativeTwenty);
        Assert.Equal(20_000_000.0, nativeSession.DecodeSampleRateHz);
        Assert.Equal(1, nativeSession.SourceSamplesPerDecodeSample);
        Assert.IsType<Int16SampleLoader>(nativeSession.Loader);
    }

    [Theory(DisplayName = "20 MSPS complete decode rejects unsupported CPU routes")]
    [InlineData("exact", "VHS", "40", "Exact complete decode")]
    [InlineData("ipp-fast", "SVHS", "40", "VHS tape format only")]
    [InlineData("ipp-fast", "VHS", "28.6", "40 or native 20 MSPS")]
    public void TwentyMspsCompleteDecodeRejectsUnsupportedCpuRoutes(
        string backend,
        string tapeFormat,
        string inputRate,
        string expectedMessage)
    {
        if (backend == "ipp-fast")
        {
            Assert.SkipUnless(
                IppRuntime.TryProbe(out _),
                "The optional IPP runtime was not staged for this test build.");
        }

        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            [
                "--dsp-backend", backend,
                "--decode-at-20msps",
                "--tape_format", tapeFormat,
                "--frequency", inputRate,
                "capture.s16",
                "output"
            ]);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => DecodeSessionFactory.Create(command));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Preview options and realtime FPS display validate their contracts")]
    public void PreviewOptionsAndRealtimeFpsDisplayValidateTheirContracts()
    {
        new PreviewServerOptions { Crf = 0 }.Validate();
        new PreviewServerOptions { Crf = 51 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PreviewServerOptions { Crf = 52 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PreviewServerOptions { PortFallbackCount = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PreviewServerOptions { PortFallbackCount = 1_001 }.Validate());

        using var output = new StringWriter();
        var display = new PreviewRealtimeFpsDisplay(output, 25.0);
        display.Start();
        long second = Stopwatch.Frequency;
        display.Report(PreviewWindowGenerationUpdate.Started(3, startedTimestamp: 0));
        display.Report(PreviewWindowGenerationUpdate.Started(4, startedTimestamp: 0));
        display.Report(PreviewWindowGenerationUpdate.Completed(
            windowIndex: 3,
            frameCount: 50,
            startedTimestamp: 0,
            completedTimestamp: second * 2));
        display.Report(PreviewWindowGenerationUpdate.Completed(
            windowIndex: 4,
            frameCount: 50,
            startedTimestamp: 0,
            completedTimestamp: second * 5 / 2));
        display.Complete();
        display.Complete();

        string progress = output.ToString();
        Assert.StartsWith(
            $"Preview windows: waiting for the first preview window...{Environment.NewLine}"
            + "Realtime FPS: pending",
            progress);
        Assert.Contains("\r\u001b[1APreview windows: W3 | W4", progress);
        Assert.Contains(
            "\r\u001b[1BRealtime FPS: decoding... | decoding... | Total pending",
            progress);
        Assert.Contains(
            "\r\u001b[1BRealtime FPS: 25.00 | decoding... | Total pending",
            progress);
        Assert.Contains(
            "\r\u001b[1BRealtime FPS: 25.00 | 20.00 | Total 40.00 (1.60x source)",
            progress);
        Assert.Equal(2, progress.Count(character => character == '\n'));
    }

    [Theory(DisplayName = "Preview dimensions follow the requested video standards")]
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

    [Theory(DisplayName = "Preview encoders deinterlace to twice-rate progressive H.264")]
    [InlineData(
        "Nvenc",
        "h264_nvenc",
        "setfield=tff,format=nv12,hwupload_cuda,yadif_cuda=mode=send_field:parity=tff:deint=all")]
    [InlineData(
        "Qsv",
        "h264_qsv",
        "setfield=tff,tpad=stop_mode=clone:stop=1,format=nv12,hwupload=extra_hw_frames=64,vpp_qsv=deinterlace=advanced:rate=field")]
    [InlineData(
        "Amf",
        "h264_amf",
        "setfield=tff,yadif=mode=send_field:parity=tff:deint=all")]
    [InlineData(
        "Libx264",
        "libx264",
        "setfield=tff,yadif=mode=send_field:parity=tff:deint=all")]
    public void PreviewEncodersDeinterlaceToTwiceRateProgressiveH264(
        string backendName,
        string expectedEncoder,
        string expectedFilter)
    {
        PreviewEncoderBackend backend = Enum.Parse<PreviewEncoderBackend>(backendName);
        var timeline = new PreviewTimeline(2.0, 30_000.0 / 1_001.0, 2.0, 1);
        var encoder = new FfmpegHlsWindowEncoder(
            "ffmpeg",
            640,
            480,
            23,
            "NTSC",
            timeline,
            backend);

        string[] arguments = encoder.BuildArguments(
            "index.m3u8",
            "init.mp4",
            "segment-%03d.m4s",
            timeline.FrameCountInWindow(0),
            timeline.FramesPerSegment,
            timeline.FramesPerSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "2.002");

        AssertOption(arguments, "-pixel_format", "yuv420p");
        AssertOption(arguments, "-c:v", expectedEncoder);
        AssertOption(arguments, "-vf", expectedFilter);
        AssertOption(
            arguments,
            "-frames:v",
            checked(timeline.FrameCountInWindow(0) * 2).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AssertOption(
            arguments,
            "-g",
            checked(timeline.FramesPerSegment * 2).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AssertOption(arguments, "-profile:v", "main");
        AssertOption(
            arguments,
            "-bsf:v",
            "h264_metadata=video_full_range_flag=0:colour_primaries=6:transfer_characteristics=1:matrix_coefficients=6");
        Assert.DoesNotContain("-flags", arguments);
        Assert.DoesNotContain("-top", arguments);
        Assert.DoesNotContain("-output_ts_offset", arguments);

        if (backend == PreviewEncoderBackend.Qsv)
        {
            AssertOption(arguments, "-init_hw_device", "qsv=preview_qsv");
            AssertOption(arguments, "-filter_hw_device", "preview_qsv");
        }
        else
        {
            Assert.DoesNotContain("-init_hw_device", arguments);
        }

        if (backend == PreviewEncoderBackend.Libx264)
        {
            AssertOption(arguments, "-crf", "23");
            string x264Parameters = arguments[Array.IndexOf(arguments, "-x264-params") + 1];
            Assert.DoesNotContain("tff=1", x264Parameters);
            Assert.Contains("colorprim=smpte170m", x264Parameters);
            Assert.Contains("fullrange=off", x264Parameters);
            AssertEncodedWindowStartsAtGlobalTimelinePosition();
        }
    }

    [Fact(DisplayName = "CUDA preview FFmpeg stage copy-muxes H264 without CPU video filters")]
    public void CudaPreviewFfmpegStageOnlyCopyMuxesH264()
    {
        var timeline = new PreviewTimeline(2.0, 25.0, 2.0, 1);
        var muxer = new FfmpegH264HlsWindowMuxer("ffmpeg", timeline);

        string[] arguments = muxer.BuildArguments(
            "index.m3u8",
            "init.mp4",
            "segment-%03d.m4s",
            windowIndex: 0);

        AssertOption(arguments, "-r", "50");
        AssertOption(arguments, "-f", "h264");
        AssertOption(arguments, "-i", "pipe:0");
        AssertOption(arguments, "-c:v", "copy");
        AssertOption(arguments, "-frames:v", "100");
        Assert.DoesNotContain("-vf", arguments);
        Assert.DoesNotContain("rawvideo", arguments);
        Assert.DoesNotContain("h264_nvenc", arguments);
        Assert.DoesNotContain("-pix_fmt", arguments);
        Assert.DoesNotContain("-output_ts_offset", arguments);
    }

    [Fact(DisplayName = "CUDA preview H264 copy-mux produces seekable progressive fMP4")]
    public void CudaPreviewH264CopyMuxProducesSeekableProgressiveFmp4()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-cuda-preview-mux-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string h264Path = Path.Combine(directory, "input.h264");
            GenerateSyntheticH264(h264Path);
            var timeline = new PreviewTimeline(2.0, 25.0, 2.0, 1);
            var muxer = new FfmpegH264HlsWindowMuxer("ffmpeg", timeline);
            PreviewSegmentWindow window = muxer.Mux(
                windowIndex: 0,
                destination =>
                {
                    using FileStream input = File.OpenRead(h264Path);
                    input.CopyTo(destination);
                },
                TestContext.Current.CancellationToken);

            Assert.NotEmpty(window.InitializationSegment);
            Assert.Single(window.Segments);
            string mediaPath = Path.Combine(directory, "window.mp4");
            WriteCombinedWindow(mediaPath, window);
            IReadOnlyDictionary<string, string> metadata = ProbeVideoStreamMetadata(mediaPath);
            Assert.Equal("progressive", metadata["field_order"]);
            Assert.Equal("50/1", metadata["avg_frame_rate"]);
            Assert.Equal("100", metadata["nb_read_frames"]);
            Assert.InRange(ProbeFirstVideoPacketTimestamp(mediaPath), 0.0, 0.02);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory(DisplayName = "Preview encoder selection follows the hardware priority order")]
    [InlineData("Nvenc", 1)]
    [InlineData("Qsv", 2)]
    [InlineData("Amf", 3)]
    [InlineData("Libx264", 4)]
    public async Task PreviewEncoderSelectionFollowsTheHardwarePriorityOrder(
        string successfulBackendName,
        int expectedAttemptCount)
    {
        PreviewEncoderBackend successfulBackend = Enum.Parse<PreviewEncoderBackend>(
            successfulBackendName);
        var attempts = new List<PreviewEncoderBackend>();
        PreviewEncoderBackend selected = await PreviewEncoderSelector.SelectAsync(
            (candidate, _) =>
            {
                attempts.Add(candidate);
                return candidate == successfulBackend
                    ? Task.CompletedTask
                    : Task.FromException(new InvalidOperationException("unavailable"));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(successfulBackend, selected);
        Assert.Equal(expectedAttemptCount, attempts.Count);
        Assert.Equal(
            PreviewEncoderSelector.CandidateOrder.Take(expectedAttemptCount),
            attempts);
    }

    [Fact(DisplayName = "Preview encoder selection reports every failed backend")]
    public async Task PreviewEncoderSelectionReportsEveryFailedBackend()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PreviewEncoderSelector.SelectAsync(
                (candidate, _) => Task.FromException(
                    new InvalidOperationException($"{candidate} rejected")),
                TestContext.Current.CancellationToken));

        foreach (PreviewEncoderBackend candidate in PreviewEncoderSelector.CandidateOrder)
        {
            Assert.Contains(PreviewEncoderSelector.DisplayName(candidate), error.Message);
            Assert.Contains($"{candidate} rejected", error.Message);
        }
    }

    [Fact(DisplayName = "Preview encoder selection propagates cancellation without fallback")]
    public async Task PreviewEncoderSelectionPropagatesCancellationWithoutFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PreviewEncoderSelector.SelectAsync(
                (_, _) =>
                {
                    attempts++;
                    return Task.CompletedTask;
                },
                cancellation.Token));
        Assert.Equal(0, attempts);
    }

    [Fact(DisplayName = "Preview encoder selection validates a real local FFmpeg pipeline")]
    public async Task PreviewEncoderSelectionValidatesARealLocalFfmpegPipeline()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg"),
            "ffmpeg must be available on PATH.");

        PreviewEncoderBackend selected = await PreviewEncoderSelector.SelectAsync(
            "ffmpeg",
            width: 768,
            height: 576,
            crf: 31,
            system: "PAL",
            framesPerSecond: 25.0,
            TestContext.Current.CancellationToken);

        Assert.Contains(selected, PreviewEncoderSelector.CandidateOrder);
    }

    [Theory(DisplayName = "Available preview encoder backends produce compliant progressive fMP4")]
    [InlineData("Nvenc", "PAL", 25.0, 768, 576, "50/1", "bt470bg")]
    [InlineData("Nvenc", "NTSC", 29.97002997002997, 640, 480, "60000/1001", "smpte170m")]
    [InlineData("Qsv", "PAL", 25.0, 768, 576, "50/1", "bt470bg")]
    [InlineData("Qsv", "NTSC", 29.97002997002997, 640, 480, "60000/1001", "smpte170m")]
    [InlineData("Amf", "PAL", 25.0, 768, 576, "50/1", "bt470bg")]
    [InlineData("Amf", "NTSC", 29.97002997002997, 640, 480, "60000/1001", "smpte170m")]
    [InlineData("Libx264", "PAL", 25.0, 768, 576, "50/1", "bt470bg")]
    [InlineData("Libx264", "NTSC", 29.97002997002997, 640, 480, "60000/1001", "smpte170m")]
    public void AvailablePreviewEncoderBackendsProduceCompliantProgressiveFmp4(
        string backendName,
        string system,
        double sourceFramesPerSecond,
        int width,
        int height,
        string expectedOutputFrameRate,
        string expectedColorStandard)
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        const int windowIndex = 3;
        PreviewEncoderBackend backend = Enum.Parse<PreviewEncoderBackend>(backendName);
        var timeline = new PreviewTimeline(1.0, sourceFramesPerSecond, 0.2, 1);
        var encoder = new FfmpegHlsWindowEncoder(
            "ffmpeg",
            width,
            height,
            31,
            system,
            timeline,
            backend);
        byte[] frame = new byte[width * height * 3 / 2];
        Array.Fill(frame, (byte)16, 0, width * height);
        Array.Fill(frame, (byte)128, width * height, frame.Length - (width * height));
        PreviewSegmentWindow? encoded = null;
        string unavailableReason = string.Empty;
        try
        {
            encoded = EncodeSyntheticWindow(encoder, timeline, windowIndex, frame);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            unavailableReason = ex.Message;
        }

        Assert.SkipUnless(
            encoded is not null,
            $"{backendName} is unavailable on this machine: {unavailableReason}");
        Assert.NotNull(encoded);

        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-preview-backend-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mediaPath = Path.Combine(directory, "window.mp4");
            WriteCombinedWindow(mediaPath, encoded);
            IReadOnlyDictionary<string, string> metadata = ProbeVideoStreamMetadata(mediaPath);
            Assert.Equal("progressive", metadata["field_order"]);
            Assert.Equal(expectedOutputFrameRate, metadata["avg_frame_rate"]);
            Assert.Equal(expectedColorStandard, metadata["color_primaries"]);
            Assert.Equal("bt709", metadata["color_transfer"]);
            Assert.Equal(expectedColorStandard, metadata["color_space"]);
            Assert.Equal("tv", metadata["color_range"]);
            Assert.Equal(
                checked(timeline.FrameCountInWindow(windowIndex) * 2).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                metadata["nb_read_frames"]);
            double firstPacketTimestamp = ProbeFirstVideoPacketTimestamp(mediaPath);
            double expected = timeline.WindowStartSeconds(windowIndex);
            Assert.InRange(firstPacketTimestamp, expected, expected + 0.05);

            double tailDuration = 1.25 / sourceFramesPerSecond;
            var tailTimeline = new PreviewTimeline(
                tailDuration,
                sourceFramesPerSecond,
                1.0 / sourceFramesPerSecond,
                segmentsPerWindow: 1);
            var tailEncoder = new FfmpegHlsWindowEncoder(
                "ffmpeg",
                width,
                height,
                31,
                system,
                tailTimeline,
                backend);
            PreviewSegmentWindow tail = EncodeSyntheticWindow(
                tailEncoder,
                tailTimeline,
                windowIndex: 0,
                frame);
            string tailPath = Path.Combine(directory, "tail.mp4");
            WriteCombinedWindow(tailPath, tail);
            IReadOnlyDictionary<string, string> tailMetadata = ProbeVideoStreamMetadata(tailPath);
            Assert.Equal("progressive", tailMetadata["field_order"]);
            Assert.Equal(expectedOutputFrameRate, tailMetadata["avg_frame_rate"]);
            Assert.Equal("2", tailMetadata["nb_read_frames"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "Preview windows retain continuous source frames")]
    public void PreviewWindowsRetainContinuousSourceFrames()
    {
        const int width = 64;
        const int height = 48;
        const int outputFrameCount = 10;
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
            outputFrameCount);
        ushort[] chroma = Enumerable.Repeat(
            (ushort)32767,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        var writes = new List<(TbcDecodedField, TbcFieldOrderDecision)>();
        for (int fieldIndex = 0; fieldIndex < outputFrameCount * 2; fieldIndex++)
        {
            bool isFirstField = (fieldIndex & 1) == 0;
            int frameIndex = fieldIndex / 2;
            ushort frameLuma = session.VideoOutput.ConvertHz(
                session.VideoOutput.IreToHz(10.0 + (frameIndex * 7.0)));
            ushort[] luma = Enumerable.Repeat(
                frameLuma,
                session.TbcFrameSpec.FieldSampleCount).ToArray();
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

        Assert.Equal(outputFrameCount, assembler.SampledFrameCount);
        Assert.Equal(outputFrameCount, assembler.WrittenFrameCount);
        Assert.Equal(outputFrameCount * width * height * 3 / 2, output.Length);
        byte[] rawFrames = output.ToArray();
        int frameBytes = width * height * 3 / 2;
        string[] frameHashes = Enumerable.Range(0, outputFrameCount)
            .Select(index => Convert.ToHexString(SHA256.HashData(
                rawFrames.AsSpan(index * frameBytes, frameBytes))))
            .ToArray();
        Assert.Equal(outputFrameCount, frameHashes.Distinct().Count());
    }

    [Fact(DisplayName = "Preview sink stops decoding when the requested frames are complete")]
    public void PreviewSinkStopsDecodingWhenTheRequestedFramesAreComplete()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--pal", "--threads", "1", "capture.lds", "output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        int readCount = 0;
        int writtenCount = 0;
        var engine = new TbcFieldSequenceDecodeEngine(
            readField: (activeSession, _, begin, _, _) =>
            {
                int fieldIndex = readCount++;
                ushort[] samples = Enumerable.Repeat(
                    checked((ushort)(0x1200 + fieldIndex)),
                    activeSession.TbcFrameSpec.FieldSampleCount).ToArray();
                return new TbcDecodedField(
                    StartSample: begin,
                    Samples: samples,
                    LineLocations: new LineLocationResult([], []),
                    Timing: new SyncTiming(
                        0,
                        0,
                        0,
                        new SyncRange(0, 0),
                        new SyncRange(0, 0),
                        new SyncRange(0, 0)),
                    SyncThresholdHz: 0,
                    MeanLineLength: 0,
                    RawPulseCount: 0,
                    ClassifiedPulseCount: 0,
                    DetectedFirstField: (fieldIndex & 1) == 0,
                    DetectedFirstFieldConfidence: 100,
                    NextFieldOffsetSamples: 100,
                    NominalFieldLengthSamples: 100);
            });

        TbcFieldStreamDecodeResult result = engine.DecodeToSink(
            session,
            Stream.Null,
            writes => writtenCount += writes.Count,
            () => writtenCount >= 2,
            maxFields: 10);

        Assert.Equal(2, writtenCount);
        Assert.Equal(2, result.WrittenFieldCount);
        Assert.InRange(readCount, 2, 3);
        Assert.True(result.DecodedFieldCount < 10);
    }

    [Theory(DisplayName = "Separate-chroma preview luma matches the composite fast path")]
    [InlineData("PAL", 768, 576)]
    [InlineData("NTSC", 640, 480)]
    public void SeparateChromaPreviewLumaMatchesTheCompositeFastPath(
        string system,
        int width,
        int height)
    {
        string standard = system == "PAL" ? "--pal" : "--ntsc";
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            [standard, "capture.lds", "output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        var renderer = new PreviewFieldRenderer(session, width, height);
        ushort neutralLuma = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(50.0));
        ushort[] luma = Enumerable.Repeat(
            neutralLuma,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        ushort[] chroma = Enumerable.Repeat(
            (ushort)32767,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        var composite = new TbcDecodedField(
            0,
            luma,
            null!,
            null!,
            0.0,
            0.0,
            0,
            0);
        var separate = composite with { ChromaSamples = chroma };

        PreviewRenderedField compositeRendered = renderer.Render(
            composite,
            isFirstField: true);
        PreviewRenderedField separateRendered = renderer.Render(
            separate,
            isFirstField: true);

        Assert.Equal(compositeRendered.Luma, separateRendered.Luma);
    }

    [Theory(DisplayName = "Preview chroma prefix buffers are deterministic across reused renders")]
    [InlineData("PAL", 768, 576)]
    [InlineData("NTSC", 640, 480)]
    public void PreviewChromaPrefixBuffersAreDeterministicAcrossReusedRenders(
        string system,
        int width,
        int height)
    {
        string standard = system == "PAL" ? "--pal" : "--ntsc";
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            [standard, "capture.lds", "output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        var renderer = new PreviewFieldRenderer(session, width, height);
        ushort neutralLuma = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(50.0));
        ushort[] luma = Enumerable.Repeat(
            neutralLuma,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        ushort[] firstChroma = Enumerable.Repeat(
            (ushort)32767,
            session.TbcFrameSpec.FieldSampleCount).ToArray();
        ushort[] interveningChroma = (ushort[])firstChroma.Clone();
        AddFourFscCarrier(
            firstChroma,
            session.TbcFrameSpec.OutputLineLength,
            session.TbcFrameSpec.OutputLineCount,
            session.TbcFrameSpec.ColourBurstStart!.Value,
            session.TbcFrameSpec.ColourBurstEnd!.Value,
            session.TbcFrameSpec.ActiveVideoStart!.Value,
            session.TbcFrameSpec.ActiveVideoEnd!.Value,
            amplitude: 6000,
            centered: true);
        AddFourFscCarrier(
            interveningChroma,
            session.TbcFrameSpec.OutputLineLength,
            session.TbcFrameSpec.OutputLineCount,
            session.TbcFrameSpec.ColourBurstStart!.Value,
            session.TbcFrameSpec.ColourBurstEnd!.Value,
            session.TbcFrameSpec.ActiveVideoStart!.Value,
            session.TbcFrameSpec.ActiveVideoEnd!.Value,
            amplitude: 3000,
            centered: true);
        var firstField = new TbcDecodedField(
            0,
            luma,
            null!,
            null!,
            0.0,
            0.0,
            0,
            0,
            FieldPhaseId: 1,
            ChromaSamples: firstChroma);
        var interveningField = firstField with { ChromaSamples = interveningChroma };

        PreviewRenderedField first = renderer.Render(firstField, isFirstField: true);
        _ = renderer.Render(interveningField, isFirstField: false);
        PreviewRenderedField repeated = renderer.Render(firstField, isFirstField: true);

        Assert.Equal(first.Luma, repeated.Luma);
        Assert.Equal(first.ChromaU, repeated.ChromaU);
        Assert.Equal(first.ChromaV, repeated.ChromaV);
        Assert.Equal(first.LumaDropouts, repeated.LumaDropouts);
        Assert.Equal(first.ChromaDropouts, repeated.ChromaDropouts);
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

    [Fact(DisplayName = "PAL preview V-switch follows burst phase instead of field parity")]
    public void PalPreviewVSwitchFollowsBurstPhaseInsteadOfFieldParity()
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
        AddPalPhaseAlternatingCarrier(
            chroma,
            session.TbcFrameSpec.OutputLineLength,
            session.TbcFrameSpec.OutputLineCount,
            session.TbcFrameSpec.ColourBurstStart!.Value,
            session.TbcFrameSpec.ColourBurstEnd!.Value,
            session.TbcFrameSpec.ActiveVideoStart!.Value,
            session.TbcFrameSpec.ActiveVideoEnd!.Value,
            burstAmplitude: 6000,
            activeAmplitude: 4000);
        var field = new TbcDecodedField(
            0,
            luma,
            null!,
            null!,
            0.0,
            0.0,
            0,
            0,
            ChromaSamples: chroma);

        PreviewRenderedField firstField = renderer.Render(field, isFirstField: true);
        PreviewRenderedField secondField = renderer.Render(field, isFirstField: false);

        Assert.Equal(firstField.ChromaU, secondField.ChromaU);
        Assert.Equal(firstField.ChromaV, secondField.ChromaV);
        Assert.Contains(firstField.ChromaU, value => value != 128);
        Assert.Contains(firstField.ChromaV, value => value != 128);
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
        using JsonDocument info = JsonDocument.Parse(
            await client.GetStringAsync("api/info", cancellationToken));
        int targetWindow = timeline.WindowCount - 1;
        Assert.Contains($"window/{targetWindow}/segment/0.m4s", manifest);
        Assert.Contains("CODECS=\"avc1.4d401f\"", master);
        Assert.Contains("RESOLUTION=640x480", master);
        Assert.Equal(50.0, info.RootElement.GetProperty("framesPerSecond").GetDouble());
        Assert.False(info.RootElement.GetProperty("interlaced").GetBoolean());
        Assert.Equal(
            "test encoder",
            info.RootElement.GetProperty("encodeBackend").GetString());
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
        Assert.Contains("controls autoplay muted playsinline", player);
        Assert.Contains("video.play().catch", player);
        Assert.Contains("ensureWindow(windowIndex + 2)", player);
        Assert.DoesNotContain("timestampOffset", player);
        Assert.DoesNotContain("requestedWindows", player);
        Assert.DoesNotContain("cdn.jsdelivr.net", player);
    }

    [Fact(DisplayName = "Default preview port increments only when its fixed port is occupied")]
    public async Task DefaultPreviewPortIncrementsWhenOccupied()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TcpListener? blocker = null;
        int occupiedPort = 0;
        try
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                blocker = new TcpListener(IPAddress.Loopback, 0)
                {
                    ExclusiveAddressUse = true
                };
                blocker.Start();
                occupiedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
                if (occupiedPort <= IPEndPoint.MaxPort - 100)
                {
                    break;
                }

                blocker.Stop();
                blocker = null;
            }

            Assert.NotNull(blocker);
            Assert.InRange(occupiedPort, IPEndPoint.MinPort + 1, IPEndPoint.MaxPort - 100);

            var fallbackProvider = new FakeProvider(
                new PreviewTimeline(4.0, 25.0, 2.0, 1));
            await using PreviewHttpServer fallbackServer = await PreviewHttpServer.StartAsync(
                fallbackProvider,
                new PreviewServerOptions
                {
                    Port = occupiedPort,
                    PortFallbackCount = 100
                },
                cancellationToken);

            Assert.InRange(fallbackServer.BaseAddress.Port, occupiedPort + 1, occupiedPort + 100);
            using (var client = new HttpClient { BaseAddress = fallbackServer.BaseAddress })
            {
                Assert.Contains(
                    "ok",
                    await client.GetStringAsync("health", cancellationToken),
                    StringComparison.Ordinal);
            }

            var exactProvider = new FakeProvider(
                new PreviewTimeline(4.0, 25.0, 2.0, 1));
            await Assert.ThrowsAnyAsync<IOException>(() => PreviewHttpServer.StartAsync(
                exactProvider,
                new PreviewServerOptions
                {
                    Port = occupiedPort,
                    PortFallbackCount = 0
                },
                cancellationToken));
        }
        finally
        {
            blocker?.Stop();
        }
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
            timeline,
            PreviewEncoderBackend.Libx264);
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
            WriteCombinedWindow(mediaPath, encoded);

            double firstPacketTimestamp = ProbeFirstVideoPacketTimestamp(mediaPath);
            double expected = timeline.WindowStartSeconds(windowIndex);
            Assert.InRange(firstPacketTimestamp, expected, expected + 0.05);
            IReadOnlyDictionary<string, string> metadata = ProbeVideoStreamMetadata(mediaPath);
            Assert.Equal("progressive", metadata["field_order"]);
            Assert.Equal("50/1", metadata["avg_frame_rate"]);
            Assert.Equal("bt470bg", metadata["color_primaries"]);
            Assert.Equal("bt709", metadata["color_transfer"]);
            Assert.Equal("bt470bg", metadata["color_space"]);
            Assert.Equal("tv", metadata["color_range"]);
            Assert.Equal(
                checked(timeline.FrameCountInWindow(windowIndex) * 2).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                metadata["nb_read_frames"]);
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

    private static void WriteCombinedWindow(
        string mediaPath,
        PreviewSegmentWindow window)
    {
        using FileStream media = File.Create(mediaPath);
        media.Write(window.InitializationSegment);
        foreach (PreviewMediaSegment segment in window.Segments)
        {
            media.Write(segment.Data);
        }
    }

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

    private static IReadOnlyDictionary<string, string> ProbeVideoStreamMetadata(string mediaPath)
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
            "-count_frames",
            "-select_streams", "v:0",
            "-show_entries", "stream=avg_frame_rate,field_order,nb_read_frames,color_range,color_space,color_transfer,color_primaries",
            "-of", "default=noprint_wrappers=1",
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
        return output.Result.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    private static void GenerateSyntheticH264(string outputPath)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", "color=c=black:s=64x48:r=50:d=2",
            "-an",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-profile:v", "main",
            "-pix_fmt", "yuv420p",
            "-g", "100",
            "-bf", "0",
            "-f", "h264",
            "-y", outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode == 0,
            $"ffmpeg exited with {process.ExitCode}: {error.Result}");
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

    private static void AddPalPhaseAlternatingCarrier(
        ushort[] samples,
        int lineLength,
        int lineCount,
        int burstStart,
        int burstEnd,
        int activeStart,
        int activeEnd,
        int burstAmplitude,
        int activeAmplitude)
    {
        for (int line = 0; line < lineCount; line++)
        {
            AddRange(burstStart, burstEnd, line & 3, burstAmplitude);
            AddRange(activeStart, activeEnd, (line + 1) & 3, activeAmplitude);

            void AddRange(int start, int end, int phase, int amplitude)
            {
                for (int x = start; x < end; x++)
                {
                    int carrier = ((x + phase) & 3) switch
                    {
                        0 => amplitude,
                        2 => -amplitude,
                        _ => 0
                    };
                    int index = (line * lineLength) + x;
                    samples[index] = (ushort)Math.Clamp(32767 + carrier, 0, ushort.MaxValue);
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
                timeline.FramesPerSecond * 2.0,
                timeline.DurationSeconds,
                640,
                480,
                31,
                false,
                "test",
                "test",
                "test encoder");
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
                timeline.FramesPerSecond * 2.0,
                timeline.DurationSeconds,
                768,
                576,
                31,
                false,
                "test",
                "test",
                "test encoder");
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

    private sealed class GeneratedSampleLoader(Func<long, double> sampleGenerator)
        : IRfSampleLoader
    {
        internal long LastSample { get; private set; }

        internal int LastReadLength { get; private set; }

        public double[] Read(Stream stream, long sample, int readLength)
        {
            LastSample = sample;
            LastReadLength = readLength;
            return Enumerable.Range(0, readLength)
                .Select(index => sampleGenerator(sample + index))
                .ToArray();
        }
    }

    private static byte[] Pack4x10(int[] samples)
    {
        if (samples.Length % 4 != 0)
        {
            throw new ArgumentException("Sample count must be divisible by four.");
        }

        byte[] output = new byte[(samples.Length / 4) * 5];
        for (int group = 0; group < samples.Length / 4; group++)
        {
            int s0 = samples[group * 4] & 0x3FF;
            int s1 = samples[(group * 4) + 1] & 0x3FF;
            int s2 = samples[(group * 4) + 2] & 0x3FF;
            int s3 = samples[(group * 4) + 3] & 0x3FF;
            int index = group * 5;
            output[index] = (byte)(s0 >> 2);
            output[index + 1] = (byte)(((s0 & 0x03) << 6) | (s1 >> 4));
            output[index + 2] = (byte)(((s1 & 0x0F) << 4) | (s2 >> 6));
            output[index + 3] = (byte)(((s2 & 0x3F) << 2) | (s3 >> 8));
            output[index + 4] = (byte)(s3 & 0xFF);
        }

        return output;
    }
}
