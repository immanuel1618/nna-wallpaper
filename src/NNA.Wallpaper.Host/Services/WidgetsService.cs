using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using NNA.Wallpaper.Host.Config;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Optional capability an <see cref="IHostApp"/> may provide: a PNG snapshot of one monitor's live
/// wallpaper window. Implemented by <c>WallpaperEngine</c> only (see
/// <c>Engine/WallpaperWindow.cs</c>'s <c>CapturePngAsync</c>) — <c>NullHostApp</c> and
/// <c>HeadlessHostApp</c> do not implement it, so a headless or engine-less run naturally falls
/// back to the static <c>widgets/&lt;id&gt;/preview.png</c> below (see
/// <see cref="WidgetsService.HandlePreview"/>). Kept as its own interface, rather than a new
/// <see cref="IHostApp"/> member, so those two host-app implementations don't need touching for a
/// capability only the real engine can offer.
/// </summary>
public interface ICapturesPreview
{
    Task<byte[]?> CapturePreviewAsync(string monitorId);
}

/// <summary>
/// GET /widgets — scans the built-in and user widget folders for subfolders containing a
/// widget.json manifest (see docs/manuals/WIDGET-SDK.md). Built-ins are scanned first, so a
/// duplicate id in the user folder loses and is reported in "errors" rather than replacing it.
///
/// GET /widgets/&lt;id&gt;/preview.png — a 610x377 preview of one block: a live capture of that
/// widget's cell on the wallpaper (when it is actually placed on a running monitor and the host
/// app can capture, see <see cref="ICapturesPreview"/>), else the static
/// <c>widgets/&lt;id&gt;/preview.png</c> shipped with the widget. <c>?live=0</c> forces the static
/// image. Live captures are cached for <see cref="PreviewCacheMinutes"/> minutes, in memory and as
/// <c>&lt;data&gt;\cache\preview-&lt;id&gt;.png</c> (so a process restart within the window still
/// avoids a re-capture).
/// </summary>
public sealed class WidgetsService : IHostService
{
    private const int PreviewCacheMinutes = 10;

    private readonly HostContext _ctx;
    private readonly object _previewLock = new();
    private readonly Dictionary<string, (DateTime At, byte[] Png)> _previewCache = new(StringComparer.OrdinalIgnoreCase);

    public WidgetsService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/widgets", Handle);
        foreach (var id in DiscoverIds())
        {
            var widgetId = id; // capture per-iteration value for the closure below
            api.Map("GET", "/widgets/" + widgetId + "/preview.png", req => HandlePreview(req, widgetId));
        }
    }

    /// <summary>Widget ids known at startup (built-in + already-present user widgets) — enough to
    /// register one exact route per id ahead of time, so the preview route never shadows the
    /// generic <c>/widgets/</c> static file mapping (see HostServices.cs) for every other path
    /// under a widget's folder (widget.js, index.html, ...).</summary>
    private IEnumerable<string> DiscoverIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { _ctx.Paths.BuiltInWidgetsDir, _ctx.Paths.UserWidgetsDir })
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); } catch { continue; }
            foreach (var sub in subdirs)
            {
                if (File.Exists(Path.Combine(sub, "widget.json"))) ids.Add(Path.GetFileName(sub));
            }
        }
        return ids;
    }

    private async Task HandlePreview(ApiRequest req, string id)
    {
        var forceStatic = req.Query("live") == "0";

        if (!forceStatic)
        {
            var cached = GetCachedPreview(id);
            if (cached is not null)
            {
                await req.Bytes(cached, "image/png", 200, "max-age=60").ConfigureAwait(false);
                return;
            }

            var live = await TryCaptureLiveAsync(id).ConfigureAwait(false);
            if (live is not null)
            {
                SetCachedPreview(id, live);
                await req.Bytes(live, "image/png", 200, "max-age=60").ConfigureAwait(false);
                return;
            }
        }

        var staticFile = FindStaticPreview(id);
        if (staticFile is not null)
        {
            var bytes = await File.ReadAllBytesAsync(staticFile).ConfigureAwait(false);
            await req.Bytes(bytes, "image/png", 200, "max-age=3600").ConfigureAwait(false);
            return;
        }

        await req.Error(404, "no preview").ConfigureAwait(false);
    }

    private byte[]? GetCachedPreview(string id)
    {
        lock (_previewLock)
        {
            if (_previewCache.TryGetValue(id, out var entry) && DateTime.UtcNow - entry.At < TimeSpan.FromMinutes(PreviewCacheMinutes))
                return entry.Png;
        }
        // Second-level cache on disk survives a process restart within the same window.
        try
        {
            var file = PreviewCacheFile(id);
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(PreviewCacheMinutes))
            {
                var bytes = File.ReadAllBytes(file);
                lock (_previewLock) _previewCache[id] = (DateTime.UtcNow, bytes);
                return bytes;
            }
        }
        catch { /* best effort — fall through to a fresh capture */ }
        return null;
    }

    private void SetCachedPreview(string id, byte[] png)
    {
        lock (_previewLock) _previewCache[id] = (DateTime.UtcNow, png);
        try
        {
            var file = PreviewCacheFile(id);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, png);
        }
        catch { /* best effort — the in-memory cache above is what actually matters */ }
    }

    private string PreviewCacheFile(string id) => Path.Combine(_ctx.Paths.DataDir, "cache", "preview-" + SafeFileId(id) + ".png");

    private static string SafeFileId(string id) => new(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    private string? FindStaticPreview(string id)
    {
        foreach (var dir in new[] { _ctx.Paths.BuiltInWidgetsDir, _ctx.Paths.UserWidgetsDir })
        {
            var p = Path.Combine(dir, id, "preview.png");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Finds the first monitor that actually has this widget placed on it, captures that
    /// monitor's whole window and crops it down to the block's grid cell. Returns null (falls back
    /// to the static preview) whenever the widget isn't placed anywhere, the monitor isn't running,
    /// the host app can't capture (headless/no engine), or the capture/crop fails for any reason.</summary>
    private async Task<byte[]?> TryCaptureLiveAsync(string id)
    {
        if (_ctx.App is not ICapturesPreview capture) return null;

        foreach (var monitor in _ctx.Config.Monitors.Monitors)
        {
            var block = (monitor.Blocks ?? new List<BlockSpec>()).FirstOrDefault(b => string.Equals(b.Widget, id, StringComparison.OrdinalIgnoreCase));
            if (block is null) continue;

            var live = _ctx.App.Monitors.FirstOrDefault(s => string.Equals(s.id, monitor.Id, StringComparison.OrdinalIgnoreCase));
            if (live is null || !live.visible || live.width <= 0 || live.height <= 0) continue;

            byte[]? fullPng;
            try { fullPng = await capture.CapturePreviewAsync(live.id).ConfigureAwait(false); }
            catch { fullPng = null; }
            if (fullPng is null || fullPng.Length == 0) continue;

            var rect = ComputeCellRect(monitor, block, live.width, live.height, _ctx.Config.App.TopBar);
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            try
            {
                using var ms = new MemoryStream(fullPng);
                using var full = new Bitmap(ms);
                var clamped = Rectangle.Intersect(rect, new Rectangle(0, 0, full.Width, full.Height));
                if (clamped.Width <= 0 || clamped.Height <= 0) return null;
                using var crop = full.Clone(clamped, full.PixelFormat);
                using var outMs = new MemoryStream();
                crop.Save(outMs, ImageFormat.Png);
                return outMs.ToArray();
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// Pixel rectangle of one placed block on its monitor — re-derives the same geometry
    /// <c>wallpaper/layout.js</c>'s <c>updateGridGeometry</c> applies to <c>.nna-grid</c> in CSS
    /// (border-box grid, equal 1fr rows, <c>colWeights</c> or equal columns, the top bar's height
    /// added to the top padding when it covers this monitor) so the crop lines up with what the
    /// live page actually renders.
    /// </summary>
    private static Rectangle ComputeCellRect(MonitorLayout monitor, BlockSpec block, int monitorWidth, int monitorHeight, TopBarSettings topBar)
    {
        var grid = monitor.Grid ?? new GridSpec();
        var cols = Math.Max(1, grid.Cols);
        var rows = Math.Max(1, grid.Rows);
        var gap = grid.Gap;
        var pad = grid.Pad;

        var padTop = pad;
        if (topBar is { Enabled: true })
        {
            var forThisMonitor = !string.Equals(topBar.Monitors, "primary", StringComparison.OrdinalIgnoreCase)
                || (monitor.Name ?? "").StartsWith("main", StringComparison.OrdinalIgnoreCase);
            if (forThisMonitor) padTop = pad + topBar.Height;
        }

        var gridWidth = Math.Max(0, monitorWidth - pad * 2);
        var gridHeight = Math.Max(0, monitorHeight - padTop - pad);

        double[] colWidths;
        if (grid.ColWeights is { Count: > 0 } weights && weights.Count == cols)
        {
            var sum = weights.Sum();
            var avail = gridWidth - gap * (cols - 1);
            colWidths = weights.Select(w => sum > 0 ? avail * (w / sum) : avail / (double)cols).ToArray();
        }
        else
        {
            var each = (gridWidth - gap * (cols - 1)) / (double)cols;
            colWidths = Enumerable.Repeat(each, cols).ToArray();
        }
        var rowEach = (gridHeight - gap * (rows - 1)) / (double)rows;
        var rowHeights = Enumerable.Repeat(rowEach, rows).ToArray();

        var col = Math.Clamp(block.Col, 1, cols);
        var colSpan = Math.Clamp(block.ColSpan, 1, cols - col + 1);
        var row = Math.Clamp(block.Row, 1, rows);
        var rowSpan = Math.Clamp(block.RowSpan, 1, rows - row + 1);

        var x = pad + gap * (col - 1) + colWidths.Take(col - 1).Sum();
        var width = gap * (colSpan - 1) + colWidths.Skip(col - 1).Take(colSpan).Sum();
        var y = padTop + gap * (row - 1) + rowHeights.Take(row - 1).Sum();
        var height = gap * (rowSpan - 1) + rowHeights.Skip(row - 1).Take(rowSpan).Sum();

        return new Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(width), (int)Math.Round(height));
    }

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
