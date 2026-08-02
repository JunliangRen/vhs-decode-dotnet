using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

public sealed record AutomaticChromaGainResult(double[] Samples, double MeanBurstRms);

public sealed record CurrentAutomaticChromaGainResult(
    double MeanBurstAmplitude,
    double NoiseFloor);

public sealed record ChromaBurstDemodulationResult(
    double PhaseDegrees,
    double PhaseOffsetDegrees,
    double Magnitude,
    double I,
    double Q)
{
    public int Start { get; init; }

    public int End { get; init; }

    public double Center { get; init; }

    public double Amplitude { get; init; }

    public double Dc { get; init; }

    public double FrequencyHz { get; init; }
}

public sealed record ChromaPhaseLine(
    int LineNumber,
    int PhaseRotation,
    double BurstPhaseDegrees = 0.0,
    double BurstPhaseOffsetDegrees = 0.0,
    double BurstMagnitude = 0.0,
    double I = 0.0,
    double Q = 0.0)
{
    public int BurstStart { get; init; }

    public int BurstEnd { get; init; }

    public double BurstCenter { get; init; }

    public double BurstAmplitude { get; init; }

    public double BurstDc { get; init; }

    public double BurstFrequencyHz { get; init; }
}

public sealed record ChromaPhaseSequenceResult(
    int NextChromaRotationIndex,
    ChromaPhaseLine[] PhaseSequence,
    int BurstDetectedLine,
    double BurstMagnitudeAverage,
    double BurstPhaseAverageDegrees,
    double EvenBurstPhaseAverageDegrees,
    double OddBurstPhaseAverageDegrees);

public sealed record ChromaCarrierEstimate(
    double NominalCarrierHz,
    double PeakCarrierHz,
    double CarrierHz,
    double OffsetHz,
    double PhaseRadians);

public sealed record VhsChromaFieldOptions(
    string ColorSystem,
    int OutputLineLength,
    int OutputLineCount,
    double OutputSampleRateHz,
    double FscMHz,
    double ColorUnderCarrierHz,
    int BurstStart,
    int BurstEnd,
    double BurstAbsRef,
    int[]? ChromaRotation,
    bool DisableComb,
    bool DisablePhaseCorrection,
    bool EnableColorKiller,
    bool DetectChromaTrackPhase)
{
    public int WorkerThreads { get; init; } = 1;

    public TransferFunction? FinalFilter { get; init; }

    public IReadOnlyList<SosSection>? FinalSosFilter { get; init; }

    internal ChromaSuperGaussianFinalFilter? SuperGaussianFinalFilter
    {
        get;
        init;
    }

    public TransferFunction? ChromaDeemphasisFilter { get; init; }

    public TransferFunction? ChromaPreFilter { get; init; }

    public IReadOnlyList<SosSection>? ChromaPreSosFilter { get; init; }

    public TransferFunction? ChromaAudioNotchFilter { get; init; }

    public TransferFunction? ChromaVideoNotchFilter { get; init; }

    public int ChromaPreFilterMoveSamples { get; init; }

    public bool ChromaAfcTrackCarrier { get; init; }

    public double ChromaAfcLineFrequencyHz { get; init; }

    public double ChromaAfcFineTuneStepHz { get; init; }

    public ChromaAfcMeasurementFilterSet? ChromaAfcMeasurementFilters { get; init; }

    public double ChromaAfcPreFilterLowHz { get; init; }

    public double ChromaAfcPreFilterUpperRatio { get; init; }

    public int ChromaAfcPreFilterOrder { get; init; }

    public double ChromaAfcDecodeSampleRateHz { get; init; }

    public bool DisableBurstHsync { get; init; }

    public int? InitialChromaRotationIndex { get; init; }

    internal bool UseCurrentChromaProcessing { get; init; }

    internal int SyncTipLength { get; init; }

    internal double CtiMix { get; init; }

    internal long CtiWidth { get; init; } = 2;
}

public sealed record VhsChromaFieldResult(
    ushort[] Samples,
    int BurstDetectedLine,
    int? FieldPhaseId,
    int NextChromaRotationIndex,
    ChromaPhaseSequenceResult Phase)
{
    public ChromaCarrierEstimate? CarrierEstimate { get; init; }
}

internal sealed record VhsChromaPhaseAnalysis(
    ChromaPhaseSequenceResult Phase,
    double[][] Heterodyne,
    double HeterodyneCarrierHz,
    double HeterodynePhaseRadians);

internal sealed class VhsChromaCarrierTableCache
{
    private readonly Lock _gate = new();
    private HeterodyneEntry? _heterodyne;
    private CarrierEntry? _carrier;

    internal double[][] GetHeterodyne(
        int sampleCount,
        double fscMHz,
        double colorUnderCarrierMHz,
        double outputSampleRateMHz,
        double phaseDriftRadians,
        int workerThreads)
    {
        lock (_gate)
        {
            if (_heterodyne is { } cached
                && cached.SampleCount == sampleCount
                && cached.FscMHz == fscMHz
                && cached.ColorUnderCarrierMHz == colorUnderCarrierMHz
                && cached.OutputSampleRateMHz == outputSampleRateMHz
                && cached.PhaseDriftRadians == phaseDriftRadians)
            {
                return cached.Table;
            }

            double[][] table = VhsChromaDecoder.BuildHeterodyneTable(
                sampleCount,
                fscMHz,
                colorUnderCarrierMHz,
                outputSampleRateMHz,
                phaseDriftRadians,
                workerThreads);
            _heterodyne = new HeterodyneEntry(
                sampleCount,
                fscMHz,
                colorUnderCarrierMHz,
                outputSampleRateMHz,
                phaseDriftRadians,
                table);
            return table;
        }
    }

    internal (double[] Sin, double[] Cos) GetCarrierTables(
        int sampleCount,
        double carrierMHz,
        double outputSampleRateMHz,
        int workerThreads)
    {
        lock (_gate)
        {
            if (_carrier is { } cached
                && cached.SampleCount == sampleCount
                && cached.CarrierMHz == carrierMHz
                && cached.OutputSampleRateMHz == outputSampleRateMHz)
            {
                return (cached.Sin, cached.Cos);
            }

            (double[] sin, double[] cos) = VhsChromaDecoder.BuildCarrierTables(
                sampleCount,
                carrierMHz,
                outputSampleRateMHz,
                workerThreads);
            _carrier = new CarrierEntry(
                sampleCount,
                carrierMHz,
                outputSampleRateMHz,
                sin,
                cos);
            return (sin, cos);
        }
    }

    private sealed record HeterodyneEntry(
        int SampleCount,
        double FscMHz,
        double ColorUnderCarrierMHz,
        double OutputSampleRateMHz,
        double PhaseDriftRadians,
        double[][] Table);

    private sealed record CarrierEntry(
        int SampleCount,
        double CarrierMHz,
        double OutputSampleRateMHz,
        double[] Sin,
        double[] Cos);
}

public delegate ChromaBurstDemodulationResult ChromaBurstProbe(
    int lineNumber,
    int phaseRotation,
    double lineScale);

public static class VhsChromaDecoder
{
    private const int ParallelSampleThreshold = 64 * 1024;
    private const int StartingLine = 16;
    private const double BurstMagnitudeThreshold = 2.5e4;
    private const int BurstCheckSkipLines = 16;
    private const int MaximumBurstProbeWorkers = 4;
    private const double TrackChangeThresholdDegrees = 90.0;
    private const double S16AbsMax = 32767.0;

    public static ushort[] ChromaToU16(ReadOnlySpan<double> chroma)
    {
        var output = new ushort[chroma.Length];
        ChromaToU16(chroma, output);
        return output;
    }

    internal static void ChromaToU16(ReadOnlySpan<double> chroma, Span<ushort> output)
    {
        if (output.Length != chroma.Length)
        {
            throw new ArgumentException("Output length must match chroma length.", nameof(output));
        }

        int vectorizedLength = chroma.Length & ~3;
        int index = 0;
        if (Avx2.IsSupported && Sse41.IsSupported)
        {
            Vector256<double> offsetVector = Vector256.Create(S16AbsMax);
            Vector256<double> maximumVector = Vector256.Create((double)ushort.MaxValue);
            Vector256<long> exponentMask = Vector256.Create(0x7FF0_0000_0000_0000L);
            ref double sourceReference = ref MemoryMarshal.GetReference(chroma);
            ref ushort destinationReference = ref MemoryMarshal.GetReference(output);
            for (; index < vectorizedLength; index += 4)
            {
                Vector256<double> values = Vector256.LoadUnsafe(ref sourceReference, (nuint)index);
                Vector256<double> shifted = Avx.Add(values, offsetVector);
                Vector256<long> exponents = Avx2.And(shifted.AsInt64(), exponentMask);
                Vector256<double> finiteMask = Avx2.CompareGreaterThan(
                    exponentMask,
                    exponents).AsDouble();
                shifted = Avx.And(shifted, finiteMask);
                shifted = Avx.Max(shifted, Vector256<double>.Zero);
                shifted = Avx.Min(shifted, maximumVector);
                Vector128<int> converted = Avx.ConvertToVector128Int32WithTruncation(shifted);
                Vector128<ushort> packed = Sse41.PackUnsignedSaturate(
                    converted,
                    Vector128<int>.Zero);
                packed.GetLower().StoreUnsafe(ref destinationReference, (nuint)index);
            }
        }

        for (; index < vectorizedLength; index++)
        {
            double shifted = chroma[index] + S16AbsMax;
            output[index] = !double.IsFinite(shifted) || shifted <= 0.0
                ? ushort.MinValue
                : shifted >= ushort.MaxValue
                    ? ushort.MaxValue
                    : (ushort)shifted;
        }

        for (; index < chroma.Length; index++)
        {
            double shifted = chroma[index] + S16AbsMax;
            output[index] = !double.IsFinite(shifted) || shifted < long.MinValue || shifted > long.MaxValue
                ? ushort.MinValue
                : unchecked((ushort)(long)shifted);
        }

    }

    public static VhsChromaFieldResult DecodeField(
        ReadOnlySpan<double> chroma,
        VhsChromaFieldOptions options,
        IReadOnlyList<double> lineLocations,
        int inputLineLength,
        int? chromaRotationIndex = null,
        int previousBurstDetectedLine = 0,
        bool? isFirstField = null,
        int fieldNumber = 0,
        Func<double[], double[]>? burstFilter = null,
        Func<double[], double[]>? finalFilter = null,
        int lineOffset = 0,
        double? previousChromaAfcCarrierHz = null,
        double previousChromaAfcPhaseRadians = 0.0)
    {
        double[] chromaField = chroma.ToArray();
        VhsChromaPhaseAnalysis analysis = AnalyzeFieldPhaseWithWorkspace(
            chromaField,
            options,
            lineLocations,
            inputLineLength,
            chromaRotationIndex,
            previousBurstDetectedLine,
            burstFilter,
            lineOffset,
            previousChromaAfcCarrierHz,
            previousChromaAfcPhaseRadians);
        return DecodeFieldWithPhaseCore(
            chromaField,
            options,
            analysis.Phase,
            isFirstField,
            fieldNumber,
            finalFilter,
            lineOffset,
            previousChromaAfcCarrierHz,
            previousChromaAfcPhaseRadians,
            analysis);
    }

    public static ChromaPhaseSequenceResult AnalyzeFieldPhase(
        ReadOnlySpan<double> chroma,
        VhsChromaFieldOptions options,
        IReadOnlyList<double> lineLocations,
        int inputLineLength,
        int? chromaRotationIndex = null,
        int previousBurstDetectedLine = 0,
        Func<double[], double[]>? burstFilter = null,
        int lineOffset = 0,
        double? previousChromaAfcCarrierHz = null,
        double previousChromaAfcPhaseRadians = 0.0)
        => AnalyzeFieldPhaseWithWorkspace(
            chroma.ToArray(),
            options,
            lineLocations,
            inputLineLength,
            chromaRotationIndex,
            previousBurstDetectedLine,
            burstFilter,
            lineOffset,
            previousChromaAfcCarrierHz,
            previousChromaAfcPhaseRadians).Phase;

    internal static VhsChromaPhaseAnalysis AnalyzeFieldPhaseWithWorkspace(
        double[] chromaField,
        VhsChromaFieldOptions options,
        IReadOnlyList<double> lineLocations,
        int inputLineLength,
        int? chromaRotationIndex = null,
        int previousBurstDetectedLine = 0,
        Func<double[], double[]>? burstFilter = null,
        int lineOffset = 0,
        double? previousChromaAfcCarrierHz = null,
        double previousChromaAfcPhaseRadians = 0.0,
        VhsChromaCarrierTableCache? carrierTableCache = null,
        bool useFloat32Samples = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(chromaField);
        ValidateLineShape(chromaField.Length, options.OutputLineCount, options.OutputLineLength);
        ValidateBurstRange(options.BurstStart, options.BurstEnd, options.OutputLineLength);

        double outputSampleRateMHz = options.FscMHz * 4.0;
        double phaseCarrierHz = options.ChromaAfcTrackCarrier
            ? previousChromaAfcCarrierHz ?? options.ColorUnderCarrierHz
            : options.ColorUnderCarrierHz;
        double phaseDriftRadians = options.ChromaAfcTrackCarrier
            ? previousChromaAfcPhaseRadians
            : 0.0;
        Func<double[], double[]>? effectiveBurstFilter = burstFilter;
        if (effectiveBurstFilter is null && options.FinalSosFilter is not null)
        {
            effectiveBurstFilter = useFloat32Samples
                ? values =>
                {
                    SosFilter.ApplyForwardBackwardFloat32InPlace(options.FinalSosFilter, values);
                    return values;
                }
            : values => SosFilter.ApplyForwardBackward(options.FinalSosFilter, values);
        }
        else if (effectiveBurstFilter is null && options.FinalFilter is not null)
        {
            effectiveBurstFilter = values => IirFilter.ApplyForwardBackward(options.FinalFilter, values);
        }

        double[][] heterodyne = carrierTableCache?.GetHeterodyne(
                chromaField.Length,
                options.FscMHz,
                phaseCarrierHz / 1_000_000.0,
                outputSampleRateMHz,
                phaseDriftRadians,
                options.WorkerThreads)
            ?? BuildHeterodyneTable(
                chromaField.Length,
                options.FscMHz,
                phaseCarrierHz / 1_000_000.0,
                outputSampleRateMHz,
                phaseDriftRadians,
                options.WorkerThreads);
        (double[] burstSin, double[] burstCos) = carrierTableCache?.GetCarrierTables(
                chromaField.Length,
                options.FscMHz,
                outputSampleRateMHz,
                options.WorkerThreads)
            ?? BuildCarrierTables(
                chromaField.Length,
                options.FscMHz,
                outputSampleRateMHz,
                options.WorkerThreads);
        ChromaPhaseSequenceResult result = GetPhaseRotationSequence(
            options.ChromaRotation,
            chromaRotationIndex,
            lineLocations,
            lineOffset,
            options.OutputLineCount,
            inputLineLength,
            (lineNumber, phaseRotation, lineScale) =>
                options.UseCurrentChromaProcessing
                    ? ProbeUpconvertedBurstCurrent(
                        chromaField,
                        heterodyne,
                        phaseRotation,
                        options.BurstStart,
                        options.BurstEnd,
                        burstSin,
                        burstCos,
                        lineNumber,
                        lineOffset,
                        options.OutputLineLength,
                        options.FscMHz * 1_000_000.0,
                        effectiveBurstFilter,
                        useFloat32Samples)
                    : ProbeUpconvertedBurst(
                        chromaField,
                        heterodyne,
                        phaseRotation,
                        options.BurstStart,
                        options.BurstEnd,
                        burstSin,
                        burstCos,
                        lineScale,
                        lineNumber,
                        lineOffset,
                        options.OutputLineLength,
                        effectiveBurstFilter,
                        useFloat32Samples),
            options.DetectChromaTrackPhase,
            rotationCheckStartLine: Math.Max(lineOffset, lineOffset + options.OutputLineCount - BurstCheckSkipLines),
            options.EnableColorKiller,
            previousBurstDetectedLine,
            options.ColorSystem,
            options.UseCurrentChromaProcessing ? options.WorkerThreads : 1);
        return new VhsChromaPhaseAnalysis(
            result,
            heterodyne,
            phaseCarrierHz,
            phaseDriftRadians);
    }

    public static VhsChromaFieldResult DecodeFieldWithPhase(
        ReadOnlySpan<double> chroma,
        VhsChromaFieldOptions options,
        ChromaPhaseSequenceResult phase,
        bool? isFirstField = null,
        int fieldNumber = 0,
        Func<double[], double[]>? finalFilter = null,
        int lineOffset = 0,
        double? previousChromaAfcCarrierHz = null,
        double previousChromaAfcPhaseRadians = 0.0)
        => DecodeFieldWithPhaseCore(
            chroma,
            options,
            phase,
            isFirstField,
            fieldNumber,
            finalFilter,
            lineOffset,
            previousChromaAfcCarrierHz,
            previousChromaAfcPhaseRadians,
            preparedAnalysis: null);

    internal static VhsChromaFieldResult DecodeFieldWithPhase(
        ReadOnlySpan<double> chroma,
        VhsChromaFieldOptions options,
        VhsChromaPhaseAnalysis analysis,
        bool? isFirstField = null,
        int fieldNumber = 0,
        Func<double[], double[]>? finalFilter = null,
        int lineOffset = 0,
        double? previousChromaAfcCarrierHz = null,
        double previousChromaAfcPhaseRadians = 0.0,
        ushort[]? outputDestination = null)
        => DecodeFieldWithPhaseCore(
            chroma,
            options,
            analysis.Phase,
            isFirstField,
            fieldNumber,
            finalFilter,
            lineOffset,
            previousChromaAfcCarrierHz,
            previousChromaAfcPhaseRadians,
            analysis,
            outputDestination: outputDestination);

    internal static VhsChromaFieldResult DecodeOwnedFieldWithPhase(
        double[] chroma,
        VhsChromaFieldOptions options,
        ChromaPhaseSequenceResult phase,
        bool? isFirstField = null,
        int fieldNumber = 0,
        Func<double[], double[]>? finalFilter = null,
        int lineOffset = 0,
        double? previousChromaAfcCarrierHz = null,
        double previousChromaAfcPhaseRadians = 0.0)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        return DecodeFieldWithPhaseCore(
            chroma,
            options,
            phase,
            isFirstField,
            fieldNumber,
            finalFilter,
            lineOffset,
            previousChromaAfcCarrierHz,
            previousChromaAfcPhaseRadians,
            preparedAnalysis: null,
            ownedChromaInput: chroma);
    }

    internal static VhsChromaFieldResult DecodeOwnedFieldWithPhase(
        double[] chroma,
        VhsChromaFieldOptions options,
        VhsChromaPhaseAnalysis analysis,
        bool? isFirstField = null,
        int fieldNumber = 0,
        Func<double[], double[]>? finalFilter = null,
        int lineOffset = 0,
        double? previousChromaAfcCarrierHz = null,
        double previousChromaAfcPhaseRadians = 0.0,
        ushort[]? outputDestination = null)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        ArgumentNullException.ThrowIfNull(analysis);
        return DecodeFieldWithPhaseCore(
            chroma,
            options,
            analysis.Phase,
            isFirstField,
            fieldNumber,
            finalFilter,
            lineOffset,
            previousChromaAfcCarrierHz,
            previousChromaAfcPhaseRadians,
            analysis,
            ownedChromaInput: chroma,
            outputDestination: outputDestination);
    }

    private static VhsChromaFieldResult DecodeFieldWithPhaseCore(
        ReadOnlySpan<double> chroma,
        VhsChromaFieldOptions options,
        ChromaPhaseSequenceResult phase,
        bool? isFirstField,
        int fieldNumber,
        Func<double[], double[]>? finalFilter,
        int lineOffset,
        double? previousChromaAfcCarrierHz,
        double previousChromaAfcPhaseRadians,
        VhsChromaPhaseAnalysis? preparedAnalysis,
        double[]? ownedChromaInput = null,
        ushort[]? outputDestination = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(phase);
        ValidateLineShape(chroma.Length, options.OutputLineCount, options.OutputLineLength);
        ValidateBurstRange(options.BurstStart, options.BurstEnd, options.OutputLineLength);
        if (outputDestination is not null && outputDestination.Length != chroma.Length)
        {
            throw new ArgumentException(
                "Output destination length must match chroma length.",
                nameof(outputDestination));
        }

        if (phase.BurstDetectedLine == -1)
        {
            ushort[] neutral = outputDestination ?? new ushort[chroma.Length];
            neutral.AsSpan().Fill((ushort)S16AbsMax);
            return new VhsChromaFieldResult(
                neutral,
                phase.BurstDetectedLine,
                null,
                phase.NextChromaRotationIndex,
                phase);
        }

        double[]? mutableChromaField = ApplyConfiguredChromaPreFilter(
            chroma,
            options,
            previousChromaAfcCarrierHz);
        mutableChromaField ??= ownedChromaInput;
        ReadOnlySpan<double> chromaField = mutableChromaField is null
            ? chroma
            : mutableChromaField;
        double outputSampleRateMHz = options.FscMHz * 4.0;
        ReadOnlySpan<double> carrierProbe = chromaField;
        if (options.ChromaAfcTrackCarrier && options.ChromaAfcMeasurementFilters is { } measurementFilters)
        {
            double[] filteredCarrierProbe = SosFilter.ApplyForwardBackwardFloat32(
                measurementFilters.HighPass,
                carrierProbe);
            SosFilter.ApplyForwardBackwardFloat32InPlace(
                measurementFilters.LowPass,
                filteredCarrierProbe);
            carrierProbe = filteredCarrierProbe;
        }

        ChromaCarrierEstimate? carrierEstimate = options.ChromaAfcTrackCarrier
            ? EstimateChromaCarrier(
                carrierProbe,
                options.FscMHz * 4_000_000.0,
                options.ColorUnderCarrierHz,
                options.ChromaAfcLineFrequencyHz,
                options.ChromaAfcFineTuneStepHz)
            : null;
        double trackedCarrierHz = carrierEstimate?.CarrierHz
            ?? previousChromaAfcCarrierHz
            ?? options.ColorUnderCarrierHz;
        double phaseDriftRadians = carrierEstimate?.PhaseRadians
            ?? previousChromaAfcPhaseRadians;
        bool usePhaseCompensation = IsNtsc(options.ColorSystem)
            && !options.DisablePhaseCorrection
            && isFirstField.HasValue;
        double[][]? heterodyne = null;
        if (!usePhaseCompensation)
        {
            heterodyne = preparedAnalysis is not null
                && preparedAnalysis.HeterodyneCarrierHz == trackedCarrierHz
                && preparedAnalysis.HeterodynePhaseRadians == phaseDriftRadians
                && preparedAnalysis.Heterodyne.Length == 4
                && preparedAnalysis.Heterodyne[0].Length == chromaField.Length
                    ? preparedAnalysis.Heterodyne
                    : BuildHeterodyneTable(
                        chromaField.Length,
                        options.FscMHz,
                        trackedCarrierHz / 1_000_000.0,
                        outputSampleRateMHz,
                        phaseDriftRadians,
                        options.WorkerThreads);
        }

        double[]? burstDeemphasizedChroma = null;
        if (IsNtsc(options.ColorSystem))
        {
            if (mutableChromaField is not null)
            {
                ApplyBurstDeemphasisInPlace(
                    mutableChromaField,
                    lineOffset,
                    options.OutputLineCount,
                    options.OutputLineLength,
                    options.BurstStart,
                    options.BurstEnd,
                    samplesAfterBurst:
                        options.UseCurrentChromaProcessing ? 4 : 5);
                burstDeemphasizedChroma = mutableChromaField;
            }
            else
            {
                burstDeemphasizedChroma = ApplyBurstDeemphasis(
                    chromaField,
                    lineOffset,
                    options.OutputLineCount,
                    options.OutputLineLength,
                    options.BurstStart,
                    options.BurstEnd,
                    samplesAfterBurst:
                        options.UseCurrentChromaProcessing ? 4 : 5);
            }

            chromaField = burstDeemphasizedChroma;
        }

        double[] upconverted;
        int? fieldPhaseId = null;
        if (usePhaseCompensation)
        {
            (fieldPhaseId, double targetPhase) = NtscFieldPhaseTarget(
                isFirstField.GetValueOrDefault(),
                fieldNumber);
            if (options.UseCurrentChromaProcessing)
            {
                upconverted = burstDeemphasizedChroma!;
                UpconvertChromaPhaseCompensatedCurrentInPlace(
                    upconverted,
                    lineOffset,
                    options.OutputLineLength,
                    phase.PhaseSequence,
                    options.ColorUnderCarrierHz,
                    options.FscMHz * 1_000_000.0,
                    targetPhaseEvenDegrees: targetPhase,
                    targetPhaseOddDegrees: targetPhase);
            }
            else
            {
                upconverted = UpconvertChromaPhaseCompensated(
                    chromaField,
                    lineOffset,
                    options.OutputLineLength,
                    phase.PhaseSequence,
                    options.ColorUnderCarrierHz,
                    options.FscMHz,
                    targetPhaseEvenDegrees: targetPhase,
                    targetPhaseOddDegrees: targetPhase);
            }
        }
        else
        {
            if (finalFilter is null
                && ownedChromaInput is not null
                && mutableChromaField is not null
                && TryUpconvertChromaInPlace(
                    mutableChromaField,
                    lineOffset,
                    options.OutputLineLength,
                    phase.PhaseSequence,
                    heterodyne!))
            {
                upconverted = mutableChromaField;
            }
            else
            {
                upconverted = UpconvertChroma(
                    chromaField,
                    lineOffset,
                    options.OutputLineLength,
                    phase.PhaseSequence,
                    heterodyne!);
            }
        }

        if (finalFilter is not null)
        {
            upconverted = finalFilter(upconverted);
        }
        else if (options.SuperGaussianFinalFilter is not null)
        {
            upconverted =
                options.SuperGaussianFinalFilter.ApplyInPlace(
                    upconverted,
                    options.WorkerThreads);
        }
        else if (options.FinalSosFilter is not null)
        {
            SosFilter.ApplyForwardBackwardFloat32InPlace(options.FinalSosFilter, upconverted);
        }
        else if (options.FinalFilter is not null)
        {
            upconverted = IirFilter.ApplyForwardBackward(options.FinalFilter, upconverted);
        }

        if (options.ChromaDeemphasisFilter is not null)
        {
            upconverted = IirFilter.ApplyForward(options.ChromaDeemphasisFilter, upconverted);
        }

        bool retainFloat32 = finalFilter is null
            && options.ChromaDeemphasisFilter is null
            && (options.SuperGaussianFinalFilter is not null
                || options.FinalSosFilter is not null
                || options.FinalFilter is null);

        ushort[] gained;
        if (options.UseCurrentChromaProcessing)
        {
            double[] currentChroma = upconverted;
            if (!options.DisableComb)
            {
                bool isNtsc = IsNtsc(options.ColorSystem);
                if (finalFilter is not null)
                {
                    // A custom filter may retain or share its returned array.
                    currentChroma = isNtsc
                        ? ApplyNtscComb(
                            currentChroma,
                            options.OutputLineLength,
                            retainFloat32)
                        : ApplyPalComb(
                            currentChroma,
                            options.OutputLineLength,
                            retainFloat32);
                }
                else if (isNtsc)
                {
                    ApplyNtscCombInPlace(
                        currentChroma,
                        options.OutputLineLength,
                        retainFloat32);
                }
                else
                {
                    ApplyPalCombInPlace(
                        currentChroma,
                        options.OutputLineLength,
                        retainFloat32);
                }
            }

            CurrentAutomaticChromaGainResult gain =
                ApplyCurrentAutomaticChromaGainInPlace(
                    currentChroma,
                    options.BurstAbsRef,
                    phase.PhaseSequence,
                    phase.BurstDetectedLine,
                    options.SyncTipLength);
            if (options.CtiMix != 0.0)
            {
                ChromaTransientImprovement.ApplyInPlace(
                    currentChroma,
                    checked(lineOffset * options.OutputLineLength),
                    options.OutputLineLength,
                    gain.NoiseFloor,
                    options.CtiWidth,
                    options.CtiMix,
                    options.WorkerThreads);
            }

            if (outputDestination is null)
            {
                gained = ChromaToU16(currentChroma);
            }
            else
            {
                ChromaToU16(currentChroma, outputDestination);
                gained = outputDestination;
            }
        }
        else
        {
            if (options.DisableComb)
            {
                gained = outputDestination is null
                    ? ApplyAutomaticChromaGainToU16(
                        upconverted,
                        options.BurstAbsRef,
                        options.BurstStart,
                        options.BurstEnd,
                        options.OutputLineLength,
                        options.OutputLineCount,
                        phase.BurstDetectedLine,
                        useFloat32Rms: retainFloat32)
                    : ApplyAutomaticChromaGainToU16(
                        upconverted,
                        options.BurstAbsRef,
                        options.BurstStart,
                        options.BurstEnd,
                        options.OutputLineLength,
                        options.OutputLineCount,
                        phase.BurstDetectedLine,
                        useFloat32Rms: retainFloat32,
                        output: outputDestination);
            }
            else
            {
                gained = outputDestination is null
                    ? ApplyAutomaticChromaGainWithCombToU16(
                        upconverted,
                        options.BurstAbsRef,
                        options.BurstStart,
                        options.BurstEnd,
                        options.OutputLineLength,
                        options.OutputLineCount,
                        phase.BurstDetectedLine,
                        IsNtsc(options.ColorSystem) ? 1 : 2,
                        retainFloat32,
                        useFloat32Rms: retainFloat32)
                    : ApplyAutomaticChromaGainWithCombToU16(
                        upconverted,
                        options.BurstAbsRef,
                        options.BurstStart,
                        options.BurstEnd,
                        options.OutputLineLength,
                        options.OutputLineCount,
                        phase.BurstDetectedLine,
                        IsNtsc(options.ColorSystem) ? 1 : 2,
                        retainFloat32,
                        useFloat32Rms: retainFloat32,
                        output: outputDestination);
            }
        }

        return new VhsChromaFieldResult(
            gained,
            phase.BurstDetectedLine,
            fieldPhaseId,
            phase.NextChromaRotationIndex,
            phase)
        {
            CarrierEstimate = carrierEstimate
        };
    }

    public static double[] ApplyChromaPreFilter(
        ReadOnlySpan<double> chroma,
        VhsChromaFieldOptions options,
        double? previousChromaAfcCarrierHz = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        double[] output = chroma.ToArray();
        return ApplyConfiguredChromaPreFilter(output, options, previousChromaAfcCarrierHz)
            ?? output;
    }

    internal static double[]? ApplyConfiguredChromaPreFilter(
        ReadOnlySpan<double> chroma,
        VhsChromaFieldOptions options,
        double? previousChromaAfcCarrierHz = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        TransferFunction? preFilter = options.ChromaPreFilter;
        IReadOnlyList<SosSection>? preSosFilter = options.ChromaPreSosFilter;
        if (options.ChromaAfcTrackCarrier
            && options.ChromaAfcPreFilterLowHz > 0.0
            && options.ChromaAfcPreFilterUpperRatio > 0.0
            && options.ChromaAfcPreFilterOrder > 0
            && options.ChromaAfcDecodeSampleRateHz > 0.0)
        {
            double carrierHz = previousChromaAfcCarrierHz ?? options.ColorUnderCarrierHz;
            preSosFilter = DecodeFilterSetBuilder.BuildChromaAfcBandPassSosFilter(
                options.ChromaAfcPreFilterLowHz,
                carrierHz * options.ChromaAfcPreFilterUpperRatio,
                options.ChromaAfcPreFilterOrder,
                options.ChromaAfcDecodeSampleRateHz);
        }

        double[] output;
        if (preSosFilter is not null)
        {
            output = SosFilter.ApplyForwardBackward(preSosFilter, chroma);
        }
        else if (preFilter is not null)
        {
            output = IirFilter.ApplyForwardBackward(preFilter, chroma);
        }
        else
        {
            return null;
        }
        if (options.ChromaAudioNotchFilter is not null)
        {
            output = IirFilter.ApplyForwardBackward(options.ChromaAudioNotchFilter, output);
        }

        if (options.ChromaVideoNotchFilter is not null)
        {
            output = IirFilter.ApplyForwardBackward(options.ChromaVideoNotchFilter, output);
        }

        return ShiftChromaAndRemoveDc(output, options.ChromaPreFilterMoveSamples);
    }

    public static ChromaCarrierEstimate? EstimateChromaCarrier(
        ReadOnlySpan<double> chroma,
        double sampleRateHz,
        double nominalCarrierHz,
        double lineFrequencyHz,
        double fineTuneStepHz)
    {
        if (chroma.IsEmpty)
        {
            return null;
        }

        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (nominalCarrierHz <= 0.0 || nominalCarrierHz >= sampleRateHz / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalCarrierHz));
        }

        if (lineFrequencyHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineFrequencyHz));
        }

        if (fineTuneStepHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(fineTuneStepHz));
        }

        int fftLength = chroma.Length;
        Complex[] spectrum = FastFourierTransform.ForwardAnyLength(chroma);
        int half = spectrum.Length / 2;
        double timeStep = 1.0 / sampleRateHz;
        double frequencyStep = 1.0 / (fftLength * timeStep);
        double minHz = Math.Max(0.0, nominalCarrierHz - (2.0 * lineFrequencyHz));
        double maxHz = Math.Min(sampleRateHz / 2.0, nominalCarrierHz + (2.0 * lineFrequencyHz));

        var power = new double[half + 1];
        double maximumPower = 0.0;
        for (int bin = 0; bin <= half; bin++)
        {
            power[bin] = (spectrum[bin].Real * spectrum[bin].Real) + (spectrum[bin].Imaginary * spectrum[bin].Imaginary);
            maximumPower = Math.Max(maximumPower, power[bin]);
        }

        double peakThreshold = maximumPower / 3.0;
        int peakBin = -1;
        double closestDistance = double.PositiveInfinity;
        for (int bin = 1; bin < half; bin++)
        {
            if (power[bin] <= peakThreshold
                || power[bin] <= power[bin - 1]
                || power[bin] <= power[bin + 1])
            {
                continue;
            }

            double frequency = bin * frequencyStep;
            double distance = Math.Abs(frequency - nominalCarrierHz);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                peakBin = bin;
            }
        }

        if (peakBin < 0)
        {
            return null;
        }

        double peakHz = peakBin * frequencyStep;
        double carrierHz = FineTuneCarrier(peakHz, nominalCarrierHz, fineTuneStepHz);
        carrierHz = Math.Clamp(carrierHz, minHz, maxHz);
        int phaseBin = Math.Clamp((int)Math.Round(carrierHz / frequencyStep), 1, half);
        double phaseBinFrequency = phaseBin * frequencyStep;
        double phase = phaseBinFrequency == carrierHz
            ? Math.Atan2(spectrum[phaseBin].Imaginary, spectrum[phaseBin].Real)
            : 0.0;
        return new ChromaCarrierEstimate(
            nominalCarrierHz,
            peakHz,
            carrierHz,
            carrierHz - nominalCarrierHz,
            phase);
    }

    private static double FineTuneCarrier(double peakHz, double nominalCarrierHz, double maxStepHz)
    {
        double tuned = peakHz;
        while (Math.Abs(tuned - nominalCarrierHz) >= maxStepHz)
        {
            tuned += tuned > nominalCarrierHz ? -maxStepHz : maxStepHz;
        }

        double more = tuned + maxStepHz;
        double less = tuned - maxStepHz;
        if (Math.Abs(tuned - nominalCarrierHz) < Math.Abs(less - nominalCarrierHz)
            && Math.Abs(tuned - nominalCarrierHz) < Math.Abs(more - nominalCarrierHz))
        {
            return tuned;
        }

        return Math.Abs(more - nominalCarrierHz) < Math.Abs(less - nominalCarrierHz) ? more : less;
    }

    public static ChromaBurstDemodulationResult DemodBurst(
        ReadOnlySpan<double> burst,
        double lineScale,
        int lineStart,
        int burstStart,
        ReadOnlySpan<double> burstSin,
        ReadOnlySpan<double> burstCos,
        bool useFloat32Samples = false)
    {
        if (burstStart < 0 || burstStart + burst.Length > burstSin.Length || burstStart + burst.Length > burstCos.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(burstStart), "Burst carrier tables must cover the requested burst range.");
        }

        if (useFloat32Samples)
        {
            return DemodBurstFloat32(
                burst,
                lineScale,
                lineStart,
                burstStart,
                burstSin,
                burstCos);
        }

        Span<double> iFirst = stackalloc double[4];
        Span<double> iSecond = stackalloc double[4];
        Span<double> qFirst = stackalloc double[4];
        Span<double> qSecond = stackalloc double[4];
        int vectorLength = burst.Length & ~7;
        for (int index = 0; index < vectorLength; index += 8)
        {
            for (int lane = 0; lane < 4; lane++)
            {
                int first = index + lane;
                int second = first + 4;
                double firstSample = burst[first];
                double secondSample = burst[second];
                iFirst[lane] = Math.FusedMultiplyAdd(
                    firstSample,
                    (float)burstCos[burstStart + first],
                    iFirst[lane]);
                iSecond[lane] = Math.FusedMultiplyAdd(
                    secondSample,
                    (float)burstCos[burstStart + second],
                    iSecond[lane]);
                qFirst[lane] = Math.FusedMultiplyAdd(
                    firstSample,
                    (float)burstSin[burstStart + first],
                    qFirst[lane]);
                qSecond[lane] = Math.FusedMultiplyAdd(
                    secondSample,
                    (float)burstSin[burstStart + second],
                    qSecond[lane]);
            }
        }

        double i0 = iSecond[0] + iFirst[0];
        double i1 = iSecond[1] + iFirst[1];
        double i2 = iSecond[2] + iFirst[2];
        double i3 = iSecond[3] + iFirst[3];
        double q0 = qSecond[0] + qFirst[0];
        double q1 = qSecond[1] + qFirst[1];
        double q2 = qSecond[2] + qFirst[2];
        double q3 = qSecond[3] + qFirst[3];
        double iComponent = (i0 + i2) + (i1 + i3);
        double qComponent = (q0 + q2) + (q1 + q3);
        for (int index = vectorLength; index < burst.Length; index++)
        {
            double sample = burst[index];
            iComponent = Math.FusedMultiplyAdd(sample, (float)burstCos[burstStart + index], iComponent);
            qComponent = Math.FusedMultiplyAdd(sample, (float)burstSin[burstStart + index], qComponent);
        }

        double phaseDegrees = PositiveDegrees(Math.Atan2(qComponent, iComponent) * (180.0 / Math.PI));
        double phaseOffsetDegrees = PositiveDegrees(
            (burstStart - lineStart) * Math.FusedMultiplyAdd(-lineScale, 90.0, 90.0));
        return new ChromaBurstDemodulationResult(
            phaseDegrees,
            phaseOffsetDegrees,
            NumpyHypot(iComponent, qComponent),
            iComponent,
            qComponent);
    }

    private static ChromaBurstDemodulationResult DemodBurstFloat32(
        ReadOnlySpan<double> burst,
        double lineScale,
        int lineStart,
        int burstStart,
        ReadOnlySpan<double> burstSin,
        ReadOnlySpan<double> burstCos)
    {
        Span<double> i0 = stackalloc double[4];
        Span<double> i1 = stackalloc double[4];
        Span<double> i2 = stackalloc double[4];
        Span<double> i3 = stackalloc double[4];
        Span<double> q0 = stackalloc double[4];
        Span<double> q1 = stackalloc double[4];
        Span<double> q2 = stackalloc double[4];
        Span<double> q3 = stackalloc double[4];
        int mainEnd = burst.Length & ~15;
        for (int index = 0; index < mainEnd; index += 16)
        {
            for (int lane = 0; lane < 4; lane++)
            {
                AccumulateFloat32Burst(burst, burstSin, burstCos, burstStart, index + lane, ref i0[lane], ref q0[lane]);
                AccumulateFloat32Burst(burst, burstSin, burstCos, burstStart, index + lane + 4, ref i1[lane], ref q1[lane]);
                AccumulateFloat32Burst(burst, burstSin, burstCos, burstStart, index + lane + 8, ref i2[lane], ref q2[lane]);
                AccumulateFloat32Burst(burst, burstSin, burstCos, burstStart, index + lane + 12, ref i3[lane], ref q3[lane]);
            }
        }

        double iComponent = HorizontalSum(i0, i1, i2, i3);
        double qComponent = HorizontalSum(q0, q1, q2, q3);
        int epilogueEnd = burst.Length & ~3;
        if (epilogueEnd > mainEnd)
        {
            Span<double> epilogueI = stackalloc double[4];
            Span<double> epilogueQ = stackalloc double[4];
            epilogueI[0] = iComponent;
            epilogueQ[0] = qComponent;
            for (int index = mainEnd; index < epilogueEnd; index += 4)
            {
                for (int lane = 0; lane < 4; lane++)
                {
                    AccumulateFloat32Burst(
                        burst,
                        burstSin,
                        burstCos,
                        burstStart,
                        index + lane,
                        ref epilogueI[lane],
                        ref epilogueQ[lane]);
                }
            }

            iComponent = (epilogueI[0] + epilogueI[2]) + (epilogueI[1] + epilogueI[3]);
            qComponent = (epilogueQ[0] + epilogueQ[2]) + (epilogueQ[1] + epilogueQ[3]);
        }

        for (int index = epilogueEnd; index < burst.Length; index++)
        {
            AccumulateFloat32Burst(
                burst,
                burstSin,
                burstCos,
                burstStart,
                index,
                ref iComponent,
                ref qComponent);
        }

        double phaseDegrees = PositiveDegrees(Math.Atan2(qComponent, iComponent) * (180.0 / Math.PI));
        double phaseOffsetDegrees = PositiveDegrees(
            (burstStart - lineStart) * Math.FusedMultiplyAdd(-lineScale, 90.0, 90.0));
        return new ChromaBurstDemodulationResult(
            phaseDegrees,
            phaseOffsetDegrees,
            NumpyHypot(iComponent, qComponent),
            iComponent,
            qComponent);

        static double HorizontalSum(
            ReadOnlySpan<double> group0,
            ReadOnlySpan<double> group1,
            ReadOnlySpan<double> group2,
            ReadOnlySpan<double> group3)
        {
            double lane0 = ((group1[0] + group0[0]) + group2[0]) + group3[0];
            double lane1 = ((group1[1] + group0[1]) + group2[1]) + group3[1];
            double lane2 = ((group1[2] + group0[2]) + group2[2]) + group3[2];
            double lane3 = ((group1[3] + group0[3]) + group2[3]) + group3[3];
            return (lane0 + lane2) + (lane1 + lane3);
        }
    }

    private static void AccumulateFloat32Burst(
        ReadOnlySpan<double> burst,
        ReadOnlySpan<double> burstSin,
        ReadOnlySpan<double> burstCos,
        int burstStart,
        int index,
        ref double iAccumulator,
        ref double qAccumulator)
    {
        float sample = (float)burst[index];
        float iProduct = sample * (float)burstCos[burstStart + index];
        float qProduct = sample * (float)burstSin[burstStart + index];
        iAccumulator += iProduct;
        qAccumulator += qProduct;
    }

    public static ChromaBurstDemodulationResult ProbeUpconvertedBurst(
        ReadOnlySpan<double> chroma,
        IReadOnlyList<double[]> chromaHeterodyne,
        int phaseRotation,
        int burstStart,
        int burstEnd,
        ReadOnlySpan<double> burstSin,
        ReadOnlySpan<double> burstCos,
        double lineScale,
        int lineNumber,
        int lineOffset,
        int lineLength,
        Func<double[], double[]>? burstFilter = null,
        bool useFloat32Samples = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ValidateBurstRange(burstStart, burstEnd, lineLength);
        (int lineStart, _) = GetLineRange(chroma.Length, lineOffset, lineLength, lineNumber);
        if (phaseRotation < 0 || phaseRotation >= chromaHeterodyne.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(phaseRotation));
        }

        double[] heterodyne = chromaHeterodyne[phaseRotation];
        if (heterodyne.Length < chroma.Length)
        {
            throw new ArgumentException("Chroma heterodyne table is shorter than the chroma field.", nameof(chromaHeterodyne));
        }

        int burstPadding = burstEnd - burstStart;
        int paddedStart = Math.Max(0, lineStart + burstStart - burstPadding);
        int paddedEnd = Math.Min(chroma.Length, paddedStart + burstEnd + burstPadding);
        var paddedBurst = new double[paddedEnd - paddedStart];
        for (int i = 0; i < paddedBurst.Length; i++)
        {
            int sourceIndex = paddedStart + i;
            float heterodyneSample = (float)heterodyne[sourceIndex];
            paddedBurst[i] = useFloat32Samples
                ? (float)(heterodyneSample * (float)chroma[sourceIndex])
                : heterodyneSample * chroma[sourceIndex];
        }

        double[] filteredPadded = burstFilter?.Invoke(paddedBurst) ?? paddedBurst;
        int filteredStart = Math.Min(burstPadding, filteredPadded.Length);
        int filteredEnd = Math.Max(filteredStart, filteredPadded.Length - burstPadding);
        return DemodBurst(
            filteredPadded.AsSpan(filteredStart, filteredEnd - filteredStart),
            lineScale,
            lineStart,
            paddedStart + burstPadding,
            burstSin,
            burstCos,
            useFloat32Samples);
    }

    internal static ChromaBurstDemodulationResult ProbeUpconvertedBurstCurrent(
        ReadOnlySpan<double> chroma,
        IReadOnlyList<double[]> chromaHeterodyne,
        int phaseRotation,
        int burstStart,
        int burstEnd,
        ReadOnlySpan<double> burstSin,
        ReadOnlySpan<double> burstCos,
        int lineNumber,
        int lineOffset,
        int lineLength,
        double fscHz,
        Func<double[], double[]>? burstFilter = null,
        bool useFloat32Samples = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fscHz);
        ValidateBurstRange(burstStart, burstEnd, lineLength);
        (int lineStart, _) = GetLineRange(
            chroma.Length,
            lineOffset,
            lineLength,
            lineNumber);
        if (phaseRotation < 0 || phaseRotation >= chromaHeterodyne.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(phaseRotation));
        }

        double[] heterodyne = chromaHeterodyne[phaseRotation];
        if (heterodyne.Length < chroma.Length)
        {
            throw new ArgumentException(
                "Chroma heterodyne table is shorter than the chroma field.",
                nameof(chromaHeterodyne));
        }

        int burstPadding = burstStart;
        int paddedStart = Math.Max(0, lineStart + burstStart - burstPadding);
        int paddedEnd = Math.Min(
            chroma.Length,
            lineStart + burstEnd + burstPadding);
        var paddedBurst = new double[Math.Max(0, paddedEnd - paddedStart)];
        for (int index = 0; index < paddedBurst.Length; index++)
        {
            int sourceIndex = paddedStart + index;
            float heterodyneSample = (float)heterodyne[sourceIndex];
            paddedBurst[index] = useFloat32Samples
                ? (float)(heterodyneSample * (float)chroma[sourceIndex])
                : heterodyneSample * chroma[sourceIndex];
        }

        double[] filteredPadded = burstFilter?.Invoke(paddedBurst) ?? paddedBurst;
        int filteredStart = Math.Min(burstPadding, filteredPadded.Length);
        int filteredEnd = Math.Max(
            filteredStart,
            filteredPadded.Length - burstPadding);
        int globalBurstStart = checked(paddedStart + burstPadding);
        CurrentChromaBurstFit fit = CurrentChromaBurstFitter.Fit(
            filteredPadded.AsSpan(
                filteredStart,
                filteredEnd - filteredStart),
            globalBurstStart,
            burstSin,
            burstCos,
            fscHz);
        return new ChromaBurstDemodulationResult(
            fit.PhaseDegrees,
            PhaseOffsetDegrees: 0.0,
            fit.Magnitude,
            fit.I,
            fit.Q)
        {
            Start = paddedStart,
            End = paddedEnd,
            Center = fit.Center,
            Amplitude = fit.Amplitude,
            Dc = fit.Dc,
            FrequencyHz = fit.FrequencyHz
        };
    }

    public static ChromaPhaseSequenceResult GetPhaseRotationSequence(
        IReadOnlyList<int>? chromaRotation,
        int? chromaRotationIndex,
        IReadOnlyList<double> lineLocations,
        int lineOffset,
        int linesOut,
        int inputLineLength,
        ChromaBurstProbe burstProbe,
        bool detectChromaTrackPhase,
        int rotationCheckStartLine,
        bool enableColorKiller,
        int prevBurstDetectedLine,
        string colorSystem)
        => GetPhaseRotationSequence(
            chromaRotation,
            chromaRotationIndex,
            lineLocations,
            lineOffset,
            linesOut,
            inputLineLength,
            burstProbe,
            detectChromaTrackPhase,
            rotationCheckStartLine,
            enableColorKiller,
            prevBurstDetectedLine,
            colorSystem,
            workerThreads: 1);

    internal static ChromaPhaseSequenceResult GetPhaseRotationSequence(
        IReadOnlyList<int>? chromaRotation,
        int? chromaRotationIndex,
        IReadOnlyList<double> lineLocations,
        int lineOffset,
        int linesOut,
        int inputLineLength,
        ChromaBurstProbe burstProbe,
        bool detectChromaTrackPhase,
        int rotationCheckStartLine,
        bool enableColorKiller,
        int prevBurstDetectedLine,
        string colorSystem,
        int workerThreads)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(linesOut);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputLineLength);
        ArgumentNullException.ThrowIfNull(burstProbe);

        int end = checked(linesOut + lineOffset);
        if (lineLocations.Count <= end)
        {
            throw new ArgumentException("Line locations must include one entry past the last chroma output line.", nameof(lineLocations));
        }

        (int nextRotationIndex, ChromaPhaseLine[] phaseSequence) = BuildPhaseSequence(
            chromaRotation,
            chromaRotationIndex,
            lineLocations,
            lineOffset,
            inputLineLength,
            end,
            burstProbe,
            detectChromaTrackPhase,
            rotationCheckStartLine,
            colorSystem,
            workerThreads);

        if (ShouldFlipTrackPhase(chromaRotation, phaseSequence, end, colorSystem))
        {
            (nextRotationIndex, phaseSequence) = BuildPhaseSequence(
                chromaRotation,
                nextRotationIndex,
                lineLocations,
                lineOffset,
                inputLineLength,
                end,
                burstProbe,
                detectChromaTrackPhase,
                rotationCheckStartLine,
                colorSystem,
                workerThreads);
        }

        return SummarizePhaseSequence(
            nextRotationIndex,
            phaseSequence,
            end,
            enableColorKiller,
            prevBurstDetectedLine);
    }

    public static double[][] BuildHeterodyneTable(
        int sampleCount,
        double fscMHz,
        double colorUnderCarrierMHz,
        double outputSampleRateMHz,
        double phaseDriftRadians = 0.0,
        int workerThreads = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRateMHz);

        double hetWaveScale = (fscMHz + colorUnderCarrierMHz) / outputSampleRateMHz;
        var table = new double[4][];
        void BuildPhase(int phase)
        {
            double phaseOffset = (Math.PI / 2.0 * phase) + phaseDriftRadians;
            table[phase] = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                table[phase][i] = (double)(float)-Math.Cos((Math.Tau * hetWaveScale * i) + phaseOffset);
            }
        }

        if (workerThreads > 1 && sampleCount >= ParallelSampleThreshold)
        {
            Parallel.For(
                0,
                table.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(workerThreads, table.Length) },
                BuildPhase);
        }
        else
        {
            for (int phase = 0; phase < table.Length; phase++)
            {
                BuildPhase(phase);
            }
        }

        return table;
    }

    public static double[] UpconvertChroma(
        ReadOnlySpan<double> chroma,
        int lineOffset,
        int lineLength,
        IReadOnlyList<ChromaPhaseLine> phaseRotationSequence,
        IReadOnlyList<double[]> chromaHeterodyne)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);

        double[] output = new double[chroma.Length];
        foreach (ChromaPhaseLine phaseLine in phaseRotationSequence)
        {
            (int lineStart, int lineEnd) = GetNumpySliceRange(
                chroma.Length,
                lineOffset,
                lineLength,
                phaseLine.LineNumber);
            if (phaseLine.PhaseRotation < 0 || phaseLine.PhaseRotation >= chromaHeterodyne.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(phaseRotationSequence), "Chroma phase rotation index has no heterodyne table.");
            }

            double[] heterodyne = chromaHeterodyne[phaseLine.PhaseRotation];
            if (heterodyne.Length < chroma.Length)
            {
                throw new ArgumentException("Chroma heterodyne table is shorter than the chroma field.", nameof(chromaHeterodyne));
            }

            for (int i = lineStart; i < lineEnd; i++)
            {
                output[i] = (double)(float)(chroma[i] * heterodyne[i]);
            }
        }

        return output;
    }

    internal static bool TryUpconvertChromaInPlace(
        double[] chroma,
        int lineOffset,
        int lineLength,
        IReadOnlyList<ChromaPhaseLine> phaseRotationSequence,
        IReadOnlyList<double[]> chromaHeterodyne)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        ArgumentNullException.ThrowIfNull(phaseRotationSequence);
        ArgumentNullException.ThrowIfNull(chromaHeterodyne);
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);

        int wrapIndex = -1;
        int firstRangeStart = 0;
        int previousEnd = 0;
        bool hasNonEmptyRange = false;
        for (int phaseIndex = 0; phaseIndex < phaseRotationSequence.Count; phaseIndex++)
        {
            ChromaPhaseLine phaseLine = phaseRotationSequence[phaseIndex];
            (int lineStart, int lineEnd) = GetNumpySliceRange(
                chroma.Length,
                lineOffset,
                lineLength,
                phaseLine.LineNumber);
            if (phaseLine.PhaseRotation < 0
                || phaseLine.PhaseRotation >= chromaHeterodyne.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(phaseRotationSequence),
                    "Chroma phase rotation index has no heterodyne table.");
            }

            double[] heterodyne = chromaHeterodyne[phaseLine.PhaseRotation];
            if (heterodyne.Length < chroma.Length)
            {
                throw new ArgumentException(
                    "Chroma heterodyne table is shorter than the chroma field.",
                    nameof(chromaHeterodyne));
            }

            if (ReferenceEquals(heterodyne, chroma))
            {
                return false;
            }

            if (lineEnd == lineStart)
            {
                continue;
            }

            if (!hasNonEmptyRange)
            {
                firstRangeStart = lineStart;
                hasNonEmptyRange = true;
            }
            else if (lineStart < previousEnd)
            {
                if (wrapIndex >= 0 || lineEnd > firstRangeStart)
                {
                    return false;
                }

                wrapIndex = phaseIndex;
            }

            if (wrapIndex >= 0 && lineEnd > firstRangeStart)
            {
                return false;
            }

            previousEnd = lineEnd;
        }

        foreach (ChromaPhaseLine phaseLine in phaseRotationSequence)
        {
            (int lineStart, int lineEnd) = GetNumpySliceRange(
                chroma.Length,
                lineOffset,
                lineLength,
                phaseLine.LineNumber);
            double[] heterodyne = chromaHeterodyne[phaseLine.PhaseRotation];
            for (int index = lineStart; index < lineEnd; index++)
            {
                chroma[index] = (double)(float)(chroma[index] * heterodyne[index]);
            }
        }

        int clearCursor = 0;
        int sortedStart = wrapIndex < 0 ? 0 : wrapIndex;
        ClearGaps(sortedStart, phaseRotationSequence.Count, ref clearCursor);
        if (wrapIndex >= 0)
        {
            ClearGaps(0, wrapIndex, ref clearCursor);
        }

        chroma.AsSpan(clearCursor).Clear();
        return true;

        void ClearGaps(int startIndex, int endIndex, ref int cursor)
        {
            for (int phaseIndex = startIndex; phaseIndex < endIndex; phaseIndex++)
            {
                ChromaPhaseLine phaseLine = phaseRotationSequence[phaseIndex];
                (int lineStart, int lineEnd) = GetNumpySliceRange(
                    chroma.Length,
                    lineOffset,
                    lineLength,
                    phaseLine.LineNumber);
                if (lineEnd == lineStart)
                {
                    continue;
                }

                chroma.AsSpan(cursor, lineStart - cursor).Clear();
                cursor = lineEnd;
            }
        }
    }

    public static double[] UpconvertChromaPhaseCompensated(
        ReadOnlySpan<double> chroma,
        int lineOffset,
        int lineLength,
        IReadOnlyList<ChromaPhaseLine> phaseRotationSequence,
        double colorUnderCarrierHz,
        double fscMHz,
        double targetPhaseEvenDegrees,
        double targetPhaseOddDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fscMHz);

        const double HalfPi = 1.5707963267948966;
        const double DegreesToRadians = 0.017453292519943295;
        const double HalfPiPerMegahertz = 1.5707963267948965e-6;

        double[] output = new double[chroma.Length];
        double carrierTerm = colorUnderCarrierHz * HalfPiPerMegahertz;
        double carrierRatio = carrierTerm / fscMHz;
        double hetCoefficient = carrierRatio + HalfPi;
        double twiceCoefficient = hetCoefficient * 2.0;
        double thriceCoefficient = hetCoefficient * 3.0;
        double fourTimesCoefficient = hetCoefficient * 4.0;

        foreach (ChromaPhaseLine phaseLine in phaseRotationSequence)
        {
            int lineStart = checked((phaseLine.LineNumber - lineOffset) * lineLength);
            int lineEnd = checked(lineStart + lineLength);
            double targetPhaseDegrees = phaseLine.LineNumber % 2 == 0
                ? targetPhaseEvenDegrees
                : targetPhaseOddDegrees;
            double phaseTerm = phaseLine.PhaseRotation * HalfPi;
            double theta = Math.FusedMultiplyAdd(lineStart, hetCoefficient, phaseTerm);
            theta = Math.FusedMultiplyAdd(
                targetPhaseDegrees + phaseLine.BurstPhaseDegrees,
                DegreesToRadians,
                theta);

            if (lineStart < 0 || lineEnd > chroma.Length)
            {
                for (int edgeIndex = lineStart; edgeIndex < lineEnd; edgeIndex++)
                {
                    int sampleIndex = NormalizeNumpyIndex(edgeIndex, chroma.Length);
                    output[sampleIndex] = (double)(float)(chroma[sampleIndex] * -Math.Cos(theta));
                    theta += hetCoefficient;
                }

                continue;
            }

            // Numba/LLVM vectorizes the reflected phase-list specialization in four lanes.
            int vectorCount = (lineEnd - lineStart) & ~3;
            int vectorEnd = lineStart + vectorCount;
            double theta0 = theta + (hetCoefficient * 0.0);
            double theta1 = theta + hetCoefficient;
            double theta2 = theta + twiceCoefficient;
            double theta3 = theta + thriceCoefficient;
            int i = lineStart;
            for (; i < vectorEnd; i += 4)
            {
                output[i] = (double)(float)(chroma[i] * -Math.Cos(theta0));
                output[i + 1] = (double)(float)(chroma[i + 1] * -Math.Cos(theta1));
                output[i + 2] = (double)(float)(chroma[i + 2] * -Math.Cos(theta2));
                output[i + 3] = (double)(float)(chroma[i + 3] * -Math.Cos(theta3));
                theta0 += fourTimesCoefficient;
                theta1 += fourTimesCoefficient;
                theta2 += fourTimesCoefficient;
                theta3 += fourTimesCoefficient;
            }

            theta += hetCoefficient * vectorCount;
            for (; i < lineEnd; i++)
            {
                output[i] = (double)(float)(chroma[i] * -Math.Cos(theta));
                theta += hetCoefficient;
            }
        }

        return output;
    }

    public static void UpconvertChromaPhaseCompensatedCurrentInPlace(
        Span<double> chroma,
        int lineOffset,
        int lineLength,
        IReadOnlyList<ChromaPhaseLine> phaseRotationSequence,
        double colorUnderCarrierHz,
        double fscHz,
        double targetPhaseEvenDegrees,
        double targetPhaseOddDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fscHz);
        ArgumentNullException.ThrowIfNull(phaseRotationSequence);

        double degreesToRadians = Math.PI / 180.0;
        double piOverTwo = Math.PI / 2.0;
        double nominalCoefficient =
            piOverTwo * (1.0 + (colorUnderCarrierHz / fscHz));
        double targetPhaseEvenRadians =
            targetPhaseEvenDegrees * degreesToRadians;
        double targetPhaseOddRadians =
            targetPhaseOddDegrees * degreesToRadians;
        double coefficientStepFactor =
            piOverTwo / (fscHz * lineLength);

        for (int phaseIndex = 0;
            phaseIndex < phaseRotationSequence.Count;
            phaseIndex++)
        {
            ChromaPhaseLine currentBurst =
                phaseRotationSequence[phaseIndex];
            double currentFrequencyRatio =
                currentBurst.BurstFrequencyHz / fscHz;
            double currentHeterodyneHz =
                currentFrequencyRatio * colorUnderCarrierHz;
            double nextHeterodyneHz;
            if (phaseIndex < phaseRotationSequence.Count - 1)
            {
                ChromaPhaseLine nextBurst =
                    phaseRotationSequence[phaseIndex + 1];
                double nextFrequencyRatio =
                    nextBurst.BurstFrequencyHz / fscHz;
                nextHeterodyneHz =
                    nextFrequencyRatio * colorUnderCarrierHz;
            }
            else
            {
                nextHeterodyneHz = currentHeterodyneHz;
            }

            int lineStart = checked(
                (currentBurst.LineNumber - lineOffset) * lineLength);
            int lineEnd = checked(lineStart + lineLength);
            if (lineStart < 0 || lineEnd > chroma.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(phaseRotationSequence),
                    "Current chroma phase line exceeds the output field.");
            }

            double targetPhaseRadians =
                (currentBurst.LineNumber & 1) != 0
                    ? targetPhaseOddRadians
                    : targetPhaseEvenRadians;
            double thetaZero =
                (nominalCoefficient * lineStart)
                + ((currentBurst.PhaseRotation * piOverTwo)
                    + targetPhaseRadians
                    + (currentBurst.BurstPhaseDegrees
                        * degreesToRadians));
            double alpha =
                piOverTwo * (1.0 + (currentHeterodyneHz / fscHz));
            double deltaCoefficient =
                (nextHeterodyneHz - currentHeterodyneHz)
                * coefficientStepFactor;
            double beta = 0.5 * deltaCoefficient;
            double dc = currentBurst.BurstDc;
            for (int sample = 0; sample < lineLength; sample++)
            {
                double localIndex = sample;
                double theta =
                    (thetaZero + (alpha * localIndex))
                    + (beta * (localIndex * localIndex));
                int outputIndex = lineStart + sample;
                chroma[outputIndex] = (float)(
                    ((float)chroma[outputIndex] * -Math.Cos(theta))
                    - dc);
            }
        }
    }

    public static double[] RefineLineLocationsFromBurst(
        IReadOnlyList<double> lineLocations,
        int outputLineLength,
        double fscRatio,
        ChromaPhaseSequenceResult phase,
        string colorSystem,
        bool useCurrentFrequencyDrift = false,
        double fscHz = 0.0)
    {
        ArgumentNullException.ThrowIfNull(lineLocations);
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLineLength);
        if (!double.IsFinite(fscRatio) || fscRatio <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(fscRatio));
        }
        if (useCurrentFrequencyDrift
            && (!double.IsFinite(fscHz) || fscHz <= 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(fscHz));
        }

        var output = new double[lineLocations.Count];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = lineLocations[i];
        }

        if (phase.BurstDetectedLine == -1)
        {
            return output;
        }

        bool isNtsc = IsNtsc(colorSystem);
        int burstTbcStart = Math.Max(9, phase.BurstDetectedLine);
        double inverseOutputLineLength = 1.0 / outputLineLength;
        double inverseFsc = useCurrentFrequencyDrift
            ? 1.0 / fscHz
            : 0.0;
        double phaseToSamplesFactor = useCurrentFrequencyDrift
            ? fscRatio / 360.0
            : 0.0;
        for (int phaseIndex = burstTbcStart; phaseIndex < phase.PhaseSequence.Length; phaseIndex++)
        {
            ChromaPhaseLine phaseLine = phase.PhaseSequence[phaseIndex];
            int lineNumber = phaseLine.LineNumber;
            if (lineNumber < 0 || lineNumber + 1 >= output.Length)
            {
                continue;
            }

            double targetPhase = isNtsc
                ? phase.BurstPhaseAverageDegrees
                : (lineNumber & 1) == 1
                    ? phase.OddBurstPhaseAverageDegrees
                    : phase.EvenBurstPhaseAverageDegrees;
            double phaseOffset = useCurrentFrequencyDrift && isNtsc
                ? 0.0
                : phaseLine.BurstPhaseOffsetDegrees;
            double phaseDelta = PositiveDegrees(
                targetPhase - phaseLine.BurstPhaseDegrees + phaseOffset + 180.0) - 180.0;
            double lineLength = output[lineNumber + 1] - output[lineNumber];
            if (useCurrentFrequencyDrift)
            {
                double scale = lineLength * inverseOutputLineLength;
                double lineAdjustment =
                    phaseDelta * phaseToSamplesFactor;
                double frequencyOffset =
                    phaseLine.BurstFrequencyHz - fscHz;
                double burstCenterDistance =
                    Math.Truncate(phaseLine.BurstCenter) - output[lineNumber];
                double accumulatedDriftSamples =
                    (frequencyOffset * burstCenterDistance) * inverseFsc;
                double correctedAdjustment =
                    lineAdjustment - accumulatedDriftSamples;
                output[lineNumber] += correctedAdjustment * scale;
            }
            else
            {
                double scale = lineLength / outputLineLength;
                output[lineNumber] +=
                    (phaseDelta / 360.0) * fscRatio * scale;
            }
        }

        return output;
    }

    public static double[] ShiftChromaAndRemoveDc(ReadOnlySpan<double> chroma, int move)
    {
        var output = new double[chroma.Length];
        if (chroma.Length == 0)
        {
            return output;
        }

        for (int i = 0; i < chroma.Length; i++)
        {
            output[PositiveModulo(i + move, chroma.Length)] = chroma[i];
        }

        double mean = NumbaReduction.MeanFloat64FastMath(output);
        for (int i = 0; i < output.Length; i++)
        {
            output[i] -= mean;
        }

        return output;
    }

    internal static double[] ShiftChromaAndRemoveDcInPlace(double[] chroma, int move)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        if (chroma.Length == 0)
        {
            return chroma;
        }

        FrequencyDomainFilter.RollInPlace(chroma, move);
        double mean = NumbaReduction.MeanFloat64FastMath(chroma);
        for (int i = 0; i < chroma.Length; i++)
        {
            chroma[i] -= mean;
        }

        return chroma;
    }

    public static double[] ShiftChromaAndRemoveDcFloat32(ReadOnlySpan<double> chroma, int move)
    {
        if (chroma.IsEmpty)
        {
            return [];
        }

        var rolled = new double[chroma.Length];
        int destinationOffset = PositiveModulo(move, chroma.Length);
        int firstCopyLength = chroma.Length - destinationOffset;
        chroma[..firstCopyLength].CopyTo(rolled.AsSpan(destinationOffset));
        if (destinationOffset != 0)
        {
            chroma[firstCopyLength..].CopyTo(rolled.AsSpan(0, destinationOffset));
        }

        RfBlockDecodePipeline.QuantizeToFloat32InPlace(rolled);

        float mean = MeanFloat32FastMath(rolled);
        for (int i = 0; i < rolled.Length; i++)
        {
            rolled[i] = (float)((float)rolled[i] - mean);
        }

        return rolled;
    }

    internal static double[] ShiftChromaAndRemoveDcFloat32InPlace(double[] chroma, int move)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        if (chroma.Length == 0)
        {
            return chroma;
        }

        FrequencyDomainFilter.RollInPlace(chroma, move);
        RfBlockDecodePipeline.QuantizeToFloat32InPlace(chroma);

        float mean = MeanFloat32FastMath(chroma);
        for (int i = 0; i < chroma.Length; i++)
        {
            chroma[i] = (float)((float)chroma[i] - mean);
        }

        return chroma;
    }

    internal static float[] ShiftChromaAndRemoveDcFloat32InPlace(float[] chroma, int move)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        if (chroma.Length == 0)
        {
            return chroma;
        }

        FrequencyDomainFilter.RollInPlace(chroma, move);
        float mean = MeanFloat32FastMath(chroma);
        for (int i = 0; i < chroma.Length; i++)
        {
            chroma[i] = (float)(chroma[i] - mean);
        }

        return chroma;
    }

    internal static double[] ShiftChromaAndRemoveDcFloat32CurrentInPlace(
        double[] chroma,
        int move)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        if (chroma.Length == 0)
        {
            return chroma;
        }

        RfBlockDecodePipeline.QuantizeToFloat32InPlace(chroma);
        int normalizedMove = PositiveModulo(move, chroma.Length);
        Span<float> wrapped = normalizedMove <= 256
            ? stackalloc float[normalizedMove]
            : new float[normalizedMove];
        int firstWrappedIndex = chroma.Length - normalizedMove;
        for (int i = 0; i < normalizedMove; i++)
        {
            wrapped[i] = (float)chroma[firstWrappedIndex + i];
        }

        double meanAccumulator = 0.0;
        for (int i = firstWrappedIndex - 1; i >= 0; i--)
        {
            meanAccumulator += chroma[i];
            chroma[i + normalizedMove] = chroma[i];
        }

        for (int i = 0; i < normalizedMove; i++)
        {
            meanAccumulator += wrapped[i];
            chroma[i] = wrapped[i];
        }

        meanAccumulator /= chroma.Length;
        for (int i = 0; i < chroma.Length; i++)
        {
            chroma[i] = (float)(chroma[i] - meanAccumulator);
        }

        return chroma;
    }

    internal static float[] ShiftChromaAndRemoveDcFloat32CurrentInPlace(
        float[] chroma,
        int move)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        if (chroma.Length == 0)
        {
            return chroma;
        }

        int normalizedMove = PositiveModulo(move, chroma.Length);
        Span<float> wrapped = normalizedMove <= 256
            ? stackalloc float[normalizedMove]
            : new float[normalizedMove];
        int firstWrappedIndex = chroma.Length - normalizedMove;
        chroma.AsSpan(firstWrappedIndex, normalizedMove).CopyTo(wrapped);

        double meanAccumulator = 0.0;
        for (int i = firstWrappedIndex - 1; i >= 0; i--)
        {
            meanAccumulator += chroma[i];
            chroma[i + normalizedMove] = chroma[i];
        }

        for (int i = 0; i < normalizedMove; i++)
        {
            meanAccumulator += wrapped[i];
            chroma[i] = wrapped[i];
        }

        meanAccumulator /= chroma.Length;
        for (int i = 0; i < chroma.Length; i++)
        {
            chroma[i] = (float)(chroma[i] - meanAccumulator);
        }

        return chroma;
    }

    public static double[] ApplyNtscComb(
        ReadOnlySpan<double> chroma,
        int lineLength,
        bool retainFloat32 = true)
    {
        double[] output = chroma.ToArray();
        ApplyCombInPlace(output, lineLength, lineDistance: 1, retainFloat32);
        return output;
    }

    public static double[] ApplyPalComb(
        ReadOnlySpan<double> chroma,
        int lineLength,
        bool retainFloat32 = true)
    {
        double[] output = chroma.ToArray();
        ApplyCombInPlace(output, lineLength, lineDistance: 2, retainFloat32);
        return output;
    }

    internal static void ApplyNtscCombInPlace(
        Span<double> chroma,
        int lineLength,
        bool retainFloat32 = true)
        => ApplyCombInPlace(chroma, lineLength, lineDistance: 1, retainFloat32);

    internal static void ApplyPalCombInPlace(
        Span<double> chroma,
        int lineLength,
        bool retainFloat32 = true)
        => ApplyCombInPlace(chroma, lineLength, lineDistance: 2, retainFloat32);

    public static double[] ApplyBurstDeemphasis(
        ReadOnlySpan<double> chroma,
        int lineOffset,
        int linesOut,
        int lineLength,
        int burstStart,
        int burstEnd,
        int samplesAfterBurst = 5)
    {
        ValidateLineShape(chroma.Length, linesOut, lineLength);
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(samplesAfterBurst);
        ValidateBurstRange(burstStart, burstEnd, lineLength);

        double[] output = chroma.ToArray();
        ApplyBurstDeemphasisCore(
            output,
            lineOffset,
            linesOut,
            lineLength,
            burstEnd,
            samplesAfterBurst);
        return output;
    }

    internal static void ApplyBurstDeemphasisInPlace(
        Span<double> chroma,
        int lineOffset,
        int linesOut,
        int lineLength,
        int burstStart,
        int burstEnd,
        int samplesAfterBurst = 5)
    {
        ValidateLineShape(chroma.Length, linesOut, lineLength);
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(samplesAfterBurst);
        ValidateBurstRange(burstStart, burstEnd, lineLength);
        ApplyBurstDeemphasisCore(
            chroma,
            lineOffset,
            linesOut,
            lineLength,
            burstEnd,
            samplesAfterBurst);
    }

    private static void ApplyBurstDeemphasisCore(
        Span<double> output,
        int lineOffset,
        int linesOut,
        int lineLength,
        int burstEnd,
        int samplesAfterBurst)
    {
        int firstDoubledSample = checked(burstEnd + samplesAfterBurst);
        if (firstDoubledSample >= lineLength)
        {
            return;
        }

        for (int line = lineOffset; line < linesOut + lineOffset; line++)
        {
            int lineStart = (line - lineOffset) * lineLength;
            int lineEnd = lineStart + lineLength;
            for (int i = lineStart + firstDoubledSample; i < lineEnd; i++)
            {
                output[i] *= 2.0;
            }
        }
    }

    public static AutomaticChromaGainResult ApplyAutomaticChromaGain(
        ReadOnlySpan<double> chroma,
        double burstAbsRef,
        int burstStart,
        int burstEnd,
        int lineLength,
        int lines,
        int burstDetectedLine,
        bool useFloat32Rms = true)
    {
        if (lines <= StartingLine)
        {
            throw new ArgumentOutOfRangeException(nameof(lines), "Chroma ACC requires more than 16 lines.");
        }

        ValidateLineShape(chroma.Length, lines, lineLength);
        ValidateBurstRange(burstStart, burstEnd, lineLength);

        double[] output = new double[chroma.Length];
        double meanBurstAccumulator = 0.0;
        for (int line = StartingLine; line < lines; line++)
        {
            int lineStart = line * lineLength;
            if (line < burstDetectedLine)
            {
                continue;
            }

            ReadOnlySpan<double> burst = chroma.Slice(
                lineStart + burstStart,
                burstEnd - burstStart);
            double rms = useFloat32Rms ? RmsFloat32(burst) : Rms(burst);
            double scale = rms != 0.0 ? burstAbsRef / rms : 1.0;
            for (int i = 0; i < lineLength; i++)
            {
                output[lineStart + i] = chroma[lineStart + i] * scale;
            }

            meanBurstAccumulator += rms;
        }

        return new AutomaticChromaGainResult(output, meanBurstAccumulator / (lines - StartingLine));
    }

    public static CurrentAutomaticChromaGainResult ApplyCurrentAutomaticChromaGainInPlace(
        Span<double> chroma,
        double burstAbsRef,
        IReadOnlyList<ChromaPhaseLine> phaseSequence,
        int burstDetectedLine,
        int syncTipLength,
        int smoothingWindow = 8,
        double madScale = 2.0)
    {
        ArgumentNullException.ThrowIfNull(phaseSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(smoothingWindow);
        int burstCount = phaseSequence.Count;
        if (burstCount == 0)
        {
            return new CurrentAutomaticChromaGainResult(0.0, 0.0);
        }

        int scratchLength = Math.Max(burstCount, Math.Max(0, syncTipLength));
        double[] rawGains = ArrayPool<double>.Shared.Rent(burstCount);
        double[] validGains = ArrayPool<double>.Shared.Rent(burstCount);
        double[] validAmplitudes = ArrayPool<double>.Shared.Rent(burstCount);
        double[] smoothedGains = ArrayPool<double>.Shared.Rent(burstCount);
        double[] medianScratch = ArrayPool<double>.Shared.Rent(
            Math.Max(1, scratchLength));
        double[] deviationScratch = ArrayPool<double>.Shared.Rent(
            Math.Max(1, scratchLength));
        try
        {
            int validCount = 0;
            for (int index = 0; index < burstCount; index++)
            {
                ChromaPhaseLine burst = phaseSequence[index];
                double amplitude = burst.BurstAmplitude != 0.0
                    ? burst.BurstAmplitude
                    : 1e-8;
                double gain = burstAbsRef / amplitude;
                rawGains[index] = gain;
                if (burst.LineNumber >= burstDetectedLine)
                {
                    validGains[validCount] = gain;
                    validAmplitudes[validCount] = amplitude;
                    validCount++;
                }
            }

            double maximumGain;
            if (validCount > 0)
            {
                double medianGain = NumpyReduction.MedianFloat64(
                    validGains.AsSpan(0, validCount),
                    medianScratch);
                for (int index = 0; index < validCount; index++)
                {
                    deviationScratch[index] = Math.Abs(
                        validGains[index] - medianGain);
                }

                double medianAbsoluteDeviation = NumpyReduction.MedianFloat64(
                    deviationScratch.AsSpan(0, validCount),
                    medianScratch);
                maximumGain = medianGain + (madScale * medianAbsoluteDeviation);
            }
            else
            {
                maximumGain = 1.0;
            }

            for (int index = 0; index < burstCount; index++)
            {
                rawGains[index] = Math.Min(rawGains[index], maximumGain);
            }

            int halfWindow = smoothingWindow / 2;
            for (int index = 0; index < burstCount; index++)
            {
                int start = Math.Max(0, index - halfWindow);
                int end = Math.Min(burstCount, index + halfWindow + 1);
                smoothedGains[index] = NumbaReduction.SumFloat64FastMath(
                    rawGains.AsSpan(start, end - start)) / (end - start);
            }

            double noiseSum = 0.0;
            int noiseSamples = 0;
            for (int index = 0; index < burstCount; index++)
            {
                ChromaPhaseLine burst = phaseSequence[index];
                int currentStart = burst.BurstStart;
                int nextStart = index < burstCount - 1
                    ? phaseSequence[index + 1].BurstStart
                    : chroma.Length;
                (int segmentStart, int segmentEnd) = GetNumpySliceRange(
                    chroma.Length,
                    currentStart,
                    nextStart);
                if (burst.LineNumber < burstDetectedLine)
                {
                    chroma[segmentStart..segmentEnd].Clear();
                    continue;
                }

                double gainStart = smoothedGains[index];
                double gainEnd = index < burstCount - 1
                    ? smoothedGains[index + 1]
                    : gainStart;
                int length = nextStart - currentStart;
                if (length <= 0)
                {
                    continue;
                }

                double gainIncrement = (gainEnd - gainStart) / length;
                double gain = gainStart;
                for (int sample = segmentStart; sample < segmentEnd; sample++)
                {
                    chroma[sample] = (float)((float)chroma[sample] * gain);
                    gain += gainIncrement;
                }

                (int syncStart, int syncEnd) = GetNumpySliceRange(
                    chroma.Length,
                    nextStart + 4 - syncTipLength,
                    nextStart - 4);
                int syncLength = syncEnd - syncStart;
                for (int sample = 0; sample < syncLength; sample++)
                {
                    medianScratch[sample] = (float)chroma[syncStart + sample];
                }

                double median = NumbaReduction.MedianFloat32(
                    medianScratch.AsSpan(0, syncLength));
                for (int sample = 0; sample < syncLength; sample++)
                {
                    deviationScratch[sample] = Math.Abs(
                        medianScratch[sample] - median);
                }

                double medianAbsoluteDeviation = NumpyReduction.MedianFloat64(
                    deviationScratch.AsSpan(0, syncLength),
                    medianScratch);
                noiseSum = Math.FusedMultiplyAdd(
                    medianAbsoluteDeviation,
                    1.4826,
                    noiseSum);
                noiseSamples++;
            }

            double noiseFloor = noiseSamples > 0
                ? noiseSum / noiseSamples
                : 0.0;
            double meanAmplitude = validCount > 0
                ? NumbaReduction.MeanFloat64FastMath(
                    validAmplitudes.AsSpan(0, validCount))
                : 0.0;
            return new CurrentAutomaticChromaGainResult(
                meanAmplitude,
                noiseFloor);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rawGains);
            ArrayPool<double>.Shared.Return(validGains);
            ArrayPool<double>.Shared.Return(validAmplitudes);
            ArrayPool<double>.Shared.Return(smoothedGains);
            ArrayPool<double>.Shared.Return(medianScratch);
            ArrayPool<double>.Shared.Return(deviationScratch);
        }
    }

    internal static ushort[] ApplyAutomaticChromaGainToU16(
        ReadOnlySpan<double> chroma,
        double burstAbsRef,
        int burstStart,
        int burstEnd,
        int lineLength,
        int lines,
        int burstDetectedLine,
        bool useFloat32Rms)
        => ApplyAutomaticChromaGainToU16(
            chroma,
            burstAbsRef,
            burstStart,
            burstEnd,
            lineLength,
            lines,
            burstDetectedLine,
            useFloat32Rms,
            new ushort[chroma.Length]);

    internal static ushort[] ApplyAutomaticChromaGainToU16(
        ReadOnlySpan<double> chroma,
        double burstAbsRef,
        int burstStart,
        int burstEnd,
        int lineLength,
        int lines,
        int burstDetectedLine,
        bool useFloat32Rms,
        ushort[] output)
    {
        if (lines <= StartingLine)
        {
            throw new ArgumentOutOfRangeException(nameof(lines), "Chroma ACC requires more than 16 lines.");
        }

        ValidateLineShape(chroma.Length, lines, lineLength);
        ValidateBurstRange(burstStart, burstEnd, lineLength);
        ValidateOutputLength(output, chroma.Length);

        int firstProcessedLine = Math.Min(lines, Math.Max(StartingLine, burstDetectedLine));
        int configuredSampleCount = checked(lines * lineLength);
        InitializeAutomaticGainChromaU16(
            output,
            checked(firstProcessedLine * lineLength),
            configuredSampleCount);
        int vectorizedLength = chroma.Length & ~3;
        for (int line = firstProcessedLine; line < lines; line++)
        {
            int lineStart = line * lineLength;
            ReadOnlySpan<double> lineSamples = chroma.Slice(lineStart, lineLength);
            ReadOnlySpan<double> burst = lineSamples.Slice(burstStart, burstEnd - burstStart);
            double rms = useFloat32Rms ? RmsFloat32(burst) : Rms(burst);
            double scale = rms != 0.0 ? burstAbsRef / rms : 1.0;
            WriteScaledChromaToU16(lineSamples, output, lineStart, scale, vectorizedLength);
        }

        return output;
    }

    internal static AutomaticChromaGainResult ApplyAutomaticChromaGainWithComb(
        ReadOnlySpan<double> chroma,
        double burstAbsRef,
        int burstStart,
        int burstEnd,
        int lineLength,
        int lines,
        int burstDetectedLine,
        int lineDistance,
        bool retainFloat32,
        bool useFloat32Rms)
    {
        if (lines <= StartingLine)
        {
            throw new ArgumentOutOfRangeException(nameof(lines), "Chroma ACC requires more than 16 lines.");
        }

        if (lineDistance is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(lineDistance));
        }

        ValidateLineShape(chroma.Length, lines, lineLength);
        ValidateBurstRange(burstStart, burstEnd, lineLength);

        const int MaxStackLineLength = 4_096;
        Span<double> combinedLine = lineLength <= MaxStackLineLength
            ? stackalloc double[lineLength]
            : new double[lineLength];
        double[] output = new double[chroma.Length];
        double meanBurstAccumulator = 0.0;
        for (int line = StartingLine; line < lines; line++)
        {
            if (line < burstDetectedLine)
            {
                continue;
            }

            int lineStart = line * lineLength;
            double rms;
            if (line < lines - 2)
            {
                ReadOnlySpan<double> current = chroma.Slice(lineStart, lineLength);
                ReadOnlySpan<double> advanced = chroma.Slice(
                    (line + lineDistance) * lineLength,
                    lineLength);
                ReadOnlySpan<double> delayed = chroma.Slice(
                    (line - lineDistance) * lineLength,
                    lineLength);
                for (int i = 0; i < lineLength; i++)
                {
                    // Match Numba's PAL delayed-first and NTSC source-order subtraction.
                    double combined = !retainFloat32 && lineDistance == 2
                        ? ((current[i] * 2.0) - delayed[i] - advanced[i]) / 4.0
                        : ((current[i] * 2.0) - advanced[i] - delayed[i]) / 4.0;
                    combinedLine[i] = retainFloat32 ? (double)(float)combined : combined;
                }

                ReadOnlySpan<double> burst = combinedLine.Slice(
                    burstStart,
                    burstEnd - burstStart);
                rms = useFloat32Rms ? RmsFloat32(burst) : Rms(burst);
                double scale = rms != 0.0 ? burstAbsRef / rms : 1.0;
                for (int i = 0; i < lineLength; i++)
                {
                    output[lineStart + i] = combinedLine[i] * scale;
                }
            }
            else
            {
                ReadOnlySpan<double> lineSamples = chroma.Slice(lineStart, lineLength);
                ReadOnlySpan<double> burst = lineSamples.Slice(
                    burstStart,
                    burstEnd - burstStart);
                rms = useFloat32Rms ? RmsFloat32(burst) : Rms(burst);
                double scale = rms != 0.0 ? burstAbsRef / rms : 1.0;
                for (int i = 0; i < lineLength; i++)
                {
                    output[lineStart + i] = lineSamples[i] * scale;
                }
            }

            meanBurstAccumulator += rms;
        }

        return new AutomaticChromaGainResult(output, meanBurstAccumulator / (lines - StartingLine));
    }

    internal static ushort[] ApplyAutomaticChromaGainWithCombToU16(
        ReadOnlySpan<double> chroma,
        double burstAbsRef,
        int burstStart,
        int burstEnd,
        int lineLength,
        int lines,
        int burstDetectedLine,
        int lineDistance,
        bool retainFloat32,
        bool useFloat32Rms)
        => ApplyAutomaticChromaGainWithCombToU16(
            chroma,
            burstAbsRef,
            burstStart,
            burstEnd,
            lineLength,
            lines,
            burstDetectedLine,
            lineDistance,
            retainFloat32,
            useFloat32Rms,
            new ushort[chroma.Length]);

    internal static ushort[] ApplyAutomaticChromaGainWithCombToU16(
        ReadOnlySpan<double> chroma,
        double burstAbsRef,
        int burstStart,
        int burstEnd,
        int lineLength,
        int lines,
        int burstDetectedLine,
        int lineDistance,
        bool retainFloat32,
        bool useFloat32Rms,
        ushort[] output)
    {
        if (lines <= StartingLine)
        {
            throw new ArgumentOutOfRangeException(nameof(lines), "Chroma ACC requires more than 16 lines.");
        }

        if (lineDistance is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(lineDistance));
        }

        ValidateLineShape(chroma.Length, lines, lineLength);
        ValidateBurstRange(burstStart, burstEnd, lineLength);
        ValidateOutputLength(output, chroma.Length);

        const int MaxStackLineLength = 4_096;
        Span<double> combinedLine = lineLength <= MaxStackLineLength
            ? stackalloc double[lineLength]
            : new double[lineLength];
        int firstProcessedLine = Math.Min(lines, Math.Max(StartingLine, burstDetectedLine));
        int configuredSampleCount = checked(lines * lineLength);
        InitializeAutomaticGainChromaU16(
            output,
            checked(firstProcessedLine * lineLength),
            configuredSampleCount);
        int vectorizedLength = chroma.Length & ~3;
        for (int line = firstProcessedLine; line < lines; line++)
        {
            int lineStart = line * lineLength;
            double rms;
            if (line < lines - 2)
            {
                ReadOnlySpan<double> current = chroma.Slice(lineStart, lineLength);
                ReadOnlySpan<double> advanced = chroma.Slice(
                    (line + lineDistance) * lineLength,
                    lineLength);
                ReadOnlySpan<double> delayed = chroma.Slice(
                    (line - lineDistance) * lineLength,
                    lineLength);
                for (int i = 0; i < lineLength; i++)
                {
                    // Match Numba's PAL delayed-first and NTSC source-order subtraction.
                    double combined = !retainFloat32 && lineDistance == 2
                        ? ((current[i] * 2.0) - delayed[i] - advanced[i]) / 4.0
                        : ((current[i] * 2.0) - advanced[i] - delayed[i]) / 4.0;
                    combinedLine[i] = retainFloat32 ? (double)(float)combined : combined;
                }

                ReadOnlySpan<double> burst = combinedLine.Slice(
                    burstStart,
                    burstEnd - burstStart);
                rms = useFloat32Rms ? RmsFloat32(burst) : Rms(burst);
                double scale = rms != 0.0 ? burstAbsRef / rms : 1.0;
                WriteScaledChromaToU16(combinedLine, output, lineStart, scale, vectorizedLength);
            }
            else
            {
                ReadOnlySpan<double> lineSamples = chroma.Slice(lineStart, lineLength);
                ReadOnlySpan<double> burst = lineSamples.Slice(
                    burstStart,
                    burstEnd - burstStart);
                rms = useFloat32Rms ? RmsFloat32(burst) : Rms(burst);
                double scale = rms != 0.0 ? burstAbsRef / rms : 1.0;
                WriteScaledChromaToU16(lineSamples, output, lineStart, scale, vectorizedLength);
            }
        }

        return output;
    }

    private static void InitializeAutomaticGainChromaU16(
        Span<ushort> output,
        int firstProcessedSample,
        int configuredSampleCount)
    {
        output[..firstProcessedSample].Fill((ushort)S16AbsMax);
        output[configuredSampleCount..].Fill((ushort)S16AbsMax);
    }

    private static void ValidateOutputLength(ushort[] output, int expectedLength)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length != expectedLength)
        {
            throw new ArgumentException("Output length must match chroma length.", nameof(output));
        }
    }

    private static void WriteScaledChromaToU16(
        ReadOnlySpan<double> source,
        Span<ushort> output,
        int outputStart,
        double scale,
        int vectorizedLength)
    {
        Span<ushort> destination = output.Slice(outputStart, source.Length);
        int saturatingLength = Math.Clamp(vectorizedLength - outputStart, 0, source.Length);
        int index = 0;
        if (Avx2.IsSupported && Sse41.IsSupported)
        {
            int simdLength = saturatingLength & ~3;
            Vector256<double> scaleVector = Vector256.Create(scale);
            Vector256<double> offsetVector = Vector256.Create(S16AbsMax);
            Vector256<double> maximumVector = Vector256.Create((double)ushort.MaxValue);
            Vector256<long> exponentMask = Vector256.Create(0x7FF0_0000_0000_0000L);
            ref double sourceReference = ref MemoryMarshal.GetReference(source);
            ref ushort destinationReference = ref MemoryMarshal.GetReference(destination);
            for (; index < simdLength; index += 4)
            {
                Vector256<double> values = Vector256.LoadUnsafe(ref sourceReference, (nuint)index);
                Vector256<double> scaled = Avx.Multiply(values, scaleVector);
                Vector256<double> shifted = Avx.Add(scaled, offsetVector);
                // Ordered comparisons include infinities, so inspect exponent bits to match double.IsFinite.
                Vector256<long> exponents = Avx2.And(shifted.AsInt64(), exponentMask);
                Vector256<double> finiteMask = Avx2.CompareGreaterThan(exponentMask, exponents).AsDouble();
                shifted = Avx.And(shifted, finiteMask);
                shifted = Avx.Max(shifted, Vector256<double>.Zero);
                shifted = Avx.Min(shifted, maximumVector);
                Vector128<int> converted = Avx.ConvertToVector128Int32WithTruncation(shifted);
                Vector128<ushort> packed = Sse41.PackUnsignedSaturate(converted, Vector128<int>.Zero);
                packed.GetLower().StoreUnsafe(ref destinationReference, (nuint)index);
            }
        }

        for (; index < saturatingLength; index++)
        {
            double scaled = source[index] * scale;
            double shifted = scaled + S16AbsMax;
            destination[index] = !double.IsFinite(shifted) || shifted <= 0.0
                ? ushort.MinValue
                : shifted >= ushort.MaxValue
                    ? ushort.MaxValue
                    : (ushort)shifted;
        }

        for (; index < source.Length; index++)
        {
            double scaled = source[index] * scale;
            double shifted = scaled + S16AbsMax;
            destination[index] = !double.IsFinite(shifted) || shifted < long.MinValue || shifted > long.MaxValue
                ? ushort.MinValue
                : unchecked((ushort)(long)shifted);
        }
    }

    private static void ApplyCombInPlace(
        Span<double> chroma,
        int lineLength,
        int lineDistance,
        bool retainFloat32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);

        int lineCount = chroma.Length / lineLength;
        if (lineCount <= StartingLine + 2)
        {
            return;
        }

        int delayedLength = checked(lineDistance * lineLength);
        double[] rented = ArrayPool<double>.Shared.Rent(delayedLength);
        try
        {
            Span<double> delayedLines = rented.AsSpan(0, delayedLength);
            int firstDelayedStart = checked((StartingLine - lineDistance) * lineLength);
            chroma.Slice(firstDelayedStart, delayedLength).CopyTo(delayedLines);

            for (int line = StartingLine; line < lineCount - 2; line++)
            {
                int lineStart = line * lineLength;
                int advancedStart = (line + lineDistance) * lineLength;
                int delayedOffset = ((line - StartingLine) % lineDistance) * lineLength;
                Span<double> delayedLine = delayedLines.Slice(delayedOffset, lineLength);
                for (int i = 0; i < lineLength; i++)
                {
                    double current = chroma[lineStart + i];
                    double advanced = chroma[advancedStart + i];
                    double delayed = delayedLine[i];
                    // Numba lowers PAL's float64 2H expression with the delayed term first; NTSC 1H retains source order.
                    double combined = !retainFloat32 && lineDistance == 2
                        ? ((current * 2.0) - delayed - advanced) / 4.0
                        : ((current * 2.0) - advanced - delayed) / 4.0;
                    chroma[lineStart + i] = retainFloat32 ? (double)(float)combined : combined;
                    delayedLine[i] = current;
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static float MeanFloat32FastMath(ReadOnlySpan<double> values)
    {
        const int vectorWidth = 8;
        const int interleave = 4;
        const int stride = vectorWidth * interleave;
        Span<float> accumulators = stackalloc float[stride];
        for (int block = 0; block < values.Length; block += stride)
        {
            for (int group = 0; group < interleave; group++)
            {
                for (int lane = 0; lane < vectorWidth; lane++)
                {
                    int index = block + (group * vectorWidth) + lane;
                    if (index < values.Length)
                    {
                        int accumulator = (group * vectorWidth) + lane;
                        accumulators[accumulator] += (float)values[index];
                    }
                }
            }
        }

        Span<float> lanes = stackalloc float[vectorWidth];
        for (int lane = 0; lane < vectorWidth; lane++)
        {
            float left = accumulators[lane] + accumulators[vectorWidth + lane];
            float right = accumulators[(2 * vectorWidth) + lane]
                + accumulators[(3 * vectorWidth) + lane];
            lanes[lane] = left + right;
        }

        for (int count = vectorWidth; count > 1; count /= 2)
        {
            for (int lane = 0; lane < count / 2; lane++)
            {
                lanes[lane] += lanes[lane + (count / 2)];
            }
        }

        return lanes[0] / values.Length;
    }

    private static float MeanFloat32FastMath(ReadOnlySpan<float> values)
    {
        const int vectorWidth = 8;
        const int interleave = 4;
        const int stride = vectorWidth * interleave;
        Span<float> accumulators = stackalloc float[stride];
        for (int block = 0; block < values.Length; block += stride)
        {
            for (int group = 0; group < interleave; group++)
            {
                for (int lane = 0; lane < vectorWidth; lane++)
                {
                    int index = block + (group * vectorWidth) + lane;
                    if (index < values.Length)
                    {
                        int accumulator = (group * vectorWidth) + lane;
                        accumulators[accumulator] += values[index];
                    }
                }
            }
        }

        Span<float> lanes = stackalloc float[vectorWidth];
        for (int lane = 0; lane < vectorWidth; lane++)
        {
            float left = accumulators[lane] + accumulators[vectorWidth + lane];
            float right = accumulators[(2 * vectorWidth) + lane]
                + accumulators[(3 * vectorWidth) + lane];
            lanes[lane] = left + right;
        }

        for (int count = vectorWidth; count > 1; count /= 2)
        {
            for (int lane = 0; lane < count / 2; lane++)
            {
                lanes[lane] += lanes[lane + (count / 2)];
            }
        }

        return lanes[0] / values.Length;
    }

    internal static (double[] Sin, double[] Cos) BuildCarrierTables(
        int sampleCount,
        double carrierMHz,
        double outputSampleRateMHz,
        int workerThreads = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRateMHz);

        double waveScale = carrierMHz / outputSampleRateMHz;
        var sine = new double[sampleCount];
        var cosine = new double[sampleCount];
        void BuildRange(int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                float theta = (float)(Math.Tau * waveScale * i);
                sine[i] = NumpyTrigFloat32(theta, cosine: false);
                cosine[i] = NumpyTrigFloat32(theta, cosine: true);
            }
        }

        if (workerThreads > 1 && sampleCount >= ParallelSampleThreshold)
        {
            Parallel.ForEach(
                Partitioner.Create(0, sampleCount),
                new ParallelOptions { MaxDegreeOfParallelism = workerThreads },
                range => BuildRange(range.Item1, range.Item2));
        }
        else
        {
            BuildRange(0, sampleCount);
        }

        return (sine, cosine);
    }

    private static float NumpyTrigFloat32(float input, bool cosine)
    {
        if (float.IsNaN(input))
        {
            return float.NaN;
        }

        float maximumCodyWaite = cosine ? 71_476.0625f : 117_435.992f;
        if (MathF.Abs(input) > maximumCodyWaite)
        {
            return cosine ? MathF.Cos(input) : MathF.Sin(input);
        }

        float twoOverPi = BitConverter.Int32BitsToSingle(unchecked((int)0x3F22F983));
        float quadrant = input * twoOverPi;
        const float roundMagic = 12_582_912.0f;
        quadrant = (quadrant + roundMagic) - roundMagic;

        float reduced = MathF.FusedMultiplyAdd(
            quadrant,
            BitConverter.Int32BitsToSingle(unchecked((int)0xBFC90FD8)),
            input);
        reduced = MathF.FusedMultiplyAdd(
            quadrant,
            BitConverter.Int32BitsToSingle(unchecked((int)0xB4A8885A)),
            reduced);
        reduced = MathF.FusedMultiplyAdd(
            quadrant,
            BitConverter.Int32BitsToSingle(unchecked((int)0xA7C234C4)),
            reduced);
        float squared = reduced * reduced;

        float cosineValue = MathF.FusedMultiplyAdd(
            BitConverter.Int32BitsToSingle(0x37CC730B),
            squared,
            BitConverter.Int32BitsToSingle(unchecked((int)0xBAB6036E)));
        cosineValue = MathF.FusedMultiplyAdd(
            cosineValue,
            squared,
            BitConverter.Int32BitsToSingle(0x3D2AAA9E));
        cosineValue = MathF.FusedMultiplyAdd(cosineValue, squared, -0.5f);
        cosineValue = MathF.FusedMultiplyAdd(cosineValue, squared, 1.0f);

        float sineValue = MathF.FusedMultiplyAdd(
            BitConverter.Int32BitsToSingle(0x363E9DDE),
            squared,
            BitConverter.Int32BitsToSingle(unchecked((int)0xB95035DD)));
        sineValue = MathF.FusedMultiplyAdd(
            sineValue,
            squared,
            BitConverter.Int32BitsToSingle(0x3C0888CD));
        sineValue = MathF.FusedMultiplyAdd(sineValue, squared, -1.0f / 6.0f);
        sineValue = MathF.FusedMultiplyAdd(sineValue, squared, 0.0f);
        sineValue = MathF.FusedMultiplyAdd(sineValue, reduced, reduced);

        int integerQuadrant = (int)MathF.Round(quadrant, MidpointRounding.ToEven);
        if (cosine)
        {
            integerQuadrant++;
        }

        float result = (integerQuadrant & 1) == 0 ? sineValue : cosineValue;
        return (integerQuadrant & 2) != 0 ? -result : result;
    }

    internal static double CurrentNtscChromaGroupDelayShiftSamples(
        VhsChromaFieldOptions options,
        bool isFirstField,
        int fieldNumber)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DisablePhaseCorrection || !IsNtsc(options.ColorSystem))
        {
            return 0.0;
        }

        (_, double targetPhaseDegrees) = NtscFieldPhaseTarget(isFirstField, fieldNumber);
        // The pinned upstream expression tests phase-value truthiness. Both mapped
        // phases are non-zero, so every NTSC color-frame state uses the positive shift.
        int shiftDirection = targetPhaseDegrees != 0.0 ? 1 : -1;
        double delayCycles = (options.FscMHz * 1_000_000.0)
            / ((2.0 * Math.PI) * options.ColorUnderCarrierHz);
        return (delayCycles * 4.0) * shiftDirection;
    }

    private static (int FieldPhaseId, double TargetPhaseDegrees) NtscFieldPhaseTarget(bool isFirstField, int fieldNumber)
    {
        bool secondColorFrame = ((fieldNumber / 2) & 1) == 1;
        return (isFirstField, secondColorFrame) switch
        {
            (true, false) => (1, -33.0),
            (false, true) => (2, 147.0),
            (true, true) => (3, 147.0),
            (false, false) => (4, -33.0)
        };
    }

    private static (int NextRotationIndex, ChromaPhaseLine[] PhaseSequence) BuildPhaseSequence(
        IReadOnlyList<int>? chromaRotation,
        int? chromaRotationStartingIndex,
        IReadOnlyList<double> lineLocations,
        int lineOffset,
        int inputLineLength,
        int lastLine,
        ChromaBurstProbe burstProbe,
        bool detectChromaTrackPhase,
        int rotationCheckStartLine,
        string colorSystem,
        int workerThreads)
    {
        bool hasRotation = chromaRotation is { Count: > 0 };
        bool doPhaseRotationCheck = detectChromaTrackPhase && hasRotation;
        int startingIndex = chromaRotationStartingIndex ?? 0;
        int chromaRotationIndex;
        int trackRotation;
        if (hasRotation)
        {
            if (startingIndex < 0 || startingIndex >= chromaRotation!.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(chromaRotationStartingIndex));
            }

            chromaRotationIndex = startingIndex;
            trackRotation = chromaRotation[chromaRotationIndex];
        }
        else
        {
            chromaRotationIndex = 0;
            trackRotation = startingIndex;
        }

        ChromaPhaseLine[]? parallelPrefix = TryProbePhasePrefixParallel(
            lineLocations,
            lineOffset,
            inputLineLength,
            lastLine,
            burstProbe,
            doPhaseRotationCheck ? rotationCheckStartLine : lastLine,
            trackRotation,
            workerThreads);
        var phaseSequence = new List<ChromaPhaseLine>(Math.Max(0, lastLine - lineOffset));
        int currentPhase = 0;
        ChromaPhaseLine? nextLine = null;
        for (int lineNumber = lineOffset; lineNumber < lastLine; lineNumber++)
        {
            ChromaPhaseLine currentLine;
            int prefixIndex = lineNumber - lineOffset;
            if (parallelPrefix is not null && prefixIndex < parallelPrefix.Length)
            {
                currentLine = parallelPrefix[prefixIndex];
                currentPhase = currentLine.PhaseRotation;
            }
            else if (nextLine is not null)
            {
                currentPhase = nextLine.PhaseRotation;
                currentLine = nextLine;
                nextLine = null;
            }
            else
            {
                currentPhase = PositiveModulo(currentPhase + trackRotation, 4);
                currentLine = ProbePhaseLine(
                    lineNumber,
                    currentPhase,
                    ComputeLineScale(lineLocations, lineNumber, inputLineLength, lastLine),
                    burstProbe);
            }

            if (doPhaseRotationCheck
                && lineNumber >= rotationCheckStartLine
                && lineNumber < lastLine - 1)
            {
                int nextPhase = PositiveModulo(currentPhase + trackRotation, 4);
                ChromaPhaseLine probedNextLine = ProbePhaseLine(
                    lineNumber + 1,
                    nextPhase,
                    ComputeLineScale(lineLocations, lineNumber + 1, inputLineLength, lastLine),
                    burstProbe);
                double comparisonBurst = IsNtsc(colorSystem)
                    ? currentLine.BurstPhaseDegrees
                    : phaseSequence.Count == 0
                        ? currentLine.BurstPhaseDegrees
                        : phaseSequence[^1].BurstPhaseDegrees;
                double phaseDeltaQuadrant = Math.Abs(SignedPhaseDeltaDegrees(probedNextLine.BurstPhaseDegrees, comparisonBurst));
                if (phaseDeltaQuadrant > TrackChangeThresholdDegrees)
                {
                    chromaRotationIndex = PositiveModulo(chromaRotationIndex + 1, 2);
                    trackRotation = chromaRotation![chromaRotationIndex];
                }
                else
                {
                    nextLine = probedNextLine;
                }
            }

            phaseSequence.Add(currentLine);
        }

        if (hasRotation && chromaRotationIndex == startingIndex)
        {
            chromaRotationIndex = PositiveModulo(chromaRotationIndex + 1, 2);
        }

        return (chromaRotationIndex, phaseSequence.ToArray());
    }

    private static ChromaPhaseLine[]? TryProbePhasePrefixParallel(
        IReadOnlyList<double> lineLocations,
        int lineOffset,
        int inputLineLength,
        int lastLine,
        ChromaBurstProbe burstProbe,
        int prefixEnd,
        int trackRotation,
        int workerThreads)
    {
        int prefixLength = Math.Clamp(prefixEnd, lineOffset, lastLine) - lineOffset;
        int workerCount = Math.Min(
            Math.Min(workerThreads, prefixLength),
            MaximumBurstProbeWorkers);
        if (workerCount <= 1 || prefixLength < workerCount * 2)
        {
            return null;
        }

        var prefix = new ChromaPhaseLine[prefixLength];
        var failures = new ExceptionDispatchInfo?[prefixLength];
        Parallel.For(
            0,
            workerCount,
            new ParallelOptions { MaxDegreeOfParallelism = workerCount },
            workerIndex =>
            {
                int start = (prefixLength * workerIndex) / workerCount;
                int end = (prefixLength * (workerIndex + 1)) / workerCount;
                for (int prefixIndex = start; prefixIndex < end; prefixIndex++)
                {
                    try
                    {
                        int lineNumber = lineOffset + prefixIndex;
                        int phaseRotation = PositiveModulo(
                            trackRotation * (prefixIndex + 1),
                            4);
                        prefix[prefixIndex] = ProbePhaseLine(
                            lineNumber,
                            phaseRotation,
                            ComputeLineScale(
                                lineLocations,
                                lineNumber,
                                inputLineLength,
                                lastLine),
                            burstProbe);
                    }
                    catch (Exception exception)
                    {
                        failures[prefixIndex] = ExceptionDispatchInfo.Capture(exception);
                    }
                }
            });

        for (int prefixIndex = 0; prefixIndex < failures.Length; prefixIndex++)
        {
            failures[prefixIndex]?.Throw();
        }

        return prefix;
    }

    private static ChromaPhaseLine ProbePhaseLine(
        int lineNumber,
        int currentPhase,
        double lineScale,
        ChromaBurstProbe burstProbe)
    {
        ChromaBurstDemodulationResult burst = burstProbe(lineNumber, currentPhase, lineScale);
        return new ChromaPhaseLine(
            lineNumber,
            currentPhase,
            burst.PhaseDegrees,
            burst.PhaseOffsetDegrees,
            burst.Magnitude,
            burst.I,
            burst.Q)
        {
            BurstStart = burst.Start,
            BurstEnd = burst.End,
            BurstCenter = burst.Center,
            BurstAmplitude = burst.Amplitude,
            BurstDc = burst.Dc,
            BurstFrequencyHz = burst.FrequencyHz
        };
    }

    private static double ComputeLineScale(
        IReadOnlyList<double> lineLocations,
        int lineNumber,
        int inputLineLength,
        int lastLine)
    {
        return lineNumber < lastLine - 1
            ? (lineLocations[lineNumber + 1] - lineLocations[lineNumber]) / inputLineLength
            : 1.0;
    }

    private static bool ShouldFlipTrackPhase(
        IReadOnlyList<int>? chromaRotation,
        IReadOnlyList<ChromaPhaseLine> phaseSequence,
        int end,
        string colorSystem)
    {
        if (chromaRotation is not { Count: > 0 })
        {
            return false;
        }

        int delta0 = 0;
        int delta90 = 0;
        int delta180 = 0;
        int delta270 = 0;
        int burstCheckEnd = end - BurstCheckSkipLines;
        for (int i = 1; i < phaseSequence.Count; i++)
        {
            ChromaPhaseLine previous = phaseSequence[i - 1];
            ChromaPhaseLine current = phaseSequence[i];
            if (current.LineNumber <= BurstCheckSkipLines || current.LineNumber >= burstCheckEnd)
            {
                continue;
            }

            double delta = PositiveDegrees(current.BurstPhaseDegrees - previous.BurstPhaseDegrees);
            int bucket = (int)Math.Floor((delta + 45.0) / 90.0) % 4;
            if (bucket == 0)
            {
                delta0++;
            }
            else if (bucket == 1)
            {
                delta90++;
            }
            else if (bucket == 2)
            {
                delta180++;
            }
            else
            {
                delta270++;
            }
        }

        if (IsNtsc(colorSystem))
        {
            return delta0 < delta180;
        }

        int alternating = delta90 + delta270;
        int repeatedOrInverted = delta0 + delta180;
        return alternating < repeatedOrInverted;
    }

    private static ChromaPhaseSequenceResult SummarizePhaseSequence(
        int nextRotationIndex,
        ChromaPhaseLine[] phaseSequence,
        int end,
        bool enableColorKiller,
        int prevBurstDetectedLine)
    {
        int burstCheckEnd = end - BurstCheckSkipLines;
        int burstDetectedLine = 0;
        double evenI = 0.0;
        double evenQ = 0.0;
        double oddI = 0.0;
        double oddQ = 0.0;
        int averageCount = 0;
        double burstMagnitudeAverage = 0.0;

        foreach (ChromaPhaseLine phaseLine in phaseSequence)
        {
            if (phaseLine.LineNumber <= BurstCheckSkipLines || phaseLine.LineNumber >= burstCheckEnd)
            {
                continue;
            }

            if (phaseLine.BurstMagnitude == 0.0)
            {
                continue;
            }

            double normalizedI = phaseLine.I / phaseLine.BurstMagnitude;
            double normalizedQ = phaseLine.Q / phaseLine.BurstMagnitude;
            averageCount++;
            burstMagnitudeAverage += phaseLine.BurstMagnitude;
            if (enableColorKiller
                && prevBurstDetectedLine == -1
                && burstDetectedLine == 0
                && phaseLine.BurstMagnitude > BurstMagnitudeThreshold)
            {
                burstDetectedLine = phaseLine.LineNumber;
            }

            if ((phaseLine.LineNumber & 1) == 1)
            {
                oddI += normalizedI;
                oddQ += normalizedQ;
            }
            else
            {
                evenI += normalizedI;
                evenQ += normalizedQ;
            }
        }

        if (averageCount == 0)
        {
            throw new InvalidOperationException("No valid chroma burst samples were available for phase averaging.");
        }

        burstMagnitudeAverage /= averageCount;
        if (enableColorKiller && burstMagnitudeAverage < BurstMagnitudeThreshold)
        {
            burstDetectedLine = -1;
        }

        return new ChromaPhaseSequenceResult(
            nextRotationIndex,
            phaseSequence,
            burstDetectedLine,
            burstMagnitudeAverage,
            PositiveDegrees(Math.Atan2(evenQ + oddQ, evenI + oddI) * 180.0 / Math.PI),
            PositiveDegrees(Math.Atan2(evenQ, evenI) * 180.0 / Math.PI),
            PositiveDegrees(Math.Atan2(oddQ, oddI) * 180.0 / Math.PI));
    }

    private static (int Start, int End) GetLineRange(int sampleCount, int lineOffset, int lineLength, int lineNumber)
    {
        int lineIndex = lineNumber - lineOffset;
        if (lineIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Chroma phase line is before the configured line offset.");
        }

        int lineStart = checked(lineIndex * lineLength);
        int lineEnd = lineStart + lineLength;
        if (lineEnd > sampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "Chroma phase line exceeds the chroma field length.");
        }

        return (lineStart, lineEnd);
    }

    private static (int Start, int End) GetNumpySliceRange(
        int sampleCount,
        int lineOffset,
        int lineLength,
        int lineNumber)
    {
        int start = checked((lineNumber - lineOffset) * lineLength);
        int end = checked(start + lineLength);
        start = NormalizeNumpySliceBoundary(start, sampleCount);
        end = NormalizeNumpySliceBoundary(end, sampleCount);
        return end > start ? (start, end) : (start, start);
    }

    private static (int Start, int End) GetNumpySliceRange(
        int sampleCount,
        int start,
        int end)
    {
        start = NormalizeNumpySliceBoundary(start, sampleCount);
        end = NormalizeNumpySliceBoundary(end, sampleCount);
        return end > start ? (start, end) : (start, start);
    }

    private static int NormalizeNumpySliceBoundary(int index, int length)
    {
        if (index < 0)
        {
            index += length;
        }

        return Math.Clamp(index, 0, length);
    }

    private static int NormalizeNumpyIndex(int index, int length)
    {
        if (index < 0)
        {
            index += length;
        }

        if ((uint)index >= (uint)length)
        {
            throw new IndexOutOfRangeException("Chroma phase index is outside the output field.");
        }

        return index;
    }

    private static void ValidateLineShape(int sampleCount, int lines, int lineLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);
        if (sampleCount < checked(lines * lineLength))
        {
            throw new ArgumentException("Chroma sample count is shorter than the requested line geometry.");
        }
    }

    private static void ValidateBurstRange(int burstStart, int burstEnd, int lineLength)
    {
        if (burstStart < 0 || burstEnd < burstStart || burstEnd > lineLength)
        {
            throw new ArgumentOutOfRangeException(nameof(burstStart), "Burst range must fit inside one output line.");
        }
    }

    private static double Rms(ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
        {
            return 0.0;
        }

        double mean = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            mean += values[i];
        }

        mean /= values.Length;
        double sumSquares = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            double centered = values[i] - mean;
            sumSquares += centered * centered;
        }

        return Math.Sqrt(sumSquares / values.Length);
    }

    private static float RmsFloat32(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            return 0.0f;
        }

        float mean = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            mean += (float)values[i];
        }

        mean /= values.Length;
        float sumSquares = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            float centered = (float)values[i] - mean;
            float square = centered * centered;
            sumSquares += square;
        }

        return MathF.Sqrt(sumSquares / values.Length);
    }

    private static int PositiveModulo(int value, int modulo)
    {
        int result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static double PositiveDegrees(double degrees)
    {
        double result = degrees % 360.0;
        return result < 0.0 ? result + 360.0 : result;
    }

    private static double NumpyHypot(double x, double y)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsHypot(x, y);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxHypot(x, y);
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsHypot(x, y);
        }

        return double.Hypot(x, y);
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_hypot", ExactSpelling = true)]
    private static extern double WindowsHypot(double x, double y);

    [DllImport("libm.so.6", EntryPoint = "hypot", ExactSpelling = true)]
    private static extern double LinuxHypot(double x, double y);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "hypot", ExactSpelling = true)]
    private static extern double MacOsHypot(double x, double y);

    private static double SignedPhaseDeltaDegrees(double current, double previous)
    {
        return PositiveDegrees(current - previous + 180.0) - 180.0;
    }

    private static bool IsNtsc(string colorSystem)
    {
        return string.Equals(colorSystem, "NTSC", StringComparison.OrdinalIgnoreCase);
    }
}
