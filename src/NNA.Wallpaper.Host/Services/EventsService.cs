using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /events — events.json contents, always with both "events" and "daily" keys plus mtime
/// (calendar data for the "events" widget; unrelated to the live-update channel below).
///
/// WebSocket /events — the live-update channel: on connect the client gets
/// <c>{"type":"hello","version":...}</c>; every <see cref="Config.ConfigStore.Changed"/> is
/// debounced 50 ms per "what" key (so a burst of edits to the same thing collapses to one
/// message) and broadcast as <c>{"type":"config-changed","what":"app|monitors|widget:&lt;id&gt;|launch|events"}</c>.
/// POST /layout/preview broadcasts a transient <c>{"type":"layout-preview",...}</c> without
/// touching disk (used while dragging blocks in the settings layout editor). GET /events/stats
/// reports {clients, sent}. In test builds POST /events/emit broadcasts an arbitrary JSON body.
/// </summary>
public sealed class EventsService : IHostService, IDisposable
{
    private const int DebounceMs = 50;

    private readonly HostContext _ctx;

    private readonly object _clientsLock = new();
    private readonly HashSet<WebSocket> _clients = new();
    private readonly Dictionary<string, int> _pendingGen = new();
    private readonly object _pendingLock = new();
    private long _sent;

    public EventsService(HostContext ctx)
    {
        _ctx = ctx;
        _ctx.Config.Changed += OnConfigChanged;
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/events", HandleEventsFile);
        api.MapWebSocket("/events", HandleEventsWs);
        api.Map("GET", "/events/stats", HandleStats);
        api.Map("POST", "/layout/preview", HandleLayoutPreview);
        if (_ctx.TestEndpoints)
        {
            api.Map("POST", "/events/emit", HandleEmit);
        }
    }

    public void Dispose()
    {
        _ctx.Config.Changed -= OnConfigChanged;
        lock (_clientsLock)
        {
            foreach (var ws in _clients)
            {
                try { ws.Abort(); } catch { }
            }
            _clients.Clear();
        }
    }

    // ------------------------------------------------------------------ /events (calendar file)

    private Task HandleEventsFile(ApiRequest req)
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

    // ------------------------------------------------------------------ live-update channel

    private async Task HandleEventsWs(WebSocket socket, ApiRequest req, CancellationToken ct)
    {
        AddClient(socket);
        try
        {
            var hello = Json.Serialize(new { type = "hello", version = HostInfo.Version });
            await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            var buffer = new byte[256];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch
        {
            // client disconnects are not errors
        }
        finally
        {
            RemoveClient(socket);
        }
    }

    private void AddClient(WebSocket socket)
    {
        lock (_clientsLock) _clients.Add(socket);
        _ctx.Log.Info("events client connected");
    }

    private void RemoveClient(WebSocket socket)
    {
        lock (_clientsLock) _clients.Remove(socket);
    }

    /// <summary>Debounce 50 ms per "what" key: only the last change in a burst is broadcast, so a
    /// rapid sequence of saves to the same thing (e.g. dragging a slider) collapses to one message.</summary>
    private void OnConfigChanged(string what)
    {
        int gen;
        lock (_pendingLock)
        {
            _pendingGen.TryGetValue(what, out var cur);
            gen = cur + 1;
            _pendingGen[what] = gen;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DebounceMs).ConfigureAwait(false); }
            catch { return; }
            lock (_pendingLock)
            {
                if (!_pendingGen.TryGetValue(what, out var cur) || cur != gen) return; // superseded
            }
            await BroadcastAsync(new { type = "config-changed", what }).ConfigureAwait(false);
        });
    }

    private async Task HandleLayoutPreview(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject;
        if (node is null)
        {
            await req.Error(400, "bad json").ConfigureAwait(false);
            return;
        }
        var monitorId = node["monitorId"]?.GetValue<string>() ?? node["monitorId"]?.ToString();
        var blocks = node["blocks"] as JsonArray ?? new JsonArray();
        await BroadcastAsync(new JsonObject
        {
            ["type"] = "layout-preview",
            ["monitorId"] = monitorId,
            ["blocks"] = blocks.DeepClone(),
        }).ConfigureAwait(false);
        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandleEmit(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject ?? new JsonObject();
        await BroadcastAsync(node).ConfigureAwait(false);
        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcasts an application event from another service (e.g. AudioControlService,
    /// SystemInfoService) to every connected /events client. Fire-and-forget: callers are
    /// expected to throttle their own fast-changing sources (a volume slider drag, a meter poll)
    /// before calling this — this method does not debounce.
    /// </summary>
    public void Broadcast(object payload) => _ = BroadcastAsync(payload);

    private Task HandleStats(ApiRequest req)
    {
        int clients;
        lock (_clientsLock) clients = _clients.Count;
        return req.Json(new { clients, sent = Interlocked.Read(ref _sent) });
    }

    /// <summary>
    /// Broadcasts one JSON message to every connected client. <c>sent</c> counts broadcasts
    /// generated by the service (config changes, previews, test emits) whether or not any client
    /// is currently attached — /events/stats is meant to prove the service is producing live
    /// updates instead of the page reloading, not to measure delivery to a particular viewer.
    /// </summary>
    private async Task BroadcastAsync(object payload)
    {
        Interlocked.Increment(ref _sent);

        List<WebSocket> snapshot;
        lock (_clientsLock)
        {
            if (_clients.Count == 0) return;
            snapshot = new List<WebSocket>(_clients);
        }

        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(payload));
        foreach (var ws in snapshot)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                RemoveClient(ws);
            }
        }
    }
}
