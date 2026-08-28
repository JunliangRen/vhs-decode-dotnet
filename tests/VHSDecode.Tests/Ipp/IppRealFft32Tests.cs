using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppRealFft32Tests
{
    [Theory(DisplayName = "IPP real FFT32 rejects non-power-of-two lengths before probing native runtime")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(255)]
    [InlineData(32_769)]
    [InlineData((1 << 27) + 1)]
    public void RejectsUnsupportedLengthsBeforeNativeProbe(int length)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new IppRealFft32(length));
        Assert.Contains("power of two", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "IPP real FFT32 agrees numerically and round-trips finite data")]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(256)]
    [InlineData(4_096)]
    [InlineData(32_768)]
    public void AgreesNumericallyAndRoundTrips(int length)
    {
        if (!IppRuntime.TryProbe(out IppRuntimeInfo? runtimeInfo))
        {
            return;
        }

        Assert.NotNull(runtimeInfo);
        Assert.Equal(0x0001_0004U, runtimeInfo.AbiVersion);

        float[] input = BuildInput(length);
        Complex32[] expected = PocketFftReal32.Forward(input);
        var actual = new IppComplex32[expected.Length];
        var reconstructed = new float[length];

        using var fft = new IppRealFft32(length);
        Assert.Equal(length, fft.Length);
        Assert.Equal(expected.Length, fft.SpectrumLength);
        fft.Forward(input, actual);
        fft.Inverse(actual, reconstructed);

        AssertSpectrumClose(expected, actual);
        AssertRealClose(input, reconstructed);
    }

    [Fact(DisplayName = "IPP real FFT32 is deterministic across dirty reuse")]
    public void IsDeterministicAcrossDirtyReuse()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 32_768;
        float[] input = BuildInput(Length);
        var firstSpectrum = new IppComplex32[(Length / 2) + 1];
        var secondSpectrum = new IppComplex32[firstSpectrum.Length];
        var firstOutput = new float[Length];
        var secondOutput = new float[Length];

        using var fft = new IppRealFft32(Length);
        fft.Forward(input, firstSpectrum);
        fft.Inverse(firstSpectrum, firstOutput);
        Array.Fill(secondSpectrum, new IppComplex32(float.NaN, float.NegativeInfinity));
        Array.Fill(secondOutput, float.NaN);
        fft.Forward(input, secondSpectrum);
        fft.Inverse(secondSpectrum, secondOutput);

        Assert.Equal(firstSpectrum, secondSpectrum);
        Assert.Equal(firstOutput, secondOutput);
    }

    [Fact(DisplayName = "IPP real FFT32 serializes parallel calls on one context")]
    public void SerializesParallelCallsOnOneContext()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 4_096;
        float[] input = BuildInput(Length);
        Complex32[] expected = PocketFftReal32.Forward(input);

        using var fft = new IppRealFft32(Length);
        Parallel.For(
            0,
            12,
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            _ =>
            {
                var actual = new IppComplex32[fft.SpectrumLength];
                var reconstructed = new float[Length];
                fft.Forward(input, actual);
                fft.Inverse(actual, reconstructed);
                AssertSpectrumClose(expected, actual);
                AssertRealClose(input, reconstructed);
            });
    }

    [Fact(DisplayName = "Separate IPP real FFT32 contexts execute correctly in parallel")]
    public void SeparateContextsExecuteCorrectlyInParallel()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 4_096;
        const int ContextCount = 6;
        float[] input = BuildInput(Length);
        Complex32[] expected = PocketFftReal32.Forward(input);
        IppRealFft32[] contexts = Enumerable.Range(0, ContextCount)
            .Select(_ => new IppRealFft32(Length))
            .ToArray();
        try
        {
            Parallel.For(
                0,
                contexts.Length,
                new ParallelOptions { MaxDegreeOfParallelism = contexts.Length },
                index =>
                {
                    var actual = new IppComplex32[contexts[index].SpectrumLength];
                    var reconstructed = new float[Length];
                    contexts[index].Forward(input, actual);
                    contexts[index].Inverse(actual, reconstructed);
                    AssertSpectrumClose(expected, actual);
                    AssertRealClose(input, reconstructed);
                });
        }
        finally
        {
            foreach (IppRealFft32 context in contexts)
            {
                context.Dispose();
            }
        }
    }

    [Fact(DisplayName = "Disposed IPP real FFT32 rejects further calls")]
    public void DisposedObjectRejectsFurtherCalls()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        var fft = new IppRealFft32(8);
        var input = new float[fft.Length];
        var spectrum = new IppComplex32[fft.SpectrumLength];
        fft.Dispose();
        fft.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fft.Forward(input, spectrum));
        Assert.Throws<ObjectDisposedException>(() => fft.Inverse(spectrum, input));
    }

    private static float[] BuildInput(int length)
        => Enumerable.Range(0, length)
            .Select(index => (float)(
                Math.Sin(index * 0.017)
                + (0.25 * Math.Cos(index * 0.031))
                + (((index * 37) % 101) * 0.0002)))
            .ToArray();

    private static void AssertSpectrumClose(
        ReadOnlySpan<Complex32> expected,
        ReadOnlySpan<IppComplex32> actual)
    {
        const double AbsoluteTolerance = 1e-3;
        const double RelativeTolerance = 2.5e-4;

        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            double realTolerance = AbsoluteTolerance
                + (RelativeTolerance * Math.Abs(expected[index].Real));
            double imaginaryTolerance = AbsoluteTolerance
                + (RelativeTolerance * Math.Abs(expected[index].Imaginary));
            Assert.InRange(
                Math.Abs(expected[index].Real - actual[index].Real),
                0.0,
                realTolerance);
            Assert.InRange(
                Math.Abs(expected[index].Imaginary - actual[index].Imaginary),
                0.0,
                imaginaryTolerance);
        }
    }

    private static void AssertRealClose(
        ReadOnlySpan<float> expected,
        ReadOnlySpan<float> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            double tolerance = 2.5e-5 * Math.Max(1.0, Math.Abs(expected[index]));
            Assert.InRange(
                Math.Abs(expected[index] - actual[index]),
                0.0,
                tolerance);
        }
    }
}
