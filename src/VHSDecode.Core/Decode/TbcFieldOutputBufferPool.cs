using System.Runtime.CompilerServices;

namespace VHSDecode.Core.Decode;

internal static class TbcDecodedFieldOutputBufferRegistry
{
    private static readonly ConditionalWeakTable<
        TbcDecodedField,
        OutputBufferRegistration> Registrations = new();

    internal static TbcFieldOutputBufferPool.TbcFieldOutputBufferLease? Get(TbcDecodedField field)
        => Registrations.TryGetValue(field, out OutputBufferRegistration? registration)
            ? Volatile.Read(ref registration.Lease)
            : null;

    internal static bool WasPooled(TbcDecodedField field)
        => Registrations.TryGetValue(field, out _);

    internal static void Attach(
        TbcDecodedField field,
        TbcFieldOutputBufferPool.TbcFieldOutputBufferLease lease)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(lease);
        Registrations.Add(field, new OutputBufferRegistration(lease));
    }

    internal static void Retain(TbcDecodedField field)
        => Get(field)?.Retain();

    internal static void Release(TbcDecodedField field)
    {
        if (!Registrations.TryGetValue(field, out OutputBufferRegistration? registration))
        {
            return;
        }

        TbcFieldOutputBufferPool.TbcFieldOutputBufferLease? lease = Volatile.Read(ref registration.Lease);
        if (lease?.ReleaseReference() == true)
        {
            Interlocked.CompareExchange(ref registration.Lease, null, lease);
        }
    }

    private sealed class OutputBufferRegistration(
        TbcFieldOutputBufferPool.TbcFieldOutputBufferLease lease)
    {
        internal TbcFieldOutputBufferPool.TbcFieldOutputBufferLease? Lease = lease;
    }
}

internal sealed class TbcFieldOutputBufferPool
{
    private const int DefaultMaximumRetainedBuffers = 8;

    private readonly ExactLengthBufferPool _luma;
    private readonly ExactLengthBufferPool? _chroma;

    internal TbcFieldOutputBufferPool(
        int lumaLength,
        int? chromaLength,
        int maximumRetainedBuffers = DefaultMaximumRetainedBuffers)
    {
        _luma = new ExactLengthBufferPool(lumaLength, maximumRetainedBuffers);
        _chroma = chromaLength.HasValue
            ? new ExactLengthBufferPool(chromaLength.Value, maximumRetainedBuffers)
            : null;
    }

    internal TbcFieldOutputBufferLease Rent()
    {
        ushort[] luma = _luma.Rent();
        try
        {
            return new TbcFieldOutputBufferLease(this, luma, _chroma?.Rent());
        }
        catch
        {
            _luma.Return(luma);
            throw;
        }
    }

    private void Return(ushort[] luma, ushort[]? chroma)
    {
        _luma.Return(luma);
        if (chroma is not null)
        {
            _chroma!.Return(chroma);
        }
    }

    internal int RetainedLumaBufferCount => _luma.RetainedCount;

    internal int RetainedChromaBufferCount => _chroma?.RetainedCount ?? 0;

    internal int CreatedLumaBufferCount => _luma.CreatedCount;

    internal int CreatedChromaBufferCount => _chroma?.CreatedCount ?? 0;

    internal sealed class TbcFieldOutputBufferLease : IDisposable
    {
        private TbcFieldOutputBufferPool? _owner;
        private int _referenceCount = 1;

        internal TbcFieldOutputBufferLease(
            TbcFieldOutputBufferPool owner,
            ushort[] luma,
            ushort[]? chroma)
        {
            _owner = owner;
            Luma = luma;
            Chroma = chroma;
        }

        internal ushort[] Luma { get; }

        internal ushort[]? Chroma { get; }

        internal void Retain()
        {
            while (true)
            {
                int current = Volatile.Read(ref _referenceCount);
                if (current <= 0)
                {
                    throw new ObjectDisposedException(nameof(TbcFieldOutputBufferLease));
                }

                if (Interlocked.CompareExchange(
                        ref _referenceCount,
                        checked(current + 1),
                        current) == current)
                {
                    return;
                }
            }
        }

        public void Dispose()
            => ReleaseReference();

        internal bool ReleaseReference()
        {
            int remaining = Interlocked.Decrement(ref _referenceCount);
            if (remaining > 0)
            {
                return false;
            }

            if (remaining < 0)
            {
                throw new ObjectDisposedException(nameof(TbcFieldOutputBufferLease));
            }

            Interlocked.Exchange(ref _owner, null)?.Return(Luma, Chroma);
            return true;
        }
    }

    private sealed class ExactLengthBufferPool
    {
        private readonly ushort[]?[] _buffers;
        private readonly Lock _gate = new();
        private readonly int _length;
        private int _createdCount;
        private int _retainedCount;

        internal ExactLengthBufferPool(int length, int maximumRetainedBuffers)
        {
            _length = length >= 0
                ? length
                : throw new ArgumentOutOfRangeException(nameof(length));
            _buffers = new ushort[maximumRetainedBuffers >= 0
                ? maximumRetainedBuffers
                : throw new ArgumentOutOfRangeException(nameof(maximumRetainedBuffers))][];
        }

        internal int RetainedCount => Volatile.Read(ref _retainedCount);

        internal int CreatedCount => Volatile.Read(ref _createdCount);

        internal ushort[] Rent()
        {
            lock (_gate)
            {
                if (_retainedCount > 0)
                {
                    int index = --_retainedCount;
                    ushort[] buffer = _buffers[index]
                        ?? throw new InvalidOperationException("A retained buffer slot was unexpectedly empty.");
                    _buffers[index] = null;
                    return buffer;
                }
            }

            Interlocked.Increment(ref _createdCount);
            return new ushort[_length];
        }

        internal void Return(ushort[] buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (buffer.Length != _length)
            {
                throw new ArgumentException(
                    "Returned buffer length does not match the pool's configured length.",
                    nameof(buffer));
            }

            lock (_gate)
            {
                if (_retainedCount >= _buffers.Length)
                {
                    return;
                }

                _buffers[_retainedCount++] = buffer;
            }
        }
    }
}
