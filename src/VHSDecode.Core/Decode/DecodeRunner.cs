using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.CudaFast;
using VHSDecode.Core.Dsp.Ipp;
using VHSDecode.Core.Formats;
using VHSDecode.Core.HiFi;
using VHSDecode.Core.Rf;
using VHSDecode.Core.Tbc;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.Decode;

public sealed class DecodeRunner
{
    private static readonly Func<CancellationToken, TbcFieldSequenceDecodeEngine> DefaultEngineFactory =
        cancellationToken => new TbcFieldSequenceDecodeEngine(cancellationToken: cancellationToken);
    private readonly Func<CancellationToken, TbcFieldSequenceDecodeEngine> _engineFactory;
    private readonly IHiFiCommandRunner _hiFiRunner;
    private readonly ICudaFastDecodeRunner _cudaFastRunner;
    private readonly Func<Complex[], TextWriter, CancellationToken, ILaserDiscCudaEfm> _laserDiscCudaEfmFactory;
    private readonly Func<DecodeFilterSet, TextWriter, CancellationToken, ILaserDiscCudaVideo> _laserDiscCudaVideoFactory;

    public DecodeRunner()
        : this(
            DefaultEngineFactory,
            new HiFiDecodeRunner(),
            new CudaFastDecodeRunner())
    {
    }

    internal DecodeRunner(Func<CancellationToken, TbcFieldSequenceDecodeEngine> engineFactory)
        : this(engineFactory, new HiFiDecodeRunner(), new CudaFastDecodeRunner())
    {
    }

    internal DecodeRunner(
        Func<CancellationToken, TbcFieldSequenceDecodeEngine> engineFactory,
        IHiFiCommandRunner hiFiRunner)
        : this(engineFactory, hiFiRunner, new CudaFastDecodeRunner())
    {
    }

    internal DecodeRunner(
        Func<CancellationToken, TbcFieldSequenceDecodeEngine> engineFactory,
        IHiFiCommandRunner hiFiRunner,
        ICudaFastDecodeRunner cudaFastRunner,
        Func<Complex[], TextWriter, CancellationToken, ILaserDiscCudaEfm>? laserDiscCudaEfmFactory = null,
        Func<DecodeFilterSet, TextWriter, CancellationToken, ILaserDiscCudaVideo>? laserDiscCudaVideoFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFactory);
        ArgumentNullException.ThrowIfNull(hiFiRunner);
        ArgumentNullException.ThrowIfNull(cudaFastRunner);
        _engineFactory = engineFactory;
        _hiFiRunner = hiFiRunner;
        _cudaFastRunner = cudaFastRunner;
        _laserDiscCudaEfmFactory = laserDiscCudaEfmFactory ?? CudaFastNativeRuntime.CreateLaserDiscEfm;
        _laserDiscCudaVideoFactory = laserDiscCudaVideoFactory ?? CudaFastNativeRuntime.CreateLaserDiscVideo;
    }

    // Only the public default Runner has this factory identity. Ordinary
    // construction finishes first; no publicly obtainable session is changed.
    private static DecodeSession CreatePrivateDefaultRunnerSession(
        ParsedCommand command,
        out RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader? ownedReader)
    {
        ownedReader = null;
        DecodeSession session = DecodeSessionFactory.Create(command);
        try
        {
            if (session.Spec.Name == "ld"
                && !session.ExecutionOptions.SuppressFileOutputs
                && session.TbcFieldDecoder.CanOmitEnvelopeForImmediateLaserDiscField)
            {
                ownedReader = session.StreamDecoder.TryCreateOwnedImmediateLaserDiscFieldReader();
            }
            return session;
        }
        catch
        {
            try { session.Dispose(); }
            catch { /* Preserve private setup's primary failure. */ }
            throw;
        }
    }

    public int Run(
        ParsedCommand command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (command.Get<bool>("help"))
        {
            output.Write(CommandHelpFormatter.Format(command.Spec, command.ProgramName));
            return 0;
        }

        if (command.Spec == CliSpecs.LaserDisc && command.Get<bool>("version"))
        {
            output.WriteLine(DecodeVersionInfo.Version);
            return 0;
        }

        if (command.Spec == CliSpecs.HiFi)
        {
            try
            {
                return _hiFiRunner.Run(command, output, error, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                output.WriteLine();
                output.WriteLine("Ctrl-C was pressed, stopping decode...");
                return 1;
            }
            catch (Exception ex) when (ex is ArgumentException
                or DivideByZeroException
                or FormatException
                or OverflowException
                or NotSupportedException
                or IppBackendUnavailableException
                or IOException)
            {
                error.WriteLine(ex.Message);
                return 1;
            }
        }

        if (command.Spec.Name is "vhs" or "cvbs"
            && (command.InputFile.Length == 0 || command.OutputBase.Length == 0))
        {
            return WriteInputOutputArgumentFailure(command, output);
        }

        bool cudaFastLaserDisc = command.Spec == CliSpecs.LaserDisc
            && string.Equals(
                command.Get<string>("dsp_backend"),
                DspBackendParser.CudaFastValue,
                StringComparison.OrdinalIgnoreCase);
        bool cudaFastLaserDiscVideoOnly = cudaFastLaserDisc && command.Get<bool>("noefm");
        if (cudaFastLaserDisc && command.Get<bool>("preview_server"))
        {
            error.WriteLine("'cuda-fast' does not support LaserDisc preview.");
            return 1;
        }

        ValidateRequiredPositionals(command);
        if (command.Spec == CliSpecs.LaserDisc
            && GetSystemConflict(command) is { } laserDiscSystemConflict)
        {
            output.WriteLine(laserDiscSystemConflict);
            return 1;
        }

        WriteEarlyCompatibilityWarnings(command, error);
        if (command.Spec.Name is "vhs" or "cvbs")
        {
            try
            {
                DecodeOutputPreflight.Validate(command);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                output.WriteLine(ex.Message);
                return 1;
            }

            if (GetSystemConflict(command) is { } systemConflict)
            {
                output.WriteLine(systemConflict);
                return 1;
            }
        }

        try
        {
            ValidateSystemSpecificOptions(command);
            if (command.Spec == CliSpecs.LaserDisc)
            {
                DecodeOutputPreflight.Validate(command);
                if (cudaFastLaserDisc)
                {
                    ValidateLaserDiscCudaOptions(command);
                }

                if (command.Get<bool>("pal") && command.Get<bool>("AC3"))
                {
                    output.WriteLine("ERROR: AC3 audio decoding is only supported for NTSC");
                    return 1;
                }
            }

            var runtimeReporter = new DecodeRuntimeReporter(output, error);
            DecodeSession? session = null;
            try
            {
                DspBackend dspBackend = DspBackendParser.Parse(
                    command.Get<string>("dsp_backend"));
                ApproxProviderResolver.EnsureOptionCompatible(
                    dspBackend,
                    command.GetSource("approx_provider")
                        != ParsedOptionSource.Default);
                ApproxResamplerResolver.EnsureOptionCompatible(
                    dspBackend,
                    command.GetSource("approx_resampler")
                        != ParsedOptionSource.Default);
                ApproxPrecisionResolver.EnsureOptionCompatible(
                    dspBackend,
                    command.GetSource("approx_precision")
                        != ParsedOptionSource.Default);
                TbcFieldSequenceDecodeResult result;
                if (dspBackend == DspBackend.CudaFast && !cudaFastLaserDisc)
                {
                    result = _cudaFastRunner.TryDecodeAndWrite(
                        command,
                        output,
                        cancellationToken);
                }
                else
                {
                    RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader? ownedReader = null;
                    ParsedCommand sessionCommand = cudaFastLaserDisc
                        ? CreateLaserDiscCudaCpuCommand(command)
                        : command;
                    session = cudaFastLaserDisc || ReferenceEquals(_engineFactory, DefaultEngineFactory)
                        ? CreatePrivateDefaultRunnerSession(sessionCommand, out ownedReader)
                        : DecodeSessionFactory.Create(sessionCommand);
                    // The original requested backend distinguishes CPU Exact from the CUDA complement.
                    if (ownedReader is not null && command.Spec == CliSpecs.LaserDisc
                        && !command.Get<bool>("preview_server") && session.System == "NTSC")
                    {
                        if (dspBackend == DspBackend.IppFast)
                        {
                            ownedReader.EnableCompactCpuPostFmVideoIfSupported();
                        }
                        else if (dspBackend == DspBackend.Exact)
                        {
                            ownedReader.EnableExactLaserDiscS0ReuseIfSupported();
                        }
                    }
                    if (cudaFastLaserDisc)
                    {
                        ownedReader = RequireLaserDiscCudaReader(session, ownedReader);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    using ILaserDiscCudaEfm? cudaEfm = cudaFastLaserDisc && !cudaFastLaserDiscVideoOnly
                        ? ownedReader!.AttachCudaEfm(filter => _laserDiscCudaEfmFactory(filter, output, cancellationToken))
                        : null;
                    if (cudaEfm is not null)
                    {
                        output.WriteLine(cudaEfm.Diagnostic);
                    }

                    bool requireCudaVideo = cudaFastLaserDiscVideoOnly && !session.RunBounds.RequestedFieldCount.IsZero;
                    bool standardCudaLdOutput = (requireCudaVideo || cudaEfm is LaserDiscCudaEfm)
                        && session.RunBounds.RequestedFieldCount > 100
                        && session.InputFile != "-" && session.TestLdfOutputPath is null
                        && session.StreamDecoder.WorkerThreads == 4 && session.ChromaOptions is null
                        && session.ExecutionOptions is
                        {
                            SuppressFileOutputs: false, Debug: false, WriteDebugData: false,
                            VerboseVits: false, UseProfiler: false
                        }
                        && session.LaserDiscAudioOptions is
                        {
                            DecodeAnalogAudio: true, WritePreEfm: false, WriteRfTbc: false, Ac3: false
                        };
                    bool overlapCudaRfHighPass = requireCudaVideo && standardCudaLdOutput
                        && session.LaserDiscAudioOptions is { DecodeDigitalAudio: false };
                    using ILaserDiscCudaVideo? cudaVideo = cudaEfm is not null || requireCudaVideo
                        ? ownedReader!.AttachCudaVideoIfSupported(
                            () => _laserDiscCudaVideoFactory(session.Filters, output, cancellationToken),
                            overlapCudaRfHighPass ? cancellationToken : null)
                        : null;
                    if (requireCudaVideo && cudaVideo is null)
                    {
                        throw new NotSupportedException(
                            "'cuda-fast' LaserDisc with --noEFM requires compatible NTSC video filters and AVX2/FMA.");
                    }
                    if (cudaVideo is not null)
                    {
                        output.WriteLine("CUDA-fast LD video: post-FM filtering in eligible RF batches.");
                    }

                    bool completeCudaMappedReadAhead = false;
                    if (standardCudaLdOutput && ReferenceEquals(_engineFactory, DefaultEngineFactory)
                        && OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                        && !command.Get<bool>("lowband")
                        && session.ExecutionOptions.UpstreamBehaviorProfile == UpstreamBehaviorProfile.V040
                        && session.TbcFieldDecoder.CanOverlapOwnedLaserDiscRenderDropout
                        && ownedReader is not null
                        && (ownedReader.CudaRfHighPassOverlapToken.HasValue
                            || (session.LaserDiscAudioOptions is { DecodeDigitalAudio: true }
                                && ownedReader.CanPrepareCudaEfmIppRfFft)))
                    {
                        StaleRawFlacFallbackSampleLoader? cudaMappedFallback = null;
                        if (ownedReader.CanPrepareCudaEfmIppRfFft && HasStandardLaserDiscReadAheadOptions(command))
                        {
                            cudaMappedFallback = StaleRawFlacFallbackSampleLoader.GetMappedFallback(session.Loader);
                        }

                        ownedReader.PrepareCudaIppRfFftOnFirstRead(cancellationToken, cudaMappedFallback);
                        completeCudaMappedReadAhead = cudaMappedFallback is not null;
                    }

                    session.RuntimeReporter = runtimeReporter;
                    DecodeSessionLogWriter.Write(
                        session,
                        VhsInitializationDiagnostics.Build(command, session));
                    if (command.Spec == CliSpecs.Vhs
                        && command.Get<bool>(CliSpecs.DecodeAt20MspsDestination))
                    {
                        DecodeSessionLogWriter.Append(
                            session,
                            "INFO",
                            session.SourceSamplesPerDecodeSample == 2
                                ? "VHS decode rate: anti-aliased 40-to-20 MSPS."
                                : "VHS decode rate: native 20 MSPS input.");
                    }
                    if (dspBackend == DspBackend.ApproxFast)
                    {
                        DecodeSessionLogWriter.Append(
                            session,
                            session.ExecutionOptions.ApproxProviderFellBackFromIpp
                                ? "WARNING"
                                : "INFO",
                            FormatApproxDiagnostic(session.ExecutionOptions));
                    }

                    if (DspBackendKernelPolicy.UsesIpp(
                        dspBackend,
                        session.ExecutionOptions.ApproxProvider))
                    {
                        DecodeSessionLogWriter.Append(
                            session,
                            "INFO",
                            FormatIppDiagnostic(IppRuntime.RequireAvailable()));
                    }

                    bool overlapLaserDiscTail = ownedReader is not null
                        && (dspBackend == DspBackend.Exact
                            || (dspBackend == DspBackend.IppFast && ownedReader.CompactCpuPostFmVideo
                                && session.Filters.LdEfm is { Length: 32_768 }
                                && session.Filters.LdAnalogAudio is not null))
                        && command.Spec == CliSpecs.LaserDisc
                        && session.System == "NTSC" && session.InputFile != "-" && session.TestLdfOutputPath is null
                        && session.DecodeSampleRateHz == 40_000_000.0 && session.BlockLength == 32_768
                        && session.StreamDecoder.BlockStride == 31_712 && session.StreamDecoder.WorkerThreads == 4
                        && session.ChromaOptions is null
                        && session.ExecutionOptions is
                        {
                            SuppressFileOutputs: false, Debug: false, WriteDebugData: false,
                            VerboseVits: false, UseProfiler: false
                        }
                        && session.LaserDiscAudioOptions is
                        {
                            DecodeDigitalAudio: true, DecodeAnalogAudio: true,
                            WritePreEfm: false, WriteRfTbc: false, Ac3: false
                        };
                    if (overlapLaserDiscTail && dspBackend == DspBackend.IppFast
                        && session.RunBounds.RequestedFieldCount > 100
                        && session.DropoutOptions is { Enabled: true, Mode: TbcDropoutDetectionMode.LaserDiscDemod })
                    {
                        ownedReader!.EnableCompactRfHighPass();
                        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                            && session.ExecutionOptions.UpstreamBehaviorProfile == UpstreamBehaviorProfile.V040
                            && session.StreamDecoder.PrefetchBlocks == 0
                            && HasStandardLaserDiscReadAheadOptions(command))
                        {
                            StaleRawFlacFallbackSampleLoader.GetMappedFallback(session.Loader)
                                ?.EnableMappedReadAhead(cancellationToken);
                        }
                    }

                    if (overlapLaserDiscTail && dspBackend == DspBackend.Exact
                        && ownedReader is { ReuseExactLaserDiscS0: true }
                        && session.Pipeline.CanProjectOwnedExactLdRawDemod
                        && session.RunBounds.RequestedFieldCount > 100
                        && session.DropoutOptions is { Enabled: true, Mode: TbcDropoutDetectionMode.LaserDiscDemod })
                    {
                        ownedReader.EnableExactRawDemodMask();
                    }

                    bool overlapExactBufferedCpuTail = false;
                    if (overlapLaserDiscTail
                        && dspBackend == DspBackend.Exact && ownedReader is { ReuseExactLaserDiscS0: true }
                        && OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                        && session.RunBounds.RequestedFieldCount > 100
                        && session.ExecutionOptions.UpstreamBehaviorProfile == UpstreamBehaviorProfile.V040
                        && session.StreamDecoder.PrefetchBlocks == 0
                        && session.DropoutOptions is { Enabled: true, Mode: TbcDropoutDetectionMode.LaserDiscDemod }
                        && HasStandardLaserDiscReadAheadOptions(command)
                        && StaleRawFlacFallbackSampleLoader.GetMappedFallback(session.Loader) is { } exactMappedFallback)
                    {
                        exactMappedFallback.EnableMappedReadAhead(cancellationToken);
                        overlapExactBufferedCpuTail = true;
                        if (ownedReader.ProjectExactRawDemod)
                        {
                            session.Pipeline.EnableOwnedExactPostDemodPairedInverse();
                        }
                    }

                    TbcFieldSequenceDecodeEngine engine = ownedReader is null
                        ? _engineFactory(cancellationToken)
                        : TbcFieldSequenceDecodeEngine.CreateForOwnedDefaultImmediateLaserDisc(
                            ownedReader, cancellationToken, deferCompletionReport: cudaFastLaserDiscVideoOnly,
                            overlapLaserDiscTail: overlapLaserDiscTail,
                            completeCudaMappedReadAhead: completeCudaMappedReadAhead,
                            overlapExactBufferedCpuTail: overlapExactBufferedCpuTail);
                    result = engine.TryDecodeAndWrite(session);
                    if (cudaFastLaserDiscVideoOnly && result.Success)
                    {
                        long processedBlocks = cudaVideo?.ProcessedBlocks ?? 0;
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CUDA LD video blocks: {processedBlocks}"));
                        if (requireCudaVideo && processedBlocks == 0)
                        {
                            result = result with
                            {
                                Success = false,
                                Message = "CUDA-fast LaserDisc with --noEFM found no eligible RF batch for GPU video."
                            };
                        }
                    }
                    else if (cudaEfm is not null && result.Success)
                    {
                        long processedBlocks = cudaEfm.ProcessedBlocks;
                        if (result.WrittenFieldCount > 0 && processedBlocks == 0)
                        {
                            result = result with
                            {
                                Success = false,
                                Message = "CUDA-fast LaserDisc wrote fields without processing any EFM blocks on the GPU."
                            };
                        }
                        else
                        {
                            output.WriteLine(string.Create(
                                CultureInfo.InvariantCulture,
                                $"CUDA LD EFM blocks: {processedBlocks}"));
                            output.WriteLine(string.Create(
                                CultureInfo.InvariantCulture,
                                $"CUDA LD video blocks: {cudaVideo?.ProcessedBlocks ?? 0}"));
                        }
                    }
                }

                if (result.Success && command.Spec == CliSpecs.Cvbs)
                {
                    runtimeReporter.WriteCvbsCompletion(
                        session!.TbcRenderer.CvbsAgcStatistics);
                }
                else if (result.Success)
                {
                    runtimeReporter.WriteCompletionMessage(result.WrittenFieldCount);
                    if (result.TestLdf is { } testLdf)
                    {
                        runtimeReporter.CompleteTestLdfReport(testLdf);
                    }
                }
                else
                {
                    error.WriteLine(result.Message);
                }

                return result.Success ? 0 : 1;
            }
            catch (DecodeFieldReadException ex)
            {
                WriteRuntimeErrorReport(command, ex, error);
                return 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WriteTerminationMessage(command.Spec, output, error);
                return 1;
            }
            finally
            {
                session?.Dispose();
                runtimeReporter.WriteStatistics();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteTerminationMessage(command.Spec, output, error);
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException
            or FormatException
            or OverflowException
            or NotSupportedException
            or IppBackendUnavailableException
            or FormatParameterException)
        {
            if (ex is VhsFieldClassSelectionException fieldClassException)
            {
                string logPath = command.OutputBase + ".log";
                File.WriteAllText(logPath, string.Empty);
                foreach (DecodeInitializationDiagnostic diagnostic in fieldClassException.Diagnostics)
                {
                    DecodeSessionLogWriter.Append(logPath, diagnostic.Level, diagnostic.Message);
                    if (!string.Equals(diagnostic.Level, "DEBUG", StringComparison.OrdinalIgnoreCase))
                    {
                        error.WriteLine(diagnostic.Message);
                    }
                }
            }
            else if (ex is DecodeThreadInitializationException)
            {
                File.WriteAllText(command.OutputBase + ".log", string.Empty);
            }

            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void ValidateLaserDiscCudaOptions(ParsedCommand command)
    {
        if (command.Get<bool>("pal") || command.Get<bool>("ntscj"))
        {
            throw new NotSupportedException("'cuda-fast' LaserDisc currently supports NTSC only.");
        }

        if (command.Values["inputfreq"] is double inputFrequency && inputFrequency != FrequencyParser.DddMHz)
        {
            throw new NotSupportedException("'cuda-fast' LaserDisc requires 40 MSPS RF input.");
        }
    }

    // Build the normal LD CPU complement. The private reader must attach its
    // CUDA accelerator before the runner opens any output.
    private static ParsedCommand CreateLaserDiscCudaCpuCommand(ParsedCommand command)
    {
        var values = new Dictionary<string, object?>(command.Values, StringComparer.Ordinal)
        {
            ["dsp_backend"] = DspBackendParser.ExactValue
        };
        return new ParsedCommand(command.Spec, values, [.. command.Positionals], command.ProgramName, command.OptionSources);
    }

    internal static bool HasStandardLaserDiscReadAheadOptions(ParsedCommand command)
        => command.Spec == CliSpecs.LaserDisc
            && command.Spec.Options.All(option =>
                option.Destination is "ntsc" or "start" or "start_fileloc" or "length"
                    or "threads" or "dsp_backend" or "ignoreleadout"
                || (command.GetSource(option.Destination) == ParsedOptionSource.Default
                    && command.Values.TryGetValue(option.Destination, out object? value)
                    && Equals(value, option.DefaultValue)));

    private static RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader RequireLaserDiscCudaReader(
        DecodeSession session,
        RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader? ownedReader)
    {
        if (session.System != "NTSC"
            || session.DecodeSampleRateHz != 40_000_000.0
            || session.StreamDecoder.BlockLength != 32_768
            || session.StreamDecoder.BlockStride != 31_712
            || session.StreamDecoder.WorkerThreads < 2
            || ownedReader is null)
        {
            throw new NotSupportedException(
                "'cuda-fast' LaserDisc requires the NTSC 40 MSPS immediate field path with at least two RF worker threads.");
        }

        return ownedReader;
    }

    internal static string FormatIppDiagnostic(IppRuntimeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Intel IPP DSP backend: {info.IppVersion} (target={info.IppTargetCpu}, enabled-features=0x{info.EnabledCpuFeatures:X16}, bridge-abi=0x{info.AbiVersion:X8})");
    }

    internal static string FormatApproxDiagnostic(
        DecodeExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        if (executionOptions.DspBackend != DspBackend.ApproxFast
            || executionOptions.ApproxProvider is not { } provider
            || executionOptions.ApproxResampler is not { } resampler
            || executionOptions.ApproxPrecision is not { } precision)
        {
            throw new ArgumentException(
                "Approx diagnostics require a resolved approx-fast execution selection.",
                nameof(executionOptions));
        }

        string selection = executionOptions.ApproxProviderIsExplicit
            ? "explicit"
            : "automatic";
        string precisionDiagnostic = executionOptions.ApproxPrecisionIsExplicit
            || precision == ApproxPrecision.Aggressive
                ? $", precision={ApproxPrecisionParser.ToCommandLineValue(precision)}"
                : string.Empty;
        string message =
            $"Approx-fast DSP backend: contract={ApproximationContract.ForSelection(resampler, precision)}, provider={ApproxProviderParser.ToCommandLineValue(provider)}, resampler={ApproxResamplerParser.ToCommandLineValue(resampler)}{precisionDiagnostic}, selection={selection}, architecture={executionOptions.ApproxProviderProcessArchitecture}.";
        if (executionOptions.ApproxProviderFellBackFromIpp)
        {
            message += " Intel IPP was unavailable before decode initialization"
                + (string.IsNullOrWhiteSpace(executionOptions.ApproxProviderDiagnostic)
                    ? string.Empty
                    : $" ({executionOptions.ApproxProviderDiagnostic})")
                + "; fell back to the managed provider.";
        }
        else if (!executionOptions.ApproxProviderIsExplicit
            && provider == ApproxProvider.Managed
            && !string.IsNullOrWhiteSpace(executionOptions.ApproxProviderDiagnostic))
        {
            message += $" {executionOptions.ApproxProviderDiagnostic}";
        }

        return message;
    }

    private static void ValidateRequiredPositionals(ParsedCommand command)
    {
        if (command.Positionals.Count < 2)
        {
            throw new ArgumentException("the following arguments are required: infile, outfile");
        }
    }

    private static int WriteInputOutputArgumentFailure(ParsedCommand command, TextWriter output)
    {
        bool canUseInput = UpstreamIoArgumentValidator.ValidateInput(command.InputFile, output);
        if (canUseInput)
        {
            _ = UpstreamIoArgumentValidator.ValidateOutput(command.OutputBase, output);
        }

        output.Write(CommandHelpFormatter.Format(command.Spec, command.ProgramName));
        output.WriteLine("Input/output file error");
        output.WriteLine(UpstreamIoArgumentValidator.ValidateInput(command.InputFile, output)
            ? "Input file: OK"
            : $"ERROR: input file '{command.InputFile}' not found");
        output.WriteLine(UpstreamIoArgumentValidator.ValidateOutput(command.OutputBase, output)
            ? "Output file: OK"
            : $"ERROR: output file '{command.OutputBase}' is not writable");
        return 1;
    }

    private static void WriteEarlyCompatibilityWarnings(ParsedCommand command, TextWriter error)
    {
        if (command.Spec == CliSpecs.LaserDisc
            && command.Get<bool>("pal")
            && command.Get<bool>("ntsc_audio_rate"))
        {
            error.WriteLine("WARNING: --ntsc_audio_rate ignored for PAL (audio is already frame-locked at 44100hz)");
        }
    }

    public static void WriteCvbsAgcStatistics(CvbsAgcStatistics? statistics, TextWriter error)
    {
        if (statistics is null)
        {
            return;
        }

        error.WriteLine("Automatic gain control statistics:");
        error.WriteLine(" Lowest detected gain:   " + FormatPythonFloat(statistics.LowestDetectedGain));
        error.WriteLine(" Highest detected gain:  " + FormatPythonFloat(statistics.HighestDetectedGain));
        error.WriteLine(" Lowest used gain:       " + FormatPythonFloat(statistics.LowestUsedGain));
        error.WriteLine(" Highest used gain:      " + FormatPythonFloat(statistics.HighestUsedGain));
    }

    public static void WriteCompletionMessage(int writtenFieldCount, TextWriter error)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(writtenFieldCount);
        error.WriteLine();
        error.WriteLine(writtenFieldCount > 0
            ? "Completed: saving JSON and exiting."
            : "Completed without handling any frames.");
    }

    public static void WriteTerminationMessage(
        DecodeCommandSpec spec,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        TextWriter target = spec.Name == "ld" ? error : output;
        target.WriteLine();
        target.WriteLine("Terminated, saving JSON and exiting");
        target.Flush();
    }

    public static void WriteRuntimeErrorReport(
        ParsedCommand command,
        DecodeFieldReadException exception,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(error);

        Exception cause = exception.InnerException ?? exception;
        error.WriteLine();
        error.WriteLine("ERROR - please paste the following into a bug report:");
        error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"current sample: {exception.CurrentSampleText}"));
        error.WriteLine($"arguments: {PythonNamespaceFormatter.Format(command)}");
        error.WriteLine($"Exception: {cause.Message}  Traceback:");
        WritePythonStyleTraceback(exception, cause, error);
        error.Flush();
    }

    public static void WriteTestLdfReport(LdTestLdfWriteResult result, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(result.OutputPath))
        {
            return;
        }

        WriteTestLdfReportStart(result.OutputPath, result.StartSample, result.EndSample, error);
        WriteTestLdfReportCompletion(result, error);
    }

    internal static void WriteTestLdfReportStart(
        string outputPath,
        long startSample,
        long endSample,
        TextWriter error)
    {
        error.WriteLine();
        error.WriteLine($"Writing input samples to {outputPath}...");
        error.WriteLine($"  Start sample: {startSample}");
        error.WriteLine($"  End sample: {endSample}");
    }

    internal static void WriteTestLdfReportCompletion(LdTestLdfWriteResult result, TextWriter error)
    {
        if (result.EndSample <= result.StartSample)
        {
            error.WriteLine("WARNING: No samples to write");
            return;
        }

        if (result.ShortReadSample.HasValue)
        {
            error.WriteLine($"WARNING: Short read at sample {result.ShortReadSample.Value}");
        }

        error.WriteLine($"  Samples written: {result.SamplesWritten}");
        error.WriteLine($"Successfully wrote {result.OutputPath}");
    }

    private static string FormatPythonFloat(double value)
    {
        string formatted = value.ToString("R", CultureInfo.InvariantCulture);
        return formatted.Contains('E', StringComparison.Ordinal)
            ? formatted.Replace('E', 'e')
            : formatted;
    }

    private static void WritePythonStyleTraceback(
        DecodeFieldReadException exception,
        Exception cause,
        TextWriter error)
    {
        StackFrame[] wrapperFrames = new StackTrace(exception, true).GetFrames() ?? [];
        StackFrame[] causeFrames = new StackTrace(cause, true).GetFrames() ?? [];
        IEnumerable<StackFrame> orderedFrames = wrapperFrames.Length == 0
            ? causeFrames.Reverse()
            : wrapperFrames.Reverse().SkipLast(1).Concat(causeFrames.Reverse());

        foreach (StackFrame frame in orderedFrames)
        {
            string methodName = frame.GetMethod()?.Name ?? "<unknown>";
            string fileName = frame.GetFileName()
                ?? frame.GetMethod()?.DeclaringType?.FullName
                ?? "<unknown>";
            error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  File \"{fileName}\", line {frame.GetFileLineNumber()}, in {methodName}"));
        }
    }

    private static void ValidateSystemSpecificOptions(ParsedCommand command)
    {
        if (command.Spec == CliSpecs.LaserDisc)
        {
            return;
        }

        string system = VideoSystemSelector.Select(command);
        if (command.Spec == CliSpecs.Vhs)
        {
            FormatCatalog.Default.GetTapeParameters(
                system,
                command.Get<string>("tape_format"),
                command.Get<string>("tape_speed"));
        }
        else if (command.Spec == CliSpecs.Cvbs)
        {
            FormatCatalog.Default.GetCvbsParameters(system);
            if (command.Get<bool>("chroma_trap"))
            {
                File.WriteAllText(command.OutputBase + ".log", string.Empty);
                throw new ArgumentException(
                    "ChromaSepClass.__init__() missing 1 required positional argument: 'logger'");
            }

            if (system is not ("PAL" or "NTSC"))
            {
                throw new ArgumentException($"('Unknown video system!', '{system}')");
            }
        }
    }

    private static string? GetSystemConflict(ParsedCommand command)
    {
        bool pal = command.Get<bool>("pal");
        bool ntsc = command.Get<bool>("ntsc");
        if (command.Spec == CliSpecs.LaserDisc)
        {
            bool ntscj = command.Get<bool>("ntscj");
            return pal && (ntsc || ntscj) ? "ERROR: Can only be PAL or NTSC" : null;
        }

        bool palm = command.Get<bool>("palm");
        if (pal && ntsc)
        {
            return "ERROR: Can only be PAL or NTSC";
        }

        if (palm && pal)
        {
            return "ERROR: Can only be PAL-M or PAL";
        }

        return palm && ntsc ? "ERROR: Can only be PAL-M or NTSC" : null;
    }
}
