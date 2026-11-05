using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
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
    [InlineData("--noEFM", null, "requires EFM output")]
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
    public void LaserDiscCudaUsesOwnedLaserDiscWriterAndDisposesGpu()
    {
        using var input = new TestFiles();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var gpu = new EmptyLaserDiscCudaEfm();
        int factoryCalls = 0;
        var runner = CreateRunner((filter, writer, token) =>
        {
            factoryCalls++;
            Assert.Equal(32_768, filter.Length);
            Assert.Same(output, writer);
            Assert.Equal(cancellation.Token, token);
            return gpu;
        });
        ParsedCommand command = input.Parse("--length", "0");

        Assert.Equal(0, runner.Run(command, output, error, cancellation.Token));
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, gpu.DisposeCalls);
        Assert.Contains(gpu.Diagnostic, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("CUDA LD EFM blocks: 0", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("cuda-fast", command.Get<string>("dsp_backend"));
        foreach (string suffix in new[] { ".tbc", ".efm", ".pcm", ".tbc.db" })
        {
            Assert.True(File.Exists(input.OutputBase + suffix), "Missing LD output " + suffix);
        }
    }

    private static DecodeRunner CreateRunner(
        Func<Complex[], TextWriter, CancellationToken, ILaserDiscCudaEfm> factory)
        => new(
            _ => throw new Xunit.Sdk.XunitException("Ordinary CPU engine must not be selected."),
            new HiFiDecodeRunner(),
            new UnexpectedVhsCudaRunner(),
            factory);

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

        public string OutputBase { get; }

        public ParsedCommand Parse(params string[] options)
            => new CommandLineParser().Parse(
                CliSpecs.LaserDisc,
                ["--dsp-backend", "cuda-fast", .. options, _inputPath, OutputBase]);

        public void AssertNoOutputs() => Assert.Equal(_inputPath, Assert.Single(Directory.GetFiles(_directory.FullName)));

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
