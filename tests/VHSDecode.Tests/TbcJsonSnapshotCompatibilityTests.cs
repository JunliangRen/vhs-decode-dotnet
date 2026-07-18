using System.Text.Json.Nodes;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcJsonSnapshotCompatibilityTests
{
    [Fact(DisplayName = "JSON recovery snapshots skip while busy and close queues the final state like v0.4.0")]
    public async Task JsonRecoverySnapshotsSkipWhileBusyAndCloseQueuesFinalStateLikeV040()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = CreateTempDirectory();
        using var firstWriteStarted = new ManualResetEventSlim();
        using var releaseFirstWrite = new ManualResetEventSlim();
        using var finalWriteStarted = new ManualResetEventSlim();
        using var releaseFinalWrite = new ManualResetEventSlim();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "capture.tbc.json");
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "capture"));
            int outputOpenCount = 0;
            Stream CreateSnapshotOutput(string path)
            {
                int current = Interlocked.Increment(ref outputOpenCount);
                ManualResetEventSlim started = current == 1 ? firstWriteStarted : finalWriteStarted;
                ManualResetEventSlim release = current == 1 ? releaseFirstWrite : releaseFinalWrite;
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                {
                    throw new TimeoutException("The JSON snapshot test did not release the background writer.");
                }

                return File.Create(path);
            }

            var writer = new TbcOutputMetadataWriter.StreamingWriter(
                session,
                jsonPath,
                CreateSnapshotOutput);
            Task? completion = null;
            try
            {
                writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));

                Task checkpoint = Task.Factory.StartNew(
                    writer.WriteSnapshot,
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                Assert.True(firstWriteStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
                await checkpoint.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

                writer.Add(BuildField(startSample: 100, detectedFirstField: false), BuildDecision(2, false));
                writer.WriteSnapshot();
                completion = Task.Factory.StartNew(
                    writer.Complete,
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                releaseFirstWrite.Set();
                Assert.True(finalWriteStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
                JsonObject recoverySnapshot = ReadJson(jsonPath);
                Assert.Single(recoverySnapshot["fields"]?.AsArray()
                    ?? throw new InvalidOperationException("The recovery snapshot did not contain fields."));
                Assert.False(completion.IsCompleted);

                releaseFinalWrite.Set();
                await completion.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

                Assert.Equal(2, outputOpenCount);
                JsonObject finalSnapshot = ReadJson(jsonPath);
                Assert.Equal(
                    2,
                    finalSnapshot["fields"]?.AsArray().Count
                        ?? throw new InvalidOperationException("The final snapshot did not contain fields."));
                Assert.Equal(
                    2,
                    finalSnapshot["videoParameters"]?["numberOfSequentialFields"]?.GetValue<int>());
                Assert.False(File.Exists(jsonPath + ".tmp"));
                Assert.False(File.Exists(jsonPath + ".fields.tmp"));
            }
            finally
            {
                releaseFirstWrite.Set();
                releaseFinalWrite.Set();
                if (completion is not null)
                {
                    try
                    {
                        await completion.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                    }
                    catch
                    {
                        // Preserve the assertion or timeout that ended the test.
                    }
                }

                writer.Dispose();
            }
        }
        finally
        {
            releaseFirstWrite.Set();
            releaseFinalWrite.Set();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "JSON snapshot worker failure does not deadlock finalization")]
    public async Task JsonSnapshotWorkerFailureDoesNotDeadlockFinalization()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = CreateTempDirectory();
        using var writeStarted = new ManualResetEventSlim();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "failure.tbc.json");
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "failure"));
            using var writer = new TbcOutputMetadataWriter.StreamingWriter(
                session,
                jsonPath,
                _ =>
                {
                    writeStarted.Set();
                    throw new IOException("Synthetic snapshot failure.");
                });

            writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));
            writer.WriteSnapshot();
            Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));

            await Task.Run(writer.Complete, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            Assert.False(File.Exists(jsonPath + ".fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static DecodeSession CreateSession(string outputBase)
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
            "--pal",
            "input.u8",
            outputBase
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static TbcDecodedField BuildField(long startSample, bool detectedFirstField)
    {
        return new TbcDecodedField(
            StartSample: startSample,
            Samples: [],
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
            DetectedFirstField: detectedFirstField,
            DetectedFirstFieldConfidence: 100);
    }

    private static TbcFieldOrderDecision BuildDecision(int seqNo, bool isFirstField)
    {
        return new TbcFieldOrderDecision(
            SeqNo: seqNo,
            IsFirstField: isFirstField,
            DetectedFirstField: isFirstField,
            IsDuplicateField: false,
            WriteField: true,
            SyncConfidence: 100,
            DecodeFaults: 0);
    }

    private static JsonObject ReadJson(string path)
        => JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"JSON snapshot {path} was empty.");

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
