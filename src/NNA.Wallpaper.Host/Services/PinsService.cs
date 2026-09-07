using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /pins?d=&lt;widgetId&gt; — image files in the folder configured for that widget
/// (config/widgets/&lt;widgetId&gt;.json -&gt; "folder"). "block" and "vertical" are accepted as
/// aliases of "photos" for compatibility with the old fixed pin directories.
/// GET /pins/file/&lt;widgetId&gt;/&lt;name&gt; — serves one image byte-for-byte.
/// </summary>
public sealed class PinsService : IHostService
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp" };
    private const string DefaultWidgetId = "photos";

    private readonly HostContext _ctx;

    public PinsService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/pins", HandleList);
        api.MapPrefix("GET", "/pins/file/", HandleFile);
    }

    private Task HandleList(ApiRequest req)
    {
        var widgetId = req.Query("d");
        if (string.IsNullOrEmpty(widgetId)) widgetId = DefaultWidgetId;

        var folder = ResolveFolder(widgetId);
        var files = folder is not null ? ListImageFiles(folder) : new List<string>();

        var urls = new JsonArray();
        foreach (var f in files) urls.Add((JsonNode)("/pins/file/" + Uri.EscapeDataString(widgetId) + "/" + Uri.EscapeDataString(f)));

        var filesArray = new JsonArray();
        foreach (var f in files) filesArray.Add((JsonNode)f);

        var obj = new JsonObject
        {
            ["files"] = filesArray,
            ["folder"] = folder,
            ["urls"] = urls,
        };
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    private async Task HandleFile(ApiRequest req)
    {
        var tail = Uri.UnescapeDataString(req.Tail ?? "");
        var slash = tail.IndexOf('/');
        if (slash <= 0 || slash == tail.Length - 1)
        {
            await req.Error(400, "bad name").ConfigureAwait(false);
            return;
        }

        var widgetId = tail[..slash];
        var name = tail[(slash + 1)..];

        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            await req.Error(400, "bad name").ConfigureAwait(false);
            return;
        }

        var folder = ResolveFolder(widgetId);
        if (folder is null)
        {
            await req.Error(404, "not found").ConfigureAwait(false);
            return;
        }

        var files = ListImageFiles(folder);
        if (!files.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            await req.Error(404, "not found").ConfigureAwait(false);
            return;
        }

        var full = Path.Combine(folder, name);
        if (!File.Exists(full))
        {
            await req.Error(404, "not found").ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(full).ConfigureAwait(false);
        await req.Bytes(bytes, LocalApi.Mime(full), 200, "max-age=3600").ConfigureAwait(false);
    }

    private string? ResolveFolder(string widgetId)
    {
        var effectiveId = widgetId is "block" or "vertical" ? DefaultWidgetId : widgetId;

        JsonObject settings;
        try
        {
            settings = _ctx.Config.LoadWidgetSettings(effectiveId);
        }
        catch
        {
            return null; // invalid widget id characters
        }

        if (settings["folder"] is JsonValue v && v.TryGetValue<string>(out var folder) && !string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            return folder;
        return null;
    }

    private static List<string> ListImageFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Select(Path.GetFileName)
                .Where(n => n is not null && ImageExtensions.Contains(Path.GetExtension(n).ToLowerInvariant()))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
