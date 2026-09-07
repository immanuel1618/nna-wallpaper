using System.Text;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Planner;

/// <summary>
/// The Planner feature: /planner/* endpoints backing the TASKS widget. Owns the signed-in session
/// (loaded from disk at startup, refreshed on a one-minute timer and on-demand on 401), a 30-second
/// cache of /planner/today, and the proxy to the capture edge function used by both the block's
/// text field and the InputWindow / voice recorder.
/// </summary>
public sealed class PlannerService : Services.IHostService, IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private const long MaxCaptureBytes = 12 * 1024 * 1024;

    private readonly HostContext _ctx;
    private readonly PlannerClient _client;
    private readonly SemaphoreSlim _todayLock = new(1, 1);
    private readonly Timer _refreshTimer;

    private PlannerSession? _session;
    private JsonObject? _todayCache;
    private DateTimeOffset _todayCacheAt = DateTimeOffset.MinValue;
    private JsonObject? _lastQuota;

    public PlannerService(HostContext ctx)
    {
        _ctx = ctx;
        _client = new PlannerClient(ctx);
        _session = PlannerSession.Load(ctx);
        _refreshTimer = new Timer(_ => _ = RefreshTickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/planner/status", Status);
        api.Map("GET", "/planner/today", Today);
        api.Map("POST", "/planner/done", Done);
        api.Map("POST", "/planner/habit", Habit);
        api.Map("POST", "/planner/capture", Capture);
        api.Map("GET", "/planner/login", Login);
        api.Map("POST", "/planner/logout", Logout);
        api.Map("GET", "/planner/callback", Callback);
        api.Map("POST", "/planner/input", Input);
        api.Map("POST", "/planner/test-delete", TestDelete);
    }

    // ---- background refresh --------------------------------------------------------------------------

    private async Task RefreshTickAsync()
    {
        var session = _session;
        if (session is null || !session.NeedsRefresh) return;
        try
        {
            var ok = await _client.RefreshAsync(session).ConfigureAwait(false);
            if (!ok) _ctx.Log.Warn("planner: background token refresh failed, sign-in will be required");
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner refresh timer", ex);
        }
    }

    // ---- routes ---------------------------------------------------------------------------------------

    private Task Status(ApiRequest req)
    {
        var s = _session;
        if (s is null) return req.Json(new { loggedIn = false, profile = (object?)null });

        var obj = new JsonObject
        {
            ["loggedIn"] = true,
            ["profile"] = new JsonObject
            {
                ["display_name"] = s.Profile.DisplayName,
                ["timezone"] = s.Profile.Timezone,
                ["tier"] = s.Profile.Tier,
            },
            ["expires_at"] = s.ExpiresAt.ToUnixTimeSeconds(),
        };
        if (_lastQuota is not null) obj["quota"] = _lastQuota.DeepClone();
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    private async Task Today(ApiRequest req)
    {
        var session = _session;
        if (session is null) { await req.Error(401, "not logged in").ConfigureAwait(false); return; }

        JsonObject? result = null;
        string? errorMsg = null;
        var errorCode = 500;

        await _todayLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_todayCache is not null && DateTimeOffset.UtcNow - _todayCacheAt <= CacheTtl)
            {
                result = _todayCache;
            }
            else
            {
                result = await BuildTodayAsync(session).ConfigureAwait(false);
                _todayCache = result;
                _todayCacheAt = DateTimeOffset.UtcNow;
            }
        }
        catch (PlannerAuthException)
        {
            errorMsg = "sign in again";
            errorCode = 401;
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner today", ex);
            errorMsg = "planner unavailable";
            errorCode = 502;
        }
        finally
        {
            _todayLock.Release();
        }

        if (errorMsg is not null) { await req.Error(errorCode, errorMsg).ConfigureAwait(false); return; }
        await req.Text(result!.ToJsonString(Json.Api), "application/json; charset=utf-8").ConfigureAwait(false);
    }

    private async Task<JsonObject> BuildTodayAsync(PlannerSession session)
    {
        var data = await _client.GetTodayAsync(session).ConfigureAwait(false);
        var zone = ResolveZoneSafe(session.Profile.Timezone);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var habitsChecked = data.Habits.Count(h => h.CheckedToday);
        var briefingText = Briefing.Build(session.Profile, data.Tasks, data.Meetings, data.SpentMinor, habitsChecked, data.Habits.Count, nowLocal);

        var tasksJson = new JsonArray(data.Tasks.Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id,
            ["title"] = t.Title,
            ["due_at"] = t.DueAt?.ToString("o"),
            ["overdue"] = t.Overdue,
            ["state"] = t.State,
        }).ToArray());

        var meetingsJson = new JsonArray(data.Meetings.Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m.Id,
            ["title"] = m.Title,
            ["starts_at"] = m.StartsAt.ToString("o"),
            ["duration_min"] = m.DurationMin,
        }).ToArray());

        var habitsJson = new JsonArray(data.Habits.Select(h => (JsonNode)new JsonObject
        {
            ["id"] = h.Id,
            ["title"] = h.Title,
            ["checkedToday"] = h.CheckedToday,
        }).ToArray());

        var byCategoryJson = new JsonObject();
        foreach (var kv in data.ByCategory) byCategoryJson[kv.Key] = kv.Value;

        var obj = new JsonObject
        {
            ["tasks"] = tasksJson,
            ["meetings"] = meetingsJson,
            ["habits"] = habitsJson,
            ["money"] = new JsonObject { ["spent_minor"] = data.SpentMinor, ["income_minor"] = data.IncomeMinor, ["by_category"] = byCategoryJson },
            ["briefing"] = briefingText,
            ["generated_at"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        if (_lastQuota is not null) obj["quota"] = _lastQuota.DeepClone();
        return obj;
    }

    private async Task Done(ApiRequest req)
    {
        var session = _session;
        if (session is null) { await req.Error(401, "not logged in").ConfigureAwait(false); return; }
        var id = req.Query("id");
        if (string.IsNullOrEmpty(id)) { await req.Error(400, "id required").ConfigureAwait(false); return; }
        var done = req.Query("done") == "1";

        try
        {
            await _client.SetTaskStateAsync(session, id, done).ConfigureAwait(false);
        }
        catch (PlannerAuthException) { await req.Error(401, "sign in again").ConfigureAwait(false); return; }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner done", ex);
            await req.Error(502, "planner unavailable").ConfigureAwait(false);
            return;
        }

        InvalidateCache();
        NotifyChanged();
        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    private async Task Habit(ApiRequest req)
    {
        var session = _session;
        if (session is null) { await req.Error(401, "not logged in").ConfigureAwait(false); return; }
        var id = req.Query("id");
        if (string.IsNullOrEmpty(id)) { await req.Error(400, "id required").ConfigureAwait(false); return; }
        var @checked = req.Query("checked") == "1";
        var zone = ResolveZoneSafe(session.Profile.Timezone);
        var todayKey = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString("yyyy-MM-dd");

        try
        {
            await _client.CheckHabitAsync(session, id, @checked, todayKey).ConfigureAwait(false);
        }
        catch (PlannerAuthException) { await req.Error(401, "sign in again").ConfigureAwait(false); return; }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner habit", ex);
            await req.Error(502, "planner unavailable").ConfigureAwait(false);
            return;
        }

        InvalidateCache();
        NotifyChanged();
        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    private async Task Capture(ApiRequest req)
    {
        var session = _session;
        if (session is null) { await req.Error(401, "not logged in").ConfigureAwait(false); return; }

        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(body) > MaxCaptureBytes)
        {
            await req.Json(new { ok = false, error = "too large" }, 413).ConfigureAwait(false);
            return;
        }

        var node = Json.ParseNode(body) as JsonObject ?? new JsonObject();
        var text = (string?)node["text"];
        var audio = (string?)node["audio_base64"];
        var mime = (string?)node["mime"];
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(audio))
        {
            await req.Error(400, "empty").ConfigureAwait(false);
            return;
        }
        var source = !string.IsNullOrEmpty(audio) ? "voice" : "text";

        PlannerHttpResult result;
        try
        {
            result = await _client.CaptureAsync(session, text, audio, mime, source).ConfigureAwait(false);
        }
        catch (PlannerAuthException) { await req.Error(401, "sign in again").ConfigureAwait(false); return; }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner capture", ex);
            await req.Error(502, "planner unavailable").ConfigureAwait(false);
            return;
        }

        if (result.Body?["quota"] is JsonNode q) _lastQuota = q.DeepClone() as JsonObject;
        InvalidateCache();
        NotifyChanged();

        if (result.Status == 429)
        {
            await req.Json(new { ok = false, error = "limit" }, 429).ConfigureAwait(false);
            return;
        }

        var payload = result.Body ?? new JsonObject { ["ok"] = false, ["error"] = "capture failed" };
        var code = result.Status is >= 200 and < 600 ? result.Status : 502;
        await req.Text(payload.ToJsonString(Json.Api), "application/json; charset=utf-8", code).ConfigureAwait(false);
    }

    private Task Login(ApiRequest req)
    {
        _ctx.App.OpenPlannerLogin();
        return req.Json(new { ok = true });
    }

    private Task Logout(ApiRequest req)
    {
        _session = null;
        PlannerSession.Delete(_ctx);
        InvalidateCache();
        NotifyChanged();
        return req.Json(new { ok = true });
    }

    /// <summary>GET /planner/callback?&lt;widget fields&gt; — arrives from the browser page inside the login window, no X-Token.</summary>
    private async Task Callback(ApiRequest req)
    {
        var fields = new Dictionary<string, string>();
        foreach (var key in new[] { "id", "first_name", "last_name", "username", "photo_url", "auth_date", "hash" })
        {
            var v = req.Query(key);
            if (!string.IsNullOrEmpty(v)) fields[key] = v;
        }

        try
        {
            var session = await _client.LoginWithWidgetAsync(fields).ConfigureAwait(false);
            session.Save(_ctx);
            _session = session;
            InvalidateCache();
            _ctx.App.PostToPages("{\"type\":\"planner\",\"event\":\"login\"}");
            await req.Text(SuccessHtml(), "text/html; charset=utf-8", 200).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("planner callback failed: " + ex.Message);
            await req.Text(FailureHtml(), "text/html; charset=utf-8", 401).ConfigureAwait(false);
        }
    }

    private async Task Input(ApiRequest req)
    {
        var monitorId = req.Query("monitor");
        if (!int.TryParse(req.Query("x"), out var x) || !int.TryParse(req.Query("y"), out var y))
        {
            await req.Error(400, "bad coordinates").ConfigureAwait(false);
            return;
        }
        var placeholder = req.Query("placeholder");
        var monitor = FindMonitor(monitorId);
        if (monitor is null)
        {
            _ctx.Log.Warn("planner input: monitor '" + monitorId + "' not found among [" + string.Join(", ", _ctx.App.Monitors.Select(m => m.id)) + "]; using primary origin");
        }
        var screenX = (monitor?.x ?? 0) + x;
        var screenY = (monitor?.y ?? 0) + y;

        _ctx.App.RequestTextInput("planner", screenX, screenY, placeholder, text => OnInputSubmitted(text, monitorId));
        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    /// <summary>Monitor by id: exact, then with slashes normalised, then by the trailing "|WxH" size.</summary>
    private MonitorStatus? FindMonitor(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        static string Norm(string s) => s.Replace('/', '\\').Trim();
        var mons = _ctx.App.Monitors;
        return mons.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase))
            ?? mons.FirstOrDefault(m => string.Equals(Norm(m.id), Norm(id), StringComparison.OrdinalIgnoreCase))
            ?? (id.Contains('|') ? mons.FirstOrDefault(m => m.id.EndsWith(id[id.LastIndexOf('|')..], StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>Callback from InputWindow; runs off the API request thread, so errors are only logged/posted, never thrown.</summary>
    private async void OnInputSubmitted(string text, string? monitorId)
    {
        var session = _session;
        if (session is null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var result = await _client.CaptureAsync(session, text, null, null, "text").ConfigureAwait(false);
            if (result.Body?["quota"] is JsonNode q) _lastQuota = q.DeepClone() as JsonObject;
            InvalidateCache();

            var ok = (bool?)result.Body?["ok"] ?? false;
            var msg = new JsonObject { ["type"] = "planner", ["event"] = "captured", ["ok"] = ok };
            if (result.Body?["entries"] is JsonNode entries) msg["entries"] = entries.DeepClone();
            if (!ok) msg["error"] = (string?)result.Body?["message"] ?? (string?)result.Body?["reason"] ?? "error";
            _ctx.App.PostToPages(msg.ToJsonString(Json.Api), monitorId);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner input capture", ex);
            _ctx.App.PostToPages("{\"type\":\"planner\",\"event\":\"captured\",\"ok\":false,\"error\":\"error\"}", monitorId);
        }
    }

    /// <summary>
    /// Test-only cleanup. Two ways in, both scoped so this can never touch a real record:
    ///  - ?batch=&lt;batch_id&gt; — undoes the whole capture batch via the self-scoped rpc/undo_batch
    ///    (RLS + my_profile_id() mean it can only remove the caller's own entries). Preferred: robust
    ///    even when the free-text capture pipeline rewrites the title the caller sent.
    ///  - ?id=&lt;entry_id&gt; — deletes one entry, but only when its title starts with "NNA WALLPAPER TEST".
    /// </summary>
    private async Task TestDelete(ApiRequest req)
    {
        var session = _session;
        if (session is null) { await req.Error(401, "not logged in").ConfigureAwait(false); return; }
        var batchId = req.Query("batch");
        var id = req.Query("id");
        if (string.IsNullOrEmpty(batchId) && string.IsNullOrEmpty(id))
        {
            await req.Error(400, "batch or id required").ConfigureAwait(false);
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(batchId))
            {
                await _client.UndoBatchAsync(session, batchId).ConfigureAwait(false);
            }
            else
            {
                var title = await _client.GetEntryTitleAsync(session, id!).ConfigureAwait(false);
                if (title is null || !title.StartsWith("NNA WALLPAPER TEST", StringComparison.Ordinal))
                {
                    await req.Error(403, "not a test entry").ConfigureAwait(false);
                    return;
                }
                await _client.DeleteEntryAsync(session, id!).ConfigureAwait(false);
            }
        }
        catch (PlannerAuthException) { await req.Error(401, "sign in again").ConfigureAwait(false); return; }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner test-delete", ex);
            await req.Error(502, "planner unavailable").ConfigureAwait(false);
            return;
        }

        InvalidateCache();
        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private void InvalidateCache()
    {
        _todayCache = null;
        _todayCacheAt = DateTimeOffset.MinValue;
    }

    private void NotifyChanged() => _ctx.App.PostToPages("{\"type\":\"planner\",\"event\":\"changed\"}");

    private static TimeZoneInfo ResolveZoneSafe(string tz)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(tz) ? "Europe/Moscow" : tz); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"); }
            catch { return TimeZoneInfo.Utc; }
        }
    }

    private static string SuccessHtml() => WrapHtml(
        "Вход выполнен",
        "Вход выполнен, окно можно закрыть.",
        "<script>try{window.chrome.webview.postMessage('done')}catch(e){}setTimeout(function(){try{window.close()}catch(e){}},1500)</script>");

    private static string FailureHtml() => WrapHtml(
        "Вход не удался",
        "Не получилось войти. Закройте окно и попробуйте снова.",
        "");

    private static string WrapHtml(string title, string message, string script) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + title + "</title>"
        + "<style>html,body{margin:0;height:100%;background:#050505;color:#fff;font-family:system-ui,sans-serif;"
        + "display:flex;align-items:center;justify-content:center;text-align:center}"
        + "div{padding:32px;max-width:360px}h1{font-size:15px;letter-spacing:.08em;text-transform:uppercase;margin:0 0 12px}"
        + "p{font-size:13px;color:#c8c8c8;margin:0;line-height:1.5}</style></head>"
        + "<body><div><h1>" + title + "</h1><p>" + message + "</p></div>" + script + "</body></html>";

    public void Dispose()
    {
        _refreshTimer.Dispose();
        _client.Dispose();
        _todayLock.Dispose();
    }
}
