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
    double[]? VideoPilot = null);

public sealed class RfBlockStreamDecoder : IDisposable
{
    private const int DecodedBlockCacheCapacity = 16;
    private const int ReusableSpanBufferSetCapacity = 2;
    internal const int MaximumConcurrentPrefetchBlocks = 8;
    internal const int MaximumPrefetchBlocks = 32;
    private readonly RfBlockDecodePipeline _pipeline;
    private readonly Dictionary<long, RfPipelineBlock> _decodedBlockCache = [];
    private readonly Dictionary<long, RfPipelineBlock> _prefetchedBlockCache = [];
    private readonly Dictionary<long, RfPipelineBlock> _sequentialBlockCache = [];
    private readonly int _decodedBlockCacheCapacity;
    private Stream? _decodedBlockCacheStream;
    private long? _lastReadFirstBlock;
    private long? _lastSequentialDecodedBlock;
    private Task<PrefetchedBlockBatch>? _prefetchTask;
    private CancellationTokenSource? _prefetchCancellation;
    private readonly ReusableSpanBuffers?[] _reusableSpanBuffers = new ReusableSpanBuffers?[ReusableSpanBufferSetCapacity];
    private bool _disposed;

    public RfBlockStreamDecoder(
        RfBlockDecodePipeline pipeline,
        int blockLength,
        int blockCut,
        int blockCutEnd,
        int workerThreads = 1,
        int prefetchBlocks = 0)
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
    }

    public int BlockLength { get; }

    public int BlockCut { get; }

    public int BlockCutEnd { get; }

    public int BlockStride { get; }

    public int WorkerThreads { get; }

    public int PrefetchBlocks { get; }

    internal int PrefetchWorkerThreads { get; }

    internal int CachedDecodedBlockCount => _decodedBlockCache.Count;

    internal int CachedPrefetchedBlockCount => _prefetchedBlockCache.Count;

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
            + Math.Min(effectiveWorkers, MaximumConcurrentPrefetchBlocks);
        return (int)Math.Min(oneAdditionalWave, MaximumPrefetchBlocks);
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
        _ = TakePendingPrefetch(cancel: true);
        _decodedBlockCache.Clear();
        _prefetchedBlockCache.Clear();
        _sequentialBlockCache.Clear();
        _decodedBlockCacheStream = null;
        _lastReadFirstBlock = null;
        _lastSequentialDecodedBlock = null;
    }

    public RfDecodedSpan? Read(Stream stream, long begin, int length)
        => ReadCore(stream, begin, length, reusableBuffers: null);

    internal RfDecodedSpanLease? ReadLeased(Stream stream, long begin, int length)
    {
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

        ReusableSpanBuffers buffers = TakeReusableSpanBuffers(length);
        try
        {
            RfDecodedSpan? span = ReadCore(stream, begin, length, buffers);
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

    private RfDecodedSpan? ReadCore(
        Stream stream,
        long begin,
        int length,
        ReusableSpanBuffers? reusableBuffers)
    {
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
        int totalDecoded = checked((int)((lastBlock - firstBlock + 1) * BlockStride));
        int offset = checked((int)(begin - (firstBlock * BlockStride)));
        double[] input = reusableBuffers?.Input ?? new double[length];
        double[] video = reusableBuffers?.Video ?? new double[length];
        double[] demodRaw = reusableBuffers?.DemodRaw ?? new double[length];
        double[] envelope = reusableBuffers?.Envelope ?? new double[length];
        double[] videoLowPass = reusableBuffers?.VideoLowPass ?? new double[length];
        double[] rfHighPass = reusableBuffers?.RfHighPass ?? new double[length];
        double[]? chroma = null;
        short[]? efm = null;
        double[]? audioLeft = null;
        double[]? audioRight = null;
        double[]? videoBurst = null;
        double[]? videoPilot = null;
        int audioDecimationFactor = 0;
        int audioDestination = 0;
        int destination = 0;

        void AppendBlock(RfPipelineBlock pipelineBlock)
        {
            CopyTrimmedWindow(pipelineBlock.Input, input, destination, offset);
            CopyTrimmedWindow(pipelineBlock.Demodulated.Video, video, destination, offset);
            CopyTrimmedWindow(pipelineBlock.Demodulated.DemodRaw, demodRaw, destination, offset);
            CopyTrimmedWindow(pipelineBlock.Demodulated.Envelope, envelope, destination, offset);
            CopyTrimmedWindow(pipelineBlock.Demodulated.VideoLowPass, videoLowPass, destination, offset);
            CopyTrimmedWindow(
                pipelineBlock.Demodulated.RfHighPass,
                rfHighPass,
                destination,
                offset,
                BlockCut - _pipeline.RfHighPassOffset);
            if (pipelineBlock.Demodulated.Chroma is not null)
            {
                chroma ??= reusableBuffers?.GetChroma() ?? new double[length];
                CopyTrimmedWindow(pipelineBlock.Demodulated.Chroma, chroma, destination, offset);
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
                LaserDiscAnalogAudioBlock audio = pipelineBlock.Demodulated.AnalogAudio;
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

            destination += BlockStride;
        }

        if (WorkerThreads <= 1 || firstBlock == lastBlock || _pipeline.RequiresSequentialBlockDecode)
        {
            if (_pipeline.RequiresSequentialBlockDecode && _sequentialBlockCache.Count > 0)
            {
                foreach (long staleBlock in _sequentialBlockCache.Keys.Where(block => block < firstBlock).ToArray())
                {
                    _sequentialBlockCache.Remove(staleBlock);
                }
            }

            for (long block = firstBlock; block <= lastBlock; block++)
            {
                if (_pipeline.RequiresSequentialBlockDecode
                    && _lastSequentialDecodedBlock is { } lastDecoded
                    && block > lastDecoded + 1)
                {
                    for (long warmBlock = lastDecoded + 1; warmBlock < block; warmBlock++)
                    {
                        long warmSample = checked(warmBlock * BlockStride);
                        RfPipelineBlock? warmed = _pipeline.DecodeBlockWithInput(stream, warmSample, BlockLength);
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
                    && TryTakeDecodedBlock(block, out cachedBlock))
                {
                    pipelineBlock = cachedBlock;
                }
                else
                {
                    long sample = checked(block * BlockStride);
                    pipelineBlock = _pipeline.DecodeBlockWithInput(stream, sample, BlockLength);
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
                        CacheDecodedBlock(block, pipelineBlock);
                    }
                }

                if (pipelineBlock is null)
                {
                    return null;
                }

                AppendBlock(pipelineBlock);
            }
        }
        else
        {
            int blockCount = checked((int)(lastBlock - firstBlock + 1));
            var preparedInputs = new double[blockCount][];
            var missingBlocks = new int[blockCount];
            int missingBlockCount = 0;
            var decodedBlocks = new RfPipelineBlock[blockCount];
            for (int i = 0; i < blockCount; i++)
            {
                long block = firstBlock + i;
                if (TryTakeDecodedBlock(block, out RfPipelineBlock cachedBlock))
                {
                    decodedBlocks[i] = cachedBlock;
                    continue;
                }

                long sample = checked((firstBlock + i) * BlockStride);
                double[]? preparedInput = _pipeline.LoadBlockInput(stream, sample, BlockLength);
                if (preparedInput is null)
                {
                    return null;
                }

                preparedInputs[i] = preparedInput;
                missingBlocks[missingBlockCount++] = i;
            }

            Parallel.For(
                0,
                missingBlockCount,
                new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads },
                missingIndex =>
                {
                    int blockIndex = missingBlocks[missingIndex];
                    decodedBlocks[blockIndex] = _pipeline.DecodePreparedBlock(preparedInputs[blockIndex]);
                });
            for (int i = 0; i < missingBlockCount; i++)
            {
                int blockIndex = missingBlocks[i];
                CacheDecodedBlock(firstBlock + blockIndex, decodedBlocks[blockIndex]);
            }

            foreach (RfPipelineBlock pipelineBlock in decodedBlocks)
            {
                AppendBlock(pipelineBlock);
            }
        }

        StartPrefetch(stream, lastBlock);

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
            videoPilot);
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
            _ = TakePendingPrefetch(cancel: true);
        }
        finally
        {
            _decodedBlockCache.Clear();
            _prefetchedBlockCache.Clear();
            _sequentialBlockCache.Clear();
            for (int i = 0; i < _reusableSpanBuffers.Length; i++)
            {
                _ = Interlocked.Exchange(ref _reusableSpanBuffers[i], null);
            }
        }
    }

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

        return new ReusableSpanBuffers(length);
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

    private void PrepareDecodedBlockCache(Stream stream, long firstBlock)
    {
        if (_pipeline.RequiresSequentialBlockDecode)
        {
            return;
        }

        bool resetCache = !ReferenceEquals(_decodedBlockCacheStream, stream)
            || (_lastReadFirstBlock.HasValue && firstBlock < _lastReadFirstBlock.Value);
        PrefetchedBlockBatch? completedPrefetch = TakePendingPrefetch(cancel: resetCache);
        if (resetCache)
        {
            _decodedBlockCache.Clear();
            _prefetchedBlockCache.Clear();
        }
        else
        {
            if (completedPrefetch is not null && ReferenceEquals(completedPrefetch.Stream, stream))
            {
                for (int i = 0; i < completedPrefetch.BlockNumbers.Length; i++)
                {
                    long block = completedPrefetch.BlockNumbers[i];
                    if (block >= firstBlock && !_decodedBlockCache.ContainsKey(block))
                    {
                        _prefetchedBlockCache[block] = completedPrefetch.Blocks[i];
                    }
                }
            }

            if (_decodedBlockCache.Count > 0)
            {
                foreach (long staleBlock in _decodedBlockCache.Keys.Where(block => block < firstBlock).ToArray())
                {
                    _decodedBlockCache.Remove(staleBlock);
                }
            }

            if (_prefetchedBlockCache.Count > 0)
            {
                foreach (long staleBlock in _prefetchedBlockCache.Keys.Where(block => block < firstBlock).ToArray())
                {
                    _prefetchedBlockCache.Remove(staleBlock);
                }
            }
        }

        _decodedBlockCacheStream = stream;
        _lastReadFirstBlock = firstBlock;
    }

    private void CacheDecodedBlock(long block, RfPipelineBlock decoded)
    {
        _decodedBlockCache[block] = decoded;
        while (_decodedBlockCache.Count > _decodedBlockCacheCapacity)
        {
            _decodedBlockCache.Remove(_decodedBlockCache.Keys.Min());
        }
    }

    private bool TryTakeDecodedBlock(long block, out RfPipelineBlock decoded)
    {
        if (_decodedBlockCache.TryGetValue(block, out RfPipelineBlock? cached))
        {
            decoded = cached;
            return true;
        }

        if (_prefetchedBlockCache.Remove(block, out cached))
        {
            decoded = cached;
            _pipeline.ReportDeferredDiagnostics(decoded);
            CacheDecodedBlock(block, decoded);
            return true;
        }

        decoded = null!;
        return false;
    }

    private void StartPrefetch(Stream stream, long lastBlock)
    {
        if (PrefetchBlocks == 0 || _prefetchTask is not null || lastBlock == long.MaxValue)
        {
            return;
        }

        int availableSlots = PrefetchBlocks - _prefetchedBlockCache.Count;
        if (availableSlots <= 0)
        {
            return;
        }

        var blockNumbers = new long[availableSlots];
        var preparedInputs = new double[availableSlots][];
        int preparedCount = 0;
        long candidate = lastBlock + 1;
        while (preparedCount < availableSlots)
        {
            if (!_decodedBlockCache.ContainsKey(candidate)
                && !_prefetchedBlockCache.ContainsKey(candidate))
            {
                double[]? preparedInput;
                try
                {
                    long sample = checked(candidate * BlockStride);
                    preparedInput = _pipeline.LoadBlockInput(stream, sample, BlockLength);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
                {
                    // Speculative I/O must not fail a field that has already decoded successfully.
                    break;
                }

                if (preparedInput is null)
                {
                    break;
                }

                blockNumbers[preparedCount] = candidate;
                preparedInputs[preparedCount] = preparedInput;
                preparedCount++;
            }

            if (candidate == long.MaxValue)
            {
                break;
            }

            candidate++;
        }

        if (preparedCount == 0)
        {
            return;
        }

        if (preparedCount != availableSlots)
        {
            Array.Resize(ref blockNumbers, preparedCount);
            Array.Resize(ref preparedInputs, preparedCount);
        }

        var cancellation = new CancellationTokenSource();
        _prefetchCancellation = cancellation;
        // The stream was read above on the caller thread; background work is compute-only.
        _prefetchTask = Task.Run(
            () => DecodePrefetchedBlocks(stream, blockNumbers, preparedInputs, cancellation.Token),
            cancellation.Token);
    }

    private PrefetchedBlockBatch DecodePrefetchedBlocks(
        Stream stream,
        long[] blockNumbers,
        double[][] preparedInputs,
        CancellationToken cancellationToken)
    {
        var decodedBlocks = new RfPipelineBlock[preparedInputs.Length];
        if (preparedInputs.Length == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decodedBlocks[0] = _pipeline.DecodePreparedBlock(preparedInputs[0], reportDiagnostics: false);
        }
        else
        {
            Parallel.For(
                0,
                preparedInputs.Length,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Min(PrefetchWorkerThreads, preparedInputs.Length)
                },
                index => decodedBlocks[index] = _pipeline.DecodePreparedBlock(
                    preparedInputs[index],
                    reportDiagnostics: false));
        }

        return new PrefetchedBlockBatch(stream, blockNumbers, decodedBlocks);
    }

    private PrefetchedBlockBatch? TakePendingPrefetch(bool cancel)
    {
        Task<PrefetchedBlockBatch>? task = _prefetchTask;
        if (task is null)
        {
            return null;
        }

        CancellationTokenSource cancellation = _prefetchCancellation
            ?? throw new InvalidOperationException("RF prefetch cancellation state was not initialized.");
        _prefetchTask = null;
        _prefetchCancellation = null;
        if (cancel)
        {
            cancellation.Cancel();
        }

        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancel)
        {
            return null;
        }
        catch (Exception exception) when (
            cancel && exception is not OutOfMemoryException and not AccessViolationException)
        {
            return null;
        }
        finally
        {
            cancellation.Dispose();
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

        internal RfDecodedSpanLease(
            RfBlockStreamDecoder? owner,
            ReusableSpanBuffers? buffers,
            RfDecodedSpan span)
        {
            _owner = owner;
            _buffers = buffers;
            Span = span;
        }

        internal RfDecodedSpan Span { get; }

        public void Dispose()
        {
            RfBlockStreamDecoder? owner = Interlocked.Exchange(ref _owner, null);
            ReusableSpanBuffers? buffers = Interlocked.Exchange(ref _buffers, null);
            if (owner is not null && buffers is not null)
            {
                owner.ReturnReusableSpanBuffers(buffers);
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

        internal ReusableSpanBuffers(int length)
        {
            Length = length;
            Input = new double[length];
            Video = new double[length];
            DemodRaw = new double[length];
            Envelope = new double[length];
            VideoLowPass = new double[length];
            RfHighPass = new double[length];
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

    private sealed record PrefetchedBlockBatch(
        Stream Stream,
        long[] BlockNumbers,
        RfPipelineBlock[] Blocks);
}
