namespace VHSDecode.Core.Rf;

internal sealed class PyAvRawFlacSampleMapper
{
    private const long AvTimeBase = 1_000_000;
    private const long RfSamplesPerContainerSample = 1_000;

    private readonly int _sampleRateHz;
    private readonly int _blockSize;

    internal PyAvRawFlacSampleMapper(int sampleRateHz, int blockSize)
    {
        if (sampleRateHz != FfmpegPcm16SampleLoader.ContainerAudioSampleRateHz)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (blockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        _sampleRateHz = sampleRateHz;
        _blockSize = blockSize;
    }

    internal bool TryMapRestartSample(long logicalSample, out long physicalSample)
    {
        physicalSample = 0;
        if (logicalSample < 0)
        {
            return false;
        }

        // LoadLDF treats the first decoded frame PTS as an RF position scaled by 1,000.
        UInt128 roughSeekSample = (
            ((UInt128)(ulong)Math.Max(
                0,
                (logicalSample / RfSamplesPerContainerSample) - _sampleRateHz)
                * (uint)_sampleRateHz)
            + (AvTimeBase / 2))
            / AvTimeBase;
        UInt128 firstFrameSample = (roughSeekSample / (uint)_blockSize) * (uint)_blockSize;
        if (firstFrameSample > long.MaxValue)
        {
            return false;
        }

        try
        {
            long presentationRfSample = checked(
                (long)firstFrameSample * RfSamplesPerContainerSample);
            physicalSample = checked(
                (long)firstFrameSample + (logicalSample - presentationRfSample));
            return physicalSample >= 0;
        }
        catch (OverflowException)
        {
            physicalSample = 0;
            return false;
        }
    }
}
