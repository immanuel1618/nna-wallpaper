using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>GET /events — events.json contents, always with both "events" and "daily" keys plus mtime.</summary>
public sealed class EventsService : IHostService
{
    private readonly HostContext _ctx;

    public EventsService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api) => api.Map("GET", "/events", Handle);

    private Task Handle(ApiRequest req)
    {
        var node = _ctx.Config.LoadEvents() as JsonObject ?? new JsonObject();
        if (node["events"] is not JsonArray) node["events"] = new JsonArray();
        if (node["daily"] is not JsonArray) node["daily"] = new JsonArray();

        double mtime = 0;
        try
        {
            if (File.Exists(_ctx.Paths.EventsFile))
                mtime = (File.GetLastWriteTimeUtc(_ctx.Paths.EventsFile) - DateTime.UnixEpoch).TotalSeconds;
        }
        catch { /* mtime stays 0 when it cannot be read */ }
        node["mtime"] = mtime;

        return req.Text(node.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }
}
