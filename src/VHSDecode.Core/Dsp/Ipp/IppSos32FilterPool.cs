using System.Buffers;
using System.Collections.Concurrent;

namespace VHSDecode.Core.Dsp;

internal sealed class IppSos32FilterPool : IDisposable
{
    internal const int DefaultMaximumRetainedContexts = 12;
    private const int MaximumStackStateLength = 64;
    private readonly SosSection[] _sections;
    private readonly float[] _steadyState;
    private readonly ConcurrentStack<IppSos32> _contexts = new();
    private readonly int _maximumRetainedContexts;
    private int _retainedContextCount;
    private int _createdContextCount;
    private int _disposed;

    private IppSos32FilterPool(
        SosSection[] sections,
        int maximumRetainedContexts)
    {
        _sections = sections;
        _maximumRetainedContexts = maximumRetainedContexts;
        _steadyState = CreateSteadyState(sections);
    }

    internal int RetainedContextCount => Volatile.Read(ref _retainedContextCount);
    internal int CreatedContextCount => Volatile.Read(ref _createdContextCount);

    internal static IppSos32FilterPool? TryCreate(
        IReadOnlyList<SosSection>? sections,
        int maximumRetainedContexts = DefaultMaximumRetainedContexts)
    {
        if (sections is null || sections.Count == 0)
        {
            return null;
        }

        if (maximumRetainedContexts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedContexts));
        }

        var snapshot = new SosSection[sections.Count];
        for (int index = 0; index < sections.Count; index++)
        {
            SosSection section = sections[index];
            if ((float)section.A0 != 1.0F)
            {
                return null;
            }

            snapshot[index] = section;
        }

        return new IppSos32FilterPool(snapshot, maximumRetainedContexts);
    }

    internal void ApplyForwardBackwardInPlace(Span<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        IppSos32 context = Rent();
        float[]? rentedState = null;
        try
        {
            Span<float> scaledState = _steadyState.Length <= MaximumStackStateLength
                ? stackalloc float[_steadyState.Length]
                : (rentedState = ArrayPool<float>.Shared.Rent(_steadyState.Length))
                    .AsSpan(0, _steadyState.Length);

            ScaleInitialConditions(_steadyState, samples[0], scaledState);
            context.SetState(scaledState);
            context.ProcessInPlace(samples);
            samples.Reverse();

            ScaleInitialConditions(_steadyState, samples[0], scaledState);
            context.SetState(scaledState);
            context.ProcessInPlace(samples);
            samples.Reverse();
        }
        finally
        {
            if (rentedState is not null)
            {
                ArrayPool<float>.Shared.Return(rentedState);
            }

            Return(context);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_contexts.TryPop(out IppSos32? context))
        {
            Interlocked.Decrement(ref _retainedContextCount);
            context.Dispose();
        }
    }

    private IppSos32 Rent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_contexts.TryPop(out IppSos32? context))
        {
            Interlocked.Decrement(ref _retainedContextCount);
            return context;
        }

        Interlocked.Increment(ref _createdContextCount);
        return new IppSos32(_sections);
    }

    private void Return(IppSos32 context)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            context.Dispose();
            return;
        }

        int retained = Interlocked.Increment(ref _retainedContextCount);
        if (retained > _maximumRetainedContexts)
        {
            Interlocked.Decrement(ref _retainedContextCount);
            context.Dispose();
            return;
        }

        _contexts.Push(context);
        if (Volatile.Read(ref _disposed) != 0 && _contexts.TryPop(out IppSos32? popped))
        {
            Interlocked.Decrement(ref _retainedContextCount);
            popped.Dispose();
        }
    }

    private static float[] CreateSteadyState(IReadOnlyList<SosSection> sections)
    {
        var result = new float[checked(sections.Count * 2)];
        float scale = 1.0F;
        for (int index = 0; index < sections.Count; index++)
        {
            SosSection source = sections[index];
            float b0 = (float)source.B0;
            float b1 = (float)source.B1;
            float b2 = (float)source.B2;
            float a0 = (float)source.A0;
            float a1 = (float)source.A1;
            float a2 = (float)source.A2;
            float firstTerm = b1 - (a1 * b0);
            float secondTerm = b2 - (a2 * b0);
            float numeratorSum = (0.0F + firstTerm) + secondTerm;
            float denominatorSum = (1.0F + a1) + a2;
            float z0 = numeratorSum / denominatorSum;
            float z1 = ((1.0F + a1) * z0) - firstTerm;
            int stateOffset = index * 2;
            result[stateOffset] = scale * z0;
            result[stateOffset + 1] = scale * z1;

            float numeratorDc = ((0.0F + b0) + b1) + b2;
            float denominatorDc = ((0.0F + a0) + a1) + a2;
            scale *= numeratorDc / denominatorDc;
        }

        return result;
    }

    private static void ScaleInitialConditions(
        ReadOnlySpan<float> source,
        float scale,
        Span<float> destination)
    {
        for (int index = 0; index < source.Length; index += 2)
        {
            destination[index] = source[index] * scale;
            destination[index + 1] = source[index + 1] * scale;
        }
    }
}
