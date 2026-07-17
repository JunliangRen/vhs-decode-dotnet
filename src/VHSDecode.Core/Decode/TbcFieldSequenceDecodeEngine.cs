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
    private readonly ILdTestLdfWriter _testLdfWriter;
    private readonly ILaserDiscEfmOutputWriter _efmOutputWriter;
    private readonly TbcFieldSequenceReadField _readField;
    private readonly VhsDiskSpaceGuard _vhsDiskSpaceGuard;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _usesSessionReader;

    private sealed class CvbsPrefetchSlot : IDisposable
    {
        public Task<TbcDecodedField?>? Current { get; private set; }

        public void Set(Task<TbcDecodedField?> task)
        {
            if (Current is not null)
            {
                throw new InvalidOperationException("A CVBS field prefetch is already in progress.");
            }

            Current = task;
        }

        public Task<TbcDecodedField?>? Take()
        {
            Task<TbcDecodedField?>? task = Current;
            Current = null;
            return task;
        }

        public void Dispose()
        {
            Task<TbcDecodedField?>? task = Take();
            if (task is null)
            {
                return;
            }

            try
            {
                _ = task.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Producer lookahead is discarded once output is complete.
            }
        }
    }

    private sealed record SequenceDecodeSummary(
        IReadOnlyList<TbcDecodedField> Fields,
        int DecodedFieldCount,
        long StartSample,
        long EndSample);

    public TbcFieldSequenceDecodeEngine(
        int extraReadLines = 3,
        ILdTestLdfWriter? testLdfWriter = null,
        ILaserDiscEfmOutputWriter? efmOutputWriter = null,
        TbcFieldSequenceReadField? readField = null,
        VhsDiskSpaceGuard? vhsDiskSpaceGuard = null,
        CancellationToken cancellationToken = default)
    {
        if (extraReadLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraReadLines));
        }

        ExtraReadLines = extraReadLines;
        _testLdfWriter = testLdfWriter ?? new FfmpegLdTestLdfWriter();
        _efmOutputWriter = efmOutputWriter ?? new LaserDiscEfmOutputWriter();
        _vhsDiskSpaceGuard = vhsDiskSpaceGuard ?? new VhsDiskSpaceGuard();
        _cancellationToken = cancellationToken;
        _usesSessionReader = readField is null;
        _readField = readField ?? ReadFieldFromSession;
    }

    public int ExtraReadLines { get; }

    internal bool EnableWorkerPrefetchForCustomReader { get; init; }

    internal Func<string, Stream> CreateTbcOutput { get; init; } = DecodeOutputFile.Create;

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
        var output = new StreamingOutputSession(session, _efmOutputWriter, CreateTbcOutput);
        bool outputCompleted = false;
        try
        {
            SequenceDecodeSummary summary = DecodeSequence(
                session,
                input,
                maxFields,
                retainFields: false,
                writeFields: writes =>
                {
                    output.Write(writes);
                },
                writeMetadataSnapshot: () =>
                {
                    output.WriteMetadataSnapshot();
                });
            if (session.Spec.Name == "cvbs")
            {
                session.RuntimeReporter?.WriteCvbsCompletion(session.TbcRenderer.CvbsAgcStatistics);
            }
            else
            {
                session.RuntimeReporter?.WriteCompletionMessage(output.WrittenFieldCount);
            }

            LdTestLdfWriteResult? testLdf = WriteOptionalTestLdf(session, input, summary);
            TbcFieldSequenceDecodeResult result = output.Complete();
            outputCompleted = true;
            return testLdf.HasValue
                ? result with
                {
                    Message = result.Message + "; " + testLdf.Value.Message,
                    TestLdf = testLdf
                }
                : result;
        }
        catch (Exception)
        {
            if (!outputCompleted)
            {
                try
                {
                    _ = output.Complete();
                }
                catch
                {
                    // Preserve the exception that interrupted decoding.
                }
            }

            throw;
        }
        finally
        {
            output.Dispose();
        }
    }

    public IReadOnlyList<TbcDecodedField> DecodeFields(DecodeSession session, Stream input, int? maxFields = null)
        => DecodeSequence(
            session,
            input,
            maxFields,
            retainFields: true,
            writeFields: null,
            writeMetadataSnapshot: null).Fields;

    private SequenceDecodeSummary DecodeSequence(
        DecodeSession session,
        Stream input,
        int? maxFields,
        bool retainFields,
        Action<IReadOnlyList<(TbcDecodedField Field, TbcFieldOrderDecision Decision)>>? writeFields,
        Action? writeMetadataSnapshot)
    {
        BigInteger requestedFields = maxFields.HasValue
            ? new BigInteger(maxFields.Value)
            : session.RunBounds.RequestedFieldCount;
        if (requestedFields < BigInteger.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFields));
        }

        _cancellationToken.ThrowIfCancellationRequested();
        int readLength = DecodeReadWindowPlanner.EstimateReadSampleCount(session, ExtraReadLines);
        if (requestedFields.IsZero && !RequiresInitialSeek(session))
        {
            long zeroLengthStartSample = session.RunBounds.StartSample;
            return new SequenceDecodeSummary([], 0, zeroLengthStartSample, zeroLengthStartSample);
        }

        long begin = ResolveInitialDecodeStart(session, input, readLength);
        long startSample = begin;

        var fields = retainFields ? new List<TbcDecodedField>() : null;
        var writePlanner = new FieldWritePlanner(session, retainWrites: false);
        int decodedFieldCount = 0;
        long laserDiscWrittenFieldCount = 0;
        TbcDecodedField? firstLaserDiscField = null;
        bool laserDiscLeadIn = false;
        bool laserDiscLeadOut = false;
        bool haveFirstTapeField = false;
        string? pendingTapeFrameStatus = null;
        int? pendingTapeCheckpointFieldCount = null;
        (TbcDecodedField Field, int DecodedIndex)? pendingCvbsField = null;
        bool pendingCvbsEndOfInputCheckpoint = false;
        bool useCvbsWorkerPrefetch = ShouldUseCvbsWorkerPrefetch(session);
        using var cvbsPrefetch = new CvbsPrefetchSlot();
        LaserDiscAutoMtfController? autoMtf = session.Spec.Name == "ld"
            ? new LaserDiscAutoMtfController()
            : null;
        double? deferredLaserDiscMtf = null;

        void ApplyDeferredLaserDiscMtf()
        {
            if (!deferredLaserDiscMtf.HasValue)
            {
                return;
            }

            ApplyLaserDiscMtf(session, deferredLaserDiscMtf.Value);
            deferredLaserDiscMtf = null;
        }

        void CheckpointOutput(int fieldsWritten)
        {
            if (!TbcOutputMetadataWriter.ShouldWriteRecoverySnapshot(fieldsWritten))
            {
                return;
            }

            writeMetadataSnapshot?.Invoke();
            if (session.Spec.Name == "vhs")
            {
                _vhsDiskSpaceGuard.Check(
                    session.OutputBase,
                    fieldsWritten,
                    session.RuntimeReporter,
                    _cancellationToken);
            }
        }

        void CompleteField(TbcDecodedField completedField, int decodedIndex)
        {
            fields?.Add(completedField);
            int fieldsWrittenBeforeAdd = writePlanner.WrittenFieldCount;
            IReadOnlyList<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> writes =
                writePlanner.Add(completedField);
            bool acceptedLaserDiscFrameState = session.Spec.Name == "ld"
                && writes.Any(write => ReferenceEquals(write.Field, completedField)
                    && !write.Decision.IsDuplicateField);
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

                if (writeFields is not null)
                {
                    writeFields(writes);
                    session.RuntimeReporter?.FieldsWritten(writes.Count);
                }
            }

            bool isFirstField = completedField.DetectedFirstField ?? ((decodedIndex & 1) == 0);
            if (acceptedLaserDiscFrameState)
            {
                if (isFirstField)
                {
                    firstLaserDiscField = completedField;
                }
                else if (firstLaserDiscField is not null)
                {
                    int rawFrame = checked((int)Math.Floor(
                        ComputeFieldDiskLocation(session, completedField) / 2.0));
                    int framesPerSecond = string.Equals(
                        session.System,
                        "PAL",
                        StringComparison.OrdinalIgnoreCase) ? 25 : 30;
                    LaserDiscVbiInterpretation interpretation = LaserDiscVbiInterpreter.Interpret(
                        (firstLaserDiscField.VbiData ?? []).Concat(completedField.VbiData ?? []),
                        framesPerSecond);
                    laserDiscLeadIn |= interpretation.LeadIn;
                    laserDiscLeadOut |= interpretation.LeadOut;
                    DecodeSessionLogWriter.Status(
                        session,
                        FormatLaserDiscFrameStatus(
                            fieldsWrittenBeforeAdd,
                            session.RunBounds.RequestedFieldCount / 2,
                            rawFrame,
                            interpretation,
                            laserDiscLeadIn,
                            laserDiscLeadOut));
                }

                autoMtf?.ObserveAcceptedField(completedField, session.System);
            }
            else if (session.Spec.Name == "cvbs" && !isFirstField && writes.Count == 1)
            {
                int rawFrame = checked((int)Math.Floor(
                    ComputeFieldDiskLocation(session, completedField) / 2.0));
                DecodeSessionLogWriter.Status(
                    session,
                    $"File Frame {rawFrame}: CAV Pulldown/Telecine Frame");
            }
        }

        while (maxFields.HasValue
            ? decodedFieldCount < requestedFields
            : writePlanner.WrittenFieldCount < requestedFields)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            TbcDecodedField? field;
            TbcFieldDecodeState? fieldState = autoMtf is null
                ? null
                : session.TbcFieldDecoder.CaptureState();
            try
            {
                Task<TbcDecodedField?>? prefetchedField = cvbsPrefetch.Take();
                field = prefetchedField is null
                    ? ReadFieldWithContext(
                        session,
                        input,
                        begin,
                        readLength,
                        decodedFieldCount,
                        writePlanner.WrittenFieldCount)
                    : prefetchedField.GetAwaiter().GetResult();
                if (autoMtf is not null)
                {
                    // v0.4.0 has already decoded this field before processing the
                    // previous field's non-retry MTF update.
                    ApplyDeferredLaserDiscMtf();
                }

                if (field is not null && autoMtf is not null)
                {
                    LaserDiscMtfUpdate mtfUpdate = autoMtf.Observe(field.BlackToWhiteRfRatio);
                    bool requiresRetry = mtfUpdate.RequiresRetry || field.LaserDiscAgcAdjusted;
                    if (mtfUpdate.Level != mtfUpdate.PreviousLevel)
                    {
                        if (requiresRetry)
                        {
                            ApplyLaserDiscMtf(session, mtfUpdate.Level);
                        }
                        else
                        {
                            deferredLaserDiscMtf = mtfUpdate.Level;
                        }
                    }

                    if (requiresRetry)
                    {
                        session.TbcFieldDecoder.RestoreStateForRetry(fieldState!);
                        field = ReadFieldWithContext(
                            session,
                            input,
                            begin,
                            readLength,
                            decodedFieldCount,
                            writePlanner.WrittenFieldCount);
                    }
                }

                _cancellationToken.ThrowIfCancellationRequested();
            }
            catch (TbcFieldDecodeRecoveryException ex)
            {
                bool directVideoNoSync = session.Spec.Name is "cvbs" or "ld"
                    && ex.Kind == TbcFieldDecodeRecoveryKind.NoSyncPulses;
                bool directVideoNoSyncAfterOutput = directVideoNoSync
                    && writePlanner.WrittenFieldCount > 0;
                LogRecovery(session, ex, directVideoNoSyncAfterOutput);
                if (session.Spec.Name == "cvbs")
                {
                    if (pendingCvbsField is not null)
                    {
                        CompleteField(FinalizeDeferredCvbsRender(
                            session,
                            pendingCvbsField.Value.Field,
                            session.TbcFieldDecoder.CurrentCvbsOutputConverter),
                            pendingCvbsField.Value.DecodedIndex);
                        pendingCvbsField = null;
                        CheckpointOutput(writePlanner.WrittenFieldCount);
                    }

                    session.TbcFieldDecoder.DiscardCvbsPreviousFieldContextAfterRecovery();
                }
                else if (session.Spec.Name == "ld")
                {
                    session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();
                    ApplyDeferredLaserDiscMtf();
                }

                if (ex.StopAfterDecodedFields && decodedFieldCount > 0 && !directVideoNoSync)
                {
                    break;
                }

                long recoveryOffset = directVideoNoSyncAfterOutput
                    ? DirectVideoNoSyncAfterOutputOffsetSamples(session)
                    : ex.SuggestedOffsetSamples;
                begin = Math.Max(0L, checked(begin + recoveryOffset));
                continue;
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DecodeFieldReadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DecodeFieldReadException(begin, ex);
            }

            if (pendingTapeFrameStatus is not null)
            {
                DecodeSessionLogWriter.Status(session, pendingTapeFrameStatus);
                pendingTapeFrameStatus = null;
            }

            if (pendingTapeCheckpointFieldCount.HasValue)
            {
                CheckpointOutput(pendingTapeCheckpointFieldCount.Value);
                pendingTapeCheckpointFieldCount = null;
            }

            if (field is null)
            {
                if (session.Spec.Name == "cvbs")
                {
                    pendingCvbsEndOfInputCheckpoint = true;
                }
                else
                {
                    CheckpointOutput(writePlanner.WrittenFieldCount);
                }

                break;
            }

            decodedFieldCount++;
            long nextBegin = EstimateNextFieldStart(session, field);
            if (nextBegin <= begin)
            {
                throw new InvalidOperationException("Decoded field did not advance the input position.");
            }

            bool mayPrefetchNextField = !maxFields.HasValue || decodedFieldCount < requestedFields;
            if (useCvbsWorkerPrefetch && mayPrefetchNextField)
            {
                cvbsPrefetch.Set(StartCvbsPrefetch(
                    session,
                    input,
                    nextBegin,
                    readLength,
                    fieldNumber: decodedFieldCount,
                    writtenFieldCount: writePlanner.WrittenFieldCount));
            }

            if (pendingCvbsField is not null)
            {
                CompleteField(FinalizeDeferredCvbsRender(
                    session,
                    pendingCvbsField.Value.Field,
                    field.OutputConverter),
                    pendingCvbsField.Value.DecodedIndex);
                pendingCvbsField = null;
                CheckpointOutput(writePlanner.WrittenFieldCount);
            }

            bool completedCurrentField = false;
            if (field.DeferredRenderSource is not null)
            {
                if (useCvbsWorkerPrefetch)
                {
                    CompleteField(
                        FinalizeDeferredCvbsWorkerRender(
                            session,
                            field,
                            cvbsPrefetch.Current,
                            waitForInitialProducer: decodedFieldCount == 1),
                        decodedFieldCount - 1);
                    completedCurrentField = true;
                }
                else
                {
                    // v0.4.0 with --threads 0 synchronously decodes the next field
                    // before downscaling the current one, exposing the next levels.
                    pendingCvbsField = (field, decodedFieldCount - 1);
                }
            }
            else
            {
                CompleteField(field, decodedFieldCount - 1);
                completedCurrentField = true;
            }

            bool isFirstField = field.DetectedFirstField ?? ((decodedFieldCount - 1) % 2 == 0);
            if (session.Spec.Name == "vhs")
            {
                if (isFirstField)
                {
                    haveFirstTapeField = true;
                    CheckpointOutput(writePlanner.WrittenFieldCount);
                }
                else if (haveFirstTapeField)
                {
                    int rawFrame = checked((int)Math.Floor(ComputeFieldDiskLocation(session, field) / 2.0));
                    pendingTapeFrameStatus = $"File Frame {rawFrame}: {session.Parameters.TapeFormat} ";
                    pendingTapeCheckpointFieldCount = writePlanner.WrittenFieldCount;
                }
                else
                {
                    CheckpointOutput(writePlanner.WrittenFieldCount);
                }
            }
            else if (completedCurrentField)
            {
                CheckpointOutput(writePlanner.WrittenFieldCount);
            }

            if (session.Spec.Name == "ld"
                && !session.ExecutionOptions.IgnoreLeadOut
                && laserDiscLeadOut)
            {
                begin = nextBegin;
                break;
            }

            begin = nextBegin;
        }

        if (pendingCvbsField is not null)
        {
            bool belongsToRequestedOutput = maxFields.HasValue
                || writePlanner.WrittenFieldCount < requestedFields;
            if (belongsToRequestedOutput)
            {
                CompleteField(FinalizeDeferredCvbsRender(
                    session,
                    pendingCvbsField.Value.Field,
                    pendingCvbsField.Value.Field.OutputConverter),
                    pendingCvbsField.Value.DecodedIndex);
                CheckpointOutput(writePlanner.WrittenFieldCount);
            }
            else if (fields is not null)
            {
                // DecodeFields retains decoded producer lookahead, but normal
                // streaming output must not write beyond --length.
                fields.Add(FinalizeDeferredCvbsRender(
                    session,
                    pendingCvbsField.Value.Field,
                    pendingCvbsField.Value.Field.OutputConverter));
            }
        }

        if (pendingCvbsEndOfInputCheckpoint)
        {
            CheckpointOutput(writePlanner.WrittenFieldCount);
        }

        bool reachedRequestedTapeOutput = !maxFields.HasValue
            && session.Spec.Name == "vhs"
            && writePlanner.WrittenFieldCount >= requestedFields;
        if (reachedRequestedTapeOutput && pendingTapeFrameStatus is not null)
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _ = ReadFieldWithContext(
                    session,
                    input,
                    begin,
                    readLength,
                    decodedFieldCount,
                    writePlanner.WrittenFieldCount);
                _cancellationToken.ThrowIfCancellationRequested();
            }
            catch (TbcFieldDecodeRecoveryException ex)
            {
                LogRecovery(session, ex);
            }
        }

        if (pendingTapeFrameStatus is not null)
        {
            DecodeSessionLogWriter.Status(session, pendingTapeFrameStatus);
        }

        if (pendingTapeCheckpointFieldCount.HasValue)
        {
            CheckpointOutput(pendingTapeCheckpointFieldCount.Value);
        }

        return new SequenceDecodeSummary(
            fields ?? [],
            decodedFieldCount,
            startSample,
            begin);
    }

    internal static string FormatLaserDiscFrameStatus(
        int fieldsWritten,
        BigInteger estimatedFrames,
        int rawFrame,
        LaserDiscVbiInterpretation interpretation,
        bool leadIn,
        bool leadOut)
    {
        int frame = (fieldsWritten / 2) + 1;
        string diskType = interpretation.IsClv ? "CLV" : "CAV";
        string prefix = $"Frame {frame}/{estimatedFrames}: File Frame {rawFrame}: {diskType} ";
        if (interpretation.IsClv
            && interpretation.IsEarlyClv
            && interpretation.ClvMinutes.HasValue)
        {
            return prefix + $"Timecode {interpretation.ClvMinutes.Value}:xx ";
        }

        if (interpretation.IsClv
            && interpretation.FrameNumber.HasValue
            && interpretation.ClvMinutes.HasValue
            && interpretation.ClvSeconds.HasValue
            && interpretation.ClvFrameNumber.HasValue)
        {
            return prefix
                + $"Timecode {interpretation.ClvMinutes.Value}:"
                + $"{interpretation.ClvSeconds.Value:00}."
                + $"{interpretation.ClvFrameNumber.Value:00} "
                + $"Frame #{interpretation.FrameNumber.Value} ";
        }

        if (interpretation.FrameNumber is > 0)
        {
            return prefix + $"Frame #{interpretation.FrameNumber.Value} ";
        }

        if (leadIn)
        {
            return prefix + "Lead In";
        }

        if (leadOut)
        {
            return prefix + "Lead Out";
        }

        return prefix + "Pulldown/Telecine Frame";
    }

    private static TbcDecodedField FinalizeDeferredCvbsRender(
        DecodeSession session,
        TbcDecodedField field,
        VideoOutputConverter? converter)
    {
        TbcDeferredRenderSource? source = field.DeferredRenderSource;
        if (source is null)
        {
            return field;
        }

        VideoOutputConverter? activeConverter = converter ?? field.OutputConverter;
        TbcRenderedField rendered = session.TbcRenderer.RenderFieldPayload(
            source.VideoHz,
            source.LineLocations,
            firstLine: source.FirstLine,
            fieldNumber: source.FieldNumber,
            converterOverride: activeConverter);
        return field with
        {
            Samples = rendered.Samples,
            OutputPayload = rendered.OutputPayload,
            OutputConverter = activeConverter,
            DeferredRenderSource = null
        };
    }

    private static TbcDecodedField FinalizeDeferredCvbsWorkerRender(
        DecodeSession session,
        TbcDecodedField field,
        Task<TbcDecodedField?>? prefetchedField,
        bool waitForInitialProducer)
    {
        TbcDeferredRenderSource? source = field.DeferredRenderSource;
        if (source is null)
        {
            return field;
        }

        TbcRenderedField rendered = session.TbcRenderer.RenderFieldPayloadWithConverterProvider(
            source.VideoHz,
            source.LineLocations,
            firstLine: source.FirstLine,
            lineCount: null,
            fieldNumber: source.FieldNumber,
            converterFallback: field.OutputConverter,
            converterProvider: () => ResolveCvbsWorkerOutputConverter(
                session,
                field.OutputConverter,
                prefetchedField,
                waitForInitialProducer));
        return field with
        {
            Samples = rendered.Samples,
            OutputPayload = rendered.OutputPayload,
            OutputConverter = rendered.OutputConverter ?? field.OutputConverter,
            DeferredRenderSource = null
        };
    }

    private static VideoOutputConverter ResolveCvbsWorkerOutputConverter(
        DecodeSession session,
        VideoOutputConverter? fieldConverter,
        Task<TbcDecodedField?>? prefetchedField,
        bool waitForInitialProducer)
    {
        if (waitForInitialProducer && prefetchedField is not null)
        {
            // The first v0.4.0 Numba downscale leaves enough warm-up time for
            // the speculative producer to publish its auto-sync levels.
            VideoOutputConverter initialConverter = fieldConverter
                ?? session.TbcFieldDecoder.CurrentCvbsOutputConverter;
            SpinWait.SpinUntil(() =>
                prefetchedField.IsCompleted
                || !ReferenceEquals(
                    initialConverter,
                    session.TbcFieldDecoder.CurrentCvbsOutputConverter));
        }

        return session.TbcFieldDecoder.CurrentCvbsOutputConverter;
    }

    private Task<TbcDecodedField?> StartCvbsPrefetch(
        DecodeSession session,
        Stream input,
        long begin,
        int readLength,
        int fieldNumber,
        int writtenFieldCount)
    {
        return Task.Factory.StartNew(
            () =>
            {
                _cancellationToken.ThrowIfCancellationRequested();
                TbcDecodedField? field = ReadFieldWithContext(
                    session,
                    input,
                    begin,
                    readLength,
                    fieldNumber,
                    writtenFieldCount);
                _cancellationToken.ThrowIfCancellationRequested();
                return field;
            },
            _cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    internal static long DirectVideoNoSyncAfterOutputOffsetSamples(DecodeSession session)
    {
        double linePeriodUs = session.Parameters.SysParams.GetProperty("line_period").GetDouble();
        double nominalLineLength = linePeriodUs * (session.DecodeSampleRateHz / 1_000_000.0);
        return checked((long)(nominalLineLength * 200.0));
    }

    private static void LogRecovery(
        DecodeSession session,
        TbcFieldDecodeRecoveryException exception,
        bool directVideoNoSyncAfterOutput = false)
    {
        string? message = (session.Spec.Name, exception.Kind) switch
        {
            ("vhs", TbcFieldDecodeRecoveryKind.NoSyncPulses) =>
                "Unable to find any sync pulses, jumping 100 ms",
            ("vhs" or "cvbs" or "ld", TbcFieldDecodeRecoveryKind.NoFirstHSync) =>
                "Unable to determine start of field - dropping field",
            ("cvbs" or "ld", TbcFieldDecodeRecoveryKind.NoSyncPulses) when directVideoNoSyncAfterOutput =>
                "Unable to find any sync pulses, skipping one field",
            ("cvbs" or "ld", TbcFieldDecodeRecoveryKind.NoSyncPulses) =>
                "Unable to find any sync pulses, skipping one second",
            ("cvbs", TbcFieldDecodeRecoveryKind.InsufficientData) =>
                "Missing data at the end of field, possibly dropped samples skipping a little.",
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
        if (span is null)
        {
            return null;
        }

        return ShouldDeferCvbsOutputConversion(session)
            ? session.TbcFieldDecoder.DecodeForSequence(span, fieldNumber)
            : session.TbcFieldDecoder.Decode(span, fieldNumber: fieldNumber);
    }

    private TbcDecodedField? ReadFieldWithContext(
        DecodeSession session,
        Stream input,
        long begin,
        int readLength,
        int fieldNumber,
        int? writtenFieldCount = null)
    {
        try
        {
            int effectiveFieldNumber = ResolveReadFieldNumber(
                _usesSessionReader,
                session.Spec.Name,
                fieldNumber,
                writtenFieldCount);
            return _readField(session, input, begin, readLength, effectiveFieldNumber);
        }
        catch (TbcFieldDecodeRecoveryException)
        {
            throw;
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DecodeFieldReadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DecodeFieldReadException(begin, ex);
        }
    }

    internal static int ResolveReadFieldNumber(
        bool usesSessionReader,
        string decoderName,
        int decodedFieldNumber,
        int? writtenFieldCount)
    {
        return usesSessionReader
            && decoderName is "cvbs" or "ld"
            && writtenFieldCount.HasValue
                ? writtenFieldCount.Value
                : decodedFieldNumber;
    }

    internal static bool ShouldDeferCvbsOutputConversion(DecodeSession session)
    {
        return session.Spec.Name == "cvbs"
            && session.TbcFieldDecoder.CanDeferCvbsOutputConversion;
    }

    private bool ShouldUseCvbsWorkerPrefetch(DecodeSession session)
    {
        return (_usesSessionReader || EnableWorkerPrefetchForCustomReader)
            && session.ExecutionOptions.RequestedThreadsInteger != BigInteger.Zero
            && ShouldDeferCvbsOutputConversion(session);
    }

    private long ResolveInitialDecodeStart(DecodeSession session, Stream input, int readLength)
    {
        if (session.Spec.Name == "cvbs" && session.ExecutionOptions.SeekFrame != -BigInteger.One)
        {
            throw new InvalidOperationException("ERROR: Seeking failed");
        }

        if (session.Spec.Name != "ld" || session.ExecutionOptions.SeekFrame == -BigInteger.One)
        {
            return session.RunBounds.StartPosition.ResolveForRead();
        }

        BigInteger targetFrame = session.ExecutionOptions.SeekFrame;
        DecodeSessionLogWriter.Append(session, "INFO", "Beginning seek");
        long nominalFieldSamples = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
        DecodeStartPosition seekStart = session.RunBounds.HasExplicitStartFrame
            ? session.RunBounds.StartFramePosition
            : DecodeStartPosition.FromInteger(targetFrame * 2 * nominalFieldSamples);
        long current = seekStart.ResolveForRead();

        ApplyLaserDiscMtf(session, 0.0);
        try
        {
            return ResolveLaserDiscSeekStart(
                session,
                input,
                readLength,
                targetFrame,
                nominalFieldSamples,
                current);
        }
        finally
        {
            ApplyLaserDiscMtf(session, 1.0);
        }
    }

    private long ResolveLaserDiscSeekStart(
        DecodeSession session,
        Stream input,
        int readLength,
        BigInteger targetFrame,
        long nominalFieldSamples,
        long current)
    {
        for (int retry = 0; retry < 3; retry++)
        {
            if (!TryReadLaserDiscFrameNumber(
                    session,
                    input,
                    readLength,
                    current,
                    out int frameNumber,
                    out long frameStart,
                    out long probeStart))
            {
                break;
            }

            current = probeStart;
            if (targetFrame == frameNumber)
            {
                DecodeSessionLogWriter.Append(session, "INFO", "Finished seek");
                session.RuntimeReporter?.WriteDirectErrorLine(
                    $"Finished seeking, starting at frame {frameNumber}");
                return frameStart;
            }

            current = ClampSamplePosition(
                current + (((targetFrame - frameNumber) * 2 - 1) * nominalFieldSamples));
        }

        throw new InvalidOperationException("ERROR: Seeking failed");
    }

    private static bool RequiresInitialSeek(DecodeSession session)
        => session.Spec.Name is "cvbs" or "ld"
            && session.ExecutionOptions.SeekFrame != -BigInteger.One;

    private static long ClampSamplePosition(BigInteger samplePosition)
    {
        if (samplePosition <= BigInteger.Zero)
        {
            return 0;
        }

        return samplePosition >= long.MaxValue
            ? long.MaxValue
            : (long)samplePosition;
    }

    private bool TryReadLaserDiscFrameNumber(
        DecodeSession session,
        Stream input,
        int readLength,
        long begin,
        out int frameNumber,
        out long frameStart,
        out long probeStart)
    {
        frameNumber = 0;
        frameStart = begin;
        probeStart = begin;
        long current = begin;
        int validFieldCount = 0;
        TbcDecodedField? previous = null;
        int framesPerSecond = string.Equals(session.System, "PAL", StringComparison.OrdinalIgnoreCase) ? 25 : 30;
        session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            TbcDecodedField? field;
            try
            {
                field = ReadFieldWithContext(
                    session,
                    input,
                    current,
                    readLength,
                    fieldNumber: validFieldCount,
                    writtenFieldCount: 0);
            }
            catch (TbcFieldDecodeRecoveryException ex)
            {
                LogRecovery(session, ex);
                session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();
                current = Math.Max(0L, checked(current + ex.SuggestedOffsetSamples));
                continue;
            }

            if (field is null)
            {
                if (probeStart != 0)
                {
                    probeStart = 0;
                    current = 0;
                    session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();
                    continue;
                }

                return false;
            }

            long next = EstimateNextFieldStart(session, field);
            if (next <= current)
            {
                throw new InvalidOperationException("Decoded field did not advance the input position.");
            }

            if (previous is not null)
            {
                LaserDiscVbiInterpretation interpretation = LaserDiscVbiInterpreter.Interpret(
                    (previous.VbiData ?? []).Concat(field.VbiData ?? []),
                    framesPerSecond);
                if (interpretation.IsEarlyClv)
                {
                    DecodeSessionLogWriter.Append(
                        session,
                        "ERROR",
                        "Cannot seek in early CLV disks w/o timecode");
                    session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();
                    return false;
                }

                if (interpretation.FrameNumber.HasValue)
                {
                    frameNumber = interpretation.FrameNumber.Value;
                    frameStart = LaserDiscSeekDecodeStart(session, field);
                    DecodeSessionLogWriter.Append(
                        session,
                        "INFO",
                        $"seeking: file loc {LaserDiscSeekFileFrame(session, field)} frame # {frameNumber}");
                    session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();
                    return true;
                }
            }

            previous = field;
            validFieldCount++;
            current = next;
            session.TbcFieldDecoder.DiscardLaserDiscPreviousFieldContextAfterRecovery();
        }

        return false;
    }

    private static long LaserDiscSeekFileFrame(DecodeSession session, TbcDecodedField field)
    {
        double framesPerSecond = session.Parameters.SysParams.GetProperty("FPS").GetDouble();
        double samplesPerField = ((int)(session.DecodeSampleRateHz / (framesPerSecond * 2.0))) + 1;
        return checked((long)Math.Floor((field.StartSample / samplesPerField) / 2.0));
    }

    private static long LaserDiscSeekDecodeStart(DecodeSession session, TbcDecodedField field)
    {
        long samplesPerField = session.TbcFieldDecoder.EstimateNominalFieldSampleCount();
        long fieldIndex = checked((long)(field.StartSample / (double)samplesPerField));
        return checked(fieldIndex * samplesPerField);
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
        using (FileStream tbc = DecodeOutputFile.Create(paths.TbcPath))
        {
            foreach (TbcDecodedField field in writtenFields)
            {
                TbcOutputWriter.WriteFrame(tbc, field.Samples, session.TbcFrameSpec, field.OutputPayload);
            }
        }

        if (writtenFields.Count == 0 && session.ChromaOptions?.WriteChroma == true)
        {
            DecodeOutputFile.Create(paths.ChromaPath!).Dispose();
        }
        else if (TbcFirstFieldDecodeEngine.ShouldWriteChroma(session, writtenFields))
        {
            TbcFirstFieldDecodeEngine.WriteChromaFields(session, writtenFields, paths.ChromaPath);
        }

        IReadOnlyList<TbcDecodedField> metadataFields = _efmOutputWriter.Write(session, writtenFields);
        if (metadataFields.Count == 0)
        {
            using var metadata = new TbcOutputMetadataWriter.StreamingWriter(session, paths.JsonPath);
            metadata.LeaveIncompleteJson();
        }
        else
        {
            TbcOutputMetadataWriter.WriteJson(
                session,
                metadataFields,
                paths.JsonPath,
                writtenOrder,
                writtenFields);
        }

        if (session.Spec.Name == "ld" || session.ExecutionOptions.WriteDebugData)
        {
            TbcSqliteMetadataWriter.Write(
                session,
                metadataFields,
                paths.DbPath!,
                writtenOrder,
                writtenFields);
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

        long endSample = checked(summary.EndSample + TestLdfLookaheadSamples);
        if (endSample <= summary.StartSample)
        {
            return null;
        }

        session.RuntimeReporter?.BeginTestLdfReport(
            session.TestLdfOutputPath,
            summary.StartSample,
            endSample);
        LdTestLdfWriteResult result = _testLdfWriter.Write(
            session,
            summary.StartSample,
            endSample,
            input);
        session.RuntimeReporter?.CompleteTestLdfReport(result);
        return result;
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
        private readonly Stream _tbc;
        private readonly Stream? _chroma;
        private readonly ILaserDiscFieldOutputSession _laserDiscOutput;
        private readonly TbcOutputMetadataWriter.StreamingWriter _metadata;
        private readonly TbcSqliteMetadataWriter.SequenceWriter? _sqlite;
        private bool _payloadsClosed;
        private bool _completed;
        private int _writtenFieldCount;

        public int WrittenFieldCount => _writtenFieldCount;

        public StreamingOutputSession(
            DecodeSession session,
            ILaserDiscEfmOutputWriter efmOutputWriter,
            Func<string, Stream> createTbcOutput)
        {
            _session = session;
            _paths = TbcFirstFieldDecodeEngine.BuildOutputPaths(session);
            Stream? tbc = null;
            Stream? chroma = null;
            ILaserDiscFieldOutputSession? laserDiscOutput = null;
            TbcOutputMetadataWriter.StreamingWriter? metadata = null;
            TbcSqliteMetadataWriter.SequenceWriter? sqlite = null;
            try
            {
                bool isLaserDisc = session.Spec.Name == "ld";
                bool writeSqlite = isLaserDisc || session.ExecutionOptions.WriteDebugData;
                if (writeSqlite && !isLaserDisc)
                {
                    sqlite = new TbcSqliteMetadataWriter.SequenceWriter(session, _paths.DbPath!);
                }

                tbc = createTbcOutput(_paths.TbcPath);
                chroma = session.ChromaOptions?.WriteChroma == true
                    ? createTbcOutput(_paths.ChromaPath!)
                    : null;
                laserDiscOutput = efmOutputWriter.Open(session);
                if (writeSqlite && isLaserDisc)
                {
                    sqlite = new TbcSqliteMetadataWriter.SequenceWriter(session, _paths.DbPath!);
                }

                metadata = new TbcOutputMetadataWriter.StreamingWriter(session, _paths.JsonPath);
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

        public void WriteMetadataSnapshot()
        {
            ObjectDisposedException.ThrowIf(_payloadsClosed, this);
            _metadata.WriteSnapshot();
        }

        public TbcFieldSequenceDecodeResult Complete()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The streaming TBC output session was already completed.");
            }

            if (_metadata.FieldCount == 0)
            {
                TbcOutputMetadataWriter.ValidatePcmAudioParameters(_session);
                _metadata.LeaveIncompleteJson();
            }
            else
            {
                _metadata.Complete();
            }

            _sqlite?.Complete(_metadata.FieldCount);
            ClosePayloads();

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

            TbcDecodedField metadataField = _laserDiscOutput.WriteBeforeMetadata(field);
            System.Text.Json.Nodes.JsonObject fieldInfo = _metadata.Add(metadataField, decision, field);
            _sqlite?.Add(fieldInfo, _metadata.FieldCount, _metadata.LastOutputConverter);

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

            _writtenFieldCount++;
            _laserDiscOutput.WriteAfterVideo(metadataField);
        }

        private void ClosePayloads()
        {
            if (_payloadsClosed)
            {
                return;
            }

            _payloadsClosed = true;
            _chroma?.Dispose();
            _tbc.Dispose();
            _laserDiscOutput.Dispose();
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
            int? previousMismatchedPhase = null;
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
                    previousMismatchedPhase = previous.Field.FieldPhaseId.Value;
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
                if (previousMismatchedPhase.HasValue && field.FieldPhaseId.HasValue)
                {
                    DecodeSessionLogWriter.Append(
                        _session,
                        "WARNING",
                        $"At field #{decision.SeqNo - 1}, Field phaseID sequence mismatch ({previousMismatchedPhase.Value}->{field.FieldPhaseId.Value}) (player may be paused)");
                }

                DecodeSessionLogWriter.Append(_session, "ERROR", "Skipped field");
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
