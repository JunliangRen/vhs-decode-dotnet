using System.Numerics;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

public sealed record RfDecodedSpan(
    long StartSample,
    double[] Input,
    double[] Video,
    double[] DemodRaw,
    double[]? Envelope = null,
    double[]? VideoLowPass = null,
    double[]? RfHighPass = null,
    short[]? Efm = null,
    LaserDiscAnalogAudioBlock? AnalogAudio = null,
    double[]? Chroma = null,
    double[]? VideoBurst = null,
    double[]? VideoPilot = null)
{
    internal int? AvailableSampleCountOverride { get; init; }

    internal RfBlockStreamDecoder.VhsPayloadMaterializer? DeferredVhsPayload { get; init; }
}

public sealed class RfBlockStreamDecoder : IDisposable
{
    private const int DecodedBlockCacheCapacity = 16;
    private const int ReusableSpanBufferSetCapacity = 2;
    // Responses are shared immutable arrays; 256 block keys cover the normal
    // 88-block NTSC lookahead plus overlapping recovery reads without retaining
    // the corresponding decoded payloads.
    private const int LaserDiscCompatibilityCacheBlocks = 256;
    private const int MaximumAdditionalPrefetchBlocks = 8;
    private const int CacheOperationIdle = 0;
    private const int CacheOperationOrdinary = 1;
    private const int CacheOperationStagedVhs = 2;
    internal const int MaximumConcurrentPrefetchBlocks = 12;
    internal const int MaximumPrefetchBlocks = 32;
    private readonly RfBlockDecodePipeline _pipeline;
    private readonly Dictionary<long, RfPipelineBlock> _decodedBlockCache = [];
    private readonly Dictionary<long, RfPipelineBlock> _prefetchedBlockCache = [];
    private readonly Dictionary<long, RfPipelineBlock> _sequentialBlockCache = [];
    private readonly Dictionary<long, Complex[]> _laserDiscCompatibilityMtfByBlock = [];
    private readonly List<RfPipelineBlock> _serialDeferredReleases = [];
    private readonly int _decodedBlockCacheCapacity;
    private readonly int _laserDiscCompatibilityPrefetchBlocks;
    private Complex[]? _activeLaserDiscCompatibilityMtf;
    private Stream? _decodedBlockCacheStream;
    private long? _lastReadFirstBlock;
    private long? _lastSequentialDecodedBlock;
    private PrefetchOperation? _prefetchOperation;
    private VhsPayloadMaterializer? _activeVhsPayload;
    private int _cacheOperationState;
    private int _prefetchCancellationCount;
    private readonly ReusableSpanBuffers?[] _reusableSpanBuffers = new ReusableSpanBuffers?[ReusableSpanBufferSetCapacity];
    private bool _disposed;

    public RfBlockStreamDecoder(
        RfBlockDecodePipeline pipeline,
        int blockLength,
        int blockCut,
        int blockCutEnd,
        int workerThreads = 1,
        int prefetchBlocks = 0,
        int laserDiscCompatibilityPrefetchBlocks = 0)
    {
        if (blockLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockLength));
        }

        if (blockCut < 0 || blockCutEnd < 0 || blockCut + blockCutEnd >= blockLength)
        {
            throw new ArgumentException("Block cuts must leave at least one decoded sample.");
        }

        if (workerThreads < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerThreads));
        }

        if (prefetchBlocks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetchBlocks));
        }

        if (laserDiscCompatibilityPrefetchBlocks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laserDiscCompatibilityPrefetchBlocks));
        }

        _pipeline = pipeline;
        BlockLength = blockLength;
        BlockCut = blockCut;
        BlockCutEnd = blockCutEnd;
        BlockStride = blockLength - blockCut - blockCutEnd;
        WorkerThreads = workerThreads;
        PrefetchBlocks = workerThreads > 1 && !_pipeline.RequiresSequentialBlockDecode
            ? Math.Min(prefetchBlocks, MaximumPrefetchBlocks)
            : 0;
        PrefetchWorkerThreads = Math.Min(
            Math.Min(WorkerThreads, PrefetchBlocks),
            MaximumConcurrentPrefetchBlocks);
        _decodedBlockCacheCapacity = checked(DecodedBlockCacheCapacity + PrefetchBlocks);
        _laserDiscCompatibilityPrefetchBlocks = laserDiscCompatibilityPrefetchBlocks;
    }

    public int BlockLength { get; }

    public int BlockCut { get; }

    public int BlockCutEnd { get; }

    public int BlockStride { get; }

    public int WorkerThreads { get; }

    public int PrefetchBlocks { get; }

    internal int PrefetchWorkerThreads { get; }

    internal int PrefetchCancellationCount => Volatile.Read(ref _prefetchCancellationCount);

    internal int CachedDecodedBlockCount => _decodedBlockCache.Count;

    internal Complex[]? LaserDiscCompatibilityMtfForTesting(long block)
        => _laserDiscCompatibilityMtfByBlock.TryGetValue(block, out Complex[]? response)
            ? response
            : null;

    internal int CachedPrefetchedBlockCount
    {
        get
        {
            int completedActiveBlocks = 0;
            PrefetchOperation? operation = Volatile.Read(ref _prefetchOperation);
            if (operation is not null)
            {
                foreach (PrefetchSlot slot in operation.Slots)
                {
                    if (!slot.Harvested
                        && slot.Completion.Task is { IsCompletedSuccessfully: true } completed
                        && completed.Result is not null)
                    {
                        completedActiveBlocks++;
                    }
                }
            }

            return _prefetchedBlockCache.Count + completedActiveBlocks;
        }
    }

    internal static int RecommendedPrefetchBlocks(int workerThreads, int processorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerThreads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorCount);
        int effectiveWorkers = Math.Min(workerThreads, processorCount);
        if (effectiveWorkers <= 1)
        {
            return 0;
        }

        long oneAdditionalWave = (long)effectiveWorkers
            + Math.Min(effectiveWorkers, MaximumAdditionalPrefetchBlocks);
        return (int)Math.Min(oneAdditionalWave, MaximumPrefetchBlocks);
    }

    internal static int LaserDiscCompatibilityPrefetchBlocks(
        double sampleRateHz,
        double framesPerSecond,
        int blockStride)
    {
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        if (blockStride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockStride));
        }

        long samplesPerField = checked((long)(sampleRateHz / (framesPerSecond * 2.0)) + 1L);
        return checked((int)((samplesPerField * 4.0) / blockStride) + 4);
    }

    internal int CachedReusableSpanBufferSetCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _reusableSpanBuffers.Length; i++)
            {
                if (Volatile.Read(ref _reusableSpanBuffers[i]) is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal void InvalidateCachedBlocks()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnterOrdinaryCacheOperation();
        try
        {
            FinishPrefetch(cancel: true, suppressFailures: true);
            ClearBlockCache(_decodedBlockCache);
            ClearBlockCache(_prefetchedBlockCache);
            ClearBlockCache(_sequentialBlockCache);
            _decodedBlockCacheStream = null;
            _lastReadFirstBlock = null;
            _lastSequentialDecodedBlock = null;
            _laserDiscCompatibilityMtfByBlock.Clear();
        }
        finally
        {
            ExitOrdinaryCacheOperation();
        }
    }

    internal void UpdateLaserDiscCompatibilityMtf(Complex[] response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (_laserDiscCompatibilityPrefetchBlocks == 0)
        {
            return;
        }

        _activeLaserDiscCompatibilityMtf = response.ToArray();
    }

    public RfDecodedSpan? Read(Stream stream, long begin, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnterOrdinaryCacheOperation();
        try
        {
            return ReadCore(
                stream,
                begin,
                length,
                reusableBuffers: null,
                stageVhsPayload: false,
                out _);
        }
        finally
        {
            ExitOrdinaryCacheOperation();
        }
    }

    internal RfDecodedSpanLease? ReadLeased(Stream stream, long begin, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnterOrdinaryCacheOperation();
        try
        {
            return ReadLeasedCore(stream, begin, length);
        }
        finally
        {
            ExitOrdinaryCacheOperation();
        }
    }

    private RfDecodedSpanLease? ReadLeasedCore(Stream stream, long begin, int length)
    {
        if (begin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(begin));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return new RfDecodedSpanLease(
                owner: null,
                buffers: null,
                span: new RfDecodedSpan(begin, [], [], []));
        }

        ReusableSpanBuffers buffers = TakeReusableSpanBuffers(length);
        try
        {
            RfDecodedSpan? span = ReadCore(
                stream,
                begin,
                length,
                buffers,
                stageVhsPayload: false,
                out _);
            if (span is null)
            {
                ReturnReusableSpanBuffers(buffers);
                return null;
            }

            return new RfDecodedSpanLease(this, buffers, span);
        }
        catch
        {
            ReturnReusableSpanBuffers(buffers);
            throw;
        }
    }

    internal RfDecodedSpanLease? ReadVhsStagedLeased(Stream stream, long begin, int length)
    {
        if (WorkerThreads <= 1
            || _pipeline.RequiresSequentialBlockDecode
            || _pipeline.RetainsRfDiagnosticChannels)
        {
            return ReadLeased(stream, begin, length);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (begin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(begin));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return new RfDecodedSpanLease(
                owner: null,
                buffers: null,
                span: new RfDecodedSpan(begin, [], [], []));
        }

        long endExclusive = checked(begin + length);
        if (begin / BlockStride == (endExclusive - 1) / BlockStride)
        {
            return ReadLeased(stream, begin, length);
        }

        ReusableSpanBuffers buffers = TakeReusableSpanBuffers(length);
        int activeCacheOperation = Interlocked.CompareExchange(
            ref _cacheOperationState,
            CacheOperationStagedVhs,
            CacheOperationIdle);
        if (activeCacheOperation != CacheOperationIdle)
        {
            ReturnReusableSpanBuffers(buffers);
            throw new InvalidOperationException(
                activeCacheOperation == CacheOperationStagedVhs
                    ? "Only one staged VHS span can be active per RF stream decoder."
                    : "A staged VHS span cannot start while another RF block cache operation is active.");
        }

        VhsPayloadMaterializer? materializer = null;
        bool reservationOwned = true;
        try
        {
            RfDecodedSpan? span = ReadCore(
                stream,
                begin,
                length,
                buffers,
                stageVhsPayload: true,
                out materializer);
            if (span is null)
            {
                ReleaseStagedVhsRead();
                reservationOwned = false;
                ReturnReusableSpanBuffers(buffers);
                return null;
            }

            if (materializer is null)
            {
                throw new InvalidOperationException("Staged VHS payload was not created.");
            }

            Volatile.Write(ref _activeVhsPayload, materializer);
            reservationOwned = false;
            return new RfDecodedSpanLease(this, buffers, span, materializer);
        }
        catch
        {
            if (materializer is not null)
            {
                materializer.Dispose();
                reservationOwned = false;
            }

            if (reservationOwned)
            {
                ReleaseStagedVhsRead();
            }

            ReturnReusableSpanBuffers(buffers);
            throw;
        }
    }

    private RfDecodedSpan? ReadCore(
        Stream stream,
        long begin,
        int length,
        ReusableSpanBuffers? reusableBuffers,
        bool stageVhsPayload,
        out VhsPayloadMaterializer? stagedMaterializer)
    {
        stagedMaterializer = null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (begin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(begin));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return new RfDecodedSpan(begin, [], [], []);
        }

        long endExclusive = checked(begin + length);
        long firstBlock = begin / BlockStride;
        long lastBlock = (endExclusive - 1) / BlockStride;
        PrepareDecodedBlockCache(stream, firstBlock);
        PrepareLaserDiscCompatibilityMtf(firstBlock, lastBlock);
        int totalDecoded = checked((int)((lastBlock - firstBlock + 1) * BlockStride));
        int offset = checked((int)(begin - (firstBlock * BlockStride)));
        bool retainRfDiagnosticChannels = _pipeline.RetainsRfDiagnosticChannels;
        double[] input = retainRfDiagnosticChannels
            ? reusableBuffers?.Input ?? new double[length]
            : [];
        double[] video = reusableBuffers?.Video ?? new double[length];
        double[] demodRaw = retainRfDiagnosticChannels
            ? reusableBuffers?.DemodRaw ?? new double[length]
            : [];
        double[] envelope = reusableBuffers?.Envelope ?? new double[length];
        double[] videoLowPass = reusableBuffers?.VideoLowPass ?? new double[length];
        double[] rfHighPass = retainRfDiagnosticChannels
            ? reusableBuffers?.RfHighPass ?? new double[length]
            : [];
        double[]? chroma = null;
        short[]? efm = null;
        double[]? audioLeft = null;
        double[]? audioRight = null;
        double[]? videoBurst = null;
        double[]? videoPilot = null;
        int audioDecimationFactor = 0;
        int audioDestination = 0;
        int destination = 0;
        RfPipelineBlock[]? stagedBlocks = null;
        RfPipelineBlock[]? stagedDeferredBlocks = null;

        void AppendAnalogAudio(LaserDiscAnalogAudioBlock audio)
        {
            if (audioDecimationFactor == 0)
            {
                audioDecimationFactor = audio.DecimationFactor;
                int totalAudioDecoded = checked(totalDecoded / audioDecimationFactor);
                if (reusableBuffers is null)
                {
                    audioLeft = new double[totalAudioDecoded];
                    audioRight = new double[totalAudioDecoded];
                }
                else
                {
                    (audioLeft, audioRight) = reusableBuffers.GetAudio(totalAudioDecoded);
                }
            }

            CopyTrimmed(audio, audioLeft!, audioRight!, audioDestination);
            audioDestination += AudioBlockStride(audioDecimationFactor);
        }

        void AppendBlock(RfPipelineBlock pipelineBlock)
        {
            if (retainRfDiagnosticChannels)
            {
                CopyTrimmedWindow(pipelineBlock.Input, input, destination, offset);
                CopyTrimmedWindow(pipelineBlock.Demodulated.DemodRaw, demodRaw, destination, offset);
                CopyTrimmedWindow(
                    pipelineBlock.Demodulated.RfHighPass,
                    rfHighPass,
                    destination,
                    offset,
                    BlockCut - _pipeline.RfHighPassOffset);
            }

            CopyTrimmedWindow(pipelineBlock.Demodulated.Video, video, destination, offset);
            CopyTrimmedWindow(pipelineBlock.Demodulated.Envelope, envelope, destination, offset);
            CopyTrimmedWindow(pipelineBlock.Demodulated.VideoLowPass, videoLowPass, destination, offset);
            if (pipelineBlock.Demodulated.Chroma is not null)
            {
                chroma ??= reusableBuffers?.GetChroma() ?? new double[length];
                CopyTrimmedWindow(pipelineBlock.Demodulated.Chroma, chroma, destination, offset);
            }
            else if (pipelineBlock.Demodulated.ChromaFloat32 is not null)
            {
                chroma ??= reusableBuffers?.GetChroma() ?? new double[length];
                CopyTrimmedWindow(pipelineBlock.Demodulated.ChromaFloat32, chroma, destination, offset);
            }

            if (pipelineBlock.Demodulated.VideoBurst is not null)
            {
                videoBurst ??= reusableBuffers?.GetVideoBurst() ?? new double[length];
                CopyTrimmedWindow(pipelineBlock.Demodulated.VideoBurst, videoBurst, destination, offset);
            }

            if (pipelineBlock.Demodulated.VideoPilot is not null)
            {
                videoPilot ??= reusableBuffers?.GetVideoPilot() ?? new double[length];
                CopyTrimmedWindow(pipelineBlock.Demodulated.VideoPilot, videoPilot, destination, offset);
            }

            if (pipelineBlock.Demodulated.Efm is not null)
            {
                efm ??= reusableBuffers?.GetEfm() ?? new short[length];
                CopyTrimmedWindow(pipelineBlock.Demodulated.Efm, efm, destination, offset);
            }

            if (pipelineBlock.Demodulated.AnalogAudio is not null)
            {
                AppendAnalogAudio(pipelineBlock.Demodulated.AnalogAudio);
            }

            destination += BlockStride;
        }

        void AppendBlocksParallel(
            RfPipelineBlock[] pipelineBlocks,
            ParallelOptions parallelOptions,
            bool syncOnly)
        {
            bool hasChroma = false;
            bool hasEfm = false;
            bool hasVideoBurst = false;
            bool hasVideoPilot = false;
            for (int i = 0; i < pipelineBlocks.Length; i++)
            {
                RfDemodulatedBlock demodulated = pipelineBlocks[i].Demodulated;
                hasChroma |= demodulated.Chroma is not null || demodulated.ChromaFloat32 is not null;
                hasEfm |= demodulated.Efm is not null;
                hasVideoBurst |= demodulated.VideoBurst is not null;
                hasVideoPilot |= demodulated.VideoPilot is not null;
            }

            chroma = hasChroma
                ? reusableBuffers?.GetChroma() ?? new double[length]
                : null;
            efm = hasEfm
                ? reusableBuffers?.GetEfm() ?? new short[length]
                : null;
            videoBurst = hasVideoBurst
                ? reusableBuffers?.GetVideoBurst() ?? new double[length]
                : null;
            videoPilot = hasVideoPilot
                ? reusableBuffers?.GetVideoPilot() ?? new double[length]
                : null;

            Parallel.For(0, pipelineBlocks.Length, parallelOptions, blockIndex =>
            {
                int blockDestination = checked(blockIndex * BlockStride);
                RfPipelineBlock pipelineBlock = pipelineBlocks[blockIndex];
                RfDemodulatedBlock demodulated = pipelineBlock.Demodulated;
                CopyTrimmedWindow(
                    demodulated.VideoLowPass,
                    videoLowPass,
                    blockDestination,
                    offset);
                if (syncOnly)
                {
                    return;
                }

                if (retainRfDiagnosticChannels)
                {
                    CopyTrimmedWindow(pipelineBlock.Input, input, blockDestination, offset);
                    CopyTrimmedWindow(demodulated.DemodRaw, demodRaw, blockDestination, offset);
                    CopyTrimmedWindow(
                        demodulated.RfHighPass,
                        rfHighPass,
                        blockDestination,
                        offset,
                        BlockCut - _pipeline.RfHighPassOffset);
                }

                CopyTrimmedWindow(demodulated.Video, video, blockDestination, offset);
                CopyTrimmedWindow(demodulated.Envelope, envelope, blockDestination, offset);
                if (chroma is not null && demodulated.Chroma is { } blockChroma)
                {
                    CopyTrimmedWindow(blockChroma, chroma, blockDestination, offset);
                }
                else if (chroma is not null && demodulated.ChromaFloat32 is { } blockChromaFloat32)
                {
                    CopyTrimmedWindow(blockChromaFloat32, chroma, blockDestination, offset);
                }

                if (efm is not null && demodulated.Efm is { } blockEfm)
                {
                    CopyTrimmedWindow(blockEfm, efm, blockDestination, offset);
                }

                if (videoBurst is not null && demodulated.VideoBurst is { } blockVideoBurst)
                {
                    CopyTrimmedWindow(blockVideoBurst, videoBurst, blockDestination, offset);
                }

                if (videoPilot is not null && demodulated.VideoPilot is { } blockVideoPilot)
                {
                    CopyTrimmedWindow(blockVideoPilot, videoPilot, blockDestination, offset);
                }
            });

            if (syncOnly)
            {
                return;
            }

            foreach (RfPipelineBlock pipelineBlock in pipelineBlocks)
            {
                if (pipelineBlock.Demodulated.AnalogAudio is { } audio)
                {
                    AppendAnalogAudio(audio);
                }
            }
        }

        if (WorkerThreads <= 1 || firstBlock == lastBlock || _pipeline.RequiresSequentialBlockDecode)
        {
            if (_pipeline.RequiresSequentialBlockDecode && _sequentialBlockCache.Count > 0)
            {
                foreach (long staleBlock in _sequentialBlockCache.Keys.Where(block => block < firstBlock).ToArray())
                {
                    RemoveAndReleaseBlock(_sequentialBlockCache, staleBlock);
                }
            }

            List<RfPipelineBlock> deferredReleases = _serialDeferredReleases;
            try
            {
                for (long block = firstBlock; block <= lastBlock; block++)
                {
                    if (_pipeline.RequiresSequentialBlockDecode
                        && _lastSequentialDecodedBlock is { } lastDecoded
                        && block > lastDecoded + 1)
                    {
                        for (long warmBlock = lastDecoded + 1; warmBlock < block; warmBlock++)
                        {
                            long warmSample = checked(warmBlock * BlockStride);
                            RfPipelineBlock? warmed = _pipeline.DecodeStreamBlockWithInput(
                                stream,
                                warmSample,
                                BlockLength);
                            if (warmed is null)
                            {
                                return null;
                            }

                            _sequentialBlockCache[warmBlock] = warmed;
                            _lastSequentialDecodedBlock = warmBlock;
                        }
                    }

                    RfPipelineBlock? pipelineBlock;
                    if (_pipeline.RequiresSequentialBlockDecode
                        && _sequentialBlockCache.TryGetValue(block, out RfPipelineBlock? cachedBlock))
                    {
                        pipelineBlock = cachedBlock;
                    }
                    else if (!_pipeline.RequiresSequentialBlockDecode
                        && TryTakeDecodedBlock(block, out cachedBlock, deferredReleases))
                    {
                        pipelineBlock = cachedBlock;
                    }
                    else
                    {
                        StopPrefetchBeforeDirectRead();
                        long sample = checked(block * BlockStride);
                        pipelineBlock = _pipeline.DecodeStreamBlockWithInput(
                            stream,
                            sample,
                            BlockLength,
                            LaserDiscCompatibilityMtfForBlock(block));
                        if (pipelineBlock is not null && _pipeline.RequiresSequentialBlockDecode)
                        {
                            _sequentialBlockCache[block] = pipelineBlock;
                            if (!_lastSequentialDecodedBlock.HasValue || block > _lastSequentialDecodedBlock.Value)
                            {
                                _lastSequentialDecodedBlock = block;
                            }
                        }
                        else if (pipelineBlock is not null)
                        {
                            CacheDecodedBlock(block, pipelineBlock, deferredReleases);
                        }
                    }

                    if (pipelineBlock is null)
                    {
                        return null;
                    }

                    AppendBlock(pipelineBlock);
                    ReleaseDeferredBlocks(deferredReleases);
                }
            }
            finally
            {
                ReleaseDeferredBlocks(deferredReleases);
            }
        }
        else
        {
            int blockCount = checked((int)(lastBlock - firstBlock + 1));
            var preparedInputs = new double[blockCount][];
            var missingBlocks = new int[blockCount];
            int missingBlockCount = 0;
            var decodedBlocks = new RfPipelineBlock[blockCount];
            Complex[]?[]? laserDiscCompatibilityMtf =
                _activeLaserDiscCompatibilityMtf is null
                    ? null
                    : new Complex[]?[blockCount];
            if (laserDiscCompatibilityMtf is not null)
            {
                for (int i = 0; i < blockCount; i++)
                {
                    laserDiscCompatibilityMtf[i] =
                        LaserDiscCompatibilityMtfForBlock(firstBlock + i);
                }
            }

            ParallelOptions? parallelOptions = null;
            bool missingBlocksDecoded = false;
            int cachedMissingBlockCount = 0;
            List<RfPipelineBlock> deferredReleases = [];
            try
            {
                try
                {
                    for (int i = 0; i < blockCount; i++)
                    {
                        long block = firstBlock + i;
                        if (TryTakeDecodedBlock(
                                block,
                                out RfPipelineBlock cachedBlock,
                                deferredReleases))
                        {
                            decodedBlocks[i] = cachedBlock;
                            continue;
                        }

                        StopPrefetchBeforeDirectRead();
                        long sample = checked((firstBlock + i) * BlockStride);
                        double[]? preparedInput = _pipeline.LoadStreamBlockInput(
                            stream,
                            sample,
                            BlockLength,
                            parallelDecode: true);
                        if (preparedInput is null)
                        {
                            return null;
                        }

                        preparedInputs[i] = preparedInput;
                        missingBlocks[missingBlockCount++] = i;
                    }

                    parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(WorkerThreads, blockCount)
                    };
                    Parallel.For(
                        0,
                        missingBlockCount,
                        parallelOptions,
                        missingIndex =>
                        {
                            int blockIndex = missingBlocks[missingIndex];
                            decodedBlocks[blockIndex] = _pipeline.DecodePreparedStreamBlock(
                                preparedInputs[blockIndex],
                                rfMtfOverride: laserDiscCompatibilityMtf?[blockIndex]);
                        });
                    missingBlocksDecoded = true;
                }
                finally
                {
                    for (int i = 0; i < missingBlockCount; i++)
                    {
                        _pipeline.ReturnStreamBlockInput(
                            preparedInputs[missingBlocks[i]],
                            parallelDecode: true);
                    }

                    if (!missingBlocksDecoded)
                    {
                        for (int i = 0; i < missingBlockCount; i++)
                        {
                            RfPipelineBlock? decoded = decodedBlocks[missingBlocks[i]];
                            if (decoded is not null)
                            {
                                _pipeline.ReleaseStreamBlock(decoded);
                            }
                        }
                    }
                }

                for (int i = 0; i < missingBlockCount; i++)
                {
                    int blockIndex = missingBlocks[i];
                    CacheDecodedBlock(
                        firstBlock + blockIndex,
                        decodedBlocks[blockIndex],
                        deferredReleases);
                    cachedMissingBlockCount++;
                }

                AppendBlocksParallel(decodedBlocks, parallelOptions!, stageVhsPayload);
                if (stageVhsPayload)
                {
                    var currentBlocks = new HashSet<RfPipelineBlock>(
                        decodedBlocks,
                        ReferenceEqualityComparer.Instance);
                    var heldBlocks = new List<RfPipelineBlock>();
                    for (int i = deferredReleases.Count - 1; i >= 0; i--)
                    {
                        RfPipelineBlock deferred = deferredReleases[i];
                        if (currentBlocks.Contains(deferred))
                        {
                            heldBlocks.Add(deferred);
                            deferredReleases.RemoveAt(i);
                        }
                    }

                    stagedBlocks = decodedBlocks;
                    stagedDeferredBlocks = heldBlocks.ToArray();
                }
            }
            finally
            {
                if (missingBlocksDecoded)
                {
                    for (int i = cachedMissingBlockCount; i < missingBlockCount; i++)
                    {
                        _pipeline.ReleaseStreamBlock(decodedBlocks[missingBlocks[i]]);
                    }
                }

                foreach (RfPipelineBlock block in deferredReleases)
                {
                    _pipeline.ReleaseStreamBlock(block);
                }
            }
        }

        try
        {
            StartPrefetch(stream, lastBlock);
            ScheduleLaserDiscCompatibilityPrefetch(lastBlock);
            if (stageVhsPayload)
            {
                stagedMaterializer = new VhsPayloadMaterializer(
                    this,
                    stagedBlocks
                        ?? throw new InvalidOperationException("Staged VHS blocks were not retained."),
                    stagedDeferredBlocks ?? [],
                    video,
                    envelope,
                    chroma,
                    offset,
                    WorkerThreads);
            }
        }
        catch
        {
            if (stagedDeferredBlocks is not null)
            {
                foreach (RfPipelineBlock block in stagedDeferredBlocks)
                {
                    _pipeline.ReleaseStreamBlock(block);
                }
            }

            throw;
        }

        try
        {
            LaserDiscAnalogAudioBlock? audioSpan = null;
            if (audioLeft is not null && audioRight is not null)
            {
                LaserDiscAnalogAudioBlock assembledAudio = _pipeline.ApplyLaserDiscAnalogAudioPhase2(
                    new LaserDiscAnalogAudioBlock(audioLeft, audioRight, audioDecimationFactor));
                int audioOffset = offset / audioDecimationFactor;
                int audioLength = Math.Min(
                    assembledAudio.Left.Length - audioOffset,
                    (int)Math.Ceiling((double)length / audioDecimationFactor));
                audioSpan = new LaserDiscAnalogAudioBlock(
                    Slice(assembledAudio.Left, audioOffset, audioLength),
                    Slice(assembledAudio.Right, audioOffset, audioLength),
                    audioDecimationFactor,
                    assembledAudio.UsesFloat32Storage);
            }

            return new RfDecodedSpan(
                begin,
                input,
                video,
                demodRaw,
                envelope,
                videoLowPass,
                rfHighPass,
                efm,
                audioSpan,
                chroma,
                videoBurst,
                videoPilot)
            {
                AvailableSampleCountOverride = length,
                DeferredVhsPayload = stagedMaterializer
            };
        }
        catch
        {
            stagedMaterializer?.Dispose();
            stagedMaterializer = null;
            throw;
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        try
        {
            Volatile.Read(ref _activeVhsPayload)?.Dispose();
            FinishPrefetch(cancel: true, suppressFailures: true);
        }
        finally
        {
            ClearBlockCache(_decodedBlockCache);
            ClearBlockCache(_prefetchedBlockCache);
            ClearBlockCache(_sequentialBlockCache);
            _laserDiscCompatibilityMtfByBlock.Clear();
            for (int i = 0; i < _reusableSpanBuffers.Length; i++)
            {
                _ = Interlocked.Exchange(ref _reusableSpanBuffers[i], null);
            }
        }
    }

    private void ReleaseActiveVhsPayload(VhsPayloadMaterializer materializer)
    {
        _ = Interlocked.CompareExchange(
            ref _activeVhsPayload,
            null,
            materializer);
        ReleaseStagedVhsRead();
    }

    private void ReleaseStagedVhsRead()
        => Volatile.Write(ref _cacheOperationState, CacheOperationIdle);

    private void EnterOrdinaryCacheOperation()
    {
        int activeCacheOperation = Interlocked.CompareExchange(
            ref _cacheOperationState,
            CacheOperationOrdinary,
            CacheOperationIdle);
        if (activeCacheOperation == CacheOperationIdle)
        {
            return;
        }

        if (activeCacheOperation == CacheOperationStagedVhs)
        {
            throw new InvalidOperationException(
                "The RF block cache cannot change while a staged VHS span is active.");
        }

        throw new InvalidOperationException(
            "Only one RF block cache operation can be active per stream decoder.");
    }

    private void ExitOrdinaryCacheOperation()
        => Volatile.Write(ref _cacheOperationState, CacheOperationIdle);

    private ReusableSpanBuffers TakeReusableSpanBuffers(int length)
    {
        for (int i = 0; i < _reusableSpanBuffers.Length; i++)
        {
            ReusableSpanBuffers? candidate = Volatile.Read(ref _reusableSpanBuffers[i]);
            if (candidate?.Length == length
                && ReferenceEquals(
                    Interlocked.CompareExchange(ref _reusableSpanBuffers[i], null, candidate),
                    candidate))
            {
                return candidate;
            }
        }

        return new ReusableSpanBuffers(length, _pipeline.RetainsRfDiagnosticChannels);
    }

    private void ReturnReusableSpanBuffers(ReusableSpanBuffers buffers)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        for (int i = 0; i < _reusableSpanBuffers.Length; i++)
        {
            if (Volatile.Read(ref _reusableSpanBuffers[i])?.Length == buffers.Length)
            {
                return;
            }
        }

        for (int i = 0; i < _reusableSpanBuffers.Length; i++)
        {
            if (Interlocked.CompareExchange(ref _reusableSpanBuffers[i], buffers, null) is null)
            {
                if (Volatile.Read(ref _disposed))
                {
                    _ = Interlocked.CompareExchange(ref _reusableSpanBuffers[i], null, buffers);
                }

                return;
            }
        }
    }

    private void ClearBlockCache(Dictionary<long, RfPipelineBlock> cache)
    {
        foreach (RfPipelineBlock block in cache.Values)
        {
            _pipeline.ReleaseStreamBlock(block);
        }

        cache.Clear();
    }

    private void RemoveAndReleaseBlock(
        Dictionary<long, RfPipelineBlock> cache,
        long blockNumber,
        ICollection<RfPipelineBlock>? deferredReleases = null)
    {
        if (cache.Remove(blockNumber, out RfPipelineBlock? block))
        {
            if (deferredReleases is null)
            {
                _pipeline.ReleaseStreamBlock(block);
            }
            else
            {
                deferredReleases.Add(block);
            }
        }
    }

    private void ReleaseDeferredBlocks(List<RfPipelineBlock> deferredReleases)
    {
        foreach (RfPipelineBlock block in deferredReleases)
        {
            _pipeline.ReleaseStreamBlock(block);
        }

        deferredReleases.Clear();
    }

    private void PrepareDecodedBlockCache(Stream stream, long firstBlock)
    {
        if (_pipeline.RequiresSequentialBlockDecode)
        {
            return;
        }

        bool streamChanged = !ReferenceEquals(_decodedBlockCacheStream, stream);
        bool resetCache = streamChanged
            || (_lastReadFirstBlock.HasValue && firstBlock < _lastReadFirstBlock.Value);
        if (resetCache)
        {
            FinishPrefetch(cancel: true, suppressFailures: true);
            ClearBlockCache(_decodedBlockCache);
            ClearBlockCache(_prefetchedBlockCache);
            if (streamChanged)
            {
                _laserDiscCompatibilityMtfByBlock.Clear();
            }
        }
        else
        {
            PrefetchOperation? operation = _prefetchOperation;
            if (operation?.ProducerTask.IsCompleted == true)
            {
                FinishPrefetch(cancel: false, suppressFailures: false);
            }

            if (_decodedBlockCache.Count > 0)
            {
                foreach (long staleBlock in _decodedBlockCache.Keys.Where(block => block < firstBlock).ToArray())
                {
                    RemoveAndReleaseBlock(_decodedBlockCache, staleBlock);
                }
            }

            if (_prefetchedBlockCache.Count > 0)
            {
                foreach (long staleBlock in _prefetchedBlockCache.Keys.Where(block => block < firstBlock).ToArray())
                {
                    RemoveAndReleaseBlock(_prefetchedBlockCache, staleBlock);
                }
            }
        }

        _decodedBlockCacheStream = stream;
        _lastReadFirstBlock = firstBlock;
    }

    private void PrepareLaserDiscCompatibilityMtf(long firstBlock, long lastBlock)
    {
        if (_activeLaserDiscCompatibilityMtf is null)
        {
            return;
        }

        long oldestRetainedBlock = Math.Max(
            0L,
            firstBlock - LaserDiscCompatibilityCacheBlocks);
        foreach (long staleBlock in _laserDiscCompatibilityMtfByBlock.Keys
                     .Where(block => block < oldestRetainedBlock)
                     .ToArray())
        {
            _laserDiscCompatibilityMtfByBlock.Remove(staleBlock);
        }

        for (long block = firstBlock; block <= lastBlock; block++)
        {
            _laserDiscCompatibilityMtfByBlock.TryAdd(
                block,
                _activeLaserDiscCompatibilityMtf);
        }
    }

    private Complex[]? LaserDiscCompatibilityMtfForBlock(long block)
    {
        if (_activeLaserDiscCompatibilityMtf is null)
        {
            return null;
        }

        if (_laserDiscCompatibilityMtfByBlock.TryGetValue(block, out Complex[]? response))
        {
            return response;
        }

        _laserDiscCompatibilityMtfByBlock.Add(block, _activeLaserDiscCompatibilityMtf);
        return _activeLaserDiscCompatibilityMtf;
    }

    private void ScheduleLaserDiscCompatibilityPrefetch(long lastBlock)
    {
        if (_activeLaserDiscCompatibilityMtf is null
            || _laserDiscCompatibilityPrefetchBlocks == 0)
        {
            return;
        }

        // DemodCache v0.4.0 starts its four-field speculative range at the
        // terminal required block. That overlapping block is already cached;
        // later blocks keep the MTF response captured when first queued.
        long firstPrefetchedBlock = lastBlock;
        for (int i = 0; i < _laserDiscCompatibilityPrefetchBlocks; i++)
        {
            long block = checked(firstPrefetchedBlock + i);
            _laserDiscCompatibilityMtfByBlock.TryAdd(
                block,
                _activeLaserDiscCompatibilityMtf);
        }
    }

    private void CacheDecodedBlock(
        long block,
        RfPipelineBlock decoded,
        ICollection<RfPipelineBlock>? deferredReleases = null)
    {
        if (_decodedBlockCache.TryGetValue(block, out RfPipelineBlock? previous)
            && !ReferenceEquals(previous, decoded))
        {
            if (deferredReleases is null)
            {
                _pipeline.ReleaseStreamBlock(previous);
            }
            else
            {
                deferredReleases.Add(previous);
            }
        }

        _decodedBlockCache[block] = decoded;
        while (_decodedBlockCache.Count > _decodedBlockCacheCapacity)
        {
            RemoveAndReleaseBlock(
                _decodedBlockCache,
                _decodedBlockCache.Keys.Min(),
                deferredReleases);
        }
    }

    private bool TryTakeDecodedBlock(
        long block,
        out RfPipelineBlock decoded,
        ICollection<RfPipelineBlock>? deferredReleases = null)
    {
        if (_decodedBlockCache.TryGetValue(block, out RfPipelineBlock? cached))
        {
            decoded = cached;
            return true;
        }

        if (_prefetchedBlockCache.Remove(block, out cached))
        {
            decoded = cached;
            ReportDeferredDiagnosticsOrRelease(decoded);
            CacheDecodedBlock(block, decoded, deferredReleases);
            return true;
        }

        PrefetchOperation? operation = _prefetchOperation;
        PrefetchSlot? slot = operation?.Find(block);
        if (slot is not null && !slot.Harvested)
        {
            RfPipelineBlock? prefetched;
            try
            {
                prefetched = slot.Completion.Task.GetAwaiter().GetResult();
            }
            catch
            {
                FinishPrefetch(cancel: true, suppressFailures: true);
                throw;
            }

            slot.Harvested = true;
            if (prefetched is not null)
            {
                decoded = prefetched;
                ReportDeferredDiagnosticsOrRelease(decoded);
                CacheDecodedBlock(block, decoded, deferredReleases);
                return true;
            }

            FinishPrefetch(cancel: false, suppressFailures: false);
        }

        decoded = null!;
        return false;
    }

    private void ReportDeferredDiagnosticsOrRelease(RfPipelineBlock decoded)
    {
        try
        {
            _pipeline.ReportDeferredDiagnostics(decoded);
        }
        catch
        {
            _pipeline.ReleaseStreamBlock(decoded);
            throw;
        }
    }

    private void StartPrefetch(Stream stream, long lastBlock)
    {
        if (PrefetchBlocks == 0 || lastBlock == long.MaxValue)
        {
            return;
        }

        if (_prefetchOperation is { } pending)
        {
            if (!pending.ProducerTask.IsCompleted)
            {
                return;
            }

            FinishPrefetch(cancel: false, suppressFailures: false);
        }

        int availableSlots = PrefetchBlocks - _prefetchedBlockCache.Count;
        if (availableSlots <= 0)
        {
            return;
        }

        var blockNumbers = new long[availableSlots];
        int blockCount = 0;
        long candidate = lastBlock + 1;
        while (blockCount < availableSlots)
        {
            if (!_decodedBlockCache.ContainsKey(candidate)
                && !_prefetchedBlockCache.ContainsKey(candidate))
            {
                blockNumbers[blockCount] = candidate;
                blockCount++;
            }

            if (candidate == long.MaxValue)
            {
                break;
            }

            candidate++;
        }

        if (blockCount == 0)
        {
            return;
        }

        if (blockCount != availableSlots)
        {
            Array.Resize(ref blockNumbers, blockCount);
        }

        var operation = new PrefetchOperation(
            stream,
            blockNumbers,
            Math.Min(PrefetchWorkerThreads, blockNumbers.Length));
        _prefetchOperation = operation;
        operation.ProducerTask = Task.Run(() => ProducePrefetchedBlocks(operation));
    }

    private void ProducePrefetchedBlocks(PrefetchOperation operation)
    {
        CancellationToken cancellationToken = operation.Cancellation.Token;
        var workers = new List<Task>(operation.Slots.Length);
        bool inputUnavailable = false;
        Exception? terminalFailure = null;
        try
        {
            foreach (PrefetchSlot slot in operation.Slots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation.WorkerSlots.Wait(cancellationToken);

                double[]? preparedInput;
                try
                {
                    long sample = checked(slot.Block * BlockStride);
                    preparedInput = _pipeline.LoadStreamBlockInput(
                        operation.Stream,
                        sample,
                        BlockLength,
                        parallelDecode: true);
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException and not AccessViolationException)
                {
                    operation.WorkerSlots.Release();
                    inputUnavailable = true;
                    break;
                }

                if (preparedInput is null)
                {
                    operation.WorkerSlots.Release();
                    inputUnavailable = true;
                    break;
                }

                double[] workerInput = preparedInput;
                Task workerTask;
                try
                {
                    workerTask = Task.Run(() =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            RfPipelineBlock decoded = _pipeline.DecodePreparedStreamBlock(
                                workerInput,
                                reportDiagnostics: false);
                            slot.Completion.TrySetResult(decoded);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            slot.Completion.TrySetCanceled(cancellationToken);
                        }
                        catch (Exception exception)
                        {
                            slot.Completion.TrySetException(exception);
                            throw;
                        }
                        finally
                        {
                            _pipeline.ReturnStreamBlockInput(workerInput, parallelDecode: true);
                            operation.WorkerSlots.Release();
                        }
                    });
                }
                catch
                {
                    _pipeline.ReturnStreamBlockInput(workerInput, parallelDecode: true);
                    operation.WorkerSlots.Release();
                    throw;
                }

                workers.Add(workerTask);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                try
                {
                    Task.WhenAll(workers).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    terminalFailure ??= exception;
                    throw;
                }
            }
            finally
            {
                foreach (PrefetchSlot slot in operation.Slots)
                {
                    if (!slot.Completion.Task.IsCompleted)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            slot.Completion.TrySetCanceled(cancellationToken);
                        }
                        else if (terminalFailure is not null)
                        {
                            slot.Completion.TrySetException(terminalFailure);
                        }
                        else if (inputUnavailable)
                        {
                            slot.Completion.TrySetResult(null);
                        }
                        else
                        {
                            slot.Completion.TrySetException(new InvalidOperationException(
                                "RF prefetch ended before the block reached a terminal state."));
                        }
                    }
                }
            }
        }
    }

    private void StopPrefetchBeforeDirectRead()
    {
        if (_prefetchOperation is not null)
        {
            // A speculative block must not fail the direct read that replaces it.
            FinishPrefetch(cancel: true, suppressFailures: true);
        }
    }

    private void FinishPrefetch(bool cancel, bool suppressFailures)
    {
        PrefetchOperation? operation = _prefetchOperation;
        if (operation is null)
        {
            return;
        }

        _prefetchOperation = null;
        if (cancel)
        {
            operation.Cancellation.Cancel();
            Interlocked.Increment(ref _prefetchCancellationCount);
        }

        try
        {
            operation.ProducerTask.GetAwaiter().GetResult();
            if (!cancel)
            {
                foreach (PrefetchSlot slot in operation.Slots)
                {
                    if (slot.Harvested
                        || !slot.Completion.Task.IsCompletedSuccessfully
                        || slot.Completion.Task.Result is not { } decoded)
                    {
                        continue;
                    }

                    slot.Harvested = true;
                    if (!_decodedBlockCache.ContainsKey(slot.Block))
                    {
                        if (_prefetchedBlockCache.TryGetValue(
                                slot.Block,
                                out RfPipelineBlock? existing))
                        {
                            if (!ReferenceEquals(existing, decoded))
                            {
                                _pipeline.ReleaseStreamBlock(decoded);
                            }
                        }
                        else
                        {
                            _prefetchedBlockCache[slot.Block] = decoded;
                        }
                    }
                    else
                    {
                        _pipeline.ReleaseStreamBlock(decoded);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancel || suppressFailures)
        {
        }
        catch (Exception exception) when (
            suppressFailures && exception is not OutOfMemoryException and not AccessViolationException)
        {
        }
        finally
        {
            foreach (PrefetchSlot slot in operation.Slots)
            {
                if (!slot.Harvested
                    && slot.Completion.Task.IsCompletedSuccessfully
                    && slot.Completion.Task.Result is { } decoded)
                {
                    slot.Harvested = true;
                    _pipeline.ReleaseStreamBlock(decoded);
                }
            }

            operation.Dispose();
        }
    }

    private void CopyTrimmedWindow(
        double[] source,
        double[] destination,
        int blockDestinationOffset,
        int windowOffset,
        int? sourceOffset = null)
    {
        if (source.Length != BlockLength)
        {
            throw new ArgumentException("Decoded block length did not match the configured block length.", nameof(source));
        }

        int actualSourceOffset = sourceOffset ?? BlockCut;
        if (actualSourceOffset < 0 || actualSourceOffset + BlockStride > source.Length)
        {
            throw new InvalidOperationException("RF high-pass delay exceeds the overlap-save block cuts.");
        }

        int copyStart = Math.Max(blockDestinationOffset, windowOffset);
        int copyEnd = Math.Min(
            checked(blockDestinationOffset + BlockStride),
            checked(windowOffset + destination.Length));
        if (copyStart >= copyEnd)
        {
            return;
        }

        Array.Copy(
            source,
            actualSourceOffset + (copyStart - blockDestinationOffset),
            destination,
            copyStart - windowOffset,
            copyEnd - copyStart);
    }

    private unsafe void CopyTrimmedWindow(
        float[] source,
        double[] destination,
        int blockDestinationOffset,
        int windowOffset)
    {
        if (source.Length != BlockLength)
        {
            throw new ArgumentException("Decoded block length did not match the configured block length.", nameof(source));
        }

        int copyStart = Math.Max(blockDestinationOffset, windowOffset);
        int copyEnd = Math.Min(
            checked(blockDestinationOffset + BlockStride),
            checked(windowOffset + destination.Length));
        if (copyStart >= copyEnd)
        {
            return;
        }

        int sourceStart = BlockCut + (copyStart - blockDestinationOffset);
        int destinationStart = copyStart - windowOffset;
        int count = copyEnd - copyStart;
        int index = 0;
        if (Avx.IsSupported)
        {
            fixed (float* sourcePointer = source)
            fixed (double* destinationPointer = destination)
            {
                int vectorizedEnd = count - (count % 4);
                for (; index < vectorizedEnd; index += 4)
                {
                    Avx.Store(
                        destinationPointer + destinationStart + index,
                        Avx.ConvertToVector256Double(
                            Sse.LoadVector128(sourcePointer + sourceStart + index)));
                }
            }
        }

        for (; index < count; index++)
        {
            destination[destinationStart + index] = source[sourceStart + index];
        }
    }

    private void CopyTrimmedWindow(
        short[] source,
        short[] destination,
        int blockDestinationOffset,
        int windowOffset)
    {
        if (source.Length != BlockLength)
        {
            throw new ArgumentException("Decoded block length did not match the configured block length.", nameof(source));
        }

        int copyStart = Math.Max(blockDestinationOffset, windowOffset);
        int copyEnd = Math.Min(
            checked(blockDestinationOffset + BlockStride),
            checked(windowOffset + destination.Length));
        if (copyStart >= copyEnd)
        {
            return;
        }

        Array.Copy(
            source,
            BlockCut + (copyStart - blockDestinationOffset),
            destination,
            copyStart - windowOffset,
            copyEnd - copyStart);
    }

    private void CopyTrimmed(
        LaserDiscAnalogAudioBlock source,
        double[] leftDestination,
        double[] rightDestination,
        int destinationOffset)
    {
        if (source.Left.Length != source.Right.Length)
        {
            throw new ArgumentException("LD analog audio channel lengths did not match.", nameof(source));
        }

        int expectedLength = BlockLength / source.DecimationFactor;
        if (source.Left.Length != expectedLength)
        {
            throw new ArgumentException("LD analog audio block length did not match the configured block length.", nameof(source));
        }

        int audioBlockCut = BlockCut / source.DecimationFactor;
        int audioStride = AudioBlockStride(source.DecimationFactor);
        Array.Copy(source.Left, audioBlockCut, leftDestination, destinationOffset, audioStride);
        Array.Copy(source.Right, audioBlockCut, rightDestination, destinationOffset, audioStride);
    }

    private int AudioBlockStride(int decimationFactor)
    {
        if (decimationFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decimationFactor));
        }

        return BlockStride / decimationFactor;
    }

    private static double[] Slice(double[] source, int offset, int length)
    {
        if (offset == 0 && length == source.Length)
        {
            return source;
        }

        var output = new double[length];
        Array.Copy(source, offset, output, 0, length);
        return output;
    }

    internal sealed class RfDecodedSpanLease : IDisposable
    {
        private RfBlockStreamDecoder? _owner;
        private ReusableSpanBuffers? _buffers;
        private VhsPayloadMaterializer? _materializer;

        internal RfDecodedSpanLease(
            RfBlockStreamDecoder? owner,
            ReusableSpanBuffers? buffers,
            RfDecodedSpan span,
            VhsPayloadMaterializer? materializer = null)
        {
            _owner = owner;
            _buffers = buffers;
            _materializer = materializer;
            Span = span;
        }

        internal RfDecodedSpan Span { get; }

        public void Dispose()
        {
            RfBlockStreamDecoder? owner = Interlocked.Exchange(ref _owner, null);
            ReusableSpanBuffers? buffers = Interlocked.Exchange(ref _buffers, null);
            VhsPayloadMaterializer? materializer = Interlocked.Exchange(ref _materializer, null);
            materializer?.Dispose();
            if (owner is not null && buffers is not null)
            {
                owner.ReturnReusableSpanBuffers(buffers);
            }
        }
    }

    internal sealed class VhsPayloadMaterializer : IDisposable
    {
        // Lower worker counts favor one contiguous copy; segmented scans pay off at high concurrency.
        internal const int MinimumSegmentedEnvelopeWorkerThreads = 20;
        private const int PairwiseBlockSize = 128;
        private readonly object _gate = new();
        private readonly RfBlockStreamDecoder _owner;
        private readonly RfPipelineBlock[] _blocks;
        private readonly RfPipelineBlock[] _deferredBlocks;
        private readonly double[] _video;
        private readonly double[] _envelope;
        private readonly double[]? _chroma;
        private readonly int _windowOffset;
        private readonly int _workerThreads;
        private readonly bool _useSegmentedEnvelope;
        private Task? _payloadMaterialization;
        private Task? _envelopeMaterialization;
        private bool _disposed;

        internal VhsPayloadMaterializer(
            RfBlockStreamDecoder owner,
            RfPipelineBlock[] blocks,
            RfPipelineBlock[] deferredBlocks,
            double[] video,
            double[] envelope,
            double[]? chroma,
            int windowOffset,
            int workerThreads)
        {
            _owner = owner;
            _blocks = blocks;
            _deferredBlocks = deferredBlocks;
            _video = video;
            _envelope = envelope;
            _chroma = chroma;
            _windowOffset = windowOffset;
            _workerThreads = workerThreads;
            _useSegmentedEnvelope = workerThreads >= MinimumSegmentedEnvelopeWorkerThreads;
        }

        internal bool UsesSegmentedEnvelope => _useSegmentedEnvelope;

        internal bool MaterializationStarted
        {
            get
            {
                lock (_gate)
                {
                    return _payloadMaterialization is not null
                        || _envelopeMaterialization is not null;
                }
            }
        }

        internal bool EnvelopeMaterializationStarted
        {
            get
            {
                lock (_gate)
                {
                    return _useSegmentedEnvelope
                        ? _envelopeMaterialization is not null
                        : _payloadMaterialization is not null;
                }
            }
        }

        internal Task BeginMaterialization()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _payloadMaterialization ??= Task.Run(MaterializePayload);
            }
        }

        internal void EnsurePayloadMaterialized()
            => BeginMaterialization().GetAwaiter().GetResult();

        internal void EnsureMaterialized()
        {
            Task payloadMaterialization;
            Task? envelopeMaterialization;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                payloadMaterialization = _payloadMaterialization ??= Task.Run(MaterializePayload);
                envelopeMaterialization = _useSegmentedEnvelope
                    ? _envelopeMaterialization ??= Task.Run(MaterializeEnvelope)
                    : null;
            }

            payloadMaterialization.GetAwaiter().GetResult();
            envelopeMaterialization?.GetAwaiter().GetResult();
        }

        internal float MeanEnvelopeFloat32()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _envelope.Length == 0
                    ? float.NaN
                    : _useSegmentedEnvelope
                        ? PairwiseEnvelopeSumFloat32(0, _envelope.Length) / _envelope.Length
                        : NumpyReduction.MeanFloat32(_envelope);
            }
        }

        internal IReadOnlyList<RfDropoutRange> FindEnvelopeDropouts(
            int start,
            int end,
            double threshold,
            double hysteresis,
            int mergeThreshold = RfDropoutDetector.DefaultMergeThreshold,
            int minimumLength = RfDropoutDetector.DefaultMinimumLength)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (start < 0 || end < start || end > _envelope.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(start));
                }

                if (threshold < 0 || hysteresis <= 0 || mergeThreshold < 0 || minimumLength < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(threshold));
                }

                if (!_useSegmentedEnvelope)
                {
                    return RfDropoutDetector.FindDropouts(
                        _envelope,
                        start,
                        end,
                        threshold,
                        hysteresis,
                        mergeThreshold,
                        minimumLength);
                }

                double downThreshold = threshold;
                double upThreshold = threshold * hysteresis;
                var rawRanges = new List<(int Start, int End)>();
                int dropoutIndex = -1;
                int index = start;
                while (index < end)
                {
                    GetEnvelopeSegment(index, end, out double[] source, out int sourceStart, out int count);
                    for (int sourceIndex = 0; sourceIndex < count; sourceIndex++)
                    {
                        int logicalIndex = index + sourceIndex;
                        double value = source[sourceStart + sourceIndex];
                        if (value <= downThreshold)
                        {
                            bool dropoutEnded = dropoutIndex >= 0
                                && rawRanges[dropoutIndex].End != -1
                                && logicalIndex - rawRanges[dropoutIndex].End > mergeThreshold;
                            if (dropoutIndex == -1 || dropoutEnded)
                            {
                                dropoutIndex++;
                                rawRanges.Add((logicalIndex, -1));
                            }
                        }
                        else if (value >= upThreshold
                            && dropoutIndex != -1
                            && rawRanges[dropoutIndex].End == -1)
                        {
                            rawRanges[dropoutIndex] = (rawRanges[dropoutIndex].Start, logicalIndex);
                        }
                    }

                    index += count;
                }

                if (dropoutIndex != -1 && rawRanges[dropoutIndex].End == -1)
                {
                    rawRanges[dropoutIndex] = (rawRanges[dropoutIndex].Start, end);
                }

                return rawRanges
                    .Where(range => range.End - range.Start > minimumLength)
                    .Select(range => new RfDropoutRange(range.Start, range.End))
                    .ToArray();
            }
        }

        public void Dispose()
        {
            Task? payloadMaterialization;
            Task? envelopeMaterialization;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                payloadMaterialization = _payloadMaterialization;
                envelopeMaterialization = _envelopeMaterialization;
            }

            ObserveTaskFailure(payloadMaterialization);
            ObserveTaskFailure(envelopeMaterialization);

            foreach (RfPipelineBlock block in _deferredBlocks)
            {
                _owner._pipeline.ReleaseStreamBlock(block);
            }

            _owner.ReleaseActiveVhsPayload(this);
        }

        private static void ObserveTaskFailure(Task? materialization)
        {
            if (materialization is null)
            {
                return;
            }

            try
            {
                materialization.GetAwaiter().GetResult();
            }
            catch
            {
                // The caller that started materialization observes its failure.
            }
        }

        private void MaterializePayload()
        {
            Parallel.For(
                0,
                _blocks.Length,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(_workerThreads, _blocks.Length)
                },
                blockIndex =>
                {
                    int blockDestination = checked(blockIndex * _owner.BlockStride);
                    RfDemodulatedBlock demodulated = _blocks[blockIndex].Demodulated;
                    ValidateEnvelopeLength(demodulated.Envelope);
                    _owner.CopyTrimmedWindow(
                        demodulated.Video,
                        _video,
                        blockDestination,
                        _windowOffset);
                    if (!_useSegmentedEnvelope)
                    {
                        _owner.CopyTrimmedWindow(
                            demodulated.Envelope,
                            _envelope,
                            blockDestination,
                            _windowOffset);
                    }
                    if (_chroma is not null && demodulated.Chroma is { } blockChroma)
                    {
                        _owner.CopyTrimmedWindow(
                            blockChroma,
                            _chroma,
                            blockDestination,
                            _windowOffset);
                    }
                    else if (_chroma is not null
                        && demodulated.ChromaFloat32 is { } blockChromaFloat32)
                    {
                        _owner.CopyTrimmedWindow(
                            blockChromaFloat32,
                            _chroma,
                            blockDestination,
                            _windowOffset);
                    }
                });
        }

        private void MaterializeEnvelope()
        {
            Parallel.For(
                0,
                _blocks.Length,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(_workerThreads, _blocks.Length)
                },
                blockIndex =>
                {
                    int blockDestination = checked(blockIndex * _owner.BlockStride);
                    _owner.CopyTrimmedWindow(
                        _blocks[blockIndex].Demodulated.Envelope,
                        _envelope,
                        blockDestination,
                        _windowOffset);
                });
        }

        private float PairwiseEnvelopeSumFloat32(int start, int length)
        {
            if (length > PairwiseBlockSize)
            {
                int split = length / 2;
                split -= split % 8;
                return PairwiseEnvelopeSumFloat32(start, split)
                    + PairwiseEnvelopeSumFloat32(start + split, length - split);
            }

            if (TryGetContiguousEnvelope(start, length, out double[] source, out int sourceStart))
            {
                return NumpyReduction.SumFloat32(source.AsSpan(sourceStart, length));
            }

            Span<double> scratch = stackalloc double[length];
            CopyEnvelopeSamples(start, scratch);
            return NumpyReduction.SumFloat32(scratch);
        }

        private bool TryGetContiguousEnvelope(
            int logicalStart,
            int length,
            out double[] source,
            out int sourceStart)
        {
            int absolute = checked(_windowOffset + logicalStart);
            int blockIndex = absolute / _owner.BlockStride;
            int blockOffset = absolute - (blockIndex * _owner.BlockStride);
            source = _blocks[blockIndex].Demodulated.Envelope;
            ValidateEnvelopeLength(source);
            sourceStart = _owner.BlockCut + blockOffset;
            return blockOffset + length <= _owner.BlockStride;
        }

        private void CopyEnvelopeSamples(int logicalStart, Span<double> destination)
        {
            int destinationIndex = 0;
            while (destinationIndex < destination.Length)
            {
                int logicalIndex = logicalStart + destinationIndex;
                GetEnvelopeSegment(
                    logicalIndex,
                    logicalStart + destination.Length,
                    out double[] source,
                    out int sourceStart,
                    out int count);
                source.AsSpan(sourceStart, count).CopyTo(destination[destinationIndex..]);
                destinationIndex += count;
            }
        }

        private void GetEnvelopeSegment(
            int logicalStart,
            int logicalEnd,
            out double[] source,
            out int sourceStart,
            out int count)
        {
            int absolute = checked(_windowOffset + logicalStart);
            int blockIndex = absolute / _owner.BlockStride;
            int blockOffset = absolute - (blockIndex * _owner.BlockStride);
            source = _blocks[blockIndex].Demodulated.Envelope;
            ValidateEnvelopeLength(source);
            sourceStart = _owner.BlockCut + blockOffset;
            count = Math.Min(logicalEnd - logicalStart, _owner.BlockStride - blockOffset);
        }

        private void ValidateEnvelopeLength(double[] source)
        {
            if (source.Length != _owner.BlockLength)
            {
                throw new ArgumentException(
                    "Decoded block length did not match the configured block length.",
                    nameof(source));
            }
        }
    }

    internal sealed class ReusableSpanBuffers
    {
        private double[]? _chroma;
        private short[]? _efm;
        private double[]? _audioLeft;
        private double[]? _audioRight;
        private double[]? _videoBurst;
        private double[]? _videoPilot;

        internal ReusableSpanBuffers(int length, bool retainRfDiagnosticChannels)
        {
            Length = length;
            Input = retainRfDiagnosticChannels ? new double[length] : [];
            Video = new double[length];
            DemodRaw = retainRfDiagnosticChannels ? new double[length] : [];
            Envelope = new double[length];
            VideoLowPass = new double[length];
            RfHighPass = retainRfDiagnosticChannels ? new double[length] : [];
        }

        internal int Length { get; }

        internal double[] Input { get; }

        internal double[] Video { get; }

        internal double[] DemodRaw { get; }

        internal double[] Envelope { get; }

        internal double[] VideoLowPass { get; }

        internal double[] RfHighPass { get; }

        internal double[] GetChroma() => _chroma ??= new double[Length];

        internal short[] GetEfm() => _efm ??= new short[Length];

        internal double[] GetVideoBurst() => _videoBurst ??= new double[Length];

        internal double[] GetVideoPilot() => _videoPilot ??= new double[Length];

        internal (double[] Left, double[] Right) GetAudio(int length)
        {
            if (_audioLeft?.Length != length || _audioRight?.Length != length)
            {
                _audioLeft = new double[length];
                _audioRight = new double[length];
            }

            return (_audioLeft!, _audioRight!);
        }
    }

    private sealed class PrefetchOperation : IDisposable
    {
        internal PrefetchOperation(Stream stream, long[] blockNumbers, int workerCount)
        {
            Stream = stream;
            Slots = blockNumbers.Select(block => new PrefetchSlot(block)).ToArray();
            WorkerSlots = new SemaphoreSlim(workerCount, workerCount);
        }

        internal Stream Stream { get; }

        internal PrefetchSlot[] Slots { get; }

        internal SemaphoreSlim WorkerSlots { get; }

        internal CancellationTokenSource Cancellation { get; } = new();

        internal Task ProducerTask { get; set; } = Task.CompletedTask;

        internal PrefetchSlot? Find(long block)
        {
            foreach (PrefetchSlot slot in Slots)
            {
                if (slot.Block == block)
                {
                    return slot;
                }
            }

            return null;
        }

        public void Dispose()
        {
            foreach (PrefetchSlot slot in Slots)
            {
                if (slot.Completion.Task.IsFaulted)
                {
                    _ = slot.Completion.Task.Exception;
                }
            }

            WorkerSlots.Dispose();
            Cancellation.Dispose();
        }
    }

    private sealed class PrefetchSlot(long block)
    {
        internal long Block { get; } = block;

        internal TaskCompletionSource<RfPipelineBlock?> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Harvested { get; set; }
    }
}
