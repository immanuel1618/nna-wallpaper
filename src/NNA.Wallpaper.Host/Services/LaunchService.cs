using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /launch/list, GET /icon/&lt;id&gt;.png, POST /launch/item?id=, POST /launch/group?id= —
/// same shapes and semantics as helper.py's launch_list()/launch_item()/launch()/icon serving.
/// launch.json is read through <see cref="Config.ConfigStore.LoadLaunch"/> and cached by the
/// file's mtime; icons are extracted lazily and cached per id under Paths.IconsDir.
/// </summary>
public sealed class LaunchService : IHostService
{
    private readonly HostContext _ctx;

    // launch.json cache, invalidated when the file's mtime changes.
    private readonly object _cacheLock = new();
    private DateTime _cacheMtime = DateTime.MinValue;
    private LaunchConfig _cache = new();

    // Icon extraction bookkeeping: which ids we already attempted since the last launch.json
    // change (mirrors helper.py's ensure_icons "tried" set), one extraction job at a time, and a
    // per-id lock so a background sweep and an on-demand /icon/ request never race on the same id.
    private readonly HashSet<string> _iconsTried = new(StringComparer.Ordinal);
    private volatile bool _iconsSweepBusy;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _iconLocks = new(StringComparer.Ordinal);

    public LaunchService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/launch/list", GetLaunchList);
        api.MapPrefix("GET", "/icon/", GetIcon);
        api.Map("POST", "/launch/item", req => DoLaunch(req, "item"));
        api.Map("POST", "/launch/group", req => DoLaunch(req, "group"));
    }

    // --------------------------------------------------------------------------- /launch/list

    private Task GetLaunchList(ApiRequest req)
    {
        var cfg = LoadConfig();
        EnsureIconsInBackground(cfg);

        var itemsOut = new JsonObject();
        foreach (var (id, item) in cfg.Items)
        {
            var file = IconFile(id);
            string? icon = null;
            if (File.Exists(file))
            {
                long v = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero).ToUnixTimeSeconds();
                icon = $"/icon/{id}.png?v={v}";
            }
            string kind = !string.IsNullOrEmpty(item.Open) && Directory.Exists(item.Open) ? "dir" : "app";
            itemsOut[id] = new JsonObject
            {
                ["label"] = item.Label,
                ["icon"] = icon,
                ["badge"] = item.Badge,
                ["kind"] = kind,
            };
        }

        var groupsOut = new JsonArray();
        foreach (var g in cfg.Groups)
        {
            groupsOut.Add(new JsonObject
            {
                ["id"] = g.Id,
                ["label"] = g.Label,
                ["items"] = new JsonArray(g.Items.Select(i => (JsonNode)i).ToArray()),
            });
        }

        return req.Json(new JsonObject { ["groups"] = groupsOut, ["items"] = itemsOut });
    }

    // --------------------------------------------------------------------------- /icon/<name>.png

    private async Task GetIcon(ApiRequest req)
    {
        var name = req.Tail;
        if (!IsValidIconName(name))
        {
            await req.Error(400, "bad name").ConfigureAwait(false);
            return;
        }

        var id = name[..^".png".Length];
        var file = IconFile(id);

        if (!File.Exists(file))
        {
            var cfg = LoadConfig();
            if (cfg.Items.TryGetValue(id, out var item))
            {
                await Task.Run(() => ExtractIconFor(id, item)).ConfigureAwait(false);
            }
        }

        if (!File.Exists(file))
        {
            await req.Error(404, "no icon").ConfigureAwait(false);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // extraction elsewhere may still be flushing the file; treat as not-yet-ready
            await req.Error(404, "no icon").ConfigureAwait(false);
            return;
        }
        await req.Bytes(bytes, "image/png", 200, "max-age=3600").ConfigureAwait(false);
    }

    private static bool IsValidIconName(string name) =>
        name.EndsWith(".png", StringComparison.Ordinal) &&
        name.Length > ".png".Length &&
        name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    // --------------------------------------------------------------------------- POST /launch/item, /launch/group

    /// <summary>
    /// <c>newInstance</c> (query <c>?newInstance=true</c> or JSON body <c>{"newInstance":true}</c>,
    /// default false): when false and a window for the item is already open, that window is raised
    /// instead of starting a new process; a launched item is also chased for up to 5s so its window
    /// can be raised once it appears. Response keeps the original <c>ok</c>/<c>launched</c>/<c>failed</c>
    /// id arrays and adds <c>activated</c> (ids whose window was raised) and <c>windows</c> (id -&gt; hwnd,
    /// for those in <c>activated</c>).
    /// </summary>
    private async Task DoLaunch(ApiRequest req, string kind)
    {
        var id = req.Query("id") ?? "";
        var newInstance = await ReadNewInstance(req).ConfigureAwait(false);
        var cfg = LoadConfig();

        List<string> ids;
        if (kind == "item")
        {
            ids = new List<string> { id };
        }
        else
        {
            var group = cfg.Groups.FirstOrDefault(g => g.Id == id);
            if (group is null)
            {
                await req.Json(new { ok = false, error = "no such group" }).ConfigureAwait(false);
                return;
            }
            ids = new List<string>(group.Items);
        }

        var done = new List<string>();
        var failed = new List<string>();
        var activated = new List<string>();
        var windows = new JsonObject();
        foreach (var itemId in ids)
        {
            if (!cfg.Items.TryGetValue(itemId, out var item))
            {
                failed.Add(itemId);
                continue;
            }
            try
            {
                var result = await LaunchOrActivate(item, newInstance).ConfigureAwait(false);
                if (result.Launched) done.Add(itemId); else failed.Add(itemId);
                if (result.Activated)
                {
                    activated.Add(itemId);
                    if (result.Hwnd is long h) windows[itemId] = h;
                }
            }
            catch (Exception ex)
            {
                _ctx.Log.Warn($"launch failed {itemId}: {ex.Message}");
                failed.Add(itemId);
            }
            await Task.Delay(250).ConfigureAwait(false);
        }

        _ctx.Log.Info($"launch {kind} {id} ok=[{string.Join(',', done)}] failed=[{string.Join(',', failed)}] activated=[{string.Join(',', activated)}] newInstance={newInstance}");
        await req.Json(new JsonObject
        {
            ["ok"] = failed.Count == 0,
            ["launched"] = new JsonArray(done.Select(x => (JsonNode)x).ToArray()),
            ["failed"] = new JsonArray(failed.Select(x => (JsonNode)x).ToArray()),
            ["activated"] = new JsonArray(activated.Select(x => (JsonNode)x).ToArray()),
            ["windows"] = windows,
        }).ConfigureAwait(false);
    }

    private static async Task<bool> ReadNewInstance(ApiRequest req)
    {
        if (ParseBool(req.Query("newInstance"))) return true;

        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return false;
        if (Json.ParseNode(body) is JsonObject obj
            && obj.TryGetPropertyValue("newInstance", out var node)
            && node is JsonValue v)
        {
            if (v.TryGetValue(out bool b)) return b;
            if (v.TryGetValue(out string? s)) return ParseBool(s);
        }
        return false;
    }

    private static bool ParseBool(string? s) =>
        !string.IsNullOrEmpty(s) && (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase));

    private readonly record struct LaunchResult(bool Launched, bool Activated, long? Hwnd);

    /// <summary>
    /// Raises an already-open window when one matches and <paramref name="newInstance"/> is false;
    /// otherwise starts the item and, for items we can match (aumid or cmd — not "open"), waits up
    /// to 5s (polling every 250ms) for its window to appear and raises it too.
    /// </summary>
    private async Task<LaunchResult> LaunchOrActivate(LaunchItem item, bool newInstance)
    {
        var ws = WindowsService.Current;
        bool canMatch = !string.IsNullOrEmpty(item.Aumid) || !string.IsNullOrEmpty(item.Cmd);

        if (!newInstance && canMatch && ws is not null)
        {
            var existing = ws.FindForItem(item.Aumid, item.Cmd);
            if (existing is not null)
            {
                bool activated = WindowActivator.Activate((HWND)(nint)existing.Hwnd, _ctx.Log);
                return new LaunchResult(true, activated, existing.Hwnd);
            }
        }

        var (started, pid) = StartItem(item);
        if (!started) return new LaunchResult(false, false, null);

        if (canMatch && ws is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(false);
                var found = !string.IsNullOrEmpty(item.Aumid)
                    ? ws.FindForItem(item.Aumid, null)
                    : pid is int p ? ws.FindByPidTree(p) : null;
                if (found is not null)
                {
                    bool activated = WindowActivator.Activate((HWND)(nint)found.Hwnd, _ctx.Log);
                    return new LaunchResult(true, activated, found.Hwnd);
                }
            }
        }
        return new LaunchResult(true, false, null);
    }

    /// <summary>Starts one item. Check order matches helper.py: aumid, then cmd(+args), then open. Returns the started process's pid for "cmd" items (used to chase its window).</summary>
    private (bool ok, int? pid) StartItem(LaunchItem item)
    {
        if (!string.IsNullOrEmpty(item.Aumid)) return (ActivateByAumid(item.Aumid), null);

        if (!string.IsNullOrEmpty(item.Cmd))
        {
            var psi = new ProcessStartInfo(item.Cmd) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in item.Args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(item.Cwd)) psi.WorkingDirectory = item.Cwd;
            var proc = Process.Start(psi);
            return (true, proc?.Id);
        }

        if (!string.IsNullOrEmpty(item.Open))
        {
            Process.Start(new ProcessStartInfo(item.Open) { UseShellExecute = true });
            return (true, null);
        }

        return (false, null);
    }

    private bool ActivateByAumid(string aumid)
    {
        bool ok = false;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                manager.ActivateApplication(aumid, null!, ACTIVATEOPTIONS.AO_NONE, out _);
                ok = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5));

        if (ok) return true;

        _ctx.Log.Warn($"aumid activation failed for {aumid}{(error is null ? "" : ": " + error.Message)}; falling back to explorer.exe");
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex2)
        {
            _ctx.Log.Warn($"explorer.exe fallback failed for {aumid}: {ex2.Message}");
            return false;
        }
    }

    // --------------------------------------------------------------------------- icons

    private string IconFile(string id) => Path.Combine(_ctx.Paths.IconsDir, id + ".png");

    /// <summary>
    /// Mirrors helper.py's ensure_icons(): queue every item that lacks an icon file and hasn't
    /// been tried since the last launch.json change, and extract them one background sweep at a
    /// time. Directories are included here (unlike the Python/PowerShell helper, which only
    /// handled files) because IShellItemImageFactory works on folders too.
    /// </summary>
    private void EnsureIconsInBackground(LaunchConfig cfg)
    {
        List<(string id, LaunchItem item)>? jobs = null;
        lock (_cacheLock)
        {
            if (_iconsSweepBusy) return;
            foreach (var (id, item) in cfg.Items)
            {
                if (File.Exists(IconFile(id)) || _iconsTried.Contains(id)) continue;
                if (!IsExtractable(item)) continue;
                (jobs ??= new()).Add((id, item));
                _iconsTried.Add(id);
            }
            if (jobs is null || jobs.Count == 0) return;
            _iconsSweepBusy = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var (id, item) in jobs)
                {
                    try { ExtractIconFor(id, item); }
                    catch (Exception ex) { _ctx.Log.Warn($"icon extract failed for {id}: {ex.Message}"); }
                }
            }
            finally
            {
                _iconsSweepBusy = false;
            }
        });
    }

    private static bool IsExtractable(LaunchItem item)
    {
        if (!string.IsNullOrEmpty(item.Aumid)) return true;
        var src = item.IconFrom ?? item.Open ?? item.Cmd;
        return !string.IsNullOrEmpty(src) && (File.Exists(src) || Directory.Exists(src));
    }

    /// <summary>Extracts (or confirms) the icon for one id, serialized per id via a semaphore.</summary>
    private bool ExtractIconFor(string id, LaunchItem item)
    {
        var gate = _iconLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var outFile = IconFile(id);
            if (File.Exists(outFile)) return true; // someone else already produced it
            var src = item.IconFrom ?? item.Open ?? item.Cmd;
            return IconExtractor.TryExtract(src, item.Aumid, outFile);
        }
        finally
        {
            gate.Release();
        }
    }

    // --------------------------------------------------------------------------- launch.json

    private LaunchConfig LoadConfig()
    {
        lock (_cacheLock)
        {
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(_ctx.Paths.LaunchFile); }
            catch { mtime = DateTime.MinValue; }

            if (mtime != _cacheMtime)
            {
                _cacheMtime = mtime;
                _cache = ParseConfig(_ctx.Config.LoadLaunch());
                _iconsTried.Clear(); // launch.json changed — give every item a fresh chance
            }
            return _cache;
        }
    }

    private static LaunchConfig ParseConfig(JsonNode? root)
    {
        var cfg = new LaunchConfig();
        if (root is not JsonObject obj) return cfg;

        if (obj["groups"] is JsonArray groups)
        {
            foreach (var g in groups)
            {
                if (g is not JsonObject go) continue;
                var group = new LaunchGroup
                {
                    Id = (string?)go["id"] ?? "",
                    Label = (string?)go["label"] ?? "",
                };
                if (go["items"] is JsonArray gitems)
                {
                    foreach (var it in gitems)
                        if (it is JsonValue v && v.TryGetValue(out string? s) && s is not null) group.Items.Add(s);
                }
                cfg.Groups.Add(group);
            }
        }

        if (obj["items"] is JsonObject itemsObj)
        {
            foreach (var kv in itemsObj)
            {
                if (kv.Value is not JsonObject io) continue;
                var item = new LaunchItem
                {
                    Label = (string?)io["label"] ?? kv.Key,
                    Open = (string?)io["open"],
                    Cmd = (string?)io["cmd"],
                    Cwd = (string?)io["cwd"],
                    Aumid = (string?)io["aumid"],
                    IconFrom = (string?)io["icon_from"],
                    Badge = (string?)io["badge"] ?? "",
                };
                if (io["args"] is JsonArray argsArr)
                {
                    foreach (var a in argsArr)
                        if (a is JsonValue v && v.TryGetValue(out string? s) && s is not null) item.Args.Add(s);
                }
                cfg.Items[kv.Key] = item;
            }
        }

        return cfg;
    }

    private sealed class LaunchConfig
    {
        public List<LaunchGroup> Groups { get; } = new();
        public Dictionary<string, LaunchItem> Items { get; } = new(StringComparer.Ordinal);
    }

    private sealed class LaunchGroup
    {
        public string Id = "";
        public string Label = "";
        public List<string> Items { get; } = new();
    }

    private sealed class LaunchItem
    {
        public string Label = "";
        public string? Open;
        public string? Cmd;
        public List<string> Args { get; } = new();
        public string? Cwd;
        public string? Aumid;
        public string? IconFrom;
        public string Badge = "";
    }
}
