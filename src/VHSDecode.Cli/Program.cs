using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;

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
            command = parser.Parse(spec, remaining, programName, output);
        }
        catch (CommandLineParseException ex)
        {
            error.Write(CommandHelpFormatter.FormatUsage(spec, programName));
            error.WriteLine($"{programName}: error: {ex.Message}");
            return 2;
        }

        try
        {
            return new DecodeRunner().Run(command, output, error, cancellationToken);
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
