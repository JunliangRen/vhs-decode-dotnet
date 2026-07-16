using System.Numerics;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

public readonly record struct HiFiRateRatio(BigInteger Numerator, BigInteger Denominator)
{
    public static HiFiRateRatio FromDouble(double value)
    {
        (BigInteger numerator, BigInteger denominator) = SoxrQuickResampler.ExactRatio(value);
        return new HiFiRateRatio(numerator, denominator);
    }
}

public sealed record HiFiResamplingRatios(
    HiFiRateRatio InputToIf,
    HiFiRateRatio IfToAudio,
    HiFiRateRatio AudioToFinal);

public sealed record HiFiResamplerConverters(
    string InputToIf,
    string IfToAudio,
    string AudioToFinal);

public sealed record HiFiBlockSizes(
    double BlocksPerSecondRatio,
    int InputSamples,
    int IfSamples,
    int AudioSamples,
    int FinalAudioSamples);

public sealed record HiFiBlockOverlap(
    int ReadSamples,
    int InputSamples,
    int AudioSamples,
    int FinalAudioSamples,
    bool HasRateSyncWarning);

public sealed record HiFiDecodePlan
{
    public required int InputRateHz { get; init; }
    public required int IfRateHz { get; init; }
    public required int AudioRateHz { get; init; }
    public required int FinalAudioRateHz { get; init; }
    public required HiFiAfeParameters Afe { get; init; }
    public required HiFiResamplingRatios ResamplingRatios { get; init; }
    public required HiFiResamplerConverters ResamplerConverters { get; init; }
    public required HiFiBlockSizes InitialBlockSizes { get; init; }
    public required HiFiBlockOverlap BlockOverlap { get; init; }
    public int PreTrimSamples => HiFiConstants.BlockPreTrimSamples;

    public static HiFiDecodePlan FromOptions(HiFiDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = HiFiDecodePreflight.Build(options);

        int inputRateHz = PythonIntRate(options.InputRateHz, nameof(options.InputRateHz));
        int ifRateHz = options.DemodType switch
        {
            HiFiConstants.DemodHilbert => HiFiConstants.HilbertIfRate,
            HiFiConstants.DemodQuadrature => inputRateHz,
            _ => throw new ArgumentException($"Unsupported HiFi demodulator: {options.DemodType}.", nameof(options))
        };
        int audioRateHz = HiFiConstants.IntermediateAudioRate;
        int finalAudioRateHz = options.AudioRateHz;

        var ratios = new HiFiResamplingRatios(
            HiFiRateRatio.FromDouble((double)ifRateHz / inputRateHz),
            HiFiRateRatio.FromDouble((double)audioRateHz / ifRateHz),
            HiFiRateRatio.FromDouble((double)finalAudioRateHz / audioRateHz));
        HiFiResamplerConverters converters = GetConverters(options.ResamplerQuality);
        HiFiBlockSizes initialBlockSizes = CalculateBlockSizes(
            inputRateHz,
            ifRateHz,
            audioRateHz,
            finalAudioRateHz,
            null);
        HiFiBlockOverlap blockOverlap = CalculateBlockOverlap(
            inputRateHz,
            audioRateHz,
            finalAudioRateHz,
            initialBlockSizes);

        return new HiFiDecodePlan
        {
            InputRateHz = inputRateHz,
            IfRateHz = ifRateHz,
            AudioRateHz = audioRateHz,
            FinalAudioRateHz = finalAudioRateHz,
            Afe = HiFiAfeParameters.FromOptions(options),
            ResamplingRatios = ratios,
            ResamplerConverters = converters,
            InitialBlockSizes = initialBlockSizes,
            BlockOverlap = blockOverlap
        };
    }

    public HiFiBlockSizes CalculateBlockSizes(int? inputSamples = null)
        => CalculateBlockSizes(InputRateHz, IfRateHz, AudioRateHz, FinalAudioRateHz, inputSamples);

    public int CalculateFinalAudioLength(int blockFramesRead, bool isLastBlock, int? inputSamples = null)
    {
        if (blockFramesRead < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockFramesRead));
        }

        HiFiBlockSizes sizes = CalculateBlockSizes(inputSamples);
        if (!isLastBlock)
        {
            return Math.Max(50, sizes.FinalAudioSamples - (BlockOverlap.FinalAudioSamples * 2));
        }

        double inputToFinalRatio = (double)sizes.InputSamples / sizes.FinalAudioSamples;
        int audioSamples = PythonRoundToInt(blockFramesRead / inputToFinalRatio);
        return Math.Max(50, checked(audioSamples + BlockOverlap.FinalAudioSamples));
    }

    private static HiFiBlockSizes CalculateBlockSizes(
        int inputRateHz,
        int ifRateHz,
        int audioRateHz,
        int finalAudioRateHz,
        int? inputSamples)
    {
        double blockRatio;
        int blockSize;
        if (inputSamples.HasValue)
        {
            if (inputSamples.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(inputSamples));
            }

            blockSize = inputSamples.Value;
            blockRatio = (double)blockSize / inputRateHz;
        }
        else
        {
            blockRatio = 1.0 / HiFiConstants.BlocksPerSecond;
            blockSize = CeilingToInt(inputRateHz * blockRatio);
        }

        return new HiFiBlockSizes(
            blockRatio,
            blockSize,
            CeilingToInt(ifRateHz * blockRatio),
            CeilingToInt(audioRateHz * blockRatio),
            CeilingToInt(finalAudioRateHz * blockRatio));
    }

    private static HiFiBlockOverlap CalculateBlockOverlap(
        int inputRateHz,
        int audioRateHz,
        int finalAudioRateHz,
        HiFiBlockSizes initialBlockSizes)
    {
        int blockSizeGcd = GreatestCommonDivisor(
            initialBlockSizes.InputSamples,
            initialBlockSizes.FinalAudioSamples);
        bool hasRateSyncWarning = blockSizeGcd <= 5;
        int audioOverlapDivisor = hasRateSyncWarning
            ? 1
            : initialBlockSizes.AudioSamples / blockSizeGcd;
        if (audioOverlapDivisor <= 0)
        {
            throw new InvalidOperationException("HiFi block overlap divisor is zero.");
        }

        int minimumResamplerOverlap = HiFiConstants.BlockPreTrimSamples
            + HiFiConstants.MinimumResamplerOverlapPadding;
        int minimumFinalOverlap = CeilingToInt(
            (double)minimumResamplerOverlap / audioRateHz * finalAudioRateHz);
        int finalAudioOverlap = checked(
            CeilingToInt((double)minimumFinalOverlap / audioOverlapDivisor)
            * audioOverlapDivisor);
        double overlapSeconds = (double)finalAudioOverlap / finalAudioRateHz;
        int inputOverlap = PythonRoundToInt(inputRateHz * overlapSeconds);
        int audioOverlap = CeilingToInt(inputRateHz * overlapSeconds);
        int readOverlap = checked(inputOverlap * 2);
        finalAudioOverlap = PythonRoundToInt(finalAudioRateHz * overlapSeconds);

        return new HiFiBlockOverlap(
            readOverlap,
            inputOverlap,
            audioOverlap,
            finalAudioOverlap,
            hasRateSyncWarning);
    }

    private static HiFiResamplerConverters GetConverters(string quality)
        => quality switch
        {
            "high" => new HiFiResamplerConverters("VHQ", "VHQ", "VHQ"),
            "medium" => new HiFiResamplerConverters("LQ", "MQ", "HQ"),
            _ => new HiFiResamplerConverters("LQ", "LQ", "LQ")
        };

    private static int PythonIntRate(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0.0 || value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return checked((int)Math.Truncate(value));
    }

    private static int CeilingToInt(double value)
        => checked((int)Math.Ceiling(value));

    private static int PythonRoundToInt(double value)
        => checked((int)Math.Round(value, MidpointRounding.ToEven));

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }
}
