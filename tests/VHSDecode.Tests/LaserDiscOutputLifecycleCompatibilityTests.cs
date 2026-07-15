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

    private static DecodeSession CreateSession(string outputBase, params string[] options)
    {
        string[] arguments = [
            "--PAL",
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
}
