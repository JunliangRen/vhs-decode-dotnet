namespace VHSDecode.Core.HiFi;

public static class HiFiConstants
{
    public const string AudioModeStereo = "s";
    public const string AudioModeStereoMidSide = "ms";
    public const string AudioModeDualMono = "d";
    public const string AudioModeDualMonoMidSide = "dms";
    public const string AudioModeMonoLeft = "l";
    public const string AudioModeMonoRight = "r";
    public const string AudioModeMonoSum = "sum";

    public const string DefaultVhsAudioMode = AudioModeStereo;
    public const string Default8MmAudioMode = AudioModeStereoMidSide;

    public const string DropoutCompensationFull = "full";
    public const string DropoutCompensationMute = "mute";
    public const string DropoutCompensationDisabled = "off";
    public const string DefaultDropoutCompensation = DropoutCompensationFull;

    public const string EnvelopeDetectionPeak = "peak";
    public const string EnvelopeDetectionRms = "rms";
    public const string DefaultEnvelopeDetection = EnvelopeDetectionPeak;

    public const double DefaultVhsExpanderGain = 30.0;
    public const double DefaultVhsExpanderRatio = 2.0;
    public const double DefaultVhsExpanderAttackTau = 6.5e-3;
    public const double DefaultVhsExpanderHoldTau = 0.0;
    public const double DefaultVhsExpanderReleaseTau = 70e-3;
    public const double DefaultVhsDeemphasisLowTau = 56e-6;
    public const double DefaultVhsDeemphasisHighTau = 20e-6;
    public const double DefaultVhsExpanderWeightingLowTau = 240e-6;
    public const double DefaultVhsExpanderWeightingHighTau = 24e-6;
    public const double DefaultVhsExpanderWeightingLowPass = 20_000.0;
    public const double DefaultVhsExpanderWeightingLowPassTransition = 100_000.0;
    public const double DefaultVhsNoiseReductionDeemphasisLowTau = 240e-6;
    public const double DefaultVhsNoiseReductionDeemphasisHighTau = 56e-6;

    public const double Default8MmExpanderGain = 6.0;
    public const double Default8MmExpanderRatio = 2.0;
    public const double Default8MmExpanderAttackTau = 3e-3;
    public const double Default8MmExpanderHoldTau = 15e-3;
    public const double Default8MmExpanderReleaseTau = 40e-3;
    public const double Default8MmNoiseReductionDeemphasisLowTau = 75e-6;
    public const double Default8MmNoiseReductionDeemphasisHighTau = 19e-6;
    public const double Default8MmDeemphasisLowTau = 75e-6;
    public const double Default8MmDeemphasisHighTau = 27e-6;
    public const double Default8MmExpanderWeightingLowTau = 75e-6;
    public const double Default8MmExpanderWeightingHighTau = 27e-6;
    public const double Default8MmExpanderWeightingLowPass = 20_000.0;
    public const double Default8MmExpanderWeightingLowPassTransition = 100_000.0;

    public const double DefaultSpectralNoiseReductionAmount = 0.0;
    public const string DefaultResamplerQuality = "high";
    public const int BlocksPerSecond = 2;
    public const int IntermediateAudioRate = 192_000;
    public const int HilbertIfRate = 1 << 23;
    public const int BlockPreTrimSamples = 1_000;
    public const int MinimumResamplerOverlapPadding = 50;
    public const int DefaultFinalAudioRate = 48_000;
    public const int PreviewAudioRate = 44_100;
    public const string DemodQuadrature = "quadrature";
    public const string DemodHilbert = "hilbert";
    public const string DefaultDemod = DemodQuadrature;
}
