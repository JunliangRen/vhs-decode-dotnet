using System.Runtime.ExceptionServices;

namespace VHSDecode.Core.Rf;

// One producer and one foreground reader. The ring only contains unread pipe
// bytes. The loader continues to own the logical cursor and rewind history.
internal sealed class BoundedPcmReadAheadStream : Stream
{
    internal const int DefaultCapacity = 2 * 1024 * 1024;
    private const int MaximumRead = 256 * 1024;
    private readonly Stream _source;
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _wakeOnCancellation;
    private int _head;
    private int _count;
    private int _disposed;
    private bool _stopping;
    private bool _ended;
    private ExceptionDispatchInfo? _failure;

    internal BoundedPcmReadAheadStream(Stream source, CancellationToken token, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _source = source;
        _token = token;
        _buffer = GC.AllocateUninitializedArray<byte>(capacity);
        _wakeOnCancellation = token.UnsafeRegister(static state => ((BoundedPcmReadAheadStream)state!).Wake(), this);
        try
        {
            Producer = Task.Factory.StartNew(Pump, CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
        catch
        {
            _wakeOnCancellation.Dispose();
            throw;
        }
    }

    internal Task Producer { get; }

    internal int BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (destination.IsEmpty)
            {
                return 0;
            }

            while (_count == 0 && !_ended && !_stopping && !_token.IsCancellationRequested)
            {
                Monitor.Wait(_gate);
            }

            _token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_count == 0)
            {
                _failure?.Throw();
                return 0;
            }

            int length = Math.Min(destination.Length, _count);
            int first = Math.Min(length, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(destination);
            _buffer.AsSpan(0, length - first).CopyTo(destination[first..]);
            _head = (_head + length) % _buffer.Length;
            _count -= length;
            Monitor.PulseAll(_gate);
            return length;
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void Wake()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    private void Pump()
    {
        try
        {
            while (true)
            {
                int start;
                int length;
                lock (_gate)
                {
                    while (_count == _buffer.Length && !_stopping && !_token.IsCancellationRequested)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_stopping || _token.IsCancellationRequested)
                    {
                        return;
                    }

                    // The consumer can only advance the head. The free tail
                    // chosen here remains exclusive until this read publishes.
                    start = (_head + _count) % _buffer.Length;
                    length = Math.Min(Math.Min(_buffer.Length - _count, _buffer.Length - start), MaximumRead);
                }

                int read = _source.Read(_buffer, start, length);
                lock (_gate)
                {
                    if (_stopping || _token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (read == 0)
                    {
                        _ended = true;
                        Monitor.PulseAll(_gate);
                        return;
                    }

                    _count += read;
                    Monitor.PulseAll(_gate);
                }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (!_stopping)
                {
                    _failure = ExceptionDispatchInfo.Capture(error);
                    _ended = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            RequestStop();
            _wakeOnCancellation.Dispose();
            _source.Dispose();
        }

        base.Dispose(disposing);
    }
}
