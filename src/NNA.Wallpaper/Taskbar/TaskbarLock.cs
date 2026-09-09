using System.Runtime.InteropServices;
using System.Windows.Threading;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Taskbar;

/// <summary>The three ways considered for holding Shell_TrayWnd off-screen (see 06-OPEN.md B5).
/// "a" and "b" are SetWindowPos-based (what autohide's own slide animation would look like if we
/// drove it ourselves); "c" is ShowWindow, which does not move the window at all - it just stops
/// drawing it. Kept as an enum, not a hard-coded choice, so <c>tests/TaskbarLockPreview
/// --variant</c> can re-measure this on a different Windows build.</summary>
public enum TrayHideStrategy
{
    /// <summary>(a) SetWindowPos to 2px above the screen bottom - the same rect autohide's own
    /// "peeking" state sits at.</summary>
    EdgeOffset,
    /// <summary>(b) SetWindowPos fully past the screen bottom (y = screen height).</summary>
    FullyOff,
    /// <summary>(c) ShowWindow(SW_HIDE) / ShowWindow(SW_SHOWNA) - does not reposition the window,
    /// just stops it from being drawn/hit-tested.</summary>
    ShowWindow,
}

/// <summary>
/// Owner decision D11: "win-only" taskbar mode. Branch B (H:\night-runs\nna-wallpaper-2\06-OPEN.md,
/// B5): the first implementation covered the taskbar's hover strip with a thin always-on-top
/// "shield" window and relied on ABM_SETSTATE autohide plus IsWindowVisible(Start) to know when to
/// drop it. On Windows 11 build 26200 (XAML taskbar) that failed both ways: the panel slides out on
/// hover regardless of the shield (it does not detect the edge through "what window is under the
/// cursor"), and the reverse hide never fired (StartMenuExperienceHost's CoreWindow reads as still
/// "visible" after Esc).
///
/// Branch B instead:
///  - Keeps the tray fully working but off-screen with a 75ms watchdog, using one of three
///    strategies (<see cref="TrayHideStrategy"/>) so the choice can be measured rather than
///    assumed. A first live measurement round (before this class had the switch - see
///    docs/TASKBAR.md for the numbers) found <c>SetWindowPos</c> (both "2px at the edge" and
///    "fully past the bottom") silently ignored by the modern taskbar host on the owner's build
///    26200 - the call returns TRUE but the window rect never moves, not even for a single frame
///    over 500ms of polling - while <c>ShowWindow(SW_HIDE)</c>/<c>(SW_SHOWNA)</c> worked
///    immediately and held for as long as tested. That result is not hard-coded here: the owner
///    may be on a different build another time, so the strategy stays a constructor parameter and
///    <c>tests/TaskbarLockPreview --variant a|b|c</c> exists to re-measure it live. Production
///    wiring (TaskbarStyler.cs) passes the strategy that measurement actually recommends.
///  - Detects the Start menu through the documented <see cref="IAppVisibility"/> COM API
///    (IsLauncherVisible) instead of guessing from CoreWindow's IsWindowVisible state.
///
/// This still needs the low-level keyboard hook (WH_KEYBOARD_LL) to notice a bare Win tap (down,
/// then up, with no other key pressed meanwhile) as opposed to Win+E, Win+D, Win+1, etc., which
/// must NOT show the taskbar. The hook only ever looks at the virtual-key code of the event it
/// receives, to decide "is this Win alone" - it never stores a key code anywhere beyond the
/// one-shot flags below, never writes anything to disk or the log, and always calls
/// <see cref="PInvoke.CallNextHookEx"/> so it never blocks a keystroke. It is removed
/// (<see cref="Stop"/>) whenever the mode changes away from win-only, whenever the wallpaper is
/// paused (see <see cref="Tick"/>), and on shutdown/Reset.
///
/// Kept independent of the host process: the constructor only needs a <see cref="Log"/>, a WPF
/// <see cref="Dispatcher"/>, and two small probes (secondary-monitor taskbars enabled? is the
/// wallpaper paused?) instead of a full <c>HostContext</c>, so <c>tests/TaskbarLockPreview</c> can
/// run one against the real Shell_TrayWnd without any of the host's config/API plumbing.
/// </summary>
public sealed class TaskbarLock : IDisposable
{
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private static readonly TimeSpan WinTapWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AwayDebounce = TimeSpan.FromMilliseconds(700);
    private static readonly Guid ClsidAppVisibility = new("7E5FE3D9-985F-4908-91F9-EE19F9FD1514");

    private readonly Log _log;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _secondaryEnabled;
    private readonly Func<bool> _isPaused;
    private readonly TrayHideStrategy _hideStrategy;
    private readonly DispatcherTimer _watchdogTimer;
    /// <summary>Each tray's normal (shown) rect, captured the first time we touch it - only used by
    /// the EdgeOffset/FullyOff strategies (ShowWindow never moves the window, so it needs no
    /// remembered rect to restore).</summary>
    private readonly Dictionary<nint, RECT> _normalRects = new();

    private bool _running;
    private bool _disposed;
    /// <summary>Whether the tray is currently shown as far as this class knows - not re-queried
    /// from Windows every tick, since the decision only needs to run the show/hide call on a state
    /// change, not every 75ms.</summary>
    private bool _shown;
    private bool _forcedVisible;
    private DateTime? _awaySince;
    private HWND _lastTray;

    private IAppVisibility? _appVisibility;
    private bool _appVisibilityFailed;

    // ---- WH_KEYBOARD_LL state: a flag and a timestamp, nothing else is ever kept about a keystroke ----
    private bool _winDown;
    private bool _winAlone;
    private DateTime _winDownAt;

    private nint _hookHandle;
    private HOOKPROC? _hookProc; // kept alive: SetWindowsHookEx does not root the delegate on its own
    private bool _hookSuspendedForPause;

    public bool HookActive => _hookHandle != 0;
    public bool Running => _running;

    /// <param name="secondaryEnabled">True while Shell_SecondaryTrayWnd instances should also be
    /// hidden/shown (mirrors app.taskbar.secondary). Queried live, not cached, so a config change
    /// takes effect on the next tick without restarting the lock.</param>
    /// <param name="isPaused">True while the wallpaper is paused (fullscreen app, locked session,
    /// user pause) - the hook is dropped for as long as this returns true (owner's D11 condition).</param>
    /// <param name="hideStrategy">Which of the three off-screen strategies to use (see
    /// <see cref="TrayHideStrategy"/>). Defaults to the one live measurement recommended
    /// (ShowWindow) - callers that want to re-measure (tests/TaskbarLockPreview) pass one
    /// explicitly.</param>
    public TaskbarLock(Log log, Dispatcher dispatcher, Func<bool> secondaryEnabled, Func<bool> isPaused,
        TrayHideStrategy hideStrategy = TrayHideStrategy.ShowWindow)
    {
        _log = log;
        _dispatcher = dispatcher;
        _secondaryEnabled = secondaryEnabled;
        _isPaused = isPaused;
        _hideStrategy = hideStrategy;
        _watchdogTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(75), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    /// <summary>Turn win-only mode on: hide the tray (ShowWindow), install the hook and watchdog.
    /// Auto-hide (ABM_SETSTATE) is left as the caller already has it - win-only rides on top of it,
    /// it does not depend on it (see class remarks: the actual hide/show is ShowWindow, not
    /// autohide's own slide animation).</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _shown = true; // Windows' own idea of the tray right now; Hide() below corrects it
        _forcedVisible = false;
        _awaySince = null;
        _lastTray = FindTray();
        CreateAppVisibility();
        Hide();
        InstallHook();
        _watchdogTimer.Start();
        _log.Info("taskbar-lock: started (win-only, branch B: ShowWindow + IAppVisibility)");
    }

    /// <summary>Turn win-only mode off: drop the hook and watchdog, make sure the tray is left
    /// shown and working for whatever mode comes next (TaskbarStyler.Apply/Reset then set the
    /// auto-hide flag they actually want).</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _watchdogTimer.Stop();
        RemoveHook();
        if (!_shown) Show();
        ReleaseAppVisibility();
        _normalRects.Clear();
        _log.Info("taskbar-lock: stopped");
    }

    public System.Text.Json.Nodes.JsonObject Status() => new()
    {
        ["mode"] = _running ? "win-only" : "off",
        ["hookActive"] = HookActive,
        ["shown"] = !_running || _shown,
        ["appVisibilityActive"] = _appVisibility is not null,
    };

    // ---- decide: show or hide, 75ms watchdog ------------------------------------------------------

    private void Tick()
    {
        if (_disposed || !_running) return;
        try
        {
            if (CheckAppPause())
            {
                if (_shown) Hide();
                return; // hook (and the rest of the decision) is suspended while paused
            }

            var tray = FindTray();
            if (tray != HWND.Null && tray != _lastTray)
            {
                _log.Info("taskbar-lock: Shell_TrayWnd handle changed (explorer restarted?), re-applying");
                _lastTray = tray;
                _shown = true; // unknown state on the new handle: force a real ShowWindow call below
                _forcedVisible = false;
                _awaySince = null;
            }

            var startOpen = IsStartMenuVisible();
            PInvoke.GetCursorPos(out var pt);
            // Only matters once already shown: while hidden the tray sits at its normal screen
            // rect (ShowWindow does not move it), so hovering the empty desktop edge must NOT
            // count as "over the tray" - that was exactly branch A's hover-reveal bug.
            var overTray = _shown && IsOverTray(pt);
            var keepVisible = startOpen || _forcedVisible || overTray;

            // Focus moved to an ordinary window (not Start, not the tray itself) while the mouse
            // is not on the tray: treat that as "the owner is done with it" even if a stale
            // forced-visible/startOpen read would otherwise keep it up.
            if (keepVisible && !overTray && !startOpen && ForegroundIsOrdinaryWindow()) keepVisible = false;

            if (keepVisible)
            {
                _awaySince = null;
                if (!_shown) Show();
            }
            else
            {
                _forcedVisible = false;
                _awaySince ??= DateTime.UtcNow;
                if (_shown && DateTime.UtcNow - _awaySince.Value >= AwayDebounce) Hide();
            }
        }
        catch (Exception ex)
        {
            _log.Error("taskbar-lock tick", ex);
        }
    }

    /// <summary>Returns true while the wallpaper is paused, per the caller-supplied probe. The hook
    /// is removed for the duration, matching the owner's condition for D11.</summary>
    private bool CheckAppPause()
    {
        bool paused;
        try { paused = _isPaused(); }
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

    // ---- Win-tap detection -----------------------------------------------------------------------

    private void ShowNow()
    {
        _forcedVisible = true;
        _awaySince = null;
        if (!_shown) Show();
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

    // ---- IAppVisibility (documented, CLSID_AppVisibility / IID_IAppVisibility) --------------------

    private unsafe void CreateAppVisibility()
    {
        if (_appVisibility is not null || _appVisibilityFailed) return;
        try
        {
            var clsid = ClsidAppVisibility;
            var iid = typeof(IAppVisibility).GUID;
            var hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, out var obj);
            if (hr.Failed || obj is not IAppVisibility av)
            {
                _appVisibilityFailed = true;
                _log.Warn("taskbar-lock: CoCreateInstance(CLSID_AppVisibility) failed: hr=" + hr.Value);
                return;
            }
            _appVisibility = av;
        }
        catch (Exception ex)
        {
            _appVisibilityFailed = true;
            _log.Error("taskbar-lock: IAppVisibility create failed", ex);
        }
    }

    private void ReleaseAppVisibility()
    {
        if (_appVisibility is null) return;
        try { Marshal.FinalReleaseComObject(_appVisibility); } catch { }
        _appVisibility = null;
        _appVisibilityFailed = false; // allow a fresh attempt next Start()
    }

    /// <summary>True while the Start menu (or Search, Widgets - anything IAppVisibility counts as
    /// "the launcher") is open. Falls back to false (never show) if the COM object could not be
    /// created - the Win-tap hook still shows the tray directly, it just will not stay up past the
    /// 700ms away-debounce once Start itself is not tracked.</summary>
    private bool IsStartMenuVisible()
    {
        if (_appVisibility is null) return false;
        try
        {
            _appVisibility.IsLauncherVisible(out var visible);
            return visible;
        }
        catch (Exception ex)
        {
            _log.Warn("taskbar-lock: IsLauncherVisible failed: " + ex.Message);
            return false;
        }
    }

    // ---- window/state probes ----------------------------------------------------------------------

    private static HWND FindTray() => PInvoke.FindWindow("Shell_TrayWnd", null);

    /// <summary>True when the foreground window is an ordinary app window - not the Start menu's
    /// CoreWindow, not the tray itself (e.g. its own context menu). Used only to force-hide when
    /// focus visibly moved away from both while the mouse also is not on the tray.</summary>
    private static unsafe bool ForegroundIsOrdinaryWindow()
    {
        var fg = PInvoke.GetForegroundWindow();
        if (fg == HWND.Null) return false;
        var cls = DesktopHost.ClassName(fg);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (cls == "Windows.UI.Core.CoreWindow")
        {
            PInvoke.GetWindowThreadProcessId(fg, out var pid);
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                if (string.Equals(proc.ProcessName, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { return false; }
        }
        return true;
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
        if (!_secondaryEnabled()) yield break;
        var h = PInvoke.FindWindowEx(HWND.Null, HWND.Null, "Shell_SecondaryTrayWnd", null);
        while (h != HWND.Null)
        {
            yield return h;
            h = PInvoke.FindWindowEx(HWND.Null, h, "Shell_SecondaryTrayWnd", null);
        }
    }

    // ---- the actual hide/show: one of the three TrayHideStrategy variants ------------------------
    // See docs/TASKBAR.md for the live measurement: on the owner's build 26200, SetWindowPos on
    // Shell_TrayWnd (EdgeOffset/FullyOff) was a silent no-op - the call returns TRUE but the rect
    // never moves, not even for one frame over 500ms of polling - while ShowWindow(SW_HIDE)/
    // (SW_SHOWNA) worked immediately and held for as long as tested. EdgeOffset/FullyOff are kept
    // implemented (not deleted) so a re-measurement on a different build has something to compare
    // ShowWindow against; they should not be assumed to work without re-checking docs/TASKBAR.md's
    // numbers for whatever build is running.

    private void CaptureNormalRect(HWND h)
    {
        if (_normalRects.ContainsKey((nint)h)) return;
        if (PInvoke.GetWindowRect(h, out var r)) _normalRects[(nint)h] = r;
    }

    private void Show()
    {
        foreach (var h in TrayWindows())
        {
            if (h == HWND.Null) continue;
            switch (_hideStrategy)
            {
                case TrayHideStrategy.EdgeOffset:
                case TrayHideStrategy.FullyOff:
                    if (_normalRects.TryGetValue((nint)h, out var r))
                    {
                        PInvoke.SetWindowPos(h, new HWND(-1), r.left, r.top, r.right - r.left, r.bottom - r.top,
                            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                    }
                    // Belt-and-suspenders: if a previous run (or a mode switch mid-session) left the
                    // window ShowWindow-hidden, this brings it back regardless of strategy.
                    PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_SHOWNA);
                    break;
                case TrayHideStrategy.ShowWindow:
                default:
                    PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_SHOWNA);
                    break;
            }
        }
        _shown = true;
    }

    private void Hide()
    {
        foreach (var h in TrayWindows())
        {
            if (h == HWND.Null) continue;
            CaptureNormalRect(h);
            switch (_hideStrategy)
            {
                case TrayHideStrategy.EdgeOffset:
                    if (_normalRects.TryGetValue((nint)h, out var r1))
                    {
                        var height = r1.bottom - r1.top;
                        PInvoke.SetWindowPos(h, new HWND(-1), r1.left, r1.bottom - 2, r1.right - r1.left, height,
                            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                    }
                    break;
                case TrayHideStrategy.FullyOff:
                    if (_normalRects.TryGetValue((nint)h, out var r2))
                    {
                        var height = r2.bottom - r2.top;
                        PInvoke.SetWindowPos(h, new HWND(-1), r2.left, r2.bottom, r2.right - r2.left, height,
                            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                    }
                    break;
                case TrayHideStrategy.ShowWindow:
                default:
                    PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_HIDE);
                    break;
            }
        }
        _shown = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
