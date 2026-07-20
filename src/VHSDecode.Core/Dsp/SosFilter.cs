using System.Buffers;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

public readonly record struct SosSection(
    double B0,
    double B1,
    double B2,
    double A0,
    double A1,
    double A2)
{
    public SosSection Normalize()
    {
        if (A0 == 0.0)
        {
            throw new ArgumentException("SOS denominator a0 must not be zero.");
        }

        return A0 == 1.0
            ? this
            : new SosSection(B0 / A0, B1 / A0, B2 / A0, 1.0, A1 / A0, A2 / A0);
    }

    public double DcGain => (B0 + B1 + B2) / (A0 + A1 + A2);
}

public static class SosFilter
{
    private readonly record struct FloatSosSection(
        float B0,
        float B1,
        float B2,
        float A0,
        float A1,
        float A2);

    public static SosSection[] FromScipyArray(int order, ReadOnlySpan<double> flattened)
    {
        if (order < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        if (flattened.Length != order * 6)
        {
            throw new ArgumentException("Flattened SOS filter length must be order * 6.", nameof(flattened));
        }

        var sections = new SosSection[order];
        for (int i = 0; i < sections.Length; i++)
        {
            int offset = i * 6;
            sections[i] = new SosSection(
                flattened[offset],
                flattened[offset + 1],
                flattened[offset + 2],
                flattened[offset + 3],
                flattened[offset + 4],
                flattened[offset + 5]).Normalize();
        }

        return sections;
    }

    public static double[] ApplyForward(IReadOnlyList<SosSection> sections, ReadOnlySpan<double> input)
    {
        double[,] zi = new double[sections.Count, 2];
        return ApplyForward(sections, input, zi);
    }

    public static double[] ApplyForward(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<double> input,
        double[,] initialConditions)
    {
        if (initialConditions.GetLength(0) != sections.Count || initialConditions.GetLength(1) != 2)
        {
            throw new ArgumentException("Initial condition array must have shape [sections, 2].", nameof(initialConditions));
        }

        double[] output = input.ToArray();
        ApplyForwardInPlace(sections, output, initialConditions);
        return output;
    }

    private static void ApplyForwardInPlace(
        IReadOnlyList<SosSection> sections,
        Span<double> output,
        double[,] initialConditions)
    {
        for (int sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            SosSection section = sections[sectionIndex].Normalize();
            double z1 = initialConditions[sectionIndex, 0];
            double z2 = initialConditions[sectionIndex, 1];

            for (int i = 0; i < output.Length; i++)
            {
                double x = output[i];
                double y = (section.B0 * x) + z1;
                z1 = (section.B1 * x) - (section.A1 * y) + z2;
                z2 = (section.B2 * x) - (section.A2 * y);
                output[i] = y;
            }
        }
    }

    public static double[] ApplyForwardBackward(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<double> input,
        int? padLength = null)
    {
        if (input.IsEmpty)
        {
            return [];
        }

        int edge = padLength ?? DefaultPadLength(sections);
        if (edge < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(padLength));
        }

        double[] extended = edge == 0 ? input.ToArray() : OddExtension(input, edge);
        double[,] zi = SteadyStateInitialConditions(sections);
        double[,] firstZi = ScaleInitialConditions(zi, extended[0]);
        ApplyForwardInPlace(sections, extended, firstZi);
        Array.Reverse(extended);
        double[,] secondZi = ScaleInitialConditions(zi, extended[0]);
        ApplyForwardInPlace(sections, extended, secondZi);
        Array.Reverse(extended);

        if (edge == 0)
        {
            return extended;
        }

        var trimmed = new double[input.Length];
        Array.Copy(extended, edge, trimmed, 0, trimmed.Length);
        return trimmed;
    }

    public static double[] ApplyForwardBackwardFloat32(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<double> input,
        int? padLength = null)
    {
        if (input.IsEmpty)
        {
            return [];
        }

        int edge = padLength ?? DefaultPadLength(sections);
        if (edge < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(padLength));
        }

        FloatSosSection[] floatSections = ConvertToFloat32(sections);
        int extendedLength = checked(input.Length + (edge * 2));
        float[] rented = ArrayPool<float>.Shared.Rent(extendedLength);
        try
        {
            Span<float> extended = rented.AsSpan(0, extendedLength);
            if (edge == 0)
            {
                ConvertToFloat32(input, extended);
            }
            else
            {
                WriteOddExtensionFloat32(input, edge, extended);
            }

            ApplyForwardBackwardFloat32InPlace(floatSections, extended);

            var output = new double[input.Length];
            ConvertToFloat64(extended.Slice(edge, input.Length), output);
            return output;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    public static float[] ApplyForwardBackwardFloat32(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<float> input,
        int? padLength = null)
    {
        if (input.IsEmpty)
        {
            return [];
        }

        int edge = padLength ?? DefaultPadLength(sections);
        if (edge < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(padLength));
        }

        FloatSosSection[] floatSections = ConvertToFloat32(sections);
        if (edge == 0)
        {
            float[] output = input.ToArray();
            ApplyForwardBackwardFloat32InPlace(floatSections, output);
            return output;
        }

        int extendedLength = checked(input.Length + (edge * 2));
        float[] rented = ArrayPool<float>.Shared.Rent(extendedLength);
        try
        {
            Span<float> extended = rented.AsSpan(0, extendedLength);
            WriteOddExtensionFloat32(input, edge, extended);
            ApplyForwardBackwardFloat32InPlace(floatSections, extended);
            return extended.Slice(edge, input.Length).ToArray();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private static void ApplyForwardBackwardFloat32InPlace(
        FloatSosSection[] floatSections,
        Span<float> values)
    {
        float[,] zi = SteadyStateInitialConditionsFloat32(floatSections);
        float[,] firstZi = ScaleInitialConditionsFloat32(zi, values[0]);
        ApplyForwardFloat32InPlace(floatSections, values, firstZi);
        values.Reverse();
        float[,] secondZi = ScaleInitialConditionsFloat32(zi, values[0]);
        ApplyForwardFloat32InPlace(floatSections, values, secondZi);
        values.Reverse();
    }

    public static int DefaultPadLength(IReadOnlyList<SosSection> sections)
    {
        int zerosAtOriginNumerator = sections.Count(section => section.B2 == 0.0);
        int zerosAtOriginDenominator = sections.Count(section => section.A2 == 0.0);
        return 3 * ((2 * sections.Count) + 1 - Math.Min(zerosAtOriginNumerator, zerosAtOriginDenominator));
    }

    public static double[,] SteadyStateInitialConditions(IReadOnlyList<SosSection> sections)
    {
        var zi = new double[sections.Count, 2];
        double scale = 1.0;
        for (int i = 0; i < sections.Count; i++)
        {
            SosSection section = sections[i].Normalize();
            double firstTerm = section.B1 - (section.A1 * section.B0);
            double secondTerm = section.B2 - (section.A2 * section.B0);
            double numeratorSum = (0.0 + firstTerm) + secondTerm;
            double denominatorSum = (1.0 + section.A1) + section.A2;
            double z0 = numeratorSum / denominatorSum;
            double z1 = ((1.0 + section.A1) * z0) - firstTerm;
            zi[i, 0] = scale * z0;
            zi[i, 1] = scale * z1;

            double numeratorDc = ((0.0 + section.B0) + section.B1) + section.B2;
            double denominatorDc = ((0.0 + section.A0) + section.A1) + section.A2;
            scale *= numeratorDc / denominatorDc;
        }

        return zi;
    }

    private static double[,] ScaleInitialConditions(double[,] zi, double scale)
    {
        var output = new double[zi.GetLength(0), zi.GetLength(1)];
        for (int i = 0; i < zi.GetLength(0); i++)
        {
            output[i, 0] = zi[i, 0] * scale;
            output[i, 1] = zi[i, 1] * scale;
        }

        return output;
    }

    private static void ApplyForwardFloat32InPlace(
        FloatSosSection[] sections,
        Span<float> values,
        float[,] initialConditions)
    {
        if (initialConditions.GetLength(0) != sections.Length || initialConditions.GetLength(1) != 2)
        {
            throw new ArgumentException("Initial condition array must have shape [sections, 2].", nameof(initialConditions));
        }

        // Preserve sample-major order while keeping common recursive states in registers.
        if (sections.Length == 1)
        {
            FloatSosSection section = sections[0];
            float z1 = initialConditions[0, 0];
            float z2 = initialConditions[0, 1];
            for (int sample = 0; sample < values.Length; sample++)
            {
                float value = values[sample];
                float filtered = (section.B0 * value) + z1;
                z1 = (section.B1 * value) - (section.A1 * filtered) + z2;
                z2 = (section.B2 * value) - (section.A2 * filtered);
                values[sample] = filtered;
            }

            return;
        }

        if (sections.Length == 2)
        {
            FloatSosSection first = sections[0];
            FloatSosSection second = sections[1];
            float firstZ1 = initialConditions[0, 0];
            float firstZ2 = initialConditions[0, 1];
            float secondZ1 = initialConditions[1, 0];
            float secondZ2 = initialConditions[1, 1];
            for (int sample = 0; sample < values.Length; sample++)
            {
                float value = values[sample];
                float filtered = (first.B0 * value) + firstZ1;
                firstZ1 = (first.B1 * value) - (first.A1 * filtered) + firstZ2;
                firstZ2 = (first.B2 * value) - (first.A2 * filtered);

                value = filtered;
                filtered = (second.B0 * value) + secondZ1;
                secondZ1 = (second.B1 * value) - (second.A1 * filtered) + secondZ2;
                secondZ2 = (second.B2 * value) - (second.A2 * filtered);
                values[sample] = filtered;
            }

            return;
        }

        if (sections.Length == 4)
        {
            FloatSosSection first = sections[0];
            FloatSosSection second = sections[1];
            FloatSosSection third = sections[2];
            FloatSosSection fourth = sections[3];
            float firstZ1 = initialConditions[0, 0];
            float firstZ2 = initialConditions[0, 1];
            float secondZ1 = initialConditions[1, 0];
            float secondZ2 = initialConditions[1, 1];
            float thirdZ1 = initialConditions[2, 0];
            float thirdZ2 = initialConditions[2, 1];
            float fourthZ1 = initialConditions[3, 0];
            float fourthZ2 = initialConditions[3, 1];
            for (int sample = 0; sample < values.Length; sample++)
            {
                float value = values[sample];
                float filtered = (first.B0 * value) + firstZ1;
                firstZ1 = (first.B1 * value) - (first.A1 * filtered) + firstZ2;
                firstZ2 = (first.B2 * value) - (first.A2 * filtered);

                value = filtered;
                filtered = (second.B0 * value) + secondZ1;
                secondZ1 = (second.B1 * value) - (second.A1 * filtered) + secondZ2;
                secondZ2 = (second.B2 * value) - (second.A2 * filtered);

                value = filtered;
                filtered = (third.B0 * value) + thirdZ1;
                thirdZ1 = (third.B1 * value) - (third.A1 * filtered) + thirdZ2;
                thirdZ2 = (third.B2 * value) - (third.A2 * filtered);

                value = filtered;
                filtered = (fourth.B0 * value) + fourthZ1;
                fourthZ1 = (fourth.B1 * value) - (fourth.A1 * filtered) + fourthZ2;
                fourthZ2 = (fourth.B2 * value) - (fourth.A2 * filtered);
                values[sample] = filtered;
            }

            return;
        }

        // Two floats per section keep the stack branch bounded to 256 bytes.
        Span<float> states = sections.Length <= 32
            ? stackalloc float[sections.Length * 2]
            : new float[sections.Length * 2];
        for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            int stateOffset = sectionIndex * 2;
            states[stateOffset] = initialConditions[sectionIndex, 0];
            states[stateOffset + 1] = initialConditions[sectionIndex, 1];
        }

        for (int sample = 0; sample < values.Length; sample++)
        {
            float value = values[sample];
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                FloatSosSection section = sections[sectionIndex];
                int stateOffset = sectionIndex * 2;
                float filtered = (section.B0 * value) + states[stateOffset];
                states[stateOffset] =
                    (section.B1 * value) - (section.A1 * filtered) + states[stateOffset + 1];
                states[stateOffset + 1] = (section.B2 * value) - (section.A2 * filtered);
                value = filtered;
            }

            values[sample] = value;
        }
    }

    private static FloatSosSection[] ConvertToFloat32(IReadOnlyList<SosSection> sections)
    {
        var output = new FloatSosSection[sections.Count];
        for (int i = 0; i < output.Length; i++)
        {
            SosSection section = sections[i];
            output[i] = new FloatSosSection(
                (float)section.B0,
                (float)section.B1,
                (float)section.B2,
                (float)section.A0,
                (float)section.A1,
                (float)section.A2);
        }

        return output;
    }

    private static float[] ConvertToFloat32(ReadOnlySpan<double> input)
    {
        var output = new float[input.Length];
        ConvertToFloat32(input, output);
        return output;
    }

    private static unsafe void ConvertToFloat32(ReadOnlySpan<double> input, Span<float> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Output span is shorter than the input span.", nameof(output));
        }

        int index = 0;
        if (Avx.IsSupported)
        {
            fixed (double* inputPointer = input)
            fixed (float* outputPointer = output)
            {
                int vectorizedEnd = input.Length - (input.Length % 4);
                for (; index < vectorizedEnd; index += 4)
                {
                    Sse.Store(
                        outputPointer + index,
                        Avx.ConvertToVector128Single(Avx.LoadVector256(inputPointer + index)));
                }
            }
        }

        for (; index < input.Length; index++)
        {
            output[index] = (float)input[index];
        }
    }

    private static unsafe void ConvertToFloat64(ReadOnlySpan<float> input, Span<double> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Output span is shorter than the input span.", nameof(output));
        }

        int index = 0;
        if (Avx.IsSupported)
        {
            fixed (float* inputPointer = input)
            fixed (double* outputPointer = output)
            {
                int vectorizedEnd = input.Length - (input.Length % 4);
                for (; index < vectorizedEnd; index += 4)
                {
                    Avx.Store(
                        outputPointer + index,
                        Avx.ConvertToVector256Double(Sse.LoadVector128(inputPointer + index)));
                }
            }
        }

        for (; index < input.Length; index++)
        {
            output[index] = input[index];
        }
    }

    private static float[,] SteadyStateInitialConditionsFloat32(IReadOnlyList<FloatSosSection> sections)
    {
        var zi = new float[sections.Count, 2];
        float scale = 1.0f;
        for (int i = 0; i < sections.Count; i++)
        {
            FloatSosSection section = sections[i];
            float firstTerm = section.B1 - (section.A1 * section.B0);
            float secondTerm = section.B2 - (section.A2 * section.B0);
            float numeratorSum = (0.0f + firstTerm) + secondTerm;
            float denominatorSum = (1.0f + section.A1) + section.A2;
            float z0 = numeratorSum / denominatorSum;
            float z1 = ((1.0f + section.A1) * z0) - firstTerm;
            zi[i, 0] = scale * z0;
            zi[i, 1] = scale * z1;

            float numeratorDc = ((0.0f + section.B0) + section.B1) + section.B2;
            float denominatorDc = ((0.0f + section.A0) + section.A1) + section.A2;
            scale *= numeratorDc / denominatorDc;
        }

        return zi;
    }

    private static float[,] ScaleInitialConditionsFloat32(float[,] zi, float scale)
    {
        var output = new float[zi.GetLength(0), zi.GetLength(1)];
        for (int i = 0; i < zi.GetLength(0); i++)
        {
            output[i, 0] = zi[i, 0] * scale;
            output[i, 1] = zi[i, 1] * scale;
        }

        return output;
    }

    private static double[] OddExtension(ReadOnlySpan<double> input, int edge)
    {
        if (input.Length <= edge)
        {
            throw new ArgumentException("Input length must be greater than pad length.");
        }

        var output = new double[input.Length + (edge * 2)];
        double first = input[0];
        for (int i = 0; i < edge; i++)
        {
            output[i] = (2.0 * first) - input[edge - i];
        }

        input.CopyTo(output.AsSpan(edge, input.Length));

        double last = input[^1];
        for (int i = 0; i < edge; i++)
        {
            output[edge + input.Length + i] = (2.0 * last) - input[input.Length - 2 - i];
        }

        return output;
    }

    private static void WriteOddExtensionFloat32(
        ReadOnlySpan<float> input,
        int edge,
        Span<float> output)
    {
        if (input.Length <= edge)
        {
            throw new ArgumentException("Input length must be greater than pad length.");
        }

        int outputLength = checked(input.Length + (edge * 2));
        if (output.Length < outputLength)
        {
            throw new ArgumentException("Output span is shorter than the extended input.", nameof(output));
        }

        float first = input[0];
        for (int i = 0; i < edge; i++)
        {
            output[i] = (2.0f * first) - input[edge - i];
        }

        input.CopyTo(output.Slice(edge, input.Length));

        float last = input[^1];
        for (int i = 0; i < edge; i++)
        {
            output[edge + input.Length + i] = (2.0f * last) - input[input.Length - 2 - i];
        }
    }

    private static void WriteOddExtensionFloat32(
        ReadOnlySpan<double> input,
        int edge,
        Span<float> output)
    {
        if (input.Length <= edge)
        {
            throw new ArgumentException("Input length must be greater than pad length.");
        }

        int outputLength = checked(input.Length + (edge * 2));
        if (output.Length < outputLength)
        {
            throw new ArgumentException("Output span is shorter than the extended input.", nameof(output));
        }

        float first = (float)input[0];
        for (int i = 0; i < edge; i++)
        {
            output[i] = (2.0f * first) - (float)input[edge - i];
        }

        ConvertToFloat32(input, output.Slice(edge, input.Length));

        float last = (float)input[^1];
        for (int i = 0; i < edge; i++)
        {
            output[edge + input.Length + i] = (2.0f * last) - (float)input[input.Length - 2 - i];
        }
    }
}
