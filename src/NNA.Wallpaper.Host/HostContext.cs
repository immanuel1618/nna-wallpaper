using System.Collections.Concurrent;
using NNA.Wallpaper.Host.Config;

namespace NNA.Wallpaper.Host;

/// <summary>Per-monitor state reported by the engine through /health.</summary>
public sealed record MonitorStatus(string id, string name, int width, int height, int x, int y, bool visible, bool paused, double scale);

/// <summary>
/// What the host may ask the application (WPF) to do. Headless mode uses <see cref="NullHostApp"/>.
/// </summary>
public interface IHostApp
{
    IReadOnlyList<MonitorStatus> Monitors { get; }
    bool Installed { get; }
    void OpenSettings(string? tab);
    void ReloadWallpaper();
    void OpenPlannerLogin();
    /// <summary>Send a JSON message to every wallpaper page (or one monitor when id is given).</summary>
    void PostToPages(string json, string? monitorId = null);
    void RequestExit();
    /// <summary>Show the top-level text input window near a screen point; the result is delivered by the caller's own flow.</summary>
    void RequestTextInput(string target, int screenX, int screenY, string? placeholder, Action<string> onSubmit);
}

public sealed class NullHostApp : IHostApp
{
    public IReadOnlyList<MonitorStatus> Monitors { get; } = Array.Empty<MonitorStatus>();
    public bool Installed => false;
    public void OpenSettings(string? tab) { }
    public void ReloadWallpaper() { }
    public void OpenPlannerLogin() { }
    public void PostToPages(string json, string? monitorId = null) { }
    public void RequestExit() { }
    public void RequestTextInput(string target, int screenX, int screenY, string? placeholder, Action<string> onSubmit) { }
}

/// <summary>Everything a host service needs: paths, config, log, port, app callbacks.</summary>
public sealed class HostContext
{
    public Paths Paths { get; }
    public ConfigStore Config { get; }
    public Log Log { get; }
    public int Port { get; }
    public IHostApp App { get; set; }
    /// <summary>Register /test/* endpoints (headless and --test-engine runs only).</summary>
    public bool TestEndpoints { get; set; }
    public DateTime Started { get; } = DateTime.UtcNow;

    /// <summary>Extra /health fields contributed by services (e.g. "gpu", "media").</summary>
    public ConcurrentDictionary<string, Func<object?>> Health { get; } = new();

    /// <summary>Events recorded by /test/event (engine click probes).</summary>
    public ConcurrentQueue<object> TestEvents { get; } = new();

    public HostContext(Paths paths, ConfigStore config, Log log, int port, IHostApp app)
    {
        Paths = paths;
        Config = config;
        Log = log;
        Port = port;
        App = app;
    }

    public string BaseUrl => "http://127.0.0.1:" + Port;
}
