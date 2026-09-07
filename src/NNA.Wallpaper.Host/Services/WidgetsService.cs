using System.Text.Json;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /widgets — scans the built-in and user widget folders for subfolders containing a
/// widget.json manifest (see docs/manuals/WIDGET-SDK.md). Built-ins are scanned first, so a
/// duplicate id in the user folder loses and is reported in "errors" rather than replacing it.
/// </summary>
public sealed class WidgetsService : IHostService
{
    private readonly HostContext _ctx;

    public WidgetsService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api) => api.Map("GET", "/widgets", Handle);

    private Task Handle(ApiRequest req)
    {
        var widgets = new JsonArray();
        var errors = new JsonArray();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanDir(_ctx.Paths.BuiltInWidgetsDir, "builtin", widgets, errors, seenIds);
        ScanDir(_ctx.Paths.UserWidgetsDir, "user", widgets, errors, seenIds);

        var obj = new JsonObject { ["widgets"] = widgets, ["errors"] = errors };
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    private static void ScanDir(string dir, string source, JsonArray widgets, JsonArray errors, HashSet<string> seenIds)
    {
        if (!Directory.Exists(dir)) return;

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return;
        }

        foreach (var sub in subdirs)
        {
            var dirName = Path.GetFileName(sub);
            var manifestPath = Path.Combine(sub, "widget.json");
            if (!File.Exists(manifestPath)) continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(File.ReadAllText(manifestPath), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            }
            catch (Exception ex)
            {
                errors.Add(new JsonObject { ["dir"] = dirName, ["error"] = ex.Message });
                continue;
            }

            if (node is not JsonObject manifest)
            {
                errors.Add(new JsonObject { ["dir"] = dirName, ["error"] = "widget.json is not an object" });
                continue;
            }

            var id = manifest["id"] is JsonValue idv && idv.TryGetValue<string>(out var idStr) ? idStr : null;
            if (string.IsNullOrEmpty(id))
            {
                errors.Add(new JsonObject { ["dir"] = dirName, ["error"] = "missing id" });
                continue;
            }

            if (!seenIds.Add(id))
            {
                errors.Add(new JsonObject { ["dir"] = dirName, ["error"] = "duplicate id: " + id });
                continue;
            }

            manifest["source"] = source;
            manifest["dir"] = dirName;
            widgets.Add(manifest);
        }
    }
}
