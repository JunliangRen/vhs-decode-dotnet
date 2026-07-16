using System.Numerics;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.Dsp;

// Modified NumPy compatibility adaptation; see THIRD-PARTY-NOTICES.md.
internal static class NumpyComplexMath
{
    public static Complex Pow(Complex value, double exponent)
    {
        if (exponent == 0.0)
        {
            return Complex.One;
        }

        if (exponent == 0.5)
        {
            return UfuncSqrt(value);
        }

        if (value.Real == 0.0 && value.Imaginary == 0.0)
        {
            return exponent > 0.0
                ? Complex.Zero
                : new Complex(double.NaN, double.NaN);
        }

        if (exponent > -100.0
            && exponent < 100.0
            && double.IsInteger(exponent))
        {
            return IntegerPow(value, (int)exponent);
        }

        var nativeValue = new NativeComplex(value.Real, value.Imaginary);
        var nativeExponent = new NativeComplex(exponent, 0.0);
        NativeComplex result;
        if (OperatingSystem.IsWindows())
        {
            result = NativeMethods.WindowsComplexPow(nativeValue, nativeExponent);
        }
        else if (OperatingSystem.IsLinux())
        {
            result = NativeMethods.LinuxComplexPow(nativeValue, nativeExponent);
        }
        else if (OperatingSystem.IsMacOS())
        {
            result = NativeMethods.MacOsComplexPow(nativeValue, nativeExponent);
        }
        else
        {
            return Complex.Pow(value, exponent);
        }

        return new Complex(result.Real, result.Imaginary);
    }

    public static Complex Sqrt(Complex value)
    {
        double real = value.Real;
        double imaginary = value.Imaginary;
        if (!double.IsFinite(real) || !double.IsFinite(imaginary))
        {
            return Complex.Sqrt(value);
        }

        if (real == 0.0 && imaginary == 0.0)
        {
            return new Complex(0.0, imaginary);
        }

        if (real >= 0.0)
        {
            double positive = Math.Sqrt((real + Complex.Abs(value)) * 0.5);
            return new Complex(positive, imaginary / (2.0 * positive));
        }

        double magnitude = Math.Sqrt((-real + Complex.Abs(value)) * 0.5);
        return new Complex(
            Math.Abs(imaginary) / (2.0 * magnitude),
            Math.CopySign(magnitude, imaginary));
    }

    private static Complex UfuncSqrt(Complex value)
    {
        double real = value.Real;
        double imaginary = value.Imaginary;
        if (!double.IsFinite(real) || !double.IsFinite(imaginary))
        {
            return Complex.Sqrt(value);
        }

        if (real == 0.0 && imaginary == 0.0)
        {
            return new Complex(0.0, imaginary);
        }

        double absolute = PlatformHypot(real, imaginary);
        if (real >= 0.0)
        {
            double positive = Math.Sqrt((real + absolute) * 0.5);
            return new Complex(positive, imaginary / (2.0 * positive));
        }

        double magnitude = Math.Sqrt((-real + absolute) * 0.5);
        return new Complex(
            Math.Abs(imaginary) / (2.0 * magnitude),
            Math.CopySign(magnitude, imaginary));
    }

    private static double PlatformHypot(double x, double y)
        => OperatingSystem.IsWindows()
            ? NativeMethods.WindowsHypot(x, y)
            : OperatingSystem.IsLinux()
                ? NativeMethods.LinuxHypot(x, y)
                : OperatingSystem.IsMacOS()
                    ? NativeMethods.MacOsHypot(x, y)
                    : double.Hypot(x, y);

    private static Complex IntegerPow(Complex value, int exponent)
    {
        if (exponent == 1)
        {
            return value;
        }

        if (exponent == 2)
        {
            return Multiply(value, value);
        }

        if (exponent == 3)
        {
            return Multiply(value, Multiply(value, value));
        }

        bool reciprocal = exponent < 0;
        int magnitude = Math.Abs(exponent);
        int mask = 1;
        Complex result = Complex.One;
        Complex power = value;
        while (true)
        {
            if ((magnitude & mask) != 0)
            {
                result = Multiply(result, power);
            }

            mask <<= 1;
            if (magnitude < mask || mask <= 0)
            {
                break;
            }

            power = Multiply(power, power);
        }

        return reciprocal ? Divide(Complex.One, result) : result;
    }

    private static Complex Multiply(Complex left, Complex right)
        => new(
            (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
            (left.Real * right.Imaginary) + (left.Imaginary * right.Real));

    private static Complex Divide(Complex left, Complex right)
    {
        double absoluteReal = Math.Abs(right.Real);
        double absoluteImaginary = Math.Abs(right.Imaginary);
        if (absoluteReal >= absoluteImaginary)
        {
            double ratio = right.Imaginary / right.Real;
            double scale = 1.0 / (right.Real + (right.Imaginary * ratio));
            return new Complex(
                (left.Real + (left.Imaginary * ratio)) * scale,
                (left.Imaginary - (left.Real * ratio)) * scale);
        }

        double alternateRatio = right.Real / right.Imaginary;
        double alternateScale = 1.0 / (right.Imaginary + (right.Real * alternateRatio));
        return new Complex(
            ((left.Real * alternateRatio) + left.Imaginary) * alternateScale,
            ((left.Imaginary * alternateRatio) - left.Real) * alternateScale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeComplex(double real, double imaginary)
    {
        public readonly double Real = real;

        public readonly double Imaginary = imaginary;
    }

    private static class NativeMethods
    {
        [DllImport("ucrtbase.dll", EntryPoint = "cpow", ExactSpelling = true)]
        internal static extern NativeComplex WindowsComplexPow(NativeComplex value, NativeComplex exponent);

        [DllImport("ucrtbase.dll", EntryPoint = "_hypot", ExactSpelling = true)]
        internal static extern double WindowsHypot(double x, double y);

        [DllImport("libm.so.6", EntryPoint = "cpow", ExactSpelling = true)]
        internal static extern NativeComplex LinuxComplexPow(NativeComplex value, NativeComplex exponent);

        [DllImport("libm.so.6", EntryPoint = "hypot", ExactSpelling = true)]
        internal static extern double LinuxHypot(double x, double y);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "cpow", ExactSpelling = true)]
        internal static extern NativeComplex MacOsComplexPow(NativeComplex value, NativeComplex exponent);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "hypot", ExactSpelling = true)]
        internal static extern double MacOsHypot(double x, double y);
    }
}
