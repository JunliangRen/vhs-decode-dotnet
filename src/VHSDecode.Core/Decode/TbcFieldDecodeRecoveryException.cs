namespace VHSDecode.Core.Decode;

public enum TbcFieldDecodeRecoveryKind
{
    NoSyncPulses,
    NoFirstHSync,
    InsufficientData
}

public sealed class TbcFieldDecodeRecoveryException : InvalidOperationException
{
    public TbcFieldDecodeRecoveryException(
        TbcFieldDecodeRecoveryKind kind,
        long suggestedOffsetSamples,
        string message,
        bool stopAfterDecodedFields = false)
        : base(message)
    {
        Kind = kind;
        SuggestedOffsetSamples = suggestedOffsetSamples;
        StopAfterDecodedFields = stopAfterDecodedFields;
    }

    public TbcFieldDecodeRecoveryKind Kind { get; }

    public long SuggestedOffsetSamples { get; }

    public bool StopAfterDecodedFields { get; }
}
