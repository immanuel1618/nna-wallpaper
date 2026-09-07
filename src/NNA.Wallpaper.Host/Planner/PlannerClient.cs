using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Planner;

/// <summary>Thrown when a request still gets 401 after one refresh attempt: the caller should report "sign in again".</summary>
public sealed class PlannerAuthException : Exception
{
    public PlannerAuthException(string message) : base(message) { }
}

/// <summary>
/// HTTP client to Supabase: auth-telegram-widget (login), GoTrue token refresh, PostgREST (entries,
/// rpc/summary_for) and the capture edge function. Every session-bound call retries once after a
/// token refresh on 401, then throws <see cref="PlannerAuthException"/>. Never logs token values.
/// </summary>
public sealed class PlannerClient : IDisposable
{
    private readonly HostContext _ctx;
    private readonly HttpClient _http;
    private readonly Config.PlannerSettings _cfg;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public PlannerClient(HostContext ctx)
    {
        _ctx = ctx;
        _cfg = ctx.Config.App.Planner;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }

    private string BaseUrl => _cfg.SupabaseUrl.TrimEnd('/');
    private string RestUrl(string pathAndQuery) => BaseUrl + "/rest/v1/" + pathAndQuery;

    // ---- login / refresh ---------------------------------------------------------------------------

    public async Task<PlannerSession> LoginWithWidgetAsync(IDictionary<string, string> fields)
    {
        var body = new JsonObject();
        foreach (var kv in fields) body[kv.Key] = kv.Value;

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/functions/v1/auth-telegram-widget");
        req.Headers.Add("apikey", _cfg.PublishableKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new PlannerAuthException("login failed: " + (int)res.StatusCode);
        var root = Json.ParseNode(text) ?? throw new PlannerAuthException("login: bad response");
        return PlannerSession.FromLoginResponse(root);
    }

    /// <summary>
    /// Downloads the Telegram-hosted avatar to <paramref name="destPath"/>. Refuses anything that is
    /// not an https://t.me/ URL (the only host photo_url is ever expected to point at) — never fetches
    /// an arbitrary URL a compromised/odd profile payload might contain. Best-effort: false on any
    /// failure, never throws.
    /// </summary>
    public async Task<bool> DownloadAvatarAsync(string photoUrl, string destPath)
    {
        if (string.IsNullOrWhiteSpace(photoUrl) || !photoUrl.StartsWith("https://t.me/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            using var res = await _http.GetAsync(photoUrl).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return false;
            var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = destPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, destPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("planner avatar download failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Refresh in place (mutates and saves <paramref name="session"/>). Serialized: concurrent 401s share one attempt.</summary>
    public async Task<bool> RefreshAsync(PlannerSession session)
    {
        if (string.IsNullOrEmpty(session.RefreshToken)) return false;
        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/auth/v1/token?grant_type=refresh_token");
            req.Headers.Add("apikey", _cfg.PublishableKey);
            req.Content = new StringContent(new JsonObject { ["refresh_token"] = session.RefreshToken }.ToJsonString(), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return false;
            var root = Json.ParseNode(text);
            if (root is null) return false;
            session.ApplyRefresh(root);
            session.Save(_ctx);
            return true;
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("planner refresh failed: " + ex.Message);
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // ---- generic session-bound send with one refresh-and-retry on 401 -----------------------------

    private async Task<HttpResponseMessage> SendAsync(PlannerSession session, Func<HttpRequestMessage> build)
    {
        var res = await SendOnceAsync(session, build).ConfigureAwait(false);
        if (res.StatusCode != HttpStatusCode.Unauthorized) return res;

        res.Dispose();
        if (!await RefreshAsync(session).ConfigureAwait(false)) throw new PlannerAuthException("sign in again");

        res = await SendOnceAsync(session, build).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            res.Dispose();
            throw new PlannerAuthException("sign in again");
        }
        return res;
    }

    private Task<HttpResponseMessage> SendOnceAsync(PlannerSession session, Func<HttpRequestMessage> build)
    {
        var req = build();
        req.Headers.Add("apikey", _cfg.PublishableKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return _http.SendAsync(req);
    }

    private static async Task<JsonArray> ReadArrayAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException("postgrest " + (int)res.StatusCode + ": " + text);
        return Json.ParseNode(text) as JsonArray ?? new JsonArray();
    }

    // ---- PostgREST reads -----------------------------------------------------------------------------

    private async Task<JsonArray> GetTasksRowsAsync(PlannerSession session)
    {
        using var res = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl("entries?kind=eq.task&state=eq.open&select=id,title,due_at,state,updated_at&order=due_at.asc"))).ConfigureAwait(false);
        return await ReadArrayAsync(res).ConfigureAwait(false);
    }

    private async Task<JsonArray> GetMeetingsRowsAsync(PlannerSession session)
    {
        var since = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-30).ToString("o"));
        using var res = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl($"entries?kind=eq.meeting&starts_at=gte.{since}&select=id,title,starts_at,duration_min&order=starts_at.asc&limit=5"))).ConfigureAwait(false);
        return await ReadArrayAsync(res).ConfigureAwait(false);
    }

    /// <summary>Public: also used directly by PlannerService's /planner/stats (habits are evergreen —
    /// not date-scoped — so stats reuses the same "open habits + meta.checks" rows as /planner/today).</summary>
    public async Task<JsonArray> GetHabitsRowsAsync(PlannerSession session)
    {
        using var res = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl("entries?kind=eq.habit&state=eq.open&select=id,title,meta"))).ConfigureAwait(false);
        return await ReadArrayAsync(res).ConfigureAwait(false);
    }

    /// <summary>Public: also used directly by PlannerService's /planner/stats with a wider (week) window.</summary>
    public async Task<JsonArray> GetMoneyRowsAsync(PlannerSession session, DateTimeOffset sinceUtc)
    {
        var since = Uri.EscapeDataString(sinceUtc.ToString("o"));
        using var res = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl($"entries?kind=eq.money&happened_at=gte.{since}&select=amount_minor,direction,category"))).ConfigureAwait(false);
        return await ReadArrayAsync(res).ConfigureAwait(false);
    }

    /// <summary>
    /// For /planner/stats: total = open tasks due before <paramref name="until"/> (includes overdue,
    /// same "due" definition /planner/today already uses) plus tasks completed (state=done) since
    /// <paramref name="since"/>; done = the latter count alone. Two plain id-only queries on columns
    /// (kind/state/due_at/updated_at) already used by GetTasksRowsAsync above, not invented ones.
    /// </summary>
    public async Task<(int total, int done)> GetTaskStatsAsync(PlannerSession session, DateTimeOffset until, DateTimeOffset since)
    {
        var untilStr = Uri.EscapeDataString(until.ToString("o"));
        var sinceStr = Uri.EscapeDataString(since.ToString("o"));

        using var openRes = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl($"entries?kind=eq.task&state=eq.open&due_at=lt.{untilStr}&select=id"))).ConfigureAwait(false);
        var openArr = await ReadArrayAsync(openRes).ConfigureAwait(false);

        using var doneRes = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl($"entries?kind=eq.task&state=eq.done&updated_at=gte.{sinceStr}&select=id"))).ConfigureAwait(false);
        var doneArr = await ReadArrayAsync(doneRes).ConfigureAwait(false);

        return (openArr.Count + doneArr.Count, doneArr.Count);
    }

    /// <summary>For /planner/stats: count of meetings starting in [since, until) — same starts_at column GetMeetingsRowsAsync already uses, just without its 5-row cap.</summary>
    public async Task<int> GetMeetingsCountAsync(PlannerSession session, DateTimeOffset since, DateTimeOffset until)
    {
        var sinceStr = Uri.EscapeDataString(since.ToString("o"));
        var untilStr = Uri.EscapeDataString(until.ToString("o"));
        using var res = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl($"entries?kind=eq.meeting&starts_at=gte.{sinceStr}&starts_at=lt.{untilStr}&select=id"))).ConfigureAwait(false);
        var arr = await ReadArrayAsync(res).ConfigureAwait(false);
        return arr.Count;
    }

    /// <summary>Secondary summary (rpc/summary_for). Best-effort: a failure here must never break /planner/today.</summary>
    private async Task<JsonObject?> GetSummaryAsync(PlannerSession session)
    {
        try
        {
            using var res = await SendAsync(session, () =>
            {
                var m = new HttpRequestMessage(HttpMethod.Post, RestUrl("rpc/summary_for"));
                m.Content = new StringContent(new JsonObject { ["p_days"] = 1 }.ToJsonString(), Encoding.UTF8, "application/json");
                return m;
            }).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return Json.ParseNode(text) as JsonObject;
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("planner summary_for failed: " + ex.Message);
            return null;
        }
    }

    public async Task<string?> GetEntryTitleAsync(PlannerSession session, string id)
    {
        using var res = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl("entries?id=eq." + Uri.EscapeDataString(id) + "&select=title"))).ConfigureAwait(false);
        var arr = await ReadArrayAsync(res).ConfigureAwait(false);
        return arr.Count > 0 ? (string?)(arr[0] as JsonObject)?["title"] : null;
    }

    /// <summary>
    /// Test-only: undo a whole capture batch via the self-scoped rpc/undo_batch (RLS + my_profile_id()
    /// mean it can only ever touch entries in the caller's own profile). Safer than matching on title,
    /// since the free-text capture pipeline may rewrite/translate the title the caller sent.
    /// </summary>
    public async Task<bool> UndoBatchAsync(PlannerSession session, string batchId)
    {
        using var res = await SendAsync(session, () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, RestUrl("rpc/undo_batch"));
            m.Content = new StringContent(new JsonObject { ["p_batch"] = batchId }.ToJsonString(), Encoding.UTF8, "application/json");
            return m;
        }).ConfigureAwait(false);
        return res.IsSuccessStatusCode;
    }

    /// <summary>Everything /planner/today needs: fetches all rows and parses them using the profile's timezone.</summary>
    public async Task<PlannerTodayData> GetTodayAsync(PlannerSession session)
    {
        var zone = ResolveZone(session.Profile.Timezone);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var startOfDayLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        var todayKey = nowLocal.ToString("yyyy-MM-dd");
        var nowUtc = DateTimeOffset.UtcNow;

        var tasksArr = await GetTasksRowsAsync(session).ConfigureAwait(false);
        var meetingsArr = await GetMeetingsRowsAsync(session).ConfigureAwait(false);
        var habitsArr = await GetHabitsRowsAsync(session).ConfigureAwait(false);
        var moneyArr = await GetMoneyRowsAsync(session, startOfDayLocal.ToUniversalTime()).ConfigureAwait(false);
        var summary = await GetSummaryAsync(session).ConfigureAwait(false);

        var tasks = new List<PlannerTaskItem>();
        foreach (var n in tasksArr.OfType<JsonObject>())
        {
            var dueRaw = (string?)n["due_at"];
            DateTimeOffset? due = dueRaw is null ? null : DateTimeOffset.Parse(dueRaw, null, System.Globalization.DateTimeStyles.RoundtripKind);
            var overdue = due is DateTimeOffset d && d < nowUtc;
            tasks.Add(new PlannerTaskItem((string?)n["id"] ?? "", (string?)n["title"] ?? "", due, overdue, (string?)n["state"] ?? "open"));
        }

        var meetings = new List<PlannerMeetingItem>();
        foreach (var n in meetingsArr.OfType<JsonObject>())
        {
            var startsRaw = (string?)n["starts_at"];
            if (startsRaw is null) continue;
            var starts = DateTimeOffset.Parse(startsRaw, null, System.Globalization.DateTimeStyles.RoundtripKind);
            meetings.Add(new PlannerMeetingItem((string?)n["id"] ?? "", (string?)n["title"] ?? "", starts, (int?)n["duration_min"]));
        }

        var habits = new List<PlannerHabitItem>();
        foreach (var n in habitsArr.OfType<JsonObject>())
        {
            var meta = n["meta"] as JsonObject;
            var checksArr = meta?["checks"] as JsonArray;
            var checkedToday = checksArr is not null && checksArr.Any(x => (string?)x == todayKey);
            habits.Add(new PlannerHabitItem((string?)n["id"] ?? "", (string?)n["title"] ?? "", checkedToday));
        }

        long spent = 0, income = 0;
        var byCategory = new Dictionary<string, long>();
        foreach (var n in moneyArr.OfType<JsonObject>())
        {
            var amount = (long?)n["amount_minor"] ?? 0;
            var direction = (string?)n["direction"] ?? "expense";
            if (direction == "income")
            {
                income += amount;
            }
            else
            {
                spent += amount;
                var category = (string?)n["category"];
                if (string.IsNullOrEmpty(category)) category = "без категории";
                byCategory[category] = byCategory.GetValueOrDefault(category) + amount;
            }
        }

        return new PlannerTodayData(tasks, meetings, habits, spent, income, byCategory, summary);
    }

    // ---- actions ------------------------------------------------------------------------------------

    public async Task SetTaskStateAsync(PlannerSession session, string id, bool done)
    {
        using var res = await SendAsync(session, () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Patch, RestUrl("entries?id=eq." + Uri.EscapeDataString(id)));
            m.Content = new StringContent(new JsonObject { ["state"] = done ? "done" : "open" }.ToJsonString(), Encoding.UTF8, "application/json");
            m.Headers.Add("Prefer", "return=minimal");
            return m;
        }).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException("postgrest " + (int)res.StatusCode);
    }

    public async Task CheckHabitAsync(PlannerSession session, string id, bool @checked, string todayKey)
    {
        using var getRes = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Get, RestUrl("entries?id=eq." + Uri.EscapeDataString(id) + "&select=meta"))).ConfigureAwait(false);
        var rows = await ReadArrayAsync(getRes).ConfigureAwait(false);
        var meta = rows.Count > 0 ? (rows[0] as JsonObject)?["meta"]?.DeepClone() as JsonObject ?? new JsonObject() : new JsonObject();
        var checksArr = meta["checks"] as JsonArray;
        var checks = checksArr is null ? new List<string>() : checksArr.Select(n => (string?)n ?? "").Where(s => s.Length > 0).ToList();

        if (@checked) { if (!checks.Contains(todayKey)) checks.Add(todayKey); }
        else checks.RemoveAll(d => d == todayKey);
        meta["checks"] = new JsonArray(checks.Select(c => (JsonNode)c).ToArray());

        using var patchRes = await SendAsync(session, () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Patch, RestUrl("entries?id=eq." + Uri.EscapeDataString(id)));
            m.Content = new StringContent(new JsonObject { ["meta"] = meta }.ToJsonString(), Encoding.UTF8, "application/json");
            m.Headers.Add("Prefer", "return=minimal");
            return m;
        }).ConfigureAwait(false);
        if (!patchRes.IsSuccessStatusCode) throw new HttpRequestException("postgrest " + (int)patchRes.StatusCode);
    }

    /// <summary>POST {SupabaseUrl}/functions/v1/capture. Status/body are returned mostly as-is; the caller decides how to shape 429.</summary>
    public async Task<PlannerHttpResult> CaptureAsync(PlannerSession session, string? text, string? audioBase64, string? mime, string source)
    {
        var body = new JsonObject { ["source"] = source };
        if (!string.IsNullOrEmpty(text)) body["text"] = text;
        if (!string.IsNullOrEmpty(audioBase64))
        {
            body["audio_base64"] = audioBase64;
            body["mime"] = string.IsNullOrEmpty(mime) ? "audio/webm" : mime;
        }

        using var res = await SendAsync(session, () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/functions/v1/capture");
            m.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return m;
        }).ConfigureAwait(false);

        var text2 = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        return new PlannerHttpResult((int)res.StatusCode, Json.ParseNode(text2) as JsonObject);
    }

    /// <summary>Test-only: delete one entry outright. The caller (PlannerService) checks the title prefix first.</summary>
    public async Task<bool> DeleteEntryAsync(PlannerSession session, string id)
    {
        using var res = await SendAsync(session, () =>
            new HttpRequestMessage(HttpMethod.Delete, RestUrl("entries?id=eq." + Uri.EscapeDataString(id)))).ConfigureAwait(false);
        return res.IsSuccessStatusCode;
    }

    private static TimeZoneInfo ResolveZone(string tz)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(tz) ? "Europe/Moscow" : tz); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"); }
            catch { return TimeZoneInfo.Utc; }
        }
    }
}
