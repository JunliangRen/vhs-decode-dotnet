namespace VHSDecode.Core.Decode;

public enum TbcFieldOrderAction
{
    Detect,
    Duplicate,
    Drop,
    None
}

public sealed record TbcFieldOrderOptions(TbcFieldOrderAction Action, int Confidence, bool AllowProgressiveFlip)
{
    public static TbcFieldOrderOptions Default { get; } = new(TbcFieldOrderAction.Detect, 100, true);

    public static TbcFieldOrderOptions Create(TbcFieldOrderAction action, int confidence, bool allowProgressiveFlip = true) =>
        new(action, confidence, allowProgressiveFlip);
}

public sealed record TbcFieldOrderInput(long StartSample, bool? DetectedFirstField);

public sealed record TbcFieldOrderDecision(
    int SeqNo,
    bool IsFirstField,
    bool DetectedFirstField,
    bool IsDuplicateField,
    bool WriteField,
    int SyncConfidence,
    int DecodeFaults);

public sealed class TbcFieldOrderPlanner
{
    private readonly TbcFieldOrderAction _action;
    private readonly bool _allowProgressiveFlip;
    private bool _duplicatePreviousField = true;
    private TbcFieldOrderDecision? _previous;
    private TbcFieldOrderDecision? _previous2;
    private long? _previousStartSample;

    public TbcFieldOrderPlanner(
        TbcFieldOrderAction action = TbcFieldOrderAction.Detect,
        bool allowProgressiveFlip = true)
    {
        _action = action;
        _allowProgressiveFlip = allowProgressiveFlip;
    }

    public TbcFieldOrderDecision Plan(
        TbcFieldOrderInput input,
        double? distanceFromPreviousField = null)
    {
        int seqNo = (_previous?.SeqNo ?? 0) + 1;
        bool detectedFirstField = input.DetectedFirstField ?? (_previous is null || !_previous.IsFirstField);
        bool isFirstField = detectedFirstField;
        bool isDuplicateField = false;
        bool writeField = true;
        int syncConfidence = 100;
        int decodeFaults = 0;

        if (_previous is not null && _previous.IsFirstField == isFirstField)
        {
            if (_allowProgressiveFlip
                && input.DetectedFirstField.HasValue
                && _previous.DetectedFirstField == input.DetectedFirstField.Value
                && _previous2 is not null
                && _previous2.DetectedFirstField == _previous.DetectedFirstField
                && distanceFromPreviousField.HasValue
                && InRange(distanceFromPreviousField.Value, 0.9, 1.1))
            {
                decodeFaults |= 1;
                syncConfidence = 10;
                isFirstField = !_previous.IsFirstField;
            }
            else
            {
                if (_action == TbcFieldOrderAction.Duplicate)
                {
                    _duplicatePreviousField = true;
                }
                else if (_action == TbcFieldOrderAction.Drop)
                {
                    _duplicatePreviousField = false;
                }
                else if (_action == TbcFieldOrderAction.Detect && distanceFromPreviousField.HasValue)
                {
                    if (distanceFromPreviousField.Value > 1.1)
                    {
                        _duplicatePreviousField = true;
                    }
                    else if (distanceFromPreviousField.Value < 0.9)
                    {
                        _duplicatePreviousField = false;
                    }
                    else
                    {
                        _duplicatePreviousField = !_duplicatePreviousField;
                    }
                }

                decodeFaults |= 4;
                syncConfidence = 0;
                if (_action == TbcFieldOrderAction.None)
                {
                    isFirstField = !_previous.IsFirstField;
                }
                else if (_duplicatePreviousField)
                {
                    isDuplicateField = true;
                }
                else
                {
                    writeField = false;
                }
            }
        }

        var decision = new TbcFieldOrderDecision(
            seqNo,
            isFirstField,
            detectedFirstField,
            isDuplicateField,
            writeField,
            syncConfidence,
            decodeFaults);
        _previous2 = _previous;
        _previous = decision;
        _previousStartSample = input.StartSample;
        return decision;
    }

    public double DistanceFromPrevious(TbcFieldOrderInput input, double nominalFieldLength)
    {
        if (nominalFieldLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalFieldLength));
        }

        return _previousStartSample.HasValue
            ? (input.StartSample - _previousStartSample.Value) / nominalFieldLength
            : 1.0;
    }

    public static TbcFieldOrderAction ParseAction(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "detect" => TbcFieldOrderAction.Detect,
            "duplicate" => TbcFieldOrderAction.Duplicate,
            "drop" => TbcFieldOrderAction.Drop,
            "none" => TbcFieldOrderAction.None,
            _ => throw new ArgumentException($"Unknown field order action '{action}'.", nameof(action))
        };
    }

    private static bool InRange(double value, double minimum, double maximum)
    {
        return value >= minimum && value <= maximum;
    }
}
