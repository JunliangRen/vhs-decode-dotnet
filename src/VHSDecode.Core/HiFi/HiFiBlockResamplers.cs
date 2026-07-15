using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

public sealed class HiFiBlockResamplers : IDisposable
{
    private readonly SoxrFloat32Resampler _inputToIf;
    private readonly SoxrFloat32Resampler _leftIfToAudio;
    private readonly SoxrFloat32Resampler _rightIfToAudio;
    private readonly SoxrFloat32Resampler _leftAudioToFinal;
    private readonly SoxrFloat32Resampler _rightAudioToFinal;
    private readonly bool _bypassInputToIf;
    private readonly bool _bypassAudioToFinal;

    public HiFiBlockResamplers(HiFiDecodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _inputToIf = Create(
            plan.ResamplingRatios.InputToIf,
            plan.ResamplerConverters.InputToIf);
        _leftIfToAudio = Create(
            plan.ResamplingRatios.IfToAudio,
            plan.ResamplerConverters.IfToAudio);
        _rightIfToAudio = Create(
            plan.ResamplingRatios.IfToAudio,
            plan.ResamplerConverters.IfToAudio);
        _leftAudioToFinal = Create(
            plan.ResamplingRatios.AudioToFinal,
            plan.ResamplerConverters.AudioToFinal);
        _rightAudioToFinal = Create(
            plan.ResamplingRatios.AudioToFinal,
            plan.ResamplerConverters.AudioToFinal);
        _bypassInputToIf = plan.InputRateHz == plan.IfRateHz;
        _bypassAudioToFinal = plan.AudioRateHz == plan.FinalAudioRateHz;
    }

    public SoxrQuality InputToIfQuality => _inputToIf.Quality;
    public SoxrQuality IfToAudioQuality => _leftIfToAudio.Quality;
    public SoxrQuality AudioToFinalQuality => _leftAudioToFinal.Quality;

    public float[] ResampleInputToIf(ReadOnlySpan<float> input)
        => _bypassInputToIf ? input.ToArray() : _inputToIf.ProcessFinalBlock(input);

    public float[] ResampleLeftIfToAudio(ReadOnlySpan<float> input)
        => _leftIfToAudio.ProcessFinalBlock(input);

    public float[] ResampleRightIfToAudio(ReadOnlySpan<float> input)
        => _rightIfToAudio.ProcessFinalBlock(input);

    public float[] ResampleLeftAudioToFinal(ReadOnlySpan<float> input)
        => _bypassAudioToFinal ? input.ToArray() : _leftAudioToFinal.ProcessFinalBlock(input);

    public float[] ResampleRightAudioToFinal(ReadOnlySpan<float> input)
        => _bypassAudioToFinal ? input.ToArray() : _rightAudioToFinal.ProcessFinalBlock(input);

    public void Dispose()
    {
        _inputToIf.Dispose();
        _leftIfToAudio.Dispose();
        _rightIfToAudio.Dispose();
        _leftAudioToFinal.Dispose();
        _rightAudioToFinal.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SoxrFloat32Resampler Create(HiFiRateRatio ratio, string converter)
    {
        SoxrQuality quality = converter switch
        {
            "LQ" => SoxrQuality.Low,
            "MQ" => SoxrQuality.Medium,
            "HQ" => SoxrQuality.High,
            "VHQ" => SoxrQuality.VeryHigh,
            _ => throw new ArgumentException($"Unsupported libsoxr converter: {converter}.", nameof(converter))
        };
        return new SoxrFloat32Resampler(
            (double)ratio.Denominator,
            (double)ratio.Numerator,
            quality);
    }
}
