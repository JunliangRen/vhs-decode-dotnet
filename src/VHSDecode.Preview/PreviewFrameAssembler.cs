using VHSDecode.Core.Decode;

namespace VHSDecode.Preview;

internal sealed class PreviewFrameAssembler
{
    private readonly Stream _output;
    private readonly int _width;
    private readonly int _height;
    private readonly PreviewFrameWeaver _weaver;
    private readonly long _targetStartSample;
    private readonly long _halfFrameSamples;
    private readonly int _outputFrameCount;
    private byte[]? _lastFrame;

    internal PreviewFrameAssembler(
        DecodeSession session,
        Stream output,
        int width,
        int height,
        long targetStartSample,
        int outputFrameCount, bool fullRaster = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (outputFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputFrameCount));
        }

        _output = output ?? throw new ArgumentNullException(nameof(output));
        _width = width;
        _height = height;
        _weaver = new PreviewFrameWeaver(session, width, height, fullRaster);
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

            byte[]? frame = _weaver.AddField(field, decision.IsFirstField, out long frameStart);
            if (frame is null)
            {
                continue;
            }

            long frameMidpoint = frameStart + _halfFrameSamples;
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
}
