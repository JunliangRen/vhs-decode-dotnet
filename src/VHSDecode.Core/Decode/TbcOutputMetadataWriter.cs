using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Decode;

public static class TbcOutputMetadataWriter
{
    internal sealed record LaserDiscNtscLine19ColorInfo(double Level, double PhaseDegrees, double RawSnr);

    internal static bool ShouldWriteRecoverySnapshot(int fieldsWritten)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldsWritten);
        return fieldsWritten < 100 || fieldsWritten % 500 == 0;
    }

    internal sealed class StreamingWriter : IDisposable
    {
        private sealed record SnapshotWorkItem(
            string Prefix,
            long FieldsByteCount,
            string Suffix,
            bool IsSentinel = false);

        private static readonly SnapshotWorkItem SnapshotSentinel = new("", 0, "", IsSentinel: true);

        private readonly DecodeSession _session;
        private readonly string _jsonPath;
        private readonly string _fieldsPath;
        private readonly bool _verbose;
        private readonly FieldObjectBuilder _fieldBuilder;
        private readonly Func<string, Stream> _createSnapshotOutput;
        private readonly BlockingCollection<SnapshotWorkItem> _snapshotQueue = new(boundedCapacity: 1);
        private readonly ManualResetEventSlim _snapshotWriting = new();
        private readonly Thread _snapshotThread;
        private StreamWriter? _fieldsWriter;
        private VideoOutputConverter? _lastOutputConverter;
        private bool _snapshotWorkerStopped;
        private bool _completed;
        private bool _disposed;

        public StreamingWriter(
            DecodeSession session,
            string jsonPath,
            Func<string, Stream>? createSnapshotOutput = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
            _session = session;
            _jsonPath = jsonPath;
            _fieldsPath = jsonPath + ".fields.tmp";
            _verbose = session.ExecutionOptions.VerboseVits;
            _fieldBuilder = new FieldObjectBuilder(session);
            _createSnapshotOutput = createSnapshotOutput ?? DecodeOutputFile.Create;
            _fieldsWriter = new StreamWriter(
                new FileStream(
                    _fieldsPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _snapshotThread = new Thread(ConsumeSnapshots)
            {
                IsBackground = true,
                Name = "vhs-decode-json-dumper"
            };
            _snapshotThread.Start();
        }

        public int FieldCount { get; private set; }

        public VideoOutputConverter? LastOutputConverter => _lastOutputConverter;

        public JsonObject Add(
            TbcDecodedField field,
            TbcFieldOrderDecision decision,
            TbcDecodedField? fieldIdentity = null)
        {
            ObjectDisposedException.ThrowIf(_fieldsWriter is null, this);
            JsonObject fieldInfo = _fieldBuilder.Add(field, decision, fieldIdentity);
            if (FieldCount != 0)
            {
                _fieldsWriter!.Write(',');
                if (_verbose)
                {
                    _fieldsWriter.Write(Environment.NewLine);
                }
            }

            _fieldsWriter!.Write(SerializeNode(fieldInfo, _verbose));
            FieldCount++;
            if (field.OutputConverter is not null)
            {
                _lastOutputConverter = field.OutputConverter;
            }

            return fieldInfo;
        }

        public void WriteSnapshot()
        {
            ObjectDisposedException.ThrowIf(_fieldsWriter is null, this);
            if (!_snapshotWriting.IsSet)
            {
                _snapshotQueue.TryAdd(CaptureSnapshot());
            }
        }

        public void Complete()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The streaming metadata writer was already completed.");
            }

            SnapshotWorkItem finalSnapshot = CaptureSnapshot();
            CloseFieldsWriter();
            try
            {
                StopSnapshotWorker(finalSnapshot);
                _completed = true;
            }
            finally
            {
                File.Delete(_fieldsPath);
            }
        }

        public void LeaveIncompleteJson()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The streaming metadata writer was already completed.");
            }

            CloseFieldsWriter();
            try
            {
                StopSnapshotWorker();
                File.Delete(_jsonPath);
                File.WriteAllText(
                    _jsonPath + ".tmp",
                    "{",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _completed = true;
            }
            finally
            {
                File.Delete(_fieldsPath);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CloseFieldsWriter();
            StopSnapshotWorker();
            if (!_completed)
            {
                File.Delete(_fieldsPath);
            }

            _snapshotQueue.Dispose();
            _snapshotWriting.Dispose();
            _disposed = true;
        }

        private SnapshotWorkItem CaptureSnapshot()
        {
            ObjectDisposedException.ThrowIf(_fieldsWriter is null, this);
            _fieldsWriter.Flush();

            using var prefix = new StringWriter(CultureInfo.InvariantCulture);
            WritePrefix(prefix, FieldCount, _lastOutputConverter);
            using var suffix = new StringWriter(CultureInfo.InvariantCulture);
            WriteSuffix(suffix);
            return new SnapshotWorkItem(
                prefix.ToString(),
                _fieldsWriter.BaseStream.Position,
                suffix.ToString());
        }

        private void ConsumeSnapshots()
        {
            try
            {
                while (true)
                {
                    SnapshotWorkItem snapshot = _snapshotQueue.Take();
                    if (snapshot.IsSentinel)
                    {
                        return;
                    }

                    _snapshotWriting.Set();
                    try
                    {
                        WriteCurrentJson(snapshot);
                    }
                    finally
                    {
                        _snapshotWriting.Reset();
                    }
                }
            }
            catch (Exception)
            {
                // v0.4.0 lets JSON-dumper thread failures terminate only that worker.
            }
        }

        private void StopSnapshotWorker(SnapshotWorkItem? finalSnapshot = null)
        {
            if (_snapshotWorkerStopped)
            {
                return;
            }

            if (finalSnapshot is not null)
            {
                _ = EnqueueWhileSnapshotWorkerIsRunning(finalSnapshot);
            }

            _ = EnqueueWhileSnapshotWorkerIsRunning(SnapshotSentinel);
            _snapshotThread.Join();
            _snapshotWorkerStopped = true;
        }

        private bool EnqueueWhileSnapshotWorkerIsRunning(SnapshotWorkItem snapshot)
        {
            while (_snapshotThread.IsAlive)
            {
                if (_snapshotQueue.TryAdd(snapshot, millisecondsTimeout: 50))
                {
                    return true;
                }
            }

            return false;
        }

        private void WriteCurrentJson(SnapshotWorkItem snapshot)
        {
            string tempPath = _jsonPath + ".tmp";
            using (Stream output = _createSnapshotOutput(tempPath))
            {
                WriteUtf8(output, snapshot.Prefix);
                using (var fields = new FileStream(
                    _fieldsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[16 * 1024];
                    long remaining = snapshot.FieldsByteCount;
                    while (remaining > 0)
                    {
                        int read = fields.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0)
                        {
                            throw new EndOfStreamException("The streaming metadata field snapshot ended unexpectedly.");
                        }

                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }

                WriteUtf8(output, snapshot.Suffix);
            }

            File.Move(tempPath, _jsonPath, overwrite: true);
        }

        private static void WriteUtf8(Stream output, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            output.Write(bytes, 0, bytes.Length);
        }

        private void WritePrefix(
            TextWriter output,
            int fieldCount,
            VideoOutputConverter? outputConverter)
        {
            JsonObject pcm = BuildPcmAudioParameters(_session);
            JsonObject video = BuildVideoParameters(
                _session,
                fieldCount,
                outputConverter ?? _session.VideoOutput);
            if (!_verbose)
            {
                output.Write('{');
                output.Write("\"pcmAudioParameters\":");
                output.Write(SerializeNode(pcm, writeIndented: false));
                output.Write(",\"videoParameters\":");
                output.Write(SerializeNode(video, writeIndented: false));
                output.Write(",\"fields\":[");
                return;
            }

            string lineBreak = Environment.NewLine;
            output.Write('{');
            output.Write(lineBreak);
            output.Write("\"pcmAudioParameters\":");
            output.Write(SerializeNode(pcm, writeIndented: true));
            output.Write(',');
            output.Write(lineBreak);
            output.Write("\"videoParameters\":");
            output.Write(SerializeNode(video, writeIndented: true));
            output.Write(',');
            output.Write(lineBreak);
            output.Write("\"fields\":[");
            output.Write(lineBreak);
        }

        private void WriteSuffix(TextWriter output)
        {
            if (!_verbose)
            {
                output.Write("]}");
                output.Write(Environment.NewLine);
                return;
            }

            string lineBreak = Environment.NewLine;
            output.Write(lineBreak);
            output.Write(']');
            output.Write(lineBreak);
            output.Write('}');
            output.Write(lineBreak);
        }

        private void CloseFieldsWriter()
        {
            _fieldsWriter?.Dispose();
            _fieldsWriter = null;
        }
    }

    public static void WriteJson(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields,
        string jsonPath,
        IReadOnlyList<TbcFieldOrderDecision>? fieldOrder = null)
        => WriteJson(session, fields, jsonPath, fieldOrder, fieldIdentities: null);

    internal static void WriteJson(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields,
        string jsonPath,
        IReadOnlyList<TbcFieldOrderDecision>? fieldOrder,
        IReadOnlyList<TbcDecodedField>? fieldIdentities)
    {
        JsonObject root = BuildJson(session, fields, fieldOrder, fieldIdentities);
        string tempPath = jsonPath + ".tmp";
        File.WriteAllText(tempPath, SerializeJson(root, session.ExecutionOptions.VerboseVits));
        File.Move(tempPath, jsonPath, overwrite: true);
    }

    internal static JsonObject BuildJson(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields,
        IReadOnlyList<TbcFieldOrderDecision>? fieldOrder = null,
        IReadOnlyList<TbcDecodedField>? fieldIdentities = null)
    {
        VideoOutputConverter converter = fields.LastOrDefault(field => field.OutputConverter is not null)?.OutputConverter
            ?? session.VideoOutput;
        JsonObject root = BuildHeader(session, fields.Count, converter);
        root["fields"] = BuildFieldArray(session, fields, fieldOrder, fieldIdentities);
        return root;
    }

    internal static JsonObject BuildHeader(
        DecodeSession session,
        int fieldCount,
        VideoOutputConverter? converter = null)
    {
        return new JsonObject
        {
            ["pcmAudioParameters"] = BuildPcmAudioParameters(session),
            ["videoParameters"] = BuildVideoParameters(
                session,
                fieldCount,
                converter ?? session.VideoOutput)
        };
    }

    private static string SerializeJson(JsonObject root, bool verboseVits)
    {
        if (!verboseVits)
        {
            return SerializeNode(root, writeIndented: false) + Environment.NewLine;
        }

        string lineBreak = Environment.NewLine;
        var json = new StringBuilder();
        json.Append('{').Append(lineBreak);
        foreach ((string name, JsonNode? value) in root)
        {
            if (name == "fields")
            {
                continue;
            }

            json.Append(JsonSerializer.Serialize(name));
            json.Append(':');
            json.Append(SerializeNode(value, writeIndented: true));
            json.Append(',').Append(lineBreak);
        }

        json.Append("\"fields\":[").Append(lineBreak);
        JsonArray fields = root["fields"]?.AsArray()
            ?? throw new InvalidOperationException("Metadata did not contain a fields array.");
        for (int i = 0; i < fields.Count; i++)
        {
            if (i != 0)
            {
                json.Append(',').Append(lineBreak);
            }

            json.Append(SerializeNode(fields[i], writeIndented: true));
        }

        json.Append(lineBreak).Append(']').Append(lineBreak).Append('}').Append(lineBreak);
        return json.ToString();
    }

    private static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    private static string SerializeNode(JsonNode? node, bool writeIndented)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = writeIndented,
            IndentSize = 4
        }))
        {
            WriteNode(writer, node);
        }

        return NormalizeLineEndings(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach ((string name, JsonNode? value) in obj)
            {
                writer.WritePropertyName(name);
                WriteNode(writer, value);
            }

            writer.WriteEndObject();
            return;
        }

        if (node is JsonArray array)
        {
            writer.WriteStartArray();
            foreach (JsonNode? value in array)
            {
                WriteNode(writer, value);
            }

            writer.WriteEndArray();
            return;
        }

        JsonValue scalar = node.AsValue();
        if (scalar.TryGetValue<bool>(out bool boolValue))
        {
            writer.WriteBooleanValue(boolValue);
        }
        else if (scalar.TryGetValue<int>(out int intValue))
        {
            writer.WriteNumberValue(intValue);
        }
        else if (scalar.TryGetValue<long>(out long longValue))
        {
            writer.WriteNumberValue(longValue);
        }
        else if (scalar.TryGetValue<JsonElement>(out JsonElement jsonElement)
            && jsonElement.ValueKind == JsonValueKind.Number)
        {
            writer.WriteRawValue(jsonElement.GetRawText(), skipInputValidation: false);
        }
        else if (scalar.TryGetValue<double>(out double doubleValue))
        {
            if (!double.IsFinite(doubleValue))
            {
                throw new JsonException("Out of range float values are not JSON compliant.");
            }

            writer.WriteRawValue(FormatPythonDouble(doubleValue), skipInputValidation: false);
        }
        else if (scalar.TryGetValue<string>(out string? stringValue))
        {
            writer.WriteStringValue(stringValue);
        }
        else
        {
            scalar.WriteTo(writer);
        }
    }

    private static string FormatPythonDouble(double value)
    {
        string formatted = value.ToString("R", CultureInfo.InvariantCulture);
        int exponentIndex = formatted.IndexOf('E');
        if (exponentIndex < 0)
        {
            return formatted.Contains('.', StringComparison.Ordinal)
                ? formatted
                : formatted + ".0";
        }

        int exponent = int.Parse(formatted.AsSpan(exponentIndex + 1), CultureInfo.InvariantCulture);
        string sign = exponent >= 0 ? "+" : "-";
        return formatted[..exponentIndex]
            + "e"
            + sign
            + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
    }

    private static JsonObject BuildVideoParameters(
        DecodeSession session,
        int fieldCount,
        VideoOutputConverter converter)
    {
        double black16bIre = ConvertIreForMetadata(session, converter, session.BlackIre);
        double white16bIre = ConvertIreForMetadata(session, converter, 100.0);
        if (session.Spec.Name == "vhs" && session.LevelAdjust != 0.0)
        {
            black16bIre *= 1.0 - session.LevelAdjust;
            white16bIre *= 1.0 + session.LevelAdjust;
        }

        JsonObject videoParameters = new()
        {
            ["numberOfSequentialFields"] = fieldCount,
            ["osInfo"] = DecodeVersionInfo.OsInfo(),
            ["version"] = DecodeVersionInfo.Version
        };

        (string gitBranch, string gitCommit) = DecodeVersionInfo.ExtractGitVersionParts(DecodeVersionInfo.Version);
        if (!string.IsNullOrEmpty(gitBranch))
        {
            videoParameters["gitBranch"] = gitBranch;
        }

        if (!string.IsNullOrEmpty(gitCommit))
        {
            videoParameters["gitCommit"] = gitCommit;
        }

        videoParameters["system"] = MetadataSystemName(session);
        videoParameters["fieldWidth"] = session.TbcFrameSpec.OutputLineLength;
        videoParameters["sampleRate"] = session.TbcFrameSpec.OutputSampleRateHz;
        videoParameters["black16bIre"] = black16bIre;
        videoParameters["white16bIre"] = white16bIre;
        videoParameters["blanking16bIre"] = ConvertIreForMetadata(session, converter, 0.0);
        videoParameters["fieldHeight"] = session.TbcFrameSpec.OutputLineCount;

        AddIfPresent(videoParameters, "colourBurstStart", session.TbcFrameSpec.ColourBurstStart);
        AddIfPresent(videoParameters, "colourBurstEnd", session.TbcFrameSpec.ColourBurstEnd);
        AddIfPresent(videoParameters, "activeVideoStart", session.TbcFrameSpec.ActiveVideoStart);
        AddIfPresent(videoParameters, "activeVideoEnd", session.TbcFrameSpec.ActiveVideoEnd);
        if (session.Spec.Name == "vhs")
        {
            videoParameters["tapeFormat"] = session.Parameters.TapeFormat;
        }

        return videoParameters;
    }

    internal static void ValidatePcmAudioParameters(DecodeSession session)
        => _ = BuildPcmAudioParameters(session);

    private static JsonObject BuildPcmAudioParameters(DecodeSession session)
    {
        LaserDiscAudioOptions? audioOptions = session.LaserDiscAudioOptions;
        JsonNode sampleRateNode;
        if (audioOptions?.AnalogAudioFrequencyInteger is { } integerSampleRate)
        {
            if (integerSampleRate.Sign >= 0)
            {
                sampleRateNode = JsonNode.Parse(
                    integerSampleRate.ToString(CultureInfo.InvariantCulture))!;
            }
            else
            {
                double lineMultiplier = (double)BigInteger.Abs(integerSampleRate);
                if (!double.IsFinite(lineMultiplier))
                {
                    throw new OverflowException("int too large to convert to float");
                }

                double sampleRate = (1_000_000.0 / JsonDouble(session.Parameters.SysParams, "line_period"))
                    * lineMultiplier;
                sampleRateNode = JsonValue.Create(sampleRate);
            }
        }
        else
        {
            double sampleRate = audioOptions?.AnalogAudioFrequency ?? 0.0;
            if (sampleRate < 0.0)
            {
                sampleRate = (1_000_000.0 / JsonDouble(session.Parameters.SysParams, "line_period")) * -sampleRate;
            }

            bool isResolvedNtscRate = sampleRate != 0.0
                && audioOptions?.UseNtscAudioRate == true
                && FormatCatalog.ParentSystem(session.System) == "NTSC";
            sampleRateNode = isResolvedNtscRate
                ? JsonValue.Create(sampleRate)
                : JsonValue.Create(checked((int)sampleRate));
        }

        return new JsonObject
        {
            ["bits"] = 16,
            ["isLittleEndian"] = true,
            ["isSigned"] = true,
            ["sampleRate"] = sampleRateNode
        };
    }

    private static double ConvertIreForMetadata(
        DecodeSession session,
        VideoOutputConverter converter,
        double ire)
    {
        double hz = converter.IreToHz(ire);
        return session.TbcRenderer.ExportRawTbc
            ? (float)hz
            : converter.ConvertHz(hz);
    }

    private static JsonArray BuildFieldArray(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields,
        IReadOnlyList<TbcFieldOrderDecision>? fieldOrder,
        IReadOnlyList<TbcDecodedField>? fieldIdentities)
    {
        if (fieldOrder is not null && fieldOrder.Count != fields.Count)
        {
            throw new ArgumentException("Field order decision count must match decoded field count.", nameof(fieldOrder));
        }

        if (fieldIdentities is not null && fieldIdentities.Count != fields.Count)
        {
            throw new ArgumentException("Field identity count must match decoded field count.", nameof(fieldIdentities));
        }

        var array = new JsonArray();
        var builder = new FieldObjectBuilder(session);
        for (int i = 0; i < fields.Count; i++)
        {
            TbcDecodedField field = fields[i];
            bool defaultFirstField = field.DetectedFirstField ?? (i % 2 == 0);
            TbcFieldOrderDecision decision = fieldOrder?[i]
                ?? new TbcFieldOrderDecision(
                    SeqNo: i + 1,
                    IsFirstField: defaultFirstField,
                    DetectedFirstField: defaultFirstField,
                    IsDuplicateField: false,
                    WriteField: true,
                    SyncConfidence: field.SyncConfidence,
                    DecodeFaults: 0);
            array.Add(builder.Add(field, decision, fieldIdentities?[i]));
        }

        return array;
    }

    internal sealed class FieldObjectBuilder(DecodeSession session)
    {
        private readonly ConditionalWeakTable<TbcDecodedField, WrittenMetadata> _writtenMetadata = new();
        private sealed record WrittenMetadata(int SeqNo, JsonObject Metadata);
        private TbcDecodedField? _previousLaserDiscFirstField;
        private int? _previousLaserDiscFirstFieldPhaseId;
        private int? _previousLaserDiscFieldPhaseId;

        public JsonObject Add(
            TbcDecodedField field,
            TbcFieldOrderDecision decision,
            TbcDecodedField? fieldIdentity = null)
        {
            TbcDecodedField identity = fieldIdentity ?? field;
            if (_writtenMetadata.TryGetValue(identity, out WrittenMetadata? previous)
                && previous is not null
                && previous.SeqNo == decision.SeqNo)
            {
                JsonObject duplicate = previous.Metadata.DeepClone().AsObject();
                if (session.Spec.Name == "ld")
                {
                    duplicate["audioSamples"] = field.AudioSampleCount;
                    duplicate["efmTValues"] = field.EfmTValueCount;
                }

                return duplicate;
            }

            int decodeFaults = decision.DecodeFaults;
            int syncConfidence = (decision.DecodeFaults & 1) != 0
                ? 10
                : Math.Min(decision.SyncConfidence, field.SyncConfidence);
            JsonObject fieldInfo;

            if (session.Spec.Name == "ld")
            {
                int fieldPhaseId = field.FieldPhaseId ?? EstimateLaserDiscFieldPhase(session, decision);
                fieldInfo = new JsonObject
                {
                    ["isFirstField"] = decision.IsFirstField,
                    ["syncConf"] = syncConfidence,
                    ["seqNo"] = decision.SeqNo,
                    ["diskLoc"] = field.DiskLocation ?? ComputeLaserDiscDiskLocation(session, field),
                    ["fileLoc"] = field.StartSample,
                    ["medianBurstIRE"] = RoundMetadataFloat(field.MedianBurstIre ?? 0.0, 3)
                };
                AddDropouts(session, field, fieldInfo);
                fieldInfo["fieldPhaseID"] = fieldPhaseId;

                if (!decision.IsDuplicateField
                    && _previousLaserDiscFieldPhaseId.HasValue
                    && !IsExpectedLaserDiscFieldPhase(
                        _previousLaserDiscFieldPhaseId.Value,
                        fieldPhaseId,
                        LaserDiscFieldPhaseCount(session)))
                {
                    decodeFaults |= 2;
                    LogFieldPhaseMismatch(decision, _previousLaserDiscFieldPhaseId.Value, fieldPhaseId);
                }
                if (!decision.IsDuplicateField)
                {
                    fieldInfo["decodeFaults"] = decodeFaults;
                    IReadOnlyDictionary<string, double>? metrics = field.VitsMetrics is { Count: > 0 }
                        ? field.VitsMetrics
                        : ComputeBasicVitsMetrics(
                            session,
                            field,
                            decision.IsFirstField,
                            _previousLaserDiscFirstField,
                            fieldPhaseId,
                            _previousLaserDiscFirstFieldPhaseId);
                    fieldInfo["vitsMetrics"] = ToJsonObject(metrics);
                    fieldInfo["vbi"] = new JsonObject
                    {
                        ["vbiData"] = ToJsonArray(field.VbiData ?? [])
                    };

                    LaserDiscVbiInterpretation? vbiInterpretation = InterpretLaserDiscVbi(
                        session,
                        decision,
                        _previousLaserDiscFirstField,
                        field);
                    if (vbiInterpretation is not null)
                    {
                        AddLaserDiscVerboseVbiFields(fieldInfo, vbiInterpretation);
                    }
                }

                _previousLaserDiscFieldPhaseId = fieldPhaseId;
                if (!decision.IsDuplicateField && decision.IsFirstField)
                {
                    _previousLaserDiscFirstField = field;
                    _previousLaserDiscFirstFieldPhaseId = fieldPhaseId;
                }

                fieldInfo["audioSamples"] = field.AudioSampleCount;
                fieldInfo["efmTValues"] = field.EfmTValueCount;
            }
            else if (session.Spec.Name == "vhs")
            {
                fieldInfo = new JsonObject
                {
                    ["isFirstField"] = decision.IsFirstField,
                    ["detectedFirstField"] = decision.DetectedFirstField,
                    ["isDuplicateField"] = decision.IsDuplicateField,
                    ["burstStartLine"] = field.BurstStartLine ?? 0,
                    ["syncConf"] = syncConfidence,
                    ["seqNo"] = decision.SeqNo,
                    ["diskLoc"] = field.DiskLocation ?? ComputeTapeDiskLocation(session, field),
                    ["fileLoc"] = field.StartSample,
                    ["fieldPhaseID"] = field.FieldPhaseId ?? EstimateTapeFieldPhase(session, decision)
                };
                AddDropouts(session, field, fieldInfo);
                IReadOnlyDictionary<string, double>? metrics = field.VitsMetrics is { Count: > 0 }
                    ? field.VitsMetrics
                    : ComputeBasicVitsMetrics(session, field, decision.IsFirstField);
                fieldInfo["vitsMetrics"] = ToJsonObject(metrics);
                if (decodeFaults != 0)
                {
                    fieldInfo["decodeFaults"] = decodeFaults;
                }
            }
            else if (session.Spec.Name == "cvbs")
            {
                int fieldPhaseId = field.FieldPhaseId ?? EstimateCvbsFieldPhase(session, decision);
                fieldInfo = new JsonObject
                {
                    ["isFirstField"] = decision.IsFirstField,
                    ["syncConf"] = syncConfidence,
                    ["seqNo"] = decision.SeqNo,
                    ["diskLoc"] = field.DiskLocation ?? ComputeLaserDiscDiskLocation(session, field),
                    ["fileLoc"] = field.StartSample,
                    ["medianBurstIRE"] = RoundMetadataFloat(CvbsMedianBurstIre(field.MedianBurstIre), 3)
                };
                AddDropouts(session, field, fieldInfo);
                fieldInfo["fieldPhaseID"] = fieldPhaseId;
                if (!decision.IsDuplicateField
                    && _previousLaserDiscFieldPhaseId.HasValue
                    && !IsExpectedLaserDiscFieldPhase(
                        _previousLaserDiscFieldPhaseId.Value,
                        fieldPhaseId,
                        LaserDiscFieldPhaseCount(session)))
                {
                    decodeFaults |= 2;
                    LogFieldPhaseMismatch(decision, _previousLaserDiscFieldPhaseId.Value, fieldPhaseId);
                }
                if (!decision.IsDuplicateField)
                {
                    fieldInfo["decodeFaults"] = decodeFaults;
                    IReadOnlyDictionary<string, double>? metrics = field.VitsMetrics is { Count: > 0 }
                        ? field.VitsMetrics
                        : ComputeBasicVitsMetrics(session, field, decision.IsFirstField);
                    fieldInfo["vitsMetrics"] = ToJsonObject(metrics);
                    fieldInfo["vbi"] = new JsonObject
                    {
                        ["vbiData"] = ToJsonArray(field.VbiData ?? [])
                    };
                }
                _previousLaserDiscFieldPhaseId = fieldPhaseId;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported metadata decoder '{session.Spec.Name}'.");
            }

            _writtenMetadata.Remove(identity);
            _writtenMetadata.Add(identity, new WrittenMetadata(decision.SeqNo, fieldInfo));
            return fieldInfo;
        }

        private void LogFieldPhaseMismatch(TbcFieldOrderDecision decision, int previous, int current)
        {
            DecodeSessionLogWriter.Append(
                session,
                "WARNING",
                $"At field #{decision.SeqNo - 1}, Field phaseID sequence mismatch ({previous}->{current}) (player may be paused)");
        }
    }

    private static void AddDropouts(
        DecodeSession session,
        TbcDecodedField field,
        JsonObject fieldInfo)
    {
        if (!session.DropoutOptions.Enabled || field.Dropouts is not { Count: > 0 } dropouts)
        {
            return;
        }

        fieldInfo["dropOuts"] = new JsonObject
        {
            ["fieldLine"] = ToJsonArray(dropouts.FieldLine),
            ["startx"] = ToJsonArray(dropouts.StartX),
            ["endx"] = ToJsonArray(dropouts.EndX)
        };
    }

    private static bool IsExpectedLaserDiscFieldPhase(int previous, int current, int fieldPhaseCount)
    {
        return (current == 1 && previous == fieldPhaseCount)
            || current == previous + 1;
    }

    private static double CvbsMedianBurstIre(double? value)
    {
        double median = value ?? 0.0;
        return double.IsNaN(median) ? 0.0 : median;
    }

    private static double RoundMetadataFloat(double value, int places)
    {
        double scale = Math.Pow(10.0, places);
        return Math.Round(value * scale, MidpointRounding.ToEven) / scale;
    }

    private static LaserDiscVbiInterpretation? InterpretLaserDiscVbi(
        DecodeSession session,
        TbcFieldOrderDecision decision,
        TbcDecodedField? previousFirstField,
        TbcDecodedField currentField)
    {
        if (!session.ExecutionOptions.VerboseVits || decision.IsFirstField || previousFirstField is null)
        {
            return null;
        }

        int[] firstCodes = previousFirstField.VbiData ?? [];
        int[] secondCodes = currentField.VbiData ?? [];
        if (firstCodes.Length == 0 && secondCodes.Length == 0)
        {
            return null;
        }

        return LaserDiscVbiInterpreter.Interpret(
            firstCodes.Concat(secondCodes),
            string.Equals(session.System, "PAL", StringComparison.OrdinalIgnoreCase) ? 25 : 30);
    }

    private static void AddLaserDiscVerboseVbiFields(JsonObject fieldInfo, LaserDiscVbiInterpretation interpretation)
    {
        if (interpretation.FrameNumber.HasValue)
        {
            fieldInfo["cavFrameNr"] = interpretation.FrameNumber.Value;
        }

        if (interpretation.IsClv && interpretation.ClvMinutes.HasValue)
        {
            fieldInfo["clvMinutes"] = interpretation.ClvMinutes.Value;
        }

        if (interpretation.IsClv
            && !interpretation.IsEarlyClv
            && interpretation.ClvSeconds.HasValue
            && interpretation.ClvFrameNumber.HasValue)
        {
            fieldInfo["clvSeconds"] = interpretation.ClvSeconds.Value;
            fieldInfo["clvFrameNr"] = interpretation.ClvFrameNumber.Value;
        }
    }

    private static double ComputeLaserDiscDiskLocation(DecodeSession session, TbcDecodedField field)
    {
        double framesPerSecond = JsonDouble(session.Parameters.SysParams, "FPS");
        if (framesPerSecond <= 0.0)
        {
            return 0.0;
        }

        double samplesPerField = ((int)(session.DecodeSampleRateHz / (framesPerSecond * 2.0))) + 1;
        return Math.Round((field.StartSample / samplesPerField) * 10.0, MidpointRounding.ToEven) / 10.0;
    }

    private static double ComputeTapeDiskLocation(DecodeSession session, TbcDecodedField field)
    {
        return ComputeLaserDiscDiskLocation(session, field);
    }

    private static int EstimateLaserDiscFieldPhase(DecodeSession session, TbcFieldOrderDecision decision)
    {
        if (FormatCatalog.ParentSystem(session.System) == "NTSC")
        {
            return 1;
        }

        int fieldPhases = LaserDiscFieldPhaseCount(session);
        return ((decision.SeqNo - 1) % fieldPhases) + 1;
    }

    private static int EstimateCvbsFieldPhase(DecodeSession session, TbcFieldOrderDecision decision)
    {
        return session.System is "PAL_M" or "PALM" or "NLINHA"
            ? 0
            : FormatCatalog.ParentSystem(session.System) == "PAL"
                ? EstimateLaserDiscFieldPhase(session, decision)
                : 1;
    }

    private static int EstimateTapeFieldPhase(DecodeSession session, TbcFieldOrderDecision decision)
    {
        if (FormatCatalog.ParentSystem(session.System) == "PAL")
        {
            return 1;
        }

        return (decision.DetectedFirstField, (decision.SeqNo / 2) % 2) switch
        {
            (true, 0) => 1,
            (false, 1) => 2,
            (true, 1) => 3,
            (false, 0) => 4,
            _ => 1
        };
    }

    internal static IReadOnlyDictionary<string, double> ComputeBasicVitsMetrics(
        DecodeSession session,
        TbcDecodedField field,
        bool isFirstField,
        TbcDecodedField? previousField = null,
        int? fieldPhaseId = null,
        int? previousFieldPhaseId = null)
    {
        Dictionary<string, double> metrics = [];
        if (field.Samples.Length == 0)
        {
            return metrics;
        }

        double[]? rawTbcSamples = ReadFloat32TbcPayload(field);
        bool quantizedOutput = rawTbcSamples is null;
        bool verboseLaserDisc = session.Spec.Name == "ld" && session.ExecutionOptions.VerboseVits;
        if (verboseLaserDisc)
        {
            AddLaserDiscVerboseTbcMetrics(metrics, session, field, isFirstField, previousField, fieldPhaseId, previousFieldPhaseId);
        }

        foreach ((int line, double startUsec, double lengthUsec) in ReadSliceTuples(session.Parameters.SysParams, "LD_VITS_whitelocs"))
        {
            bool read = rawTbcSamples is null
                ? TryReadTbcSlice(field.Samples, session, line, startUsec, lengthUsec, out double[] values)
                : TryReadTbcSlice(rawTbcSamples, session, line, startUsec, lengthUsec, out values);
            if (!read)
            {
                continue;
            }

            double mean = MeanIreNumpyFloat64(values, session, quantizedOutput);
            if (mean is >= 90.0 and <= 110.0)
            {
                AddRawMetric(metrics, "wSNR", CalcPeakSnr(values, session, quantizedOutput));
                if (verboseLaserDisc)
                {
                    AddRawMetric(metrics, "whiteIRE", mean);
                    if (TryReadPreTbcSlice(
                            session,
                            field,
                            isFirstField,
                            line,
                            startUsec,
                            lengthUsec,
                            (int)session.Filters.LdVideoWhiteOffset,
                            field.RawInputSamples,
                            out double[] whiteRawValues))
                    {
                        AddRawMetric(metrics, "whiteRFLevel", StandardDeviation(whiteRawValues));
                    }
                }

                break;
            }
        }

        if (TryReadMetricTbcSlice(session, field, rawTbcSamples, "blacksnr_slice", out double[] blackValues))
        {
            if (verboseLaserDisc)
            {
                if (TryReadPreTbcSlice(
                        session,
                        field,
                        isFirstField,
                        "blacksnr_slice",
                        delaySamples: (int)session.Filters.LdVideoSyncOffset,
                        source: field.RawInputSamples,
                        out double[] blackRawValues))
                {
                    AddRawMetric(metrics, "blackLineRFLevel", StandardDeviation(blackRawValues));
                }

                if (TryReadPreTbcSlice(
                        session,
                        field,
                        isFirstField,
                        "blacksnr_slice",
                        delaySamples: 0,
                        source: field.PreTbcVideoSamples,
                        out double[] blackPreTbcValues))
                {
                    AddRawMetric(
                        metrics,
                        "blackLinePreTBCIRE",
                        MeanPreTbcIreNumpyFloat32(blackPreTbcValues, session.VideoOutput));
                }

                AddRawMetric(
                    metrics,
                    "blackLinePostTBCIRE",
                    MeanPostTbcIreNumpy(blackValues, session, quantizedOutput));
            }

            AddRawMetric(metrics, "bPSNR", CalcPeakSnr(blackValues, session, quantizedOutput));
        }

        if (verboseLaserDisc
            && metrics.TryGetValue("blackLineRFLevel", out double blackLineRfLevel)
            && metrics.TryGetValue("whiteRFLevel", out double whiteRfLevel)
            && whiteRfLevel != 0.0)
        {
            AddRawMetric(metrics, "blackToWhiteRFRatio", blackLineRfLevel / whiteRfLevel);
        }

        return RoundMetrics(metrics, verboseLaserDisc);
    }

    private static void AddLaserDiscVerboseTbcMetrics(
        Dictionary<string, double> metrics,
        DecodeSession session,
        TbcDecodedField field,
        bool isFirstField,
        TbcDecodedField? previousField,
        int? fieldPhaseId,
        int? previousFieldPhaseId)
    {
        if (string.Equals(session.System, "NTSC", StringComparison.OrdinalIgnoreCase))
        {
            AddLaserDiscNtscVerboseTbcMetrics(metrics, session, field, isFirstField, previousField, fieldPhaseId, previousFieldPhaseId);
        }
        else
        {
            AddLaserDiscPalVerboseTbcMetrics(metrics, session, field, isFirstField);
        }
    }

    private static void AddLaserDiscNtscVerboseTbcMetrics(
        Dictionary<string, double> metrics,
        DecodeSession session,
        TbcDecodedField field,
        bool isFirstField,
        TbcDecodedField? previousField,
        int? fieldPhaseId,
        int? previousFieldPhaseId)
    {
        if (TryReadTbcSlice(field.Samples, session, line: 11, startUsec: 15.0, lengthUsec: 40.0, out double[] whiteFlag)
            && InRange(MeanIreNumpyFloat64(whiteFlag, session), 92.0, 108.0))
        {
            AddRawMetric(metrics, "ntscWhiteFlagSNR", CalcPeakSnr(whiteFlag, session));
        }

        if (TryComputeNtscLine19ColorInfo(session, field.Samples, fieldPhaseId ?? field.FieldPhaseId ?? 1, previousSamples: null, previousFieldPhaseId: null, out LaserDiscNtscLine19ColorInfo colorInfo))
        {
            AddRawMetric(metrics, "ntscLine19ColorPhase", colorInfo.PhaseDegrees);
            AddRawMetric(metrics, "ntscLine19ColorRawSNR", colorInfo.RawSnr);
        }

        if (TryReadTbcSlice(field.Samples, session, line: 19, startUsec: 36.0, lengthUsec: 10.0, out double[] grey))
        {
            AddRawMetric(metrics, "greyPSNR", CalcPeakSnr(grey, session));
            AddRawMetric(metrics, "greyIRE", MeanIreNumbaFloat64(grey, session));
        }

        if (TryReadPreTbcSlice(
                session,
                field,
                isFirstField,
                line: 19,
                startUsec: 36.0,
                lengthUsec: 10.0,
                delaySamples: (int)session.Filters.LdVideoWhiteOffset,
                source: field.RawInputSamples,
                out double[] greyRawValues))
        {
            AddRawMetric(metrics, "greyRFLevel", StandardDeviation(greyRawValues));
        }

        if (!isFirstField
            && previousField is { Samples.Length: > 0 }
            && TryComputeNtscLine19ColorInfo(
                session,
                field.Samples,
                fieldPhaseId ?? field.FieldPhaseId ?? 1,
                previousField.Samples,
                previousFieldPhaseId ?? previousField.FieldPhaseId ?? 1,
                out LaserDiscNtscLine19ColorInfo color3dInfo))
        {
            AddRawMetric(metrics, "ntscLine19Burst70IRE", color3dInfo.Level);
            AddRawMetric(metrics, "ntscLine19Color3DRawSNR", color3dInfo.RawSnr);
            if (TryReadTbcSlice(field.Samples, session, line: 19, startUsec: 5.5, lengthUsec: 2.4, out double[] currentBurst)
                && TryReadTbcSlice(previousField.Samples, session, line: 19, startUsec: 5.5, lengthUsec: 2.4, out double[] previousBurst)
                && currentBurst.Length == previousBurst.Length)
            {
                var diff = new double[currentBurst.Length];
                for (int i = 0; i < diff.Length; i++)
                {
                    diff[i] = (currentBurst[i] - previousBurst[i]) / 2.0;
                }

                AddRawMetric(
                    metrics,
                    "ntscLine19Burst0IRE",
                    Math.Sqrt(2.0) * StandardDeviationNumbaFloat64(diff) / session.VideoOutput.OutputScale);
            }
        }
    }

    private static void AddLaserDiscPalVerboseTbcMetrics(
        Dictionary<string, double> metrics,
        DecodeSession session,
        TbcDecodedField field,
        bool isFirstField)
    {
        if (isFirstField)
        {
            if (TryReadTbcSlice(field.Samples, session, line: 13, startUsec: 20.2, lengthUsec: 3.0, out double[] grey))
            {
                AddRawMetric(metrics, "greyPSNR", CalcPeakSnr(grey, session));
                AddRawMetric(metrics, "greyIRE", MeanIreNumbaFloat64(grey, session));
            }
        }
        else if (TryReadTbcSlice(field.Samples, session, line: 13, startUsec: 36.0, lengthUsec: 20.0, out double[] burst50))
        {
            AddRawMetric(
                metrics,
                "palVITSBurst50Level",
                StandardDeviationNumbaFloat64(burst50) / session.VideoOutput.OutputScale);
        }
    }

    private static bool TryReadTbcSlice(
        DecodeSession session,
        TbcDecodedField field,
        string propertyName,
        out double[] values)
    {
        values = [];
        if (!session.Parameters.SysParams.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() < 3)
        {
            return false;
        }

        return TryReadTbcSlice(
            field.Samples,
            session,
            property[0].GetInt32(),
            property[1].GetDouble(),
            property[2].GetDouble(),
            out values);
    }

    private static bool TryReadTbcSlice(
        ushort[] samples,
        DecodeSession session,
        int line,
        double startUsec,
        double lengthUsec,
        out double[] values)
    {
        values = [];
        if (!TryGetTbcSliceRange(session, line, startUsec, lengthUsec, samples.Length, out int start, out int end))
        {
            return false;
        }

        values = new double[end - start];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = samples[start + i];
        }

        return true;
    }

    private static bool TryReadTbcSlice(
        double[] samples,
        DecodeSession session,
        int line,
        double startUsec,
        double lengthUsec,
        out double[] values)
    {
        values = [];
        if (!TryGetTbcSliceRange(session, line, startUsec, lengthUsec, samples.Length, out int start, out int end))
        {
            return false;
        }

        values = samples[start..end];
        return true;
    }

    private static bool TryReadMetricTbcSlice(
        DecodeSession session,
        TbcDecodedField field,
        double[]? rawTbcSamples,
        string propertyName,
        out double[] values)
    {
        values = [];
        if (!session.Parameters.SysParams.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() < 3)
        {
            return false;
        }

        int line = property[0].GetInt32();
        double startUsec = property[1].GetDouble();
        double lengthUsec = property[2].GetDouble();
        return rawTbcSamples is null
            ? TryReadTbcSlice(field.Samples, session, line, startUsec, lengthUsec, out values)
            : TryReadTbcSlice(rawTbcSamples, session, line, startUsec, lengthUsec, out values);
    }

    private static double[]? ReadFloat32TbcPayload(TbcDecodedField field)
    {
        if (field.OutputPayload is not { SampleFormat: TbcOutputSampleFormat.Float32 } payload
            || payload.SampleCount != field.Samples.Length)
        {
            return null;
        }

        var samples = new double[payload.SampleCount];
        ReadOnlySpan<byte> bytes = payload.Bytes;
        for (int i = 0; i < samples.Length; i++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * sizeof(float), sizeof(float)));
            samples[i] = BitConverter.Int32BitsToSingle(bits);
        }

        return samples;
    }

    internal static bool TryComputeNtscLine19ColorInfo(
        DecodeSession session,
        ushort[] samples,
        int fieldPhaseId,
        ushort[]? previousSamples,
        int? previousFieldPhaseId,
        out LaserDiscNtscLine19ColorInfo info)
    {
        info = new LaserDiscNtscLine19ColorInfo(0.0, 0.0, 0.0);
        if (!TryGetTbcSliceRange(session, line: 19, startUsec: 14.0, lengthUsec: 18.0, samples.Length, out int gateStart, out int gateEnd)
            || !IsNtscLine19ColorGateValid(samples, gateStart, gateEnd))
        {
            return false;
        }

        if (!TryGetTbcSliceRange(session, line: 19, startUsec: 0.0, lengthUsec: 40.0, samples.Length, out int lineStart, out int lineEnd))
        {
            return false;
        }

        float[] cbuffer = BuildNtscCombBuffer(samples, lineStart, lineEnd);
        if (previousSamples is not null)
        {
            if (!TryGetTbcSliceRange(session, line: 19, startUsec: 14.0, lengthUsec: 18.0, previousSamples.Length, out int previousGateStart, out int previousGateEnd)
                || !IsNtscLine19ColorGateValid(previousSamples, previousGateStart, previousGateEnd)
                || !TryGetTbcSliceRange(session, line: 19, startUsec: 0.0, lengthUsec: 40.0, previousSamples.Length, out int previousLineStart, out int previousLineEnd))
            {
                return false;
            }

            float[] previousCbuffer = BuildNtscCombBuffer(previousSamples, previousLineStart, previousLineEnd);
            int length = Math.Min(cbuffer.Length, previousCbuffer.Length);
            if (length == 0)
            {
                return false;
            }

            if (cbuffer.Length != length)
            {
                Array.Resize(ref cbuffer, length);
            }

            for (int i = 0; i < length; i++)
            {
                cbuffer[i] = (cbuffer[i] - previousCbuffer[i]) / 2.0f;
            }
        }

        SplitNtscCombIq(cbuffer, line: 19, fieldPhaseId: fieldPhaseId, out float[] si, out float[] sq);
        const int statsStart = 110;
        const int statsEnd = 230;
        if (si.Length < statsEnd || sq.Length < statsEnd)
        {
            return false;
        }

        int count = statsEnd - statsStart;
        var chromaMagnitude = new float[count];
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = statsStart + i;
            float iValue = si[sourceIndex];
            float qValue = sq[sourceIndex];
            chromaMagnitude[i] = MathF.Sqrt((iValue * iValue) + (qValue * qValue));
        }

        float siMean = NumpyReduction.MeanFloat32(si.AsSpan(statsStart, count));
        float sqMean = NumpyReduction.MeanFloat32(sq.AsSpan(statsStart, count));
        (float signal, float noise) = NumpyReduction.MeanStandardDeviationFloat32(chromaMagnitude);
        float phase = (MathF.Atan2(siMean, sqMean) * 180.0f) / (float)Math.PI;
        if (phase < 0.0)
        {
            phase += 360.0f;
        }

        float rawSnr = 20.0f * MathF.Log10(signal / noise);
        info = new LaserDiscNtscLine19ColorInfo(signal / (2.0 * session.VideoOutput.OutputScale), phase, rawSnr);
        return true;
    }

    private static bool IsNtscLine19ColorGateValid(ushort[] samples, int start, int end)
    {
        ushort min = ushort.MaxValue;
        ushort max = ushort.MinValue;
        for (int i = start; i < end; i++)
        {
            min = Math.Min(min, samples[i]);
            max = Math.Max(max, samples[i]);
        }

        return max < 100.0 && min > 40.0;
    }

    private static float[] BuildNtscCombBuffer(ushort[] samples, int start, int end)
    {
        var data = new float[end - start];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = samples[start + i];
        }

        var cbuffer = new float[data.Length];
        for (int i = 2; i < cbuffer.Length - 2; i++)
        {
            cbuffer[i] = ((data[i - 2] + data[i + 2]) / 2.0f) - data[i];
        }

        return cbuffer;
    }

    private static void SplitNtscCombIq(float[] cbuffer, int line, int fieldPhaseId, out float[] si, out float[] sq)
    {
        sq = new float[(cbuffer.Length + 1) / 2];
        si = new float[cbuffer.Length / 2];
        for (int i = 0; i < sq.Length; i++)
        {
            sq[i] = cbuffer[i * 2];
        }

        for (int i = 0; i < si.Length; i++)
        {
            si[i] = cbuffer[(i * 2) + 1];
        }

        bool linePhase = (line % 2) == 0
            ? fieldPhaseId is 1 or 4
            : fieldPhaseId is 2 or 3;
        if (!linePhase)
        {
            for (int i = 0; i < si.Length; i += 2)
            {
                si[i] = -si[i];
            }

            for (int i = 1; i < sq.Length; i += 2)
            {
                sq[i] = -sq[i];
            }
        }
        else
        {
            for (int i = 1; i < si.Length; i += 2)
            {
                si[i] = -si[i];
            }

            for (int i = 0; i < sq.Length; i += 2)
            {
                sq[i] = -sq[i];
            }
        }
    }

    private static double MeanPreTbcIreNumpyFloat32(
        IReadOnlyList<double> values,
        VideoOutputConverter converter)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        float mean = PairwiseSumNumpyFloat32(values, 0, values.Count) / values.Count;
        return (mean - (float)converter.Ire0) / (float)converter.HzIre;
    }

    private static float PairwiseSumNumpyFloat32(
        IReadOnlyList<double> values,
        int start,
        int count)
    {
        // NumPy reduces float32 means recursively in 128-value blocks with eight accumulators.
        const int pairwiseBlockSize = 128;
        if (count < 8)
        {
            float sum = -0.0f;
            for (int i = 0; i < count; i++)
            {
                sum += (float)values[start + i];
            }

            return sum;
        }

        if (count <= pairwiseBlockSize)
        {
            Span<float> lanes = stackalloc float[8];
            for (int lane = 0; lane < lanes.Length; lane++)
            {
                lanes[lane] = (float)values[start + lane];
            }

            int index = 8;
            int vectorizedEnd = count - (count % lanes.Length);
            for (; index < vectorizedEnd; index += lanes.Length)
            {
                for (int lane = 0; lane < lanes.Length; lane++)
                {
                    lanes[lane] += (float)values[start + index + lane];
                }
            }

            float sum = ((lanes[0] + lanes[1]) + (lanes[2] + lanes[3]))
                + ((lanes[4] + lanes[5]) + (lanes[6] + lanes[7]));
            for (; index < count; index++)
            {
                sum += (float)values[start + index];
            }

            return sum;
        }

        int leftCount = count / 2;
        leftCount -= leftCount % 8;
        return PairwiseSumNumpyFloat32(values, start, leftCount)
            + PairwiseSumNumpyFloat32(values, start + leftCount, count - leftCount);
    }

    private static bool TryGetTbcSliceRange(
        DecodeSession session,
        int line,
        double startUsec,
        double lengthUsec,
        int sampleLength,
        out int start,
        out int end)
    {
        start = 0;
        end = 0;
        double begin = ((line - 1) * session.TbcFrameSpec.OutputLineLength)
            + (startUsec * JsonDouble(session.Parameters.SysParams, "outfreq"));
        start = (int)Math.Round(begin, MidpointRounding.ToEven);
        end = (int)Math.Round(
            begin + (lengthUsec * JsonDouble(session.Parameters.SysParams, "outfreq")),
            MidpointRounding.ToEven);
        return line > 0 && start >= 0 && end > start && end <= sampleLength;
    }

    private static bool TryReadPreTbcSlice(
        DecodeSession session,
        TbcDecodedField field,
        bool isFirstField,
        string propertyName,
        int delaySamples,
        double[]? source,
        out double[] values)
    {
        values = [];
        if (!session.Parameters.SysParams.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() < 3)
        {
            return false;
        }

        return TryReadPreTbcSlice(
            session,
            field,
            isFirstField,
            property[0].GetInt32(),
            property[1].GetDouble(),
            property[2].GetDouble(),
            delaySamples,
            source,
            out values);
    }

    private static bool TryReadPreTbcSlice(
        DecodeSession session,
        TbcDecodedField field,
        bool isFirstField,
        int line,
        double startUsec,
        double lengthUsec,
        int delaySamples,
        double[]? source,
        out double[] values)
    {
        values = [];
        if (source is null || source.Length == 0 || lengthUsec <= 0.0)
        {
            return false;
        }

        double[] lineLocations = field.LineLocations.Locations;
        int physicalLine = line + LaserDiscPreTbcLineOffset(session, isFirstField);
        if (physicalLine <= 0 || physicalLine >= lineLocations.Length)
        {
            return false;
        }

        double nominalSamplesPerUsec = session.DecodeSampleRateHz / 1_000_000.0;
        double localLineLength = physicalLine + 1 < lineLocations.Length
            ? (lineLocations[physicalLine + 1] - lineLocations[physicalLine - 1]) / 2.0
            : lineLocations[physicalLine] - lineLocations[physicalLine - 1];
        double linePeriodUsec = JsonDouble(session.Parameters.SysParams, "line_period");
        double startSamplesPerUsec = double.IsFinite(localLineLength) && localLineLength > 0.0 && linePeriodUsec > 0.0
            ? localLineLength / linePeriodUsec
            : nominalSamplesPerUsec;
        double begin = lineLocations[physicalLine] + (startUsec * startSamplesPerUsec) - delaySamples;
        double end = begin + (lengthUsec * nominalSamplesPerUsec) + 1.0;
        int start = (int)Math.Floor(begin);
        int endIndex = (int)Math.Floor(end);
        if (start < 0 || endIndex <= start || endIndex > source.Length)
        {
            return false;
        }

        values = new double[endIndex - start];
        Array.Copy(source, start, values, 0, values.Length);
        return true;
    }

    private static IEnumerable<(int Line, double StartUsec, double LengthUsec)> ReadSliceTuples(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement tuple in property.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 3)
            {
                continue;
            }

            yield return (tuple[0].GetInt32(), tuple[1].GetDouble(), tuple[2].GetDouble());
        }
    }

    private static double CalcPeakSnr(
        double[] outputValues,
        DecodeSession session,
        bool quantizedOutput = true)
    {
        double[] ireValues = ToIre(outputValues, session, quantizedOutput);
        double noise = StandardDeviation(ireValues);
        if (noise == 0.0)
        {
            return session.Spec.Name == "ld" ? double.PositiveInfinity : 0.0;
        }

        return 20.0 * Math.Log10(100.0 / noise);
    }

    private static double MeanIreNumpyFloat64(
        double[] outputValues,
        DecodeSession session,
        bool quantizedOutput = true)
    {
        double[] ireValues = ToIre(
            outputValues,
            session,
            quantizedOutput,
            wrapQuantizedSubtraction: true);
        return NumpyReduction.MeanFloat64(ireValues);
    }

    private static double MeanIreNumbaFloat64(
        double[] outputValues,
        DecodeSession session,
        bool quantizedOutput = true)
    {
        double[] ireValues = ToIre(
            outputValues,
            session,
            quantizedOutput,
            wrapQuantizedSubtraction: true);
        return NumbaReduction.MeanFloat64(ireValues);
    }

    private static double MeanPostTbcIreNumpy(
        double[] outputValues,
        DecodeSession session,
        bool quantizedOutput)
    {
        double meanOutput = quantizedOutput
            ? NumpyReduction.MeanFloat64(outputValues)
            : NumpyReduction.MeanFloat32(outputValues);
        return ((meanOutput - session.VideoOutput.OutputZero) / session.VideoOutput.OutputScale)
            + session.VideoOutput.VSyncIre;
    }

    private static double[] ToIre(
        double[] outputValues,
        DecodeSession session,
        bool quantizedOutput = true,
        bool wrapQuantizedSubtraction = false)
    {
        var ire = new double[outputValues.Length];
        for (int i = 0; i < outputValues.Length; i++)
        {
            if (!quantizedOutput)
            {
                ire[i] = ((outputValues[i] - session.VideoOutput.OutputZero) / session.VideoOutput.OutputScale)
                    + session.VideoOutput.VSyncIre;
                continue;
            }

            ushort output = (ushort)Math.Clamp(outputValues[i], ushort.MinValue, ushort.MaxValue);
            if (wrapQuantizedSubtraction)
            {
                ire[i] = session.VideoOutput.OutputToIreWithUInt16Subtraction(output);
            }
            else
            {
                ire[i] = session.VideoOutput.OutputToIre(output);
            }
        }

        return ire;
    }

    private static void AddRawMetric(Dictionary<string, double> metrics, string name, double value)
    {
        metrics[name] = value;
    }

    private static IReadOnlyDictionary<string, double> RoundMetrics(
        IReadOnlyDictionary<string, double> metrics,
        bool verbose)
    {
        IEnumerable<string> names = verbose ? metrics.Keys : ["wSNR", "bPSNR"];
        var roundedMetrics = new Dictionary<string, double>();
        foreach (string name in names)
        {
            if (!metrics.TryGetValue(name, out double value))
            {
                continue;
            }

            double rounded = RoundMetadataFloat(value, MetricRoundingPlaces(name));
            if (double.IsFinite(rounded))
            {
                roundedMetrics[name] = rounded;
            }
        }

        return roundedMetrics;
    }

    private static int MetricRoundingPlaces(string name)
    {
        if (name.Contains("Ratio", StringComparison.Ordinal))
        {
            return 4;
        }

        return name.Contains("Burst", StringComparison.Ordinal) ? 3 : 1;
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
        => StandardDeviationNumpyFloat64(values);

    private static double StandardDeviationNumbaFloat64(ReadOnlySpan<double> values)
        => NumbaReduction.MeanStandardDeviationFloat64(values).StandardDeviation;

    internal static double StandardDeviationNumpyFloat64(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        double mean = PairwiseSumNumpyFloat64(values, 0, values.Count) / values.Count;
        var squaredDistances = new double[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            double distance = values[i] - mean;
            squaredDistances[i] = distance * distance;
        }

        double variance = PairwiseSumNumpyFloat64(
            squaredDistances,
            0,
            squaredDistances.Length) / squaredDistances.Length;
        return Math.Sqrt(variance);
    }

    private static double PairwiseSumNumpyFloat64(
        IReadOnlyList<double> values,
        int start,
        int count)
    {
        const int pairwiseBlockSize = 128;
        if (count < 8)
        {
            double sum = -0.0;
            for (int i = 0; i < count; i++)
            {
                sum += values[start + i];
            }

            return sum;
        }

        if (count <= pairwiseBlockSize)
        {
            Span<double> lanes = stackalloc double[8];
            for (int lane = 0; lane < lanes.Length; lane++)
            {
                lanes[lane] = values[start + lane];
            }

            int index = 8;
            int vectorizedEnd = count - (count % lanes.Length);
            for (; index < vectorizedEnd; index += lanes.Length)
            {
                for (int lane = 0; lane < lanes.Length; lane++)
                {
                    lanes[lane] += values[start + index + lane];
                }
            }

            double sum = ((lanes[0] + lanes[1]) + (lanes[2] + lanes[3]))
                + ((lanes[4] + lanes[5]) + (lanes[6] + lanes[7]));
            for (; index < count; index++)
            {
                sum += values[start + index];
            }

            return sum;
        }

        int leftCount = count / 2;
        leftCount -= leftCount % 8;
        return PairwiseSumNumpyFloat64(values, start, leftCount)
            + PairwiseSumNumpyFloat64(values, start + leftCount, count - leftCount);
    }

    private static bool InRange(double value, double minimum, double maximum)
        => value >= minimum && value <= maximum;

    private static int LaserDiscFieldPhaseCount(DecodeSession session)
        => Math.Max(1, JsonInt(session.Parameters.SysParams, "fieldPhases") ?? 1);

    private static int LaserDiscPreTbcLineOffset(DecodeSession session, bool isFirstField)
        => string.Equals(session.System, "PAL", StringComparison.OrdinalIgnoreCase)
            ? isFirstField ? 2 : 3
            : 0;

    private static void AddIfPresent(JsonObject target, string name, int? value)
    {
        if (value.HasValue)
        {
            target[name] = value.Value;
        }
    }

    private static string MetadataSystemName(DecodeSession session)
    {
        if ((session.Spec.Name is "vhs" or "cvbs")
            && session.System is "PAL_M" or "PALM" or "NLINHA")
        {
            return "PAL-M";
        }

        if (session.Spec.Name == "vhs")
        {
            return FormatCatalog.ParentSystem(session.System);
        }

        return session.System;
    }

    private static JsonArray ToJsonArray(int[] values)
    {
        JsonArray array = [.. values];
        return array;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, double> values)
    {
        var obj = new JsonObject();
        foreach ((string key, double value) in values)
        {
            if (double.IsFinite(value))
            {
                obj[key] = value;
            }
        }

        return obj;
    }

    private static double JsonDouble(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetDouble();
    }

    private static int? JsonInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind != JsonValueKind.Null
            ? property.GetInt32()
            : null;
    }
}
