using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscAudioPhase2BufferTests
{
    [Fact(DisplayName = "LD short-field audio retains padded scalar bits without redundant real buffers")]
    public void ShortFieldRetainsPaddedScalarBitsWithoutRedundantRealBuffers()
    {
        const int length = 8_192;
        const int blockLength = 32_768;
        var filter = new Complex[blockLength];
        for (int i = 0; i < filter.Length; i++)
        {
            filter[i] = new Complex(1.0 / (1.0 + i), (i % 31 - 15) / 127.0);
        }

        var channel = new LaserDiscAnalogAudioChannelFilter(
            LowBin: 0,
            BinCount: blockLength,
            SliceSampleRateHz: 1_250_000.0,
            LowFrequencyHz: 0.0,
            CenterFrequencyHz: 0.0,
            Stage1Filter: [],
            Stage2Filter: filter);
        var filters = new LaserDiscAnalogAudioFilterSet(channel, channel, 32);
        double[] left = Enumerable.Range(0, length)
            .Select(i => (((i * 7_919) % 200_003) - 100_001) * 0.25 + 0.00000001)
            .ToArray();
        double[] right = left.Reverse().ToArray();
        left[0] = -0.0;
        right[^1] = 0.0;
        var input = new LaserDiscAnalogAudioBlock(left, right, 32);
        double[] expectedLeft = FilterScalarWithRealPadding(left, filter);
        double[] expectedRight = FilterScalarWithRealPadding(right, filter);

        LaserDiscAnalogAudioPhase2.Apply(input, filters);
        long before = GC.GetAllocatedBytesForCurrentThread();
        LaserDiscAnalogAudioBlock actual = LaserDiscAnalogAudioPhase2.Apply(input, filters);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(actual.UsesFloat32Storage);
        Assert.True(MemoryMarshal.AsBytes(expectedLeft.AsSpan())
            .SequenceEqual(MemoryMarshal.AsBytes(actual.Left.AsSpan())));
        Assert.True(MemoryMarshal.AsBytes(expectedRight.AsSpan())
            .SequenceEqual(MemoryMarshal.AsBytes(actual.Right.AsSpan())));
        // Allow FFT/runtime overhead, but catch redundant real padding, spectrum or output copies.
        Assert.True(allocated < 1_300_000, $"Short-field audio allocated {allocated:N0} bytes.");
    }

    [Theory(DisplayName = "LD short-field audio returns independently owned channel buffers")]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(1_023)]
    [InlineData(1_024)]
    public void ShortFieldReturnsIndependentlyOwnedChannelBuffers(int length)
    {
        const int blockLength = 1_024;
        Complex[] identity = Enumerable.Repeat(Complex.One, blockLength).ToArray();
        var channel = new LaserDiscAnalogAudioChannelFilter(
            LowBin: 0,
            BinCount: blockLength,
            SliceSampleRateHz: 1_250_000.0,
            LowFrequencyHz: 0.0,
            CenterFrequencyHz: 1_000.0,
            Stage1Filter: [],
            Stage2Filter: identity);
        var filters = new LaserDiscAnalogAudioFilterSet(channel, channel, 32);
        double[] source = Enumerable.Range(0, length).Select(i => 1_000.0 + i).ToArray();
        double[] original = source.ToArray();
        var input = new LaserDiscAnalogAudioBlock(source, source, 32);

        LaserDiscAnalogAudioBlock first = LaserDiscAnalogAudioPhase2.Apply(input, filters);
        Assert.False(first.UsesFloat32Storage);
        Assert.NotSame(source, first.Left);
        Assert.NotSame(source, first.Right);
        Assert.NotSame(first.Left, first.Right);
        double[] expected = first.Right.ToArray();
        first.Left[0] = -123.0;
        Assert.Equal(expected, first.Right);
        Assert.Equal(original, source);

        LaserDiscAnalogAudioBlock second = LaserDiscAnalogAudioPhase2.Apply(input, filters);
        Assert.NotSame(first.Left, second.Left);
        Assert.NotSame(first.Right, second.Right);
        Assert.NotSame(second.Left, second.Right);
        Assert.Equal(expected, second.Left);
        Assert.Equal(expected, second.Right);
    }

    private static double[] FilterScalarWithRealPadding(double[] input, Complex[] filter)
    {
        var padded = new double[filter.Length];
        for (int i = 0; i < input.Length; i++)
        {
            padded[i] = (float)input[i] - 0.0f;
        }

        Complex[] spectrum = PocketFftComplex.ForwardDucc(padded);
        for (int i = 0; i < spectrum.Length; i++)
        {
            Complex value = spectrum[i];
            Complex multiplier = filter[i];
            spectrum[i] = new Complex(
                Math.FusedMultiplyAdd(value.Real, multiplier.Real, -(value.Imaginary * multiplier.Imaginary)),
                Math.FusedMultiplyAdd(value.Real, multiplier.Imaginary, value.Imaginary * multiplier.Real));
        }

        PocketFftComplex.InverseDuccInPlace(spectrum);
        return spectrum.Take(input.Length).Select(value => value.Real + 0.0).ToArray();
    }
}
