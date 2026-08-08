using System.Numerics;

namespace VHSDecode.Core.Dsp;

internal sealed class IppComplexFft64Pool : IDisposable
{
    private const int MaximumRetainedContexts = 32;
    private readonly object _sync = new();
    private readonly Dictionary<int, Stack<IppComplexFft64>> _available = [];
    private int _retainedContextCount;
    private bool _disposed;

    internal int RetainedContextCount
    {
        get
        {
            lock (_sync)
            {
                return _retainedContextCount;
            }
        }
    }

    internal void Forward(ReadOnlySpan<Complex> input, Span<Complex> output)
    {
        using Lease lease = Rent(input.Length);
        lease.Context.Forward(input, output);
    }

    internal void Inverse(ReadOnlySpan<Complex> input, Span<Complex> output)
    {
        using Lease lease = Rent(input.Length);
        lease.Context.Inverse(input, output);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Stack<IppComplexFft64> contexts in _available.Values)
            {
                while (contexts.TryPop(out IppComplexFft64? context))
                {
                    context.Dispose();
                }
            }

            _available.Clear();
            _retainedContextCount = 0;
        }
    }

    private Lease Rent(int length)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_available.TryGetValue(length, out Stack<IppComplexFft64>? contexts)
                && contexts.TryPop(out IppComplexFft64? context))
            {
                _retainedContextCount--;
                return new Lease(this, context);
            }
        }

        return new Lease(this, new IppComplexFft64(length));
    }

    private void Return(IppComplexFft64 context)
    {
        lock (_sync)
        {
            if (!_disposed && _retainedContextCount < MaximumRetainedContexts)
            {
                if (!_available.TryGetValue(context.Length, out Stack<IppComplexFft64>? contexts))
                {
                    contexts = new Stack<IppComplexFft64>();
                    _available.Add(context.Length, contexts);
                }

                contexts.Push(context);
                _retainedContextCount++;
                return;
            }
        }

        context.Dispose();
    }

    private sealed class Lease(IppComplexFft64Pool owner, IppComplexFft64 context) : IDisposable
    {
        private IppComplexFft64Pool? _owner = owner;

        internal IppComplexFft64 Context { get; } = context;

        public void Dispose()
        {
            IppComplexFft64Pool? activeOwner = Interlocked.Exchange(ref _owner, null);
            activeOwner?.Return(Context);
        }
    }
}
