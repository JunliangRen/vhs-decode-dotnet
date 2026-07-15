namespace VHSDecode.Core.Decode;

public sealed class DecodeFieldReadException : Exception
{
    public DecodeFieldReadException(long currentSample, Exception innerException)
        : base(innerException?.Message, innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        CurrentSample = currentSample;
    }

    public long CurrentSample { get; }
}
