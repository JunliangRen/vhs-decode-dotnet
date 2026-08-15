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
    private readonly int _chromaPrefixStart;
    private readonly int _chromaPrefixEnd;
    private readonly int _burstStart;
    private readonly int _burstEnd;
    private readonly byte[] _lumaLut;
    private readonly bool _palChroma;
    private readonly bool _ntscChroma;
    private readonly int[] _realPrefix0;
    private readonly int[] _imaginaryPrefix0;
    private readonly int[] _realPrefix1;
    private readonly int[] _imaginaryPrefix1;
    private readonly BurstVector[] _burstVectors;
    private readonly BurstRotation[] _burstRotations;

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
        _chromaPrefixStart = _chromaStarts[0];
        _chromaPrefixEnd = _chromaEnds[^1];
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
        int prefixLength = checked(_sourceLineLength + 1);
        _realPrefix0 = new int[prefixLength];
        _imaginaryPrefix0 = new int[prefixLength];
        _realPrefix1 = new int[prefixLength];
        _imaginaryPrefix1 = new int[prefixLength];
        int burstStorageLineCount = checked(_sourceLines[^1] + 3);
        _burstVectors = new BurstVector[burstStorageLineCount];
        _burstRotations = new BurstRotation[burstStorageLineCount];
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
        if (separateChroma)
        {
            RenderSeparateLuma(samples, destination);
            return;
        }

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
                if (sourceColumn >= 2)
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

    private void RenderSeparateLuma(
        ReadOnlySpan<ushort> samples,
        Span<byte> destination)
    {
        for (int y = 0; y < _halfHeight; y++)
        {
            int sourceOffset = checked(_sourceLines[y] * _sourceLineLength);
            int destinationOffset = checked(y * _width);
            for (int x = 0; x < _width; x++)
            {
                int index = sourceOffset + _sourceColumns[x];
                destination[destinationOffset + x] = (uint)index < (uint)samples.Length
                    ? _lumaLut[samples[index]]
                    : (byte)16;
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
        BurstRotation[] bursts = BuildBurstRotations(samples, separateChroma);
        for (int y = 0; y < _chromaHeight; y++)
        {
            int fieldY0 = y * 2;
            int fieldY1 = Math.Min(_halfHeight - 1, fieldY0 + 1);
            int sourceLine0 = _sourceLines[fieldY0];
            int sourceLine1 = _sourceLines[fieldY1];
            BurstRotation burst0 = bursts[sourceLine0];
            BurstRotation burst1 = bursts[sourceLine1];
            bool validPrefix0 = BuildDemodulatedPrefixes(
                samples,
                sourceLine0,
                separateChroma,
                _realPrefix0,
                _imaginaryPrefix0);
            bool validPrefix1 = BuildDemodulatedPrefixes(
                samples,
                sourceLine1,
                separateChroma,
                _realPrefix1,
                _imaginaryPrefix1);
            int destinationOffset = y * _chromaWidth;
            for (int x = 0; x < _chromaWidth; x++)
            {
                double u0 = 128.0;
                double v0 = 128.0;
                bool valid0 = validPrefix0 && TryDecodeChroma(
                    _realPrefix0,
                    _imaginaryPrefix0,
                    sourceLine0,
                    burst0,
                    isFirstField,
                    fieldPhaseId,
                    x,
                    out u0,
                    out v0);
                double u1 = 128.0;
                double v1 = 128.0;
                bool valid1 = validPrefix1 && TryDecodeChroma(
                    _realPrefix1,
                    _imaginaryPrefix1,
                    sourceLine1,
                    burst1,
                    isFirstField,
                    fieldPhaseId,
                    x,
                    out u1,
                    out v1);
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

    private BurstRotation[] BuildBurstRotations(
        ReadOnlySpan<ushort> samples,
        bool separateChroma)
    {
        int lineCount = samples.Length / _sourceLineLength;
        int firstActiveLine = _sourceLines[0];
        int lastActiveLine = _sourceLines[^1];
        Array.Clear(_burstRotations, firstActiveLine, lastActiveLine - firstActiveLine + 1);
        int firstBurstLine = _palChroma
            ? Math.Max(0, firstActiveLine - 2)
            : firstActiveLine;
        int lastBurstLine = _palChroma
            ? Math.Min(lineCount - 1, lastActiveLine + 2)
            : lastActiveLine;
        for (int sourceLine = firstBurstLine; sourceLine <= lastBurstLine; sourceLine++)
        {
            _burstVectors[sourceLine] = DetectBurstVector(
                samples,
                sourceLine,
                separateChroma);
        }

        int lastAvailableActiveLine = Math.Min(lastActiveLine, lineCount - 1);
        for (int sourceLine = firstActiveLine; sourceLine <= lastAvailableActiveLine; sourceLine++)
        {
            BurstVector vector = _burstVectors[sourceLine];
            double magnitude = Math.Sqrt(
                (vector.Real * vector.Real)
                + (vector.Imaginary * vector.Imaginary));
            if (magnitude < MinimumBurstMagnitude)
            {
                continue;
            }

            _burstRotations[sourceLine] = new BurstRotation(
                vector.Real / magnitude,
                vector.Imaginary / magnitude,
                _palChroma ? DetectPalVSwitch(_burstVectors, sourceLine, lineCount) : 0,
                true);
        }

        return _burstRotations;
    }

    private bool BuildDemodulatedPrefixes(
        ReadOnlySpan<ushort> samples,
        int sourceLine,
        bool separateChroma,
        Span<int> realPrefix,
        Span<int> imaginaryPrefix)
    {
        int lineOffset = checked(sourceLine * _sourceLineLength);
        if (lineOffset < 0
            || lineOffset + _sourceLineLength > samples.Length
            || (!separateChroma && _chromaPrefixStart < 2))
        {
            return false;
        }

        int real = 0;
        int imaginary = 0;
        realPrefix[_chromaPrefixStart] = 0;
        imaginaryPrefix[_chromaPrefixStart] = 0;
        for (int x = _chromaPrefixStart; x < _chromaPrefixEnd; x++)
        {
            int value = ChromaSampleScaledByTwo(samples, lineOffset, x, separateChroma);
            AccumulateDemodulated(value, x, ref real, ref imaginary);
            realPrefix[x + 1] = real;
            imaginaryPrefix[x + 1] = imaginary;
        }

        return true;
    }

    private BurstVector DetectBurstVector(
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
        int count = _burstEnd - _burstStart;
        if (count == 0)
        {
            return default;
        }

        for (int x = _burstStart; x < _burstEnd; x++)
        {
            double value = ChromaSample(samples, lineOffset, x, separateChroma);
            AccumulateDemodulated(value, x, ref real, ref imaginary);
        }

        return new BurstVector(real / count, imaginary / count);
    }

    private static int DetectPalVSwitch(
        ReadOnlySpan<BurstVector> vectors,
        int sourceLine,
        int availableLineCount)
    {
        if (sourceLine < 2 || sourceLine + 2 >= availableLineCount)
        {
            return 0;
        }

        BurstVector current = vectors[sourceLine];
        BurstVector delayed = vectors[sourceLine - 2];
        BurstVector advanced = vectors[sourceLine + 2];
        BurstVector previous = vectors[sourceLine - 1];
        BurstVector next = vectors[sourceLine + 1];
        double currentReal =
            (current.Real - ((delayed.Real + advanced.Real) * 0.5)) * 0.5;
        double currentImaginary =
            (current.Imaginary - ((delayed.Imaginary + advanced.Imaginary) * 0.5)) * 0.5;
        double oppositeReal = (next.Real - previous.Real) * 0.5;
        double oppositeImaginary = (next.Imaginary - previous.Imaginary) * 0.5;

        double currentMagnitudeSquared =
            (currentReal * currentReal) + (currentImaginary * currentImaginary);
        if (currentMagnitudeSquared == 0.0)
        {
            return 0;
        }

        double realDifference = currentReal - oppositeReal;
        double imaginaryDifference = currentImaginary - oppositeImaginary;
        double differenceMagnitudeSquared =
            (realDifference * realDifference)
            + (imaginaryDifference * imaginaryDifference);
        return differenceMagnitudeSquared < currentMagnitudeSquared * 2.0 ? 1 : -1;
    }

    private bool TryDecodeChroma(
        ReadOnlySpan<int> realPrefix,
        ReadOnlySpan<int> imaginaryPrefix,
        int sourceLine,
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

        int start = _chromaStarts[destinationX];
        int end = _chromaEnds[destinationX];
        int count = end - start;
        if (count <= 0)
        {
            return false;
        }

        double real = ((realPrefix[end] - realPrefix[start]) * 0.5) / count;
        double imaginary = ((imaginaryPrefix[end] - imaginaryPrefix[start]) * 0.5) / count;
        double rotatedReal = (real * burst.Real) + (imaginary * burst.Imaginary);
        double rotatedImaginary = (imaginary * burst.Real) - (real * burst.Imaginary);
        if (_ntscChroma)
        {
            u += rotatedReal * NtscUScale;
            v += rotatedImaginary * NtscVScale;
            return true;
        }

        double switchSign = burst.VSwitch;
        if (switchSign == 0.0)
        {
            switchSign = isFirstField == ((sourceLine & 1) == 0) ? 1.0 : -1.0;
            int phase = Math.Clamp(fieldPhaseId ?? 1, 1, 8);
            if (phase is 3 or 4 or 7 or 8)
            {
                switchSign = -switchSign;
            }
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

    private static int ChromaSampleScaledByTwo(
        ReadOnlySpan<ushort> samples,
        int lineOffset,
        int x,
        bool separateChroma)
    {
        int index = lineOffset + x;
        return separateChroma
            ? checked(((int)samples[index] - 32767) * 2)
            : (int)samples[index] - samples[index - 2];
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

    private static void AccumulateDemodulated(
        int value,
        int x,
        ref int real,
        ref int imaginary)
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
        int VSwitch,
        bool Valid);

    private readonly record struct BurstVector(
        double Real,
        double Imaginary);
}
