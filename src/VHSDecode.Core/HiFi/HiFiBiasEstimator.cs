using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

internal readonly record struct HiFiBiasEstimate(
    double LeftCarrierHz,
    double RightCarrierHz);

internal static class HiFiBiasEstimator
{
    private const int MaximumBlocks = 11;

    public static HiFiDecodeOptions Measure(
        HiFiDecodeOptions options,
        IHiFiSampleReader reader,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);
        if (options.InputFile == "-")
        {
            throw new NotSupportedException(
                "HiFi --bias_guess cannot rewind stdin for the subsequent decode.");
        }

        output.WriteLine("Measuring carrier bias ... ");
        HiFiDecodePlan originalPlan = HiFiDecodePlan.FromOptions(options);
        int blockLength = originalPlan.InputRateHz;
        var blocks = new List<float[]>(MaximumBlocks);
        while (blocks.Count < MaximumBlocks)
        {
            var block = new float[blockLength];
            int framesRead = reader.Read(block, cancellationToken);
            if (framesRead == 0)
            {
                break;
            }

            blocks.Add(block);
            if (framesRead < block.Length)
            {
                break;
            }
        }

        if (blocks.Count == 0)
        {
            throw new InvalidDataException("No RF samples were available for HiFi carrier bias measurement.");
        }

        HiFiBiasEstimate estimate = MeasureBlocks(
            options,
            blocks,
            cancellationToken,
            (current, total, currentEstimate) =>
            {
                output.Write(FormatProgress(current, total, currentEstimate));
                output.Write('\r');
            });
        output.WriteLine();
        output.WriteLine("done!");
        double updatedLeft = options.TapeFormat == "vhs"
            ? Math.Clamp(
                estimate.LeftCarrierHz,
                originalPlan.Afe.LeftCarrierHz - 10_000.0,
                originalPlan.Afe.LeftCarrierHz + 10_000.0)
            : estimate.LeftCarrierHz;
        double updatedRight = options.TapeFormat == "vhs"
            ? Math.Clamp(
                estimate.RightCarrierHz,
                originalPlan.Afe.RightCarrierHz - 10_000.0,
                originalPlan.Afe.RightCarrierHz + 10_000.0)
            : estimate.RightCarrierHz;
        return options with
        {
            AfeLeftCarrierHz = updatedLeft,
            AfeRightCarrierHz = updatedRight
        };
    }

    internal static HiFiBiasEstimate MeasureBlocks(
        HiFiDecodeOptions options,
        IReadOnlyList<float[]> blocks,
        CancellationToken cancellationToken = default,
        Action<int, int, HiFiBiasEstimate>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
        {
            throw new ArgumentException("At least one HiFi bias block is required.", nameof(blocks));
        }

        HiFiDecodePlan plan = HiFiDecodePlan.FromOptions(options);
        HiFiAfeParameters afe = plan.Afe;
        double leftSum = 0.0;
        double rightSum = 0.0;
        HiFiBiasEstimate estimate = default;
        using var resampler = new SoxrFloat32Resampler(
            plan.InputRateHz,
            HiFiConstants.HilbertIfRate,
            SoxrQuality.Low);
        var leftFilter = new HiFiAfeFilter(
            plan.IfRateHz,
            afe.LeftCarrierHz,
            afe.LeftNotchWidthHz);
        var rightFilter = new HiFiAfeFilter(
            plan.IfRateHz,
            afe.RightCarrierHz,
            afe.RightNotchWidthHz);
        var leftDiscriminator = new HiFiHilbertDiscriminator(
            HiFiConstants.HilbertIfRate,
            afe.LeftCarrierHz,
            afe.LeftCarrierDeviationHz);
        var rightDiscriminator = new HiFiHilbertDiscriminator(
            HiFiConstants.HilbertIfRate,
            afe.RightCarrierHz,
            afe.RightCarrierDeviationHz);

        for (int i = 0; i < blocks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] resampled = resampler.ProcessFinalBlock(blocks[i]);
            float[] filteredLeft = leftFilter.Apply(resampled);
            float[] filteredRight = rightFilter.Apply(resampled);
            var demodulatedLeft = new float[filteredLeft.Length];
            var demodulatedRight = new float[filteredRight.Length];
            leftDiscriminator.Demodulate(filteredLeft, demodulatedLeft);
            rightDiscriminator.Demodulate(filteredRight, demodulatedRight);
            int count = demodulatedLeft.Length - (HiFiConstants.BlockPreTrimSamples * 2);
            if (count <= 0 || demodulatedRight.Length != demodulatedLeft.Length)
            {
                throw new InvalidDataException("HiFi bias block is too short after demodulation.");
            }

            leftSum += NumpyMeanFloat32(
                demodulatedLeft.AsSpan(HiFiConstants.BlockPreTrimSamples, count));
            rightSum += NumpyMeanFloat32(
                demodulatedRight.AsSpan(HiFiConstants.BlockPreTrimSamples, count));
            estimate = CreateEstimate(
                afe,
                leftSum / (i + 1),
                rightSum / (i + 1));
            progress?.Invoke(i + 1, blocks.Count, estimate);
        }

        return estimate;
    }

    internal static string FormatProgress(
        int current,
        int total,
        HiFiBiasEstimate estimate)
    {
        string label = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Carrier L {estimate.LeftCarrierHz / 1_000_000.0:F6} MHz, "
            + $"R {estimate.RightCarrierHz / 1_000_000.0:F6} MHz");
        return HiFiProgressReporter.FormatProgressBar(current, total, label);
    }

    private static HiFiBiasEstimate CreateEstimate(
        HiFiAfeParameters afe,
        double leftMean,
        double rightMean)
        => new(
            (leftMean * afe.LeftCarrierDeviationHz) + afe.LeftCarrierHz + 1_000_000.0,
            (rightMean * afe.RightCarrierDeviationHz) + afe.RightCarrierHz + 1_000_000.0);

    private static float NumpyMeanFloat32(ReadOnlySpan<float> values)
        => PairwiseSumFloat32(values) / values.Length;

    private static float PairwiseSumFloat32(ReadOnlySpan<float> values)
    {
        const int BlockSize = 128;
        if (values.Length < 8)
        {
            float result = -0.0f;
            for (int i = 0; i < values.Length; i++)
            {
                result += values[i];
            }

            return result;
        }

        if (values.Length <= BlockSize)
        {
            Span<float> accumulators = stackalloc float[8];
            values[..8].CopyTo(accumulators);
            int index = 8;
            int vectorEnd = values.Length - (values.Length % 8);
            for (; index < vectorEnd; index += 8)
            {
                for (int lane = 0; lane < accumulators.Length; lane++)
                {
                    accumulators[lane] += values[index + lane];
                }
            }

            float left = (accumulators[0] + accumulators[1])
                + (accumulators[2] + accumulators[3]);
            float right = (accumulators[4] + accumulators[5])
                + (accumulators[6] + accumulators[7]);
            float result = left + right;
            for (; index < values.Length; index++)
            {
                result += values[index];
            }

            return result;
        }

        int midpoint = values.Length / 2;
        midpoint -= midpoint % 8;
        return PairwiseSumFloat32(values[..midpoint])
            + PairwiseSumFloat32(values[midpoint..]);
    }
}
