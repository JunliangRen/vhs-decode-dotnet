namespace VHSDecode.Core.Rf;

public interface IRfSampleLoader
{
    double[]? Read(Stream stream, long sample, int readLength);
}
