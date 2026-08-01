using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsSessionReaderOutputBufferPoolIntegrationTests
{
    [Fact(DisplayName = "VHS session reader pools and returns decoded luma output")]
    public void VhsSessionReaderPoolsAndReturnsDecodedLumaOutput()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "session-reader");
            ParsedCommand command = new CommandLineParser().Parse(
                CliSpecs.Vhs,
                [
                    "--pal",
                    "--frequency", "40",
                    "--no_resample",
                    "--skip_chroma",
                    "--fallback_vsync",
                    "--relaxed_line0",
                    "--threads", "2",
                    "input.s16",
                    outputBase
                ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            Assert.True(session.ExecutionOptions.WorkerThreads > 1);
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
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static byte[] BuildPalVhsRf(DecodeSession session)
    {
        const int LineSamples = 2_560;
        const int FieldSamples = 800_000;
        const int FirstFieldStart = 20_000;
        const int SampleCount = 2_700_000;
        var ire = new double[SampleCount];
        PaintField(ire, FirstFieldStart, isFirstField: false);
        PaintField(ire, FirstFieldStart + FieldSamples, isFirstField: true);
        PaintField(ire, FirstFieldStart + (2 * FieldSamples), isFirstField: false);

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
}
