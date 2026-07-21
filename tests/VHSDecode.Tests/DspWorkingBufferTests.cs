using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DspWorkingBufferCollection
{
    public const string Name = "DSP working buffers";
}

[Collection(DspWorkingBufferCollection.Name)]
public sealed class DspWorkingBufferTests
{
    [Fact(DisplayName = "Pooled real FFT scratch remains bit-exact under parallel load")]
    public void PooledRealFftScratchRemainsBitExactUnderParallelLoad()
    {
        double[] input = Enumerable.Range(0, 4_096)
            .Select(index => Math.Sin(index * 0.017) + (0.25 * Math.Cos(index * 0.031)))
            .ToArray();
        Complex[] expectedSpectrum = PocketFftReal.Forward(input);
        double[] expectedInverse = PocketFftReal.Inverse(expectedSpectrum, input.Length);

        Parallel.For(
            0,
            24,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            _ =>
            {
                Complex[] actualSpectrum = PocketFftReal.Forward(input);
                double[] actualInverse = PocketFftReal.Inverse(actualSpectrum, input.Length);
                Assert.Equal(expectedSpectrum, actualSpectrum);
                Assert.Equal(expectedInverse, actualInverse);
            });
    }

    [Fact(DisplayName = "Caller-provided real FFT buffers match allocating APIs exactly")]
    public void CallerProvidedRealFftBuffersMatchAllocatingApisExactly()
    {
        const int length = 4_096;
        double[] input = Enumerable.Range(0, length)
            .Select(index => Math.Sin(index * 0.019) + (0.375 * Math.Cos(index * 0.037)))
            .ToArray();
        Complex[] expectedSpectrum = PocketFftReal.Forward(input);
        double[] expectedInverse = PocketFftReal.Inverse(expectedSpectrum, length);

        var spectrumBuffer = Enumerable.Repeat(
            new Complex(double.NaN, double.NaN),
            expectedSpectrum.Length + 19).ToArray();
        PocketFftReal.Forward(input, spectrumBuffer);
        Assert.Equal(expectedSpectrum, spectrumBuffer.AsSpan(0, expectedSpectrum.Length).ToArray());
        Assert.All(spectrumBuffer[expectedSpectrum.Length..], value =>
        {
            Assert.True(double.IsNaN(value.Real));
            Assert.True(double.IsNaN(value.Imaginary));
        });

        var inverseBuffer = Enumerable.Repeat(double.NaN, length + 23).ToArray();
        PocketFftReal.Inverse(expectedSpectrum, length, inverseBuffer);
        Assert.Equal(expectedInverse, inverseBuffer.AsSpan(0, length).ToArray());
        Assert.All(inverseBuffer[length..], value => Assert.True(double.IsNaN(value)));
    }

    [Fact(DisplayName = "32K real FFT radix stages remain bit-exact")]
    public void ThirtyTwoKilobyteRealFftRadixStagesRemainBitExact()
    {
        const int length = 32_768;
        var input = new double[length];
        uint state = 0x12345678;
        for (int index = 0; index < input.Length; index++)
        {
            state = (1_664_525 * state) + 1_013_904_223;
            input[index] = ((state >> 8) / 16_777_216.0) - 0.5;
        }

        Complex[] spectrum = PocketFftReal.Forward(input);
        double[] inverse = PocketFftReal.Inverse(spectrum, length);

        Assert.Equal(
            "2109467E306D566CF2E101D16D0DEB464BB235BD1D896847980A956EC4E0FF32",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(spectrum.AsSpan()))));
        Assert.Equal(
            "252170F26D392BD773B180ABAFF095B2DE32361B70531C65AAEA666DA5C5D4D4",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(inverse.AsSpan()))));
    }

    [Fact(DisplayName = "In-place RF rotations match allocating compatibility paths exactly")]
    public void InPlaceRfRotationsMatchAllocatingCompatibilityPathsExactly()
    {
        foreach (int length in new[] { 1, 2, 7, 32, 257 })
        {
            double[] values = Enumerable.Range(0, length)
                .Select(index => Math.Sin(index * 0.17) + (index * 0.03125))
                .ToArray();
            for (int shift = -(length * 2) - 3; shift <= (length * 2) + 3; shift++)
            {
                double[] expected = FrequencyDomainFilter.Roll(values, shift);
                double[] actual = values.ToArray();
                FrequencyDomainFilter.RollInPlace(actual, shift);
                Assert.Equal(expected, actual);
            }
        }

        double[] chroma = Enumerable.Range(0, 257)
            .Select(index => Math.Sin(index * 0.071) * 12_000.0)
            .ToArray();
        double[] expectedDouble = VhsChromaDecoder.ShiftChromaAndRemoveDc(chroma, -19);
        double[] actualDouble = chroma.ToArray();
        VhsChromaDecoder.ShiftChromaAndRemoveDcInPlace(actualDouble, -19);
        Assert.Equal(expectedDouble, actualDouble);

        double[] expectedFloat = VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32(chroma, 23);
        double[] actualFloat = chroma.ToArray();
        VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32InPlace(actualFloat, 23);
        Assert.Equal(expectedFloat, actualFloat);
    }

    [Fact(DisplayName = "Unconfigured VHS chroma prefilter borrows input without changing public ownership")]
    public void UnconfiguredVhsChromaPrefilterBorrowsInputWithoutChangingPublicOwnership()
    {
        var options = new VhsChromaFieldOptions(
            ColorSystem: "PAL",
            OutputLineLength: 64,
            OutputLineCount: 64,
            OutputSampleRateHz: 4_000_000.0,
            FscMHz: 1.0,
            ColorUnderCarrierHz: 0.0,
            BurstStart: 8,
            BurstEnd: 16,
            BurstAbsRef: 10.0,
            ChromaRotation: null,
            DisableComb: true,
            DisablePhaseCorrection: true,
            EnableColorKiller: false,
            DetectChromaTrackPhase: false);
        double[] chroma = Enumerable.Range(0, options.OutputLineLength * options.OutputLineCount)
            .Select(index => Math.Sin(index * 0.03125) + (index * 0.0001))
            .ToArray();
        double[] original = chroma.ToArray();

        _ = VhsChromaDecoder.ApplyConfiguredChromaPreFilter(chroma, options);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double[]? borrowed = VhsChromaDecoder.ApplyConfiguredChromaPreFilter(chroma, options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(borrowed);
        Assert.True(
            allocated < 256,
            $"Unconfigured VHS chroma prefilter allocated {allocated:N0} bytes.");
        Assert.Equal(original, chroma);

        double[] publicResult = VhsChromaDecoder.ApplyChromaPreFilter(chroma, options);
        Assert.NotSame(chroma, publicResult);
        Assert.Equal(original, publicResult);
        publicResult[0] += 1.0;
        Assert.Equal(original, chroma);
    }

    [Fact(DisplayName = "Fused VHS chroma comb and gain match allocating stages with one field output")]
    public void FusedVhsChromaCombAndGainMatchAllocatingStagesWithOneFieldOutput()
    {
        const int lineLength = 257;
        const int lines = 24;
        const int burstStart = 8;
        const int burstEnd = 32;
        const double burstAbsRef = 30_000.0;
        double[] input = Enumerable.Range(0, lines * lineLength)
            .Select(index => Math.Sin(index * 0.019) + (index * 0.00037))
            .ToArray();
        double[] original = input.ToArray();

        foreach (bool retainFloat32 in new[] { true, false })
        {
            foreach (bool useFloat32Rms in new[] { true, false })
            {
                AssertFusedCombGain(
                    input,
                    lineLength,
                    lines,
                    burstStart,
                    burstEnd,
                    burstAbsRef,
                    lineDistance: 1,
                    retainFloat32,
                    useFloat32Rms);
                AssertFusedCombGain(
                    input,
                    lineLength,
                    lines,
                    burstStart,
                    burstEnd,
                    burstAbsRef,
                    lineDistance: 2,
                    retainFloat32,
                    useFloat32Rms);
            }
        }

        Assert.Equal(original, input);

        const int productionLineLength = 1_135;
        const int productionLines = 273;
        double[] allocationProbe = Enumerable.Range(0, productionLines * productionLineLength)
            .Select(index => Math.Cos(index * 0.013) - (index * 0.00011))
            .ToArray();
        _ = VhsChromaDecoder.ApplyAutomaticChromaGainWithComb(
            allocationProbe,
            burstAbsRef,
            burstStart,
            burstEnd,
            productionLineLength,
            productionLines,
            burstDetectedLine: 0,
            lineDistance: 2,
            retainFloat32: true,
            useFloat32Rms: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        AutomaticChromaGainResult result = VhsChromaDecoder.ApplyAutomaticChromaGainWithComb(
            allocationProbe,
            burstAbsRef,
            burstStart,
            burstEnd,
            productionLineLength,
            productionLines,
            burstDetectedLine: 0,
            lineDistance: 2,
            retainFloat32: true,
            useFloat32Rms: true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(result);
        long maximumExpected = ((long)allocationProbe.Length * sizeof(double)) + 4_096;
        Assert.True(
            allocated < maximumExpected,
            $"Fused VHS chroma comb/gain allocated {allocated:N0} bytes.");
    }

    [Fact(DisplayName = "Fused VHS chroma gain-to-U16 matches allocating stages with one output")]
    public void FusedVhsChromaGainToU16MatchesAllocatingStagesWithOneOutput()
    {
        const int lineLength = 257;
        const int lines = 25;
        const int burstStart = 8;
        const int burstEnd = 32;
        const double burstAbsRef = 30_000.0;
        double[] input = Enumerable.Range(0, lines * lineLength)
            .Select(index => Math.Sin(index * 0.019) + (index * 0.00037))
            .ToArray();
        input[(18 * lineLength) + 100] = double.NaN;
        input[(18 * lineLength) + 101] = double.PositiveInfinity;
        input[(18 * lineLength) + 102] = double.NegativeInfinity;
        input[^2] = -50_339.0;
        input[^1] = 80_733.0;
        double[] original = input.ToArray();

        foreach (bool useFloat32Rms in new[] { true, false })
        {
            AssertFusedGainToU16(
                input,
                lineLength,
                lines,
                burstStart,
                burstEnd,
                burstAbsRef,
                useFloat32Rms);
            foreach (bool retainFloat32 in new[] { true, false })
            {
                AssertFusedCombGainToU16(
                    input,
                    lineLength,
                    lines,
                    burstStart,
                    burstEnd,
                    burstAbsRef,
                    lineDistance: 1,
                    retainFloat32,
                    useFloat32Rms);
                AssertFusedCombGainToU16(
                    input,
                    lineLength,
                    lines,
                    burstStart,
                    burstEnd,
                    burstAbsRef,
                    lineDistance: 2,
                    retainFloat32,
                    useFloat32Rms);
            }
        }

        Assert.Equal(original, input);

        const int productionLineLength = 1_135;
        const int productionLines = 273;
        double[] allocationProbe = Enumerable.Range(0, productionLines * productionLineLength)
            .Select(index => Math.Cos(index * 0.013) - (index * 0.00011))
            .ToArray();
        _ = VhsChromaDecoder.ApplyAutomaticChromaGainToU16(
            allocationProbe,
            burstAbsRef,
            burstStart,
            burstEnd,
            productionLineLength,
            productionLines,
            burstDetectedLine: 0,
            useFloat32Rms: true);
        _ = VhsChromaDecoder.ApplyAutomaticChromaGainWithCombToU16(
            allocationProbe,
            burstAbsRef,
            burstStart,
            burstEnd,
            productionLineLength,
            productionLines,
            burstDetectedLine: 0,
            lineDistance: 2,
            retainFloat32: true,
            useFloat32Rms: true);

        long maximumExpected = ((long)allocationProbe.Length * sizeof(ushort)) + 4_096;
        long before = GC.GetAllocatedBytesForCurrentThread();
        ushort[] gainResult = VhsChromaDecoder.ApplyAutomaticChromaGainToU16(
            allocationProbe,
            burstAbsRef,
            burstStart,
            burstEnd,
            productionLineLength,
            productionLines,
            burstDetectedLine: 0,
            useFloat32Rms: true);
        long gainAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(gainResult);
        Assert.True(
            gainAllocated < maximumExpected,
            $"Fused VHS chroma gain-to-U16 allocated {gainAllocated:N0} bytes.");

        before = GC.GetAllocatedBytesForCurrentThread();
        ushort[] combResult = VhsChromaDecoder.ApplyAutomaticChromaGainWithCombToU16(
            allocationProbe,
            burstAbsRef,
            burstStart,
            burstEnd,
            productionLineLength,
            productionLines,
            burstDetectedLine: 0,
            lineDistance: 2,
            retainFloat32: true,
            useFloat32Rms: true);
        long combAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(combResult);
        Assert.True(
            combAllocated < maximumExpected,
            $"Fused VHS chroma comb/gain-to-U16 allocated {combAllocated:N0} bytes.");
    }

    [Fact(DisplayName = "PAL VHS RF block reuses temporary spectra after warm-up")]
    public void PalVhsRfBlockReusesTemporarySpectraAfterWarmUp()
    {
        const int length = DecodeSessionFactory.DefaultBlockLength;
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--pal", "--no_resample", "probe.s16", "probe-output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command, length);
        double[] input = BuildPalVhsProbe(length, session.DecodeSampleRateHz);

        for (int i = 0; i < 4; i++)
        {
            _ = session.Pipeline.DecodePreparedBlock(input, reportDiagnostics: false);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        RfPipelineBlock block = session.Pipeline.DecodePreparedBlock(input, reportDiagnostics: false);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(
            "AEC0D4FFFF58D5A35771AE7374C0A62C1DA955125249B91C6AD9946D7BBBFEF4",
            Hash(block));
        Assert.True(
            allocated < 3_000_000,
            $"Warm PAL VHS RF block allocated {allocated:N0} bytes.");
    }

    [Fact(DisplayName = "Compact PAL VHS RF blocks omit the unconsumed analytic channel")]
    public void CompactPalVhsRfBlocksOmitTheUnconsumedAnalyticChannel()
    {
        const int length = DecodeSessionFactory.DefaultBlockLength;
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--pal", "--no_resample", "probe.s16", "probe-output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command, length);
        double[] input = BuildPalVhsProbe(length, session.DecodeSampleRateHz);

        RfPipelineBlock full = session.Pipeline.DecodePreparedBlock(input, reportDiagnostics: false);
        RfPipelineBlock compact = session.Pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false);

        Assert.Equal(length, full.Demodulated.Analytic.Length);
        Assert.Empty(compact.Input);
        Assert.Empty(compact.Demodulated.DemodRaw);
        Assert.Empty(compact.Demodulated.Analytic);
        Assert.Empty(compact.Demodulated.RfHighPass);
        Assert.Equal(full.Demodulated.Video, compact.Demodulated.Video);
        Assert.Equal(full.Demodulated.Envelope, compact.Demodulated.Envelope);
        Assert.Equal(full.Demodulated.VideoLowPass, compact.Demodulated.VideoLowPass);
        Assert.Equal(full.Demodulated.VhsWeakRfSignal, compact.Demodulated.VhsWeakRfSignal);
        Assert.NotNull(full.Demodulated.Chroma);
        Assert.Null(compact.Demodulated.Chroma);
        float[] compactChroma = Assert.IsType<float[]>(compact.Demodulated.ChromaFloat32);
        Assert.Equal(full.Demodulated.Chroma.Length, compactChroma.Length);
        for (int i = 0; i < compactChroma.Length; i++)
        {
            Assert.Equal(full.Demodulated.Chroma[i], (double)compactChroma[i]);
        }
    }

    [Fact(DisplayName = "VHS diff-demod repair reuses its analytic workspace after warm-up")]
    public void VhsDiffDemodRepairReusesAnalyticWorkspaceAfterWarmUp()
    {
        const int length = DecodeSessionFactory.DefaultBlockLength;
        const double sampleRateHz = 40_000_000.0;
        double[] input = BuildPalVhsProbe(length, sampleRateHz);
        Complex[] identity = RfDemodulator.IdentityFilter(length);
        SosSection[] identitySos = [new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)];
        var demodulator = new RfDemodulator(sampleRateHz);

        RfDemodulatedBlock expected = DecodeDiffRepairProbe(
            demodulator,
            input,
            identity,
            identitySos);
        for (int i = 0; i < 3; i++)
        {
            _ = DecodeDiffRepairProbe(demodulator, input, identity, identitySos);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        RfDemodulatedBlock actual = DecodeDiffRepairProbe(
            demodulator,
            input,
            identity,
            identitySos);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected.Analytic, actual.Analytic);
        Assert.Equal(Hash(expected), Hash(actual));
        Assert.True(
            allocated < 2_600_000,
            $"Warm VHS diff-demod RF block allocated {allocated:N0} bytes.");
    }

    [Fact(DisplayName = "PAL VHS RF workspaces remain bit-exact under parallel load")]
    public void PalVhsRfWorkspacesRemainBitExactUnderParallelLoad()
    {
        const int length = DecodeSessionFactory.DefaultBlockLength;
        const string expectedHash =
            "AEC0D4FFFF58D5A35771AE7374C0A62C1DA955125249B91C6AD9946D7BBBFEF4";
        ParsedCommand command = new CommandLineParser().Parse(
            CliSpecs.Vhs,
            ["--pal", "--no_resample", "probe.s16", "probe-output"]);
        using DecodeSession session = DecodeSessionFactory.Create(command, length);
        double[] input = BuildPalVhsProbe(length, session.DecodeSampleRateHz);
        var hashes = new string[32];

        Parallel.For(
            0,
            hashes.Length,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            index =>
            {
                RfPipelineBlock block = session.Pipeline.DecodePreparedBlock(
                    input,
                    reportDiagnostics: false);
                hashes[index] = Hash(block);
            });

        Assert.All(hashes, hash => Assert.Equal(expectedHash, hash));
    }

    [Fact(DisplayName = "Hot DSP paths retain bounded managed allocation after warm-up")]
    public void HotDspPathsRetainBoundedManagedAllocationAfterWarmUp()
    {
        const int length = 32_768;
        double[] input = Enumerable.Range(0, length)
            .Select(index => Math.Sin(index * 0.013) + (0.125 * Math.Cos(index * 0.029)))
            .ToArray();

        _ = PocketFftReal.Forward(input);
        long beforeForward = GC.GetAllocatedBytesForCurrentThread();
        Complex[] spectrum = PocketFftReal.Forward(input);
        long forwardBytes = GC.GetAllocatedBytesForCurrentThread() - beforeForward;
        GC.KeepAlive(spectrum);
        Assert.True(
            forwardBytes < 350_000,
            $"Warm real FFT forward allocated {forwardBytes:N0} bytes.");

        _ = PocketFftReal.Inverse(spectrum, length);
        long beforeInverse = GC.GetAllocatedBytesForCurrentThread();
        double[] inverse = PocketFftReal.Inverse(spectrum, length);
        long inverseBytes = GC.GetAllocatedBytesForCurrentThread() - beforeInverse;
        GC.KeepAlive(inverse);
        Assert.True(
            inverseBytes < 350_000,
            $"Warm real FFT inverse allocated {inverseBytes:N0} bytes.");

        SosSection[] sections =
        [
            new SosSection(0.06745527, 0.13491055, 0.06745527, 1.0, -1.1429805, 0.4128016),
            new SosSection(1.0, 2.0, 1.0, 1.0, -1.4043849, 0.7359152)
        ];
        _ = SosFilter.ApplyForwardBackwardFloat32(sections, input);
        long beforeSos = GC.GetAllocatedBytesForCurrentThread();
        double[] filtered = SosFilter.ApplyForwardBackwardFloat32(sections, input);
        long sosBytes = GC.GetAllocatedBytesForCurrentThread() - beforeSos;
        GC.KeepAlive(filtered);
        Assert.True(
            sosBytes < 300_000,
            $"Warm float32 SOS forward/backward allocated {sosBytes:N0} bytes.");

        _ = SosFilter.ApplyForwardBackwardFloat32ToSingle(sections, input);
        long beforeCompactSos = GC.GetAllocatedBytesForCurrentThread();
        float[] compactFiltered = SosFilter.ApplyForwardBackwardFloat32ToSingle(sections, input);
        long compactSosBytes = GC.GetAllocatedBytesForCurrentThread() - beforeCompactSos;
        GC.KeepAlive(compactFiltered);
        Assert.True(
            compactSosBytes < 170_000,
            $"Warm compact float32 SOS forward/backward allocated {compactSosBytes:N0} bytes.");

        TransferFunction iir = IirFilterDesign.ButterworthLowPassTransferFunction(
            order: 4,
            normalizedCutoff: 0.2);
        _ = IirFilter.ApplyForwardBackward(iir, input);
        long beforeIir = GC.GetAllocatedBytesForCurrentThread();
        double[] iirFiltered = IirFilter.ApplyForwardBackward(iir, input);
        long iirBytes = GC.GetAllocatedBytesForCurrentThread() - beforeIir;
        GC.KeepAlive(iirFiltered);
        Assert.True(
            iirBytes < 350_000,
            $"Warm IIR forward/backward allocated {iirBytes:N0} bytes.");

        const int outputLineLength = 512;
        const int lineCount = 64;
        double[] resampleSource = Enumerable.Range(0, (lineCount + 1) * 1_024 + 16)
            .Select(index => Math.Sin(index * 0.007) + (0.2 * Math.Cos(index * 0.011)))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => line * 1_024.0)
            .ToArray();
        var resampler = new TbcLineResampler(
            outputLineLength,
            nominalInputLineLength: 1_024.0,
            workerThreads: 1);
        _ = resampler.ResampleLines(resampleSource, lineLocations, firstLine: 0, lineCount);
        long beforeResample = GC.GetAllocatedBytesForCurrentThread();
        double[] resampled = resampler.ResampleLines(
            resampleSource,
            lineLocations,
            firstLine: 0,
            lineCount);
        long resampleBytes = GC.GetAllocatedBytesForCurrentThread() - beforeResample;
        GC.KeepAlive(resampled);
        Assert.True(
            resampleBytes < 350_000,
            $"Warm linear TBC resampling allocated {resampleBytes:N0} bytes.");
    }

    [Fact(DisplayName = "Float32 SOS common-section kernels remain bit-exact")]
    public void Float32SosCommonSectionKernelsRemainBitExact()
    {
        const int length = 32_768;
        double[] input = Enumerable.Range(0, length)
            .Select(index => Math.Sin(index * 0.013) + (0.125 * Math.Cos(index * 0.029)))
            .ToArray();
        SosSection[] sections =
        [
            new SosSection(0.06745527, 0.13491055, 0.06745527, 1.0, -1.1429805, 0.4128016),
            new SosSection(1.0, 2.0, 1.0, 1.0, -1.4043849, 0.7359152)
        ];

        double[] output = SosFilter.ApplyForwardBackwardFloat32(sections, input);
        float[] compactOutput = SosFilter.ApplyForwardBackwardFloat32ToSingle(sections, input);

        Assert.Equal(output.Length, compactOutput.Length);
        for (int i = 0; i < output.Length; i++)
        {
            Assert.Equal(output[i], (double)compactOutput[i]);
        }

        Assert.Equal(
            "0FCE85E5CEB0155E93B8C49D41679A5D40D76D931138D2469D3F7E5EBABAF7D5",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));

        SosSection[] fourSections = IirFilterDesign.ButterworthBandPassSos(
            order: 4,
            normalizedLowCutoff: 0.1,
            normalizedHighCutoff: 0.4);
        output = SosFilter.ApplyForwardBackwardFloat32(fourSections, input);
        Assert.Equal(
            "A8BE7ABAF7A46FC3F7674726B0295133EA5329679EBAFB06E94E253302ECD228",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));

        (int Order, string Hash)[] genericCases =
        [
            (5, "621A5D26038D8CEF52EA92BEEA4BCF06083246C28316617CC6FCA99E180A9DDE"),
            (8, "D8DA7CDBC6147027D7CD040B337083890D19895DA284E6AD8BC34693AED9F6BE"),
            (10, "0BE5EFFF1ABF73E14AE2B8F6F60AFFF71C5BFD9EC2A27CE492C9EA9C0CC11E2C")
        ];
        foreach ((int order, string hash) in genericCases)
        {
            SosSection[] genericSections = IirFilterDesign.ButterworthBandPassSos(
                order,
                normalizedLowCutoff: 0.1,
                normalizedHighCutoff: 0.4);
            output = SosFilter.ApplyForwardBackwardFloat32(genericSections, input);
            Assert.Equal(
                hash,
                Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));
        }
    }

    [Fact(DisplayName = "Double SOS common-section kernels remain section-major bit-exact")]
    public void DoubleSosCommonSectionKernelsRemainSectionMajorBitExact()
    {
        const int length = 4_096;
        double[] input = Enumerable.Range(0, length)
            .Select(index => Math.Sin(index * 0.013) + (0.125 * Math.Cos(index * 0.029)))
            .ToArray();
        SosSection[][] cases =
        [
            IirFilterDesign.ButterworthBandPassSos(
                order: 2,
                normalizedLowCutoff: 0.1,
                normalizedHighCutoff: 0.4),
            IirFilterDesign.ButterworthBandPassSos(
                order: 4,
                normalizedLowCutoff: 0.1,
                normalizedHighCutoff: 0.4)
        ];

        foreach (SosSection[] sections in cases)
        {
            var initialConditions = new double[sections.Length, 2];
            for (int section = 0; section < sections.Length; section++)
            {
                initialConditions[section, 0] = (section + 1) * 0.03125;
                initialConditions[section, 1] = -(section + 1) * 0.015625;
            }

            AssertDoubleBitsEqual(
                ApplyForwardSectionMajorReference(sections, input, initialConditions),
                SosFilter.ApplyForward(sections, input, initialConditions));
            AssertDoubleBitsEqual(
                ApplyForwardBackwardSectionMajorReference(sections, input, padLength: 0),
                SosFilter.ApplyForwardBackward(sections, input, padLength: 0));
            AssertDoubleBitsEqual(
                ApplyForwardBackwardSectionMajorReference(sections, input),
                SosFilter.ApplyForwardBackward(sections, input));
        }
    }

    [Fact(DisplayName = "In-place IIR filtering remains allocating-reference bit-exact")]
    public void InPlaceIirFilteringRemainsAllocatingReferenceBitExact()
    {
        const int length = 4_096;
        double[] input = Enumerable.Range(0, length)
            .Select(index => Math.Sin(index * 0.013) + (0.125 * Math.Cos(index * 0.029)))
            .ToArray();
        TransferFunction[] filters =
        [
            new TransferFunction([1.0], [1.0]),
            IirFilterDesign.ButterworthLowPassTransferFunction(order: 4, normalizedCutoff: 0.2),
            IirFilterDesign.ButterworthHighPassTransferFunction(order: 9, normalizedCutoff: 0.35),
            new TransferFunction([0.25, 0.1], [2.0, -0.3, 0.1]),
            new TransferFunction([0.2, 0.1, 0.05], [1.0, -0.25])
        ];

        foreach (TransferFunction filter in filters)
        {
            foreach (int? padLength in new int?[] { null, 0, 7 })
            {
                double[] expectedFiltered = ApplyForwardBackwardIirReference(filter, input, padLength);
                AssertDoubleBitsEqual(
                    expectedFiltered,
                    IirFilter.ApplyForwardBackward(filter, input, padLength));
                double[] inPlace = input.ToArray();
                IirFilter.ApplyForwardBackwardInPlace(filter, inPlace, padLength);
                AssertDoubleBitsEqual(expectedFiltered, inPlace);
            }

            int stateLength = Math.Max(filter.Numerator.Length, filter.Denominator.Length) - 1;
            double[] initialState = Enumerable.Range(0, stateLength)
                .Select(index => (index + 1) * 0.03125)
                .ToArray();
            double[] expectedFinalState = initialState.ToArray();
            double[] expected = ApplyForwardIirReference(filter, input, expectedFinalState);
            double[] actual = IirFilter.ApplyForward(filter, input, initialState, out double[] actualFinalState);
            AssertDoubleBitsEqual(expected, actual);
            AssertDoubleBitsEqual(expectedFinalState, actualFinalState);
            AssertDoubleBitsEqual(
                initialState,
                Enumerable.Range(0, stateLength).Select(index => (index + 1) * 0.03125).ToArray());
        }
    }

    private static double[] ApplyForwardBackwardIirReference(
        TransferFunction filter,
        ReadOnlySpan<double> input,
        int? padLength)
    {
        (double[] numerator, double[] denominator) = NormalizeIirReference(filter);
        int edge = padLength ?? (3 * Math.Max(numerator.Length, denominator.Length));
        double[] extended = edge == 0
            ? input.ToArray()
            : OddExtensionReference(input, Math.Min(edge, input.Length - 1));
        double[] zi = SteadyStateIirReference(numerator, denominator);
        double[] forward = ApplyForwardIirReference(
            numerator,
            denominator,
            extended,
            ScaleIirReference(zi, extended[0]));
        Array.Reverse(forward);
        double[] backward = ApplyForwardIirReference(
            numerator,
            denominator,
            forward,
            ScaleIirReference(zi, forward[0]));
        Array.Reverse(backward);
        if (edge == 0)
        {
            return backward;
        }

        int actualEdge = (extended.Length - input.Length) / 2;
        return backward.AsSpan(actualEdge, input.Length).ToArray();
    }

    private static double[] ApplyForwardIirReference(
        TransferFunction filter,
        ReadOnlySpan<double> input,
        double[] state)
    {
        (double[] numerator, double[] denominator) = NormalizeIirReference(filter);
        return ApplyForwardIirReference(numerator, denominator, input, state);
    }

    private static double[] ApplyForwardIirReference(
        double[] numerator,
        double[] denominator,
        ReadOnlySpan<double> input,
        double[] state)
    {
        int stateLength = Math.Max(numerator.Length, denominator.Length) - 1;
        var output = new double[input.Length];
        for (int sample = 0; sample < input.Length; sample++)
        {
            double x = input[sample];
            double y = (numerator[0] * x) + (stateLength > 0 ? state[0] : 0.0);
            for (int i = 1; i < stateLength; i++)
            {
                double b = i < numerator.Length ? numerator[i] : 0.0;
                double a = i < denominator.Length ? denominator[i] : 0.0;
                state[i - 1] = (b * x) + state[i] - (a * y);
            }

            if (stateLength > 0)
            {
                int i = stateLength;
                double b = i < numerator.Length ? numerator[i] : 0.0;
                double a = i < denominator.Length ? denominator[i] : 0.0;
                state[stateLength - 1] = (b * x) - (a * y);
            }

            output[sample] = y;
        }

        return output;
    }

    private static (double[] Numerator, double[] Denominator) NormalizeIirReference(
        TransferFunction filter)
    {
        double[] numerator = filter.Numerator.ToArray();
        double[] denominator = filter.Denominator.ToArray();
        double a0 = denominator[0];
        if (a0 != 1.0)
        {
            for (int i = 0; i < numerator.Length; i++)
            {
                numerator[i] /= a0;
            }

            for (int i = 0; i < denominator.Length; i++)
            {
                denominator[i] /= a0;
            }
        }

        return (numerator, denominator);
    }

    private static double[] SteadyStateIirReference(double[] numerator, double[] denominator)
    {
        int stateLength = Math.Max(numerator.Length, denominator.Length) - 1;
        if (stateLength == 0)
        {
            return [];
        }

        int coefficientCount = stateLength + 1;
        var paddedNumerator = new double[coefficientCount];
        var paddedDenominator = new double[coefficientCount];
        numerator.CopyTo(paddedNumerator, 0);
        denominator.CopyTo(paddedDenominator, 0);
        double steadyOutput = SumNumpyIirReference(paddedNumerator)
            / SumNumpyIirReference(paddedDenominator);
        var state = new double[stateLength];
        double cumulative = 0.0;
        for (int i = coefficientCount - 1; i >= 1; i--)
        {
            cumulative += paddedNumerator[i] - (steadyOutput * paddedDenominator[i]);
            state[i - 1] = cumulative;
        }

        return state;
    }

    private static double SumNumpyIirReference(ReadOnlySpan<double> values)
    {
        if (values.Length < 8)
        {
            double shortSum = -0.0;
            foreach (double value in values)
            {
                shortSum += value;
            }

            return shortSum;
        }

        double r0 = values[0];
        double r1 = values[1];
        double r2 = values[2];
        double r3 = values[3];
        double r4 = values[4];
        double r5 = values[5];
        double r6 = values[6];
        double r7 = values[7];
        int index = 8;
        int blockEnd = values.Length - (values.Length % 8);
        for (; index < blockEnd; index += 8)
        {
            r0 += values[index];
            r1 += values[index + 1];
            r2 += values[index + 2];
            r3 += values[index + 3];
            r4 += values[index + 4];
            r5 += values[index + 5];
            r6 += values[index + 6];
            r7 += values[index + 7];
        }

        double sum = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
        for (; index < values.Length; index++)
        {
            sum += values[index];
        }

        return sum;
    }

    private static double[] ScaleIirReference(ReadOnlySpan<double> values, double scale)
    {
        var output = new double[values.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = values[i] * scale;
        }

        return output;
    }

    private static double[] ApplyForwardBackwardSectionMajorReference(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<double> input,
        int? padLength = null)
    {
        int edge = padLength ?? SosFilter.DefaultPadLength(sections);
        double[] extended = edge == 0
            ? input.ToArray()
            : OddExtensionReference(input, edge);
        double[,] zi = SosFilter.SteadyStateInitialConditions(sections);
        ApplyForwardSectionMajorReferenceInPlace(
            sections,
            extended,
            ScaleInitialConditionsReference(zi, extended[0]));
        Array.Reverse(extended);
        ApplyForwardSectionMajorReferenceInPlace(
            sections,
            extended,
            ScaleInitialConditionsReference(zi, extended[0]));
        Array.Reverse(extended);
        return edge == 0
            ? extended
            : extended.AsSpan(edge, input.Length).ToArray();
    }

    private static double[] ApplyForwardSectionMajorReference(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<double> input,
        double[,] initialConditions)
    {
        double[] output = input.ToArray();
        ApplyForwardSectionMajorReferenceInPlace(sections, output, initialConditions);
        return output;
    }

    private static void ApplyForwardSectionMajorReferenceInPlace(
        IReadOnlyList<SosSection> sections,
        Span<double> output,
        double[,] initialConditions)
    {
        for (int sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            SosSection section = sections[sectionIndex].Normalize();
            double z1 = initialConditions[sectionIndex, 0];
            double z2 = initialConditions[sectionIndex, 1];
            for (int sample = 0; sample < output.Length; sample++)
            {
                double value = output[sample];
                double filtered = (section.B0 * value) + z1;
                z1 = (section.B1 * value) - (section.A1 * filtered) + z2;
                z2 = (section.B2 * value) - (section.A2 * filtered);
                output[sample] = filtered;
            }
        }
    }

    private static double[,] ScaleInitialConditionsReference(double[,] initialConditions, double scale)
    {
        var output = new double[initialConditions.GetLength(0), 2];
        for (int section = 0; section < output.GetLength(0); section++)
        {
            output[section, 0] = initialConditions[section, 0] * scale;
            output[section, 1] = initialConditions[section, 1] * scale;
        }

        return output;
    }

    private static double[] OddExtensionReference(ReadOnlySpan<double> input, int edge)
    {
        var output = new double[input.Length + (edge * 2)];
        double first = input[0];
        for (int index = 0; index < edge; index++)
        {
            output[index] = (2.0 * first) - input[edge - index];
        }

        input.CopyTo(output.AsSpan(edge, input.Length));
        double last = input[^1];
        for (int index = 0; index < edge; index++)
        {
            output[edge + input.Length + index] =
                (2.0 * last) - input[input.Length - 2 - index];
        }

        return output;
    }

    private static void AssertFusedCombGain(
        double[] input,
        int lineLength,
        int lines,
        int burstStart,
        int burstEnd,
        double burstAbsRef,
        int lineDistance,
        bool retainFloat32,
        bool useFloat32Rms)
    {
        double[] combined = lineDistance == 1
            ? VhsChromaDecoder.ApplyNtscComb(input, lineLength, retainFloat32)
            : VhsChromaDecoder.ApplyPalComb(input, lineLength, retainFloat32);
        AutomaticChromaGainResult expected = VhsChromaDecoder.ApplyAutomaticChromaGain(
            combined,
            burstAbsRef,
            burstStart,
            burstEnd,
            lineLength,
            lines,
            burstDetectedLine: 18,
            useFloat32Rms);
        AutomaticChromaGainResult actual = VhsChromaDecoder.ApplyAutomaticChromaGainWithComb(
            input,
            burstAbsRef,
            burstStart,
            burstEnd,
            lineLength,
            lines,
            burstDetectedLine: 18,
            lineDistance,
            retainFloat32,
            useFloat32Rms);

        AssertDoubleBitsEqual(expected.Samples, actual.Samples);
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(expected.MeanBurstRms),
            BitConverter.DoubleToUInt64Bits(actual.MeanBurstRms));
    }

    private static void AssertFusedGainToU16(
        double[] input,
        int lineLength,
        int lines,
        int burstStart,
        int burstEnd,
        double burstAbsRef,
        bool useFloat32Rms)
    {
        ushort[] expected = VhsChromaDecoder.ChromaToU16(
            VhsChromaDecoder.ApplyAutomaticChromaGain(
                input,
                burstAbsRef,
                burstStart,
                burstEnd,
                lineLength,
                lines,
                burstDetectedLine: 18,
                useFloat32Rms).Samples);
        ushort[] actual = VhsChromaDecoder.ApplyAutomaticChromaGainToU16(
            input,
            burstAbsRef,
            burstStart,
            burstEnd,
            lineLength,
            lines,
            burstDetectedLine: 18,
            useFloat32Rms);

        Assert.Equal(expected, actual);
    }

    private static void AssertFusedCombGainToU16(
        double[] input,
        int lineLength,
        int lines,
        int burstStart,
        int burstEnd,
        double burstAbsRef,
        int lineDistance,
        bool retainFloat32,
        bool useFloat32Rms)
    {
        double[] combined = lineDistance == 1
            ? VhsChromaDecoder.ApplyNtscComb(input, lineLength, retainFloat32)
            : VhsChromaDecoder.ApplyPalComb(input, lineLength, retainFloat32);
        ushort[] expected = VhsChromaDecoder.ChromaToU16(
            VhsChromaDecoder.ApplyAutomaticChromaGain(
                combined,
                burstAbsRef,
                burstStart,
                burstEnd,
                lineLength,
                lines,
                burstDetectedLine: 18,
                useFloat32Rms).Samples);
        ushort[] actual = VhsChromaDecoder.ApplyAutomaticChromaGainWithCombToU16(
            input,
            burstAbsRef,
            burstStart,
            burstEnd,
            lineLength,
            lines,
            burstDetectedLine: 18,
            lineDistance,
            retainFloat32,
            useFloat32Rms);

        Assert.Equal(expected, actual);
    }

    private static void AssertDoubleBitsEqual(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Double sequences differ at the bit level.");
    }

    private static double[] BuildPalVhsProbe(int length, double sampleRateHz)
    {
        var input = new double[length];
        double phase = 0.0;
        for (int i = 0; i < input.Length; i++)
        {
            double linePhase = (i % 2560) / 2560.0;
            double video = 0.45 * Math.Sin(Math.Tau * linePhase)
                + (linePhase < 0.075 ? -0.75 : 0.0);
            double frequencyHz = 3_800_000.0 + (650_000.0 * video);
            phase += Math.Tau * frequencyHz / sampleRateHz;
            input[i] = 12_000.0 * Math.Cos(phase)
                + 1_300.0 * Math.Cos(Math.Tau * 627_000.0 * i / sampleRateHz);
        }

        return input;
    }

    private static RfDemodulatedBlock DecodeDiffRepairProbe(
        RfDemodulator demodulator,
        double[] input,
        Complex[] identity,
        SosSection[] identitySos)
    {
        return demodulator.Demodulate(
            input,
            identity,
            identity,
            ReadOnlySpan<Complex>.Empty,
            identity,
            identity,
            diffDemodRepair: new DiffDemodRepairOptions(double.NegativeInfinity),
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation,
            vhsEnvelopeFilter: identitySos);
    }

    private static string Hash(RfPipelineBlock block)
        => Hash(block.Demodulated);

    private static string Hash(RfDemodulatedBlock demodulated)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, demodulated.Video);
        Append(hash, demodulated.DemodRaw);
        Append(hash, demodulated.Envelope);
        Append(hash, demodulated.VideoLowPass);
        Append(hash, demodulated.RfHighPass);
        if (demodulated.Chroma is not null)
        {
            Append(hash, demodulated.Chroma);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, double[] values)
    {
        hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
    }
}
