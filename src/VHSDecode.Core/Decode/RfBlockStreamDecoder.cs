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
    internal const int MaximumPrefetchBlocks = 8;
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
            ? Math.Min(prefetchBlocks, Math.Min(workerThreads, MaximumPrefetchBlocks))
            : 0;
        _decodedBlockCacheCapacity = checked(DecodedBlockCacheCapacity + PrefetchBlocks);
    }

    public int BlockLength { get; }

    public int BlockCut { get; }

    public int BlockCutEnd { get; }

    public int BlockStride { get; }

    public int WorkerThreads { get; }

    public int PrefetchBlocks { get; }

    internal int CachedDecodedBlockCount => _decodedBlockCache.Count;

    internal int CachedPrefetchedBlockCount => _prefetchedBlockCache.Count;

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
        var input = new double[totalDecoded];
        var video = new double[totalDecoded];
        var demodRaw = new double[totalDecoded];
        var envelope = new double[totalDecoded];
        var videoLowPass = new double[totalDecoded];
        var rfHighPass = new double[totalDecoded];
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
            CopyTrimmed(pipelineBlock.Input, input, destination);
            CopyTrimmed(pipelineBlock.Demodulated.Video, video, destination);
            CopyTrimmed(pipelineBlock.Demodulated.DemodRaw, demodRaw, destination);
            CopyTrimmed(pipelineBlock.Demodulated.Envelope, envelope, destination);
            CopyTrimmed(pipelineBlock.Demodulated.VideoLowPass, videoLowPass, destination);
            CopyTrimmed(
                pipelineBlock.Demodulated.RfHighPass,
                rfHighPass,
                destination,
                BlockCut - _pipeline.RfHighPassOffset);
            if (pipelineBlock.Demodulated.Chroma is not null)
            {
                chroma ??= new double[totalDecoded];
                CopyTrimmed(pipelineBlock.Demodulated.Chroma, chroma, destination);
            }

            if (pipelineBlock.Demodulated.VideoBurst is not null)
            {
                videoBurst ??= new double[totalDecoded];
                CopyTrimmed(pipelineBlock.Demodulated.VideoBurst, videoBurst, destination);
            }

            if (pipelineBlock.Demodulated.VideoPilot is not null)
            {
                videoPilot ??= new double[totalDecoded];
                CopyTrimmed(pipelineBlock.Demodulated.VideoPilot, videoPilot, destination);
            }

            if (pipelineBlock.Demodulated.Efm is not null)
            {
                efm ??= new short[totalDecoded];
                CopyTrimmed(pipelineBlock.Demodulated.Efm, efm, destination);
            }

            if (pipelineBlock.Demodulated.AnalogAudio is not null)
            {
                LaserDiscAnalogAudioBlock audio = pipelineBlock.Demodulated.AnalogAudio;
                if (audioDecimationFactor == 0)
                {
                    audioDecimationFactor = audio.DecimationFactor;
                    int totalAudioDecoded = checked(totalDecoded / audioDecimationFactor);
                    audioLeft = new double[totalAudioDecoded];
                    audioRight = new double[totalAudioDecoded];
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

        int offset = checked((int)(begin - (firstBlock * BlockStride)));
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
            Slice(input, offset, length),
            Slice(video, offset, length),
            Slice(demodRaw, offset, length),
            Slice(envelope, offset, length),
            Slice(videoLowPass, offset, length),
            Slice(rfHighPass, offset, length),
            efm is null ? null : Slice(efm, offset, length),
            audioSpan,
            chroma is null ? null : Slice(chroma, offset, length),
            videoBurst is null ? null : Slice(videoBurst, offset, length),
            videoPilot is null ? null : Slice(videoPilot, offset, length));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _ = TakePendingPrefetch(cancel: true);
        }
        finally
        {
            _decodedBlockCache.Clear();
            _prefetchedBlockCache.Clear();
            _sequentialBlockCache.Clear();
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
                    MaxDegreeOfParallelism = Math.Min(WorkerThreads, preparedInputs.Length)
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

    private void CopyTrimmed(
        double[] source,
        double[] destination,
        int destinationOffset,
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

        Array.Copy(source, actualSourceOffset, destination, destinationOffset, BlockStride);
    }

    private void CopyTrimmed(short[] source, short[] destination, int destinationOffset)
    {
        if (source.Length != BlockLength)
        {
            throw new ArgumentException("Decoded block length did not match the configured block length.", nameof(source));
        }

        Array.Copy(source, BlockCut, destination, destinationOffset, BlockStride);
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

    private static short[] Slice(short[] source, int offset, int length)
    {
        if (offset == 0 && length == source.Length)
        {
            return source;
        }

        var output = new short[length];
        Array.Copy(source, offset, output, 0, length);
        return output;
    }

    private sealed record PrefetchedBlockBatch(
        Stream Stream,
        long[] BlockNumbers,
        RfPipelineBlock[] Blocks);
}
