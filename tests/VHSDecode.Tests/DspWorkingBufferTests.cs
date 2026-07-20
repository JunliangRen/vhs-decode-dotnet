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

    private static string Hash(RfPipelineBlock block)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, block.Demodulated.Video);
        Append(hash, block.Demodulated.DemodRaw);
        Append(hash, block.Demodulated.Envelope);
        Append(hash, block.Demodulated.VideoLowPass);
        Append(hash, block.Demodulated.RfHighPass);
        if (block.Demodulated.Chroma is not null)
        {
            Append(hash, block.Demodulated.Chroma);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, double[] values)
    {
        hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
    }
}
