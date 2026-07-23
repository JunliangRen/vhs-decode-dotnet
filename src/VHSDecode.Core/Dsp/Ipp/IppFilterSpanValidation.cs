namespace VHSDecode.Core.Dsp.Ipp;

internal static class IppFilterSpanValidation
{
    internal static void ValidateProcessBuffers(
        ReadOnlySpan<double> input,
        Span<double> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException(
                "Output must be at least as long as the input.",
                nameof(output));
        }

        Span<double> destination = output[..input.Length];
        if (input.Overlaps(destination, out int elementOffset) && elementOffset != 0)
        {
            throw new ArgumentException(
                "Input and output may be identical or disjoint, but must not partially overlap.",
                nameof(output));
        }
    }
}
