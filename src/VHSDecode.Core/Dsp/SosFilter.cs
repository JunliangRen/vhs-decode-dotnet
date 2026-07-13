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

        var output = input.ToArray();
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

        return output;
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
        double[] forward = ApplyForward(sections, extended, firstZi);
        Array.Reverse(forward);
        double[,] secondZi = ScaleInitialConditions(zi, forward[0]);
        double[] backward = ApplyForward(sections, forward, secondZi);
        Array.Reverse(backward);

        if (edge == 0)
        {
            return backward;
        }

        var trimmed = new double[input.Length];
        Array.Copy(backward, edge, trimmed, 0, trimmed.Length);
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

        var floatSections = new FloatSosSection[sections.Count];
        for (int i = 0; i < floatSections.Length; i++)
        {
            SosSection section = sections[i];
            floatSections[i] = new FloatSosSection(
                (float)section.B0,
                (float)section.B1,
                (float)section.B2,
                (float)section.A0,
                (float)section.A1,
                (float)section.A2);
        }

        var values = new float[input.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (float)input[i];
        }

        float[] extended = edge == 0 ? values : OddExtensionFloat32(values, edge);
        float[,] zi = SteadyStateInitialConditionsFloat32(floatSections);
        float[,] firstZi = ScaleInitialConditionsFloat32(zi, extended[0]);
        float[] forward = ApplyForwardFloat32(floatSections, extended, firstZi);
        Array.Reverse(forward);
        float[,] secondZi = ScaleInitialConditionsFloat32(zi, forward[0]);
        float[] backward = ApplyForwardFloat32(floatSections, forward, secondZi);
        Array.Reverse(backward);

        int outputStart = edge == 0 ? 0 : edge;
        var output = new double[input.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = backward[outputStart + i];
        }

        return output;
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

    private static float[] ApplyForwardFloat32(
        IReadOnlyList<FloatSosSection> sections,
        ReadOnlySpan<float> input,
        float[,] initialConditions)
    {
        if (initialConditions.GetLength(0) != sections.Count || initialConditions.GetLength(1) != 2)
        {
            throw new ArgumentException("Initial condition array must have shape [sections, 2].", nameof(initialConditions));
        }

        var states = (float[,])initialConditions.Clone();
        var output = new float[input.Length];
        for (int sample = 0; sample < input.Length; sample++)
        {
            float value = input[sample];
            for (int sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
            {
                FloatSosSection section = sections[sectionIndex];
                float filtered = (section.B0 * value) + states[sectionIndex, 0];
                states[sectionIndex, 0] =
                    (section.B1 * value) - (section.A1 * filtered) + states[sectionIndex, 1];
                states[sectionIndex, 1] = (section.B2 * value) - (section.A2 * filtered);
                value = filtered;
            }

            output[sample] = value;
        }

        return output;
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

    private static float[] OddExtensionFloat32(ReadOnlySpan<float> input, int edge)
    {
        if (input.Length <= edge)
        {
            throw new ArgumentException("Input length must be greater than pad length.");
        }

        var output = new float[input.Length + (edge * 2)];
        float first = input[0];
        for (int i = 0; i < edge; i++)
        {
            output[i] = (2.0f * first) - input[edge - i];
        }

        input.CopyTo(output.AsSpan(edge, input.Length));

        float last = input[^1];
        for (int i = 0; i < edge; i++)
        {
            output[edge + input.Length + i] = (2.0f * last) - input[input.Length - 2 - i];
        }

        return output;
    }
}
