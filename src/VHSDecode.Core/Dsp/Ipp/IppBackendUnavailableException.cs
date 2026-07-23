namespace VHSDecode.Core.Dsp.Ipp;

public enum IppBackendFailureKind
{
    UnsupportedProcessArchitecture,
    NativeLibraryNotFound,
    NativeLibraryInvalid,
    MissingExport,
    AbiVersionMismatch,
    RuntimeRejected
}

public sealed class IppBackendUnavailableException : InvalidOperationException
{
    internal IppBackendUnavailableException(
        IppBackendFailureKind failureKind,
        string detail,
        Exception? innerException = null)
        : base($"The explicit '{DspBackendParser.IppFastValue}' DSP backend is unavailable: {detail}", innerException)
    {
        FailureKind = failureKind;
    }

    public IppBackendFailureKind FailureKind { get; }
}
