using System.Numerics;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Decode;

public sealed record TbcFieldSequenceDecodeResult(
    bool Success,
    string Message,
    TbcOutputPaths? Paths,
    IReadOnlyList<TbcDecodedField> Fields,
    int WrittenFieldCount = 0,
    LdTestLdfWriteResult? TestLdf = null);

public delegate TbcDecodedField? TbcFieldSequenceReadField(
    DecodeSession session,
    Stream input,
    long begin,
    int readLength,
    int fieldNumber);

public sealed class TbcFieldSequenceDecodeEngine
{
    private const int TestLdfLookaheadSamples = 1_100_000;
    private const int LaserDiscLeadOutCode = 0x80EEEE;
    private const int LaserDiscLeadOutRequiredCount = 2;
    private readonly ILdTestLdfWriter _testLdfWriter;
    private readonly ILaserDiscEfmOutputWriter _efmOutputWriter;
    private readonly TbcFieldSequenceReadField _readField;
    private sealed record SequenceDecodeSummary(
        IReadOnlyList<TbcDecodedField> Fields,
        int DecodedFieldCount,
        long? FirstDecodedSample,
        long? EndDecodedSample);

    public TbcFieldSequenceDecodeEngine(
        int extraReadLines = 3,
        ILdTestLdfWriter? testLdfWriter = null,
        ILaserDiscEfmOutputWriter? efmOutputWriter = null,
        TbcFieldSequenceReadField? readField = null)
    {
        if (extraReadLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraReadLines));
        }

        ExtraReadLines = extraReadLines;
        _testLdfWriter = testLdfWriter ?? new FfmpegLdTestLdfWriter();
        _efmOutputWriter = efmOutputWriter ?? new LaserDiscEfmOutputWriter();
        _readField = readField ?? ReadFieldFromSession;
    }

    public int ExtraReadLines { get; }

    public TbcFieldSequenceDecodeResult TryDecodeAndWrite(DecodeSession session, int? maxFields = null)
    {
        try
        {
            if (session.InputFile == "-")
            {
                return DecodeAndWrite(session, Console.OpenStandardInput(), maxFields);
            }

            if (!File.Exists(session.InputFile))
            {
                return Fail($"Input file was not found: {session.InputFile}");
            }

            using FileStream input = File.OpenRead(session.InputFile);
            return DecodeAndWrite(session, input, maxFields);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    public TbcFieldSequenceDecodeResult TryDecodeAndWrite(
        DecodeSession session,
        Stream input,
        int? maxFields = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return DecodeAndWrite(session, input, maxFields);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    private TbcFieldSequenceDecodeResult DecodeAndWrite(
        DecodeSession session,
        Stream input,
        int? maxFields)
    {
        StreamingOutputSession? output = null;
        try
        {
            SequenceDecodeSummary summary = DecodeSequence(
                session,
                input,
                maxFields,
                retainFields: false,
                writeFields: writes =>
                {
                    output ??= new StreamingOutputSession(session, _efmOutputWriter);
                    output.Write(writes);
                });
            output ??= new StreamingOutputSession(session, _efmOutputWriter);
            TbcFieldSequenceDecodeResult result = output.Complete();
            LdTestLdfWriteResult? testLdf = WriteOptionalTestLdf(session, input, summary);
            return testLdf.HasValue
                ? result with
                {
                    Message = result.Message + "; " + testLdf.Value.Message,
                    TestLdf = testLdf
                }
                : result;
        }
        finally
        {
            output?.Dispose();
        }
    }

    public IReadOnlyList<TbcDecodedField> DecodeFields(DecodeSession session, Stream input, int? maxFields = null)
        => DecodeSequence(session, input, maxFields, retainFields: true, writeFields: null).Fields;

    private SequenceDecodeSummary DecodeSequence(
        DecodeSession session,
        Stream input,
        int? maxFields,
        bool retainFields,
        Action<IReadOnlyList<(TbcDecodedField Field, TbcFieldOrderDecision Decision)>>? writeFields)
    {
        int requestedFields = maxFields ?? session.RunBounds.RequestedFieldCount;
        if (requestedFields < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFields));
        }

        if (requestedFields == 0)
        {
            return new SequenceDecodeSummary([], 0, null, null);
        }

        int readLength = DecodeReadWindowPlanner.EstimateReadSampleCount(session, ExtraReadLines);
        var fields = retainFields ? new List<TbcDecodedField>() : null;
        var writePlanner = new FieldWritePlanner(session, retainWrites: false);
        long begin = ResolveInitialDecodeStart(session, input, readLength);
        long? firstDecodedSample = null;
        long? endDecodedSample = null;
        int decodedFieldCount = 0;
        long laserDiscWrittenFieldCount = 0;
        int laserDiscLeadOutCount = 0;
        bool haveFirstTapeField = false;
        string? pendingTapeFrameStatus = null;
        LaserDiscAutoMtfController? autoMtf = session.Spec.Name == "ld"
            ? new LaserDiscAutoMtfController()
            : null;

        while (maxFields.HasValue
            ? decodedFieldCount < requestedFields
            : writePlanner.WrittenFieldCount < requestedFields)
        {
            TbcDecodedField? field;
            TbcFieldDecodeState? fieldState = autoMtf is null
                ? null
                : session.TbcFieldDecoder.CaptureState();
            try
            {
                field = _readField(session, input, begin, readLength, decodedFieldCount);
                if (field is not null && autoMtf is not null)
                {
                    LaserDiscMtfUpdate mtfUpdate = autoMtf.Observe(field.BlackToWhiteRfRatio);
                    if (field.BlackToWhiteRfRatio.HasValue && mtfUpdate.Level != mtfUpdate.PreviousLevel)
                    {
                        ApplyLaserDiscMtf(session, mtfUpdate.Level);
                    }

                    if (mtfUpdate.RequiresRetry || field.LaserDiscAgcAdjusted)
                    {
                        session.TbcFieldDecoder.RestoreStateForRetry(fieldState!);
                        field = _readField(session, input, begin, readLength, decodedFieldCount);
                    }
                }
            }
            catch (TbcFieldDecodeRecoveryException ex)
            {
                LogRecovery(session, ex);
                if (ex.StopAfterDecodedFields && decodedFieldCount > 0)
                {
                    break;
                }

                begin = Math.Max(0L, checked(begin + ex.SuggestedOffsetSamples));
                continue;
            }
            catch (InvalidOperationException)
            {
                if (decodedFieldCount == 0)
                {
                    throw;
                }

                break;
            }

            if (pendingTapeFrameStatus is not null)
            {
                DecodeSessionLogWriter.Append(session, "DEBUG", pendingTapeFrameStatus);
                pendingTapeFrameStatus = null;
            }

            if (field is null)
            {
                break;
            }

            fields?.Add(field);
            decodedFieldCount++;
            firstDecodedSample ??= field.StartSample;
            endDecodedSample = EstimateNextFieldStart(session, field);
            IReadOnlyList<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> writes = writePlanner.Add(field);
            if (writes.Count > 0)
            {
                if (session.Spec.Name == "ld")
                {
                    foreach ((TbcDecodedField writtenField, _) in writes)
                    {
                        session.TbcFieldDecoder.CommitLaserDiscAnalogAudioWrite(
                            writtenField,
                            laserDiscWrittenFieldCount++);
                    }
                }

                writeFields?.Invoke(writes);
            }

            autoMtf?.ObserveAcceptedField(field, session.System);
            bool isFirstField = field.DetectedFirstField ?? ((decodedFieldCount - 1) % 2 == 0);
            if (session.Spec.Name == "cvbs" && !isFirstField)
            {
                int rawFrame = checked((int)Math.Floor(ComputeFieldDiskLocation(session, field) / 2.0));
                DecodeSessionLogWriter.Append(
                    session,
                    "DEBUG",
                    $"File Frame {rawFrame}: CAV Pulldown/Telecine Frame");
            }

            if (session.Spec.Name == "vhs")
            {
                if (isFirstField)
                {
                    haveFirstTapeField = true;
                }
                else if (haveFirstTapeField)
                {
                    int rawFrame = checked((int)Math.Floor(ComputeFieldDiskLocation(session, field) / 2.0));
                    pendingTapeFrameStatus = $"File Frame {rawFrame}: {session.Parameters.TapeFormat} ";
                }
            }

            if (ShouldStopAfterLaserDiscLeadOut(
                    session,
                    field,
                    isFirstField,
                    ref laserDiscLeadOutCount))
            {
                break;
            }

            long nextBegin = endDecodedSample.Value;
            if (nextBegin <= begin)
            {
                throw new InvalidOperationException("Decoded field did not advance the input position.");
            }

            begin = nextBegin;
        }

        bool reachedRequestedTapeOutput = !maxFields.HasValue
            && session.Spec.Name == "vhs"
            && writePlanner.WrittenFieldCount >= requestedFields;
        if (reachedRequestedTapeOutput && pendingTapeFrameStatus is not null)
        {
            try
            {
                _ = _readField(session, input, begin, readLength, decodedFieldCount);
            }
            catch (TbcFieldDecodeRecoveryException ex)
            {
                LogRecovery(session, ex);
            }
            catch (InvalidOperationException)
            {
                // Upstream may have one producer field in flight after the requested output is complete.
            }
        }

        if (pendingTapeFrameStatus is not null)
        {
            DecodeSessionLogWriter.Append(session, "DEBUG", pendingTapeFrameStatus);
        }

        return new SequenceDecodeSummary(
            fields ?? [],
            decodedFieldCount,
            firstDecodedSample,
            endDecodedSample);
    }

    private static void LogRecovery(DecodeSession session, TbcFieldDecodeRecoveryException exception)
    {
        if (session.Spec.Name != "vhs")
        {
            return;
        }

        string? message = exception.Kind switch
        {
            TbcFieldDecodeRecoveryKind.NoSyncPulses => "Unable to find any sync pulses, jumping 100 ms",
            TbcFieldDecodeRecoveryKind.NoFirstHSync => "Unable to determine start of field - dropping field",
            _ => null
        };
        if (message is not null)
        {
            DecodeSessionLogWriter.Append(session, "ERROR", message);
        }
    }

    private static void ApplyLaserDiscMtf(DecodeSession session, double targetMtf)
    {
        Complex[] response = DecodeFilterSetBuilder.BuildLaserDiscMtf(
            session.Parameters,
            session.FilterOptions,
            targetMtf,
            session.DecodeSampleRateHz,
            session.BlockLength);
        if (response.Length != session.Filters.RfMtf.Length
            || response.Length != session.Filters.RfMtfMagnitude.Length)
        {
            throw new InvalidOperationException("Dynamic LD MTF response length did not match the active RF filter set.");
        }

        for (int i = 0; i < response.Length; i++)
        {
            session.Filters.RfMtf[i] = response[i];
            session.Filters.RfMtfMagnitude[i] = response[i].Magnitude;
        }
    }

    private static TbcDecodedField? ReadFieldFromSession(
        DecodeSession session,
        Stream input,
        long begin,
        int readLength,
        int fieldNumber)
    {
        DecodeReadWindow window = DecodeReadWindowPlanner.Resolve(session, begin, readLength);
        RfDecodedSpan? span = session.StreamDecoder.Read(input, window.StartSample, window.SampleCount);
        return span is null
            ? null
            : session.TbcFieldDecoder.Decode(span, fieldNumber: fieldNumber);
    }

    private long ResolveInitialDecodeStart(DecodeSession session, Stream input, int readLength)
    {
        if (session.Spec.Name == "cvbs" && session.ExecutionOptions.SeekFrame >= 0)
        {
            throw new InvalidOperationException("ERROR: Seeking failed");
        }

        if (session.Spec.Name != "ld" || session.ExecutionOptions.SeekFrame < 0)
        {
            return session.RunBounds.StartSample;
        }

        int targetFrame = session.ExecutionOptions.SeekFrame;
        long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
        long current = session.RunBounds.StartSample > 0
            ? session.RunBounds.StartSample
            : checked(targetFrame * 2L * nominalFieldSamples);

        for (int retry = 0; retry < 3; retry++)
        {
            if (!TryReadLaserDiscFrameNumber(session, input, readLength, current, out int frameNumber, out long frameStart))
            {
                if (current != 0)
                {
                    current = 0;
                    continue;
                }

                break;
            }

            if (frameNumber == targetFrame)
            {
                return frameStart;
            }

            current = Math.Max(0L, checked(current + (((targetFrame - frameNumber) * 2L - 1L) * nominalFieldSamples)));
        }

        throw new InvalidOperationException("ERROR: Seeking failed");
    }

    private bool TryReadLaserDiscFrameNumber(
        DecodeSession session,
        Stream input,
        int readLength,
        long begin,
        out int frameNumber,
        out long frameStart)
    {
        frameNumber = 0;
        frameStart = begin;
        TbcDecodedField? first = _readField(session, input, begin, readLength, fieldNumber: 0);
        if (first is null)
        {
            return false;
        }

        long secondBegin = EstimateNextFieldStart(session, first);
        TbcDecodedField? second = _readField(session, input, secondBegin, readLength, fieldNumber: 1);
        if (second is null)
        {
            return false;
        }

        int framesPerSecond = string.Equals(session.System, "PAL", StringComparison.OrdinalIgnoreCase) ? 25 : 30;
        LaserDiscVbiInterpretation interpretation = LaserDiscVbiInterpreter.Interpret(
            (first.VbiData ?? []).Concat(second.VbiData ?? []),
            framesPerSecond);
        if (!interpretation.FrameNumber.HasValue || interpretation.IsEarlyClv)
        {
            return false;
        }

        frameNumber = interpretation.FrameNumber.Value;
        frameStart = first.StartSample;
        return true;
    }

    public static bool ShouldStopAfterLaserDiscLeadOut(
        DecodeSession session,
        TbcDecodedField field,
        bool isFirstField,
        ref int leadOutCodeCount)
    {
        if (session.Spec.Name != "ld" || session.ExecutionOptions.IgnoreLeadOut || field.VbiData is null)
        {
            return false;
        }

        if (isFirstField)
        {
            leadOutCodeCount = 0;
        }

        foreach (int code in field.VbiData)
        {
            if (code == LaserDiscLeadOutCode)
            {
                leadOutCodeCount++;
            }
        }

        return !isFirstField && leadOutCodeCount >= LaserDiscLeadOutRequiredCount;
    }

    public TbcFieldSequenceDecodeResult WriteDecodedFields(DecodeSession session, IReadOnlyList<TbcDecodedField> fields)
    {
        TbcOutputPaths paths = TbcFirstFieldDecodeEngine.BuildOutputPaths(session);
        List<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> writes = BuildWrites(session, fields);
        var writtenFields = new List<TbcDecodedField>(writes.Count);
        var writtenOrder = new List<TbcFieldOrderDecision>(writes.Count);
        foreach ((TbcDecodedField field, TbcFieldOrderDecision decision) in writes)
        {
            if (field.Samples.Length != session.TbcFrameSpec.FieldSampleCount)
            {
                throw new ArgumentException(
                    $"Decoded field sample count {field.Samples.Length} does not match TBC frame spec {session.TbcFrameSpec.FieldSampleCount} for {paths.TbcPath}.");
            }

            writtenFields.Add(field);
            writtenOrder.Add(decision);
        }
        using (FileStream tbc = File.Create(paths.TbcPath))
        {
            foreach (TbcDecodedField field in writtenFields)
            {
                TbcOutputWriter.WriteFrame(tbc, field.Samples, session.TbcFrameSpec, field.OutputPayload);
            }
        }

        if (writtenFields.Count == 0 && session.ChromaOptions?.WriteChroma == true)
        {
            File.Create(paths.ChromaPath!).Dispose();
        }
        else if (TbcFirstFieldDecodeEngine.ShouldWriteChroma(session, writtenFields))
        {
            TbcFirstFieldDecodeEngine.WriteChromaFields(session, writtenFields, paths.ChromaPath);
        }

        IReadOnlyList<TbcDecodedField> metadataFields = _efmOutputWriter.Write(session, writtenFields);
        TbcOutputMetadataWriter.WriteJson(session, metadataFields, paths.JsonPath, writtenOrder);
        if (session.Spec.Name == "ld" || session.ExecutionOptions.WriteDebugData)
        {
            TbcSqliteMetadataWriter.Write(session, metadataFields, paths.DbPath!, writtenOrder);
        }

        return new TbcFieldSequenceDecodeResult(
            true,
            $"Wrote {writtenFields.Count} TBC field(s) to {paths.TbcPath}",
            paths,
            fields,
            writtenFields.Count);
    }

    public LdTestLdfWriteResult? WriteOptionalTestLdf(
        DecodeSession session,
        Stream input,
        IReadOnlyList<TbcDecodedField> fields)
    {
        (long startSample, long endSample)? range = ComputeTestLdfSampleRange(session, fields);
        if (!range.HasValue)
        {
            return null;
        }

        return _testLdfWriter.Write(session, range.Value.startSample, range.Value.endSample, input);
    }

    private LdTestLdfWriteResult? WriteOptionalTestLdf(
        DecodeSession session,
        Stream input,
        SequenceDecodeSummary summary)
    {
        if (session.Spec.Name != "ld" || string.IsNullOrWhiteSpace(session.TestLdfOutputPath))
        {
            return null;
        }

        long startSample = summary.FirstDecodedSample ?? session.RunBounds.StartSample;
        long decodedEndSample = summary.EndDecodedSample ?? startSample;
        long endSample = checked(decodedEndSample + TestLdfLookaheadSamples);
        return endSample > startSample
            ? _testLdfWriter.Write(session, startSample, endSample, input)
            : null;
    }

    public static (long StartSample, long EndSample)? ComputeTestLdfSampleRange(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields)
    {
        if (session.Spec.Name != "ld" || string.IsNullOrWhiteSpace(session.TestLdfOutputPath))
        {
            return null;
        }

        long startSample = fields.Count > 0 ? fields[0].StartSample : session.RunBounds.StartSample;
        long decodedEndSample = fields.Count > 0
            ? EstimateNextFieldStart(session, fields[^1])
            : startSample;
        long endSample = checked(decodedEndSample + TestLdfLookaheadSamples);
        return endSample > startSample ? (startSample, endSample) : null;
    }

    public static long EstimateNextFieldStart(DecodeSession session, TbcDecodedField field)
    {
        double relativeNextField = ValidPositive(field.NextFieldOffsetSamples)
            ? field.NextFieldOffsetSamples!.Value
            : EstimateLegacyNextFieldOffset(session, field);
        long advance = Math.Max(1L, (long)Math.Round(relativeNextField, MidpointRounding.AwayFromZero));
        return checked(field.StartSample + advance);
    }

    private static double EstimateLegacyNextFieldOffset(DecodeSession session, TbcDecodedField field)
    {
        int fallbackLine = Math.Max(0, session.TbcFrameSpec.OutputLineCount - 7);
        if (field.LineLocations.Locations.Length > fallbackLine
            && ValidPositive(field.LineLocations.Locations[fallbackLine]))
        {
            return field.LineLocations.Locations[fallbackLine];
        }

        return field.LineLocations.Locations.Length > session.TbcFrameSpec.OutputLineCount
            && ValidPositive(field.LineLocations.Locations[session.TbcFrameSpec.OutputLineCount])
                ? field.LineLocations.Locations[session.TbcFrameSpec.OutputLineCount]
                : session.TbcFrameSpec.OutputLineCount * field.MeanLineLength;
    }

    private static List<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> BuildWrites(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields)
    {
        var planner = new FieldWritePlanner(session, retainWrites: true);
        foreach (TbcDecodedField field in fields)
        {
            planner.Add(field);
        }

        return [.. planner.Writes];
    }

    private sealed class StreamingOutputSession : IDisposable
    {
        private readonly DecodeSession _session;
        private readonly TbcOutputPaths _paths;
        private readonly FileStream _tbc;
        private readonly FileStream? _chroma;
        private readonly ILaserDiscFieldOutputSession _laserDiscOutput;
        private readonly TbcOutputMetadataWriter.StreamingWriter _metadata;
        private readonly TbcSqliteMetadataWriter.SequenceWriter? _sqlite;
        private bool _payloadsClosed;
        private bool _completed;
        private int _writtenFieldCount;

        public StreamingOutputSession(DecodeSession session, ILaserDiscEfmOutputWriter efmOutputWriter)
        {
            _session = session;
            _paths = TbcFirstFieldDecodeEngine.BuildOutputPaths(session);
            FileStream? tbc = null;
            FileStream? chroma = null;
            ILaserDiscFieldOutputSession? laserDiscOutput = null;
            TbcOutputMetadataWriter.StreamingWriter? metadata = null;
            TbcSqliteMetadataWriter.SequenceWriter? sqlite = null;
            try
            {
                tbc = File.Create(_paths.TbcPath);
                chroma = session.ChromaOptions?.WriteChroma == true
                    ? File.Create(_paths.ChromaPath!)
                    : null;
                laserDiscOutput = efmOutputWriter.Open(session);
                metadata = new TbcOutputMetadataWriter.StreamingWriter(session, _paths.JsonPath);
                sqlite = session.Spec.Name == "ld" || session.ExecutionOptions.WriteDebugData
                    ? new TbcSqliteMetadataWriter.SequenceWriter(session, _paths.DbPath!)
                    : null;
                _tbc = tbc;
                _chroma = chroma;
                _laserDiscOutput = laserDiscOutput;
                _metadata = metadata;
                _sqlite = sqlite;
            }
            catch
            {
                sqlite?.Dispose();
                metadata?.Dispose();
                laserDiscOutput?.Dispose();
                chroma?.Dispose();
                tbc?.Dispose();
                throw;
            }
        }

        public void Write(IReadOnlyList<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> writes)
        {
            ObjectDisposedException.ThrowIf(_payloadsClosed, this);
            foreach ((TbcDecodedField field, TbcFieldOrderDecision decision) in writes)
            {
                Write(field, decision);
            }
        }

        public TbcFieldSequenceDecodeResult Complete()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The streaming TBC output session was already completed.");
            }

            ClosePayloads();
            _metadata.Complete();
            _sqlite?.Complete(_metadata.FieldCount, _metadata.LastOutputConverter);

            _completed = true;
            return new TbcFieldSequenceDecodeResult(
                true,
                $"Wrote {_writtenFieldCount} TBC field(s) to {_paths.TbcPath}",
                _paths,
                [],
                _writtenFieldCount);
        }

        public void Dispose()
        {
            ClosePayloads();
            _sqlite?.Dispose();
            _metadata.Dispose();
        }

        private void Write(TbcDecodedField field, TbcFieldOrderDecision decision)
        {
            if (field.Samples.Length != _session.TbcFrameSpec.FieldSampleCount)
            {
                throw new ArgumentException(
                    $"Decoded field sample count {field.Samples.Length} does not match TBC frame spec {_session.TbcFrameSpec.FieldSampleCount} for {_paths.TbcPath}.");
            }

            TbcOutputWriter.WriteFrame(_tbc, field.Samples, _session.TbcFrameSpec, field.OutputPayload);
            if (_chroma is not null)
            {
                if (field.ChromaSamples is null)
                {
                    throw new InvalidOperationException("VHS chroma output was enabled but a decoded field did not contain chroma samples.");
                }

                if (field.ChromaSamples.Length != _session.TbcFrameSpec.FieldSampleCount)
                {
                    throw new ArgumentException(
                        $"Decoded chroma field sample count {field.ChromaSamples.Length} does not match TBC frame spec {_session.TbcFrameSpec.FieldSampleCount} for {_paths.ChromaPath}.");
                }

                TbcOutputWriter.WriteFrame(_chroma, field.ChromaSamples, _session.TbcFrameSpec);
            }

            TbcDecodedField metadataField = _laserDiscOutput.Write(field);
            System.Text.Json.Nodes.JsonObject fieldInfo = _metadata.Add(metadataField, decision);
            _sqlite?.Add(fieldInfo);
            _writtenFieldCount++;
        }

        private void ClosePayloads()
        {
            if (_payloadsClosed)
            {
                return;
            }

            _payloadsClosed = true;
            _laserDiscOutput.Dispose();
            _chroma?.Dispose();
            _tbc.Dispose();
        }
    }

    private sealed class FieldWritePlanner
    {
        private readonly DecodeSession _session;
        private readonly List<(TbcDecodedField Field, TbcFieldOrderDecision Decision)>? _retainedWrites;
        private readonly List<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> _history = [];
        private readonly Dictionary<bool, (TbcDecodedField Field, TbcFieldOrderDecision Decision)> _lastValid = [];
        private bool _duplicatePreviousField = true;
        private int _writtenFieldCount;

        public FieldWritePlanner(DecodeSession session, bool retainWrites)
        {
            _session = session;
            _retainedWrites = retainWrites ? [] : null;
        }

        public IReadOnlyList<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> Writes
            => _retainedWrites ?? [];

        public int WrittenFieldCount => _writtenFieldCount;

        public IReadOnlyList<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> Add(TbcDecodedField field)
        {
            var emitted = new List<(TbcDecodedField Field, TbcFieldOrderDecision Decision)>(2);
            if (_session.Spec.Name is "ld" or "cvbs")
            {
                AddLaserDiscStyleField(field, emitted);
            }
            else
            {
                AddTapeField(field, emitted);
            }

            return emitted;
        }

        private void AddTapeField(
            TbcDecodedField field,
            List<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> emitted)
        {
            bool detectedFirstField = field.DetectedFirstField
                ?? (_writtenFieldCount == 0 || !_history[^1].Decision.IsFirstField);
            if (_writtenFieldCount == 0 && !detectedFirstField)
            {
                return;
            }

            int seqNo = _writtenFieldCount + 1;
            bool isFirstField = detectedFirstField;
            bool isDuplicateField = false;
            bool writeField = true;
            int syncConfidence = 100;
            int decodeFaults = 0;

            if (_writtenFieldCount > 0 && _history[^1].Decision.IsFirstField == isFirstField)
            {
                double distance = ComputeFieldDiskLocation(_session, field)
                    - ComputeFieldDiskLocation(_session, _history[^1].Field);
                bool progressive = _session.FieldOrderOptions.AllowProgressiveFlip
                    && _history[^1].Decision.DetectedFirstField == detectedFirstField
                    && _writtenFieldCount > 1
                    && _history[^2].Decision.DetectedFirstField == _history[^1].Decision.DetectedFirstField
                    && distance >= 0.9
                    && distance <= 1.1;
                if (progressive)
                {
                    decodeFaults |= 1;
                    syncConfidence = 10;
                    isFirstField = !_history[^1].Decision.IsFirstField;
                }
                else
                {
                    switch (_session.FieldOrderOptions.Action)
                    {
                        case TbcFieldOrderAction.Duplicate:
                            _duplicatePreviousField = true;
                            break;
                        case TbcFieldOrderAction.Drop:
                            _duplicatePreviousField = false;
                            break;
                        case TbcFieldOrderAction.Detect:
                            _duplicatePreviousField = distance > 1.1
                                ? true
                                : distance < 0.9
                                    ? false
                                    : !_duplicatePreviousField;
                            break;
                    }

                    decodeFaults |= 4;
                    syncConfidence = 0;
                    if (_session.FieldOrderOptions.Action == TbcFieldOrderAction.None)
                    {
                        isFirstField = !_history[^1].Decision.IsFirstField;
                    }
                    else if (_duplicatePreviousField)
                    {
                        isDuplicateField = true;
                    }
                    else
                    {
                        writeField = false;
                    }
                }
            }

            TbcFieldOrderDecision decision = new(
                seqNo,
                isFirstField,
                detectedFirstField,
                isDuplicateField,
                writeField,
                syncConfidence,
                decodeFaults);
            var current = (Field: field, Decision: decision);
            if (writeField)
            {
                _lastValid[detectedFirstField] = current;
            }

            if (isDuplicateField)
            {
                if (_lastValid.TryGetValue(!detectedFirstField, out var opposite)
                    && _lastValid.TryGetValue(detectedFirstField, out var duplicateCurrent))
                {
                    Emit(opposite, emitted);
                    Emit(duplicateCurrent, emitted);
                }

                return;
            }

            if (writeField)
            {
                Emit(current, emitted);
            }
        }

        private void AddLaserDiscStyleField(
            TbcDecodedField field,
            List<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> emitted)
        {
            bool detectedFirstField = field.DetectedFirstField
                ?? (_writtenFieldCount == 0 || !_history[^1].Decision.IsFirstField);
            if (_writtenFieldCount == 0 && !detectedFirstField)
            {
                return;
            }

            bool isFirstField = detectedFirstField;
            bool isDuplicateField = false;
            int syncConfidence = 100;
            int decodeFaults = 0;
            if (_writtenFieldCount > 0)
            {
                (TbcDecodedField Field, TbcFieldOrderDecision Decision) previous = _history[^1];
                if (field.FieldPhaseId.HasValue
                    && previous.Field.FieldPhaseId.HasValue
                    && !IsExpectedLaserDiscFieldPhase(
                        previous.Field.FieldPhaseId.Value,
                        field.FieldPhaseId.Value,
                        LaserDiscFieldPhaseCount(_session)))
                {
                    decodeFaults |= 2;
                }

                if (previous.Decision.IsFirstField == isFirstField)
                {
                    double distance = ComputeFieldDiskLocation(_session, field)
                        - ComputeFieldDiskLocation(_session, previous.Field);
                    if (distance >= 0.95 && distance <= 1.05)
                    {
                        decodeFaults |= 1;
                        syncConfidence = 10;
                        isFirstField = !previous.Decision.IsFirstField;
                    }
                    else
                    {
                        decodeFaults |= 4;
                        syncConfidence = 0;
                        isDuplicateField = true;
                    }
                }
            }

            TbcFieldOrderDecision decision = new(
                SeqNo: _writtenFieldCount + 1,
                isFirstField,
                detectedFirstField,
                isDuplicateField,
                WriteField: true,
                syncConfidence,
                decodeFaults);
            var current = (Field: field, Decision: decision);
            _lastValid[detectedFirstField] = current;

            if (isDuplicateField)
            {
                if (_lastValid.TryGetValue(!detectedFirstField, out var opposite))
                {
                    Emit(opposite, emitted);
                    Emit(current, emitted);
                }

                return;
            }

            Emit(current, emitted);
        }

        private void Emit(
            (TbcDecodedField Field, TbcFieldOrderDecision Decision) write,
            List<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> emitted)
        {
            emitted.Add(write);
            _retainedWrites?.Add(write);
            _writtenFieldCount++;
            _history.Add(write);
            if (_history.Count > 2)
            {
                _history.RemoveAt(0);
            }
        }
    }

    private static double ComputeFieldDiskLocation(DecodeSession session, TbcDecodedField field)
    {
        if (field.DiskLocation.HasValue)
        {
            return field.DiskLocation.Value;
        }

        double framesPerSecond = session.Parameters.SysParams.GetProperty("FPS").GetDouble();
        if (framesPerSecond <= 0.0)
        {
            return 0.0;
        }

        double samplesPerField = ((int)(session.DecodeSampleRateHz / (framesPerSecond * 2.0))) + 1;
        return Math.Round((field.StartSample / samplesPerField) * 10.0, MidpointRounding.ToEven) / 10.0;
    }

    private static bool IsExpectedLaserDiscFieldPhase(int previous, int current, int fieldPhaseCount)
    {
        return (current == 1 && previous == fieldPhaseCount) || current == previous + 1;
    }

    private static int LaserDiscFieldPhaseCount(DecodeSession session)
    {
        return session.Parameters.SysParams.TryGetProperty("fieldPhases", out var fieldPhases)
            ? fieldPhases.GetInt32()
            : 4;
    }

    private static bool ValidPositive(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value) && value.Value > 0.0;
    }

    private static TbcFieldSequenceDecodeResult Fail(string message)
    {
        return new TbcFieldSequenceDecodeResult(false, message, null, [], 0);
    }
}
