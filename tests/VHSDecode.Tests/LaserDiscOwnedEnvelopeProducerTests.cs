using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

[Collection(DspWorkingBufferCollection.Name)]
public sealed class LaserDiscOwnedEnvelopeProducerTests
{
    private const int Length = 32768;
    private const double SampleRateHz = 40_000_000;

    [Theory]
    [InlineData(DspBackend.Exact, 0)]
    [InlineData(DspBackend.Exact, 1)]
    [InlineData(DspBackend.Exact, 2)]
    [InlineData(DspBackend.IppFast, 0)]
    [InlineData(DspBackend.IppFast, 1)]
    [InlineData(DspBackend.IppFast, 2)]
    public void AcceptedShapeIsReturnedUnwrittenAndEveryOtherLeafChannelKeepsFullBits(DspBackend backend, int boostKind)
    {
        RequireBackend(backend);
        var fixture = new Fixture();
        var configuration = new Configuration(Boost: boostKind == 0 ? null : Boost(boostKind == 1 ? 0.0 : -0.0));
        double[] shape = Poison(Length), shapeBefore = shape.ToArray();
        Outcome first = Run(fixture, backend, configuration, null);
        Outcome second = Run(fixture, backend, configuration, null);
        Outcome owned = Run(fixture, backend, configuration, shape);
        Assert.Null(first.Error); Assert.Null(second.Error); Assert.Null(owned.Error);
        Equivalent(first, second, includeEnvelope: true, "fresh full self");
        Equivalent(second, owned, includeEnvelope: false, "owned versus full");
        RfDemodulatedBlock actual = Assert.IsType<RfDemodulatedBlock>(owned.Block);
        RfDemodulatedBlock full = Assert.IsType<RfDemodulatedBlock>(second.Block);
        Assert.Same(shape, actual.Envelope);
        Equal(shapeBefore, shape, "accepted shape must not be written");
        Assert.NotSame(shape, full.Envelope);
        Assert.All(full.Envelope, value => Assert.True(double.IsFinite(value)));
        Assert.Equal(Length, actual.Video.Length); Assert.Equal(Length, actual.DemodRaw.Length);
        Assert.Equal(Length, actual.VideoLowPass.Length); Assert.Equal(Length, actual.RfHighPass.Length);
        Assert.Equal(Length, Assert.IsType<double[]>(actual.VideoBurst).Length);
        Assert.Equal(Length, Assert.IsType<double[]>(actual.VideoPilot).Length);
        Assert.Empty(actual.Analytic);
        Assert.Equal(backend == DspBackend.IppFast, owned.UsesIppComplexFft);
        // EFM/analog audio are added by RfBlockDecodePipeline, not this leaf.
        Assert.Null(full.Efm); Assert.Null(actual.Efm);
        Assert.Null(full.AnalogAudio); Assert.Null(actual.AnalogAudio);
        fixture.Unchanged();
    }

    [Theory]
    [InlineData(DspBackend.Exact, "null-shape")]
    [InlineData(DspBackend.Exact, "empty-shape")]
    [InlineData(DspBackend.Exact, "short-shape")]
    [InlineData(DspBackend.Exact, "long-shape")]
    [InlineData(DspBackend.Exact, "analytic")]
    [InlineData(DspBackend.Exact, "buffers")]
    [InlineData(DspBackend.Exact, "no-reference")]
    [InlineData(DspBackend.Exact, "unclipped")]
    [InlineData(DspBackend.Exact, "vhs-mode")]
    [InlineData(DspBackend.Exact, "boost")]
    [InlineData(DspBackend.Exact, "nan-boost")]
    [InlineData(DspBackend.Exact, "infinite-boost")]
    [InlineData(DspBackend.IppFast, "analytic")]
    [InlineData(DspBackend.IppFast, "buffers")]
    public void DeclinedShapeKeepsOriginalFullOutputGraph(DspBackend backend, string scenario)
    {
        RequireBackend(backend);
        var fixture = new Fixture();
        Configuration configuration = ForScenario(scenario);
        double[]? shape = scenario switch
        {
            "null-shape" => null,
            "empty-shape" => [],
            "short-shape" => Poison(Length - 1),
            "long-shape" => Poison(Length + 1),
            _ => Poison(Length)
        };
        double[]? shapeBefore = shape?.ToArray();
        Outcome first = Run(fixture, backend, configuration, null);
        Outcome second = Run(fixture, backend, configuration, null);
        Outcome actual = Run(fixture, backend, configuration, shape);
        Assert.Null(first.Error); Assert.Null(second.Error); Assert.Null(actual.Error);
        Equivalent(first, second, includeEnvelope: true, "fresh full self");
        Equivalent(second, actual, includeEnvelope: true, "declined versus full");
        RfDemodulatedBlock block = Assert.IsType<RfDemodulatedBlock>(actual.Block);
        Assert.Equal(Length, block.Envelope.Length);
        if (shape is not null)
        {
            Assert.NotSame(shape, block.Envelope);
            Equal(shapeBefore!, shape, "declined shape must not be written");
        }
        if (configuration.Buffers)
            Assert.Same(Assert.IsType<RfDemodulatedBlockOutputBuffers>(actual.Buffers).Envelope, block.Envelope);
        fixture.Unchanged();
    }

    [Theory]
    [InlineData(DspBackend.Exact, "bad-output-buffer", "outputBuffers")]
    [InlineData(DspBackend.Exact, "bad-rf-filter", "filter")]
    [InlineData(DspBackend.Exact, "bad-boost-band", "options")]
    [InlineData(DspBackend.Exact, "unknown-mode", "mode")]
    [InlineData(DspBackend.IppFast, "bad-output-buffer", "outputBuffers")]
    public void OriginalValidationAndExceptionPriorityPrecedeShapeOmission(DspBackend backend, string scenario, string parameter)
    {
        RequireBackend(backend);
        var fixture = new Fixture();
        Configuration configuration = ForScenario(scenario);
        double[] shape = Poison(Length), shapeBefore = shape.ToArray();
        Outcome first = Run(fixture, backend, configuration, null);
        Outcome second = Run(fixture, backend, configuration, null);
        Outcome actual = Run(fixture, backend, configuration, shape);
        Assert.Equal(parameter, Assert.IsAssignableFrom<ArgumentException>(first.Error).ParamName);
        Equivalent(first, second, includeEnvelope: true, "fresh full exception self");
        Equivalent(second, actual, includeEnvelope: true, "owned exception versus full");
        Equal(shapeBefore, shape, "failed call must not write shape");
        fixture.Unchanged();
    }

    [Fact]
    public void EmptyInputReturnsOriginalEmptyBlockWithoutObservingShape()
    {
        var fixture = new Fixture(emptyInput: true);
        double[] shape = Poison(Length), before = shape.ToArray();
        Outcome first = Run(fixture, DspBackend.Exact, new(), null);
        Outcome second = Run(fixture, DspBackend.Exact, new(), null);
        Outcome actual = Run(fixture, DspBackend.Exact, new(), shape);
        Assert.Null(first.Error); Assert.Null(second.Error); Assert.Null(actual.Error);
        Equivalent(first, second, true, "empty full self");
        Equivalent(second, actual, true, "empty owned versus full");
        Assert.Empty(Assert.IsType<RfDemodulatedBlock>(actual.Block).Envelope);
        Equal(before, shape, "empty input shape");
        fixture.Unchanged();
    }

    private sealed record Configuration(bool IncludeAnalytic = false, bool Buffers = false,
        bool WrongBufferLength = false, bool BadRfFilter = false, bool HasReference = true,
        bool Clip = true, RfFmDemodulatorMode Mode = RfFmDemodulatorMode.ConjugateProduct,
        RfHighBoostOptions? Boost = null);

    private sealed record Outcome(RfDemodulatedBlock? Block, Exception? Error,
        RfDemodulatedBlockOutputBuffers? Buffers, bool UsesIppComplexFft);

    private static Configuration ForScenario(string scenario) => scenario switch
    {
        "analytic" => new(IncludeAnalytic: true),
        "buffers" => new(Buffers: true),
        "no-reference" => new(HasReference: false),
        "unclipped" => new(Clip: false),
        "vhs-mode" => new(Mode: RfFmDemodulatorMode.VhsRustApproximation),
        "boost" => new(Boost: Boost(0.75)),
        "nan-boost" => new(Boost: Boost(double.NaN)),
        "infinite-boost" => new(Boost: Boost(double.PositiveInfinity)),
        "bad-output-buffer" => new(Buffers: true, WrongBufferLength: true),
        "bad-rf-filter" => new(BadRfFilter: true),
        "bad-boost-band" => new(Boost: new RfHighBoostOptions(0.75, -1, 16_000_000)),
        "unknown-mode" => new(Mode: (RfFmDemodulatorMode)1234),
        _ => new()
    };

    private static RfHighBoostOptions Boost(double multiplier) => new(multiplier, 8_000_000, 16_000_000);

    private static Outcome Run(Fixture fixture, DspBackend backend, Configuration configuration, double[]? shape)
    {
        RfDemodulatedBlockOutputBuffers? buffers = configuration.Buffers
            ? new RfDemodulatedBlockOutputBuffers(configuration.WrongBufferLength ? Length - 1 : Length)
            : null;
        if (buffers is not null)
        {
            PoisonInto(buffers.Video); PoisonInto(buffers.Envelope); PoisonInto(buffers.VideoLowPass);
        }
        bool usesIpp = false;
        try
        {
            using var demodulator = new RfDemodulator(SampleRateHz, backend);
            usesIpp = demodulator.UsesIppComplexFft;
            RfDemodulatedBlock block = demodulator.DemodulateCore(
                fixture.Input, configuration.BadRfFilter ? fixture.ShortFilter : fixture.Identity,
                fixture.Identity, fixture.Identity, fixture.Identity, fixture.Identity,
                videoLowPassOffset: 0, removeLdPalV4300DSpur: false,
                rfHighBoost: configuration.Boost, diffDemodRepair: null, chromaTrap: null,
                sharpnessEq: null, nonlinearDeemphasis: null, subDeemphasis: null,
                betamaxFscNotchHz: null,
                referenceFilters: configuration.HasReference
                    ? new RfVideoReferenceFilterSet(fixture.Identity, 0, fixture.Identity, configuration.Clip)
                    : null,
                fmDemodulatorMode: configuration.Mode,
                vhsEnvelopeFilter: configuration.Mode == RfFmDemodulatorMode.VhsRustApproximation
                    ? [new SosSection(1, 0, 0, 1, 0, 0)] : null,
                vhsRfTopFilter: null, precomputedInputSpectrum: null,
                includeRfHighPassOutput: true, includeAnalyticOutput: configuration.IncludeAnalytic,
                includeDemodRawOutput: true, useNumpyComplexVhsAnalytic: false,
                outputBuffers: buffers, ownedImmediateLaserDiscEnvelopeShape: shape);
            return new(block, null, buffers, usesIpp);
        }
        catch (Exception error)
        {
            return new(null, error, buffers, usesIpp);
        }
    }

    private static void Equivalent(Outcome expected, Outcome actual, bool includeEnvelope, string label)
    {
        if (expected.Error is { } error)
        {
            Exception other = Assert.IsAssignableFrom<Exception>(actual.Error);
            Assert.Equal(error.GetType(), other.GetType()); Assert.Equal(error.Message, other.Message);
            Assert.Equal(error.HResult, other.HResult);
            Assert.Equal((error as ArgumentException)?.ParamName, (other as ArgumentException)?.ParamName);
            Assert.Null(expected.Block); Assert.Null(actual.Block);
        }
        else
        {
            Assert.Null(actual.Error);
            RfDemodulatedBlock a = Assert.IsType<RfDemodulatedBlock>(expected.Block);
            RfDemodulatedBlock b = Assert.IsType<RfDemodulatedBlock>(actual.Block);
            Equal(a.Video, b.Video, label + " Video"); Equal(a.DemodRaw, b.DemodRaw, label + " DemodRaw");
            Equal(a.Analytic, b.Analytic, label + " Analytic");
            if (includeEnvelope) Equal(a.Envelope, b.Envelope, label + " Envelope");
            Equal(a.VideoLowPass, b.VideoLowPass, label + " LP"); Equal(a.RfHighPass, b.RfHighPass, label + " HP");
            NullableEqual(a.Chroma, b.Chroma, label + " Chroma");
            NullableEqual(a.VideoBurst, b.VideoBurst, label + " Burst"); NullableEqual(a.VideoPilot, b.VideoPilot, label + " Pilot");
            NullableEqual(a.Efm, b.Efm, label + " EFM");
            Assert.Null(a.AnalogAudio); Assert.Null(b.AnalogAudio);
            NullableEqual(a.VideoFloat32, b.VideoFloat32, label + " Video32");
            NullableEqual(a.VideoLowPassFloat32, b.VideoLowPassFloat32, label + " LP32");
            NullableEqual(a.ChromaFloat32, b.ChromaFloat32, label + " Chroma32");
            Assert.Equal(a.VhsWeakRfSignal, b.VhsWeakRfSignal);
        }
        NullableEqual(expected.Buffers?.Video, actual.Buffers?.Video, label + " pooled Video state");
        NullableEqual(expected.Buffers?.Envelope, actual.Buffers?.Envelope, label + " pooled Envelope state");
        NullableEqual(expected.Buffers?.VideoLowPass, actual.Buffers?.VideoLowPass, label + " pooled LP state");
    }

    private static void NullableEqual<T>(T[]? expected, T[]? actual, string label) where T : unmanaged
    {
        if (expected is null) Assert.Null(actual);
        else Equal(expected, Assert.IsType<T[]>(actual), label);
    }

    private static void Equal<T>(T[] expected, T[] actual, string label) where T : unmanaged
        => Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())), label);

    private static double[] Poison(int length)
    {
        var values = new double[length]; PoisonInto(values); return values;
    }

    private static void PoisonInto(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ulong bits = (i % 4) switch
            {
                0 => 0x7FF8_0123_0000_0000UL | (uint)(i + 1),
                1 => 0xFFF0_0456_0000_0000UL | (uint)(i + 1),
                2 => 0x8000_0000_0000_0000UL,
                _ => 0x3FF0_0000_0000_0000UL | (uint)(i + 1)
            };
            values[i] = BitConverter.UInt64BitsToDouble(bits);
        }
    }

    private static void RequireBackend(DspBackend backend)
    {
        if (backend == DspBackend.IppFast)
            Assert.SkipUnless(IppTestRuntime.TryProbe(out _), "Intel IPP runtime is unavailable.");
    }

    private sealed class Fixture
    {
        internal double[] Input { get; }
        internal Complex[] Identity { get; } = RfDemodulator.IdentityFilter(Length);
        internal Complex[] ShortFilter { get; } = RfDemodulator.IdentityFilter(Length - 1);
        private readonly double[] _inputBefore;
        private readonly Complex[] _identityBefore;
        private readonly Complex[] _shortBefore;

        internal Fixture(bool emptyInput = false)
        {
            Input = emptyInput ? [] : BuildInput();
            _inputBefore = Input.ToArray(); _identityBefore = Identity.ToArray(); _shortBefore = ShortFilter.ToArray();
        }

        internal void Unchanged()
        {
            Equal(_inputBefore, Input, "input readonly"); Equal(_identityBefore, Identity, "filter readonly");
            Equal(_shortBefore, ShortFilter, "invalid filter readonly");
        }

        // Same bounded 32K modulated identity fixture as IppRfDemodulatorDifferentialTests.
        private static double[] BuildInput()
        {
            var input = new double[Length]; double phase = 0.0;
            for (int i = 0; i < input.Length; i++)
            {
                double linePhase = (i % 2560) / 2560.0;
                double video = 0.45 * Math.Sin(Math.Tau * linePhase) + (linePhase < 0.075 ? -0.75 : 0.0);
                phase += Math.Tau * (3_800_000.0 + 650_000.0 * video) / SampleRateHz;
                input[i] = 12_000.0 * Math.Cos(phase) + 1_300.0 * Math.Cos(Math.Tau * 627_000.0 * i / SampleRateHz);
            }
            return input;
        }
    }
}
