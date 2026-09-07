using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NNA.Wallpaper.Host.Config;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Backs the settings window: one endpoint that hands the page everything it needs
/// (<c>GET /config/full</c>), one that applies edits (<c>PUT /config</c>), one that resolves the
/// built-in default layout for a monitor (<c>GET /config/defaults</c>) and a small helper to open
/// Explorer on the data/log folders (<c>POST /config/open-folder</c>).
/// </summary>
public sealed class ConfigApiService : IHostService
{
    private const int MaxMonitorsBackups = 5;

    private readonly HostContext _ctx;

    public ConfigApiService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/config/full", GetFull);
        api.Map("GET", "/config/defaults", GetDefaults);
        api.Map("PUT", "/config", PutConfig);
        api.Map("POST", "/config/open-folder", OpenFolder);
    }

    /// <summary>Everything the settings page renders on load.</summary>
    private Task GetFull(ApiRequest req)
    {
        var cfg = _ctx.Config;

        var appJson = JsonNode.Parse(JsonSerializer.Serialize(cfg.App, Json.Config))!.AsObject();
        appJson.Remove("apiToken");

        var monitorsJson = JsonNode.Parse(JsonSerializer.Serialize(cfg.Monitors, Json.Config));

        var liveMonitors = new JsonArray(_ctx.App.Monitors.Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m.id,
            ["name"] = m.name,
            ["width"] = m.width,
            ["height"] = m.height,
            ["x"] = m.x,
            ["y"] = m.y,
            ["visible"] = m.visible,
            ["paused"] = m.paused,
            ["scale"] = m.scale,
        }).ToArray());

        var obj = new JsonObject
        {
            ["ok"] = true,
            ["app"] = appJson,
            ["monitors"] = monitorsJson,
            ["widgetSettings"] = CollectWidgetSettings(),
            ["liveMonitors"] = liveMonitors,
            ["launch"] = cfg.LoadLaunch() ?? new JsonObject(),
            ["events"] = cfg.LoadEvents() ?? new JsonObject(),
        };
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    /// <summary>The built-in default layout for a monitor, picked by the live monitor's orientation.</summary>
    private Task GetDefaults(ApiRequest req)
    {
        var monitorId = req.Query("monitor");
        if (string.IsNullOrWhiteSpace(monitorId)) monitorId = "main";

        var status = _ctx.App.Monitors.FirstOrDefault(m => string.Equals(m.id, monitorId, StringComparison.OrdinalIgnoreCase));
        var layout = status is not null
            ? (status.height > status.width ? LayoutResolver.DefaultVertical(status.id) : LayoutResolver.DefaultMain(status.id))
            : LayoutResolver.DefaultMain(monitorId);

        var json = JsonNode.Parse(JsonSerializer.Serialize(layout, Json.Config));
        var obj = new JsonObject { ["ok"] = true, ["monitor"] = json };
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    /// <summary>
    /// Applies any of app (partial merge), monitors (full replace, validated), widgetSettings,
    /// launch, events. Validates everything first: on any error nothing is written.
    /// </summary>
    private async Task PutConfig(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        JsonObject root;
        try
        {
            root = (Json.ParseNode(body) as JsonObject) ?? throw new InvalidOperationException("expected a JSON object");
        }
        catch (Exception ex)
        {
            await req.Json(new { error = "bad json", detail = new[] { ex.Message } }, 400).ConfigureAwait(false);
            return;
        }

        var errors = new List<string>();

        AppSettings? mergedApp = null;
        if (root.TryGetPropertyValue("app", out var appNode) && appNode is JsonObject appPatch)
        {
            try
            {
                var current = JsonNode.Parse(JsonSerializer.Serialize(_ctx.Config.App, Json.Config))!.AsObject();
                foreach (var kv in appPatch)
                {
                    if (string.Equals(kv.Key, "apiToken", StringComparison.OrdinalIgnoreCase)) continue;
                    current[kv.Key] = kv.Value?.DeepClone();
                }
                mergedApp = current.Deserialize<AppSettings>(Json.Config);
                if (mergedApp is null) errors.Add("app: could not parse");
            }
            catch (Exception ex)
            {
                errors.Add("app: " + ex.Message);
            }
        }

        MonitorsConfig? monitors = null;
        if (root.TryGetPropertyValue("monitors", out var monitorsNode) && monitorsNode is not null)
        {
            try
            {
                monitors = monitorsNode.Deserialize<MonitorsConfig>(Json.Config);
                if (monitors is null) errors.Add("monitors: could not parse");
                else errors.AddRange(ValidateMonitors(monitors));
            }
            catch (Exception ex)
            {
                errors.Add("monitors: " + ex.Message);
            }
        }

        var widgetSettings = new List<(string Id, JsonObject Obj)>();
        if (root.TryGetPropertyValue("widgetSettings", out var wsNode) && wsNode is JsonObject wsObj)
        {
            foreach (var kv in wsObj)
            {
                try
                {
                    var id = ConfigStore.SafeId(kv.Key);
                    var obj = kv.Value as JsonObject ?? new JsonObject();
                    widgetSettings.Add((id, obj));
                }
                catch (Exception ex)
                {
                    errors.Add("widgetSettings." + kv.Key + ": " + ex.Message);
                }
            }
        }

        root.TryGetPropertyValue("launch", out var launchNode);
        root.TryGetPropertyValue("events", out var eventsNode);

        if (errors.Count > 0)
        {
            await req.Json(new { error = "validation failed", detail = errors }, 400).ConfigureAwait(false);
            return;
        }

        // Nothing was written above: everything from here on is a real write.
        if (monitors is not null)
        {
            BackupMonitorsFile();
            _ctx.Config.ReplaceMonitors(monitors);
            _ctx.Log.Info("layout reloaded");
        }
        if (mergedApp is not null)
        {
            _ctx.Config.ReplaceApp(mergedApp);
        }
        foreach (var (id, obj) in widgetSettings)
        {
            _ctx.Config.SaveWidgetSettings(id, obj);
        }
        if (launchNode is not null)
        {
            _ctx.Config.SaveLaunch(launchNode);
        }
        if (eventsNode is not null)
        {
            _ctx.Config.SaveEvents(eventsNode);
        }

        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    private Task OpenFolder(ApiRequest req)
    {
        var what = req.Query("what");
        var path = string.Equals(what, "logs", StringComparison.OrdinalIgnoreCase) ? _ctx.Paths.LogsDir : _ctx.Paths.DataDir;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            return req.Error(500, "open-folder failed: " + ex.Message);
        }
        return req.Json(new { ok = true });
    }

    private JsonObject CollectWidgetSettings()
    {
        var result = new JsonObject();
        foreach (var dir in new[] { _ctx.Paths.BuiltInWidgetsDir, _ctx.Paths.UserWidgetsDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var manifestFile = Path.Combine(sub, "widget.json");
                if (!File.Exists(manifestFile)) continue;
                string? id;
                try
                {
                    var manifest = JsonNode.Parse(File.ReadAllText(manifestFile)) as JsonObject;
                    id = manifest?["id"]?.GetValue<string>();
                }
                catch (Exception ex)
                {
                    _ctx.Log.Warn("bad widget manifest " + manifestFile + ": " + ex.Message);
                    continue;
                }
                id ??= Path.GetFileName(sub);
                if (string.IsNullOrWhiteSpace(id) || result.ContainsKey(id)) continue;
                try
                {
                    result[id] = _ctx.Config.LoadWidgetSettings(id);
                }
                catch (Exception ex)
                {
                    _ctx.Log.Warn("widget settings load failed for " + id + ": " + ex.Message);
                }
            }
        }
        return result;
    }

    private void BackupMonitorsFile()
    {
        var file = _ctx.Paths.MonitorsConfigFile;
        if (!File.Exists(file)) return;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = file + ".bak-" + stamp;
        try
        {
            File.Copy(file, backup, overwrite: true);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("monitors backup failed: " + ex.Message);
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(file)!;
            var name = Path.GetFileName(file);
            var backups = Directory.EnumerateFiles(dir, name + ".bak-*")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(MaxMonitorsBackups);
            foreach (var old in backups)
            {
                try { File.Delete(old); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("monitors backup prune failed: " + ex.Message);
        }
    }

    /// <summary>Grid bounds and non-overlap for every block on every monitor.</summary>
    private static List<string> ValidateMonitors(MonitorsConfig config)
    {
        var errors = new List<string>();
        foreach (var m in config.Monitors ?? new List<MonitorLayout>())
        {
            var label = string.IsNullOrEmpty(m.Id) ? "(no id)" : m.Id;
            var grid = m.Grid ?? new GridSpec();
            var blocks = m.Blocks ?? new List<BlockSpec>();

            if (grid.Cols < 1) errors.Add($"monitor {label}: grid.cols must be >= 1");
            if (grid.Rows < 1) errors.Add($"monitor {label}: grid.rows must be >= 1");

            for (var i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (string.IsNullOrWhiteSpace(b.Widget)) errors.Add($"monitor {label} block {i}: widget is required");
                if (b.Col < 1) errors.Add($"monitor {label} block {i}: col must be >= 1");
                if (b.Row < 1) errors.Add($"monitor {label} block {i}: row must be >= 1");
                if (b.ColSpan < 1) errors.Add($"monitor {label} block {i}: colSpan must be >= 1");
                if (b.RowSpan < 1) errors.Add($"monitor {label} block {i}: rowSpan must be >= 1");
                if (grid.Cols >= 1 && b.Col + b.ColSpan - 1 > grid.Cols)
                    errors.Add($"monitor {label} block {i}: col+colSpan exceeds grid.cols ({grid.Cols})");
                if (grid.Rows >= 1 && b.Row + b.RowSpan - 1 > grid.Rows)
                    errors.Add($"monitor {label} block {i}: row+rowSpan exceeds grid.rows ({grid.Rows})");
            }

            for (var i = 0; i < blocks.Count; i++)
            {
                for (var j = i + 1; j < blocks.Count; j++)
                {
                    if (Overlaps(blocks[i], blocks[j]))
                        errors.Add($"monitor {label}: blocks {i} and {j} overlap");
                }
            }
        }
        return errors;
    }

    private static bool Overlaps(BlockSpec a, BlockSpec b)
    {
        var aColEnd = a.Col + a.ColSpan - 1;
        var bColEnd = b.Col + b.ColSpan - 1;
        var aRowEnd = a.Row + a.RowSpan - 1;
        var bRowEnd = b.Row + b.RowSpan - 1;
        return a.Col <= bColEnd && b.Col <= aColEnd && a.Row <= bRowEnd && b.Row <= aRowEnd;
    }
}
