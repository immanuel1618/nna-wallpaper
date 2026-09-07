using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace NNA.Wallpaper.Host;

public delegate Task ApiHandler(ApiRequest req);
public delegate Task WsHandler(WebSocket socket, ApiRequest req, CancellationToken ct);

/// <summary>One HTTP request plus response helpers. Keys in JSON are written exactly as named.</summary>
public sealed class ApiRequest
{
    public HttpListenerRequest Raw { get; }
    public HttpListenerResponse Response { get; }
    public string Method { get; }
    public string Path { get; }
    /// <summary>For prefix routes: the part of the path after the prefix (e.g. "steam.png" for /icon/steam.png).</summary>
    public string Tail { get; internal set; } = "";

    internal ApiRequest(HttpListenerContext ctx)
    {
        Raw = ctx.Request;
        Response = ctx.Response;
        Method = ctx.Request.HttpMethod.ToUpperInvariant();
        Path = ctx.Request.Url?.AbsolutePath ?? "/";
    }

    public string? Query(string key) => Raw.QueryString[key];

    public async Task<string> ReadBodyAsync()
    {
        using var reader = new StreamReader(Raw.InputStream, Raw.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    public Task Json(object? value, int code = 200)
    {
        var body = Encoding.UTF8.GetBytes(Host.Json.Serialize(value));
        return Bytes(body, "application/json; charset=utf-8", code, "no-store");
    }

    public Task Error(int code, string message) => Json(new { error = message }, code);

    public Task Text(string text, string contentType = "text/plain; charset=utf-8", int code = 200) =>
        Bytes(Encoding.UTF8.GetBytes(text), contentType, code, "no-store");

    public async Task Bytes(byte[] data, string contentType, int code = 200, string? cacheControl = null)
    {
        Response.StatusCode = code;
        Response.ContentType = contentType;
        // Every response: browsers must not MIME-sniff a served file (e.g. a widget icon) into
        // something executable and run it as such.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (cacheControl is not null) Response.Headers["Cache-Control"] = cacheControl;
        Response.ContentLength64 = data.Length;
        try
        {
            // HEAD must not send a body (RFC 7231 4.3.2) — writing one anyway is not just wasted
            // work: .NET's HttpListenerResponse can throw ProtocolViolationException for it
            // ("Bytes to be written to the stream exceed the Content-Length bytes size
            // specified"), which happened in production against a static /widgets/*/widget.js
            // request. That left the underlying keep-alive connection framed for a body that was
            // never fully sent, so the client (WebView2) hangs waiting for the rest of that
            // response forever — and every later request the page queues on the same connection
            // (retries, /planner/status polls) stalls behind it until something forces a new
            // connection, e.g. a full page reload. Skipping the write for HEAD avoids the
            // exception — and hence the stuck connection — entirely.
            if (Method != "HEAD")
            {
                await Response.OutputStream.WriteAsync(data).ConfigureAwait(false);
            }
        }
        catch
        {
            // Whatever went wrong mid-write, the promised Content-Length vs. bytes actually sent
            // may now disagree. Abort drops the TCP connection outright instead of trying to
            // gracefully finish it, so the client sees a clean disconnect (and can retry on a
            // fresh connection) rather than hanging forever waiting for bytes that will never
            // arrive.
            try { Response.Abort(); } catch { }
            throw;
        }
        finally
        {
            try { Response.Close(); } catch { }
        }
    }
}

/// <summary>
/// Local HTTP API on 127.0.0.1 only. CORS "*" (pages are local), every non-GET request needs the
/// API token (header X-Token or ?t=), same as the Python helper. Also serves static web roots and
/// WebSocket endpoints.
/// </summary>
public sealed class LocalApi : IDisposable
{
    private sealed record Route(string Method, string Path, ApiHandler Handler);
    private sealed record PrefixRoute(string Method, string Prefix, ApiHandler Handler);
    private sealed record StaticRoute(string Prefix, string[] Roots);

    /// <summary>
    /// GET endpoints that hand back something a same-origin page needs but a third-party site
    /// embedding/navigating this loopback origin must not get for free: the API token (/config),
    /// window/audio-session titles and icons, dock/planner/system state, launch list, the live
    /// events stream. Kept in one place (also referenced by HostServices.GetConfig for the token
    /// itself) so the list can't drift between the token gate and the request gate.
    /// </summary>
    private static readonly string[] SensitiveExactPaths =
    {
        "/config/full", "/windows", "/windows/icon",
        "/audio/sessions", "/audio/session-icon", "/events",
    };

    private static readonly string[] SensitivePrefixes =
    {
        "/dock/", "/planner/", "/system/", "/cursor/", "/launch/",
    };

    /// <summary>True for any GET/HEAD path that must not be served to a request whose
    /// Sec-Fetch-Site says it did not originate from this same page (see <see cref="SensitiveExactPaths"/>).</summary>
    public static bool IsSensitivePath(string path)
    {
        // /planner/callback is, by design, reached by a cross-site top-level navigation — the login
        // page (a different site entirely) sends the browser here via location.href once Telegram's
        // widget confirms the user. That is exactly the request shape (Sec-Fetch-Site: cross-site)
        // this gate exists to block for every other path under /planner/, so it must be exempted here
        // rather than broken; its own defense is the one-time state= nonce (PlannerService.Callback).
        //
        // Bare /config is exempted too: unlike /config/full (settings-page only, no legitimate
        // cross-site caller, so a flat 403 is correct) it is what the wallpaper page itself polls for
        // theme/layout/widget settings, and HostServices.GetConfig already redacts the one genuinely
        // sensitive field (the API token) per-request via IsSameOriginRequest — a blanket 403 here
        // would be redundant with that and would break nothing real, but the contract this gate must
        // not violate is "GET /config cross-site still answers, just without token" (see
        // tests/security-probe.ps1).
        if (path is "/planner/callback" or "/config") return false;
        return Array.IndexOf(SensitiveExactPaths, path) >= 0
            || Array.Exists(SensitivePrefixes, p => path.StartsWith(p, StringComparison.Ordinal))
            || (path.StartsWith("/widgets/", StringComparison.Ordinal) && path.EndsWith("/preview.png", StringComparison.Ordinal));
    }

    /// <summary>Sec-Fetch-Site values that mean "this request was made by our own page" (Chromium/WebView2
    /// send this header on essentially every request; curl and other non-browser clients never do).</summary>
    private static bool IsSameOriginSite(string site) =>
        string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(site, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the request is verifiably our own page: either the browser told us so
    /// (Sec-Fetch-Site: same-origin/none), or nothing told us otherwise — no Sec-Fetch-Site AND no
    /// Origin/Referer at all, which is what curl and our own PowerShell test scripts send against
    /// localhost. Used to decide whether GET /config may include the API token in its response.
    /// </summary>
    public static bool IsSameOriginRequest(ApiRequest req)
    {
        var site = req.Raw.Headers["Sec-Fetch-Site"];
        if (!string.IsNullOrEmpty(site)) return IsSameOriginSite(site);
        return string.IsNullOrEmpty(req.Raw.Headers["Origin"]) && string.IsNullOrEmpty(req.Raw.Headers["Referer"]);
    }

    /// <summary>True only when the browser explicitly says this request is not same-origin/none — i.e.
    /// it sent Sec-Fetch-Site with some other value ("cross-site", "same-site"). A request with no
    /// Sec-Fetch-Site at all (curl, our test scripts) is not flagged here; the Origin/Host checks
    /// earlier in <see cref="Handle"/> already cover a browser page trying to reach us cross-origin.</summary>
    private static bool IsCrossSiteRequest(ApiRequest req)
    {
        var site = req.Raw.Headers["Sec-Fetch-Site"];
        return !string.IsNullOrEmpty(site) && !IsSameOriginSite(site);
    }

    private readonly HostContext _ctx;
    private readonly List<Route> _routes = new();
    private readonly List<PrefixRoute> _prefixes = new();
    private readonly List<StaticRoute> _static = new();
    private readonly Dictionary<string, WsHandler> _sockets = new();
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;

    public int Port => _ctx.Port;
    public bool IsRunning => _listener?.IsListening == true;

    public LocalApi(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Map(string method, string path, ApiHandler handler) =>
        _routes.Add(new Route(method.ToUpperInvariant(), path, handler));

    public void MapPrefix(string method, string prefix, ApiHandler handler) =>
        _prefixes.Add(new PrefixRoute(method.ToUpperInvariant(), prefix, handler));

    public void MapWebSocket(string path, WsHandler handler) => _sockets[path] = handler;

    /// <summary>Serve files under <paramref name="roots"/> (first hit wins) at <paramref name="prefix"/> (must end with '/').</summary>
    public void MapStatic(string prefix, params string[] roots) =>
        _static.Add(new StaticRoute(prefix, roots.Select(System.IO.Path.GetFullPath).ToArray()));

    public void Start()
    {
        var port = _ctx.Port;
        foreach (var host in new[] { "127.0.0.1", "localhost" })
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://{host}:{port}/");
            try
            {
                l.Start();
                _listener = l;
                _ctx.Log.Info($"api listening on http://{host}:{port}/");
                break;
            }
            catch (HttpListenerException ex)
            {
                _ctx.Log.Warn($"api bind {host}:{port} failed: {ex.Message}");
                l.Close();
            }
        }
        if (_listener is null) throw new InvalidOperationException($"cannot bind port {port}");
        _ = Task.Run(AcceptLoop);
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop()
    {
        var listener = _listener!;
        while (!_cts.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("api accept", ex);
                continue;
            }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext http)
    {
        var req = new ApiRequest(http);
        var res = http.Response;
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Token";
        res.Headers["Server"] = "nna-wallpaper/" + HostInfo.Version;

        try
        {
            // Only our own pages (served from this loopback origin) may talk to the API from a browser
            // context. A foreign Origin (any web page open in a browser) is refused outright, so the
            // API token handed to the wallpaper page through /config can never be read cross-origin.
            var hostHeader = http.Request.Headers["Host"] ?? "";
            if (!IsOwnHost(hostHeader))
            {
                _ctx.Log.Warn("refused foreign host header " + hostHeader + " on " + req.Method + " " + req.Path);
                await req.Json(new { error = "forbidden host" }, 403).ConfigureAwait(false);
                return;
            }
            var origin = http.Request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                if (!IsOwnOrigin(origin))
                {
                    _ctx.Log.Warn("refused foreign origin " + origin + " on " + req.Method + " " + req.Path);
                    await req.Json(new { error = "forbidden origin" }, 403).ConfigureAwait(false);
                    return;
                }
                res.Headers["Access-Control-Allow-Origin"] = origin;
                res.Headers["Vary"] = "Origin";
            }

            // Origin/Host cover a browser page reaching us cross-origin; Sec-Fetch-Site catches the
            // gap they miss — a same-site (different port on 127.0.0.1/localhost isn't "same-site" for
            // this header, but a plain <img>/<script> "no-cors" load from another page on this machine
            // can still omit Origin) or cross-site GET for state that isn't meant to leave this page:
            // the token, window/audio-session titles, dock/planner/system state, the events stream.
            if (req.Method is "GET" or "HEAD" && IsSensitivePath(req.Path) && IsCrossSiteRequest(req))
            {
                _ctx.Log.Warn("refused cross-site Sec-Fetch-Site " + http.Request.Headers["Sec-Fetch-Site"] + " on " + req.Method + " " + req.Path);
                await req.Json(new { error = "forbidden site" }, 403).ConfigureAwait(false);
                return;
            }

            if (req.Method == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            if (http.Request.IsWebSocketRequest && _sockets.TryGetValue(req.Path, out var ws))
            {
                var wsCtx = await http.AcceptWebSocketAsync(null).ConfigureAwait(false);
                try { await ws(wsCtx.WebSocket, req, _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
                catch (Exception ex) { _ctx.Log.Error("ws " + req.Path, ex); }
                finally { try { wsCtx.WebSocket.Dispose(); } catch { } }
                return;
            }

            if (req.Method is not ("GET" or "HEAD") && !Authorized(req))
            {
                await req.Json(new { error = "bad token" }, 403).ConfigureAwait(false);
                return;
            }

            var handler = Resolve(req);
            if (handler is null)
            {
                if (await TryStatic(req).ConfigureAwait(false)) return;
                await req.Json(new { error = "not found" }, 404).ConfigureAwait(false);
                return;
            }

            await handler(req).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error(req.Method + " " + req.Path, ex);
            try { await req.Json(new { error = ex.Message }, 500).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Host header must name this loopback listener (guards against DNS rebinding).</summary>
    private bool IsOwnHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return true; // HTTP/1.0 clients without Host
        var h = host.Trim();
        return h.Equals("127.0.0.1:" + _ctx.Port, StringComparison.OrdinalIgnoreCase)
            || h.Equals("localhost:" + _ctx.Port, StringComparison.OrdinalIgnoreCase)
            || h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || h.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsOwnOrigin(string origin) =>
        string.Equals(origin, "http://127.0.0.1:" + _ctx.Port, StringComparison.OrdinalIgnoreCase)
        || string.Equals(origin, "http://localhost:" + _ctx.Port, StringComparison.OrdinalIgnoreCase);

    private bool Authorized(ApiRequest req)
    {
        var token = req.Raw.Headers["X-Token"];
        if (string.IsNullOrEmpty(token)) token = req.Query("t");
        var expected = _ctx.Config.App.ApiToken;
        return !string.IsNullOrEmpty(expected) && string.Equals(token, expected, StringComparison.Ordinal);
    }

    private ApiHandler? Resolve(ApiRequest req)
    {
        var method = req.Method == "HEAD" ? "GET" : req.Method;
        foreach (var r in _routes)
        {
            if (r.Method == method && r.Path == req.Path) return r.Handler;
        }
        PrefixRoute? best = null;
        foreach (var p in _prefixes)
        {
            if (p.Method == method && req.Path.StartsWith(p.Prefix, StringComparison.Ordinal) && (best is null || p.Prefix.Length > best.Prefix.Length))
                best = p;
        }
        if (best is null) return null;
        req.Tail = req.Path.Substring(best.Prefix.Length);
        return best.Handler;
    }

    private async Task<bool> TryStatic(ApiRequest req)
    {
        if (req.Method is not ("GET" or "HEAD")) return false;
        StaticRoute? route = null;
        foreach (var s in _static)
        {
            if (req.Path.StartsWith(s.Prefix, StringComparison.Ordinal) && (route is null || s.Prefix.Length > route.Prefix.Length)) route = s;
        }
        if (route is null) return false;

        var rel = Uri.UnescapeDataString(req.Path.Substring(route.Prefix.Length));
        if (rel.Length == 0 || rel.EndsWith('/')) rel += "index.html";
        if (rel.Contains("..") || rel.Contains(':') || rel.Contains('\\'))
        {
            await req.Json(new { error = "bad path" }, 400).ConfigureAwait(false);
            return true;
        }
        foreach (var root in route.Roots)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(full)) continue;
            var bytes = await File.ReadAllBytesAsync(full).ConfigureAwait(false);
            await req.Bytes(bytes, Mime(full), 200, "no-store").ConfigureAwait(false);
            return true;
        }
        await req.Json(new { error = "not found" }, 404).ConfigureAwait(false);
        return true;
    }

    public static string Mime(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        ".map" => "application/json",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".webm" => "video/webm",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };
}
