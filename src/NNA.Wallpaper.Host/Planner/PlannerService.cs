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
    private static readonly TimeSpan LoginStateTtl = TimeSpan.FromMinutes(10);

    /// <summary>Set by the constructor so Hotkeys (a different project/thread — push-to-talk lives in
    /// NNA.Wallpaper, this service in NNA.Wallpaper.Host) can reach the signed-in session's capture
    /// pipeline without HostServices exposing every service instance; same pattern as EventsService.Current.</summary>
    public static PlannerService? Current { get; private set; }

    private readonly HostContext _ctx;
    private readonly PlannerClient _client;
    private readonly SemaphoreSlim _todayLock = new(1, 1);
    private readonly Timer _refreshTimer;

    private PlannerSession? _session;
    private JsonObject? _todayCache;
    private DateTimeOffset _todayCacheAt = DateTimeOffset.MinValue;
    private JsonObject? _lastQuota;

    /// <summary>Outstanding login nonces (session-fixation guard): GET /planner/callback saves a
    /// session only if it carries a state= that was minted here for this run of the login window,
    /// each one usable exactly once and expiring after <see cref="LoginStateTtl"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _loginStates = new();

    /// <summary>Last known state of the global push-to-talk hotkey, set by NNA.Wallpaper.Hotkeys after
    /// it tries RegisterHotKey; surfaced on /planner/status.hotkey. Not thread-locked — a torn read of
    /// this small struct-like tuple is harmless (worst case: one stale poll of /planner/status).</summary>
    private static (bool Registered, string? Error) _hotkeyState = (false, null);

    public PlannerService(HostContext ctx)
    {
        _ctx = ctx;
        _client = new PlannerClient(ctx);
        _session = PlannerSession.Load(ctx);
        _refreshTimer = new Timer(_ => _ = RefreshTickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Current = this;
    }

    /// <summary>Called by NNA.Wallpaper.Hotkeys after every RegisterHotKey/UnregisterHotKey attempt.</summary>
    public static void SetHotkeyState(bool registered, string? error) => _hotkeyState = (registered, error);

    public void Register(LocalApi api)
    {
        api.Map("GET", "/planner/status", Status);
        api.Map("GET", "/planner/today", Today);
        api.Map("POST", "/planner/done", Done);
        api.Map("POST", "/planner/habit", Habit);
        api.Map("POST", "/planner/capture", Capture);
        // POST, not GET: opening the Telegram login window is a state-changing action that must
        // require the API token like any other POST — a GET would let a plain <img>/<a> from any
        // page pop it open unattended (and, pre-state, cross-site engineer a session fixation via
        // /planner/callback below).
        api.Map("POST", "/planner/login", Login);
        api.Map("POST", "/planner/logout", Logout);
        api.Map("GET", "/planner/callback", Callback);
        api.Map("POST", "/planner/input", Input);
        api.Map("POST", "/planner/test-delete", TestDelete);
        api.Map("POST", "/planner/undo", Undo);
        api.Map("GET", "/planner/profile", Profile);
        api.Map("GET", "/planner/avatar", Avatar);
        api.Map("GET", "/planner/stats", Stats);
        if (_ctx.TestEndpoints)
        {
            // Test-only: sends a WAV file through the exact same capture path a hotkey push-to-talk
            // release does (CaptureVoiceAsync), so the pipeline can be probed headless (no WPF, no
            // real global hotkey) — see tests/hotkey-probe.ps1.
            api.Map("POST", "/planner/capture-file", CaptureFile);
        }
    }

    // ---- background refresh --------------------------------------------------------------------------

    private async Task RefreshTickAsync()
    {
        var session = _session;
        if (session is null || !session.NeedsRefresh) return;
        try
        {
            var ok = await _client.RefreshAsync(session).ConfigureAwait(false);
            if (!ok) { _ctx.Log.Warn("planner: background token refresh failed, sign-in will be required"); return; }
            // A session saved before the v2 profile fields existed gets first_name/username/photo_url
            // opportunistically merged in by ApplyRefresh above (if the refresh response carries them);
            // cache the avatar now that PhotoUrl may have just appeared.
            await EnsureAvatarCachedAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner refresh timer", ex);
        }
    }

    private string AvatarFilePath => Path.Combine(_ctx.Paths.DataDir, "cache", "avatar.jpg");

    /// <summary>Downloads the Telegram avatar once (skips if the file already exists) — see PlannerClient.DownloadAvatarAsync for the https://t.me/ restriction. Never throws.</summary>
    private async Task EnsureAvatarCachedAsync(PlannerSession session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.Profile.PhotoUrl)) return;
            if (File.Exists(AvatarFilePath)) return;
            await _client.DownloadAvatarAsync(session.Profile.PhotoUrl, AvatarFilePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("planner avatar cache: " + ex.Message);
        }
    }

    // ---- routes ---------------------------------------------------------------------------------------

    private static JsonObject HotkeyStatusJson() => new()
    {
        ["registered"] = _hotkeyState.Registered,
        ["error"] = _hotkeyState.Error,
    };

    private Task Status(ApiRequest req)
    {
        var s = _session;
        if (s is null)
        {
            var loggedOut = new JsonObject
            {
                ["loggedIn"] = false,
                ["profile"] = null,
                ["hotkey"] = HotkeyStatusJson(),
            };
            return req.Text(loggedOut.ToJsonString(Json.Api), "application/json; charset=utf-8");
        }

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
            ["hotkey"] = HotkeyStatusJson(),
        };
        if (_lastQuota is not null) obj["quota"] = _lastQuota.DeepClone();
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    /// <summary>GET /planner/profile — the settings page's profile card. Distinct from the compact
    /// /planner/status.profile (used by the wallpaper block): this one carries everything the card
    /// needs, including the avatar URL and the raw session fields the block has no use for.</summary>
    private Task Profile(ApiRequest req)
    {
        var s = _session;
        if (s is null) return req.Json(new { loggedIn = false });

        var obj = new JsonObject
        {
            ["loggedIn"] = true,
            ["display_name"] = s.Profile.DisplayName,
            ["first_name"] = s.Profile.FirstName,
            ["username"] = s.Profile.Username,
            ["avatar"] = File.Exists(AvatarFilePath) ? "/planner/avatar" : null,
            ["tier"] = s.Profile.Tier,
            ["timezone"] = s.Profile.Timezone,
            ["lang"] = s.Profile.Lang,
            ["expires_at"] = s.ExpiresAt.ToUnixTimeSeconds(),
            ["sections"] = s.Profile.Sections is { Count: > 0 }
                ? new JsonArray(s.Profile.Sections.Select(x => (JsonNode)x).ToArray())
                : null,
        };
        return req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8");
    }

    /// <summary>GET /planner/avatar — the cached JPEG, or 404 when there is none (no photo_url on the
    /// profile, or the one-time download hasn't happened/succeeded yet).</summary>
    private async Task Avatar(ApiRequest req)
    {
        var path = AvatarFilePath;
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) { await req.Error(404, "no avatar").ConfigureAwait(false); return; }
            bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch
        {
            await req.Error(404, "no avatar").ConfigureAwait(false);
            return;
        }
        await req.Bytes(bytes, "image/jpeg", 200, "no-store").ConfigureAwait(false);
    }

    /// <summary>
    /// GET /planner/stats?range=day|week — small stat tiles for the settings page. Every field here
    /// is computed from columns PlannerClient already queries elsewhere (kind/state/due_at/updated_at/
    /// starts_at/meta.checks/amount_minor/direction) — nothing from rpc/summary_for, whose actual
    /// response shape this client has never had a verified reason to depend on. "currency" has no
    /// backing column anywhere in this client's queries, so it is a fixed "RUB", not sourced data —
    /// see docs/PLANNER.md for the exact definition of every field.
    /// </summary>
    private async Task Stats(ApiRequest req)
    {
        var session = _session;
        if (session is null) { await req.Error(401, "not logged in").ConfigureAwait(false); return; }

        var range = string.Equals(req.Query("range"), "week", StringComparison.OrdinalIgnoreCase) ? "week" : "day";
        var days = range == "week" ? 7 : 1;

        try
        {
            var zone = ResolveZoneSafe(session.Profile.Timezone);
            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
            var todayStartLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
            var rangeStartLocal = todayStartLocal.AddDays(-(days - 1));
            var untilLocal = todayStartLocal.AddDays(1);
            var sinceUtc = rangeStartLocal.ToUniversalTime();
            var untilUtc = untilLocal.ToUniversalTime();
            var nowUtc = DateTimeOffset.UtcNow;

            var (tasksTotal, tasksDone) = await _client.GetTaskStatsAsync(session, untilUtc, sinceUtc).ConfigureAwait(false);
            var meetings = await _client.GetMeetingsCountAsync(session, nowUtc, untilUtc).ConfigureAwait(false);
            var habitsArr = await _client.GetHabitsRowsAsync(session).ConfigureAwait(false);
            var moneyArr = await _client.GetMoneyRowsAsync(session, sinceUtc).ConfigureAwait(false);

            var windowKeys = Enumerable.Range(0, days).Select(i => nowLocal.AddDays(-i).ToString("yyyy-MM-dd")).ToHashSet();
            var todayKey = nowLocal.ToString("yyyy-MM-dd");
            int habitsTotal = 0, habitsDone = 0, streak = 0;
            foreach (var n in habitsArr.OfType<JsonObject>())
            {
                habitsTotal++;
                var checks = ((n["meta"] as JsonObject)?["checks"] as JsonArray)?
                    .Select(x => (string?)x ?? "").ToHashSet() ?? new HashSet<string>();
                if (checks.Overlaps(windowKeys)) habitsDone++;

                var s = 0;
                var d = nowLocal.Date;
                while (checks.Contains(d.ToString("yyyy-MM-dd"))) { s++; d = d.AddDays(-1); }
                if (s > streak) streak = s;
            }
            _ = todayKey; // kept for clarity of intent (day range === windowKeys of size 1 === {todayKey})

            long spent = 0, income = 0;
            foreach (var n in moneyArr.OfType<JsonObject>())
            {
                var amount = (long?)n["amount_minor"] ?? 0;
                if ((string?)n["direction"] == "income") income += amount; else spent += amount;
            }

            var obj = new JsonObject
            {
                ["tasks"] = new JsonObject { ["done"] = tasksDone, ["total"] = tasksTotal },
                ["habits"] = new JsonObject { ["done"] = habitsDone, ["total"] = habitsTotal, ["streak"] = streak },
                ["money"] = new JsonObject { ["sum"] = income - spent, ["currency"] = "RUB" },
                ["meetings"] = meetings,
            };
            await req.Text(obj.ToJsonString(Json.Api), "application/json; charset=utf-8").ConfigureAwait(false);
        }
        catch (PlannerAuthException) { await req.Error(401, "sign in again").ConfigureAwait(false); }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner stats", ex);
            await req.Error(502, "planner unavailable").ConfigureAwait(false);
        }
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

    /// <summary>Mints a one-time login nonce and remembers it for <see cref="LoginStateTtl"/>. Called
    /// right before the Telegram login window navigates, so the nonce can ride along on the login
    /// page's URL as state= and come back unchanged on /planner/callback — without it (or with a
    /// stale/foreign one) the callback is refused, closing the session-fixation hole where a page
    /// could otherwise drive a victim's own /planner/callback navigation to plant an attacker's session.</summary>
    public string CreateLoginState()
    {
        PruneExpiredLoginStates();
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _loginStates[state] = DateTimeOffset.UtcNow + LoginStateTtl;
        return state;
    }

    private void PruneExpiredLoginStates()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _loginStates)
        {
            if (kv.Value < now) _loginStates.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>One-time use: valid states are removed whether or not the caller goes on to complete login.</summary>
    private bool ConsumeLoginState(string? state)
    {
        PruneExpiredLoginStates();
        return !string.IsNullOrEmpty(state) && _loginStates.TryRemove(state, out var expiry) && expiry >= DateTimeOffset.UtcNow;
    }

    private Task Logout(ApiRequest req)
    {
        _session = null;
        PlannerSession.Delete(_ctx);
        // Drop the cached avatar too: otherwise a different Telegram account signing in next would
        // briefly show the previous account's photo until EnsureAvatarCachedAsync's "already exists"
        // early-out is bypassed — it never is, so this file has to go now, not lazily.
        try { if (File.Exists(AvatarFilePath)) File.Delete(AvatarFilePath); } catch { }
        InvalidateCache();
        NotifyChanged();
        return req.Json(new { ok = true });
    }

    /// <summary>GET /planner/callback?&lt;widget fields&gt;&amp;state=&lt;nonce&gt; — arrives from the browser page
    /// inside the login window, no X-Token. state must be one minted by <see cref="CreateLoginState"/>
    /// for this login attempt (session-fixation guard: without this check, anything that could get a
    /// victim to load this URL — no token needed — could sign the victim's app into an attacker's
    /// planner account).</summary>
    private async Task Callback(ApiRequest req)
    {
        if (!ConsumeLoginState(req.Query("state")))
        {
            _ctx.Log.Warn("planner callback refused: missing/invalid/expired state");
            await req.Text(FailureHtml(), "text/html; charset=utf-8", 403).ConfigureAwait(false);
            return;
        }

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
            _ = EnsureAvatarCachedAsync(session); // fire-and-forget: must not delay the login page's redirect/close
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
            if (result.Body?["batch_id"] is JsonNode batchId) msg["batch_id"] = batchId.DeepClone();
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

    /// <summary>
    /// POST /planner/undo?batch=&lt;batch_id&gt; — production undo for the block's "ОТМЕНИТЬ" button
    /// (shown for a few seconds right after a capture). Same self-scoped rpc/undo_batch as
    /// <see cref="TestDelete"/>'s batch branch (RLS + my_profile_id() mean it can only ever touch the
    /// caller's own entries), kept as a separate route so the test-only one-entry-by-title-prefix path
    /// stays test-only.
    /// </summary>
    private async Task Undo(ApiRequest req)
    {
        var session = _session;
        if (session is null) { await req.Error(401, "not logged in").ConfigureAwait(false); return; }
        var batchId = req.Query("batch");
        if (string.IsNullOrEmpty(batchId)) { await req.Error(400, "batch required").ConfigureAwait(false); return; }

        bool ok;
        try
        {
            ok = await _client.UndoBatchAsync(session, batchId).ConfigureAwait(false);
        }
        catch (PlannerAuthException) { await req.Error(401, "sign in again").ConfigureAwait(false); return; }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner undo", ex);
            await req.Error(502, "planner unavailable").ConfigureAwait(false);
            return;
        }

        InvalidateCache();
        NotifyChanged();
        await req.Json(new { ok = ok }).ConfigureAwait(false);
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

    /// <summary>
    /// Sends already-recorded WAV bytes through the same capture pipeline /planner/input's
    /// OnInputSubmitted uses for text, and broadcasts the exact same <c>{"type":"planner",
    /// "event":"captured",...}</c> shape the wallpaper block's widget.js already listens for (see
    /// onHostMessage/handleCaptureResult there) — so a push-to-talk hotkey capture shows up in the
    /// TASKS block's recognized-text/undo panel identically to a mic capture started from the block
    /// itself. Called from NNA.Wallpaper.Hotkeys (via <see cref="Current"/>) and from the test-only
    /// /planner/capture-file route below. Returns null when there is no signed-in session or the
    /// capture call itself fails outright (logged either way, never throws).
    /// </summary>
    public async Task<JsonObject?> CaptureVoiceAsync(byte[] wavBytes, string source)
    {
        var session = _session;
        if (session is null)
        {
            _ctx.Log.Warn("planner capture (" + source + "): not logged in");
            return null;
        }
        if (wavBytes.Length == 0) return null;

        PlannerHttpResult result;
        try
        {
            var base64 = Convert.ToBase64String(wavBytes);
            result = await _client.CaptureAsync(session, null, base64, "audio/wav", source).ConfigureAwait(false);
        }
        catch (PlannerAuthException)
        {
            _ctx.Log.Warn("planner capture (" + source + "): sign in again");
            return null;
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner capture (" + source + ")", ex);
            return null;
        }

        if (result.Body?["quota"] is JsonNode q) _lastQuota = q.DeepClone() as JsonObject;
        InvalidateCache();

        var ok = (bool?)result.Body?["ok"] ?? false;
        var msg = new JsonObject { ["type"] = "planner", ["event"] = "captured", ["ok"] = ok };
        if (result.Body?["entries"] is JsonNode entries) msg["entries"] = entries.DeepClone();
        if (result.Body?["batch_id"] is JsonNode batchId) msg["batch_id"] = batchId.DeepClone();
        if (!ok)
        {
            msg["error"] = result.Status == 429 ? "limit" : ((string?)result.Body?["message"] ?? (string?)result.Body?["reason"] ?? "error");
        }
        else
        {
            NotifyChanged();
        }
        _ctx.App.PostToPages(msg.ToJsonString(Json.Api));
        return msg;
    }

    /// <summary>Test-only (TestEndpoints): POST {"path": "..."} — reads a WAV fixture off disk and runs it through CaptureVoiceAsync, exactly like a hotkey release would with real mic bytes.</summary>
    private async Task CaptureFile(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject ?? new JsonObject();
        var path = (string?)node["path"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await req.Error(400, "path required").ConfigureAwait(false);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await req.Error(400, "read failed: " + ex.Message).ConfigureAwait(false);
            return;
        }

        var result = await CaptureVoiceAsync(bytes, "test-file").ConfigureAwait(false);
        if (result is null)
        {
            await req.Error(401, "not logged in, or capture failed (see log)").ConfigureAwait(false);
            return;
        }
        await req.Text(result.ToJsonString(Json.Api), "application/json; charset=utf-8").ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Current == this) Current = null;
        _refreshTimer.Dispose();
        _client.Dispose();
        _todayLock.Dispose();
    }
}
