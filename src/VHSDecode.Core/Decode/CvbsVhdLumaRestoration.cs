using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Decode;

/// <summary>Offline NTSC CVBS restoration for the measured VHD luminance ripple.</summary>
internal sealed partial class CvbsVhdLumaRestoration : IDisposable
{
    private const double FieldRate = 60000.0 / 1001;
    private const double LineTime = 1001.0 / 15750000;
    private const int FirstRow = 25;
    private const int LastRow = 250;
    private const int RowStep = 3;
    private const int ActiveStart = 134;
    private const int ActiveEnd = 894;
    private const int ChunkFields = 240;
    private static readonly int[] FitRows = Enumerable.Range(0, (LastRow - FirstRow) / RowStep)
        .Select(y => FirstRow + RowStep * y).ToArray();
    private static readonly double[] LumaKernel = CvbsReceiver.Convolve(
        CvbsReceiver.MakeLowPass(65, 2e6 / (315e6 / 22)));
    private static readonly SosSection[] ReferenceBand = IirFilterDesign.ButterworthBandPassSos(2,
        3.5 / (FieldRate / 2), 6.3 / (FieldRate / 2));

    private readonly DecodeSession _session;
    private readonly string _output;
    private readonly FileStream _references;
    private readonly float[] _referenceBuffer;
    private readonly CancellationToken _cancellationToken;

    internal CvbsVhdLumaRestoration(DecodeSession session, string output, CancellationToken cancellationToken = default)
    {
        _session = session;
        _output = output;
        _cancellationToken = cancellationToken;
        _referenceBuffer = new float[session.TbcFrameSpec.OutputLineCount * 2];
        _references = new FileStream(TemporaryPath(), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    internal void Add(TbcDecodedField field)
    {
        double time = field.CvbsBlanking is not null && field.LineLocations.Locations.Length != 0
            ? (field.StartSample + field.LineLocations.Locations[0]) / _session.DecodeSampleRateHz
            : double.NaN;
        Span<double> value = stackalloc double[1] { time };
        _references.Write(MemoryMarshal.AsBytes(value));
        _references.Write(MemoryMarshal.AsBytes((field.CvbsBlanking ?? _referenceBuffer).AsSpan()));
    }

    internal string Apply(int fieldCount)
    {
        if (fieldCount < 80)
        {
            return "VHD CVBS rolling correction skipped: fewer than 80 fields.";
        }

        string temporary = TemporaryPath();
        int correctedBlocks = 0;
        int referenceBlocks = 0;
        _references.Position = 0;
        try
        {
            using (FileStream input = File.OpenRead(_output))
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ushort[] field = new ushort[_session.TbcFrameSpec.FieldSampleCount];
                if (input.Length != (long)fieldCount * field.Length * sizeof(ushort))
                {
                    return "VHD CVBS rolling correction skipped: unexpected TBC length.";
                }
                int segments = (fieldCount - 1) / ChunkFields + 1;
                for (int segment = 0; segment < segments; segment++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    int begin = (int)((long)segment * fieldCount / segments);
                    int count = (int)((long)(segment + 1) * fieldCount / segments) - begin;
                    double[] times = new double[count];
                    double[,,] references = ReadReferences(times);
                    double[,] luma = ReadLuma(input, field, times.Length);
                    double start = times[0];
                    for (int i = 0; i < count; i++)
                    {
                        times[i] -= start;
                    }
                    Ripple? ripple = count >= 80 && Continuous(times) ? FitRipple(luma, times) : null;
                    ReferenceCorrection? reference = null;
                    if (ripple is not null)
                    {
                        correctedBlocks++;
                        // The same low-band offset and gain are applied to both the
                        // picture and the strip means used for the residual fit.
                        double[,] residualLuma = PredictCorrectedLuma(luma, times, ripple);
                        reference = FitReference(residualLuma, references);
                        if (reference is not null)
                        {
                            referenceBlocks++;
                        }
                    }
                    input.Position -= (long)count * field.Length * sizeof(ushort);
                    for (int f = 0; f < count; f++)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        input.ReadExactly(MemoryMarshal.AsBytes(field.AsSpan()));
                        if (ripple is not null)
                        {
                            double strength = Math.Min(begin == 0 ? 1 : f / 12.0,
                                begin + count == fieldCount ? 1 : (count - 1 - f) / 12.0);
                            Correct(field, f, times[f], ripple, reference, strength);
                        }
                        destination.Write(MemoryMarshal.AsBytes(field.AsSpan()));
                    }
                }
            }
            if (correctedBlocks == 0)
            {
                return "VHD CVBS rolling correction skipped: no segment passed the coherence and holdout checks.";
            }
            File.Move(temporary, _output, overwrite: true);
            return $"VHD CVBS rolling correction applied to {correctedBlocks} segment(s), with blanking-reference refinement in {referenceBlocks}.";
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Dispose() => _references.Dispose();

    private string TemporaryPath()
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_output))!, $".cvbs-vhd-{Guid.NewGuid():N}.tmp");

    private double[,,] ReadReferences(double[] times)
    {
        var values = new double[times.Length, FitRows.Length, 2];
        for (int f = 0; f < times.Length; f++)
        {
            _references.ReadExactly(MemoryMarshal.AsBytes(times.AsSpan(f, 1)));
            _references.ReadExactly(MemoryMarshal.AsBytes(_referenceBuffer.AsSpan()));
            for (int y = 0; y < FitRows.Length; y++)
            {
                int row = FitRows[y];
                for (int c = 0; c < 2; c++)
                {
                    double sum = 0;
                    for (int dy = -8; dy <= 8; dy++)
                    {
                        sum += _referenceBuffer[2 * (row + dy) + c];
                    }
                    values[f, y, c] = sum / 17;
                }
            }
        }
        return values;
    }

    private double[,] ReadLuma(FileStream input, ushort[] field, int count)
    {
        var luma = new double[count, FitRows.Length * 3];
        for (int f = 0; f < count; f++)
        {
            input.ReadExactly(MemoryMarshal.AsBytes(field.AsSpan()));
            for (int y = 0; y < FitRows.Length; y++)
            {
                int row = FitRows[y] * _session.TbcFrameSpec.OutputLineLength;
                for (int region = 0; region < 3; region++)
                {
                    long sum = 0;
                    for (int x = 150 + region * 240; x < 390 + region * 240; x++)
                    {
                        sum += field[row + x];
                    }
                    luma[f, y * 3 + region] = _session.VideoOutput.OutputToIre((ushort)(sum / 240));
                }
            }
        }
        return luma;
    }

    private static bool Continuous(double[] times)
    {
        if (!double.IsFinite(times[0]))
        {
            return false;
        }
        for (int i = 1; i < times.Length; i++)
        {
            double delta = times[i] - times[i - 1];
            if (!(delta > .013 && delta < .021))
            {
                return false;
            }
        }
        return true;
    }

    private static double ScanOffset(int column)
        => (FitRows[column / 3] + 1) * LineTime + (270 + 240 * (column % 3)) / (315e6 / 22);

    private void Correct(ushort[] field, int index, double time, Ripple ripple,
        ReferenceCorrection? reference, double strength)
    {
        int width = _session.TbcFrameSpec.OutputLineLength;
        int cap = _session.TbcFrameSpec.OutputLineCount;
        VideoOutputConverter converter = _session.VideoOutput;
        double[] c = ripple.Coefficients[index];
        int r = LumaKernel.Length / 2;
        double[] luma = new double[width + 2 * r];
        for (int y = 20; y < Math.Min(259, cap); y++)
        {
            int line = y * width;
            for (int x = 0; x < width; x++)
            {
                luma[x + r] = converter.OutputToIre(field[line + x]);
            }
            for (int x = 1; x <= r; x++)
            {
                luma[r - x] = 2 * luma[r] - luma[r + x];
                luma[r + width - 1 + x] = 2 * luma[r + width - 1] - luma[r + width - 1 - x];
            }
            (double rs, double rb) = reference?.At(index, y) ?? default;
            double[]? rc = reference?.Coefficients[index];
            for (int x = ActiveStart + 1; x < ActiveEnd - 1; x++)
            {
                double low = 0;
                for (int j = 0; j < LumaKernel.Length; j++)
                {
                    low += LumaKernel[j] * luma[x + j];
                }
                double scan = time + (y + 1) * LineTime + x / _session.TbcFrameSpec.OutputSampleRateHz - ripple.Epoch;
                double cleaned = CorrectSample(low, c, ripple.Frequency, scan);
                if (rc is not null)
                {
                    double correction = rs * rc[0] + rb * rc[1]
                        + (rs * rc[2] + rb * rc[3]) * (cleaned - 30) / 20;
                    cleaned -= Math.Clamp(correction, -.75, .75);
                }
                double gate = Math.Min(1, Math.Min(x - ActiveStart, ActiveEnd - 1 - x) / 15.0);
                double code = field[line + x] + (cleaned - low) * strength * gate * converter.OutputScale;
                field[line + x] = (ushort)Math.Clamp(Math.Round(code), 0, ushort.MaxValue);
            }
        }
    }

    private static double[,] PredictCorrectedLuma(double[,] luma, double[] times, Ripple ripple)
    {
        var result = new double[times.Length, luma.GetLength(1)];
        for (int f = 0; f < times.Length; f++)
        {
            for (int column = 0; column < result.GetLength(1); column++)
            {
                result[f, column] = CorrectSample(luma[f, column], ripple.Coefficients[f], ripple.Frequency,
                    times[f] + ScanOffset(column) - ripple.Epoch);
            }
        }
        return result;
    }

    private static double CorrectSample(double value, double[] coeff, double frequency, double scan)
    {
        (double s, double c) = Math.SinCos(2 * Math.PI * frequency * scan);
        (double s2, double c2) = Math.SinCos(2 * Math.PI * 4.7 * scan);
        double offset = c * coeff[0] + s * coeff[1] + c2 * coeff[4] + s2 * coeff[5];
        double gain = c * coeff[2] + s * coeff[3] + c2 * coeff[6] + s2 * coeff[7];
        return (value - offset) / (1 + gain);
    }
}
