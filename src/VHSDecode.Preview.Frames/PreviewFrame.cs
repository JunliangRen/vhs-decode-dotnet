namespace VHSDecode.Preview;

/// <summary>A woven, top-field-first preview frame with limited-range BT.601 YUV420 pixels.</summary>
public sealed class PreviewFrame
{
    internal PreviewFrame(byte[] pixels, int width, int height, TimeSpan timestamp, double framesPerSecond,
        PreviewRectangle? active = null, PreviewRectangle? raster = null)
    {
        Yuv420 = pixels;
        Width = width;
        Height = height;
        Timestamp = timestamp;
        FramesPerSecond = framesPerSecond;
        RasterRect = raster ?? new PreviewRectangle(0, 0, width, height);
        ActiveVideoRect = active ?? RasterRect;
    }

    /// <summary>Owned, tightly packed Y, U, and V planes, in that order. The chroma rows are interleaved by field.</summary>
    public ReadOnlyMemory<byte> Yuv420 { get; }
    public int Width { get; }
    public int Height { get; }
    public double FramesPerSecond { get; }
    public PreviewRectangle RasterRect { get; }
    public PreviewRectangle ActiveVideoRect { get; }
    public double DisplayAspectRatio => (4.0 / 3.0) * (Width / (double)ActiveVideoRect.Width)
        * (ActiveVideoRect.Height / (double)Height);
    public bool TopFieldFirst => true;

    /// <summary>Frame midpoint relative to the start of the input file.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>Converts the frame to tightly packed, opaque BGRA while preserving both fields.</summary>
    public void CopyToBgra(Span<byte> destination)
    {
        int lumaSize = checked(Width * Height);
        if (destination.Length < checked(lumaSize * 4))
        {
            throw new ArgumentException("The destination cannot hold the BGRA frame.", nameof(destination));
        }

        ReadOnlySpan<byte> pixels = Yuv420.Span;
        int chromaStride = Width / 2;
        int vOffset = lumaSize + lumaSize / 4;
        for (int y = 0; y < Height; y++)
        {
            // Chroma is 2:1 vertically within each field, not across adjacent raster lines.
            int chromaRow = ((y / 4) * 2 + y % 2) * chromaStride;
            int lumaRow = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int chroma = chromaRow + x / 2;
                int pixel = lumaRow + x;
                WriteBgra(destination, pixel * 4, pixels[pixel], pixels[lumaSize + chroma], pixels[vOffset + chroma]);
            }
        }
    }

    internal static void WriteBgra(Span<byte> destination, int offset, byte y, byte u, byte v)
    {
        int luma = 298 * (y - 16);
        int cb = u - 128;
        int cr = v - 128;
        destination[offset] = ToByte(luma + 516 * cb);
        destination[offset + 1] = ToByte(luma - 100 * cb - 208 * cr);
        destination[offset + 2] = ToByte(luma + 409 * cr);
        destination[offset + 3] = 255;
    }

    private static byte ToByte(int value) => (byte)Math.Clamp((value + 128) >> 8, 0, 255);
}
