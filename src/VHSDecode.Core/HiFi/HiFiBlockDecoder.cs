using System.Buffers;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

public sealed record HiFiDemodulatedBlock(
    float[]? Left,
    float[]? Right,
    float LeftDc,
    float RightDc);

public sealed record HiFiDecodedBlock(
    float[] Left,
    float[] Right,
    float LeftDc,
    float RightDc);

public sealed class HiFiBlockDecoder : IHiFiBlockDecoder
{
    private readonly HiFiDecodePlan _plan;
    private readonly HiFiBlockResamplers _resamplers;
    private readonly HiFiHeadSwitchProcessor? _headSwitchProcessor;
    private readonly int _maximumOscillatorLength;
    private readonly bool _decodeLeft;
    private readonly bool _decodeRight;
    private readonly Action<float[]>? _gnuRadioSink;
    private HiFiAfeFilter _leftAfe = null!;
    private HiFiAfeFilter _rightAfe = null!;
    private HiFiQuadratureDiscriminator? _leftQuadrature;
    private HiFiQuadratureDiscriminator? _rightQuadrature;
    private HiFiHilbertDiscriminator? _leftHilbert;
    private HiFiHilbertDiscriminator? _rightHilbert;
    private double _leftCarrierHz;
    private double _rightCarrierHz;

    public HiFiBlockDecoder(HiFiDecodeOptions options)
        : this(options, null)
    {
    }

    internal HiFiBlockDecoder(
        HiFiDecodeOptions options,
        Action<float[]>? gnuRadioSink)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        _gnuRadioSink = gnuRadioSink;
        _plan = HiFiDecodePlan.FromOptions(options);
        _resamplers = new HiFiBlockResamplers(_plan);
        _decodeLeft = options.AudioMode != HiFiConstants.AudioModeMonoRight;
        _decodeRight = options.AudioMode != HiFiConstants.AudioModeMonoLeft;
        _maximumOscillatorLength = _plan.InitialBlockSizes.IfSamples;
        _leftCarrierHz = _plan.Afe.LeftCarrierHz;
        _rightCarrierHz = _plan.Afe.RightCarrierHz;
        ConfigureSignalPath();
        _headSwitchProcessor = options.HeadSwitchingInterpolation
            ? new HiFiHeadSwitchProcessor(_plan.AudioRateHz, _plan.Afe.FieldRateHz)
            : null;
    }

    public HiFiDecodeOptions Options { get; }
    public HiFiDecodePlan Plan => _plan;
    public double LeftCarrierHz => _leftCarrierHz;
    public double RightCarrierHz => _rightCarrierHz;

    public HiFiDecodedBlock Decode(ReadOnlySpan<float> rfData)
        => FinishBlock(DecodeDemodulated(rfData));

    internal bool CanDecodeStereoPairs => Sse2.IsSupported && _decodeLeft && _decodeRight
        && !Options.AutoFineTune && !Options.Preview && _gnuRadioSink is null
        && _plan.IfRateHz == _plan.InputRateHz
        && _plan.InitialBlockSizes.IfSamples <= SosFilter.MaximumStereoPairSamples;

    internal (HiFiDecodedBlock First, HiFiDecodedBlock Second)? TryDecodeStereoPair(
        ReadOnlySpan<float> first, ReadOnlySpan<float> second)
    {
        if (_leftAfe.TryApplyStereoPair(_rightAfe, first, second) is not { } rf)
        {
            return null;
        }

        return (FinishBlock(DemodulateFiltered(rf.First.Left, rf.First.Right)),
            FinishBlock(DemodulateFiltered(rf.Second.Left, rf.Second.Right)));
    }

    private HiFiDecodedBlock FinishBlock(HiFiDemodulatedBlock demodulated)
    {
        float[]? left = demodulated.Left;
        float[]? right = demodulated.Right;

        HiFiDropoutCompensator.Compensate(
            left,
            right,
            Options.AudioMode,
            Options.DropoutCompensation);

        if (_headSwitchProcessor is not null)
        {
            if (left is not null)
            {
                left = _headSwitchProcessor.RemoveNoise(left);
            }

            if (right is not null)
            {
                right = _headSwitchProcessor.RemoveNoise(right);
            }
        }

        if (left is not null)
        {
            left = _resamplers.ResampleLeftAudioToFinal(left);
        }

        if (right is not null)
        {
            right = _resamplers.ResampleRightAudioToFinal(right);
        }

        if (Options.AutoFineTune)
        {
            AutoFineTune(demodulated.LeftDc, demodulated.RightDc);
        }

        (float[] mixedLeft, float[] mixedRight) = MixForMode(left, right, Options.AudioMode);
        if (Options.Gain != 1.0)
        {
            AdjustGain(mixedLeft, Options.Gain);
            AdjustGain(mixedRight, Options.Gain);
        }

        return new HiFiDecodedBlock(
            mixedLeft,
            mixedRight,
            demodulated.LeftDc,
            demodulated.RightDc);
    }

    internal HiFiDemodulatedBlock DecodeDemodulated(ReadOnlySpan<float> rfData)
    {
        if (rfData.IsEmpty)
        {
            throw new ArgumentException("HiFi RF block must not be empty.", nameof(rfData));
        }

        ReadOnlySpan<float> ifData = _plan.InputRateHz == _plan.IfRateHz
            ? rfData
            : _resamplers.ResampleInputToIf(rfData);
        float[]? filteredLeft = null;
        float[]? filteredRight = null;

        if (_decodeLeft && _decodeRight && _leftAfe.TryApplyStereo(_rightAfe, ifData) is { } stereo)
        {
            (filteredLeft, filteredRight) = stereo;
        }
        else
        {
            if (_decodeLeft)
            {
                filteredLeft = _leftAfe.Apply(ifData);
            }

            if (_decodeRight)
            {
                filteredRight = _rightAfe.Apply(ifData);
            }
        }

        if (_gnuRadioSink is not null)
        {
            _gnuRadioSink(SumFilteredChannels(filteredLeft, filteredRight));
        }

        return DemodulateFiltered(filteredLeft, filteredRight);
    }

    private HiFiDemodulatedBlock DemodulateFiltered(float[]? filteredLeft, float[]? filteredRight)
    {
        float[]? left = null;
        float[]? right = null;
        float leftDc = 0.0f;
        float rightDc = 0.0f;

        if (filteredLeft is not null)
        {
            left = DemodulateToAudio(filteredLeft, rightChannel: false);
            leftDc = HiFiAudioProcessing.CancelDcAndTrim(left, _plan.PreTrimSamples);
        }

        if (filteredRight is not null)
        {
            right = DemodulateToAudio(filteredRight, rightChannel: true);
            rightDc = HiFiAudioProcessing.CancelDcAndTrim(right, _plan.PreTrimSamples);
        }

        return new HiFiDemodulatedBlock(left, right, leftDc, rightDc);
    }

    private static float[] SumFilteredChannels(float[]? left, float[]? right)
    {
        ReadOnlySpan<float> sourceLeft = left ?? [];
        ReadOnlySpan<float> sourceRight = right ?? [];
        int length = Math.Max(sourceLeft.Length, sourceRight.Length);
        var output = new float[length];
        for (int i = 0; i < length; i++)
        {
            float leftSample = i < sourceLeft.Length ? sourceLeft[i] : 0.0f;
            float rightSample = i < sourceRight.Length ? sourceRight[i] : 0.0f;
            output[i] = leftSample + rightSample;
        }

        return output;
    }

    public void Dispose()
    {
        _resamplers.Dispose();
        GC.SuppressFinalize(this);
    }

    private float[] DemodulateToAudio(Span<float> filtered, bool rightChannel)
    {
        HiFiQuadratureDiscriminator? quadrature = rightChannel ? _rightQuadrature : _leftQuadrature;
        float[] buffer = quadrature is null
            ? new float[filtered.Length]
            : ArrayPool<float>.Shared.Rent(filtered.Length);
        try
        {
            Span<float> demodulated = buffer.AsSpan(0, filtered.Length);
            if (quadrature is not null)
            {
                // Quadrature demodulation writes one result per adjacent input pair.
                demodulated[^1] = 0.0f;
                quadrature.Demodulate(filtered, demodulated, Options.DspBackend == DspBackend.IppFast);
            }
            else
            {
                (rightChannel ? _rightHilbert! : _leftHilbert!).Demodulate(filtered, demodulated);
            }

            return rightChannel
                ? _resamplers.ResampleRightIfToAudio(demodulated)
                : _resamplers.ResampleLeftIfToAudio(demodulated);
        }
        finally
        {
            if (quadrature is not null)
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }
    }

    private void AutoFineTune(float leftDc, float rightDc)
    {
        if (_decodeLeft)
        {
            double updated = Math.Round(
                _plan.Afe.LeftCarrierHz + (leftDc * _plan.Afe.LeftCarrierDeviationHz),
                MidpointRounding.ToEven);
            _leftCarrierHz = Math.Clamp(
                updated,
                _plan.Afe.LeftCarrierHz - 10_000.0,
                _plan.Afe.LeftCarrierHz + 10_000.0);
        }

        if (_decodeRight)
        {
            double updated = Math.Round(
                _plan.Afe.RightCarrierHz + (rightDc * _plan.Afe.RightCarrierDeviationHz),
                MidpointRounding.ToEven);
            _rightCarrierHz = Math.Clamp(
                updated,
                _plan.Afe.RightCarrierHz - 10_000.0,
                _plan.Afe.RightCarrierHz + 10_000.0);
        }

        ConfigureSignalPath();
    }

    private void ConfigureSignalPath()
    {
        _leftAfe = new HiFiAfeFilter(
            _plan.IfRateHz,
            _leftCarrierHz,
            _plan.Afe.LeftNotchWidthHz);
        _rightAfe = new HiFiAfeFilter(
            _plan.IfRateHz,
            _rightCarrierHz,
            _plan.Afe.RightNotchWidthHz);

        if (Options.DemodType == HiFiConstants.DemodQuadrature)
        {
            _leftQuadrature = new HiFiQuadratureDiscriminator(
                _plan.IfRateHz,
                _leftCarrierHz,
                _plan.Afe.LeftCarrierDeviationHz,
                _maximumOscillatorLength);
            _rightQuadrature = new HiFiQuadratureDiscriminator(
                _plan.IfRateHz,
                _rightCarrierHz,
                _plan.Afe.RightCarrierDeviationHz,
                _maximumOscillatorLength);
            _leftHilbert = null;
            _rightHilbert = null;
        }
        else
        {
            _leftHilbert = new HiFiHilbertDiscriminator(
                _plan.IfRateHz,
                _leftCarrierHz,
                _plan.Afe.LeftCarrierDeviationHz);
            _rightHilbert = new HiFiHilbertDiscriminator(
                _plan.IfRateHz,
                _rightCarrierHz,
                _plan.Afe.RightCarrierDeviationHz);
            _leftQuadrature = null;
            _rightQuadrature = null;
        }
    }

    private static (float[] Left, float[] Right) MixForMode(
        float[]? left,
        float[]? right,
        string audioMode)
        => audioMode switch
        {
            HiFiConstants.AudioModeStereoMidSide or HiFiConstants.AudioModeDualMonoMidSide
                => AddSubtractChannels(left, right),
            HiFiConstants.AudioModeMonoLeft => DuplicateChannel(left, nameof(left)),
            HiFiConstants.AudioModeMonoRight => DuplicateChannel(right, nameof(right)),
            HiFiConstants.AudioModeMonoSum => SumChannels(left, right),
            HiFiConstants.AudioModeStereo or HiFiConstants.AudioModeDualMono
                => RequireStereo(left, right),
            _ => throw new ArgumentException($"Unsupported HiFi audio mode: {audioMode}.", nameof(audioMode))
        };

    private static (float[] Left, float[] Right) AddSubtractChannels(
        float[]? left,
        float[]? right)
    {
        (float[] sourceLeft, float[] sourceRight) = RequireStereo(left, right);
        var mixedLeft = new float[sourceLeft.Length];
        var mixedRight = new float[sourceLeft.Length];
        for (int i = 0; i < mixedLeft.Length; i++)
        {
            float sum = sourceLeft[i] + sourceRight[i];
            float difference = sourceLeft[i] - sourceRight[i];
            mixedLeft[i] = sum * 0.5f;
            mixedRight[i] = difference * 0.5f;
        }

        return (mixedLeft, mixedRight);
    }

    private static (float[] Left, float[] Right) SumChannels(
        float[]? left,
        float[]? right)
    {
        (float[] sourceLeft, float[] sourceRight) = RequireStereo(left, right);
        var mixedLeft = new float[sourceLeft.Length];
        for (int i = 0; i < mixedLeft.Length; i++)
        {
            float sum = sourceLeft[i] + sourceRight[i];
            mixedLeft[i] = sum * 0.5f;
        }

        return (mixedLeft, mixedLeft.ToArray());
    }

    private static (float[] Left, float[] Right) DuplicateChannel(
        float[]? channel,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(channel, parameterName);
        return (channel, channel);
    }

    private static (float[] Left, float[] Right) RequireStereo(
        float[]? left,
        float[]? right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length)
        {
            throw new ArgumentException("HiFi channel lengths must match before stereo mixing.");
        }

        return (left, right);
    }

    private static void AdjustGain(Span<float> audio, double gain)
    {
        float gainFloat = (float)gain;
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] *= gainFloat;
        }
    }
}
