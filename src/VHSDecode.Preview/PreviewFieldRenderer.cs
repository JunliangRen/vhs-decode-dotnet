using VHSDecode.Core.Decode;
using VHSDecode.Core.Formats;

namespace VHSDecode.Preview;

internal sealed record PreviewRenderedField(
    byte[] Luma,
    byte[] ChromaU,
    byte[] ChromaV,
    bool[] LumaDropouts,
    bool[] ChromaDropouts);

internal sealed class PreviewFieldRenderer
{
    private const double MinimumBurstMagnitude = 64.0;
    private const double NtscUScale = -3.8 / 256.0;
    private const double NtscVScale = -2.75 / 256.0;
    private const double PalUScale = -2.445 / 256.0;
    private const double PalVScale = 1.733 / 256.0;
    private readonly int _width;
    private readonly int _halfHeight;
    private readonly int _chromaWidth;
    private readonly int _chromaHeight;
    private readonly int _sourceLineLength;
    private readonly int _sourceFirstLine;
    private readonly int[] _sourceLines;
    private readonly int[] _sourceColumns;
    private readonly int[] _chromaStarts;
    private readonly int[] _chromaEnds;
    private readonly int _burstStart;
    private readonly int _burstEnd;
    private readonly byte[] _lumaLut;
    private readonly bool _palChroma;
    private readonly bool _ntscChroma;

    internal PreviewFieldRenderer(
        DecodeSession session,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 3) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Preview dimensions must be positive, even-width, and divisible by four in height.");
        }

        _width = width;
        _halfHeight = height / 2;
        _chromaWidth = width / 2;
        _chromaHeight = height / 4;
        _sourceLineLength = session.TbcFrameSpec.OutputLineLength;
        string normalizedSystem = FormatCatalog.NormalizeSystem(session.System);
        string parentSystem = FormatCatalog.ParentSystem(session.System);
        bool ntscGeometry = normalizedSystem.StartsWith("NTSC", StringComparison.Ordinal)
            || parentSystem == "NTSC";
        _sourceFirstLine = ntscGeometry ? 20 : 22;
        _sourceLines = BuildSourceLines(
            _halfHeight,
            _sourceFirstLine,
            session.TbcFrameSpec.OutputLineCount);
        int activeStart = Math.Clamp(
            session.TbcFrameSpec.ActiveVideoStart ?? 0,
            0,
            _sourceLineLength - 1);
        int activeEnd = Math.Clamp(
            session.TbcFrameSpec.ActiveVideoEnd ?? _sourceLineLength,
            activeStart + 1,
            _sourceLineLength);
        _sourceColumns = BuildSourceColumns(width, activeStart, activeEnd);
        (_chromaStarts, _chromaEnds) = BuildChromaRanges(
            _chromaWidth,
            activeStart,
            activeEnd);
        _burstStart = Math.Clamp(
            session.TbcFrameSpec.ColourBurstStart ?? 0,
            2,
            _sourceLineLength);
        _burstEnd = Math.Clamp(
            session.TbcFrameSpec.ColourBurstEnd ?? 0,
            _burstStart,
            _sourceLineLength);
        _lumaLut = BuildLumaLut(session);

        _palChroma = normalizedSystem.StartsWith("PAL", StringComparison.Ordinal);
        _ntscChroma = !_palChroma && ntscGeometry;
    }

    internal PreviewRenderedField Render(TbcDecodedField field, bool isFirstField)
    {
        ArgumentNullException.ThrowIfNull(field);
        bool separateChroma = field.ChromaSamples is { Length: > 0 } chroma
            && chroma.Length >= field.Samples.Length;
        var luma = new byte[checked(_width * _halfHeight)];
        RenderLuma(field.Samples, separateChroma, luma);

        var chromaU = new byte[checked(_chromaWidth * _chromaHeight)];
        var chromaV = new byte[chromaU.Length];
        Array.Fill(chromaU, (byte)128);
        Array.Fill(chromaV, (byte)128);
        if ((_palChroma || _ntscChroma) && _burstEnd > _burstStart)
        {
            ReadOnlySpan<ushort> chromaSource = separateChroma
                ? field.ChromaSamples!
                : field.Samples;
            RenderChroma(
                chromaSource,
                separateChroma,
                isFirstField,
                field.FieldPhaseId,
                chromaU,
                chromaV);
        }

        (bool[] lumaDropouts, bool[] chromaDropouts) = BuildDropoutMasks(field.Dropouts);
        return new PreviewRenderedField(
            luma,
            chromaU,
            chromaV,
            lumaDropouts,
            chromaDropouts);
    }

    private void RenderLuma(
        ReadOnlySpan<ushort> samples,
        bool separateChroma,
        Span<byte> destination)
    {
        for (int y = 0; y < _halfHeight; y++)
        {
            int sourceOffset = checked(_sourceLines[y] * _sourceLineLength);
            int destinationOffset = checked(y * _width);
            for (int x = 0; x < _width; x++)
            {
                int sourceColumn = _sourceColumns[x];
                int index = sourceOffset + sourceColumn;
                if ((uint)index >= (uint)samples.Length)
                {
                    destination[destinationOffset + x] = 16;
                    continue;
                }

                ushort value = samples[index];
                if (!separateChroma && sourceColumn >= 2)
                {
                    int previous = index - 2;
                    if ((uint)previous < (uint)samples.Length)
                    {
                        value = (ushort)(((int)value + samples[previous] + 1) / 2);
                    }
                }

                destination[destinationOffset + x] = _lumaLut[value];
            }
        }
    }

    private void RenderChroma(
        ReadOnlySpan<ushort> samples,
        bool separateChroma,
        bool isFirstField,
        int? fieldPhaseId,
        Span<byte> destinationU,
        Span<byte> destinationV)
    {
        for (int y = 0; y < _chromaHeight; y++)
        {
            int fieldY0 = y * 2;
            int fieldY1 = Math.Min(_halfHeight - 1, fieldY0 + 1);
            int sourceLine0 = _sourceLines[fieldY0];
            int sourceLine1 = _sourceLines[fieldY1];
            BurstRotation burst0 = DetectBurst(
                samples,
                sourceLine0,
                separateChroma);
            BurstRotation burst1 = DetectBurst(
                samples,
                sourceLine1,
                separateChroma);
            int destinationOffset = y * _chromaWidth;
            for (int x = 0; x < _chromaWidth; x++)
            {
                bool valid0 = TryDecodeChroma(
                    samples,
                    sourceLine0,
                    separateChroma,
                    burst0,
                    isFirstField,
                    fieldPhaseId,
                    x,
                    out double u0,
                    out double v0);
                bool valid1 = TryDecodeChroma(
                    samples,
                    sourceLine1,
                    separateChroma,
                    burst1,
                    isFirstField,
                    fieldPhaseId,
                    x,
                    out double u1,
                    out double v1);
                if (!valid0 && !valid1)
                {
                    continue;
                }

                double u = valid0 && valid1 ? (u0 + u1) * 0.5 : valid0 ? u0 : u1;
                double v = valid0 && valid1 ? (v0 + v1) * 0.5 : valid0 ? v0 : v1;
                destinationU[destinationOffset + x] = ChromaByte(u);
                destinationV[destinationOffset + x] = ChromaByte(v);
            }
        }
    }

    private BurstRotation DetectBurst(
        ReadOnlySpan<ushort> samples,
        int sourceLine,
        bool separateChroma)
    {
        int lineOffset = checked(sourceLine * _sourceLineLength);
        if (lineOffset < 0 || lineOffset + _sourceLineLength > samples.Length)
        {
            return default;
        }

        double real = 0.0;
        double imaginary = 0.0;
        int count = 0;
        for (int x = _burstStart; x < _burstEnd; x++)
        {
            double value = ChromaSample(samples, lineOffset, x, separateChroma);
            AccumulateDemodulated(value, x, ref real, ref imaginary);
            count++;
        }

        if (count == 0)
        {
            return default;
        }

        real /= count;
        imaginary /= count;
        double magnitude = Math.Sqrt((real * real) + (imaginary * imaginary));
        return magnitude >= MinimumBurstMagnitude
            ? new BurstRotation(real / magnitude, imaginary / magnitude, true)
            : default;
    }

    private bool TryDecodeChroma(
        ReadOnlySpan<ushort> samples,
        int sourceLine,
        bool separateChroma,
        BurstRotation burst,
        bool isFirstField,
        int? fieldPhaseId,
        int destinationX,
        out double u,
        out double v)
    {
        u = 128.0;
        v = 128.0;
        if (!burst.Valid)
        {
            return false;
        }

        int lineOffset = checked(sourceLine * _sourceLineLength);
        if (lineOffset < 0 || lineOffset + _sourceLineLength > samples.Length)
        {
            return false;
        }

        double real = 0.0;
        double imaginary = 0.0;
        int start = _chromaStarts[destinationX];
        int end = _chromaEnds[destinationX];
        for (int x = start; x < end; x++)
        {
            double value = ChromaSample(samples, lineOffset, x, separateChroma);
            AccumulateDemodulated(value, x, ref real, ref imaginary);
        }

        int count = end - start;
        if (count <= 0)
        {
            return false;
        }

        real /= count;
        imaginary /= count;
        double rotatedReal = (real * burst.Real) + (imaginary * burst.Imaginary);
        double rotatedImaginary = (imaginary * burst.Real) - (real * burst.Imaginary);
        if (_ntscChroma)
        {
            u += rotatedReal * NtscUScale;
            v += rotatedImaginary * NtscVScale;
            return true;
        }

        double switchSign = isFirstField == ((sourceLine & 1) == 0) ? 1.0 : -1.0;
        int phase = Math.Clamp(fieldPhaseId ?? 1, 1, 8);
        if (phase is 3 or 4 or 7 or 8)
        {
            switchSign = -switchSign;
        }

        u += (rotatedReal + (switchSign * rotatedImaginary)) * PalUScale;
        v += (rotatedReal - (switchSign * rotatedImaginary)) * PalVScale;
        return true;
    }

    private (bool[] Luma, bool[] Chroma) BuildDropoutMasks(TbcDropoutMap? dropouts)
    {
        var luma = new bool[checked(_width * _halfHeight)];
        if (dropouts is { Count: > 0 })
        {
            for (int dropout = 0; dropout < dropouts.Count; dropout++)
            {
                int sourceLine = dropouts.FieldLine[dropout];
                int destinationY = sourceLine - _sourceFirstLine;
                if ((uint)destinationY >= (uint)_halfHeight
                    || _sourceLines[destinationY] != sourceLine)
                {
                    continue;
                }

                int startX = dropouts.StartX[dropout];
                int endX = dropouts.EndX[dropout];
                int destinationOffset = destinationY * _width;
                for (int x = 0; x < _width; x++)
                {
                    int sourceX = _sourceColumns[x];
                    if (sourceX >= startX && sourceX < endX)
                    {
                        luma[destinationOffset + x] = true;
                    }
                }
            }
        }

        var chroma = new bool[checked(_chromaWidth * _chromaHeight)];
        for (int y = 0; y < _chromaHeight; y++)
        {
            int lumaY0 = y * 2;
            int lumaY1 = Math.Min(_halfHeight - 1, lumaY0 + 1);
            for (int x = 0; x < _chromaWidth; x++)
            {
                int lumaX0 = x * 2;
                int lumaX1 = Math.Min(_width - 1, lumaX0 + 1);
                chroma[(y * _chromaWidth) + x] =
                    luma[(lumaY0 * _width) + lumaX0]
                    || luma[(lumaY0 * _width) + lumaX1]
                    || luma[(lumaY1 * _width) + lumaX0]
                    || luma[(lumaY1 * _width) + lumaX1];
            }
        }

        return (luma, chroma);
    }

    private static double ChromaSample(
        ReadOnlySpan<ushort> samples,
        int lineOffset,
        int x,
        bool separateChroma)
    {
        int index = lineOffset + x;
        if (separateChroma)
        {
            return samples[index] - 32767.0;
        }

        return ((int)samples[index] - samples[index - 2]) * 0.5;
    }

    private static void AccumulateDemodulated(
        double value,
        int x,
        ref double real,
        ref double imaginary)
    {
        switch (x & 3)
        {
            case 0:
                real += value;
                break;
            case 1:
                imaginary -= value;
                break;
            case 2:
                real -= value;
                break;
            default:
                imaginary += value;
                break;
        }
    }

    private static int[] BuildSourceLines(int height, int firstLine, int lineCount)
    {
        var output = new int[height];
        for (int y = 0; y < height; y++)
        {
            output[y] = Math.Clamp(firstLine + y, 0, lineCount - 1);
        }

        return output;
    }

    private static int[] BuildSourceColumns(int width, int start, int end)
    {
        int sourceWidth = end - start;
        var output = new int[width];
        for (int x = 0; x < width; x++)
        {
            output[x] = start + Math.Min(
                sourceWidth - 1,
                checked((int)(((long)x * sourceWidth) / width)));
        }

        return output;
    }

    private static (int[] Starts, int[] Ends) BuildChromaRanges(
        int width,
        int activeStart,
        int activeEnd)
    {
        int sourceWidth = activeEnd - activeStart;
        var starts = new int[width];
        var ends = new int[width];
        for (int x = 0; x < width; x++)
        {
            int center = activeStart + checked((int)(((long)((x * 2) + 1) * sourceWidth) / (width * 2L)));
            starts[x] = Math.Max(activeStart, center - 4);
            ends[x] = Math.Min(activeEnd, center + 6);
        }

        return (starts, ends);
    }

    private static byte[] BuildLumaLut(DecodeSession session)
    {
        var output = new byte[ushort.MaxValue + 1];
        for (int value = 0; value < output.Length; value++)
        {
            double ire = session.VideoOutput.OutputToIre((ushort)value);
            output[value] = (byte)Math.Clamp(
                (int)Math.Round(16.0 + (ire * 2.19), MidpointRounding.AwayFromZero),
                16,
                235);
        }

        return output;
    }

    private static byte ChromaByte(double value)
        => (byte)Math.Clamp(
            (int)Math.Round(value, MidpointRounding.AwayFromZero),
            16,
            240);

    private readonly record struct BurstRotation(
        double Real,
        double Imaginary,
        bool Valid);
}
