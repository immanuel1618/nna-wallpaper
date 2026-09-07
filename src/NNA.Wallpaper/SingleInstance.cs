using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;

namespace NNA.Wallpaper;

/// <summary>
/// Enforces one running NNA Wallpaper process via a named local mutex. When a second process
/// launches, it never starts the engine or opens a window: it forwards the CLI flag to the already
/// running instance's local API (reading that instance's port and token from its own app.json) and
/// exits. A bare second launch with no flags is a silent no-op — it must return quickly so scripts
/// and shell integrations that just "make sure it's running" don't hang.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "NNA.Wallpaper";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Tries to become the one instance. Returns the held <see cref="Mutex"/> (keep it alive for the
    /// life of the process, release and dispose on exit) when this is the first instance, or null
    /// when another instance already holds it.
    /// </summary>
    public static Mutex? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }

    /// <summary>Handles a second launch: forwards the flag (or performs --import) and returns the process exit code.</summary>
    public static int ForwardAndExit(CliArgs args)
    {
        var paths = Paths.Resolve(args.DataDir);
        var (port, token) = ReadEndpoint(paths, args.Port);

        if (args.Import is not null)
        {
            return RunImportAndReload(paths, port, token, args.Import);
        }

        var route = RouteFor(args);
        if (route is null) return 0; // bare second launch: nothing to send, nothing to wait for

        Post(port, token, route);
        return 0;
    }

    private static string? RouteFor(CliArgs args)
    {
        if (args.Exit) return "/app/exit";
        if (args.Settings) return "/app/settings";
        if (args.Reload) return "/app/reload";
        if (args.Pause) return "/app/pause";
        if (args.Resume) return "/app/resume";
        if (args.CheckUpdates) return "/app/check-updates";
        return null;
    }

    private static int RunImportAndReload(Paths paths, int port, string token, string sourceDir)
    {
        var log = new Log(paths);

        // Check the source before touching config: ConfigStore.Load() writes default app.json /
        // monitors.json as a side effect when they don't exist yet, and a failed import must not
        // touch the running instance's config at all.
        if (!Directory.Exists(Path.GetFullPath(sourceDir)))
        {
            log.Error("import failed: source not found: " + sourceDir);
            return 2;
        }

        try
        {
            var cfg = new ConfigStore(paths, log);
            cfg.Load();
            var monitors = FetchMonitors(port);
            var report = Import.Run(paths, cfg, log, sourceDir, monitors);
            log.Info("import (forwarded to running instance's data dir): " + report);
        }
        catch (DirectoryNotFoundException ex)
        {
            log.Error("import failed: source not found", ex);
            return 2;
        }
        catch (Exception ex)
        {
            log.Error("import failed", ex);
            return 2;
        }

        Post(port, token, "/app/reload");
        return 0;
    }

    private static void Post(int port, string token, string route)
    {
        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout };
            if (!string.IsNullOrEmpty(token)) http.DefaultRequestHeaders.Add("X-Token", token);
            using var content = new ByteArrayContent(Array.Empty<byte>());
            using var response = http.PostAsync($"http://127.0.0.1:{port}{route}", content).GetAwaiter().GetResult();
            _ = response; // best effort; a second launch has no further recourse either way
        }
        catch
        {
            // the running instance may be unreachable or shutting down; nothing more to do here
        }
    }

    private static IReadOnlyList<MonitorStatus> FetchMonitors(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout };
            var json = http.GetStringAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult();
            if (JsonNode.Parse(json) is not JsonObject root || root["monitors"] is not JsonArray arr)
                return Array.Empty<MonitorStatus>();

            var list = new List<MonitorStatus>();
            foreach (var node in arr)
            {
                if (node is not JsonObject m) continue;
                list.Add(new MonitorStatus(
                    (string?)m["id"] ?? "",
                    (string?)m["name"] ?? "",
                    (int?)m["width"] ?? 0,
                    (int?)m["height"] ?? 0,
                    (int?)m["x"] ?? 0,
                    (int?)m["y"] ?? 0,
                    (bool?)m["visible"] ?? false,
                    (bool?)m["paused"] ?? false,
                    (double?)m["scale"] ?? 1.0));
            }
            return list;
        }
        catch
        {
            return Array.Empty<MonitorStatus>();
        }
    }

    private static (int port, string token) ReadEndpoint(Paths paths, int? portOverride)
    {
        var port = portOverride ?? 1618;
        var token = "";
        try
        {
            if (File.Exists(paths.AppConfigFile) && JsonNode.Parse(File.ReadAllText(paths.AppConfigFile)) is JsonObject app)
            {
                if (portOverride is null && app["apiPort"] is JsonValue pv && pv.TryGetValue<int>(out var p)) port = p;
                if (app["apiToken"] is JsonValue tv && tv.TryGetValue<string>(out var t)) token = t;
            }
        }
        catch
        {
            // fall back to defaults; the forwarded request will simply fail if these are wrong
        }
        return (port, token);
    }
}
