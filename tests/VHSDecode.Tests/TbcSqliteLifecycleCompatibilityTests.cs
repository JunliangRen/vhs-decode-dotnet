using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcSqliteLifecycleCompatibilityTests
{
    [Theory(DisplayName = "VHS/CVBS SQLite creation failures precede video creation and field reads like v0.4.0")]
    [InlineData("vhs")]
    [InlineData("cvbs")]
    public void TapeSqliteCreationFailuresPrecedeVideoAndFieldReadsLikeV040(string decoder)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, decoder + "-db-failure");
            Directory.CreateDirectory(outputBase + ".tbc.db");
            using DecodeSession session = CreateSession(decoder, outputBase);
            int readCalls = 0;
            var engine = new TbcFieldSequenceDecodeEngine(readField: (_, _, _, _, _) =>
            {
                readCalls++;
                return null;
            });

            _ = Assert.Throws<SqliteException>(() => engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1));

            Assert.Equal(0, readCalls);
            Assert.False(File.Exists(outputBase + ".tbc"));
            Assert.False(File.Exists(outputBase + "_chroma.tbc"));
            Assert.False(File.Exists(outputBase + ".tbc.json"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.json.fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = "VHS/CVBS video creation failures retain the earlier SQLite schema like v0.4.0")]
    [InlineData("vhs")]
    [InlineData("cvbs")]
    public void TapeVideoCreationFailuresRetainEarlierSqliteSchemaLikeV040(string decoder)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, decoder + "-video-failure");
            Directory.CreateDirectory(outputBase + ".tbc");
            using DecodeSession session = CreateSession(decoder, outputBase);
            int readCalls = 0;
            var engine = new TbcFieldSequenceDecodeEngine(readField: (_, _, _, _, _) =>
            {
                readCalls++;
                return null;
            });

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success);
            Assert.Equal(0, readCalls);
            Assert.True(Directory.Exists(outputBase + ".tbc"));
            Assert.Equal(0, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM capture"));
            Assert.Equal(0, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
            Assert.False(File.Exists(outputBase + "_chroma.tbc"));
            Assert.False(File.Exists(outputBase + ".tbc.json"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.json.fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "VHS chroma creation failures retain earlier SQLite and video artifacts like v0.4.0")]
    public void VhsChromaCreationFailuresRetainEarlierSqliteAndVideoLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "vhs-chroma-failure");
            Directory.CreateDirectory(outputBase + "_chroma.tbc");
            using DecodeSession session = CreateSession("vhs", outputBase);
            int readCalls = 0;
            var engine = new TbcFieldSequenceDecodeEngine(readField: (_, _, _, _, _) =>
            {
                readCalls++;
                return null;
            });

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success);
            Assert.Equal(0, readCalls);
            Assert.Equal(0, new FileInfo(outputBase + ".tbc").Length);
            Assert.True(Directory.Exists(outputBase + "_chroma.tbc"));
            Assert.Equal(0, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM capture"));
            Assert.Equal(0, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
            Assert.False(File.Exists(outputBase + ".tbc.json"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.json.fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD SQLite creation failures retain earlier video and PCM artifacts like v0.4.0")]
    public void LdSqliteCreationFailuresRetainEarlierVideoAndPcmLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "ld-db-failure");
            Directory.CreateDirectory(outputBase + ".tbc.db");
            ParsedCommand parsed = new CommandLineParser().Parse(CliSpecs.LaserDisc, [
                "--PAL",
                "--noEFM",
                "input.s16",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(parsed);
            int readCalls = 0;
            var engine = new TbcFieldSequenceDecodeEngine(readField: (_, _, _, _, _) =>
            {
                readCalls++;
                return null;
            });

            _ = Assert.Throws<SqliteException>(() => engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1));

            Assert.Equal(0, readCalls);
            Assert.Equal(0, new FileInfo(outputBase + ".tbc").Length);
            Assert.Equal(0, new FileInfo(outputBase + ".pcm").Length);
            Assert.False(File.Exists(outputBase + ".tbc.json"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.json.fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD main TBC write failures retain first-field metadata like v0.4.0")]
    public void LdMainTbcWriteFailuresRetainFirstFieldMetadataLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "ld-video-write-failure");
            using DecodeSession session = CreateSession("ld", outputBase);
            TbcDecodedField field = BuildField(0, detectedFirstField: true) with
            {
                Samples = new ushort[session.TbcFrameSpec.FieldSampleCount],
                NextFieldOffsetSamples = 100,
                NominalFieldLengthSamples = 100
            };
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (_, _, _, _, fieldNumber) => fieldNumber == 0 ? field : null)
            {
                CreateTbcOutput = _ => new ThrowingWriteStream("synthetic main TBC failure")
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success);
            Assert.Contains("synthetic main TBC failure", result.Message, StringComparison.Ordinal);
            JsonNode document = JsonNode.Parse(File.ReadAllText(outputBase + ".tbc.json"))
                ?? throw new InvalidOperationException("Partial metadata JSON was empty.");
            Assert.Single(document["fields"]?.AsArray()
                ?? throw new InvalidOperationException("Partial metadata JSON did not contain fields."));
            Assert.Equal(1, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.json.fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "VHS chroma write failures retain first-field metadata like v0.4.0")]
    public void VhsChromaWriteFailuresRetainFirstFieldMetadataLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "vhs-chroma-write-failure");
            using DecodeSession session = CreateSession("vhs", outputBase);
            TbcDecodedField field = BuildField(0, detectedFirstField: true) with
            {
                Samples = new ushort[session.TbcFrameSpec.FieldSampleCount],
                ChromaSamples = new ushort[session.TbcFrameSpec.FieldSampleCount],
                NextFieldOffsetSamples = 100,
                NominalFieldLengthSamples = 100
            };
            TrackingWriteStream? video = null;
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (_, _, _, _, fieldNumber) => fieldNumber == 0 ? field : null)
            {
                CreateTbcOutput = path => path.EndsWith("_chroma.tbc", StringComparison.OrdinalIgnoreCase)
                    ? new ThrowingWriteStream("synthetic chroma TBC failure")
                    : video = new TrackingWriteStream()
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success);
            Assert.Contains("synthetic chroma TBC failure", result.Message, StringComparison.Ordinal);
            Assert.NotNull(video);
            Assert.Equal(
                session.TbcFrameSpec.FieldSampleCount * (long)sizeof(ushort),
                video.BytesWritten);
            JsonNode document = JsonNode.Parse(File.ReadAllText(outputBase + ".tbc.json"))
                ?? throw new InvalidOperationException("Partial metadata JSON was empty.");
            Assert.Single(document["fields"]?.AsArray()
                ?? throw new InvalidOperationException("Partial metadata JSON did not contain fields."));
            Assert.Equal(1, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.json.fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "VHS zero-field JSON finalizes before chroma and video close like v0.4.0")]
    public void VhsZeroFieldJsonFinalizesBeforeChromaAndVideoCloseLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "vhs-close-order");
            using DecodeSession session = CreateSession("vhs", outputBase);
            var closeOrder = new List<string>();
            var metadataReady = new List<bool>();
            var engine = new TbcFieldSequenceDecodeEngine(readField: (_, _, _, _, _) => null)
            {
                CreateTbcOutput = path => new DisposeTrackingStream(
                    path.EndsWith("_chroma.tbc", StringComparison.OrdinalIgnoreCase) ? "chroma" : "video",
                    label =>
                    {
                        closeOrder.Add(label);
                        metadataReady.Add(File.Exists(outputBase + ".tbc.json.tmp"));
                    })
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.True(result.Success);
            Assert.Equal(["chroma", "video"], closeOrder);
            Assert.All(metadataReady, Assert.True);
            Assert.Equal("{", File.ReadAllText(outputBase + ".tbc.json.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = "Zero-field metadata artifacts match v0.4.0")]
    [InlineData("vhs")]
    [InlineData("cvbs")]
    [InlineData("ld")]
    public void ZeroFieldMetadataArtifactsMatchV040(string decoder)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, decoder);
            using DecodeSession session = CreateSession(decoder, outputBase);

            TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine()
                .WriteDecodedFields(session, []);

            Assert.True(result.Success);
            Assert.Equal(0, result.WrittenFieldCount);
            Assert.Equal(0, new FileInfo(result.Paths!.TbcPath).Length);
            Assert.False(File.Exists(result.Paths.JsonPath));
            Assert.Equal("{", File.ReadAllText(result.Paths.JsonPath + ".tmp"));
            Assert.NotNull(result.Paths.DbPath);
            Assert.Equal(0, QueryLong(result.Paths.DbPath!, "SELECT COUNT(*) FROM capture"));
            Assert.Equal(0, QueryLong(result.Paths.DbPath!, "SELECT COUNT(*) FROM field_record"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "SQLite capture count commits with every field like v0.4.0")]
    public void SqliteCaptureCountCommitsWithEveryFieldLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "capture.tbc.db");
            using DecodeSession session = CreateSession("vhs", Path.Combine(tempDirectory, "capture"));
            var builder = new TbcOutputMetadataWriter.FieldObjectBuilder(session);
            using var writer = new TbcSqliteMetadataWriter.SequenceWriter(session, databasePath);

            writer.Add(
                builder.Add(BuildField(0, true), BuildDecision(1, true)),
                fieldCount: 1,
                converter: null);
            Assert.Equal(1, QueryLong(databasePath, "SELECT number_of_sequential_fields FROM capture"));
            Assert.Equal(1, QueryLong(databasePath, "SELECT COUNT(*) FROM field_record"));

            writer.Add(
                builder.Add(BuildField(1, false), BuildDecision(2, false)),
                fieldCount: 2,
                converter: null);
            Assert.Equal(2, QueryLong(databasePath, "SELECT number_of_sequential_fields FROM capture"));
            Assert.Equal(2, QueryLong(databasePath, "SELECT COUNT(*) FROM field_record"));

            writer.Complete(fieldCount: 2);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Failure-path SQLite normalization errors do not escape Dispose")]
    public void FailurePathSqliteNormalizationErrorsDoNotEscapeDispose()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "cleanup.tbc.db");
            using DecodeSession session = CreateSession("vhs", Path.Combine(tempDirectory, "cleanup"));
            int normalizationCalls = 0;
            var writer = new TbcSqliteMetadataWriter.SequenceWriter(
                session,
                databasePath,
                _ =>
                {
                    normalizationCalls++;
                    throw new IOException("synthetic header normalization failure");
                });

            writer.Dispose();

            Assert.Equal(1, normalizationCalls);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "SQLite completion surfaces normalization errors directly")]
    public void SqliteCompletionSurfacesNormalizationErrorsDirectly()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "completion.tbc.db");
            using DecodeSession session = CreateSession("vhs", Path.Combine(tempDirectory, "completion"));
            int normalizationCalls = 0;
            var writer = new TbcSqliteMetadataWriter.SequenceWriter(
                session,
                databasePath,
                _ =>
                {
                    normalizationCalls++;
                    throw new IOException("synthetic header normalization failure");
                });

            IOException exception = Assert.Throws<IOException>(() => writer.Complete(fieldCount: 0));
            Assert.Equal("synthetic header normalization failure", exception.Message);
            writer.Dispose();
            Assert.Equal(1, normalizationCalls);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "First SQLite capture header commits before its field row like v0.4.0")]
    public void FirstSqliteCaptureHeaderCommitsBeforeFieldRowLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "capture.tbc.db");
            using DecodeSession session = CreateSession("vhs", Path.Combine(tempDirectory, "capture"));
            using (var writer = new TbcSqliteMetadataWriter.SequenceWriter(session, databasePath))
            {
                Assert.Throws<InvalidOperationException>(() => writer.Add(
                    new JsonObject(),
                    fieldCount: 1,
                    converter: null));
            }

            Assert.Equal(1, QueryLong(databasePath, "SELECT COUNT(*) FROM capture"));
            Assert.Equal(1, QueryLong(databasePath, "SELECT number_of_sequential_fields FROM capture"));
            Assert.Equal(0, QueryLong(databasePath, "SELECT COUNT(*) FROM field_record"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Output failures finalize previously committed metadata like v0.4.0")]
    public void OutputFailuresFinalizePreviouslyCommittedMetadataLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "partial");
            using DecodeSession session = CreateSession("ld", outputBase);
            int reads = 0;
            TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int ___)
            {
                reads++;
                if (reads > 2)
                {
                    return null;
                }

                return BuildField(begin, detectedFirstField: reads == 1) with
                {
                    Samples = new ushort[activeSession.TbcFrameSpec.FieldSampleCount],
                    NextFieldOffsetSamples = 100,
                    NominalFieldLengthSamples = 100
                };
            }

            var engine = new TbcFieldSequenceDecodeEngine(
                efmOutputWriter: new ThrowOnSecondFieldOutputWriter(),
                readField: ReadField);

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 2);

            Assert.False(result.Success);
            Assert.Contains("synthetic output failure", result.Message, StringComparison.Ordinal);
            JsonNode document = JsonNode.Parse(File.ReadAllText(outputBase + ".tbc.json"))
                ?? throw new InvalidOperationException("Partial metadata JSON was empty.");
            Assert.Single(document["fields"]?.AsArray()
                ?? throw new InvalidOperationException("Partial metadata JSON did not contain fields."));
            Assert.Equal(1, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
            Assert.Equal(
                1,
                QueryLong(outputBase + ".tbc.db", "SELECT number_of_sequential_fields FROM capture"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.json.fields.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "CVBS completion message precedes payload close like v0.4.0")]
    public void CvbsCompletionMessagePrecedesPayloadCloseLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "cvbs-completion-order");
            using DecodeSession session = CreateSession("cvbs", outputBase);
            var output = new StringWriter();
            var error = new StringWriter();
            session.RuntimeReporter = new DecodeRuntimeReporter(output, error);
            int payloadsClosed = 0;
            var engine = new TbcFieldSequenceDecodeEngine(readField: (_, _, _, _, _) => null)
            {
                CreateTbcOutput = _ => new DisposeTrackingStream("cvbs", _ =>
                {
                    payloadsClosed++;
                    Assert.Contains("saving JSON and exiting", output.ToString(), StringComparison.Ordinal);
                })
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.True(result.Success);
            Assert.True(payloadsClosed > 0);
            string completionOutput = output.ToString();
            Assert.Equal("saving JSON and exiting" + Environment.NewLine, completionOutput);
            session.RuntimeReporter.WriteCvbsCompletion(session.TbcRenderer.CvbsAgcStatistics);
            Assert.Equal(completionOutput, output.ToString());
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static DecodeSession CreateSession(string decoder, string outputBase)
    {
        (DecodeCommandSpec Spec, string[] Arguments) command = decoder switch
        {
            "vhs" => (CliSpecs.Vhs, ["--pal", "--write_db", "input.u8", outputBase]),
            "cvbs" => (CliSpecs.Cvbs, ["--pal", "--write_db", "input.u8", outputBase]),
            "ld" => (CliSpecs.LaserDisc, [
                "--PAL",
                "--noEFM",
                "--disable_analog_audio",
                "input.s16",
                outputBase
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(decoder), decoder, "Unknown decoder.")
        };
        ParsedCommand parsed = new CommandLineParser().Parse(command.Spec, command.Arguments);
        return DecodeSessionFactory.Create(parsed);
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

    private static long QueryLong(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar()
            ?? throw new InvalidOperationException("SQLite query did not return a value."));
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private class TrackingWriteStream : MemoryStream
    {
        public long BytesWritten { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            BytesWritten += count;
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BytesWritten += buffer.Length;
            base.Write(buffer);
        }
    }

    private sealed class ThrowingWriteStream(string message) : TrackingWriteStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new IOException(message);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            throw new IOException(message);
        }
    }

    private sealed class DisposeTrackingStream(string label, Action<string> onDispose) : MemoryStream
    {
        private bool _disposed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                onDispose(label);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowOnSecondFieldOutputWriter : ILaserDiscEfmOutputWriter
    {
        public IReadOnlyList<TbcDecodedField> Write(
            DecodeSession session,
            IReadOnlyList<TbcDecodedField> fields)
        {
            throw new NotSupportedException("The batch path is not used by this test.");
        }

        public ILaserDiscFieldOutputSession Open(DecodeSession session)
        {
            return new OutputSession();
        }

        private sealed class OutputSession : ILaserDiscFieldOutputSession
        {
            private int _fieldCount;

            public TbcDecodedField Write(TbcDecodedField field)
            {
                _fieldCount++;
                return _fieldCount == 2
                    ? throw new IOException("synthetic output failure")
                    : field;
            }

            public void Dispose()
            {
            }
        }
    }
}
