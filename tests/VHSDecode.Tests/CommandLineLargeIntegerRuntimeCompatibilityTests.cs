using System.Buffers.Binary;
using System.Numerics;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CommandLineLargeIntegerRuntimeCompatibilityTests
{
    [Fact(DisplayName = "Large field-order confidence values clamp like Python integers")]
    public void LargeFieldOrderConfidenceValuesClampLikePythonIntegers()
    {
        using DecodeSession negative = CreateVhs(
            "--field_order_confidence",
            "-999999999999999999999999999999");
        using DecodeSession positive = CreateVhs(
            "--field_order_confidence",
            "999999999999999999999999999999");

        Assert.Equal(0, negative.FieldOrderOptions.Confidence);
        Assert.Equal(100, positive.FieldOrderOptions.Confidence);
    }

    [Fact(DisplayName = "Large track phase values retain v0.4.0 validation semantics")]
    public void LargeTrackPhaseValuesRetainV040ValidationSemantics()
    {
        const string largeTrackPhase = "999999999999999999999999999999";

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => CreateVhs("--track_phase", largeTrackPhase));
        Assert.Equal("Track phase can only be 0, 1 or None", exception.Message);

        using DecodeSession meSecam = CreateVhs(
            "--system",
            "MESECAM",
            "--track_phase",
            largeTrackPhase);
        Assert.Null(meSecam.TbcRenderer.TrackPhaseIre0Offset);
    }

    [Fact(DisplayName = "Large sharpness values use Python integer true-division semantics")]
    public void LargeSharpnessValuesUsePythonIntegerTrueDivisionSemantics()
    {
        using DecodeSession beyondInt32 = CreateVhs("--sharpness", "3000000000");
        Assert.Equal(30_000_000.0, beyondInt32.FilterOptions.SharpnessEq?.Level);

        string finiteHugeValue = "1" + new string('0', 309);
        using DecodeSession finiteHuge = CreateVhs("--sharpness", finiteHugeValue);
        Assert.Equal(1e307, finiteHuge.FilterOptions.SharpnessEq?.Level);

        string overflowingValue = "1" + new string('0', 400);
        OverflowException exception = Assert.Throws<OverflowException>(
            () => CreateVhs("--sharpness", overflowingValue));
        Assert.Equal("integer division result too large for a float", exception.Message);
    }

    [Fact(DisplayName = "Large VHS wow smoothing values do not fall back to the default")]
    public void LargeVhsWowSmoothingValuesDoNotFallBackToTheDefault()
    {
        using DecodeSession session = CreateVhs(
            "--wow_level_adjust_smoothing",
            "3000000000");

        Assert.Equal(3_000_000_000.0, session.TbcRenderer.WowLevelAdjustSmoothing);
    }

    [Fact(DisplayName = "Large decode lengths retain Python integer field bounds")]
    public void LargeDecodeLengthsRetainPythonIntegerFieldBounds()
    {
        const string framesText = "999999999999999999999999999999";
        using DecodeSession positive = CreateVhs("--length", framesText);
        using DecodeSession negative = CreateVhs("--length", "-" + framesText);

        Assert.Equal(BigInteger.Parse(framesText) * 2, positive.RunBounds.RequestedFieldCount);
        Assert.Equal(BigInteger.Zero, negative.RunBounds.RequestedFieldCount);
    }

    [Fact(DisplayName = "Large thread values retain v0.4.0 override and negative-range semantics")]
    public void LargeThreadValuesRetainV040OverrideAndNegativeRangeSemantics()
    {
        const string positiveText = "99999999999999999999999999999999999999999999999999";
        const string negativeText = "-99999999999999999999999999999999999999999999999999";

        using DecodeSession debugPlot = CreateVhs(
            "--threads",
            positiveText,
            "--debug_plot",
            "demodblock");
        using DecodeSession negativeDebugPlot = CreateVhs(
            "--threads",
            negativeText,
            "--debug_plot",
            "demodblock");
        using DecodeSession cvbs = Create(CliSpecs.Cvbs, "--threads", negativeText);
        using DecodeSession laserDisc = Create(CliSpecs.LaserDisc, "--threads", negativeText);

        Assert.Equal(BigInteger.Parse(positiveText), debugPlot.ExecutionOptions.RequestedThreadsInteger);
        Assert.Equal(BigInteger.Parse(negativeText), negativeDebugPlot.ExecutionOptions.RequestedThreadsInteger);
        Assert.Equal(BigInteger.Parse(negativeText), cvbs.ExecutionOptions.RequestedThreadsInteger);
        Assert.Equal(BigInteger.Parse(negativeText), laserDisc.ExecutionOptions.RequestedThreadsInteger);
        Assert.Equal(0, debugPlot.ExecutionOptions.WorkerThreads);
        Assert.Equal(0, negativeDebugPlot.ExecutionOptions.WorkerThreads);
        Assert.Equal(0, cvbs.ExecutionOptions.WorkerThreads);
        Assert.Equal(0, laserDisc.ExecutionOptions.WorkerThreads);
    }

    [Fact(DisplayName = "Invalid VHS thread counts retain Python worker initialization errors")]
    public void InvalidVhsThreadCountsRetainPythonWorkerInitializationErrors()
    {
        ArgumentException negative = Assert.ThrowsAny<ArgumentException>(
            () => CreateVhs("--threads", "-1"));
        Assert.Equal("max_workers must be greater than 0", negative.Message);

        const string hugeText = "99999999999999999999999999999999999999999999999999";
        foreach (DecodeCommandSpec spec in new[] { CliSpecs.Vhs, CliSpecs.Cvbs, CliSpecs.LaserDisc })
        {
            ArgumentException huge = Assert.ThrowsAny<ArgumentException>(
                () => Create(spec, "--threads", hugeText));
            Assert.Equal("can't start new thread", huge.Message);
        }
    }

    [Fact(DisplayName = "Thread initialization failures retain the upstream empty log artifact")]
    public void ThreadInitializationFailuresRetainUpstreamEmptyLogArtifact()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string inputPath = Path.Combine(tempDirectory, "input.s16");
            string outputBase = Path.Combine(tempDirectory, "failure");
            File.WriteAllBytes(inputPath, [0, 0]);
            ParsedCommand command = new CommandLineParser().Parse(
                CliSpecs.Vhs,
                ["--pal", "--threads", "-1", inputPath, outputBase]);
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = new DecodeRunner().Run(
                command,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Equal("max_workers must be greater than 0" + Environment.NewLine, error.ToString());
            Assert.True(File.Exists(outputBase + ".log"));
            Assert.Equal(0, new FileInfo(outputBase + ".log").Length);
            Assert.False(File.Exists(outputBase + ".tbc"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Ignored and negative huge thread values survive zero-field completion")]
    public void IgnoredAndNegativeHugeThreadValuesSurviveZeroFieldCompletion()
    {
        const string positiveText = "99999999999999999999999999999999999999999999999999";
        const string negativeText = "-99999999999999999999999999999999999999999999999999";
        string tempDirectory = CreateTempDirectory();
        try
        {
            using DecodeSession vhs = CreateWithOutput(
                CliSpecs.Vhs,
                Path.Combine(tempDirectory, "vhs"),
                "--pal",
                "--length",
                "0",
                "--threads",
                positiveText,
                "--debug_plot",
                "demodblock");
            using DecodeSession cvbs = CreateWithOutput(
                CliSpecs.Cvbs,
                Path.Combine(tempDirectory, "cvbs"),
                "--pal",
                "--length",
                "0",
                "--threads",
                negativeText);
            using DecodeSession laserDisc = CreateWithOutput(
                CliSpecs.LaserDisc,
                Path.Combine(tempDirectory, "ld"),
                "--PAL",
                "--length",
                "0",
                "--threads",
                negativeText);

            var engine = new TbcFieldSequenceDecodeEngine();
            Assert.True(engine.TryDecodeAndWrite(vhs, new MemoryStream()).Success);
            Assert.True(engine.TryDecodeAndWrite(cvbs, new MemoryStream()).Success);
            Assert.True(engine.TryDecodeAndWrite(laserDisc, new MemoryStream()).Success);
            Assert.Equal("{", File.ReadAllText(vhs.OutputBase + ".tbc.json.tmp"));
            Assert.Equal("{", File.ReadAllText(cvbs.OutputBase + ".tbc.json.tmp"));
            Assert.Equal("{", File.ReadAllText(laserDisc.OutputBase + ".tbc.json.tmp"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Decode seek uses only minus one as the disabled sentinel")]
    public void DecodeSeekUsesOnlyMinusOneAsTheDisabledSentinel()
    {
        using DecodeSession negativeCvbs = Create(CliSpecs.Cvbs, "--seek", "-2");
        Assert.Equal(new BigInteger(-2), negativeCvbs.ExecutionOptions.SeekFrame);
        InvalidOperationException cvbsException = Assert.Throws<InvalidOperationException>(
            () => new TbcFieldSequenceDecodeEngine().DecodeFields(
                negativeCvbs,
                new MemoryStream()));
        Assert.Equal("ERROR: Seeking failed", cvbsException.Message);

        const string hugeSeek = "999999999999999999999999999999";
        using DecodeSession laserDisc = Create(CliSpecs.LaserDisc, "--seek", hugeSeek);
        Assert.Equal(BigInteger.Parse(hugeSeek), laserDisc.ExecutionOptions.SeekFrame);
        var engine = new TbcFieldSequenceDecodeEngine(
            readField: static (_, _, _, _, _) => null);
        InvalidOperationException ldException = Assert.Throws<InvalidOperationException>(
            () => engine.DecodeFields(laserDisc, new MemoryStream()));
        Assert.Equal("ERROR: Seeking failed", ldException.Message);
    }

    [Fact(DisplayName = "Ignored large LD integer options do not overflow before decoding")]
    public void IgnoredLargeLdIntegerOptionsDoNotOverflowBeforeDecoding()
    {
        const string hugeValue = "99999999999999999999999999999999999999999999999999";
        using DecodeSession disabledAudio = Create(
            CliSpecs.LaserDisc,
            "--PAL",
            "--disable_analog_audio",
            "--analog_audio_frequency",
            hugeValue,
            "--video_lpf_order",
            "-" + hugeValue);
        using DecodeSession ntscRate = Create(
            CliSpecs.LaserDisc,
            "--NTSC",
            "--ntsc_audio_rate",
            "--analog_audio_frequency",
            hugeValue);

        Assert.False(disabledAudio.LaserDiscAudioOptions!.DecodeAnalogAudio);
        Assert.Equal(0.0, disabledAudio.LaserDiscAudioOptions.AnalogAudioFrequency);
        Assert.Equal(BigInteger.Zero, disabledAudio.LaserDiscAudioOptions.AnalogAudioFrequencyInteger);
        Assert.Equal(7, disabledAudio.Parameters.RfParams.GetProperty("video_lpf_order").GetInt32());
        Assert.Equal(-2.8, ntscRate.LaserDiscAudioOptions!.AnalogAudioFrequency);
        Assert.Null(ntscRate.LaserDiscAudioOptions.AnalogAudioFrequencyInteger);
    }

    [Fact(DisplayName = "Large positive LD audio rates survive zero-field finalization like Python integers")]
    public void LargePositiveLdAudioRatesSurviveZeroFieldFinalizationLikePythonIntegers()
    {
        string hugeValue = "1" + new string('0', 400);
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "positive");
            using DecodeSession session = CreateLaserDiscWithOutput(
                outputBase,
                "--PAL",
                "--length",
                "0",
                "--threads",
                "0",
                "--noEFM",
                "--analog_audio_frequency",
                hugeValue);

            Assert.True(double.IsPositiveInfinity(session.LaserDiscAudioOptions!.AnalogAudioFrequency));
            Assert.Equal(
                BigInteger.Parse(hugeValue),
                session.LaserDiscAudioOptions.AnalogAudioFrequencyInteger);

            TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine()
                .TryDecodeAndWrite(session, new MemoryStream());

            Assert.True(result.Success);
            Assert.Equal("{", File.ReadAllText(outputBase + ".tbc.json.tmp"));
            Assert.True(File.Exists(outputBase + ".pcm"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Large negative LD audio rates overflow during final metadata like v0.4.0")]
    public void LargeNegativeLdAudioRatesOverflowDuringFinalMetadataLikeV040()
    {
        string hugeValue = "-1" + new string('0', 400);
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "negative");
            using DecodeSession session = CreateLaserDiscWithOutput(
                outputBase,
                "--PAL",
                "--length",
                "0",
                "--threads",
                "0",
                "--noEFM",
                "--analog_audio_frequency",
                hugeValue);

            OverflowException exception = Assert.Throws<OverflowException>(
                () => new TbcFieldSequenceDecodeEngine().TryDecodeAndWrite(
                    session,
                    new MemoryStream()));

            Assert.Equal("int too large to convert to float", exception.Message);
            Assert.True(File.Exists(outputBase + ".pcm"));
            Assert.False(File.Exists(outputBase + ".tbc.json.tmp"));
            byte[] sqlite = File.ReadAllBytes(outputBase + ".tbc.db");
            Assert.Equal(
                3_050_004u,
                BinaryPrimitives.ReadUInt32BigEndian(sqlite.AsSpan(96, sizeof(uint))));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static DecodeSession CreateVhs(params string[] options)
        => Create(CliSpecs.Vhs, options);

    private static DecodeSession Create(DecodeCommandSpec spec, params string[] options)
    {
        var arguments = new List<string>(options)
        {
            "input.u8",
            "out"
        };
        ParsedCommand command = new CommandLineParser().Parse(spec, arguments);
        return DecodeSessionFactory.Create(command);
    }

    private static DecodeSession CreateLaserDiscWithOutput(string outputBase, params string[] options)
        => CreateWithOutput(CliSpecs.LaserDisc, outputBase, options);

    private static DecodeSession CreateWithOutput(
        DecodeCommandSpec spec,
        string outputBase,
        params string[] options)
    {
        var arguments = new List<string>(options)
        {
            "input.s16",
            outputBase
        };
        ParsedCommand command = new CommandLineParser().Parse(spec, arguments);
        return DecodeSessionFactory.Create(command);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "vhsdecode-large-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
