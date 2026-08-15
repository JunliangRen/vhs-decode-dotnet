namespace VHSDecode.Preview;

public sealed class PreviewSegmentCache : IAsyncDisposable
{
    private readonly IPreviewSegmentProvider _provider;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<int, CacheEntry> _entries = [];
    private readonly Dictionary<int, Task<PreviewSegmentWindow>> _inflight = [];
    private readonly LinkedList<int> _lru = [];
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public PreviewSegmentCache(IPreviewSegmentProvider provider, int capacity)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    public async Task<PreviewSegmentWindow> GetWindowAsync(
        int windowIndex,
        CancellationToken cancellationToken = default)
    {
        Task<PreviewSegmentWindow> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(windowIndex, out CacheEntry? cached))
            {
                Touch(cached);
                return cached.Window;
            }

            if (!_inflight.TryGetValue(windowIndex, out task!))
            {
                task = GenerateAndStoreAsync(windowIndex);
                _inflight.Add(windowIndex, task);
            }
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreviewSegmentWindow> GenerateAndStoreAsync(int windowIndex)
    {
        try
        {
            PreviewSegmentWindow window = await _provider.GenerateWindowAsync(
                windowIndex,
                _shutdown.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed)
                {
                    return window;
                }

                if (!_entries.ContainsKey(windowIndex))
                {
                    LinkedListNode<int> node = _lru.AddFirst(windowIndex);
                    _entries.Add(windowIndex, new CacheEntry(window, node));
                    while (_entries.Count > _capacity)
                    {
                        LinkedListNode<int> oldest = _lru.Last!;
                        _lru.RemoveLast();
                        _entries.Remove(oldest.Value);
                    }
                }
            }

            return window;
        }
        finally
        {
            lock (_gate)
            {
                _inflight.Remove(windowIndex);
            }
        }
    }

    private void Touch(CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] inflight;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            inflight = [.. _inflight.Values];
            _entries.Clear();
            _lru.Clear();
        }

        _shutdown.Cancel();

        try
        {
            await Task.WhenAll(inflight).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Requests observe generation failures; disposal only drains outstanding work.
        }
        finally
        {
            _shutdown.Dispose();
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record CacheEntry(
        PreviewSegmentWindow Window,
        LinkedListNode<int> Node);
}
