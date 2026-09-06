using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NNA.Wallpaper.Host;

/// <summary>Shared serializer options and small helpers.</summary>
public static class Json
{
    /// <summary>
    /// API responses: keys are written exactly as named in C# (the helper contract uses snake_case
    /// like <c>uptime_s</c>), Cyrillic is not escaped (matches Python's ensure_ascii=False).
    /// </summary>
    public static readonly JsonSerializerOptions Api = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Config files: camelCase, indented, nulls omitted.</summary>
    public static readonly JsonSerializerOptions Config = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Api);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Config);

    public static JsonNode? ParseNode(string json)
    {
        try { return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch { return null; }
    }

    public static JsonNode? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return ParseNode(File.ReadAllText(path));
        }
        catch { return null; }
    }

    /// <summary>Atomic write: temp file next to the target, then replace.</summary>
    public static void WriteFileAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
