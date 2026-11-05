using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests;

[Collection(DspWorkingBufferCollection.Name)]
public sealed class LaserDiscRfScratchOwnershipTests
{
    private const int Length = 32_768;

    [Fact]
    public void OwnedLaserDiscDefersPostFmVideoWithDefaultDiagnostics()
    {
        Assert.SkipUnless(Avx2.IsSupported && Fma.IsSupported, "LD CUDA video requires AVX2 and FMA.");
        using DecodeSession session = CreateSession("NTSC", DspBackend.Exact);
        Assert.True(session.Pipeline.CanDeferOwnedLaserDiscPostFmVideo);
        var reader = Assert.IsType<RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader>(
            session.StreamDecoder.TryCreateOwnedImmediateLaserDiscFieldReader());

        RfDemodulatedBlock deferred = session.Pipeline.DecodePreparedStreamBlock(
            BuildInput(session),
            ownedImmediateEnvelopeShape: reader.EnvelopeShape,
            deferEfm: true,
            deferPostFmVideo: true).Demodulated;

        Assert.Empty(deferred.Video);
        Assert.Empty(deferred.VideoLowPass);
        Assert.Null(deferred.VideoBurst);
        Assert.Null(deferred.Efm);
        Assert.NotNull(deferred.AnalogAudio);
        Assert.Same(reader.EnvelopeShape, deferred.Envelope);
        Assert.Equal(Length, deferred.RfHighPass.Length);
        Assert.Equal(Length, deferred.DemodRaw.Length);
        Assert.Contains(deferred.DemodRaw, sample => double.IsFinite(sample) && sample != (double)(float)sample);
    }

    [Theory(DisplayName = "LD streaming omits only analytic output and keeps public RF diagnostics")]
    [InlineData("PAL", DspBackend.Exact)]
    [InlineData("NTSC", DspBackend.Exact)]
    [InlineData("PAL", DspBackend.IppFast)]
    [InlineData("NTSC", DspBackend.IppFast)]
    public void StreamingOmitsOnlyAnalyticAndKeepsPublicDiagnostics(string system, DspBackend backend)
    {
        RequireBackend(backend);
        using DecodeSession session = CreateSession(system, backend);
        double[] input = BuildInput(session);
        double[] original = input.ToArray();
        RfPipelineBlock full = session.Pipeline.DecodePreparedBlock(input, reportDiagnostics: false);
        string retained = Hash(full.Demodulated, includeAnalytic: true);
        RfPipelineBlock streamed = session.Pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false);
        Assert.Equal(Length, full.Demodulated.Analytic.Length);
        Assert.Empty(streamed.Demodulated.Analytic);
        Equal(input, streamed.Input, "Input");
        EqualChannels(full.Demodulated, streamed.Demodulated);
        Assert.NotNull(streamed.Demodulated.Efm);
        Assert.NotNull(streamed.Demodulated.AnalogAudio);
        AssertRfHighPassMatchesIndependentInverse(session, backend, input, full.Demodulated.RfHighPass);
        _ = session.Pipeline.DecodePreparedStreamBlock(BuildInput(session, 0.37), reportDiagnostics: false);
        Assert.Equal(retained, Hash(full.Demodulated, includeAnalytic: true));
        Equal(original, input, "preserved input");

        using var stream = new MemoryStream(MemoryMarshal.AsBytes(input.Select(value => (short)value).ToArray().AsSpan()).ToArray());
        RfPipelineBlock diagnostic = Assert.IsType<RfPipelineBlock>(session.Pipeline.DecodeBlockWithInput(stream, 0, Length));
        Assert.Equal(Length, diagnostic.Demodulated.Analytic.Length);
        RfDecodedSpan span = Assert.IsType<RfDecodedSpan>(session.StreamDecoder.Read(stream, 0, 1_024));
        Assert.Equal(1_024, Assert.IsType<double[]>(span.Envelope).Length);
        Equal(diagnostic.Demodulated.Envelope.AsSpan(session.BlockCut, 1_024).ToArray(), span.Envelope, "public span envelope");
    }

    [Theory(DisplayName = "LD reused spectra preserve boost repair deemphasis and reference dependencies")]
    [InlineData(DspBackend.Exact, "zero-boost")]
    [InlineData(DspBackend.Exact, "boost")]
    [InlineData(DspBackend.Exact, "repair")]
    [InlineData(DspBackend.Exact, "deemphasis")]
    [InlineData(DspBackend.Exact, "spur")]
    [InlineData(DspBackend.Exact, "no-high-pass")]
    [InlineData(DspBackend.IppFast, "zero-boost")]
    [InlineData(DspBackend.IppFast, "boost")]
    [InlineData(DspBackend.IppFast, "repair")]
    [InlineData(DspBackend.IppFast, "deemphasis")]
    [InlineData(DspBackend.IppFast, "spur")]
    [InlineData(DspBackend.IppFast, "no-high-pass")]
    public void ReusedSpectraKeepDependentStages(DspBackend backend, string scenario)
    {
        RequireBackend(backend);
        using DecodeSession session = CreateSession("PAL", backend, audio: false);
        using var demodulator = new RfDemodulator(session.DecodeSampleRateHz, backend);
        double[] input = BuildInput(session);
        Complex[] spectrum = demodulator.ForwardLaserDiscFullSpectrum(input);
        string spectrumBefore = Hash(spectrum);
        RfHighBoostOptions? boost = scenario == "boost"
            ? new RfHighBoostOptions(0.75, 8_000_000.0, 16_000_000.0)
            : scenario == "zero-boost" ? new RfHighBoostOptions(-0.0, 8_000_000.0, 16_000_000.0) : null;
        DiffDemodRepairOptions? repair = scenario == "repair"
            ? new DiffDemodRepairOptions(double.NegativeInfinity) : null;
        NonlinearDeemphasisOptions? nonlinear = scenario == "deemphasis"
            ? new NonlinearDeemphasisOptions(2_000_000.0, null, 2, -250_000.0, 250_000.0) : null;
        SubDeemphasisOptions? sub = scenario == "deemphasis"
            ? new SubDeemphasisOptions(2_000_000.0, null, 2, 500_000.0, 4_000_000.0, 1.0,
                null, null, null, null, null) : null;
        RfDemodulatedBlock full = Decode(retainAnalytic: true);
        string retained = Hash(full, includeAnalytic: true);
        RfDemodulatedBlock compact = Decode(retainAnalytic: false);
        Assert.Equal(Length, full.Analytic.Length);
        Assert.Empty(compact.Analytic);
        EqualChannels(full, compact);
        Assert.Equal(spectrumBefore, Hash(spectrum));
        Assert.Equal(retained, Hash(full, includeAnalytic: true));
        if (scenario == "boost")
        {
            RfDemodulatedBlock plain = demodulator.Demodulate(input, session.Filters.RfVideo,
                session.Filters.RfHighPass, session.Filters.RfMtf, session.Filters.Video,
                session.Filters.VideoLowPass05);
            Assert.NotEqual(Hash(plain.Analytic), Hash(full.Analytic));
        }
        if (scenario == "repair")
        {
            RfDemodulatedBlock plain = demodulator.Demodulate(input, session.Filters.RfVideo,
                session.Filters.RfHighPass, session.Filters.RfMtf, session.Filters.Video,
                session.Filters.VideoLowPass05);
            Assert.NotEqual(Hash(plain.DemodRaw), Hash(full.DemodRaw));
        }
        if (scenario == "deemphasis")
        {
            Assert.True(demodulator.NonlinearDeemphasisFilterPlanBuildCount > 0);
            Assert.True(demodulator.SubDeemphasisFilterPlanBuildCount > 0);
        }

        RfDemodulatedBlock Decode(bool retainAnalytic) => demodulator.Demodulate(
            input, session.Filters.RfVideo, session.Filters.RfHighPass, session.Filters.RfMtf,
            session.Filters.Video, session.Filters.VideoLowPass05,
            session.Filters.VideoLowPass05Offset,
            removeLdPalV4300DSpur: scenario == "spur",
            rfHighBoost: boost, diffDemodRepair: repair,
            nonlinearDeemphasis: nonlinear, subDeemphasis: sub,
            referenceFilters: new RfVideoReferenceFilterSet(session.Filters.LdVideoBurst,
                session.Filters.LdVideoBurstOffset, session.Filters.LdVideoPilot, ClipDemodForVideo: true),
            precomputedInputSpectrum: spectrum,
            includeRfHighPassOutput: scenario != "no-high-pass",
            includeAnalyticOutput: retainAnalytic);
    }

    [Theory(DisplayName = "LD stream scratch removes one additional full complex allocation")]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    public void StreamScratchAvoidsAnalyticCopyAllocation(DspBackend backend)
    {
        RequireBackend(backend);
        using DecodeSession session = CreateSession("PAL", backend, audio: false);
        double[] input = BuildInput(session);
        for (int warmup = 0; warmup < 8; warmup++)
        {
            _ = session.Pipeline.DecodePreparedBlock(input, reportDiagnostics: false);
            _ = session.Pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false);
        }
        long full = Measure(streamed: false);
        long compact = Measure(streamed: true);
        Assert.True(full - compact >= Length * 16L,
            $"LD analytic scratch saved {full - compact} bytes; expected at least {Length * 16L}.");

        long Measure(bool streamed)
        {
            long minimum = long.MaxValue;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                RfPipelineBlock block = streamed
                    ? session.Pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false)
                    : session.Pipeline.DecodePreparedBlock(input, reportDiagnostics: false);
                minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
                GC.KeepAlive(block);
            }
            return minimum;
        }
    }

    [Theory(DisplayName = "Parallel LD stream blocks keep scratch and retained diagnostic data independent")]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    public void ParallelStreamScratchKeepsDiagnosticOwnership(DspBackend backend)
    {
        RequireBackend(backend);
        using DecodeSession session = CreateSession("NTSC", backend);
        double[][] inputs = Enumerable.Range(0, 8).Select(index => BuildInput(session, index * 0.17)).ToArray();
        RfDemodulatedBlock retained = session.Pipeline.DecodePreparedBlock(inputs[0], reportDiagnostics: false).Demodulated;
        string retainedBefore = Hash(retained, includeAnalytic: true);
        string[] expected = inputs.Select(input => Hash(session.Pipeline.DecodePreparedBlock(input,
            reportDiagnostics: false).Demodulated, includeAnalytic: false)).ToArray();
        Parallel.For(0, inputs.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            RfDemodulatedBlock actual = session.Pipeline.DecodePreparedStreamBlock(inputs[index], reportDiagnostics: false).Demodulated;
            Assert.Empty(actual.Analytic);
            Assert.Equal(expected[index], Hash(actual, includeAnalytic: false));
        });
        Assert.Equal(retainedBefore, Hash(retained, includeAnalytic: true));
    }

    private static DecodeSession CreateSession(string system, DspBackend backend, bool audio = true)
    {
        var arguments = new List<string> { "--" + system, "--dsp-backend", backend == DspBackend.Exact ? "exact" : "ipp-fast" };
        if (!audio)
        {
            arguments.AddRange(["--noEFM", "--disable_analog_audio"]);
        }
        arguments.AddRange(["probe.s16", "probe-output"]);
        return DecodeSessionFactory.Create(new CommandLineParser().Parse(CliSpecs.LaserDisc, arguments.ToArray()), Length);
    }

    private static double[] BuildInput(DecodeSession session, double offset = 0.0)
    {
        var input = new double[Length];
        double phase = offset;
        double left = session.Filters.LdAnalogAudio?.Left.CenterFrequencyHz ?? 683_593.75;
        double right = session.Filters.LdAnalogAudio?.Right.CenterFrequencyHz ?? 1_066_406.25;
        for (int i = 0; i < input.Length; i++)
        {
            phase += Math.Tau * (8_300_000.0 + (400_000.0 * Math.Sin(i * 0.0017))) / session.DecodeSampleRateHz;
            input[i] = Math.Round((20_000.0 * Math.Cos(phase))
                + (1_500.0 * Math.Cos((Math.Tau * left * i / session.DecodeSampleRateHz) + offset))
                + (1_200.0 * Math.Sin((Math.Tau * right * i / session.DecodeSampleRateHz) - offset)));
        }
        return input;
    }

    private static void AssertRfHighPassMatchesIndependentInverse(DecodeSession session, DspBackend backend,
        double[] input, double[] actual)
    {
        using var demodulator = new RfDemodulator(session.DecodeSampleRateHz, backend);
        Complex[] spectrum = demodulator.ForwardLaserDiscFullSpectrum(input);
        var highPass = new Complex[input.Length];
        NumpyComplexMultiply.Apply(spectrum, session.Filters.RfHighPass, highPass);
        demodulator.InverseLaserDiscFullSpectrumInPlace(highPass);
        Equal(highPass.Select(value => (double)(float)value.Real).ToArray(), actual, "independent RF high-pass");
    }

    private static void EqualChannels(RfDemodulatedBlock expected, RfDemodulatedBlock actual)
    {
        Equal(expected.Video, actual.Video, "Video");
        Equal(expected.DemodRaw, actual.DemodRaw, "DemodRaw");
        Equal(expected.Envelope, actual.Envelope, "Envelope");
        Equal(expected.VideoLowPass, actual.VideoLowPass, "VideoLowPass");
        Equal(expected.RfHighPass, actual.RfHighPass, "RfHighPass");
        Equal(expected.VideoBurst, actual.VideoBurst, "VideoBurst");
        Equal(expected.VideoPilot, actual.VideoPilot, "VideoPilot");
        Equal(expected.Efm, actual.Efm, "Efm");
        Assert.Equal(expected.VhsWeakRfSignal, actual.VhsWeakRfSignal);
        Assert.Equal(expected.AnalogAudio is null, actual.AnalogAudio is null);
        if (expected.AnalogAudio is not null)
        {
            LaserDiscAnalogAudioBlock audio = Assert.IsType<LaserDiscAnalogAudioBlock>(actual.AnalogAudio);
            Equal(expected.AnalogAudio.Left, audio.Left, "audio left");
            Equal(expected.AnalogAudio.Right, audio.Right, "audio right");
            Assert.Equal(expected.AnalogAudio.DecimationFactor, audio.DecimationFactor);
            Assert.Equal(expected.AnalogAudio.UsesFloat32Storage, audio.UsesFloat32Storage);
        }
    }

    private static void Equal<T>(T[]? expected, T[]? actual, string channel) where T : unmanaged
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }
        T[] values = Assert.IsType<T[]>(actual);
        Assert.Equal(expected.Length, values.Length);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(values.AsSpan())),
            $"LD {channel} bits changed.");
    }

    private static string Hash(RfDemodulatedBlock block, bool includeAnalytic)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(block.Video);
        Append(block.DemodRaw);
        Append(block.Envelope);
        Append(block.VideoLowPass);
        Append(block.RfHighPass);
        Append(block.VideoBurst);
        Append(block.VideoPilot);
        if (includeAnalytic) Append(block.Analytic);
        Append(block.Efm);
        if (block.AnalogAudio is not null)
        {
            Append(block.AnalogAudio.Left);
            Append(block.AnalogAudio.Right);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
        void Append<T>(T[]? values) where T : unmanaged
        {
            if (values is not null) hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }
    }

    private static string Hash<T>(T[] values) where T : unmanaged
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));

    private static void RequireBackend(DspBackend backend)
    {
        if (backend == DspBackend.IppFast)
            Assert.SkipUnless(IppTestRuntime.TryProbe(out _), "Intel IPP runtime is unavailable.");
    }
}
