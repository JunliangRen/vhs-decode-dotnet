using System.Numerics;

namespace VHSDecode.Core.Dsp;

public static class FastFourierTransform
{
    public static Complex[] ForwardAnyLength(ReadOnlySpan<double> realInput)
    {
        var data = new Complex[realInput.Length];
        for (int i = 0; i < realInput.Length; i++)
        {
            data[i] = new Complex(realInput[i], 0.0);
        }

        return ForwardAnyLength(data);
    }

    public static Complex[] ForwardAnyLength(ReadOnlySpan<Complex> input)
    {
        if (input.IsEmpty)
        {
            return [];
        }

        if (IsPowerOfTwo(input.Length))
        {
            return Forward(input);
        }

        int convolutionLength = 1;
        int requiredLength = checked((input.Length * 2) - 1);
        while (convolutionLength < requiredLength)
        {
            convolutionLength = checked(convolutionLength * 2);
        }

        var a = new Complex[convolutionLength];
        var b = new Complex[convolutionLength];
        for (int i = 0; i < input.Length; i++)
        {
            double angle = ChirpAngle(i, input.Length);
            Complex forwardChirp = Complex.FromPolarCoordinates(1.0, -angle);
            Complex reverseChirp = Complex.FromPolarCoordinates(1.0, angle);
            a[i] = input[i] * forwardChirp;
            b[i] = reverseChirp;
            if (i != 0)
            {
                b[convolutionLength - i] = reverseChirp;
            }
        }

        TransformInPlace(a, inverse: false);
        TransformInPlace(b, inverse: false);
        for (int i = 0; i < convolutionLength; i++)
        {
            a[i] *= b[i];
        }

        TransformInPlace(a, inverse: true);
        var output = new Complex[input.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = a[i] * Complex.FromPolarCoordinates(1.0, -ChirpAngle(i, input.Length));
        }

        return output;
    }

    public static Complex[] Forward(ReadOnlySpan<double> realInput)
    {
        var data = new Complex[realInput.Length];
        for (int i = 0; i < realInput.Length; i++)
        {
            data[i] = new Complex(realInput[i], 0.0);
        }

        TransformInPlace(data, inverse: false);
        return data;
    }

    public static Complex[] Forward(ReadOnlySpan<Complex> input)
    {
        Complex[] data = input.ToArray();
        TransformInPlace(data, inverse: false);
        return data;
    }

    public static Complex[] Inverse(ReadOnlySpan<Complex> input)
    {
        Complex[] data = input.ToArray();
        TransformInPlace(data, inverse: true);
        return data;
    }

    public static void TransformInPlace(Span<Complex> data, bool inverse)
    {
        int n = data.Length;
        if (n == 0)
        {
            return;
        }

        if (!IsPowerOfTwo(n))
        {
            throw new ArgumentException("FFT length must be a power of two.", nameof(data));
        }

        BitReverseInPlace(data);

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = (inverse ? 2.0 : -2.0) * Math.PI / length;
            Complex wLength = Complex.FromPolarCoordinates(1.0, angle);
            int halfLength = length / 2;

            for (int i = 0; i < n; i += length)
            {
                Complex w = Complex.One;
                for (int j = 0; j < halfLength; j++)
                {
                    Complex u = data[i + j];
                    Complex v = data[i + j + halfLength] * w;
                    data[i + j] = u + v;
                    data[i + j + halfLength] = u - v;
                    w *= wLength;
                }
            }
        }

        if (inverse)
        {
            for (int i = 0; i < n; i++)
            {
                data[i] /= n;
            }
        }
    }

    private static void BitReverseInPlace(Span<Complex> data)
    {
        int j = 0;
        for (int i = 1; i < data.Length; i++)
        {
            int bit = data.Length >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static double ChirpAngle(int index, int length)
    {
        long period = checked(2L * length);
        long squareModuloPeriod = ((long)index * index) % period;
        return Math.PI * squareModuloPeriod / length;
    }
}
