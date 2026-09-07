using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Taskbar;

/// <summary>
/// Owner decision D11: "win-only" taskbar mode. The Windows taskbar is auto-hidden (ABM_SETSTATE)
/// and additionally kept from sliding out on mouse-over by a thin always-on-top "shield" window
/// sitting over its hover strip on every monitor. It only appears again while Win is held down
/// (together with the Start menu it opens) or while the mouse is actually over its rectangle; once
/// Start closes and the mouse leaves, it is covered again.
///
/// This needs a low-level keyboard hook (WH_KEYBOARD_LL) to notice a bare Win tap (down, then up,
/// with no other key pressed meanwhile) as opposed to Win+E, Win+D, Win+1, etc., which must NOT show
/// the taskbar. The hook only ever looks at the virtual-key code of the event it receives, to decide
/// "is this Win alone" — it never stores a key code anywhere beyond the one-shot flags below, never
/// writes anything to disk or the log, and always calls <see cref="PInvoke.CallNextHookEx"/> so it
/// never blocks a keystroke. It is removed (<see cref="Stop"/>) whenever the mode changes away from
/// win-only, whenever the wallpaper is paused (see <see cref="Tick"/>), and on shutdown/Reset.
/// </summary>
public sealed class TaskbarLock : IDisposable
{
    // Local window-style flags (same values TopBarWindow.cs uses for its own always-on-top strip).
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int ShieldHeight = 2;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private static readonly TimeSpan WinTapWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AwayDebounce = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ReapplyDelay = TimeSpan.FromSeconds(2);

    private readonly HostContext _ctx;
    private readonly Log _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _decideTimer;
    private readonly DispatcherTimer _watchdogTimer;
    private readonly List<ShieldWindow> _shields = new();

    private bool _running;
    private bool _disposed;
    private bool _shielded;
    private bool _forcedVisible;
    private DateTime? _awaySince;
    private HWND _lastTray;
    private DateTime? _reapplyAt;

    // ---- WH_KEYBOARD_LL state: a flag and a timestamp, nothing else is ever kept about a keystroke ----
    private bool _winDown;
    private bool _winAlone;
    private DateTime _winDownAt;

    private nint _hookHandle;
    private HOOKPROC? _hookProc; // kept alive: SetWindowsHookEx does not root the delegate on its own
    private bool _hookSuspendedForPause;

    public bool HookActive => _hookHandle != 0;
    public bool Running => _running;

    public TaskbarLock(HostContext ctx, Dispatcher dispatcher)
    {
        _ctx = ctx;
        _log = ctx.Log;
        _dispatcher = dispatcher;
        _decideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        _watchdogTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => WatchdogTick(), dispatcher);
    }

    /// <summary>Turn win-only mode on: auto-hide the taskbar, cover its hover strip, install the hook.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _shielded = false;
        _forcedVisible = false;
        _awaySince = null;
        _lastTray = FindTray();
        SetAutoHide(true);
        ShowShields();
        InstallHook();
        _decideTimer.Start();
        _watchdogTimer.Start();
        _log.Info("taskbar-lock: started (win-only)");
    }

    /// <summary>Turn win-only mode off: drop the hook, close the shields. Auto-hide itself is left to
    /// the caller (TaskbarStyler.Apply/Reset decide the target auto-hide state for the next mode).</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _decideTimer.Stop();
        _watchdogTimer.Stop();
        RemoveHook();
        CloseShields();
        _log.Info("taskbar-lock: stopped");
    }

    public System.Text.Json.Nodes.JsonObject Status() => new()
    {
        ["mode"] = _running ? "win-only" : "off",
        ["hookActive"] = HookActive,
        ["shields"] = _shields.Count,
        ["trayVisible"] = _running && !_shielded,
    };

    // ---- decide: show or cover -----------------------------------------------------------------

    private void Tick()
    {
        if (_disposed || !_running) return;
        try
        {
            if (CheckAppPause()) return; // hook (and the rest of the decision) is suspended while paused

            var startOpen = IsStartMenuOpen();
            PInvoke.GetCursorPos(out var pt);
            var overTray = IsOverTray(pt);
            var shouldShow = startOpen || overTray || _forcedVisible;

            if (shouldShow)
            {
                _awaySince = null;
                if (!startOpen && !overTray)
                {
                    // Win was tapped: stay visible until Start opens, the mouse reaches it, or the
                    // 700ms-away debounce below fires — whichever happens first.
                }
                if (_shielded) HideShields();
            }
            else
            {
                _forcedVisible = false;
                _awaySince ??= DateTime.UtcNow;
                if (!_shielded && DateTime.UtcNow - _awaySince.Value >= AwayDebounce)
                {
                    SetAutoHide(true);
                    ShowShields();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("taskbar-lock tick", ex);
        }
    }

    /// <summary>Returns true while the wallpaper is paused (SetUserPause, fullscreen, locked session —
    /// see WallpaperEngine.CheckPause), reported through every monitor's IHostApp.Monitors.paused. The
    /// hook is removed for the duration, matching the owner's condition for D11.</summary>
    private bool CheckAppPause()
    {
        bool paused;
        try
        {
            var monitors = _ctx.App.Monitors;
            paused = monitors.Count > 0 && monitors.All(m => m.paused);
        }
        catch { paused = false; }

        if (paused && !_hookSuspendedForPause)
        {
            _hookSuspendedForPause = true;
            RemoveHook();
            _log.Info("taskbar-lock: hook removed (app paused)");
        }
        else if (!paused && _hookSuspendedForPause)
        {
            _hookSuspendedForPause = false;
            InstallHook();
            _log.Info("taskbar-lock: hook reinstalled (app resumed)");
        }
        return paused;
    }

    private void WatchdogTick()
    {
        if (_disposed || !_running) return;
        try
        {
            foreach (var s in _shields) s.ReassertTopmost();

            var tray = FindTray();
            if (tray != _lastTray)
            {
                _lastTray = tray;
                _reapplyAt = DateTime.UtcNow + ReapplyDelay;
                _log.Info("taskbar-lock: Shell_TrayWnd handle changed (explorer restarted?), re-applying in 2s");
            }
            if (_reapplyAt is { } at && DateTime.UtcNow >= at)
            {
                _reapplyAt = null;
                Reapply();
            }
        }
        catch (Exception ex)
        {
            _log.Error("taskbar-lock watchdog", ex);
        }
    }

    private void Reapply()
    {
        if (!_running) return;
        CloseShields();
        SetAutoHide(true);
        ShowShields();
        _lastTray = FindTray();
        _log.Info("taskbar-lock: re-applied after explorer restart");
    }

    // ---- Win-tap detection -----------------------------------------------------------------------

    private void ShowNow()
    {
        _forcedVisible = true;
        _awaySince = null;
        HideShields();
        SetAutoHide(false); // briefly let the panel slide out; Tick() re-hides it once Start closes and the mouse leaves
    }

    private unsafe LRESULT HookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var kb = *(KBDLLHOOKSTRUCT*)lParam.Value;
                var vk = kb.vkCode;
                var msg = (uint)wParam.Value;
                var isWin = vk == VK_LWIN || vk == VK_RWIN;
                var isDown = msg == PInvoke.WM_KEYDOWN || msg == PInvoke.WM_SYSKEYDOWN;
                var isUp = msg == PInvoke.WM_KEYUP || msg == PInvoke.WM_SYSKEYUP;

                if (isWin && isDown)
                {
                    if (!_winDown) { _winDown = true; _winAlone = true; _winDownAt = DateTime.UtcNow; }
                }
                else if (isWin && isUp)
                {
                    if (_winDown && _winAlone && DateTime.UtcNow - _winDownAt < WinTapWindow)
                    {
                        _dispatcher.BeginInvoke(ShowNow);
                    }
                    _winDown = false;
                    _winAlone = false;
                }
                else if (!isWin && isDown && _winDown)
                {
                    _winAlone = false; // some other key went down while Win was held: not a bare tap
                }
            }
            catch { /* never let a hook exception take Explorer's input pipeline down with it */ }
        }
        return PInvoke.CallNextHookEx(default, nCode, wParam, lParam);
    }

    private unsafe void InstallHook()
    {
        if (_hookHandle != 0) return;
        _hookProc = HookCallback;
        var hMod = PInvoke.GetModuleHandle((string?)null);
        var handle = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _hookProc, hMod, 0);
        _hookHandle = (nint)handle.Value;
        if (_hookHandle == 0) _log.Warn("taskbar-lock: SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
    }

    private void RemoveHook()
    {
        if (_hookHandle == 0) return;
        try { PInvoke.UnhookWindowsHookEx(new HHOOK(_hookHandle)); } catch { }
        _hookHandle = 0;
        _hookProc = null;
        _winDown = false;
        _winAlone = false;
    }

    // ---- shields ---------------------------------------------------------------------------------

    private void ShowShields()
    {
        if (_shielded) return;
        CloseShields();
        var color = _ctx.Config.App.Theme.Palette.TryGetValue("bgPage", out var c) ? c : "#0B0B0B";
        var secondary = _ctx.Config.App.Taskbar.Secondary;
        var monitors = DisplayMonitors.Enumerate();
        if (!secondary) monitors = monitors.Where(m => m.Primary).ToList();
        foreach (var m in monitors)
        {
            try { _shields.Add(new ShieldWindow(m, color)); }
            catch (Exception ex) { _log.Error("taskbar-lock shield " + m.Id, ex); }
        }
        _shielded = true;
    }

    private void HideShields()
    {
        if (!_shielded && _shields.Count == 0) return;
        CloseShields();
        _shielded = false;
    }

    private void CloseShields()
    {
        foreach (var s in _shields) s.Close();
        _shields.Clear();
    }

    // ---- window/state probes ----------------------------------------------------------------------

    private static HWND FindTray() => PInvoke.FindWindow("Shell_TrayWnd", null);

    private static unsafe bool IsCloaked(HWND hwnd)
    {
        int cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return hr.Succeeded && cloaked != 0;
    }

    /// <summary>The Start menu on Windows 11 is a Windows.UI.Core.CoreWindow owned by
    /// StartMenuExperienceHost.exe; matched by owning process rather than window title so it works
    /// regardless of Windows display language.</summary>
    private static unsafe bool IsStartMenuOpen()
    {
        var found = false;
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (DesktopHost.ClassName(hwnd) != "Windows.UI.Core.CoreWindow") return true;
            if (!PInvoke.IsWindowVisible(hwnd) || IsCloaked(hwnd)) return true;
            PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                if (!string.Equals(proc.ProcessName, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }
            found = true;
            return false; // stop enumerating
        }, (LPARAM)0);
        return found;
    }

    private bool IsOverTray(System.Drawing.Point pt)
    {
        foreach (var hwnd in TrayWindows())
        {
            if (hwnd == HWND.Null) continue;
            if (!PInvoke.GetWindowRect(hwnd, out var r)) continue;
            if (pt.X >= r.left && pt.X < r.right && pt.Y >= r.top && pt.Y < r.bottom) return true;
        }
        return false;
    }

    private IEnumerable<HWND> TrayWindows()
    {
        var primary = FindTray();
        if (primary != HWND.Null) yield return primary;
        if (!_ctx.Config.App.Taskbar.Secondary) yield break;
        var h = PInvoke.FindWindowEx(HWND.Null, HWND.Null, "Shell_SecondaryTrayWnd", null);
        while (h != HWND.Null)
        {
            yield return h;
            h = PInvoke.FindWindowEx(HWND.Null, h, "Shell_SecondaryTrayWnd", null);
        }
    }

    private static unsafe void SetAutoHide(bool on)
    {
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        d.hWnd = FindTray();
        d.lParam = (LPARAM)(nint)(on ? PInvoke.ABS_AUTOHIDE : 0u);
        PInvoke.SHAppBarMessage(PInvoke.ABM_SETSTATE, &d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>A 2px, click-opaque, always-on-top strip over one monitor's taskbar hover zone. It
    /// paints the wallpaper's own base colour so it reads as "empty desktop edge", not a visible bar.</summary>
    private sealed class ShieldWindow
    {
        private readonly Window _window;
        private HWND _hwnd;

        public ShieldWindow(MonitorInfo mon, string colorHex)
        {
            var (r, g, b) = TaskbarStyler.ParseColor(colorHex);
            var scale = mon.Scale <= 0 ? 1.0 : mon.Scale;
            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                Left = mon.Left / scale,
                Top = (mon.Top + mon.Height - ShieldHeight) / scale,
                Width = Math.Max(1, mon.Width / scale),
                Height = Math.Max(1, ShieldHeight / scale),
            };
            _window.SourceInitialized += (_, _) =>
            {
                _hwnd = (HWND)new WindowInteropHelper(_window).Handle;
                var ex = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                PInvoke.SetWindowPos(_hwnd, new HWND(-1), mon.Left, mon.Top + mon.Height - ShieldHeight, mon.Width, ShieldHeight,
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            };
            _window.Show();
        }

        /// <summary>Explorer keeps re-topmosting Shell_TrayWnd on its own; called from the 1s watchdog
        /// so the shield stays above it rather than the other way around.</summary>
        public void ReassertTopmost()
        {
            if (_hwnd == HWND.Null) return;
            PInvoke.SetWindowPos(_hwnd, new HWND(-1), 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
        }

        public void Close()
        {
            try { _window.Close(); } catch { }
        }
    }
}
