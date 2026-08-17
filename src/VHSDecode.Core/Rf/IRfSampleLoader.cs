namespace VHSDecode.Core.Rf;

public interface IRfSampleLoader
{
    double[]? Read(Stream stream, long sample, int readLength);
}

internal interface IReusableRfSampleLoader : IRfSampleLoader
{
    bool ReuseForSequentialDecode { get; }

    double[]? ReadReusable(Stream stream, long sample, int readLength);

    void ReturnReusable(double[] buffer);
}

internal interface IFloat32RfSampleLoader : IRfSampleLoader
{
    bool TryReadFloat32(
        Stream stream,
        long sample,
        Span<float> destination,
        out int samplesRead);
}

internal interface IInt16RfSampleLoader : IRfSampleLoader
{
    bool TryReadInt16(
        Stream stream,
        long sample,
        Span<short> destination,
        out int samplesRead);
}
