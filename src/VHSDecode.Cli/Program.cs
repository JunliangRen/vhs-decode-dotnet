using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Preview;

namespace VHSDecode.Cli
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            string? executablePath = Environment.ProcessPath;
            string[] invocation = DecodeDispatcher.NormalizeInvocation(args, executablePath);
            using var cancellationSource = new CancellationTokenSource();
            void CancelHandler(object? _, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                cancellationSource.Cancel();
            }

            Console.CancelKeyPress += CancelHandler;
            try
            {
                return Run(
                    invocation,
                    DecodeDispatcher.InvocationProgramName(executablePath),
                    Console.Out,
                    Console.Error,
                    cancellationSource.Token);
            }
            finally
            {
                Console.CancelKeyPress -= CancelHandler;
            }
        }

        private static int Run(
            string[] args,
            string programName,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            if (args.Length == 0)
            {
                PrintTopLevelOptions(output);
                return 0;
            }

            if (!DecodeDispatcher.TryDispatch(args, out DecodeCommandSpec? spec, out string[] remaining) || spec is null)
            {
                PrintTopLevelOptions(output);
                output.WriteLine($"Instead got: {args[0].ToLowerInvariant()}");
                return 0;
            }

            ParsedCommand command;
            try
            {
                var parser = new CommandLineParser();
                if (Environment.GetEnvironmentVariable("VHSDECODE_PROCESS_DIAGNOSTICS") == "1")
                {
                    // Legacy VHS I/O validation requires the profile's output directory to exist.
                    ParsedCommand diagnosticCommand = parser.Parse(spec, remaining, programName);
                    if (!diagnosticCommand.Get<bool>("help") && diagnosticCommand.OutputBase.Length != 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(
                            Path.GetFullPath(diagnosticCommand.OutputBase))!);
                    }
                }
                command = parser.Parse(spec, remaining, programName, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Unable to prepare process diagnostics: {ex.Message}");
                return 2;
            }
            catch (CommandLineParseException ex)
            {
                error.Write(CommandHelpFormatter.FormatUsage(spec, programName));
                error.WriteLine($"{programName}: error: {ex.Message}");
                return 2;
            }

            try
            {
                DecodeProcessObservation? observation;
                try
                {
                    observation = DecodeProcessObservation.StartIfRequested(command);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"Unable to start process diagnostics: {ex.Message}");
                    return 2;
                }

                using (observation)
                {
                    int exitCode = PreviewCommandRunner.IsRequested(command)
                        ? new PreviewCommandRunner().RunAsync(
                            command, output, error, cancellationToken).GetAwaiter().GetResult()
                        : new DecodeRunner().Run(command, output, error, cancellationToken);
                    observation?.SetExitCode(exitCode);
                    return exitCode;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                error.WriteLine(ex.Message);
                return 2;
            }
        }

        private static void PrintTopLevelOptions(TextWriter output)
        {
            output.WriteLine("Options are vhs, cvbs, ld, hifi, filter-tune, decode-launcher");
        }
    }
}
