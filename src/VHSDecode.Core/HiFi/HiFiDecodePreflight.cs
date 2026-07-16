using System.Numerics;

namespace VHSDecode.Core.HiFi;

internal sealed record HiFiDecodePreflightResult(
    BigInteger InitialInputSamples,
    BigInteger InitialFinalAudioSamples,
    BigInteger ReadOverlapSamples,
    BigInteger InputOverlapSamples,
    BigInteger AudioOverlapSamples,
    BigInteger FinalAudioOverlapSamples,
    bool HasRateSyncWarning,
    BigInteger DecoderSharedMemoryBytes);

internal static class HiFiDecodePreflight
{
    private const int SharedMemoryAlignment = 64;
    private const int Float32Size = sizeof(float);

    public static HiFiDecodePreflightResult Build(HiFiDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int inputRateHz = PythonIntRate(options.InputRateHz, nameof(options.InputRateHz));
        BigInteger finalAudioRate = options.AudioRateInteger;
        double finalRateRatio = PythonTrueDivide(
            finalAudioRate,
            HiFiConstants.IntermediateAudioRate);
        if (finalRateRatio <= 0.0)
        {
            throw new ArgumentException("Sample rate should be over 0");
        }

        double finalAudioRateFloat = PythonIntegerToDouble(finalAudioRate);
        double blockRatio = 1.0 / HiFiConstants.BlocksPerSecond;
        BigInteger initialInputSamples = PythonCeilingToInteger(inputRateHz * blockRatio);
        BigInteger initialFinalAudioSamples = PythonCeilingToInteger(
            finalAudioRateFloat * blockRatio);
        BigInteger blockSizeGcd = BigInteger.GreatestCommonDivisor(
            initialInputSamples,
            initialFinalAudioSamples);
        bool hasRateSyncWarning = blockSizeGcd <= 5;
        int audioOverlapDivisor = hasRateSyncWarning
            ? 1
            : checked((int)Math.Truncate(PythonTrueDivide(
                HiFiConstants.IntermediateAudioRate / HiFiConstants.BlocksPerSecond,
                blockSizeGcd)));
        if (audioOverlapDivisor == 0)
        {
            throw new DivideByZeroException("division by zero");
        }

        int minimumResamplerOverlap = HiFiConstants.BlockPreTrimSamples
            + HiFiConstants.MinimumResamplerOverlapPadding;
        double minimumOverlapRatio = (double)minimumResamplerOverlap
            / HiFiConstants.IntermediateAudioRate;
        BigInteger minimumFinalOverlap = PythonCeilingToInteger(
            minimumOverlapRatio * finalAudioRateFloat);
        BigInteger finalAudioOverlap = PythonCeilingToInteger(PythonTrueDivide(
                minimumFinalOverlap,
                audioOverlapDivisor))
            * audioOverlapDivisor;
        double overlapSeconds = PythonTrueDivide(finalAudioOverlap, finalAudioRate);
        BigInteger inputOverlap = PythonRoundToInteger(inputRateHz * overlapSeconds);
        BigInteger audioOverlap = PythonCeilingToInteger(inputRateHz * overlapSeconds);
        BigInteger readOverlap = inputOverlap * 2;
        finalAudioOverlap = PythonRoundToInteger(finalAudioRateFloat * overlapSeconds);

        BigInteger maxAudioBytes = (
                initialFinalAudioSamples + Align(initialFinalAudioSamples))
            * Float32Size;
        BigInteger inputBytes = (initialInputSamples + (inputOverlap * 2)) * Float32Size;
        BigInteger sharedMemoryBytes = BigInteger.Max(maxAudioBytes, inputBytes);

        return new HiFiDecodePreflightResult(
            initialInputSamples,
            initialFinalAudioSamples,
            readOverlap,
            inputOverlap,
            audioOverlap,
            finalAudioOverlap,
            hasRateSyncWarning,
            sharedMemoryBytes);
    }

    public static void ValidateSharedMemorySize(HiFiDecodePreflightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.DecoderSharedMemoryBytes > new BigInteger(nint.MaxValue))
        {
            throw new OverflowException("Python int too large to convert to C ssize_t");
        }
    }

    public static string FormatRateSyncWarning(HiFiDecodeOptions options)
        => $"WARNING: The input sample rate is not evenly divisible by the output sample rate. "
            + $"Audio sync issues may occur. Input Rate: {PythonIntRate(options.InputRateHz, nameof(options.InputRateHz))}, "
            + $"Output Rate: {options.AudioRateInteger}.";

    private static BigInteger Align(BigInteger value)
    {
        BigInteger remainder = value % SharedMemoryAlignment;
        return remainder.IsZero
            ? value
            : value + (SharedMemoryAlignment - remainder);
    }

    private static int PythonIntRate(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0.0 || value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return checked((int)Math.Truncate(value));
    }

    private static double PythonIntegerToDouble(BigInteger value)
    {
        try
        {
            return PythonTrueDivide(value, BigInteger.One);
        }
        catch (OverflowException)
        {
            throw new OverflowException("int too large to convert to float");
        }
    }

    private static double PythonTrueDivide(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException("division by zero");
        }

        if (numerator.IsZero)
        {
            return 0.0;
        }

        bool negative = numerator.Sign != denominator.Sign;
        BigInteger absoluteNumerator = BigInteger.Abs(numerator);
        BigInteger absoluteDenominator = BigInteger.Abs(denominator);
        long exponent = absoluteNumerator.GetBitLength() - absoluteDenominator.GetBitLength();
        if (RatioIsLessThanPowerOfTwo(absoluteNumerator, absoluteDenominator, exponent))
        {
            exponent--;
        }

        ulong bits;
        if (exponent >= -1022)
        {
            if (exponent > 1023)
            {
                throw new OverflowException("integer division result too large for a float");
            }

            long shift = 52 - exponent;
            BigInteger scaledNumerator = absoluteNumerator;
            BigInteger scaledDenominator = absoluteDenominator;
            if (shift >= 0)
            {
                scaledNumerator <<= CheckedShift(shift);
            }
            else
            {
                scaledDenominator <<= CheckedShift(-shift);
            }

            BigInteger significand = RoundedQuotient(scaledNumerator, scaledDenominator);
            BigInteger significandLimit = BigInteger.One << 53;
            if (significand >= significandLimit)
            {
                significand >>= 1;
                exponent++;
                if (exponent > 1023)
                {
                    throw new OverflowException("integer division result too large for a float");
                }
            }

            ulong fraction = checked((ulong)(significand - (BigInteger.One << 52)));
            bits = (checked((ulong)(exponent + 1023)) << 52) | fraction;
        }
        else
        {
            BigInteger subnormal = RoundedQuotient(
                absoluteNumerator << 1074,
                absoluteDenominator);
            if (subnormal >= (BigInteger.One << 52))
            {
                bits = 1UL << 52;
            }
            else
            {
                bits = checked((ulong)subnormal);
            }
        }

        if (negative)
        {
            bits |= 1UL << 63;
        }

        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    private static bool RatioIsLessThanPowerOfTwo(
        BigInteger numerator,
        BigInteger denominator,
        long exponent)
        => exponent >= 0
            ? numerator < (denominator << CheckedShift(exponent))
            : (numerator << CheckedShift(-exponent)) < denominator;

    private static int CheckedShift(long shift)
        => checked((int)shift);

    private static BigInteger RoundedQuotient(BigInteger numerator, BigInteger denominator)
    {
        BigInteger quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        int comparison = (remainder << 1).CompareTo(denominator);
        if (comparison > 0 || (comparison == 0 && !quotient.IsEven))
        {
            quotient++;
        }

        return quotient;
    }

    private static BigInteger PythonCeilingToInteger(double value)
    {
        if (double.IsPositiveInfinity(value) || double.IsNegativeInfinity(value))
        {
            throw new OverflowException("cannot convert float infinity to integer");
        }

        if (double.IsNaN(value))
        {
            throw new ArgumentException("cannot convert float NaN to integer");
        }

        return new BigInteger(Math.Ceiling(value));
    }

    private static BigInteger PythonRoundToInteger(double value)
    {
        if (double.IsPositiveInfinity(value) || double.IsNegativeInfinity(value))
        {
            throw new OverflowException("cannot convert float infinity to integer");
        }

        if (double.IsNaN(value))
        {
            throw new ArgumentException("cannot convert float NaN to integer");
        }

        return new BigInteger(Math.Round(value, MidpointRounding.ToEven));
    }
}
