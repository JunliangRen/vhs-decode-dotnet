using System.Numerics;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppComplexFft64Tests
{
    [Theory(DisplayName = "IPP complex FFT rejects non-power-of-two lengths before native probing")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(255)]
    [InlineData(32_769)]
    public void RejectsUnsupportedLengthsBeforeNativeProbe(int length)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new IppComplexFft64(length));
        Assert.Contains("power of two", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "IPP complex FFT agrees numerically and round-trips")]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(256)]
    [InlineData(4_096)]
    [InlineData(32_768)]
    public void AgreesNumericallyAndRoundTrips(int length)
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        Complex[] input = BuildInput(length);
        Complex[] expected = PocketFftComplex.ForwardDucc(input);
        var actual = new Complex[length];
        var reconstructed = new Complex[length];

        using var fft = new IppComplexFft64(length);
        fft.Forward(input, actual);
        fft.Inverse(actual, reconstructed);

        AssertComplexClose(expected, actual, 2.5e-11);
        AssertComplexClose(input, reconstructed, 2.5e-12);
    }

    [Fact(DisplayName = "IPP complex FFT supports deterministic in-place reuse")]
    public void SupportsDeterministicInPlaceReuse()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 4_096;
        Complex[] input = BuildInput(Length);
        Complex[] first = input.ToArray();
        Complex[] second = input.ToArray();

        using var fft = new IppComplexFft64(Length);
        fft.Forward(first, first);
        fft.Inverse(first, first);
        fft.Forward(second, second);
        fft.Inverse(second, second);

        Assert.Equal(first, second);
        AssertComplexClose(input, first, 2.5e-12);
    }

    [Fact(DisplayName = "IPP complex FFT rejects partial input-output overlap")]
    public void RejectsPartialInputOutputOverlap()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 256;
        Complex[] values = BuildInput(Length + 1);
        using var fft = new IppComplexFft64(Length);

        Assert.Throws<ArgumentException>(() =>
            fft.Forward(values.AsSpan(0, Length), values.AsSpan(1, Length)));
        Assert.Throws<ArgumentException>(() =>
            fft.Inverse(values.AsSpan(1, Length), values.AsSpan(0, Length)));
    }

    [Fact(DisplayName = "IPP complex FFT pool is bounded across lengths and parallel callers")]
    public void PoolIsBoundedAcrossLengthsAndParallelCallers()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        var pool = new IppComplexFft64Pool();
        Parallel.For(0, 64, iteration =>
        {
            int length = (iteration & 1) == 0 ? 256 : 512;
            Complex[] input = BuildInput(length);
            var spectrum = new Complex[length];
            var output = new Complex[length];
            pool.Forward(input, spectrum);
            pool.Inverse(spectrum, output);
            AssertComplexClose(input, output, 2.5e-12);
        });

        Assert.InRange(pool.RetainedContextCount, 1, 32);
        pool.Dispose();
        Assert.Equal(0, pool.RetainedContextCount);
        Assert.Throws<ObjectDisposedException>(() =>
            pool.Forward(BuildInput(256), new Complex[256]));
    }

    private static Complex[] BuildInput(int length)
        => Enumerable.Range(0, length)
            .Select(index => new Complex(
                Math.Sin(index * 0.071) + (0.25 * Math.Cos(index * 0.013)),
                Math.Cos(index * 0.037) - (0.125 * Math.Sin(index * 0.019))))
            .ToArray();

    private static void AssertComplexClose(
        ReadOnlySpan<Complex> expected,
        ReadOnlySpan<Complex> actual,
        double relativeTolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            double scale = Math.Max(1.0, expected[i].Magnitude);
            Assert.InRange(
                Complex.Abs(expected[i] - actual[i]),
                0.0,
                relativeTolerance * scale);
        }
    }
}
