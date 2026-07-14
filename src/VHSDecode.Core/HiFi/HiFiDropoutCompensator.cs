using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

internal readonly record struct HiFiSampleRange(int Start, int End);

internal readonly record struct HiFiDropoutAction(
    HiFiSampleRange? FillRange,
    HiFiSampleRange? MuteRange);

internal static class HiFiDropoutCompensator
{
    internal const int WindowSize = 128;
    internal const int WindowHopSize = 128;
    internal const int FftStart = 26;
    internal const int FftEnd = 63;
    internal const int FadeSamples = 128;
    internal const int DcOffsetSamples = 256;
    private const float AmplitudeThreshold = 1.0f;
    private const float StandardDeviationThreshold = 1.0f;
    private const float MuteEpsilon = 0.0009765625f;

    internal static List<HiFiSampleRange> Detect(ReadOnlySpan<float> audio)
    {
        var ranges = new List<HiFiSampleRange>();
        int completedRangeCount = 0;

        for (int start = 0; start < audio.Length - WindowSize; start += WindowHopSize)
        {
            int end = start + WindowSize;
            (float mean, float standardDeviation) =
                AnalyzeWindow(audio.Slice(start, WindowSize));
            bool hasBroadbandNoise = mean > AmplitudeThreshold
                && standardDeviation > StandardDeviationThreshold;
            if (hasBroadbandNoise)
            {
                if (ranges.Count == completedRangeCount)
                {
                    ranges.Add(new HiFiSampleRange(start, audio.Length));
                }
            }
            else if (ranges.Count > completedRangeCount)
            {
                ranges[completedRangeCount] = ranges[completedRangeCount] with { End = end };
                completedRangeCount++;
            }
        }

        return MergeRanges(ranges);
    }

    internal static (float Mean, float StandardDeviation) AnalyzeWindow(
        ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSize)
        {
            throw new ArgumentException($"DOC analysis windows must contain {WindowSize} samples.", nameof(window));
        }

        float[] magnitude = AnalyzeWindowMagnitude(window);
        return HiFiAudioProcessing.NumbaFastMeanStandardDeviation(magnitude);
    }

    internal static float[] AnalyzeWindowMagnitude(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSize)
        {
            throw new ArgumentException($"DOC analysis windows must contain {WindowSize} samples.", nameof(window));
        }

        Complex32[] spectrum = NumpyComplex64Fft.ForwardReal(window);
        var magnitude = new float[FftEnd - FftStart];
        for (int i = FftStart; i < FftEnd; i++)
        {
            Complex32 value = spectrum[i];
            magnitude[i - FftStart] = NumpyHypot(value.Real, value.Imaginary);
        }

        return magnitude;
    }

    internal static List<HiFiDropoutAction> CheckOtherChannel(
        IReadOnlyList<HiFiSampleRange> gapsToFill,
        IReadOnlyList<HiFiSampleRange> sourceGaps)
    {
        var result = new List<HiFiDropoutAction>();
        foreach (HiFiSampleRange gap in gapsToFill)
        {
            int start = gap.Start;
            bool overlapFound = false;
            foreach (HiFiSampleRange sourceGap in sourceGaps)
            {
                if (gap.End <= sourceGap.Start || start >= sourceGap.End)
                {
                    continue;
                }

                overlapFound = true;
                if (start < sourceGap.Start)
                {
                    result.Add(new HiFiDropoutAction(
                        new HiFiSampleRange(start, sourceGap.Start),
                        null));
                }

                int overlapStart = Math.Max(start, sourceGap.Start);
                int overlapEnd = Math.Min(gap.End, sourceGap.End);
                result.Add(new HiFiDropoutAction(
                    null,
                    new HiFiSampleRange(overlapStart, overlapEnd)));
                start = overlapEnd;
            }

            if (!overlapFound || start < gap.End)
            {
                result.Add(new HiFiDropoutAction(
                    new HiFiSampleRange(start, gap.End),
                    null));
            }
        }

        return result;
    }

    internal static void Fill(
        int start,
        int end,
        Span<float> outer,
        ReadOnlySpan<float> inner,
        bool mute)
    {
        if (start < 0 || end < start || end > outer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (!mute && inner.Length != outer.Length)
        {
            throw new ArgumentException("DOC source and destination channels must have equal lengths.", nameof(inner));
        }

        int fadeStart = Math.Max(0, start - FadeSamples);
        int fadeStartDuration = start - fadeStart;
        int dcBeforeStart = Math.Max(0, fadeStart - DcOffsetSamples);
        float dcBefore = dcBeforeStart < start
            ? HiFiAudioProcessing.NumbaFastMean(outer[dcBeforeStart..start])
            : 0.0f;

        int fadeEnd = Math.Min(outer.Length, end + FadeSamples);
        int fadeEndDuration = fadeEnd - end;
        int dcAfterEnd = Math.Min(outer.Length, fadeEnd + DcOffsetSamples);
        float dcAfter = end < dcAfterEnd
            ? HiFiAudioProcessing.NumbaFastMean(outer[end..dcAfterEnd])
            : 0.0f;
        float dcInner = mute
            ? 0.0f
            : HiFiAudioProcessing.NumbaFastMean(inner[fadeStart..fadeEnd]);

        int dcLength = fadeEnd - fadeStart;
        float[] interpolatedDc = InterpolateDc(dcBefore, dcAfter, dcLength);

        for (int i = 0; i < fadeStartDuration; i++)
        {
            int index = fadeStart + i;
            int dcIndex = index - fadeStart;
            double fadeIn = (double)i / fadeStartDuration;
            double outerSample = outer[index];
            double adjustedInner = mute
                ? MuteEpsilon
                : inner[index] - (double)dcInner + interpolatedDc[dcIndex];
            double difference = adjustedInner - outerSample;
            outer[index] = (float)Math.FusedMultiplyAdd(
                fadeIn,
                difference,
                outerSample);
        }

        for (int i = start; i < end; i++)
        {
            double innerSample = mute ? MuteEpsilon : inner[i];
            outer[i] = (float)(innerSample - dcInner + interpolatedDc[i - fadeStart]);
        }

        for (int i = 0; i < fadeEndDuration; i++)
        {
            int index = end + i;
            int dcIndex = index - fadeStart;
            double fadeIn = (double)(i + 1) / fadeEndDuration;
            double outerSample = outer[index];
            double adjustedInner = mute
                ? MuteEpsilon
                : inner[index] - (double)dcInner + interpolatedDc[dcIndex];
            outer[index] = (float)Math.FusedMultiplyAdd(
                fadeIn,
                outerSample - adjustedInner,
                adjustedInner);
        }
    }

    internal static float[] InterpolateDc(float before, float after, int length)
    {
        if (length <= 0)
        {
            return [];
        }

        var output = new float[length];
        if (length == 1)
        {
            output[0] = before;
            return output;
        }

        double inverseDenominator = 1.0 / (length - 1);
        double delta = (double)after - before;
        for (int i = 0; i < length; i++)
        {
            double angle = (i * Math.PI) * inverseDenominator;
            double smooth = 0.5 - (PlatformCos(angle) * 0.5);
            output[i] = (float)Math.FusedMultiplyAdd(delta, smooth, before);
        }

        return output;
    }

    internal static void Compensate(
        float[]? left,
        float[]? right,
        string decodeMode,
        string compensationMode)
    {
        if (compensationMode == HiFiConstants.DropoutCompensationDisabled)
        {
            return;
        }

        if (compensationMode is not HiFiConstants.DropoutCompensationFull
            and not HiFiConstants.DropoutCompensationMute)
        {
            throw new ArgumentException(
                $"Unsupported HiFi dropout compensation mode: {compensationMode}.",
                nameof(compensationMode));
        }

        bool decodesLeft = decodeMode != HiFiConstants.AudioModeMonoRight;
        bool decodesRight = decodeMode != HiFiConstants.AudioModeMonoLeft;
        if (decodesLeft && left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (decodesRight && right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        if (left is not null && right is not null && left.Length != right.Length)
        {
            throw new ArgumentException("DOC channel lengths must match.");
        }

        List<HiFiSampleRange> leftBoundaries = decodesLeft
            ? Detect(left!)
            : [new HiFiSampleRange(0, right!.Length)];
        List<HiFiSampleRange> rightBoundaries = decodesRight
            ? Detect(right!)
            : [new HiFiSampleRange(0, left!.Length)];
        bool dualMono = decodeMode is HiFiConstants.AudioModeDualMono
            or HiFiConstants.AudioModeDualMonoMidSide;

        List<HiFiDropoutAction> leftActions;
        List<HiFiDropoutAction> rightActions;
        if (dualMono || compensationMode == HiFiConstants.DropoutCompensationMute)
        {
            leftActions = MuteActions(leftBoundaries);
            rightActions = MuteActions(rightBoundaries);
        }
        else
        {
            leftActions = decodesLeft
                ? CheckOtherChannel(leftBoundaries, rightBoundaries)
                : MuteActions(leftBoundaries);
            rightActions = decodesRight
                ? CheckOtherChannel(rightBoundaries, leftBoundaries)
                : MuteActions(rightBoundaries);
        }

        if (decodesLeft)
        {
            ApplyActions(left!, right, leftActions);
        }

        if (decodesRight)
        {
            ApplyActions(right!, left, rightActions);
        }
    }

    private static void ApplyActions(
        float[] current,
        float[]? other,
        IReadOnlyList<HiFiDropoutAction> actions)
    {
        foreach (HiFiDropoutAction action in actions)
        {
            if (action.FillRange is HiFiSampleRange fill)
            {
                Fill(fill.Start, fill.End, current, other!, mute: false);
            }

            if (action.MuteRange is HiFiSampleRange mute)
            {
                Fill(mute.Start, mute.End, current, [], mute: true);
            }
        }
    }

    private static List<HiFiDropoutAction> MuteActions(IEnumerable<HiFiSampleRange> ranges)
        => ranges
            .Select(range => new HiFiDropoutAction(null, range))
            .ToList();

    private static List<HiFiSampleRange> MergeRanges(IEnumerable<HiFiSampleRange> ranges)
    {
        var merged = new List<HiFiSampleRange>();
        foreach (HiFiSampleRange range in ranges.OrderBy(value => value.Start))
        {
            if (merged.Count == 0 || merged[^1].End < range.Start)
            {
                merged.Add(range);
            }
            else
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
            }
        }

        return merged;
    }

    // Modified NumPy SIMD complex-absolute adaptation; see THIRD-PARTY-NOTICES.md.
    private static float NumpyHypot(float left, float right)
    {
        float absoluteLeft = MathF.Abs(left);
        float absoluteRight = MathF.Abs(right);
        if (float.IsInfinity(absoluteLeft) || float.IsInfinity(absoluteRight))
        {
            return float.PositiveInfinity;
        }

        if (float.IsNaN(absoluteLeft) || float.IsNaN(absoluteRight))
        {
            return float.NaN;
        }

        float larger = MathF.Max(absoluteLeft, absoluteRight);
        if (larger == 0.0f)
        {
            return 0.0f;
        }

        float smaller = MathF.Min(absoluteLeft, absoluteRight);
        float ratio = smaller / larger;
        float scaled = MathF.Sqrt(MathF.FusedMultiplyAdd(ratio, ratio, 1.0f));
        return scaled * larger;
    }

    private static double PlatformCos(double value)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsCos(value);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxCos(value);
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsCos(value);
        }

        return Math.Cos(value);
    }

    [DllImport("ucrtbase.dll", EntryPoint = "cos", ExactSpelling = true)]
    private static extern double WindowsCos(double value);

    [DllImport("libm.so.6", EntryPoint = "cos", ExactSpelling = true)]
    private static extern double LinuxCos(double value);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "cos", ExactSpelling = true)]
    private static extern double MacOsCos(double value);
}
