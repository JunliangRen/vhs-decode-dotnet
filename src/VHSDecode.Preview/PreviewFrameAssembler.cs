using VHSDecode.Core.Decode;

namespace VHSDecode.Preview;

internal sealed class PreviewFrameAssembler
{
    private readonly Stream _output;
    private readonly int _width;
    private readonly int _height;
    private readonly int _halfHeight;
    private readonly int _chromaWidth;
    private readonly int _chromaHeight;
    private readonly PreviewFieldRenderer _renderer;
    private readonly long _targetStartSample;
    private readonly long _halfFrameSamples;
    private readonly int _outputFrameCount;
    private PreviewRenderedField? _firstField;
    private long _firstFieldStart;
    private byte[]? _lastFrame;

    internal PreviewFrameAssembler(
        DecodeSession session,
        Stream output,
        int width,
        int height,
        long targetStartSample,
        int outputFrameCount)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (outputFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputFrameCount));
        }

        _output = output ?? throw new ArgumentNullException(nameof(output));
        _width = width;
        _height = height;
        _halfHeight = height / 2;
        _chromaWidth = width / 2;
        _chromaHeight = height / 2;
        _renderer = new PreviewFieldRenderer(session, width, height);
        _targetStartSample = targetStartSample;
        double framesPerSecond = session.Parameters.SysParams.GetProperty("FPS").GetDouble();
        _halfFrameSamples = Math.Max(
            1L,
            checked((long)Math.Round(
                session.DecodeSampleRateHz / (framesPerSecond * 2.0),
                MidpointRounding.AwayFromZero)));
        _outputFrameCount = outputFrameCount;
    }

    internal int WrittenFrameCount { get; private set; }

    internal int SampledFrameCount { get; private set; }

    internal void Accept(
        IReadOnlyList<(TbcDecodedField Field, TbcFieldOrderDecision Decision)> writes)
    {
        foreach ((TbcDecodedField field, TbcFieldOrderDecision decision) in writes)
        {
            if (SampledFrameCount >= _outputFrameCount)
            {
                return;
            }

            PreviewRenderedField rendered = _renderer.Render(field, decision.IsFirstField);
            if (decision.IsFirstField)
            {
                _firstField = rendered;
                _firstFieldStart = field.StartSample;
                continue;
            }

            if (_firstField is null)
            {
                continue;
            }

            byte[] frame = Weave(_firstField, rendered);
            long frameMidpoint = Math.Max(_firstFieldStart, field.StartSample) + _halfFrameSamples;
            _firstField = null;
            _lastFrame = frame;
            if (frameMidpoint < _targetStartSample)
            {
                continue;
            }

            WriteFrame(frame);
            SampledFrameCount++;
        }
    }

    internal void Complete()
    {
        byte[] fill = _lastFrame ?? CreateBlackFrame();
        while (WrittenFrameCount < _outputFrameCount)
        {
            WriteFrame(fill);
        }
    }

    private byte[] Weave(PreviewRenderedField first, PreviewRenderedField second)
    {
        int lumaLength = checked(_width * _height);
        int chromaLength = checked(_chromaWidth * _chromaHeight);
        var frame = new byte[checked(lumaLength + (chromaLength * 2))];
        var lumaDropouts = new bool[lumaLength];
        var chromaDropouts = new bool[chromaLength];

        WeavePlane(
            first.Luma,
            second.Luma,
            frame.AsSpan(0, lumaLength),
            _width,
            _halfHeight);
        WeavePlane(
            first.LumaDropouts,
            second.LumaDropouts,
            lumaDropouts,
            _width,
            _halfHeight);

        int fieldChromaHeight = _halfHeight / 2;
        WeavePlane(
            first.ChromaU,
            second.ChromaU,
            frame.AsSpan(lumaLength, chromaLength),
            _chromaWidth,
            fieldChromaHeight);
        WeavePlane(
            first.ChromaV,
            second.ChromaV,
            frame.AsSpan(lumaLength + chromaLength, chromaLength),
            _chromaWidth,
            fieldChromaHeight);
        WeavePlane(
            first.ChromaDropouts,
            second.ChromaDropouts,
            chromaDropouts,
            _chromaWidth,
            fieldChromaHeight);

        _ = PreviewDropoutConcealer.Apply(
            frame.AsSpan(0, lumaLength),
            lumaDropouts,
            _width,
            _height);
        _ = PreviewDropoutConcealer.Apply(
            frame.AsSpan(lumaLength, chromaLength),
            chromaDropouts,
            _chromaWidth,
            _chromaHeight);
        _ = PreviewDropoutConcealer.Apply(
            frame.AsSpan(lumaLength + chromaLength, chromaLength),
            chromaDropouts,
            _chromaWidth,
            _chromaHeight);
        return frame;
    }

    private void WriteFrame(byte[] frame)
    {
        _output.Write(frame);
        WrittenFrameCount++;
    }

    private byte[] CreateBlackFrame()
    {
        int lumaLength = checked(_width * _height);
        var frame = new byte[checked(lumaLength + ((_width / 2) * (_height / 2) * 2))];
        Array.Fill(frame, (byte)16, 0, lumaLength);
        Array.Fill(frame, (byte)128, lumaLength, frame.Length - lumaLength);
        return frame;
    }

    private static void WeavePlane<T>(
        ReadOnlySpan<T> first,
        ReadOnlySpan<T> second,
        Span<T> destination,
        int width,
        int fieldHeight)
    {
        int expectedFieldLength = checked(width * fieldHeight);
        if (first.Length != expectedFieldLength
            || second.Length != expectedFieldLength
            || destination.Length != checked(expectedFieldLength * 2))
        {
            throw new ArgumentException("Preview field plane dimensions do not match.");
        }

        for (int line = 0; line < fieldHeight; line++)
        {
            int source = line * width;
            first.Slice(source, width).CopyTo(destination.Slice((line * 2) * width, width));
            second.Slice(source, width).CopyTo(destination.Slice(((line * 2) + 1) * width, width));
        }
    }

}
