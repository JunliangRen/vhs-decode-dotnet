using System.Globalization;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Rf;

namespace VHSDecode.Core.Dsp.CudaFast;

internal interface ICudaFastDecodeRunner
{
    TbcFieldSequenceDecodeResult TryDecodeAndWrite(
        ParsedCommand command,
        TextWriter output,
        CancellationToken cancellationToken);
}

internal sealed class CudaFastDecodeRunner : ICudaFastDecodeRunner
{
    private const int MaximumManagedReadSamples = 1024 * 1024;
    private const int DeviceId = 0;

    private static readonly HashSet<string> SupportedExplicitOptions = new(
        StringComparer.Ordinal)
    {
        "dsp_backend",
        "inputfreq",
        "length",
        "no_resample",
        "ntsc",
        "overwrite",
        "pal",
        "start",
        "start_fileloc",
        "system",
        "tape_format",
        "tape_speed",
        "threads"
    };

    private readonly Func<TextWriter, CancellationToken, ICudaFastNativeRuntime> _runtimeFactory;

    internal CudaFastDecodeRunner()
    {
        _runtimeFactory = CudaFastNativeRuntime.RequireAvailable;
    }

    internal CudaFastDecodeRunner(Func<ICudaFastNativeRuntime> runtimeFactory)
    {
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        _runtimeFactory = (_, _) => runtimeFactory();
    }

    public TbcFieldSequenceDecodeResult TryDecodeAndWrite(
        ParsedCommand command,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(output);

        IRfSampleLoader? loader = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CudaFastProfile profile = ValidateCommand(command);
            int nominalFieldSamples = EstimateNominalFieldSampleCount(profile);
            DecodeRunBounds runBounds = DecodeRunBounds.FromCommand(
                command,
                nominalFieldSamples);
            if (!TryGetInputSampleCount(command.InputFile, out long sourceSampleCount))
            {
                throw new NotSupportedException(
                    $"'{DspBackendParser.CudaFastValue}' cannot determine the RF sample count for '{command.InputFile}'. "
                    + "The first implementation supports native-rate .ldf/.flac and unpacked .s16/.raw/.r16/.u16/.rf/.s8/.r8/.u8 inputs.");
            }

            long sourceStart = runBounds.StartPosition.ResolveForRead();
            if (sourceStart < 0 || sourceStart >= sourceSampleCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(command),
                    $"CUDA-fast start sample {sourceStart} is outside the input's {sourceSampleCount} samples.");
            }

            int? requestedFields = GetExplicitRequestedFieldCount(command, runBounds);
            long availableSamples = sourceSampleCount - sourceStart;
            long decodeSampleCount = ResolveDecodeSampleCount(
                availableSamples,
                requestedFields,
                nominalFieldSamples);
            if (decodeSampleCount <= 0)
            {
                throw new InvalidOperationException(
                    "CUDA-fast requires at least one field of RF samples after the selected start position.");
            }

            ICudaFastNativeRuntime runtime = _runtimeFactory(output, cancellationToken);
            CudaFastRuntimeInfo runtimeInfo = runtime.GetRuntimeInfo(DeviceId);
            string runtimeDiagnostic = FormatRuntimeDiagnostic(runtimeInfo);
            string logPath = command.OutputBase + ".log";
            File.WriteAllText(logPath, string.Empty);
            WriteDiagnostic(output, logPath, runtimeDiagnostic);
            if (command.GetSource("threads") != ParsedOptionSource.Default)
            {
                WriteDiagnostic(
                    output,
                    logPath,
                    "CUDA-fast owns GPU scheduling; --threads does not select CUDA kernel concurrency.");
            }

            loader = CreateInputLoader(command.InputFile);
            using var callbackContext = new ManagedReadContext(
                loader,
                command.InputFile,
                sourceStart,
                decodeSampleCount,
                cancellationToken);
            WriteDiagnostic(
                output,
                logPath,
                callbackContext.InputSampleFormat == CudaFastInputSampleFormat.Int16
                    ? "CUDA-fast RF input: PCM16 direct upload with GPU FP32 conversion."
                    : "CUDA-fast RF input: managed FP32 upload.");
            GCHandle contextHandle = GCHandle.Alloc(callbackContext);
            try
            {
                CudaFastReadCallback readCallback = ReadCallback;
                CudaFastCancelCallback cancelCallback = CancelCallback;
                CudaFastNativeResult nativeResult = runtime.Run(
                    new CudaFastNativeRunConfiguration(
                        profile,
                        ResolveTapeSpeed(command.Get<string>("tape_speed")),
                        DeviceId,
                        40.0,
                        checked((ulong)decodeSampleCount),
                        requestedFields.HasValue
                            ? checked((uint)requestedFields.Value)
                            : 0U,
                        command.OutputBase,
                        command.Get<bool>("overwrite"),
                        callbackContext.InputSampleFormat,
                        readCallback,
                        cancelCallback,
                        GCHandle.ToIntPtr(contextHandle)));
                callbackContext.ThrowIfFailed();
                cancellationToken.ThrowIfCancellationRequested();

                CudaFastOutputSummary summary = FinalizeOutputs(
                    command.OutputBase,
                    sourceStart,
                    requestedFields,
                    nativeResult);
                string message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"CUDA-fast wrote {summary.WrittenFields} TBC fields in {nativeResult.ElapsedSeconds:F3} seconds.");
                return new TbcFieldSequenceDecodeResult(
                    Success: true,
                    Message: message,
                    Paths: summary.Paths,
                    Fields: [],
                    WrittenFieldCount: summary.WrittenFields);
            }
            finally
            {
                if (contextHandle.IsAllocated)
                {
                    contextHandle.Free();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException
            or CudaFastBackendUnavailableException
            or InvalidOperationException
            or IOException
            or JsonException
            or NotSupportedException
            or OverflowException
            or UnauthorizedAccessException)
        {
            return new TbcFieldSequenceDecodeResult(
                Success: false,
                Message: ex.Message,
                Paths: null,
                Fields: []);
        }
        finally
        {
            (loader as IDisposable)?.Dispose();
        }
    }

    internal static string FormatRuntimeDiagnostic(CudaFastRuntimeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        double gibibytes = info.TotalVramBytes / (1024.0 * 1024.0 * 1024.0);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"CUDA-fast full DSP backend: {info.DeviceName} (compute {info.ComputeMajor}.{info.ComputeMinor}, {gibibytes:F1} GiB VRAM, bridge-abi=0x{info.AbiVersion:X8})");
    }

    internal static CudaFastProfile ValidateCommand(ParsedCommand command)
    {
        if (command.Spec != CliSpecs.Vhs)
        {
            throw new NotSupportedException(
                $"'{DspBackendParser.CudaFastValue}' currently supports only the vhs command.");
        }

        foreach ((string destination, ParsedOptionSource source) in command.OptionSources)
        {
            if (source != ParsedOptionSource.Default
                && !SupportedExplicitOptions.Contains(destination))
            {
                OptionSpec? option = command.Spec.Options.FirstOrDefault(
                    candidate => candidate.Destination == destination);
                string displayName = option?.DisplayName ?? destination;
                throw new NotSupportedException(
                    $"'{DspBackendParser.CudaFastValue}' does not implement the explicit {displayName} option; it was not ignored and no CPU fallback was performed.");
            }
        }

        if (!string.Equals(
                command.Get<string>("tape_format"),
                "VHS",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"'{DspBackendParser.CudaFastValue}' currently implements VHS only; tape format '{command.Get<string>("tape_format")}' was not decoded with another profile.");
        }

        CudaFastProfile profile = ResolveProfile(VideoSystemSelector.Select(command));
        _ = ResolveTapeSpeed(command.Get<string>("tape_speed"));

        double inputSampleRateMhz = ResolveInputSampleRateMhz(command);
        if (Math.Abs(inputSampleRateMhz - 40.0) > 0.0000005)
        {
            throw new NotSupportedException(
                $"'{DspBackendParser.CudaFastValue}' currently accepts native-rate 40 MSPS input only; selected input rate was {inputSampleRateMhz:R} MSPS.");
        }

        return profile;
    }

    internal static bool TryGetInputSampleCount(string path, out long sampleCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((path.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            && RawFlacStreamInfo.TryRead(path, out RawFlacStreamInfo info)
            && info.TotalSamples is > 0 and long totalSamples)
        {
            sampleCount = totalSamples;
            return true;
        }

        int bytesPerSample = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".s16" or ".raw" or ".r16" or ".u16" => 2,
            ".rf" => 4,
            ".s8" or ".r8" or ".u8" => 1,
            _ => 0
        };
        if (bytesPerSample == 0)
        {
            sampleCount = 0;
            return false;
        }

        long bytes = new FileInfo(path).Length;
        sampleCount = bytes / bytesPerSample;
        return sampleCount > 0;
    }

    internal static long ResolveDecodeSampleCount(
        long availableSamples,
        int? requestedFields,
        int nominalFieldSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nominalFieldSamples);
        // The native pipeline stops after MaximumOutputFields valid fields.
        // Keep the remaining source visible so it can scan past leader/noise
        // instead of fabricating nominal fields to satisfy a short --length.
        return availableSamples;
    }

    internal static CudaFastOutputSummary FinalizeOutputs(
        string outputBase,
        long sourceStart,
        int? requestedFields,
        CudaFastNativeResult nativeResult)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBase);
        string lumaPath = outputBase + ".tbc";
        string chromaPath = outputBase + "_chroma.tbc";
        string jsonPath = outputBase + ".tbc.json";
        JsonObject root = JsonNode.Parse(File.ReadAllText(jsonPath))?.AsObject()
            ?? throw new JsonException("CUDA-fast metadata root was not an object.");
        JsonObject videoParameters = root["videoParameters"]?.AsObject()
            ?? throw new JsonException("CUDA-fast metadata omitted videoParameters.");
        JsonArray fields = root["fields"]?.AsArray()
            ?? throw new JsonException("CUDA-fast metadata omitted fields.");

        if (videoParameters["fieldWidth"] is not JsonValue fieldWidthNode
            || !fieldWidthNode.TryGetValue(out int metadataFieldWidth)
            || videoParameters["fieldHeight"] is not JsonValue fieldHeightNode
            || !fieldHeightNode.TryGetValue(out int metadataFieldHeight))
        {
            throw new JsonException(
                "CUDA-fast metadata omitted integer fieldWidth or fieldHeight values.");
        }
        if (metadataFieldWidth <= 0
            || metadataFieldHeight <= 0
            || nativeResult.OutputLineLength != checked((uint)metadataFieldWidth)
            || nativeResult.OutputFieldLines != checked((uint)metadataFieldHeight))
        {
            throw new InvalidOperationException(
                $"CUDA-fast native geometry {nativeResult.OutputLineLength}x{nativeResult.OutputFieldLines} did not match metadata geometry {metadataFieldWidth}x{metadataFieldHeight}.");
        }
        if (videoParameters["numberOfSequentialFields"] is not JsonValue fieldCountNode
            || !fieldCountNode.TryGetValue(out int metadataFieldCount)
            || metadataFieldCount != fields.Count)
        {
            throw new InvalidOperationException(
                "CUDA-fast metadata field count did not match its fields array.");
        }
        if (nativeResult.FieldsWritten > int.MaxValue
            || checked((int)nativeResult.FieldsWritten) != fields.Count)
        {
            throw new InvalidOperationException(
                $"CUDA-fast native field count {nativeResult.FieldsWritten} did not match metadata field count {fields.Count}.");
        }

        int writtenFields = fields.Count;
        if (requestedFields.HasValue)
        {
            writtenFields = Math.Min(writtenFields, requestedFields.Value);
        }

        if (writtenFields <= 0)
        {
            throw new InvalidOperationException(
                "CUDA-fast did not find a complete field in the selected RF range.");
        }

        while (fields.Count > writtenFields)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        for (int index = 0; index < fields.Count; index++)
        {
            JsonObject field = fields[index]?.AsObject()
                ?? throw new JsonException($"CUDA-fast metadata field {index} was not an object.");
            field["seqNo"] = index + 1;
            if (field["fileLoc"] is not JsonValue fileLocation
                || !fileLocation.TryGetValue(out long relativeFileLocation))
            {
                throw new JsonException(
                    $"CUDA-fast metadata field {index} omitted an integer fileLoc.");
            }

            field["fileLoc"] = checked(sourceStart + relativeFileLocation);
        }

        videoParameters["numberOfSequentialFields"] = writtenFields;
        videoParameters["dspBackend"] = DspBackendParser.CudaFastValue;

        long bytesPerField = checked(
            (long)nativeResult.OutputLineLength
            * nativeResult.OutputFieldLines
            * sizeof(ushort));
        long retainedBytes = checked(bytesPerField * writtenFields);
        TruncateOutput(lumaPath, retainedBytes);
        TruncateOutput(chromaPath, retainedBytes);
        WriteJsonAtomically(jsonPath, root);

        return new CudaFastOutputSummary(
            writtenFields,
            new TbcOutputPaths(lumaPath, jsonPath, chromaPath));
    }

    private static int? GetExplicitRequestedFieldCount(
        ParsedCommand command,
        DecodeRunBounds runBounds)
    {
        if (command.GetSource("length") == ParsedOptionSource.Default)
        {
            return null;
        }

        BigInteger fields = runBounds.RequestedFieldCount;
        if (fields <= BigInteger.Zero)
        {
            throw new NotSupportedException(
                $"'{DspBackendParser.CudaFastValue}' does not yet emit an empty output for --length 0.");
        }

        if (fields > int.MaxValue)
        {
            throw new NotSupportedException(
                $"'{DspBackendParser.CudaFastValue}' explicit field count exceeds {int.MaxValue}.");
        }

        return (int)fields;
    }

    private static double ResolveInputSampleRateMhz(ParsedCommand command)
    {
        if (!command.Values.TryGetValue("inputfreq", out object? value)
            || value is null)
        {
            return FrequencyParser.DddMHz;
        }

        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static int EstimateNominalFieldSampleCount(CudaFastProfile profile)
    {
        double framesPerSecond = profile switch
        {
            CudaFastProfile.Pal => 25.0,
            CudaFastProfile.Ntsc => 30_000.0 / 1_001.0,
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
        return checked((int)(40_000_000.0 / (framesPerSecond * 2.0)) + 1);
    }

    private static IRfSampleLoader CreateInputLoader(string path)
    {
        IRfSampleLoader loader = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".raw" or ".s16" => new DirectInt16SampleLoader(),
            ".s8" => new Int8SampleLoader(),
            _ => RfLoaderFactory.CreateNative(
                path,
                preferPyAvMappedRawFlacSeeking: false,
                fastContainerSeeking: false,
                ignoreExtensionCase: true)
        };
        return loader is FfmpegPcm16SampleLoader ffmpeg
            ? new FfmpegPcm16InputAdapter(ffmpeg)
            : loader;
    }

    internal static int ReadInt16WithFallback(
        IRfSampleLoader loader,
        Stream stream,
        long sample,
        Span<short> destination)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        if (loader is not IInt16RfSampleLoader int16Loader)
        {
            throw new IOException(
                "The selected CUDA-fast PCM16 input loader does not expose native-width samples.");
        }

        if (int16Loader.TryReadInt16(
                stream,
                sample,
                destination,
                out int directRead))
        {
            if ((uint)directRead > (uint)destination.Length)
            {
                throw new IOException(
                    $"The PCM16 RF loader returned {directRead} samples for a {destination.Length}-sample request.");
            }

            return directRead;
        }

        double[]? fallback = loader.Read(stream, sample, destination.Length);
        if (fallback is null)
        {
            return 0;
        }
        if (fallback.Length > destination.Length)
        {
            throw new IOException(
                $"The fallback PCM16 RF loader returned {fallback.Length} samples for a {destination.Length}-sample request.");
        }

        for (int i = 0; i < fallback.Length; i++)
        {
            double value = fallback[i];
            if (!double.IsFinite(value)
                || value < short.MinValue
                || value > short.MaxValue
                || value != Math.Truncate(value))
            {
                throw new IOException(
                    $"The fallback PCM16 RF loader returned invalid sample {value:R} at index {i}.");
            }

            destination[i] = (short)value;
        }

        return fallback.Length;
    }

    private static void WriteDiagnostic(
        TextWriter output,
        string logPath,
        string message)
    {
        output.WriteLine(message);
        DecodeSessionLogWriter.Append(logPath, "INFO", message);
    }

    private static CudaFastProfile ResolveProfile(string system)
    {
        return FormatCatalog.NormalizeSystem(system) switch
        {
            "NTSC" => CudaFastProfile.Ntsc,
            "PAL" => CudaFastProfile.Pal,
            string unsupported => throw new NotSupportedException(
                $"'{DspBackendParser.CudaFastValue}' currently implements NTSC and PAL VHS only; system '{unsupported}' was not approximated.")
        };
    }

    private static CudaFastTapeSpeed ResolveTapeSpeed(string tapeSpeed)
    {
        return FormatCatalog.NormalizeTapeSpeedName(tapeSpeed) switch
        {
            "sp" => CudaFastTapeSpeed.Sp,
            "lp" => CudaFastTapeSpeed.Lp,
            "ep" => CudaFastTapeSpeed.Ep,
            string unsupported => throw new NotSupportedException(
                $"'{DspBackendParser.CudaFastValue}' does not implement VHS tape speed '{unsupported}'.")
        };
    }

    private static void TruncateOutput(string path, long length)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite);
        if (stream.Length < length)
        {
            throw new InvalidOperationException(
                $"CUDA-fast output '{path}' was shorter than its metadata contract ({stream.Length} < {length} bytes).");
        }

        stream.SetLength(length);
    }

    private static void WriteJsonAtomically(string path, JsonObject root)
    {
        string temporaryPath = path + ".cuda-fast.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                    + Environment.NewLine);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static nuint ReadCallback(
        nint userData,
        nint destination,
        ulong sampleOffset,
        nuint sampleCount)
    {
        try
        {
            return GetContext(userData).Read(destination, sampleOffset, sampleCount);
        }
        catch (Exception ex)
        {
            GetContext(userData).CaptureFailure(ex);
            return 0;
        }
    }

    private static int CancelCallback(nint userData)
        => GetContext(userData).IsCancellationRequested ? 1 : 0;

    private static ManagedReadContext GetContext(nint userData)
        => (ManagedReadContext)(GCHandle.FromIntPtr(userData).Target
            ?? throw new InvalidOperationException("CUDA-fast managed reader context was released."));

    internal sealed record CudaFastOutputSummary(
        int WrittenFields,
        TbcOutputPaths Paths);

    private sealed class FfmpegPcm16InputAdapter(
        FfmpegPcm16SampleLoader loader) : IInt16RfSampleLoader, IDisposable
    {
        private readonly FfmpegPcm16SampleLoader _loader = loader;

        public double[]? Read(Stream stream, long sample, int readLength)
            => _loader.Read(stream, sample, readLength);

        public bool TryReadInt16(
            Stream stream,
            long sample,
            Span<short> destination,
            out int samplesRead)
            => _loader.TryReadInt16(stream, sample, destination, out samplesRead);

        public void Dispose() => _loader.Dispose();
    }

    private sealed class ManagedReadContext : IDisposable
    {
        private readonly IRfSampleLoader _loader;
        private readonly FileStream _input;
        private readonly long _sourceStart;
        private readonly long _sampleCount;
        private readonly CancellationToken _cancellationToken;
        private readonly CudaFastInputSampleFormat _inputSampleFormat;
        private readonly bool _useDirectFloat32Input;
        private readonly object _gate = new();
        private float[] _floatSamples = Array.Empty<float>();
        private ExceptionDispatchInfo? _failure;

        internal ManagedReadContext(
            IRfSampleLoader loader,
            string inputPath,
            long sourceStart,
            long sampleCount,
            CancellationToken cancellationToken)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _sourceStart = sourceStart;
            _sampleCount = sampleCount;
            _cancellationToken = cancellationToken;
            bool forceLegacyInput = string.Equals(
                Environment.GetEnvironmentVariable(
                    "VHSDECODE_CUDA_FAST_FORCE_LEGACY_INPUT"),
                "1",
                StringComparison.Ordinal);
            bool forceFloat32Input = string.Equals(
                Environment.GetEnvironmentVariable(
                    "VHSDECODE_CUDA_FAST_FORCE_FP32_INPUT"),
                "1",
                StringComparison.Ordinal);
            _inputSampleFormat = !forceLegacyInput
                && !forceFloat32Input
                && loader is IInt16RfSampleLoader
                    ? CudaFastInputSampleFormat.Int16
                    : CudaFastInputSampleFormat.Float32;
            _useDirectFloat32Input = !forceLegacyInput;
            _input = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.SequentialScan);
        }

        internal bool IsCancellationRequested => _cancellationToken.IsCancellationRequested;

        internal CudaFastInputSampleFormat InputSampleFormat => _inputSampleFormat;

        internal unsafe nuint Read(nint destination, ulong sampleOffset, nuint sampleCount)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (sampleOffset >= checked((ulong)_sampleCount) || sampleCount == 0)
            {
                return 0;
            }

            ulong allowed = Math.Min(
                checked((ulong)sampleCount),
                checked((ulong)_sampleCount) - sampleOffset);
            ulong written = 0;
            lock (_gate)
            {
                while (written < allowed)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    int count = checked((int)Math.Min(
                        (ulong)MaximumManagedReadSamples,
                        allowed - written));
                    long absoluteSample = checked(
                        _sourceStart + (long)sampleOffset + (long)written);
                    if (_inputSampleFormat == CudaFastInputSampleFormat.Int16)
                    {
                        var directDestination = new Span<short>(
                            (void*)(destination + checked((nint)(written * sizeof(short)))),
                            count);
                        int directRead = ReadInt16WithFallback(
                            _loader,
                            _input,
                            absoluteSample,
                            directDestination);

                        written += checked((uint)directRead);
                        if (directRead < count)
                        {
                            break;
                        }

                        continue;
                    }

                    if (_useDirectFloat32Input
                        && _loader is IFloat32RfSampleLoader float32Loader)
                    {
                        var directDestination = new Span<float>(
                            (void*)(destination + checked((nint)(written * sizeof(float)))),
                            count);
                        if (float32Loader.TryReadFloat32(
                                _input,
                                absoluteSample,
                                directDestination,
                                out int directRead))
                        {
                            if ((uint)directRead > (uint)count)
                            {
                                throw new IOException(
                                    $"The FP32 RF loader returned {directRead} samples for a {count}-sample request.");
                            }

                            written += checked((uint)directRead);
                            if (directRead < count)
                            {
                                break;
                            }

                            continue;
                        }
                    }

                    double[]? samples = _loader is IReusableRfSampleLoader reusable
                        ? reusable.ReadReusable(_input, absoluteSample, count)
                        : _loader.Read(_input, absoluteSample, count);
                    if (samples is null || samples.Length < count)
                    {
                        if (samples is not null && _loader is IReusableRfSampleLoader partialReusable)
                        {
                            partialReusable.ReturnReusable(samples);
                        }

                        break;
                    }

                    try
                    {
                        if (_floatSamples.Length < count)
                        {
                            _floatSamples = GC.AllocateUninitializedArray<float>(count);
                        }
                        for (int index = 0; index < count; index++)
                        {
                            _floatSamples[index] = checked((float)samples[index]);
                        }
                        Marshal.Copy(
                            _floatSamples,
                            0,
                            destination + checked((nint)(written * sizeof(float))),
                            count);
                    }
                    finally
                    {
                        if (_loader is IReusableRfSampleLoader reusableOwner)
                        {
                            reusableOwner.ReturnReusable(samples);
                        }
                    }

                    written += checked((uint)count);
                }
            }

            return checked((nuint)written);
        }

        internal void CaptureFailure(Exception exception)
        {
            lock (_gate)
            {
                _failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        internal void ThrowIfFailed() => _failure?.Throw();

        public void Dispose() => _input.Dispose();
    }
}
