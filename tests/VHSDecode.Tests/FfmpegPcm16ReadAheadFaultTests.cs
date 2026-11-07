using System.Buffers.Binary;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class FfmpegPcm16ReadAheadFaultTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task BoundedRingWrapsWithoutDroppingOddChunksOrExceedingCapacity()
    {
        byte[] expected = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var source = new ScriptedSource(expected);
        using var ring = new BoundedPcmReadAheadStream(source, CancellationToken.None, capacity: 5);
        var output = new List<byte>();
        byte[] destination = new byte[3];

        Assert.True(SpinWait.SpinUntil(() => ring.BufferedBytes == 5, Watchdog));
        Assert.Equal(5, source.BytesRead);
        Assert.Equal(2, ring.Read(destination, 0, 2));
        output.AddRange(destination.AsSpan(0, 2).ToArray());

        // The two free bytes at the front must be filled before continuing.
        Assert.True(SpinWait.SpinUntil(() => ring.BufferedBytes == 5, Watchdog));
        Assert.Equal(7, source.BytesRead);
        await Task.Run(() =>
        {
            int count;
            while ((count = ring.Read(destination, 0, destination.Length)) != 0)
            {
                output.AddRange(destination.AsSpan(0, count).ToArray());
            }
        }).WaitAsync(Watchdog);

        await ring.Producer.WaitAsync(Watchdog);
        Assert.Equal(expected, output.ToArray());
        Assert.Equal(0, ring.BufferedBytes);
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("nonzero-exit")]
    [InlineData("stdout-fault")]
    public async Task IndexedPrefetchRevealsTerminalOnlyAtTheForegroundByteFrontier(string terminal)
    {
        var stdoutFailure = new IOException("scripted stdout read failure");
        var source = new ScriptedSource(
            [.. Pcm(700, 701, -32768, 32767, -7, 8, -9), 0xA6],
            terminal == "stdout-fault" ? stdoutFailure : null);
        int opens = 0;
        int exitChecks = 0;
        using var loader = OpenMapped((_, sample) =>
        {
            Assert.Equal(0L, sample);
            Assert.Equal(1, ++opens);
            return new PrefixSkippingStream(source, 2 * sizeof(short));
        }, rewindSize: 8, seekThreshold: 64, exitCode: () =>
        {
            exitChecks++;
            return terminal == "nonzero-exit" ? 17 : 0;
        });
        loader.EnableMappedReadAhead(CancellationToken.None);

        Assert.Equal(new double[] { -32768, 32767 }, loader.Read(Stream.Null, 0, 2));
        await source.AtTerminal.Task.WaitAsync(Watchdog);
        Assert.Equal(0, exitChecks);
        Assert.Equal(new double[] { -7, 8 }, loader.Read(Stream.Null, 2, 2));
        Assert.Equal(0, exitChecks);

        var partial = new short[2];
        switch (terminal)
        {
            case "clean":
                Assert.True(loader.TryReadInt16(Stream.Null, 4, partial, out int samplesRead));
                Assert.Equal(1, samplesRead);
                Assert.Equal(-9, partial[0]);
                Assert.Equal(1, exitChecks);
                Assert.Equal(new double[] { -7, 8, -9 }, loader.Read(Stream.Null, 2, 3));
                Assert.Null(loader.Read(Stream.Null, 6, 1));
                break;

            case "nonzero-exit":
                var exit = Assert.Throws<InvalidOperationException>(() => loader.Read(Stream.Null, 4, 2));
                Assert.Contains("scripted FFmpeg stderr", exit.Message);
                Assert.Equal(1, exitChecks);
                Assert.Equal(new double[] { -32768, 32767, -7, 8 }, loader.Read(Stream.Null, 0, 4));
                break;

            case "stdout-fault":
                Assert.Same(stdoutFailure, Assert.Throws<IOException>(() => loader.Read(Stream.Null, 4, 2)));
                Assert.Equal(0, exitChecks);
                Assert.Equal(new double[] { -32768, 32767, -7, 8 }, loader.Read(Stream.Null, 0, 4));
                break;
        }

        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task DeclinedExactConsumePreservesRewindAndEveryBufferedSample()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var source = new BlockingSource(Pcm(71, -32768, 32767, -9));
        int opens = 0;
        using var loader = OpenMapped((_, _) =>
        {
            Assert.Equal(1, ++opens);
            return source;
        }, rewindSize: 8, seekThreshold: 16);
        loader.EnableMappedReadAhead(cancellationToken);
        Assert.Equal(new double[] { 71, -32768 }, loader.Read(Stream.Null, 0, 2));
        await source.Blocked.Task.WaitAsync(Watchdog, cancellationToken);

        // One sample is in rewind and two are ready in the live ring. The
        // fourth requested sample cannot be supplied without blocking.
        var decline = await Task.Run(() =>
        {
            bool loaded = loader.TryConsumeExact(1, 4, out double[]? samples);
            return (loaded, samples);
        }, cancellationToken).WaitAsync(Watchdog, cancellationToken);
        Assert.False(decline.loaded);
        Assert.Null(decline.samples);
        Assert.False(source.ReadExited.Task.IsCompleted);

        double[]? ordinary = await Task.Run(() => loader.Read(Stream.Null, 1, 2), cancellationToken).WaitAsync(Watchdog, cancellationToken);
        Assert.Equal(new double[] { -32768, 32767 }, ordinary);
        Assert.True(loader.TryConsumeExact(2, 2, out double[]? buffered));
        Assert.Equal(new double[] { 32767, -9 }, buffered);
        Assert.Equal(new double[] { 71, -32768, 32767, -9 }, loader.Read(Stream.Null, 0, 4));
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task CancellationWakesForegroundWhileBlockedSourceStopsOnClose()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new BlockingSource(Pcm(71, 72));
        using var loader = OpenMapped((_, _) => source, rewindSize: 4, seekThreshold: 16);
        loader.EnableMappedReadAhead(cancellation.Token);
        Assert.Equal(new double[] { 71, 72 }, loader.Read(Stream.Null, 0, 2));
        await source.Blocked.Task.WaitAsync(Watchdog);
        var entered = Signal();
        Task<double[]?> reader = Task.Run(() =>
        {
            entered.TrySetResult(true);
            return loader.Read(Stream.Null, 2, 2);
        });
        await entered.Task.WaitAsync(Watchdog);

        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => { await reader.WaitAsync(Watchdog); });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(source.ReadExited.Task.IsCompleted);
        Assert.Equal(new double[] { 71, 72 }, loader.Read(Stream.Null, 0, 2));

        loader.Dispose();
        loader.Dispose();
        Assert.True(source.ReadExited.Task.IsCompletedSuccessfully);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task CancellationWakesProducerAtFullCapacityWithoutAnotherSourceRead()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new ScriptedSource([0, 1, 2, 3, 4, 5, 6, 7, 8]);
        using var ring = new BoundedPcmReadAheadStream(source, cancellation.Token, capacity: 5);
        Assert.True(SpinWait.SpinUntil(() => ring.BufferedBytes == 5, Watchdog));
        Assert.Equal(5, source.BytesRead);

        cancellation.Cancel();
        await ring.Producer.WaitAsync(Watchdog);
        Assert.Equal(5, source.BytesRead);
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(
            () => ring.Read(new byte[1], 0, 1)).CancellationToken);
        ring.Dispose();
        ring.Dispose();
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(10L, false)]
    [InlineData(100L, false)]
    [InlineData(100L, true)]
    public async Task SeekDrainsOldGenerationBeforeOpenAndRejectsCloseFailure(long next, bool failClose)
    {
        var closeFailure = new IOException("scripted close failure");
        var old = new BlockingSource(Pcm(11, 12), holdExitAfterClose: true,
            closeFailure: failClose ? closeFailure : null);
        var replacement = new ScriptedSource(Pcm(211, 212));
        var opens = new List<long>();
        using var loader = OpenMapped((_, physical) =>
        {
            opens.Add(physical);
            if (opens.Count == 1)
            {
                return old;
            }

            Assert.True(old.ReadExited.Task.IsCompletedSuccessfully);
            Assert.Equal(1, old.DisposeCount);
            return replacement;
        }, rewindSize: 4, seekThreshold: 16);
        loader.EnableMappedReadAhead(CancellationToken.None);
        Assert.Equal(new double[] { 11, 12 }, loader.Read(Stream.Null, 12, 2));
        await old.Blocked.Task.WaitAsync(Watchdog);

        Task<double[]?> seek = Task.Run(() => loader.Read(Stream.Null, next, 2));
        try
        {
            await old.Closed.Task.WaitAsync(Watchdog);
            Assert.False(seek.IsCompleted);
        }
        finally
        {
            old.AllowExit.TrySetResult(true);
        }

        if (failClose)
        {
            var error = await Assert.ThrowsAsync<IOException>(async () => { await seek.WaitAsync(Watchdog); });
            Assert.Same(closeFailure, error.InnerException);
            Assert.Equal(new long[] { 12 }, opens.ToArray());
        }
        else
        {
            Assert.Equal(new double[] { 211, 212 }, await seek.WaitAsync(Watchdog));
            Assert.Equal(new long[] { 12, next }, opens.ToArray());
        }

        loader.Dispose();
        loader.Dispose();
        Assert.True(old.ReadExited.Task.IsCompletedSuccessfully);
        Assert.Equal(1, old.DisposeCount);
        Assert.Equal(failClose ? 0 : 1, replacement.DisposeCount);
    }

    [Fact]
    public void StockLaserDiscOptionsAllowMeasuredStartAndTerminalControls()
    {
        var parser = new CommandLineParser();
        var bounded = parser.Parse(CliSpecs.LaserDisc,
            ["--NTSC", "--start", "16000", "-l", "480", "-t", "4", "--dsp-backend", "ipp-fast", "capture.ldf", "output"]);
        var terminal = parser.Parse(CliSpecs.LaserDisc,
            ["--start_fileloc", "110790417408", "--ignoreleadout", "--dsp-backend", "ipp-fast", "capture.ldf", "output"]);

        Assert.True(DecodeRunner.HasStandardLaserDiscReadAheadOptions(bounded));
        Assert.True(DecodeRunner.HasStandardLaserDiscReadAheadOptions(terminal));
    }

    [Theory]
    [InlineData("--PAL")]
    [InlineData("--NTSCJ")]
    [InlineData("--noEFM")]
    [InlineData("--daa")]
    [InlineData("--noDOD")]
    [InlineData("--lowband")]
    [InlineData("--preview-server")]
    [InlineData("--RF_TBC")]
    [InlineData("--noAGC")]
    [InlineData("--V4300D_notch_filter")]
    [InlineData("--frequency=40")]
    [InlineData("--MTF=0.8")]
    [InlineData("--analog_audio_frequency=48000")]
    [InlineData("--seek=10")]
    public void NonstandardLaserDiscOptionsDoNotAdmitReadAhead(string option)
    {
        string[] args = option == "--preview-server"
            ? [option, "--dsp-backend", "ipp-fast", "capture.ldf"]
            : [option, "--dsp-backend", "ipp-fast", "capture.ldf", "output"];
        var command = new CommandLineParser().Parse(CliSpecs.LaserDisc, args);
        Assert.False(DecodeRunner.HasStandardLaserDiscReadAheadOptions(command));
    }

    [Fact]
    public void NondefaultValuesCannotBypassTheGuardByOmittingOptionSources()
    {
        var parser = new CommandLineParser();
        var command = parser.Parse(CliSpecs.LaserDisc, ["capture.ldf", "output"]);
        var values = new Dictionary<string, object?>(command.Values) { ["MTF"] = 0.8 };
        var changed = new ParsedCommand(CliSpecs.LaserDisc, values, command.Positionals.ToList());

        Assert.False(DecodeRunner.HasStandardLaserDiscReadAheadOptions(changed));
    }

    [Fact]
    public void NativeReadsKeepTheMappedFallbackLazy()
    {
        using var loader = new LibsndfilePcm16SampleLoader(
            "capture.ldf",
            _ => new NativeSource(),
            new StaleRawFlacFallbackSampleLoader(() => throw new InvalidOperationException("Unexpected fallback creation.")));

        loader.MappedReadAheadFallback!.EnableMappedReadAhead(CancellationToken.None);

        Assert.Equal(new double[] { 71, 71 }, loader.Read(Stream.Null, 0, 2));
    }

    [Fact]
    public void UnmappedFfmpegFallbackStillReadsOnDemand()
    {
        var source = new ScriptedSource(Pcm(31, 32, 33, 34));
        using var fallback = new StaleRawFlacFallbackSampleLoader(
            () => new FfmpegPcm16SampleLoader("capture.ldf", (_, _) => source));

        fallback.EnableMappedReadAhead(CancellationToken.None);

        Assert.Empty(fallback.Read(Stream.Null, 0, 0)!);
        Assert.Null(fallback.SelectedSeekMode);
        Assert.Equal(new double[] { 31, 32 }, fallback.Read(Stream.Null, 0, 2));
        Assert.Equal(FfmpegPcm16SeekMode.Exact, fallback.SelectedSeekMode);
        Assert.Equal(4, source.BytesRead);
    }

    private sealed class NativeSource : ILibsndfilePcm16Source
    {
        public long Frames => 2;
        public long Seek(long sample) => sample;

        public long ReadFrames(Span<short> samples)
        {
            samples.Fill(71);
            return samples.Length;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task TerminalCompletionReportsCloseFailureButDisposalPreservesThePrimaryError()
    {
        var closeFailure = new IOException("scripted close failure");
        var source = new BlockingSource(Pcm(11, 12), closeFailure: closeFailure);
        using var loader = OpenMapped((_, _) => source, rewindSize: 4, seekThreshold: 16);
        loader.EnableMappedReadAhead(CancellationToken.None);
        Assert.Equal(new double[] { 11, 12 }, loader.Read(Stream.Null, 0, 2));
        await source.Blocked.Task.WaitAsync(Watchdog);

        var error = Assert.Throws<IOException>(loader.CompleteMappedReadAhead);
        Assert.Same(closeFailure, error.InnerException);

        var primary = new InvalidOperationException("primary decode failure");
        Action disposeAfterFailure = () =>
        {
            try { throw primary; }
            finally { loader.Dispose(); }
        };
        var observed = Assert.Throws<InvalidOperationException>(disposeAfterFailure);
        Assert.Same(primary, observed);
        Assert.True(source.ReadExited.Task.IsCompletedSuccessfully);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task RestartTimeoutKeepsTheOldGenerationUntilItActuallyFinishes()
    {
        var old = new BlockingSource(Pcm(11, 12), holdExitAfterClose: true);
        var replacement = new ScriptedSource(Pcm(211, 212));
        var opens = new List<long>();
        using var loader = OpenMapped((_, sample) =>
        {
            opens.Add(sample);
            if (opens.Count == 1)
            {
                return old;
            }

            Assert.True(old.ReadExited.Task.IsCompletedSuccessfully);
            return replacement;
        }, rewindSize: 4, seekThreshold: 16);
        loader.EnableMappedReadAhead(CancellationToken.None);
        Assert.Equal(new double[] { 11, 12 }, loader.Read(Stream.Null, 0, 2));
        await old.Blocked.Task.WaitAsync(Watchdog);

        Task<double[]?> seek = Task.Run(() => loader.Read(Stream.Null, 100, 2));
        try
        {
            await old.Closed.Task.WaitAsync(Watchdog);
            var error = await Assert.ThrowsAsync<IOException>(async () => { await seek.WaitAsync(Watchdog); });
            Assert.IsType<TimeoutException>(error.InnerException);
            Assert.Equal(new long[] { 0 }, opens.ToArray());
        }
        finally
        {
            old.AllowExit.TrySetResult(true);
        }

        Assert.Equal(new double[] { 211, 212 }, loader.Read(Stream.Null, 100, 2));
        Assert.Equal(new long[] { 0, 100 }, opens.ToArray());
    }

    private static FfmpegPcm16SampleLoader OpenMapped(
        Func<string, long, Stream> open, int rewindSize, int seekThreshold, Func<int?>? exitCode = null)
        => new("capture.ldf",
            new PyAvRawFlacSampleMapper(FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz, blockSize: 2_048),
            open, rewindSize, seekThreshold, exitCode, () => "scripted FFmpeg stderr");

    private static TaskCompletionSource<bool> Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), samples[i]);
        }

        return bytes;
    }

    private abstract class Source : Stream
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public sealed override bool CanRead => true;
        public sealed override bool CanSeek => false;
        public sealed override bool CanWrite => false;
        public sealed override long Length => throw new NotSupportedException();
        public sealed override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public sealed override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public sealed override void Flush() => throw new NotSupportedException();
        public sealed override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public sealed override void SetLength(long value) => throw new NotSupportedException();
        public sealed override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected sealed override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposeCount);
                CloseSource();
            }

            base.Dispose(disposing);
        }

        protected virtual void CloseSource() { }
    }

    private sealed class ScriptedSource(byte[] data, IOException? fault = null) : Source
    {
        private int _position;
        internal TaskCompletionSource<bool> AtTerminal { get; } = Signal();
        internal int BytesRead => Volatile.Read(ref _position);

        public override int Read(Span<byte> buffer)
        {
            if (_position == data.Length)
            {
                AtTerminal.TrySetResult(true);
                if (fault is not null)
                {
                    throw fault;
                }

                return 0;
            }

            int count = Math.Min(3, Math.Min(buffer.Length, data.Length - _position));
            data.AsSpan(_position, count).CopyTo(buffer);
            Volatile.Write(ref _position, _position + count);
            return count;
        }
    }

    private sealed class BlockingSource(
        byte[] first, bool holdExitAfterClose = false, IOException? closeFailure = null) : Source
    {
        private int _position;
        internal TaskCompletionSource<bool> Blocked { get; } = Signal();
        internal TaskCompletionSource<bool> Closed { get; } = Signal();
        internal TaskCompletionSource<bool> AllowExit { get; } = Signal();
        internal TaskCompletionSource<bool> ReadExited { get; } = Signal();

        public override int Read(Span<byte> buffer)
        {
            if (_position < first.Length)
            {
                int count = Math.Min(buffer.Length, first.Length - _position);
                first.AsSpan(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            Blocked.TrySetResult(true);
            try
            {
                AllowExit.Task.WaitAsync(Watchdog).GetAwaiter().GetResult();
                buffer[0] = 0x91;
                return 1;
            }
            finally
            {
                ReadExited.TrySetResult(true);
            }
        }

        protected override void CloseSource()
        {
            Closed.TrySetResult(true);
            if (!holdExitAfterClose)
            {
                AllowExit.TrySetResult(true);
            }

            if (DisposeCount == 1 && closeFailure is not null)
            {
                throw closeFailure;
            }
        }
    }
}
