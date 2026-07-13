using VHSDecode.Core.Tbc;

namespace VHSDecode.Core.Decode;

public sealed record TbcOutputPaths(string TbcPath, string JsonPath, string? ChromaPath = null, string? DbPath = null);

public sealed record TbcFirstFieldDecodeResult(
    bool Success,
    string Message,
    TbcOutputPaths? Paths,
    TbcDecodedField? Field);

public sealed class TbcFirstFieldDecodeEngine
{
    public TbcFirstFieldDecodeEngine(int extraReadLines = 3)
    {
        if (extraReadLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraReadLines));
        }

        ExtraReadLines = extraReadLines;
    }

    public int ExtraReadLines { get; }

    public TbcFirstFieldDecodeResult TryDecodeAndWrite(DecodeSession session)
    {
        try
        {
            if (!File.Exists(session.InputFile))
            {
                return Fail($"Input file was not found: {session.InputFile}");
            }

            using FileStream input = File.OpenRead(session.InputFile);
            TbcDecodedField field = DecodeFirstField(session, input);
            return WriteDecodedField(session, field);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    public TbcDecodedField DecodeFirstField(DecodeSession session, Stream input)
    {
        int readLength = DecodeReadWindowPlanner.EstimateReadSampleCount(session, ExtraReadLines);
        DecodeReadWindow window = DecodeReadWindowPlanner.Resolve(session, session.RunBounds.StartSample, readLength);
        RfDecodedSpan? span = session.StreamDecoder.Read(input, begin: window.StartSample, length: window.SampleCount);
        if (span is null)
        {
            throw new InvalidOperationException(
                $"Input ended before enough samples were available for a first field ({window.SampleCount} samples requested).");
        }

        return session.TbcFieldDecoder.Decode(span);
    }

    public TbcFirstFieldDecodeResult WriteDecodedField(DecodeSession session, TbcDecodedField field)
    {
        TbcOutputPaths paths = BuildOutputPaths(session);
        using (FileStream tbc = File.Create(paths.TbcPath))
        {
            TbcOutputWriter.WriteFrame(tbc, field.Samples, session.TbcFrameSpec, field.OutputPayload);
        }

        if (ShouldWriteChroma(session, [field]))
        {
            WriteChromaFields(session, [field], paths.ChromaPath);
        }

        TbcOutputMetadataWriter.WriteJson(session, [field], paths.JsonPath);
        if (session.Spec.Name == "ld" || session.ExecutionOptions.WriteDebugData)
        {
            TbcSqliteMetadataWriter.Write(session, [field], paths.DbPath!);
        }

        return new TbcFirstFieldDecodeResult(true, $"Wrote first TBC field to {paths.TbcPath}", paths, field);
    }

    public static TbcOutputPaths BuildOutputPaths(DecodeSession session)
    {
        bool oldRawChroma = session.ChromaOptions is { WriteChroma: true, UseOldRawChromaOutput: true };
        return BuildOutputPaths(session.OutputBase, oldRawChroma);
    }

    public static TbcOutputPaths BuildOutputPaths(string outputBase, bool oldRawChroma = false)
    {
        string tbcPath = outputBase + (oldRawChroma ? ".tbcy" : ".tbc");
        string chromaPath = outputBase + (oldRawChroma ? ".tbcc" : "_chroma.tbc");
        return new TbcOutputPaths(tbcPath, outputBase + ".tbc.json", chromaPath, outputBase + ".tbc.db");
    }

    internal static bool ShouldWriteChroma(DecodeSession session, IReadOnlyList<TbcDecodedField> fields)
    {
        return session.ChromaOptions?.WriteChroma == true
            && fields.Any(static field => field.ChromaSamples is not null);
    }

    internal static void WriteChromaFields(
        DecodeSession session,
        IReadOnlyList<TbcDecodedField> fields,
        string? chromaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chromaPath);
        using FileStream chroma = File.Create(chromaPath);
        foreach (TbcDecodedField field in fields)
        {
            if (field.ChromaSamples is null)
            {
                throw new InvalidOperationException("VHS chroma output was enabled but a decoded field did not contain chroma samples.");
            }

            if (field.ChromaSamples.Length != session.TbcFrameSpec.FieldSampleCount)
            {
                throw new ArgumentException(
                    $"Decoded chroma field sample count {field.ChromaSamples.Length} does not match TBC frame spec {session.TbcFrameSpec.FieldSampleCount} for {chromaPath}.");
            }

            TbcOutputWriter.WriteFrame(chroma, field.ChromaSamples, session.TbcFrameSpec);
        }
    }

    private static TbcFirstFieldDecodeResult Fail(string message)
    {
        return new TbcFirstFieldDecodeResult(false, message, null, null);
    }

}
