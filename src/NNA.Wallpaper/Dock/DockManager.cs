using System.Windows.Threading;
using Microsoft.Win32;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using Windows.Win32;

namespace NNA.Wallpaper.Dock;

/// <summary>Creates one <see cref="DockWindow"/> per monitor (or the primary only) from config, and
/// keeps it in sync — the dock counterpart of <see cref="TopBar.TopBarManager"/>.</summary>
public sealed class DockManager : IDisposable
{
    private readonly HostContext _ctx;
    private readonly Dispatcher _dispatcher;
    private readonly List<DockWindow> _docks = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public IReadOnlyList<DockWindow> Docks => _docks;

    public DockManager(HostContext ctx, Dispatcher dispatcher)
    {
        _ctx = ctx;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    /// <summary>Re-read config and (re)create the docks. Cheap enough to call on every config change.</summary>
    public void Apply()
    {
        if (_disposed) return;
        Close();
        var cfg = _ctx.Config.App.Dock;
        if (!cfg.Enabled) { _timer.Stop(); return; }
        var monitors = DisplayMonitors.Enumerate();
        if (string.Equals(cfg.Monitors, "primary", StringComparison.OrdinalIgnoreCase))
            monitors = monitors.Where(m => m.Primary).ToList();
        foreach (var m in monitors)
        {
            try
            {
                var dock = new DockWindow(_ctx, m, cfg);
                dock.Show();
                _docks.Add(dock);
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("dock create " + m.Id, ex);
            }
        }
        _ctx.Log.Info("dock: " + _docks.Count + " window(s), size " + cfg.Size + ", mode " + cfg.Style.Mode);
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Reload()
    {
        foreach (var d in _docks) d.Reload();
    }

    private void Tick()
    {
        if (_disposed || _docks.Count == 0) return;
        try
        {
            var cfg = _ctx.Config.App.Dock;
            var globalFull = FullscreenWatcher.GlobalFullscreen();
            var rect = FullscreenWatcher.ForegroundAppRect(out _);
            PInvoke.GetCursorPos(out var pt);
            foreach (var dock in _docks)
            {
                var covered = globalFull || (rect is { } r && FullscreenWatcher.Covers(r, dock.Monitor));
                var hidden = covered;
                if (!hidden && cfg.AutoHide)
                {
                    var nearBottom = dock.Monitor.Contains(pt.X, pt.Y) && dock.BottomEdgeY - pt.Y <= dock.HeightPx + 2;
                    hidden = !nearBottom;
                }
                dock.SetHidden(hidden);
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("dock tick", ex);
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Apply);

    private void Close()
    {
        foreach (var d in _docks)
        {
            try { d.Close(); } catch { }
        }
        _docks.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Close();
    }
}
