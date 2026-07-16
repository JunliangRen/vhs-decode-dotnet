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

        return new TbcDecodedField(
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
    }

    private static FileStream OpenPreview(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
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
}
