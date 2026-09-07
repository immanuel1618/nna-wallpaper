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
    private readonly Dictionary<string, PopupWindow> _popups = new();
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
                bar.PopupRequested += OnPopupRequested;
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

    // ---- popovers --------------------------------------------------------------------------

    private void OnPopupRequested(TopBarWindow bar, string module, double anchorXCss, double anchorWCss)
        => TogglePopup(bar, module, anchorXCss, anchorWCss);

    /// <summary>Opens the popover for <paramref name="module"/> anchored under the topbar element
    /// that requested it; a second call for the module already open on that monitor closes it
    /// instead ("повторный клик по тому же модулю закрывает"). Opening a different module on a
    /// monitor that already has one open replaces it.</summary>
    private void TogglePopup(TopBarWindow bar, string module, double anchorXCss, double anchorWCss)
    {
        var monitorId = bar.Monitor.Id;
        if (_popups.TryGetValue(monitorId, out var existing))
        {
            var sameModule = string.Equals(existing.Module, module, StringComparison.Ordinal);
            ClosePopup(monitorId);
            if (sameModule) return;
        }

        var scale = bar.Monitor.Scale <= 0 ? 1.0 : bar.Monitor.Scale;
        var anchorCenterXPhysical = bar.Monitor.Left + (int)Math.Round((anchorXCss + anchorWCss / 2.0) * scale);
        var topYPhysical = bar.Monitor.Top + bar.HeightPx;

        try
        {
            var popup = new PopupWindow(_ctx, bar.Monitor, module, anchorCenterXPhysical, topYPhysical);
            popup.PopupClosed += OnPopupClosed;
            _popups[monitorId] = popup;
            popup.Show();
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("top bar popup open (" + module + ")", ex);
        }
    }

    private void OnPopupClosed(PopupWindow popup)
    {
        if (_popups.TryGetValue(popup.MonitorId, out var current) && ReferenceEquals(current, popup))
            _popups.Remove(popup.MonitorId);
    }

    private void ClosePopup(string monitorId)
    {
        if (!_popups.Remove(monitorId, out var popup)) return;
        popup.PopupClosed -= OnPopupClosed;
        try { popup.Close(); } catch { }
    }

    private void CloseAllPopups()
    {
        foreach (var popup in _popups.Values.ToList())
        {
            popup.PopupClosed -= OnPopupClosed;
            try { popup.Close(); } catch { }
        }
        _popups.Clear();
    }

    private void Close()
    {
        CloseAllPopups();
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
