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
