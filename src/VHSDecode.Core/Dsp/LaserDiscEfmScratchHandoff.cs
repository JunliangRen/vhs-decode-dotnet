using System.Numerics;

namespace VHSDecode.Core.Dsp;

// One synchronous demodulation invocation owns each instance. Never cache or
// share it between workers. It holds no native pointer or pooled workspace.
internal sealed class LaserDiscEfmScratchHandoff(RfDemodulator owner)
{
    internal const int BlockLength = 32_768;

    private readonly RfDemodulator _owner = owner;
    private Complex[]? _scratch;

    internal void Publish(RfDemodulator producer, Complex[]? scratch)
    {
        if (ReferenceEquals(_owner, producer)
            && scratch is { Length: BlockLength })
        {
            _scratch = scratch;
        }
    }

    internal Complex[]? Take(RfDemodulator consumer)
    {
        Complex[]? scratch = _scratch;
        _scratch = null;
        return ReferenceEquals(_owner, consumer)
            && scratch is { Length: BlockLength }
                ? scratch
                : null;
    }
}
