namespace VHSDecode.Core.HiFi;

public sealed record HiFiPostProcessedBlock(
    float[] Left,
    float[] Right,
    float[] Stereo,
    float LeftPeak,
    float RightPeak);

public sealed class HiFiAudioPostProcessor
{
    private readonly HiFiChannelPostProcessor _left;
    private readonly HiFiChannelPostProcessor _right;
    private readonly int _sampleRateHz;
    private int _nextBlockNumber;

    public HiFiAudioPostProcessor(HiFiDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TapeFormat is not ("vhs" or "8mm"))
        {
            throw new ArgumentException(
                $"Unsupported HiFi tape format: {options.TapeFormat}.",
                nameof(options));
        }

        _sampleRateHz = options.AudioRateHz;
        _left = new HiFiChannelPostProcessor(options);
        _right = new HiFiChannelPostProcessor(options);
    }

    public int NextBlockNumber => _nextBlockNumber;
    public float PeakLeft { get; private set; }
    public float PeakRight { get; private set; }

    internal HiFiChannelPostProcessor LeftChannel => _left;
    internal HiFiChannelPostProcessor RightChannel => _right;

    public HiFiPostProcessedBlock Process(HiFiDecodedBlock block, int blockNumber)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (blockNumber != _nextBlockNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockNumber),
                blockNumber,
                $"Expected HiFi block {_nextBlockNumber}, but received {blockNumber}.");
        }

        if (block.Left.Length != block.Right.Length)
        {
            throw new ArgumentException(
                "HiFi left and right channel lengths must match.",
                nameof(block));
        }

        bool firstBlock = blockNumber == 0;
        HiFiPostProcessedChannel left = _left.Process(block.Left, firstBlock);
        HiFiPostProcessedChannel right = _right.Process(block.Right, firstBlock);
        (float[] stereo, float leftPeak, float rightPeak) = Interleave(
            left.Post,
            right.Post,
            firstBlock);

        if (leftPeak > PeakLeft)
        {
            PeakLeft = leftPeak;
        }

        if (rightPeak > PeakRight)
        {
            PeakRight = rightPeak;
        }

        _nextBlockNumber++;
        return new HiFiPostProcessedBlock(
            left.Post,
            right.Post,
            stereo,
            leftPeak,
            rightPeak);
    }

    private (float[] Stereo, float LeftPeak, float RightPeak) Interleave(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        bool firstBlock)
    {
        var stereo = new float[checked(left.Length * 2)];
        int startSample = firstBlock
            ? (int)(0.0015 * _sampleRateHz)
            : 0;
        if (startSample > left.Length)
        {
            throw new ArgumentException(
                "The first HiFi block is shorter than the startup mute interval.");
        }

        float leftPeak = 0.0f;
        float rightPeak = 0.0f;
        for (int i = startSample; i < left.Length; i++)
        {
            float leftSample = left[i];
            stereo[i * 2] = leftSample;
            float gain = MathF.Abs(leftSample);
            if (gain > leftPeak)
            {
                leftPeak = gain;
            }

            float rightSample = right[i];
            stereo[(i * 2) + 1] = rightSample;
            gain = MathF.Abs(rightSample);
            if (gain > rightPeak)
            {
                rightPeak = gain;
            }
        }

        return (stereo, leftPeak, rightPeak);
    }
}

internal readonly record struct HiFiPostProcessedChannel(
    float[] Pre,
    float[] Post);

internal sealed class HiFiChannelPostProcessor
{
    private readonly string _tapeFormat;
    private readonly bool _enableDeemphasis;
    private readonly bool _enableExpander;
    private readonly HiFiDcBlocker _dcBlocker;
    private readonly HiFiFirstOrderFilter _deemphasisPre;
    private readonly HiFiFirstOrderFilter _deemphasisPost;
    private readonly HiFiFirstOrderFilter _noiseReductionDeemphasis;
    private readonly HiFiExpander _expander;
    private readonly HiFiSpectralNoiseReduction? _spectralNoiseReduction;

    internal HiFiChannelPostProcessor(HiFiDecodeOptions options)
    {
        _tapeFormat = options.TapeFormat;
        _enableDeemphasis = options.EnableDeemphasis;
        _enableExpander = options.EnableExpander;
        _dcBlocker = new HiFiDcBlocker(options.AudioRateHz, 1.0);
        HiFiFirstOrderCoefficients deemphasis = HiFiShelfFilterDesign.Low(
            options.DeemphasisLowTau,
            options.DeemphasisHighTau,
            options.AudioRateHz);
        HiFiFirstOrderCoefficients noiseReductionDeemphasis = HiFiShelfFilterDesign.Low(
            options.NoiseReductionDeemphasisLowTau,
            options.NoiseReductionDeemphasisHighTau,
            options.AudioRateHz);
        _deemphasisPre = new HiFiFirstOrderFilter(deemphasis);
        _deemphasisPost = new HiFiFirstOrderFilter(deemphasis);
        _noiseReductionDeemphasis = new HiFiFirstOrderFilter(
            noiseReductionDeemphasis);
        if (options.SpectralNoiseReductionAmount > 0.0)
        {
            _spectralNoiseReduction = new HiFiSpectralNoiseReduction(
                options.AudioRateHz,
                options.SpectralNoiseReductionAmount);
        }

        _expander = new HiFiExpander(
            options.AudioRateHz,
            options.ExpanderGain,
            options.ExpanderRatio,
            options.ExpanderEnvelopeDetection,
            options.ExpanderAttackTau,
            options.ExpanderHoldTau,
            options.ExpanderReleaseTau,
            options.ExpanderWeightingLowTau,
            options.ExpanderWeightingHighTau);
    }

    internal HiFiDcBlocker DcBlocker => _dcBlocker;
    internal HiFiExpander Expander => _expander;
    internal HiFiFirstOrderCoefficients DeemphasisCoefficients
        => _deemphasisPost.Coefficients;
    internal HiFiFirstOrderCoefficients NoiseReductionDeemphasisCoefficients
        => _noiseReductionDeemphasis.Coefficients;

    internal HiFiPostProcessedChannel Process(
        ReadOnlySpan<float> source,
        bool firstBlock)
    {
        float[] pre = source.ToArray();
        if (firstBlock)
        {
            float[] prime = pre.ToArray();
            _dcBlocker.Process(prime);
        }

        _dcBlocker.Process(pre);

        var post = new float[pre.Length];
        if (_spectralNoiseReduction is null)
        {
            // Release 4.0 copies the DC-blocked signal when spectral NR is disabled.
            pre.CopyTo(post, 0);
        }
        else
        {
            _spectralNoiseReduction.Process(pre, post);
        }

        if (_tapeFormat == "8mm")
        {
            Process8Mm(pre, post, firstBlock);
        }
        else
        {
            ProcessVhs(pre, post, firstBlock);
        }

        return new HiFiPostProcessedChannel(pre, post);
    }

    private void ProcessVhs(Span<float> pre, Span<float> post, bool firstBlock)
    {
        if (_enableDeemphasis)
        {
            _deemphasisPre.Process(pre);
            _deemphasisPost.Process(post);
            _noiseReductionDeemphasis.Process(post);
        }

        if (!_enableExpander)
        {
            return;
        }

        if (firstBlock)
        {
            float[] primePre = pre.ToArray();
            float[] primePost = post.ToArray();
            _expander.Process(primePre, primePost);
        }

        _expander.Process(pre, post);
    }

    private void Process8Mm(Span<float> pre, Span<float> post, bool firstBlock)
    {
        if (_enableDeemphasis)
        {
            _deemphasisPost.Process(post);
        }

        if (_enableExpander)
        {
            if (firstBlock)
            {
                float[] primePost = post.ToArray();
                _expander.Process(pre, primePost);
            }

            _expander.Process(pre, post);
        }

        if (_enableDeemphasis)
        {
            _noiseReductionDeemphasis.Process(post);
        }
    }
}
