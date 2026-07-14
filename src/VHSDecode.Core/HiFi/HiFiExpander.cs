namespace VHSDecode.Core.HiFi;

internal sealed class HiFiExpander
{
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private readonly HiFiFirstOrderFilter _weightingFilter;
    private readonly double _attackCoefficient;
    private readonly double _releaseCoefficient;
    private readonly double _makeupGainDb;
    private readonly double _ratio;
    private readonly bool _useRms;
    private readonly int _holdSamples;
    private double _envelope;
    private int _holdState;

    internal HiFiExpander(
        int sampleRateHz,
        double makeupGainDb,
        double ratio,
        string envelopeDetection,
        double attackTau,
        double holdTau,
        double releaseTau,
        double weightingLowTau,
        double weightingHighTau)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(makeupGainDb))
        {
            throw new ArgumentOutOfRangeException(nameof(makeupGainDb));
        }

        if (!double.IsFinite(ratio))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio));
        }

        if (!double.IsFinite(attackTau) || attackTau <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(attackTau));
        }

        if (!double.IsFinite(holdTau) || holdTau < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(holdTau));
        }

        if (!double.IsFinite(releaseTau) || releaseTau <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseTau));
        }

        _makeupGainDb = makeupGainDb;
        _ratio = ratio;
        _useRms = envelopeDetection switch
        {
            HiFiConstants.EnvelopeDetectionRms => true,
            HiFiConstants.EnvelopeDetectionPeak => false,
            _ => throw new ArgumentException(
                $"Unsupported HiFi envelope detector: {envelopeDetection}.",
                nameof(envelopeDetection))
        };
        _envelope = _useRms ? 1e-12 : 0.0;
        _attackCoefficient = Math.Exp(-1.0 / (attackTau * sampleRateHz));
        _releaseCoefficient = Math.Exp(-1.0 / (releaseTau * sampleRateHz));
        _holdSamples = checked((int)Math.Round(
            holdTau * sampleRateHz,
            MidpointRounding.ToEven));
        _weightingFilter = new HiFiFirstOrderFilter(
            HiFiShelfFilterDesign.High(
                weightingLowTau,
                weightingHighTau,
                sampleRateHz));
    }

    internal double AttackCoefficient => _attackCoefficient;
    internal double ReleaseCoefficient => _releaseCoefficient;
    internal int HoldSamples => _holdSamples;
    internal double Envelope => _envelope;
    internal int HoldState => _holdState;
    internal HiFiFirstOrderCoefficients WeightingCoefficients
        => _weightingFilter.Coefficients;

    internal void Process(Span<float> sideChain, Span<float> audio)
    {
        if (sideChain.Length != audio.Length)
        {
            throw new ArgumentException("HiFi expander side-chain and audio lengths must match.");
        }

        _weightingFilter.Process(sideChain);
        Expand(sideChain, audio);
    }

    private void Expand(Span<float> sideChain, Span<float> audio)
    {
        double envelope = _envelope;
        int holdState = _holdState;
        double oneMinusAttack = 1.0 - _attackCoefficient;
        if (_useRms)
        {
            for (int i = 0; i < sideChain.Length; i++)
            {
                float sideChainSample = sideChain[i];
                float squared = sideChainSample * sideChainSample;
                if (squared > envelope)
                {
                    envelope = (_attackCoefficient * envelope)
                        + (oneMinusAttack * squared);
                    holdState = _holdSamples;
                }
                else if (holdState > 0)
                {
                    holdState--;
                }
                else
                {
                    envelope = _releaseCoefficient * envelope;
                }

                sideChain[i] = (float)Math.Sqrt(envelope + MachineEpsilon);
            }
        }
        else
        {
            for (int i = 0; i < sideChain.Length; i++)
            {
                float sample = MathF.Abs(sideChain[i]);
                if (sample > envelope)
                {
                    envelope = (_attackCoefficient * envelope)
                        + (oneMinusAttack * sample);
                    holdState = _holdSamples;
                }
                else if (holdState > 0)
                {
                    holdState--;
                }
                else
                {
                    envelope = _releaseCoefficient * envelope;
                }

                sideChain[i] = (float)envelope;
            }
        }

        double ratioMinusOne = _ratio - 1.0;
        for (int i = 0; i < audio.Length; i++)
        {
            double envelopeDb = 20.0 * Math.Log10(
                Math.Max(sideChain[i], MachineEpsilon));
            double targetGainDb = (ratioMinusOne * envelopeDb) + _makeupGainDb;
            double gain = Math.Pow(10.0, targetGainDb * 0.05);
            audio[i] = (float)(audio[i] * gain);
        }

        _envelope = envelope;
        _holdState = holdState;
    }
}
