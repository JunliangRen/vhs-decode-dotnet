namespace VHSDecode.Preview;

public sealed class PreviewSegmentCache : IAsyncDisposable
{
    private readonly IPreviewSegmentProvider _provider;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<int, CacheEntry> _entries = [];
    private readonly Dictionary<int, InflightEntry> _inflight = [];
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
        while (true)
        {
            InflightEntry? entry = null;
            Task<PreviewSegmentWindow>? abandonedGeneration = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.TryGetValue(windowIndex, out CacheEntry? cached))
                {
                    Touch(cached);
                    return cached.Window;
                }

                if (_inflight.TryGetValue(windowIndex, out entry))
                {
                    if (entry.Abandoned)
                    {
                        abandonedGeneration = entry.GenerationTask;
                    }
                    else
                    {
                        entry.WaiterCount++;
                    }
                }
                else
                {
                    var generationCancellation = CancellationTokenSource
                        .CreateLinkedTokenSource(_shutdown.Token);
                    entry = new InflightEntry(generationCancellation)
                    {
                        WaiterCount = 1
                    };
                    _inflight.Add(windowIndex, entry);
                    entry.GenerationTask = GenerateAndStoreAsync(windowIndex, entry);
                }
            }

            if (abandonedGeneration is not null)
            {
                try
                {
                    await abandonedGeneration.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A new waiter retries after the abandoned generation drains.
                }

                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            try
            {
                return await entry!.GenerationTask
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ReleaseWaiter(windowIndex, entry!);
            }
        }
    }

    private async Task<PreviewSegmentWindow> GenerateAndStoreAsync(
        int windowIndex,
        InflightEntry entry)
    {
        try
        {
            PreviewSegmentWindow window = await _provider.GenerateWindowAsync(
                windowIndex,
                entry.GenerationCancellation.Token).ConfigureAwait(false);
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
                if (_inflight.TryGetValue(windowIndex, out InflightEntry? current)
                    && ReferenceEquals(current, entry))
                {
                    _inflight.Remove(windowIndex);
                }
            }

            entry.GenerationCancellation.Dispose();
        }
    }

    private void ReleaseWaiter(int windowIndex, InflightEntry entry)
    {
        bool cancelGeneration = false;
        lock (_gate)
        {
            if (_inflight.TryGetValue(windowIndex, out InflightEntry? current)
                && ReferenceEquals(current, entry))
            {
                entry.WaiterCount--;
                cancelGeneration = entry.WaiterCount == 0
                    && !entry.GenerationTask.IsCompleted;
                if (cancelGeneration)
                {
                    entry.Abandoned = true;
                }
            }
        }

        if (cancelGeneration)
        {
            try
            {
                entry.GenerationCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The generation completed between the waiter release and cancellation.
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
            inflight = [.. _inflight.Values.Select(entry => entry.GenerationTask)];
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

    private sealed class InflightEntry(CancellationTokenSource generationCancellation)
    {
        internal CancellationTokenSource GenerationCancellation { get; } = generationCancellation;

        internal Task<PreviewSegmentWindow> GenerationTask { get; set; } = null!;

        internal int WaiterCount { get; set; }

        internal bool Abandoned { get; set; }
    }
}
