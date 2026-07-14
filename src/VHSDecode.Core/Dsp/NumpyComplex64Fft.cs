using System.Numerics;

namespace VHSDecode.Core.Dsp;

// Modified NumPy compatibility adaptation; see THIRD-PARTY-NOTICES.md.
internal readonly record struct Complex32(float Real, float Imaginary);

internal static class NumpyComplex64Fft
{
    internal static Complex32[] ForwardReal(ReadOnlySpan<float> input)
    {
        var doubleInput = new double[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            doubleInput[i] = input[i];
        }

        Complex[] doubleSpectrum = PocketFftComplex.ForwardReal(doubleInput);
        var output = new Complex32[doubleSpectrum.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = new Complex32(
                (float)doubleSpectrum[i].Real,
                (float)doubleSpectrum[i].Imaginary);
        }

        return output;
    }
}
