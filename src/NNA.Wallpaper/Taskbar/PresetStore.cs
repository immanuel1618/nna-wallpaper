using System.IO;
using System.Text.Json.Nodes;
using NNA.Wallpaper.Host;

namespace NNA.Wallpaper.Taskbar;

/// <summary>Built-in presets ship in &lt;install&gt;\presets\taskbar; user presets live in &lt;data&gt;\presets\taskbar.</summary>
public static class PresetStore
{
    private static string BuiltInDir(HostContext ctx) => Path.Combine(ctx.Paths.WebRoot("presets"), "taskbar");
    private static string UserDir(HostContext ctx) => Path.Combine(ctx.Paths.DataDir, "presets", "taskbar");

    public static JsonObject List(HostContext ctx)
    {
        JsonArray Scan(string dir, bool user)
        {
            var arr = new JsonArray();
            if (!Directory.Exists(dir)) return arr;
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x))
            {
                if (Path.GetFileName(f).Equals("index.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (Json.LoadFile(f) is not JsonObject o) continue;
                var id = o["id"]?.ToString() ?? Path.GetFileNameWithoutExtension(f);
                arr.Add(new JsonObject { ["id"] = id, ["name"] = o["name"]?.DeepClone(), ["file"] = Path.GetFileName(f), ["user"] = user });
            }
            return arr;
        }
        return new JsonObject { ["builtin"] = Scan(BuiltInDir(ctx), false), ["user"] = Scan(UserDir(ctx), true) };
    }

    public static JsonObject? Read(HostContext ctx, string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) return null;
        foreach (var dir in new[] { UserDir(ctx), BuiltInDir(ctx) })
        {
            var f = Path.Combine(dir, id + ".json");
            if (File.Exists(f) && Json.LoadFile(f) is JsonObject o) return o;
        }
        return null;
    }

    public static bool SaveUser(HostContext ctx, JsonObject preset, out string id)
    {
        id = (preset["id"]?.ToString() ?? "").Trim().ToLowerInvariant();
        if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) return false;
        if (preset["taskbar"] is not JsonObject || preset["topBar"] is not JsonObject || preset["name"] is null) return false;
        Directory.CreateDirectory(UserDir(ctx));
        Json.WriteFileAtomic(Path.Combine(UserDir(ctx), id + ".json"), preset.ToJsonString(Json.Config));
        return true;
    }
}
