using System.Diagnostics;
using System.Text.Json;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class DecodeProcessObservationTests
{
    [Fact]
    public void DisabledObservationDoesNotCreateOutputDirectories()
    {
        string directory = NewPath();
        Assert.Null(DecodeProcessObservation.Start(Command(directory), null, "invalid"));
        Assert.Null(DecodeProcessObservation.Start(Command(directory), "0", "invalid"));
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("yes", "1000")]
    [InlineData("1", "49")]
    [InlineData("1", "60001")]
    [InlineData("1", "bad")]
    public void InvalidObservationSettingsFailBeforeCreatingFiles(string enabled, string interval)
    {
        string directory = NewPath();
        Assert.Throws<ArgumentException>(() => DecodeProcessObservation.Start(Command(directory), enabled, interval));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void HelpDoesNotCreateObservationFiles()
    {
        ParsedCommand help = new CommandLineParser().Parse(CliSpecs.Vhs, ["--help"]);
        Assert.Null(DecodeProcessObservation.Start(help, "1", null));
    }

    [Fact]
    public async Task ObservationStreamsSamplesAndStopsWithoutRetainingFileHandles()
    {
        string directory = NewPath();
        string report = Path.Combine(directory, "capture.process.jsonl");
        try
        {
            var observation = DecodeProcessObservation.Start(Command(directory), "1", "50")!;
            using (observation)
            {
                var timeout = Stopwatch.StartNew();
                while (ReadShared(report).Length < 3 && timeout.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(25, TestContext.Current.CancellationToken);
                }
                Assert.True(ReadShared(report).Length >= 3, "Periodic samples were not flushed during execution.");
                observation.SetExitCode(7);
            }
            observation.Dispose();
            string[] lines = File.ReadAllLines(report);
            using JsonDocument first = JsonDocument.Parse(lines[0]);
            using JsonDocument last = JsonDocument.Parse(lines[^1]);
            Assert.Equal("start", first.RootElement.GetProperty("kind").GetString());
            Assert.Equal("stop", last.RootElement.GetProperty("kind").GetString());
            Assert.Equal(7, last.RootElement.GetProperty("exitCode").GetInt32());
            double previous = -1;
            foreach (string line in lines)
            {
                using JsonDocument row = JsonDocument.Parse(line);
                JsonElement value = row.RootElement;
                Assert.Equal(Environment.ProcessId, value.GetProperty("processId").GetInt32());
                Assert.True(value.GetProperty("workingSetBytes").GetInt64() > 0);
                Assert.True(value.GetProperty("managedAllocatedBytes").GetInt64() > 0);
                Assert.Equal(JsonValueKind.Null, value.GetProperty("samplingError").ValueKind);
                double elapsed = value.GetProperty("elapsedSeconds").GetDouble();
                Assert.True(elapsed >= previous);
                previous = elapsed;
            }
            byte[] bytes = File.ReadAllBytes(report);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            Assert.Equal(lines.Length, bytes.Count(value => value == (byte)'\r'));
            using FileStream exclusive = File.Open(report, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObservationNeverOverwritesAnExistingReport()
    {
        string directory = NewPath();
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, "capture.process.jsonl");
        File.WriteAllText(report, "retained report");
        try
        {
            Assert.Throws<IOException>(() => DecodeProcessObservation.Start(Command(directory), "1", null));
            Assert.Equal("retained report", File.ReadAllText(report));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "decode observation " + Guid.NewGuid());
    private static ParsedCommand Command(string directory) => new CommandLineParser().Parse(CliSpecs.Vhs,
        ["input.flac", Path.Combine(directory, "capture")]);
}
