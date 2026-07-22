using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

public readonly record struct Pulse(int Start, int Length);

public static class PulseDetection
{
    public static bool InRange(double value, double minimum, double maximum)
    {
        return value >= minimum && value <= maximum;
    }

    public static double? CalculateZeroCrossing(
        ReadOnlySpan<double> data,
        int startOffset,
        double target,
        int edge = 0,
        int count = 16,
        bool reverse = false)
    {
        if (startOffset < 0 || startOffset >= data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset));
        }

        if (reverse)
        {
            double[] reversed = new double[startOffset + 1];
            for (int i = 0; i <= startOffset; i++)
            {
                reversed[i] = data[startOffset - i];
            }

            double? reverseCrossing = CalculateZeroCrossingForward(reversed, 0, target, edge, count);
            return reverseCrossing is null ? null : startOffset - reverseCrossing.Value;
        }

        return CalculateZeroCrossingForward(data, startOffset, target, edge, count);
    }

    public static IReadOnlyList<Pulse> FindPulses(
        ReadOnlySpan<double> syncReference,
        double high,
        int minimumSyncLength = 0,
        int maximumSyncLength = 5000)
    {
        var pulses = new List<Pulse>();
        FindPulses(
            syncReference,
            high,
            minimumSyncLength,
            maximumSyncLength,
            pulses,
            positionScale: 1);
        return pulses;
    }

    internal static void FindPulses(
        ReadOnlySpan<double> syncReference,
        double high,
        int minimumSyncLength,
        int maximumSyncLength,
        List<Pulse> pulses,
        int positionScale)
    {
        ArgumentNullException.ThrowIfNull(pulses);
        if (positionScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionScale));
        }

        pulses.Clear();
        if (syncReference.IsEmpty)
        {
            return;
        }

        if (Avx.IsSupported && syncReference.Length >= Vector256<double>.Count + 1)
        {
            FindPulsesAvx(
                syncReference,
                high,
                minimumSyncLength,
                maximumSyncLength,
                pulses,
                positionScale);
        }
        else
        {
            FindPulsesScalar(
                syncReference,
                high,
                minimumSyncLength,
                maximumSyncLength,
                pulses,
                positionScale);
        }
    }

    private static void FindPulsesAvx(
        ReadOnlySpan<double> syncReference,
        double high,
        int minimumSyncLength,
        int maximumSyncLength,
        List<Pulse> pulses,
        int positionScale)
    {
        bool inPulse = syncReference[0] <= high;
        int currentStart = 0;
        int position = 1;
        int lastVectorStart = syncReference.Length - Vector256<double>.Count;
        Vector256<double> highVector = Vector256.Create(high);
        ref double source = ref MemoryMarshal.GetReference(syncReference);

        // SIMD only locates the next state transition; pulse commits remain scalar and ordered.
        while (position <= lastVectorStart)
        {
            Vector256<double> values = Vector256.LoadUnsafe(ref source, (nuint)position);
            Vector256<double> transitions = inPulse
                ? Avx.Compare(
                    values,
                    highVector,
                    FloatComparisonMode.OrderedGreaterThanNonSignaling)
                : Avx.Compare(
                    values,
                    highVector,
                    FloatComparisonMode.OrderedLessThanOrEqualNonSignaling);
            int transitionMask = Avx.MoveMask(transitions);
            if (transitionMask == 0)
            {
                position += Vector256<double>.Count;
                continue;
            }

            position += BitOperations.TrailingZeroCount((uint)transitionMask);
            if (inPulse)
            {
                int length = position - currentStart;
                if (InRange(length, minimumSyncLength, maximumSyncLength) && currentStart != 0)
                {
                    pulses.Add(new Pulse(
                        currentStart * positionScale,
                        length * positionScale));
                }

                inPulse = false;
            }
            else
            {
                currentStart = position;
                inPulse = true;
            }

            position++;
        }

        for (; position < syncReference.Length; position++)
        {
            double value = syncReference[position];
            if (inPulse)
            {
                if (value > high)
                {
                    int length = position - currentStart;
                    if (InRange(length, minimumSyncLength, maximumSyncLength) && currentStart != 0)
                    {
                        pulses.Add(new Pulse(
                            currentStart * positionScale,
                            length * positionScale));
                    }

                    inPulse = false;
                }
            }
            else if (value <= high)
            {
                currentStart = position;
                inPulse = true;
            }
        }
    }

    private static void FindPulsesScalar(
        ReadOnlySpan<double> syncReference,
        double high,
        int minimumSyncLength,
        int maximumSyncLength,
        List<Pulse> pulses,
        int positionScale)
    {
        bool inPulse = syncReference[0] <= high;
        int currentStart = 0;

        for (int position = 0; position < syncReference.Length; position++)
        {
            double value = syncReference[position];
            if (inPulse)
            {
                if (value > high)
                {
                    int length = position - currentStart;
                    if (InRange(length, minimumSyncLength, maximumSyncLength) && currentStart != 0)
                    {
                        pulses.Add(new Pulse(
                            currentStart * positionScale,
                            length * positionScale));
                    }

                    inPulse = false;
                }
            }
            else if (value <= high)
            {
                currentStart = position;
                inPulse = true;
            }
        }
    }

    private static double? CalculateZeroCrossingForward(
        ReadOnlySpan<double> data,
        int startOffset,
        double target,
        int edge,
        int count)
    {
        int actualEdge = edge;
        if (actualEdge == 0)
        {
            actualEdge = data[startOffset] < target ? 1 : -1;
        }

        int searchEnd = Math.Min(data.Length, startOffset + count + 1);
        int? location = FindFirstCrossing(data[startOffset..searchEnd], target, actualEdge == 1);
        if (location is null)
        {
            return null;
        }

        int x = startOffset + location.Value;
        double a = data[x - 1] - target;
        double b = data[x] - target;
        double y = b - a != 0.0 ? -a / (-a + b) : 0.0;
        return x - 1 + y;
    }

    private static int? FindFirstCrossing(ReadOnlySpan<double> data, double target, bool rising)
    {
        if (rising)
        {
            for (int i = 1; i < data.Length; i++)
            {
                if (data[i - 1] < target && data[i] >= target)
                {
                    return i;
                }
            }
        }
        else
        {
            for (int i = 1; i < data.Length; i++)
            {
                if (data[i - 1] > target && data[i] <= target)
                {
                    return i;
                }
            }
        }

        return null;
    }
}
