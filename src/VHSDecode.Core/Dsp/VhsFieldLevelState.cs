namespace VHSDecode.Core.Dsp;

internal sealed class VhsFieldLevelState
{
    private readonly MovingAverageWindow _syncLevels;
    private readonly MovingAverageWindow _blankLevels;

    public VhsFieldLevelState(double framesPerSecond)
    {
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        double fieldRate = framesPerSecond * 2.0;
        int window = checked((int)Math.Round(
            fieldRate < 60.0 ? fieldRate / 5.0 : fieldRate / 6.0,
            MidpointRounding.ToEven));
        _syncLevels = new MovingAverageWindow(window);
        _blankLevels = new MovingAverageWindow(window);
    }

    public bool HasLevels => _syncLevels.HasValues && _blankLevels.HasValues;

    public void PushSyncLevel(double syncLevel) => _syncLevels.Push(syncLevel);

    public void PushLevels(double syncLevel, double blankLevel)
    {
        _blankLevels.Push(blankLevel);
        PushSyncLevel(syncLevel);
    }

    public double? PullSyncLevel() => _syncLevels.Pull();

    public (double SyncLevel, double BlankLevel)? PullLevels()
    {
        double? blankLevel = _blankLevels.Pull();
        if (!blankLevel.HasValue)
        {
            return null;
        }

        double? syncLevel = PullSyncLevel();
        return syncLevel.HasValue ? (syncLevel.Value, blankLevel.Value) : null;
    }

    private sealed class MovingAverageWindow(int window)
    {
        private readonly List<double> _values = [];

        public bool HasValues => _values.Count > 0;

        public void Push(double value) => _values.Add(value);

        public double? Pull()
        {
            if (_values.Count == 0)
            {
                return null;
            }

            if (_values.Count >= window)
            {
                _values.RemoveRange(0, _values.Count - window);
            }

            return _values.Average();
        }
    }
}
