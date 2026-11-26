namespace VHSDecode.Preview;

public enum PreviewRegion { ActiveVideo, FullRaster }

public readonly record struct PreviewRectangle(int X, int Y, int Width, int Height);

/// <summary>An owned BGRA field, rendered before 4:2:0 subsampling.</summary>
public sealed class PreviewField
{
    internal PreviewField(byte[] bgra, PreviewRectangle raster, PreviewRectangle activeVideo, bool isTopField,
        TimeSpan timestamp, double fieldsPerSecond, ReadOnlyMemory<ushort> samples,
        ReadOnlyMemory<ushort> chromaSamples, double tbcSampleRateHz, double tbcZeroIre, double tbcUnitsPerIre,
        PreviewRectangle? tbcRaster = null)
    {
        Bgra = bgra;
        RasterRect = raster;
        ActiveVideoRect = activeVideo;
        IsTopField = isTopField;
        Timestamp = timestamp;
        FieldsPerSecond = fieldsPerSecond;
        TbcRasterRect = tbcRaster ?? raster;
        TbcSamples = samples;
        TbcChromaSamples = chromaSamples;
        TbcSampleRateHz = tbcSampleRateHz;
        TbcZeroIre = tbcZeroIre;
        TbcUnitsPerIre = tbcUnitsPerIre;
    }

    public ReadOnlyMemory<byte> Bgra { get; }
    public PreviewRectangle RasterRect { get; }
    public PreviewRectangle ActiveVideoRect { get; }
    public int Width => RasterRect.Width;
    public int Height => RasterRect.Height;
    public int Stride => checked(Width * 4);
    /// <summary>Pixel aspect ratio when the two fields are displayed as a 4:3 active frame.</summary>
    public double PixelAspectRatio => (8.0 / 3.0) * ActiveVideoRect.Height / ActiveVideoRect.Width;
    public bool IsTopField { get; }
    public TimeSpan Timestamp { get; }
    public double FieldsPerSecond { get; }

    /// <summary>Full-raster dimensions of the optional TBC planes, independent of the BGRA crop.</summary>
    public PreviewRectangle TbcRasterRect { get; }

    /// <summary>Owned, row-major TBC samples when requested. The main plane can contain composite video.</summary>
    public ReadOnlyMemory<ushort> TbcSamples { get; }
    /// <summary>Owned, row-major color carrier data when the decoder provides a separate plane.</summary>
    public ReadOnlyMemory<ushort> TbcChromaSamples { get; }
    public double TbcSampleRateHz { get; }
    public double TbcZeroIre { get; }
    public double TbcUnitsPerIre { get; }

    /// <summary>Copies the selected rectangle from the stored BGRA image.</summary>
    /// <remarks>FullRaster copies the entire stored image, which may already be cropped by the decoder.</remarks>
    public void CopyToBgra(Span<byte> destination, PreviewRegion region)
    {
        PreviewRectangle bounds = region switch
        {
            PreviewRegion.ActiveVideo => ActiveVideoRect,
            PreviewRegion.FullRaster => RasterRect,
            _ => throw new ArgumentOutOfRangeException(nameof(region))
        };
        int rowBytes = checked(bounds.Width * 4);
        if (destination.Length < checked(rowBytes * bounds.Height))
            throw new ArgumentException("The destination cannot hold the selected BGRA rectangle.", nameof(destination));

        for (int y = 0; y < bounds.Height; y++)
        {
            int offset = (bounds.Y + y) * Stride + bounds.X * 4;
            Bgra.Span.Slice(offset, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
        }
    }
}
