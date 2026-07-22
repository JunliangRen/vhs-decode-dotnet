using System.Numerics;
using VHSDecode.Core.Compute.Cuda;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

internal enum CudaRfExecutionGraph
{
    StandardFullGpu,
    StandardGpuFirstStage,
    VhsGpuFirstStage,
    CvbsGpu
}

internal readonly record struct CudaRfBackendCompatibility(bool IsSupported, string Reason)
{
    internal static CudaRfBackendCompatibility Supported() => new(true, string.Empty);

    internal static CudaRfBackendCompatibility Unsupported(string reason) => new(false, reason);
}

/// <summary>
/// Executes the RF batch selected during decode preflight. Once constructed this
/// backend never falls back to the CPU reference graph: native execution errors
/// are fatal for the current decode.
/// </summary>
internal sealed class CudaRfBlockComputeBackend : IRfBlockComputeBackend
{
    private const ulong MinimumFreeMemoryReserveBytes = 512UL * 1024 * 1024;
    private const ulong WorkspaceSafetyMultiplier = 6;
    private readonly CudaBackendContext _context;
    private readonly RfBlockDecodePipeline _pipeline;
    private readonly CudaRfExecutionPlan _plan;
    private readonly object _executeLock = new();
    private int _nextStreamIndex;
    private bool _disposed;

    private CudaRfBlockComputeBackend(
        CudaBackendContext context,
        RfBlockDecodePipeline pipeline,
        CudaRfExecutionPlan plan)
    {
        _context = context;
        _pipeline = pipeline;
        _plan = plan;
    }

    public string Name => $"cuda:{_context.Device.Ordinal}";

    public bool IsHardwareAccelerated => true;

    public int GetMaximumBatchSize(int requestedBlockCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (requestedBlockCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedBlockCount));
        }

        lock (_executeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CudaNativeStatus status = _context.GetCurrentDeviceInfo(
                out CudaDeviceInfo currentDevice,
                out string errorDetail);
            if (status != CudaNativeStatus.Success)
            {
                throw new InvalidOperationException(
                    $"CUDA device-memory query failed after the CUDA backend was selected: {errorDetail}");
            }

            // ABI-v1 components built before memory reporting leave the flag
            // clear. Preserve compatibility while avoiding an unbounded batch.
            if (currentDevice.AvailableGlobalMemoryBytes is not { } availableBytes)
            {
                return 1;
            }

            bool includesLdFrequencyWorkspace =
                _plan.LdEfmFilter is not null || _plan.LdAnalogAudioFilters is not null;
            int maximum = CalculateMemoryLimitedBatchSize(
                requestedBlockCount,
                _plan.Descriptor.SampleCount,
                includesLdFrequencyWorkspace,
                availableBytes,
                currentDevice.TotalGlobalMemoryBytes);
            if (maximum == 0)
            {
                throw new InvalidOperationException(
                    $"CUDA device {_context.Device.Ordinal} has only " +
                    $"{availableBytes / (1024 * 1024)} MiB free; the reserved-memory policy " +
                    "cannot safely schedule even one RF block.");
            }

            return maximum;
        }
    }

    internal static int CalculateMemoryLimitedBatchSize(
        int requestedBlockCount,
        int sampleCount,
        bool includesLdFrequencyWorkspace,
        ulong availableBytes,
        ulong totalBytes)
    {
        if (requestedBlockCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedBlockCount));
        }

        if (sampleCount < 2 || (sampleCount & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        ulong sampleBytes = checked((ulong)sampleCount * sizeof(double));
        ulong spectrumBytes = checked(((ulong)sampleCount / 2 + 1) * 16UL);
        ulong perBlockBytes = checked(
            (sampleBytes * 8UL) +
            (spectrumBytes * 3UL) +
            32UL +
            (includesLdFrequencyWorkspace ? sampleBytes : 0UL));
        ulong fixedBytesPerStream = checked(
            spectrumBytes * (includesLdFrequencyWorkspace ? 8UL : 5UL));
        ulong fixedBytesForTwoStreams = checked(fixedBytesPerStream * 2UL);

        ulong boundedAvailable = totalBytes == 0
            ? availableBytes
            : Math.Min(availableBytes, totalBytes);
        ulong reserveBytes = Math.Max(
            MinimumFreeMemoryReserveBytes,
            totalBytes / 16UL);
        if (boundedAvailable <= reserveBytes)
        {
            return 0;
        }

        // cuFFT owns plan work areas outside the explicit RF workspace. Spend
        // only one sixth of the post-reserve free bytes on the calculated
        // arrays so plan creation, fragmentation, and both streams retain room.
        ulong rawWorkspaceBudget =
            (boundedAvailable - reserveBytes) / WorkspaceSafetyMultiplier;
        if (rawWorkspaceBudget <= fixedBytesForTwoStreams)
        {
            return 0;
        }

        ulong blocks =
            (rawWorkspaceBudget - fixedBytesForTwoStreams) / perBlockBytes;
        return (int)Math.Min((ulong)requestedBlockCount, blocks);
    }

    internal static CudaRfBackendCompatibility CheckCompatibility(
        CudaBackendContext context,
        RfBlockDecodePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        CudaRfPipelineDescriptor descriptor = pipeline.DescribeCudaRfPipeline();
        return TryBuildPlan(context, descriptor, out _, out string reason)
            ? CudaRfBackendCompatibility.Supported()
            : CudaRfBackendCompatibility.Unsupported(reason);
    }

    internal static bool TryCreate(
        CudaBackendContext context,
        RfBlockDecodePipeline pipeline,
        out CudaRfBlockComputeBackend? backend,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        CudaRfPipelineDescriptor descriptor = pipeline.DescribeCudaRfPipeline();
        if (!TryBuildPlan(context, descriptor, out CudaRfExecutionPlan? plan, out reason))
        {
            backend = null;
            return false;
        }

        backend = new CudaRfBlockComputeBackend(context, pipeline, plan!);
        return true;
    }

    public RfPipelineBlock[] DecodeBatch(
        RfBlockDecodePipeline pipeline,
        IReadOnlyList<double[]> preparedInputs,
        bool reportDiagnostics)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(preparedInputs);
        if (!ReferenceEquals(pipeline, _pipeline))
        {
            throw new InvalidOperationException(
                "A CUDA RF backend cannot be reused with a different decode pipeline.");
        }

        if (preparedInputs.Count == 0)
        {
            return [];
        }

        int sampleCount = _plan.Descriptor.SampleCount;
        int batchCount = preparedInputs.Count;
        int totalSampleCount = checked(sampleCount * batchCount);
        var contiguousInput = new double[totalSampleCount];
        for (int batch = 0; batch < batchCount; batch++)
        {
            double[] input = preparedInputs[batch]
                ?? throw new ArgumentException("A prepared CUDA RF input block is null.", nameof(preparedInputs));
            if (input.Length != sampleCount)
            {
                throw new ArgumentException(
                    $"Prepared CUDA RF block {batch} contains {input.Length} samples; {sampleCount} are required.",
                    nameof(preparedInputs));
            }

            input.CopyTo(contiguousInput, batch * sampleCount);
        }

        CudaRfBatchBuffers buffers = AllocateBuffers(totalSampleCount, batchCount);
        LaserDiscAnalogAudioFilterSet? ldAnalogAudio = _plan.LdAnalogAudioFilters;
        var job = new CudaRfBatchJob
        {
            Flags = buffers.Flags,
            SampleCount = sampleCount,
            BatchCount = batchCount,
            StreamIndex = unchecked((uint)(Interlocked.Increment(ref _nextStreamIndex) - 1)) & 1u,
            Mode = _plan.Descriptor.Mode,
            DemodPhaseScale = _plan.Descriptor.Mode == CudaRfMode.Cvbs
                ? 0.0
                : _plan.Descriptor.SampleRateHz / Math.Tau,
            CvbsRawScale = _plan.Descriptor.CvbsRawScale,
            CvbsRawOffset = _plan.Descriptor.CvbsRawOffset,
            Input = contiguousInput,
            RfVideoFilter = _plan.RfVideoFilter,
            RfHighPassFilter = _plan.RfHighPassFilter,
            MtfFilter = _plan.MtfFilter,
            DemodVideoFilter = _plan.VideoFilter,
            DemodVideoLowPassFilter = _plan.VideoLowPassFilter,
            RfHighPassOutput = buffers.RfHighPass,
            AnalyticRealOutput = buffers.AnalyticReal,
            AnalyticImaginaryOutput = buffers.AnalyticImaginary,
            EnvelopeOutput = buffers.Envelope,
            DemodRawOutput = buffers.DemodRaw,
            VideoOutput = buffers.Video,
            VideoLowPassOutput = buffers.VideoLowPass,
            LdFrequencyOptions = _plan.LdEfmFilter is null && ldAnalogAudio is null
                ? null
                : new CudaLdFrequencyOptions
                {
                    AudioLeftLowBin = ldAnalogAudio?.Left.LowBin ?? 0,
                    AudioLeftBinCount = ldAnalogAudio?.Left.BinCount ?? 0,
                    AudioRightLowBin = ldAnalogAudio?.Right.LowBin ?? 0,
                    AudioRightBinCount = ldAnalogAudio?.Right.BinCount ?? 0,
                    EfmFilter = _plan.LdEfmFilter,
                    AudioLeftFilter = ldAnalogAudio?.Left.Stage1Filter,
                    AudioRightFilter = ldAnalogAudio?.Right.Stage1Filter,
                    EfmOutput = buffers.LdEfm,
                    AudioLeftOutput = buffers.LdAudioLeft,
                    AudioRightOutput = buffers.LdAudioRight
                }
        };

        CudaNativeStatus status;
        lock (_executeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshDynamicFilters();
            status = _context.ExecuteRfBatch(job);
        }

        if (status != CudaNativeStatus.Success)
        {
            throw new InvalidOperationException(
                $"CUDA RF batch execution failed after the CUDA backend was selected: {_context.GetLastError(status)}");
        }

        var results = new RfPipelineBlock[batchCount];
        for (int batch = 0; batch < batchCount; batch++)
        {
            int offset = batch * sampleCount;
            double[] input = preparedInputs[batch];
            double[] demodRaw = SliceRequired(buffers.DemodRaw, offset, sampleCount, nameof(buffers.DemodRaw));
            double[] rfHighPass = SliceOptional(buffers.RfHighPass, offset, sampleCount);
            Complex[] analytic = CombineAnalytic(
                buffers.AnalyticReal,
                buffers.AnalyticImaginary,
                offset,
                sampleCount);
            short[]? precomputedEfm = buffers.LdEfm is null
                ? null
                : pipeline.ConvertCudaLdEfmOutput(
                    buffers.LdEfm.AsSpan(offset, sampleCount));
            LaserDiscAnalogAudioBlock? precomputedAnalogAudio = null;
            if (ldAnalogAudio is not null)
            {
                Complex[] leftOutput = buffers.LdAudioLeft
                    ?? throw new InvalidDataException("CUDA LD analog-audio left output is missing.");
                Complex[] rightOutput = buffers.LdAudioRight
                    ?? throw new InvalidDataException("CUDA LD analog-audio right output is missing.");
                precomputedAnalogAudio = pipeline.ConvertCudaLdAnalogAudioOutput(
                    leftOutput.AsSpan(batch * ldAnalogAudio.Left.BinCount, ldAnalogAudio.Left.BinCount),
                    rightOutput.AsSpan(batch * ldAnalogAudio.Right.BinCount, ldAnalogAudio.Right.BinCount));
            }

            results[batch] = _plan.Graph switch
            {
                CudaRfExecutionGraph.StandardFullGpu => pipeline.CompleteCudaStandardFullGraph(
                    input,
                    SliceRequired(buffers.Video, offset, sampleCount, nameof(buffers.Video)),
                    demodRaw,
                    analytic,
                    SliceRequired(buffers.Envelope, offset, sampleCount, nameof(buffers.Envelope)),
                    SliceRequired(buffers.VideoLowPass, offset, sampleCount, nameof(buffers.VideoLowPass)),
                    rfHighPass,
                    reportDiagnostics,
                    precomputedEfm,
                    precomputedAnalogAudio),
                CudaRfExecutionGraph.StandardGpuFirstStage => pipeline.CompleteCudaStandardFirstStage(
                    input,
                    demodRaw,
                    analytic,
                    SliceRequired(buffers.Envelope, offset, sampleCount, nameof(buffers.Envelope)),
                    rfHighPass,
                    reportDiagnostics,
                    precomputedEfm,
                    precomputedAnalogAudio),
                CudaRfExecutionGraph.VhsGpuFirstStage => pipeline.CompleteCudaVhsFirstStage(
                    input,
                    demodRaw,
                    analytic,
                    rfHighPass,
                    reportDiagnostics),
                CudaRfExecutionGraph.CvbsGpu => pipeline.CompleteCudaCvbsGraph(
                    input,
                    demodRaw,
                    SliceRequired(buffers.Envelope, offset, sampleCount, nameof(buffers.Envelope)),
                    SliceRequired(buffers.VideoLowPass, offset, sampleCount, nameof(buffers.VideoLowPass))),
                _ => throw new InvalidOperationException("Unknown CUDA RF execution graph.")
            };
        }

        return results;
    }

    public void Dispose()
    {
        lock (_executeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _context.Dispose();
        }
    }

    private void RefreshDynamicFilters()
    {
        if (_plan.MtfFilter is not { } activeMtf)
        {
            return;
        }

        // LD auto-MTF and seek recovery update the full response in place.
        // Refresh the submitted half-spectrum only after any prior batch has
        // completed under _executeLock so every batch observes one complete
        // filter version. The native side compares filter contents and skips
        // the H2D upload when this copy is unchanged.
        _plan.Descriptor.RfMtfFilter
            .AsSpan(0, activeMtf.Length)
            .CopyTo(activeMtf);
    }

    private CudaRfBatchBuffers AllocateBuffers(int totalSampleCount, int batchCount)
    {
        bool retainDiagnostics = _plan.Descriptor.RetainRfDiagnosticChannels;
        CudaRfBatchFlags flags = CudaRfBatchFlags.OutputDemodRaw;
        double[]? highPass = null;
        double[]? analyticReal = null;
        double[]? analyticImaginary = null;
        double[]? envelope = null;
        double[]? video = null;
        double[]? videoLowPass = null;
        double[]? ldEfm = null;
        Complex[]? ldAudioLeft = null;
        Complex[]? ldAudioRight = null;

        if ((retainDiagnostics && _plan.Descriptor.Mode != CudaRfMode.Cvbs)
            || _plan.Graph == CudaRfExecutionGraph.VhsGpuFirstStage)
        {
            flags |= CudaRfBatchFlags.OutputAnalytic;
            analyticReal = new double[totalSampleCount];
            analyticImaginary = new double[totalSampleCount];
        }

        if (retainDiagnostics && _plan.Descriptor.Mode != CudaRfMode.Cvbs)
        {
            flags |= CudaRfBatchFlags.OutputHighPass;
            highPass = new double[totalSampleCount];
        }

        if (_plan.Graph is CudaRfExecutionGraph.StandardFullGpu
            or CudaRfExecutionGraph.StandardGpuFirstStage
            or CudaRfExecutionGraph.CvbsGpu)
        {
            flags |= CudaRfBatchFlags.OutputEnvelope;
            envelope = new double[totalSampleCount];
        }

        if (_plan.Graph == CudaRfExecutionGraph.StandardFullGpu)
        {
            flags |= CudaRfBatchFlags.OutputVideo | CudaRfBatchFlags.OutputVideoLowPass;
            video = new double[totalSampleCount];
            videoLowPass = new double[totalSampleCount];
        }
        else if (_plan.Graph == CudaRfExecutionGraph.CvbsGpu)
        {
            flags |= CudaRfBatchFlags.OutputVideoLowPass;
            videoLowPass = new double[totalSampleCount];
        }

        if (_plan.Descriptor.AppliesMtf && _plan.Descriptor.Mode != CudaRfMode.Cvbs)
        {
            flags |= CudaRfBatchFlags.ApplyMtf;
        }

        if (_plan.LdEfmFilter is not null)
        {
            flags |= CudaRfBatchFlags.OutputLdEfm;
            ldEfm = new double[totalSampleCount];
        }

        if (_plan.LdAnalogAudioFilters is { } ldAnalogAudio)
        {
            flags |= CudaRfBatchFlags.OutputLdAnalogAudio;
            ldAudioLeft = new Complex[checked(ldAnalogAudio.Left.BinCount * batchCount)];
            ldAudioRight = new Complex[checked(ldAnalogAudio.Right.BinCount * batchCount)];
        }

        return new CudaRfBatchBuffers(
            flags,
            highPass,
            analyticReal,
            analyticImaginary,
            envelope,
            new double[totalSampleCount],
            video,
            videoLowPass,
            ldEfm,
            ldAudioLeft,
            ldAudioRight);
    }

    private static bool TryBuildPlan(
        CudaBackendContext context,
        CudaRfPipelineDescriptor descriptor,
        out CudaRfExecutionPlan? plan,
        out string reason)
    {
        plan = null;
        reason = string.Empty;
        if (context.IsDisposed)
        {
            reason = "The probed CUDA context has already been disposed.";
            return false;
        }

        if (descriptor.SampleCount <= 0 || (descriptor.SampleCount & 1) != 0)
        {
            reason = $"CUDA RF requires a positive even block length; got {descriptor.SampleCount}.";
            return false;
        }

        if (context.BlockLength != descriptor.SampleCount)
        {
            reason = $"CUDA context block length {context.BlockLength} does not match the RF pipeline block length {descriptor.SampleCount}.";
            return false;
        }

        foreach ((Complex[] filter, string name, bool allowEmpty) in EnumerateFilters(descriptor))
        {
            if ((!allowEmpty || filter.Length != 0) && filter.Length != descriptor.SampleCount)
            {
                reason = $"CUDA RF filter '{name}' contains {filter.Length} bins; {descriptor.SampleCount} are required.";
                return false;
            }
        }

        if (descriptor.LdEfmFilter is { } ldEfmFilter &&
            ldEfmFilter.Length != descriptor.SampleCount)
        {
            reason = $"CUDA LD EFM filter contains {ldEfmFilter.Length} bins; {descriptor.SampleCount} are required.";
            return false;
        }

        if ((descriptor.LdEfmFilter is not null || descriptor.LdAnalogAudioFilters is not null) &&
            descriptor.Mode != CudaRfMode.StandardConjugate)
        {
            reason = "CUDA LD EFM and analog-audio frequency branches require standard conjugate RF mode.";
            return false;
        }

        if (descriptor.LdAnalogAudioFilters is { } ldAnalogAudio &&
            !ValidateLdAnalogAudio(descriptor.SampleCount, ldAnalogAudio, out reason))
        {
            return false;
        }

        CudaRfExecutionGraph graph;
        switch (descriptor.Mode)
        {
            case CudaRfMode.StandardConjugate:
                if (!RequireCapabilities(
                        context.Capabilities,
                        CudaBackendCapabilities.RfBatchTwoStage |
                        CudaBackendCapabilities.RfModeStandard |
                        CudaBackendCapabilities.RfConjugateDemod |
                        CudaBackendCapabilities.RfEnvelope,
                        "standard conjugate RF graph",
                        out reason))
                {
                    return false;
                }

                if (descriptor.LdEfmFilter is not null &&
                    !RequireCapabilities(
                        context.Capabilities,
                        CudaBackendCapabilities.LdEfmFrequency,
                        "LD EFM frequency branch",
                        out reason))
                {
                    return false;
                }

                if (descriptor.LdAnalogAudioFilters is not null &&
                    !RequireCapabilities(
                        context.Capabilities,
                        CudaBackendCapabilities.LdAnalogAudioFrequency,
                        "LD analog-audio frequency branches",
                        out reason))
                {
                    return false;
                }

                if (descriptor.RemoveLdPalV4300DSpur)
                {
                    reason = "CUDA standard RF mode does not implement the LD PAL V4300D spectral-spike removal pass.";
                    return false;
                }

                if (descriptor.RfHighBoost is { Multiplier: not 0.0 })
                {
                    reason = "CUDA standard RF mode does not implement envelope-dependent RF high boost.";
                    return false;
                }

                if (descriptor.DiffDemodRepair is not null
                    || descriptor.ChromaTrap is not null
                    || descriptor.SharpnessEq is not null
                    || descriptor.NonlinearDeemphasis is not null
                    || descriptor.SubDeemphasis is not null
                    || descriptor.BetamaxFscNotchHz is not null)
                {
                    reason = "CUDA standard RF mode cannot preserve the selected demodulation/video post-filter options.";
                    return false;
                }

                graph = descriptor.RequiresCpuStandardVideoStage
                    ? CudaRfExecutionGraph.StandardGpuFirstStage
                    : CudaRfExecutionGraph.StandardFullGpu;
                if (graph == CudaRfExecutionGraph.StandardFullGpu
                    && !RequireCapabilities(
                        context.Capabilities,
                        CudaBackendCapabilities.RfVideo | CudaBackendCapabilities.RfVideoLowPass,
                        "standard second-stage video graph",
                        out reason))
                {
                    return false;
                }

                if (!ValidateStandardHalfSpectrumEquivalence(descriptor, graph, out reason))
                {
                    return false;
                }

                break;

            case CudaRfMode.VhsRustApproximation:
                if (!RequireCapabilities(
                        context.Capabilities,
                        CudaBackendCapabilities.RfBatchTwoStage |
                        CudaBackendCapabilities.RfModeVhsRustApproximation |
                        CudaBackendCapabilities.VhsRustDemodKernel |
                        CudaBackendCapabilities.RfAnalytic,
                        "VHS Rust-compatible RF first stage",
                        out reason))
                {
                    return false;
                }

                if (descriptor.RemoveLdPalV4300DSpur)
                {
                    reason = "CUDA VHS mode cannot be combined with LD PAL V4300D spectral-spike removal.";
                    return false;
                }

                if (descriptor.RfHighBoost is { Multiplier: not 0.0 })
                {
                    reason = "CUDA VHS mode does not yet support envelope-dependent RF high boost; use CPU or disable high boost.";
                    return false;
                }

                if (descriptor.SharpnessEq is not null)
                {
                    reason = "CUDA VHS mode cannot be combined with stateful sharpness EQ.";
                    return false;
                }

                graph = CudaRfExecutionGraph.VhsGpuFirstStage;
                break;

            case CudaRfMode.Cvbs:
                if (!RequireCapabilities(
                        context.Capabilities,
                        CudaBackendCapabilities.RfBatchTwoStage |
                        CudaBackendCapabilities.RfModeCvbs |
                        CudaBackendCapabilities.RfEnvelope |
                        CudaBackendCapabilities.RfVideoLowPass,
                        "CVBS reconstruction graph",
                        out reason))
                {
                    return false;
                }

                if (descriptor.ChromaTrap is not null
                    || descriptor.CvbsVideoNotchHz is not null
                    || descriptor.SharpnessEq is not null)
                {
                    reason = "CUDA CVBS mode cannot preserve the selected chroma-trap, video-notch, or stateful sharpness pass.";
                    return false;
                }

                graph = CudaRfExecutionGraph.CvbsGpu;
                break;

            default:
                reason = $"CUDA RF mode '{descriptor.Mode}' is unknown.";
                return false;
        }

        CudaBackendCapabilities optionalCapabilities = CudaBackendCapabilities.None;
        if (descriptor.RetainRfDiagnosticChannels)
        {
            if (descriptor.Mode != CudaRfMode.Cvbs)
            {
                optionalCapabilities |= CudaBackendCapabilities.RfHighPass;
                optionalCapabilities |= CudaBackendCapabilities.RfAnalytic;
            }
        }

        if (descriptor.AppliesMtf && descriptor.Mode != CudaRfMode.Cvbs)
        {
            optionalCapabilities |= CudaBackendCapabilities.RfMtf;
        }

        if (!RequireCapabilities(
                context.Capabilities,
                optionalCapabilities,
                "selected RF diagnostic/MTF outputs",
                out reason))
        {
            return false;
        }

        int halfLength = (descriptor.SampleCount / 2) + 1;
        plan = new CudaRfExecutionPlan(
            descriptor,
            graph,
            descriptor.Mode == CudaRfMode.Cvbs ? null : descriptor.RfVideoFilter[..halfLength],
            descriptor.RetainRfDiagnosticChannels && descriptor.Mode != CudaRfMode.Cvbs
                ? descriptor.RfHighPassFilter[..halfLength]
                : null,
            descriptor.AppliesMtf && descriptor.Mode != CudaRfMode.Cvbs
                ? descriptor.RfMtfFilter[..halfLength]
                : null,
            graph == CudaRfExecutionGraph.StandardFullGpu
                ? descriptor.VideoFilter[..halfLength]
                : null,
            graph is CudaRfExecutionGraph.StandardFullGpu or CudaRfExecutionGraph.CvbsGpu
                ? descriptor.VideoLowPassFilter[..halfLength]
                : null,
            descriptor.LdEfmFilter?[..halfLength],
            descriptor.LdAnalogAudioFilters);
        return true;
    }

    private static IEnumerable<(Complex[] Filter, string Name, bool AllowEmpty)> EnumerateFilters(
        CudaRfPipelineDescriptor descriptor)
    {
        yield return (descriptor.RfVideoFilter, "rf-video", false);
        yield return (descriptor.RfHighPassFilter, "rf-high-pass", false);
        yield return (descriptor.RfMtfFilter, "rf-mtf", true);
        yield return (descriptor.VideoFilter, "video", false);
        yield return (descriptor.VideoLowPassFilter, "video-low-pass", false);
    }

    private static bool RequireCapabilities(
        CudaBackendCapabilities actual,
        CudaBackendCapabilities required,
        string operation,
        out string reason)
    {
        CudaBackendCapabilities missing = required & ~actual;
        if (missing == CudaBackendCapabilities.None)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"CUDA component does not support the {operation}; missing capabilities: {missing}.";
        return false;
    }

    private static bool ValidateLdAnalogAudio(
        int sampleCount,
        LaserDiscAnalogAudioFilterSet filters,
        out string reason)
    {
        if (filters.DecimationFactor <= 0)
        {
            reason = "CUDA LD analog-audio decimation factor must be positive.";
            return false;
        }

        if (!ValidateLdAnalogAudioChannel(sampleCount, filters.Left, "left", out reason) ||
            !ValidateLdAnalogAudioChannel(sampleCount, filters.Right, "right", out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool ValidateLdAnalogAudioChannel(
        int sampleCount,
        LaserDiscAnalogAudioChannelFilter filter,
        string channel,
        out string reason)
    {
        bool isPowerOfTwo = filter.BinCount >= 2 &&
            (filter.BinCount & (filter.BinCount - 1)) == 0;
        if (filter.LowBin < 0 ||
            !isPowerOfTwo ||
            (filter.BinCount & 1) != 0 ||
            filter.BinCount > (sampleCount / 2) + 1 ||
            (long)filter.LowBin + (filter.BinCount / 2) > sampleCount / 2)
        {
            reason = $"CUDA LD analog-audio {channel} slice must be an even power-of-two range inside the positive RF half-spectrum.";
            return false;
        }

        if (filter.Stage1Filter.Length != filter.BinCount)
        {
            reason = $"CUDA LD analog-audio {channel} stage-1 filter contains {filter.Stage1Filter.Length} bins; {filter.BinCount} are required.";
            return false;
        }

        if (!double.IsFinite(filter.SliceSampleRateHz) || filter.SliceSampleRateHz <= 0.0 ||
            !double.IsFinite(filter.LowFrequencyHz))
        {
            reason = $"CUDA LD analog-audio {channel} frequency metadata must be finite and its slice sample rate must be positive.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool ValidateStandardHalfSpectrumEquivalence(
        CudaRfPipelineDescriptor descriptor,
        CudaRfExecutionGraph graph,
        out string reason)
    {
        if (descriptor.RetainRfDiagnosticChannels
            && !IsHermitianRealResponse(descriptor.RfHighPassFilter))
        {
            reason = "CUDA R2C high-pass output cannot reproduce the non-Hermitian CPU RF high-pass response.";
            return false;
        }

        if (graph == CudaRfExecutionGraph.StandardFullGpu
            && (!IsHermitianRealResponse(descriptor.VideoFilter)
                || !IsHermitianRealResponse(descriptor.VideoLowPassFilter)))
        {
            reason = "CUDA R2C video output cannot reproduce the selected non-Hermitian CPU video response.";
            return false;
        }

        var rfResponse = new Complex[descriptor.SampleCount];
        for (int i = 0; i < rfResponse.Length; i++)
        {
            rfResponse[i] = descriptor.RfVideoFilter[i]
                * (descriptor.AppliesMtf ? descriptor.RfMtfFilter[i] : Complex.One);
        }

        if (!IsHermitianRealResponse(rfResponse))
        {
            reason = "CUDA R2C analytic construction cannot reproduce the selected non-Hermitian RF video/MTF response.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsHermitianRealResponse(ReadOnlySpan<Complex> response)
    {
        if (response.IsEmpty || (response.Length & 1) != 0)
        {
            return false;
        }

        int nyquist = response.Length / 2;
        if (!NearlyZero(response[0].Imaginary, response[0].Magnitude)
            || !NearlyZero(response[nyquist].Imaginary, response[nyquist].Magnitude))
        {
            return false;
        }

        for (int i = 1; i < nyquist; i++)
        {
            Complex expected = Complex.Conjugate(response[i]);
            Complex actual = response[response.Length - i];
            double scale = Math.Max(1.0, Math.Max(expected.Magnitude, actual.Magnitude));
            if (Complex.Abs(actual - expected) > scale * 1e-12)
            {
                return false;
            }
        }

        return true;
    }

    private static bool NearlyZero(double value, double scale) =>
        Math.Abs(value) <= Math.Max(1.0, scale) * 1e-12;

    private static double[] SliceRequired(double[]? source, int offset, int count, string name)
    {
        if (source is null)
        {
            throw new InvalidDataException($"CUDA RF batch did not materialize required output '{name}'.");
        }

        return source.AsSpan(offset, count).ToArray();
    }

    private static double[] SliceOptional(double[]? source, int offset, int count) =>
        source is null ? [] : source.AsSpan(offset, count).ToArray();

    private static Complex[] CombineAnalytic(
        double[]? real,
        double[]? imaginary,
        int offset,
        int count)
    {
        if (real is null && imaginary is null)
        {
            return [];
        }

        if (real is null || imaginary is null)
        {
            throw new InvalidDataException("CUDA RF batch returned only one analytic component.");
        }

        var output = new Complex[count];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = new Complex(real[offset + i], imaginary[offset + i]);
        }

        return output;
    }

    private sealed record CudaRfExecutionPlan(
        CudaRfPipelineDescriptor Descriptor,
        CudaRfExecutionGraph Graph,
        Complex[]? RfVideoFilter,
        Complex[]? RfHighPassFilter,
        Complex[]? MtfFilter,
        Complex[]? VideoFilter,
        Complex[]? VideoLowPassFilter,
        Complex[]? LdEfmFilter,
        LaserDiscAnalogAudioFilterSet? LdAnalogAudioFilters);

    private sealed record CudaRfBatchBuffers(
        CudaRfBatchFlags Flags,
        double[]? RfHighPass,
        double[]? AnalyticReal,
        double[]? AnalyticImaginary,
        double[]? Envelope,
        double[] DemodRaw,
        double[]? Video,
        double[]? VideoLowPass,
        double[]? LdEfm,
        Complex[]? LdAudioLeft,
        Complex[]? LdAudioRight);
}
