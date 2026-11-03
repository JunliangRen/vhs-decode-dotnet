using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscEfmQuantizationTests
{
    private delegate short[] DecodeEfm(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<Complex> filter);

    [Theory(DisplayName = "LD EFM direct conversion preserves real-only clamp and cast boundaries")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(257)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    public void DirectConversionPreservesRealOnlyClampAndCastBoundaries(int length)
    {
        double[] values =
        [
            0.0, -0.0, -0.75, 0.75,
            short.MinValue - 1.0, short.MinValue - 0.5, short.MinValue,
            short.MinValue + 0.5, short.MaxValue - 0.5, short.MaxValue,
            short.MaxValue + 0.5, short.MaxValue + 1.0,
            Math.BitDecrement(short.MinValue), Math.BitIncrement(short.MinValue),
            Math.BitDecrement(short.MinValue + 1.0), Math.BitIncrement(short.MinValue + 1.0),
            Math.BitDecrement(short.MaxValue - 1.0), Math.BitIncrement(short.MaxValue - 1.0),
            Math.BitDecrement(short.MaxValue), Math.BitIncrement(short.MaxValue),
            Math.BitDecrement(0.0), Math.BitIncrement(0.0),
            double.MinValue, double.MaxValue, double.NegativeInfinity, double.PositiveInfinity,
            BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0123),
            BitConverter.UInt64BitsToDouble(0xFFF8_0000_0000_0456)
        ];
        var guarded = new Complex[length + 4];
        for (int i = 0; i < guarded.Length; i++)
        {
            guarded[i] = new Complex(values[i % values.Length], values[(i + 7) % values.Length]);
        }
        byte[] original = MemoryMarshal.AsBytes(guarded.AsSpan()).ToArray();
        ReadOnlySpan<Complex> input = guarded.AsSpan(2, length);
        short[] expected = ConvertWithRealIntermediate(input);
        short[] actual = RfBlockDecodePipeline.ConvertEfmSamplesToInt16(input);
        Complex[] alteredImaginary = input.ToArray();
        for (int i = 0; i < alteredImaginary.Length; i++)
        {
            alteredImaginary[i] = new Complex(alteredImaginary[i].Real, values[(i + 13) % values.Length]);
        }

        Assert.Equal(length, actual.Length);
        Assert.Equal(expected, actual);
        Assert.Equal(expected, RfBlockDecodePipeline.ConvertEfmSamplesToInt16(alteredImaginary));
        Assert.True(original.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(guarded.AsSpan())));
    }

    [Fact(DisplayName = "LD EFM direct conversion avoids one full double block allocation")]
    public void DirectConversionAvoidsOneFullDoubleBlockAllocation()
    {
        const int length = 32_768;
        Complex[] input = Enumerable.Range(0, length)
            .Select(i => new Complex(((i * 7_919L) % 200_003) - 100_001.25, -i))
            .ToArray();
        _ = ConvertWithRealIntermediate(input);
        _ = RfBlockDecodePipeline.ConvertEfmSamplesToInt16(input);

        long before = GC.GetAllocatedBytesForCurrentThread();
        short[] expected = ConvertWithRealIntermediate(input);
        long referenceAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        short[] actual = RfBlockDecodePipeline.ConvertEfmSamplesToInt16(input);
        long actualAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, actual);
        Assert.True(referenceAllocated - actualAllocated >= length * sizeof(double),
            $"Direct EFM conversion saved {referenceAllocated - actualAllocated:N0} bytes.");
        Assert.InRange(actualAllocated, length * sizeof(short), (length * sizeof(short)) + 128);
    }

    [Theory(DisplayName = "LD EFM filtering retains the original spectrum multiply and inverse bits")]
    [InlineData(DspBackend.Exact, 1_024)]
    [InlineData(DspBackend.Exact, 32_768)]
    [InlineData(DspBackend.IppFast, 1_024)]
    [InlineData(DspBackend.IppFast, 32_768)]
    public void EfmFilteringRetainsOriginalSpectrumMultiplyAndInverseBits(DspBackend backend, int length)
    {
        if (backend == DspBackend.IppFast)
        {
            Assert.SkipUnless(IppTestRuntime.TryProbe(out _), "Intel IPP runtime is unavailable.");
        }

        using RfBlockDecodePipeline pipeline = CreatePipeline(backend);
        DecodeEfm decode = typeof(RfBlockDecodePipeline).GetMethod(
                "DecodeEfmBlock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<DecodeEfm>(pipeline);
        using var demodulator = new RfDemodulator(40_000_000.0, backend);
        var spectrum = new Complex[length];
        var filter = new Complex[length];
        for (int i = 0; i < length; i++)
        {
            spectrum[i] = new Complex(((i * 7_919L) % 200_003) - 100_001.25, (i % 71) - 35.125);
            filter[i] = new Complex(1.0 / (1.0 + i), (i % 31 - 15) / 127.0);
        }
        spectrum[0] = new Complex(0.0, -0.0);
        Check();
        spectrum[3] = new Complex(double.PositiveInfinity, double.NaN);
        spectrum[7] = new Complex(double.NegativeInfinity, -0.0);
        Check();

        ArgumentException error = Assert.Throws<ArgumentException>(() => decode(spectrum, filter.AsSpan(1)));
        Assert.Equal("filter", error.ParamName);

        void Check()
        {
            byte[] originalSpectrum = MemoryMarshal.AsBytes(spectrum.AsSpan()).ToArray();
            byte[] originalFilter = MemoryMarshal.AsBytes(filter.AsSpan()).ToArray();
            var filtered = new Complex[length];
            for (int i = 0; i < length; i++)
            {
                filtered[i] = spectrum[i] * filter[i];
            }
            demodulator.InverseLaserDiscFullSpectrumInPlace(filtered, useDuccExact: false);
            short[] expected = ConvertWithRealIntermediate(filtered);
            short[] actual = decode(spectrum, filter);

            Assert.Equal(expected, actual);
            Assert.True(originalSpectrum.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(spectrum.AsSpan())));
            Assert.True(originalFilter.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(filter.AsSpan())));
        }
    }

    private static RfBlockDecodePipeline CreatePipeline(DspBackend backend)
    {
        var filters = new DecodeFilterSet([], [], [], [], [], [], null, [], [], [], [], [], [], null);
        return new RfBlockDecodePipeline(new Int16SampleLoader(), filters, 40_000_000.0, dspBackend: backend);
    }

    private static short[] ConvertWithRealIntermediate(ReadOnlySpan<Complex> input)
    {
        double[] real = ExtractReal(input);
        var output = new short[real.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = (short)Math.Clamp(real[i], short.MinValue, short.MaxValue);
        }
        return output;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double[] ExtractReal(ReadOnlySpan<Complex> input)
    {
        var real = new double[input.Length];
        for (int i = 0; i < real.Length; i++)
        {
            real[i] = input[i].Real;
        }
        return real;
    }
}
