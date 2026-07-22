using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CommandLineComputeBackendTests
{
    [Fact(DisplayName = "RF compute options are available only to video decoders")]
    public void RfComputeOptionsAreAvailableOnlyToVideoDecoders()
    {
        foreach (DecodeCommandSpec spec in VideoSpecs())
        {
            Assert.True(spec.TryGetOption("--compute-backend", out _));
            Assert.True(spec.TryGetOption("--cuda-device", out _));

            ParsedCommand command = Parse(spec);
            Assert.Equal("auto", command.Get<string>("compute_backend"));
            Assert.Equal(0, command.Get<int>("cuda_device"));
        }

        Assert.False(CliSpecs.HiFi.TryGetOption("--compute-backend", out _));
        Assert.False(CliSpecs.HiFi.TryGetOption("--cuda-device", out _));
    }

    [Fact(DisplayName = "RF compute options normalize and populate decode execution options")]
    public void RfComputeOptionsNormalizeAndPopulateDecodeExecutionOptions()
    {
        foreach (DecodeCommandSpec spec in VideoSpecs())
        {
            ParsedCommand command = Parse(
                spec,
                "--compute-backend", "CUDA",
                "--cuda-device", "2");

            Assert.Equal("cuda", command.Get<string>("compute_backend"));
            Assert.Equal(2, command.Get<int>("cuda_device"));

            // Session construction intentionally performs the real CUDA
            // sidecar/device/self-test preflight. Use CPU here so this parser
            // test remains valid on ordinary CPU-only CI.
            ParsedCommand cpuCommand = Parse(
                spec,
                "--compute-backend", "CPU",
                "--cuda-device", "2");
            using DecodeSession session = DecodeSessionFactory.Create(cpuCommand);
            Assert.Equal(RfComputeBackend.Cpu, session.ExecutionOptions.ComputeBackend);
            Assert.Equal(2, session.ExecutionOptions.CudaDevice);
        }
    }

    [Fact(DisplayName = "RF compute option validation rejects invalid backends and devices")]
    public void RfComputeOptionValidationRejectsInvalidBackendsAndDevices()
    {
        CommandLineParseException backend = Assert.Throws<CommandLineParseException>(
            () => Parse(CliSpecs.Vhs, "--compute-backend", "metal"));
        Assert.Equal(
            "argument --compute-backend: invalid choice: 'metal' (choose from auto, cpu, cuda)",
            backend.Message);

        CommandLineParseException negativeDevice = Assert.Throws<CommandLineParseException>(
            () => Parse(CliSpecs.Cvbs, "--cuda-device", "-1"));
        Assert.Equal(
            "argument --cuda-device: must be a non-negative integer",
            negativeDevice.Message);

        CommandLineParseException oversizedDevice = Assert.Throws<CommandLineParseException>(
            () => Parse(CliSpecs.LaserDisc, "--cuda-device", "2147483648"));
        Assert.Equal(
            "argument --cuda-device: must be no greater than 2147483647",
            oversizedDevice.Message);
    }

    [Fact(DisplayName = "RF compute options do not alter Python Namespace diagnostics")]
    public void RfComputeOptionsDoNotAlterPythonNamespaceDiagnostics()
    {
        foreach (DecodeCommandSpec spec in VideoSpecs())
        {
            string baseline = PythonNamespaceFormatter.Format(Parse(spec));
            string configured = PythonNamespaceFormatter.Format(Parse(
                spec,
                "--compute-backend", "cuda",
                "--cuda-device", "3"));

            Assert.Equal(baseline, configured);
            Assert.DoesNotContain("compute_backend", configured, StringComparison.Ordinal);
            Assert.DoesNotContain("cuda_device", configured, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "RF compute options are documented for every video invocation")]
    public void RfComputeOptionsAreDocumentedForEveryVideoInvocation()
    {
        foreach (DecodeCommandSpec spec in VideoSpecs())
        {
            foreach (string programName in new[] { spec.Aliases[0], "decode.py" })
            {
                string help = CommandHelpFormatter.Format(spec, programName);
                Assert.Contains("--compute-backend {auto,cpu,cuda}", help, StringComparison.Ordinal);
                Assert.Contains("--cuda-device device", help, StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain(
            "--compute-backend",
            CommandHelpFormatter.Format(CliSpecs.HiFi, "hifi-decode"),
            StringComparison.Ordinal);
    }

    private static IEnumerable<DecodeCommandSpec> VideoSpecs()
        => [CliSpecs.Vhs, CliSpecs.Cvbs, CliSpecs.LaserDisc];

    private static ParsedCommand Parse(DecodeCommandSpec spec, params string[] options)
    {
        string[] positionals = spec.Name switch
        {
            "ld" => ["input.s16", "out"],
            _ => ["input.u8", "out"]
        };
        return new CommandLineParser().Parse(spec, [.. options, .. positionals]);
    }
}
