namespace VHSDecode.Core.Rf;

internal readonly record struct PyAvAudioFrameGeometry(
    int LogicalSamples,
    long? PresentationRfSample);

// PyAV exposes each converted plane's aligned capacity in addition to its logical samples.
internal sealed class PyAvAudioPlanePaddingStream : Stream
{
    private readonly Stream _source;
    private readonly int _fixedLogicalFrameSamples;
    private readonly int _fixedPaddedFrameSamples;
    private readonly Func<PyAvAudioFrameGeometry?>? _nextFrame;
    private readonly long _targetSample;
    private byte[] _frame;
    private long _remainingInitialSkipSamples;
    private bool _initialSkipResolved;
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
        _fixedLogicalFrameSamples = logicalFrameSamples;
        _fixedPaddedFrameSamples = paddedFrameSamples;
        _frame = new byte[checked(paddedFrameSamples * sizeof(short))];
        _remainingInitialSkipSamples = initialSkipSamples;
        _initialSkipResolved = true;
    }

    public PyAvAudioPlanePaddingStream(
        Stream source,
        Func<PyAvAudioFrameGeometry?> nextFrame,
        long targetSample)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(nextFrame);
        if (!source.CanRead)
        {
            throw new ArgumentException("Source stream must be readable.", nameof(source));
        }

        if (targetSample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSample));
        }

        _source = source;
        _nextFrame = nextFrame;
        _targetSample = targetSample;
        _frame = [];
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
        while (true)
        {
            PyAvAudioFrameGeometry? geometry = NextFrameGeometry();
            if (geometry is null)
            {
                _frameOffset = 0;
                _frameLength = 0;
                return false;
            }

            int logicalFrameSamples = geometry.Value.LogicalSamples;
            if (logicalFrameSamples <= 0)
            {
                throw new InvalidDataException("FFmpeg reported an invalid audio frame length.");
            }

            int paddedFrameSamples = _nextFrame is null
                ? _fixedPaddedFrameSamples
                : CalculatePaddedFrameSamples(logicalFrameSamples);
            int logicalFrameBytes = checked(logicalFrameSamples * sizeof(short));
            int paddedFrameBytes = checked(paddedFrameSamples * sizeof(short));
            if (_frame.Length != paddedFrameBytes)
            {
                _frame = new byte[paddedFrameBytes];
            }
            else
            {
                Array.Clear(_frame);
            }

            int logicalBytes = 0;
            while (logicalBytes < logicalFrameBytes)
            {
                int read = _source.Read(_frame.AsSpan(
                    logicalBytes,
                    logicalFrameBytes - logicalBytes));
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

            if (_nextFrame is not null && logicalBytes != logicalFrameBytes)
            {
                throw new InvalidDataException(
                    "FFmpeg ended in the middle of a reported audio frame.");
            }

            ResolveInitialSkip(geometry.Value);
            long skippedSamples = Math.Min(_remainingInitialSkipSamples, paddedFrameSamples);
            _remainingInitialSkipSamples -= skippedSamples;
            if (skippedSamples == paddedFrameSamples)
            {
                continue;
            }

            _frameOffset = checked((int)skippedSamples * sizeof(short));
            _frameLength = _frame.Length;
            return true;
        }
    }

    private PyAvAudioFrameGeometry? NextFrameGeometry()
        => _nextFrame is null
            ? new PyAvAudioFrameGeometry(_fixedLogicalFrameSamples, null)
            : _nextFrame();

    private void ResolveInitialSkip(PyAvAudioFrameGeometry geometry)
    {
        if (_initialSkipResolved)
        {
            return;
        }

        _remainingInitialSkipSamples = geometry.PresentationRfSample.HasValue
            ? Math.Max(0, _targetSample - geometry.PresentationRfSample.Value)
            : 0;
        _initialSkipResolved = true;
    }
}
