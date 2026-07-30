using System.Collections.Concurrent;

namespace VHSDecode.Core.Dsp;

internal sealed class SingleCreationCache<TKey, TValue>
    where TKey : notnull
{
    private sealed class CreationGate
    {
        public int OwnerThreadId { get; set; }
    }

    private readonly ConcurrentDictionary<TKey, TValue> _values = new();
    private readonly ConcurrentDictionary<TKey, CreationGate> _creationGates = new();

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        if (_values.TryGetValue(key, out TValue? value))
        {
            return value;
        }

        // GetOrAdd can invoke an expensive factory more than once on a cold key.
        CreationGate gate = _creationGates.GetOrAdd(key, static _ => new CreationGate());
        TValue result;
        lock (gate)
        {
            if (_values.TryGetValue(key, out value))
            {
                result = value;
            }
            else
            {
                int threadId = Environment.CurrentManagedThreadId;
                if (gate.OwnerThreadId == threadId)
                {
                    throw new InvalidOperationException(
                        "A value factory cannot re-enter the same cache key.");
                }

                gate.OwnerThreadId = threadId;
                try
                {
                    TValue created = valueFactory(key);
                    result = _values.GetOrAdd(key, created);
                }
                finally
                {
                    gate.OwnerThreadId = 0;
                }
            }
        }

        // Once a value is published, future callers use the lock-free read path.
        _creationGates.TryRemove(key, out _);
        return result;
    }
}
