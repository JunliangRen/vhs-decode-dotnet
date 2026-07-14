using System.Diagnostics;
using System.Globalization;
using VHSDecode.Core.CommandLine;

namespace VHSDecode.Core.HiFi;

internal interface IHiFiCommandRunner
{
    int Run(
        ParsedCommand command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken);
}

internal sealed class HiFiDecodeRunner : IHiFiCommandRunner
{
    private readonly HiFiStreamingDecoder _streamingDecoder;
    private readonly Func<HiFiDecodeOptions, TextWriter, IHiFiSampleReader> _inputFactory;
    private readonly Func<HiFiDecodeOptions, HiFiOutputWriter> _outputFactory;

    public HiFiDecodeRunner()
        : this(
            new HiFiStreamingDecoder(),
            HiFiInputReader.Open,
            options => new HiFiOutputWriter(options))
    {
    }

    internal HiFiDecodeRunner(
        HiFiStreamingDecoder streamingDecoder,
        Func<HiFiDecodeOptions, TextWriter, IHiFiSampleReader> inputFactory,
        Func<HiFiDecodeOptions, HiFiOutputWriter> outputFactory)
    {
        _streamingDecoder = streamingDecoder ?? throw new ArgumentNullException(nameof(streamingDecoder));
        _inputFactory = inputFactory ?? throw new ArgumentNullException(nameof(inputFactory));
        _outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));
    }

    public int Run(
        ParsedCommand command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (command.Spec != CliSpecs.HiFi)
        {
            throw new ArgumentException("Parsed command is not a HiFi decode command.", nameof(command));
        }

        HiFiDecodeOptions options = HiFiDecodeOptions.FromCommand(command);
        if (!options.Gui && !options.Overwrite && File.Exists(options.OutputFile))
        {
            output.WriteLine(
                "Existing decode files found, remove them or run command with --overwrite");
            output.WriteLine("\t " + options.OutputFile);
            return 1;
        }

        output.WriteLine("Initializing ...");
        if (options.Gui)
        {
            output.WriteLine(
                "PyQt5/PyQt6 is not installed, can not use graphical UI, "
                + "falling back to command line interface..");
        }

        bool inputValid = UpstreamIoArgumentValidator.ValidateInput(options.InputFile, output);
        bool outputValid = inputValid
            && UpstreamIoArgumentValidator.ValidateOutput(options.OutputFile, output);
        if (!inputValid || !outputValid)
        {
            output.Write(CommandHelpFormatter.Format(command.Spec, command.ProgramName));
            bool inputStillValid = UpstreamIoArgumentValidator.ValidateInput(options.InputFile, output);
            output.WriteLine(inputStillValid
                ? $"ERROR: output file '{options.OutputFile}' cannot be created nor overwritten"
                : "ERROR: input file not found");
            return 1;
        }

        string system = options.Standard == "p" ? "PAL" : "NTSC";
        string format = options.TapeFormat == "vhs" ? "VHS" : "8mm";
        output.WriteLine($"{system} {format} format selected, Audio mode is {options.AudioMode}");
        if (options.BiasGuess)
        {
            using IHiFiSampleReader biasInput = _inputFactory(options, output);
            _ = HiFiBiasEstimator.Measure(
                options,
                biasInput,
                output,
                cancellationToken);
            // Release 4.0 workers build their AFE/FM path before receiving the measured standard.
        }

        if (options.Preview)
        {
            output.WriteLine("Import of sounddevice failed, preview is not available!");
        }

        if (options.GnuRadio)
        {
            output.WriteLine(
                $"Set gnuradio sample rate at {HiFiDecodePlan.FromOptions(options).IfRateHz} Hz, type float");
        }

        var stopwatch = Stopwatch.StartNew();
        output.WriteLine("Starting decode...");
        using IHiFiSampleReader input = _inputFactory(options, output);
        using HiFiOutputWriter audioOutput = _outputFactory(options);
        HiFiStreamingDecodeResult result = _streamingDecoder.Decode(
            options,
            input,
            audioOutput,
            output,
            cancellationToken,
            () => stopwatch.Elapsed);

        output.WriteLine();
        output.WriteLine("Decode finishing up. Emptying the queue");
        output.WriteLine();
        audioOutput.Complete(result.LeftPeak, result.RightPeak, output);
        stopwatch.Stop();
        long elapsedSeconds = checked((long)Math.Round(
            stopwatch.Elapsed.TotalSeconds,
            MidpointRounding.ToEven));
        output.WriteLine();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Decode finished, seconds elapsed: {elapsedSeconds}"));
        output.WriteLine("Decode finished successfully");
        return 0;
    }
}
