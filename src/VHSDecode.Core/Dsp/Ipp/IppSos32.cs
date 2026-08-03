using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.Dsp;

internal sealed unsafe class IppSos32 : IDisposable
{
    private readonly object _sync = new();
    private readonly IppSos32SafeHandle _context;

    internal IppSos32(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<float> initialState = default)
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

        var nativeSections = new IppSos32Section[sections.Count];
        for (int index = 0; index < sections.Count; index++)
        {
            SosSection section = sections[index];
            if ((float)section.A0 == 0.0F)
            {
                throw new ArgumentException(
                    $"SOS section {index} denominator a0 must not round to zero in float32.",
                    nameof(sections));
            }

            nativeSections[index] = new IppSos32Section(section);
        }

        _ = IppRuntime.RequireAvailable();

        nint nativeContext;
        int status;
        try
        {
            fixed (IppSos32Section* sectionsPointer = nativeSections)
            fixed (float* statePointer = initialState)
            {
                status = IppNativeMethods.Sos32Create(
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

        IppStatus.ThrowIfFailed(status, "sos32_create");
        if (nativeContext == 0)
        {
            throw new InvalidOperationException(
                "Native IPP sos32_create reported success but returned a null context.");
        }

        SectionCount = sections.Count;
        StateLength = stateLength;
        _context = IppSos32SafeHandle.FromNativeHandle(nativeContext);
    }

    internal int SectionCount { get; }
    internal int StateLength { get; }

    internal void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        IppFilterSpanValidation.ValidateProcessBuffers(input, output);
        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (float* inputPointer = input)
            fixed (float* outputPointer = output)
            {
                int status = IppNativeMethods.Sos32Process(
                    _context,
                    inputPointer,
                    outputPointer,
                    input.Length);
                IppStatus.ThrowIfFailed(status, "sos32_process");
            }
        }
    }

    internal void ProcessInPlace(Span<float> samples)
        => Process(samples, samples);

    internal float[] GetState()
    {
        var state = new float[StateLength];
        GetState(state);
        return state;
    }

    internal void GetState(Span<float> state)
    {
        ValidateStateLength(state.Length, nameof(state));
        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (float* statePointer = state)
            {
                int status = IppNativeMethods.Sos32GetState(
                    _context,
                    statePointer,
                    state.Length);
                IppStatus.ThrowIfFailed(status, "sos32_get_state");
            }
        }
    }

    internal void SetState(ReadOnlySpan<float> state)
    {
        ValidateStateLength(state.Length, nameof(state));
        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (float* statePointer = state)
            {
                int status = IppNativeMethods.Sos32SetState(
                    _context,
                    statePointer,
                    state.Length);
                IppStatus.ThrowIfFailed(status, "sos32_set_state");
            }
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            IppStatus.ThrowIfFailed(
                IppNativeMethods.Sos32Reset(_context),
                "sos32_reset");
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

    private void ValidateStateLength(int length, string parameterName)
    {
        if (length != StateLength)
        {
            throw new ArgumentException(
                $"State length must equal {StateLength}.",
                parameterName);
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_context.IsClosed, this);
}
