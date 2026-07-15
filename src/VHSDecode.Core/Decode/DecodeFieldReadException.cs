using System.Globalization;

namespace VHSDecode.Core.Decode;

public sealed class DecodeFieldReadException : Exception
{
    public DecodeFieldReadException(long currentSample, Exception innerException)
        : this(
            currentSample,
            currentSample.ToString(CultureInfo.InvariantCulture),
            innerException)
    {
    }

    public DecodeFieldReadException(string currentSample, Exception innerException)
        : this(0, currentSample, innerException)
    {
    }

    private DecodeFieldReadException(
        long currentSample,
        string currentSampleText,
        Exception innerException)
        : base(innerException?.Message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSampleText);
        ArgumentNullException.ThrowIfNull(innerException);
        CurrentSample = currentSample;
        CurrentSampleText = currentSampleText;
    }

    public long CurrentSample { get; }

    public string CurrentSampleText { get; }
}
