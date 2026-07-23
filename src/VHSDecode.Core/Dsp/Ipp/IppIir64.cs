using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.Dsp;

public sealed unsafe class IppIir64 : IDisposable
{
    private readonly object _sync = new();
    private readonly IppIir64SafeHandle _context;

    public IppIir64(
        ReadOnlySpan<double> numerator,
        ReadOnlySpan<double> denominator,
        ReadOnlySpan<double> initialState = default)
    {
        if (numerator.IsEmpty)
        {
            throw new ArgumentException("IIR numerator must not be empty.", nameof(numerator));
        }

        if (denominator.IsEmpty)
        {
            throw new ArgumentException("IIR denominator must not be empty.", nameof(denominator));
        }

        if (denominator[0] == 0.0)
        {
            throw new ArgumentException("IIR denominator a0 must not be zero.", nameof(denominator));
        }

        int order = Math.Max(numerator.Length, denominator.Length) - 1;
        if (order < 1)
        {
            throw new ArgumentException("IIR order must be at least one.", nameof(numerator));
        }

        if (!initialState.IsEmpty && initialState.Length != order)
        {
            throw new ArgumentException(
                $"Initial state length must equal the IIR order ({order}).",
                nameof(initialState));
        }

        _ = IppRuntime.RequireAvailable();

        nint nativeContext;
        int status;
        try
        {
            fixed (double* numeratorPointer = numerator)
            fixed (double* denominatorPointer = denominator)
            fixed (double* statePointer = initialState)
            {
                status = IppNativeMethods.Iir64Create(
                    numeratorPointer,
                    numerator.Length,
                    denominatorPointer,
                    denominator.Length,
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

        IppStatus.ThrowIfFailed(status, "iir64_create");
        if (nativeContext == 0)
        {
            throw new InvalidOperationException(
                "Native IPP iir64_create reported success but returned a null context.");
        }

        Order = order;
        _context = IppIir64SafeHandle.FromNativeHandle(nativeContext);
    }

    public int Order { get; }
    public int StateLength => Order;

    public void Process(ReadOnlySpan<double> input, Span<double> output)
    {
        IppFilterSpanValidation.ValidateProcessBuffers(input, output);
        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (double* inputPointer = input)
            fixed (double* outputPointer = output)
            {
                int status = IppNativeMethods.Iir64Process(
                    _context,
                    inputPointer,
                    outputPointer,
                    input.Length);
                IppStatus.ThrowIfFailed(status, "iir64_process");
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
                int status = IppNativeMethods.Iir64GetState(
                    _context,
                    statePointer,
                    state.Length);
                IppStatus.ThrowIfFailed(status, "iir64_get_state");
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
                int status = IppNativeMethods.Iir64SetState(
                    _context,
                    statePointer,
                    state.Length);
                IppStatus.ThrowIfFailed(status, "iir64_set_state");
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            IppStatus.ThrowIfFailed(
                IppNativeMethods.Iir64Reset(_context),
                "iir64_reset");
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
