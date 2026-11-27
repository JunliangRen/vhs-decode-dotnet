using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Tbc;
using VHSDecode.Preview;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PreviewFrameTests
{
    [Fact]
    public void BgraConversionUsesChromaFromTheSameField()
    {
        var frame = new PreviewFrame(
            [100, 100, 100, 100, 100, 100, 100, 100, 16, 240, 128, 128],
            2, 4, TimeSpan.Zero, 25);
        var bgra = new byte[32];

        frame.CopyToBgra(bgra);

        Assert.Equal(bgra[..8], bgra[16..24]);
        Assert.Equal(bgra[8..16], bgra[24..]);
        Assert.Equal(0, bgra[0]);
        Assert.Equal(255, bgra[8]);
        Assert.All(new[] { 3, 7, 11, 15, 19, 23, 27, 31 }, i => Assert.Equal(255, bgra[i]));
    }

    [Fact]
    public void FullFieldCropPreservesRowsAndKeepsOwnedSourcePixels()
    {
        byte[] pixels = Enumerable.Range(0, 48).Select(value => (byte)value).ToArray();
        var field = new PreviewField(pixels, new(0, 0, 4, 3), new(1, 1, 2, 2),
            true, TimeSpan.FromMilliseconds(16), 50, default, default, 17_734_475, 16_000, 256);
        var cropped = new byte[16];
        field.CopyToBgra(cropped, PreviewRegion.ActiveVideo);

        Assert.Equal(pixels[20..28].Concat(pixels[36..44]).ToArray(), cropped);
        var full = new byte[48];
        field.CopyToBgra(full, PreviewRegion.FullRaster);
        Assert.Equal(pixels, full);
        Assert.Throws<ArgumentException>(() => field.CopyToBgra(cropped, PreviewRegion.FullRaster));
    }

    [Fact]
    public void FullRasterLumaUsesTheFieldCalibrationWithoutChangingLegacyRendering()
    {
        var command = new CommandLineParser().Parse(CliSpecs.Vhs, ["--pal", "input.s16", "output"]);
        using var session = DecodeSessionFactory.Create(command);
        int width = session.TbcFrameSpec.OutputLineLength;
        int height = session.TbcFrameSpec.OutputLineCount;
        var converter = new VideoOutputConverter(4_100_000, 8000, 25_000, -40, 200);
        ushort white = converter.ConvertHz(converter.IreToHz(100));
        var samples = Enumerable.Repeat(white, checked(width * height)).ToArray();
        var field = new TbcDecodedField(0, samples, null!, null!, 0, 0, 0, 0, OutputConverter: converter);
        var native = new PreviewFieldRenderer(session, width, height * 2, fullRaster: true, nativeChroma: true);
        byte[] pixel = native.RenderFieldBgra(field, true)[..4];
        Assert.Equal<byte>([255, 255, 255, 255], pixel);

        var legacy = new PreviewFieldRenderer(session, 64, 48);
        var first = legacy.Render(field, true);
        var second = legacy.Render(field with { OutputConverter = null }, true);
        Assert.Equal(first.Luma, second.Luma);
    }

    [Theory]
    [InlineData("pal", 576)]
    [InlineData("ntsc", 480)]
    public void FullRasterRetainsEverySourceSampleWithEncoderPadding(string system, int activeHeight)
    {
        var command = new VHSDecode.Core.CommandLine.CommandLineParser().Parse(
            VHSDecode.Core.CommandLine.CliSpecs.Vhs,
            ["--" + system, "--preview-server", "--preview-full-raster", "input.s16"]);
        Assert.True(command.Get<bool>("preview_full_raster"));
        using var session = VHSDecode.Core.Decode.DecodeSessionFactory.Create(command);
        var spec = session.TbcFrameSpec;
        var raster = PreviewFileDecoder.Dimensions(session, PreviewRegion.FullRaster);
        Assert.Equal(activeHeight, PreviewFileDecoder.Dimensions(session, PreviewRegion.ActiveVideo).Height);
        Assert.InRange(raster.Width - spec.OutputLineLength, 0, 1);
        Assert.InRange(raster.Height - 2 * spec.OutputLineCount, 0, 2);
        Assert.Equal(0, raster.Width % 2);
        Assert.Equal(0, raster.Height % 4);
    }

    [Theory]
    [InlineData("ntsc", false)]
    [InlineData("pal", true)]
    public void NativeRendererCanRenderTheActiveCropDirectly(string standard, bool separateChroma)
    {
        var command = new CommandLineParser().Parse(CliSpecs.Vhs, ["--" + standard, "input.s16", "output"]);
        using var session = DecodeSessionFactory.Create(command);
        var spec = session.TbcFrameSpec;
        int width = spec.OutputLineLength;
        int height = spec.OutputLineCount;
        PreviewRectangle active = PreviewFileDecoder.ActiveFieldRectangle(session);
        var samples = new ushort[spec.FieldSampleCount];
        ushort[]? chroma = separateChroma ? new ushort[samples.Length] : null;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int carrier = (x & 3) < 2 ? 4096 : -4096;
                if (chroma is not null)
                    chroma[i] = (ushort)(32767 + carrier);
                samples[i] = (ushort)(24000 + (x * 11) + (chroma is null ? carrier : 0));
            }
        }

        var field = new TbcDecodedField(0, samples, null!, null!, 0, 0, 0, 0, ChromaSamples: chroma);
        var renderer = new PreviewFieldRenderer(session, width, height * 2, fullRaster: true, nativeChroma: true);
        byte[] full = renderer.RenderFieldBgra(field, true);
        byte[] cropped = renderer.RenderFieldBgra(field, true, active);
        Assert.Equal(active.Width * active.Height * 4, cropped.Length);
        for (int y = 0; y < active.Height; y++)
        {
            Assert.True(full.AsSpan(((active.Y + y) * width + active.X) * 4, active.Width * 4)
                .SequenceEqual(cropped.AsSpan(y * active.Width * 4, active.Width * 4)));
        }
    }

    [Theory]
    [InlineData(20_000_000)]
    [InlineData(40_000_000)]
    public void PalBetamaxRealtimeFileAcceptsVideoAndRejectsNtscHiFi(int rate)
    {
        var options = new PreviewFieldOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".s16"),
            PreviewSourceFormat.Betamax, PreviewVideoStandard.Pal, rate) { DecodeMode = PreviewDecodeMode.Realtime };
        var token = TestContext.Current.CancellationToken;
        Assert.Throws<FileNotFoundException>(() => PreviewFileDecoder.DecodeFields(options, _ => { }, token));
        Assert.Throws<NotSupportedException>(() => PreviewFileDecoder.DecodeFields(
            options with { BetamaxProfile = BetamaxVideoProfile.HiFi }, _ => { }, token));
        var audioOptions = new RealtimePreviewOptions(PreviewSourceFormat.Betamax, PreviewVideoStandard.Pal, rate)
        {
            AudioTrack = PreviewAudioTrack.BetamaxHiFi
        };
        Assert.Throws<NotSupportedException>(() => new RealtimePreviewSession(audioOptions, _ => { }, _ => { }));
    }

    [Fact]
    public void RealtimeScopeAndTapeOptionsFollowTheFieldFormat()
    {
        foreach (var source in new[] { PreviewSourceFormat.SecamVhs, PreviewSourceFormat.MesecamVhs })
        {
            var options = new RealtimePreviewOptions(source, PreviewVideoStandard.Secam, 20_000_000)
                { IncludeSignalScopes = true };
            Assert.True(RealtimePreviewCapabilities.IsSupported(options));
            Assert.True(RealtimePreviewCapabilities.IsSupported(options with { Standard = PreviewVideoStandard.Pal }));
            Assert.False(RealtimePreviewCapabilities.IsSupported(options with { Standard = PreviewVideoStandard.Ntsc }));
        }
        var audio = new RealtimePreviewOptions(PreviewSourceFormat.Vhs, PreviewVideoStandard.Pal, 40_000_000)
            { AudioTrack = PreviewAudioTrack.VhsHiFi, IncludeSignalScopes = true };
        Assert.False(RealtimePreviewCapabilities.IsSupported(audio));
        var ed = new RealtimePreviewOptions(PreviewSourceFormat.Betamax, PreviewVideoStandard.Ntsc, 40_000_000)
            { BetamaxProfile = BetamaxVideoProfile.EdBeta, AudioTrack = PreviewAudioTrack.BetamaxHiFi };
        Assert.True(RealtimePreviewCapabilities.IsSupported(ed));
        Assert.False(RealtimePreviewCapabilities.IsSupported(ed with { Standard = PreviewVideoStandard.Pal }));
    }

    [Fact]
    public void RealtimeCapabilitiesMatchSessionAndFileValidation()
    {
        var combinations = RealtimePreviewCapabilities.SupportedCombinations;
        Assert.Equal(combinations.Count, combinations.Distinct().Count());
        foreach (var c in combinations)
        {
            var options = new RealtimePreviewOptions(c.SourceFormat, c.Standard, c.InputSampleRateHz)
                { BetamaxProfile = c.BetamaxProfile, AudioTrack = c.AudioTrack, Cvbs50Ohms = c.SupportsCvbs50Ohms };
            Assert.True(RealtimePreviewCapabilities.IsSupported(options));
            RealtimePreviewSession.Validate(options);
        }

        foreach (var profile in new[] { BetamaxVideoProfile.SuperBeta, BetamaxVideoProfile.SuperBetaHiFi })
        {
            var options = new RealtimePreviewOptions(PreviewSourceFormat.Betamax, PreviewVideoStandard.Ntsc, 40_000_000)
                { BetamaxProfile = profile };
            Assert.True(RealtimePreviewCapabilities.IsSupported(options));
            Assert.Equal(profile == BetamaxVideoProfile.SuperBetaHiFi,
                RealtimePreviewCapabilities.IsSupported(options with { AudioTrack = PreviewAudioTrack.BetamaxHiFi }));
            var invalid = options with { Standard = PreviewVideoStandard.Pal };
            Assert.False(RealtimePreviewCapabilities.IsSupported(invalid));
            Assert.Throws<NotSupportedException>(() => RealtimePreviewSession.Validate(invalid));
            var file = new PreviewFieldOptions("missing.s16", invalid.SourceFormat, invalid.Standard)
                { DecodeMode = PreviewDecodeMode.Realtime, BetamaxProfile = profile };
            Assert.Throws<NotSupportedException>(() => PreviewFileDecoder.DecodeFields(file, _ => { }, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(PreviewSourceFormat.Vhs)]
    [InlineData(PreviewSourceFormat.Betamax)]
    [InlineData(PreviewSourceFormat.LaserDisc)]
    public void RealtimeRejectsCvbsPresetForRf(PreviewSourceFormat source)
    {
        var options = new RealtimePreviewOptions(source, PreviewVideoStandard.Ntsc, 40_000_000) { Cvbs50Ohms = true };
        Assert.Throws<NotSupportedException>(() => new RealtimePreviewSession(options, _ => { }));
        var file = new PreviewFieldOptions("missing.flac", source, PreviewVideoStandard.Ntsc)
        {
            DecodeMode = PreviewDecodeMode.Realtime, Cvbs50Ohms = true
        };
        Assert.Throws<NotSupportedException>(() => PreviewFileDecoder.DecodeFields(file, _ => { }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealtimeCvbsLocksDirectCompositeAfterDiscontinuity(bool pal)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
            && File.Exists(Path.Combine(AppContext.BaseDirectory, "vhsdecode_realtime_preview.dll")),
            "The Windows x64 Realtime preview library was not staged.");
        const int rate = 20_000_000;
        var samples = CvbsTestSignal.Create(pal);
        var options = new RealtimePreviewOptions(PreviewSourceFormat.Cvbs,
            pal ? PreviewVideoStandard.Pal : PreviewVideoStandard.Ntsc, rate);
        var fields = new List<(TimeSpan Time, bool Top)>();
        using var decoder = new RealtimePreviewSession(options, f =>
        {
            Assert.True(f.SignalYuv.IsEmpty);
            fields.Add((f.Timestamp, f.IsTopField));
        });
        decoder.Process(samples, 0, TestContext.Current.CancellationToken);
        Assert.True(fields.Count >= 4);
        PreviewField? first = null;
        using (var scoped = new RealtimePreviewSession(options with { IncludeSignalScopes = true }, f => first ??= f))
        {
            scoped.Process(samples, 0, TestContext.Current.CancellationToken);
            Assert.NotNull(first);
            Assert.Equal(first.Width * first.Height * 3, first.SignalYuv.Length);
            Assert.True(first.SignalYuv.Span.ContainsAnyExcept((short)0));
            var saved = first.SignalYuv.ToArray();
            scoped.Process(samples, rate, TestContext.Current.CancellationToken);
            Assert.Equal(saved, first.SignalYuv.ToArray());
        }
        Assert.True(decoder.LockStatus.VerticalLocked);
        Assert.Null(decoder.LockStatus.AudioLocked);
        var initial = fields.ToArray();
        fields.Clear();
        decoder.Process(samples, rate, TestContext.Current.CancellationToken);
        Assert.Equal(initial.Length, fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            Assert.Equal(initial[i].Time + TimeSpan.FromSeconds(1), fields[i].Time);
            Assert.Equal(initial[i].Top, fields[i].Top);
            if (i > 0) Assert.NotEqual(fields[i - 1].Top, fields[i].Top);
        }
    }

    [Fact]
    public void HorizontalOnlyInputExpiresFieldLock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X64 && File.Exists(Path.Combine(AppContext.BaseDirectory,
            "vhsdecode_realtime_preview.dll")), "The Windows x64 Realtime preview library was not staged.");
        var options = new RealtimePreviewOptions(PreviewSourceFormat.Cvbs, PreviewVideoStandard.Pal, 20_000_000);
        using var session = new RealtimePreviewSession(options, _ => { });
        var samples = CvbsTestSignal.Create(true);
        session.Process(samples, 0, TestContext.Current.CancellationToken);
        Assert.True(session.LockStatus.VerticalLocked);
        short[] line = samples.AsSpan(20 * 1280, 1280).ToArray();
        short[] horizontal = new short[1280 * 1000];
        for (int i = 0; i < horizontal.Length; i += line.Length)
            line.CopyTo(horizontal, i);
        session.Process(horizontal, samples.Length, TestContext.Current.CancellationToken);
        Assert.Equal(new PreviewLockStatus(true, false, false, null), session.LockStatus);
        session.Reset();
        Assert.Equal(new PreviewLockStatus(false, false, false, null), session.LockStatus);
    }

    [Fact]
    public void RealtimeRejectsUnsupportedSettingsBeforeOpeningInput()
    {
        var options = new PreviewFieldOptions("missing.s16", PreviewSourceFormat.Vhs,
            PreviewVideoStandard.Ntsc, 20_000_000)
        {
            DecodeMode = PreviewDecodeMode.Realtime
        };
        Action<PreviewField> sink = _ => throw new InvalidOperationException("Unexpected field");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.Throws<NotSupportedException>(() =>
            PreviewFileDecoder.DecodeFields(options with { SourceFormat = PreviewSourceFormat.LaserDisc, Standard = PreviewVideoStandard.Secam }, sink, cancellationToken));
        Assert.Throws<NotSupportedException>(() =>
            PreviewFileDecoder.DecodeFields(options with { InputSampleRateHz = 10_000_000 }, sink, cancellationToken));
        Assert.Throws<NotSupportedException>(() =>
            PreviewFileDecoder.DecodeFields(options with { IncludeTbcSamples = true }, sink, cancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PreviewFileDecoder.DecodeFields(options with { DecodeThreads = 65 }, sink, cancellationToken));
    }

    [Theory]
    [InlineData(PreviewVideoStandard.Ntsc, 1, 8_000_000)]
    [InlineData(PreviewVideoStandard.Pal, 1, 8_000_000)]
    [InlineData(PreviewVideoStandard.Ntsc, 4, 8_000_000)]
    [InlineData(PreviewVideoStandard.Pal, 4, 8_000_000)]
    [InlineData(PreviewVideoStandard.Pal, 4, 10_000_000)]
    [InlineData(PreviewVideoStandard.Ntsc, 4, 20_000_000)]
    [InlineData(PreviewVideoStandard.Ntsc, 4, 40_000_000)]
    [InlineData(PreviewVideoStandard.Pal, 1, 40_000_000)]
    public void RealtimeVhsHiFiKeepsPcmAndSourceClockAcrossCalls(PreviewVideoStandard standard, int threads, int rate)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64
            && File.Exists(Path.Combine(AppContext.BaseDirectory, "vhsdecode_realtime_preview.dll")),
            "The Windows x64 Realtime preview library was not staged.");

        var options = new RealtimePreviewOptions(PreviewSourceFormat.Vhs, standard, rate)
        {
            AudioTrack = PreviewAudioTrack.VhsHiFi
        };
        var rf = new short[rate / 50 + 113];
        var random = new Random(7);
        for (int i = 0; i < rf.Length; i++)
            rf[i] = (short)random.Next(-500, 500);

        CancellationToken token = TestContext.Current.CancellationToken;
        var whole = new List<PreviewAudioBlock>();
        using (var decoder = new RealtimePreviewSession(options, audioSink: whole.Add))
            decoder.Process(rf, 0, token);
        Assert.NotEmpty(whole);
        float[] expected = whole.SelectMany(block => block.InterleavedSamples.ToArray()).ToArray();

        var chopped = new List<PreviewAudioBlock>();
        int caller = Environment.CurrentManagedThreadId;
        using var session = new RealtimePreviewSession(options with { DecodeThreads = threads }, audioSink: block =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            chopped.Add(block);
        });
        for (int pos = 0; pos < rf.Length; pos += 11_237)
            session.Process(rf.AsSpan(pos, Math.Min(11_237, rf.Length - pos)), pos, token);
        Assert.Equal(whole[^1].EndSourceSample, chopped[^1].EndSourceSample);
        Assert.Equal(expected, chopped.SelectMany(block => block.InterleavedSamples.ToArray()));

        chopped.Clear();
        long jump = rate == 40_000_000 ? 900_002 : 900_001;
        session.Process(rf, jump, token);
        Assert.Equal(whole[^1].EndSourceSample + jump, chopped[^1].EndSourceSample);
        Assert.Equal(expected, chopped.SelectMany(block => block.InterleavedSamples.ToArray()));

        session.Reset();
        chopped.Clear();
        session.Process(rf, 0, token);
        Assert.Equal(whole[^1].EndSourceSample, chopped[^1].EndSourceSample);
    }

    [Theory]
    [InlineData(20_000_000, PreviewSourceFormat.Betamax, PreviewAudioTrack.BetamaxHiFi, 1)]
    [InlineData(20_000_000, PreviewSourceFormat.LaserDisc, PreviewAudioTrack.LaserDiscAnalog, 1)]
    [InlineData(40_000_000, PreviewSourceFormat.Betamax, PreviewAudioTrack.BetamaxHiFi, 1)]
    [InlineData(40_000_000, PreviewSourceFormat.LaserDisc, PreviewAudioTrack.LaserDiscAnalog, 1)]
    [InlineData(40_000_000, PreviewSourceFormat.Betamax, PreviewAudioTrack.BetamaxHiFi, 2)]
    [InlineData(40_000_000, PreviewSourceFormat.Betamax, PreviewAudioTrack.BetamaxHiFi, 4)]
    [InlineData(40_000_000, PreviewSourceFormat.Betamax, PreviewAudioTrack.BetamaxHiFi, 8)]
    public void RealtimeAvKeepsAudioClockWithNoVideoLock(int rate, PreviewSourceFormat source, PreviewAudioTrack track, int threads)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64
            && File.Exists(Path.Combine(AppContext.BaseDirectory, "vhsdecode_realtime_preview.dll")),
            "The Windows x64 Realtime preview library was not staged.");
        var options = new RealtimePreviewOptions(source, PreviewVideoStandard.Ntsc, rate)
        {
            AudioTrack = track,
            DecodeThreads = threads
        };
        var blocks = new List<PreviewAudioBlock>();
        using var session = new RealtimePreviewSession(options, _ => Assert.Fail("Unexpected locked field"), blocks.Add);
        short[] silence = new short[rate / 100];
        var token = TestContext.Current.CancellationToken;
        session.Process(silence, 0, token);
        Assert.Equal(480, blocks.Sum(b => b.Frames));
        Assert.All(blocks, b => Assert.False(b.IsLocked));
        long end = blocks[^1].EndSourceSample;
        Assert.InRange(silence.Length - end, 0L, rate / 48_000 + 32L);

        blocks.Clear();
        // LD audio ticks follow the absolute RF clock across gaps.
        long start = source == PreviewSourceFormat.LaserDisc ? rate : rate + 2;
        for (int i = 0; i < silence.Length; i += 511)
            session.Process(silence.AsSpan(i, Math.Min(511, silence.Length - i)), start + i, token);
        Assert.Equal(480, blocks.Sum(b => b.Frames));
        Assert.Equal(start + end, blocks[^1].EndSourceSample);
    }

    [Theory]
    [InlineData(PreviewAudioTrack.LaserDiscAnalog)]
    [InlineData(PreviewAudioTrack.LaserDiscDigitalPcm)]
    [InlineData(PreviewAudioTrack.LaserDiscDts)]
    [InlineData(PreviewAudioTrack.LaserDiscAc3Rf)]
    public void LaserDiscAudioRequiresNtscVideoAndBothSinks(PreviewAudioTrack track)
    {
        var options = new RealtimePreviewOptions(PreviewSourceFormat.LaserDisc, PreviewVideoStandard.Ntsc, 40_000_000)
        {
            AudioTrack = track
        };
        Assert.Throws<ArgumentException>(() => new RealtimePreviewSession(options, fieldSink: _ => { }));
        Assert.Throws<ArgumentException>(() => new RealtimePreviewSession(options, audioSink: _ => { }));
        Assert.Throws<NotSupportedException>(() => new RealtimePreviewSession(options with { Standard = PreviewVideoStandard.Pal }));
        Assert.Throws<NotSupportedException>(() => new RealtimePreviewSession(options with { SourceFormat = PreviewSourceFormat.Vhs }));
    }

    [Theory]
    [InlineData(20_000_000, PreviewAudioTrack.LaserDiscDigitalPcm)]
    [InlineData(20_000_000, PreviewAudioTrack.LaserDiscAc3Rf)]
    [InlineData(40_000_000, PreviewAudioTrack.LaserDiscDigitalPcm)]
    [InlineData(40_000_000, PreviewAudioTrack.LaserDiscAc3Rf)]
    public void LaserDiscDigitalSilenceDoesNotFabricatePcmOnFinish(int rate, PreviewAudioTrack track)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64
            && File.Exists(Path.Combine(AppContext.BaseDirectory, "vhsdecode_realtime_preview.dll")),
            "The Windows x64 Realtime preview library was not staged.");
        var options = new RealtimePreviewOptions(PreviewSourceFormat.LaserDisc, PreviewVideoStandard.Ntsc, rate)
        {
            AudioTrack = track
        };
        using var session = new RealtimePreviewSession(options, _ => Assert.Fail("Unexpected field"),
            _ => Assert.Fail("Unexpected digital audio"));
        var silence = new short[65537];
        session.Process(silence, 0, TestContext.Current.CancellationToken);
        session.Finish();
        session.Process(silence, rate + 1, TestContext.Current.CancellationToken);
        session.Finish();
        session.Finish();
    }

    [Fact]
    public void CancelledFileDecodeDoesNotOpenTheInput()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.Throws<OperationCanceledException>(() => PreviewFileDecoder.Decode(
            new PreviewFileOptions("missing.s16", PreviewSourceFormat.Vhs, PreviewVideoStandard.Ntsc),
            _ => throw new InvalidOperationException("Unexpected frame"), stop.Token));
    }
}
