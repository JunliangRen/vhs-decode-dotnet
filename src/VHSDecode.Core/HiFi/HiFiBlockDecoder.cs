namespace VHSDecode.Core.HiFi;

public sealed record HiFiDemodulatedBlock(
    float[]? Left,
    float[]? Right,
    float LeftDc,
    float RightDc);

public sealed class HiFiBlockDecoder : IDisposable
{
    private readonly HiFiDecodePlan _plan;
    private readonly HiFiBlockResamplers _resamplers;
    private readonly HiFiAfeFilter _leftAfe;
    private readonly HiFiAfeFilter _rightAfe;
    private readonly HiFiQuadratureDiscriminator? _leftQuadrature;
    private readonly HiFiQuadratureDiscriminator? _rightQuadrature;
    private readonly HiFiHilbertDiscriminator? _leftHilbert;
    private readonly HiFiHilbertDiscriminator? _rightHilbert;
    private readonly bool _decodeLeft;
    private readonly bool _decodeRight;

    public HiFiBlockDecoder(HiFiDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        _plan = HiFiDecodePlan.FromOptions(options);
        _resamplers = new HiFiBlockResamplers(_plan);
        (_leftAfe, _rightAfe) = HiFiAfeFilter.FromPlan(_plan);
        _decodeLeft = options.AudioMode != HiFiConstants.AudioModeMonoRight;
        _decodeRight = options.AudioMode != HiFiConstants.AudioModeMonoLeft;

        int maximumOscillatorLength = _plan.InitialBlockSizes.IfSamples;
        if (options.DemodType == HiFiConstants.DemodQuadrature)
        {
            _leftQuadrature = new HiFiQuadratureDiscriminator(
                _plan.IfRateHz,
                _plan.Afe.LeftCarrierHz,
                _plan.Afe.LeftCarrierDeviationHz,
                maximumOscillatorLength);
            _rightQuadrature = new HiFiQuadratureDiscriminator(
                _plan.IfRateHz,
                _plan.Afe.RightCarrierHz,
                _plan.Afe.RightCarrierDeviationHz,
                maximumOscillatorLength);
        }
        else
        {
            _leftHilbert = new HiFiHilbertDiscriminator(
                _plan.IfRateHz,
                _plan.Afe.LeftCarrierHz,
                _plan.Afe.LeftCarrierDeviationHz);
            _rightHilbert = new HiFiHilbertDiscriminator(
                _plan.IfRateHz,
                _plan.Afe.RightCarrierHz,
                _plan.Afe.RightCarrierDeviationHz);
        }
    }

    public HiFiDecodeOptions Options { get; }
    public HiFiDecodePlan Plan => _plan;

    public HiFiDemodulatedBlock Decode(ReadOnlySpan<float> rfData)
    {
        if (rfData.IsEmpty)
        {
            throw new ArgumentException("HiFi RF block must not be empty.", nameof(rfData));
        }

        float[] ifData = _resamplers.ResampleInputToIf(rfData);
        float[]? left = null;
        float[]? right = null;
        float leftDc = 0.0f;
        float rightDc = 0.0f;

        if (_decodeLeft)
        {
            float[] filtered = _leftAfe.Apply(ifData);
            float[] demodulated = DemodulateLeft(filtered);
            left = _resamplers.ResampleLeftIfToAudio(demodulated);
            leftDc = HiFiAudioProcessing.CancelDcAndTrim(left, _plan.PreTrimSamples);
        }

        if (_decodeRight)
        {
            float[] filtered = _rightAfe.Apply(ifData);
            float[] demodulated = DemodulateRight(filtered);
            right = _resamplers.ResampleRightIfToAudio(demodulated);
            rightDc = HiFiAudioProcessing.CancelDcAndTrim(right, _plan.PreTrimSamples);
        }

        return new HiFiDemodulatedBlock(left, right, leftDc, rightDc);
    }

    public void Dispose()
    {
        _resamplers.Dispose();
        GC.SuppressFinalize(this);
    }

    private float[] DemodulateLeft(float[] filtered)
    {
        var output = new float[filtered.Length];
        if (_leftQuadrature is not null)
        {
            _leftQuadrature.Demodulate(filtered, output);
        }
        else
        {
            _leftHilbert!.Demodulate(filtered, output);
        }

        return output;
    }

    private float[] DemodulateRight(float[] filtered)
    {
        var output = new float[filtered.Length];
        if (_rightQuadrature is not null)
        {
            _rightQuadrature.Demodulate(filtered, output);
        }
        else
        {
            _rightHilbert!.Demodulate(filtered, output);
        }

        return output;
    }
}
