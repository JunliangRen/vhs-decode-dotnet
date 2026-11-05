using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

internal static class LdCompactVideoExtractor
{
    internal static unsafe float[] Create(ReadOnlySpan<Complex> input)
    {
        float[] output = GC.AllocateUninitializedArray<float>(input.Length);
        int index = 0;
        if (Avx2.IsSupported)
        {
            fixed (Complex* inputPointer = input)
            fixed (float* outputPointer = output)
            {
                for (; index <= input.Length - 4; index += 4)
                {
                    double* source = (double*)(inputPointer + index);
                    Vector256<double> lower = Avx.LoadVector256(source);
                    Vector256<double> upper = Avx.LoadVector256(source + 4);
                    // Match the original ComplexRealExtractor gather, then the
                    // ordinary LD output's existing FP64-to-FP32 instruction.
                    Vector256<double> real = Avx2.Permute4x64(Avx.UnpackLow(lower, upper), 0xD8);
                    Sse.Store(outputPointer + index, Avx.ConvertToVector128Single(real));
                }
            }
        }

        for (; index < input.Length; index++)
        {
            output[index] = (float)input[index].Real;
        }

        return output;
    }
}
