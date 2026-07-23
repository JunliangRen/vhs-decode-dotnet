using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

public sealed record HiFiDecodeOptions
{
    public required double InputRateHz { get; init; }
    public required string? InputFormatOverride { get; init; }
    public required string Standard { get; init; }
    public required string TapeFormat { get; init; }
    public required bool Preview { get; init; }
    public required string DemodType { get; init; }
    public required double AfeLeftCarrierDeviationHz { get; init; }
    public required double AfeRightCarrierDeviationHz { get; init; }
    public required double AfeLeftCarrierHz { get; init; }
    public required double AfeRightCarrierHz { get; init; }
    public required string ResamplerQuality { get; init; }
    public required double SpectralNoiseReductionAmount { get; init; }
    public required bool HeadSwitchingInterpolation { get; init; }
    public required string DropoutCompensation { get; init; }
    public required bool EnableExpander { get; init; }
    public required bool EnableDeemphasis { get; init; }
    public required bool AutoFineTune { get; init; }
    public required bool BiasGuess { get; init; }
    public required bool Normalize { get; init; }
    public required double ExpanderGain { get; init; }
    public required double ExpanderRatio { get; init; }
    public required string ExpanderEnvelopeDetection { get; init; }
    public required double ExpanderAttackTau { get; init; }
    public required double ExpanderHoldTau { get; init; }
    public required double ExpanderReleaseTau { get; init; }
    public required double ExpanderWeightingLowTau { get; init; }
    public required double ExpanderWeightingHighTau { get; init; }
    public required double ExpanderWeightingLowPassHz { get; init; }
    public required double ExpanderWeightingLowPassTransitionHz { get; init; }
    public required double NoiseReductionDeemphasisLowTau { get; init; }
    public required double NoiseReductionDeemphasisHighTau { get; init; }
    public required double DeemphasisLowTau { get; init; }
    public required double DeemphasisHighTau { get; init; }
    public required bool GnuRadio { get; init; }
    public required BigInteger RequestedAudioRateInteger { get; init; }
    public required BigInteger AudioRateInteger { get; init; }
    public int AudioRateHz => checked((int)AudioRateInteger);
    public required double Gain { get; init; }
    public required string InputFile { get; init; }
    public required string OutputFile { get; init; }
    public required string AudioMode { get; init; }
    public required BigInteger ThreadsInteger { get; init; }
    public int Threads => checked((int)ThreadsInteger);
    public required bool Overwrite { get; init; }
    public required bool Gui { get; init; }
    public DspBackend DspBackend { get; init; } = DspBackend.Exact;

    public static HiFiDecodeOptions FromCommand(ParsedCommand command)
    {
        if (command.Spec != CliSpecs.HiFi)
        {
            throw new ArgumentException("Parsed command is not a HiFi decode command.", nameof(command));
        }

        bool is8Mm = command.Get<bool>("format_8mm");
        bool preview = command.Get<bool>("preview");
        BigInteger requestedAudioRate = command.Get<BigInteger>("rate");
        BigInteger audioRate = preview
            ? new BigInteger(HiFiConstants.PreviewAudioRate)
            : requestedAudioRate;
        string requestedQuality = command.Get<string>("resampler_quality");
        string resamplerQuality = requestedQuality is "low" or "medium" or "high"
            ? requestedQuality
            : HiFiConstants.DefaultResamplerQuality;

        double defaultExpanderGain = is8Mm
            ? HiFiConstants.Default8MmExpanderGain
            : HiFiConstants.DefaultVhsExpanderGain;
        double defaultExpanderRatio = is8Mm
            ? HiFiConstants.Default8MmExpanderRatio
            : HiFiConstants.DefaultVhsExpanderRatio;
        double defaultExpanderAttackTau = is8Mm
            ? HiFiConstants.Default8MmExpanderAttackTau
            : HiFiConstants.DefaultVhsExpanderAttackTau;
        double defaultExpanderHoldTau = is8Mm
            ? HiFiConstants.Default8MmExpanderHoldTau
            : HiFiConstants.DefaultVhsExpanderHoldTau;
        double defaultExpanderReleaseTau = is8Mm
            ? HiFiConstants.Default8MmExpanderReleaseTau
            : HiFiConstants.DefaultVhsExpanderReleaseTau;
        double defaultDeemphasisLowTau = is8Mm
            ? HiFiConstants.Default8MmDeemphasisLowTau
            : HiFiConstants.DefaultVhsDeemphasisLowTau;
        double defaultDeemphasisHighTau = is8Mm
            ? HiFiConstants.Default8MmDeemphasisHighTau
            : HiFiConstants.DefaultVhsDeemphasisHighTau;
        double defaultNoiseReductionLowTau = is8Mm
            ? HiFiConstants.Default8MmNoiseReductionDeemphasisLowTau
            : HiFiConstants.DefaultVhsNoiseReductionDeemphasisLowTau;
        double defaultNoiseReductionHighTau = is8Mm
            ? HiFiConstants.Default8MmNoiseReductionDeemphasisHighTau
            : HiFiConstants.DefaultVhsNoiseReductionDeemphasisHighTau;
        double defaultWeightingLowTau = is8Mm
            ? HiFiConstants.Default8MmExpanderWeightingLowTau
            : HiFiConstants.DefaultVhsExpanderWeightingLowTau;
        double defaultWeightingHighTau = is8Mm
            ? HiFiConstants.Default8MmExpanderWeightingHighTau
            : HiFiConstants.DefaultVhsExpanderWeightingHighTau;
        double defaultWeightingLowPass = is8Mm
            ? HiFiConstants.Default8MmExpanderWeightingLowPass
            : HiFiConstants.DefaultVhsExpanderWeightingLowPass;
        double defaultWeightingTransition = is8Mm
            ? HiFiConstants.Default8MmExpanderWeightingLowPassTransition
            : HiFiConstants.DefaultVhsExpanderWeightingLowPassTransition;
        string defaultMode = is8Mm
            ? HiFiConstants.Default8MmAudioMode
            : HiFiConstants.DefaultVhsAudioMode;

        return new HiFiDecodeOptions
        {
            InputRateHz = command.Get<double>("inputfreq") * 1_000_000.0,
            InputFormatOverride = command.Get<string?>("raw_format"),
            Standard = command.Get<bool>("pal") ? "p" : "n",
            TapeFormat = is8Mm ? "8mm" : "vhs",
            Preview = preview,
            DemodType = command.Get<string>("demod_type"),
            AfeLeftCarrierDeviationHz = command.Get<double>("afe_left_carrier_deviation") * 1_000_000.0,
            AfeRightCarrierDeviationHz = command.Get<double>("afe_right_carrier_deviation") * 1_000_000.0,
            AfeLeftCarrierHz = command.Get<double>("afe_left_carrier") * 1_000_000.0,
            AfeRightCarrierHz = command.Get<double>("afe_right_carrier") * 1_000_000.0,
            ResamplerQuality = preview ? "low" : resamplerQuality,
            SpectralNoiseReductionAmount = preview
                ? 0.0
                : command.Get<double>("spectral_nr_amount"),
            HeadSwitchingInterpolation = command.Get<string>("head_switching_interpolation") == "on",
            DropoutCompensation = command.Get<string>("doc"),
            EnableExpander = command.Get<string>("enable_expander") == "on",
            EnableDeemphasis = command.Get<string>("enable_deemphasis") == "on",
            AutoFineTune = !preview && command.Get<string>("auto_fine_tune") == "on",
            BiasGuess = command.Get<bool>("bias_guess"),
            Normalize = command.Get<bool>("normalize"),
            ExpanderGain = PythonOr(command.Get<double?>("expander_gain"), defaultExpanderGain),
            ExpanderRatio = PythonOr(command.Get<double?>("expander_ratio"), defaultExpanderRatio),
            ExpanderEnvelopeDetection = PythonOr(
                command.Get<string?>("expander_env_detection"),
                HiFiConstants.DefaultEnvelopeDetection),
            ExpanderAttackTau = PythonOr(command.Get<double?>("expander_attack_tau"), defaultExpanderAttackTau),
            ExpanderHoldTau = PythonOr(command.Get<double?>("expander_hold_tau"), defaultExpanderHoldTau),
            ExpanderReleaseTau = PythonOr(command.Get<double?>("expander_release_tau"), defaultExpanderReleaseTau),
            ExpanderWeightingLowTau = PythonOr(command.Get<double?>("expander_weighting_low_tau"), defaultWeightingLowTau),
            ExpanderWeightingHighTau = PythonOr(command.Get<double?>("expander_weighting_high_tau"), defaultWeightingHighTau),
            ExpanderWeightingLowPassHz = PythonOr(command.Get<double?>("expander_weighting_low_pass"), defaultWeightingLowPass),
            ExpanderWeightingLowPassTransitionHz = PythonOr(
                command.Get<double?>("expander_weighting_low_pass_transition"),
                defaultWeightingTransition),
            NoiseReductionDeemphasisLowTau = PythonOr(
                command.Get<double?>("nr_deemphasis_low_tau"),
                defaultNoiseReductionLowTau),
            NoiseReductionDeemphasisHighTau = PythonOr(
                command.Get<double?>("nr_deemphasis_high_tau"),
                defaultNoiseReductionHighTau),
            DeemphasisLowTau = PythonOr(command.Get<double?>("deemphasis_low_tau"), defaultDeemphasisLowTau),
            DeemphasisHighTau = PythonOr(command.Get<double?>("deemphasis_high_tau"), defaultDeemphasisHighTau),
            GnuRadio = command.Get<bool>("GRC"),
            RequestedAudioRateInteger = requestedAudioRate,
            AudioRateInteger = audioRate,
            Gain = command.Get<double>("gain"),
            InputFile = command.InputFile,
            OutputFile = command.OutputBase,
            AudioMode = PythonOr(command.Get<string?>("mode"), defaultMode),
            ThreadsInteger = command.Get<BigInteger>("threads"),
            Overwrite = command.Get<bool>("overwrite"),
            Gui = command.Get<bool>("UI"),
            DspBackend = DspBackendParser.Parse(command.Get<string>("dsp_backend"))
        };
    }

    private static double PythonOr(double? value, double fallback)
        => value.HasValue && value.Value != 0.0 ? value.Value : fallback;

    private static string PythonOr(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;
}
