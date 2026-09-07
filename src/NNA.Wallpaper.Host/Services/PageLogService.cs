using System.Text;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// POST /log/page — lets the wallpaper page (and its widgets) write into app.log, so a boot or
/// widget failure that never shows up anywhere (DevTools are off in production) still leaves a
/// trace. Body: <c>{level:"info"|"warn"|"error", monitor, msg, data?}</c>. Written as
/// <c>page[&lt;monitor&gt;] &lt;level&gt;: &lt;msg&gt;</c> (optionally followed by a compact JSON dump of
/// <c>data</c>). Same X-Token as every other POST route.
///
/// Two cheap abuse guards, since this is reachable from page JS (i.e. from widget code, which is
/// not fully trusted): a 2 KB body cap (oversized bodies are rejected before being parsed or
/// logged) and a 20-messages/second global rate limit (further messages in that window are
/// dropped silently — the page calls this fire-and-forget and never checks the response). The
/// API token, if it appears verbatim in the message, is replaced with "***" before it ever
/// reaches the log file.
/// </summary>
public sealed class PageLogService : IHostService
{
    private const long MaxBodyBytes = 2 * 1024;
    private const int MaxMessagesPerSecond = 20;

    private readonly HostContext _ctx;
    private readonly object _rateLock = new();
    private long _rateWindowSecond = -1;
    private int _rateCountInWindow;

    public PageLogService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api)
    {
        api.Map("POST", "/log/page", Handle);
    }

    private async Task Handle(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
        {
            await req.Json(new { ok = false, error = "too large" }, 413).ConfigureAwait(false);
            return;
        }

        if (!AllowOneMore())
        {
            // Rate-limited: the page fires this without awaiting the response, so a quiet 429
            // is enough to keep a runaway logger from ever filling the disk.
            await req.Json(new { ok = false, error = "rate limited" }, 429).ConfigureAwait(false);
            return;
        }

        var node = Json.ParseNode(body) as JsonObject;
        var level = NormalizeLevel((string?)node?["level"]);
        var monitor = (string?)node?["monitor"];
        if (string.IsNullOrEmpty(monitor)) monitor = "?";
        var msg = (string?)node?["msg"] ?? "";
        if (msg.Length > 500) msg = msg[..500];
        msg = Redact(msg);

        var line = "page[" + monitor + "] " + level + ": " + msg;
        var dataNode = node?["data"];
        if (dataNode is not null)
        {
            try
            {
                var dataStr = dataNode.ToJsonString(Json.Api);
                if (dataStr.Length > 500) dataStr = dataStr[..500];
                line += " " + Redact(dataStr);
            }
            catch { /* malformed data: skip it, the message alone is still useful */ }
        }

        switch (level)
        {
            case "warn": _ctx.Log.Warn(line); break;
            case "error": _ctx.Log.Error(line); break;
            default: _ctx.Log.Info(line); break;
        }

        await req.Json(new { ok = true }).ConfigureAwait(false);
    }

    private static string NormalizeLevel(string? level) => level switch
    {
        "warn" => "warn",
        "error" => "error",
        _ => "info",
    };

    /// <summary>Simple fixed-window counter: resets every wall-clock second. Good enough for an
    /// abuse guard on a fire-and-forget local endpoint — it does not need to be exact.</summary>
    private bool AllowOneMore()
    {
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_rateLock)
        {
            if (second != _rateWindowSecond)
            {
                _rateWindowSecond = second;
                _rateCountInWindow = 0;
            }
            if (_rateCountInWindow >= MaxMessagesPerSecond) return false;
            _rateCountInWindow++;
            return true;
        }
    }

    /// <summary>Replaces every occurrence of the live API token with "***" so a message that
    /// happens to include it (e.g. an error text that echoed a request URL) never lands in the
    /// log file in the clear.</summary>
    private string Redact(string text)
    {
        var token = _ctx.Config.App.ApiToken;
        if (string.IsNullOrEmpty(token) || text.IndexOf(token, StringComparison.Ordinal) < 0) return text;
        return text.Replace(token, "***", StringComparison.Ordinal);
    }
}
