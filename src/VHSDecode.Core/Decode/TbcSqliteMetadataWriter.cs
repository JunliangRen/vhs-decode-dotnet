using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Decode;

public static class TbcSqliteMetadataWriter
{
    private const int UpstreamSqliteVersionNumber = 3_050_004;

    private static readonly string[] SchemaStatements =
    [
        "PRAGMA user_version = 1",
        """
        CREATE TABLE capture (
            capture_id INTEGER PRIMARY KEY,
            system TEXT NOT NULL CHECK (system IN ('NTSC','PAL','PAL_M')),
            decoder TEXT NOT NULL CHECK (decoder IN ('ld-decode','vhs-decode')),
            git_branch TEXT,
            git_commit TEXT,
            video_sample_rate REAL,
            active_video_start INTEGER,
            active_video_end INTEGER,
            field_width INTEGER,
            field_height INTEGER,
            number_of_sequential_fields INTEGER,
            colour_burst_start INTEGER,
            colour_burst_end INTEGER,
            is_mapped INTEGER CHECK (is_mapped IN (0,1)),
            is_subcarrier_locked INTEGER CHECK (is_subcarrier_locked IN (0,1)),
            is_widescreen INTEGER CHECK (is_widescreen IN (0,1)),
            white_16b_ire INTEGER,
            black_16b_ire INTEGER,
            blanking_16b_ire INTEGER,
            capture_notes TEXT
        )
        """,
        """
        CREATE TABLE pcm_audio_parameters (
            capture_id INTEGER PRIMARY KEY REFERENCES capture(capture_id) ON DELETE CASCADE,
            bits INTEGER,
            is_signed INTEGER CHECK (is_signed IN (0,1)),
            is_little_endian INTEGER CHECK (is_little_endian IN (0,1)),
            sample_rate REAL
        )
        """,
        """
        CREATE TABLE field_record (
            capture_id INTEGER NOT NULL REFERENCES capture(capture_id) ON DELETE CASCADE,
            field_id INTEGER NOT NULL,
            audio_samples INTEGER,
            decode_faults INTEGER,
            disk_loc REAL,
            efm_t_values INTEGER,
            field_phase_id INTEGER,
            file_loc INTEGER,
            is_first_field INTEGER CHECK (is_first_field IN (0,1)),
            median_burst_ire REAL,
            pad INTEGER CHECK (pad IN (0,1)),
            sync_conf INTEGER,
            ntsc_is_fm_code_data_valid INTEGER CHECK (ntsc_is_fm_code_data_valid IN (0,1)),
            ntsc_fm_code_data INTEGER,
            ntsc_field_flag INTEGER CHECK (ntsc_field_flag IN (0,1)),
            ntsc_is_video_id_data_valid INTEGER CHECK (ntsc_is_video_id_data_valid IN (0,1)),
            ntsc_video_id_data INTEGER,
            ntsc_white_flag INTEGER CHECK (ntsc_white_flag IN (0,1)),
            PRIMARY KEY (capture_id, field_id)
        )
        """,
        """
        CREATE TABLE vits_metrics (
            capture_id INTEGER NOT NULL,
            field_id INTEGER NOT NULL,
            b_psnr REAL,
            w_snr REAL,
            FOREIGN KEY (capture_id, field_id) 
                REFERENCES field_record(capture_id, field_id) ON DELETE CASCADE,
            PRIMARY KEY (capture_id, field_id)
        )
        """,
        """
        CREATE TABLE vbi (
            capture_id INTEGER NOT NULL,
            field_id INTEGER NOT NULL,
            vbi0 INTEGER NOT NULL,
            vbi1 INTEGER NOT NULL,
            vbi2 INTEGER NOT NULL,
            FOREIGN KEY (capture_id, field_id) 
                REFERENCES field_record(capture_id, field_id) ON DELETE CASCADE,
            PRIMARY KEY (capture_id, field_id)
        )
        """,
        """
        CREATE TABLE drop_outs (
            capture_id INTEGER NOT NULL,
            field_id INTEGER NOT NULL,
            field_line INTEGER NOT NULL,
            startx INTEGER NOT NULL,
            endx INTEGER NOT NULL,
            FOREIGN KEY (capture_id, field_id) 
                REFERENCES field_record(capture_id, field_id) ON DELETE CASCADE,
            PRIMARY KEY (capture_id, field_id, field_line, startx, endx)
        )
        """,
        """
        CREATE TABLE vitc (
            capture_id INTEGER NOT NULL,
            field_id INTEGER NOT NULL,
            vitc0 INTEGER NOT NULL,
            vitc1 INTEGER NOT NULL,
            vitc2 INTEGER NOT NULL,
            vitc3 INTEGER NOT NULL,
            vitc4 INTEGER NOT NULL,
            vitc5 INTEGER NOT NULL,
            vitc6 INTEGER NOT NULL,
            vitc7 INTEGER NOT NULL,
            FOREIGN KEY (capture_id, field_id) 
                REFERENCES field_record(capture_id, field_id) ON DELETE CASCADE,
            PRIMARY KEY (capture_id, field_id)
        )
        """,
        """
        CREATE TABLE closed_caption (
            capture_id INTEGER NOT NULL,
            field_id INTEGER NOT NULL,
            data0 INTEGER,
            data1 INTEGER,
            FOREIGN KEY (capture_id, field_id) 
                REFERENCES field_record(capture_id, field_id) ON DELETE CASCADE,
            PRIMARY KEY (capture_id, field_id)
        )
        """
    ];

    static TbcSqliteMetadataWriter()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    internal sealed class SequenceWriter : IDisposable
    {
        private readonly DecodeSession _session;
        private readonly string _dbPath;
        private readonly SqliteConnection _connection;
        private SqliteTransaction _transaction;
        private readonly long _captureId;
        private int _fieldIndex;
        private bool _completed;
        private bool _disposed;

        public SequenceWriter(DecodeSession session, string dbPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
            _session = session;
            _dbPath = dbPath;
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            string? directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
            _connection.Open();
            try
            {
                foreach (string statement in SchemaStatements)
                {
                    Execute(_connection, transaction: null, statement);
                }

                _transaction = _connection.BeginTransaction();
                JsonObject header = TbcOutputMetadataWriter.BuildHeader(session, 0);
                _captureId = InsertCapture(_connection, _transaction, header);
                InsertPcmAudioParameters(_connection, _transaction, header, _captureId);
            }
            catch
            {
                _connection.Dispose();
                throw;
            }
        }

        public void Add(JsonObject fieldInfo)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            InsertField(
                _connection,
                _transaction,
                _session,
                fieldInfo,
                _captureId,
                _fieldIndex);
            _fieldIndex++;
            _transaction.Commit();
            _transaction.Dispose();
            _transaction = _connection.BeginTransaction();
        }

        public void Complete(int fieldCount, VideoOutputConverter? converter)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException("The streaming SQLite metadata writer was already completed.");
            }

            if (fieldCount != _fieldIndex)
            {
                throw new InvalidOperationException(
                    $"SQLite metadata field count {_fieldIndex} did not match JSON field count {fieldCount}.");
            }

            JsonObject header = TbcOutputMetadataWriter.BuildHeader(
                _session,
                fieldCount,
                converter ?? _session.VideoOutput);
            UpdateCapture(_connection, _transaction, header, _captureId);
            _transaction.Commit();
            _completed = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _transaction.Dispose();
            _connection.Dispose();
            if (_completed)
            {
                NormalizeSqliteHeader(_dbPath);
            }
        }
    }

    public static void Write(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields,
        string dbPath,
        IReadOnlyList<TbcFieldOrderDecision>? fieldOrder = null)
        => Write(session, fields, dbPath, fieldOrder, fieldIdentities: null);

    internal static void Write(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields,
        string dbPath,
        IReadOnlyList<TbcFieldOrderDecision>? fieldOrder,
        IReadOnlyList<TbcDecodedField>? fieldIdentities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        string? directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonObject metadata = TbcOutputMetadataWriter.BuildJson(
            session,
            fields,
            fieldOrder,
            fieldIdentities);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();

        foreach (string statement in SchemaStatements)
        {
            Execute(connection, transaction: null, statement);
        }

        SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            JsonObject initialMetadata = TbcOutputMetadataWriter.BuildHeader(session, 0);
            long captureId = InsertCapture(connection, transaction, initialMetadata);
            InsertPcmAudioParameters(connection, transaction, initialMetadata, captureId);
            JsonArray metadataFields = RequiredArray(metadata, "fields");
            for (int i = 0; i < metadataFields.Count; i++)
            {
                JsonObject field = metadataFields[i]?.AsObject()
                    ?? throw new InvalidOperationException("Field metadata entry was not an object.");
                InsertField(connection, transaction, session, field, captureId, i);
                transaction.Commit();
                transaction.Dispose();
                transaction = connection.BeginTransaction();
            }

            UpdateCapture(connection, transaction, metadata, captureId);
            transaction.Commit();
        }
        finally
        {
            transaction.Dispose();
        }

        connection.Close();
        NormalizeSqliteHeader(dbPath);
    }

    private static void NormalizeSqliteHeader(string dbPath)
    {
        using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Span<byte> version = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(version, UpstreamSqliteVersionNumber);
        stream.Position = 96;
        stream.Write(version);
    }

    private static long InsertCapture(SqliteConnection connection, SqliteTransaction transaction, JsonObject metadata)
    {
        JsonObject video = RequiredObject(metadata, "videoParameters");
        Execute(
            connection,
            transaction,
            """
            INSERT INTO capture (
                system, decoder, git_branch, git_commit,
                video_sample_rate, active_video_start, active_video_end,
                field_width, field_height, number_of_sequential_fields,
                colour_burst_start, colour_burst_end,
                white_16b_ire, black_16b_ire, blanking_16b_ire,
                is_mapped, is_subcarrier_locked, is_widescreen
            ) VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9, $p10, $p11, $p12, $p13, $p14, $p15, $p16, $p17)
            """,
            StringValue(video, "system") ?? "NTSC",
            StringValue(video, "decoder") ?? "ld-decode",
            StringValue(video, "gitBranch") ?? "",
            StringValue(video, "gitCommit") ?? "",
            NumberValue(video, "sampleRate"),
            DbValue(video, "activeVideoStart"),
            DbValue(video, "activeVideoEnd"),
            DbValue(video, "fieldWidth"),
            DbValue(video, "fieldHeight"),
            DbValue(video, "numberOfSequentialFields"),
            DbValue(video, "colourBurstStart"),
            DbValue(video, "colourBurstEnd"),
            DbValue(video, "white16bIre"),
            DbValue(video, "black16bIre"),
            DbValue(video, "blanking16bIre"),
            0,
            (StringValue(video, "system") ?? "NTSC").Equals("NTSC", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            0);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_insert_rowid()";
        return (long)command.ExecuteScalar()!;
    }

    private static void UpdateCapture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JsonObject metadata,
        long captureId)
    {
        JsonObject video = RequiredObject(metadata, "videoParameters");
        string system = StringValue(video, "system") ?? "NTSC";
        Execute(
            connection,
            transaction,
            """
            UPDATE capture SET
                system = $p0,
                decoder = $p1,
                git_branch = $p2,
                git_commit = $p3,
                video_sample_rate = $p4,
                active_video_start = $p5,
                active_video_end = $p6,
                field_width = $p7,
                field_height = $p8,
                number_of_sequential_fields = $p9,
                colour_burst_start = $p10,
                colour_burst_end = $p11,
                white_16b_ire = $p12,
                black_16b_ire = $p13,
                blanking_16b_ire = $p14,
                is_mapped = $p15,
                is_subcarrier_locked = $p16,
                is_widescreen = $p17
            WHERE capture_id = $p18
            """,
            system,
            StringValue(video, "decoder") ?? "ld-decode",
            StringValue(video, "gitBranch") ?? "",
            StringValue(video, "gitCommit") ?? "",
            NumberValue(video, "sampleRate"),
            DbValue(video, "activeVideoStart"),
            DbValue(video, "activeVideoEnd"),
            DbValue(video, "fieldWidth"),
            DbValue(video, "fieldHeight"),
            DbValue(video, "numberOfSequentialFields"),
            DbValue(video, "colourBurstStart"),
            DbValue(video, "colourBurstEnd"),
            DbValue(video, "white16bIre"),
            DbValue(video, "black16bIre"),
            DbValue(video, "blanking16bIre"),
            0,
            system.Equals("NTSC", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            0,
            captureId);
    }

    private static void InsertPcmAudioParameters(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JsonObject metadata,
        long captureId)
    {
        JsonObject pcm = RequiredObject(metadata, "pcmAudioParameters");
        Execute(
            connection,
            transaction,
            """
            INSERT INTO pcm_audio_parameters (
                capture_id, bits, is_little_endian, is_signed, sample_rate
            ) VALUES ($p0, $p1, $p2, $p3, $p4)
            """,
            captureId,
            DbValue(pcm, "bits"),
            BoolIntValue(pcm, "isLittleEndian"),
            BoolIntValue(pcm, "isSigned"),
            NumberValue(pcm, "sampleRate"));
    }

    private static void InsertField(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DecodeSession session,
        JsonObject field,
        long captureId,
        int fieldIndex)
    {
        long fieldCaptureId = session.Spec.Name is "vhs" or "cvbs"
            ? session.DropoutOptions.Enabled ? captureId : 0L
            : captureId;
        int fieldId = session.Spec.Name is "vhs" or "cvbs"
            ? (IntValue(field, "seqNo")
                ?? throw new InvalidOperationException("VHS/CVBS field metadata did not contain 'seqNo'.")) - 1
            : fieldIndex;
        int? decodeFaults = IntValue(field, "decodeFaults");
        Execute(
            connection,
            transaction,
            """
            INSERT INTO field_record (
                capture_id, field_id, audio_samples, decode_faults, disk_loc,
                efm_t_values, field_phase_id, file_loc, is_first_field,
                median_burst_ire, pad, sync_conf
            ) VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9, $p10, $p11)
            """,
            fieldCaptureId,
            fieldId,
            session.Spec.Name == "ld" ? DbValue(field, "audioSamples") : null,
            decodeFaults == 0 ? null : decodeFaults,
            NumberValue(field, "diskLoc"),
            session.Spec.Name == "ld" ? DbValue(field, "efmTValues") : null,
            DbValue(field, "fieldPhaseID"),
            DbValue(field, "fileLoc"),
            BoolIntValue(field, "isFirstField") ?? 0,
            session.Spec.Name == "ld" ? NumberValue(field, "medianBurstIRE") : null,
            0,
            DbValue(field, "syncConf"));

        InsertVitsMetrics(connection, transaction, session, field, fieldCaptureId, fieldId);
        InsertVbi(connection, transaction, field, fieldCaptureId, fieldId);
        InsertDropouts(connection, transaction, session, field, fieldCaptureId, fieldId);
    }

    private static void InsertVitsMetrics(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DecodeSession session,
        JsonObject field,
        long captureId,
        int fieldId)
    {
        JsonObject? metrics = OptionalObject(field, "vitsMetrics");
        if (session.Spec.Name == "ld" && (metrics is null || metrics.Count == 0))
        {
            return;
        }

        Execute(
            connection,
            transaction,
            """
            INSERT INTO vits_metrics (
                capture_id, field_id, w_snr, b_psnr
            ) VALUES ($p0, $p1, $p2, $p3)
            """,
            captureId,
            fieldId,
            NumberValue(metrics, "wSNR") ?? 0.0,
            NumberValue(metrics, "bPSNR") ?? 0.0);
    }

    private static void InsertVbi(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JsonObject field,
        long captureId,
        int fieldId)
    {
        JsonArray? vbiData = OptionalObject(field, "vbi")?["vbiData"]?.AsArray();
        if (vbiData is null || vbiData.Count == 0)
        {
            return;
        }

        Execute(
            connection,
            transaction,
            """
            INSERT INTO vbi (
                capture_id, field_id, vbi0, vbi1, vbi2
            ) VALUES ($p0, $p1, $p2, $p3, $p4)
            """,
            captureId,
            fieldId,
            ArrayInt(vbiData, 0),
            ArrayInt(vbiData, 1),
            ArrayInt(vbiData, 2));
    }

    private static void InsertDropouts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DecodeSession session,
        JsonObject field,
        long captureId,
        int fieldId)
    {
        if (!session.DropoutOptions.Enabled)
        {
            return;
        }

        JsonObject? dropouts = OptionalObject(field, "dropOuts");
        if (dropouts is null)
        {
            return;
        }

        JsonArray? fieldLines = dropouts["fieldLine"]?.AsArray();
        JsonArray? starts = dropouts["startx"]?.AsArray();
        JsonArray? ends = dropouts["endx"]?.AsArray();
        if (fieldLines is null || starts is null || ends is null)
        {
            return;
        }

        int count = Math.Min(fieldLines.Count, Math.Min(starts.Count, ends.Count));
        for (int i = 0; i < count; i++)
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO drop_outs (
                    capture_id, field_id, field_line, startx, endx
                ) VALUES ($p0, $p1, $p2, $p3, $p4)
                """,
                captureId,
                fieldId,
                ArrayInt(fieldLines, i),
                ArrayInt(starts, i),
                ArrayInt(ends, i));
        }
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params object?[] values)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (int i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue("$p" + i.ToString(), values[i] ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static JsonObject RequiredObject(JsonObject obj, string name)
        => OptionalObject(obj, name)
            ?? throw new InvalidOperationException($"Metadata did not contain '{name}'.");

    private static JsonArray RequiredArray(JsonObject obj, string name)
        => obj[name]?.AsArray()
            ?? throw new InvalidOperationException($"Metadata did not contain '{name}'.");

    private static JsonObject? OptionalObject(JsonObject? obj, string name)
    {
        JsonNode? node = obj?[name];
        return node is null ? null : node.AsObject();
    }

    private static object? DbValue(JsonObject? obj, string name)
    {
        JsonNode? node = obj?[name];
        if (node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out bool boolValue))
            {
                return boolValue ? 1 : 0;
            }

            if (value.TryGetValue<int>(out int intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<long>(out long longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<double>(out double doubleValue))
            {
                return double.IsFinite(doubleValue) ? doubleValue : null;
            }

            if (value.TryGetValue<string>(out string? stringValue))
            {
                return stringValue;
            }
        }

        return null;
    }

    private static double? NumberValue(JsonObject? obj, string name)
    {
        object? value = DbValue(obj, name);
        return value switch
        {
            int intValue => intValue,
            long longValue => longValue,
            double doubleValue => doubleValue,
            _ => null
        };
    }

    private static int? IntValue(JsonObject? obj, string name)
    {
        object? value = DbValue(obj, name);
        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            double doubleValue => checked((int)doubleValue),
            _ => null
        };
    }

    private static int? BoolIntValue(JsonObject? obj, string name)
    {
        object? value = DbValue(obj, name);
        return value switch
        {
            int intValue => intValue == 0 ? 0 : 1,
            long longValue => longValue == 0 ? 0 : 1,
            double doubleValue => doubleValue == 0.0 ? 0 : 1,
            _ => null
        };
    }

    private static string? StringValue(JsonObject? obj, string name)
        => DbValue(obj, name) as string;

    private static int ArrayInt(JsonArray array, int index)
        => index < array.Count && array[index] is JsonNode node && IntValue(new JsonObject { ["value"] = node.DeepClone() }, "value") is { } value
            ? value
            : 0;

}
