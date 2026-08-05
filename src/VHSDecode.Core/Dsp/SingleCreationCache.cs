using System.Collections.Concurrent;

namespace VHSDecode.Core.Dsp;

internal sealed class SingleCreationCache<TKey, TValue>
    where TKey : notnull
{
    private sealed class CreationGate
    {
        public int OwnerThreadId { get; set; }
    }

    private sealed class BoundedState
    {
        public BoundedState(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }

        public object Gate { get; } = new();

        public HashSet<TKey> KeysBeingCreated { get; } = [];

        public Queue<TKey> InsertionOrder { get; } = new();
    }

    private readonly ConcurrentDictionary<TKey, TValue> _values = new();
    private readonly ConcurrentDictionary<TKey, CreationGate> _creationGates = new();
    private readonly BoundedState? _boundedState;

    public SingleCreationCache(int? capacity = null)
    {
        if (capacity is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (capacity is int boundedCapacity)
        {
            _boundedState = new BoundedState(boundedCapacity);
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        if (_values.TryGetValue(key, out TValue? value))
        {
            return value;
        }

        return _boundedState is { } boundedState
            ? GetOrAddBounded(key, valueFactory, boundedState)
            : GetOrAddUnbounded(key, valueFactory);
    }

    private TValue GetOrAddBounded(
        TKey key,
        Func<TKey, TValue> valueFactory,
        BoundedState state)
    {
        lock (state.Gate)
        {
            if (_values.TryGetValue(key, out TValue? value))
            {
                return value;
            }

            if (!state.KeysBeingCreated.Add(key))
            {
                throw new InvalidOperationException(
                    "A value factory cannot re-enter the same cache key.");
            }

            try
            {
                TValue created = valueFactory(key);
                while (_values.Count >= state.Capacity)
                {
                    TKey evictedKey = state.InsertionOrder.Dequeue();
                    _values.TryRemove(evictedKey, out _);
                }

                _values.TryAdd(key, created);
                state.InsertionOrder.Enqueue(key);
                return created;
            }
            finally
            {
                state.KeysBeingCreated.Remove(key);
            }
        }
    }

    private TValue GetOrAddUnbounded(TKey key, Func<TKey, TValue> valueFactory)
    {
        // GetOrAdd can invoke an expensive factory more than once on a cold key.
        CreationGate gate = _creationGates.GetOrAdd(key, static _ => new CreationGate());
        TValue result;
        lock (gate)
        {
            if (_values.TryGetValue(key, out TValue? value))
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
