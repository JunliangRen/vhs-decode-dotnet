using System.Numerics;

namespace VHSDecode.Core.Dsp;

public sealed class RfDemodulator
{
    private SharpnessEqOptions? _sharpnessLeadingOptions;
    private TransferFunction? _sharpnessLeadingFilter;
    private double[]? _sharpnessLeadingState;

    public RfDemodulator(double sampleRateHz)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        SampleRateHz = sampleRateHz;
    }

    public double SampleRateHz { get; }

    public RfDemodulatedBlock Demodulate(
        ReadOnlySpan<double> input,
        ReadOnlySpan<Complex> rfVideoFilter,
        ReadOnlySpan<Complex> videoFilter)
    {
        return Demodulate(input, rfVideoFilter, IdentityFilter(input.Length), videoFilter, videoFilter, videoLowPassOffset: 0);
    }

    public RfDemodulatedBlock Demodulate(
        ReadOnlySpan<double> input,
        ReadOnlySpan<Complex> rfVideoFilter,
        ReadOnlySpan<Complex> videoFilter,
        ReadOnlySpan<Complex> videoLowPassFilter,
        int videoLowPassOffset = 0)
    {
        return Demodulate(input, rfVideoFilter, IdentityFilter(input.Length), videoFilter, videoLowPassFilter, videoLowPassOffset);
    }

    public RfDemodulatedBlock Demodulate(
        ReadOnlySpan<double> input,
        ReadOnlySpan<Complex> rfVideoFilter,
        ReadOnlySpan<Complex> rfHighPassFilter,
        ReadOnlySpan<Complex> videoFilter,
        ReadOnlySpan<Complex> videoLowPassFilter,
        int videoLowPassOffset = 0,
        bool removeLdPalV4300DSpur = false)
    {
        return Demodulate(
            input,
            rfVideoFilter,
            rfHighPassFilter,
            ReadOnlySpan<Complex>.Empty,
            videoFilter,
            videoLowPassFilter,
            videoLowPassOffset,
            removeLdPalV4300DSpur);
    }

    public RfDemodulatedBlock Demodulate(
        ReadOnlySpan<double> input,
        ReadOnlySpan<Complex> rfVideoFilter,
        ReadOnlySpan<Complex> rfHighPassFilter,
        ReadOnlySpan<Complex> rfMtfFilter,
        ReadOnlySpan<Complex> videoFilter,
        ReadOnlySpan<Complex> videoLowPassFilter,
        int videoLowPassOffset = 0,
        bool removeLdPalV4300DSpur = false,
        RfHighBoostOptions? rfHighBoost = null,
        DiffDemodRepairOptions? diffDemodRepair = null,
        ChromaTrapOptions? chromaTrap = null,
        SharpnessEqOptions? sharpnessEq = null,
        NonlinearDeemphasisOptions? nonlinearDeemphasis = null,
        SubDeemphasisOptions? subDeemphasis = null,
        double? betamaxFscNotchHz = null,
        RfVideoReferenceFilterSet? referenceFilters = null,
        RfFmDemodulatorMode fmDemodulatorMode = RfFmDemodulatorMode.ConjugateProduct,
        IReadOnlyList<SosSection>? vhsEnvelopeFilter = null,
        IReadOnlyList<SosSection>? vhsRfTopFilter = null,
        Complex[]? precomputedInputSpectrum = null)
    {
        if (input.IsEmpty)
        {
            return new RfDemodulatedBlock([], [], [], [], [], []);
        }

        bool useVhsRealFft = fmDemodulatorMode == RfFmDemodulatorMode.VhsRustApproximation;
        bool useVhsComplexRfHighBoostPath = useVhsRealFft
            && !removeLdPalV4300DSpur
            && rfHighBoost is { Multiplier: not 0.0 }
            && vhsEnvelopeFilter is not null
            && vhsRfTopFilter is not null;
        bool useVhsRealRfPath = useVhsRealFft
            && !removeLdPalV4300DSpur
            && !useVhsComplexRfHighBoostPath
            && (rfHighBoost is null || rfHighBoost.Multiplier == 0.0 || vhsRfTopFilter is not null);
        Complex[] analytic;
        double[] rfHighPass;
        Complex[]? rfFilteredSpectrum = null;
        double[]? hilbertMultiplier = null;
        Complex[]? vhsRfFilteredHalf = null;
        double[]? vhsEnvelopeSource = null;
        double[]? vhsRfFilteredReal = null;
        if (useVhsComplexRfHighBoostPath)
        {
            Complex[] inputSpectrum = PocketFftComplex.ForwardReal(input);
            Complex[] rfHighPassSpectrum = ApplyNumpyRealFrequencyFilter(
                inputSpectrum,
                rfHighPassFilter,
                input.Length);
            rfHighPass = ExtractReal(PocketFftComplex.Inverse(rfHighPassSpectrum));
            rfFilteredSpectrum = ApplyNumpyRealFrequencyFilter(
                inputSpectrum,
                rfVideoFilter,
                input.Length);
            if (!rfMtfFilter.IsEmpty)
            {
                rfFilteredSpectrum = ApplyNumpyRealFrequencyFilter(
                    rfFilteredSpectrum,
                    rfMtfFilter,
                    input.Length);
            }

            hilbertMultiplier = PortedMath.BuildHilbertMultiplier(rfFilteredSpectrum.Length);
            analytic = BuildVhsComplexAnalyticSignal(rfFilteredSpectrum, hilbertMultiplier);
            vhsEnvelopeSource = ExtractReal(analytic);
            vhsRfFilteredReal = ExtractReal(PocketFftComplex.Inverse(rfFilteredSpectrum));
        }
        else if (useVhsRealRfPath)
        {
            Complex[] inputSpectrum = PocketFftReal.Forward(input);
            Complex[] rfHighPassSpectrum = ApplyNumpyRealFrequencyFilter(
                inputSpectrum,
                rfHighPassFilter,
                input.Length);
            rfHighPass = PocketFftReal.Inverse(rfHighPassSpectrum, input.Length);
            vhsRfFilteredHalf = ApplyNumpyRealFrequencyFilter(
                inputSpectrum,
                rfVideoFilter,
                input.Length);
            if (!rfMtfFilter.IsEmpty)
            {
                vhsRfFilteredHalf = ApplyNumpyRealFrequencyFilter(
                    vhsRfFilteredHalf,
                    rfMtfFilter,
                    input.Length);
            }

            analytic = BuildVhsAnalyticSignal(
                vhsRfFilteredHalf,
                input.Length,
                out vhsRfFilteredReal);
            vhsEnvelopeSource = vhsRfFilteredReal;
        }
        else
        {
            Complex[] spectrum = precomputedInputSpectrum ?? PocketFftComplex.ForwardDuccRealFull(input);
            if (spectrum.Length != input.Length)
            {
                throw new ArgumentException(
                    "Precomputed input spectrum length must match the RF block length.",
                    nameof(precomputedInputSpectrum));
            }

            Complex[] rfHighPassSpectrum = ApplyFrequencyFilter(spectrum, rfHighPassFilter);
            PocketFftComplex.InverseDuccInPlace(rfHighPassSpectrum);
            rfHighPass = new double[rfHighPassSpectrum.Length];
            for (int i = 0; i < rfHighPass.Length; i++)
            {
                rfHighPass[i] = rfHighPassSpectrum[i].Real;
            }

            if (removeLdPalV4300DSpur)
            {
                spectrum = RemoveLdPalV4300DSpur(spectrum, SampleRateHz);
            }

            rfFilteredSpectrum = ApplyFrequencyFilter(spectrum, rfVideoFilter);
            if (!rfMtfFilter.IsEmpty)
            {
                MultiplyFrequencyFilterInPlace(rfFilteredSpectrum, rfMtfFilter);
            }

            hilbertMultiplier = PortedMath.BuildHilbertMultiplier(rfFilteredSpectrum.Length);
            Complex[] analyticSpectrum = rfFilteredSpectrum.ToArray();
            ApplyHilbertMultiplierInPlace(analyticSpectrum, hilbertMultiplier);
            PocketFftComplex.InverseDuccInPlace(analyticSpectrum);
            analytic = analyticSpectrum;
        }

        double[] envelope = vhsEnvelopeSource is not null && vhsEnvelopeFilter is not null
            ? BuildVhsEnvelope(vhsEnvelopeSource, vhsEnvelopeFilter)
            : BuildAnalyticMagnitudeEnvelope(analytic);
        bool vhsWeakRfSignal = false;
        if (vhsEnvelopeSource is not null)
        {
            for (int i = 0; i < envelope.Length; i++)
            {
                if (envelope[i] == 0.0)
                {
                    vhsWeakRfSignal = true;
                    break;
                }
            }
        }

        if (useVhsComplexRfHighBoostPath)
        {
            if (ApplyVhsComplexRfHighBoostIfPresent(
                rfFilteredSpectrum!,
                vhsRfFilteredReal!,
                envelope,
                rfHighBoost,
                vhsRfTopFilter!))
            {
                analytic = BuildVhsComplexAnalyticSignal(
                    rfFilteredSpectrum,
                    hilbertMultiplier!);
            }
        }
        else if (vhsRfFilteredHalf is not null
            && vhsRfFilteredReal is not null
            && vhsRfTopFilter is not null
            && ApplyVhsRfHighBoostIfPresent(
                vhsRfFilteredHalf,
                vhsRfFilteredReal,
                envelope,
                rfHighBoost,
                vhsRfTopFilter))
        {
            analytic = BuildVhsAnalyticSignal(
                vhsRfFilteredHalf,
                input.Length,
                out _);
        }
        else if (rfFilteredSpectrum is not null
            && ApplyRfHighBoostIfPresent(rfFilteredSpectrum, envelope, rfHighBoost))
        {
            Complex[] analyticSpectrum = rfFilteredSpectrum.ToArray();
            ApplyHilbertMultiplierInPlace(analyticSpectrum, hilbertMultiplier!);
            PocketFftComplex.InverseDuccInPlace(analyticSpectrum);
            analytic = analyticSpectrum;
        }

        double[] demodRaw = DemodulateAnalytic(analytic, fmDemodulatorMode);
        ApplyDiffDemodRepairIfPresent(demodRaw, analytic, diffDemodRepair, fmDemodulatorMode);
        if (sharpnessEq is not null)
        {
            demodRaw = ApplySharpnessEqStateful(demodRaw, sharpnessEq);
        }

        if (chromaTrap is not null)
        {
            demodRaw = ApplyChromaTrap(demodRaw, SampleRateHz, chromaTrap.FscHz);
        }

        ReadOnlySpan<double> demodVideoSource = demodRaw;
        double[]? clippedDemod = null;
        if (referenceFilters?.ClipDemodForVideo == true)
        {
            clippedDemod = ClipDemodForVideo(demodRaw, SampleRateHz);
            demodVideoSource = clippedDemod;
        }

        Complex[] demodSpectrum;
        Complex[] videoSpectrum;
        double[] video;
        if (useVhsRealFft)
        {
            demodSpectrum = PocketFftReal.Forward(demodVideoSource);
            videoSpectrum = ApplyNumpyRealFrequencyFilter(
                demodSpectrum,
                videoFilter,
                demodVideoSource.Length);
            video = PocketFftReal.Inverse(videoSpectrum, demodVideoSource.Length);
        }
        else
        {
            demodSpectrum = PocketFftComplex.ForwardDuccRealFull(demodVideoSource);
            videoSpectrum = ApplyFrequencyFilter(demodSpectrum, videoFilter);
            Complex[] videoComplex;
            if (nonlinearDeemphasis is null && subDeemphasis is null)
            {
                PocketFftComplex.InverseDuccInPlace(videoSpectrum);
                videoComplex = videoSpectrum;
            }
            else
            {
                videoComplex = PocketFftComplex.InverseDucc(videoSpectrum);
            }

            video = new double[videoComplex.Length];
            for (int i = 0; i < video.Length; i++)
            {
                video[i] = videoComplex[i].Real;
            }
        }

        ApplyNonlinearDeemphasisIfPresent(video, videoSpectrum, nonlinearDeemphasis, useVhsRealFft);
        ApplySubDeemphasisIfPresent(video, videoSpectrum, subDeemphasis, useVhsRealFft);
        if (betamaxFscNotchHz is { } fscNotchHz)
        {
            video = ApplyBetamaxFscNotch(video, SampleRateHz, fscNotchHz);
        }

        double[] videoLowPass;
        if (useVhsRealFft)
        {
            Complex[] videoLowPassSpectrum = ApplyNumpyRealFrequencyFilter(
                demodSpectrum,
                videoLowPassFilter,
                demodVideoSource.Length);
            videoLowPass = PocketFftReal.Inverse(videoLowPassSpectrum, demodVideoSource.Length);
        }
        else
        {
            Complex[] videoLowPassSpectrum = ApplyFrequencyFilter(demodSpectrum, videoLowPassFilter);
            PocketFftComplex.InverseDuccInPlace(videoLowPassSpectrum);
            videoLowPass = new double[videoLowPassSpectrum.Length];
            for (int i = 0; i < videoLowPass.Length; i++)
            {
                videoLowPass[i] = videoLowPassSpectrum[i].Real;
            }
        }

        if (videoLowPassOffset != 0)
        {
            videoLowPass = FrequencyDomainFilter.Roll(videoLowPass, -videoLowPassOffset);
        }

        double[]? videoBurst = useVhsRealFft
            ? DecodeRealReferenceIfPresent(
                demodSpectrum,
                referenceFilters?.VideoBurst,
                referenceFilters?.VideoBurstOffset ?? 0,
                demodVideoSource.Length)
            : DecodeReferenceIfPresent(
                demodSpectrum,
                referenceFilters?.VideoBurst,
                referenceFilters?.VideoBurstOffset ?? 0);
        double[]? videoPilot = useVhsRealFft
            ? DecodeRealReferenceIfPresent(
                demodSpectrum,
                referenceFilters?.VideoPilot,
                offset: 0,
                demodVideoSource.Length)
            : DecodeReferenceIfPresent(
                demodSpectrum,
                referenceFilters?.VideoPilot,
                offset: 0);

        return new RfDemodulatedBlock(
            video,
            demodRaw,
            analytic,
            envelope,
            videoLowPass,
            rfHighPass,
            VideoBurst: videoBurst,
            VideoPilot: videoPilot,
            VhsWeakRfSignal: vhsWeakRfSignal);
    }

    public static Complex[] RemoveLdPalV4300DSpur(ReadOnlySpan<Complex> spectrum, double sampleRateHz)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        Complex[] output = spectrum.ToArray();
        if (output.Length == 0)
        {
            return output;
        }

        int start = Math.Clamp((int)(output.Length * (8_420_000.0 / sampleRateHz)), 0, output.Length);
        int end = Math.Clamp((int)(1.0 + (output.Length * (8_600_000.0 / sampleRateHz))), start, output.Length);
        int count = end - start;
        if (count <= 0)
        {
            return output;
        }

        var magnitudes = new double[count];
        for (int i = 0; i < magnitudes.Length; i++)
        {
            Complex value = output[start + i];
            double realSquared = value.Real * value.Real;
            double imaginarySquared = value.Imaginary * value.Imaginary;
            magnitudes[i] = Math.Sqrt(realSquared + imaginarySquared);
        }

        (double mean, double standardDeviation) = NumpyReduction.MeanStandardDeviationFloat64(magnitudes);
        double threshold = mean + (standardDeviation * 3.0);
        for (int i = 0; i < magnitudes.Length; i++)
        {
            if (magnitudes[i] > threshold)
            {
                int bin = start + i;
                ZeroMirroredBin(output, bin - 1);
                ZeroMirroredBin(output, bin);
                ZeroMirroredBin(output, bin + 1);
            }
        }

        return output;
    }

    public static void ReplaceSpikes(
        double[] demod,
        double[] demodDiffed,
        double maxValue,
        int replaceStart = 8,
        int replaceEnd = 30)
    {
        if (demod.Length != demodDiffed.Length)
        {
            throw new ArgumentException("Diff demod length doesn't match demod length.", nameof(demodDiffed));
        }

        var toFix = new List<int>();
        for (int i = 0; i < demod.Length; i++)
        {
            if (demod[i] > maxValue)
            {
                toFix.Add(i);
            }
        }

        foreach (int i in toFix)
        {
            int start = Math.Max(i - replaceStart, 0);
            int end = Math.Min(i + replaceEnd, demodDiffed.Length - 1);
            if (start >= end)
            {
                continue;
            }

            if (Max(demodDiffed, start, end) < Max(demod, start, end))
            {
                Array.Copy(demodDiffed, start, demod, start, end - start);
            }
        }
    }

    public static double[] ApplyChromaTrap(ReadOnlySpan<double> luminance, double sampleRateHz, double fscHz)
        => ChromaTrapFilter.Apply(luminance, sampleRateHz, fscHz);

    public static double[] ApplyBetamaxFscNotch(
        ReadOnlySpan<double> video,
        double sampleRateHz,
        double fscHz)
    {
        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        double nyquistHz = sampleRateHz / 2.0;
        if (fscHz <= 0.0 || fscHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(fscHz), "Betamax fsc notch frequency must be between 0 Hz and Nyquist.");
        }

        TransferFunction notch = IirFilterDesign.Notch(fscHz / nyquistHz, q: 2.0);
        int defaultPadLength = IirFilter.DefaultPadLength(notch);
        return video.Length > defaultPadLength
            ? IirFilter.ApplyForwardBackward(notch, video)
            : IirFilter.ApplyForwardBackward(notch, video, padLength: 0);
    }

    public static double[] ApplySharpnessEq(
        ReadOnlySpan<double> demod,
        double sampleRateHz,
        SharpnessEqOptions options)
    {
        if (options.Level == 0.0)
        {
            return demod.ToArray();
        }

        TransferFunction highPass = BuildSharpnessHighPass(sampleRateHz, options);
        double[] leadingState = IirFilter.SteadyStateInitialConditions(highPass);
        return ApplySharpnessEqCore(demod, options, highPass, leadingState, out _);
    }

    internal double[] ApplySharpnessEqStateful(
        ReadOnlySpan<double> demod,
        SharpnessEqOptions options)
    {
        if (options.Level == 0.0)
        {
            return demod.ToArray();
        }

        if (_sharpnessLeadingFilter is null || _sharpnessLeadingOptions != options)
        {
            _sharpnessLeadingOptions = options;
            _sharpnessLeadingFilter = BuildSharpnessHighPass(SampleRateHz, options);
            _sharpnessLeadingState = IirFilter.SteadyStateInitialConditions(_sharpnessLeadingFilter);
        }

        double[] output = ApplySharpnessEqCore(
            demod,
            options,
            _sharpnessLeadingFilter,
            _sharpnessLeadingState!,
            out double[] finalState);
        _sharpnessLeadingState = finalState;
        return output;
    }

    private static TransferFunction BuildSharpnessHighPass(
        double sampleRateHz,
        SharpnessEqOptions options)
    {
        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        double nyquistHz = sampleRateHz / 2.0;
        if (options.CornerHz <= 0.0 || options.CornerHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sharpness EQ corner must be between 0 Hz and Nyquist.");
        }

        if (options.OrderLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sharpness EQ order limit must be positive.");
        }

        if (options.TransitionHz <= 0.0 || options.CornerHz + options.TransitionHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sharpness EQ transition must end below Nyquist.");
        }

        (int designedOrder, double normalizedCutoff) = IirFilterDesign.ButterworthLowPassOrder(
            options.CornerHz / nyquistHz,
            (options.CornerHz + options.TransitionHz) / nyquistHz,
            passRippleDb: 3.0,
            stopAttenuationDb: 30.0);
        int order = Math.Min(designedOrder, options.OrderLimit);
        return IirFilterDesign.ButterworthHighPassTransferFunction(order, normalizedCutoff);
    }

    private static double[] ApplySharpnessEqCore(
        ReadOnlySpan<double> demod,
        SharpnessEqOptions options,
        TransferFunction highPass,
        double[] leadingState,
        out double[] finalLeadingState)
    {
        int defaultPadLength = IirFilter.DefaultPadLength(highPass);
        double[] highBand = demod.Length > defaultPadLength
            ? IirFilter.ApplyForwardBackward(highPass, demod)
            : IirFilter.ApplyForwardBackward(highPass, demod, padLength: 0);
        int overlap = Math.Min(10, demod.Length);
        if (overlap > 0)
        {
            double[] leading = IirFilter.ApplyForward(
                highPass,
                demod[..overlap],
                leadingState,
                out finalLeadingState);
            Array.Copy(leading, highBand, overlap);
        }
        else
        {
            finalLeadingState = leadingState.ToArray();
        }

        var output = demod.ToArray();
        for (int i = 0; i < output.Length; i++)
        {
            double amplified = options.OrderLimit * highBand[i];
            double sharpened = options.Level * amplified;
            output[i] = sharpened + output[i];
        }

        return output;
    }

    public static double[] ApplyNonlinearDeemphasis(
        ReadOnlySpan<double> video,
        ReadOnlySpan<Complex> videoSpectrum,
        double sampleRateHz,
        NonlinearDeemphasisOptions options)
    {
        if (video.Length != videoSpectrum.Length)
        {
            throw new ArgumentException("Video spectrum length must match video length.", nameof(videoSpectrum));
        }

        Complex[] highPass = BuildNonlinearHighPassFilter(sampleRateHz, video.Length, options);
        Complex[] highSpectrum = ApplyFrequencyFilter(videoSpectrum, highPass);
        Complex[] highComplex = FastFourierTransform.Inverse(highSpectrum);
        double[] output = video.ToArray();
        for (int i = 0; i < output.Length; i++)
        {
            output[i] -= Math.Clamp(highComplex[i].Real, options.LimitLow, options.LimitHigh);
        }

        return output;
    }

    public static double[] ApplySubDeemphasis(
        ReadOnlySpan<double> video,
        ReadOnlySpan<Complex> videoSpectrum,
        double sampleRateHz,
        SubDeemphasisOptions options)
    {
        if (video.Length != videoSpectrum.Length)
        {
            throw new ArgumentException("Video spectrum length must match video length.", nameof(videoSpectrum));
        }

        if (options.Deviation <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sub-deemphasis deviation must be positive.");
        }

        double nyquistHz = sampleRateHz / 2.0;
        if (options.AmplitudeLowPassHz <= 0.0 || options.AmplitudeLowPassHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sub-deemphasis amplitude low-pass frequency must be between 0 Hz and Nyquist.");
        }

        Complex[] highPass = BuildNonlinearHighPassFilter(
            sampleRateHz,
            video.Length,
            options.HighPassHz,
            options.BandPassUpperHz,
            options.Order);
        Complex[] highSpectrum = ApplyFrequencyFilter(videoSpectrum, highPass);
        Complex[] highComplex = FastFourierTransform.Inverse(highSpectrum);
        double[] highPart = new double[highComplex.Length];
        for (int i = 0; i < highPart.Length; i++)
        {
            highPart[i] = highComplex[i].Real;
        }

        double[] amplitude = BuildAnalyticMagnitude(highPart);
        double deviation = options.Deviation / 2.0;
        for (int i = 0; i < amplitude.Length; i++)
        {
            amplitude[i] /= deviation;
        }

        SosSection[] amplitudeLowPass = IirFilterDesign.ButterworthLowPass(order: 1, options.AmplitudeLowPassHz / nyquistHz);
        int defaultPadLength = SosFilter.DefaultPadLength(amplitudeLowPass);
        amplitude = amplitude.Length > defaultPadLength
            ? SosFilter.ApplyForwardBackward(amplitudeLowPass, amplitude)
            : SosFilter.ApplyForwardBackward(amplitudeLowPass, amplitude, padLength: 0);
        for (int i = 0; i < amplitude.Length; i++)
        {
            amplitude[i] = Math.Max(0.0, amplitude[i]);
            if (options.Scaling1 is { } scaling1)
            {
                amplitude[i] *= scaling1;
            }

            amplitude[i] = Math.Pow(amplitude[i], options.ExponentialScaling);
            if (options.Scaling2 is { } scaling2)
            {
                amplitude[i] *= scaling2;
            }

            if (options.LogisticRate is { } logisticRate && logisticRate > 0.0)
            {
                double logisticMid = options.LogisticMid ?? 0.0;
                amplitude[i] *= 1.0 / (1.0 + Math.Exp(-logisticRate * (amplitude[i] - logisticMid)));
            }
        }

        double[] output = video.ToArray();
        double staticFactor = options.StaticFactor ?? 0.0;
        for (int i = 0; i < output.Length; i++)
        {
            double staticPart = highPart[i] * staticFactor;
            output[i] -= (highPart[i] * (1.0 - amplitude[i])) + staticPart;
        }

        return output;
    }

    public static Complex[] IdentityFilter(int length)
    {
        var output = new Complex[length];
        Array.Fill(output, Complex.One);
        return output;
    }

    private static Complex[] ApplyFrequencyFilter(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<Complex> filter)
    {
        if (filter.IsEmpty)
        {
            return spectrum.ToArray();
        }

        if (filter.Length != spectrum.Length)
        {
            throw new ArgumentException("Frequency filter length must match input block length.", nameof(filter));
        }

        var output = new Complex[spectrum.Length];
        NumpyComplexMultiply.Apply(spectrum, filter, output);

        return output;
    }

    private static Complex[] ApplyNumpyRealFrequencyFilter(
        ReadOnlySpan<Complex> spectrum,
        ReadOnlySpan<Complex> filter,
        int realLength)
    {
        if (filter.IsEmpty)
        {
            return spectrum.ToArray();
        }

        if (filter.Length != spectrum.Length && filter.Length != realLength)
        {
            throw new ArgumentException(
                "Frequency filter length must match the real block or half-spectrum length.",
                nameof(filter));
        }

        var output = new Complex[spectrum.Length];
        NumpyComplexMultiply.Apply(spectrum, filter[..spectrum.Length], output);

        return output;
    }

    private static Complex[] BuildVhsAnalyticSignal(
        ReadOnlySpan<Complex> filteredHalfSpectrum,
        int realLength,
        out double[] real)
    {
        if (filteredHalfSpectrum.Length != (realLength / 2) + 1)
        {
            throw new ArgumentException(
                "Filtered half-spectrum length does not match the real block length.",
                nameof(filteredHalfSpectrum));
        }

        var hilbertHalfSpectrum = new Complex[filteredHalfSpectrum.Length];
        for (int i = 1; i < hilbertHalfSpectrum.Length - 1; i++)
        {
            hilbertHalfSpectrum[i] = new Complex(
                filteredHalfSpectrum[i].Imaginary,
                -filteredHalfSpectrum[i].Real);
        }

        real = PocketFftReal.Inverse(filteredHalfSpectrum, realLength);
        double[] imaginary = PocketFftReal.Inverse(hilbertHalfSpectrum, realLength);
        var output = new Complex[realLength];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = new Complex(real[i], imaginary[i]);
        }

        return output;
    }

    private static Complex[] BuildVhsComplexAnalyticSignal(
        ReadOnlySpan<Complex> filteredSpectrum,
        ReadOnlySpan<double> hilbertMultiplier)
    {
        Complex[] analyticSpectrum = filteredSpectrum.ToArray();
        ApplyHilbertMultiplierInPlace(analyticSpectrum, hilbertMultiplier);
        return PocketFftComplex.Inverse(analyticSpectrum);
    }

    private static double[] ExtractReal(ReadOnlySpan<Complex> values)
    {
        var output = new double[values.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = values[i].Real;
        }

        return output;
    }

    private static double[] BuildAnalyticMagnitudeEnvelope(ReadOnlySpan<Complex> analytic)
    {
        var envelope = new double[analytic.Length];
        for (int i = 0; i < envelope.Length; i++)
        {
            envelope[i] = analytic[i].Magnitude;
        }

        return envelope;
    }

    private static double[] BuildVhsEnvelope(
        ReadOnlySpan<double> filteredReal,
        IReadOnlyList<SosSection> envelopeFilter)
    {
        var rawEnvelope = new double[filteredReal.Length];
        for (int i = 0; i < rawEnvelope.Length; i++)
        {
            rawEnvelope[i] = MathF.Abs((float)filteredReal[i]);
        }

        rawEnvelope = FrequencyDomainFilter.Roll(rawEnvelope, 4);
        return SosFilter.ApplyForwardBackwardFloat32(envelopeFilter, rawEnvelope);
    }

    private static double[]? DecodeReferenceIfPresent(
        ReadOnlySpan<Complex> spectrum,
        Complex[]? filter,
        int offset)
    {
        if (filter is null)
        {
            return null;
        }

        Complex[] filteredSpectrum = ApplyFrequencyFilter(spectrum, filter);
        PocketFftComplex.InverseDuccInPlace(filteredSpectrum);
        var output = new double[filteredSpectrum.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = filteredSpectrum[i].Real;
        }

        return offset == 0
            ? output
            : FrequencyDomainFilter.Roll(output, -offset);
    }

    private static double[]? DecodeRealReferenceIfPresent(
        ReadOnlySpan<Complex> spectrum,
        Complex[]? filter,
        int offset,
        int realLength)
    {
        if (filter is null)
        {
            return null;
        }

        Complex[] filteredSpectrum = ApplyNumpyRealFrequencyFilter(
            spectrum,
            filter,
            realLength);
        double[] output = PocketFftReal.Inverse(filteredSpectrum, realLength);
        return offset == 0
            ? output
            : FrequencyDomainFilter.Roll(output, -offset);
    }

    private static double[] ClipDemodForVideo(ReadOnlySpan<double> demod, double sampleRateHz)
    {
        double upper = sampleRateHz * 0.75;
        var output = new double[demod.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = Math.Clamp(demod[i], 1_500_000.0, upper);
        }

        return output;
    }

    private static void MultiplyFrequencyFilterInPlace(Complex[] spectrum, ReadOnlySpan<Complex> filter)
    {
        if (filter.Length != spectrum.Length)
        {
            throw new ArgumentException("Frequency filter length must match input block length.", nameof(filter));
        }

        NumpyComplexMultiply.ApplyInPlace(spectrum, filter);
    }

    private static bool ApplyVhsRfHighBoostIfPresent(
        Complex[] rfFilteredHalfSpectrum,
        ReadOnlySpan<double> rfFiltered,
        ReadOnlySpan<double> envelope,
        RfHighBoostOptions? options,
        IReadOnlyList<SosSection> rfTopFilter)
    {
        if (rfFiltered.Length != envelope.Length
            || rfFilteredHalfSpectrum.Length != (rfFiltered.Length / 2) + 1)
        {
            throw new ArgumentException("VHS RF boost inputs must describe one real FFT block.");
        }

        double[]? highPart = BuildVhsRfHighPart(rfFiltered, envelope, options, rfTopFilter);
        if (highPart is null)
        {
            return false;
        }

        Complex[] highPartSpectrum = PocketFftReal.Forward(highPart);
        for (int i = 0; i < rfFilteredHalfSpectrum.Length; i++)
        {
            rfFilteredHalfSpectrum[i] = new Complex(
                rfFilteredHalfSpectrum[i].Real + highPartSpectrum[i].Real,
                rfFilteredHalfSpectrum[i].Imaginary + highPartSpectrum[i].Imaginary);
        }

        return true;
    }

    private static bool ApplyVhsComplexRfHighBoostIfPresent(
        Complex[] rfFilteredSpectrum,
        ReadOnlySpan<double> rfFiltered,
        ReadOnlySpan<double> envelope,
        RfHighBoostOptions? options,
        IReadOnlyList<SosSection> rfTopFilter)
    {
        if (rfFiltered.Length != envelope.Length
            || rfFilteredSpectrum.Length != rfFiltered.Length)
        {
            throw new ArgumentException("VHS RF boost inputs must describe one complex FFT block.");
        }

        double[]? highPart = BuildVhsRfHighPart(rfFiltered, envelope, options, rfTopFilter);
        if (highPart is null)
        {
            return false;
        }

        Complex[] highPartSpectrum = PocketFftComplex.ForwardReal(highPart);
        for (int i = 0; i < rfFilteredSpectrum.Length; i++)
        {
            rfFilteredSpectrum[i] = new Complex(
                rfFilteredSpectrum[i].Real + highPartSpectrum[i].Real,
                rfFilteredSpectrum[i].Imaginary + highPartSpectrum[i].Imaginary);
        }

        return true;
    }

    private static double[]? BuildVhsRfHighPart(
        ReadOnlySpan<double> rfFiltered,
        ReadOnlySpan<double> envelope,
        RfHighBoostOptions? options,
        IReadOnlyList<SosSection> rfTopFilter)
    {
        if (options is null || options.Multiplier == 0.0)
        {
            return null;
        }

        for (int i = 0; i < envelope.Length; i++)
        {
            if (envelope[i] == 0.0)
            {
                return null;
            }
        }

        float envelopeMean = NumpyReduction.MeanFloat32(envelope);
        float envelopeNumerator = envelopeMean * 0.9f;
        double[] highBand = SosFilter.ApplyForwardBackward(rfTopFilter, rfFiltered);
        var highPart = new double[highBand.Length];
        for (int i = 0; i < highPart.Length; i++)
        {
            float envelopeScale = envelopeNumerator / (float)envelope[i];
            highPart[i] = (highBand[i] * envelopeScale) * options.Multiplier;
        }

        return highPart;
    }

    private bool ApplyRfHighBoostIfPresent(
        Complex[] rfFilteredSpectrum,
        ReadOnlySpan<double> envelope,
        RfHighBoostOptions? options)
    {
        if (options is null || options.Multiplier == 0.0)
        {
            return false;
        }

        double envelopeSum = 0.0;
        for (int i = 0; i < envelope.Length; i++)
        {
            if (envelope[i] == 0.0)
            {
                return false;
            }

            envelopeSum += envelope[i];
        }

        double envelopeMean = envelopeSum / envelope.Length;
        if (envelopeMean == 0.0)
        {
            return false;
        }

        Complex[] dataFilteredComplex = FastFourierTransform.Inverse(rfFilteredSpectrum);
        double[] dataFiltered = new double[dataFilteredComplex.Length];
        for (int i = 0; i < dataFiltered.Length; i++)
        {
            dataFiltered[i] = dataFilteredComplex[i].Real;
        }

        Complex[] rfTopFilter = BuildRfTopZeroPhaseResponse(options, dataFiltered.Length);
        Complex[] highPartSpectrum = ApplyFrequencyFilter(FastFourierTransform.Forward(dataFiltered), rfTopFilter);
        Complex[] highPartComplex = FastFourierTransform.Inverse(highPartSpectrum);
        double[] highPart = new double[highPartComplex.Length];
        for (int i = 0; i < highPart.Length; i++)
        {
            highPart[i] = highPartComplex[i].Real * ((envelopeMean * 0.9) / envelope[i]) * options.Multiplier;
        }

        Complex[] boostedSpectrum = FastFourierTransform.Forward(highPart);
        for (int i = 0; i < rfFilteredSpectrum.Length; i++)
        {
            rfFilteredSpectrum[i] += boostedSpectrum[i];
        }

        return true;
    }

    private Complex[] BuildRfTopZeroPhaseResponse(RfHighBoostOptions options, int length)
    {
        double nyquistHz = SampleRateHz / 2.0;
        if (options.LowHz <= 0.0 || options.LowHz >= options.HighHz || options.HighHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RF high boost band must be inside 0 Hz..Nyquist.");
        }

        Complex[] highPass = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthHighPass(order: 1, options.LowHz / nyquistHz),
            length);
        Complex[] lowPass = IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthLowPass(order: 1, options.HighHz / nyquistHz),
            length);
        var response = new Complex[length];
        for (int i = 0; i < response.Length; i++)
        {
            double magnitude = (highPass[i] * lowPass[i]).Magnitude;
            response[i] = new Complex(magnitude * magnitude, 0.0);
        }

        return response;
    }

    private void ApplyDiffDemodRepairIfPresent(
        double[] demod,
        ReadOnlySpan<Complex> analytic,
        DiffDemodRepairOptions? options,
        RfFmDemodulatorMode fmDemodulatorMode)
    {
        if (options is null || demod.Length <= 40)
        {
            return;
        }

        bool hasSpike = false;
        for (int i = 20; i < demod.Length - 20; i++)
        {
            if (demod[i] > options.MaxValue)
            {
                hasSpike = true;
                break;
            }
        }

        if (!hasSpike)
        {
            return;
        }

        Complex[] diffed = analytic.ToArray();
        for (int i = diffed.Length - 1; i >= 1; i--)
        {
            diffed[i] -= diffed[i - 1];
        }

        diffed[0] = Complex.Zero;
        double[] demodDiffed = DemodulateAnalytic(diffed, fmDemodulatorMode);
        ReplaceSpikes(demod, demodDiffed, options.MaxValue);
    }

    private double[] DemodulateAnalytic(
        ReadOnlySpan<Complex> analytic,
        RfFmDemodulatorMode mode)
    {
        return mode switch
        {
            RfFmDemodulatorMode.ConjugateProduct =>
                PortedMath.UnwrapHilbertConjugateProduct(analytic, SampleRateHz),
            RfFmDemodulatorMode.VhsRustApproximation =>
                PortedMath.UnwrapHilbertVhsRustApproximation(analytic, SampleRateHz),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private void ApplyNonlinearDeemphasisIfPresent(
        double[] video,
        ReadOnlySpan<Complex> videoSpectrum,
        NonlinearDeemphasisOptions? options,
        bool useVhsRealFft)
    {
        if (options is null)
        {
            return;
        }

        double[] output = useVhsRealFft
            ? ApplyNonlinearDeemphasisReal(video, videoSpectrum, SampleRateHz, options)
            : ApplyNonlinearDeemphasis(video, videoSpectrum, SampleRateHz, options);
        Array.Copy(output, video, video.Length);
    }

    private void ApplySubDeemphasisIfPresent(
        double[] video,
        ReadOnlySpan<Complex> videoSpectrum,
        SubDeemphasisOptions? options,
        bool useVhsRealFft)
    {
        if (options is null)
        {
            return;
        }

        double[] output = useVhsRealFft
            ? ApplySubDeemphasisReal(video, videoSpectrum, SampleRateHz, options)
            : ApplySubDeemphasis(video, videoSpectrum, SampleRateHz, options);
        Array.Copy(output, video, video.Length);
    }

    internal static double[] ApplyNonlinearDeemphasisReal(
        ReadOnlySpan<double> video,
        ReadOnlySpan<Complex> videoSpectrum,
        double sampleRateHz,
        NonlinearDeemphasisOptions options)
    {
        if (videoSpectrum.Length != (video.Length / 2) + 1)
        {
            throw new ArgumentException("Video half-spectrum length does not match video length.", nameof(videoSpectrum));
        }

        Complex[] highPass = BuildNonlinearHighPassFilter(sampleRateHz, video.Length, options);
        Complex[] highSpectrum = ApplyNumpyRealFrequencyFilter(videoSpectrum, highPass, video.Length);
        double[] highPart = PocketFftReal.Inverse(highSpectrum, video.Length);
        double[] output = video.ToArray();
        for (int i = 0; i < output.Length; i++)
        {
            output[i] -= Math.Clamp(highPart[i], options.LimitLow, options.LimitHigh);
        }

        return output;
    }

    internal static double[] ApplySubDeemphasisReal(
        ReadOnlySpan<double> video,
        ReadOnlySpan<Complex> videoSpectrum,
        double sampleRateHz,
        SubDeemphasisOptions options)
    {
        if (videoSpectrum.Length != (video.Length / 2) + 1)
        {
            throw new ArgumentException("Video half-spectrum length does not match video length.", nameof(videoSpectrum));
        }

        if (options.Deviation <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sub-deemphasis deviation must be positive.");
        }

        double nyquistHz = sampleRateHz / 2.0;
        if (options.AmplitudeLowPassHz <= 0.0 || options.AmplitudeLowPassHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Sub-deemphasis amplitude low-pass frequency must be between 0 Hz and Nyquist.");
        }

        Complex[] highPass = BuildNonlinearHighPassFilter(
            sampleRateHz,
            video.Length,
            options.HighPassHz,
            options.BandPassUpperHz,
            options.Order);
        Complex[] highSpectrum = ApplyNumpyRealFrequencyFilter(videoSpectrum, highPass, video.Length);
        double[] highPart = PocketFftReal.Inverse(highSpectrum, video.Length);
        double[] amplitude = BuildAnalyticMagnitude(highPart);
        double deviation = options.Deviation / 2.0;
        for (int i = 0; i < amplitude.Length; i++)
        {
            amplitude[i] /= deviation;
        }

        SosSection[] amplitudeLowPass = IirFilterDesign.ButterworthLowPass(
            order: 1,
            options.AmplitudeLowPassHz / nyquistHz);
        int defaultPadLength = SosFilter.DefaultPadLength(amplitudeLowPass);
        amplitude = amplitude.Length > defaultPadLength
            ? SosFilter.ApplyForwardBackward(amplitudeLowPass, amplitude)
            : SosFilter.ApplyForwardBackward(amplitudeLowPass, amplitude, padLength: 0);
        for (int i = 0; i < amplitude.Length; i++)
        {
            amplitude[i] = Math.Max(0.0, amplitude[i]);
            if (options.Scaling1 is { } scaling1)
            {
                amplitude[i] *= scaling1;
            }

            amplitude[i] = Math.Pow(amplitude[i], options.ExponentialScaling);
            if (options.Scaling2 is { } scaling2)
            {
                amplitude[i] *= scaling2;
            }

            if (options.LogisticRate is { } logisticRate && logisticRate > 0.0)
            {
                double logisticMid = options.LogisticMid ?? 0.0;
                amplitude[i] *= 1.0 / (1.0 + Math.Exp(-logisticRate * (amplitude[i] - logisticMid)));
            }
        }

        double[] output = video.ToArray();
        double staticFactor = options.StaticFactor ?? 0.0;
        for (int i = 0; i < output.Length; i++)
        {
            double staticPart = highPart[i] * staticFactor;
            output[i] -= (highPart[i] * (1.0 - amplitude[i])) + staticPart;
        }

        return output;
    }

    internal static Complex[] BuildNonlinearHighPassFilter(
        double sampleRateHz,
        int length,
        NonlinearDeemphasisOptions options)
    {
        return BuildNonlinearHighPassFilter(sampleRateHz, length, options.HighPassHz, options.BandPassUpperHz, options.Order);
    }

    internal static Complex[] BuildNonlinearHighPassFilter(
        double sampleRateHz,
        int length,
        double highPassHz,
        double? bandPassUpperHz,
        int order)
    {
        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        double nyquistHz = sampleRateHz / 2.0;
        if (highPassHz <= 0.0 || highPassHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(highPassHz), "Nonlinear high-pass frequency must be between 0 Hz and Nyquist.");
        }

        if (bandPassUpperHz is not { } upperHz)
        {
            return IirFilterDesign.FrequencyResponse(
                IirFilterDesign.ButterworthHighPass(order, highPassHz / nyquistHz),
                length);
        }

        if (upperHz <= highPassHz || upperHz >= nyquistHz)
        {
            throw new ArgumentOutOfRangeException(nameof(bandPassUpperHz), "Nonlinear band-pass upper frequency must be above the high-pass frequency and below Nyquist.");
        }

        return IirFilterDesign.FrequencyResponse(
            IirFilterDesign.ButterworthBandPass(order, highPassHz / nyquistHz, upperHz / nyquistHz),
            length);
    }

    internal static double[] BuildAnalyticMagnitude(ReadOnlySpan<double> input)
    {
        Complex[] spectrum = PocketFftComplex.ForwardDuccRealFull(input);
        int nyquist = spectrum.Length / 2;
        for (int i = 1; i < nyquist; i++)
        {
            spectrum[i] *= 2.0;
        }

        for (int i = nyquist + 1; i < spectrum.Length; i++)
        {
            spectrum[i] = Complex.Zero;
        }

        PocketFftComplex.InverseDuccInPlace(spectrum);
        var magnitude = new double[spectrum.Length];
        for (int i = 0; i < magnitude.Length; i++)
        {
            magnitude[i] = NumpyComplexMagnitude(spectrum[i]);
        }

        return magnitude;
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

    private static double Max(double[] values, int start, int end)
    {
        double max = double.NegativeInfinity;
        for (int i = start; i < end; i++)
        {
            max = Math.Max(max, values[i]);
        }

        return max;
    }

    private static void ApplyHilbertMultiplierInPlace(Complex[] spectrum, ReadOnlySpan<double> hilbertMultiplier)
    {
        for (int i = 0; i < spectrum.Length; i++)
        {
            spectrum[i] *= hilbertMultiplier[i];
        }
    }

    private static double[] ResampleLinear(ReadOnlySpan<double> input, int outputLength)
    {
        if (outputLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }

        if (input.IsEmpty)
        {
            return [];
        }

        if (input.Length == outputLength)
        {
            return input.ToArray();
        }

        var output = new double[outputLength];
        for (int i = 0; i < output.Length; i++)
        {
            double sourcePosition = (double)i * input.Length / output.Length;
            output[i] = SampleLinear(input, sourcePosition);
        }

        return output;
    }

    private static double SampleLinear(ReadOnlySpan<double> input, double position)
    {
        if (position <= 0.0)
        {
            return input[0];
        }

        int left = (int)Math.Floor(position);
        if (left >= input.Length - 1)
        {
            return input[^1];
        }

        double fraction = position - left;
        return input[left] + ((input[left + 1] - input[left]) * fraction);
    }

    private static void ZeroMirroredBin(Complex[] spectrum, int index)
    {
        if (index <= 0 || index >= spectrum.Length)
        {
            return;
        }

        spectrum[index] = Complex.Zero;
        int mirrored = spectrum.Length - index;
        if (mirrored > 0 && mirrored < spectrum.Length)
        {
            spectrum[mirrored] = Complex.Zero;
        }
    }
}

public sealed record RfDemodulatedBlock(
    double[] Video,
    double[] DemodRaw,
    Complex[] Analytic,
    double[] Envelope,
    double[] VideoLowPass,
    double[] RfHighPass,
    short[]? Efm = null,
    LaserDiscAnalogAudioBlock? AnalogAudio = null,
    double[]? Chroma = null,
    double[]? VideoBurst = null,
    double[]? VideoPilot = null,
    bool VhsWeakRfSignal = false);

public sealed record LaserDiscAnalogAudioBlock(
    double[] Left,
    double[] Right,
    int DecimationFactor,
    bool UsesFloat32Storage = true);

public sealed record RfVideoReferenceFilterSet(
    Complex[]? VideoBurst,
    int VideoBurstOffset,
    Complex[]? VideoPilot,
    bool ClipDemodForVideo = false);
