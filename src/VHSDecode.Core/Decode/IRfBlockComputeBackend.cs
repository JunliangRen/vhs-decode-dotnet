namespace VHSDecode.Core.Decode;

/// <summary>
/// Executes one or more already-loaded RF blocks without taking ownership of the input buffers.
/// Implementations must return results in the same order as <paramref name="preparedInputs"/>.
/// </summary>
internal interface IRfBlockComputeBackend : IDisposable
{
    string Name { get; }

    bool IsHardwareAccelerated { get; }

    /// <summary>
    /// Returns the largest safe prefix of an already prepared batch. Backends
    /// without a device-memory constraint retain the caller's requested size.
    /// </summary>
    int GetMaximumBatchSize(int requestedBlockCount) => requestedBlockCount;

    RfPipelineBlock[] DecodeBatch(
        RfBlockDecodePipeline pipeline,
        IReadOnlyList<double[]> preparedInputs,
        bool reportDiagnostics);
}
