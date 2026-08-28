using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using VHSDecode.Core.Rf;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ApproxFastBackendTests
{
    private const int Length = 32_768;
    private const double SampleRateHz = 40_000_000.0;

    [Fact(DisplayName = "Approx managed VHS RF transform is finite, deterministic, and distinct from Exact")]
    public void ManagedRfTransformIsFiniteDeterministicAndDistinctFromExact()
    {
        double[] input = BuildPalVhsProbe();
        Complex[] identity = RfDemodulator.IdentityFilter(Length);
        SosSection[] identitySos =
        [
            new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
        ];

        using var exactDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.Exact);
        using var approxDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            ApproxProvider.Managed);

        RfDemodulatedBlock exact = Decode(
            exactDemodulator,
            input,
            identity,
            identitySos);
        RfDemodulatedBlock first = Decode(
            approxDemodulator,
            input,
            identity,
            identitySos);
        RfDemodulatedBlock second = Decode(
            approxDemodulator,
            input,
            identity,
            identitySos);

        Assert.Equal(ApproxProvider.Managed, approxDemodulator.ApproxProvider);
        Assert.NotEqual(Hash(exact), Hash(first));
        Assert.Equal(Hash(first), Hash(second));
        AssertApproxQuality(exact, first);
    }

    [Fact(DisplayName = "Approx IPP VHS RF transform satisfies the same float32 contract as managed")]
    public void IppRfTransformSatisfiesManagedFloat32Contract()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildPalVhsProbe();
        Complex[] identity = RfDemodulator.IdentityFilter(Length);
        SosSection[] identitySos =
        [
            new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
        ];

        using var managedDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            ApproxProvider.Managed);
        using var ippDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            ApproxProvider.Ipp);

        RfDemodulatedBlock managed = Decode(
            managedDemodulator,
            input,
            identity,
            identitySos);
        RfDemodulatedBlock first = Decode(
            ippDemodulator,
            input,
            identity,
            identitySos);
        RfDemodulatedBlock second = Decode(
            ippDemodulator,
            input,
            identity,
            identitySos);

        Assert.Equal(ApproxProvider.Ipp, ippDemodulator.ApproxProvider);
        Assert.Equal(Hash(first), Hash(second));
        AssertApproxQuality(managed, first);
    }

    [Theory(DisplayName = "Approx resident spectra match the legacy v1 block and clear reused workspace state")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void ResidentSpectraPreserveV1BlockAndClearReusedWorkspaceState(
        ApproxProvider provider)
    {
        if (provider == ApproxProvider.Ipp && !IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildPalVhsProbe();
        double[] poisonInput = BuildWorkspacePoisonProbe(input);
        using var residentDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            provider);
        using var legacyDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            parallelizeVhsInverseStaging: false,
            companionIppFftFactory: null,
            vhsInverseCompanionWorkerThreads: 0,
            approxProvider: provider,
            useResidentApproxSpectra: false);

        RfDemodulatedBlock first = DecodeResidentSpectrumProbe(
            residentDemodulator,
            input,
            filterSeed: 3);
        string firstHash = Hash(first);
        RfDemodulatedBlock poison = DecodeResidentSpectrumProbe(
            residentDemodulator,
            poisonInput,
            filterSeed: 11);
        RfDemodulatedBlock reused = DecodeResidentSpectrumProbe(
            residentDemodulator,
            input,
            filterSeed: 3);
        RfDemodulatedBlock legacy = DecodeResidentSpectrumProbe(
            legacyDemodulator,
            input,
            filterSeed: 3);
        RfDemodulatedBlock legacyPoison = DecodeResidentSpectrumProbe(
            legacyDemodulator,
            poisonInput,
            filterSeed: 11);

        Assert.Equal(Hash(legacy), firstHash);
        Assert.Equal(Hash(legacyPoison), Hash(poison));
        Assert.NotEqual(firstHash, Hash(poison));
        Assert.Equal(firstHash, Hash(first));
        Assert.Equal(firstHash, Hash(reused));
    }

    [Theory(DisplayName = "Approx resident spectra match legacy v1 across sub-deemphasis materialization")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void ResidentSpectraMatchLegacyAcrossSubDeemphasisMaterialization(
        ApproxProvider provider)
    {
        if (provider == ApproxProvider.Ipp && !IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildPalVhsProbe();
        var subDeemphasis = new SubDeemphasisOptions(
            HighPassHz: 820_000.0,
            BandPassUpperHz: null,
            Order: 2,
            AmplitudeLowPassHz: 700_000.0,
            Deviation: 1_400_000.0,
            ExponentialScaling: 0.12,
            Scaling1: 0.1,
            Scaling2: null,
            LogisticMid: null,
            LogisticRate: null,
            StaticFactor: null);
        using var residentDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            provider);
        using var legacyDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            parallelizeVhsInverseStaging: false,
            companionIppFftFactory: null,
            vhsInverseCompanionWorkerThreads: 0,
            approxProvider: provider,
            useResidentApproxSpectra: false);

        RfDemodulatedBlock resident = DecodeResidentSpectrumProbe(
            residentDemodulator,
            input,
            filterSeed: 5,
            subDeemphasis: subDeemphasis);
        RfDemodulatedBlock legacy = DecodeResidentSpectrumProbe(
            legacyDemodulator,
            input,
            filterSeed: 5,
            subDeemphasis: subDeemphasis);
        RfDemodulatedBlock withoutSubDeemphasis = DecodeResidentSpectrumProbe(
            residentDemodulator,
            input,
            filterSeed: 5);

        string residentHash = Hash(resident);
        Assert.Equal(Hash(legacy), residentHash);
        Assert.NotEqual(Hash(withoutSubDeemphasis), residentHash);
    }

    [Theory(DisplayName = "Approx immutable RF filter bank preserves the complete legacy v1 block")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void ImmutableFilterBankPreservesCompleteLegacyV1Block(
        ApproxProvider provider)
    {
        SkipUnlessProviderAvailable(provider);

        double[] input = BuildPalVhsProbe();
        DecodeFilterSet filters = BuildApproxPipelineFilters(filterSeed: 13);
        Array.Fill(filters.RfMtf, Complex.One);
        using var bankPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true);
        using var legacyPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: false);

        string expected = DecodePipelineBlockHash(legacyPipeline, input);
        string actual = DecodePipelineBlockHash(bankPipeline, input);

        Assert.Equal(expected, actual);
        Assert.Equal(actual, DecodePipelineBlockHash(bankPipeline, input));
    }

    [Theory(DisplayName = "Approx compact float32 block outputs preserve expanded video channels")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void CompactFloat32BlockOutputsPreserveExpandedVideoChannels(
        ApproxProvider provider)
    {
        SkipUnlessProviderAvailable(provider);

        double[] input = BuildPalVhsProbe();
        DecodeFilterSet filters = BuildApproxPipelineFilters(filterSeed: 41);
        using var referencePipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            retainRfDiagnosticChannels: false);
        using var compactPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false);
        using var directPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            useDirectStreamOutputBufferLease: true);
        RfPipelineBlock reference = referencePipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        RfPipelineBlock compact = compactPipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        RfPipelineBlock direct = directPipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        try
        {
            Assert.Empty(compact.Demodulated.Video);
            Assert.Empty(compact.Demodulated.VideoLowPass);
            Assert.NotNull(compact.Demodulated.VideoFloat32);
            Assert.NotNull(compact.Demodulated.VideoLowPassFloat32);
            AssertExpandedFloat32Equal(
                reference.Demodulated.Video,
                compact.Demodulated.VideoFloat32!);
            AssertExpandedFloat32Equal(
                reference.Demodulated.VideoLowPass,
                compact.Demodulated.VideoLowPassFloat32!);
            Assert.Equal(reference.Demodulated.Envelope, compact.Demodulated.Envelope);
            Assert.Equal(reference.Demodulated.Envelope, direct.Demodulated.Envelope);
            Assert.Equal(
                reference.Demodulated.VhsWeakRfSignal,
                compact.Demodulated.VhsWeakRfSignal);
            Assert.Equal(
                reference.Demodulated.VhsWeakRfSignal,
                direct.Demodulated.VhsWeakRfSignal);
        }
        finally
        {
            referencePipeline.ReleaseStreamBlock(reference);
            compactPipeline.ReleaseStreamBlock(compact);
            directPipeline.ReleaseStreamBlock(direct);
        }
    }

    [Theory(DisplayName = "Approx frequency-domain chroma matches an independent FFT and remains deterministic")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public async Task FrequencyDomainChromaMatchesIndependentTransformAndRemainsDeterministic(
        ApproxProvider provider)
    {
        SkipUnlessProviderAvailable(provider);

        double[] input = BuildPalVhsProbe();
        double[] poisonInput = BuildWorkspacePoisonProbe(input);
        DecodeFilterSet filters = BuildApproxFrequencyChromaFilters(filterSeed: 47);
        ApproxRfFilterBank filterBank = ApproxRfFilterBank.Create(filters, input.Length);
        using var independentDemodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            provider);
        var independent = new float[input.Length];
        independentDemodulator.FilterApproxRealFrequencyDomain(
            input,
            filterBank.ChromaBurst,
            independent);
        VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32CurrentInPlace(
            independent,
            filters.ChromaOffsetSamples);

        using RfBlockDecodePipeline residentPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            useApproxFrequencyDomainChroma: true);
        Assert.True(residentPipeline.UsesApproxFrequencyDomainChroma);

        float[] first = DecodePipelineChromaFloat32(residentPipeline, input);
        float[] poison = DecodePipelineChromaFloat32(residentPipeline, poisonInput);
        float[] reused = DecodePipelineChromaFloat32(residentPipeline, input);
        AssertFloatBitsEqual(independent, first);
        AssertFloatBitsEqual(independent, reused);
        Assert.NotEqual(HashFloat32(independent), HashFloat32(poison));

        Task<float[]>[] concurrentDecodes = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(
                () => DecodePipelineChromaFloat32(residentPipeline, input)))
            .ToArray();
        float[][] concurrent = await Task.WhenAll(concurrentDecodes);
        Assert.All(concurrent, actual => AssertFloatBitsEqual(independent, actual));
    }

    [Fact(DisplayName = "Approx frequency-domain chroma direct leases reuse output only after release")]
    public void FrequencyDomainChromaDirectLeasesReuseOutputOnlyAfterRelease()
    {
        double[] input = BuildPalVhsProbe();
        double[] poisonInput = BuildWorkspacePoisonProbe(input);
        DecodeFilterSet filters = BuildApproxFrequencyChromaFilters(filterSeed: 53);
        using RfBlockDecodePipeline pipeline = BuildApproxPipeline(
            filters,
            ApproxProvider.Managed,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            useDirectStreamOutputBufferLease: true,
            useApproxFrequencyDomainChroma: true);

        RfPipelineBlock first = pipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        RfPipelineBlock second = pipeline.DecodePreparedStreamBlock(
            poisonInput,
            reportDiagnostics: false);
        RfPipelineBlock? reused = null;
        try
        {
            float[] firstChroma = Assert.IsType<float[]>(first.Demodulated.ChromaFloat32);
            float[] expected = firstChroma.ToArray();
            float[] secondChroma = Assert.IsType<float[]>(second.Demodulated.ChromaFloat32);
            Assert.NotSame(firstChroma, secondChroma);
            AssertFloatBitsEqual(expected, firstChroma);
            Assert.NotEqual(HashFloat32(expected), HashFloat32(secondChroma));
            Assert.Equal(2, pipeline.CreatedStreamOutputBufferSetCount);
            Assert.Equal(0, pipeline.RetainedStreamOutputBufferSetCount);

            pipeline.ReleaseStreamBlock(first);
            pipeline.ReleaseStreamBlock(first);
            Assert.Equal(1, pipeline.RetainedStreamOutputBufferSetCount);

            reused = pipeline.DecodePreparedStreamBlock(
                input,
                reportDiagnostics: false);
            float[] reusedChroma = Assert.IsType<float[]>(reused.Demodulated.ChromaFloat32);
            Assert.Same(firstChroma, reusedChroma);
            AssertFloatBitsEqual(expected, reusedChroma);
            Assert.Equal(2, pipeline.CreatedStreamOutputBufferSetCount);
            Assert.Equal(0, pipeline.RetainedStreamOutputBufferSetCount);
        }
        finally
        {
            pipeline.ReleaseStreamBlock(first);
            pipeline.ReleaseStreamBlock(second);
            if (reused is not null)
            {
                pipeline.ReleaseStreamBlock(reused);
            }
        }

        Assert.Equal(2, pipeline.RetainedStreamOutputBufferSetCount);
    }

    [Theory(DisplayName = "Approx resident float32 time domain preserves forced diff repair")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void ResidentFloat32TimeDomainPreservesForcedDiffRepair(
        ApproxProvider provider)
    {
        SkipUnlessProviderAvailable(provider);

        double[] input = BuildPalVhsProbe();
        DecodeFilterSet filters = BuildApproxPipelineFilters(filterSeed: 43);
        var forceRepair = new DiffDemodRepairOptions(double.NegativeInfinity);
        using var referencePipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            retainRfDiagnosticChannels: false,
            diffDemodRepair: forceRepair);
        using var compactPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            diffDemodRepair: forceRepair);

        RfPipelineBlock reference = referencePipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        RfPipelineBlock compact = compactPipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        try
        {
            AssertExpandedFloat32Equal(
                reference.Demodulated.Video,
                compact.Demodulated.VideoFloat32!);
            AssertExpandedFloat32Equal(
                reference.Demodulated.VideoLowPass,
                compact.Demodulated.VideoLowPassFloat32!);
            Assert.Equal(reference.Demodulated.Envelope, compact.Demodulated.Envelope);
            Assert.Equal(
                reference.Demodulated.VhsWeakRfSignal,
                compact.Demodulated.VhsWeakRfSignal);
        }
        finally
        {
            referencePipeline.ReleaseStreamBlock(reference);
            compactPipeline.ReleaseStreamBlock(compact);
        }
    }

    [Fact(DisplayName = "Approx direct float32 diff repair leaves workspaces untouched without an interior spike")]
    public void DirectFloat32DiffRepairLeavesWorkspacesUntouchedWithoutInteriorSpike()
    {
        const int length = 128;
        float[] demod = Enumerable.Range(0, length).Select(i => (float)(i % 11)).ToArray();
        demod[0] = 1_000.0f;
        demod[^1] = 1_000.0f;
        float[] real = Enumerable.Range(0, length).Select(i => MathF.Sin(i * 0.03125f)).ToArray();
        float[] imaginary = Enumerable.Range(0, length).Select(i => MathF.Cos(i * 0.046875f)).ToArray();
        float[] scratch = Enumerable.Range(0, length).Select(i => -1_234.5f + i).ToArray();
        float[] expectedDemod = demod.ToArray();
        float[] expectedReal = real.ToArray();
        float[] expectedImaginary = imaginary.ToArray();
        float[] expectedScratch = scratch.ToArray();
        using var demodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            ApproxProvider.Managed);

        demodulator.ApplyApproxDiffDemodRepairIfPresent(
            demod,
            real,
            imaginary,
            new DiffDemodRepairOptions(100.0),
            scratch);

        AssertFloatBitsEqual(expectedDemod, demod);
        AssertFloatBitsEqual(expectedReal, real);
        AssertFloatBitsEqual(expectedImaginary, imaginary);
        AssertFloatBitsEqual(expectedScratch, scratch);
    }

    [Fact(DisplayName = "Approx direct float32 diff repair matches legacy finite semantics bit-for-bit")]
    public void DirectFloat32DiffRepairMatchesLegacyFiniteSemantics()
    {
        const int length = 256;
        const double threshold = 10_000_000.25;
        var real = new float[length];
        var imaginary = new float[length];
        for (int i = 0; i < length; i++)
        {
            float phase = (i * 0.173f) + (i * i * 0.00031f);
            float amplitude = 1.0f + ((i % 7) * 0.125f);
            real[i] = MathF.Cos(phase) * amplitude;
            imaginary[i] = MathF.Sin(phase) * (amplitude + 0.25f);
        }

        var originalDemod = new float[length];
        PortedMath.UnwrapHilbertVhsRustApproximation(
            real,
            imaginary,
            SampleRateHz,
            originalDemod);
        originalDemod[64] = float.MaxValue;
        originalDemod[90] = float.MaxValue;

        var legacyDiffed = new Complex[length];
        for (int i = 1; i < length; i++)
        {
            legacyDiffed[i] = new Complex(
                (double)real[i] - real[i - 1],
                (double)imaginary[i] - imaginary[i - 1]);
        }

        double[] legacyDemodDiffed = PortedMath.UnwrapHilbertVhsRustApproximation(
            legacyDiffed,
            SampleRateHz);
        double[] legacyDemod = originalDemod.Select(static value => (double)value).ToArray();
        RfDemodulator.ReplaceSpikes(legacyDemod, legacyDemodDiffed, threshold);
        float[] expectedDemod = legacyDemod.Select(static value => (float)value).ToArray();
        float[] expectedScratch = legacyDemodDiffed.Select(static value => (float)value).ToArray();
        var expectedReal = new float[length];
        var expectedImaginary = new float[length];
        for (int i = 1; i < length; i++)
        {
            expectedReal[i] = (float)((double)real[i] - real[i - 1]);
            expectedImaginary[i] = (float)((double)imaginary[i] - imaginary[i - 1]);
        }

        float[] actual = originalDemod.ToArray();
        float[] actualReal = real.ToArray();
        float[] actualImaginary = imaginary.ToArray();
        var scratch = Enumerable.Repeat(float.NaN, length).ToArray();
        using var demodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            ApproxProvider.Managed);

        demodulator.ApplyApproxDiffDemodRepairIfPresent(
            actual,
            actualReal,
            actualImaginary,
            new DiffDemodRepairOptions(threshold),
            scratch);

        Assert.NotEqual(HashFloat32(originalDemod), HashFloat32(actual));
        AssertFloatBitsEqual(expectedDemod, actual);
        AssertFloatBitsEqual(expectedScratch, scratch);
        AssertFloatBitsEqual(expectedReal, actualReal);
        AssertFloatBitsEqual(expectedImaginary, actualImaginary);
    }

    [Theory(DisplayName = "Approx v6 direct float32 diff repair matches widened finite repair")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void V6DirectFloat32DiffRepairMatchesWidenedFiniteRepair(
        ApproxProvider provider)
    {
        SkipUnlessProviderAvailable(provider);

        double[] input = BuildPalVhsProbe();
        DecodeFilterSet filters = BuildApproxFrequencyChromaFilters(filterSeed: 59);
        var forceRepair = new DiffDemodRepairOptions(double.NegativeInfinity);
        using var widened = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            diffDemodRepair: forceRepair,
            useApproxFrequencyDomainChroma: false);
        using var direct = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            diffDemodRepair: forceRepair,
            useDirectStreamOutputBufferLease: true,
            useApproxFrequencyDomainChroma: true);

        RfPipelineBlock expected = widened.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        RfPipelineBlock actual = direct.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        try
        {
            Assert.False(widened.UsesApproxFrequencyDomainChroma);
            Assert.True(direct.UsesApproxFrequencyDomainChroma);
            AssertFloatBitsEqual(
                expected.Demodulated.VideoFloat32!,
                actual.Demodulated.VideoFloat32!);
            AssertFloatBitsEqual(
                expected.Demodulated.VideoLowPassFloat32!,
                actual.Demodulated.VideoLowPassFloat32!);
            Assert.Equal(expected.Demodulated.Envelope, actual.Demodulated.Envelope);
            Assert.Equal(
                expected.Demodulated.VhsWeakRfSignal,
                actual.Demodulated.VhsWeakRfSignal);
        }
        finally
        {
            widened.ReleaseStreamBlock(expected);
            direct.ReleaseStreamBlock(actual);
        }
    }

    [Theory(DisplayName = "Approx immutable RF filter bank snapshots mutable static filter sources")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void ImmutableFilterBankSnapshotsMutableStaticFilterSources(
        ApproxProvider provider)
    {
        SkipUnlessProviderAvailable(provider);

        double[] input = BuildPalVhsProbe();
        DecodeFilterSet filters = BuildApproxPipelineFilters(filterSeed: 17);
        using var originalBankPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true);
        string originalHash = DecodePipelineBlockHash(originalBankPipeline, input);

        ReplaceApproxPipelineFilterSources(filters, filterSeed: 71);

        string retainedSnapshotHash = DecodePipelineBlockHash(
            originalBankPipeline,
            input);
        using var rebuiltBankPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true);
        using var mutatedLegacyPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: false);
        string rebuiltHash = DecodePipelineBlockHash(rebuiltBankPipeline, input);

        Assert.Equal(originalHash, retainedSnapshotHash);
        Assert.NotEqual(originalHash, rebuiltHash);
        Assert.Equal(
            DecodePipelineBlockHash(mutatedLegacyPipeline, input),
            rebuiltHash);
    }

    [Theory(DisplayName = "Approx frequency-domain chroma snapshots mutable filter sources")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void FrequencyDomainChromaSnapshotsMutableFilterSources(
        ApproxProvider provider)
    {
        SkipUnlessProviderAvailable(provider);

        double[] input = BuildPalVhsProbe();
        DecodeFilterSet filters = BuildApproxFrequencyChromaFilters(filterSeed: 59);
        using RfBlockDecodePipeline originalPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            useApproxFrequencyDomainChroma: true);
        float[] original = DecodePipelineChromaFloat32(originalPipeline, input);

        Array.Fill(
            filters.ChromaBurst
                ?? throw new InvalidOperationException("The Approx chroma probe omitted its frequency response."),
            Complex.Zero);

        float[] retainedSnapshot = DecodePipelineChromaFloat32(originalPipeline, input);
        using RfBlockDecodePipeline rebuiltPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            useApproxFrequencyDomainChroma: true);
        float[] rebuilt = DecodePipelineChromaFloat32(rebuiltPipeline, input);

        AssertFloatBitsEqual(original, retainedSnapshot);
        Assert.NotEqual(HashFloat32(original), HashFloat32(rebuilt));
    }

    [Theory(DisplayName = "Approx dynamic RF MTF overrides bypass the immutable bank without stale state")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void DynamicRfMtfOverridesBypassImmutableBankWithoutStaleState(
        ApproxProvider provider)
    {
        SkipUnlessProviderAvailable(provider);

        double[] input = BuildPalVhsProbe();
        DecodeFilterSet filters = BuildApproxPipelineFilters(filterSeed: 23);
        Complex[] overrideB = BuildResidentSpectrumFilter(Length, seed: 83);
        Complex[] overrideC = BuildResidentSpectrumFilter(Length, seed: 109);
        using var bankPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: true);
        using var legacyPipeline = BuildApproxPipeline(
            filters,
            provider,
            useImmutableApproxFilterBank: false);

        string defaultA = DecodePipelineBlockHash(bankPipeline, input);
        string legacyA = DecodePipelineBlockHash(legacyPipeline, input);
        string overrideBHash = DecodePipelineBlockHash(
            bankPipeline,
            input,
            overrideB);
        string legacyB = DecodePipelineBlockHash(
            legacyPipeline,
            input,
            overrideB);
        string defaultAAfterB = DecodePipelineBlockHash(bankPipeline, input);
        string overrideCHash = DecodePipelineBlockHash(
            bankPipeline,
            input,
            overrideC);
        string legacyC = DecodePipelineBlockHash(
            legacyPipeline,
            input,
            overrideC);
        string overrideBAfterC = DecodePipelineBlockHash(
            bankPipeline,
            input,
            overrideB);

        Assert.Equal(legacyA, defaultA);
        Assert.Equal(legacyB, overrideBHash);
        Assert.Equal(legacyC, overrideCHash);
        Assert.Equal(defaultA, defaultAAfterB);
        Assert.Equal(overrideBHash, overrideBAfterC);
        Assert.NotEqual(defaultA, overrideBHash);
        Assert.NotEqual(defaultA, overrideCHash);
        Assert.NotEqual(overrideBHash, overrideCHash);
    }

    [Theory(DisplayName = "Approx block output is deterministic across concurrent workspace leases")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public async Task BlockOutputIsDeterministicAcrossConcurrentWorkspaceLeases(
        ApproxProvider provider)
    {
        if (provider == ApproxProvider.Ipp && !IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildPalVhsProbe();
        using var demodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            provider);
        string expected = Hash(DecodeResidentSpectrumProbe(
            demodulator,
            input,
            filterSeed: 7));

        Task<string>[] decodes = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(
                () => Hash(DecodeResidentSpectrumProbe(
                    demodulator,
                    input,
                    filterSeed: 7))))
            .ToArray();
        string[] actual = await Task.WhenAll(decodes);

        Assert.All(actual, hash => Assert.Equal(expected, hash));
    }

    [Theory(DisplayName = "Approx analytic Hilbert path rejects DC and Nyquist imaginary leakage")]
    [InlineData(ApproxProvider.Managed)]
    [InlineData(ApproxProvider.Ipp)]
    public void AnalyticHilbertPathRejectsDcAndNyquistImaginaryLeakage(
        ApproxProvider provider)
    {
        if (provider == ApproxProvider.Ipp && !IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildDcAndNyquistProbe();
        Complex[] identity = RfDemodulator.IdentityFilter(Length);
        SosSection[] identitySos =
        [
            new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
        ];
        using var demodulator = new RfDemodulator(
            SampleRateHz,
            DspBackend.ApproxFast,
            provider);

        RfDemodulatedBlock block = Decode(
            demodulator,
            input,
            identity,
            identitySos);

        Assert.Equal(Length, block.Analytic.Length);
        Assert.All(
            block.Analytic,
            value => Assert.InRange(Math.Abs(value.Imaginary), 0.0, 1e-6));
        AssertFinite(block.RfHighPass);
        AssertFinite(block.Video);
        AssertFinite(block.VideoLowPass);
    }

    [Fact(DisplayName = "Approx resident Hilbert spectrum rotation is bit-exact in place")]
    public void ResidentHilbertSpectrumRotationIsBitExactInPlace()
    {
        int[] lengths = [2, 3, 4, 5, 8, 9, 17, 18, (Length / 2) + 1];
        uint[] patterns =
        [
            0x00000000U,
            0x80000000U,
            0x00000001U,
            0x80000001U,
            0x007FFFFFU,
            0x00800000U,
            0x3F800000U,
            0xBF800000U,
            0x7F7FFFFFU,
            0xFF7FFFFFU,
            0x7F800000U,
            0xFF800000U,
            0x7FC00001U,
            0xFFC12345U,
            0x7FA00001U
        ];

        foreach (int length in lengths)
        {
            var actual = new Complex32[length];
            var expected = new Complex32[length];
            for (int index = 0; index < length; index++)
            {
                uint realBits = patterns[(index * 2) % patterns.Length];
                uint imaginaryBits = patterns[((index * 2) + 1) % patterns.Length];
                actual[index] = new Complex32(
                    BitConverter.UInt32BitsToSingle(realBits),
                    BitConverter.UInt32BitsToSingle(imaginaryBits));
                expected[index] = index == 0 || index == length - 1
                    ? default
                    : new Complex32(
                        BitConverter.UInt32BitsToSingle(imaginaryBits),
                        BitConverter.UInt32BitsToSingle(realBits ^ 0x80000000U));
            }

            RfDemodulator.PrepareApproxHilbertSpectrumInPlace(actual);

            AssertComplex32BitsEqual(expected, actual);
        }
    }

    [Fact(DisplayName = "Approx provider cannot be attached to another numerical backend")]
    public void ProviderCannotBeAttachedToAnotherBackend()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new RfDemodulator(
                SampleRateHz,
                DspBackend.Exact,
                ApproxProvider.Managed));

        Assert.Contains("only for the approx-fast", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Approx rejects an undefined provider instead of silently using managed")]
    public void UndefinedProviderFailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RfDemodulator(
                SampleRateHz,
                DspBackend.ApproxFast,
                (ApproxProvider)99));
    }

    [Fact(DisplayName = "Approx frequency-domain chroma gate falls back without partial routing")]
    public void FrequencyDomainChromaGateFallsBackWithoutPartialRouting()
    {
        DecodeFilterSet filters = BuildApproxFrequencyChromaFilters(filterSeed: 61);

        using (RfBlockDecodePipeline valid = BuildApproxPipeline(
                   filters,
                   ApproxProvider.Managed,
                   useImmutableApproxFilterBank: true,
                   useCompactApproxFloat32Outputs: true,
                   retainRfDiagnosticChannels: false,
                   useApproxFrequencyDomainChroma: true))
        {
            Assert.True(valid.UsesApproxFrequencyDomainChroma);
        }

        AssertGateDisabled(useImmutableApproxFilterBank: false);
        AssertGateDisabled(useCompactApproxFloat32Outputs: false);
        AssertGateDisabled(retainRfDiagnosticChannels: true);
        AssertGateDisabled(upstreamBehaviorProfile: UpstreamBehaviorProfile.V040);
        AssertGateDisabled(candidateFilters: filters with { ChromaBurst = null });
        AssertGateDisabled(candidateFilters: filters with { ChromaBurstSos = null });
        AssertGateDisabled(filterOptions: new DecodeFilterOptions(
            UseChromaAfc: true,
            FmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation));
        AssertGateDisabled(candidateFilters: filters with
        {
            ChromaBurstUsesDemodulatedVideo = true
        });
        AssertGateDisabled(candidateFilters: filters with
        {
            ChromaBurstAudioNotch = new TransferFunction([1.0], [1.0])
        });
        AssertGateDisabled(candidateFilters: filters with
        {
            ChromaBurstVideoNotch = new TransferFunction([1.0], [1.0])
        });

        DecodeFilterSet fallbackFilters = filters with
        {
            ChromaBurstAudioNotch = new TransferFunction([1.0], [1.0])
        };
        using RfBlockDecodePipeline requestedFallback = BuildApproxPipeline(
            fallbackFilters,
            ApproxProvider.Managed,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            useApproxFrequencyDomainChroma: true);
        using RfBlockDecodePipeline explicitLegacy = BuildApproxPipeline(
            fallbackFilters,
            ApproxProvider.Managed,
            useImmutableApproxFilterBank: true,
            useCompactApproxFloat32Outputs: true,
            retainRfDiagnosticChannels: false,
            useApproxFrequencyDomainChroma: false);
        Assert.False(requestedFallback.UsesApproxFrequencyDomainChroma);
        AssertDoubleBitsEqual(
            DecodePipelineChromaFloat64(explicitLegacy, BuildPalVhsProbe()),
            DecodePipelineChromaFloat64(requestedFallback, BuildPalVhsProbe()));

        void AssertGateDisabled(
            DecodeFilterSet? candidateFilters = null,
            bool useImmutableApproxFilterBank = true,
            bool useCompactApproxFloat32Outputs = true,
            bool retainRfDiagnosticChannels = false,
            DecodeFilterOptions? filterOptions = null,
            UpstreamBehaviorProfile upstreamBehaviorProfile =
                UpstreamBehaviorProfile.Current)
        {
            using RfBlockDecodePipeline pipeline = BuildApproxPipeline(
                candidateFilters ?? filters,
                ApproxProvider.Managed,
                useImmutableApproxFilterBank,
                useCompactApproxFloat32Outputs,
                retainRfDiagnosticChannels,
                useApproxFrequencyDomainChroma: true,
                filterOptions: filterOptions,
                upstreamBehaviorProfile: upstreamBehaviorProfile);
            Assert.False(pipeline.UsesApproxFrequencyDomainChroma);
        }
    }

    [Theory(DisplayName = "Approx v6 direct stream leases are selected for serial worker counts")]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(20, false)]
    public void DirectStreamLeaseSelectionIsLimitedToSerialWorkers(
        int workerThreads,
        bool expectedDirectLease)
    {
        using DecodeSession session = CreateSession(
            $"approx-direct-lease-t{workerThreads}",
            "--threads",
            workerThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--compat-version",
            "current",
            "--dsp-backend",
            "approx-fast",
            "--approx-provider",
            "managed",
            "--approx-resampler",
            "catmull-rom4",
            "--approx-precision",
            "aggressive");

        Assert.Equal(
            expectedDirectLease,
            session.Pipeline.UsesDirectStreamOutputBufferLease);
        Assert.True(session.Pipeline.UsesApproxFrequencyDomainChroma);
    }

    [Fact(DisplayName = "Approx metadata records the effective provider and contract without changing Exact metadata")]
    public void MetadataRecordsEffectiveProviderAndContract()
    {
        using DecodeSession approx = CreateSession(
            "approx-output",
            "--dsp-backend",
            "approx-fast",
            "--approx-provider",
            "managed");
        using DecodeSession catmullRom4 = CreateSession(
            "approx-catmull-output",
            "--dsp-backend",
            "approx-fast",
            "--approx-provider",
            "managed",
            "--approx-resampler",
            "catmull-rom4");
        using DecodeSession aggressive = CreateSession(
            "approx-aggressive-output",
            "--compat-version",
            "current",
            "--dsp-backend",
            "approx-fast",
            "--approx-provider",
            "managed",
            "--approx-resampler",
            "catmull-rom4",
            "--approx-precision",
            "aggressive");
        using DecodeSession exact = CreateSession("exact-output");

        JsonObject approxVideo = GetVideoParameters(approx);
        Assert.Equal(ApproxResampler.Sinc16, approx.ExecutionOptions.ApproxResampler);
        Assert.Equal(ApproxPrecision.Balanced, approx.ExecutionOptions.ApproxPrecision);
        Assert.False(approx.Pipeline.UsesApproxFrequencyDomainChroma);
        Assert.Equal(
            TbcSampleResamplingKernel.KaiserSinc16,
            approx.TbcRenderer.SampleResamplingKernel);
        Assert.Equal("approx-fast", approxVideo["dspBackend"]?.GetValue<string>());
        Assert.Equal(
            ApproximationContract.VhsRfTransformFloat32V1,
            approxVideo["approximationContract"]?.GetValue<string>());
        Assert.Equal("managed", approxVideo["approxProvider"]?.GetValue<string>());
        Assert.Equal("sinc16", approxVideo["approxResampler"]?.GetValue<string>());
        Assert.False(approxVideo.ContainsKey("approxPrecision"));
        Assert.False(approx.ExecutionOptions.ApproxPrecisionIsExplicit);
        Assert.Equal(
            CurrentChromaBurstFitter.DefaultMaximumIterations,
            approx.TbcFieldDecoder.ChromaFieldOptions?
                .ChromaBurstFitMaximumIterations);
        Assert.Equal(
            TbcSampleResamplingKernel.KaiserSinc16,
            approx.TbcFieldDecoder.ChromaBurstResamplingKernel);
        Assert.Equal("explicit", approxVideo["approxProviderSelection"]?.GetValue<string>());
        Assert.False(approxVideo["approxProviderFallback"]?.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(
            approxVideo["approxProviderArchitecture"]?.GetValue<string>()));

        JsonObject catmullVideo = GetVideoParameters(catmullRom4);
        Assert.Equal(
            ApproxResampler.CatmullRom4,
            catmullRom4.ExecutionOptions.ApproxResampler);
        Assert.False(catmullRom4.Pipeline.UsesApproxFrequencyDomainChroma);
        Assert.Equal(
            TbcSampleResamplingKernel.CatmullRom4Float32,
            catmullRom4.TbcRenderer.SampleResamplingKernel);
        Assert.Equal(
            ApproximationContract.VhsRfTransformFloat32CatmullRom4V2,
            catmullVideo["approximationContract"]?.GetValue<string>());
        Assert.Equal(
            "catmull-rom4",
            catmullVideo["approxResampler"]?.GetValue<string>());
        Assert.False(catmullVideo.ContainsKey("approxPrecision"));
        Assert.Equal(
            CurrentChromaBurstFitter.DefaultMaximumIterations,
            catmullRom4.TbcFieldDecoder.ChromaFieldOptions?
                .ChromaBurstFitMaximumIterations);
        Assert.Equal(
            TbcSampleResamplingKernel.KaiserSinc16,
            catmullRom4.TbcFieldDecoder.ChromaBurstResamplingKernel);
        using TbcFieldDecodePipeline reconstructedBalancedCatmull =
            TbcFieldDecodePipeline.FromSession(catmullRom4);
        Assert.Equal(
            TbcSampleResamplingKernel.KaiserSinc16,
            reconstructedBalancedCatmull.ChromaBurstResamplingKernel);

        JsonObject aggressiveVideo = GetVideoParameters(aggressive);
        Assert.Equal(
            ApproxPrecision.Aggressive,
            aggressive.ExecutionOptions.ApproxPrecision);
        Assert.True(aggressive.ExecutionOptions.ApproxPrecisionIsExplicit);
        Assert.True(aggressive.Pipeline.UsesApproxFrequencyDomainChroma);
        Assert.Equal(
            ApproximationContract.VhsRfTransformFieldFloat32CatmullRom4BurstIqPrefixChromaFftV6,
            aggressiveVideo["approximationContract"]?.GetValue<string>());
        Assert.Equal(
            "aggressive",
            aggressiveVideo["approxPrecision"]?.GetValue<string>());
        Assert.Equal(
            0,
            aggressive.TbcFieldDecoder.ChromaFieldOptions?
                .ChromaBurstFitMaximumIterations);
        Assert.True(aggressive.TbcFieldDecoder.UsesApproxFloat32Sync);
        Assert.True(aggressive.TbcFieldDecoder.UsesApproxFloat32FieldPayloads);
        Assert.Equal(
            TbcSampleResamplingKernel.CatmullRom4Float32,
            aggressive.TbcFieldDecoder.ChromaBurstResamplingKernel);
        using TbcFieldDecodePipeline reconstructedAggressive =
            TbcFieldDecodePipeline.FromSession(aggressive);
        Assert.Equal(
            0,
            reconstructedAggressive.ChromaFieldOptions?
                .ChromaBurstFitMaximumIterations);
        Assert.True(reconstructedAggressive.UsesApproxFloat32Sync);
        Assert.True(reconstructedAggressive.UsesApproxFloat32FieldPayloads);
        Assert.Equal(
            TbcSampleResamplingKernel.CatmullRom4Float32,
            reconstructedAggressive.ChromaBurstResamplingKernel);

        JsonObject exactVideo = GetVideoParameters(exact);
        Assert.False(exactVideo.ContainsKey("dspBackend"));
        Assert.False(exactVideo.ContainsKey("approximationContract"));
        Assert.False(exactVideo.ContainsKey("approxProvider"));
        Assert.False(exactVideo.ContainsKey("approxResampler"));
        Assert.False(exactVideo.ContainsKey("approxPrecision"));
        Assert.Null(exact.ExecutionOptions.ApproxResampler);
        Assert.Null(exact.ExecutionOptions.ApproxPrecision);
        Assert.False(exact.Pipeline.UsesApproxFrequencyDomainChroma);
        Assert.Equal(
            CurrentChromaBurstFitter.DefaultMaximumIterations,
            exact.TbcFieldDecoder.ChromaFieldOptions?
                .ChromaBurstFitMaximumIterations);
        Assert.Equal(
            TbcSampleResamplingKernel.KaiserSinc16,
            exact.TbcRenderer.SampleResamplingKernel);
        Assert.Equal(
            TbcSampleResamplingKernel.KaiserSinc16,
            exact.TbcFieldDecoder.ChromaBurstResamplingKernel);
    }

    [Fact(DisplayName = "Explicit Approx resampler cannot be attached to another numerical backend")]
    public void ResamplerCannotBeAttachedToAnotherBackend()
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => CreateSession(
                "invalid-resampler-output",
                "--approx-resampler",
                "catmull-rom4"));

        Assert.Contains("valid only with '--dsp-backend approx-fast'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("was not ignored", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Explicit Approx precision cannot be attached to another numerical backend")]
    public void PrecisionCannotBeAttachedToAnotherBackend()
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => CreateSession(
                "invalid-precision-output",
                "--approx-precision",
                "aggressive"));

        Assert.Contains("valid only with '--dsp-backend approx-fast'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("was not ignored", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Aggressive Approx precision fails closed without its v6 prerequisites")]
    public void AggressivePrecisionRequiresV6Prerequisites()
    {
        NotSupportedException resampler = Assert.Throws<NotSupportedException>(
            () => CreateSession(
                "invalid-aggressive-resampler-output",
                "--compat-version",
                "current",
                "--dsp-backend",
                "approx-fast",
                "--approx-provider",
                "managed",
                "--approx-precision",
                "aggressive"));
        NotSupportedException behavior = Assert.Throws<NotSupportedException>(
            () => CreateSession(
                "invalid-aggressive-behavior-output",
                "--dsp-backend",
                "approx-fast",
                "--approx-provider",
                "managed",
                "--approx-resampler",
                "catmull-rom4",
                "--approx-precision",
                "aggressive"));
        NotSupportedException sampleRate = Assert.Throws<NotSupportedException>(
            () => CreateSession(
                "invalid-aggressive-sample-rate-output",
                "--compat-version",
                "current",
                "--dsp-backend",
                "approx-fast",
                "--approx-provider",
                "managed",
                "--approx-resampler",
                "catmull-rom4",
                "--approx-precision",
                "aggressive",
                "--decode-at-20msps"));

        Assert.Contains("--approx-resampler catmull-rom4", resampler.Message, StringComparison.Ordinal);
        Assert.Contains("--compat-version current", behavior.Message, StringComparison.Ordinal);
        Assert.Contains("requires a 40 MSPS decode rate", sampleRate.Message, StringComparison.Ordinal);
        Assert.Contains("no partial fallback", resampler.Message, StringComparison.Ordinal);
        Assert.Contains("no partial fallback", behavior.Message, StringComparison.Ordinal);
        Assert.Contains("no partial fallback", sampleRate.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Aggressive Approx v6 rejects chroma AFC without partial fallback")]
    public void AggressiveV6RejectsChromaAfcWithoutPartialFallback()
    {
        AssertAggressiveV6SessionFailsClosed(
            "invalid-aggressive-v6-cafc-output",
            "--cafc");
    }

    [Fact(DisplayName = "Aggressive Approx v6 rejects input chroma notch without partial fallback")]
    public void AggressiveV6RejectsInputChromaNotchWithoutPartialFallback()
    {
        AssertAggressiveV6SessionFailsClosed(
            "invalid-aggressive-v6-notch-output",
            "--notch",
            "2.5",
            "--notch_q",
            "20");
    }

    [Fact(DisplayName = "Aggressive Approx v6 rejects raw TBC export without partial fallback")]
    public void AggressiveV6RejectsRawTbcExportWithoutPartialFallback()
    {
        AssertAggressiveV6SessionFailsClosed(
            "invalid-aggressive-v6-raw-tbc-output",
            "--export_raw_tbc");
    }

    [Fact(DisplayName = "Approx fails closed when RF high boost would bypass its float32 RF path")]
    public void NonZeroRfHighBoostFailsClosed()
    {
        ParsedCommand command = Parse(
            "boost-output",
            "--dsp-backend",
            "approx-fast",
            "--approx-provider",
            "managed",
            "--high_boost",
            "1");

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => DecodeSessionFactory.Create(command));

        Assert.Contains("does not yet support non-zero VHS RF high boost", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no partial Approx fallback", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Approx diagnostics identify contract, provider, resampler, precision, selection, and fallback")]
    [InlineData(
        ApproxResampler.Sinc16,
        ApproxPrecision.Balanced,
        ApproximationContract.VhsRfTransformFloat32V1,
        "sinc16",
        "balanced")]
    [InlineData(
        ApproxResampler.CatmullRom4,
        ApproxPrecision.Balanced,
        ApproximationContract.VhsRfTransformFloat32CatmullRom4V2,
        "catmull-rom4",
        "balanced")]
    [InlineData(
        ApproxResampler.CatmullRom4,
        ApproxPrecision.Aggressive,
        ApproximationContract.VhsRfTransformFieldFloat32CatmullRom4BurstIqPrefixChromaFftV6,
        "catmull-rom4",
        "aggressive")]
    public void DiagnosticsIdentifyResolvedSelection(
        ApproxResampler resampler,
        ApproxPrecision precision,
        string contract,
        string commandLineValue,
        string precisionValue)
    {
        var options = new DecodeExecutionOptions(
            RequestedThreads: 4,
            WorkerThreads: 4,
            SeekFrame: BigInteger.Zero,
            WriteDebugData: false,
            Debug: false,
            DebugPlotPath: null,
            IgnoreLeadOut: false,
            VerboseVits: false,
            UseProfiler: false,
            CxAdcCompatibilityMode: false,
            DspBackend: DspBackend.ApproxFast,
            UpstreamBehaviorProfile: UpstreamBehaviorProfile.Current)
        {
            ApproxProvider = ApproxProvider.Managed,
            ApproxResampler = resampler,
            ApproxPrecision = precision,
            ApproxPrecisionIsExplicit = true,
            ApproxProviderIsExplicit = false,
            ApproxProviderFellBackFromIpp = true,
            ApproxProviderProcessArchitecture = Architecture.X64,
            ApproxProviderDiagnostic = "test probe unavailable"
        };

        string message = DecodeRunner.FormatApproxDiagnostic(options);

        Assert.Contains(contract, message, StringComparison.Ordinal);
        Assert.Contains("provider=managed", message, StringComparison.Ordinal);
        Assert.Contains($"resampler={commandLineValue}", message, StringComparison.Ordinal);
        Assert.Contains($"precision={precisionValue}", message, StringComparison.Ordinal);
        Assert.Contains("selection=automatic", message, StringComparison.Ordinal);
        Assert.Contains("architecture=X64", message, StringComparison.Ordinal);
        Assert.Contains("test probe unavailable", message, StringComparison.Ordinal);
        Assert.Contains("fell back", message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Omitted Approx precision preserves the legacy diagnostic surface")]
    public void OmittedPrecisionDoesNotAddDiagnosticIdentity()
    {
        var options = new DecodeExecutionOptions(
            RequestedThreads: 4,
            WorkerThreads: 4,
            SeekFrame: BigInteger.Zero,
            WriteDebugData: false,
            Debug: false,
            DebugPlotPath: null,
            IgnoreLeadOut: false,
            VerboseVits: false,
            UseProfiler: false,
            CxAdcCompatibilityMode: false,
            DspBackend: DspBackend.ApproxFast,
            UpstreamBehaviorProfile: UpstreamBehaviorProfile.Current)
        {
            ApproxProvider = ApproxProvider.Managed,
            ApproxResampler = ApproxResampler.CatmullRom4,
            ApproxPrecision = ApproxPrecision.Balanced,
            ApproxPrecisionIsExplicit = false,
            ApproxProviderIsExplicit = true,
            ApproxProviderProcessArchitecture = Architecture.X64
        };

        string message = DecodeRunner.FormatApproxDiagnostic(options);

        Assert.Contains(
            ApproximationContract.VhsRfTransformFloat32CatmullRom4V2,
            message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("precision=", message, StringComparison.Ordinal);
    }

    private static DecodeSession CreateSession(
        string outputBase,
        params string[] options)
        => DecodeSessionFactory.Create(Parse(outputBase, options));

    private static void AssertAggressiveV6SessionFailsClosed(
        string outputBase,
        params string[] incompatibleOptions)
    {
        string[] options =
        [
            "--compat-version",
            "current",
            "--dsp-backend",
            "approx-fast",
            "--approx-provider",
            "managed",
            "--approx-resampler",
            "catmull-rom4",
            "--approx-precision",
            "aggressive",
            .. incompatibleOptions
        ];
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => CreateSession(outputBase, options));
        Assert.Contains("v6", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no partial", exception.Message, StringComparison.Ordinal);
    }

    private static ParsedCommand Parse(
        string outputBase,
        params string[] options)
    {
        var arguments = new List<string>(options)
        {
            "input.u8",
            outputBase
        };
        return new CommandLineParser().Parse(CliSpecs.Vhs, arguments);
    }

    private static JsonObject GetVideoParameters(DecodeSession session)
        => TbcOutputMetadataWriter.BuildHeader(session, fieldCount: 0)["videoParameters"]?.AsObject()
            ?? throw new InvalidOperationException("Metadata omitted videoParameters.");

    private static void SkipUnlessProviderAvailable(ApproxProvider provider)
    {
        if (provider == ApproxProvider.Ipp)
        {
            Assert.SkipUnless(
                IppRuntime.TryProbe(out _),
                "The Intel IPP native runtime is unavailable.");
        }
    }

    private static RfBlockDecodePipeline BuildApproxPipeline(
        DecodeFilterSet filters,
        ApproxProvider provider,
        bool useImmutableApproxFilterBank,
        bool useCompactApproxFloat32Outputs = false,
        bool retainRfDiagnosticChannels = true,
        DiffDemodRepairOptions? diffDemodRepair = null,
        bool useDirectStreamOutputBufferLease = false,
        bool useApproxFrequencyDomainChroma = false,
        DecodeFilterOptions? filterOptions = null,
        UpstreamBehaviorProfile upstreamBehaviorProfile =
            UpstreamBehaviorProfile.Current)
        => new(
            new Pcm16StreamSampleLoader(),
            filters,
            SampleRateHz,
            filterOptions: filterOptions ?? new DecodeFilterOptions(
                    DiffDemodRepair: diffDemodRepair,
                    FmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation),
            cvbsOptions: null,
            inputProcessor: null,
            diagnosticLogger: null,
            retainRfDiagnosticChannels: retainRfDiagnosticChannels,
            dspBackend: DspBackend.ApproxFast,
            approxProvider: provider,
            upstreamBehaviorProfile: upstreamBehaviorProfile,
            parallelizeVhsInverseStaging: false,
            vhsInverseCompanionWorkerThreads: 1,
            useImmutableApproxFilterBank: useImmutableApproxFilterBank,
            useCompactApproxFloat32Outputs: useCompactApproxFloat32Outputs,
            useDirectStreamOutputBufferLease: useDirectStreamOutputBufferLease,
            useApproxFrequencyDomainChroma: useApproxFrequencyDomainChroma);

    private static void AssertExpandedFloat32Equal(
        ReadOnlySpan<double> expected,
        ReadOnlySpan<float> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }
    }

    private static void AssertDoubleBitsEqual(
        ReadOnlySpan<double> expected,
        ReadOnlySpan<double> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Double sequences differ at the bit level.");
    }

    private static void AssertFloatBitsEqual(
        ReadOnlySpan<float> expected,
        ReadOnlySpan<float> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Float sequences differ at the bit level.");
    }

    private static void AssertComplex32BitsEqual(
        ReadOnlySpan<Complex32> expected,
        ReadOnlySpan<Complex32> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Complex32 sequences differ at the bit level.");
    }

    private static DecodeFilterSet BuildApproxPipelineFilters(int filterSeed)
    {
        Complex[] rfVideo = BuildResidentSpectrumFilter(Length, filterSeed);
        Complex[] rfHighPass = BuildResidentSpectrumFilter(Length, filterSeed + 1);
        Complex[] rfMtf = BuildResidentSpectrumFilter(Length, filterSeed + 2);
        Complex[] video = BuildResidentSpectrumFilter(Length, filterSeed + 3);
        Complex[] videoLowPass = BuildResidentSpectrumFilter(Length, filterSeed + 4);
        Complex[] videoLowPass05 = BuildResidentSpectrumFilter(Length, filterSeed + 5);
        double[] magnitudes = Enumerable.Repeat(1.0, Length).ToArray();
        SosSection[] identitySos =
        [
            new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
        ];
        return new DecodeFilterSet(
            rfVideo,
            rfHighPass,
            rfMtf,
            video,
            videoLowPass,
            videoLowPass05,
            null,
            magnitudes,
            magnitudes,
            magnitudes,
            magnitudes,
            magnitudes,
            magnitudes,
            null,
            VhsEnvelopeSos: identitySos);
    }

    private static DecodeFilterSet BuildApproxFrequencyChromaFilters(int filterSeed)
    {
        DecodeFilterSet filters = BuildApproxPipelineFilters(filterSeed);
        SosSection[] chromaSos = IirFilterDesign.ButterworthBandPassSos(
            order: 4,
            normalizedLowCutoff: 0.02,
            normalizedHighCutoff: 0.12);
        Complex[] chromaResponse = IirFilterDesign.FrequencyResponse(
            chromaSos,
            Length);
        for (int index = 0; index < chromaResponse.Length; index++)
        {
            Complex value = chromaResponse[index];
            double magnitudeSquared = (value.Real * value.Real)
                + (value.Imaginary * value.Imaginary);
            chromaResponse[index] = new Complex(magnitudeSquared, 0.0);
        }

        return filters with
        {
            ChromaBurst = chromaResponse,
            ChromaBurstMagnitude = chromaResponse
                .Select(static value => value.Magnitude)
                .ToArray(),
            ChromaOffsetSamples = 13,
            ChromaBurstSos = chromaSos,
            ChromaBurstUsesDemodulatedVideo = false
        };
    }

    private static void ReplaceApproxPipelineFilterSources(
        DecodeFilterSet filters,
        int filterSeed)
    {
        ReplaceApproxFilterHalfSpectrum(filters.RfVideo, filterSeed);
        ReplaceApproxFilterHalfSpectrum(filters.RfHighPass, filterSeed + 1);
        ReplaceApproxFilterHalfSpectrum(filters.RfMtf, filterSeed + 2);
        ReplaceApproxFilterHalfSpectrum(filters.Video, filterSeed + 3);
        ReplaceApproxFilterHalfSpectrum(filters.VideoLowPass05, filterSeed + 5);
    }

    private static void ReplaceApproxFilterHalfSpectrum(
        Complex[] destination,
        int filterSeed)
    {
        int spectrumLength = (Length / 2) + 1;
        BuildResidentSpectrumFilter(Length, filterSeed)
            .AsSpan(0, spectrumLength)
            .CopyTo(destination);
    }

    private static string DecodePipelineBlockHash(
        RfBlockDecodePipeline pipeline,
        double[] input,
        Complex[]? rfMtfOverride = null)
        => Hash(pipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false,
            rfMtfOverride: rfMtfOverride).Demodulated);

    private static float[] DecodePipelineChromaFloat32(
        RfBlockDecodePipeline pipeline,
        double[] input)
    {
        RfPipelineBlock block = pipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        try
        {
            return Assert.IsType<float[]>(block.Demodulated.ChromaFloat32).ToArray();
        }
        finally
        {
            pipeline.ReleaseStreamBlock(block);
        }
    }

    private static double[] DecodePipelineChromaFloat64(
        RfBlockDecodePipeline pipeline,
        double[] input)
    {
        RfPipelineBlock block = pipeline.DecodePreparedStreamBlock(
            input,
            reportDiagnostics: false);
        try
        {
            return Assert.IsType<double[]>(block.Demodulated.Chroma).ToArray();
        }
        finally
        {
            pipeline.ReleaseStreamBlock(block);
        }
    }

    private static RfDemodulatedBlock Decode(
        RfDemodulator demodulator,
        double[] input,
        Complex[] identity,
        SosSection[] identitySos)
        => demodulator.Demodulate(
            input,
            identity,
            identity,
            ReadOnlySpan<Complex>.Empty,
            identity,
            identity,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation,
            vhsEnvelopeFilter: identitySos);

    private static RfDemodulatedBlock DecodeResidentSpectrumProbe(
        RfDemodulator demodulator,
        double[] input,
        int filterSeed,
        SubDeemphasisOptions? subDeemphasis = null)
    {
        Complex[] rfVideoFilter = BuildResidentSpectrumFilter(input.Length, filterSeed);
        Complex[] rfHighPassFilter = BuildResidentSpectrumFilter(input.Length, filterSeed + 1);
        Complex[] rfMtfFilter = BuildResidentSpectrumFilter(input.Length, filterSeed + 2);
        Complex[] videoFilter = BuildResidentSpectrumFilter(input.Length, filterSeed + 3);
        Complex[] videoLowPassFilter = BuildResidentSpectrumFilter(input.Length, filterSeed + 4);
        SosSection[] identitySos =
        [
            new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
        ];

        return demodulator.Demodulate(
            input,
            rfVideoFilter,
            rfHighPassFilter,
            rfMtfFilter,
            videoFilter,
            videoLowPassFilter,
            subDeemphasis: subDeemphasis,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation,
            vhsEnvelopeFilter: identitySos);
    }

    private static Complex[] BuildResidentSpectrumFilter(int length, int seed)
    {
        var filter = new Complex[length];
        int nyquistIndex = length / 2;
        for (int index = 0; index < filter.Length; index++)
        {
            float real = 0.625f + (((index * seed) & 15) * 0.015625f);
            float imaginary = index == 0 || index == nyquistIndex
                ? 0.0f
                : ((((index * (seed + 2)) % 17) - 8) * 0.0078125f);
            filter[index] = new Complex(real, imaginary);
        }

        return filter;
    }

    private static double[] BuildWorkspacePoisonProbe(ReadOnlySpan<double> input)
    {
        var output = new double[input.Length];
        for (int index = 0; index < output.Length; index++)
        {
            int sourceIndex = (index + 113) % input.Length;
            output[index] = (input[sourceIndex] * 0.6875)
                + (((index & 31) - 16) * 7.25);
        }

        return output;
    }

    private static double[] BuildDcAndNyquistProbe()
    {
        var input = new double[Length];
        for (int index = 0; index < input.Length; index++)
        {
            input[index] = 4096.0 + ((index & 1) == 0 ? 1024.0 : -1024.0);
        }

        return input;
    }

    private static double[] BuildPalVhsProbe()
    {
        var input = new double[Length];
        double phase = 0.0;
        for (int index = 0; index < input.Length; index++)
        {
            double linePhase = (index % 2560) / 2560.0;
            double video = (0.45 * Math.Sin(Math.Tau * linePhase))
                + (linePhase < 0.075 ? -0.75 : 0.0);
            double frequencyHz = 3_800_000.0 + (650_000.0 * video);
            phase += Math.Tau * frequencyHz / SampleRateHz;
            input[index] = (12_000.0 * Math.Cos(phase))
                + (1_300.0 * Math.Cos(Math.Tau * 627_000.0 * index / SampleRateHz));
        }

        return input;
    }

    private static void AssertApproxQuality(
        RfDemodulatedBlock reference,
        RfDemodulatedBlock actual)
    {
        ApproxMetrics[] metrics =
        [
            Measure("Video", reference.Video, actual.Video),
            Measure("DemodRaw", reference.DemodRaw, actual.DemodRaw),
            Measure("Analytic", reference.Analytic, actual.Analytic),
            Measure("Envelope", reference.Envelope, actual.Envelope),
            Measure("VideoLowPass", reference.VideoLowPass, actual.VideoLowPass),
            Measure("RfHighPass", reference.RfHighPass, actual.RfHighPass)
        ];

        Assert.All(metrics, metric => Assert.True(metric.AllFinite, metric.ToString()));
        Assert.True(
            metrics.All(metric => metric.RelativeRmsDelta <= 0.01),
            string.Join(Environment.NewLine, metrics.Select(metric => metric.ToString())));
    }

    private static void AssertFinite(ReadOnlySpan<double> values)
    {
        Assert.Equal(Length, values.Length);
        for (int index = 0; index < values.Length; index++)
        {
            Assert.True(double.IsFinite(values[index]), $"Sample {index} was {values[index]:R}.");
        }
    }

    private static ApproxMetrics Measure(
        string name,
        ReadOnlySpan<double> reference,
        ReadOnlySpan<double> actual)
    {
        Assert.Equal(reference.Length, actual.Length);
        double deltaSquares = 0.0;
        double referenceSquares = 0.0;
        double maximumAbsoluteDelta = 0.0;
        bool allFinite = true;
        for (int index = 0; index < reference.Length; index++)
        {
            allFinite &= double.IsFinite(reference[index]) && double.IsFinite(actual[index]);
            double delta = actual[index] - reference[index];
            deltaSquares += delta * delta;
            referenceSquares += reference[index] * reference[index];
            maximumAbsoluteDelta = Math.Max(maximumAbsoluteDelta, Math.Abs(delta));
        }

        double referenceRms = Math.Sqrt(referenceSquares / reference.Length);
        double rmsDelta = Math.Sqrt(deltaSquares / reference.Length);
        return new ApproxMetrics(
            name,
            allFinite,
            maximumAbsoluteDelta,
            rmsDelta,
            referenceRms,
            referenceRms == 0.0 ? rmsDelta : rmsDelta / referenceRms);
    }

    private static ApproxMetrics Measure(
        string name,
        ReadOnlySpan<Complex> reference,
        ReadOnlySpan<Complex> actual)
    {
        Assert.Equal(reference.Length, actual.Length);
        double deltaSquares = 0.0;
        double referenceSquares = 0.0;
        double maximumAbsoluteDelta = 0.0;
        bool allFinite = true;
        for (int index = 0; index < reference.Length; index++)
        {
            allFinite &= double.IsFinite(reference[index].Real)
                && double.IsFinite(reference[index].Imaginary)
                && double.IsFinite(actual[index].Real)
                && double.IsFinite(actual[index].Imaginary);
            double delta = Complex.Abs(actual[index] - reference[index]);
            double referenceMagnitude = reference[index].Magnitude;
            deltaSquares += delta * delta;
            referenceSquares += referenceMagnitude * referenceMagnitude;
            maximumAbsoluteDelta = Math.Max(maximumAbsoluteDelta, delta);
        }

        double referenceRms = Math.Sqrt(referenceSquares / reference.Length);
        double rmsDelta = Math.Sqrt(deltaSquares / reference.Length);
        return new ApproxMetrics(
            name,
            allFinite,
            maximumAbsoluteDelta,
            rmsDelta,
            referenceRms,
            referenceRms == 0.0 ? rmsDelta : rmsDelta / referenceRms);
    }

    private static string Hash(RfDemodulatedBlock block)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, block.Video);
        Append(hash, block.DemodRaw);
        Append(hash, block.Analytic);
        Append(hash, block.Envelope);
        Append(hash, block.VideoLowPass);
        Append(hash, block.RfHighPass);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashFloat32(ReadOnlySpan<float> values)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(values));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<double> values)
        => hash.AppendData(MemoryMarshal.AsBytes(values));

    private static void Append(IncrementalHash hash, ReadOnlySpan<Complex> values)
        => hash.AppendData(MemoryMarshal.AsBytes(values));

    private sealed record ApproxMetrics(
        string Name,
        bool AllFinite,
        double MaximumAbsoluteDelta,
        double RmsDelta,
        double ReferenceRms,
        double RelativeRmsDelta)
    {
        public override string ToString()
            => $"{Name}: finite={AllFinite}, maxAbs={MaximumAbsoluteDelta:R}, rms={RmsDelta:R}, referenceRms={ReferenceRms:R}, relativeRms={RelativeRmsDelta:R}";
    }
}
