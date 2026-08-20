using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Preview;

internal static class PreviewDecodeCommandFactory
{
    internal const string DecodeAt20MspsOption = CliSpecs.DecodeAt20MspsDestination;

    internal static ParsedCommand CreateFastTemplate(ParsedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Spec.Name is not ("vhs" or "ld")
            || !BoolValue(command, "preview_server"))
        {
            throw new ArgumentException(
                "The preview server is supported only by the VHS and LaserDisc commands.",
                nameof(command));
        }

        if (command.Positionals.Count != 1 || string.IsNullOrWhiteSpace(command.InputFile))
        {
            throw new ArgumentException("Preview mode requires exactly one RF input file.");
        }

        bool cudaFast = DspBackendParser.Parse(command.Get<string>("dsp_backend"))
            == DspBackend.CudaFast;
        if (cudaFast && command.Spec.Name != "vhs")
        {
            throw new NotSupportedException(
                "CUDA-fast preview currently supports the VHS command only; no CPU preview fallback was performed.");
        }

        var values = new Dictionary<string, object?>(command.Values, StringComparer.Ordinal);
        var sources = new Dictionary<string, ParsedOptionSource>(command.OptionSources, StringComparer.Ordinal);
        Set(values, sources, "write_db", false);
        Set(values, sources, "debug", false);
        if (command.GetSource("threads") == ParsedOptionSource.Default)
        {
            Set(values, sources, "threads", Math.Max(1, Environment.ProcessorCount));
        }

        if (command.GetSource("dsp_backend") == ParsedOptionSource.Default
            && IppRuntime.TryProbe(out _))
        {
            Set(values, sources, "dsp_backend", "ipp-fast");
        }

        if (command.Spec.Name == "vhs")
        {
            Set(values, sources, "skip_chroma", false);
            Set(values, sources, "nodod", false);
            Set(values, sources, "disable_diff_demod", true);
            Set(values, sources, "skip_hsync_refine", true);
            Set(values, sources, "disable_comb", true);
            Set(values, sources, "detect_chroma_track_phase", false);
            Set(values, sources, "cti_mix", 0.0);
            if (command.GetSource("compat_version") == ParsedOptionSource.Default)
            {
                Set(values, sources, "compat_version", "current");
            }

            double inputSampleRateMHz = command.Get<bool>("cxadc")
                ? FrequencyParser.CxAdcMHz
                : command.Values.TryGetValue("inputfreq", out object? inputFrequency)
                    && inputFrequency is double parsedInputFrequency
                    && parsedInputFrequency > 0.0
                        ? parsedInputFrequency
                        : FrequencyParser.DddMHz;
            if (string.Equals(
                    command.Get<string>("tape_format"),
                    "VHS",
                    StringComparison.Ordinal)
                && Math.Abs(inputSampleRateMHz - FrequencyParser.DddMHz) <= 1e-9)
            {
                Set(values, sources, DecodeAt20MspsOption, true);
            }
            else if (string.Equals(
                    command.Get<string>("tape_format"),
                    "VHS",
                    StringComparison.Ordinal)
                && Math.Abs(inputSampleRateMHz - (FrequencyParser.DddMHz / 2.0)) <= 1e-9)
            {
                Set(values, sources, DecodeAt20MspsOption, true);
                Set(values, sources, "no_resample", true);
            }
        }
        else
        {
            BigInteger seek = command.Get<BigInteger>("seek");
            if (seek >= BigInteger.Zero)
            {
                throw new ArgumentException(
                    "--seek addresses LaserDisc program frame numbers and cannot define a seekable file timeline; use --start for preview mode.");
            }

            Set(values, sources, "noefm", true);
            Set(values, sources, "prefm", false);
            Set(values, sources, "daa", true);
            Set(values, sources, "AC3", false);
            Set(values, sources, "RF_TBC", false);
            Set(values, sources, "nodod", false);
            Set(values, sources, "verboseVITS", false);
            Set(values, sources, "use_profiler", false);
            Set(values, sources, "write_test_ldf", null);
            Set(values, sources, "ignoreleadout", true);
        }

        return new ParsedCommand(
            command.Spec,
            values,
            [command.InputFile],
            command.ProgramName,
            sources);
    }

    internal static ParsedCommand ForWindow(
        ParsedCommand template,
        long startSample,
        int requestedFrames)
    {
        if (startSample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSample));
        }

        if (requestedFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedFrames));
        }

        var values = new Dictionary<string, object?>(template.Values, StringComparer.Ordinal)
        {
            ["start_fileloc"] = (double)startSample,
            ["length"] = new BigInteger(requestedFrames),
            ["start"] = template.Spec.Name == "ld" ? 0.0 : BigInteger.Zero
        };
        return new ParsedCommand(
            template.Spec,
            values,
            [template.InputFile],
            template.ProgramName,
            template.OptionSources);
    }

    private static void Set(
        IDictionary<string, object?> values,
        IDictionary<string, ParsedOptionSource> sources,
        string name,
        object? value)
    {
        if (values.ContainsKey(name))
        {
            values[name] = value;
            sources[name] = ParsedOptionSource.Default;
        }
    }

    private static bool BoolValue(ParsedCommand command, string name)
        => command.Values.TryGetValue(name, out object? value)
            && value is true;
}
