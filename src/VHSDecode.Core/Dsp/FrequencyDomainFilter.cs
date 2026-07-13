using System.Numerics;

namespace VHSDecode.Core.Dsp;

public static class FrequencyDomainFilter
{
    public static double[] LowPassSuperGaussianHalf(
        double cornerFrequency,
        int order,
        double nyquistHz,
        int blockLength)
    {
        ValidateEvenBlockLength(blockLength);
        int count = (blockLength / 2) + 1;
        var output = new double[count];
        for (int i = 0; i < output.Length; i++)
        {
            double frequency = nyquistHz * i / (count - 1);
            output[i] = PortedMath.SuperGaussian(frequency, cornerFrequency, order);
        }

        return output;
    }

    public static double[] BandPassSuperGaussianHalf(
        double lowFrequency,
        double highFrequency,
        int order,
        double nyquistHz,
        int blockLength)
    {
        ValidateEvenBlockLength(blockLength);
        int count = (blockLength / 2) + 1;
        var output = new double[count];
        double width = highFrequency - lowFrequency;
        double center = (highFrequency + lowFrequency) / 2.0;
        for (int i = 0; i < output.Length; i++)
        {
            double frequency = nyquistHz * i / (count - 1);
            output[i] = PortedMath.SuperGaussian(frequency, width, order, center);
        }

        return output;
    }

    public static double[] MirrorHalfToFull(ReadOnlySpan<double> halfSpectrum)
    {
        if (halfSpectrum.Length < 2)
        {
            throw new ArgumentException("Half spectrum must contain DC and Nyquist bins.", nameof(halfSpectrum));
        }

        int blockLength = (halfSpectrum.Length - 1) * 2;
        var output = new double[blockLength];
        halfSpectrum.CopyTo(output);
        for (int i = 1; i < halfSpectrum.Length - 1; i++)
        {
            output[blockLength - i] = halfSpectrum[i];
        }

        return output;
    }

    public static double[] RampFilter(
        double startFrequencyHz,
        double boostStart,
        double boostMax,
        double nyquistHz,
        int blockLength)
    {
        ValidateEvenBlockLength(blockLength);
        double maxFrequencyHz = 20e6;
        int half = blockLength / 2;
        int zeroCount = (int)((startFrequencyHz / nyquistHz) * half);
        zeroCount = Math.Clamp(zeroCount, 0, half);

        var halfSpectrum = new double[half];
        int rampCount = half - zeroCount;
        for (int i = 0; i < rampCount; i++)
        {
            double t = rampCount == 1 ? 0.0 : (double)i / (rampCount - 1);
            double end = boostMax * (nyquistHz / maxFrequencyHz);
            halfSpectrum[zeroCount + i] = boostStart + ((end - boostStart) * t);
        }

        var output = new double[blockLength];
        for (int i = 0; i < half; i++)
        {
            output[i] = halfSpectrum[i];
            output[blockLength - 1 - i] = halfSpectrum[i];
        }

        return output;
    }

    public static Complex[] Apply(ReadOnlySpan<Complex> spectrum, ReadOnlySpan<double> response)
    {
        if (spectrum.Length != response.Length)
        {
            throw new ArgumentException("Response length must match spectrum length.", nameof(response));
        }

        var output = new Complex[spectrum.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = spectrum[i] * response[i];
        }

        return output;
    }

    public static double[] Roll(ReadOnlySpan<double> input, int shift)
    {
        if (input.IsEmpty)
        {
            return [];
        }

        int length = input.Length;
        int normalized = ((shift % length) + length) % length;
        var output = new double[length];
        for (int i = 0; i < length; i++)
        {
            output[(i + normalized) % length] = input[i];
        }

        return output;
    }

    private static void ValidateEvenBlockLength(int blockLength)
    {
        if (blockLength <= 0 || blockLength % 2 != 0)
        {
            throw new ArgumentException("Block length must be a positive even value.", nameof(blockLength));
        }
    }
}
