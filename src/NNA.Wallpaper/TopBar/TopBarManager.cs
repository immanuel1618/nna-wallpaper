using System.Windows.Threading;
using Microsoft.Win32;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using Windows.Win32;

namespace NNA.Wallpaper.TopBar;

/// <summary>Creates one <see cref="TopBarWindow"/> per monitor (or the primary only) from config, and keeps them in sync.</summary>
public sealed class TopBarManager : IDisposable
{
    private readonly HostContext _ctx;
    private readonly Dispatcher _dispatcher;
    private readonly List<TopBarWindow> _bars = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public IReadOnlyList<TopBarWindow> Bars => _bars;

    public TopBarManager(HostContext ctx, Dispatcher dispatcher)
    {
        _ctx = ctx;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    /// <summary>Re-read config and (re)create the bars. Cheap enough to call on every config change.</summary>
    public void Apply()
    {
        if (_disposed) return;
        Close();
        var cfg = _ctx.Config.App.TopBar;
        if (!cfg.Enabled) { _timer.Stop(); return; }
        var monitors = DisplayMonitors.Enumerate();
        if (string.Equals(cfg.Monitors, "primary", StringComparison.OrdinalIgnoreCase))
            monitors = monitors.Where(m => m.Primary).ToList();
        foreach (var m in monitors)
        {
            try
            {
                var bar = new TopBarWindow(_ctx, m, cfg);
                bar.Show();
                _bars.Add(bar);
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("top bar create " + m.Id, ex);
            }
        }
        _ctx.Log.Info("top bar: " + _bars.Count + " bar(s), height " + cfg.Height + ", mode " + cfg.Style.Mode);
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Reload()
    {
        foreach (var b in _bars) b.Reload();
    }

    private void Tick()
    {
        if (_disposed || _bars.Count == 0) return;
        try
        {
            var cfg = _ctx.Config.App.TopBar;
            var globalFull = FullscreenWatcher.GlobalFullscreen();
            var rect = FullscreenWatcher.ForegroundAppRect(out _);
            PInvoke.GetCursorPos(out var pt);
            foreach (var bar in _bars)
            {
                var covered = globalFull || (rect is { } r && FullscreenWatcher.Covers(r, bar.Monitor));
                var hidden = covered;
                if (!hidden && cfg.AutoHide)
                {
                    var nearTop = bar.Monitor.Contains(pt.X, pt.Y) && pt.Y - bar.Monitor.Top <= bar.HeightPx + 2;
                    hidden = !nearTop;
                }
                bar.SetHidden(hidden);
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("top bar tick", ex);
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Apply);

    private void Close()
    {
        foreach (var b in _bars)
        {
            try { b.Close(); } catch { }
        }
        _bars.Clear();
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
