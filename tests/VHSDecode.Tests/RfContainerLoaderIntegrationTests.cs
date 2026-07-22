using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Fact(DisplayName = "PyAV plane padding follows variable frame geometry and first-frame PTS")]
    public void PyAvPlanePaddingFollowsVariableFrameGeometryAndFirstFramePts()
    {
        short[] source = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        int[] frameLengths = [3, 5, 4];
        short[] expected = BuildPaddedSamples(source, frameLengths);

        using (PyAvAudioPlanePaddingStream stream = CreateVariablePaddingStream(source, 1_002))
        {
            Assert.Equal(Pcm16Bytes(expected[2..]), ReadToEnd(stream));
        }

        using (PyAvAudioPlanePaddingStream stream = CreateVariablePaddingStream(source, 1_066))
        {
            Assert.Equal(Pcm16Bytes(expected[66..]), ReadToEnd(stream));
        }

        using PyAvAudioPlanePaddingStream beforeFirstPts = CreateVariablePaddingStream(source, 999);
        Assert.Equal(Pcm16Bytes(expected), ReadToEnd(beforeFirstPts));
    }

    [Fact(DisplayName = "PyAV frame timing can preserve logical mono s16 frame lengths")]
    public void PyAvFrameTimingCanPreserveLogicalMonoS16FrameLengths()
    {
        short[] source = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var geometries = new Queue<PyAvAudioFrameGeometry>(
        [
            new PyAvAudioFrameGeometry(3, 1_000),
            new PyAvAudioFrameGeometry(5, 1_064),
            new PyAvAudioFrameGeometry(4, 1_128)
        ]);
        using var stream = new PyAvAudioPlanePaddingStream(
            new MemoryStream(Pcm16Bytes(source)),
            () => geometries.TryDequeue(out PyAvAudioFrameGeometry geometry) ? geometry : null,
            targetSample: 1_002,
            preservePlanePadding: false);

        Assert.Equal(Pcm16Bytes(source.AsSpan(2)), ReadToEnd(stream));
    }

    [Fact(DisplayName = "PyAV plane padding retains high-water capacity and recycled tails")]
    public void PyAvPlanePaddingRetainsHighWaterCapacityAndRecycledTails()
    {
        short[] source = Enumerable.Range(1, 76).Select(value => (short)value).ToArray();
        var geometries = new Queue<PyAvAudioFrameGeometry>(
        [
            new PyAvAudioFrameGeometry(3, 0),
            new PyAvAudioFrameGeometry(65, 3_000),
            new PyAvAudioFrameGeometry(4, 68_000),
            new PyAvAudioFrameGeometry(4, 72_000)
        ]);
        using var stream = new PyAvAudioPlanePaddingStream(
            new MemoryStream(Pcm16Bytes(source)),
            () => geometries.TryDequeue(out PyAvAudioFrameGeometry geometry) ? geometry : null,
            targetSample: 0);

        byte[] output = ReadToEnd(stream);
        Assert.Equal((64 + 128 + 128 + 128) * sizeof(short), output.Length);
        Assert.Equal(Pcm16Bytes(source.AsSpan(0, 3)), output.AsSpan(0, 3 * sizeof(short)).ToArray());
        Assert.Equal(Pcm16Bytes(source.AsSpan(3, 65)), output.AsSpan(64 * sizeof(short), 65 * sizeof(short)).ToArray());
        Assert.Equal(Pcm16Bytes(source.AsSpan(68, 4)), output.AsSpan(192 * sizeof(short), 4 * sizeof(short)).ToArray());
        Assert.Equal(Pcm16Bytes(source.AsSpan(72, 4)), output.AsSpan(320 * sizeof(short), 4 * sizeof(short)).ToArray());
        Assert.All(output.AsSpan(324 * sizeof(short), 32 * sizeof(short)).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(Pcm16Bytes(source.AsSpan(39, 29)), output.AsSpan(356 * sizeof(short), 29 * sizeof(short)).ToArray());
        Assert.All(output.AsSpan(3 * sizeof(short), (64 - 3) * sizeof(short)).ToArray(), value => Assert.Equal(0, value));
        Assert.All(output.AsSpan(129 * sizeof(short), (192 - 129) * sizeof(short)).ToArray(), value => Assert.Equal(0, value));
        Assert.All(output.AsSpan(196 * sizeof(short), (320 - 196) * sizeof(short)).ToArray(), value => Assert.Equal(0, value));
        Assert.All(output.AsSpan(385 * sizeof(short)).ToArray(), value => Assert.Equal(0, value));
    }

    [Fact(DisplayName = "PyAV framed FFmpeg metadata and seek arguments match Release 4.0")]
    public void PyAvFramedFfmpegMetadataAndSeekArgumentsMatchRelease40()
    {
        IReadOnlyList<string> noSeek = FfmpegPcm16SampleLoader.BuildPyAvFramedFfmpegArguments(
            "RF source.vhs",
            5_000_000,
            40_000);
        Assert.DoesNotContain("-ss", noSeek);

        IReadOnlyList<string> seek = FfmpegPcm16SampleLoader.BuildPyAvFramedFfmpegArguments(
            "RF source.vhs",
            80_000_000,
            40_000);
        Assert.Contains(
            "-copyts -ss 0.04 -noaccurate_seek -seek_timestamp 1 -seek2any 1 -i RF source.vhs",
            string.Join(' ', seek),
            StringComparison.Ordinal);

        const string line = "[Parsed_ashowinfo_0 @ 000001] [info] "
            + "n:0 pts:1105 pts_time:0.0250567 fmt:s16 channels:1 "
            + "chlayout:mono rate:44100 nb_samples:47 checksum:00000000";
        Assert.True(FfmpegPcm16SampleLoader.TryParsePyAvAudioFrameGeometry(
            line,
            out PyAvAudioFrameGeometry geometry));
        Assert.Equal(47, geometry.LogicalSamples);
        Assert.Equal(1_105_000, geometry.PresentationRfSample);

        const string noPtsLine = "[Parsed_ashowinfo_0 @ 000001] [info] "
            + "n:0 pts:NOPTS pts_time:NOPTS fmt:s16 channels:1 "
            + "chlayout:mono rate:40000 nb_samples:2848 checksum:00000000";
        Assert.True(FfmpegPcm16SampleLoader.TryParsePyAvAudioFrameGeometry(
            noPtsLine,
            out PyAvAudioFrameGeometry noPtsGeometry));
        Assert.Equal(2_848, noPtsGeometry.LogicalSamples);
        Assert.Null(noPtsGeometry.PresentationRfSample);
        Assert.False(FfmpegPcm16SampleLoader.TryParsePyAvAudioFrameGeometry(
            "[info] unrelated FFmpeg output",
            out _));
    }

    [Fact(DisplayName = "PyAV plane padding rejects truncated reported frames")]
    public void PyAvPlanePaddingRejectsTruncatedReportedFrames()
    {
        var geometries = new Queue<PyAvAudioFrameGeometry>(
            [new PyAvAudioFrameGeometry(4, 0)]);
        using var stream = new PyAvAudioPlanePaddingStream(
            new MemoryStream(Pcm16Bytes([1, 2, 3])),
            () => geometries.TryDequeue(out PyAvAudioFrameGeometry geometry) ? geometry : null,
            targetSample: 0);

        Assert.Throws<InvalidDataException>(() => ReadToEnd(stream));
    }

    [Theory(DisplayName = "IMA WAV 2-5 bit mono and stereo decode matches FFmpeg 8.1.2")]
    [InlineData(2, 1, "1C187B39D2F739A1A9EC7ED422B748D7B4092548F74BE11009B74F24EB787BF2")]
    [InlineData(2, 2, "743E1496FBE4D95207FA449C9BE397B543B9A34B78954AF004DD7376A39004B7")]
    [InlineData(3, 1, "435D63C182A1A8F4D4C6F1F6CEBFA196DD68B1465F545DED5FD499E3CC5C0623")]
    [InlineData(3, 2, "96C9AB78BF22300A19868C66BCDD284807C27E784450F0E64E8510368B8D0C84")]
    [InlineData(4, 1, "9E25D04FB335E702344CC3519AD533411C0C570880A94C7A6A3A92A5A547C369")]
    [InlineData(4, 2, "11A5FE1382B7FD2C3DACDEF2366F9D1CD78DCEFB45BA3C797B72751750FE6DF2")]
    [InlineData(5, 1, "30A3EEF5E40B072E279BE2EBE1361399C02C86B31C7D6616A3082907240CE102")]
    [InlineData(5, 2, "27015F066F78E1407A339DFC49A217401F51F26C64352E1721C618402FC08896")]
    public void ImaWavReferenceDecoderMatchesFfmpeg812(
        int bitsPerSample,
        int channelCount,
        string expectedSha256)
    {
        string directory = CreateTestDirectory();
        try
        {
            string path = Path.Combine(directory, $"IMA {bitsPerSample} bit {channelCount} channel.wav");
            WriteImaWave(path, bitsPerSample, channelCount);

            Assert.True(ImaWavPcm16Stream.TryOpen(path, out ImaWavPcm16Stream? opened));
            using ImaWavPcm16Stream decoder = Assert.IsType<ImaWavPcm16Stream>(opened);
            using var output = new MemoryStream();
            var frameSamples = new List<int>();
            long logicalSamplePosition = 0;
            while (decoder.ReadNextFrameGeometry() is { } geometry)
            {
                Assert.Equal(logicalSamplePosition * 1000, geometry.PresentationRfSample);
                frameSamples.Add(geometry.LogicalSamples);
                byte[] frame = new byte[checked(geometry.LogicalSamples * sizeof(short))];
                decoder.ReadExactly(frame);
                output.Write(frame);
                logicalSamplePosition += geometry.LogicalSamples;
            }

            int fullFrameSamples = bitsPerSample == 4 ? 25 : bitsPerSample == 2 ? 49 : 97;
            int shortFrameSamples = bitsPerSample == 4 ? 9 : bitsPerSample == 2 ? 17 : 33;
            Assert.Equal([fullFrameSamples, fullFrameSamples, shortFrameSamples], frameSamples);
            Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(output.ToArray())));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "IMA WAV non-48 kHz input remains on the FFmpeg resampling path")]
    public void ImaWavReferenceDecoderDefersNon48KhzInputToFfmpeg()
    {
        string directory = CreateTestDirectory();
        try
        {
            string path = Path.Combine(directory, "IMA 44.1 kHz.wav");
            WriteImaWave(path, bitsPerSample: 4, channelCount: 1, sampleRateHz: 44_100);

            Assert.False(ImaWavPcm16Stream.TryOpen(path, out ImaWavPcm16Stream? opened));
            Assert.Null(opened);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "IMA WAV loader preserves PyAV plane padding and random reads")]
    public void ImaWavLoaderPreservesPyAvPlanePaddingAndRandomReads()
    {
        string directory = CreateTestDirectory();
        try
        {
            string path = Path.Combine(directory, "IMA 4 bit padded.wav");
            WriteImaWave(path, bitsPerSample: 4, channelCount: 1);
            using var loader = new FfmpegPcm16SampleLoader(path);
            using FileStream input = File.OpenRead(path);
            double[] all = Assert.IsType<double[]>(loader.Read(input, 0, 192));
            Assert.Equal(
                "F696382542B2A05CF500F2DBE48BFB7C7AAE72D127290974C4823C66A6790D74",
                Sha256(all));
            Assert.Null(loader.Read(input, 184, 16));

            using var offsetLoader = new FfmpegPcm16SampleLoader(path);
            using FileStream offsetInput = File.OpenRead(path);
            Assert.Equal(
                all.AsSpan(130, 32).ToArray(),
                offsetLoader.Read(offsetInput, 130, 32));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "RF ALAC variable terminal frame and EOF match Release 4.0")]
    public void RfAlacVariableTerminalFrameAndEofMatchRelease40()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        string directory = CreateTestDirectory();
        try
        {
            short[] source = CreateSamples(52_000);
            string wavePath = Path.Combine(directory, "variable ALAC source.wav");
            string alacPath = Path.Combine(directory, "variable ALAC frames.m4a");
            WriteWave(wavePath, source);
            EncodeAlac(wavePath, alacPath);

            int[] frameLengths = ProbeAudioFrameLengths(alacPath);
            Assert.True(frameLengths.Length > 1);
            Assert.True(frameLengths[^1] < frameLengths[0]);
            short[] expected = BuildPaddedSamples(source, frameLengths);

            using var loader = new FfmpegPcm16SampleLoader(alacPath);
            using FileStream input = File.OpenRead(alacPath);
            Assert.Equal(
                expected.Select(value => (double)value),
                loader.Read(input, 0, expected.Length));
            Assert.Null(loader.Read(input, expected.Length - 8, 16));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "RF Vorbis resampler high-water frames match Release 4.0")]
    public void RfVorbisResamplerHighWaterFramesMatchRelease40()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        string directory = CreateTestDirectory();
        try
        {
            short[] source = CreateSamples(200_000);
            string wavePath = Path.Combine(directory, "Vorbis source.wav");
            string vorbisPath = Path.Combine(directory, "variable Vorbis frames.vhs");
            WriteWave(wavePath, source);
            EncodeVorbis(wavePath, vorbisPath);

            int[] frameLengths = ProbeAudioFrameLengths(vorbisPath);
            int largestFrameIndex = Array.IndexOf(frameLengths, frameLengths.Max());
            Assert.InRange(largestFrameIndex, 0, frameLengths.Length - 2);
            Assert.Contains(frameLengths[(largestFrameIndex + 1)..], length => length < frameLengths[largestFrameIndex]);
            short[] decoded = DecodePcm16(vorbisPath);
            short[] expected = BuildPaddedSamples(decoded, frameLengths);

            using var loader = new FfmpegPcm16SampleLoader(vorbisPath);
            using FileStream input = File.OpenRead(vorbisPath);
            Assert.Equal(
                expected.Select(value => (double)value),
                loader.Read(input, 0, expected.Length));
            Assert.Null(loader.Read(input, expected.Length - 8, 16));

            using var offsetLoader = new FfmpegPcm16SampleLoader(vorbisPath);
            using FileStream offsetInput = File.OpenRead(vorbisPath);
            Assert.Equal(
                expected.AsSpan(50_000, 64).ToArray().Select(value => (double)value),
                offsetLoader.Read(offsetInput, 50_000, 64));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "RF MP3 variable frames and first PTS match Release 4.0")]
    public void RfMp3VariableFramesAndFirstPtsMatchRelease40()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        string directory = CreateTestDirectory();
        try
        {
            short[] source = CreateSamples(57_330);
            string wavePath = Path.Combine(directory, "variable MP3 source.wav");
            string mp3Path = Path.Combine(directory, "variable MP3 frames.vhs");
            WriteWave(wavePath, source, sampleRate: 44_100);
            EncodeMp3(wavePath, mp3Path);

            int[] frameLengths = ProbeAudioFrameLengths(mp3Path);
            Assert.True(frameLengths.Length > 1);
            int firstFrameLength = frameLengths[0];
            Assert.True(frameLengths[1] > firstFrameLength + 16);
            short[] decoded = DecodePcm16(mp3Path);
            int paddedFirstFrameLength = PyAvAudioPlanePaddingStream.CalculatePaddedFrameSamples(
                firstFrameLength);
            int comparisonOffset = paddedFirstFrameLength + firstFrameLength;

            using (var loader = new FfmpegPcm16SampleLoader(mp3Path))
            using (FileStream input = File.OpenRead(mp3Path))
            {
                double[] actual = Assert.IsType<double[]>(loader.Read(
                    input,
                    0,
                    comparisonOffset + 16));
                Assert.Equal(
                    decoded
                        .AsSpan(firstFrameLength * 2, 16)
                        .ToArray()
                        .Select(value => (double)value),
                    actual.AsSpan(comparisonOffset, 16).ToArray());
            }

            using var zeroLoader = new FfmpegPcm16SampleLoader(mp3Path);
            using var offsetLoader = new FfmpegPcm16SampleLoader(mp3Path);
            using FileStream zeroInput = File.OpenRead(mp3Path);
            using FileStream offsetInput = File.OpenRead(mp3Path);
            Assert.Equal(
                zeroLoader.Read(zeroInput, 0, 32),
                offsetLoader.Read(offsetInput, 40, 32));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "RF AAC and float WAV frame geometry match Release 4.0")]
    public void RfAacAndFloatWaveFrameGeometryMatchRelease40()
    {
        Assert.SkipUnless(
            CommandIsAvailable("ffmpeg") && CommandIsAvailable("ffprobe"),
            "ffmpeg and ffprobe must be available on PATH.");

        string directory = CreateTestDirectory();
        try
        {
            short[] source = CreateSamples(50_000);
            string monoWavePath = Path.Combine(directory, "AAC source.wav");
            string aacPath = Path.Combine(directory, "AAC frames.vhs");
            string stereoWavePath = Path.Combine(directory, "float source.wav");
            string floatWavePath = Path.Combine(directory, "float RF frames.wav");
            WriteWave(monoWavePath, source, sampleRate: 44_100);
            WriteWave(stereoWavePath, source, stereo: true);
            EncodeAac(monoWavePath, aacPath);
            EncodeFloatWave(stereoWavePath, floatWavePath);

            VerifyDecodedPlaneGeometry(aacPath);
            VerifyDecodedPlaneGeometry(floatWavePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static short[] CreateSamples(int sampleCount = SampleCount)
    {
        var samples = new short[sampleCount];
        for (ulong index = 0; index < (ulong)samples.Length; index++)
        {
            ulong value = ((index * 1_103_515_245UL) + 12_345UL) >> 8;
            samples[index] = unchecked((short)(ushort)value);
        }

        return samples;
    }

    private static PyAvAudioPlanePaddingStream CreateVariablePaddingStream(
        short[] source,
        long targetSample)
    {
        var geometries = new Queue<PyAvAudioFrameGeometry>(
        [
            new PyAvAudioFrameGeometry(3, 1_000),
            new PyAvAudioFrameGeometry(5, 1_064),
            new PyAvAudioFrameGeometry(4, 1_128)
        ]);
        return new PyAvAudioPlanePaddingStream(
            new MemoryStream(Pcm16Bytes(source)),
            () => geometries.TryDequeue(out PyAvAudioFrameGeometry geometry) ? geometry : null,
            targetSample);
    }

    private static short[] BuildPaddedSamples(short[] source, IReadOnlyList<int> frameLengths)
    {
        var output = new List<short>();
        short[][] recycledFrames = [[], []];
        int sourceOffset = 0;
        int paddedLength = 0;
        for (int frameIndex = 0; frameIndex < frameLengths.Count; frameIndex++)
        {
            int frameLength = frameLengths[frameIndex];
            Assert.InRange(frameLength, 1, source.Length - sourceOffset);
            paddedLength = Math.Max(
                paddedLength,
                PyAvAudioPlanePaddingStream.CalculatePaddedFrameSamples(frameLength));
            int slot = frameIndex & 1;
            if (recycledFrames[slot].Length < paddedLength)
            {
                recycledFrames[slot] = new short[paddedLength];
            }

            source.AsSpan(sourceOffset, frameLength).CopyTo(recycledFrames[slot]);
            Array.Clear(
                recycledFrames[slot],
                frameLength,
                Math.Min(32, recycledFrames[slot].Length - frameLength));
            output.AddRange(recycledFrames[slot]);
            sourceOffset += frameLength;
        }

        Assert.Equal(source.Length, sourceOffset);
        return output.ToArray();
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-container-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
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

    private static void VerifyDecodedPlaneGeometry(string path)
    {
        int[] frameLengths = ProbeAudioFrameLengths(path);
        short[] decoded = DecodePcm16(path);
        short[] expected = BuildPaddedSamples(decoded, frameLengths);
        using var loader = new FfmpegPcm16SampleLoader(path);
        using FileStream input = File.OpenRead(path);
        Assert.Equal(
            expected.Select(value => (double)value),
            loader.Read(input, 0, expected.Length));
        Assert.Null(loader.Read(input, expected.Length - 8, 16));
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

    private static void WriteImaWave(
        string path,
        int bitsPerSample,
        int channelCount,
        int sampleRateHz = FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz)
    {
        int compressedBytesPerChannel = bitsPerSample switch
        {
            2 => 4,
            3 => 12,
            4 => 4,
            5 => 20,
            _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample))
        };
        int samplesPerGroup = bitsPerSample == 2 ? 16 : bitsPerSample == 4 ? 8 : 32;
        const int fullGroups = 3;
        int blockAlign = checked((4 * channelCount) + (compressedBytesPerChannel * channelCount * fullGroups));
        int samplesPerBlock = checked(1 + (samplesPerGroup * fullGroups));
        byte[][] blocks =
        [
            BuildImaBlock(bitsPerSample, channelCount, fullGroups, blockIndex: 0),
            BuildImaBlock(bitsPerSample, channelCount, fullGroups, blockIndex: 1),
            BuildImaBlock(bitsPerSample, channelCount, groups: 1, blockIndex: 2)
        ];
        int dataLength = blocks.Sum(block => block.Length);
        int totalSamples = checked((samplesPerBlock * 2) + 1 + samplesPerGroup);
        int averageBytesPerSecond = checked(sampleRateHz * blockAlign / samplesPerBlock);

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(20);
            writer.Write((short)0x0011);
            writer.Write((short)channelCount);
            writer.Write(sampleRateHz);
            writer.Write(averageBytesPerSecond);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);
            writer.Write((short)2);
            writer.Write((short)samplesPerBlock);
            writer.Write("fact"u8);
            writer.Write(4);
            writer.Write(totalSamples);
            writer.Write("data"u8);
            writer.Write(dataLength);
            foreach (byte[] block in blocks)
            {
                writer.Write(block);
            }
        }

        byte[] waveBody = body.ToArray();
        using var output = new BinaryWriter(File.Create(path), Encoding.ASCII, leaveOpen: false);
        output.Write("RIFF"u8);
        output.Write(waveBody.Length);
        output.Write(waveBody);
    }

    private static byte[] BuildImaBlock(
        int bitsPerSample,
        int channelCount,
        int groups,
        int blockIndex)
    {
        int compressedBytesPerChannel = bitsPerSample switch
        {
            2 => 4,
            3 => 12,
            4 => 4,
            5 => 20,
            _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample))
        };
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
        {
            for (int channel = 0; channel < channelCount; channel++)
            {
                writer.Write(checked((short)(-21_000 + (blockIndex * 7_000) + (channel * 1_337))));
                writer.Write(checked((byte)(17 + (blockIndex * 19) + (channel * 7))));
                writer.Write((byte)0);
            }

            int length = checked(compressedBytesPerChannel * channelCount * groups);
            uint state = 0x9E3779B9U
                ^ checked((uint)bitsPerSample << 24)
                ^ checked((uint)channelCount << 16)
                ^ checked((uint)blockIndex);
            for (int index = 0; index < length; index++)
            {
                state = unchecked((state * 1_664_525U) + 1_013_904_223U);
                writer.Write((byte)(state >> 24));
            }
        }

        return output.ToArray();
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
        var arguments = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            inputPath,
            "-c:a",
            "flac"
        };

        if (oggContainer)
        {
            arguments.AddRange(["-f", "ogg", "-compression_level", "6"]);
        }

        arguments.Add(outputPath);
        RunFfmpeg(arguments);
    }

    private static void EncodeAlac(string inputPath, string outputPath)
        => RunFfmpeg(
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-i", inputPath,
            "-c:a", "alac",
            outputPath
        ]);

    private static void EncodeVorbis(string inputPath, string outputPath)
        => RunFfmpeg(
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-i", inputPath,
            "-c:a", "libvorbis",
            "-q:a", "4",
            "-f", "ogg",
            outputPath
        ]);

    private static void EncodeMp3(string inputPath, string outputPath)
        => RunFfmpeg(
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-i", inputPath,
            "-c:a", "libmp3lame",
            "-b:a", "192k",
            "-f", "mp3",
            outputPath
        ]);

    private static void EncodeAac(string inputPath, string outputPath)
        => RunFfmpeg(
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-i", inputPath,
            "-c:a", "aac",
            "-b:a", "192k",
            "-f", "adts",
            outputPath
        ]);

    private static void EncodeFloatWave(string inputPath, string outputPath)
        => RunFfmpeg(
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-i", inputPath,
            "-c:a", "pcm_f32le",
            outputPath
        ]);

    private static void RunFfmpeg(IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = CreateProcessStartInfo("ffmpeg", arguments);
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

    private static int[] ProbeAudioFrameLengths(string path)
    {
        ProcessStartInfo startInfo = CreateProcessStartInfo(
            "ffprobe",
        [
            "-v", "error",
            "-select_streams", "a:0",
            "-show_entries", "frame=nb_samples",
            "-of", "json",
            path
        ]);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffprobe.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode == 0,
            $"ffprobe exited with {process.ExitCode}: {standardError.Result}");

        using JsonDocument document = JsonDocument.Parse(standardOutput.Result);
        return document.RootElement
            .GetProperty("frames")
            .EnumerateArray()
            .Select(frame => frame.GetProperty("nb_samples").GetInt32())
            .ToArray();
    }

    private static short[] DecodePcm16(string path)
    {
        ProcessStartInfo startInfo = CreateProcessStartInfo(
            "ffmpeg",
        [
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-i", path,
            "-map", "0:a:0",
            "-af", "aformat=sample_fmts=s16:channel_layouts=mono",
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "-ac", "1",
            "-"
        ]);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        standardError.GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode == 0,
            $"ffmpeg exited with {process.ExitCode}: {standardError.Result}");

        byte[] bytes = output.ToArray();
        Assert.Equal(0, bytes.Length % sizeof(short));
        var samples = new short[bytes.Length / sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(
                bytes.AsSpan(i * sizeof(short), sizeof(short)));
        }

        return samples;
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string filename,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(filename)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
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
