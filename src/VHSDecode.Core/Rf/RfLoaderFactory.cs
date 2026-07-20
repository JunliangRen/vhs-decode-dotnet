using System.Globalization;

namespace VHSDecode.Core.Rf;

public static class RfLoaderFactory
{
    public static IRfSampleLoader CreateNative(string filename)
    {
        if (filename.EndsWith(".lds", StringComparison.Ordinal))
        {
            return new PackedDdD4To40SampleLoader();
        }

        if (filename.EndsWith(".r30", StringComparison.Ordinal))
        {
            return new Packed3To32SampleLoader();
        }

        if (filename.EndsWith(".rf", StringComparison.Ordinal))
        {
            return new Float32SampleLoader();
        }

        if (filename.EndsWith(".s16", StringComparison.Ordinal))
        {
            return new Int16SampleLoader();
        }

        if (filename.EndsWith(".r16", StringComparison.Ordinal) || filename.EndsWith(".u16", StringComparison.Ordinal))
        {
            return new UInt16SampleLoader();
        }

        if (filename.EndsWith(".r8", StringComparison.Ordinal) || filename.EndsWith(".u8", StringComparison.Ordinal))
        {
            return new UInt8SampleLoader();
        }

        if (filename.EndsWith(".ldf", StringComparison.Ordinal)
            || filename.EndsWith(".flac", StringComparison.Ordinal)
            || filename.EndsWith(".vhs", StringComparison.Ordinal)
            || filename.EndsWith(".wav", StringComparison.Ordinal)
            || filename.EndsWith("raw.oga", StringComparison.Ordinal))
        {
            return new FfmpegPcm16SampleLoader(filename);
        }

        return new FfmpegStreamSampleLoader([], []);
    }

    public static IRfSampleLoader CreateResampling(string filename, double inputFrequencyMHz)
    {
        if (filename.EndsWith(".lds", StringComparison.Ordinal) || filename.EndsWith(".r30", StringComparison.Ordinal))
        {
            throw new NotSupportedException("File format not supported when resampling: " + filename);
        }

        // FFmpeg's s16le-to-s16le path is bit-preserving when no rate conversion is requested.
        if (inputFrequencyMHz == 40.0 && filename.EndsWith(".s16", StringComparison.Ordinal))
        {
            return new Int16SampleLoader();
        }

        return new FfmpegStreamSampleLoader(
            BuildResamplingInputArguments(filename),
            BuildResamplingOutputArguments(inputFrequencyMHz));
    }

    public static IReadOnlyList<string> BuildResamplingInputArguments(string filename)
    {
        if (filename.EndsWith(".s16", StringComparison.Ordinal) || filename.EndsWith(".raw", StringComparison.Ordinal))
        {
            return ["-f", "s16le"];
        }

        if (filename.EndsWith(".r16", StringComparison.Ordinal)
            || filename.EndsWith(".u16", StringComparison.Ordinal)
            || filename.EndsWith(".tbc", StringComparison.Ordinal))
        {
            return ["-f", "u16le"];
        }

        if (filename.EndsWith(".rf", StringComparison.Ordinal))
        {
            return ["-f", "f32le"];
        }

        if (filename.EndsWith(".s8", StringComparison.Ordinal))
        {
            return ["-f", "s8"];
        }

        if (filename.EndsWith(".r8", StringComparison.Ordinal) || filename.EndsWith(".u8", StringComparison.Ordinal))
        {
            return ["-f", "u8"];
        }

        return [];
    }

    public static IReadOnlyList<string> BuildResamplingOutputArguments(double inputFrequencyMHz)
    {
        if (inputFrequencyMHz == 40.0)
        {
            return [];
        }

        string inputRateHz = PythonFloatString(inputFrequencyMHz * 1_000_000.0);
        return ["-filter:a", $"asetrate={inputRateHz},aresample=40000000.0"];
    }

    private static string PythonFloatString(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        string text = value.ToString("R", CultureInfo.InvariantCulture)
            .Replace('E', 'e');
        return text.Contains('.')
            || text.Contains('e')
            ? text
            : text + ".0";
    }
}
