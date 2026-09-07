using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Config;

/// <summary>
/// Loads and saves app.json, monitors.json, config/widgets/&lt;id&gt;.json, launch.json, events.json.
/// All writes are atomic. <see cref="Changed"/> fires with the logical name of what changed
/// ("app", "monitors", "widget:eq", "launch", "events") so the wallpaper pages can reload.
/// </summary>
public sealed class ConfigStore
{
    private readonly Paths _paths;
    private readonly Log _log;
    private readonly object _lock = new();

    public AppSettings App { get; private set; } = new();
    public MonitorsConfig Monitors { get; private set; } = new();

    public event Action<string>? Changed;

    public ConfigStore(Paths paths, Log log)
    {
        _paths = paths;
        _log = log;
    }

    public void Load()
    {
        lock (_lock)
        {
            _paths.EnsureDirectories();
            var app = ReadOrDefault<AppSettings>(_paths.AppConfigFile);
            var dirty = app is null;
            app ??= new AppSettings();
            if (string.IsNullOrEmpty(app.ApiToken))
            {
                app.ApiToken = NewToken();
                dirty = true;
            }
            // v3 token migration: old configs (or fresh ones read before this pass) may still carry
            // the pre-tokens font names. The wallpaper page no longer applies theme.fonts at all
            // (ui/tokens.css fixes the brand font now), but app.json should not keep serving stale
            // names to anything that still reads it (the settings editor, /config/full). The palette
            // is left untouched: it is unused either way, so migrating it would just be churn.
            if (app.Theme.Fonts.TryGetValue("display", out var display) && display == "Kharkiv Tone")
            {
                app.Theme.Fonts["display"] = "Roboto Flex";
                dirty = true;
            }
            if (app.Theme.Fonts.TryGetValue("mono", out var mono) && mono == "DM Mono")
            {
                app.Theme.Fonts["mono"] = "JetBrains Mono";
                dirty = true;
            }
            App = app;
            if (dirty) SaveAppUnlocked();

            var mon = ReadOrDefault<MonitorsConfig>(_paths.MonitorsConfigFile);
            if (mon is null)
            {
                mon = new MonitorsConfig();
                Monitors = mon;
                SaveMonitorsUnlocked();
            }
            Monitors = mon;
        }
    }

    public void SaveApp()
    {
        lock (_lock) SaveAppUnlocked();
        Changed?.Invoke("app");
    }

    public void SaveMonitors()
    {
        lock (_lock) SaveMonitorsUnlocked();
        Changed?.Invoke("monitors");
    }

    /// <summary>Replace app settings (from PUT /config) and persist.</summary>
    public void ReplaceApp(AppSettings next)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(next.ApiToken)) next.ApiToken = App.ApiToken;
            App = next;
            SaveAppUnlocked();
        }
        Changed?.Invoke("app");
    }

    public void ReplaceMonitors(MonitorsConfig next)
    {
        lock (_lock)
        {
            Monitors = next;
            SaveMonitorsUnlocked();
        }
        Changed?.Invoke("monitors");
    }

    public JsonObject LoadWidgetSettings(string widgetId)
    {
        var node = Json.LoadFile(Path.Combine(_paths.WidgetSettingsDir, SafeId(widgetId) + ".json"));
        return node as JsonObject ?? new JsonObject();
    }

    public void SaveWidgetSettings(string widgetId, JsonObject settings)
    {
        var file = Path.Combine(_paths.WidgetSettingsDir, SafeId(widgetId) + ".json");
        lock (_lock) Json.WriteFileAtomic(file, settings.ToJsonString(Json.Config));
        Changed?.Invoke("widget:" + widgetId);
    }

    public JsonNode? LoadLaunch() => Json.LoadFile(_paths.LaunchFile);
    public JsonNode? LoadEvents() => Json.LoadFile(_paths.EventsFile);

    public void SaveLaunch(JsonNode node)
    {
        lock (_lock) Json.WriteFileAtomic(_paths.LaunchFile, node.ToJsonString(Json.Config));
        Changed?.Invoke("launch");
    }

    public void SaveEvents(JsonNode node)
    {
        lock (_lock) Json.WriteFileAtomic(_paths.EventsFile, node.ToJsonString(Json.Config));
        Changed?.Invoke("events");
    }

    public void NotifyChanged(string what) => Changed?.Invoke(what);

    public static string SafeId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("bad widget id");
        return id;
    }

    private T? ReadOrDefault<T>(string file) where T : class
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json.Config);
        }
        catch (Exception ex)
        {
            _log.Error("config read failed: " + Path.GetFileName(file), ex);
            return null;
        }
    }

    private void SaveAppUnlocked() =>
        Json.WriteFileAtomic(_paths.AppConfigFile, JsonSerializer.Serialize(App, Json.Config));

    private void SaveMonitorsUnlocked() =>
        Json.WriteFileAtomic(_paths.MonitorsConfigFile, JsonSerializer.Serialize(Monitors, Json.Config));

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
