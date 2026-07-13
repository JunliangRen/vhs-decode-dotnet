namespace VHSDecode.Core.Decode;

public sealed record LaserDiscMtfUpdate(
    double PreviousLevel,
    double Level,
    bool RequiresRetry,
    int RatioCount);

public sealed class LaserDiscAutoMtfController
{
    private const int CavWindow = 30;
    private const int ClvWindow = 900;
    private readonly List<double> _blackToWhiteRatios = [];
    private int[]? _firstFieldVbi;

    public double Level { get; private set; } = 1.0;

    public bool IsClv { get; private set; }

    public IReadOnlyList<double> BlackToWhiteRatios => _blackToWhiteRatios;

    public LaserDiscMtfUpdate Observe(double? blackToWhiteRfRatio)
    {
        double previous = Level;
        if (!blackToWhiteRfRatio.HasValue || !double.IsFinite(blackToWhiteRfRatio.Value))
        {
            return new LaserDiscMtfUpdate(previous, Level, RequiresRetry: false, _blackToWhiteRatios.Count);
        }

        _blackToWhiteRatios.Add(blackToWhiteRfRatio.Value);
        int keep = IsClv ? ClvWindow : CavWindow;
        if (_blackToWhiteRatios.Count > keep)
        {
            _blackToWhiteRatios.RemoveRange(0, _blackToWhiteRatios.Count - keep);
        }

        Level = Math.Clamp((_blackToWhiteRatios.Average() - 1.08) / 0.38, 0.0, 1.0);
        return new LaserDiscMtfUpdate(
            previous,
            Level,
            Math.Abs(Level - previous) >= 0.05,
            _blackToWhiteRatios.Count);
    }

    public void ObserveAcceptedField(TbcDecodedField field, string system)
    {
        if (field.VbiData is not { Length: > 0 } codes || !field.DetectedFirstField.HasValue)
        {
            return;
        }

        if (field.DetectedFirstField.Value)
        {
            _firstFieldVbi = codes.ToArray();
            return;
        }

        if (_firstFieldVbi is null)
        {
            return;
        }

        int framesPerSecond = string.Equals(system, "PAL", StringComparison.OrdinalIgnoreCase) ? 25 : 30;
        LaserDiscVbiInterpretation interpretation = LaserDiscVbiInterpreter.Interpret(
            _firstFieldVbi.Concat(codes),
            framesPerSecond);
        IsClv = interpretation.IsClv;
        _firstFieldVbi = null;
    }
}
