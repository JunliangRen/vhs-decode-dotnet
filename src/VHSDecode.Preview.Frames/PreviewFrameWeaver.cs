using VHSDecode.Core.Decode;

namespace VHSDecode.Preview;

internal sealed class PreviewFrameWeaver(DecodeSession session, int width, int height, bool fullRaster = false)
{
    private readonly int _width = width;
    private readonly int _height = height;
    private readonly int _halfHeight = height / 2;
    private readonly int _chromaWidth = width / 2;
    private readonly int _chromaHeight = height / 2;
    private readonly PreviewFieldRenderer _renderer = new(session, width, height, fullRaster);
    private PreviewRenderedField? _firstField;
    private long _firstFieldStart;

    internal byte[]? AddField(TbcDecodedField field, bool isFirstField, out long frameStart)
    {
        PreviewRenderedField rendered = _renderer.Render(field, isFirstField);
        frameStart = 0;
        if (isFirstField)
        {
            _firstField = rendered;
            _firstFieldStart = field.StartSample;
            return null;
        }

        if (_firstField is not { } first)
        {
            return null;
        }

        _firstField = null;
        frameStart = Math.Max(_firstFieldStart, field.StartSample);
        return Weave(first, rendered);
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
