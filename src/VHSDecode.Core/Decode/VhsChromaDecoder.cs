using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

public sealed record AutomaticChromaGainResult(double[] Samples, double MeanBurstRms);

public sealed record ChromaBurstDemodulationResult(
    double PhaseDegrees,
    double PhaseOffsetDegrees,
    double Magnitude,
    double I,
    double Q);

public sealed record ChromaPhaseLine(
    int LineNumber,
    int PhaseRotation,
    double BurstPhaseDegrees = 0.0,
    double BurstPhaseOffsetDegrees = 0.0,
    double BurstMagnitude = 0.0,
    double I = 0.0,
    double Q = 0.0);

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
    private readonly object _gate = new();
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
    private const double TrackChangeThresholdDegrees = 90.0;
    private const double S16AbsMax = 32767.0;

    public static ushort[] ChromaToU16(ReadOnlySpan<double> chroma)
    {
        var output = new ushort[chroma.Length];
        int vectorizedLength = chroma.Length & ~3;
        for (int i = 0; i < vectorizedLength; i++)
        {
            double shifted = chroma[i] + S16AbsMax;
            output[i] = !double.IsFinite(shifted) || shifted <= 0.0
                ? ushort.MinValue
                : shifted >= ushort.MaxValue
                    ? ushort.MaxValue
                    : (ushort)shifted;
        }

        for (int i = vectorizedLength; i < chroma.Length; i++)
        {
            double shifted = chroma[i] + S16AbsMax;
            output[i] = !double.IsFinite(shifted) || shifted < long.MinValue || shifted > long.MaxValue
                ? ushort.MinValue
                : unchecked((ushort)(long)shifted);
        }

        return output;
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
                ? values => SosFilter.ApplyForwardBackwardFloat32(options.FinalSosFilter, values)
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
            (lineNumber, phaseRotation, lineScale) => ProbeUpconvertedBurst(
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
            options.ColorSystem);
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
        double previousChromaAfcPhaseRadians = 0.0)
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
            analysis);

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
        VhsChromaPhaseAnalysis? preparedAnalysis)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(phase);
        ValidateLineShape(chroma.Length, options.OutputLineCount, options.OutputLineLength);
        ValidateBurstRange(options.BurstStart, options.BurstEnd, options.OutputLineLength);

        if (phase.BurstDetectedLine == -1)
        {
            return new VhsChromaFieldResult(
                ChromaToU16(new double[chroma.Length]),
                phase.BurstDetectedLine,
                null,
                phase.NextChromaRotationIndex,
                phase);
        }

        double[]? ownedChromaField = ApplyConfiguredChromaPreFilter(
            chroma,
            options,
            previousChromaAfcCarrierHz);
        ReadOnlySpan<double> chromaField = ownedChromaField is null
            ? chroma
            : ownedChromaField;
        double outputSampleRateMHz = options.FscMHz * 4.0;
        ReadOnlySpan<double> carrierProbe = chromaField;
        if (options.ChromaAfcTrackCarrier && options.ChromaAfcMeasurementFilters is { } measurementFilters)
        {
            double[] filteredCarrierProbe = SosFilter.ApplyForwardBackwardFloat32(
                measurementFilters.HighPass,
                carrierProbe);
            filteredCarrierProbe = SosFilter.ApplyForwardBackwardFloat32(
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

        if (IsNtsc(options.ColorSystem))
        {
            chromaField = ApplyBurstDeemphasis(
                chromaField,
                lineOffset,
                options.OutputLineCount,
                options.OutputLineLength,
                options.BurstStart,
                options.BurstEnd);
        }

        double[] upconverted;
        int? fieldPhaseId = null;
        if (usePhaseCompensation)
        {
            (fieldPhaseId, double targetPhase) = NtscFieldPhaseTarget(
                isFirstField.GetValueOrDefault(),
                fieldNumber);
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
        else
        {
            upconverted = UpconvertChroma(
                chromaField,
                lineOffset,
                options.OutputLineLength,
                phase.PhaseSequence,
                heterodyne!);
        }

        if (finalFilter is not null)
        {
            upconverted = finalFilter(upconverted);
        }
        else if (options.FinalSosFilter is not null)
        {
            upconverted = SosFilter.ApplyForwardBackwardFloat32(options.FinalSosFilter, upconverted);
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
            && (options.FinalSosFilter is not null || options.FinalFilter is null);

        if (!options.DisableComb)
        {
            upconverted = IsNtsc(options.ColorSystem)
                ? ApplyNtscComb(upconverted, options.OutputLineLength, retainFloat32)
                : ApplyPalComb(upconverted, options.OutputLineLength, retainFloat32);
        }

        AutomaticChromaGainResult gained = ApplyAutomaticChromaGain(
            upconverted,
            options.BurstAbsRef,
            options.BurstStart,
            options.BurstEnd,
            options.OutputLineLength,
            options.OutputLineCount,
            phase.BurstDetectedLine,
            useFloat32Rms: retainFloat32);
        return new VhsChromaFieldResult(
            ChromaToU16(gained.Samples),
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
            colorSystem);

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
                colorSystem);
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

    public static double[] RefineLineLocationsFromBurst(
        IReadOnlyList<double> lineLocations,
        int outputLineLength,
        double fscRatio,
        ChromaPhaseSequenceResult phase,
        string colorSystem)
    {
        ArgumentNullException.ThrowIfNull(lineLocations);
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLineLength);
        if (!double.IsFinite(fscRatio) || fscRatio <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(fscRatio));
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

        int burstTbcStart = Math.Max(9, phase.BurstDetectedLine);
        for (int phaseIndex = burstTbcStart; phaseIndex < phase.PhaseSequence.Length; phaseIndex++)
        {
            ChromaPhaseLine phaseLine = phase.PhaseSequence[phaseIndex];
            int lineNumber = phaseLine.LineNumber;
            if (lineNumber < 0 || lineNumber + 1 >= output.Length)
            {
                continue;
            }

            double targetPhase = IsNtsc(colorSystem)
                ? phase.BurstPhaseAverageDegrees
                : (lineNumber & 1) == 1
                    ? phase.OddBurstPhaseAverageDegrees
                    : phase.EvenBurstPhaseAverageDegrees;
            double phaseDelta = PositiveDegrees(
                targetPhase - phaseLine.BurstPhaseDegrees + phaseLine.BurstPhaseOffsetDegrees + 180.0) - 180.0;
            double lineLength = output[lineNumber + 1] - output[lineNumber];
            double scale = lineLength / outputLineLength;
            output[lineNumber] += (phaseDelta / 360.0) * fscRatio * scale;
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

    public static double[] ApplyNtscComb(
        ReadOnlySpan<double> chroma,
        int lineLength,
        bool retainFloat32 = true)
    {
        return ApplyComb(chroma, lineLength, lineDistance: 1, retainFloat32);
    }

    public static double[] ApplyPalComb(
        ReadOnlySpan<double> chroma,
        int lineLength,
        bool retainFloat32 = true)
    {
        return ApplyComb(chroma, lineLength, lineDistance: 2, retainFloat32);
    }

    public static double[] ApplyBurstDeemphasis(
        ReadOnlySpan<double> chroma,
        int lineOffset,
        int linesOut,
        int lineLength,
        int burstStart,
        int burstEnd)
    {
        ValidateLineShape(chroma.Length, linesOut, lineLength);
        ArgumentOutOfRangeException.ThrowIfNegative(lineOffset);
        ValidateBurstRange(burstStart, burstEnd, lineLength);

        double[] output = chroma.ToArray();
        int firstDoubledSample = burstEnd + 5;
        if (firstDoubledSample >= lineLength)
        {
            return output;
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

        return output;
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

    private static double[] ApplyComb(
        ReadOnlySpan<double> chroma,
        int lineLength,
        int lineDistance,
        bool retainFloat32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineLength);

        double[] output = chroma.ToArray();
        int lineCount = chroma.Length / lineLength;
        for (int line = StartingLine; line < lineCount - 2; line++)
        {
            int lineStart = line * lineLength;
            int advancedStart = (line + lineDistance) * lineLength;
            int delayedStart = (line - lineDistance) * lineLength;
            for (int i = 0; i < lineLength; i++)
            {
                // Numba lowers PAL's float64 2H expression with the delayed term first; NTSC 1H retains source order.
                double combined = !retainFloat32 && lineDistance == 2
                    ? ((chroma[lineStart + i] * 2.0) - chroma[delayedStart + i] - chroma[advancedStart + i]) / 4.0
                    : ((chroma[lineStart + i] * 2.0) - chroma[advancedStart + i] - chroma[delayedStart + i]) / 4.0;
                output[lineStart + i] = retainFloat32 ? (double)(float)combined : combined;
            }
        }

        return output;
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
        string colorSystem)
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

        var phaseSequence = new List<ChromaPhaseLine>(Math.Max(0, lastLine - lineOffset));
        int currentPhase = 0;
        ChromaPhaseLine? nextLine = null;
        for (int lineNumber = lineOffset; lineNumber < lastLine; lineNumber++)
        {
            ChromaPhaseLine currentLine;
            if (nextLine is not null)
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
            burst.Q);
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
