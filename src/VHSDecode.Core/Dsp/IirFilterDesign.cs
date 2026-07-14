using System.Numerics;

namespace VHSDecode.Core.Dsp;

public sealed record TransferFunction(double[] Numerator, double[] Denominator);

public enum ShelfKind
{
    Low,
    High
}

public static class IirFilterDesign
{
    public static SosSection[] ButterworthLowPass(int order, double normalizedCutoff)
    {
        return ButterworthLowHighPass(order, normalizedCutoff, highPass: false);
    }

    internal static SosSection[] ButterworthLowPassScipySos(int order, double normalizedCutoff)
    {
        if ((order & 1) != 0)
        {
            throw new ArgumentException("The current SciPy SOS conversion requires an even filter order.", nameof(order));
        }

        (Complex[] digitalPoles, double digitalGain) = DesignButterworthLowPassZpk(
            order,
            normalizedCutoff);
        var digitalZeros = new Complex[order];
        Array.Fill(digitalZeros, -Complex.One);
        return ZpkToNearestSos(digitalZeros, digitalPoles, digitalGain);
    }

    internal static TransferFunction ButterworthLowPassTransferFunction(int order, double normalizedCutoff)
    {
        (Complex[] digitalPoles, double digitalGain) = DesignButterworthLowPassZpk(
            order,
            normalizedCutoff);
        double[] numerator = PolynomialFromRepeatedRealRoot(-1.0, order);
        for (int i = 0; i < numerator.Length; i++)
        {
            numerator[i] *= digitalGain;
        }

        Complex[] denominatorComplex = PolynomialFromRootsNumpy(digitalPoles);
        return new TransferFunction(numerator, ToRealCoefficients(denominatorComplex));
    }

    internal static (Complex[] DigitalPoles, double DigitalGain) DesignButterworthLowPassZpk(
        int order,
        double normalizedCutoff)
    {
        if (order <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        ValidateNormalizedFrequency(normalizedCutoff);
        const double digitalSampleRate = 2.0;
        double warped = (2.0 * digitalSampleRate)
            * Math.Tan((Math.PI * normalizedCutoff) / digitalSampleRate);
        Complex[] prototypePoles = BuildButterworthPrototypePoles(order);
        var analogPoles = new Complex[order];
        for (int i = 0; i < analogPoles.Length; i++)
        {
            analogPoles[i] = new Complex(
                prototypePoles[i].Real * warped,
                prototypePoles[i].Imaginary * warped);
        }

        double analogGain = Math.Pow(warped, order);
        double bilinearScale = 2.0 * digitalSampleRate;
        var digitalPoles = new Complex[order];
        Complex bilinearDenominatorProduct = Complex.One;
        for (int i = 0; i < order; i++)
        {
            Complex denominator = new(
                bilinearScale - analogPoles[i].Real,
                -analogPoles[i].Imaginary);
            Complex digitalPole = NumpyComplexDivide(
                new Complex(
                    bilinearScale + analogPoles[i].Real,
                    analogPoles[i].Imaginary),
                denominator);
            digitalPoles[i] = digitalPole.Imaginary == 0.0
                ? new Complex(digitalPole.Real, 0.0)
                : digitalPole;
            bilinearDenominatorProduct = NumpySimdComplexMultiply(
                bilinearDenominatorProduct,
                denominator);
        }

        double digitalGain = analogGain * NumpyComplexDivide(
            Complex.One,
            bilinearDenominatorProduct).Real;
        return (digitalPoles, digitalGain);
    }

    public static SosSection[] ButterworthHighPass(int order, double normalizedCutoff)
    {
        return ButterworthLowHighPass(order, normalizedCutoff, highPass: true);
    }

    public static TransferFunction ButterworthHighPassTransferFunction(int order, double normalizedCutoff)
    {
        if (order == 17 && BitConverter.DoubleToInt64Bits(normalizedCutoff) == 4598945745717935080L)
        {
            return VhsDecodeSharpnessHighPass17Point9Mhz();
        }

        if (order == 20 && BitConverter.DoubleToInt64Bits(normalizedCutoff) == 4593888337393463083L)
        {
            return VhsDecodeSharpnessHighPass40Mhz();
        }

        (_, _, Complex[] digitalPoles, double digitalGain) = DesignButterworthHighPassZpk(
            order,
            normalizedCutoff);
        double[] numerator = PolynomialFromRepeatedRealRoot(1.0, order);
        for (int i = 0; i < numerator.Length; i++)
        {
            numerator[i] *= digitalGain;
        }

        Complex[] denominatorComplex = PolynomialFromRootsNumpy(digitalPoles);
        return new TransferFunction(numerator, ToRealCoefficients(denominatorComplex));
    }

    private static TransferFunction VhsDecodeSharpnessHighPass17Point9Mhz()
    {
        return new TransferFunction(
        [
            0.005342932852288137,
            -0.09082985848889832,
            0.7266388679111866,
            -3.6331943395559327,
            12.716180188445765,
            -33.06206848995899,
            66.12413697991798,
            -103.90935811129968,
            129.8866976391246,
            -129.8866976391246,
            103.90935811129968,
            -66.12413697991798,
            33.06206848995899,
            -12.716180188445765,
            3.6331943395559327,
            -0.7266388679111866,
            0.09082985848889832,
            -0.005342932852288137
        ],
        [
            1.0,
            -7.038321997090006,
            24.874648831049683,
            -57.73625516292309,
            97.72038846360302,
            -127.22454342655489,
            131.35356435169217,
            -109.47707717142846,
            74.36682379101379,
            -41.31525881117518,
            18.7370477537318,
            -6.886243424517096,
            2.0230406212436236,
            -0.46455555981703966,
            0.08043970314216008,
            -0.009887526023096081,
            0.0007696731741546088,
            -2.8546931462853986e-05
        ]);
    }

    private static TransferFunction VhsDecodeSharpnessHighPass40Mhz()
    {
        return new TransferFunction(
        [
            0.07080115485922975,
            -1.416023097184595,
            13.452219423253652,
            -80.71331653952191,
            343.0315952929681,
            -1097.701104937498,
            2744.252762343745,
            -5488.50552468749,
            8918.821477617172,
            -11891.761970156229,
            13080.938167171851,
            -11891.761970156229,
            8918.821477617172,
            -5488.50552468749,
            2744.252762343745,
            -1097.701104937498,
            343.0315952929681,
            -80.71331653952191,
            13.452219423253652,
            -1.416023097184595,
            0.07080115485922975
        ],
        [
            1.0,
            -14.754595576527567,
            103.94648321884307,
            -464.81167109700175,
            1479.2575815988475,
            -3560.7896402343918,
            6725.635941945188,
            -10205.418680576624,
            12632.912854610553,
            -12881.000416128949,
            10876.225847857373,
            -7617.184167295951,
            4416.574167158443,
            -2108.307314042942,
            820.4111392548792,
            -256.21325473531834,
            62.705124728323675,
            -11.589588894834376,
            1.5217256607866756,
            -0.1265502523951289,
            0.005012803529400631
        ]);
    }

    internal static (
        Complex[] PrototypePoles,
        Complex[] AnalogPoles,
        Complex[] DigitalPoles,
        double DigitalGain) DesignButterworthHighPassZpk(int order, double normalizedCutoff)
    {
        if (order <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        ValidateNormalizedFrequency(normalizedCutoff);

        const double digitalSampleRate = 2.0;
        double warped = (2.0 * digitalSampleRate)
            * Math.Tan((Math.PI * normalizedCutoff) / digitalSampleRate);
        Complex[] prototypePoles = BuildButterworthPrototypePoles(order);

        Complex prototypePoleProduct = Complex.One;
        foreach (Complex pole in prototypePoles)
        {
            prototypePoleProduct = NumpyComplexMultiply(prototypePoleProduct, -pole);
        }

        double analogGain = NumpyComplexDivide(Complex.One, prototypePoleProduct).Real;
        var analogPoles = new Complex[order];
        for (int i = 0; i < analogPoles.Length; i++)
        {
            analogPoles[i] = NumpyComplexDivide(new Complex(warped, 0.0), prototypePoles[i]);
        }

        double bilinearScale = 2.0 * digitalSampleRate;
        var digitalPoles = new Complex[order];
        Complex bilinearDenominatorProduct = Complex.One;
        double bilinearNumeratorProduct = 1.0;
        for (int i = 0; i < order; i++)
        {
            Complex denominator = new(
                bilinearScale - analogPoles[i].Real,
                -analogPoles[i].Imaginary);
            digitalPoles[i] = NumpyComplexDivide(
                new Complex(
                    bilinearScale + analogPoles[i].Real,
                    analogPoles[i].Imaginary),
                denominator);
            bilinearDenominatorProduct = NumpyComplexMultiply(bilinearDenominatorProduct, denominator);
            bilinearNumeratorProduct *= bilinearScale;
        }

        double digitalGain = analogGain * NumpyComplexDivide(
            new Complex(bilinearNumeratorProduct, 0.0),
            bilinearDenominatorProduct).Real;
        return (prototypePoles, analogPoles, digitalPoles, digitalGain);
    }

    internal static Complex[] BuildButterworthPrototypePoles(int order)
    {
        if (order == 2)
        {
            return
            [
                new Complex(-0.7071067811865476, 0.7071067811865476),
                new Complex(-0.7071067811865476, -0.7071067811865476)
            ];
        }

        if (order == 6)
        {
            return
            [
                new Complex(-0.25881904510252096, 0.9659258262890682),
                new Complex(-0.7071067811865476, 0.7071067811865476),
                new Complex(-0.9659258262890683, 0.25881904510252074),
                new Complex(-0.9659258262890683, -0.25881904510252074),
                new Complex(-0.7071067811865476, -0.7071067811865476),
                new Complex(-0.25881904510252096, -0.9659258262890682)
            ];
        }

        if (order == 20)
        {
            return
            [
                new Complex(-0.078459095727845, 0.996917333733128),
                new Complex(-0.23344536385590525, 0.9723699203976767),
                new Complex(-0.38268343236508984, 0.9238795325112867),
                new Complex(-0.5224985647159487, 0.8526401643540923),
                new Complex(-0.6494480483301837, 0.7604059656000309),
                new Complex(-0.7604059656000309, 0.6494480483301837),
                new Complex(-0.8526401643540923, 0.5224985647159488),
                new Complex(-0.9238795325112867, 0.3826834323650898),
                new Complex(-0.9723699203976766, 0.2334453638559054),
                new Complex(-0.996917333733128, 0.07845909572784494),
                new Complex(-0.996917333733128, -0.07845909572784494),
                new Complex(-0.9723699203976766, -0.2334453638559054),
                new Complex(-0.9238795325112867, -0.3826834323650898),
                new Complex(-0.8526401643540923, -0.5224985647159488),
                new Complex(-0.7604059656000309, -0.6494480483301837),
                new Complex(-0.6494480483301837, -0.7604059656000309),
                new Complex(-0.5224985647159487, -0.8526401643540923),
                new Complex(-0.38268343236508984, -0.9238795325112867),
                new Complex(-0.23344536385590525, -0.9723699203976767),
                new Complex(-0.078459095727845, -0.996917333733128)
            ];
        }

        var poles = new Complex[order];
        for (int index = 0, m = -order + 1; index < order; index++, m += 2)
        {
            double angle = (Math.PI * m) / (2.0 * order);
            poles[index] = new Complex(-Math.Cos(angle), -Math.Sin(angle));
        }

        return poles;
    }

    public static (int Order, double NormalizedCutoff) ButterworthLowPassOrder(
        double normalizedPassFrequency,
        double normalizedStopFrequency,
        double passRippleDb,
        double stopAttenuationDb)
    {
        ValidateNormalizedFrequency(normalizedPassFrequency);
        ValidateNormalizedFrequency(normalizedStopFrequency);
        if (normalizedPassFrequency >= normalizedStopFrequency)
        {
            throw new ArgumentException("Low-pass stop frequency must be above the pass frequency.");
        }

        if (passRippleDb <= 0.0 || stopAttenuationDb <= 0.0 || passRippleDb >= stopAttenuationDb)
        {
            throw new ArgumentException("Butterworth attenuation values must satisfy 0 < passRippleDb < stopAttenuationDb.");
        }

        double warpedPass = Math.Tan(Math.PI * normalizedPassFrequency / 2.0);
        double warpedStop = Math.Tan(Math.PI * normalizedStopFrequency / 2.0);
        double naturalRatio = warpedStop / warpedPass;
        double pass = Math.Pow(10.0, 0.1 * Math.Abs(passRippleDb)) - 1.0;
        double stop = Math.Pow(10.0, 0.1 * Math.Abs(stopAttenuationDb)) - 1.0;
        int order = checked((int)Math.Ceiling(
            Math.Log10(stop / pass) / (2.0 * Math.Log10(naturalRatio))));
        double prototypeNatural = Math.Pow(pass, -1.0 / (2.0 * order));
        double warpedNatural = prototypeNatural * warpedPass;
        double normalizedNatural = Math.Atan(warpedNatural) * 2.0 / Math.PI;
        return (order, normalizedNatural);
    }

    public static TransferFunction ButterworthBandPass(int order, double normalizedLowCutoff, double normalizedHighCutoff)
    {
        (Complex[] digitalZeros, Complex[] digitalPoles, Complex gain) = DesignButterworthBandPassZpk(
            order,
            normalizedLowCutoff,
            normalizedHighCutoff);
        Complex[] numerator = PolynomialFromRootsOpenBlasDot(digitalZeros);
        Complex[] denominator = PolynomialFromRootsOpenBlasDot(digitalPoles);

        for (int i = 0; i < numerator.Length; i++)
        {
            numerator[i] *= gain;
        }

        return new TransferFunction(ToRealCoefficients(numerator), ToRealCoefficients(denominator));
    }

    internal static SosSection[] ChebyshevTypeIIBandPassSos(
        int order,
        double stopAttenuationDb,
        double lowCutoffHz,
        double highCutoffHz,
        double sampleRateHz)
    {
        if (order <= 0 || (order & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "HiFi Chebyshev-II order must be positive and even.");
        }

        if (!double.IsFinite(stopAttenuationDb) || stopAttenuationDb <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(stopAttenuationDb));
        }

        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(lowCutoffHz)
            || !double.IsFinite(highCutoffHz)
            || lowCutoffHz <= 0.0
            || lowCutoffHz >= highCutoffHz
            || highCutoffHz >= sampleRateHz / 2.0)
        {
            throw new ArgumentException("Band-pass frequencies must satisfy 0 < low < high < Nyquist.");
        }

        (Complex[] zeros, Complex[] poles, double gain) = BuildChebyshevTypeIIPrototype(
            order,
            stopAttenuationDb);
        double nyquist = sampleRateHz / 2.0;
        double normalizedLow = lowCutoffHz / nyquist;
        double normalizedHigh = highCutoffHz / nyquist;
        const double designSampleRate = 2.0;
        double warpedLow = 2.0 * designSampleRate
            * Math.Tan(Math.PI * normalizedLow / designSampleRate);
        double warpedHigh = 2.0 * designSampleRate
            * Math.Tan(Math.PI * normalizedHigh / designSampleRate);
        double bandwidth = warpedHigh - warpedLow;
        double center = Math.Sqrt(warpedLow * warpedHigh);
        (zeros, poles, gain) = LowPassToBandPassZpk(
            zeros,
            poles,
            gain,
            center,
            bandwidth);
        (zeros, poles, gain) = BilinearZpk(
            zeros,
            poles,
            gain,
            designSampleRate);
        return ZpkToConjugateNearestSos(zeros, poles, gain);
    }

    public static SosSection[] ButterworthBandPassSos(int order, double normalizedLowCutoff, double normalizedHighCutoff)
    {
        if (order == 1)
        {
            return [ButterworthBandPassFirstOrderSos(normalizedLowCutoff, normalizedHighCutoff)];
        }

        (Complex[] zeros, Complex[] poles, Complex gain) = DesignButterworthBandPassZpk(
            order,
            normalizedLowCutoff,
            normalizedHighCutoff);
        return ZpkToNearestSos(zeros, poles, gain.Real);
    }

    private static SosSection ButterworthBandPassFirstOrderSos(
        double normalizedLowCutoff,
        double normalizedHighCutoff)
    {
        ValidateNormalizedFrequency(normalizedLowCutoff);
        ValidateNormalizedFrequency(normalizedHighCutoff);
        if (normalizedLowCutoff >= normalizedHighCutoff)
        {
            throw new ArgumentException("Band-pass low cutoff must be below the high cutoff.");
        }

        const double sampleRate = 2.0;
        const double bilinearScale = 2.0 * sampleRate;
        double warpedLow = (2.0 * sampleRate)
            * Math.Tan((Math.PI * normalizedLowCutoff) / sampleRate);
        double warpedHigh = (2.0 * sampleRate)
            * Math.Tan((Math.PI * normalizedHighCutoff) / sampleRate);
        double bandwidth = warpedHigh - warpedLow;
        double center = Math.Sqrt(warpedLow * warpedHigh);

        double analogPoleReal = (-1.0 * bandwidth) / 2.0;
        double analogPoleImaginary = Math.Sqrt(
            (center * center) - (analogPoleReal * analogPoleReal));
        Complex analogPole1 = new(analogPoleReal, -analogPoleImaginary);
        Complex analogPole2 = new(analogPoleReal, analogPoleImaginary);
        Complex digitalPole1 = NumpyComplexDivide(
            new Complex(
                bilinearScale + analogPole1.Real,
                analogPole1.Imaginary),
            new Complex(
                bilinearScale - analogPole1.Real,
                -analogPole1.Imaginary));
        Complex digitalPole2 = NumpyComplexDivide(
            new Complex(
                bilinearScale + analogPole2.Real,
                analogPole2.Imaginary),
            new Complex(
                bilinearScale - analogPole2.Real,
                -analogPole2.Imaginary));

        Complex denominatorProduct = NumpyComplexMultiply(
            new Complex(
                bilinearScale - analogPole1.Real,
                -analogPole1.Imaginary),
            new Complex(
                bilinearScale - analogPole2.Real,
                -analogPole2.Imaginary));
        Complex gainRatio = NumpyComplexDivide(
            new Complex(bilinearScale, 0.0),
            denominatorProduct);
        double gain = bandwidth * gainRatio.Real;
        double denominator1 = -(digitalPole1.Real + digitalPole2.Real);
        double denominator2 = NumpyComplexMultiply(digitalPole1, digitalPole2).Real;
        return new SosSection(
            gain,
            0.0 * gain,
            -1.0 * gain,
            1.0,
            denominator1,
            denominator2);
    }

    private static Complex NumpyComplexMultiply(Complex left, Complex right)
    {
        double realLeft = left.Real * right.Real;
        double realRight = left.Imaginary * right.Imaginary;
        double imaginaryLeft = left.Real * right.Imaginary;
        double imaginaryRight = left.Imaginary * right.Real;
        return new Complex(realLeft - realRight, imaginaryLeft + imaginaryRight);
    }

    private static Complex NumpySimdComplexMultiply(Complex left, Complex right)
    {
        return new Complex(
            Math.FusedMultiplyAdd(
                left.Real,
                right.Real,
                -(left.Imaginary * right.Imaginary)),
            Math.FusedMultiplyAdd(
                left.Imaginary,
                right.Real,
                left.Real * right.Imaginary));
    }

    private static Complex NumpyComplexDivide(Complex left, Complex right)
    {
        double realAbsolute = Math.Abs(right.Real);
        double imaginaryAbsolute = Math.Abs(right.Imaginary);
        if (realAbsolute >= imaginaryAbsolute)
        {
            if (realAbsolute == 0.0 && imaginaryAbsolute == 0.0)
            {
                return new Complex(left.Real / realAbsolute, left.Imaginary / imaginaryAbsolute);
            }

            double ratio = right.Imaginary / right.Real;
            double scaledDenominator = 1.0 / (right.Real + (right.Imaginary * ratio));
            double real = (left.Real + (left.Imaginary * ratio)) * scaledDenominator;
            double imaginary = (left.Imaginary - (left.Real * ratio)) * scaledDenominator;
            return new Complex(real, imaginary);
        }

        double alternateRatio = right.Real / right.Imaginary;
        double alternateScale = 1.0 / (right.Imaginary + (right.Real * alternateRatio));
        double alternateReal = ((left.Real * alternateRatio) + left.Imaginary) * alternateScale;
        double alternateImaginary = ((left.Imaginary * alternateRatio) - left.Real) * alternateScale;
        return new Complex(alternateReal, alternateImaginary);
    }

    public static TransferFunction ButterworthBandStop(int order, double normalizedLowCutoff, double normalizedHighCutoff)
    {
        (Complex[] digitalZeros, Complex[] digitalPoles, double digitalGain) = DesignButterworthBandStopZpk(
            order,
            normalizedLowCutoff,
            normalizedHighCutoff);
        Complex[] numerator = PolynomialFromRootsNumpy(digitalZeros);
        Complex[] denominator = PolynomialFromRootsNumpy(digitalPoles);
        for (int i = 0; i < numerator.Length; i++)
        {
            numerator[i] = new Complex(
                numerator[i].Real * digitalGain,
                numerator[i].Imaginary * digitalGain);
        }

        return new TransferFunction(ToRealCoefficients(numerator), ToRealCoefficients(denominator));
    }

    internal static (Complex[] DigitalZeros, Complex[] DigitalPoles, double DigitalGain)
        DesignButterworthBandStopZpk(
            int order,
            double normalizedLowCutoff,
            double normalizedHighCutoff)
    {
        if (order <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        ValidateNormalizedFrequency(normalizedLowCutoff);
        ValidateNormalizedFrequency(normalizedHighCutoff);
        if (normalizedLowCutoff >= normalizedHighCutoff)
        {
            throw new ArgumentException("Band-stop low cutoff must be below the high cutoff.");
        }

        const double sampleRate = 2.0;
        double warpedLow = 2.0 * sampleRate * Math.Tan(Math.PI * normalizedLowCutoff / 2.0);
        double warpedHigh = 2.0 * sampleRate * Math.Tan(Math.PI * normalizedHighCutoff / 2.0);
        double bandwidth = warpedHigh - warpedLow;
        double center = Math.Sqrt(warpedLow * warpedHigh);

        Complex[] prototypePoles = BuildButterworthPrototypePoles(order);
        Complex prototypePoleProduct = Complex.One;
        foreach (Complex pole in prototypePoles)
        {
            prototypePoleProduct = NumpyComplexMultiply(prototypePoleProduct, -pole);
        }

        double analogGain = NumpyComplexDivide(Complex.One, prototypePoleProduct).Real;
        var positiveAnalogPoles = new Complex[order];
        var negativeAnalogPoles = new Complex[order];
        double centerSquared = center * center;
        for (int i = 0; i < order; i++)
        {
            Complex highPassPole = NumpyComplexDivide(
                new Complex(bandwidth / 2.0, 0.0),
                prototypePoles[i]);
            Complex squared = NumpyComplexMultiply(highPassPole, highPassPole);
            Complex root = Complex.Sqrt(new Complex(squared.Real - centerSquared, squared.Imaginary));
            positiveAnalogPoles[i] = highPassPole + root;
            negativeAnalogPoles[i] = highPassPole - root;
        }

        Complex[] analogPoles = [.. positiveAnalogPoles, .. negativeAnalogPoles];
        Complex[] analogZeros =
        [
            .. Enumerable.Repeat(new Complex(0.0, center), order),
            .. Enumerable.Repeat(new Complex(0.0, -center), order)
        ];
        double bilinearScale = 2.0 * sampleRate;
        var digitalZeros = new Complex[analogZeros.Length];
        var digitalPoles = new Complex[analogPoles.Length];
        Complex bilinearNumeratorProduct = Complex.One;
        Complex bilinearDenominatorProduct = Complex.One;
        for (int i = 0; i < analogZeros.Length; i++)
        {
            Complex bilinearNumerator = new(
                bilinearScale + analogZeros[i].Real,
                analogZeros[i].Imaginary);
            Complex bilinearDenominator = new(
                bilinearScale - analogZeros[i].Real,
                -analogZeros[i].Imaginary);
            digitalZeros[i] = NumpyComplexDivide(bilinearNumerator, bilinearDenominator);
            bilinearNumeratorProduct = NumpyComplexMultiply(bilinearNumeratorProduct, bilinearDenominator);
        }

        for (int i = 0; i < analogPoles.Length; i++)
        {
            Complex bilinearNumerator = new(
                bilinearScale + analogPoles[i].Real,
                analogPoles[i].Imaginary);
            Complex bilinearDenominator = new(
                bilinearScale - analogPoles[i].Real,
                -analogPoles[i].Imaginary);
            digitalPoles[i] = NumpyComplexDivide(bilinearNumerator, bilinearDenominator);
            bilinearDenominatorProduct = NumpyComplexMultiply(bilinearDenominatorProduct, bilinearDenominator);
        }

        double digitalGain = analogGain * NumpyComplexDivide(
            bilinearNumeratorProduct,
            bilinearDenominatorProduct).Real;
        return (digitalZeros, digitalPoles, digitalGain);
    }

    public static TransferFunction Notch(double normalizedFrequency, double q)
    {
        ValidateNormalizedFrequency(normalizedFrequency);
        if (q <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(q));
        }

        double bandwidth = (normalizedFrequency / q) * Math.PI;
        double omega = normalizedFrequency * Math.PI;
        double beta = Math.Tan(bandwidth / 2.0);
        double gain = 1.0 / (1.0 + beta);
        double cos = Math.Cos(omega);
        return new TransferFunction(
            [gain, (-2.0 * cos) * gain, gain],
            [1.0, (-2.0 * gain) * cos, (2.0 * gain) - 1.0]);
    }

    public static TransferFunction PeakingConstantQ(double normalizedFrequency, double gainDb, double bandwidthOctaves)
    {
        ValidateNormalizedFrequency(normalizedFrequency);
        if (bandwidthOctaves <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bandwidthOctaves));
        }

        double q = 1.0 / (2.0 * Math.Sinh((Math.Log(2.0) / 2.0) * bandwidthOctaves));
        double gain = Math.Pow(10.0, gainDb / 20.0);
        double zeroGain = gainDb > 0 ? gain : 1.0;
        double poleGain = gainDb > 0 ? 1.0 : gain;
        double warped = 4.0 * Math.Tan(Math.PI * normalizedFrequency / 2.0);

        double[] analogNumerator = [1.0, (zeroGain / q) * warped, warped * warped];
        double[] analogDenominator = [1.0, (1.0 / (poleGain * q)) * warped, warped * warped];
        return BilinearSecondOrder(analogNumerator, analogDenominator, sampleRate: 2.0);
    }

    public static TransferFunction Shelf(
        double centerFrequencyHz,
        double gainDb,
        ShelfKind kind,
        double sampleRateHz,
        double q)
    {
        if (centerFrequencyHz <= 0 || sampleRateHz <= 0 || centerFrequencyHz >= sampleRateHz / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(centerFrequencyHz));
        }

        if (q <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(q));
        }

        double gain = Math.Pow(10.0, gainDb / 40.0);
        double omega = Math.Tau * (centerFrequencyHz / sampleRateHz);
        double alpha = Math.Sin(omega) / (2.0 * q);
        double cos = Math.Cos(omega);
        double rootGain = Math.Sqrt(gain);

        double b0;
        double b1;
        double b2;
        double a0;
        double a1;
        double a2;
        if (kind == ShelfKind.Low)
        {
            b0 = gain * ((gain + 1.0) - ((gain - 1.0) * cos) + (2.0 * rootGain * alpha));
            b1 = 2.0 * gain * ((gain - 1.0) - ((gain + 1.0) * cos));
            b2 = gain * ((gain + 1.0) - ((gain - 1.0) * cos) - (2.0 * rootGain * alpha));
            a0 = (gain + 1.0) + ((gain - 1.0) * cos) + (2.0 * rootGain * alpha);
            a1 = -2.0 * ((gain - 1.0) + ((gain + 1.0) * cos));
            a2 = (gain + 1.0) + ((gain - 1.0) * cos) - (2.0 * rootGain * alpha);
        }
        else
        {
            b0 = gain * ((gain + 1.0) + ((gain - 1.0) * cos) + (2.0 * rootGain * alpha));
            b1 = -2.0 * gain * ((gain - 1.0) + ((gain + 1.0) * cos));
            b2 = gain * ((gain + 1.0) + ((gain - 1.0) * cos) - (2.0 * rootGain * alpha));
            a0 = (gain + 1.0) - ((gain - 1.0) * cos) + (2.0 * rootGain * alpha);
            a1 = 2.0 * ((gain - 1.0) - ((gain + 1.0) * cos));
            a2 = (gain + 1.0) - ((gain - 1.0) * cos) - (2.0 * rootGain * alpha);
        }

        return new TransferFunction([b0, b1, b2], [a0, a1, a2]);
    }

    public static TransferFunction VideoDeEmphasisShelf(
        double sampleRateHz,
        double gainDb,
        double midpointHz,
        double q)
    {
        TransferFunction preEmphasis = Shelf(midpointHz, gainDb, ShelfKind.High, sampleRateHz, q);
        return new TransferFunction(
            preEmphasis.Denominator.ToArray(),
            preEmphasis.Numerator.ToArray());
    }

    public static TransferFunction EmphasisIir(double zeroTimeConstant, double poleTimeConstant, double sampleRateHz)
    {
        if (zeroTimeConstant <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroTimeConstant));
        }

        if (poleTimeConstant <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(poleTimeConstant));
        }

        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        double zeroFrequency = 2.0 * sampleRateHz * Math.Tan((1.0 / zeroTimeConstant) / (2.0 * sampleRateHz));
        double poleFrequency = 2.0 * sampleRateHz * Math.Tan((1.0 / poleTimeConstant) / (2.0 * sampleRateHz));
        double gain = poleFrequency / zeroFrequency;
        return BilinearFirstOrder(
            [gain, gain * zeroFrequency],
            [1.0, poleFrequency],
            sampleRateHz);
    }

    public static Complex[] FrequencyResponse(TransferFunction filter, int length, bool whole = true)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (whole
            && length >= 2
            && filter.Denominator.Length == 1
            && filter.Numerator.Length <= length
            && BitOperations.IsPow2((uint)length))
        {
            var padded = new double[length];
            filter.Numerator.CopyTo(padded, 0);
            Complex[] half = PocketFftComplex.ForwardDuccReal(padded);
            double scalarDenominator = filter.Denominator[0];
            for (int i = 0; i < half.Length; i++)
            {
                half[i] /= scalarDenominator;
            }

            var fastOutput = new Complex[length];
            half.CopyTo(fastOutput, 0);
            int mirroredCount = (length % 2) == 0 ? half.Length - 2 : half.Length - 1;
            for (int i = 0; i < mirroredCount; i++)
            {
                fastOutput[half.Length + i] = Complex.Conjugate(half[mirroredCount - i]);
            }

            return fastOutput;
        }

        var output = new Complex[length];
        double step = (whole ? Math.Tau : Math.PI) / length;
        for (int i = 0; i < output.Length; i++)
        {
            double omega = i * step;
            Complex inverseUnit = Complex.FromPolarCoordinates(1.0, -omega);
            Complex numerator = EvaluatePolynomialHorner(filter.Numerator, inverseUnit);
            Complex denominator = EvaluatePolynomialHorner(filter.Denominator, inverseUnit);
            output[i] = NumpyComplexDivide(numerator, denominator);
        }

        return output;
    }

    private static Complex EvaluatePolynomialHorner(ReadOnlySpan<double> coefficients, Complex value)
    {
        Complex result = new(coefficients[^1], 0.0);
        for (int i = coefficients.Length - 2; i >= 0; i--)
        {
            Complex product = NumpyVectorComplexMultiply(result, value);
            result = new Complex(
                coefficients[i] + product.Real,
                product.Imaginary);
        }

        return result;
    }

    public static Complex[] FrequencyResponse(IReadOnlyList<SosSection> sections, int length, bool whole = true)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        Complex[]? output = null;
        foreach (SosSection raw in sections)
        {
            SosSection section = raw.Normalize();
            Complex[] sectionResponse = FrequencyResponse(
                new TransferFunction(
                    [section.B0, section.B1, section.B2],
                    [section.A0, section.A1, section.A2]),
                length,
                whole);
            if (output is null)
            {
                output = sectionResponse;
                continue;
            }

            for (int i = 0; i < output.Length; i++)
            {
                output[i] = NumpyVectorComplexMultiply(output[i], sectionResponse[i]);
            }
        }

        if (output is null)
        {
            throw new ArgumentException("At least one SOS section is required.", nameof(sections));
        }

        return output;
    }

    private static Complex NumpyVectorComplexMultiply(Complex left, Complex right)
    {
        return new Complex(
            Math.FusedMultiplyAdd(
                left.Real,
                right.Real,
                -(left.Imaginary * right.Imaginary)),
            Math.FusedMultiplyAdd(
                left.Real,
                right.Imaginary,
                left.Imaginary * right.Real));
    }

    public static double[] MagnitudeResponse(IReadOnlyList<SosSection> sections, int length, bool whole = true)
    {
        return FrequencyResponse(sections, length, whole).Select(value => value.Magnitude).ToArray();
    }

    private static SosSection[] ButterworthLowHighPass(int order, double normalizedCutoff, bool highPass)
    {
        if (order <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        ValidateNormalizedFrequency(normalizedCutoff);
        double k = Math.Tan(Math.PI * normalizedCutoff / 2.0);
        int secondOrderSections = order / 2;
        bool hasFirstOrder = (order % 2) != 0;
        var sections = new List<SosSection>(secondOrderSections + (hasFirstOrder ? 1 : 0));

        for (int sectionIndex = 0; sectionIndex < secondOrderSections; sectionIndex++)
        {
            double q = 1.0 / (2.0 * Math.Sin(((2.0 * sectionIndex) + 1.0) * Math.PI / (2.0 * order)));
            sections.Add(highPass ? HighPassBiquad(k, q) : LowPassBiquad(k, q));
        }

        if (hasFirstOrder)
        {
            sections.Add(highPass ? HighPassFirstOrder(k) : LowPassFirstOrder(k));
        }

        return sections.ToArray();
    }

    private static SosSection LowPassFirstOrder(double k)
    {
        double norm = 1.0 / (1.0 + k);
        return new SosSection(k * norm, k * norm, 0.0, 1.0, (k - 1.0) * norm, 0.0);
    }

    private static SosSection HighPassFirstOrder(double k)
    {
        double norm = 1.0 / (1.0 + k);
        return new SosSection(norm, -norm, 0.0, 1.0, (k - 1.0) * norm, 0.0);
    }

    private static SosSection LowPassBiquad(double k, double q)
    {
        double k2 = k * k;
        double norm = 1.0 / (1.0 + (k / q) + k2);
        return new SosSection(
            k2 * norm,
            2.0 * k2 * norm,
            k2 * norm,
            1.0,
            2.0 * (k2 - 1.0) * norm,
            (1.0 - (k / q) + k2) * norm);
    }

    private static SosSection HighPassBiquad(double k, double q)
    {
        double k2 = k * k;
        double norm = 1.0 / (1.0 + (k / q) + k2);
        return new SosSection(
            norm,
            -2.0 * norm,
            norm,
            1.0,
            2.0 * (k2 - 1.0) * norm,
            (1.0 - (k / q) + k2) * norm);
    }

    private static (Complex[] Zeros, Complex[] Poles, Complex Gain) DesignButterworthBandPassZpk(
        int order,
        double normalizedLowCutoff,
        double normalizedHighCutoff)
    {
        if (order <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        ValidateNormalizedFrequency(normalizedLowCutoff);
        ValidateNormalizedFrequency(normalizedHighCutoff);
        if (normalizedLowCutoff >= normalizedHighCutoff)
        {
            throw new ArgumentException("Band-pass low cutoff must be below the high cutoff.");
        }

        const double sampleRate = 2.0;
        double warpedLow = 2.0 * sampleRate * Math.Tan(Math.PI * normalizedLowCutoff / 2.0);
        double warpedHigh = 2.0 * sampleRate * Math.Tan(Math.PI * normalizedHighCutoff / 2.0);
        double bandwidth = warpedHigh - warpedLow;
        double center = Math.Sqrt(warpedLow * warpedHigh);
        Complex[] prototypePoles = BuildButterworthPrototypePoles(order);
        var positiveAnalogPoles = new Complex[order];
        var negativeAnalogPoles = new Complex[order];
        for (int i = 0; i < order; i++)
        {
            Complex scaledPole = prototypePoles[i] * (bandwidth / 2.0);
            Complex root = Complex.Sqrt(
                NumpyComplexMultiply(scaledPole, scaledPole) - (center * center));
            positiveAnalogPoles[i] = scaledPole + root;
            negativeAnalogPoles[i] = scaledPole - root;
        }

        Complex[] analogPoles = [.. positiveAnalogPoles, .. negativeAnalogPoles];
        double bilinearScale = 2.0 * sampleRate;
        var digitalZeros = new Complex[2 * order];
        for (int i = 0; i < order; i++)
        {
            digitalZeros[i] = Complex.One;
            digitalZeros[order + i] = -Complex.One;
        }

        var digitalPoles = new Complex[analogPoles.Length];
        Complex denominatorProduct = Complex.One;
        for (int i = 0; i < analogPoles.Length; i++)
        {
            Complex pole = analogPoles[i];
            Complex denominator = new(bilinearScale - pole.Real, -pole.Imaginary);
            digitalPoles[i] = NumpyComplexDivide(
                new Complex(bilinearScale + pole.Real, pole.Imaginary),
                denominator);
            denominatorProduct = NumpySimdComplexMultiply(denominatorProduct, denominator);
        }

        double analogGain = Math.Pow(bandwidth, order);
        double numeratorProduct = Math.Pow(bilinearScale, order);
        double digitalGain = analogGain * NumpyComplexDivide(
            new Complex(numeratorProduct, 0.0),
            denominatorProduct).Real;
        return (digitalZeros, digitalPoles, new Complex(digitalGain, 0.0));
    }

    private static (Complex[] Zeros, Complex[] Poles, double Gain)
        BuildChebyshevTypeIIPrototype(int order, double stopAttenuationDb)
    {
        double epsilon = 1.0 / Math.Sqrt(Math.Pow(10.0, 0.1 * stopAttenuationDb) - 1.0);
        double mu = Math.Asinh(1.0 / epsilon) / order;
        int zeroCount = (order & 1) == 0 ? order : order - 1;
        var zeros = new Complex[zeroCount];
        int zeroIndex = 0;
        for (int m = -order + 1; m < order; m += 2)
        {
            if ((order & 1) != 0 && m == 0)
            {
                continue;
            }

            zeros[zeroIndex++] = new Complex(
                0.0,
                1.0 / Math.Sin(m * Math.PI / (2.0 * order)));
        }

        var poles = new Complex[order];
        for (int index = 0, m = -order + 1; index < order; index++, m += 2)
        {
            double theta = Math.PI * m / (2.0 * order);
            Complex hyperbolicSine = new(
                Math.Sinh(mu) * Math.Cos(theta),
                Math.Cosh(mu) * Math.Sin(theta));
            poles[index] = NumpyComplexDivide(-Complex.One, hyperbolicSine);
        }

        Complex poleProduct = Complex.One;
        foreach (Complex pole in poles)
        {
            poleProduct = NumpyComplexMultiply(poleProduct, -pole);
        }

        Complex zeroProduct = Complex.One;
        foreach (Complex zero in zeros)
        {
            zeroProduct = NumpyComplexMultiply(zeroProduct, -zero);
        }

        double gain = NumpyComplexDivide(poleProduct, zeroProduct).Real;
        return (zeros, poles, gain);
    }

    private static (Complex[] Zeros, Complex[] Poles, double Gain) LowPassToBandPassZpk(
        IReadOnlyList<Complex> zeros,
        IReadOnlyList<Complex> poles,
        double gain,
        double center,
        double bandwidth)
    {
        int degree = poles.Count - zeros.Count;
        double scale = bandwidth / 2.0;
        double centerSquared = center * center;
        var positiveZeros = new Complex[zeros.Count];
        var negativeZeros = new Complex[zeros.Count];
        for (int i = 0; i < zeros.Count; i++)
        {
            Complex scaled = new(zeros[i].Real * scale, zeros[i].Imaginary * scale);
            Complex squared = NumpySimdComplexMultiply(scaled, scaled);
            Complex root = Complex.Sqrt(new Complex(squared.Real - centerSquared, squared.Imaginary));
            positiveZeros[i] = scaled + root;
            negativeZeros[i] = scaled - root;
        }

        var positivePoles = new Complex[poles.Count];
        var negativePoles = new Complex[poles.Count];
        for (int i = 0; i < poles.Count; i++)
        {
            Complex scaled = new(poles[i].Real * scale, poles[i].Imaginary * scale);
            Complex squared = NumpySimdComplexMultiply(scaled, scaled);
            Complex root = Complex.Sqrt(new Complex(squared.Real - centerSquared, squared.Imaginary));
            positivePoles[i] = scaled + root;
            negativePoles[i] = scaled - root;
        }

        Complex[] bandPassZeros =
        [
            .. positiveZeros,
            .. negativeZeros,
            .. Enumerable.Repeat(Complex.Zero, degree)
        ];
        Complex[] bandPassPoles = [.. positivePoles, .. negativePoles];
        return (bandPassZeros, bandPassPoles, gain * Math.Pow(bandwidth, degree));
    }

    private static (Complex[] Zeros, Complex[] Poles, double Gain) BilinearZpk(
        IReadOnlyList<Complex> zeros,
        IReadOnlyList<Complex> poles,
        double gain,
        double sampleRate)
    {
        int degree = poles.Count - zeros.Count;
        double scale = 2.0 * sampleRate;
        var digitalZeros = new Complex[zeros.Count + degree];
        Complex zeroProduct = Complex.One;
        for (int i = 0; i < zeros.Count; i++)
        {
            Complex numerator = new(scale + zeros[i].Real, zeros[i].Imaginary);
            Complex denominator = new(scale - zeros[i].Real, -zeros[i].Imaginary);
            digitalZeros[i] = NumpyComplexDivide(numerator, denominator);
            zeroProduct = NumpySimdComplexMultiply(zeroProduct, denominator);
        }

        for (int i = zeros.Count; i < digitalZeros.Length; i++)
        {
            digitalZeros[i] = -Complex.One;
        }

        var digitalPoles = new Complex[poles.Count];
        Complex poleProduct = Complex.One;
        for (int i = 0; i < poles.Count; i++)
        {
            Complex numerator = new(scale + poles[i].Real, poles[i].Imaginary);
            Complex denominator = new(scale - poles[i].Real, -poles[i].Imaginary);
            digitalPoles[i] = NumpyComplexDivide(numerator, denominator);
            poleProduct = NumpySimdComplexMultiply(poleProduct, denominator);
        }

        double digitalGain = gain * NumpyComplexDivide(zeroProduct, poleProduct).Real;
        return (digitalZeros, digitalPoles, digitalGain);
    }

    private static SosSection[] ZpkToNearestSos(
        IReadOnlyCollection<Complex> zeros,
        IReadOnlyCollection<Complex> poles,
        double gain)
    {
        if (zeros.Count != poles.Count || (poles.Count & 1) != 0)
        {
            throw new ArgumentException("Digital SOS conversion requires an equal, even number of zeros and poles.");
        }

        var remainingPoles = poles.ToList();
        var polePairs = new List<(Complex First, Complex Second, double Radius)>();
        while (remainingPoles.Count > 0)
        {
            int firstIndex = remainingPoles.FindIndex(value => value.Imaginary > 1e-12);
            if (firstIndex < 0)
            {
                firstIndex = 0;
            }

            Complex first = remainingPoles[firstIndex];
            remainingPoles.RemoveAt(firstIndex);
            Complex target = Complex.Conjugate(first);
            int secondIndex = 0;
            double closest = double.PositiveInfinity;
            for (int i = 0; i < remainingPoles.Count; i++)
            {
                double distance = Complex.Abs(remainingPoles[i] - target);
                if (distance < closest)
                {
                    closest = distance;
                    secondIndex = i;
                }
            }

            Complex second = remainingPoles[secondIndex];
            remainingPoles.RemoveAt(secondIndex);
            polePairs.Add((first, second, Math.Max(first.Magnitude, second.Magnitude)));
        }

        var remainingZeros = zeros.ToList();
        var assigned = new List<(Complex Pole1, Complex Pole2, Complex Zero1, Complex Zero2, double Radius)>();
        foreach ((Complex first, Complex second, double radius) in polePairs.OrderByDescending(pair => pair.Radius))
        {
            int zero1Index = IndexOfNearest(remainingZeros, first);
            Complex zero1 = remainingZeros[zero1Index];
            remainingZeros.RemoveAt(zero1Index);
            int zero2Index = IndexOfNearest(remainingZeros, first);
            Complex zero2 = remainingZeros[zero2Index];
            remainingZeros.RemoveAt(zero2Index);
            assigned.Add((first, second, zero1, zero2, radius));
        }

        var sections = new List<SosSection>(assigned.Count);
        foreach (var pair in assigned.OrderBy(pair => pair.Radius))
        {
            sections.Add(new SosSection(
                1.0,
                -(pair.Zero1 + pair.Zero2).Real,
                (pair.Zero1 * pair.Zero2).Real,
                1.0,
                -(pair.Pole1 + pair.Pole2).Real,
                (pair.Pole1 * pair.Pole2).Real));
        }

        if (sections.Count > 0)
        {
            SosSection first = sections[0];
            sections[0] = first with
            {
                B0 = first.B0 * gain,
                B1 = first.B1 * gain,
                B2 = first.B2 * gain
            };
        }

        return sections.ToArray();
    }

    private static SosSection[] ZpkToConjugateNearestSos(
        IReadOnlyCollection<Complex> zeros,
        IReadOnlyCollection<Complex> poles,
        double gain)
    {
        if (zeros.Count != poles.Count || (poles.Count & 1) != 0)
        {
            throw new ArgumentException("Digital SOS conversion requires an equal, even number of zeros and poles.");
        }

        var remainingPoles = poles.ToList();
        var polePairs = new List<(Complex First, Complex Second, double Radius)>();
        while (remainingPoles.Count > 0)
        {
            int firstIndex = remainingPoles.FindIndex(value => value.Imaginary > 1e-12);
            if (firstIndex < 0)
            {
                throw new ArgumentException("Conjugate SOS conversion requires complex pole pairs.", nameof(poles));
            }

            Complex first = remainingPoles[firstIndex];
            remainingPoles.RemoveAt(firstIndex);
            int secondIndex = IndexOfNearest(remainingPoles, Complex.Conjugate(first));
            Complex second = remainingPoles[secondIndex];
            remainingPoles.RemoveAt(secondIndex);
            polePairs.Add((first, second, Math.Max(first.Magnitude, second.Magnitude)));
        }

        var remainingZeros = zeros.ToList();
        var assigned = new List<(Complex Pole1, Complex Pole2, Complex Zero1, Complex Zero2, double Radius)>();
        foreach ((Complex first, Complex second, double radius) in polePairs.OrderByDescending(pair => pair.Radius))
        {
            int zero1Index = IndexOfNearest(remainingZeros, first);
            Complex zero1 = remainingZeros[zero1Index];
            remainingZeros.RemoveAt(zero1Index);
            int zero2Index = IndexOfNearest(remainingZeros, Complex.Conjugate(zero1));
            Complex zero2 = remainingZeros[zero2Index];
            remainingZeros.RemoveAt(zero2Index);
            assigned.Add((first, second, zero1, zero2, radius));
        }

        var sections = new List<SosSection>(assigned.Count);
        foreach (var pair in assigned.OrderBy(pair => pair.Radius))
        {
            sections.Add(new SosSection(
                1.0,
                -(pair.Zero1 + pair.Zero2).Real,
                (pair.Zero1 * pair.Zero2).Real,
                1.0,
                -(pair.Pole1 + pair.Pole2).Real,
                (pair.Pole1 * pair.Pole2).Real));
        }

        SosSection firstSection = sections[0];
        sections[0] = firstSection with
        {
            B0 = firstSection.B0 * gain,
            B1 = firstSection.B1 * gain,
            B2 = firstSection.B2 * gain
        };
        return sections.ToArray();
    }

    private static int IndexOfNearest(IReadOnlyList<Complex> values, Complex target)
    {
        int selected = 0;
        double closest = double.PositiveInfinity;
        for (int i = 0; i < values.Count; i++)
        {
            double distance = Complex.Abs(values[i] - target);
            if (distance < closest)
            {
                closest = distance;
                selected = i;
            }
        }

        return selected;
    }

    private static TransferFunction BilinearSecondOrder(
        ReadOnlySpan<double> analogNumerator,
        ReadOnlySpan<double> analogDenominator,
        double sampleRate)
    {
        if (analogNumerator.Length != 3 || analogDenominator.Length != 3)
        {
            throw new ArgumentException("Only second-order transfer functions are supported.");
        }

        double c = 2.0 * sampleRate;
        double c2 = c * c;
        double n0 = (analogNumerator[0] * c2) + (analogNumerator[1] * c) + analogNumerator[2];
        double n1 = (-2.0 * analogNumerator[0] * c2) + (2.0 * analogNumerator[2]);
        double n2 = (analogNumerator[0] * c2) - (analogNumerator[1] * c) + analogNumerator[2];
        double d0 = (analogDenominator[0] * c2) + (analogDenominator[1] * c) + analogDenominator[2];
        double d1 = (-2.0 * analogDenominator[0] * c2) + (2.0 * analogDenominator[2]);
        double d2 = (analogDenominator[0] * c2) - (analogDenominator[1] * c) + analogDenominator[2];

        return new TransferFunction(
            [n0 / d0, n1 / d0, n2 / d0],
            [1.0, d1 / d0, d2 / d0]);
    }

    private static TransferFunction BilinearFirstOrder(
        ReadOnlySpan<double> analogNumerator,
        ReadOnlySpan<double> analogDenominator,
        double sampleRate)
    {
        if (analogNumerator.Length != 2 || analogDenominator.Length != 2)
        {
            throw new ArgumentException("Only first-order transfer functions are supported.");
        }

        // SciPy 1.18 splits 2*fs through sqrt before polynomial expansion.
        double factor = Math.Sqrt(sampleRate * 2.0);
        double inverseFactor = 1.0 / factor;
        double numeratorConstant = (analogNumerator[1] * inverseFactor)
            + (analogNumerator[0] * -factor);
        double numeratorLinear = (analogNumerator[1] * inverseFactor)
            + (analogNumerator[0] * factor);
        double denominatorConstant = (analogDenominator[1] * inverseFactor)
            + (analogDenominator[0] * -factor);
        double denominatorLinear = (analogDenominator[1] * inverseFactor)
            + (analogDenominator[0] * factor);

        return new TransferFunction(
            [numeratorLinear / denominatorLinear, numeratorConstant / denominatorLinear],
            [denominatorLinear / denominatorLinear, denominatorConstant / denominatorLinear]);
    }

    private static Complex EvaluatePolynomial(ReadOnlySpan<double> coefficients, double omega)
    {
        Complex sum = Complex.Zero;
        for (int k = 0; k < coefficients.Length; k++)
        {
            sum += coefficients[k] * Complex.FromPolarCoordinates(1.0, -omega * k);
        }

        return sum;
    }

    private static Complex EvaluatePolynomial(ReadOnlySpan<Complex> coefficients, double omega)
    {
        Complex sum = Complex.Zero;
        for (int k = 0; k < coefficients.Length; k++)
        {
            sum += coefficients[k] * Complex.FromPolarCoordinates(1.0, -omega * k);
        }

        return sum;
    }

    internal static Complex[] PolynomialFromRoots(IReadOnlyList<Complex> roots)
    {
        Complex[] coefficients = [Complex.One];
        foreach (Complex root in roots)
        {
            var next = new Complex[coefficients.Length + 1];
            for (int i = 0; i < coefficients.Length; i++)
            {
                next[i] += coefficients[i];
                next[i + 1] -= coefficients[i] * root;
            }

            coefficients = next;
        }

        return coefficients;
    }

    internal static Complex[] PolynomialFromRootsNumpy(IReadOnlyList<Complex> roots)
    {
        return PolynomialFromRootsOpenBlasDot(roots);
    }

    private static Complex[] PolynomialFromRootsOpenBlasDot(IReadOnlyList<Complex> roots)
    {
        Complex[] coefficients = [Complex.One];
        foreach (Complex root in roots)
        {
            var next = new Complex[coefficients.Length + 1];
            next[0] = coefficients[0];
            Complex negativeRoot = -root;
            for (int i = 1; i < coefficients.Length; i++)
            {
                next[i] = OpenBlasTwoElementDot(
                    coefficients[i - 1],
                    negativeRoot,
                    coefficients[i]);
            }

            next[^1] = OpenBlasTwoElementDot(
                coefficients[^1],
                negativeRoot,
                Complex.Zero);
            coefficients = next;
        }

        return coefficients;
    }

    private static Complex OpenBlasTwoElementDot(Complex left, Complex right, Complex add)
    {
        double realReal = (left.Real * right.Real) + add.Real;
        double imaginaryImaginary = left.Imaginary * right.Imaginary;
        double realImaginary = left.Real * right.Imaginary;
        double imaginaryReal = (left.Imaginary * right.Real) + add.Imaginary;
        return new Complex(
            realReal - imaginaryImaginary,
            realImaginary + imaginaryReal);
    }

    private static double[] PolynomialFromRepeatedRealRoot(double root, int count)
    {
        double[] coefficients = [1.0];
        for (int rootIndex = 0; rootIndex < count; rootIndex++)
        {
            var next = new double[coefficients.Length + 1];
            for (int i = 0; i < coefficients.Length; i++)
            {
                next[i] += coefficients[i];
                next[i + 1] -= coefficients[i] * root;
            }

            coefficients = next;
        }

        return coefficients;
    }

    private static double[] ToRealCoefficients(IReadOnlyList<Complex> coefficients)
    {
        var real = new double[coefficients.Count];
        for (int i = 0; i < coefficients.Count; i++)
        {
            real[i] = coefficients[i].Real;
        }

        return real;
    }

    private static void ValidateNormalizedFrequency(double normalizedFrequency)
    {
        if (normalizedFrequency <= 0.0 || normalizedFrequency >= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedFrequency), "Frequency must be normalized to the Nyquist range (0, 1).");
        }
    }
}
