using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsWeakRfWarningCompatibilityTests : IDisposable
{
    private const string Warning = "RF signal is weak. Is your deck tracking properly?";
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));

    public VhsWeakRfWarningCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact(DisplayName = "VHS zero RF envelope emits the exact v0.4.0 warning")]
    public void ZeroRfEnvelopeEmitsExactWarning()
    {
        const int blockLength = 4_096;
        string outputBase = Path.Combine(_tempDirectory, "weak-rf");
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
            "--pal",
            "-f",
            "40",
            "--no_resample",
            "input.s16",
            outputBase
        ]);
        using DecodeSession session = DecodeSessionFactory.Create(command, blockLength);
        var error = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);
        DecodeSessionLogWriter.Write(session);
        using var input = new MemoryStream(new byte[blockLength * sizeof(short)]);

        RfDemodulatedBlock block = Assert.IsType<RfDemodulatedBlock>(
            session.Pipeline.DecodeBlock(input, sample: 0, blockLength));

        Assert.True(block.VhsWeakRfSignal);
        Assert.Equal(Warning + Environment.NewLine, error.ToString());
        Assert.Contains(
            "WARNING - " + Warning,
            File.ReadAllText(outputBase + ".log"),
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }
}
