using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class DecodeStartPositionCompatibilityTests
{
    [Fact(DisplayName = "Large integer start frames retain exact Python sample arithmetic")]
    public void LargeIntegerStartFramesRetainExactPythonSampleArithmetic()
    {
        const string startFrame = "9007199254740993";
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
            "--start",
            startFrame,
            "input.u8",
            "out"
        ]);

        DecodeRunBounds bounds = DecodeRunBounds.FromCommand(command, nominalFieldSampleCount: 100);

        Assert.Equal(
            BigInteger.Parse(startFrame) * 200,
            new BigInteger(bounds.StartSample));
    }

    [Fact(DisplayName = "Non-finite start offsets fail on first read like v0.4.0")]
    public void NonFiniteStartOffsetsFailOnFirstReadLikeV040()
    {
        AssertNonFiniteStartFailure(
            "inf",
            "inf",
            "cannot convert float infinity to integer");
        AssertNonFiniteStartFailure(
            "nan",
            "nan",
            "cannot convert float NaN to integer");
    }

    [Fact(DisplayName = "LD seek replaces start file location with its frame probe")]
    public void LdSeekReplacesStartFileLocationWithItsFrameProbe()
    {
        using DecodeSession targetStart = CreateLaserDiscSession(
            "--start_fileloc",
            "inf",
            "--seek",
            "7");
        long nominalFieldSamples = targetStart.TbcFieldDecoder.EstimateNominalFieldSampleCount();
        var targetReads = new List<long>();
        var targetEngine = new TbcFieldSequenceDecodeEngine(
            readField: (_, _, begin, _, _) =>
            {
                targetReads.Add(begin);
                return null;
            });

        Assert.Throws<InvalidOperationException>(
            () => targetEngine.DecodeFields(targetStart, new MemoryStream()));
        Assert.Equal(7 * 2 * nominalFieldSamples, targetReads[0]);

        using DecodeSession explicitStart = CreateLaserDiscSession(
            "--start",
            "1.5",
            "--start_fileloc",
            "12345",
            "--seek",
            "7");
        var explicitReads = new List<long>();
        var explicitEngine = new TbcFieldSequenceDecodeEngine(
            readField: (_, _, begin, _, _) =>
            {
                explicitReads.Add(begin);
                return null;
            });

        Assert.Throws<InvalidOperationException>(
            () => explicitEngine.DecodeFields(explicitStart, new MemoryStream()));
        Assert.Equal(3 * nominalFieldSamples, explicitReads[0]);
    }

    private static void AssertNonFiniteStartFailure(
        string argument,
        string expectedSample,
        string expectedMessage)
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-start-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string inputPath = Path.Combine(tempDirectory, "input.u8");
            string outputBase = Path.Combine(tempDirectory, "output");
            File.WriteAllBytes(inputPath, [0]);
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
                "--threads",
                "0",
                "--length",
                "1",
                "--start_fileloc",
                argument,
                inputPath,
                outputBase
            ]);
            var output = new StringWriter();
            var error = new StringWriter();
            var runner = new DecodeRunner(
                _ => new TbcFieldSequenceDecodeEngine(
                    readField: static (_, _, _, _, _) => null));

            int exitCode = runner.Run(command, output, error);
            string errorText = error.ToString();

            Assert.Equal(1, exitCode);
            Assert.Contains($"current sample: {expectedSample}{Environment.NewLine}", errorText);
            Assert.Contains($"Exception: {expectedMessage}  Traceback:{Environment.NewLine}", errorText);
            Assert.Contains("arguments: " + PythonNamespaceFormatter.Format(command), errorText);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static DecodeSession CreateLaserDiscSession(params string[] options)
    {
        var arguments = new List<string>(options)
        {
            "input.u16",
            "out"
        };
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc, arguments);
        return DecodeSessionFactory.Create(command);
    }
}
