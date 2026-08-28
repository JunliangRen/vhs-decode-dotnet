using System.Numerics;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.Dsp.Ipp;

[StructLayout(LayoutKind.Sequential, Pack = sizeof(float))]
public readonly struct IppComplex32(float real, float imaginary)
{
    public readonly float Real = real;
    public readonly float Imaginary = imaginary;
}

[StructLayout(LayoutKind.Sequential, Pack = sizeof(double))]
public readonly struct IppComplex64(double real, double imaginary)
{
    public readonly double Real = real;
    public readonly double Imaginary = imaginary;

    public IppComplex64(Complex value)
        : this(value.Real, value.Imaginary)
    {
    }

    public Complex ToComplex() => new(Real, Imaginary);
}

internal static class IppComplexLayout
{
    static IppComplexLayout()
    {
        if (Marshal.SizeOf<Complex32>() != Marshal.SizeOf<IppComplex32>())
        {
            throw new PlatformNotSupportedException(
                "Complex32 does not have the 8-byte ABI layout required by vhsdecode_ipp.");
        }

        Complex32 singleSample = new(1.25F, -2.5F);
        ReadOnlySpan<float> singleComponents = MemoryMarshal.Cast<Complex32, float>(
            MemoryMarshal.CreateReadOnlySpan(ref singleSample, 1));
        if (singleComponents.Length != 2
            || singleComponents[0] != singleSample.Real
            || singleComponents[1] != singleSample.Imaginary)
        {
            throw new PlatformNotSupportedException(
                "Complex32 does not have the two-float ABI ordering required by vhsdecode_ipp.");
        }

        if (Marshal.SizeOf<Complex>() != Marshal.SizeOf<IppComplex64>())
        {
            throw new PlatformNotSupportedException(
                "System.Numerics.Complex does not have the 16-byte ABI layout required by vhsdecode_ipp.");
        }

        Complex sample = new(1.25, -2.5);
        ReadOnlySpan<double> components = MemoryMarshal.Cast<Complex, double>(
            MemoryMarshal.CreateReadOnlySpan(ref sample, 1));
        if (components.Length != 2
            || components[0] != sample.Real
            || components[1] != sample.Imaginary)
        {
            throw new PlatformNotSupportedException(
                "System.Numerics.Complex does not have the two-double ABI ordering required by vhsdecode_ipp.");
        }
    }

    internal static void EnsureSupported()
    {
    }
}
