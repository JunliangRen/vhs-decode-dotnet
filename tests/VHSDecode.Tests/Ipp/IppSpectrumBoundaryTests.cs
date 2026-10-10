using System.Numerics;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppSpectrumBoundaryTests
{
    [Theory(DisplayName = "IPP FFT32 and FFT64 preserve analytical impulse, DC, and Nyquist spectra across ABI boundaries")]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(8, 0)]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    [InlineData(256, 0)]
    [InlineData(256, 1)]
    [InlineData(256, 2)]
    [InlineData(32_768, 0)]
    [InlineData(32_768, 1)]
    [InlineData(32_768, 2)]
    public void AnalyticalBoundarySpectraRoundTrip(int length, int signal)
    {
        if (!IppTestRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = Enumerable.Range(0, length).Select(index => signal switch
        {
            0 => index == 0 ? 1.0 : 0.0,
            1 => 1.0,
            _ => (index & 1) == 0 ? 1.0 : -1.0
        }).ToArray();
        float[] singleInput = input.Select(value => (float)value).ToArray();
        using var fft64 = new IppRealFft(length);
        using var fft32 = new IppRealFft32(length);
        var spectrum64 = new Complex[(length / 2) + 1];
        var spectrum32 = new IppComplex32[spectrum64.Length];
        fft64.Forward(input, spectrum64);
        fft32.Forward(singleInput, spectrum32);

        for (int bin = 0; bin < spectrum64.Length; bin++)
        {
            double expected = signal switch
            {
                0 => 1.0,
                1 => bin == 0 ? length : 0.0,
                _ => bin == length / 2 ? length : 0.0
            };
            Assert.InRange(Math.Abs(spectrum64[bin].Real - expected), 0.0, 1e-10);
            Assert.InRange(Math.Abs(spectrum64[bin].Imaginary), 0.0, 1e-10);
            Assert.InRange(Math.Abs(spectrum32[bin].Real - expected), 0.0, 1e-5);
            Assert.InRange(Math.Abs(spectrum32[bin].Imaginary), 0.0, 1e-5);
        }

        double[] output64 = Enumerable.Repeat(double.NaN, length).ToArray();
        float[] output32 = Enumerable.Repeat(float.NaN, length).ToArray();
        fft64.Inverse(spectrum64, output64);
        fft32.Inverse(spectrum32, output32);
        for (int index = 0; index < length; index++)
        {
            Assert.InRange(Math.Abs(output64[index] - input[index]), 0.0, 1e-11);
            Assert.InRange(Math.Abs(output32[index] - singleInput[index]), 0.0, 1e-5);
        }
    }
}
