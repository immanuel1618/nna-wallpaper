using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host;

/// <summary>
/// Composition root of the host: creates every service and registers its routes on the local API.
/// Services live in their own files and expose <c>Register(LocalApi)</c>; long-running ones are disposable.
/// </summary>
public sealed class HostServices : IDisposable
{
    private readonly HostContext _ctx;
    private readonly List<IDisposable> _disposables = new();

    public LocalApi Api { get; }

    public HostServices(HostContext ctx)
    {
        _ctx = ctx;
        Api = new LocalApi(ctx);

        // Core endpoints owned by the host itself.
        Api.Map("GET", "/health", Health);
        Api.Map("GET", "/config", GetConfig);
        if (ctx.TestEndpoints)
        {
            Api.Map("POST", "/test/event", TestEvent);
            Api.Map("GET", "/test/events", TestEvents);
            Api.Map("GET", "/test/log", TestLog);
        }
        Api.Map("POST", "/app/exit", req => { _ctx.App.RequestExit(); return req.Json(new { ok = true }); });
        Api.Map("POST", "/app/reload", req => { _ctx.Config.Load(); _ctx.App.ReloadWallpaper(); return req.Json(new { ok = true }); });

        // Static web roots shipped with the app; user widgets folder is searched after built-ins.
        Api.MapStatic("/wallpaper/", ctx.Paths.WebRoot("wallpaper"));
        Api.MapStatic("/settings/", ctx.Paths.WebRoot("settings"));
        Api.MapStatic("/widgets/", ctx.Paths.BuiltInWidgetsDir, ctx.Paths.UserWidgetsDir);

        // Feature services (each in its own file). Order matters only for /health contributors.
        RegisterServices();
    }

    // Filled in as stages land: Stats, Launch, Graph, Weather, Events, Pins, Open, Widgets (2);
    // Audio, Media (3); Planner (7); ConfigApi (8).
    private void RegisterServices()
    {
        Add(new Services.StatsService(_ctx));
        Add(new Services.LaunchService(_ctx));
        Add(new Services.GraphService(_ctx));
        Add(new Services.WeatherService(_ctx));
        Add(new Services.EventsService(_ctx));
        Add(new Services.PinsService(_ctx));
        Add(new Services.OpenService(_ctx));
        Add(new Services.WidgetsService(_ctx));
        Add(new Services.MediaService(_ctx));
        Add(new Services.AudioService(_ctx));
        Add(new Services.ConfigApiService(_ctx));
        Add(new Planner.PlannerService(_ctx));
    }

    private void Add(object service)
    {
        if (service is Services.IHostService s) s.Register(Api);
        if (service is IDisposable d) _disposables.Add(d);
    }

    public void Start() => Api.Start();

    public void Dispose()
    {
        Api.Dispose();
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
    }

    private Task Health(ApiRequest req)
    {
        var extra = new JsonObject();
        foreach (var kv in _ctx.Health)
        {
            try { extra[kv.Key] = JsonValue.Create(kv.Value()); } catch { extra[kv.Key] = null; }
        }
        var obj = new JsonObject
        {
            ["ok"] = true,
            ["app"] = HostInfo.AppName,
            ["version"] = HostInfo.Version,
            ["app_version"] = HostInfo.Version,
            ["uptime_s"] = (int)Math.Round((DateTime.UtcNow - _ctx.Started).TotalSeconds),
            ["installed"] = _ctx.App.Installed,
            ["port"] = _ctx.Port,
            ["media"] = extra.ContainsKey("media") ? extra["media"]?.DeepClone() : false,
            ["media_error"] = extra.ContainsKey("media_error") ? extra["media_error"]?.DeepClone() : null,
            ["gpu"] = extra.ContainsKey("gpu") ? extra["gpu"]?.DeepClone() : false,
            ["audio"] = extra.ContainsKey("audio") ? extra["audio"]?.DeepClone() : false,
            ["monitors"] = new JsonArray(_ctx.App.Monitors.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.id, ["name"] = m.name, ["width"] = m.width, ["height"] = m.height,
                ["x"] = m.x, ["y"] = m.y, ["visible"] = m.visible, ["paused"] = m.paused, ["scale"] = m.scale,
            }).ToArray()),
        };
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    /// <summary>
    /// GET /config?monitor=&lt;id|main|vertical&gt; — what the wallpaper page needs: theme, dim, api token,
    /// the monitor's layout and the settings of every widget placed on it.
    /// </summary>
    private Task GetConfig(ApiRequest req)
    {
        var cfg = _ctx.Config;
        var app = cfg.App;
        var monitorId = req.Query("monitor");
        var layout = Services.LayoutResolver.Resolve(cfg, _ctx.App.Monitors, monitorId);

        var widgets = new JsonObject();
        foreach (var b in layout.Blocks)
        {
            if (widgets.ContainsKey(b.Widget)) continue;
            widgets[b.Widget] = cfg.LoadWidgetSettings(b.Widget);
        }

        var obj = new JsonObject
        {
            ["ok"] = true,
            ["version"] = HostInfo.Version,
            ["port"] = _ctx.Port,
            ["token"] = app.ApiToken,
            ["language"] = app.Language,
            ["fpsCap"] = app.FpsCap,
            ["theme"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(app.Theme, Json.Config)),
            ["monitor"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(layout, Json.Config)),
            ["widgets"] = widgets,
            ["planner"] = new JsonObject { ["show"] = new JsonArray(app.Planner.Show.Select(s => (JsonNode)s).ToArray()) },
        };
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    private async Task TestEvent(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject ?? new JsonObject();
        node["at"] = DateTime.UtcNow.ToString("o");
        node["monitor"] ??= req.Query("monitor");
        _ctx.TestEvents.Enqueue(node);
        while (_ctx.TestEvents.Count > 100 && _ctx.TestEvents.TryDequeue(out _)) { }
        _ctx.Log.Info("test event " + node.ToJsonString(Json.Api));
        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    private Task TestEvents(ApiRequest req) =>
        req.Text(new JsonArray(_ctx.TestEvents.Select(e => ((JsonNode)e).DeepClone()).ToArray()).ToJsonString(Json.Api), "application/json; charset=utf-8");

    private Task TestLog(ApiRequest req) =>
        req.Json(new { lines = _ctx.Log.Tail() });
}
