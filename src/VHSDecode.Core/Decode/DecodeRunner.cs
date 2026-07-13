using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Tbc;
using System.Globalization;

namespace VHSDecode.Core.Decode;

public sealed class DecodeRunner
{
    public int Run(ParsedCommand command, TextWriter output, TextWriter error)
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
                WriteSessionCompatibilityWarnings(session, error);
                TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine().TryDecodeAndWrite(session);
                if (result.Success && command.Spec == CliSpecs.Cvbs)
                {
                    WriteCvbsAgcStatistics(session.TbcRenderer.CvbsAgcStatistics, error);
                    output.WriteLine("saving JSON and exiting");
                }
                else if (result.Success)
                {
                    WriteCompletionMessage(result.WrittenFieldCount, error);
                    if (result.TestLdf is { } testLdf)
                    {
                        WriteTestLdfReport(testLdf, error);
                    }
                }
                else
                {
                    error.WriteLine(result.Message);
                }

                return result.Success ? 0 : 1;
            }
            finally
            {
                session.Dispose();
                runtimeReporter.WriteStatistics();
            }
        }
        catch (Exception ex) when (ex is ArgumentException
            or FormatException
            or OverflowException
            or NotSupportedException
            or FormatParameterException)
        {
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

    private static void WriteSessionCompatibilityWarnings(DecodeSession session, TextWriter error)
    {
        if (session.ExecutionOptions.CxAdcCompatibilityMode)
        {
            error.WriteLine("--cxadc is deprecated! use -f 8fsc instead!");
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

    public static void WriteTestLdfReport(LdTestLdfWriteResult result, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(result.OutputPath))
        {
            return;
        }

        error.WriteLine();
        error.WriteLine($"Writing input samples to {result.OutputPath}...");
        error.WriteLine($"  Start sample: {result.StartSample}");
        error.WriteLine($"  End sample: {result.EndSample}");
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
