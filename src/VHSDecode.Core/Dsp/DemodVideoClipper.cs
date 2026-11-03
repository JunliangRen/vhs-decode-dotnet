using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

internal static class DemodVideoClipper
{
    internal static double[] Create(ReadOnlySpan<double> demod, double sampleRateHz)
    {
        double upper = sampleRateHz * 0.75;
        // The output owns its storage, and every slot is written before return.
        double[] output = GC.AllocateUninitializedArray<double>(demod.Length);
        Fill(demod, output, upper);
        return output;
    }

    private static unsafe void Fill(ReadOnlySpan<double> demod, Span<double> output, double upper)
    {
        int index = 0;
        // Math.Clamp checks inverted bounds within each iteration. Keep that path
        // scalar so empty input still succeeds; a NaN upper bound remains allowed.
        if (Avx.IsSupported && !(1_500_000.0 > upper))
        {
            Vector256<double> minimum = Vector256.Create(1_500_000.0);
            Vector256<double> maximum = Vector256.Create(upper);
            fixed (double* source = demod)
            fixed (double* destination = output)
            {
                for (; index <= demod.Length - 4; index += 4)
                {
                    Vector256<double> value = Avx.LoadVector256(source + index);
                    Vector256<double> below = Avx.Compare(value, minimum,
                        FloatComparisonMode.OrderedLessThanNonSignaling);
                    Vector256<double> above = Avx.Compare(value, maximum,
                        FloatComparisonMode.OrderedGreaterThanNonSignaling);
                    // Ordered masks and bit blends retain the input NaN payload.
                    Vector256<double> result = Avx.BlendVariable(value, minimum, below);
                    result = Avx.BlendVariable(result, maximum, above);
                    Avx.Store(destination + index, result);
                }
            }
        }

        for (; index < demod.Length; index++)
        {
            output[index] = Math.Clamp(demod[index], 1_500_000.0, upper);
        }
    }
}
