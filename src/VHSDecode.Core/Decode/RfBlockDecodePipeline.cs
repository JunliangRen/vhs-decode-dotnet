using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Decode;

public sealed record CvbsDecodeOptions(bool AutoSync, VideoOutputConverter VideoOutput);

public sealed class RfBlockDecodePipeline : IDisposable
{
    // Bounds the idle reuse reservoir at the decoded cache's maximum configured capacity.
    internal const int MaximumRetainedStreamOutputBufferSets = 48;
    private readonly IRfSampleLoader _loader;
    private readonly DecodeFilterSet _filters;
    private readonly DecodeFilterOptions _filterOptions;
    private readonly RfDemodulator _demodulator;
    private readonly RfVideoReferenceFilterSet? _referenceFilters;
    private readonly CvbsDecodeOptions? _cvbsOptions;
    private readonly IRfInputProcessor? _inputProcessor;
    private readonly IReusableRfSampleLoader? _reusableStreamInputLoader;
    private readonly Action<string, string>? _diagnosticLogger;
    private readonly bool _retainRfDiagnosticChannels;
    private readonly bool _useCurrentChromaShiftDc;
    private readonly bool _useNumpyComplexVhsAnalytic;
    private readonly IppSos32FilterPool? _chromaBurstIppSos;
    private readonly IppSos32FilterPool? _vhsEnvelopeIppSos;
    private readonly ConcurrentStack<StreamBlockOutputBuffers> _streamOutputBufferPool = new();
    private readonly ConditionalWeakTable<RfPipelineBlock, StreamBlockOutputBuffers>
        _streamOutputBufferLeases = new();
    private int _retainedStreamOutputBufferSetCount;
    private int _createdStreamOutputBufferSetCount;
    private int _streamOutputBufferPoolDisposed;

    public RfBlockDecodePipeline(
        IRfSampleLoader loader,
        DecodeFilterSet filters,
        double sampleRateHz,
        DecodeFilterOptions? filterOptions = null,
        CvbsDecodeOptions? cvbsOptions = null,
        IRfInputProcessor? inputProcessor = null,
        Action<string, string>? diagnosticLogger = null,
        bool retainRfDiagnosticChannels = true,
        DspBackend dspBackend = DspBackend.Exact,
        UpstreamBehaviorProfile upstreamBehaviorProfile =
            UpstreamBehaviorProfile.V040)
        : this(
            loader,
            filters,
            sampleRateHz,
            filterOptions,
            cvbsOptions,
            inputProcessor,
            diagnosticLogger,
            retainRfDiagnosticChannels,
            dspBackend,
            upstreamBehaviorProfile,
            parallelizeVhsInverseStaging: false)
    {
    }

    internal RfBlockDecodePipeline(
        IRfSampleLoader loader,
        DecodeFilterSet filters,
        double sampleRateHz,
        DecodeFilterOptions? filterOptions,
        CvbsDecodeOptions? cvbsOptions,
        IRfInputProcessor? inputProcessor,
        Action<string, string>? diagnosticLogger,
        bool retainRfDiagnosticChannels,
        DspBackend dspBackend,
        UpstreamBehaviorProfile upstreamBehaviorProfile,
        bool parallelizeVhsInverseStaging,
        int vhsInverseCompanionWorkerThreads = 1)
    {
        _loader = loader;
        _filters = filters;
        _filterOptions = filterOptions ?? new DecodeFilterOptions();
        _demodulator = new RfDemodulator(
            sampleRateHz,
            dspBackend,
            parallelizeVhsInverseStaging,
            companionIppFftFactory: null,
            vhsInverseCompanionWorkerThreads);
        _referenceFilters = filters.LdVideoBurst is null && filters.LdVideoPilot is null && !_filterOptions.LdClipDemodForVideo
            ? null
            : new RfVideoReferenceFilterSet(
                filters.LdVideoBurst,
                filters.LdVideoBurstOffset,
                filters.LdVideoPilot,
                _filterOptions.LdClipDemodForVideo);
        _cvbsOptions = cvbsOptions;
        _inputProcessor = inputProcessor;
        _reusableStreamInputLoader = !retainRfDiagnosticChannels
            && cvbsOptions is null
            && inputProcessor is null
            ? loader as IReusableRfSampleLoader
            : null;
        _diagnosticLogger = diagnosticLogger;
        _retainRfDiagnosticChannels = retainRfDiagnosticChannels;
        _useCurrentChromaShiftDc =
            upstreamBehaviorProfile == UpstreamBehaviorProfile.Current;
        _useNumpyComplexVhsAnalytic = dspBackend == DspBackend.Exact;
        if (dspBackend == DspBackend.IppFast)
        {
            _chromaBurstIppSos = IppSos32FilterPool.TryCreate(filters.ChromaBurstSos);
            _vhsEnvelopeIppSos = IppSos32FilterPool.TryCreate(filters.VhsEnvelopeSos);
        }
    }

    public IRfInputProcessor? InputProcessor => _inputProcessor;

    internal int RfHighPassOffset => _filters.RfHighPassOffset;

    internal bool RetainsRfDiagnosticChannels => _retainRfDiagnosticChannels;

    internal bool ParallelizesVhsInverseStaging =>
        _demodulator.ParallelizesVhsInverseStaging;

    internal int VhsInverseCompanionWorkerThreads =>
        _demodulator.VhsInverseCompanionWorkerThreads;

    internal bool UsesIppVhsEnvelopeSos => _vhsEnvelopeIppSos is not null;

    internal int CreatedIppVhsEnvelopeSosContextCount =>
        _vhsEnvelopeIppSos?.CreatedContextCount ?? 0;

    internal int RetainedIppVhsEnvelopeSosContextCount =>
        _vhsEnvelopeIppSos?.RetainedContextCount ?? 0;

    internal bool RequiresSequentialBlockDecode => _filterOptions.SharpnessEq is not null;

    internal int RetainedStreamOutputBufferSetCount =>
        Volatile.Read(ref _retainedStreamOutputBufferSetCount);

    internal int CreatedStreamOutputBufferSetCount =>
        Volatile.Read(ref _createdStreamOutputBufferSetCount);

    public RfDemodulatedBlock? DecodeBlock(Stream stream, long sample, int blockLength)
    {
        return DecodeBlockWithInput(stream, sample, blockLength)?.Demodulated;
    }

    internal LaserDiscAnalogAudioBlock ApplyLaserDiscAnalogAudioPhase2(LaserDiscAnalogAudioBlock fieldAudio)
    {
        return _filters.LdAnalogAudio is null
            ? fieldAudio
            : LaserDiscAnalogAudioPhase2.Apply(fieldAudio, _filters.LdAnalogAudio);
    }

    public RfPipelineBlock? DecodeBlockWithInput(Stream stream, long sample, int blockLength)
    {
        double[]? input = LoadBlockInput(stream, sample, blockLength);
        if (input is null)
        {
            return null;
        }

        return DecodePreparedBlock(input);
    }

    internal RfPipelineBlock? DecodeStreamBlockWithInput(
        Stream stream,
        long sample,
        int blockLength,
        Complex[]? rfMtfOverride = null)
    {
        double[]? input = LoadStreamBlockInput(stream, sample, blockLength);
        if (input is null)
        {
            return null;
        }

        try
        {
            return DecodePreparedStreamBlock(input, rfMtfOverride: rfMtfOverride);
        }
        finally
        {
            ReturnStreamBlockInput(input);
        }
    }

    internal double[]? LoadBlockInput(Stream stream, long sample, int blockLength)
    {
        double[]? loadedInput = _loader.Read(stream, sample, blockLength);
        return loadedInput is null
            ? null
            : _inputProcessor?.Process(loadedInput) ?? loadedInput;
    }

    internal double[]? LoadStreamBlockInput(
        Stream stream,
        long sample,
        int blockLength,
        bool parallelDecode = false)
        => !CanReuseStreamBlockInput(parallelDecode)
            ? LoadBlockInput(stream, sample, blockLength)
            : _reusableStreamInputLoader!.ReadReusable(stream, sample, blockLength);

    internal void ReturnStreamBlockInput(double[] input, bool parallelDecode = false)
    {
        if (CanReuseStreamBlockInput(parallelDecode))
        {
            _reusableStreamInputLoader!.ReturnReusable(input);
        }
    }

    private bool CanReuseStreamBlockInput(bool parallelDecode)
        => _reusableStreamInputLoader is not null
            && (parallelDecode || _reusableStreamInputLoader.ReuseForSequentialDecode);

    internal RfPipelineBlock DecodePreparedBlock(double[] input, bool reportDiagnostics = true)
        => DecodePreparedBlockCore(
            input,
            reportDiagnostics,
            retainRfDiagnosticChannels: true,
            reuseStreamOutputBuffers: false);

    internal RfPipelineBlock DecodePreparedStreamBlock(
        double[] input,
        bool reportDiagnostics = true,
        Complex[]? rfMtfOverride = null)
        => DecodePreparedBlockCore(
            input,
            reportDiagnostics,
            _retainRfDiagnosticChannels,
            reuseStreamOutputBuffers: !_retainRfDiagnosticChannels,
            rfMtfOverride);

    private RfPipelineBlock DecodePreparedBlockCore(
        double[] input,
        bool reportDiagnostics,
        bool retainRfDiagnosticChannels,
        bool reuseStreamOutputBuffers,
        Complex[]? rfMtfOverride = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (_cvbsOptions is not null)
        {
            return new RfPipelineBlock(input, DecodeCvbsBlock(input));
        }

        StreamBlockOutputBuffers? streamOutputBuffers = reuseStreamOutputBuffers
            ? RentStreamOutputBuffers(input.Length)
            : null;
        try
        {
            Complex[]? inputSpectrum = _filters.LdEfm is not null || _filters.LdAnalogAudio is not null
                ? _demodulator.ForwardLaserDiscFullSpectrum(input)
                : null;
            RfDemodulatedBlock demodulated = _demodulator.DemodulateCore(
                input,
                _filters.RfVideo,
                _filters.RfHighPass,
                rfMtfOverride ?? _filters.RfMtf,
                _filters.Video,
                _filters.VideoLowPass05,
                _filters.VideoLowPass05Offset,
                _filterOptions.LdPalV4300DNotch,
                _filterOptions.RfHighBoost,
                _filterOptions.DiffDemodRepair,
                _filterOptions.ChromaTrap,
                _filterOptions.SharpnessEq,
                _filterOptions.NonlinearDeemphasis,
                _filterOptions.SubDeemphasis,
                _filterOptions.BetamaxFscNotchHz,
                _referenceFilters,
                _filterOptions.FmDemodulatorMode,
                _filters.VhsEnvelopeSos,
                _filters.VhsRfTopSos,
                inputSpectrum,
                includeRfHighPassOutput: retainRfDiagnosticChannels,
                includeAnalyticOutput: retainRfDiagnosticChannels,
                includeDemodRawOutput: retainRfDiagnosticChannels || _filterOptions.ExportRawTbc,
                useNumpyComplexVhsAnalytic: _useNumpyComplexVhsAnalytic,
                outputBuffers: streamOutputBuffers?.Demodulated,
                vhsEnvelopeIppFilter: _vhsEnvelopeIppSos);
            if (reportDiagnostics)
            {
                ReportDiagnostics(demodulated);
            }

            if (_filterOptions.LdClipDemodForVideo)
            {
                QuantizeLaserDiscVideoChannels(demodulated);
            }

            if (_filters.ChromaBurst is not null || _filters.ChromaBurstSos is not null)
            {
                bool keepCompactFloat32 = !retainRfDiagnosticChannels
                    && !_filterOptions.UseChromaAfc
                    && _filters.ChromaBurstSos is not null
                    && !_filters.ChromaBurstUsesDemodulatedVideo
                    && _filters.ChromaBurstAudioNotch is null
                    && _filters.ChromaBurstVideoNotch is null;
                demodulated = keepCompactFloat32
                    ? demodulated with
                    {
                        ChromaFloat32 = DecodeChromaBurstFloat32(
                            input,
                            _filters,
                            _useCurrentChromaShiftDc,
                            streamOutputBuffers?.Chroma,
                            _chromaBurstIppSos)
                    }
                    : demodulated with
                    {
                        Chroma = _filterOptions.UseChromaAfc
                        ? input.ToArray()
                        : DecodeChromaBurst(
                            _filters.ChromaBurstUsesDemodulatedVideo
                                ? demodulated.Video
                                : input,
                            _filters,
                            _useCurrentChromaShiftDc,
                            _chromaBurstIppSos)
                    };
            }

            if (_filters.LdEfm is not null)
            {
                inputSpectrum ??= _demodulator.ForwardLaserDiscFullSpectrum(input);
                demodulated = demodulated with { Efm = DecodeEfmBlock(inputSpectrum, _filters.LdEfm) };
            }

            if (_filters.LdAnalogAudio is not null)
            {
                inputSpectrum ??= _demodulator.ForwardLaserDiscFullSpectrum(input);
                demodulated = demodulated with { AnalogAudio = DecodeAnalogAudioBlock(inputSpectrum, _filters.LdAnalogAudio) };
            }

            if (_filterOptions.ExportRawTbc)
            {
                demodulated = demodulated with { Video = demodulated.DemodRaw };
            }

            if (!retainRfDiagnosticChannels)
            {
                // VHS consumes these arrays only while decoding this block; field assembly needs the retained channels below.
                demodulated = demodulated with { DemodRaw = [], Analytic = [], RfHighPass = [] };
            }

            var pipelineBlock = new RfPipelineBlock(
                retainRfDiagnosticChannels ? input : [],
                demodulated);
            if (streamOutputBuffers is not null)
            {
                _streamOutputBufferLeases.Add(pipelineBlock, streamOutputBuffers);
                streamOutputBuffers = null;
            }

            return pipelineBlock;
        }
        catch
        {
            if (streamOutputBuffers is not null)
            {
                ReturnStreamOutputBuffers(streamOutputBuffers);
            }

            throw;
        }
    }

    internal void ReportDeferredDiagnostics(RfPipelineBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        ReportDiagnostics(block.Demodulated);
    }

    internal void ReleaseStreamBlock(RfPipelineBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (_streamOutputBufferLeases.TryGetValue(
                block,
                out StreamBlockOutputBuffers? buffers)
            && _streamOutputBufferLeases.Remove(block))
        {
            ReturnStreamOutputBuffers(buffers);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _streamOutputBufferPoolDisposed, 1) != 0)
        {
            return;
        }

        while (_streamOutputBufferPool.TryPop(out _))
        {
            Interlocked.Decrement(ref _retainedStreamOutputBufferSetCount);
        }

        try
        {
            _chromaBurstIppSos?.Dispose();
            try
            {
                _vhsEnvelopeIppSos?.Dispose();
            }
            finally
            {
                _demodulator.Dispose();
            }
        }
        finally
        {
            _inputProcessor?.Dispose();
        }
    }

    private StreamBlockOutputBuffers RentStreamOutputBuffers(int length)
    {
        while (_streamOutputBufferPool.TryPop(out StreamBlockOutputBuffers? buffers))
        {
            Interlocked.Decrement(ref _retainedStreamOutputBufferSetCount);
            if (buffers.Length == length)
            {
                return buffers;
            }
        }

        Interlocked.Increment(ref _createdStreamOutputBufferSetCount);
        return new StreamBlockOutputBuffers(length);
    }

    private void ReturnStreamOutputBuffers(StreamBlockOutputBuffers buffers)
    {
        if (Volatile.Read(ref _streamOutputBufferPoolDisposed) != 0)
        {
            return;
        }

        int retained = Interlocked.Increment(ref _retainedStreamOutputBufferSetCount);
        if (retained > MaximumRetainedStreamOutputBufferSets)
        {
            Interlocked.Decrement(ref _retainedStreamOutputBufferSetCount);
            return;
        }

        _streamOutputBufferPool.Push(buffers);
        if (Volatile.Read(ref _streamOutputBufferPoolDisposed) != 0
            && _streamOutputBufferPool.TryPop(out _))
        {
            Interlocked.Decrement(ref _retainedStreamOutputBufferSetCount);
        }
    }

    private void ReportDiagnostics(RfDemodulatedBlock demodulated)
    {
        if (demodulated.VhsWeakRfSignal)
        {
            _diagnosticLogger?.Invoke(
                "WARNING",
                "RF signal is weak. Is your deck tracking properly?");
        }
    }

    private static double[] DecodeChromaBurst(
        ReadOnlySpan<double> input,
        DecodeFilterSet filters,
        bool useCurrentChromaShiftDc,
        IppSos32FilterPool? ippFilter = null)
    {
        bool retainFloat32 = filters.ChromaBurstSos is not null
            && !filters.ChromaBurstUsesDemodulatedVideo;
        double[] chroma = filters.ChromaBurstSos is not null
            ? retainFloat32
                ? ApplyFloat32SosToDouble(filters.ChromaBurstSos, input, ippFilter)
                : SosFilter.ApplyForwardBackward(filters.ChromaBurstSos, input)
            : FilterRealSignal(
                PocketFftComplex.ForwardReal(input),
                filters.ChromaBurst ?? throw new InvalidOperationException("A chroma burst filter is required."));
        if (filters.ChromaBurstAudioNotch is not null)
        {
            chroma = IirFilter.ApplyForwardBackward(filters.ChromaBurstAudioNotch, chroma);
            retainFloat32 = false;
        }

        if (filters.ChromaBurstVideoNotch is not null)
        {
            chroma = IirFilter.ApplyForwardBackward(filters.ChromaBurstVideoNotch, chroma);
            retainFloat32 = false;
        }

        if (retainFloat32)
        {
            return useCurrentChromaShiftDc
                ? VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32CurrentInPlace(
                    chroma,
                    filters.ChromaOffsetSamples)
                : VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32InPlace(
                    chroma,
                    filters.ChromaOffsetSamples);
        }

        return VhsChromaDecoder.ShiftChromaAndRemoveDcInPlace(
            chroma,
            filters.ChromaOffsetSamples);
    }

    private static float[] DecodeChromaBurstFloat32(
        ReadOnlySpan<double> input,
        DecodeFilterSet filters,
        bool useCurrentChromaShiftDc,
        float[]? destination = null,
        IppSos32FilterPool? ippFilter = null)
    {
        IReadOnlyList<SosSection> chromaBurstSos = filters.ChromaBurstSos
            ?? throw new InvalidOperationException("A float32 chroma burst SOS filter is required.");
        float[] chroma;
        if (destination is null)
        {
            chroma = SosFilter.ApplyForwardBackwardFloat32ToSingle(
                chromaBurstSos,
                input,
                ippFilter: ippFilter);
        }
        else
        {
            SosFilter.ApplyForwardBackwardFloat32ToSingle(
                chromaBurstSos,
                input,
                destination,
                ippFilter: ippFilter);
            chroma = destination;
        }

        return useCurrentChromaShiftDc
            ? VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32CurrentInPlace(
                chroma,
                filters.ChromaOffsetSamples)
            : VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32InPlace(
                chroma,
                filters.ChromaOffsetSamples);
    }

    private static double[] ApplyFloat32SosToDouble(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<double> input,
        IppSos32FilterPool? ippFilter)
    {
        var output = new double[input.Length];
        SosFilter.ApplyForwardBackwardFloat32(
            sections,
            input,
            output,
            ippFilter: ippFilter);
        return output;
    }

    private RfDemodulatedBlock DecodeCvbsBlock(ReadOnlySpan<double> input)
    {
        Complex[] inputHalfSpectrum = PocketFftComplex.ForwardDuccReal(input);
        double[] reconstructedInput = PocketFftComplex.InverseDuccReal(inputHalfSpectrum, input.Length);
        double[] luma = _cvbsOptions!.AutoSync
            ? reconstructedInput
            : ConvertCvbsRawToHz(reconstructedInput, _cvbsOptions.VideoOutput);
        if (_filterOptions.ChromaTrap is not null)
        {
            luma = RfDemodulator.ApplyChromaTrap(luma, _demodulator.SampleRateHz, _filterOptions.ChromaTrap.FscHz);
        }

        if (_filterOptions.VideoNotchHz is { } notchHz)
        {
            luma = IirFilter.ApplyForwardBackward(
                IirFilterDesign.Notch(
                    notchHz / (_demodulator.SampleRateHz / 2.0),
                    _filterOptions.VideoNotchQ),
                luma);
        }

        Complex[] lumaSpectrum = PocketFftComplex.ForwardDuccReal(luma);
        double[] video = luma.ToArray();
        double[] videoLowPass = FilterRealSignalPocket(
            lumaSpectrum,
            _filters.VideoLowPass05,
            luma.Length);
        if (_filters.VideoLowPass05Offset != 0)
        {
            videoLowPass = FrequencyDomainFilter.Roll(videoLowPass, -_filters.VideoLowPass05Offset);
        }

        double[]? videoBurst = _filters.CvbsVideoBurst is null
            ? null
            : FilterRealSignalPocket(lumaSpectrum, _filters.CvbsVideoBurst, luma.Length);
        if (videoBurst is not null)
        {
            for (int i = 0; i < videoBurst.Length; i++)
            {
                videoBurst[i] = (float)videoBurst[i];
            }
        }

        double[] envelope = new double[luma.Length];
        for (int i = 0; i < envelope.Length; i++)
        {
            envelope[i] = Math.Abs(luma[i]);
        }

        return new RfDemodulatedBlock(
            video,
            luma.ToArray(),
            [],
            envelope,
            videoLowPass,
            luma.ToArray(),
            VideoBurst: videoBurst);
    }

    public static double[] ConvertCvbsRawToHz(ReadOnlySpan<double> input, VideoOutputConverter converter)
    {
        var output = new double[input.Length];
        double whiteHz = converter.IreToHz(100.0);
        double syncHz = converter.IreToHz(converter.VSyncIre);
        for (int i = 0; i < output.Length; i++)
        {
            double luma = input[i] + (ushort.MaxValue / 2.0);
            luma /= 4.0 * ushort.MaxValue;
            luma *= whiteHz;
            luma += syncHz;
            output[i] = luma;
        }

        return output;
    }

    private static double[] FilterRealSignal(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<Complex> filter)
    {
        if (filter.Length != spectrum.Length)
        {
            throw new ArgumentException("Frequency filter length must match input block length.", nameof(filter));
        }

        var filtered = new Complex[spectrum.Length];
        for (int i = 0; i < filtered.Length; i++)
        {
            filtered[i] = spectrum[i] * filter[i];
        }

        Complex[] complex = PocketFftComplex.Inverse(filtered);
        var real = new double[complex.Length];
        for (int i = 0; i < real.Length; i++)
        {
            real[i] = complex[i].Real;
        }

        return real;
    }

    private static double[] FilterRealSignalPocket(
        ReadOnlySpan<Complex> halfSpectrum,
        ReadOnlySpan<Complex> fullFilter,
        int realLength)
    {
        int expectedHalfLength = (realLength / 2) + 1;
        if (halfSpectrum.Length != expectedHalfLength)
        {
            throw new ArgumentException("Half-spectrum length must match the real input length.", nameof(halfSpectrum));
        }

        if (fullFilter.Length < expectedHalfLength)
        {
            throw new ArgumentException("Frequency filter is shorter than the real half-spectrum.", nameof(fullFilter));
        }

        var filtered = new Complex[expectedHalfLength];
        NumpyComplexMultiply.Apply(halfSpectrum, fullFilter[..expectedHalfLength], filtered);

        return PocketFftComplex.InverseDuccReal(filtered, realLength);
    }

    private short[] DecodeEfmBlock(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<Complex> efmFilter)
    {
        double[] filtered = FilterLaserDiscSignal(spectrum, efmFilter);
        var output = new short[filtered.Length];
        for (int i = 0; i < output.Length; i++)
        {
            double clipped = Math.Clamp(filtered[i], short.MinValue, short.MaxValue);
            output[i] = (short)clipped;
        }

        return output;
    }

    private LaserDiscAnalogAudioBlock DecodeAnalogAudioBlock(
        ReadOnlySpan<Complex> spectrum,
        LaserDiscAnalogAudioFilterSet filters)
    {
        double[] left = DecodeAnalogAudioChannel(spectrum, filters.Left);
        double[] right = DecodeAnalogAudioChannel(spectrum, filters.Right);
        return new LaserDiscAnalogAudioBlock(left, right, filters.DecimationFactor);
    }

    private double[] DecodeAnalogAudioChannel(
        ReadOnlySpan<Complex> spectrum,
        LaserDiscAnalogAudioChannelFilter filter)
    {
        Complex[] sliced = DecodeFilterSetBuilder.SliceSpectrum(spectrum, filter.LowBin, filter.BinCount, spectrum.Length);
        for (int i = 0; i < sliced.Length; i++)
        {
            sliced[i] *= filter.Stage1Filter[i];
        }

        Complex[] analytic = _demodulator.InverseLaserDiscFullSpectrum(
            sliced,
            useDuccExact: false);
        double[] demodulated = PortedMath.UnwrapHilbert(analytic, filter.SliceSampleRateHz);
        for (int i = 0; i < demodulated.Length; i++)
        {
            demodulated[i] = (float)(demodulated[i] + filter.LowFrequencyHz);
        }

        return demodulated;
    }

    private double[] FilterLaserDiscSignal(
        ReadOnlySpan<Complex> spectrum,
        ReadOnlySpan<Complex> filter)
    {
        if (filter.Length != spectrum.Length)
        {
            throw new ArgumentException(
                "Frequency filter length must match input block length.",
                nameof(filter));
        }

        var filtered = new Complex[spectrum.Length];
        for (int i = 0; i < filtered.Length; i++)
        {
            filtered[i] = spectrum[i] * filter[i];
        }

        Complex[] complex = _demodulator.InverseLaserDiscFullSpectrum(
            filtered,
            useDuccExact: false);
        var real = new double[complex.Length];
        for (int i = 0; i < real.Length; i++)
        {
            real[i] = complex[i].Real;
        }

        return real;
    }

    private static void QuantizeLaserDiscVideoChannels(RfDemodulatedBlock block)
    {
        QuantizeToFloat32InPlace(block.Video);
        QuantizeToFloat32InPlace(block.DemodRaw);
        QuantizeToFloat32InPlace(block.VideoLowPass);
        QuantizeToFloat32InPlace(block.RfHighPass);
        if (block.VideoBurst is not null)
        {
            QuantizeToFloat32InPlace(block.VideoBurst);
        }

        if (block.VideoPilot is not null)
        {
            QuantizeToFloat32InPlace(block.VideoPilot);
        }
    }

    internal static unsafe void QuantizeToFloat32InPlace(Span<double> values)
    {
        int index = 0;
        if (Avx.IsSupported)
        {
            fixed (double* valuesPointer = values)
            {
                int vectorizedEnd = values.Length - (values.Length % 4);
                for (; index < vectorizedEnd; index += 4)
                {
                    Avx.Store(
                        valuesPointer + index,
                        Avx.ConvertToVector256Double(
                            Avx.ConvertToVector128Single(
                                Avx.LoadVector256(valuesPointer + index))));
                }
            }
        }

        for (; index < values.Length; index++)
        {
            values[index] = (float)values[index];
        }
    }

    private sealed class StreamBlockOutputBuffers
    {
        internal StreamBlockOutputBuffers(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            Length = length;
            Demodulated = new RfDemodulatedBlockOutputBuffers(length);
            Chroma = new float[length];
        }

        internal int Length { get; }

        internal RfDemodulatedBlockOutputBuffers Demodulated { get; }

        internal float[] Chroma { get; }
    }
}

public sealed record RfPipelineBlock(double[] Input, RfDemodulatedBlock Demodulated);
