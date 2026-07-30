using System.Collections.Concurrent;

namespace VHSDecode.Core.Dsp;

internal sealed class SingleCreationCache<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _values = new();
    // GetOrAdd can invoke an expensive factory more than once on a cold key.
    private readonly object _creationLock = new();

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        if (_values.TryGetValue(key, out TValue? value))
        {
            return value;
        }

        lock (_creationLock)
        {
            if (_values.TryGetValue(key, out value))
            {
                return value;
            }

            value = valueFactory(key);
            _values[key] = value;
            return value;
        }
    }
}
