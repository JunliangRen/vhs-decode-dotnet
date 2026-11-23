using System.Threading.Channels;
using System.Runtime.ExceptionServices;

namespace VHSDecode.Core.HiFi;

internal interface IHiFiBlockDecoder : IDisposable
{
    HiFiDecodePlan Plan { get; }

    HiFiDecodedBlock Decode(ReadOnlySpan<float> rfData);
}

internal sealed record HiFiStreamingDecodeResult(
    long InputSamples,
    long AudioFrames,
    int BlockCount,
    float LeftPeak,
    float RightPeak);

internal sealed class HiFiStreamingDecoder
{
    private readonly Func<HiFiDecodeOptions, Action<float[]>?, IHiFiBlockDecoder> _decoderFactory;

    public HiFiStreamingDecoder()
        : this((options, gnuRadioSink) => new HiFiBlockDecoder(options, gnuRadioSink))
    {
    }

    internal HiFiStreamingDecoder(Func<HiFiDecodeOptions, IHiFiBlockDecoder> decoderFactory)
        : this((options, _) => decoderFactory(options))
    {
    }

    internal HiFiStreamingDecoder(
        Func<HiFiDecodeOptions, Action<float[]>?, IHiFiBlockDecoder> decoderFactory)
    {
        _decoderFactory = decoderFactory ?? throw new ArgumentNullException(nameof(decoderFactory));
    }

    public HiFiStreamingDecodeResult Decode(
        HiFiDecodeOptions options,
        IHiFiSampleReader input,
        HiFiOutputWriter output,
        TextWriter diagnostics,
        CancellationToken cancellationToken = default,
        IHiFiPreviewSink? previewSink = null,
        Func<TimeSpan>? elapsedProvider = null)
        => DecodeAsync(
                options,
                input,
                output,
                diagnostics,
                cancellationToken,
                previewSink,
                elapsedProvider)
            .GetAwaiter()
            .GetResult();

    private async Task<HiFiStreamingDecodeResult> DecodeAsync(
        HiFiDecodeOptions options,
        IHiFiSampleReader input,
        HiFiOutputWriter output,
        TextWriter diagnostics,
        CancellationToken cancellationToken,
        IHiFiPreviewSink? previewSink,
        Func<TimeSpan>? elapsedProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (options.TapeFormat == "betamax")
        {
            return new BetaHiFiStreamingDecoder().Decode(
                options, input, output, diagnostics, cancellationToken, elapsedProvider);
        }
        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(options);
        var progressReporter = new HiFiProgressReporter(
            diagnostics,
            plan.InputRateHz,
            plan.FinalAudioRateHz,
            input.TotalSamples,
            elapsedProvider);
        if (options.ThreadsInteger <= 0)
        {
            return await WaitWithoutDecoderWorkers(
                options,
                plan,
                input,
                diagnostics,
                progressReporter,
                cancellationToken);
        }

        int workerCount = options.GnuRadio ? 1 : options.Threads;
        var jobs = Channel.CreateBounded<HiFiDecodeJob>(new BoundedChannelOptions(workerCount + 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = workerCount == 1
        });
        var results = Channel.CreateBounded<HiFiDecodeBlockResult>(new BoundedChannelOptions(
            workerCount + 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = workerCount == 1,
            SingleReader = true
        });
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken stopToken = stopSource.Token;

        var framer = new HiFiBlockFramer(plan, input);
        var postProcessor = new HiFiAudioPostProcessor(options);
        // Start normal file decoding without filling the bounded queues on the
        // caller before the ordered output loop can begin draining them.
        bool startConcurrently = !options.AutoFineTune && !options.GnuRadio
            && !options.Preview && previewSink is null
            && SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;
        Task producer = ProduceBlocks(
            framer,
            jobs.Writer,
            progressReporter,
            stopSource,
            stopToken,
            startConcurrently);
        int firstOutputWritten = 0;
        Func<bool>? canPairBlocks = previewSink is null
            ? () => Volatile.Read(ref firstOutputWritten) != 0
            : null;
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => DecodeBlocks(
                options,
                jobs.Reader,
                results.Writer,
                diagnostics,
                stopSource,
                stopToken,
                canPairBlocks,
                startConcurrently))
            .ToArray();
        Task resultCompletion = CompleteResults(workers, results.Writer, stopSource);

        var pending = new SortedDictionary<int, HiFiDecodeBlockResult>();
        int nextBlock = 0;
        long audioFrames = 0;
        bool wrotePadding = false;
        try
        {
            await foreach (HiFiDecodeBlockResult result in results.Reader.ReadAllAsync(stopToken))
            {
                if (!pending.TryAdd(result.BlockNumber, result))
                {
                    throw new InvalidDataException(
                        $"HiFi block {result.BlockNumber} was decoded more than once.");
                }

                while (pending.Remove(nextBlock, out HiFiDecodeBlockResult? ordered))
                {
                    if (!wrotePadding)
                    {
                        output.WriteInitialPadding(plan.BlockOverlap.FinalAudioSamples);
                        wrotePadding = true;
                    }

                    progressReporter.ReportBias(ordered.Decoded, options.AudioMode, plan.Afe);
                    HiFiPostProcessedBlock processed = postProcessor.Process(
                        ordered.Decoded,
                        ordered.BlockNumber);
                    output.Write(processed);
                    if (nextBlock == 0)
                    {
                        Volatile.Write(ref firstOutputWritten, 1);
                    }
                    previewSink?.Write(processed.Stereo, stopToken);
                    audioFrames = checked(audioFrames + processed.Left.Length);
                    progressReporter.ReportOutput(
                        framer.InputSamplesRead,
                        audioFrames,
                        framer.BlockCount);
                    nextBlock++;
                }
            }

            await producer;
            await resultCompletion;
        }
        catch (Exception exception)
        {
            stopSource.Cancel();
            await ObserveFailure(producer);
            await ObserveFailure(resultCompletion);
            Exception? primaryFailure = PrimaryFailure(producer, workers);
            if (primaryFailure is not null
                && (exception is OperationCanceledException or ChannelClosedException))
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            throw;
        }

        if (pending.Count != 0 || nextBlock != framer.BlockCount)
        {
            throw new InvalidDataException(
                $"HiFi decode ended with a missing block. Expected {nextBlock}, produced {framer.BlockCount}.");
        }

        return new HiFiStreamingDecodeResult(
            framer.InputSamplesRead,
            audioFrames,
            nextBlock,
            postProcessor.PeakLeft,
            postProcessor.PeakRight);
    }

    private static async Task<HiFiStreamingDecodeResult> WaitWithoutDecoderWorkers(
        HiFiDecodeOptions options,
        HiFiDecodePlan plan,
        IHiFiSampleReader input,
        TextWriter diagnostics,
        HiFiProgressReporter progressReporter,
        CancellationToken cancellationToken)
    {
        int idleBufferCount = options.ThreadsInteger == 0
            ? 2
            : options.ThreadsInteger == -1
                ? 1
                : 0;
        if (idleBufferCount > 0)
        {
            var framer = new HiFiBlockFramer(plan, input);
            for (int i = 0; i < idleBufferCount; i++)
            {
                HiFiDecodeJob job = framer.ReadNext(cancellationToken)
                    ?? throw new InvalidDataException("HiFi input ended without a terminal block.");
                progressReporter.ReportInput(
                    framer.InputSamplesRead,
                    framer.BlockCount);
                if (job.IsLastBlock)
                {
                    diagnostics.WriteLine();
                    diagnostics.WriteLine("Decode finishing up. Emptying the queue");
                    diagnostics.WriteLine();
                    break;
                }
            }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("No HiFi decoder worker became available.");
    }

    private static async Task ProduceBlocks(
        HiFiBlockFramer framer,
        ChannelWriter<HiFiDecodeJob> writer,
        HiFiProgressReporter progressReporter,
        CancellationTokenSource stopSource,
        CancellationToken cancellationToken,
        bool startConcurrently)
    {
        if (startConcurrently)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        Exception? failure = null;
        try
        {
            while (framer.ReadNext(cancellationToken) is { } job)
            {
                await writer.WriteAsync(job, cancellationToken);
                progressReporter.ReportInput(
                    framer.InputSamplesRead,
                    framer.BlockCount);
                if (job.IsLastBlock)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            stopSource.Cancel();
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private async Task DecodeBlocks(
        HiFiDecodeOptions options,
        ChannelReader<HiFiDecodeJob> reader,
        ChannelWriter<HiFiDecodeBlockResult> writer,
        TextWriter diagnostics,
        CancellationTokenSource stopSource,
        CancellationToken cancellationToken,
        Func<bool>? canPairBlocks,
        bool startConcurrently)
    {
        if (startConcurrently)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        try
        {
            using var gnuRadioSink = options.GnuRadio
                ? new HiFiGnuRadioSink(diagnostics, cancellationToken)
                : null;
            Action<float[]>? gnuRadioCallback = gnuRadioSink is null
                ? null
                : gnuRadioSink.Send;
            using IHiFiBlockDecoder decoder = _decoderFactory(
                options,
                gnuRadioCallback);
            HiFiBlockDecoder? pairDecoder = canPairBlocks is not null
                && decoder is HiFiBlockDecoder { CanDecodeStereoPairs: true } stereo
                ? stereo : null;
            await foreach (HiFiDecodeJob job in reader.ReadAllAsync(cancellationToken))
            {
                // Pair only after output starts, and only with a job already queued.
                if (pairDecoder is not null && !job.IsLastBlock && canPairBlocks!()
                    && reader.TryRead(out HiFiDecodeJob? next))
                {
                    if (!next.IsLastBlock
                        && pairDecoder.TryDecodeStereoPair(job.Samples, next.Samples) is { } pair)
                    {
                        await WriteResult(job, pair.First);
                        await WriteResult(next, pair.Second);
                    }
                    else
                    {
                        await WriteResult(job, decoder.Decode(job.Samples));
                        await WriteResult(next, decoder.Decode(next.Samples));
                    }

                    continue;
                }

                await WriteResult(job, decoder.Decode(job.Samples));
            }

            ValueTask WriteResult(HiFiDecodeJob job, HiFiDecodedBlock decoded)
                => writer.WriteAsync(
                    new HiFiDecodeBlockResult(job.BlockNumber, TrimDecodedBlock(decoded, decoder.Plan, job)),
                    cancellationToken);
        }
        catch
        {
            stopSource.Cancel();
            throw;
        }
    }

    private static async Task CompleteResults(
        IReadOnlyList<Task> workers,
        ChannelWriter<HiFiDecodeBlockResult> writer,
        CancellationTokenSource stopSource)
    {
        Exception? failure = null;
        try
        {
            await Task.WhenAll(workers);
        }
        catch (Exception ex)
        {
            failure = ex;
            stopSource.Cancel();
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    internal static HiFiDecodedBlock TrimDecodedBlock(
        HiFiDecodedBlock decoded,
        HiFiDecodePlan plan,
        HiFiDecodeJob job)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(job);
        if (decoded.Left.Length != decoded.Right.Length)
        {
            throw new InvalidDataException("HiFi decoded channel lengths do not match.");
        }

        int desiredLength = plan.CalculateFinalAudioLength(
            job.FramesRead,
            job.IsLastBlock,
            job.PlanInputSamples);
        int trimStart = job.IsLastBlock
            ? decoded.Left.Length
                - (desiredLength + plan.BlockOverlap.FinalAudioSamples)
            : Math.Max(
                0,
                checked((int)Math.Round(
                    (decoded.Left.Length - desiredLength) / 2.0,
                    MidpointRounding.ToEven)));
        if (desiredLength > decoded.Left.Length
            || trimStart < -decoded.Left.Length
            || (trimStart >= 0 && trimStart > decoded.Left.Length - desiredLength))
        {
            throw new InvalidDataException(
                $"HiFi block {job.BlockNumber} produced {decoded.Left.Length} samples, "
                + $"which cannot satisfy the Release 4.0 trim of {desiredLength} samples at {trimStart}.");
        }

        return decoded with
        {
            Left = CopyRelease40WorkerSlice(decoded.Left, trimStart, desiredLength),
            Right = CopyRelease40WorkerSlice(decoded.Right, trimStart, desiredLength)
        };
    }

    private static float[] CopyRelease40WorkerSlice(
        float[] source,
        int sourceOffset,
        int length)
    {
        if (sourceOffset >= 0)
        {
            return source.AsSpan(sourceOffset, length).ToArray();
        }

        // The Release 4.0 Numba worker indexes each sample separately. A negative
        // source offset therefore wraps through the array's tail before index zero.
        var destination = new float[length];
        int wrappedLength = Math.Min(-sourceOffset, length);
        source.AsSpan(source.Length + sourceOffset, wrappedLength).CopyTo(destination);
        source.AsSpan(0, length - wrappedLength).CopyTo(destination.AsSpan(wrappedLength));
        return destination;
    }

    private static async Task ObserveFailure(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static Exception? PrimaryFailure(Task producer, IEnumerable<Task> workers)
    {
        IEnumerable<Task> tasks = new[] { producer }.Concat(workers);
        foreach (Task task in tasks)
        {
            Exception? failure = task.Exception?
                .Flatten()
                .InnerExceptions
                .FirstOrDefault(exception => exception is not OperationCanceledException);
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }
}

internal sealed record HiFiDecodeJob(
    int BlockNumber,
    float[] Samples,
    int FramesRead,
    bool IsLastBlock,
    int PlanInputSamples);

internal sealed record HiFiDecodeBlockResult(
    int BlockNumber,
    HiFiDecodedBlock Decoded);

internal sealed class HiFiBlockFramer
{
    private readonly HiFiDecodePlan _plan;
    private readonly IHiFiSampleReader _reader;
    private readonly int _blockSize;
    private readonly int _readOverlap;
    private readonly int _discardOverlap;
    private readonly int _readLength;
    private float[]? _previousOverlap;
    private float[]? _previousBlock;
    private bool _finished;

    public HiFiBlockFramer(HiFiDecodePlan plan, IHiFiSampleReader reader)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _blockSize = plan.InitialBlockSizes.InputSamples;
        _readOverlap = plan.BlockOverlap.ReadSamples;
        _discardOverlap = plan.BlockOverlap.InputSamples;
        _readLength = checked(_blockSize - _readOverlap);
        if (_readLength <= 0)
        {
            throw new InvalidOperationException("HiFi block overlap is not smaller than its block size.");
        }
    }

    private int _blockCount;
    private long _inputSamplesRead;

    public int BlockCount => Volatile.Read(ref _blockCount);
    public long InputSamplesRead => Volatile.Read(ref _inputSamplesRead);

    public HiFiDecodeJob? ReadNext(CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            return null;
        }

        var input = new float[_readLength];
        int framesRead = _reader.Read(input, cancellationToken);
        Volatile.Write(
            ref _inputSamplesRead,
            checked(_inputSamplesRead + framesRead));
        bool isLastBlock = framesRead < input.Length;
        int blockNumber = _blockCount++;

        float[] block;
        int planInputSamples;
        if (blockNumber == 0)
        {
            planInputSamples = checked(framesRead + (_readOverlap - _discardOverlap));
            block = new float[Math.Max(planInputSamples, 1)];
            int prefixLength = Math.Min(_discardOverlap, block.Length);
            input.AsSpan(0, Math.Min(prefixLength, input.Length)).CopyTo(block);
            int copyLength = Math.Min(framesRead, block.Length - prefixLength);
            input.AsSpan(0, copyLength).CopyTo(block.AsSpan(prefixLength));
        }
        else if (isLastBlock && framesRead > 0)
        {
            planInputSamples = _blockSize;
            block = new float[_blockSize];
            int blockInputOffset = checked(_blockSize - (framesRead + _discardOverlap));
            if (blockInputOffset < 0)
            {
                throw new InvalidDataException("HiFi final block is larger than the configured block size.");
            }

            input.AsSpan(0, framesRead).CopyTo(block.AsSpan(blockInputOffset));
            float[] previousBlock = _previousBlock
                ?? throw new InvalidDataException("HiFi final block has no previous overlap source.");
            int previousOffset = checked(previousBlock.Length - blockInputOffset);
            previousBlock.AsSpan(previousOffset, blockInputOffset).CopyTo(block);
        }
        else
        {
            planInputSamples = _blockSize;
            block = new float[_blockSize];
            if (_previousOverlap is not null)
            {
                _previousOverlap.CopyTo(block, 0);
            }

            input.AsSpan(0, framesRead).CopyTo(block.AsSpan(_readOverlap));
        }

        if (!isLastBlock)
        {
            _previousOverlap = new float[_readOverlap];
            int overlapStart = Math.Max(0, block.Length - _readOverlap);
            int overlapLength = Math.Min(_readOverlap, block.Length);
            block.AsSpan(overlapStart, overlapLength)
                .CopyTo(_previousOverlap.AsSpan(_readOverlap - overlapLength));
            _previousBlock = new float[_blockSize];
            block.AsSpan(0, Math.Min(block.Length, _blockSize)).CopyTo(_previousBlock);
        }

        _finished = isLastBlock;
        return new HiFiDecodeJob(
            blockNumber,
            block,
            framesRead,
            isLastBlock,
            planInputSamples);
    }
}
