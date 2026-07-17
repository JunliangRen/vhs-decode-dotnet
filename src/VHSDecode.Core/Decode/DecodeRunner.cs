using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Formats;
using VHSDecode.Core.HiFi;
using VHSDecode.Core.Tbc;
using System.Diagnostics;
using System.Globalization;

namespace VHSDecode.Core.Decode;

public sealed class DecodeRunner
{
    private readonly Func<CancellationToken, TbcFieldSequenceDecodeEngine> _engineFactory;
    private readonly IHiFiCommandRunner _hiFiRunner;

    public DecodeRunner()
        : this(
            cancellationToken => new TbcFieldSequenceDecodeEngine(
                cancellationToken: cancellationToken),
            new HiFiDecodeRunner())
    {
    }

    internal DecodeRunner(Func<CancellationToken, TbcFieldSequenceDecodeEngine> engineFactory)
        : this(engineFactory, new HiFiDecodeRunner())
    {
    }

    internal DecodeRunner(
        Func<CancellationToken, TbcFieldSequenceDecodeEngine> engineFactory,
        IHiFiCommandRunner hiFiRunner)
    {
        ArgumentNullException.ThrowIfNull(engineFactory);
        ArgumentNullException.ThrowIfNull(hiFiRunner);
        _engineFactory = engineFactory;
        _hiFiRunner = hiFiRunner;
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
                if (command.Get<bool>("pal") && command.Get<bool>("AC3"))
                {
                    output.WriteLine("ERROR: AC3 audio decoding is only supported for NTSC");
                    return 1;
                }
            }

            var runtimeReporter = new DecodeRuntimeReporter(output, error);
            DecodeSession session = DecodeSessionFactory.Create(command);
            session.RuntimeReporter = runtimeReporter;
            try
            {
                DecodeSessionLogWriter.Write(session);
                WriteSessionCompatibilityWarnings(command, session, error);
                TbcFieldSequenceDecodeResult result = _engineFactory(cancellationToken)
                    .TryDecodeAndWrite(session);
                if (result.Success && command.Spec == CliSpecs.Cvbs)
                {
                    runtimeReporter.WriteCvbsCompletion(session.TbcRenderer.CvbsAgcStatistics);
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
                session.Dispose();
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
            or FormatParameterException)
        {
            if (ex is DecodeThreadInitializationException)
            {
                File.WriteAllText(command.OutputBase + ".log", string.Empty);
            }

            error.WriteLine(ex.Message);
            return 1;
        }
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

    private static void WriteSessionCompatibilityWarnings(
        ParsedCommand command,
        DecodeSession session,
        TextWriter error)
    {
        if (session.ExecutionOptions.CxAdcCompatibilityMode)
        {
            error.WriteLine("--cxadc is deprecated! use -f 8fsc instead!");
        }

        if (command.Spec != CliSpecs.Vhs)
        {
            return;
        }

        int configuredDivisor = command.Get<int>("level_detect_divisor");
        double sampleRateMHz = session.DecodeSampleRateHz / 1_000_000.0;
        if (configuredDivisor < 1 || configuredDivisor > 10)
        {
            DecodeSessionLogWriter.Append(
                session,
                "WARNING",
                $"Invalid level detect divisor value {configuredDivisor}, using default.");
        }
        else if (sampleRateMHz / configuredDivisor < 4.0)
        {
            DecodeSessionLogWriter.Append(
                session,
                "WARNING",
                "Level detect divisor too high "
                + $"({configuredDivisor}) for input frequency "
                + $"({PythonNamespaceFormatter.FormatValue(sampleRateMHz)}) mhz. "
                + $"Limiting to {session.SyncDetectionOptions.LevelDetectDivisor}");
        }

        if (Math.Truncate(session.FilterOptions.FmAudioNotchQ) > 0.0
            && (!session.Parameters.RfParams.TryGetProperty("fm_audio_channel_0_freq", out _)
                || !session.Parameters.RfParams.TryGetProperty("fm_audio_channel_1_freq", out _)))
        {
            DecodeSessionLogWriter.Append(
                session,
                "WARNING",
                "Audio frequencies are not specified for this format, audio fm notch filters not enabled!");
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
