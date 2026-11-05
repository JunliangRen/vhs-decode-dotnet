namespace VHSDecode.Core.Decode;

internal interface ILaserDiscCudaEfm : IDisposable
{
    string Diagnostic { get; }
    long ProcessedBlocks { get; }

    // Calls are serial. Each result owns an independent EFM buffer, and the
    // accelerator stops using the inputs before returning or throwing.
    short[][] Process(double[][] preparedInputs, int[] ordinals, int count);
}
