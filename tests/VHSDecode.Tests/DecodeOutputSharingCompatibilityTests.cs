using System.Diagnostics;
using System.Text.Json;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class DecodeOutputSharingCompatibilityTests
{
    [Theory(DisplayName = "Active TBC and JSON outputs remain preview-readable like v0.4.0")]
    [InlineData("vhs")]
    [InlineData("betamax")]
    [InlineData("cvbs")]
    [InlineData("ld")]
    public async Task ActiveTbcAndJsonOutputsRemainPreviewReadableLikeV040(string decoder)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = CreateTempDirectory();
        using var secondReadStarted = new ManualResetEventSlim();
        using var releaseSecondRead = new ManualResetEventSlim();
        Task<TbcFieldSequenceDecodeResult>? decodeTask = null;
        try
        {
            string outputBase = Path.Combine(tempDirectory, decoder);
            using DecodeSession session = CreateVideoSession(decoder, outputBase);
            int readCount = 0;
            TbcDecodedField? ReadField(
                DecodeSession activeSession,
                Stream _,
                long begin,
                int __,
                int ___)
            {
                int current = Interlocked.Increment(ref readCount);
                if (current == 1)
                {
                    return BuildField(activeSession, begin, detectedFirstField: true, 0x1234);
                }

                secondReadStarted.Set();
                if (!releaseSecondRead.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                {
                    throw new TimeoutException("The output sharing test did not release the second field.");
                }

                return BuildField(activeSession, begin, detectedFirstField: false, 0x5678);
            }

            var engine = new TbcFieldSequenceDecodeEngine(readField: ReadField);
            decodeTask = Task.Factory.StartNew(
                () => engine.TryDecodeAndWrite(session, Stream.Null, maxFields: 2),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(secondReadStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            long fieldBytes = checked(session.TbcFrameSpec.FieldSampleCount * sizeof(ushort));
            string tbcPath = outputBase + ".tbc";
            using FileStream tbcPreview = OpenPreview(tbcPath);
            Assert.Equal(fieldBytes, tbcPreview.Length);
            Assert.Equal(0x34, tbcPreview.ReadByte());
            Assert.Equal(0x12, tbcPreview.ReadByte());

            FileStream? chromaPreview = null;
            try
            {
                if (session.ChromaOptions?.WriteChroma == true)
                {
                    chromaPreview = OpenPreview(outputBase + "_chroma.tbc");
                    Assert.Equal(fieldBytes, chromaPreview.Length);
                    Assert.Equal(0x35, chromaPreview.ReadByte());
                    Assert.Equal(0x12, chromaPreview.ReadByte());
                }

                string jsonPath = outputBase + ".tbc.json";
                Assert.True(SpinWait.SpinUntil(
                    () => File.Exists(jsonPath),
                    TimeSpan.FromSeconds(10)));
                using (FileStream jsonPreview = OpenPreviewEventually(
                           jsonPath,
                           TimeSpan.FromSeconds(10),
                           cancellationToken))
                using (JsonDocument document = JsonDocument.Parse(jsonPreview))
                {
                    Assert.Equal(
                        1,
                        document.RootElement.GetProperty("fields").GetArrayLength());
                }

                releaseSecondRead.Set();
                TbcFieldSequenceDecodeResult result = await decodeTask.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken);

                Assert.True(result.Success, result.Message);
                Assert.Equal(2, result.WrittenFieldCount);
                Assert.Equal(fieldBytes * 2, tbcPreview.Length);
                if (chromaPreview is not null)
                {
                    Assert.Equal(fieldBytes * 2, chromaPreview.Length);
                }
            }
            finally
            {
                chromaPreview?.Dispose();
            }
        }
        finally
        {
            releaseSecondRead.Set();
            if (decodeTask is not null)
            {
                try
                {
                    await decodeTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                catch
                {
                    // Preserve the assertion or timeout that ended the test.
                }
            }

            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Parallel VHS payload output writes luma and chroma concurrently")]
    public void ParallelVhsPayloadOutputWritesLumaAndChromaConcurrently()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "parallel-payload");
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
                "--pal",
                "--threads", "2",
                "input.u8",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            Assert.True(session.StreamDecoder.WorkerThreads > 1);

            using var writeBarrier = new Barrier(2);
            using var luma = new CoordinatedWriteStream(writeBarrier);
            using var chroma = new CoordinatedWriteStream(writeBarrier);
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (activeSession, _, begin, _, _) =>
                    BuildField(activeSession, begin, detectedFirstField: true, 0x1234))
            {
                EnableParallelPayloadWritesForCustomReader = true,
                CreateTbcOutput = path => path.EndsWith("_chroma.tbc", StringComparison.Ordinal)
                    ? chroma
                    : luma
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 1);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.WrittenFieldCount);
            Assert.True(luma.Coordinated);
            Assert.True(chroma.Coordinated);
            Assert.Equal(0x34, luma.ToArray()[0]);
            Assert.Equal(0x12, luma.ToArray()[1]);
            Assert.Equal(0x35, chroma.ToArray()[0]);
            Assert.Equal(0x12, chroma.ToArray()[1]);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "VHS payload output overlaps the next field read without publishing metadata early")]
    public async Task VhsPayloadOutputOverlapsTheNextFieldReadWithoutPublishingMetadataEarly()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = CreateTempDirectory();
        using var writeStarted = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        using var writeCompleted = new ManualResetEventSlim();
        using var secondReadStarted = new ManualResetEventSlim();
        using var releaseSecondRead = new ManualResetEventSlim();
        Task<TbcFieldSequenceDecodeResult>? decodeTask = null;
        try
        {
            string outputBase = Path.Combine(tempDirectory, "overlapped-payload");
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
                "--pal",
                "--threads", "2",
                "input.u8",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            using var luma = new BlockingFirstWriteStream(
                writeStarted,
                releaseWrite,
                writeCompleted,
                cancellationToken);
            using var chroma = new MemoryStream();
            int readCount = 0;
            TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int ___)
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    return BuildField(activeSession, begin, detectedFirstField: true, 0x1234);
                }

                secondReadStarted.Set();
                if (!releaseSecondRead.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                {
                    throw new TimeoutException("The payload-overlap test did not release the second field read.");
                }

                return null;
            }

            var engine = new TbcFieldSequenceDecodeEngine(readField: ReadField)
            {
                EnableParallelPayloadWritesForCustomReader = true,
                EnablePayloadWriteOverlapForCustomReader = true,
                CreateTbcOutput = path => path.EndsWith("_chroma.tbc", StringComparison.Ordinal)
                    ? chroma
                    : luma
            };
            decodeTask = Task.Factory.StartNew(
                () => engine.TryDecodeAndWrite(session, Stream.Null, maxFields: 2),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            Assert.True(secondReadStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            Assert.False(writeCompleted.IsSet);
            Assert.False(File.Exists(outputBase + ".tbc.json"));

            releaseWrite.Set();
            Assert.True(writeCompleted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(outputBase + ".tbc.json"),
                TimeSpan.FromSeconds(10)));
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(outputBase + ".tbc.json")))
            {
                Assert.Equal(1, document.RootElement.GetProperty("fields").GetArrayLength());
            }

            releaseSecondRead.Set();
            TbcFieldSequenceDecodeResult result = await decodeTask.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.WrittenFieldCount);
            Assert.Equal(
                session.TbcFrameSpec.FieldSampleCount * sizeof(ushort),
                luma.ToArray().Length);
        }
        finally
        {
            releaseWrite.Set();
            releaseSecondRead.Set();
            if (decodeTask is not null)
            {
                try
                {
                    await decodeTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                catch
                {
                    // Preserve the assertion or timeout that ended the test.
                }
            }

            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Overlapped VHS output releases pooled field buffers")]
    public void OverlappedVhsOutputReleasesPooledFieldBuffers()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "pooled-overlap");
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
                "--pal",
                "--threads", "2",
                "input.u8",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            int sampleCount = session.TbcFrameSpec.FieldSampleCount;
            var pool = new TbcFieldOutputBufferPool(
                sampleCount,
                sampleCount,
                maximumRetainedBuffers: 4);
            int readCount = 0;
            TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int ___)
            {
                int current = Interlocked.Increment(ref readCount);
                return current switch
                {
                    1 => BuildPooledField(activeSession, pool, begin, detectedFirstField: true, 0x1234),
                    2 => BuildPooledField(activeSession, pool, begin, detectedFirstField: false, 0x5678),
                    _ => null
                };
            }

            var engine = new TbcFieldSequenceDecodeEngine(readField: ReadField)
            {
                EnableParallelPayloadWritesForCustomReader = true,
                EnablePayloadWriteOverlapForCustomReader = true,
                CreateTbcOutput = _ => new MemoryStream()
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 2);

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.WrittenFieldCount);
            Assert.Equal(2, pool.RetainedLumaBufferCount);
            Assert.Equal(2, pool.RetainedChromaBufferCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Failed overlapped VHS output releases active and queued pooled buffers")]
    public async Task FailedOverlappedVhsOutputReleasesActiveAndQueuedPooledBuffers()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDirectory = CreateTempDirectory();
        using var writeStarted = new ManualResetEventSlim();
        using var releaseFailure = new ManualResetEventSlim();
        using var secondReadCompleted = new ManualResetEventSlim();
        Task<TbcFieldSequenceDecodeResult>? decodeTask = null;
        try
        {
            string outputBase = Path.Combine(tempDirectory, "pooled-failure");
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
                "--pal",
                "--threads", "2",
                "input.u8",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            int sampleCount = session.TbcFrameSpec.FieldSampleCount;
            var pool = new TbcFieldOutputBufferPool(
                sampleCount,
                sampleCount,
                maximumRetainedBuffers: 4);
            int readCount = 0;
            TbcDecodedField? ReadField(DecodeSession activeSession, Stream _, long begin, int __, int ___)
            {
                int current = Interlocked.Increment(ref readCount);
                if (current > 2)
                {
                    return null;
                }

                TbcDecodedField field = BuildPooledField(
                    activeSession,
                    pool,
                    begin,
                    detectedFirstField: current == 1,
                    sample: current == 1 ? (ushort)0x1234 : (ushort)0x5678);
                if (current == 2)
                {
                    secondReadCompleted.Set();
                }

                return field;
            }

            var failingOutput = new BlockingThrowWriteStream(
                writeStarted,
                releaseFailure,
                cancellationToken);
            var engine = new TbcFieldSequenceDecodeEngine(readField: ReadField)
            {
                EnablePayloadWriteOverlapForCustomReader = true,
                CreateTbcOutput = path => path.EndsWith("_chroma.tbc", StringComparison.Ordinal)
                    ? new MemoryStream()
                    : failingOutput
            };
            decodeTask = Task.Factory.StartNew(
                () => engine.TryDecodeAndWrite(session, Stream.Null, maxFields: 2),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            Assert.True(secondReadCompleted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            releaseFailure.Set();
            TbcFieldSequenceDecodeResult result = await decodeTask.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);

            Assert.False(result.Success);
            Assert.Contains("synthetic pooled output failure", result.Message, StringComparison.Ordinal);
            Assert.Equal(2, pool.RetainedLumaBufferCount);
            Assert.Equal(2, pool.RetainedChromaBufferCount);
        }
        finally
        {
            releaseFailure.Set();
            if (decodeTask is not null)
            {
                try
                {
                    await decodeTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                catch
                {
                    // Preserve the assertion or timeout that ended the test.
                }
            }

            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Overlapped VHS output buffer count stays bounded across 500 fields")]
    public void OverlappedVhsOutputBufferCountStaysBoundedAcrossFiveHundredFields()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "pooled-long-run");
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
                "--pal",
                "--threads", "2",
                "input.u8",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            int sampleCount = session.TbcFrameSpec.FieldSampleCount;
            var pool = new TbcFieldOutputBufferPool(
                sampleCount,
                sampleCount,
                maximumRetainedBuffers: 8);
            var engine = new TbcFieldSequenceDecodeEngine(
                readField: (activeSession, _, begin, _, fieldNumber) => BuildPooledField(
                    activeSession,
                    pool,
                    begin,
                    detectedFirstField: (fieldNumber & 1) == 0,
                    sample: unchecked((ushort)fieldNumber)))
            {
                EnablePayloadWriteOverlapForCustomReader = true,
                CreateTbcOutput = _ => new CountingWriteStream()
            };

            TbcFieldSequenceDecodeResult result = engine.TryDecodeAndWrite(
                session,
                Stream.Null,
                maxFields: 500);

            Assert.True(result.Success, result.Message);
            Assert.Equal(500, result.WrittenFieldCount);
            Assert.InRange(pool.CreatedLumaBufferCount, 1, 4);
            Assert.InRange(pool.CreatedChromaBufferCount, 1, 4);
            Assert.Equal(pool.CreatedLumaBufferCount, pool.RetainedLumaBufferCount);
            Assert.Equal(pool.CreatedChromaBufferCount, pool.RetainedChromaBufferCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Raw decode output sharing matches Python deny-none behavior")]
    public void RawDecodeOutputSharingMatchesPythonDenyNoneBehavior()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(tempDirectory, "shared.tbc");
            using FileStream output = DecodeOutputFile.Create(path);
            output.WriteByte(0x5a);
            output.Flush();

            using FileStream preview = OpenPreview(path);
            using var concurrentWriter = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);

            Assert.Equal(0x5a, preview.ReadByte());
            Assert.True(concurrentWriter.CanWrite);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Active LD raw audio sidecars use Python-compatible sharing")]
    public void ActiveLdRawAudioSidecarsUsePythonCompatibleSharing()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "ld-sidecars");
            using DecodeSession session = CreateLaserDiscSidecarSession(outputBase);
            using ILaserDiscFieldOutputSession output = new LaserDiscEfmOutputWriter().Open(session);

            foreach (string extension in new[] { ".pcm", ".efm", ".prefm" })
            {
                string path = outputBase + extension;
                using FileStream preview = OpenPreview(path);
                using var concurrentWriter = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                Assert.True(preview.CanRead);
                Assert.True(concurrentWriter.CanWrite);
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static DecodeSession CreateVideoSession(string decoder, string outputBase)
    {
        (DecodeCommandSpec Spec, string[] Arguments) command = decoder switch
        {
            "vhs" => (CliSpecs.Vhs, ["--pal", "input.u8", outputBase]),
            "betamax" => (CliSpecs.Vhs, [
                "--pal",
                "--tape_format", "BETAMAX",
                "input.u8",
                outputBase
            ]),
            "cvbs" => (CliSpecs.Cvbs, ["--pal", "input.u8", outputBase]),
            "ld" => (CliSpecs.LaserDisc, [
                "--PAL",
                "--noEFM",
                "--disable_analog_audio",
                "input.s16",
                outputBase
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(decoder), decoder, "Unknown decoder.")
        };
        ParsedCommand parsed = new CommandLineParser().Parse(command.Spec, command.Arguments);
        return DecodeSessionFactory.Create(parsed);
    }

    private static DecodeSession CreateLaserDiscSidecarSession(string outputBase)
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc, [
            "--NTSC",
            "--threads", "0",
            "--preEFM",
            "input.s16",
            outputBase
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static TbcDecodedField BuildField(
        DecodeSession session,
        long startSample,
        bool detectedFirstField,
        ushort sample)
    {
        var samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        Array.Fill(samples, sample);
        ushort[]? chroma = null;
        if (session.ChromaOptions?.WriteChroma == true)
        {
            chroma = new ushort[samples.Length];
            Array.Fill(chroma, checked((ushort)(sample + 1)));
        }

        TbcDecodedField field = new(
            StartSample: startSample,
            Samples: samples,
            LineLocations: new LineLocationResult([], []),
            Timing: new SyncTiming(
                0,
                0,
                0,
                new SyncRange(0, 0),
                new SyncRange(0, 0),
                new SyncRange(0, 0)),
            SyncThresholdHz: 0,
            MeanLineLength: 0,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0,
            DetectedFirstField: detectedFirstField,
            DetectedFirstFieldConfidence: 100,
            ChromaSamples: chroma,
            NextFieldOffsetSamples: 100,
            NominalFieldLengthSamples: 100);
        return field;
    }

    private static TbcDecodedField BuildPooledField(
        DecodeSession session,
        TbcFieldOutputBufferPool pool,
        long startSample,
        bool detectedFirstField,
        ushort sample)
    {
        TbcFieldOutputBufferPool.TbcFieldOutputBufferLease lease = pool.Rent();
        Array.Fill(lease.Luma, sample);
        Array.Fill(lease.Chroma!, checked((ushort)(sample + 1)));
        TbcDecodedField field = new(
            StartSample: startSample,
            Samples: lease.Luma,
            LineLocations: new LineLocationResult([], []),
            Timing: new SyncTiming(
                0,
                0,
                0,
                new SyncRange(0, 0),
                new SyncRange(0, 0),
                new SyncRange(0, 0)),
            SyncThresholdHz: 0,
            MeanLineLength: 0,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0,
            DetectedFirstField: detectedFirstField,
            DetectedFirstFieldConfidence: 100,
            ChromaSamples: lease.Chroma,
            NextFieldOffsetSamples: 100,
            NominalFieldLengthSamples: 100);
        field.AttachOutputBuffers(lease);
        return field;
    }

    private static FileStream OpenPreview(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
    }

    private sealed class BlockingThrowWriteStream(
        ManualResetEventSlim writeStarted,
        ManualResetEventSlim releaseFailure,
        CancellationToken cancellationToken) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            writeStarted.Set();
            if (!releaseFailure.Wait(TimeSpan.FromSeconds(10), cancellationToken))
            {
                throw new TimeoutException("The pooled output failure test did not release the writer.");
            }

            throw new IOException("synthetic pooled output failure");
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));
    }

    private sealed class CountingWriteStream : Stream
    {
        private long _length;
        private long _position;

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long basis = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => _length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = checked(basis + offset);
            return _position;
        }

        public override void SetLength(long value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _length = value;
            if (_position > value)
            {
                _position = value;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _position = checked(_position + buffer.Length);
            _length = Math.Max(_length, _position);
        }
    }

    private static FileStream OpenPreviewEventually(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return OpenPreview(path);
            }
            catch (IOException) when (stopwatch.Elapsed < timeout)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(10));
            }
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CoordinatedWriteStream(Barrier barrier) : MemoryStream
    {
        private int _firstWriteEntered;

        public bool Coordinated { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            CoordinateFirstWrite();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CoordinateFirstWrite();
            base.Write(buffer);
        }

        private void CoordinateFirstWrite()
        {
            if (Interlocked.Exchange(ref _firstWriteEntered, 1) != 0)
            {
                return;
            }

            Coordinated = barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            if (!Coordinated)
            {
                throw new TimeoutException("The luma and chroma payload writes did not overlap.");
            }
        }
    }

    private sealed class BlockingFirstWriteStream(
        ManualResetEventSlim started,
        ManualResetEventSlim release,
        ManualResetEventSlim completed,
        CancellationToken cancellationToken) : MemoryStream
    {
        private int _firstWriteEntered;

        public override void Write(byte[] buffer, int offset, int count)
        {
            BlockFirstWrite();
            base.Write(buffer, offset, count);
            completed.Set();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BlockFirstWrite();
            base.Write(buffer);
            completed.Set();
        }

        private void BlockFirstWrite()
        {
            if (Interlocked.Exchange(ref _firstWriteEntered, 1) != 0)
            {
                return;
            }

            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10), cancellationToken))
            {
                throw new TimeoutException("The payload-overlap test did not release the first payload write.");
            }
        }
    }
}
