using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscEfmScratchHandoffTests
{
    private const int Length = 32_768;
    private const double SampleRateHz = 40_000_000;

    [Fact]
    public void OriginalCoreNameStillResolvesOneThirtyFiveParameterEntry()
    {
        MethodInfo method = Assert.Single(typeof(RfDemodulator).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            value => value.Name == "DemodulateCore");
        Assert.Equal(35, method.GetParameters().Length);
        Assert.Equal(typeof(ReadOnlySpan<double>), method.GetParameters()[0].ParameterType);
        Assert.Equal("ownedImmediateLaserDiscEnvelopeShape", method.GetParameters()[^1].Name);
        Assert.NotNull(typeof(RfDemodulator).GetMethod("DemodulateCoreForOwnedEfmScratch", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void PublicationAndTakeRequireTheSameOwnerAndClearTheRecipient()
    {
        using var owner = new RfDemodulator(SampleRateHz);
        using var other = new RfDemodulator(SampleRateHz);
        var recipient = new LaserDiscEfmScratchHandoff(owner);
        Complex[] storage = PoisonComplex(Length, 0);
        Assert.Null(recipient.Take(owner));
        recipient.Publish(other, storage);
        Assert.Null(recipient.Take(owner));
        recipient.Publish(owner, null);
        recipient.Publish(owner, new Complex[Length - 1]);
        Assert.Null(recipient.Take(owner));
        recipient.Publish(owner, storage);
        Assert.Same(storage, recipient.Take(owner));
        Assert.Null(recipient.Take(owner));
        recipient.Publish(owner, storage);
        Assert.Null(recipient.Take(other));
        Assert.Null(recipient.Take(owner));
    }

    [Theory]
    [InlineData(DspBackend.Exact, 0, 0)]
    [InlineData(DspBackend.Exact, 1, 1)]
    [InlineData(DspBackend.Exact, 2, 2)]
    [InlineData(DspBackend.Exact, 3, 0)]
    [InlineData(DspBackend.IppFast, 0, 0)]
    [InlineData(DspBackend.IppFast, 1, 1)]
    [InlineData(DspBackend.IppFast, 2, 2)]
    [InlineData(DspBackend.IppFast, 3, 0)]
    public void PublishedDeadSpectrumKeepsEveryLastReaderAndTheOriginalInputSpectrum(DspBackend backend, int references, int boostKind)
    {
        RequireBackend(backend);
        var fixture = new Fixture(backend);
        var configuration = new Configuration(References: references, BoostKind: boostKind);
        Outcome first = Run(fixture, backend, configuration, originalEntry: true);
        Outcome second = Run(fixture, backend, configuration, originalEntry: true);
        Outcome actual = Run(fixture, backend, configuration, originalEntry: false);
        Compare(first, second);
        Compare(second, actual);
        Assert.Null(actual.Error);
        Complex[] dead = Assert.IsType<Complex[]>(actual.Scratch);
        Assert.Equal(Length, dead.Length);
        RfDemodulatedBlock block = Assert.IsType<RfDemodulatedBlock>(actual.Block);
        Assert.Same(actual.Shape, block.Envelope);
        Bits(PoisonShape(Length), block.Envelope);
        Assert.Empty(block.Analytic);
        Assert.NotSame(fixture.Spectrum, dead);
        Assert.NotSame(fixture.Identity, dead);
        Assert.NotSame(fixture.EfmFilter, dead);
        Assert.Equal(references is 0 or 1, block.VideoBurst is not null);
        Assert.Equal(references is 0 or 2, block.VideoPilot is not null);
        RfDemodulatedBlock saved = Snapshot(block);

        using var pipeline = Pipeline(fixture, backend);
        OldEfm oldEfm = Bind<OldEfm>(pipeline, "DecodeEfmBlock");
        OwnedEfm ownedEfm = Bind<OwnedEfm>(pipeline, "DecodeEfmBlockWithOwnedScratch");
        short[] expected = oldEfm(fixture.Spectrum, fixture.EfmFilter);
        Bits(expected, ownedEfm(fixture.Spectrum, fixture.EfmFilter, dead));
        Blocks(saved, block);
        fixture.Unchanged();
        // Leaf EFM/audio are not claimed: those channels are added by the pipeline.
        Assert.Null(block.Efm);
        Assert.Null(block.AnalogAudio);
    }

    [Theory]
    [InlineData(DspBackend.Exact, "null-shape")]
    [InlineData(DspBackend.Exact, "short-shape")]
    [InlineData(DspBackend.Exact, "analytic")]
    [InlineData(DspBackend.Exact, "buffers")]
    [InlineData(DspBackend.Exact, "vhs")]
    [InlineData(DspBackend.Exact, "boost")]
    [InlineData(DspBackend.Exact, "nan-boost")]
    [InlineData(DspBackend.Exact, "infinite-boost")]
    [InlineData(DspBackend.IppFast, "analytic")]
    public void UnsupportedProducerCallsKeepTheOriginalGraphWithoutPublication(DspBackend backend, string scenario)
    {
        RequireBackend(backend);
        var fixture = new Fixture(backend);
        Configuration configuration = scenario switch
        {
            "null-shape" => new(ShapeLength: 0),
            "short-shape" => new(ShapeLength: Length - 1),
            "analytic" => new(IncludeAnalytic: true),
            "buffers" => new(Buffers: true),
            "vhs" => new(Vhs: true),
            "boost" => new(BoostKind: 3),
            "nan-boost" => new(BoostKind: 4),
            _ => new(BoostKind: 5)
        };
        Outcome expected = Run(fixture, backend, configuration, originalEntry: true);
        Outcome actual = Run(fixture, backend, configuration, originalEntry: false);
        if (scenario is "nan-boost" or "infinite-boost")
        {
            SameError(expected.Error, actual.Error);
            if (expected.Error is null)
                SpecialLeafBlocks(Assert.IsType<RfDemodulatedBlock>(expected.Block), Assert.IsType<RfDemodulatedBlock>(actual.Block));
        }
        else Compare(expected, actual);
        Assert.Null(actual.Scratch);
        if (actual.Shape is not null) Bits(PoisonShape(actual.Shape.Length), actual.Shape);
        fixture.Unchanged();
    }

    [Theory]
    [InlineData("bad-rf")]
    [InlineData("bad-reference")]
    public void ProducerErrorsPreserveOriginalPriorityAndNeverPublish(string scenario)
    {
        var fixture = new Fixture(DspBackend.Exact);
        var configuration = new Configuration(BadRf: scenario == "bad-rf", BadReference: scenario == "bad-reference");
        Outcome expected = Run(fixture, DspBackend.Exact, configuration, originalEntry: true);
        Outcome actual = Run(fixture, DspBackend.Exact, configuration, originalEntry: false);
        Assert.NotNull(expected.Error);
        Compare(expected, actual);
        Assert.Null(actual.Scratch);
        Bits(PoisonShape(Length), actual.Shape!);
        fixture.Unchanged();
    }

    [Theory]
    [InlineData(DspBackend.Exact, 0)]
    [InlineData(DspBackend.Exact, 1)]
    [InlineData(DspBackend.Exact, 2)]
    [InlineData(DspBackend.IppFast, 0)]
    [InlineData(DspBackend.IppFast, 1)]
    [InlineData(DspBackend.IppFast, 2)]
    public void EfmFullyOverwritesBothPoisonDestinationsAndMatchesTheOldAllocation(DspBackend backend, int pattern)
    {
        RequireBackend(backend);
        var fixture = new Fixture(backend);
        using var pipeline = Pipeline(fixture, backend);
        Complex[] spectrum = Signal(Length, pattern), response = Response(Length);
        Complex[] spectrumBefore = spectrum.ToArray(), responseBefore = response.ToArray();
        FilterSignal oldSignal = Bind<FilterSignal>(pipeline, "FilterLaserDiscSignal");
        OldEfm oldEfm = Bind<OldEfm>(pipeline, "DecodeEfmBlock");
        OwnedEfm ownedEfm = Bind<OwnedEfm>(pipeline, "DecodeEfmBlockWithOwnedScratch");
        Complex[] expectedState = oldSignal(spectrum, response);
        Bits(expectedState, oldSignal(spectrum, response));
        short[] expected = oldEfm(spectrum, response);
        foreach (int poison in new[] { 0, 1 })
        {
            Complex[] destination = PoisonComplex(Length, poison);
            short[] actual = ownedEfm(spectrum, response, destination);
            Bits(expectedState, destination);
            Bits(expected, actual);
            Assert.All(destination, value => Assert.True(double.IsFinite(value.Real) && double.IsFinite(value.Imaginary)));
            Assert.NotSame(expected, actual);
        }
        Bits(spectrumBefore, spectrum);
        Bits(responseBefore, response);
    }

    [Theory]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    public void ExceptionalSpectrumKeepsTheOriginalInt16EfmResultAndReadonlyInputs(DspBackend backend)
    {
        RequireBackend(backend);
        var fixture = new Fixture(backend);
        using var pipeline = Pipeline(fixture, backend);
        Complex[] spectrum = Signal(Length, 0), response = Response(Length);
        spectrum[0] = new Complex(BitConverter.UInt64BitsToDouble(0x7FF0_1234_5678_9ABC), -0.0);
        spectrum[1] = new Complex(double.PositiveInfinity, double.NegativeInfinity);
        response[7] = new Complex(0.0, BitConverter.UInt64BitsToDouble(0xFFF8_1234_5678_9ABC));
        Complex[] sourceBefore = spectrum.ToArray(), filterBefore = response.ToArray();
        OldEfm oldEfm = Bind<OldEfm>(pipeline, "DecodeEfmBlock");
        OwnedEfm ownedEfm = Bind<OwnedEfm>(pipeline, "DecodeEfmBlockWithOwnedScratch");
        short[] expected = oldEfm(spectrum, response);
        Bits(expected, oldEfm(spectrum, response));
        Bits(expected, ownedEfm(spectrum, response, PoisonComplex(Length, 0)));
        Bits(sourceBefore, spectrum); Bits(filterBefore, response);
        // The observable short output is exact. This does not add a raw NaN
        // payload promise for the internal full-FFT Complex result.
    }

    [Theory]
    [InlineData("source-same")]
    [InlineData("source-shifted-short")]
    [InlineData("filter-same")]
    [InlineData("filter-shifted-short")]
    public void LegalManagedAliasesDeclineWithoutChangingAnySource(string scenario)
    {
        var fixture = new Fixture(DspBackend.Exact);
        using var pipeline = Pipeline(fixture, DspBackend.Exact);
        OldEfm oldEfm = Bind<OldEfm>(pipeline, "DecodeEfmBlock");
        OwnedEfm ownedEfm = Bind<OwnedEfm>(pipeline, "DecodeEfmBlockWithOwnedScratch");
        Complex[] scratch = Signal(Length, 0), spectrum = Signal(Length, 0), filter = Response(Length);
        bool shifted = scenario.EndsWith("shifted-short", StringComparison.Ordinal);
        int count = shifted ? Length - 1 : Length;
        int sourceStart = 0, filterStart = 0;
        if (scenario.StartsWith("source", StringComparison.Ordinal)) { spectrum = scratch; sourceStart = shifted ? 1 : 0; }
        else { filter = scratch; filterStart = shifted ? 1 : 0; }
        Complex[] scratchBefore = scratch.ToArray(), spectrumBefore = spectrum.ToArray(), filterBefore = filter.ToArray();
        short[]? expected = null, actual = null;
        Exception? oldError = Record.Exception(() => expected = oldEfm(spectrum.AsSpan(sourceStart, count), filter.AsSpan(filterStart, count)));
        Exception? newError = Record.Exception(() => actual = ownedEfm(spectrum.AsSpan(sourceStart, count), filter.AsSpan(filterStart, count), scratch));
        SameError(oldError, newError);
        if (oldError is null) Bits(expected!, actual!);
        Bits(scratchBefore, scratch); Bits(spectrumBefore, spectrum); Bits(filterBefore, filter);
        // A shifted span entirely inside Complex[N] is necessarily shorter than N.
        // These two cases prove the shape-decline contract, not a full-N shifted alias.
    }

    [Theory]
    [InlineData("null-scratch")]
    [InlineData("short-scratch")]
    [InlineData("long-scratch")]
    [InlineData("short-filter")]
    [InlineData("long-filter")]
    [InlineData("empty")]
    [InlineData("one")]
    [InlineData("two")]
    [InlineData("odd")]
    public void ShapeDeclinesKeepOldResultsErrorsAndUnwrittenScratch(string scenario)
    {
        var fixture = new Fixture(DspBackend.Exact);
        using var pipeline = Pipeline(fixture, DspBackend.Exact);
        int length = scenario switch { "empty" => 0, "one" => 1, "two" => 2, "odd" => 7, _ => Length };
        Complex[] spectrum = Signal(length, 0);
        Complex[] filter = Response(scenario == "short-filter" ? length - 1 : scenario == "long-filter" ? length + 1 : length);
        Complex[]? scratch = scenario == "null-scratch" ? null : PoisonComplex(scenario == "short-scratch" ? Length - 1 : scenario == "long-scratch" ? Length + 1 : Length, 0);
        Complex[]? scratchBefore = scratch?.ToArray();
        Complex[] sourceBefore = spectrum.ToArray(), filterBefore = filter.ToArray();
        OldEfm oldEfm = Bind<OldEfm>(pipeline, "DecodeEfmBlock");
        OwnedEfm ownedEfm = Bind<OwnedEfm>(pipeline, "DecodeEfmBlockWithOwnedScratch");
        short[]? expected = null, actual = null;
        Exception? oldError = Record.Exception(() => expected = oldEfm(spectrum, filter));
        Exception? newError = Record.Exception(() => actual = ownedEfm(spectrum, filter, scratch));
        SameError(oldError, newError);
        if (scenario.EndsWith("filter", StringComparison.Ordinal)) Assert.Equal("filter", Assert.IsAssignableFrom<ArgumentException>(oldError).ParamName);
        if (oldError is null) Bits(expected!, actual!);
        if (scratch is not null) Bits(scratchBefore!, scratch);
        Bits(sourceBefore, spectrum); Bits(filterBefore, filter);
    }

    [Theory]
    [InlineData(DspBackend.Exact, true)]
    [InlineData(DspBackend.IppFast, true)]
    [InlineData(DspBackend.Exact, false)]
    public void ActualImmediatePipelineKeepsEfmAnalogAndRetainedPublicOutputs(DspBackend backend, bool hasEfm)
    {
        RequireBackend(backend);
        var fixture = new Fixture(backend);
        using var pipeline = Pipeline(fixture, backend, hasEfm);
        Assert.True(pipeline.CanOmitEnvelopeForOwnedImmediateLaserDisc);
        RfDemodulatedBlock full = pipeline.DecodePreparedBlock(fixture.Input, reportDiagnostics: false).Demodulated;
        RfDemodulatedBlock saved = Snapshot(full);
        RfDemodulatedBlock ordinary = pipeline.DecodePreparedStreamBlock(fixture.Input, reportDiagnostics: false).Demodulated;
        double[] shape = PoisonShape(Length);
        RfDemodulatedBlock owned = pipeline.DecodePreparedStreamBlock(fixture.Input, reportDiagnostics: false,
            ownedImmediateEnvelopeShape: shape).Demodulated;
        Assert.Same(shape, owned.Envelope);
        Bits(PoisonShape(Length), shape);
        Assert.Empty(owned.Analytic);
        Blocks(ordinary, owned, includeEnvelope: false);
        Assert.Equal(hasEfm, owned.Efm is not null);
        Assert.NotNull(owned.AnalogAudio);
        _ = pipeline.DecodePreparedStreamBlock(fixture.Input, reportDiagnostics: false,
            ownedImmediateEnvelopeShape: PoisonShape(Length));
        Blocks(saved, full);
        fixture.Unchanged();
        // Branch eligibility comes from the frozen source/producer publication tests;
        // equal output alone is not claimed as a pipeline dispatch counter.
    }

    [Theory]
    [InlineData(DspBackend.Exact)]
    [InlineData(DspBackend.IppFast)]
    public async Task ConcurrentInvocationsKeepRecipientsAndRetainedChannelsIndependent(DspBackend backend)
    {
        RequireBackend(backend);
        var fixture = new Fixture(backend);
        Outcome expected = Run(fixture, backend, new(), originalEntry: true);
        Outcome[] actual = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => Run(fixture, backend, new(), originalEntry: false), TestContext.Current.CancellationToken)));
        foreach (Outcome value in actual) { Compare(expected, value); Assert.NotNull(value.Scratch); }
        for (int i = 0; i < actual.Length; i++)
            for (int j = i + 1; j < actual.Length; j++) Assert.NotSame(actual[i].Scratch, actual[j].Scratch);
        RfDemodulatedBlock saved = Snapshot(Assert.IsType<RfDemodulatedBlock>(actual[0].Block));
        actual[1].Scratch![0] = new Complex(-12345, 67890);
        Blocks(saved, Assert.IsType<RfDemodulatedBlock>(actual[0].Block));
        fixture.Unchanged();
    }

    private sealed record Configuration(int References = 0, int BoostKind = 0, int ShapeLength = Length,
        bool IncludeAnalytic = false, bool Buffers = false, bool Vhs = false, bool BadRf = false, bool BadReference = false);
    private sealed record Outcome(RfDemodulatedBlock? Block, Complex[]? Scratch, double[]? Shape, Exception? Error);

    private static Outcome Run(Fixture fixture, DspBackend backend, Configuration configuration, bool originalEntry)
    {
        using var owner = new RfDemodulator(SampleRateHz, backend);
        var recipient = new LaserDiscEfmScratchHandoff(owner);
        double[]? shape = configuration.ShapeLength == 0 ? null : PoisonShape(configuration.ShapeLength);
        try
        {
            RfDemodulatedBlock block = originalEntry
                ? OldCore(owner, fixture, configuration, shape)
                : OwnedCore(owner, fixture, configuration, shape, recipient);
            return new(block, recipient.Take(owner), shape, null);
        }
        catch (Exception error) { return new(null, recipient.Take(owner), shape, error); }
    }

    private static RfVideoReferenceFilterSet References(Fixture fixture, Configuration configuration)
        => new(configuration.BadReference ? fixture.ShortFilter : configuration.References is 0 or 1 ? fixture.Identity : null,
            7, configuration.References is 0 or 2 ? fixture.Identity : null, ClipDemodForVideo: true);

    private static RfHighBoostOptions? Boost(Configuration configuration) => configuration.BoostKind switch
    {
        0 => null, 1 => new(0.0, 8_000_000, 16_000_000), 2 => new(-0.0, 8_000_000, 16_000_000),
        3 => new(0.75, 8_000_000, 16_000_000), 4 => new(double.NaN, 8_000_000, 16_000_000),
        _ => new(double.PositiveInfinity, 8_000_000, 16_000_000)
    };

    private static RfDemodulatedBlock OldCore(RfDemodulator owner, Fixture fixture, Configuration configuration, double[]? shape)
        => owner.DemodulateCore(fixture.Input, configuration.BadRf ? fixture.ShortFilter : fixture.Identity,
            fixture.Identity, fixture.Identity, fixture.Identity, fixture.Identity,
            videoLowPassOffset: 13, removeLdPalV4300DSpur: false, rfHighBoost: Boost(configuration),
            diffDemodRepair: null, chromaTrap: null, sharpnessEq: null, nonlinearDeemphasis: null,
            subDeemphasis: null, betamaxFscNotchHz: null, referenceFilters: References(fixture, configuration),
            fmDemodulatorMode: configuration.Vhs ? RfFmDemodulatorMode.VhsRustApproximation : RfFmDemodulatorMode.ConjugateProduct,
            vhsEnvelopeFilter: configuration.Vhs ? [new SosSection(1, 0, 0, 1, 0, 0)] : null,
            vhsRfTopFilter: null, precomputedInputSpectrum: fixture.Spectrum,
            includeRfHighPassOutput: true, includeAnalyticOutput: configuration.IncludeAnalytic,
            includeDemodRawOutput: true, useNumpyComplexVhsAnalytic: false,
            outputBuffers: configuration.Buffers ? new RfDemodulatedBlockOutputBuffers(Length) : null,
            ownedImmediateLaserDiscEnvelopeShape: shape);

    private static RfDemodulatedBlock OwnedCore(RfDemodulator owner, Fixture fixture, Configuration configuration,
        double[]? shape, LaserDiscEfmScratchHandoff recipient)
        => owner.DemodulateCoreForOwnedEfmScratch(recipient,
            fixture.Input, configuration.BadRf ? fixture.ShortFilter : fixture.Identity,
            fixture.Identity, fixture.Identity, fixture.Identity, fixture.Identity,
            videoLowPassOffset: 13, removeLdPalV4300DSpur: false, rfHighBoost: Boost(configuration),
            diffDemodRepair: null, chromaTrap: null, sharpnessEq: null, nonlinearDeemphasis: null,
            subDeemphasis: null, betamaxFscNotchHz: null, referenceFilters: References(fixture, configuration),
            fmDemodulatorMode: configuration.Vhs ? RfFmDemodulatorMode.VhsRustApproximation : RfFmDemodulatorMode.ConjugateProduct,
            vhsEnvelopeFilter: configuration.Vhs ? [new SosSection(1, 0, 0, 1, 0, 0)] : null,
            vhsRfTopFilter: null, precomputedInputSpectrum: fixture.Spectrum,
            includeRfHighPassOutput: true, includeAnalyticOutput: configuration.IncludeAnalytic,
            includeDemodRawOutput: true, useNumpyComplexVhsAnalytic: false,
            outputBuffers: configuration.Buffers ? new RfDemodulatedBlockOutputBuffers(Length) : null,
            ownedImmediateLaserDiscEnvelopeShape: shape);

    private static RfBlockDecodePipeline Pipeline(Fixture fixture, DspBackend backend, bool hasEfm = true)
    {
        Complex[] audio = Response(1024);
        var analog = new LaserDiscAnalogAudioFilterSet(
            new(0, 1024, 1_250_000, 0, 2_300_000, audio, audio),
            new(0, 1024, 1_250_000, 0, 2_800_000, audio, audio), 32);
        var filters = new DecodeFilterSet(fixture.Identity, fixture.Identity, fixture.Identity, fixture.Identity,
            fixture.Identity, fixture.Identity, hasEfm ? fixture.EfmFilter : null,
            [], [], [], [], [], [], null, LdAnalogAudio: analog,
            LdVideoBurst: fixture.Identity, LdVideoPilot: fixture.Identity)
            { VideoLowPass05Offset = 13, LdVideoBurstOffset = 7 };
        return new(new NeverReadLoader(), filters, SampleRateHz,
            new DecodeFilterOptions(LdClipDemodForVideo: true), dspBackend: backend);
    }

    private delegate short[] OldEfm(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<Complex> filter);
    private delegate short[] OwnedEfm(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<Complex> filter, Complex[]? scratch);
    private delegate Complex[] FilterSignal(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<Complex> filter);

    private static T Bind<T>(RfBlockDecodePipeline pipeline, string name) where T : Delegate
        => (T)(typeof(RfBlockDecodePipeline).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing EFM method: " + name)).CreateDelegate(typeof(T), pipeline);

    private static void Compare(Outcome expected, Outcome actual)
    {
        SameError(expected.Error, actual.Error);
        if (expected.Error is null) Blocks(Assert.IsType<RfDemodulatedBlock>(expected.Block), Assert.IsType<RfDemodulatedBlock>(actual.Block));
        else { Assert.Null(expected.Block); Assert.Null(actual.Block); }
    }

    private static void SameError(Exception? expected, Exception? actual)
    {
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal(expected.HResult, actual.HResult);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal((expected as ArgumentException)?.ParamName, (actual as ArgumentException)?.ParamName);
    }

    private static void SpecialLeafBlocks(RfDemodulatedBlock expected, RfDemodulatedBlock actual)
    {
        // Only the two special high-boost fallback graphs use the pre-existing
        // scalar FFT NaN contract; all defined values still require exact bits.
        FftDefined(expected.Video, actual.Video); FftDefined(expected.DemodRaw, actual.DemodRaw);
        FftDefined(expected.Envelope, actual.Envelope); FftDefined(expected.VideoLowPass, actual.VideoLowPass);
        FftDefined(expected.RfHighPass, actual.RfHighPass);
        NullableFftDefined(expected.VideoBurst, actual.VideoBurst); NullableFftDefined(expected.VideoPilot, actual.VideoPilot);
        NullableFftDefined(expected.Chroma, actual.Chroma);
        Assert.Empty(expected.Analytic); Assert.Empty(actual.Analytic);
        Assert.Null(expected.Efm); Assert.Null(actual.Efm); Assert.Null(expected.AnalogAudio); Assert.Null(actual.AnalogAudio);
        NullableBits(expected.VideoFloat32, actual.VideoFloat32); NullableBits(expected.VideoLowPassFloat32, actual.VideoLowPassFloat32);
        NullableBits(expected.ChromaFloat32, actual.ChromaFloat32); Assert.Equal(expected.VhsWeakRfSignal, actual.VhsWeakRfSignal);
    }

    private static void NullableFftDefined(double[]? expected, double[]? actual)
    {
        if (expected is null) Assert.Null(actual);
        else FftDefined(expected, Assert.IsType<double[]>(actual));
    }

    private static void FftDefined(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            if (double.IsNaN(expected[index]) && double.IsNaN(actual[index])) continue;
            Assert.Equal(BitConverter.DoubleToUInt64Bits(expected[index]), BitConverter.DoubleToUInt64Bits(actual[index]));
        }
    }

    private static void Blocks(RfDemodulatedBlock expected, RfDemodulatedBlock actual, bool includeEnvelope = true)
    {
        Bits(expected.Video, actual.Video); Bits(expected.DemodRaw, actual.DemodRaw); Bits(expected.Analytic, actual.Analytic);
        if (includeEnvelope) Bits(expected.Envelope, actual.Envelope);
        Bits(expected.VideoLowPass, actual.VideoLowPass); Bits(expected.RfHighPass, actual.RfHighPass);
        NullableBits(expected.VideoBurst, actual.VideoBurst); NullableBits(expected.VideoPilot, actual.VideoPilot);
        NullableBits(expected.Chroma, actual.Chroma); NullableBits(expected.Efm, actual.Efm);
        NullableBits(expected.VideoFloat32, actual.VideoFloat32); NullableBits(expected.VideoLowPassFloat32, actual.VideoLowPassFloat32);
        NullableBits(expected.ChromaFloat32, actual.ChromaFloat32); Assert.Equal(expected.VhsWeakRfSignal, actual.VhsWeakRfSignal);
        Assert.Equal(expected.AnalogAudio is null, actual.AnalogAudio is null);
        if (expected.AnalogAudio is { } audio)
        {
            LaserDiscAnalogAudioBlock other = Assert.IsType<LaserDiscAnalogAudioBlock>(actual.AnalogAudio);
            Bits(audio.Left, other.Left); Bits(audio.Right, other.Right);
            Assert.Equal(audio.DecimationFactor, other.DecimationFactor); Assert.Equal(audio.UsesFloat32Storage, other.UsesFloat32Storage);
        }
    }

    private static RfDemodulatedBlock Snapshot(RfDemodulatedBlock block)
        => block with { Video = block.Video.ToArray(), DemodRaw = block.DemodRaw.ToArray(), Analytic = block.Analytic.ToArray(),
            Envelope = block.Envelope.ToArray(), VideoLowPass = block.VideoLowPass.ToArray(), RfHighPass = block.RfHighPass.ToArray(),
            VideoBurst = block.VideoBurst?.ToArray(), VideoPilot = block.VideoPilot?.ToArray(), Chroma = block.Chroma?.ToArray(), Efm = block.Efm?.ToArray(),
            VideoFloat32 = block.VideoFloat32?.ToArray(), VideoLowPassFloat32 = block.VideoLowPassFloat32?.ToArray(), ChromaFloat32 = block.ChromaFloat32?.ToArray(),
            AnalogAudio = block.AnalogAudio is { } audio ? audio with { Left = audio.Left.ToArray(), Right = audio.Right.ToArray() } : null };

    private static void NullableBits<T>(T[]? expected, T[]? actual) where T : unmanaged
    {
        if (expected is null) Assert.Null(actual);
        else Bits(expected, Assert.IsType<T[]>(actual));
    }

    private static void Bits<T>(T[] expected, T[] actual) where T : unmanaged
        => Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));

    private static Complex[] Signal(int length, int pattern)
        => Enumerable.Range(0, length).Select(index => pattern switch
        {
            1 => new Complex(index % 2 == 0 ? -0.0 : 0.0, index % 3 == 0 ? -0.0 : 0.0),
            2 => new Complex(index % 2 == 0 ? double.Epsilon : -double.Epsilon, 0.0),
            _ => new Complex((index * 73 % 997 - 498) * 0.125, (index * 97 % 991 - 495) * 0.0625)
        }).ToArray();

    private static Complex[] Response(int length)
        => Enumerable.Range(0, length).Select(index => new Complex(0.5 + index % 17 / 64.0, (index % 11 - 5) / 128.0)).ToArray();

    private static Complex[] PoisonComplex(int length, int variant)
        => Enumerable.Range(0, length).Select(index => new Complex(
            BitConverter.UInt64BitsToDouble((variant == 0 ? 0x7FF8_0123_0000_0000UL : 0xFFF8_0456_0000_0000UL) | (uint)(index + 1)),
            BitConverter.UInt64BitsToDouble((variant == 0 ? 0xFFF8_0789_0000_0000UL : 0x7FF8_0ABC_0000_0000UL) | (uint)(index + 1)))).ToArray();

    private static double[] PoisonShape(int length)
        => Enumerable.Range(0, length).Select(index => BitConverter.UInt64BitsToDouble(0x7FF8_0123_0000_0000UL | (uint)(index + 1))).ToArray();

    private static void RequireBackend(DspBackend backend)
    {
        if (backend == DspBackend.IppFast) Assert.SkipUnless(IppTestRuntime.TryProbe(out _), "Intel IPP runtime is unavailable.");
    }

    private sealed class Fixture
    {
        internal readonly double[] Input;
        internal readonly Complex[] Identity = RfDemodulator.IdentityFilter(Length);
        internal readonly Complex[] ShortFilter = RfDemodulator.IdentityFilter(Length - 1);
        internal readonly Complex[] EfmFilter = Response(Length);
        internal readonly Complex[] Spectrum;
        private readonly double[] _inputBefore;
        private readonly Complex[][] _arrays;
        private readonly Complex[][] _before;

        internal Fixture(DspBackend backend)
        {
            Input = new double[Length]; double phase = 0;
            for (int index = 0; index < Input.Length; index++)
            {
                double linePhase = (index % 2560) / 2560.0;
                double video = 0.45 * Math.Sin(Math.Tau * linePhase) + (linePhase < 0.075 ? -0.75 : 0.0);
                phase += Math.Tau * (3_800_000.0 + 650_000.0 * video) / SampleRateHz;
                Input[index] = 12_000.0 * Math.Cos(phase) + 1_300.0 * Math.Cos(Math.Tau * 627_000.0 * index / SampleRateHz);
            }
            using var demodulator = new RfDemodulator(SampleRateHz, backend);
            Spectrum = demodulator.ForwardLaserDiscFullSpectrum(Input);
            _inputBefore = Input.ToArray(); _arrays = [Identity, ShortFilter, EfmFilter, Spectrum];
            _before = _arrays.Select(values => values.ToArray()).ToArray();
        }

        internal void Unchanged()
        {
            Bits(_inputBefore, Input);
            for (int index = 0; index < _arrays.Length; index++) Bits(_before[index], _arrays[index]);
        }
    }

    private sealed class NeverReadLoader : IRfSampleLoader
    {
        public double[]? Read(Stream stream, long sample, int readLength) => throw new InvalidOperationException("No loader read expected.");
    }
}
