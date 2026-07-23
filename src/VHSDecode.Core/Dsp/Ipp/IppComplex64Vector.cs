using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.Dsp;

public static unsafe class IppComplex64Vector
{
    static IppComplex64Vector()
    {
        IppComplexLayout.EnsureSupported();
    }

    public static void Multiply(
        ReadOnlySpan<Complex> left,
        ReadOnlySpan<Complex> right,
        Span<Complex> output)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Complex input lengths must match.", nameof(right));
        }

        if (output.Length < left.Length)
        {
            throw new ArgumentException(
                "Output must be at least as long as the inputs.",
                nameof(output));
        }

        Span<Complex> destination = output[..left.Length];
        if (left.Overlaps(destination) || right.Overlaps(destination))
        {
            throw new ArgumentException(
                "Native complex multiply requires disjoint input and output spans.",
                nameof(output));
        }

        if (left.IsEmpty)
        {
            return;
        }

        _ = IppRuntime.RequireAvailable();
        fixed (Complex* leftPointer = left)
        fixed (Complex* rightPointer = right)
        fixed (Complex* outputPointer = destination)
        {
            int status = IppNativeMethods.Complex64Multiply(
                (IppComplex64*)leftPointer,
                (IppComplex64*)rightPointer,
                (IppComplex64*)outputPointer,
                left.Length);
            IppStatus.ThrowIfFailed(status, "complex64_multiply");
        }
    }

    public static void Magnitude(
        ReadOnlySpan<Complex> input,
        Span<double> magnitude)
        => MagnitudePhase(input, magnitude, default);

    public static void Phase(
        ReadOnlySpan<Complex> input,
        Span<double> phase)
        => MagnitudePhase(input, default, phase);

    public static void MagnitudePhase(
        ReadOnlySpan<Complex> input,
        Span<double> magnitude,
        Span<double> phase)
    {
        bool writeMagnitude = !magnitude.IsEmpty;
        bool writePhase = !phase.IsEmpty;
        if (!input.IsEmpty && !writeMagnitude && !writePhase)
        {
            throw new ArgumentException(
                "At least one magnitude or phase output must be provided.");
        }

        if (writeMagnitude && magnitude.Length < input.Length)
        {
            throw new ArgumentException(
                "Magnitude output must be at least as long as the input.",
                nameof(magnitude));
        }

        if (writePhase && phase.Length < input.Length)
        {
            throw new ArgumentException(
                "Phase output must be at least as long as the input.",
                nameof(phase));
        }

        Span<double> magnitudeDestination = writeMagnitude
            ? magnitude[..input.Length]
            : default;
        Span<double> phaseDestination = writePhase
            ? phase[..input.Length]
            : default;
        if (writeMagnitude
            && writePhase
            && magnitudeDestination.Overlaps(phaseDestination))
        {
            throw new ArgumentException(
                "Magnitude and phase outputs must not overlap.",
                nameof(phase));
        }

        ReadOnlySpan<byte> inputBytes = MemoryMarshal.AsBytes(input);
        if ((writeMagnitude && inputBytes.Overlaps(MemoryMarshal.AsBytes(magnitudeDestination)))
            || (writePhase && inputBytes.Overlaps(MemoryMarshal.AsBytes(phaseDestination))))
        {
            throw new ArgumentException(
                "Complex input and real outputs must not overlap.");
        }

        if (input.IsEmpty)
        {
            return;
        }

        _ = IppRuntime.RequireAvailable();
        fixed (Complex* inputPointer = input)
        fixed (double* magnitudePointer = magnitudeDestination)
        fixed (double* phasePointer = phaseDestination)
        {
            int status = IppNativeMethods.Complex64MagnitudePhase(
                (IppComplex64*)inputPointer,
                magnitudePointer,
                phasePointer,
                input.Length);
            IppStatus.ThrowIfFailed(status, "complex64_magnitude_phase");
        }
    }
}
