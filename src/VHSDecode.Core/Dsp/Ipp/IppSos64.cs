using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.Dsp;

public sealed unsafe class IppSos64 : IDisposable
{
    private readonly object _sync = new();
    private readonly IppSos64SafeHandle _context;

    public IppSos64(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<double> initialState = default)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Count == 0)
        {
            throw new ArgumentException("SOS cascade must contain at least one section.", nameof(sections));
        }

        int stateLength = checked(sections.Count * 2);
        if (!initialState.IsEmpty && initialState.Length != stateLength)
        {
            throw new ArgumentException(
                $"Initial state length must equal twice the section count ({stateLength}).",
                nameof(initialState));
        }

        var nativeSections = new IppSos64Section[sections.Count];
        for (int index = 0; index < sections.Count; index++)
        {
            SosSection section = sections[index];
            if (section.A0 == 0.0)
            {
                throw new ArgumentException(
                    $"SOS section {index} denominator a0 must not be zero.",
                    nameof(sections));
            }

            nativeSections[index] = new IppSos64Section(section);
        }

        _ = IppRuntime.RequireAvailable();

        nint nativeContext;
        int status;
        try
        {
            fixed (IppSos64Section* sectionsPointer = nativeSections)
            fixed (double* statePointer = initialState)
            {
                status = IppNativeMethods.Sos64Create(
                    sectionsPointer,
                    nativeSections.Length,
                    statePointer,
                    initialState.Length,
                    out nativeContext);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            throw IppRuntime.CreateLoaderException(exception);
        }

        IppStatus.ThrowIfFailed(status, "sos64_create");
        if (nativeContext == 0)
        {
            throw new InvalidOperationException(
                "Native IPP sos64_create reported success but returned a null context.");
        }

        SectionCount = sections.Count;
        StateLength = stateLength;
        _context = IppSos64SafeHandle.FromNativeHandle(nativeContext);
    }

    public int SectionCount { get; }
    public int StateLength { get; }

    public void Process(ReadOnlySpan<double> input, Span<double> output)
    {
        IppFilterSpanValidation.ValidateProcessBuffers(input, output);
        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (double* inputPointer = input)
            fixed (double* outputPointer = output)
            {
                int status = IppNativeMethods.Sos64Process(
                    _context,
                    inputPointer,
                    outputPointer,
                    input.Length);
                IppStatus.ThrowIfFailed(status, "sos64_process");
            }
        }
    }

    public void ProcessInPlace(Span<double> samples)
        => Process(samples, samples);

    public double[] GetState()
    {
        var state = new double[StateLength];
        GetState(state);
        return state;
    }

    public void GetState(Span<double> state)
    {
        if (state.Length != StateLength)
        {
            throw new ArgumentException(
                $"State length must equal {StateLength}.",
                nameof(state));
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (double* statePointer = state)
            {
                int status = IppNativeMethods.Sos64GetState(
                    _context,
                    statePointer,
                    state.Length);
                IppStatus.ThrowIfFailed(status, "sos64_get_state");
            }
        }
    }

    public void SetState(ReadOnlySpan<double> state)
    {
        if (state.Length != StateLength)
        {
            throw new ArgumentException(
                $"State length must equal {StateLength}.",
                nameof(state));
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (double* statePointer = state)
            {
                int status = IppNativeMethods.Sos64SetState(
                    _context,
                    statePointer,
                    state.Length);
                IppStatus.ThrowIfFailed(status, "sos64_set_state");
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            IppStatus.ThrowIfFailed(
                IppNativeMethods.Sos64Reset(_context),
                "sos64_reset");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _context.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_context.IsClosed, this);
}
