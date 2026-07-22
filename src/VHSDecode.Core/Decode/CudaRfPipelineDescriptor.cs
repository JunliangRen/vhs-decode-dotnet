using System.Numerics;
using VHSDecode.Core.Compute.Cuda;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

/// <summary>
/// Immutable description of the RF graph selected by the normal decode setup.
/// It deliberately carries all mode-specific switches even when the currently
/// installed native component does not advertise the corresponding mode.
/// </summary>
internal sealed record CudaRfPipelineDescriptor(
    CudaRfMode Mode,
    int SampleCount,
    double SampleRateHz,
    Complex[] RfVideoFilter,
    Complex[] RfHighPassFilter,
    Complex[] RfMtfFilter,
    Complex[] VideoFilter,
    Complex[] VideoLowPassFilter,
    int VideoLowPassOffset,
    Complex[]? LdEfmFilter,
    LaserDiscAnalogAudioFilterSet? LdAnalogAudioFilters,
    RfVideoReferenceFilterSet? ReferenceFilters,
    bool RetainRfDiagnosticChannels,
    bool RemoveLdPalV4300DSpur,
    RfHighBoostOptions? RfHighBoost,
    DiffDemodRepairOptions? DiffDemodRepair,
    ChromaTrapOptions? ChromaTrap,
    SharpnessEqOptions? SharpnessEq,
    NonlinearDeemphasisOptions? NonlinearDeemphasis,
    SubDeemphasisOptions? SubDeemphasis,
    double? BetamaxFscNotchHz,
    bool HasVhsEnvelopeFilter,
    bool HasVhsRfTopFilter,
    double? CvbsVideoNotchHz,
    bool HasCvbsVideoBurst,
    double CvbsRawScale,
    double CvbsRawOffset)
{
    internal bool RequiresCpuStandardVideoStage => ReferenceFilters is not null;

    internal bool AppliesMtf => RfMtfFilter.Length != 0;
}
