using System.Runtime.ExceptionServices;
using VHSDecode.Core.Rf;

namespace VHSDecode.Core.Decode;

internal static class ExactBufferedCpuTail
{
    [ThreadStatic]
    private static Scope? _active;

    internal static bool IsActive => _active is not null;

    internal static Scope Open(Func<bool> pollWriter, Action commitPrior, CancellationToken cancellationToken)
    {
        var next = new Scope(_active, pollWriter, commitPrior, cancellationToken);
        _active = next;
        return next;
    }

    internal static bool TryLoad(IRfSampleLoader loader, long sample, int length, out double[]? input)
    {
        input = null;
        if (_active is not { } scope)
        {
            return false;
        }

        if (scope.CancellationToken.IsCancellationRequested || !scope.WriterPending())
        {
            Fence();
            return false;
        }

        bool loaded = loader switch
        {
            LibsndfilePcm16SampleLoader native => native.TryConsumeExact(sample, length, out input),
            StaleRawFlacFallbackSampleLoader fallback => fallback.TryConsumeExact(sample, length, out input),
            _ => false,
        };
        if (!loaded)
        {
            Fence();
        }

        return loaded;
    }

    internal static void Fence()
    {
        if (_active is not { } scope)
        {
            return;
        }

        // Each TryConsumeExact has returned and released every acquired lock.
        _active = null;
        scope.JoinPrior();
    }

    internal sealed class Scope(
        Scope? previous, Func<bool> pollWriter, Action commitPrior, CancellationToken cancellationToken) : IDisposable
    {
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal ExceptionDispatchInfo? PriorFailure { get; private set; }

        internal bool WriterPending()
        {
            try
            {
                return pollWriter();
            }
            catch (Exception failure)
            {
                PriorFailure = ExceptionDispatchInfo.Capture(failure);
                throw;
            }
        }

        internal void JoinPrior()
        {
            try
            {
                commitPrior();
            }
            catch (Exception failure)
            {
                PriorFailure = ExceptionDispatchInfo.Capture(failure);
                throw;
            }

            CancellationToken.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            _active = previous;
        }
    }
}
