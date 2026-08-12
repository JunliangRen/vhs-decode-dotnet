using System.Text.Json.Nodes;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcJsonSnapshotCompatibilityTests
{
    [Fact(DisplayName = "streaming JSON final output remains byte-identical to the compatibility writer")]
    public void StreamingJsonFinalOutputRemainsByteIdenticalToCompatibilityWriter()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "byte-exact");
            string expectedPath = outputBase + ".expected.json";
            string actualPath = outputBase + ".actual.json";
            using DecodeSession session = CreateSession(outputBase);
            TbcDecodedField[] fields =
            [
                BuildField(startSample: 0, detectedFirstField: true),
                BuildField(startSample: 100, detectedFirstField: false)
            ];
            TbcFieldOrderDecision[] decisions =
            [
                BuildDecision(1, true),
                BuildDecision(2, false)
            ];

            TbcOutputMetadataWriter.WriteJson(session, fields, expectedPath, decisions);
            using (var writer = new TbcOutputMetadataWriter.StreamingWriter(session, actualPath))
            {
                writer.Add(fields[0], decisions[0]);
                writer.Add(fields[1], decisions[1]);
                writer.Complete();
            }

            Assert.Equal(File.ReadAllBytes(expectedPath), File.ReadAllBytes(actualPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

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

    [Fact(DisplayName = "JSON snapshot worker failure does not stop later snapshots or finalization")]
    public async Task JsonSnapshotWorkerFailureDoesNotStopLaterSnapshotsOrFinalization()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = CreateTempDirectory();
        using var writeStarted = new ManualResetEventSlim();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "failure.tbc.json");
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "failure"));
            int outputOpenCount = 0;
            using var writer = new TbcOutputMetadataWriter.StreamingWriter(
                session,
                jsonPath,
                path =>
                {
                    if (Interlocked.Increment(ref outputOpenCount) == 1)
                    {
                        writeStarted.Set();
                        throw new IOException("Synthetic snapshot failure.");
                    }

                    return File.Create(path);
                });

            writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));
            writer.WriteSnapshot();
            Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            Assert.True(SpinWait.SpinUntil(
                () => writer.LastSnapshotFailure is not null,
                TimeSpan.FromSeconds(10)));
            Assert.Contains(
                "Synthetic snapshot failure.",
                writer.LastSnapshotFailure?.Message,
                StringComparison.Ordinal);

            writer.Add(BuildField(startSample: 100, detectedFirstField: false), BuildDecision(2, false));
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    writer.WriteSnapshot();
                    return File.Exists(jsonPath);
                },
                TimeSpan.FromSeconds(10)));
            Assert.True(SpinWait.SpinUntil(
                () => writer.LastSnapshotFailure is null,
                TimeSpan.FromSeconds(10)));

            await Task.Run(writer.Complete, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            JsonObject finalSnapshot = ReadJson(jsonPath);
            Assert.Equal(2, finalSnapshot["fields"]?.AsArray().Count);
            Assert.True(outputOpenCount >= 3);
            Assert.False(File.Exists(jsonPath + ".fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = "JSON snapshot publishing retries transient sharing and access failures")]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonSnapshotPublishingRetriesTransientSharingAndAccessFailures(bool unauthorizedAccess)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "retry.tbc.json");
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "retry"));
            int publishAttempts = 0;
            var delays = new List<TimeSpan>();
            using var writer = new TbcOutputMetadataWriter.StreamingWriter(
                session,
                jsonPath,
                publishSnapshot: (source, destination) =>
                {
                    if (Interlocked.Increment(ref publishAttempts) <= 3)
                    {
                        throw unauthorizedAccess
                            ? new UnauthorizedAccessException("Synthetic access failure.")
                            : new IOException("Synthetic sharing violation.");
                    }

                    File.Move(source, destination, overwrite: true);
                },
                delaySnapshotRetry: delays.Add);

            writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));
            writer.Complete();

            Assert.Equal(4, publishAttempts);
            Assert.Equal(
                [
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(2)
                ],
                delays);
            Assert.Single(ReadJson(jsonPath)["fields"]?.AsArray()
                ?? throw new InvalidOperationException("The final snapshot did not contain fields."));
            Assert.False(File.Exists(jsonPath + ".tmp"));
            Assert.False(File.Exists(jsonPath + ".final"));
            Assert.False(File.Exists(jsonPath + ".fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "JSON finalization preserves existing recovery snapshots")]
    public void JsonFinalizationPreservesExistingRecoverySnapshots()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "existing-recovery.tbc.json");
            string firstRecoveryPath = jsonPath + ".final";
            string nextRecoveryPath = jsonPath + ".final.1";
            File.WriteAllText(firstRecoveryPath, "existing recovery");
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "existing-recovery"));
            var writer = new TbcOutputMetadataWriter.StreamingWriter(
                session,
                jsonPath,
                publishSnapshot: (_, _) => throw new IOException("Synthetic persistent sharing violation."),
                delaySnapshotRetry: _ => { });

            writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));

            IOException exception = Assert.ThrowsAny<IOException>(writer.Complete);
            Assert.Contains(nextRecoveryPath, exception.Message, StringComparison.Ordinal);
            Assert.Equal("existing recovery", File.ReadAllText(firstRecoveryPath));
            Assert.Single(ReadJson(nextRecoveryPath)["fields"]?.AsArray()
                ?? throw new InvalidOperationException("The numbered recovery snapshot did not contain fields."));

            writer.Dispose();
            Assert.True(File.Exists(jsonPath + ".fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "JSON finalization failure preserves complete recovery metadata and journal")]
    public void JsonFinalizationFailurePreservesCompleteRecoveryMetadataAndJournal()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "persistent-failure.tbc.json");
            string journalPath = jsonPath + ".fields.tmp";
            string recoveryPath = jsonPath + ".final";
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "persistent-failure"));
            var writer = new TbcOutputMetadataWriter.StreamingWriter(
                session,
                jsonPath,
                publishSnapshot: (_, _) => throw new IOException("Synthetic persistent sharing violation."),
                delaySnapshotRetry: _ => { });

            writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));
            writer.Add(BuildField(startSample: 100, detectedFirstField: false), BuildDecision(2, false));

            IOException exception = Assert.ThrowsAny<IOException>(writer.Complete);
            Assert.Contains(jsonPath, exception.Message, StringComparison.Ordinal);
            Assert.Contains(recoveryPath, exception.Message, StringComparison.Ordinal);
            Assert.Contains(journalPath, exception.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(journalPath));
            Assert.True(new FileInfo(journalPath).Length > 0);
            Assert.False(File.Exists(jsonPath));
            Assert.False(File.Exists(jsonPath + ".tmp"));

            JsonObject recovery = ReadJson(recoveryPath);
            Assert.Equal(2, recovery["fields"]?.AsArray().Count);
            Assert.Equal(
                2,
                recovery["videoParameters"]?["numberOfSequentialFields"]?.GetValue<int>());

            writer.Dispose();
            Assert.True(File.Exists(journalPath));
            Assert.True(File.Exists(recoveryPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Windows JSON sharing violation preserves final metadata without truncation")]
    public void WindowsJsonSharingViolationPreservesFinalMetadataWithoutTruncation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDirectory = CreateTempDirectory();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "shared.tbc.json");
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "shared"));
            var writer = new TbcOutputMetadataWriter.StreamingWriter(
                session,
                jsonPath,
                delaySnapshotRetry: _ => { });

            writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    writer.WriteSnapshot();
                    return File.Exists(jsonPath);
                },
                TimeSpan.FromSeconds(10)));

            using (var heldJson = new FileStream(
                jsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                writer.Add(BuildField(startSample: 100, detectedFirstField: false), BuildDecision(2, false));
                IOException exception = Assert.ThrowsAny<IOException>(writer.Complete);
                Assert.Contains(jsonPath + ".final", exception.Message, StringComparison.Ordinal);
                Assert.Equal(2, ReadJson(jsonPath + ".final")["fields"]?.AsArray().Count);
                Assert.Single(ReadJson(jsonPath)["fields"]?.AsArray()
                    ?? throw new InvalidOperationException("The published checkpoint did not contain fields."));
            }

            writer.Dispose();
            Assert.True(File.Exists(jsonPath + ".fields.tmp"));
            Assert.True(File.Exists(jsonPath + ".final"));
        }
        finally
        {
            DeleteWindowsSharedTempDirectory(tempDirectory);
        }
    }

    [Fact(DisplayName = "JSON output creation failure preserves the append-only field journal")]
    public void JsonOutputCreationFailurePreservesAppendOnlyFieldJournal()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "write-failure.tbc.json");
            string journalPath = jsonPath + ".fields.tmp";
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "write-failure"));
            var writer = new TbcOutputMetadataWriter.StreamingWriter(
                session,
                jsonPath,
                _ => throw new IOException("Synthetic disk-full failure."));

            writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));

            IOException exception = Assert.ThrowsAny<IOException>(writer.Complete);
            Assert.Contains("OUTPUT INCOMPLETE", exception.Message, StringComparison.Ordinal);
            Assert.Contains(journalPath, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(jsonPath + ".final", exception.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(journalPath));
            Assert.False(File.Exists(jsonPath));
            Assert.False(File.Exists(jsonPath + ".final"));

            writer.Dispose();
            Assert.True(File.Exists(journalPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "metadata disposal does not mask the finalization failure")]
    public void MetadataDisposalDoesNotMaskFinalizationFailure()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string jsonPath = Path.Combine(tempDirectory, "dispose-failure.tbc.json");
            using DecodeSession session = CreateSession(Path.Combine(tempDirectory, "dispose-failure"));

            IOException exception = Assert.ThrowsAny<IOException>(() =>
            {
                using var writer = new TbcOutputMetadataWriter.StreamingWriter(
                    session,
                    jsonPath,
                    createFieldsOutput: _ => new DisposeThrowingMemoryStream());
                writer.Add(BuildField(startSample: 0, detectedFirstField: true), BuildDecision(1, true));
                writer.Complete();
            });

            Assert.Contains("OUTPUT INCOMPLETE", exception.Message, StringComparison.Ordinal);
            Assert.Contains(
                "Synthetic field journal close failure.",
                exception.InnerException?.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "metadata finalization failure reports incomplete output after the compatibility completion message")]
    public void MetadataFinalizationFailureReportsIncompleteOutputAfterCompatibilityCompletionMessage()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "engine-failure");
            using DecodeSession session = CreateSession(outputBase);
            using var output = new StringWriter();
            using var error = new StringWriter();
            session.RuntimeReporter = new DecodeRuntimeReporter(output, error);
            int fieldSampleCount = session.TbcFrameSpec.FieldSampleCount;
            TbcDecodedField field = BuildField(startSample: 0, detectedFirstField: true) with
            {
                Samples = new ushort[fieldSampleCount],
                ChromaSamples = new ushort[fieldSampleCount]
            };
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (_, _, _, _, fieldNumber) => fieldNumber == 0 ? field : null)
            {
                CreateMetadataWriter = (decodeSession, path) =>
                    new TbcOutputMetadataWriter.StreamingWriter(
                        decodeSession,
                        path,
                        publishSnapshot: (_, _) =>
                            throw new IOException("Synthetic persistent sharing violation."),
                        delaySnapshotRetry: _ => { })
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success, result.Message);
            Assert.Contains("OUTPUT INCOMPLETE", result.Message, StringComparison.Ordinal);
            Assert.Contains(
                "Completed: saving JSON and exiting.",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.True(File.Exists(outputBase + ".tbc.json.final"));
            Assert.True(File.Exists(outputBase + ".tbc.json.fields.tmp"));
            Assert.Equal(1, ReadJson(outputBase + ".tbc.json.final")["fields"]?.AsArray().Count);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "metadata finalization failure returns a nonzero decode exit code")]
    public void MetadataFinalizationFailureReturnsNonzeroDecodeExitCode()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string inputPath = Path.Combine(tempDirectory, "input.u8");
            string outputBase = Path.Combine(tempDirectory, "runner-failure");
            File.WriteAllBytes(inputPath, [0]);
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
                "--pal",
                inputPath,
                outputBase
            ]);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var runner = new DecodeRunner(
                cancellationToken => new TbcFieldSequenceDecodeEngine(
                    readField: (activeSession, _, _, _, fieldNumber) =>
                        fieldNumber == 0
                            ? BuildField(startSample: 0, detectedFirstField: true) with
                            {
                                Samples = new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                                ChromaSamples = new ushort[activeSession.TbcFrameSpec.FieldSampleCount]
                            }
                            : null,
                    cancellationToken: cancellationToken)
                {
                    CreateMetadataWriter = (decodeSession, path) =>
                        new TbcOutputMetadataWriter.StreamingWriter(
                            decodeSession,
                            path,
                            publishSnapshot: (_, _) =>
                                throw new IOException("Synthetic persistent sharing violation."),
                            delaySnapshotRetry: _ => { })
                });

            int exitCode = runner.Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains("OUTPUT INCOMPLETE", error.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                "Took ",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.True(File.Exists(outputBase + ".tbc.json.final"));
            Assert.True(File.Exists(outputBase + ".tbc.json.fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "payload length mismatch fails completion and preserves metadata journal")]
    public void PayloadLengthMismatchFailsCompletionAndPreservesMetadataJournal()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "truncated-payload");
            using DecodeSession session = CreateSession(outputBase);
            int fieldSampleCount = session.TbcFrameSpec.FieldSampleCount;
            TbcDecodedField field = BuildField(startSample: 0, detectedFirstField: true) with
            {
                Samples = new ushort[fieldSampleCount],
                ChromaSamples = new ushort[fieldSampleCount]
            };
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (_, _, _, _, fieldNumber) => fieldNumber == 0 ? field : null)
            {
                CreateTbcOutput = path => path.EndsWith("_chroma.tbc", StringComparison.OrdinalIgnoreCase)
                    ? new MemoryStream()
                    : new TruncatingMemoryStream()
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success, result.Message);
            Assert.Contains("contains", result.Message, StringComparison.Ordinal);
            Assert.Contains("expected", result.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(outputBase + ".tbc.json.fields.tmp"));
            Assert.Single(ReadJson(outputBase + ".tbc.json")["fields"]?.AsArray()
                ?? throw new InvalidOperationException("The last recovery checkpoint did not contain fields."));
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

    private sealed class TruncatingMemoryStream : MemoryStream
    {
        public override long Length => Math.Max(0, base.Length - sizeof(ushort));

        public override void Write(ReadOnlySpan<byte> buffer)
            => base.Write(buffer[..Math.Max(0, buffer.Length - sizeof(ushort))]);

        public override void Write(byte[] buffer, int offset, int count)
            => base.Write(buffer, offset, Math.Max(0, count - sizeof(ushort)));
    }

    private sealed class DisposeThrowingMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                throw new IOException("Synthetic field journal close failure.");
            }
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteWindowsSharedTempDirectory(string path)
    {
        const int MaximumAttempts = 50;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                OperatingSystem.IsWindows()
                && attempt < MaximumAttempts
                && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
        }
    }
}
