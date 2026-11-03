using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscComplexFilterCompatibilityTests
{
    private delegate double[] DecodeChannel(ReadOnlySpan<Complex> spectrum, LaserDiscAnalogAudioChannelFilter filter);
    private delegate Complex[] FilterSignal(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<Complex> filter);

    [Theory(DisplayName = "LD analog stage-one filtering keeps normal and malformed length behavior")]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void StageOneKeepsFilterLengthBehavior(int filterLength)
    {
        using RfBlockDecodePipeline pipeline = CreatePipeline();
        DecodeChannel decode = typeof(RfBlockDecodePipeline)
            .GetMethod("DecodeAnalogAudioChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<DecodeChannel>(pipeline);
        Complex[] spectrum = BuildSpectrum();
        Complex[] stage1 = Enumerable.Range(0, filterLength)
            .Select(index => new Complex(0.9 + (index * 0.001), -0.2)).ToArray();
        var filter = new LaserDiscAnalogAudioChannelFilter(
            LowBin: 0, BinCount: 4, SliceSampleRateHz: 2_000_000.0,
            LowFrequencyHz: 25_000.0, CenterFrequencyHz: 1_000_000.0,
            Stage1Filter: stage1, Stage2Filter: []);
        if (filterLength < 4)
        {
            Assert.Throws<IndexOutOfRangeException>(() => decode(spectrum, filter));
            return;
        }

        Complex[] expectedSpectrum = DecodeFilterSetBuilder.SliceSpectrum(spectrum, 0, 4, spectrum.Length);
        for (int index = 0; index < expectedSpectrum.Length; index++) expectedSpectrum[index] *= stage1[index];
        double[] expected = PortedMath.UnwrapHilbert(PocketFftComplex.Inverse(expectedSpectrum), filter.SliceSampleRateHz);
        for (int index = 0; index < expected.Length; index++) expected[index] = (float)(expected[index] + filter.LowFrequencyHz);
        double[] actual = decode(spectrum, filter);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
    }

    [Fact(DisplayName = "LD EFM filtering retains scalar multiply bits and length validation")]
    public void EfmRetainsScalarMultiplyBitsAndLengthValidation()
    {
        using RfBlockDecodePipeline pipeline = CreatePipeline();
        FilterSignal filter = typeof(RfBlockDecodePipeline)
            .GetMethod("FilterLaserDiscSignal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<FilterSignal>(pipeline);
        Complex[] spectrum = BuildSpectrum();
        Complex[] coefficients = Enumerable.Range(0, spectrum.Length)
            .Select(index => new Complex(0.75, index * 0.01)).ToArray();
        var scalar = new Complex[spectrum.Length];
        for (int index = 0; index < scalar.Length; index++) scalar[index] = spectrum[index] * coefficients[index];
        Complex[] expected = PocketFftComplex.Inverse(scalar);
        Complex[] actual = filter(spectrum, coefficients);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
        ArgumentException error = Assert.Throws<ArgumentException>(() => filter(spectrum, new Complex[7]));
        Assert.Equal("filter", error.ParamName);
    }

    private static Complex[] BuildSpectrum()
        => Enumerable.Range(0, 8).Select(index => new Complex(1.0 + (index * 3.7), 0.5 - (index * 0.13))).ToArray();

    private static RfBlockDecodePipeline CreatePipeline()
    {
        Complex[] identity = Enumerable.Repeat(Complex.One, 8).ToArray();
        double[] magnitude = Enumerable.Repeat(1.0, 8).ToArray();
        var filters = new DecodeFilterSet(
            identity, identity, identity, identity, identity, identity, null,
            magnitude, magnitude, magnitude, magnitude, magnitude, magnitude, null);
        return new RfBlockDecodePipeline(new Int16SampleLoader(), filters, sampleRateHz: 40_000_000.0);
    }
}
