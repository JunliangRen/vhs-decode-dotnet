using System.Numerics;

namespace VHSDecode.Core.Dsp;

public static class AnalyticSignal
{
    public static Complex[] FromReal(ReadOnlySpan<double> input)
    {
        if (input.IsEmpty)
        {
            return [];
        }

        Complex[] spectrum = FastFourierTransform.Forward(input);
        double[] hilbert = PortedMath.BuildHilbertMultiplier(spectrum.Length);
        for (int i = 0; i < spectrum.Length; i++)
        {
            spectrum[i] *= hilbert[i];
        }

        return FastFourierTransform.Inverse(spectrum);
    }

    public static double[] FmDemodulate(ReadOnlySpan<double> input, double sampleRateHz)
    {
        Complex[] analytic = FromReal(input);
        return PortedMath.UnwrapHilbert(analytic, sampleRateHz);
    }

    public static double[] FmDemodulateAnalytic(ReadOnlySpan<Complex> analyticInput, double sampleRateHz)
    {
        return PortedMath.UnwrapHilbert(analyticInput, sampleRateHz);
    }
}
