using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VHSDecode.Core.Decode;

public static class DecodeSessionLogWriter
{
    private static readonly object WriteLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 4
    };

    public static string Write(DecodeSession session, string version = DecodeVersionInfo.Version)
    {
        string path = session.OutputBase + ".log";
        var builder = new StringBuilder();
        if (session.Spec.Name == "ld")
        {
            AppendRecord(builder, "DEBUG", $"ld-decode version {version}");
        }
        else if (session.Spec.Name == "vhs")
        {
            if (session.ExecutionOptions.CxAdcCompatibilityMode)
            {
                AppendRecord(builder, "WARNING", "--cxadc is deprecated! use -f 8fsc instead!");
            }

            AppendRecord(
                builder,
                "DEBUG",
                "Sys Parameters: " + Environment.NewLine + SerializeSorted(session.Parameters.SysParams));
            AppendRecord(
                builder,
                "DEBUG",
                "RF Parameters: " + Environment.NewLine + SerializeVhsDecoderParameters(
                    session.Parameters.SysParams,
                    session.Parameters.RfParams));
        }

        lock (WriteLock)
        {
            File.WriteAllText(path, builder.ToString());
        }

        return path;
    }

    public static void Append(DecodeSession session, string level, string message)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentNullException.ThrowIfNull(message);
        var builder = new StringBuilder();
        AppendRecord(builder, level, message);
        lock (WriteLock)
        {
            File.AppendAllText(session.OutputBase + ".log", builder.ToString());
            session.RuntimeReporter?.Log(level, message);
        }
    }

    public static void Status(DecodeSession session, string message)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        var builder = new StringBuilder();
        AppendRecord(builder, "DEBUG", message);
        lock (WriteLock)
        {
            File.AppendAllText(session.OutputBase + ".log", builder.ToString());
            session.RuntimeReporter?.Status(message);
        }
    }

    public static void Append(string path, string level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentNullException.ThrowIfNull(message);

        var builder = new StringBuilder();
        AppendRecord(builder, level, message);
        lock (WriteLock)
        {
            File.AppendAllText(path, builder.ToString());
        }
    }

    private static string SerializeSorted(JsonElement element)
    {
        JsonNode? sorted = SortNode(element);
        return sorted?.ToJsonString(JsonOptions) ?? "null";
    }

    private static string SerializeVhsDecoderParameters(JsonElement sysParams, JsonElement rfParams)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (JsonProperty property in rfParams.EnumerateObject())
        {
            values[property.Name] = SortNode(property.Value);
        }

        foreach (string name in new[] { "hz_ire", "ire0", "vsync_ire" })
        {
            if (sysParams.TryGetProperty(name, out JsonElement value))
            {
                values[name] = SortNode(value);
            }
        }

        values["track_ire0_offset"] = sysParams.TryGetProperty("track_ire0_offset", out JsonElement trackOffset)
            ? SortNode(trackOffset)
            : new JsonArray(JsonValue.Create(0), JsonValue.Create(0));

        var output = new JsonObject();
        foreach ((string name, JsonNode? value) in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            output[name] = value;
        }

        return output.ToJsonString(JsonOptions);
    }

    private static JsonNode? SortNode(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var output = new JsonObject();
            foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                output[property.Name] = SortNode(property.Value);
            }

            return output;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var output = new JsonArray();
            foreach (JsonElement item in element.EnumerateArray())
            {
                output.Add(SortNode(item));
            }

            return output;
        }

        return JsonNode.Parse(element.GetRawText());
    }

    private static void AppendRecord(StringBuilder builder, string level, string message)
    {
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture));
        builder.Append(" - lddecode - ");
        builder.Append(level);
        builder.Append(" - ");
        builder.AppendLine(message);
    }
}
