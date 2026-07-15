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
        long? FirstDecodedSample,
        long? EndDecodedSample);

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
                    output ??= new StreamingOutputSession(session, _efmOutputWriter);
                    output.Write(writes);
                },
                writeMetadataSnapshot: () =>
                {
                    output ??= new StreamingOutputSession(session, _efmOutputWriter);
                    output.WriteMetadataSnapshot();
                });
            output ??= new StreamingOutputSession(session, _efmOutputWriter);
            TbcFieldSequenceDecodeResult result = output.Complete();
            outputCompleted = true;
            LdTestLdfWriteResult? testLdf = WriteOptionalTestLdf(session, input, summary);
            return testLdf.HasValue
                ? result with
                {
                    Message = result.Message + "; " + testLdf.Value.Message,
                    TestLdf = testLdf
                }
                : result;
        }
        catch (Exception ex)
        {
            if (!outputCompleted)
            {
                if (output is null && ex is OperationCanceledException or DecodeFieldReadException)
                {
                    output = new StreamingOutputSession(session, _efmOutputWriter);
                }

                if (output is not null)
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
            }

            throw;
        }
        finally
        {
            output?.Dispose();
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
        int requestedFields = maxFields ?? session.RunBounds.RequestedFieldCount;
        if (requestedFields < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFields));
        }

        _cancellationToken.ThrowIfCancellationRequested();
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
            if (session.Spec.Name == "ld")
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
            }
            else if (session.Spec.Name == "cvbs" && !isFirstField && writes.Count > 0)
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
                LogRecovery(session, ex);
                if (ex.StopAfterDecodedFields && decodedFieldCount > 0)
                {
                    break;
                }

                begin = Math.Max(0L, checked(begin + ex.SuggestedOffsetSamples));
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
            firstDecodedSample ??= field.StartSample;
            endDecodedSample = EstimateNextFieldStart(session, field);
            long nextBegin = endDecodedSample.Value;
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

            autoMtf?.ObserveAcceptedField(field, session.System);
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

            if (ShouldStopAfterLaserDiscLeadOut(
                    session,
                    field,
                    isFirstField,
                    ref laserDiscLeadOutCount))
            {
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
            firstDecodedSample,
            endDecodedSample);
    }

    internal static string FormatLaserDiscFrameStatus(
        int fieldsWritten,
        int estimatedFrames,
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
            return prefix + $"Timecode {interpretation.ClvMinutes.Value}:xx";
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
                + $"Frame #{interpretation.FrameNumber.Value}";
        }

        if (interpretation.FrameNumber is > 0)
        {
            return prefix + $"Frame #{interpretation.FrameNumber.Value}";
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

    private static void LogRecovery(DecodeSession session, TbcFieldDecodeRecoveryException exception)
    {
        string? message = (session.Spec.Name, exception.Kind) switch
        {
            ("vhs", TbcFieldDecodeRecoveryKind.NoSyncPulses) =>
                "Unable to find any sync pulses, jumping 100 ms",
            ("vhs", TbcFieldDecodeRecoveryKind.NoFirstHSync) =>
                "Unable to determine start of field - dropping field",
            ("cvbs", TbcFieldDecodeRecoveryKind.NoSyncPulses) =>
                "Unable to find any sync pulses, skipping one second",
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
            && session.ExecutionOptions.RequestedThreads != 0
            && ShouldDeferCvbsOutputConversion(session);
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
        TbcDecodedField? first = ReadFieldWithContext(
            session,
            input,
            begin,
            readLength,
            fieldNumber: 0);
        if (first is null)
        {
            return false;
        }

        long secondBegin = EstimateNextFieldStart(session, first);
        TbcDecodedField? second = ReadFieldWithContext(
            session,
            input,
            secondBegin,
            readLength,
            fieldNumber: 1);
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

            ClosePayloads();
            if (_writtenFieldCount == 0)
            {
                _metadata.LeaveIncompleteJson();
            }
            else
            {
                _metadata.Complete();
            }

            _sqlite?.Complete(_metadata.FieldCount);

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
