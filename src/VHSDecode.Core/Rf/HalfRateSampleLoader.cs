using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Rf;

internal sealed class HalfRateSampleLoader : IReusableRfSampleLoader, IDisposable
{
    private const int HalfWidth = 15;
    private const int MaximumRetainedOutputLength = 1024 * 1024;
    private const int MaximumRetainedOutputCount = 32;
    private const double CenterCoefficient = 0.5000046374907835;

    private static readonly (int Offset, double Coefficient)[] SymmetricCoefficients =
    [
        (1, 0.3126333216205309),
        (3, -0.09010692207961689),
        (5, 0.040107417651266825),
        (7, -0.017917030421899405),
        (9, 0.007100857132041433),
        (11, -0.0022302855185240434),
        (13, 0.00041032287080936366)
    ];

    private readonly IRfSampleLoader _source;
    private readonly object _outputBufferLock = new();
    private readonly double[]?[] _outputBuffers = new double[]?[MaximumRetainedOutputCount];
    private int _outputBufferCount;
    private bool _disposed;

    internal HalfRateSampleLoader(IRfSampleLoader source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    internal IRfSampleLoader Source => _source;

    public double[]? Read(Stream stream, long sample, int readLength)
        => ReadCore(stream, sample, readLength, reuseBuffers: false);

    bool IReusableRfSampleLoader.ReuseForSequentialDecode => true;

    double[]? IReusableRfSampleLoader.ReadReusable(
        Stream stream,
        long sample,
        int readLength)
        => ReadCore(stream, sample, readLength, reuseBuffers: true);

    void IReusableRfSampleLoader.ReturnReusable(double[] buffer)
        => ReturnOutputBuffer(buffer);

    private double[]? ReadCore(
        Stream stream,
        long sample,
        int readLength,
        bool reuseBuffers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        ArgumentOutOfRangeException.ThrowIfNegative(readLength);
        if (readLength == 0)
        {
            return [];
        }

        long firstCenter = checked(sample * 2L);
        long requestedSourceStart = firstCenter - HalfWidth;
        long sourceStart = Math.Max(0L, requestedSourceStart);
        long lastCenter = checked(firstCenter + ((readLength - 1L) * 2L));
        long sourceEnd = checked(lastCenter + HalfWidth);
        int sourceLength = checked((int)(sourceEnd - sourceStart + 1L));
        IReusableRfSampleLoader? reusableSource = reuseBuffers
            ? _source as IReusableRfSampleLoader
            : null;
        double[]? source = reusableSource is null
            ? _source.Read(stream, sourceStart, sourceLength)
            : reusableSource.ReadReusable(stream, sourceStart, sourceLength);
        if (source is null)
        {
            return null;
        }

        double[]? output = null;
        bool completed = false;
        try
        {
            output = reuseBuffers
                ? TakeOutputBuffer(readLength)
                : new double[readLength];
            int firstCenterOffset = checked((int)(firstCenter - sourceStart));
            Filter(source, firstCenterOffset, output);

            completed = true;
            return output;
        }
        finally
        {
            if (reusableSource is not null)
            {
                reusableSource.ReturnReusable(source);
            }

            if (reuseBuffers && output is not null && !completed)
            {
                ReturnOutputBuffer(output);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_outputBufferLock)
        {
            Array.Clear(_outputBuffers);
            _outputBufferCount = 0;
        }

        if (_source is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private double[] TakeOutputBuffer(int length)
    {
        lock (_outputBufferLock)
        {
            for (int index = _outputBufferCount - 1; index >= 0; index--)
            {
                double[]? candidate = _outputBuffers[index];
                if (candidate?.Length != length)
                {
                    continue;
                }

                int lastIndex = --_outputBufferCount;
                _outputBuffers[index] = _outputBuffers[lastIndex];
                _outputBuffers[lastIndex] = null;
                return candidate;
            }
        }

        return GC.AllocateUninitializedArray<double>(length);
    }

    private void ReturnOutputBuffer(double[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_outputBufferLock)
        {
            if (!_disposed
                && buffer.Length <= MaximumRetainedOutputLength
                && _outputBufferCount < _outputBuffers.Length)
            {
                _outputBuffers[_outputBufferCount++] = buffer;
            }
        }
    }

    private static void Filter(
        ReadOnlySpan<double> source,
        int firstCenterOffset,
        Span<double> output)
    {
        if (!Avx.IsSupported || output.Length < 64)
        {
            FilterScalar(source, firstCenterOffset, output);
            return;
        }

        double[] centers = ArrayPool<double>.Shared.Rent(output.Length);
        double[] neighbors = ArrayPool<double>.Shared.Rent(
            checked(output.Length + (SymmetricCoefficients.Length * 2) - 1));
        try
        {
            for (int index = 0; index < output.Length; index++)
            {
                centers[index] = SampleOrZero(
                    source,
                    checked(firstCenterOffset + (index * 2)));
            }

            // All non-zero half-band side taps address the opposite source
            // parity. Split that parity into one contiguous sequence so AVX
            // can filter four output samples at a time without gathers.
            int neighborPadding = SymmetricCoefficients.Length;
            int neighborCount = checked(
                output.Length + (SymmetricCoefficients.Length * 2) - 1);
            for (int index = 0; index < neighborCount; index++)
            {
                int logicalNeighbor = index - neighborPadding;
                neighbors[index] = SampleOrZero(
                    source,
                    checked(firstCenterOffset + (logicalNeighbor * 2) + 1));
            }

            ref double centerReference = ref MemoryMarshal.GetArrayDataReference(centers);
            ref double neighborReference = ref MemoryMarshal.GetArrayDataReference(neighbors);
            ref double outputReference = ref MemoryMarshal.GetReference(output);
            int vectorizedLength = output.Length & ~3;
            Vector256<double> centerCoefficient = Vector256.Create(CenterCoefficient);
            for (int index = 0; index < vectorizedLength; index += 4)
            {
                Vector256<double> value = Avx.Multiply(
                    Vector256.LoadUnsafe(ref centerReference, (nuint)index),
                    centerCoefficient);
                for (int coefficientIndex = 0;
                    coefficientIndex < SymmetricCoefficients.Length;
                    coefficientIndex++)
                {
                    Vector256<double> pair = Avx.Add(
                        Vector256.LoadUnsafe(
                            ref neighborReference,
                            (nuint)(index + neighborPadding - coefficientIndex - 1)),
                        Vector256.LoadUnsafe(
                            ref neighborReference,
                            (nuint)(index + neighborPadding + coefficientIndex)));
                    value = Avx.Add(
                        value,
                        Avx.Multiply(
                            pair,
                            Vector256.Create(
                                SymmetricCoefficients[coefficientIndex].Coefficient)));
                }

                value.StoreUnsafe(ref outputReference, (nuint)index);
            }

            for (int index = vectorizedLength; index < output.Length; index++)
            {
                double value = CenterCoefficient * centers[index];
                for (int coefficientIndex = 0;
                    coefficientIndex < SymmetricCoefficients.Length;
                    coefficientIndex++)
                {
                    value += SymmetricCoefficients[coefficientIndex].Coefficient
                        * (neighbors[index + neighborPadding - coefficientIndex - 1]
                            + neighbors[index + neighborPadding + coefficientIndex]);
                }

                output[index] = value;
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(centers);
            ArrayPool<double>.Shared.Return(neighbors);
        }
    }

    private static void FilterScalar(
        ReadOnlySpan<double> source,
        int firstCenterOffset,
        Span<double> output)
    {
        for (int outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            int center = checked(firstCenterOffset + (outputIndex * 2));
            double value = CenterCoefficient * SampleOrZero(source, center);
            foreach ((int offset, double coefficient) in SymmetricCoefficients)
            {
                value += coefficient
                    * (SampleOrZero(source, center - offset)
                        + SampleOrZero(source, center + offset));
            }

            output[outputIndex] = value;
        }
    }

    private static double SampleOrZero(ReadOnlySpan<double> samples, int index)
        => (uint)index < (uint)samples.Length ? samples[index] : 0.0;
}
