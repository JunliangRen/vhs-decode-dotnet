using System.Numerics;
using System.Text.Json;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Dsp;

public readonly record struct DecodeDelayEstimates(
    int RfHighPassOffset,
    double VideoWhiteOffset,
    double VideoSyncOffset);

public static class DecodeDelayEstimator
{
    private const int VideoWhiteProbeSample = 3000;
    private const int VideoSyncProbeSample = 1500;
    private const int VideoRotProbeSample = 6000;
    private const int ZeroCrossingSearchSamples = 512;

    public static int EstimateRfHighPassOffset(
        FormatParameterSet parameters,
        DecodeFilterSet filters,
        double sampleRateHz,
        int blockLength)
    {
        return EstimateLaserDiscDelays(parameters, filters, sampleRateHz, blockLength).RfHighPassOffset;
    }

    public static double EstimateVideoWhiteOffset(
        FormatParameterSet parameters,
        DecodeFilterSet filters,
        double sampleRateHz,
        int blockLength)
    {
        return EstimateLaserDiscDelays(parameters, filters, sampleRateHz, blockLength).VideoWhiteOffset;
    }

    public static double EstimateVideoSyncOffset(
        FormatParameterSet parameters,
        DecodeFilterSet filters,
        double sampleRateHz,
        int blockLength)
    {
        return EstimateLaserDiscDelays(parameters, filters, sampleRateHz, blockLength).VideoSyncOffset;
    }

    public static DecodeDelayEstimates EstimateLaserDiscDelays(
        FormatParameterSet parameters,
        DecodeFilterSet filters,
        double sampleRateHz,
        int blockLength)
    {
        if (parameters.TapeFormat != "LD"
            || sampleRateHz <= 0
            || blockLength <= VideoRotProbeSample + ZeroCrossingSearchSamples + 1)
        {
            return default;
        }

        try
        {
            VideoOutputConverter converter = VideoOutputConverter.FromParameters(parameters);
            double[] fakeSignal = BuildFakeRfSignal(parameters, filters, converter, sampleRateHz, blockLength);
            for (int i = VideoRotProbeSample; i < Math.Min(fakeSignal.Length, VideoRotProbeSample + 5); i++)
            {
                fakeSignal[i] = 0.0;
            }

            RfDemodulatedBlock decoded = DemodulateFakeSignal(fakeSignal, filters, sampleRateHz);
            double[] video = QuantizeToFloat32(decoded.Video);
            double? syncCrossing = PulseDetection.CalculateZeroCrossing(
                video,
                VideoSyncProbeSample,
                converter.IreToHz(converter.VSyncIre / 2.0),
                count: ZeroCrossingSearchSamples);
            double? whiteCrossing = PulseDetection.CalculateZeroCrossing(
                video,
                VideoWhiteProbeSample,
                converter.IreToHz(50.0),
                count: ZeroCrossingSearchSamples);
            double? rotationCrossing = PulseDetection.CalculateZeroCrossing(
                video,
                VideoRotProbeSample,
                converter.IreToHz(-10.0),
                count: ZeroCrossingSearchSamples);

            return new DecodeDelayEstimates(
                RfHighPassOffset: IsFinite(rotationCrossing)
                    ? (int)Math.Round(rotationCrossing!.Value - VideoRotProbeSample, MidpointRounding.ToEven)
                    : 0,
                VideoWhiteOffset: IsFinite(whiteCrossing)
                    ? whiteCrossing!.Value - VideoWhiteProbeSample
                    : 0.0,
                VideoSyncOffset: IsFinite(syncCrossing)
                    ? syncCrossing!.Value - VideoSyncProbeSample
                    : 0.0);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static bool IsFinite(double? value) => value.HasValue && double.IsFinite(value.Value);

    private static double[] BuildFakeRfSignal(
        FormatParameterSet parameters,
        DecodeFilterSet filters,
        VideoOutputConverter converter,
        double sampleRateHz,
        int blockLength)
    {
        double[] fakeVideo = BuildFakeVideo(parameters, converter, sampleRateHz, blockLength);
        double[] fmInput = PrepareFakeVideoForFm(parameters, filters, fakeVideo, sampleRateHz);
        double[] fakeSignal = GenerateFmWave(fmInput, sampleRateHz);
        for (int i = 0; i < fakeSignal.Length; i++)
        {
            fakeSignal[i] = (fakeSignal[i] * 4096.0) + 8192.0;
        }

        return fakeSignal;
    }

    private static RfDemodulatedBlock DemodulateFakeSignal(
        ReadOnlySpan<double> fakeSignal,
        DecodeFilterSet filters,
        double sampleRateHz)
    {
        var demodulator = new RfDemodulator(sampleRateHz);
        return demodulator.Demodulate(
            fakeSignal,
            filters.RfVideo,
            filters.RfHighPass,
            ReadOnlySpan<Complex>.Empty,
            filters.Video,
            filters.VideoLowPass05,
            filters.VideoLowPass05Offset,
            referenceFilters: new RfVideoReferenceFilterSet(
                VideoBurst: null,
                VideoBurstOffset: 0,
                VideoPilot: null,
                ClipDemodForVideo: true));
    }

    private static double[] QuantizeToFloat32(ReadOnlySpan<double> source)
    {
        var output = new double[source.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = (float)source[i];
        }

        return output;
    }

    private static double[] BuildFakeVideo(
        FormatParameterSet parameters,
        VideoOutputConverter converter,
        double sampleRateHz,
        int blockLength)
    {
        var output = new double[blockLength];
        Array.Fill(output, converter.IreToHz(0.0));

        double samplesPerMicrosecond = sampleRateHz / 1_000_000.0;
        int syncLength = Math.Max(1, (int)(4.7 * samplesPerMicrosecond));
        Fill(output, 1500, 1500 + syncLength, converter.IreToHz(converter.VSyncIre));
        Fill(output, 2000, 2000 + syncLength, converter.IreToHz(converter.VSyncIre));
        Fill(output, 3000, 3500, converter.IreToHz(100.0));
        Fill(output, 4500, 5000, converter.IreToHz(100.0));

        if (parameters.TapeFormat == "LD"
            && TryJsonDouble(parameters.SysParams, "fsc_mhz", out double fscMHz)
            && fscMHz > 0.0)
        {
            double fscHz = fscMHz * 1_000_000.0;
            int porchEnd = 2000 + syncLength + (int)(0.6 * samplesPerMicrosecond);
            int burstEnd = porchEnd + (int)(1.2 * samplesPerMicrosecond);
            AddCarrier(output, porchEnd, burstEnd, fscHz, sampleRateHz, converter.HzIre * 20.0);
            AddCarrier(output, 4200, 5500, fscHz, sampleRateHz, converter.HzIre * 20.0);
            SetCarrier(output, 2000, 2000 + syncLength, fscHz, sampleRateHz, converter.IreToHz(converter.VSyncIre), converter.HzIre * converter.VSyncIre);
        }

        return output;
    }

    private static double[] PrepareFakeVideoForFm(
        FormatParameterSet parameters,
        DecodeFilterSet filters,
        double[] fakeVideo,
        double sampleRateHz)
    {
        if (parameters.TapeFormat != "LD" || filters.VideoLowPass.Length != fakeVideo.Length || !IsPowerOfTwo(fakeVideo.Length))
        {
            return fakeVideo;
        }

        try
        {
            Complex[] spectrum = FastFourierTransform.Forward(fakeVideo);
            MultiplyInPlace(spectrum, filters.VideoLowPass);
            Complex[]? preEmphasis = BuildLaserDiscPreEmphasis(parameters.RfParams, sampleRateHz, fakeVideo.Length);
            if (preEmphasis is not null)
            {
                MultiplyInPlace(spectrum, preEmphasis);
            }

            Complex[] prepared = FastFourierTransform.Inverse(spectrum);
            var output = new double[prepared.Length];
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = prepared[i].Real;
            }

            return output;
        }
        catch (Exception)
        {
            return fakeVideo;
        }
    }

    private static Complex[]? BuildLaserDiscPreEmphasis(JsonElement rfParams, double sampleRateHz, int length)
    {
        if (!rfParams.TryGetProperty("video_deemp", out JsonElement timeConstants)
            || timeConstants.ValueKind != JsonValueKind.Array
            || timeConstants.GetArrayLength() < 2)
        {
            return null;
        }

        return IirFilterDesign.FrequencyResponse(
            IirFilterDesign.EmphasisIir(timeConstants[1].GetDouble(), timeConstants[0].GetDouble(), sampleRateHz),
            length);
    }

    private static double[] GenerateFmWave(ReadOnlySpan<double> instantaneousFrequencyHz, double sampleRateHz)
    {
        var output = new double[instantaneousFrequencyHz.Length];
        double angle = 0.0;
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = Math.Sin(angle);
            angle += Math.Tau * instantaneousFrequencyHz[i] / sampleRateHz;
            if (angle > Math.PI)
            {
                angle -= Math.Tau;
            }
        }

        return output;
    }

    private static void AddCarrier(
        double[] output,
        int start,
        int end,
        double carrierHz,
        double sampleRateHz,
        double amplitudeHz)
    {
        int actualStart = Math.Clamp(start, 0, output.Length);
        int actualEnd = Math.Clamp(end, actualStart, output.Length);
        double angle = 0.0;
        double step = Math.Tau * carrierHz / sampleRateHz;
        for (int i = actualStart; i < actualEnd; i++)
        {
            output[i] += Math.Sin(angle) * amplitudeHz;
            angle += step;
            if (angle > Math.PI)
            {
                angle -= Math.Tau;
            }
        }
    }

    private static void SetCarrier(
        double[] output,
        int start,
        int end,
        double carrierHz,
        double sampleRateHz,
        double baseHz,
        double amplitudeHz)
    {
        int actualStart = Math.Clamp(start, 0, output.Length);
        int actualEnd = Math.Clamp(end, actualStart, output.Length);
        double angle = 0.0;
        double step = Math.Tau * carrierHz / sampleRateHz;
        for (int i = actualStart; i < actualEnd; i++)
        {
            output[i] = baseHz + (Math.Sin(angle) * amplitudeHz);
            angle += step;
            if (angle > Math.PI)
            {
                angle -= Math.Tau;
            }
        }
    }

    private static void MultiplyInPlace(Complex[] target, ReadOnlySpan<Complex> filter)
    {
        for (int i = 0; i < target.Length; i++)
        {
            target[i] *= filter[i];
        }
    }

    private static void Fill(double[] output, int start, int end, double value)
    {
        int actualStart = Math.Clamp(start, 0, output.Length);
        int actualEnd = Math.Clamp(end, actualStart, output.Length);
        for (int i = actualStart; i < actualEnd; i++)
        {
            output[i] = value;
        }
    }

    private static bool TryJsonDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetDouble();
            return true;
        }

        value = 0.0;
        return false;
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }
}
