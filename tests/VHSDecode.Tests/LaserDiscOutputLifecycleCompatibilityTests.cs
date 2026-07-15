using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscOutputLifecycleCompatibilityTests
{
    [Fact(DisplayName = "LD EFM failures occur before metadata and main TBC like v0.4.0")]
    public void LdEfmFailuresOccurBeforeMetadataAndMainTbcLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "efm-failure");
            using DecodeSession session = CreateSession(outputBase, "--disable_analog_audio");
            TbcDecodedField field = BuildField(session) with
            {
                Efm = BuildEfmSquareWave(2048)
            };
            var engine = new TbcFieldSequenceDecodeEngine(
                efmOutputWriter: new LaserDiscEfmOutputWriter(path =>
                    path.EndsWith(".efm", StringComparison.OrdinalIgnoreCase)
                        ? new ThrowingWriteStream("synthetic EFM failure")
                        : new TrackingWriteStream()),
                readField: OneFieldReader(field));

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success);
            Assert.Contains("synthetic EFM failure", result.Message, StringComparison.Ordinal);
            Assert.Equal(0, new FileInfo(outputBase + ".tbc").Length);
            Assert.False(File.Exists(outputBase + ".tbc.json"));
            Assert.Equal("{", File.ReadAllText(outputBase + ".tbc.json.tmp"));
            Assert.Equal(0, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM capture"));
            Assert.Equal(0, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD PCM failures occur after metadata and main TBC like v0.4.0")]
    public void LdPcmFailuresOccurAfterMetadataAndMainTbcLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "pcm-failure");
            using DecodeSession session = CreateSession(outputBase, "--noEFM");
            TbcDecodedField field = BuildField(session) with
            {
                AudioPcm = [100, -100, 200, -200]
            };
            var engine = new TbcFieldSequenceDecodeEngine(
                efmOutputWriter: new LaserDiscEfmOutputWriter(path =>
                    path.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase)
                        ? new ThrowingWriteStream("synthetic PCM failure")
                        : new TrackingWriteStream()),
                readField: OneFieldReader(field));

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success);
            Assert.Contains("synthetic PCM failure", result.Message, StringComparison.Ordinal);
            Assert.Equal(session.TbcFrameSpec.FieldSampleCount * (long)sizeof(ushort), new FileInfo(outputBase + ".tbc").Length);
            JsonObject metadataField = ReadOnlyMetadataField(outputBase);
            Assert.Equal(2, metadataField["audioSamples"]?.GetValue<int>());
            Assert.Equal(1, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
            Assert.Equal(2, QueryLong(outputBase + ".tbc.db", "SELECT audio_samples FROM field_record"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD RF TBC failures precede PCM writes like v0.4.0")]
    public void LdRfTbcFailuresPrecedePcmWritesLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "rf-failure");
            using DecodeSession session = CreateSession(outputBase, "--noEFM", "--RF_TBC");
            TbcDecodedField field = BuildField(session) with
            {
                AudioPcm = [100, -100],
                RfTbc = [1, -2, 3, -4]
            };
            TrackingWriteStream? pcm = null;
            var engine = new TbcFieldSequenceDecodeEngine(
                efmOutputWriter: new LaserDiscEfmOutputWriter(path =>
                {
                    if (path.EndsWith(".tbc.ldf", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ThrowingWriteStream("synthetic RF TBC failure");
                    }

                    pcm = new TrackingWriteStream();
                    return pcm;
                }),
                readField: OneFieldReader(field));

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success);
            Assert.Contains("synthetic RF TBC failure", result.Message, StringComparison.Ordinal);
            Assert.NotNull(pcm);
            Assert.Equal(0, pcm.BytesWritten);
            Assert.Equal(session.TbcFrameSpec.FieldSampleCount * (long)sizeof(ushort), new FileInfo(outputBase + ".tbc").Length);
            Assert.Single(ReadMetadataFields(outputBase));
            Assert.Equal(1, QueryLong(outputBase + ".tbc.db", "SELECT COUNT(*) FROM field_record"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD missing analog audio writes zero metadata like v0.4.0")]
    public void LdMissingAnalogAudioWritesZeroMetadataLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "missing-audio");
            using DecodeSession session = CreateSession(outputBase, "--noEFM");
            TbcDecodedField field = BuildField(session) with
            {
                AudioPcm = null,
                AudioSampleCount = 17,
                EfmTValueCount = 19
            };
            TrackingWriteStream? pcm = null;
            var engine = new TbcFieldSequenceDecodeEngine(
                efmOutputWriter: new LaserDiscEfmOutputWriter(_ => pcm = new TrackingWriteStream()),
                readField: OneFieldReader(field));

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.True(result.Success);
            Assert.NotNull(pcm);
            Assert.Equal(0, pcm.BytesWritten);
            JsonObject metadataField = ReadOnlyMetadataField(outputBase);
            Assert.Equal(0, metadataField["audioSamples"]?.GetValue<int>());
            Assert.Equal(0, metadataField["efmTValues"]?.GetValue<int>());
            Assert.Equal(0, QueryLong(outputBase + ".tbc.db", "SELECT audio_samples FROM field_record"));
            Assert.Equal(0, QueryLong(outputBase + ".tbc.db", "SELECT efm_t_values FROM field_record"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD sidecar creation order matches v0.4.0")]
    public void LdSidecarCreationOrderMatchesV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "creation-order");
            using DecodeSession session = CreateNtscSession(
                outputBase,
                "--preEFM",
                "--RF_TBC",
                "--AC3");
            var createdPaths = new List<string>();
            var writer = new LaserDiscEfmOutputWriter(path =>
            {
                createdPaths.Add(path);
                return new TrackingWriteStream();
            });

            using ILaserDiscFieldOutputSession output = writer.Open(session);

            Assert.Equal(
                [
                    outputBase + ".pcm",
                    outputBase + ".efm",
                    outputBase + ".prefm",
                    outputBase + ".tbc.ldf",
                    outputBase + ".ac3"
                ],
                createdPaths);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD zero-field JSON finalizes before payload close in v0.4.0 order")]
    public void LdZeroFieldJsonFinalizesBeforePayloadCloseInV040Order()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "close-order");
            using DecodeSession session = CreateNtscSession(
                outputBase,
                "--RF_TBC",
                "--AC3");
            var closeOrder = new List<string>();
            var metadataReady = new List<bool>();
            void RecordClose(string label)
            {
                closeOrder.Add(label);
                metadataReady.Add(File.Exists(outputBase + ".tbc.json.tmp"));
            }

            var writer = new LaserDiscEfmOutputWriter(path => new DisposeTrackingStream(
                path.EndsWith(".tbc.ldf", StringComparison.OrdinalIgnoreCase)
                    ? "rf"
                    : Path.GetExtension(path).TrimStart('.'),
                RecordClose));
            var engine = new TbcFieldSequenceDecodeEngine(
                efmOutputWriter: writer,
                readField: (_, _, _, _, _) => null)
            {
                CreateTbcOutput = _ => new DisposeTrackingStream("video", RecordClose)
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.True(result.Success);
            Assert.Equal(["video", "pcm", "efm", "rf", "ac3"], closeOrder);
            Assert.All(metadataReady, Assert.True);
            Assert.Equal("{", File.ReadAllText(outputBase + ".tbc.json.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD EFM creation failures retain earlier PCM artifact like v0.4.0")]
    public void LdEfmCreationFailuresRetainEarlierPcmArtifactLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "efm-open-failure");
            using DecodeSession session = CreateSession(outputBase);
            TbcDecodedField field = BuildField(session) with
            {
                AudioPcm = [100, -100],
                Efm = BuildEfmSquareWave(2048)
            };
            int readCalls = 0;
            var engine = new TbcFieldSequenceDecodeEngine(
                efmOutputWriter: new LaserDiscEfmOutputWriter(path =>
                {
                    if (path.EndsWith(".efm", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("synthetic EFM creation failure");
                    }

                    return File.Create(path);
                }),
                readField: (_, _, _, _, fieldNumber) =>
                {
                    readCalls++;
                    return fieldNumber == 0 ? field : null;
                });

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.False(result.Success);
            Assert.Contains("synthetic EFM creation failure", result.Message, StringComparison.Ordinal);
            Assert.Equal(0, readCalls);
            Assert.Equal(0, new FileInfo(outputBase + ".tbc").Length);
            Assert.Equal(0, new FileInfo(outputBase + ".pcm").Length);
            Assert.False(File.Exists(outputBase + ".efm"));
            Assert.False(File.Exists(outputBase + ".tbc.json"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.json.fields.tmp"));
            Assert.False(File.Exists(outputBase + ".tbc.db"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD test LDF starts at the pre-decode offset after initial recovery like v0.4.0")]
    public void LdTestLdfStartsBeforeInitialRecoveryLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "ldf-initial-recovery");
            using DecodeSession session = CreateSession(
                outputBase,
                "--disable_analog_audio",
                "--noEFM",
                "--start_fileloc",
                "100",
                "--write-test-ldf",
                Path.Combine(tempDirectory, "initial-recovery.ldf"));
            var testLdfWriter = new RecordingLdTestLdfWriter();
            int attempts = 0;
            var engine = new TbcFieldSequenceDecodeEngine(
                testLdfWriter: testLdfWriter,
                readField: (activeSession, _, begin, _, _) => ++attempts == 1
                    ? throw new TbcFieldDecodeRecoveryException(
                        TbcFieldDecodeRecoveryKind.NoFirstHSync,
                        50,
                        "synthetic initial recovery")
                    : BuildField(activeSession) with { StartSample = begin })
            {
                CreateTbcOutput = _ => new MemoryStream()
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.True(result.Success);
            Assert.Equal(2, attempts);
            (long startSample, long endSample) = Assert.Single(testLdfWriter.Ranges);
            Assert.Equal(100, startSample);
            Assert.Equal(1_100_250, endSample);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD test LDF ends at the final recovery offset like v0.4.0")]
    public void LdTestLdfEndsAfterFinalRecoveryLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "ldf-final-recovery");
            using DecodeSession session = CreateSession(
                outputBase,
                "--disable_analog_audio",
                "--noEFM",
                "--start_fileloc",
                "100",
                "--write-test-ldf",
                Path.Combine(tempDirectory, "final-recovery.ldf"));
            var testLdfWriter = new RecordingLdTestLdfWriter();
            int attempts = 0;
            var engine = new TbcFieldSequenceDecodeEngine(
                testLdfWriter: testLdfWriter,
                readField: (activeSession, _, begin, _, _) => ++attempts switch
                {
                    1 => BuildField(activeSession) with { StartSample = begin },
                    2 => throw new TbcFieldDecodeRecoveryException(
                        TbcFieldDecodeRecoveryKind.InsufficientData,
                        25,
                        "synthetic final recovery"),
                    _ => null
                })
            {
                CreateTbcOutput = _ => new MemoryStream()
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(session, Stream.Null);

            Assert.True(result.Success);
            Assert.Equal(3, attempts);
            (long startSample, long endSample) = Assert.Single(testLdfWriter.Ranges);
            Assert.Equal(100, startSample);
            Assert.Equal(1_100_225, endSample);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD zero-length test LDF still resolves seek like v0.4.0")]
    public void LdZeroLengthTestLdfStillResolvesSeekLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "ldf-zero-length-seek");
            using DecodeSession session = CreateNtscSession(
                outputBase,
                "--disable_analog_audio",
                "--noEFM",
                "--seek",
                "12",
                "--length",
                "0",
                "--write-test-ldf",
                Path.Combine(tempDirectory, "zero-length-seek.ldf"));
            long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
            long initialProbe = 12L * 2L * nominalFieldSamples;
            long targetProbe = initialProbe + (3L * nominalFieldSamples);
            var readBegins = new List<long>();
            var testLdfWriter = new RecordingLdTestLdfWriter();
            var engine = new TbcFieldSequenceDecodeEngine(
                testLdfWriter: testLdfWriter,
                readField: (activeSession, _, begin, _, fieldNumber) =>
                {
                    readBegins.Add(begin);
                    int frameNumber = begin >= targetProbe ? 12 : 10;
                    return BuildField(activeSession) with
                    {
                        StartSample = begin,
                        DetectedFirstField = fieldNumber == 0,
                        NextFieldOffsetSamples = nominalFieldSamples,
                        VbiData = fieldNumber == 1 ? [EncodeCavFrameCode(frameNumber)] : []
                    };
                })
            {
                CreateTbcOutput = _ => new MemoryStream()
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(session, Stream.Null);

            Assert.True(result.Success);
            Assert.Equal(
                [initialProbe, initialProbe + nominalFieldSamples, targetProbe, targetProbe + nominalFieldSamples],
                readBegins);
            (long startSample, long endSample) = Assert.Single(testLdfWriter.Ranges);
            Assert.Equal(targetProbe, startSample);
            Assert.Equal(targetProbe + 1_100_000, endSample);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "LD test LDF includes the lead-out field that stops decoding like v0.4.0")]
    public void LdTestLdfIncludesStoppingLeadOutFieldLikeV040()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "ldf-lead-out");
            using DecodeSession session = CreateNtscSession(
                outputBase,
                "--disable_analog_audio",
                "--noEFM",
                "--start_fileloc",
                "100",
                "--write-test-ldf",
                Path.Combine(tempDirectory, "lead-out.ldf"));
            var testLdfWriter = new RecordingLdTestLdfWriter();
            int reads = 0;
            var engine = new TbcFieldSequenceDecodeEngine(
                testLdfWriter: testLdfWriter,
                readField: (activeSession, _, begin, _, fieldNumber) =>
                {
                    reads++;
                    return BuildField(activeSession) with
                    {
                        StartSample = begin,
                        DetectedFirstField = fieldNumber == 0,
                        VbiData = [0x80EEEE]
                    };
                })
            {
                CreateTbcOutput = _ => new MemoryStream()
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(session, Stream.Null);

            Assert.True(result.Success);
            Assert.Equal(2, reads);
            (long startSample, long endSample) = Assert.Single(testLdfWriter.Ranges);
            Assert.Equal(100, startSample);
            Assert.Equal(1_100_300, endSample);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static DecodeSession CreateSession(string outputBase, params string[] options)
        => CreateSessionForSystem(outputBase, "--PAL", options);

    private static DecodeSession CreateNtscSession(string outputBase, params string[] options)
        => CreateSessionForSystem(outputBase, "--NTSC", options);

    private static DecodeSession CreateSessionForSystem(
        string outputBase,
        string systemOption,
        params string[] options)
    {
        string[] arguments = [
            systemOption,
            "--threads",
            "0",
            .. options,
            "input.s16",
            outputBase
        ];
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc, arguments);
        return DecodeSessionFactory.Create(command);
    }

    private static TbcDecodedField BuildField(DecodeSession session)
    {
        return new TbcDecodedField(
            StartSample: 0,
            Samples: new ushort[session.TbcFrameSpec.FieldSampleCount],
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
            DetectedFirstField: true,
            DetectedFirstFieldConfidence: 100,
            NextFieldOffsetSamples: 100,
            NominalFieldLengthSamples: 100);
    }

    private static TbcFieldSequenceReadField OneFieldReader(TbcDecodedField field)
    {
        return (_, _, _, _, fieldNumber) => fieldNumber == 0 ? field : null;
    }

    private static short[] BuildEfmSquareWave(int length)
    {
        var samples = new short[length];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(((i / 9) & 1) == 0 ? 12_000 : -12_000);
        }

        return samples;
    }

    private static int EncodeCavFrameCode(int frameNumber)
    {
        int value = frameNumber;
        int bcd = 0;
        int shift = 0;
        do
        {
            bcd |= (value % 10) << shift;
            value /= 10;
            shift += 4;
        }
        while (value > 0);

        return 0xF00000 | bcd;
    }

    private static JsonObject ReadOnlyMetadataField(string outputBase)
    {
        JsonArray fields = ReadMetadataFields(outputBase);
        return Assert.IsType<JsonObject>(Assert.Single(fields));
    }

    private static JsonArray ReadMetadataFields(string outputBase)
    {
        JsonNode document = JsonNode.Parse(File.ReadAllText(outputBase + ".tbc.json"))
            ?? throw new InvalidOperationException("Metadata JSON was empty.");
        return document["fields"]?.AsArray()
            ?? throw new InvalidOperationException("Metadata JSON did not contain fields.");
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
    }

    private sealed class ThrowingWriteStream(string message) : TrackingWriteStream
    {
        public override void Write(byte[] buffer, int offset, int count)
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

    private sealed class RecordingLdTestLdfWriter : ILdTestLdfWriter
    {
        public List<(long StartSample, long EndSample)> Ranges { get; } = [];

        public LdTestLdfWriteResult Write(
            DecodeSession session,
            long startSample,
            long endSample,
            Stream input)
        {
            Ranges.Add((startSample, endSample));
            return new LdTestLdfWriteResult(
                true,
                "recorded LD test LDF range",
                endSample - startSample,
                startSample,
                endSample,
                session.TestLdfOutputPath);
        }
    }
}
