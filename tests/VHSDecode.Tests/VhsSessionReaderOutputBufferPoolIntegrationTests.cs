using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsSessionReaderOutputBufferPoolIntegrationTests
{
    [Theory(DisplayName = "Staged VHS sequence output matches eager fallback and saved-level decoding")]
    [InlineData("v0.4.0")]
    [InlineData("current")]
    public void StagedVhsSequenceOutputMatchesEagerFallbackAndSavedLevelDecoding(
        string compatibility)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string eagerOutput = Path.Combine(tempDirectory, "eager");
            string stagedOutput = Path.Combine(tempDirectory, "staged");
            using DecodeSession eagerSession = CreateSession(
                eagerOutput,
                compatibility,
                threads: 1);
            byte[] inputBytes = BuildPalVhsRf(eagerSession);
            using DecodeSession stagedSession = CreateSession(
                stagedOutput,
                compatibility,
                threads: 20);
            Action<string, string>? stagedFieldLogger = stagedSession.TbcFieldDecoder.DiagnosticLogger;
            Action<string, string>? stagedRenderLogger = stagedSession.TbcRenderer.DiagnosticLogger;
            using var eagerInput = new MemoryStream(inputBytes, writable: false);
            using var stagedInput = new MemoryStream(inputBytes, writable: false);

            TbcFieldSequenceDecodeResult eager = new TbcFieldSequenceDecodeEngine()
                .TryDecodeAndWrite(eagerSession, eagerInput, maxFields: 2);
            TbcFieldSequenceDecodeResult staged = new TbcFieldSequenceDecodeEngine()
                .TryDecodeAndWrite(stagedSession, stagedInput, maxFields: 2);

            Assert.True(eager.Success, eager.Message);
            Assert.True(staged.Success, staged.Message);
            Assert.Equal(2, eager.WrittenFieldCount);
            Assert.Equal(eager.WrittenFieldCount, staged.WrittenFieldCount);
            Assert.Equal(
                File.ReadAllBytes(eagerOutput + ".tbc"),
                File.ReadAllBytes(stagedOutput + ".tbc"));
            Assert.Equal(
                File.ReadAllBytes(eagerOutput + "_chroma.tbc"),
                File.ReadAllBytes(stagedOutput + "_chroma.tbc"));
            Assert.Equal(
                File.ReadAllText(eagerOutput + ".tbc.json"),
                File.ReadAllText(stagedOutput + ".tbc.json"));
            Assert.Equal(
                NormalizeLog(File.ReadAllText(eagerOutput + ".log")),
                NormalizeLog(File.ReadAllText(stagedOutput + ".log")));
            AssertWavefrontWorkspacesReturned(
                stagedSession,
                expectedActive: compatibility == "v0.4.0");
            Assert.Same(stagedFieldLogger, stagedSession.TbcFieldDecoder.DiagnosticLogger);
            Assert.Same(stagedRenderLogger, stagedSession.TbcRenderer.DiagnosticLogger);

            using TbcFieldDecodePipeline exactFromSession =
                TbcFieldDecodePipeline.FromSession(stagedSession);
            Assert.Equal(
                compatibility == "v0.4.0",
                exactFromSession.CanUseVhsWavefront);
            if (OperatingSystem.IsWindows())
            {
                _ = IppRuntime.ProbeRequired();
                using DecodeSession ippSession = CreateSession(
                    stagedOutput + "-ipp-profile",
                    compatibility,
                    threads: 20,
                    dspBackend: "ipp-fast");
                using TbcFieldDecodePipeline ippFromSession =
                    TbcFieldDecodePipeline.FromSession(ippSession);
                Assert.True(ippFromSession.CanUseVhsWavefront);
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = "Wavefront VHS terminal lookahead matches serial output and diagnostics")]
    [InlineData("v0.4.0")]
    [InlineData("current")]
    public void WavefrontVhsTerminalLookaheadMatchesSerialOutputAndDiagnostics(
        string compatibility)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string serialOutput = Path.Combine(tempDirectory, "serial-terminal");
            string wavefrontOutput = Path.Combine(tempDirectory, "wavefront-terminal");
            using DecodeSession serialSession = CreateSession(
                serialOutput,
                compatibility,
                threads: 1,
                requestedFields: 2);
            byte[] inputBytes = BuildPalVhsRf(serialSession);
            using DecodeSession wavefrontSession = CreateSession(
                wavefrontOutput,
                compatibility,
                threads: 2,
                requestedFields: 2);
            using var serialInput = new MemoryStream(inputBytes, writable: false);
            using var wavefrontInput = new MemoryStream(inputBytes, writable: false);

            TbcFieldSequenceDecodeResult serial = new TbcFieldSequenceDecodeEngine()
                .TryDecodeAndWrite(serialSession, serialInput);
            TbcFieldSequenceDecodeResult wavefront = new TbcFieldSequenceDecodeEngine()
                .TryDecodeAndWrite(wavefrontSession, wavefrontInput);

            Assert.True(serial.Success, serial.Message);
            Assert.True(wavefront.Success, wavefront.Message);
            Assert.Equal(2, serial.WrittenFieldCount);
            Assert.Equal(serial.WrittenFieldCount, wavefront.WrittenFieldCount);
            Assert.Equal(
                File.ReadAllBytes(serialOutput + ".tbc"),
                File.ReadAllBytes(wavefrontOutput + ".tbc"));
            Assert.Equal(
                File.ReadAllBytes(serialOutput + "_chroma.tbc"),
                File.ReadAllBytes(wavefrontOutput + "_chroma.tbc"));
            Assert.Equal(
                File.ReadAllText(serialOutput + ".tbc.json"),
                File.ReadAllText(wavefrontOutput + ".tbc.json"));
            Assert.Equal(
                NormalizeLog(File.ReadAllText(serialOutput + ".log")),
                NormalizeLog(File.ReadAllText(wavefrontOutput + ".log")));
            AssertWavefrontWorkspacesReturned(
                wavefrontSession,
                expectedActive: compatibility == "v0.4.0");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = "Wavefront defers terminal failure until prior VHS output commits")]
    [InlineData("v0.4.0")]
    [InlineData("current")]
    public void WavefrontDefersTerminalRecoveryUntilPriorVhsOutputCommits(
        string compatibility)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string serialOutput = Path.Combine(tempDirectory, "serial-recovery");
            string wavefrontOutput = Path.Combine(tempDirectory, "wavefront-recovery");
            using DecodeSession serialSession = CreateSession(
                serialOutput,
                compatibility,
                threads: 1,
                requestedFields: 1);
            byte[] inputBytes = BuildPalVhsRf(
                serialSession,
                paintSecondField: false,
                paintTerminalLookahead: false);
            using DecodeSession wavefrontSession = CreateSession(
                wavefrontOutput,
                compatibility,
                threads: 2,
                requestedFields: 1);
            using var serialInput = new MemoryStream(inputBytes, writable: false);
            using var wavefrontInput = new MemoryStream(inputBytes, writable: false);

            TbcFieldSequenceDecodeResult serial = new TbcFieldSequenceDecodeEngine()
                .TryDecodeAndWrite(serialSession, serialInput);
            TbcFieldSequenceDecodeResult wavefront = new TbcFieldSequenceDecodeEngine()
                .TryDecodeAndWrite(wavefrontSession, wavefrontInput);

            Assert.True(serial.Success, serial.Message);
            Assert.True(wavefront.Success, wavefront.Message);
            Assert.True(serial.WrittenFieldCount >= 1);
            Assert.Equal(serial.WrittenFieldCount, wavefront.WrittenFieldCount);
            Assert.Equal(
                File.ReadAllBytes(serialOutput + ".tbc"),
                File.ReadAllBytes(wavefrontOutput + ".tbc"));
            Assert.Equal(
                File.ReadAllBytes(serialOutput + "_chroma.tbc"),
                File.ReadAllBytes(wavefrontOutput + "_chroma.tbc"));
            Assert.Equal(
                File.ReadAllText(serialOutput + ".tbc.json"),
                File.ReadAllText(wavefrontOutput + ".tbc.json"));
            string serialLog = NormalizeLog(File.ReadAllText(serialOutput + ".log"));
            string wavefrontLog = NormalizeLog(File.ReadAllText(wavefrontOutput + ".log"));
            Assert.Equal(serialLog, wavefrontLog);
            AssertWavefrontWorkspacesReturned(
                wavefrontSession,
                expectedActive: compatibility == "v0.4.0");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = "VHS session reader pools and returns decoded field output")]
    [InlineData(true)]
    [InlineData(false)]
    public void VhsSessionReaderPoolsAndReturnsDecodedFieldOutput(bool skipChroma)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "session-reader");
            List<string> arguments =
            [
                "--pal",
                "--frequency", "40",
                "--no_resample",
                "--fallback_vsync",
                "--relaxed_line0",
                "--threads", "2"
            ];
            if (skipChroma)
            {
                arguments.Add("--skip_chroma");
            }

            arguments.Add("input.s16");
            arguments.Add(outputBase);
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, arguments);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            Assert.True(session.StreamDecoder.WorkerThreads > 1);
            using var input = new MemoryStream(BuildPalVhsRf(session));

            TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine()
                .TryDecodeAndWrite(session, input, maxFields: 1);

            Assert.True(result.Success, result.Message);
            string log = File.Exists(outputBase + ".log")
                ? File.ReadAllText(outputBase + ".log")
                : "<no log>";
            Assert.True(
                result.WrittenFieldCount == 1,
                $"{result.Message}; created={session.TbcFieldDecoder.CreatedFieldOutputLumaBufferCount}; log={log}");
            Assert.InRange(session.TbcFieldDecoder.CreatedFieldOutputLumaBufferCount, 1, 4);
            Assert.Equal(
                session.TbcFieldDecoder.CreatedFieldOutputLumaBufferCount,
                session.TbcFieldDecoder.RetainedFieldOutputLumaBufferCount);
            Assert.Equal(
                session.TbcFrameSpec.FieldSampleCount * sizeof(ushort),
                new FileInfo(outputBase + ".tbc").Length);
            if (skipChroma)
            {
                Assert.Equal(0, session.TbcFieldDecoder.CreatedFieldOutputChromaBufferCount);
                Assert.Equal(0, session.TbcFieldDecoder.RetainedFieldOutputChromaBufferCount);
                Assert.False(File.Exists(outputBase + "_chroma.tbc"));
                Assert.Equal(0, session.TbcFieldDecoder.CreatedVhsWavefrontChromaWorkspaceCount);
                Assert.Equal(0, session.TbcFieldDecoder.CreatedVhsWavefrontVideoWorkspaceCount);
            }
            else
            {
                Assert.InRange(session.TbcFieldDecoder.CreatedFieldOutputChromaBufferCount, 1, 4);
                Assert.Equal(
                    session.TbcFieldDecoder.CreatedFieldOutputChromaBufferCount,
                    session.TbcFieldDecoder.RetainedFieldOutputChromaBufferCount);
                Assert.Equal(
                    session.TbcFrameSpec.FieldSampleCount * sizeof(ushort),
                    new FileInfo(outputBase + "_chroma.tbc").Length);
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static byte[] BuildPalVhsRf(
        DecodeSession session,
        bool paintSecondField = true,
        bool paintTerminalLookahead = true)
    {
        const int LineSamples = 2_560;
        const int FieldSamples = 800_000;
        const int FirstFieldStart = 20_000;
        const int SampleCount = 2_700_000;
        var ire = new double[SampleCount];
        PaintField(ire, FirstFieldStart, isFirstField: false);
        if (paintSecondField)
        {
            PaintField(ire, FirstFieldStart + FieldSamples, isFirstField: true);
        }

        if (paintSecondField && paintTerminalLookahead)
        {
            PaintField(ire, FirstFieldStart + (2 * FieldSamples), isFirstField: false);
        }

        var samples = new short[SampleCount];
        double phase = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            double frequencyHz = session.VideoOutput.IreToHz(ire[i]);
            phase += Math.Tau * frequencyHz / session.DecodeSampleRateHz;
            samples[i] = (short)Math.Round(12_000.0 * Math.Cos(phase));
        }

        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;

        static void PaintField(double[] output, int line0, bool isFirstField)
        {
            const int HalfLineSamples = LineSamples / 2;
            const int HSyncSamples = 188;
            const int EqualizingSamples = 94;
            const int VSyncSamples = 1_080;
            const int NumPulses = 5;
            int firstHSyncHalfLines = isFirstField ? 1 : 2;
            int secondEqualizingHalfLines = isFirstField ? 1 : 2;

            for (int line = -2; line < 313; line++)
            {
                PaintPulse(output, line0 + (line * LineSamples), HSyncSamples);
            }

            int equalizing1Start = line0 + (firstHSyncHalfLines * HalfLineSamples);
            for (int pulse = 0; pulse < NumPulses; pulse++)
            {
                PaintPulse(output, equalizing1Start + (pulse * HalfLineSamples), EqualizingSamples);
            }

            int vSyncStart = equalizing1Start + (NumPulses * HalfLineSamples);
            for (int pulse = 0; pulse < NumPulses; pulse++)
            {
                PaintPulse(output, vSyncStart + (pulse * HalfLineSamples), VSyncSamples);
            }

            int equalizing2Start = vSyncStart + (NumPulses * HalfLineSamples);
            for (int pulse = 0; pulse < NumPulses; pulse++)
            {
                PaintPulse(output, equalizing2Start + (pulse * HalfLineSamples), EqualizingSamples);
            }

            int followingHSync = equalizing2Start
                + ((NumPulses - 1 + secondEqualizingHalfLines) * HalfLineSamples);
            PaintPulse(output, followingHSync, HSyncSamples);
        }

        static void PaintPulse(double[] output, int start, int length)
        {
            int first = Math.Max(0, start);
            int end = Math.Min(output.Length, start + length);
            for (int i = first; i < end; i++)
            {
                output[i] = -40.0;
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

    private static void AssertWavefrontWorkspacesReturned(
        DecodeSession session,
        bool expectedActive)
    {
        if (!expectedActive)
        {
            Assert.Equal(0, session.TbcFieldDecoder.CreatedVhsWavefrontChromaWorkspaceCount);
            Assert.Equal(0, session.TbcFieldDecoder.RetainedVhsWavefrontChromaWorkspaceCount);
            Assert.Equal(0, session.TbcFieldDecoder.CreatedVhsWavefrontVideoWorkspaceCount);
            Assert.Equal(0, session.TbcFieldDecoder.RetainedVhsWavefrontVideoWorkspaceCount);
            return;
        }

        Assert.InRange(
            session.TbcFieldDecoder.CreatedVhsWavefrontChromaWorkspaceCount,
            1,
            2);
        Assert.Equal(
            session.TbcFieldDecoder.CreatedVhsWavefrontChromaWorkspaceCount,
            session.TbcFieldDecoder.RetainedVhsWavefrontChromaWorkspaceCount);
        Assert.InRange(
            session.TbcFieldDecoder.CreatedVhsWavefrontVideoWorkspaceCount,
            1,
            2);
        Assert.Equal(
            session.TbcFieldDecoder.CreatedVhsWavefrontVideoWorkspaceCount,
            session.TbcFieldDecoder.RetainedVhsWavefrontVideoWorkspaceCount);
    }

    private static DecodeSession CreateSession(
        string outputBase,
        string compatibility,
        int threads,
        int? requestedFields = null,
        string? dspBackend = null)
    {
        List<string> arguments =
        [
            "--pal",
            "--frequency", "40",
            "--no_resample",
            "--fallback_vsync",
            "--relaxed_line0",
            "--use_saved_levels",
            "--clamp",
            "--ire0_adjust",
            "--compat-version", compatibility,
            "--threads", threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        if (dspBackend is not null)
        {
            arguments.Add("--dsp-backend");
            arguments.Add(dspBackend);
        }

        if (requestedFields.HasValue)
        {
            arguments.Add("--length");
            arguments.Add(requestedFields.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add("input.s16");
        arguments.Add(outputBase);
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, arguments);
        return DecodeSessionFactory.Create(command);
    }

    private static string NormalizeLog(string value)
        => System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3} - ",
            string.Empty);
}
