using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

internal static class IppComplexInputStager
{
    internal static Complex[] Create(ReadOnlySpan<double> input)
    {
        // The fresh array cannot alias input. Every real and imaginary lane is
        // written before IPP sees it, so clearing this allocation first is redundant.
        Complex[] output = GC.AllocateUninitializedArray<Complex>(input.Length);
        Fill(input, output);
        return output;
    }

    private static unsafe void Fill(ReadOnlySpan<double> input, Span<Complex> output)
    {
        int index = 0;
        if (Avx.IsSupported)
        {
            Span<double> outputComponents = MemoryMarshal.Cast<Complex, double>(output);
            fixed (double* inputPointer = input)
            fixed (double* outputPointer = outputComponents)
            {
                Vector256<double> zero = Vector256<double>.Zero;
                for (; index <= input.Length - 4; index += 4)
                {
                    Vector256<double> values = Avx.LoadVector256(inputPointer + index);
                    Vector256<double> low = Avx.UnpackLow(values, zero);
                    Vector256<double> high = Avx.UnpackHigh(values, zero);
                    int outputIndex = 2 * index;
                    Avx.Store(outputPointer + outputIndex, Avx.Permute2x128(low, high, 0x20));
                    Avx.Store(outputPointer + outputIndex + 4, Avx.Permute2x128(low, high, 0x31));
                }
            }
        }

        for (; index < input.Length; index++)
        {
            output[index] = new Complex(input[index], 0.0);
        }
    }
}
