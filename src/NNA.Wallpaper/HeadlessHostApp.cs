using System.IO;
using System.Windows.Threading;
using NNA.Wallpaper.Host;

namespace NNA.Wallpaper;

/// <summary>
/// <see cref="IHostApp"/> used for --headless runs, and as the fallback before the wallpaper engine
/// takes over (and after it, if it fails to start). Unlike <see cref="NullHostApp"/> its
/// <see cref="RequestExit"/> actually quits the process — dispatched onto the UI thread — so
/// <c>/app/exit</c> and a forwarded <c>--exit</c> work even when there is no engine running.
/// </summary>
public sealed class HeadlessHostApp : IHostApp
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<int> _shutdown;

    public IReadOnlyList<MonitorStatus> Monitors { get; } = Array.Empty<MonitorStatus>();
    public bool Installed { get; }

    public HeadlessHostApp(Dispatcher dispatcher, Action<int> shutdown)
    {
        _dispatcher = dispatcher;
        _shutdown = shutdown;
        Installed = DetectInstalled();
    }

    public void OpenSettings(string? tab) { }
    public void ReloadWallpaper() { }
    public void OpenPlannerLogin() { }
    public void PostToPages(string json, string? monitorId = null) { }
    public void RequestExit() => _dispatcher.BeginInvoke(() => _shutdown(0));

    /// <summary>Same "installed via Velopack" detection <c>WallpaperEngine</c> uses: base dir is "current" with a sibling Update.exe.</summary>
    private static bool DetectInstalled()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            return dir.Name.Equals("current", StringComparison.OrdinalIgnoreCase)
                && dir.Parent is not null && File.Exists(Path.Combine(dir.Parent.FullName, "Update.exe"));
        }
        catch
        {
            return false;
        }
    }

    public void RequestTextInput(string target, int screenX, int screenY, string? placeholder, Action<string> onSubmit) { }

}
