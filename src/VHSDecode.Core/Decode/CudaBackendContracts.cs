using System.Numerics;
using VHSDecode.Core.Decode;

namespace VHSDecode.Core.Compute.Cuda;

internal static class CudaNativeAbi
{
    internal const uint Version = 1;
    internal const string ComponentFileName = "vhsdecode_cuda.dll";
    internal const int SelfTestSampleCount = 32_768;
    internal const double MaximumSelfTestAbsoluteError = 1e-9;
    internal const double MaximumSelfTestNrmse = 1e-11;
}

internal enum CudaBackendRequestMode
{
    Auto,
    Required
}

internal readonly record struct CudaBackendProbeRequest(
    CudaBackendRequestMode Mode,
    int DeviceIndex = 0,
    int BlockLength = CudaNativeAbi.SelfTestSampleCount,
    string? ComponentDirectory = null,
    Action<string>? Diagnostic = null)
{
    internal static CudaBackendProbeRequest Auto(
        int deviceIndex = 0,
        int blockLength = CudaNativeAbi.SelfTestSampleCount,
        Action<string>? diagnostic = null) =>
        new(CudaBackendRequestMode.Auto, deviceIndex, blockLength, Diagnostic: diagnostic);

    internal static CudaBackendProbeRequest Required(
        int deviceIndex = 0,
        int blockLength = CudaNativeAbi.SelfTestSampleCount,
        Action<string>? diagnostic = null) =>
        new(CudaBackendRequestMode.Required, deviceIndex, blockLength, Diagnostic: diagnostic);

    internal static CudaBackendProbeRequest ForBackend(
        RfComputeBackend backend,
        int deviceIndex,
        int blockLength,
        Action<string>? diagnostic = null,
        string? componentDirectory = null) =>
        backend switch
        {
            RfComputeBackend.Auto => new(
                CudaBackendRequestMode.Auto,
                deviceIndex,
                blockLength,
                componentDirectory,
                diagnostic),
            RfComputeBackend.Cuda => new(
                CudaBackendRequestMode.Required,
                deviceIndex,
                blockLength,
                componentDirectory,
                diagnostic),
            _ => throw new ArgumentException(
                "The explicit CPU backend does not require CUDA probing.",
                nameof(backend))
        };
}

internal enum CudaBackendProbeOutcome
{
    Ready,
    CpuFallback,
    FatalFailure
}

internal enum CudaBackendFailure
{
    None,
    InvalidBlockLength,
    UnsupportedPlatform,
    ComponentMissing,
    ComponentLoadFailed,
    RequiredExportMissing,
    AbiMismatch,
    NativeProbeFailed,
    NoDevices,
    InvalidDeviceIndex,
    DeviceUnsupported,
    ContextCreationFailed,
    AllocationFailed,
    SelfTestFailed,
    UnexpectedFailure
}

// Values are part of the vhsdecode_cuda C ABI v1 and must not be reordered.
internal enum CudaNativeStatus
{
    Success = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    CudaUnavailable = 3,
    DeviceNotFound = 4,
    DeviceUnsupported = 5,
    AllocationFailed = 6,
    CudaError = 7,
    CufftError = 8,
    SelfTestFailed = 9,
    BufferTooSmall = 10,
    NotSupported = 11,
    InternalError = 12
}

[Flags]
internal enum CudaBackendCapabilities : ulong
{
    None = 0,
    RfBatchTwoStage = 1UL << 0,
    RfHighPass = 1UL << 1,
    RfMtf = 1UL << 2,
    RfAnalytic = 1UL << 3,
    RfEnvelope = 1UL << 4,
    RfConjugateDemod = 1UL << 5,
    RfVideo = 1UL << 6,
    RfVideoLowPass = 1UL << 7,
    RfModeStandard = 1UL << 8,
    RfModeVhsRustApproximation = 1UL << 9,
    RfModeCvbs = 1UL << 10,
    VhsRustDemodKernel = 1UL << 11,
    LdEfmFrequency = 1UL << 12,
    LdAnalogAudioFrequency = 1UL << 13
}

internal enum CudaRfMode : uint
{
    StandardConjugate = 0,
    VhsRustApproximation = 1,
    Cvbs = 2
}

[Flags]
internal enum CudaRfBatchFlags : uint
{
    None = 0,
    OutputHighPass = 1u << 0,
    OutputAnalytic = 1u << 1,
    OutputEnvelope = 1u << 2,
    OutputDemodRaw = 1u << 3,
    OutputVideo = 1u << 4,
    OutputVideoLowPass = 1u << 5,
    ApplyMtf = 1u << 6,
    OutputLdEfm = 1u << 7,
    OutputLdAnalogAudio = 1u << 8,
    All = OutputHighPass |
          OutputAnalytic |
          OutputEnvelope |
          OutputDemodRaw |
          OutputVideo |
          OutputVideoLowPass |
          ApplyMtf |
          OutputLdEfm |
          OutputLdAnalogAudio
}

internal sealed record CudaDeviceInfo(
    int Ordinal,
    string Name,
    int ComputeCapabilityMajor,
    int ComputeCapabilityMinor,
    ulong TotalGlobalMemoryBytes,
    int DriverVersion,
    int RuntimeVersion,
    int CufftVersion,
    bool SupportsFp64,
    bool SupportsConcurrentCopyAndCompute,
    ulong? AvailableGlobalMemoryBytes = null)
{
    internal bool MeetsMinimumComputeCapability =>
        ComputeCapabilityMajor > 7 ||
        (ComputeCapabilityMajor == 7 && ComputeCapabilityMinor >= 5);
}

internal readonly record struct CudaSelfTestMetrics(
    bool Passed,
    double MaximumAbsoluteError,
    double Nrmse,
    ulong SampleCount,
    CudaNativeStatus NativeCudaStatus);

/// <summary>
/// Optional LD branches attached to an ABI-v1 RF job through reserved[0].
/// Filters and outputs remain pinned only for the synchronous native call.
/// </summary>
internal sealed class CudaLdFrequencyOptions
{
    internal int AudioLeftLowBin { get; init; }

    internal int AudioLeftBinCount { get; init; }

    internal int AudioRightLowBin { get; init; }

    internal int AudioRightBinCount { get; init; }

    internal Complex[]? EfmFilter { get; init; }

    internal Complex[]? AudioLeftFilter { get; init; }

    internal Complex[]? AudioRightFilter { get; init; }

    internal double[]? EfmOutput { get; init; }

    internal Complex[]? AudioLeftOutput { get; init; }

    internal Complex[]? AudioRightOutput { get; init; }
}

/// <summary>
/// Host buffers for one synchronous CUDA RF batch. Samples are block-major and contiguous.
/// The native call does not retain any array pointer after it returns.
/// </summary>
internal sealed class CudaRfBatchJob
{
    internal required CudaRfBatchFlags Flags { get; init; }

    internal required int SampleCount { get; init; }

    internal required int BatchCount { get; init; }

    internal uint StreamIndex { get; init; }

    internal CudaRfMode Mode { get; init; } = CudaRfMode.StandardConjugate;

    internal required double DemodPhaseScale { get; init; }

    internal double CvbsRawScale { get; init; } = 1.0;

    internal double CvbsRawOffset { get; init; }

    internal required double[] Input { get; init; }

    internal Complex[]? RfVideoFilter { get; init; }

    internal Complex[]? RfHighPassFilter { get; init; }

    internal Complex[]? MtfFilter { get; init; }

    internal Complex[]? DemodVideoFilter { get; init; }

    internal Complex[]? DemodVideoLowPassFilter { get; init; }

    internal Complex[]? PreviousAnalyticPerBatch { get; init; }

    internal Complex[]? LastAnalyticPerBatch { get; init; }

    internal double[]? RfHighPassOutput { get; init; }

    internal double[]? AnalyticRealOutput { get; init; }

    internal double[]? AnalyticImaginaryOutput { get; init; }

    internal double[]? EnvelopeOutput { get; init; }

    internal double[]? DemodRawOutput { get; init; }

    internal double[]? VideoOutput { get; init; }

    internal double[]? VideoLowPassOutput { get; init; }

    internal CudaLdFrequencyOptions? LdFrequencyOptions { get; init; }

    internal void Validate(int contextBlockLength)
    {
        if (SampleCount < 2 ||
            (SampleCount & 1) != 0 ||
            SampleCount != contextBlockLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SampleCount),
                SampleCount,
                $"CUDA batch sample count must be even, at least 2, and equal the selected RF block length ({contextBlockLength}).");
        }

        if (BatchCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchCount));
        }

        if (StreamIndex >= 2)
        {
            throw new ArgumentOutOfRangeException(nameof(StreamIndex), "CUDA stream index must be 0 or 1.");
        }

        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        if (!double.IsFinite(CvbsRawScale) || !double.IsFinite(CvbsRawOffset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CvbsRawScale),
                "CUDA CVBS raw mapping values must be finite.");
        }

        if (Flags == CudaRfBatchFlags.None || (Flags & ~CudaRfBatchFlags.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Flags), Flags, "CUDA RF batch flags are invalid.");
        }

        CudaRfBatchFlags demodGraph =
            CudaRfBatchFlags.OutputDemodRaw |
            CudaRfBatchFlags.OutputVideo |
            CudaRfBatchFlags.OutputVideoLowPass;
        if (Mode != CudaRfMode.Cvbs &&
            (Flags & demodGraph) != 0 &&
            (!double.IsFinite(DemodPhaseScale) || DemodPhaseScale <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(DemodPhaseScale),
                "CUDA demodulation phase scale must be finite and positive.");
        }

        if (Mode == CudaRfMode.Cvbs)
        {
            const CudaRfBatchFlags cvbsFlags =
                CudaRfBatchFlags.OutputEnvelope |
                CudaRfBatchFlags.OutputDemodRaw |
                CudaRfBatchFlags.OutputVideo |
                CudaRfBatchFlags.OutputVideoLowPass;
            if ((Flags & ~cvbsFlags) != 0 ||
                PreviousAnalyticPerBatch is not null ||
                LastAnalyticPerBatch is not null)
            {
                throw new NotSupportedException(
                    "CUDA CVBS mode supports envelope, direct demod/video, and video low-pass outputs only.");
            }
        }

        const CudaRfBatchFlags ldFrequencyFlags =
            CudaRfBatchFlags.OutputLdEfm |
            CudaRfBatchFlags.OutputLdAnalogAudio;
        CudaRfBatchFlags requestedLdFrequencyFlags = Flags & ldFrequencyFlags;
        if (requestedLdFrequencyFlags != 0 && Mode != CudaRfMode.StandardConjugate)
        {
            throw new NotSupportedException(
                "CUDA LD EFM and analog-audio frequency branches require standard RF mode.");
        }

        if (requestedLdFrequencyFlags == 0)
        {
            if (LdFrequencyOptions is not null)
            {
                throw new ArgumentException(
                    "CUDA LD frequency options were supplied without requesting an LD output.",
                    nameof(LdFrequencyOptions));
            }
        }
        else if (LdFrequencyOptions is null)
        {
            throw new ArgumentException(
                "CUDA LD frequency outputs require an options structure.",
                nameof(LdFrequencyOptions));
        }

        if (Mode == CudaRfMode.VhsRustApproximation &&
            ((Flags & (CudaRfBatchFlags.OutputEnvelope |
                       CudaRfBatchFlags.OutputVideo |
                       CudaRfBatchFlags.OutputVideoLowPass)) != 0 ||
             PreviousAnalyticPerBatch is not null ||
             LastAnalyticPerBatch is not null))
        {
            throw new NotSupportedException(
                "CUDA VHS Rust mode supports the non-recursive first stage only.");
        }

        int sampleValueCount = checked(SampleCount * BatchCount);
        int filterBinCount = (SampleCount / 2) + 1;
        RequireLength(Input, sampleValueCount, nameof(Input));

        CudaRfBatchFlags analyticGraph =
            CudaRfBatchFlags.OutputAnalytic |
            CudaRfBatchFlags.OutputEnvelope |
            CudaRfBatchFlags.OutputDemodRaw |
            CudaRfBatchFlags.OutputVideo |
            CudaRfBatchFlags.OutputVideoLowPass;
        if (Mode != CudaRfMode.Cvbs && (Flags & analyticGraph) != 0)
        {
            RequireLength(RfVideoFilter, filterBinCount, nameof(RfVideoFilter));
        }

        if ((Flags & CudaRfBatchFlags.OutputHighPass) != 0)
        {
            RequireLength(RfHighPassFilter, filterBinCount, nameof(RfHighPassFilter));
            RequireLength(RfHighPassOutput, sampleValueCount, nameof(RfHighPassOutput));
        }

        if ((Flags & CudaRfBatchFlags.ApplyMtf) != 0)
        {
            RequireLength(MtfFilter, filterBinCount, nameof(MtfFilter));
        }

        if ((Flags & CudaRfBatchFlags.OutputAnalytic) != 0)
        {
            RequireLength(AnalyticRealOutput, sampleValueCount, nameof(AnalyticRealOutput));
            RequireLength(AnalyticImaginaryOutput, sampleValueCount, nameof(AnalyticImaginaryOutput));
        }

        if ((Flags & CudaRfBatchFlags.OutputEnvelope) != 0)
        {
            RequireLength(EnvelopeOutput, sampleValueCount, nameof(EnvelopeOutput));
        }

        if ((Flags & CudaRfBatchFlags.OutputDemodRaw) != 0)
        {
            RequireLength(DemodRawOutput, sampleValueCount, nameof(DemodRawOutput));
        }

        if ((Flags & CudaRfBatchFlags.OutputVideo) != 0)
        {
            if (Mode != CudaRfMode.Cvbs)
            {
                RequireLength(DemodVideoFilter, filterBinCount, nameof(DemodVideoFilter));
            }
            RequireLength(VideoOutput, sampleValueCount, nameof(VideoOutput));
        }

        if ((Flags & CudaRfBatchFlags.OutputVideoLowPass) != 0)
        {
            RequireLength(DemodVideoLowPassFilter, filterBinCount, nameof(DemodVideoLowPassFilter));
            RequireLength(VideoLowPassOutput, sampleValueCount, nameof(VideoLowPassOutput));
        }

        if (LdFrequencyOptions is { } ldOptions)
        {
            if ((Flags & CudaRfBatchFlags.OutputLdEfm) != 0)
            {
                RequireLength(ldOptions.EfmFilter, filterBinCount, nameof(ldOptions.EfmFilter));
                RequireLength(ldOptions.EfmOutput, sampleValueCount, nameof(ldOptions.EfmOutput));
            }

            if ((Flags & CudaRfBatchFlags.OutputLdAnalogAudio) != 0)
            {
                ValidateAudioSlice(
                    ldOptions.AudioLeftLowBin,
                    ldOptions.AudioLeftBinCount,
                    SampleCount,
                    nameof(ldOptions.AudioLeftBinCount));
                ValidateAudioSlice(
                    ldOptions.AudioRightLowBin,
                    ldOptions.AudioRightBinCount,
                    SampleCount,
                    nameof(ldOptions.AudioRightBinCount));
                RequireLength(
                    ldOptions.AudioLeftFilter,
                    ldOptions.AudioLeftBinCount,
                    nameof(ldOptions.AudioLeftFilter));
                RequireLength(
                    ldOptions.AudioRightFilter,
                    ldOptions.AudioRightBinCount,
                    nameof(ldOptions.AudioRightFilter));
                RequireLength(
                    ldOptions.AudioLeftOutput,
                    checked(ldOptions.AudioLeftBinCount * BatchCount),
                    nameof(ldOptions.AudioLeftOutput));
                RequireLength(
                    ldOptions.AudioRightOutput,
                    checked(ldOptions.AudioRightBinCount * BatchCount),
                    nameof(ldOptions.AudioRightOutput));
            }
        }

        ValidateOptionalLength(PreviousAnalyticPerBatch, BatchCount, nameof(PreviousAnalyticPerBatch));
        ValidateOptionalLength(LastAnalyticPerBatch, BatchCount, nameof(LastAnalyticPerBatch));
    }

    private static void RequireLength(Array? values, int minimumLength, string parameterName)
    {
        if (values is null || values.Length < minimumLength)
        {
            throw new ArgumentException(
                $"CUDA RF batch {parameterName} requires at least {minimumLength} element(s).",
                parameterName);
        }
    }

    private static void ValidateOptionalLength(Array? values, int minimumLength, string parameterName)
    {
        if (values is not null)
        {
            RequireLength(values, minimumLength, parameterName);
        }
    }

    private static void ValidateAudioSlice(
        int lowBin,
        int binCount,
        int sampleCount,
        string parameterName)
    {
        bool isPowerOfTwo = binCount >= 2 && (binCount & (binCount - 1)) == 0;
        if (lowBin < 0 ||
            !isPowerOfTwo ||
            (binCount & 1) != 0 ||
            binCount > (sampleCount / 2) + 1 ||
            (long)lowBin + (binCount / 2) > sampleCount / 2)
        {
            throw new NotSupportedException(
                $"CUDA LD analog-audio {parameterName} must describe an even power-of-two slice inside the positive RF half-spectrum.");
        }
    }
}

internal interface ICudaNativeApi : IDisposable
{
    uint GetAbiVersion();

    CudaNativeStatus GetDeviceCount(out int deviceCount);

    CudaNativeStatus GetDeviceInfo(int deviceIndex, out CudaDeviceInfo deviceInfo);

    CudaNativeStatus CreateContext(int deviceIndex, out nint context);

    void DestroyContext(nint context);

    CudaNativeStatus RunSelfTest(nint context, out CudaSelfTestMetrics metrics);

    CudaNativeStatus GetCapabilities(nint context, out CudaBackendCapabilities capabilities);

    CudaNativeStatus ExecuteRfBatch(nint context, CudaRfBatchJob job);

    string GetLastError(nint context);
}

internal interface ICudaNativeApiLoader
{
    CudaNativeApiLoadResult Load(string? componentDirectory);
}

internal readonly record struct CudaNativeApiLoadResult(
    ICudaNativeApi? Api,
    CudaBackendFailure Failure,
    string Message)
{
    internal bool Succeeded => Api is not null && Failure == CudaBackendFailure.None;

    internal static CudaNativeApiLoadResult Success(ICudaNativeApi api) =>
        new(api, CudaBackendFailure.None, string.Empty);

    internal static CudaNativeApiLoadResult Failed(CudaBackendFailure failure, string message) =>
        new(null, failure, message);
}
