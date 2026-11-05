using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.CudaFast;
using VHSDecode.Core.HiFi;
using VHSDecode.Preview;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscCudaCommandTests
{
    [Fact]
    public void LaserDiscCudaPreviewIsRejectedWithoutOpeningInputOrGpu()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.LaserDisc, ["--preview-server", "--dsp-backend", "cuda-fast", "capture.ldf"]);
        using var error = new StringWriter();
        var runner = CreateRunner((_, _, _) => throw new Xunit.Sdk.XunitException("GPU must not be opened."));

        Assert.Equal(1, runner.Run(command, TextWriter.Null, error, TestContext.Current.CancellationToken));
        Assert.Contains("does not support LaserDisc preview", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaserDiscCudaPreviewCommandRejectsBeforeCreatingAProvider()
    {
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.LaserDisc, ["--preview-server", "--dsp-backend", "cuda-fast", "missing-capture.ldf"]);
        using var error = new StringWriter();

        Assert.True(PreviewCommandRunner.IsRequested(command));
        Assert.Equal(1, await new PreviewCommandRunner().RunAsync(
            command, TextWriter.Null, error, TestContext.Current.CancellationToken));
        Assert.Contains("does not support LaserDisc preview", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--PAL", null, "supports NTSC only")]
    [InlineData("--NTSCJ", null, "supports NTSC only")]
    [InlineData("--frequency", "20", "requires 40 MSPS RF input")]
    [InlineData("--threads", "1", "at least two RF worker threads")]
    [InlineData("--noDOD", null, "requires the NTSC 40 MSPS immediate field path")]
    public void UnsupportedLaserDiscCudaOptionsFailBeforeOpeningGpuOrOutputs(
        string option, string? value, string expected)
    {
        using var input = new TestFiles();
        ParsedCommand command = value is null ? input.Parse(option) : input.Parse(option, value);
        using var error = new StringWriter();
        var runner = CreateRunner((_, _, _) => throw new Xunit.Sdk.XunitException("GPU must not be opened."));

        Assert.Equal(1, runner.Run(command, TextWriter.Null, error, TestContext.Current.CancellationToken));
        Assert.Contains(expected, error.ToString(), StringComparison.Ordinal);
        input.AssertNoOutputs();
    }

    [Fact]
    public void UnavailableLaserDiscGpuFailsBeforeOpeningOutputs()
    {
        using var input = new TestFiles();
        using var error = new StringWriter();
        int factoryCalls = 0;
        var runner = CreateRunner((filter, _, _) =>
        {
            factoryCalls++;
            Assert.Equal(32_768, filter.Length);
            throw new CudaFastBackendUnavailableException("test LD EFM native capability is unavailable");
        });

        Assert.Equal(1, runner.Run(input.Parse(), TextWriter.Null, error, TestContext.Current.CancellationToken));
        Assert.Equal(1, factoryCalls);
        Assert.Contains("test LD EFM native capability is unavailable", error.ToString(), StringComparison.Ordinal);
        input.AssertNoOutputs();
    }

    [Fact]
    public void UnavailableLaserDiscGpuVideoDisposesEfmAndFailsBeforeOpeningOutputs()
    {
        Assert.SkipUnless(Avx2.IsSupported && Fma.IsSupported, "LD CUDA video requires AVX2 and FMA.");
        using var input = new TestFiles();
        using var error = new StringWriter();
        var efm = new EmptyLaserDiscCudaEfm();
        int videoFactoryCalls = 0;
        var runner = CreateRunner((_, _, _) => efm, (filters, _, _) =>
        {
            videoFactoryCalls++;
            Assert.Equal(32_768, filters.Video.Length);
            Assert.Equal(32_768, filters.VideoLowPass05.Length);
            Assert.Equal(32_768, Assert.IsType<Complex[]>(filters.LdVideoBurst).Length);
            Assert.Equal(32, filters.VideoLowPass05Offset);
            Assert.Equal(40, filters.LdVideoBurstOffset);
            throw new CudaFastBackendUnavailableException("test LD video native capability is unavailable");
        });

        Assert.Equal(1, runner.Run(input.Parse(), TextWriter.Null, error, TestContext.Current.CancellationToken));
        Assert.Equal(1, videoFactoryCalls);
        Assert.Equal(1, efm.DisposeCalls);
        Assert.Contains("test LD video native capability is unavailable", error.ToString(), StringComparison.Ordinal);
        input.AssertNoOutputs();
    }

    [Fact]
    public void LaserDiscCudaUsesOwnedLaserDiscWriterAndDisposesGpu()
    {
        using var input = new TestFiles();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var gpu = new EmptyLaserDiscCudaEfm();
        var video = new EmptyLaserDiscCudaVideo();
        int factoryCalls = 0;
        int videoFactoryCalls = 0;
        var runner = CreateRunner((filter, writer, token) =>
        {
            factoryCalls++;
            Assert.Equal(32_768, filter.Length);
            Assert.Same(output, writer);
            Assert.Equal(cancellation.Token, token);
            return gpu;
        }, (_, writer, token) =>
        {
            videoFactoryCalls++;
            Assert.Same(output, writer);
            Assert.Equal(cancellation.Token, token);
            return video;
        });
        ParsedCommand command = input.Parse("--length", "0");

        Assert.Equal(0, runner.Run(command, output, error, cancellation.Token));
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, gpu.DisposeCalls);
        Assert.Contains(gpu.Diagnostic, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("CUDA LD EFM blocks: 0", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("CUDA LD video blocks: 0", output.ToString(), StringComparison.Ordinal);
        int expectedVideoCalls = Avx2.IsSupported && Fma.IsSupported ? 1 : 0;
        Assert.Equal(expectedVideoCalls, videoFactoryCalls);
        Assert.Equal(expectedVideoCalls, video.DisposeCalls);
        Assert.Equal("cuda-fast", command.Get<string>("dsp_backend"));
        foreach (string suffix in new[] { ".tbc", ".efm", ".pcm", ".tbc.db" })
        {
            Assert.True(File.Exists(input.OutputBase + suffix), "Missing LD output " + suffix);
        }
    }

    [Fact]
    public void LaserDiscCudaVideoOnlyEmptyDecodeOpensNeitherProviderNorEfmFiles()
    {
        using var input = new TestFiles();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = CreateRunner((_, _, _) => throw new Xunit.Sdk.XunitException("EFM must not be opened."));

        Assert.Equal(0, runner.Run(input.Parse("--noEFM", "--preEFM", "--length", "0"),
            output, error, TestContext.Current.CancellationToken));
        Assert.Contains("CUDA LD video blocks: 0", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CUDA LD EFM blocks:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Completed without handling any frames.", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, new FileInfo(input.OutputBase + ".tbc").Length);
        Assert.Equal(0, new FileInfo(input.OutputBase + ".pcm").Length);
        Assert.True(File.Exists(input.OutputBase + ".tbc.db"));
        Assert.False(File.Exists(input.OutputBase + ".tbc.json"));
        Assert.False(File.Exists(input.OutputBase + ".efm"));
        Assert.False(File.Exists(input.OutputBase + ".prefm"));
    }

    [Fact]
    public void LaserDiscCudaVideoOnlyUnavailableProviderFailsBeforeOutputsWithoutOpeningEfm()
    {
        Assert.SkipUnless(Avx2.IsSupported && Fma.IsSupported, "LD CUDA video requires AVX2 and FMA.");
        using var input = new TestFiles();
        using var error = new StringWriter();
        int calls = 0;
        var runner = CreateRunner(
            (_, _, _) => throw new Xunit.Sdk.XunitException("EFM must not be opened."),
            (filters, _, _) =>
            {
                calls++;
                Assert.Null(filters.LdEfm);
                throw new CudaFastBackendUnavailableException("test video-only native capability is unavailable");
            });

        Assert.Equal(1, runner.Run(input.Parse("--noEFM"), TextWriter.Null, error, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        Assert.Contains("test video-only native capability is unavailable", error.ToString(), StringComparison.Ordinal);
        input.AssertNoOutputs();
    }

    [Fact]
    public void LaserDiscCudaVideoOnlyPositiveRequestWithNoEligibleWaveFailsTruthfully()
    {
        Assert.SkipUnless(Avx2.IsSupported && Fma.IsSupported, "LD CUDA video requires AVX2 and FMA.");
        using var input = new TestFiles();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var video = new EmptyLaserDiscCudaVideo();
        var runner = CreateRunner(
            (_, _, _) => throw new Xunit.Sdk.XunitException("EFM must not be opened."),
            (_, _, _) => video);

        Assert.Equal(1, runner.Run(input.Parse("--noEFM", "--length", "1"),
            output, error, TestContext.Current.CancellationToken));
        Assert.Equal(1, video.DisposeCalls);
        Assert.Contains("CUDA LD video blocks: 0", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CUDA LD EFM blocks:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("no eligible RF batch for GPU video", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Completed", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(input.OutputBase + ".efm"));
        Assert.False(File.Exists(input.OutputBase + ".prefm"));
    }

    [Fact]
    public void LaserDiscCudaVideoOnlyProcessesEligibleOwnedWaveAndPublishesCompactChannels()
    {
        Assert.SkipUnless(Avx2.IsSupported && Fma.IsSupported, "LD CUDA video requires AVX2 and FMA.");
        using var input = new TestFiles();
        var command = new CommandLineParser().Parse(CliSpecs.LaserDisc,
            ["--NTSC", "--noEFM", "--threads", "4", "--dsp-backend", "exact", input.InputPath, input.OutputBase]);
        using DecodeSession session = DecodeSessionFactory.Create(command);
        Assert.Null(session.Filters.LdEfm);
        var reader = Assert.IsType<RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader>(
            session.StreamDecoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        using var video = new MarkedLaserDiscCudaVideo();
        Assert.Same(video, reader.AttachCudaVideoIfSupported(() => video));

        int stride = session.StreamDecoder.BlockStride;
        int length = 21 * stride + 1;
        int sampleCount = 21 * stride + session.StreamDecoder.BlockLength;
        var samples = new byte[sampleCount * sizeof(short)];
        for (int i = 0; i < sampleCount; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * sizeof(short)), (short)(((i & 7) - 4) * 2048));
        }

        using var stream = new MemoryStream(samples, writable: false);
        using var span = Assert.IsType<RfBlockStreamDecoder.RfDecodedSpanLease>(reader.Read(stream, 0, length));
        Assert.Equal(1, video.Calls);
        Assert.InRange(video.ProcessedBlocks, 21L, 22L);
        Assert.Null(span.Span.Efm);
        Assert.Equal(1_234_567.0, span.Span.Video[stride]);
        Assert.Equal(2_345_678.0, Assert.IsType<double[]>(span.Span.VideoLowPass)[stride]);
        Assert.Equal(3_456_789.0, Assert.IsType<double[]>(span.Span.VideoBurst)[stride]);
    }

    private static DecodeRunner CreateRunner(
        Func<Complex[], TextWriter, CancellationToken, ILaserDiscCudaEfm> factory,
        Func<DecodeFilterSet, TextWriter, CancellationToken, ILaserDiscCudaVideo>? videoFactory = null)
        => new(
            _ => throw new Xunit.Sdk.XunitException("Ordinary CPU engine must not be selected."),
            new HiFiDecodeRunner(),
            new UnexpectedVhsCudaRunner(),
            factory,
            videoFactory ?? ((_, _, _) => throw new Xunit.Sdk.XunitException("GPU video must not be opened.")));

    private sealed class UnexpectedVhsCudaRunner : ICudaFastDecodeRunner
    {
        public TbcFieldSequenceDecodeResult TryDecodeAndWrite(
            ParsedCommand command, TextWriter output, CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException("VHS CUDA runner must not be selected.");
    }

    private sealed class EmptyLaserDiscCudaEfm : ILaserDiscCudaEfm
    {
        public string Diagnostic => "test LD CUDA EFM";
        public long ProcessedBlocks => 0;
        public int DisposeCalls { get; private set; }

        public short[][] Process(double[][] preparedInputs, int[] ordinals, int count)
            => throw new Xunit.Sdk.XunitException("Empty decode must not dispatch EFM blocks.");

        public void Dispose() => DisposeCalls++;
    }

    private sealed class EmptyLaserDiscCudaVideo : ILaserDiscCudaVideo
    {
        public long ProcessedBlocks => 0;
        public int DisposeCalls { get; private set; }

        public LaserDiscCudaVideoChannels[] Process(double[][] rawByBlock, int[] ordinals)
            => throw new Xunit.Sdk.XunitException("Empty decode must not dispatch video blocks.");

        public void Dispose() => DisposeCalls++;
    }

    private sealed class MarkedLaserDiscCudaVideo : ILaserDiscCudaVideo
    {
        public long ProcessedBlocks { get; private set; }
        public int Calls { get; private set; }

        public LaserDiscCudaVideoChannels[] Process(double[][] rawByBlock, int[] ordinals)
        {
            Calls++;
            ProcessedBlocks += ordinals.Length;
            var result = new LaserDiscCudaVideoChannels[ordinals.Length];
            for (int i = 0; i < ordinals.Length; i++)
            {
                Assert.Equal(32_768, rawByBlock[ordinals[i]].Length);
                var video = new float[32_768];
                var lowPass = new float[32_768];
                var burst = new float[32_768];
                Array.Fill(video, 1_234_567.0f);
                Array.Fill(lowPass, 2_345_678.0f);
                Array.Fill(burst, 3_456_789.0f);
                result[i] = new LaserDiscCudaVideoChannels(video, lowPass, burst);
            }
            return result;
        }

        public void Dispose() { }
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ld-cuda-command-tests-");
        private readonly string _inputPath;

        public TestFiles()
        {
            _inputPath = Path.Combine(_directory.FullName, "capture.s16");
            File.WriteAllBytes(_inputPath, [0, 0]);
            OutputBase = Path.Combine(_directory.FullName, "output");
        }

        public string InputPath => _inputPath;
        public string OutputBase { get; }

        public ParsedCommand Parse(params string[] options)
            => new CommandLineParser().Parse(
                CliSpecs.LaserDisc,
                ["--dsp-backend", "cuda-fast", .. options, _inputPath, OutputBase]);

        public void AssertNoOutputs() => Assert.Equal(_inputPath, Assert.Single(Directory.GetFiles(_directory.FullName)));

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
