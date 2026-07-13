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

public sealed class RfBlockStreamDecoder
{
    private readonly RfBlockDecodePipeline _pipeline;
    private readonly Dictionary<long, RfPipelineBlock> _sequentialBlockCache = [];
    private long? _lastSequentialDecodedBlock;

    public RfBlockStreamDecoder(
        RfBlockDecodePipeline pipeline,
        int blockLength,
        int blockCut,
        int blockCutEnd,
        int workerThreads = 1)
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

        _pipeline = pipeline;
        BlockLength = blockLength;
        BlockCut = blockCut;
        BlockCutEnd = blockCutEnd;
        BlockStride = blockLength - blockCut - blockCutEnd;
        WorkerThreads = workerThreads;
    }

    public int BlockLength { get; }

    public int BlockCut { get; }

    public int BlockCutEnd { get; }

    public int BlockStride { get; }

    public int WorkerThreads { get; }

    public RfDecodedSpan? Read(Stream stream, long begin, int length)
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
            return new RfDecodedSpan(begin, [], [], []);
        }

        long endExclusive = checked(begin + length);
        long firstBlock = begin / BlockStride;
        long lastBlock = (endExclusive - 1) / BlockStride;
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
            for (int i = 0; i < blockCount; i++)
            {
                long sample = checked((firstBlock + i) * BlockStride);
                double[]? preparedInput = _pipeline.LoadBlockInput(stream, sample, BlockLength);
                if (preparedInput is null)
                {
                    return null;
                }

                preparedInputs[i] = preparedInput;
            }

            var decodedBlocks = new RfPipelineBlock[blockCount];
            Parallel.For(
                0,
                blockCount,
                new ParallelOptions { MaxDegreeOfParallelism = WorkerThreads },
                i => decodedBlocks[i] = _pipeline.DecodePreparedBlock(preparedInputs[i]));
            foreach (RfPipelineBlock pipelineBlock in decodedBlocks)
            {
                AppendBlock(pipelineBlock);
            }
        }

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
                audioDecimationFactor);
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
        var output = new double[length];
        Array.Copy(source, offset, output, 0, length);
        return output;
    }

    private static short[] Slice(short[] source, int offset, int length)
    {
        var output = new short[length];
        Array.Copy(source, offset, output, 0, length);
        return output;
    }
}
