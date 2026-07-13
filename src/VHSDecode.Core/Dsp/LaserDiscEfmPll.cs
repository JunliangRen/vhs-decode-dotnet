namespace VHSDecode.Core.Dsp;

public sealed class LaserDiscEfmPll
{
    private short _zcPreviousInput;
    private double _delta;
    private byte[] _pllResult = new byte[1 << 16];
    private int _pllResultCount;

    private readonly double _basePeriod = 40_000_000.0 / 4_321_800.0;
    private readonly double _minimumPeriod;
    private readonly double _maximumPeriod;
    private readonly double _periodAdjustBase;

    private double _currentPeriod;
    private double _phaseAdjust;
    private double _refClockTime;
    private int _frequencyHysteresis;
    private byte _tCounter = 1;

    public LaserDiscEfmPll()
    {
        _minimumPeriod = _basePeriod * 0.90;
        _maximumPeriod = _basePeriod * 1.10;
        _periodAdjustBase = _basePeriod * 0.0001;
        _currentPeriod = _basePeriod;
    }

    public byte[] Process(ReadOnlySpan<short> input)
    {
        if (_pllResult.Length < input.Length)
        {
            _pllResult = new byte[input.Length];
        }

        _pllResultCount = 0;
        foreach (short curr in input)
        {
            short prev = _zcPreviousInput;
            if ((prev < 0 && curr >= 0) || (prev >= 0 && curr < 0))
            {
                double fraction = (double)-prev / (curr - prev);
                PushEdge(_delta + fraction);
                _delta = 1.0 - fraction;
            }
            else
            {
                _delta += 1.0;
            }

            _zcPreviousInput = curr;
        }

        var output = new byte[_pllResultCount];
        Array.Copy(_pllResult, output, output.Length);
        return output;
    }

    private void PushEdge(double sampleDelta)
    {
        while (sampleDelta >= _refClockTime)
        {
            double nextTime = _refClockTime + _currentPeriod + _phaseAdjust;
            _refClockTime = nextTime;

            if ((sampleDelta > nextTime || _tCounter < 3) && _tCounter < 11)
            {
                _phaseAdjust = 0.0;
                _tCounter++;
                continue;
            }

            double edgeDelta = sampleDelta - (nextTime - (_currentPeriod / 2.0));
            _phaseAdjust = edgeDelta * 0.005;
            AdjustFrequency(edgeDelta);

            AddResult(_tCounter);
            _tCounter = 1;
        }

        _refClockTime -= sampleDelta;
    }

    private void AdjustFrequency(double edgeDelta)
    {
        if (edgeDelta < 0.0)
        {
            _frequencyHysteresis = _frequencyHysteresis < 0 ? _frequencyHysteresis - 1 : -1;
        }
        else if (edgeDelta > 0.0)
        {
            _frequencyHysteresis = _frequencyHysteresis > 0 ? _frequencyHysteresis + 1 : 1;
        }
        else
        {
            _frequencyHysteresis = 0;
        }

        if (_frequencyHysteresis <= -2 || _frequencyHysteresis >= 2)
        {
            double adjustment = _periodAdjustBase * edgeDelta / _currentPeriod;
            adjustment = Math.Clamp(adjustment, -_periodAdjustBase, _periodAdjustBase);
            _currentPeriod = Math.Clamp(_currentPeriod + adjustment, _minimumPeriod, _maximumPeriod);
        }
    }

    private void AddResult(byte value)
    {
        if (_pllResultCount >= _pllResult.Length)
        {
            Array.Resize(ref _pllResult, _pllResult.Length * 2);
        }

        _pllResult[_pllResultCount] = value;
        _pllResultCount++;
    }
}
