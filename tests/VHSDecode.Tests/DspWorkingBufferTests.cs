using System.Numerics;
using VHSDecode.Core.Dsp;
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
            sosBytes < 500_000,
            $"Warm float32 SOS forward/backward allocated {sosBytes:N0} bytes.");
    }
}
