namespace VHSDecode.Core.Rf;

// PyAV exposes each converted plane's aligned capacity, including its zero-filled tail.
internal sealed class PyAvAudioPlanePaddingStream : Stream
{
    private readonly Stream _source;
    private readonly int _logicalFrameBytes;
    private readonly byte[] _frame;
    private int _initialSkipBytes;
    private int _frameOffset;
    private int _frameLength;
    private bool _disposed;

    public PyAvAudioPlanePaddingStream(
        Stream source,
        int logicalFrameSamples,
        int paddedFrameSamples,
        int initialSkipSamples = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Source stream must be readable.", nameof(source));
        }

        if (logicalFrameSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalFrameSamples),
                "Logical frame length must be positive.");
        }

        if (paddedFrameSamples < logicalFrameSamples)
        {
            throw new ArgumentOutOfRangeException(
                nameof(paddedFrameSamples),
                "Padded frame length cannot be shorter than the logical frame.");
        }

        if (initialSkipSamples < 0 || initialSkipSamples >= paddedFrameSamples)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialSkipSamples),
                "Initial skip must address a sample inside the padded frame.");
        }

        _source = source;
        _logicalFrameBytes = checked(logicalFrameSamples * sizeof(short));
        _frame = new byte[checked(paddedFrameSamples * sizeof(short))];
        _initialSkipBytes = checked(initialSkipSamples * sizeof(short));
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal static int CalculatePaddedFrameSamples(int logicalFrameSamples)
    {
        if (logicalFrameSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalFrameSamples),
                "Logical frame length must be positive.");
        }

        return checked(((logicalFrameSamples + 31) & ~31) + 32);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int total = 0;
        while (!buffer.IsEmpty)
        {
            if (_frameOffset >= _frameLength && !LoadFrame())
            {
                break;
            }

            int count = Math.Min(buffer.Length, _frameLength - _frameOffset);
            _frame.AsSpan(_frameOffset, count).CopyTo(buffer);
            _frameOffset += count;
            buffer = buffer[count..];
            total += count;
        }

        return total;
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    private bool LoadFrame()
    {
        Array.Clear(_frame);
        int logicalBytes = 0;
        while (logicalBytes < _logicalFrameBytes)
        {
            int read = _source.Read(_frame.AsSpan(
                logicalBytes,
                _logicalFrameBytes - logicalBytes));
            if (read == 0)
            {
                break;
            }

            logicalBytes += read;
        }

        if (logicalBytes == 0)
        {
            _frameOffset = 0;
            _frameLength = 0;
            return false;
        }

        _frameOffset = _initialSkipBytes;
        _initialSkipBytes = 0;
        _frameLength = _frame.Length;
        return true;
    }
}
