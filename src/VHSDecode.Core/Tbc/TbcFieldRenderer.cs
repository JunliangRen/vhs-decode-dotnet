namespace VHSDecode.Core.Tbc;

public sealed record Ire0AdjustOptions(
    bool BackPorch,
    bool HSync,
    int BackPorchStart,
    int BackPorchEnd,
    int Padding = 4);

public sealed record CvbsClampAgcOptions(
    double Speed,
    double GainFactor,
    double SetGain);

public sealed record CvbsAgcStatistics(
    double LowestDetectedGain,
    double HighestDetectedGain,
    double LowestUsedGain,
    double HighestUsedGain);

public sealed record TrackPhaseIre0OffsetOptions(
    int TrackPhase,
    double Offset0Hz,
    double Offset1Hz)
{
    public double GetOffsetHz(int fieldNumber)
    {
        int phase = TrackPhase ^ (fieldNumber & 1);
        return phase == 0 ? Offset0Hz : Offset1Hz;
    }
}

public sealed record TbcRenderedField(
    ushort[] Samples,
    TbcOutputPayload? OutputPayload = null,
    VideoOutputConverter? OutputConverter = null);

public sealed class TbcFieldRenderer
{
    private readonly TbcLineResampler _resampler;
    private readonly VideoOutputConverter _converter;
    private double? _cvbsAgcGain;

    public TbcFieldRenderer(
        TbcFrameSpec frameSpec,
        VideoOutputConverter converter,
        double yCombLimitHz = 0.0,
        Ire0AdjustOptions? ire0Adjust = null,
        CvbsClampAgcOptions? cvbsClampAgc = null,
        bool exportRawTbc = false,
        TrackPhaseIre0OffsetOptions? trackPhaseIre0Offset = null,
        TbcLineInterpolationMethod interpolationMethod = TbcLineInterpolationMethod.Linear,
        double wowLevelAdjustSmoothing = 0.0,
        double? nominalInputLineLength = null)
    {
        FrameSpec = frameSpec;
        _converter = converter;
        _resampler = new TbcLineResampler(
            frameSpec.OutputLineLength,
            interpolationMethod,
            wowLevelAdjustSmoothing,
            nominalInputLineLength);
        YCombLimitHz = yCombLimitHz;
        Ire0Adjust = ire0Adjust;
        CvbsClampAgc = cvbsClampAgc;
        ExportRawTbc = exportRawTbc;
        TrackPhaseIre0Offset = trackPhaseIre0Offset;
        InterpolationMethod = interpolationMethod;
        WowLevelAdjustSmoothing = Math.Max(0.0, wowLevelAdjustSmoothing);
    }

    public TbcFrameSpec FrameSpec { get; }

    public double YCombLimitHz { get; }

    public Ire0AdjustOptions? Ire0Adjust { get; }

    public CvbsClampAgcOptions? CvbsClampAgc { get; }

    public bool ExportRawTbc { get; }

    public TrackPhaseIre0OffsetOptions? TrackPhaseIre0Offset { get; }

    public TbcLineInterpolationMethod InterpolationMethod { get; }

    public double WowLevelAdjustSmoothing { get; }

    public (double SyncLevel, double BlankLevel)? LastCvbsSyncLevels { get; private set; }

    public CvbsAgcStatistics? CvbsAgcStatistics { get; private set; }

    public ushort[] RenderField(
        ReadOnlySpan<double> videoHz,
        IReadOnlyList<double> lineLocations,
        int firstLine = 0,
        int? lineCount = null,
        int fieldNumber = 0,
        VideoOutputConverter? converterOverride = null)
    {
        return RenderFieldPayload(videoHz, lineLocations, firstLine, lineCount, fieldNumber, converterOverride).Samples;
    }

    public double[] ResampleField(
        ReadOnlySpan<double> videoHz,
        IReadOnlyList<double> lineLocations,
        int firstLine = 0,
        int? lineCount = null)
    {
        int lines = lineCount ?? FrameSpec.OutputLineCount;
        if (lines != FrameSpec.OutputLineCount)
        {
            throw new ArgumentException("Rendered line count must match the configured TBC field height.", nameof(lineCount));
        }

        return _resampler.ResampleLines(videoHz, lineLocations, firstLine, lines);
    }

    public TbcRenderedField RenderFieldPayload(
        ReadOnlySpan<double> videoHz,
        IReadOnlyList<double> lineLocations,
        int firstLine = 0,
        int? lineCount = null,
        int fieldNumber = 0,
        VideoOutputConverter? converterOverride = null)
        => RenderFieldPayloadCore(
            videoHz,
            lineLocations,
            firstLine,
            lineCount,
            fieldNumber,
            converterOverride,
            converterProvider: null);

    internal TbcRenderedField RenderFieldPayloadWithConverterProvider(
        ReadOnlySpan<double> videoHz,
        IReadOnlyList<double> lineLocations,
        int firstLine,
        int? lineCount,
        int fieldNumber,
        VideoOutputConverter? converterFallback,
        Func<VideoOutputConverter?> converterProvider)
    {
        ArgumentNullException.ThrowIfNull(converterProvider);
        return RenderFieldPayloadCore(
            videoHz,
            lineLocations,
            firstLine,
            lineCount,
            fieldNumber,
            converterFallback,
            converterProvider);
    }

    private TbcRenderedField RenderFieldPayloadCore(
        ReadOnlySpan<double> videoHz,
        IReadOnlyList<double> lineLocations,
        int firstLine,
        int? lineCount,
        int fieldNumber,
        VideoOutputConverter? converterOverride,
        Func<VideoOutputConverter?>? converterProvider)
    {
        double[] resampled = ResampleField(videoHz, lineLocations, firstLine, lineCount);
        if (YCombLimitHz != 0.0)
        {
            ApplyYCombInPlace(resampled, FrameSpec.OutputLineLength, YCombLimitHz);
        }

        TbcOutputPayload? rawPayload = ExportRawTbc
            ? new TbcOutputPayload(
                TbcOutputWriter.ToLittleEndianFloat32Bytes(resampled),
                TbcOutputSampleFormat.Float32)
            : null;

        if (CvbsClampAgc is not null)
        {
            return new TbcRenderedField(ConvertCvbsClampAgc(resampled, CvbsClampAgc), rawPayload);
        }

        VideoOutputConverter activeConverter = converterProvider?.Invoke()
            ?? converterOverride
            ?? _converter;
        return new TbcRenderedField(
            BuildFieldConverter(resampled, fieldNumber, activeConverter).ConvertHz(resampled),
            rawPayload,
            activeConverter);
    }

    public static void ApplyYCombInPlace(double[] data, int lineLength, double limit)
    {
        if (lineLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineLength));
        }

        if (data.Length % lineLength != 0)
        {
            throw new ArgumentException("Data length must be an exact multiple of line length.", nameof(data));
        }

        if (limit < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (data.Length == 0 || limit == 0.0)
        {
            return;
        }

        double[] original = data.ToArray();
        float floatLimit = (float)limit;
        for (int i = 0; i < data.Length; i++)
        {
            float current = (float)original[i];
            float diffBackward = current - (float)original[(i + lineLength) % data.Length];
            int previous = (i - lineLength) % data.Length;
            if (previous < 0)
            {
                previous += data.Length;
            }

            float diffForward = current - (float)original[previous];
            float combined = diffBackward + diffForward;
            float adjustment = Math.Clamp(combined, -floatLimit, floatLimit) / 2.0f;
            data[i] = current - adjustment;
        }
    }

    public byte[] RenderFieldBytes(
        ReadOnlySpan<double> videoHz,
        IReadOnlyList<double> lineLocations,
        int firstLine = 0,
        int? lineCount = null,
        int fieldNumber = 0,
        VideoOutputConverter? converterOverride = null)
    {
        TbcRenderedField field = RenderFieldPayload(videoHz, lineLocations, firstLine, lineCount, fieldNumber, converterOverride);
        return field.OutputPayload?.Bytes ?? TbcOutputWriter.ToLittleEndianBytes(field.Samples);
    }

    private VideoOutputConverter BuildFieldConverter(
        double[] resampled,
        int fieldNumber,
        VideoOutputConverter? converterOverride)
    {
        VideoOutputConverter baseConverter = converterOverride ?? _converter;
        if (Ire0Adjust is null && TrackPhaseIre0Offset is null)
        {
            return baseConverter;
        }

        double ire0 = baseConverter.Ire0;
        double hzIre = baseConverter.HzIre;
        if (Ire0Adjust is not null && resampled.Length == FrameSpec.FieldSampleCount)
        {
            int backPorchStart = Ire0Adjust.BackPorchStart + Ire0Adjust.Padding;
            int backPorchEnd = Ire0Adjust.BackPorchEnd - Ire0Adjust.Padding;
            if (Ire0Adjust.BackPorch && IsValidLineRange(backPorchStart, backPorchEnd))
            {
                ire0 = MeanMiddleThirdFloat32(LineMediansFloat32(resampled, backPorchStart, backPorchEnd));
            }

            int hsyncStart = Ire0Adjust.Padding;
            int hsyncEnd = Ire0Adjust.BackPorchStart - Ire0Adjust.Padding;
            if (Ire0Adjust.HSync && IsValidLineRange(hsyncStart, hsyncEnd))
            {
                double hsyncLevel = MeanMiddleThirdFloat32(LineMediansFloat32(resampled, hsyncStart, hsyncEnd));
                double measuredHzIre = (float)(
                    ((float)ire0 - (float)hsyncLevel) / (float)-baseConverter.VSyncIre);
                if (double.IsFinite(measuredHzIre) && measuredHzIre != 0.0)
                {
                    hzIre = measuredHzIre;
                }
            }
        }

        if (TrackPhaseIre0Offset is not null)
        {
            ire0 += TrackPhaseIre0Offset.GetOffsetHz(fieldNumber);
        }

        return new VideoOutputConverter(
            ire0,
            hzIre,
            baseConverter.OutputZero,
            baseConverter.VSyncIre,
            baseConverter.OutputScale);
    }

    private ushort[] ConvertCvbsClampAgc(double[] input, CvbsClampAgcOptions options)
    {
        if (FrameSpec.OutputLineLength < 164 || FrameSpec.OutputLineCount < 12)
        {
            return _converter.ConvertHz(input);
        }

        double[] blankLevels = LineMedians(input, 96, 164);
        double[] syncLevels = LineMedians(input, 12, 72);
        int measuredStart = (FrameSpec.OutputLineCount / 3) * 2;
        LastCvbsSyncLevels = (
            Median(syncLevels, measuredStart, FrameSpec.OutputLineCount),
            Median(blankLevels, measuredStart, FrameSpec.OutputLineCount));
        double[] reduced = input.ToArray();
        int lineLength = FrameSpec.OutputLineLength;
        int lineCount = FrameSpec.OutputLineCount;

        SubtractRange(reduced, 0, Math.Min(reduced.Length, (6 * lineLength) + 130), Median(blankLevels, 7, 12));
        for (int line = 7; line < lineCount - 5; line++)
        {
            int start = (line * lineLength) + 130;
            int end = Math.Min(reduced.Length, ((line + 1) * lineLength) + 130);
            double from = Median(blankLevels, line - 2, line + 3);
            double to = Median(blankLevels, line - 1, line + 4);
            SubtractLinear(reduced, start, end, from, to);
        }

        int tailStart = Math.Min(reduced.Length, ((lineCount - 5) * lineLength) + 130);
        SubtractRange(reduced, tailStart, reduced.Length, Median(blankLevels, lineCount - 9, lineCount - 5));

        double gain = options.SetGain != 0.0
            ? options.SetGain
            : ComputeCvbsAgcGain(blankLevels, syncLevels, options.Speed);
        double gainScale = gain * options.GainFactor;
        if (!double.IsFinite(gainScale) || gainScale == 0.0)
        {
            return _converter.ConvertHz(input);
        }

        var output = new ushort[reduced.Length];
        for (int i = 0; i < reduced.Length; i++)
        {
            double value = (reduced[i] / gainScale) - _converter.VSyncIre;
            output[i] = ClipToUInt16((value * _converter.OutputScale) + _converter.OutputZero);
        }

        return output;
    }

    private double ComputeCvbsAgcGain(double[] blankLevels, double[] syncLevels, double speed)
    {
        int start = 7;
        int end = FrameSpec.OutputLineCount - 5;
        var vsyncs = new double[Math.Max(0, end - start)];
        for (int i = 0; i < vsyncs.Length; i++)
        {
            int line = start + i;
            vsyncs[i] = blankLevels[line] - syncLevels[line];
        }

        Array.Sort(vsyncs);
        double measured = Mean(vsyncs, vsyncs.Length / 4, (vsyncs.Length * 3) / 4) / -_converter.VSyncIre;
        if (!_cvbsAgcGain.HasValue)
        {
            _cvbsAgcGain = measured;
            CvbsAgcStatistics = new CvbsAgcStatistics(measured, measured, measured, measured);
        }
        else
        {
            _cvbsAgcGain = (measured * speed) + (_cvbsAgcGain.Value * (1.0 - speed));
            CvbsAgcStatistics current = CvbsAgcStatistics
                ?? new CvbsAgcStatistics(measured, measured, _cvbsAgcGain.Value, _cvbsAgcGain.Value);
            CvbsAgcStatistics = current with
            {
                LowestDetectedGain = Math.Min(current.LowestDetectedGain, measured),
                HighestDetectedGain = Math.Max(current.HighestDetectedGain, measured),
                LowestUsedGain = Math.Min(current.LowestUsedGain, _cvbsAgcGain.Value),
                HighestUsedGain = Math.Max(current.HighestUsedGain, _cvbsAgcGain.Value)
            };
        }

        return _cvbsAgcGain.Value;
    }

    private bool IsValidLineRange(int start, int end)
    {
        return start >= 0 && end > start && end <= FrameSpec.OutputLineLength;
    }

    private double[] LineMedians(double[] data, int start, int end, bool sortOutput = false)
    {
        var medians = new double[FrameSpec.OutputLineCount];
        int width = end - start;
        var scratch = new double[width];
        for (int line = 0; line < FrameSpec.OutputLineCount; line++)
        {
            Array.Copy(data, (line * FrameSpec.OutputLineLength) + start, scratch, 0, width);
            Array.Sort(scratch);
            medians[line] = MedianSorted(scratch);
        }

        if (sortOutput)
        {
            Array.Sort(medians);
        }

        return medians;
    }

    private float[] LineMediansFloat32(double[] data, int start, int end)
    {
        var medians = new float[FrameSpec.OutputLineCount];
        int width = end - start;
        var scratch = new float[width];
        for (int line = 0; line < FrameSpec.OutputLineCount; line++)
        {
            int lineStart = (line * FrameSpec.OutputLineLength) + start;
            for (int i = 0; i < width; i++)
            {
                scratch[i] = (float)data[lineStart + i];
            }

            Array.Sort(scratch);
            int middle = scratch.Length / 2;
            medians[line] = scratch.Length % 2 == 0
                ? (scratch[middle - 1] + scratch[middle]) / 2.0f
                : scratch[middle];
        }

        Array.Sort(medians);
        return medians;
    }

    private static float MeanMiddleThirdFloat32(float[] sortedValues)
    {
        int start = sortedValues.Length / 3;
        int end = (sortedValues.Length * 2) / 3;
        if (end <= start)
        {
            start = 0;
            end = sortedValues.Length;
        }

        return PairwiseSumFloat32(sortedValues.AsSpan(start, end - start)) / (end - start);
    }

    private static float PairwiseSumFloat32(ReadOnlySpan<float> values)
    {
        const int blockSize = 128;
        if (values.Length < 8)
        {
            float result = -0.0f;
            for (int i = 0; i < values.Length; i++)
            {
                result += values[i];
            }

            return result;
        }

        if (values.Length <= blockSize)
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

    private static double Mean(double[] sortedValues, int start, int end)
    {
        if (end <= start)
        {
            start = 0;
            end = sortedValues.Length;
        }

        double sum = 0.0;
        for (int i = start; i < end; i++)
        {
            sum += sortedValues[i];
        }

        return sum / (end - start);
    }

    private static double Median(double[] values, int start, int end)
    {
        start = Math.Clamp(start, 0, values.Length);
        end = Math.Clamp(end, start, values.Length);
        if (end <= start)
        {
            return 0.0;
        }

        double[] scratch = values[start..end];
        Array.Sort(scratch);
        return MedianSorted(scratch);
    }

    private static void SubtractRange(double[] data, int start, int end, double value)
    {
        for (int i = start; i < end; i++)
        {
            data[i] -= value;
        }
    }

    private static void SubtractLinear(double[] data, int start, int end, double from, double to)
    {
        int count = end - start;
        if (count <= 0)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            double t = count == 1 ? 0.0 : (double)i / (count - 1);
            data[start + i] -= from + ((to - from) * t);
        }
    }

    private static ushort ClipToUInt16(double value)
    {
        double rounded = value + 0.5;
        if (rounded <= 0.0)
        {
            return 0;
        }

        if (rounded >= ushort.MaxValue)
        {
            return ushort.MaxValue;
        }

        return (ushort)rounded;
    }

    private static double MedianSorted(double[] sortedValues)
    {
        int mid = sortedValues.Length / 2;
        return sortedValues.Length % 2 == 0
            ? (sortedValues[mid - 1] + sortedValues[mid]) / 2.0
            : sortedValues[mid];
    }
}
