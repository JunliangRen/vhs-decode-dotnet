using System.Numerics;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Decode;

public sealed class LaserDiscAc3Filter
{
    private const int CutBegin = 1024;

    private readonly Complex[] _filter;
    private readonly List<double> _buffer = [];

    public LaserDiscAc3Filter(Complex[] filter)
    {
        _filter = filter.Length > CutBegin
            ? filter
            : throw new ArgumentException("AC3 filter length must be greater than the cut length.", nameof(filter));
    }

    public int BlockLength => _filter.Length;

    public byte[] Process(ReadOnlySpan<short> rfTbc)
    {
        if (rfTbc.Length == 0)
        {
            return [];
        }

        for (int i = 0; i < rfTbc.Length; i++)
        {
            _buffer.Add(rfTbc[i]);
        }

        var output = new List<byte>();
        int stride = BlockLength - CutBegin;
        while (_buffer.Count >= BlockLength)
        {
            Complex[] spectrum = FastFourierTransform.Forward(_buffer.Take(BlockLength).ToArray());
            for (int i = 0; i < spectrum.Length; i++)
            {
                spectrum[i] *= _filter[i];
            }

            Complex[] filtered = FastFourierTransform.Inverse(spectrum);
            for (int i = CutBegin; i < filtered.Length; i++)
            {
                double clipped = Math.Clamp(filtered[i].Real / 64.0, -100.0, 100.0);
                sbyte sample = (sbyte)Math.Truncate(clipped);
                output.Add(unchecked((byte)sample));
            }

            _buffer.RemoveRange(0, stride);
        }

        return output.ToArray();
    }
}
