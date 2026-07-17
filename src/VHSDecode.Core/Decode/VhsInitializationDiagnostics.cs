using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Decode;

internal static class VhsInitializationDiagnostics
{
    public static IReadOnlyList<DecodeInitializationDiagnostic> Build(
        ParsedCommand command,
        DecodeSession session)
    {
        var diagnostics = new List<DecodeInitializationDiagnostic>();
        if (session.ExecutionOptions.CxAdcCompatibilityMode)
        {
            diagnostics.Add(new DecodeInitializationDiagnostic(
                "WARNING",
                "--cxadc is deprecated! use -f 8fsc instead!"));
        }

        if (command.Spec != CliSpecs.Vhs)
        {
            return diagnostics;
        }

        foreach (string warning in session.Parameters.Warnings)
        {
            diagnostics.Add(new DecodeInitializationDiagnostic("WARNING", warning));
        }

        if (Math.Truncate(session.FilterOptions.FmAudioNotchQ) > 0.0
            && (!session.Parameters.RfParams.TryGetProperty("fm_audio_channel_0_freq", out _)
                || !session.Parameters.RfParams.TryGetProperty("fm_audio_channel_1_freq", out _)))
        {
            diagnostics.Add(new DecodeInitializationDiagnostic(
                "WARNING",
                "Audio frequencies are not specified for this format, audio fm notch filters not enabled!"));
        }

        int configuredDivisor = command.Get<int>("level_detect_divisor");
        double sampleRateMHz = session.DecodeSampleRateHz / 1_000_000.0;
        if (configuredDivisor < 1 || configuredDivisor > 10)
        {
            diagnostics.Add(new DecodeInitializationDiagnostic(
                "WARNING",
                $"Invalid level detect divisor value {configuredDivisor}, using default."));
        }
        else if (sampleRateMHz / configuredDivisor < 4.0)
        {
            diagnostics.Add(new DecodeInitializationDiagnostic(
                "WARNING",
                "Level detect divisor too high "
                + $"({configuredDivisor}) for input frequency "
                + $"({PythonNamespaceFormatter.FormatValue(sampleRateMHz)}) mhz. "
                + $"Limiting to {session.SyncDetectionOptions.LevelDetectDivisor}"));
        }

        if (GetFieldClassFallbackMessage(session.System, session.Parameters.TapeFormat) is { } fallbackMessage)
        {
            diagnostics.Add(new DecodeInitializationDiagnostic("INFO", fallbackMessage));
        }

        return diagnostics;
    }

    public static bool IsUnsupportedFieldClassCombination(string system, string tapeFormat)
    {
        string normalizedSystem = FormatCatalog.NormalizeSystem(system);
        string normalizedFormat = tapeFormat.ToUpperInvariant();
        return normalizedSystem is "PAL_M" or "NLINHA" or "MESECAM"
            && normalizedFormat != "VHS";
    }

    public static string? GetFieldClassFallbackMessage(string system, string tapeFormat)
    {
        return (FormatCatalog.NormalizeSystem(system), tapeFormat.ToUpperInvariant()) switch
        {
            ("PAL", not ("UMATIC" or "UMATIC_HI" or "UMATIC_SP" or "EIAJ" or "VCR" or "VCR_LP"
                or "TYPEC" or "TYPEB" or "QUADRUPLEX" or "SVHS" or "SVHS_ET" or "BETAMAX"
                or "VIDEO8" or "HI8" or "VHS" or "VHSHQ" or "VIDEO2000")) =>
                "Tape format unimplemented for PAL, using VHS field class.",
            ("NTSC", not ("UMATIC" or "UMATIC_HI" or "UMATIC_SP" or "EIAJ" or "TYPEC" or "TYPEB"
                or "VHD" or "SVHS" or "SVHS_ET" or "BETAMAX" or "BETAMAX_HIFI" or "SUPERBETA"
                or "VIDEO8" or "HI8" or "VHS" or "VHSHQ")) =>
                "Tape format unimplemented for NTSC, using VHS field class.",
            _ => null
        };
    }
}

internal sealed class VhsFieldClassSelectionException : ArgumentException
{
    public VhsFieldClassSelectionException(
        string system,
        IReadOnlyList<DecodeInitializationDiagnostic> diagnostics)
        : base($"('Unknown video system and/or tape format combination!', '{FormatCatalog.NormalizeSystem(system)}')")
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<DecodeInitializationDiagnostic> Diagnostics { get; }
}
