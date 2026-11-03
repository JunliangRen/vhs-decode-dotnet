using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

internal static class ComplexRealExtractor
{
    internal static double[] Create(ReadOnlySpan<Complex> input)
    {
        // This output is independent of input, and every raw f64 slot is written
        // before return. Existing caller-owned/pool destinations use their old path.
        double[] output = GC.AllocateUninitializedArray<double>(input.Length);
        Fill(input, output);
        return output;
    }

    private static unsafe void Fill(ReadOnlySpan<Complex> input, Span<double> output)
    {
        int index = 0;
        if (Avx2.IsSupported)
        {
            fixed (Complex* inputPointer = input)
            fixed (double* outputPointer = output)
            {
                for (; index <= input.Length - 4; index += 4)
                {
                    double* source = (double*)(inputPointer + index);
                    Vector256<double> lower = Avx.LoadVector256(source);
                    Vector256<double> upper = Avx.LoadVector256(source + 4);
                    // Interleaved pairs -> r0,r2,r1,r3 -> r0,r1,r2,r3.
                    // No arithmetic or quantization: retain sNaN payloads and ±0.
                    Vector256<double> real = Avx2.Permute4x64(Avx.UnpackLow(lower, upper), 0xD8);
                    Avx.Store(outputPointer + index, real);
                }
            }
        }

        for (; index < input.Length; index++)
        {
            output[index] = input[index].Real;
        }
    }
}
