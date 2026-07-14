using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Dsp;

public sealed record DecodeFilterSet(
    Complex[] RfVideo,
    Complex[] RfHighPass,
    Complex[] RfMtf,
    Complex[] Video,
    Complex[] VideoLowPass,
    Complex[] VideoLowPass05,
    Complex[]? LdEfm,
    double[] RfVideoMagnitude,
    double[] RfHighPassMagnitude,
    double[] RfMtfMagnitude,
    double[] VideoMagnitude,
    double[] VideoLowPassMagnitude,
    double[] VideoLowPass05Magnitude,
    double[]? LdEfmMagnitude,
    LaserDiscAnalogAudioFilterSet? LdAnalogAudio = null,
    Complex[]? LdAc3 = null,
    double[]? LdAc3Magnitude = null,
    Complex[]? ChromaBurst = null,
    double[]? ChromaBurstMagnitude = null,
    int ChromaOffsetSamples = 0,
    Complex[]? LdVideoBurst = null,
    double[]? LdVideoBurstMagnitude = null,
    Complex[]? LdVideoPilot = null,
    double[]? LdVideoPilotMagnitude = null,
    Complex[]? CvbsVideoBurst = null,
    double[]? CvbsVideoBurstMagnitude = null,
    SosSection[]? ChromaBurstSos = null,
    TransferFunction? ChromaBurstAudioNotch = null,
    TransferFunction? ChromaBurstVideoNotch = null,
    SosSection[]? VhsEnvelopeSos = null,
    SosSection[]? VhsRfTopSos = null)
{
    public const int DefaultVideoLowPass05Offset = 32;

    public const int DefaultLdVideoBurstOffset = 40;

    public int VideoLowPass05Offset { get; init; }

    public int LdVideoBurstOffset { get; init; }

    public int RfHighPassOffset { get; init; }

    public double LdVideoWhiteOffset { get; init; }

    public double LdVideoSyncOffset { get; init; }

    public bool ChromaBurstUsesDemodulatedVideo { get; init; }
}

public sealed record DecodeFilterOptions(
    double? VideoNotchHz = null,
    double VideoNotchQ = 10.0,
    bool LdNtscColorNotch = false,
    bool LdPalV4300DNotch = false,
    bool LdVideoGroupDelayEqualizer = true,
    bool LdNtscAnalogAudioNotch = false,
    bool LdDecodeDigitalAudio = true,
    bool LdDecodeAnalogAudio = false,
    double? LdAudioFilterWidthHz = null,
    double? LdMtfLevel = null,
    double LdMtfOffset = 0.0,
    bool LdClipDemodForVideo = false,
    double FmAudioNotchQ = 0.0,
    RfHighBoostOptions? RfHighBoost = null,
    DiffDemodRepairOptions? DiffDemodRepair = null,
    double? BetamaxFscNotchHz = null,
    ChromaTrapOptions? ChromaTrap = null,
    SharpnessEqOptions? SharpnessEq = null,
    NonlinearDeemphasisOptions? NonlinearDeemphasis = null,
    SubDeemphasisOptions? SubDeemphasis = null,
    bool ExportRawTbc = false,
    bool UseChromaAfc = false,
    RfFmDemodulatorMode FmDemodulatorMode = RfFmDemodulatorMode.ConjugateProduct);

public enum RfFmDemodulatorMode
{
    ConjugateProduct,
    VhsRustApproximation
}

public sealed record RfHighBoostOptions(double Multiplier, double LowHz, double HighHz);

public sealed record DiffDemodRepairOptions(double MaxValue);

public sealed record ChromaTrapOptions(double FscHz);

public sealed record SharpnessEqOptions(double Level, double CornerHz, double TransitionHz, int OrderLimit);

public sealed record NonlinearDeemphasisOptions(
    double HighPassHz,
    double? BandPassUpperHz,
    int Order,
    double LimitLow,
    double LimitHigh);

public sealed record SubDeemphasisOptions(
    double HighPassHz,
    double? BandPassUpperHz,
    int Order,
    double AmplitudeLowPassHz,
    double Deviation,
    double ExponentialScaling,
    double? Scaling1,
    double? Scaling2,
    double? LogisticMid,
    double? LogisticRate,
    double? StaticFactor);

public sealed record ChromaAfcMeasurementFilterSet(
    SosSection[] HighPass,
    SosSection[] LowPass);

public sealed record LaserDiscAnalogAudioFilterSet(
    LaserDiscAnalogAudioChannelFilter Left,
    LaserDiscAnalogAudioChannelFilter Right,
    int DecimationFactor);

public sealed record LaserDiscAnalogAudioChannelFilter(
    int LowBin,
    int BinCount,
    double SliceSampleRateHz,
    double LowFrequencyHz,
    double CenterFrequencyHz,
    Complex[] Stage1Filter,
    Complex[] Stage2Filter);

public static class DecodeFilterSetBuilder
{
    private const double RfDropoutHighPassHz = 10_000_000.0;

    public static DecodeFilterSet BuildBasic(
        FormatParameterSet parameters,
        double sampleRateHz,
        int blockLength,
        DecodeFilterOptions? options = null)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (blockLength <= 0 || blockLength % 2 != 0)
        {
            throw new ArgumentException("Block length must be a positive even value.", nameof(blockLength));
        }

        double nyquistHz = sampleRateHz / 2.0;
        options ??= new DecodeFilterOptions();
        bool isLaserDisc = parameters.TapeFormat == "LD";
        bool isVhsRfDecode = parameters.TapeFormat is not ("LD" or "CVBS");
        Complex[] rfVideo = BuildRfVideo(
            parameters.SysParams,
            parameters.RfParams,
            isLaserDisc,
            isVhsRfDecode,
            options,
            nyquistHz,
            blockLength);
        Complex[] rfHighPass = BuildRfHighPass(nyquistHz, blockLength);
        Complex[] rfMtf = BuildLdMtf(parameters.RfParams, isLaserDisc, options, nyquistHz, blockLength);
        Complex[]? ldEfm = BuildLdEfm(isLaserDisc && options.LdDecodeDigitalAudio, sampleRateHz, nyquistHz, blockLength);
        Complex[]? ldAc3 = BuildLdAc3(parameters, nyquistHz, blockLength);
        LaserDiscAnalogAudioFilterSet? ldAnalogAudio = BuildLdAnalogAudio(parameters, options, sampleRateHz, nyquistHz, blockLength);
        SosSection[]? chromaBurstSos = BuildChromaBurstSos(parameters, nyquistHz);
        Complex[]? chromaBurst = BuildChromaBurst(chromaBurstSos, options, nyquistHz, blockLength);
        TransferFunction? chromaBurstAudioNotch = chromaBurstSos is null
            ? null
            : BuildChromaAudioNotchFilter(parameters, sampleRateHz);
        TransferFunction? chromaBurstVideoNotch = chromaBurstSos is null
            ? null
            : BuildChromaVideoNotchFilter(options, sampleRateHz);
        SosSection[]? vhsEnvelopeSos = BuildVhsEnvelopeSos(parameters, nyquistHz);
        SosSection[]? vhsRfTopSos = BuildVhsRfTopSos(parameters, nyquistHz);
        Complex[] videoLowPass = BuildVideoLowPass(
            parameters.RfParams,
            options,
            nyquistHz,
            blockLength,
            zeroPhaseMagnitude: !isLaserDisc);
        Complex[] video = BuildVideo(
            parameters.System,
            parameters.RfParams,
            isLaserDisc,
            videoLowPass,
            sampleRateHz,
            blockLength,
            options);
        Complex[] videoLowPass05Source = parameters.TapeFormat switch
        {
            "LD" => BuildVideoReference(parameters.RfParams, videoLowPass, sampleRateHz, blockLength),
            "CVBS" => Enumerable.Repeat(Complex.One, blockLength).ToArray(),
            _ => BuildVideoReference(parameters.RfParams, videoLowPass, sampleRateHz, blockLength)
        };
        Complex[] videoLowPass05 = BuildVideoLowPass05(videoLowPass05Source, nyquistHz, blockLength);
        Complex[]? ldVideoBurst = BuildLdVideoBurst(parameters, videoLowPass05Source, nyquistHz, blockLength);
        Complex[]? ldVideoPilot = BuildLdVideoPilot(parameters, videoLowPass05Source, nyquistHz, blockLength);
        Complex[]? cvbsVideoBurst = BuildCvbsVideoBurst(parameters, nyquistHz, blockLength);
        var filters = new DecodeFilterSet(
            rfVideo,
            rfHighPass,
            rfMtf,
            video,
            videoLowPass,
            videoLowPass05,
            ldEfm,
            rfVideo.Select(value => value.Magnitude).ToArray(),
            rfHighPass.Select(value => value.Magnitude).ToArray(),
            rfMtf.Select(value => value.Magnitude).ToArray(),
            video.Select(value => value.Magnitude).ToArray(),
            videoLowPass.Select(value => value.Magnitude).ToArray(),
            videoLowPass05.Select(value => value.Magnitude).ToArray(),
            ldEfm?.Select(value => value.Magnitude).ToArray(),
            ldAnalogAudio,
            LdAc3: ldAc3,
            LdAc3Magnitude: ldAc3?.Select(value => value.Magnitude).ToArray(),
            ChromaBurst: chromaBurst,
            ChromaBurstMagnitude: chromaBurst?.Select(value => value.Magnitude).ToArray(),
            ChromaOffsetSamples: chromaBurst is null
                ? 0
                : (int)(JsonDoubleOrDefault(parameters.RfParams, "chroma_offset", 5.0) * (sampleRateHz / 40_000_000.0)),
            LdVideoBurst: ldVideoBurst,
            LdVideoBurstMagnitude: ldVideoBurst?.Select(value => value.Magnitude).ToArray(),
            LdVideoPilot: ldVideoPilot,
            LdVideoPilotMagnitude: ldVideoPilot?.Select(value => value.Magnitude).ToArray(),
            CvbsVideoBurst: cvbsVideoBurst,
            CvbsVideoBurstMagnitude: cvbsVideoBurst?.Select(value => value.Magnitude).ToArray(),
            ChromaBurstSos: chromaBurstSos,
            ChromaBurstAudioNotch: chromaBurstAudioNotch,
            ChromaBurstVideoNotch: chromaBurstVideoNotch,
            VhsEnvelopeSos: vhsEnvelopeSos,
            VhsRfTopSos: vhsRfTopSos)
        {
            VideoLowPass05Offset = DecodeFilterSet.DefaultVideoLowPass05Offset,
            LdVideoBurstOffset = ldVideoBurst is null ? 0 : DecodeFilterSet.DefaultLdVideoBurstOffset,
            ChromaBurstUsesDemodulatedVideo = isVhsRfDecode
                && !FormatCatalog.IsColorUnder(parameters.TapeFormat)
        };
        DecodeDelayEstimates delays = parameters.TapeFormat == "LD"
            ? DecodeDelayEstimator.EstimateLaserDiscDelays(parameters, filters, sampleRateHz, blockLength)
            : default;
        return filters with
        {
            RfHighPassOffset = delays.RfHighPassOffset,
            LdVideoWhiteOffset = delays.VideoWhiteOffset,
            LdVideoSyncOffset = delays.VideoSyncOffset
        };
    }

    public static Complex[] BuildLaserDiscMtf(
        FormatParameterSet parameters,
        DecodeFilterOptions options,
        double targetMtf,
        double sampleRateHz,
        int blockLength)
    {
        if (!double.IsFinite(targetMtf))
        {
            throw new ArgumentOutOfRangeException(nameof(targetMtf));
        }

        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (blockLength <= 0 || blockLength % 2 != 0)
        {
            throw new ArgumentException("Block length must be a positive even value.", nameof(blockLength));
        }

        double mtfMultiplier = options.LdMtfLevel ?? 1.0;
        DecodeFilterOptions effectiveOptions = options with
        {
            LdMtfLevel = targetMtf * mtfMultiplier
        };
        return BuildLdMtf(
            parameters.RfParams,
            parameters.TapeFormat == "LD",
            effectiveOptions,
            sampleRateHz / 2.0,
            blockLength);
    }

    private static Complex[] BuildRfHighPass(double nyquistHz, int blockLength)
    {
        if (RfDropoutHighPassHz >= nyquistHz)
        {
            return Ones(blockLength);
        }

        return IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthHighPass(
                order: 1,
                normalizedCutoff: RfDropoutHighPassHz / nyquistHz),
            blockLength);
    }

    private static Complex[] BuildRfVideo(
        JsonElement sysParams,
        JsonElement rfParams,
        bool useLaserDiscSplitSkirts,
        bool applyUserRfNotch,
        DecodeFilterOptions options,
        double nyquistHz,
        int blockLength)
    {
        double low = JsonDouble(rfParams, "video_bpf_low");
        double high = JsonDouble(rfParams, "video_bpf_high");

        Complex[] output;
        if (JsonBool(rfParams, "video_bpf_supergauss", defaultValue: false))
        {
            double[] half = FrequencyDomainFilter.BandPassSuperGaussianHalf(
                low,
                high,
                JsonInt(rfParams, "video_bpf_order", defaultValue: 1) ?? 1,
                nyquistHz,
                blockLength);
            output = FrequencyDomainFilter.MirrorHalfToFull(half)
                .Select(value => new Complex(value, 0.0))
                .ToArray();
        }
        else
        {
            int lowOrder = JsonInt(rfParams, "video_bpf_low_order", defaultValue: -1) ?? -1;
            int highOrder = JsonInt(rfParams, "video_bpf_high_order", defaultValue: -1) ?? -1;
            int? order = JsonInt(rfParams, "video_bpf_order");
            if (!useLaserDiscSplitSkirts)
            {
                if (order is > 0)
                {
                    output = IirFilterDesign.FrequencyResponse(
                        IirFilterDesign.ButterworthBandPass(
                            order.Value,
                            low / nyquistHz,
                            high / nyquistHz),
                        blockLength);
                }
                else
                {
                    output = Ones(blockLength);
                }
            }
            else if (lowOrder > 0 && highOrder > 0)
            {
                Complex[] highPass = IirFilterDesign.FrequencyResponse(
                    IirFilterDesign.ButterworthHighPassTransferFunction(lowOrder, low / nyquistHz),
                    blockLength);
                Complex[] lowPass = IirFilterDesign.FrequencyResponse(
                    IirFilterDesign.ButterworthLowPassTransferFunction(highOrder, high / nyquistHz),
                    blockLength);
                output = Multiply(highPass, lowPass);
            }
            else if (order is > 0)
            {
                output = IirFilterDesign.FrequencyResponse(
                    IirFilterDesign.ButterworthBandPass(
                        order.Value,
                        low / nyquistHz,
                        high / nyquistHz),
                    blockLength);
            }
            else
            {
                output = Ones(blockLength);
            }
        }

        if (!useLaserDiscSplitSkirts)
        {
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = new Complex(NumpyComplexMagnitude(output[i]), 0.0);
            }
        }

        MultiplyIfPresent(output, LowPassExtra(rfParams, nyquistHz, blockLength));
        MultiplyIfPresent(output, HighPassExtra(rfParams, nyquistHz, blockLength));
        ApplyPeakingIfPresent(output, rfParams, nyquistHz, blockLength);
        ApplyFmAudioNotchIfPresent(output, rfParams, options, nyquistHz, blockLength);
        ApplyLdNtscAnalogAudioNotchIfPresent(output, sysParams, rfParams, options, nyquistHz, blockLength);
        ApplyRampIfPresent(output, rfParams, nyquistHz, blockLength);
        if (applyUserRfNotch)
        {
            ApplyRfInputNotchIfPresent(output, options, nyquistHz, blockLength);
        }

        if (!useLaserDiscSplitSkirts)
        {
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = new Complex(NumpyComplexMagnitude(output[i]), 0.0);
            }
        }

        return output;
    }

    private static Complex[] BuildLdMtf(
        JsonElement rfParams,
        bool useLaserDiscMtf,
        DecodeFilterOptions options,
        double nyquistHz,
        int blockLength)
    {
        if (!useLaserDiscMtf || options.LdMtfLevel is not { } mtfLevel)
        {
            return Ones(blockLength);
        }

        double effectiveLevel = (mtfLevel + options.LdMtfOffset) * JsonDoubleOrDefault(rfParams, "MTF_basemult", 1.0);
        if (effectiveLevel == 0.0)
        {
            return Ones(blockLength);
        }

        double poleDistance = JsonDouble(rfParams, "MTF_poledist");
        double poleFrequency = JsonDouble(rfParams, "MTF_freq") * 1_000_000.0;
        if (poleDistance <= 0.0 || poleDistance >= 1.0 || poleFrequency <= 0.0 || poleFrequency >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "LD MTF parameters must describe stable poles inside the Nyquist range.");
        }

        Complex lowPole = Complex.FromPolarCoordinates(poleDistance, Math.PI * (poleFrequency / nyquistHz));
        Complex highPole = Complex.FromPolarCoordinates(poleDistance, Math.PI * (2.0 - (poleFrequency / nyquistHz)));
        double a1 = -(lowPole + highPole).Real;
        double a2 = (lowPole * highPole).Real;
        Complex[] response = IirFilterDesign.FrequencyResponse(
            new TransferFunction([1.0], [1.0, a1, a2]),
            blockLength);

        if (effectiveLevel != 1.0)
        {
            for (int i = 0; i < response.Length; i++)
            {
                response[i] = Complex.Pow(response[i], effectiveLevel);
            }
        }

        return response;
    }

    private static Complex[]? BuildLdEfm(bool enabled, double sampleRateHz, double nyquistHz, int blockLength)
    {
        if (!enabled)
        {
            return null;
        }

        double[] controlFrequencies =
        [
            0.0,
            190_000.0,
            380_000.0,
            570_000.0,
            760_000.0,
            950_000.0,
            1_140_000.0,
            1_330_000.0,
            1_520_000.0,
            1_710_000.0,
            1_900_000.0
        ];
        double[] amplitudes = [0.0, 0.215, 0.41, 0.73, 0.98, 1.03, 0.99, 0.81, 0.59, 0.42, 0.0];
        double[] phases = [0.0, -1.15, -1.2875, -1.3875, -1.5, -1.5, -1.5, -1.5, -1.3125, -1.1875, -1.0];
        double[] amplitudeSecondDerivatives = BuildNaturalCubicSecondDerivatives(controlFrequencies, amplitudes);
        double[] phaseSecondDerivatives = BuildNaturalCubicSecondDerivatives(controlFrequencies, phases);

        Complex[] output = new Complex[blockLength];
        double freqPerBin = sampleRateHz / blockLength;
        int nonzeroBins = Math.Min((int)(controlFrequencies[^1] / freqPerBin) + 1, (blockLength / 2) + 1);
        double[] bandPass = FrequencyDomainFilter.BandPassSuperGaussianHalf(
            lowFrequency: 20_000.0,
            highFrequency: 1_600_000.0,
            order: 60,
            nyquistHz,
            blockLength);
        for (int i = 0; i < nonzeroBins; i++)
        {
            double frequency = i * freqPerBin;
            double amplitude = InterpolateNaturalCubic(controlFrequencies, amplitudes, amplitudeSecondDerivatives, frequency);
            double phase = InterpolateNaturalCubic(controlFrequencies, phases, phaseSecondDerivatives, frequency);
            output[i] = 8.0 * bandPass[i] * Complex.FromPolarCoordinates(amplitude, -phase);
        }

        return output;
    }

    private static Complex[]? BuildLdAc3(FormatParameterSet parameters, double nyquistHz, int blockLength)
    {
        if (parameters.TapeFormat != "LD" || !JsonBool(parameters.SysParams, "AC3", defaultValue: false))
        {
            return null;
        }

        double ac3Carrier = JsonDouble(parameters.SysParams, "audio_rfreq_AC3");
        double passWidth = 288_000.0 * 0.5;
        Complex[] fixedPass = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthBandPass(
                order: 3,
                (2_880_000.0 - 500_000.0) / nyquistHz,
                (2_880_000.0 + 500_000.0) / nyquistHz),
            blockLength);
        Complex[] carrierPass = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthBandPass(
                order: 3,
                (ac3Carrier - passWidth) / nyquistHz,
                (ac3Carrier + passWidth) / nyquistHz),
            blockLength);
        return Multiply(fixedPass, carrierPass);
    }

    private static LaserDiscAnalogAudioFilterSet? BuildLdAnalogAudio(
        FormatParameterSet parameters,
        DecodeFilterOptions options,
        double sampleRateHz,
        double nyquistHz,
        int blockLength)
    {
        if (parameters.TapeFormat != "LD" || !options.LdDecodeAnalogAudio)
        {
            return null;
        }

        double filterWidthHz = options.LdAudioFilterWidthHz
            ?? JsonDouble(parameters.RfParams, "audio_filterwidth");
        int filterOrder = JsonInt(parameters.RfParams, "audio_filterorder") ?? 512;
        double leftCarrier = JsonDouble(parameters.SysParams, "audio_lfreq");
        double rightCarrier = JsonDouble(parameters.SysParams, "audio_rfreq");

        LaserDiscAnalogAudioChannelFilter left = BuildLdAnalogAudioChannel(
            leftCarrier,
            filterWidthHz,
            filterOrder,
            sampleRateHz,
            nyquistHz,
            blockLength);
        LaserDiscAnalogAudioChannelFilter right = BuildLdAnalogAudioChannel(
            rightCarrier,
            filterWidthHz,
            filterOrder,
            sampleRateHz,
            nyquistHz,
            blockLength);
        int decimation = blockLength / left.BinCount;
        return new LaserDiscAnalogAudioFilterSet(left, right, decimation);
    }

    private static SosSection[]? BuildChromaBurstSos(
        FormatParameterSet parameters,
        double nyquistHz)
    {
        if (parameters.TapeFormat == "LD")
        {
            return null;
        }

        JsonElement rfParams = parameters.RfParams;
        if (!FormatCatalog.IsColorUnder(parameters.TapeFormat))
        {
            double fscMHz = JsonDouble(parameters.SysParams, "fsc_mhz");
            double outputNyquistMHz = fscMHz * 2.0;
            return IirFilterDesign.ButterworthBandPassSos(
                order: 4,
                normalizedLowCutoff: (fscMHz - 0.1) / outputNyquistMHz,
                normalizedHighCutoff: (fscMHz + 0.1) / outputNyquistMHz);
        }

        if (!TryJsonDouble(rfParams, "color_under_carrier", out _)
            || !TryJsonDouble(rfParams, "chroma_bpf_upper", out double highCutHz))
        {
            return null;
        }

        double lowCutHz = JsonDoubleOrDefault(rfParams, "chroma_bpf_lower", 60_000.0);
        int order = JsonInt(rfParams, "chroma_bpf_order", defaultValue: 4) ?? 4;
        if (lowCutHz <= 0.0 || highCutHz <= lowCutHz || highCutHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Chroma burst band-pass frequencies must be inside the Nyquist range.");
        }

        return IirFilterDesign.ButterworthBandPassSos(
            order,
            lowCutHz / nyquistHz,
            highCutHz / nyquistHz);
    }

    private static SosSection[]? BuildVhsEnvelopeSos(
        FormatParameterSet parameters,
        double nyquistHz)
    {
        if (parameters.TapeFormat is "LD" or "CVBS")
        {
            return null;
        }

        const double cutoffHz = 700_000.0;
        if (cutoffHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters),
                "VHS envelope low-pass frequency must be below Nyquist.");
        }

        return IirFilterDesign.ButterworthLowPass(order: 1, cutoffHz / nyquistHz);
    }

    private static SosSection[]? BuildVhsRfTopSos(
        FormatParameterSet parameters,
        double nyquistHz)
    {
        if (parameters.TapeFormat is "LD" or "CVBS"
            || !TryJsonDouble(parameters.RfParams, "boost_bpf_low", out double lowHz)
            || !TryJsonDouble(parameters.RfParams, "boost_bpf_high", out double highHz))
        {
            return null;
        }

        if (lowHz <= 0.0 || highHz <= lowHz || highHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters),
                "VHS RF boost band-pass frequencies must be inside the Nyquist range.");
        }

        return IirFilterDesign.ButterworthBandPassSos(
            order: 1,
            lowHz / nyquistHz,
            highHz / nyquistHz);
    }

    private static Complex[]? BuildChromaBurst(
        IReadOnlyList<SosSection>? sos,
        DecodeFilterOptions options,
        double nyquistHz,
        int blockLength)
    {
        if (sos is null)
        {
            return null;
        }

        Complex[] response = IirFilterDesign.FrequencyResponse(sos, blockLength);
        MakeZeroPhaseMagnitudeSquared(response);

        return response;
    }

    public static TransferFunction? BuildChromaFinalFilter(
        FormatParameterSet parameters,
        double outputSampleRateHz,
        bool colorUnderFormat = true)
    {
        if (outputSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSampleRateHz));
        }

        if (parameters.TapeFormat == "LD" || !FormatCatalog.IsColorUnder(parameters.TapeFormat))
        {
            return null;
        }

        if (!TryJsonDouble(parameters.SysParams, "fsc_mhz", out double fscMHz)
            || !TryJsonDouble(parameters.RfParams, "color_under_carrier", out double colorUnderHz))
        {
            return null;
        }

        double nyquistHz = outputSampleRateHz / 2.0;
        double fscHz = fscMHz * 1_000_000.0;
        double lowerHz;
        double upperHz;
        if (colorUnderFormat)
        {
            lowerHz = fscHz - (colorUnderHz * 0.9);
            upperHz = fscHz + (colorUnderHz * 0.75);
        }
        else
        {
            lowerHz = fscHz - 100_000.0;
            upperHz = fscHz + 100_000.0;
        }

        if (lowerHz <= 0.0 || upperHz <= lowerHz || upperHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Final chroma band-pass frequencies must be inside the TBC output Nyquist range.");
        }

        return IirFilterDesign.ButterworthBandPass(
            order: 4,
            normalizedLowCutoff: lowerHz / nyquistHz,
            normalizedHighCutoff: upperHz / nyquistHz);
    }

    public static SosSection[]? BuildChromaFinalSosFilter(
        FormatParameterSet parameters,
        double outputSampleRateHz,
        bool colorUnderFormat = true)
    {
        if (outputSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSampleRateHz));
        }

        if (parameters.TapeFormat == "LD" || !FormatCatalog.IsColorUnder(parameters.TapeFormat))
        {
            return null;
        }

        if (!TryJsonDouble(parameters.SysParams, "fsc_mhz", out double fscMHz)
            || !TryJsonDouble(parameters.RfParams, "color_under_carrier", out double colorUnderHz))
        {
            return null;
        }

        double nyquistHz = outputSampleRateHz / 2.0;
        double fscHz = fscMHz * 1_000_000.0;
        double lowerHz;
        double upperHz;
        if (colorUnderFormat)
        {
            lowerHz = fscHz - (colorUnderHz * 0.9);
            upperHz = fscHz + (colorUnderHz * 0.75);
        }
        else
        {
            lowerHz = fscHz - 100_000.0;
            upperHz = fscHz + 100_000.0;
        }

        if (lowerHz <= 0.0 || upperHz <= lowerHz || upperHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Final chroma band-pass frequencies must be inside the TBC output Nyquist range.");
        }

        return IirFilterDesign.ButterworthBandPassSos(
            order: 4,
            normalizedLowCutoff: lowerHz / nyquistHz,
            normalizedHighCutoff: upperHz / nyquistHz);
    }

    public static TransferFunction BuildChromaDeemphasisFilter(FormatParameterSet parameters, double outputSampleRateHz)
    {
        if (outputSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSampleRateHz));
        }

        double nyquistHz = outputSampleRateHz / 2.0;
        double fscHz = JsonDouble(parameters.SysParams, "fsc_mhz") * 1_000_000.0;
        if (fscHz <= 0.0 || fscHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Chroma deemphasis center frequency must be inside the TBC output Nyquist range.");
        }

        return IirFilterDesign.PeakingConstantQ(
            normalizedFrequency: fscHz / nyquistHz,
            gainDb: 3.4,
            bandwidthOctaves: 500_000.0 / nyquistHz);
    }

    public static TransferFunction BuildChromaAfcBandPassFilter(FormatParameterSet parameters, double decodeSampleRateHz)
    {
        double lowCutHz = JsonDoubleOrDefault(parameters.RfParams, "chroma_bpf_lower", 60_000.0);
        double highCutHz = JsonDouble(parameters.RfParams, "chroma_bpf_upper");
        int order = JsonInt(parameters.RfParams, "chroma_bpf_order", defaultValue: 4) ?? 4;
        return BuildChromaAfcBandPassFilter(lowCutHz, highCutHz, order, decodeSampleRateHz);
    }

    public static TransferFunction BuildChromaAfcBandPassFilter(
        double lowCutHz,
        double highCutHz,
        int order,
        double decodeSampleRateHz)
    {
        if (decodeSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeSampleRateHz));
        }

        double nyquistHz = decodeSampleRateHz / 2.0;
        if (lowCutHz <= 0.0 || highCutHz <= lowCutHz || highCutHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(highCutHz), "Chroma AFC band-pass frequencies must be inside the decode Nyquist range.");
        }

        return IirFilterDesign.ButterworthBandPass(order, lowCutHz / nyquistHz, highCutHz / nyquistHz);
    }

    public static SosSection[] BuildChromaAfcBandPassSosFilter(
        FormatParameterSet parameters,
        double decodeSampleRateHz)
    {
        double lowCutHz = JsonDoubleOrDefault(parameters.RfParams, "chroma_bpf_lower", 60_000.0);
        double highCutHz = JsonDouble(parameters.RfParams, "chroma_bpf_upper");
        int order = JsonInt(parameters.RfParams, "chroma_bpf_order", defaultValue: 4) ?? 4;
        return BuildChromaAfcBandPassSosFilter(lowCutHz, highCutHz, order, decodeSampleRateHz);
    }

    public static SosSection[] BuildChromaAfcBandPassSosFilter(
        double lowCutHz,
        double highCutHz,
        int order,
        double decodeSampleRateHz)
    {
        if (decodeSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeSampleRateHz));
        }

        double nyquistHz = decodeSampleRateHz / 2.0;
        if (lowCutHz <= 0.0 || highCutHz <= lowCutHz || highCutHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(highCutHz), "Chroma AFC band-pass frequencies must be inside the decode Nyquist range.");
        }

        return IirFilterDesign.ButterworthBandPassSos(
            order,
            lowCutHz / nyquistHz,
            highCutHz / nyquistHz);
    }

    public static ChromaAfcMeasurementFilterSet BuildChromaAfcMeasurementFilters(
        FormatParameterSet parameters,
        double outputSampleRateHz)
    {
        if (outputSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSampleRateHz));
        }

        double nyquistHz = outputSampleRateHz / 2.0;
        double carrierHz = JsonDouble(parameters.RfParams, "color_under_carrier");
        double lineFrequencyHz = JsonDouble(parameters.SysParams, "FPS")
            * JsonDouble(parameters.SysParams, "frame_lines");
        double stopFrequencyHz = carrierHz + (24.0 * lineFrequencyHz);
        if (carrierHz <= 0.0 || stopFrequencyHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters),
                "Chroma AFC measurement filters must fit inside the TBC output Nyquist range.");
        }

        (int order, double normalizedCutoff) = IirFilterDesign.ButterworthLowPassOrder(
            carrierHz / nyquistHz,
            stopFrequencyHz / nyquistHz,
            passRippleDb: 3.0,
            stopAttenuationDb: 30.0);
        order = Math.Min(order, 200);
        return new ChromaAfcMeasurementFilterSet(
            IirFilterDesign.ButterworthHighPass(order, normalizedCutoff),
            IirFilterDesign.ButterworthLowPass(order, normalizedCutoff));
    }

    public static TransferFunction? BuildChromaAudioNotchFilter(FormatParameterSet parameters, double outputSampleRateHz)
    {
        if (outputSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSampleRateHz));
        }

        double notchHz = JsonDoubleOrDefault(parameters.RfParams, "chroma_audio_notch_freq", 0.0);
        if (notchHz <= 0.0)
        {
            return null;
        }

        double nyquistHz = outputSampleRateHz / 2.0;
        if (notchHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Chroma audio notch frequency must be inside the TBC output Nyquist range.");
        }

        return IirFilterDesign.Notch(notchHz / nyquistHz, q: 10.0);
    }

    public static TransferFunction? BuildChromaVideoNotchFilter(DecodeFilterOptions filterOptions, double outputSampleRateHz)
    {
        if (outputSampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSampleRateHz));
        }

        if (filterOptions.VideoNotchHz is not { } notchHz || notchHz <= 0.0)
        {
            return null;
        }

        double nyquistHz = outputSampleRateHz / 2.0;
        if (notchHz >= nyquistHz)
        {
            return null;
        }

        return IirFilterDesign.Notch(notchHz / nyquistHz, filterOptions.VideoNotchQ);
    }

    private static LaserDiscAnalogAudioChannelFilter BuildLdAnalogAudioChannel(
        double centerFrequencyHz,
        double filterWidthHz,
        int filterOrder,
        double sampleRateHz,
        double nyquistHz,
        int blockLength)
    {
        (int lowBin, int binCount, double sliceSampleRateHz) = DetermineFftSlice(
            centerFrequencyHz,
            minBandwidthHz: 200_000.0,
            sampleRateHz,
            blockLength);
        double lowFrequencyHz = sampleRateHz * lowBin / blockLength;
        double[] taps = FirWinBandPass(
            filterOrder,
            (centerFrequencyHz - filterWidthHz) / nyquistHz,
            (centerFrequencyHz + filterWidthHz) / nyquistHz);
        Complex[] fullResponse = IirFilterDesign.FrequencyResponse(new TransferFunction(taps, [1.0]), blockLength);
        Complex[] stage1 = SliceSpectrum(fullResponse, lowBin, binCount, blockLength);
        double[] hilbert = PortedMath.BuildHilbertMultiplier(binCount);
        for (int i = 0; i < stage1.Length; i++)
        {
            stage1[i] *= hilbert[i];
        }

        Complex[] stage2 = BuildLdAnalogAudioStage2Filter(sliceSampleRateHz, blockLength);
        return new LaserDiscAnalogAudioChannelFilter(
            lowBin,
            binCount,
            sliceSampleRateHz,
            lowFrequencyHz,
            centerFrequencyHz,
            stage1,
            stage2);
    }

    private static Complex[] BuildLdAnalogAudioStage2Filter(double sliceSampleRateHz, int blockLength)
    {
        double nyquistHz = sliceSampleRateHz / 2.0;
        (int order, double normalizedCutoff) = IirFilterDesign.ButterworthLowPassOrder(
            normalizedPassFrequency: 20_000.0 / nyquistHz,
            normalizedStopFrequency: 24_000.0 / nyquistHz,
            passRippleDb: 1.0,
            stopAttenuationDb: 9.0);
        TransferFunction lowPassTransfer = IirFilterDesign.ButterworthLowPassTransferFunction(
            order,
            normalizedCutoff);
        Complex[] lowPass = IirFilterDesign.FrequencyResponse(lowPassTransfer, blockLength);
        TransferFunction deemphasisTransfer = IirFilterDesign.EmphasisIir(
            5.3e-6,
            75e-6,
            sliceSampleRateHz);
        Complex[] deemphasis = IirFilterDesign.FrequencyResponse(
            deemphasisTransfer,
            blockLength);
        return Multiply(lowPass, deemphasis);
    }

    private static (int LowBin, int BinCount, double SliceSampleRateHz) DetermineFftSlice(
        double centerFrequencyHz,
        double minBandwidthHz,
        double sampleRateHz,
        int blockLength)
    {
        double binWidth = sampleRateHz / blockLength;
        int centerBin = RoundToEven(centerFrequencyHz / binWidth);
        int bandwidthBins = Math.Max(1, RoundToEven(minBandwidthHz / binWidth));
        int binCount = 2 * NextPowerOfTwo(bandwidthBins * 2);
        int lowBin = centerBin - (binCount / 4);
        if (lowBin < 0 || lowBin + (binCount / 2) >= blockLength)
        {
            throw new ArgumentOutOfRangeException(nameof(centerFrequencyHz), "LD analog audio FFT slice is outside the RF block spectrum.");
        }

        return (lowBin, binCount, binWidth * binCount);
    }

    public static Complex[] SliceSpectrum(ReadOnlySpan<Complex> spectrum, int lowBin, int binCount, int blockLength)
    {
        if (spectrum.Length != blockLength)
        {
            throw new ArgumentException("Spectrum length must match the source block length.", nameof(spectrum));
        }

        if (binCount <= 0 || (binCount % 2) != 0)
        {
            throw new ArgumentException("FFT slice bin count must be positive and even.", nameof(binCount));
        }

        int half = binCount / 2;
        if (lowBin < 0 || lowBin + half > blockLength || blockLength - lowBin < half)
        {
            throw new ArgumentOutOfRangeException(nameof(lowBin));
        }

        var output = new Complex[binCount];
        for (int i = 0; i < half; i++)
        {
            output[i] = spectrum[lowBin + i];
            output[half + i] = spectrum[blockLength - lowBin - half + i];
        }

        return output;
    }

    private static Complex[] BuildVideoLowPass(
        JsonElement rfParams,
        DecodeFilterOptions options,
        double nyquistHz,
        int blockLength,
        bool zeroPhaseMagnitude)
    {
        double corner = JsonDouble(rfParams, "video_lpf_freq");
        int order = JsonInt(rfParams, "video_lpf_order", defaultValue: 1) ?? 1;

        Complex[] response;
        if (JsonBool(rfParams, "video_lpf_supergauss", defaultValue: false))
        {
            double[] half = FrequencyDomainFilter.LowPassSuperGaussianHalf(
                corner,
                order,
                nyquistHz,
                blockLength);
            response = FrequencyDomainFilter.MirrorHalfToFull(half)
                .Select(value => new Complex(value, 0.0))
                .ToArray();
        }
        else
        {
            response = zeroPhaseMagnitude && (order & 1) == 0
                ? IirFilterDesign.FrequencyResponse(
                    IirFilterDesign.ButterworthLowPassScipySos(order, corner / nyquistHz),
                    blockLength)
                : IirFilterDesign.FrequencyResponse(
                    IirFilterDesign.ButterworthLowPassTransferFunction(order, corner / nyquistHz),
                    blockLength);
            if (zeroPhaseMagnitude)
            {
                for (int i = 0; i < response.Length; i++)
                {
                    response[i] = new Complex(NumpyComplexMagnitude(response[i]), 0.0);
                }
            }
        }

        ApplyLdNtscColorNotchIfPresent(response, options, corner, nyquistHz, blockLength);
        return response;
    }

    private static Complex[] BuildVideo(
        string system,
        JsonElement rfParams,
        bool useLaserDiscGroupDelayEqualizer,
        Complex[] videoLowPass,
        double sampleRateHz,
        int blockLength,
        DecodeFilterOptions options)
    {
        Complex[] video = videoLowPass.ToArray();
        ApplyVideoDeemphasis(video, rfParams, sampleRateHz, blockLength, useConfiguredStrength: true);

        if (useLaserDiscGroupDelayEqualizer && options.LdVideoGroupDelayEqualizer)
        {
            Complex[] groupDelayEqualizer = BuildLdGroupDelayEqualizer(
                system,
                rfParams,
                videoLowPass,
                sampleRateHz,
                blockLength);
            for (int i = 0; i < video.Length; i++)
            {
                video[i] = NumpyVectorComplexMultiply(video[i], groupDelayEqualizer[i]);
            }
        }

        ApplyCustomVideoFilters(video, rfParams, sampleRateHz, blockLength);
        return video;
    }

    private static Complex[] BuildVideoReference(
        JsonElement rfParams,
        Complex[] videoLowPass,
        double sampleRateHz,
        int blockLength)
    {
        Complex[] video = videoLowPass.ToArray();
        ApplyVideoDeemphasis(video, rfParams, sampleRateHz, blockLength, useConfiguredStrength: false);
        return video;
    }

    private static void ApplyVideoDeemphasis(
        Complex[] video,
        JsonElement rfParams,
        double sampleRateHz,
        int blockLength,
        bool useConfiguredStrength)
    {
        if (rfParams.TryGetProperty("deemph_gain", out JsonElement gainElement)
            && gainElement.ValueKind != JsonValueKind.Null
            && rfParams.TryGetProperty("deemph_mid", out JsonElement midpointElement)
            && midpointElement.ValueKind != JsonValueKind.Null)
        {
            double q = JsonDoubleOrDefault(rfParams, "deemph_q", 0.5);
            Complex[] deemphasis = IirFilterDesign.FrequencyResponse(
                IirFilterDesign.VideoDeEmphasisShelf(
                    sampleRateHz,
                    gainElement.GetDouble(),
                    midpointElement.GetDouble(),
                    q),
                blockLength);

            for (int i = 0; i < video.Length; i++)
            {
                video[i] = NumpyVectorComplexMultiply(video[i], deemphasis[i]);
            }
        }
        else if (rfParams.TryGetProperty("video_deemp", out JsonElement timeConstantsElement)
            && timeConstantsElement.ValueKind == JsonValueKind.Array
            && timeConstantsElement.GetArrayLength() >= 2)
        {
            Complex[] deemphasis = IirFilterDesign.FrequencyResponse(
                IirFilterDesign.EmphasisIir(
                    timeConstantsElement[0].GetDouble(),
                    timeConstantsElement[1].GetDouble(),
                    sampleRateHz),
                blockLength);
            double strength = useConfiguredStrength
                ? JsonDoubleOrDefault(rfParams, "video_deemp_strength", 1.0)
                : 1.0;

            for (int i = 0; i < video.Length; i++)
            {
                Complex factor = strength == 1.0
                    ? deemphasis[i]
                    : Complex.Pow(deemphasis[i], strength);
                video[i] = NumpyVectorComplexMultiply(video[i], factor);
            }
        }
    }

    private static Complex[] BuildLdGroupDelayEqualizer(
        string system,
        JsonElement rfParams,
        Complex[] videoLowPass,
        double sampleRateHz,
        int blockLength)
    {
        double[] frequencies;
        double[] targetDelays;
        if (FormatCatalog.ParentSystem(system) == "PAL")
        {
            frequencies = [0.0, 0.5e6, 2.0e6, 3.0e6, 4.0e6, 4.4336e6, 4.8e6, 5.5e6];
            targetDelays = [0.0, 0.0, 10e-9, 35e-9, 85e-9, 135e-9, 200e-9, 200e-9];
        }
        else
        {
            frequencies = [0.0, 0.5e6, 2.0e6, 3.0e6, 3.58e6, 4.0e6, 4.2e6, 4.8e6];
            targetDelays = [0.0, 0.0, 15e-9, 45e-9, 80e-9, 135e-9, 200e-9, 200e-9];
        }

        var binFrequencies = new double[blockLength];
        for (int i = 0; i < binFrequencies.Length; i++)
        {
            binFrequencies[i] = i <= blockLength / 2
                ? i * sampleRateHz / blockLength
                : (blockLength - i) * sampleRateHz / blockLength;
        }

        double[] target = InterpolatePiecewiseLinear(frequencies, targetDelays, binFrequencies);
        double[] phase = PortedMath.UnwrapAngles(videoLowPass.Select(value => value.Phase).ToArray());
        var lpfGroupDelay = new double[blockLength];
        double binWidthHz = sampleRateHz / blockLength;
        for (int i = 0; i < blockLength; i++)
        {
            double derivative;
            if (i == 0)
            {
                derivative = phase[1] - phase[0];
            }
            else if (i == blockLength - 1)
            {
                derivative = phase[^1] - phase[^2];
            }
            else
            {
                derivative = (phase[i + 1] - phase[i - 1]) / 2.0;
            }

            lpfGroupDelay[i] = -derivative / (Math.Tau * binWidthHz);
        }

        int half = blockLength / 2;
        int halfMegahertzBin = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < binFrequencies.Length; i++)
        {
            double distance = Math.Abs(binFrequencies[i] - 500_000.0);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                halfMegahertzBin = i;
            }
        }

        double lpfFrequency = JsonDouble(rfParams, "video_lpf_freq");
        double taperStart = lpfFrequency + 300_000.0;
        double taperEnd = lpfFrequency + 1_300_000.0;
        var equalizer = new Complex[blockLength];
        double cumulative = 0.0;
        for (int i = 0; i <= half; i++)
        {
            double residual = target[i] - (lpfGroupDelay[i] - lpfGroupDelay[halfMegahertzBin]);
            double taper = Math.Clamp((taperEnd - binFrequencies[i]) / (taperEnd - taperStart), 0.0, 1.0);
            residual *= taper;
            if (binFrequencies[i] < 400_000.0)
            {
                residual = 0.0;
            }

            cumulative += residual;
            double phaseRadians = -Math.Tau * cumulative * binWidthHz;
            equalizer[i] = Complex.FromPolarCoordinates(1.0, phaseRadians);
        }

        for (int i = 1; i < half; i++)
        {
            equalizer[blockLength - i] = Complex.Conjugate(equalizer[i]);
        }

        equalizer[0] = Complex.One;
        equalizer[half] = Complex.One;
        return equalizer;
    }

    private static Complex[] BuildVideoLowPass05(Complex[] video, double nyquistHz, int blockLength)
    {
        if (500_000.0 >= nyquistHz)
        {
            return video.ToArray();
        }

        Complex[] halfMegahertzLowPass = IirFilterDesign.FrequencyResponse(
            new TransferFunction(
                FirWinLowPass(
                    numTaps: 65,
                    normalizedCutoff: 0.5 / (nyquistHz / 1_000_000.0)),
                [1.0]),
            blockLength);
        return Multiply(video, halfMegahertzLowPass);
    }

    private static Complex[]? BuildLdVideoBurst(
        FormatParameterSet parameters,
        Complex[] videoReference,
        double nyquistHz,
        int blockLength)
    {
        if (parameters.TapeFormat != "LD")
        {
            return null;
        }

        double fscMhz = JsonDouble(parameters.SysParams, "fsc_mhz");
        Complex[] burst = BuildFirBandPassResponse(
            fscMhz,
            halfWidth: 0.2,
            numTaps: 81,
            nyquist: nyquistHz / 1_000_000.0,
            blockLength);
        return Multiply(videoReference, burst);
    }

    private static Complex[]? BuildCvbsVideoBurst(
        FormatParameterSet parameters,
        double nyquistHz,
        int blockLength)
    {
        if (parameters.TapeFormat != "CVBS")
        {
            return null;
        }

        double fscMhz = JsonDouble(parameters.SysParams, "fsc_mhz");
        return BuildFirBandPassResponse(
            fscMhz,
            halfWidth: 0.2,
            numTaps: 81,
            nyquist: nyquistHz / 1_000_000.0,
            blockLength);
    }

    private static Complex[]? BuildLdVideoPilot(
        FormatParameterSet parameters,
        Complex[] videoReference,
        double nyquistHz,
        int blockLength)
    {
        if (parameters.TapeFormat != "LD" || FormatCatalog.ParentSystem(parameters.System) != "PAL")
        {
            return null;
        }

        double pilotMhz = JsonDouble(parameters.SysParams, "pilot_mhz");
        double nyquistMhz = nyquistHz / 1_000_000.0;
        Complex[] pilot = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthBandPass(
                order: 1,
                normalizedLowCutoff: (pilotMhz - 0.1) / nyquistMhz,
                normalizedHighCutoff: (pilotMhz + 0.1) / nyquistMhz),
            blockLength);
        return Multiply(videoReference, pilot);
    }

    private static Complex[] BuildFirBandPassResponse(
        double centerFrequency,
        double halfWidth,
        int numTaps,
        double nyquist,
        int blockLength)
    {
        if (centerFrequency - halfWidth <= 0.0 || centerFrequency + halfWidth >= nyquist)
        {
            throw new ArgumentOutOfRangeException(nameof(centerFrequency), "Band-pass frequencies must be inside the Nyquist range.");
        }

        double[] taps = FirWinBandPass(
            numTaps,
            (centerFrequency - halfWidth) / nyquist,
            (centerFrequency + halfWidth) / nyquist);
        return IirFilterDesign.FrequencyResponse(new TransferFunction(taps, [1.0]), blockLength);
    }

    private static double[] FirWinLowPass(int numTaps, double normalizedCutoff)
    {
        if (numTaps <= 0 || (numTaps % 2) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numTaps));
        }

        if (normalizedCutoff <= 0.0 || normalizedCutoff >= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedCutoff));
        }

        var taps = new double[numTaps];
        double center = (numTaps - 1) / 2.0;
        for (int n = 0; n < taps.Length; n++)
        {
            double sample = n - center;
            double ideal = normalizedCutoff * Sinc(normalizedCutoff * sample);
            double window = HammingWindowValue(n, numTaps);
            taps[n] = ideal * window;
        }

        double sum = NumpyPairwiseSum(taps);
        for (int n = 0; n < taps.Length; n++)
        {
            taps[n] /= sum;
        }

        return taps;
    }

    internal static double[] FirWinBandPass(int numTaps, double normalizedLowCutoff, double normalizedHighCutoff)
    {
        if (numTaps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numTaps));
        }

        if (normalizedLowCutoff <= 0.0 || normalizedHighCutoff >= 1.0 || normalizedLowCutoff >= normalizedHighCutoff)
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedLowCutoff));
        }

        var taps = new double[numTaps];
        double center = (numTaps - 1) / 2.0;
        for (int n = 0; n < taps.Length; n++)
        {
            double sample = n - center;
            double ideal = (normalizedHighCutoff * Sinc(normalizedHighCutoff * sample))
                - (normalizedLowCutoff * Sinc(normalizedLowCutoff * sample));
            double window = HammingWindowValue(n, numTaps);
            taps[n] = ideal * window;
        }

        double midpoint = 0.5 * (normalizedLowCutoff + normalizedHighCutoff);
        var scaleProducts = new double[taps.Length];
        for (int n = 0; n < taps.Length; n++)
        {
            double sample = n - center;
            scaleProducts[n] = taps[n] * Math.Cos(Math.PI * sample * midpoint);
        }

        double scale = NumpyPairwiseSum(scaleProducts);
        if (scale != 0.0)
        {
            for (int n = 0; n < taps.Length; n++)
            {
                taps[n] /= scale;
            }
        }

        return taps;
    }

    private static double Sinc(double value)
    {
        if (Math.Abs(value) < 1e-12)
        {
            return 1.0;
        }

        double scaled = Math.PI * value;
        return Math.Sin(scaled) / scaled;
    }

    private static double HammingWindowValue(int index, int length)
    {
        if (length == 1)
        {
            return 1.0;
        }

        double delta = Math.PI - (-Math.PI);
        double phase = index == length - 1
            ? Math.PI
            : (index * (delta / (length - 1.0))) - Math.PI;
        double value = 0.54 * Math.Cos(0.0 * phase);
        value += (1.0 - 0.54) * Math.Cos(phase);
        return value;
    }

    private static double NumpyPairwiseSum(ReadOnlySpan<double> values)
    {
        const int blockSize = 128;
        if (values.Length < 8)
        {
            double sum = -0.0;
            for (int i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            return sum;
        }

        if (values.Length > blockSize)
        {
            int split = values.Length / 2;
            split -= split % 8;
            return NumpyPairwiseSum(values[..split]) + NumpyPairwiseSum(values[split..]);
        }

        double r0 = values[0];
        double r1 = values[1];
        double r2 = values[2];
        double r3 = values[3];
        double r4 = values[4];
        double r5 = values[5];
        double r6 = values[6];
        double r7 = values[7];
        int blockEnd = values.Length - (values.Length % 8);
        int index = 8;
        for (; index < blockEnd; index += 8)
        {
            r0 += values[index];
            r1 += values[index + 1];
            r2 += values[index + 2];
            r3 += values[index + 3];
            r4 += values[index + 4];
            r5 += values[index + 5];
            r6 += values[index + 6];
            r7 += values[index + 7];
        }

        double result = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
        for (; index < values.Length; index++)
        {
            result += values[index];
        }

        return result;
    }

    private static int RoundToEven(double value)
    {
        return (int)Math.Round(value, MidpointRounding.ToEven);
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        int output = 1;
        while (output < value)
        {
            output <<= 1;
        }

        return output;
    }

    private static double[] BuildNaturalCubicSecondDerivatives(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length || x.Length < 2)
        {
            throw new ArgumentException("Cubic interpolation requires matching x/y arrays.");
        }

        int n = x.Length;
        var second = new double[n];
        if (n == 2)
        {
            return second;
        }

        var u = new double[n - 1];
        for (int i = 1; i < n - 1; i++)
        {
            double sig = (x[i] - x[i - 1]) / (x[i + 1] - x[i - 1]);
            double p = (sig * second[i - 1]) + 2.0;
            second[i] = (sig - 1.0) / p;
            double slopeNext = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
            double slopePrevious = (y[i] - y[i - 1]) / (x[i] - x[i - 1]);
            u[i] = ((6.0 * (slopeNext - slopePrevious) / (x[i + 1] - x[i - 1])) - (sig * u[i - 1])) / p;
        }

        for (int k = n - 2; k >= 0; k--)
        {
            second[k] = (second[k] * second[k + 1]) + u[k];
        }

        return second;
    }

    private static double InterpolateNaturalCubic(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        ReadOnlySpan<double> secondDerivatives,
        double value)
    {
        if (value <= x[0])
        {
            return y[0];
        }

        if (value >= x[^1])
        {
            return y[^1];
        }

        int low = 0;
        int high = x.Length - 1;
        while (high - low > 1)
        {
            int middle = (high + low) / 2;
            if (x[middle] > value)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        double width = x[high] - x[low];
        double a = (x[high] - value) / width;
        double b = (value - x[low]) / width;
        return (a * y[low])
            + (b * y[high])
            + ((((a * a * a) - a) * secondDerivatives[low]
                + (((b * b * b) - b) * secondDerivatives[high])) * width * width / 6.0);
    }

    private static double[] InterpolatePiecewiseLinear(
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        ReadOnlySpan<double> values)
    {
        if (x.Length != y.Length || x.Length < 2)
        {
            throw new ArgumentException("Linear interpolation requires matching x/y arrays.");
        }

        var output = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];
            if (value <= x[0])
            {
                output[i] = y[0];
                continue;
            }

            if (value >= x[^1])
            {
                output[i] = y[^1];
                continue;
            }

            int high = 1;
            while (high < x.Length - 1 && x[high] < value)
            {
                high++;
            }

            int low = high - 1;
            double fraction = (value - x[low]) / (x[high] - x[low]);
            output[i] = y[low] + ((y[high] - y[low]) * fraction);
        }

        return output;
    }

    private static void ApplyCustomVideoFilters(Complex[] target, JsonElement rfParams, double sampleRateHz, int blockLength)
    {
        if (!rfParams.TryGetProperty("video_custom_luma_filters", out JsonElement filtersElement)
            || filtersElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement filterElement in filtersElement.EnumerateArray())
        {
            string? type = filterElement.GetProperty("type").GetString();
            Complex[]? response = type switch
            {
                "file" => TryLoadCustomFilter(
                    filterElement.GetProperty("filename").GetString() ?? string.Empty,
                    sampleRateHz,
                    blockLength),
                "highshelf" => IirFilterDesign.FrequencyResponse(
                    IirFilterDesign.Shelf(
                        filterElement.GetProperty("midfreq").GetDouble(),
                        filterElement.GetProperty("gain").GetDouble(),
                        ShelfKind.High,
                        sampleRateHz / 2.0,
                        filterElement.GetProperty("q").GetDouble()),
                    blockLength),
                "lowshelf" => IirFilterDesign.FrequencyResponse(
                    IirFilterDesign.Shelf(
                        filterElement.GetProperty("midfreq").GetDouble(),
                        filterElement.GetProperty("gain").GetDouble(),
                        ShelfKind.Low,
                        sampleRateHz / 2.0,
                        filterElement.GetProperty("q").GetDouble()),
                    blockLength),
                _ => null
            };

            if (response is null || response.Length != target.Length)
            {
                continue;
            }

            for (int i = 0; i < target.Length; i++)
            {
                target[i] *= response[i];
            }
        }
    }

    private static void ApplyRfInputNotchIfPresent(Complex[] target, DecodeFilterOptions options, double nyquistHz, int blockLength)
    {
        if (options.VideoNotchHz is not { } notchHz)
        {
            return;
        }

        if (notchHz <= 0.0 || notchHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Video notch frequency must be between 0 Hz and Nyquist.");
        }

        Complex[] notch = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.Notch(notchHz / nyquistHz, options.VideoNotchQ),
            blockLength);

        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= notch[i].Magnitude;
        }
    }

    private static void ApplyLdNtscColorNotchIfPresent(
        Complex[] target,
        DecodeFilterOptions options,
        double videoLowPassHz,
        double nyquistHz,
        int blockLength)
    {
        if (!options.LdNtscColorNotch)
        {
            return;
        }

        const double highCutHz = 5_000_000.0;
        if (videoLowPassHz <= 0.0 || videoLowPassHz >= highCutHz || highCutHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "LD NTSC color notch requires a valid band between video_lpf_freq and 5 MHz.");
        }

        Complex[] colorNotch = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthBandStop(order: 3, videoLowPassHz / nyquistHz, highCutHz / nyquistHz),
            blockLength);

        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= colorNotch[i];
        }
    }

    private static Complex[]? TryLoadCustomFilter(string filename, double sampleRateHz, int blockLength)
    {
        if (filename.Length == 0)
        {
            return null;
        }

        string resourceFile = $"{filename}-{(int)sampleRateHz}.txt";
        Assembly assembly = typeof(DecodeFilterSetBuilder).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".Formats.custom.{resourceFile}", StringComparison.Ordinal));
        if (resourceName is null)
        {
            return null;
        }

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        var half = new List<Complex>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            half.Add(ParsePythonComplex(line));
        }

        if (half.Count == blockLength)
        {
            return half.ToArray();
        }

        if (half.Count != (blockLength / 2) + 1)
        {
            return null;
        }

        var output = new Complex[blockLength];
        for (int i = 0; i < half.Count; i++)
        {
            output[i] = half[i];
        }

        for (int i = 1; i < half.Count - 1; i++)
        {
            output[blockLength - i] = Complex.Conjugate(half[i]);
        }

        return output;
    }

    private static Complex ParsePythonComplex(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.EndsWith('j'))
        {
            throw new FormatException($"Invalid complex value '{value}'.");
        }

        string withoutSuffix = trimmed[..^1];
        int separator = FindImaginarySeparator(withoutSuffix);
        if (separator <= 0)
        {
            throw new FormatException($"Invalid complex value '{value}'.");
        }

        double real = double.Parse(withoutSuffix[..separator], CultureInfo.InvariantCulture);
        double imaginary = double.Parse(withoutSuffix[separator..], CultureInfo.InvariantCulture);
        return new Complex(real, imaginary);
    }

    private static int FindImaginarySeparator(string value)
    {
        for (int i = value.Length - 1; i > 0; i--)
        {
            char c = value[i];
            if ((c == '+' || c == '-') && value[i - 1] != 'e' && value[i - 1] != 'E')
            {
                return i;
            }
        }

        return -1;
    }

    private static double JsonDouble(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetDouble();
    }

    private static double JsonDoubleOrDefault(JsonElement element, string propertyName, double defaultValue)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind != JsonValueKind.Null
            ? property.GetDouble()
            : defaultValue;
    }

    private static bool TryJsonDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind != JsonValueKind.Null)
        {
            value = property.GetDouble();
            return true;
        }

        value = 0.0;
        return false;
    }

    private static int? JsonInt(JsonElement element, string propertyName, int? defaultValue = null)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind != JsonValueKind.Null
            ? property.GetInt32()
            : defaultValue;
    }

    private static bool JsonBool(JsonElement element, string propertyName, bool defaultValue)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind != JsonValueKind.Null
            ? property.GetBoolean()
            : defaultValue;
    }

    private static Complex[]? LowPassExtra(JsonElement rfParams, double nyquistHz, int blockLength)
    {
        if (!rfParams.TryGetProperty("video_lpf_extra", out JsonElement cornerElement)
            || cornerElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        int order = JsonInt(rfParams, "video_lpf_extra_order", defaultValue: 1) ?? 1;
        return IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthLowPass(order, cornerElement.GetDouble() / nyquistHz),
            blockLength);
    }

    private static Complex[]? HighPassExtra(JsonElement rfParams, double nyquistHz, int blockLength)
    {
        if (!rfParams.TryGetProperty("video_hpf_extra", out JsonElement cornerElement)
            || cornerElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        int order = JsonInt(rfParams, "video_hpf_extra_order", defaultValue: 1) ?? 1;
        return IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthHighPass(order, cornerElement.GetDouble() / nyquistHz),
            blockLength);
    }

    private static void ApplyPeakingIfPresent(Complex[] target, JsonElement rfParams, double nyquistHz, int blockLength)
    {
        if (!rfParams.TryGetProperty("video_rf_peak_freq", out JsonElement frequencyElement)
            || frequencyElement.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        double frequency = frequencyElement.GetDouble();
        if (frequency <= 0)
        {
            return;
        }

        double gain = JsonDoubleOrDefault(rfParams, "video_rf_peak_gain", 3.0);
        double bandwidth = JsonDoubleOrDefault(rfParams, "video_rf_peak_bandwidth", 2_500_000.0) / nyquistHz;
        Complex[] peaking = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.PeakingConstantQ(frequency / nyquistHz, gain, bandwidth),
            blockLength);

        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= NumpyComplexMagnitude(peaking[i]);
        }
    }

    private static void ApplyFmAudioNotchIfPresent(
        Complex[] target,
        JsonElement rfParams,
        DecodeFilterOptions options,
        double nyquistHz,
        int blockLength)
    {
        if (Math.Truncate(options.FmAudioNotchQ) <= 0.0)
        {
            return;
        }

        if (!TryJsonDouble(rfParams, "fm_audio_channel_0_freq", out double leftFrequency)
            || !TryJsonDouble(rfParams, "fm_audio_channel_1_freq", out double rightFrequency))
        {
            return;
        }

        double[] magnitude = BuildFmAudioNotchMagnitude(
            leftFrequency,
            rightFrequency,
            options.FmAudioNotchQ,
            nyquistHz,
            blockLength);
        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= magnitude[i];
        }
    }

    internal static double[] BuildFmAudioNotchMagnitude(
        double leftFrequencyHz,
        double rightFrequencyHz,
        double q,
        double nyquistHz,
        int blockLength)
    {
        Complex[] left = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.Notch(leftFrequencyHz / nyquistHz, q),
            blockLength);
        Complex[] right = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.Notch(rightFrequencyHz / nyquistHz, q),
            blockLength);
        Complex[] combined = Multiply(left, right);
        var magnitude = new double[combined.Length];
        for (int i = 0; i < magnitude.Length; i++)
        {
            magnitude[i] = NumpyComplexMagnitude(combined[i]);
        }

        return magnitude;
    }

    private static void ApplyNotchMagnitude(
        Complex[] target,
        double frequencyHz,
        double q,
        double nyquistHz,
        int blockLength)
    {
        Complex[] notch = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.Notch(frequencyHz / nyquistHz, q),
            blockLength);

        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= NumpyComplexMagnitude(notch[i]);
        }
    }

    private static void ApplyNotchMagnitudeSquared(
        Complex[] target,
        double frequencyHz,
        double q,
        double nyquistHz,
        int blockLength)
    {
        Complex[] notch = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.Notch(frequencyHz / nyquistHz, q),
            blockLength);

        for (int i = 0; i < target.Length; i++)
        {
            double magnitude = NumpyComplexMagnitude(notch[i]);
            target[i] *= magnitude * magnitude;
        }
    }

    private static void ApplyLdNtscAnalogAudioNotchIfPresent(
        Complex[] target,
        JsonElement sysParams,
        JsonElement rfParams,
        DecodeFilterOptions options,
        double nyquistHz,
        int blockLength)
    {
        if (!options.LdNtscAnalogAudioNotch
            || !TryJsonDouble(sysParams, "audio_lfreq", out double leftFrequency)
            || !TryJsonDouble(sysParams, "audio_rfreq", out double rightFrequency))
        {
            return;
        }

        int order = JsonInt(rfParams, "audio_notchorder", defaultValue: 2) ?? 2;
        double width = JsonDoubleOrDefault(rfParams, "audio_notchwidth", 200_000.0);
        Complex[] leftNotch = BuildBandStopResponse(
            leftFrequency,
            width,
            order,
            nyquistHz,
            blockLength);
        Complex[] rightNotch = BuildBandStopResponse(
            rightFrequency,
            width,
            order,
            nyquistHz,
            blockLength);
        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= leftNotch[i] * rightNotch[i];
        }
    }

    private static Complex[] BuildBandStopResponse(
        double centerFrequencyHz,
        double widthHz,
        int order,
        double nyquistHz,
        int blockLength)
    {
        double low = centerFrequencyHz - widthHz;
        double high = centerFrequencyHz + widthHz;
        if (low <= 0.0 || high >= nyquistHz || low >= high)
        {
            throw new ArgumentOutOfRangeException(nameof(centerFrequencyHz), "Band-stop notch frequencies must be between 0 Hz and Nyquist.");
        }

        return IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthBandStop(order, low / nyquistHz, high / nyquistHz),
            blockLength);
    }

    private static void MakeZeroPhaseMagnitudeSquared(Complex[] target)
    {
        for (int i = 0; i < target.Length; i++)
        {
            double magnitude = NumpyComplexMagnitude(target[i]);
            target[i] = new Complex(magnitude * magnitude, 0.0);
        }
    }

    private static Complex[] Multiply(Complex[] left, Complex[] right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Filter response lengths must match.");
        }

        var output = new Complex[left.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = NumpyVectorComplexMultiply(left[i], right[i]);
        }

        return output;
    }

    private static Complex NumpyVectorComplexMultiply(Complex left, Complex right)
    {
        return new Complex(
            Math.FusedMultiplyAdd(
                left.Real,
                right.Real,
                -(left.Imaginary * right.Imaginary)),
            Math.FusedMultiplyAdd(
                left.Real,
                right.Imaginary,
                left.Imaginary * right.Real));
    }

    private static double NumpyComplexMagnitude(Complex value)
    {
        double real = Math.Abs(value.Real);
        double imaginary = Math.Abs(value.Imaginary);
        if (double.IsInfinity(real) || double.IsInfinity(imaginary))
        {
            return double.PositiveInfinity;
        }

        if (double.IsNaN(real) || double.IsNaN(imaginary))
        {
            return double.NaN;
        }

        double larger = Math.Max(real, imaginary);
        double smaller = Math.Min(real, imaginary);
        if (larger == 0.0)
        {
            return 0.0;
        }

        double ratio = smaller / larger;
        return Math.Sqrt(Math.FusedMultiplyAdd(ratio, ratio, 1.0)) * larger;
    }

    private static void MultiplyIfPresent(Complex[] target, Complex[]? response)
    {
        if (response is null)
        {
            return;
        }

        if (target.Length != response.Length)
        {
            throw new ArgumentException("Filter response lengths must match.");
        }

        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= NumpyComplexMagnitude(response[i]);
        }
    }

    private static void ApplyRampIfPresent(Complex[] target, JsonElement rfParams, double nyquistHz, int blockLength)
    {
        if (!rfParams.TryGetProperty("boost_rf_linear_0", out JsonElement boostStartElement)
            || boostStartElement.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        double start = JsonDoubleOrDefault(rfParams, "start_rf_linear", 0.0);
        double boostStart = boostStartElement.GetDouble();
        double boostMax = JsonDoubleOrDefault(rfParams, "boost_rf_linear_20", 1.0);
        double[] ramp = FrequencyDomainFilter.RampFilter(start, boostStart, boostMax, nyquistHz, blockLength);
        bool doubleRamp = JsonBool(rfParams, "boost_rf_linear_double", defaultValue: false);

        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= ramp[i];
            if (doubleRamp)
            {
                target[i] *= ramp[i];
            }
        }
    }

    private static Complex[] Ones(int length)
    {
        var output = new Complex[length];
        Array.Fill(output, Complex.One);
        return output;
    }
}
