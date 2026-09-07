using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Backs the mac-like dock (stage 9): GET /dock/items merges pinned launch.json items with
/// currently open windows (matched the same way LaunchService raises them — aumid, then exe
/// path/process name), GET/POST /dock/trash* wrap the recycle bin, GET /dock/folder and
/// /dock/file-icon serve the "fan" preview for the configured folders (Downloads/Desktop by
/// default). Registers its own static web root ("/dock/" -&gt; dock/index.html etc.) so
/// HostServices.RegisterServices only needs the one <c>Add(new DockService(_ctx))</c> line.
///
/// Pinning: <c>AppSettings.Dock.Pinned</c> is the source of truth once non-empty. Empty means
/// "derive automatically": launch.json items flagged <c>"dock": true</c>, else the first 8 items
/// of a group whose id is "work" (case-insensitive), else the first 8 items overall, in
/// launch.json's own order.
/// </summary>
public sealed class DockService : IHostService, IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(500);

    private readonly HostContext _ctx;
    private readonly object _cacheLock = new();
    private readonly object _pinLock = new();
    private DateTime _cacheAt = DateTime.MinValue;
    private JsonObject _cache = new();

    private readonly System.Threading.Timer _pollTimer;
    private string _lastWindowSignature = "";
    private bool _disposed;

    public DockService(HostContext ctx)
    {
        _ctx = ctx;
        // 1s poll of the (already 500ms-cached) window list; only broadcasts "dock-changed" when
        // the open-window set actually changed, so this never floods /events on an idle desktop.
        _pollTimer = new System.Threading.Timer(_ => PollWindows(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Register(LocalApi api)
    {
        api.MapStatic("/dock/", _ctx.Paths.WebRoot("dock"));
        api.Map("GET", "/dock/items", GetItems);
        api.Map("POST", "/dock/pin", Pin);
        api.Map("POST", "/dock/unpin", Unpin);
        api.Map("POST", "/dock/reorder", Reorder);
        api.Map("GET", "/dock/trash", GetTrash);
        api.Map("POST", "/dock/trash/open", OpenTrash);
        api.Map("POST", "/dock/trash/empty", EmptyTrash);
        api.Map("GET", "/dock/folder", GetFolder);
        api.Map("POST", "/dock/folder/open", OpenFolderInExplorer);
        api.Map("GET", "/dock/file-icon", GetFileIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
    }

    // --------------------------------------------------------------------------- GET /dock/items

    private Task GetItems(ApiRequest req) =>
        req.Text(GetItemsCached().ToJsonString(Json.Api), "application/json; charset=utf-8");

    private JsonObject GetItemsCached()
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _cacheAt < CacheTtl) return _cache;
        }
        JsonObject fresh;
        try
        {
            fresh = BuildItemsNow();
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("dock/items build failed", ex);
            fresh = new JsonObject { ["items"] = new JsonArray() };
        }
        lock (_cacheLock)
        {
            _cache = fresh;
            _cacheAt = DateTime.UtcNow;
        }
        return fresh;
    }

    private JsonObject BuildItemsNow()
    {
        var dockCfg = _ctx.Config.App.Dock;
        var ws = WindowsService.Current;
        var all = ParseLaunchItems(out var workGroupIds);
        var byId = new Dictionary<string, RawLaunchItem>(StringComparer.Ordinal);
        foreach (var item in all) byId[item.Id] = item;

        var claimed = new HashSet<long>();
        var outItems = new JsonArray();

        foreach (var id in ResolvePinnedIds(all, workGroupIds))
        {
            if (!byId.TryGetValue(id, out var item)) continue;
            outItems.Add(BuildAppEntry(item, pinned: true, ws, claimed));
        }

        if (dockCfg.ShowRunning && ws is not null)
        {
            var order = new List<string>();
            var groups = new Dictionary<string, List<WindowsService.WindowInfo>>(StringComparer.Ordinal);
            foreach (var w in ws.Snapshot())
            {
                if (claimed.Contains(w.Hwnd)) continue;
                var key = GroupKey(w);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<WindowsService.WindowInfo>();
                    groups[key] = list;
                    order.Add(key);
                }
                list.Add(w);
            }
            foreach (var key in order)
            {
                var wins = groups[key];
                var first = wins[0];
                var title = !string.IsNullOrEmpty(first.ProcessName) ? first.ProcessName! : first.Title;
                var icon = "/windows/icon?hwnd=" + first.Hwnd;
                var entry = BuildEntryFromWindows("win:" + key, title, icon, wins, launchId: null, path: first.ExePath);
                entry["pinned"] = false;
                outItems.Add(entry);
            }
        }

        outItems.Add(new JsonObject { ["kind"] = "separator" });

        var fi = 0;
        foreach (var folder in ResolvedFolders())
        {
            outItems.Add(new JsonObject
            {
                ["kind"] = "folder",
                ["id"] = "folder:" + fi,
                ["title"] = FolderTitle(folder),
                ["icon"] = "/dock/file-icon?path=" + Uri.EscapeDataString(folder),
                ["path"] = folder,
            });
            fi++;
        }

        if (dockCfg.ShowTrash)
        {
            outItems.Add(new JsonObject { ["kind"] = "trash", ["id"] = "trash", ["title"] = "Trash" });
        }

        // HostServices' GET /config is off-limits for this stage (only one line of HostServices
        // may change, in RegisterServices), so the dock page reads its own rendering settings —
        // icon size, magnify, style — from here instead of from /config like topbar/bar.js does.
        return new JsonObject
        {
            ["items"] = outItems,
            ["settings"] = new JsonObject
            {
                ["size"] = dockCfg.Size,
                ["magnify"] = dockCfg.Magnify,
                ["magnifyMax"] = dockCfg.MagnifyMax,
                ["autoHide"] = dockCfg.AutoHide,
                ["style"] = new JsonObject
                {
                    ["mode"] = dockCfg.Style.Mode,
                    ["color"] = dockCfg.Style.Color,
                    ["opacity"] = dockCfg.Style.Opacity,
                },
            },
        };
    }

    private static string GroupKey(WindowsService.WindowInfo w)
    {
        if (!string.IsNullOrEmpty(w.Aumid)) return "aumid:" + w.Aumid;
        if (!string.IsNullOrEmpty(w.ExePath)) return "exe:" + w.ExePath.ToLowerInvariant();
        return "proc:" + (w.ProcessName ?? w.Pid.ToString());
    }

    private static string FolderTitle(string full)
    {
        var name = Path.GetFileName(full.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? full : name;
    }

    private JsonObject BuildAppEntry(RawLaunchItem item, bool pinned, WindowsService? ws, HashSet<long> claimed)
    {
        var windows = ws is not null && (!string.IsNullOrEmpty(item.Aumid) || !string.IsNullOrEmpty(item.Cmd))
            ? ws.FindAllForItem(item.Aumid, item.Cmd)
            : new List<WindowsService.WindowInfo>();
        foreach (var w in windows) claimed.Add(w.Hwnd);

        var entry = BuildEntryFromWindows(item.Id, item.Label, "/icon/" + item.Id + ".png", windows, item.Id, item.Open ?? item.Cmd);
        entry["pinned"] = pinned;
        return entry;
    }

    private static JsonObject BuildEntryFromWindows(string id, string title, string icon, List<WindowsService.WindowInfo> windows, string? launchId, string? path)
    {
        var winArr = new JsonArray(windows.Select(w => (JsonNode)new JsonObject
        {
            ["hwnd"] = w.Hwnd,
            ["title"] = w.Title,
            ["minimized"] = w.Minimized,
        }).ToArray());
        return new JsonObject
        {
            ["kind"] = "app",
            ["id"] = id,
            ["title"] = title,
            ["icon"] = icon,
            ["running"] = windows.Count > 0,
            ["foreground"] = windows.Any(w => w.Foreground),
            ["windows"] = winArr,
            ["pinned"] = false,
            ["launchId"] = launchId,
            ["path"] = path,
        };
    }

    // --------------------------------------------------------------------------- launch.json (raw, own tiny parse)

    private sealed record RawLaunchItem(string Id, string Label, string? Aumid, string? Cmd, string? Open, bool Dock);

    /// <summary>
    /// Reads launch.json directly through <see cref="Config.ConfigStore.LoadLaunch"/> — DockService
    /// keeps its own tiny parse instead of reaching into LaunchService's private LaunchConfig, and
    /// only needs a few fields per item (matching aumid/cmd, the icon id, and the optional "dock"
    /// flag LaunchService does not know about).
    /// </summary>
    private List<RawLaunchItem> ParseLaunchItems(out List<string> workGroupIds)
    {
        var result = new List<RawLaunchItem>();
        workGroupIds = new List<string>();
        if (_ctx.Config.LoadLaunch() is not JsonObject root) return result;

        if (root["items"] is JsonObject items)
        {
            foreach (var kv in items)
            {
                if (kv.Value is not JsonObject io) continue;
                var label = (string?)io["label"] ?? kv.Key;
                var aumid = (string?)io["aumid"];
                var cmd = (string?)io["cmd"];
                var open = (string?)io["open"];
                var dockFlag = io["dock"] is JsonValue dv && dv.TryGetValue(out bool db) && db;
                result.Add(new RawLaunchItem(kv.Key, label, aumid, cmd, open, dockFlag));
            }
        }

        if (root["groups"] is JsonArray groups)
        {
            foreach (var g in groups)
            {
                if (g is not JsonObject go) continue;
                if (!string.Equals((string?)go["id"], "work", StringComparison.OrdinalIgnoreCase)) continue;
                if (go["items"] is not JsonArray gi) continue;
                foreach (var it in gi)
                {
                    if (it is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s)) workGroupIds.Add(s);
                }
            }
        }

        return result;
    }

    private List<string> ResolvePinnedIds(List<RawLaunchItem> all, List<string> workGroupIds)
    {
        var configured = _ctx.Config.App.Dock.Pinned;
        if (configured.Count > 0)
        {
            var known = new HashSet<string>(all.Select(a => a.Id), StringComparer.Ordinal);
            return configured.Where(id => known.Contains(id)).ToList();
        }

        var flagged = all.Where(a => a.Dock).Select(a => a.Id).ToList();
        if (flagged.Count > 0) return flagged;

        if (workGroupIds.Count > 0)
        {
            var known = new HashSet<string>(all.Select(a => a.Id), StringComparer.Ordinal);
            var fromWork = workGroupIds.Where(id => known.Contains(id)).Take(8).ToList();
            if (fromWork.Count > 0) return fromWork;
        }

        return all.Select(a => a.Id).Take(8).ToList();
    }

    // --------------------------------------------------------------------------- POST /dock/pin, /dock/unpin, /dock/reorder

    private async Task Pin(ApiRequest req)
    {
        var id = await ReadLaunchId(req).ConfigureAwait(false);
        if (string.IsNullOrEmpty(id)) { await req.Error(400, "missing launchId").ConfigureAwait(false); return; }
        List<string> pinned;
        lock (_pinLock)
        {
            pinned = _ctx.Config.App.Dock.Pinned;
            if (!pinned.Contains(id, StringComparer.Ordinal))
            {
                pinned.Add(id);
                _ctx.Config.SaveApp();
            }
        }
        await req.Json(new { ok = true, pinned }).ConfigureAwait(false);
    }

    private async Task Unpin(ApiRequest req)
    {
        var id = await ReadLaunchId(req).ConfigureAwait(false);
        if (string.IsNullOrEmpty(id)) { await req.Error(400, "missing launchId").ConfigureAwait(false); return; }
        List<string> pinned;
        lock (_pinLock)
        {
            pinned = _ctx.Config.App.Dock.Pinned;
            if (pinned.Remove(id)) _ctx.Config.SaveApp();
        }
        await req.Json(new { ok = true, pinned }).ConfigureAwait(false);
    }

    private async Task Reorder(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        if (Json.ParseNode(body) is not JsonObject obj || obj["ids"] is not JsonArray idsArr)
        {
            await req.Error(400, "expected {ids:[]}").ConfigureAwait(false);
            return;
        }
        var ids = new List<string>();
        foreach (var n in idsArr)
        {
            if (n is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s)) ids.Add(s);
        }
        lock (_pinLock)
        {
            _ctx.Config.App.Dock.Pinned = ids;
            _ctx.Config.SaveApp();
        }
        await req.Json(new { ok = true, pinned = ids }).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLaunchId(ApiRequest req)
    {
        var q = req.Query("launchId") ?? req.Query("id");
        if (!string.IsNullOrEmpty(q)) return q;

        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        if (Json.ParseNode(body) is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("launchId", out var n) && n is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s)) return s;
            if (obj.TryGetPropertyValue("id", out var n2) && n2 is JsonValue v2 && v2.TryGetValue(out string? s2) && !string.IsNullOrEmpty(s2)) return s2;
        }
        return null;
    }

    // --------------------------------------------------------------------------- trash

    // Recycle bin: shell32's SHQueryRecycleBin/SHEmptyRecycleBin are architecture-specific in the
    // Win32 metadata (CsWin32 refuses to generate them for this AnyCPU-by-default host project —
    // "PInvoke005: only available when targeting a specific CPU architecture"), so — same as
    // TaskbarStyler's SetWindowCompositionAttribute — they are declared here directly instead of
    // widening NativeMethods.txt/PlatformTarget for the whole project over two routes.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(nint hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    private Task GetTrash(ApiRequest req)
    {
        long itemsCount = 0, bytes = 0;
        bool ok;
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            ok = SHQueryRecycleBinW(null, ref info) == 0;
            if (ok)
            {
                itemsCount = info.i64NumItems;
                bytes = info.i64Size;
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("trash query failed: " + ex.Message);
            ok = false;
        }
        return req.Json(new { ok, empty = itemsCount == 0, items = itemsCount, bytes });
    }

    private Task OpenTrash(ApiRequest req)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
            return req.Json(new { ok = true });
        }
        catch (Exception ex)
        {
            return req.Json(new { ok = false, error = ex.Message });
        }
    }

    private async Task EmptyTrash(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var confirmed = Json.ParseNode(body) is JsonObject obj
            && obj.TryGetPropertyValue("confirm", out var c) && c is JsonValue v && v.TryGetValue(out bool b) && b;
        if (!confirmed)
        {
            await req.Error(400, "confirm required").ConfigureAwait(false);
            return;
        }
        try
        {
            // The dock page already asked the owner to confirm (NNAUI.dialog), so skip Windows'
            // own confirmation prompt and progress dialog too — they would just be a second ask.
            var flags = SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND;
            var hr = SHEmptyRecycleBinW(0, null, flags);
            await req.Json(new { ok = hr == 0 }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("trash empty failed: " + ex.Message);
            await req.Json(new { ok = false, error = ex.Message }).ConfigureAwait(false);
        }
    }

    // --------------------------------------------------------------------------- folders (fan)

    /// <summary>Configured folders (env vars expanded), existing ones only, in configured order.</summary>
    private IEnumerable<string> ResolvedFolders()
    {
        foreach (var raw in _ctx.Config.App.Dock.Folders)
        {
            string full;
            try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw)); }
            catch { continue; }
            if (Directory.Exists(full)) yield return full;
        }
    }

    /// <summary>
    /// Only ever lists one of the configured dock folders — this route needs no API token (GET),
    /// so it must not become a generic "list any directory on disk" endpoint.
    /// </summary>
    private Task GetFolder(ApiRequest req)
    {
        var raw = req.Query("path") ?? "";
        string full;
        try { full = Path.GetFullPath(raw); }
        catch { return req.Error(400, "bad path"); }

        if (!ResolvedFolders().Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
            return req.Error(403, "path not allowed");
        if (!Directory.Exists(full)) return req.Error(404, "not found");

        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(full).EnumerateFileSystemInfos()
                .OrderByDescending(SafeWriteTime)
                .Take(21)
                .ToList();
        }
        catch (Exception ex)
        {
            return req.Error(500, ex.Message);
        }

        var arr = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
        {
            ["name"] = e.Name,
            ["path"] = e.FullName,
            ["isDir"] = e is DirectoryInfo,
            ["icon"] = "/dock/file-icon?path=" + Uri.EscapeDataString(e.FullName),
        }).ToArray());
        return req.Json(new { entries = arr });
    }

    /// <summary>
    /// Opens one of the configured dock folders in Explorer — a dedicated route rather than
    /// reusing <c>POST /open</c> (OpenService), because that one only allows paths under
    /// <c>AppSettings.OpenRoots</c>, which is empty on a clean install; a folder the owner
    /// explicitly listed in <c>Dock.Folders</c> should not additionally need OpenRoots.
    /// </summary>
    private async Task OpenFolderInExplorer(ApiRequest req)
    {
        var raw = req.Query("path");
        if (string.IsNullOrEmpty(raw))
        {
            var body = await req.ReadBodyAsync().ConfigureAwait(false);
            if (Json.ParseNode(body) is JsonObject obj && obj.TryGetPropertyValue("path", out var n)
                && n is JsonValue v && v.TryGetValue(out string? s)) raw = s;
        }

        string full;
        try { full = Path.GetFullPath(raw ?? ""); }
        catch { await req.Error(400, "bad path").ConfigureAwait(false); return; }

        if (!ResolvedFolders().Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
        {
            await req.Error(403, "path not allowed").ConfigureAwait(false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + full + "\"") { UseShellExecute = true });
            await req.Json(new { ok = true }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await req.Json(new { ok = false, error = ex.Message }).ConfigureAwait(false);
        }
    }

    private static DateTime SafeWriteTime(FileSystemInfo e)
    {
        try { return e.LastWriteTimeUtc; } catch { return DateTime.MinValue; }
    }

    /// <summary>Icon for a file/folder inside (or equal to) one of the configured dock folders,
    /// same restriction as <see cref="GetFolder"/> and for the same reason (unauthenticated GET).</summary>
    private async Task GetFileIcon(ApiRequest req)
    {
        var raw = req.Query("path") ?? "";
        string full;
        try { full = Path.GetFullPath(raw); }
        catch { await req.Error(400, "bad path").ConfigureAwait(false); return; }

        var folders = ResolvedFolders().ToList();
        var allowed = folders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase))
            || folders.Any(f => full.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!allowed) { await req.Error(403, "path not allowed").ConfigureAwait(false); return; }
        if (!File.Exists(full) && !Directory.Exists(full)) { await req.Error(404, "not found").ConfigureAwait(false); return; }

        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())));
        var outFile = Path.Combine(_ctx.Paths.IconsDir, "file-" + key + ".png");
        if (!File.Exists(outFile))
        {
            var ok = await Task.Run(() => IconExtractor.TryExtract(full, null, outFile)).ConfigureAwait(false);
            if (!ok) { await req.Error(404, "no icon").ConfigureAwait(false); return; }
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(outFile).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await req.Error(404, "no icon").ConfigureAwait(false);
            return;
        }
        await req.Bytes(bytes, "image/png", 200, "max-age=3600").ConfigureAwait(false);
    }

    // --------------------------------------------------------------------------- window-change poll

    private void PollWindows()
    {
        try
        {
            var ws = WindowsService.Current;
            if (ws is null) return;
            var sig = string.Join("|", ws.Snapshot()
                .OrderBy(w => w.Hwnd)
                .Select(w => w.Hwnd + ":" + (w.Minimized ? 1 : 0) + ":" + (w.Foreground ? 1 : 0)));
            if (sig == _lastWindowSignature) return;
            _lastWindowSignature = sig;
            _ = EventsService.Current?.BroadcastAsync(new { type = "dock-changed" });
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("dock poll failed: " + ex.Message);
        }
    }
}
