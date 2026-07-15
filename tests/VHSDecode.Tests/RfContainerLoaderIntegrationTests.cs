using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class RfContainerLoaderIntegrationTests
{
    private const int SampleCount = 1_200_000;

    private static readonly ReadCase[] ReadCases =
    [
        new(0, 17, "878F4E56A3D57FE599E9430F3C15E99D72F65CBA7EB87445BBA6CF509C7C9D4A"),
        new(1_100_000, 31, "2C217FF2DDC8B903ECFD780057197695D5391993429A46CBAB9DFE57366205BC"),
        new(50_000, 37, "D0D0D5A52DA5A9B0CE01D60DEEE995E35CFAF6EE352811EBF4774F628974A2E0"),
        new(49_990, 45, "A379476E3A2E4D285AAF247FFC4F12F133F0CC8657659329E3569CCB7374D230"),
        new(1_199_970, 30, "BCBA6E111F8441BE50E782DFBCAFB50C31450DD9369F47F71059A03BBD414DCA"),
        new(1_199_980, 30, null)
    ];

    private static readonly ReadCase[] StereoWaveReadCases =
    [
        new(0, 17, "D5424BB3240059EB6A3A9C082B940B0DF9D18A134B3D405FE543FDF947858B50"),
        new(1_100_000, 31, "038FE2851B7350B4BE73503F503FED1F0E26C0379C5927E705880AE5F55DAAEA"),
        new(50_000, 37, "EA94BC1364CA59B0B3738108351AB1C81BAC25F49FE3780D11453BBEF94C2083"),
        new(49_990, 45, "9ABFD8412C2B6BF10F2E6EFA3A8199062A62700B221B7539BDB5E008668F6216"),
        new(1_199_970, 30, "297CC09F77BCEE0642881A23B09255267E4568DE4AEC94D06F4C4C8E529122A3"),
        new(1_199_980, 30, "1EB8778CD7621E0A024878082E7CB61F0F532021590FE15BFCB671C91EF0FCF7")
    ];

    private static readonly ReadCase[] StereoFlacReadCases =
    [
        new(0, 17, "D5424BB3240059EB6A3A9C082B940B0DF9D18A134B3D405FE543FDF947858B50"),
        new(1_100_000, 31, "534ADC4167846C2414935FFC2555AE748C6D8EC5CD47126BA7B262D0272E30D5"),
        new(50_000, 37, "DA37FC6F879CA6BC0ECB7123A71435202D65D6FFF192B7E9D2360CE4A3DB4A39"),
        new(49_990, 45, "16E30A93DE0B31648A1406370F8447CDC56CC6C922B6884E879998B1B728E5CB"),
        new(1_199_970, 30, "23E29C39E51779F2739EDBD9E81923F130F2F98EA4751AEE60C17D073360ED45"),
        new(1_199_980, 30, "FA85C3DFB09566E0C5CD7FA6CFC7E336FE09B93733CD55065B25180FB209C593")
    ];

    private static readonly ReadCase[] StereoWave17900ReadCases =
    [
        new(0, 17, "D5424BB3240059EB6A3A9C082B940B0DF9D18A134B3D405FE543FDF947858B50"),
        new(1_100_000, 31, "57B35DF0DE3484958D6FA688E21ADA6B152FB33B5925B4EBFB36ECBBDBB271D4"),
        new(50_000, 37, "DCB52212393107C0356469AF918A8787367F7D40BF47DC027422FBF4C837584A"),
        new(49_990, 45, "DCCADDB1DE226254D2957D97BCABBC6CADF322A0CF0E585D0D0AD7C0349DBFDF"),
        new(1_199_970, 30, "5B681F493753E9628CF89D19343B9E371F6A9489A5C0B9BECA883CE010B44104"),
        new(1_199_980, 30, "5627D10F78F4BC02E8DB4DA51177FCC255AF34F9BF31441F32D8026EFE019D35")
    ];

    private static readonly ReadCase[] Pcm24WaveReadCases =
    [
        new(0, 17, "878F4E56A3D57FE599E9430F3C15E99D72F65CBA7EB87445BBA6CF509C7C9D4A"),
        new(1_100_000, 31, "5D5256948FB17A3DAB053A4BAB63A38735444256503556A39BFC67098B424A7C"),
        new(50_000, 37, "7995BFCDE1DD859A971CA3DA33F8B65A301C8BE6343FF633006F10E2CC5AE35A"),
        new(49_990, 45, "EC99732D3C83B5A834210E23511BAD4D94F56D06E8A62C82323A745C2D4EB2E8"),
        new(1_199_970, 30, "23E7CE6B376772E500C8F0DF92260D683521F3C34908181AF4870010728C5026"),
        new(1_199_980, 30, "E060A1416A3E29B176281939074BEA5D270C7402F9E04875FF8B63BD9E4D00E9")
    ];

    private static readonly ReadCase[] Pcm24FlacReadCases =
    [
        new(0, 17, "878F4E56A3D57FE599E9430F3C15E99D72F65CBA7EB87445BBA6CF509C7C9D4A"),
        new(1_100_000, 31, "AFD22D79C449ADD4C5EE7AF3B977C8CF44FA13E903E7ECCD91F9C0F054603121"),
        new(50_000, 37, "41E8240DB8FDAD403EBF47751CA7C4344039C084679932F40F13A4BDB89B27F8"),
        new(49_990, 45, "CD900CF23FF73C86A521FAF036DF30A284A58F6CA873575F047CFD4F35CA8EB0"),
        new(1_199_970, 30, "00BE1AA1E0FC5D01AF89E92497087BF18FAF2FA3A2B0DEA1C6D57DF60FD52B19"),
        new(1_199_980, 30, "347903A86072AB2FE31C061E2AFBD0F94C334C81E8E807EEF81E75E39D82DC08")
    ];

    [Fact(DisplayName = "RF WAV FLAC and LDF random access matches Release 4.0")]
    public void RfWaveFlacAndLdfRandomAccessMatchesRelease40()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-container-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            short[] samples = CreateSamples();
            string wavePath = Path.Combine(directory, "RF source with spaces.wav");
            string wave17900Path = Path.Combine(directory, "RF source 17900 Hz.wav");
            string wave24Path = Path.Combine(directory, "RF source 24 bit.wav");
            string flac24Path = Path.Combine(directory, "RF source 24 bit.flac");
            string flacPath = Path.Combine(directory, "RF source with spaces.flac");
            string ldfPath = Path.Combine(directory, "RF source with spaces.ldf");
            WriteWave(wavePath, samples);
            WriteWave(wave17900Path, samples, sampleRate: 17_900);
            WriteWave(wave24Path, samples, bitsPerSample: 24);
            EncodeFlac(wavePath, flacPath, oggContainer: false);
            EncodeFlac(wavePath, ldfPath, oggContainer: true);
            EncodeFlac(wave24Path, flac24Path, oggContainer: false);

            foreach (string path in new[] { wavePath, wave17900Path, flacPath, ldfPath })
            {
                VerifyReads(path, samples);
            }

            VerifyHashes(wave24Path, Pcm24WaveReadCases);
            VerifyHashes(flac24Path, Pcm24FlacReadCases);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "RF stereo WAV downmix and EOF flush matches Release 4.0")]
    public void RfStereoWaveDownmixAndEofFlushMatchesRelease40()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-container-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            short[] samples = CreateSamples();
            string wavePath = Path.Combine(directory, "stereo RF source with spaces.wav");
            string wave17900Path = Path.Combine(directory, "stereo RF source 17900 Hz.wav");
            string flacPath = Path.Combine(directory, "stereo RF source with spaces.flac");
            string ldfPath = Path.Combine(directory, "stereo RF source with spaces.ldf");
            WriteWave(wavePath, samples, stereo: true);
            WriteWave(wave17900Path, samples, stereo: true, sampleRate: 17_900);
            EncodeFlac(wavePath, flacPath, oggContainer: false);
            EncodeFlac(wavePath, ldfPath, oggContainer: true);
            VerifyHashes(wavePath, StereoWaveReadCases);
            VerifyHashes(wave17900Path, StereoWave17900ReadCases);
            VerifyHashes(flacPath, StereoFlacReadCases);
            VerifyHashes(ldfPath, StereoFlacReadCases);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "PyAV plane padding stream preserves frame and seek geometry")]
    public void PyAvPlanePaddingStreamPreservesFrameAndSeekGeometry()
    {
        Assert.Equal(2_080, PyAvAudioPlanePaddingStream.CalculatePaddedFrameSamples(2_048));
        Assert.Equal(4_128, PyAvAudioPlanePaddingStream.CalculatePaddedFrameSamples(4_096));

        byte[] source = Pcm16Bytes([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        using (var padded = new PyAvAudioPlanePaddingStream(
            new MemoryStream(source),
            logicalFrameSamples: 4,
            paddedFrameSamples: 6))
        {
            Assert.Equal(
                Pcm16Bytes([1, 2, 3, 4, 0, 0, 5, 6, 7, 8, 0, 0, 9, 10, 0, 0, 0, 0]),
                ReadToEnd(padded));
        }

        using var skipped = new PyAvAudioPlanePaddingStream(
            new MemoryStream(source),
            logicalFrameSamples: 4,
            paddedFrameSamples: 6,
            initialSkipSamples: 5);
        Assert.Equal(
            Pcm16Bytes([0, 5, 6, 7, 8, 0, 0, 9, 10, 0, 0, 0, 0]),
            ReadToEnd(skipped));
    }

    private static short[] CreateSamples()
    {
        var samples = new short[SampleCount];
        for (ulong index = 0; index < (ulong)samples.Length; index++)
        {
            ulong value = ((index * 1_103_515_245UL) + 12_345UL) >> 8;
            samples[index] = unchecked((short)(ushort)value);
        }

        return samples;
    }

    private static void VerifyReads(string path, short[] source)
    {
        using var loader = new FfmpegPcm16SampleLoader(path);
        using FileStream input = File.OpenRead(path);
        foreach (ReadCase readCase in ReadCases)
        {
            double[]? actual = loader.Read(input, readCase.Start, readCase.Length);
            if (readCase.Sha256 is null)
            {
                Assert.Null(actual);
                continue;
            }

            Assert.NotNull(actual);
            var expected = new double[readCase.Length];
            for (int i = 0; i < expected.Length; i++)
            {
                expected[i] = source[readCase.Start + i];
            }

            Assert.Equal(expected, actual);
            Assert.Equal(readCase.Sha256, Sha256(actual));
        }
    }

    private static void VerifyHashes(string path, IReadOnlyList<ReadCase> readCases)
    {
        using var loader = new FfmpegPcm16SampleLoader(path);
        using FileStream input = File.OpenRead(path);
        foreach (ReadCase readCase in readCases)
        {
            double[]? actual = loader.Read(input, readCase.Start, readCase.Length);
            if (readCase.Sha256 is null)
            {
                Assert.Null(actual);
                continue;
            }

            Assert.NotNull(actual);
            Assert.Equal(readCase.Sha256, Sha256(actual));
        }
    }

    private static string Sha256(double[] samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(short))];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(i * sizeof(short), sizeof(short)),
                checked((short)samples[i]));
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static byte[] Pcm16Bytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[checked(samples.Length * sizeof(short))];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(i * sizeof(short), sizeof(short)),
                samples[i]);
        }

        return bytes;
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteWave(
        string path,
        short[] samples,
        bool stereo = false,
        int sampleRate = FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz,
        int bitsPerSample = 16)
    {
        int channelCount = stereo ? 2 : 1;
        int bytesPerSample = bitsPerSample / 8;
        int dataLength = checked(samples.Length * bytesPerSample * channelCount);
        using var output = new BinaryWriter(File.Create(path), Encoding.ASCII, leaveOpen: false);
        output.Write("RIFF"u8);
        output.Write(checked(36 + dataLength));
        output.Write("WAVE"u8);
        output.Write("fmt "u8);
        output.Write(16);
        output.Write((short)1);
        output.Write((short)channelCount);
        output.Write(sampleRate);
        output.Write(sampleRate * bytesPerSample * channelCount);
        output.Write((short)(bytesPerSample * channelCount));
        output.Write((short)bitsPerSample);
        output.Write("data"u8);
        output.Write(dataLength);
        for (int i = 0; i < samples.Length; i++)
        {
            WritePcmSample(output, samples[i], bitsPerSample);
            if (stereo)
            {
                int rightIndex = (i - 17 + samples.Length) % samples.Length;
                WritePcmSample(output, samples[rightIndex], bitsPerSample);
            }
        }
    }

    private static void WritePcmSample(BinaryWriter output, short sample, int bitsPerSample)
    {
        if (bitsPerSample == 16)
        {
            output.Write(sample);
            return;
        }

        if (bitsPerSample != 24)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        }

        int value = sample << 8;
        output.Write((byte)value);
        output.Write((byte)(value >> 8));
        output.Write((byte)(value >> 16));
    }

    private static void EncodeFlac(string inputPath, string outputPath, bool oggContainer)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            inputPath,
            "-c:a",
            "flac"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (oggContainer)
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("ogg");
            startInfo.ArgumentList.Add("-compression_level");
            startInfo.ArgumentList.Add("6");
        }

        startInfo.ArgumentList.Add(outputPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode == 0,
            $"ffmpeg exited with {process.ExitCode}: {standardError.Result}");
    }

    private static bool CommandIsAvailable(string command)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-version");
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private sealed record ReadCase(int Start, int Length, string? Sha256);
}
