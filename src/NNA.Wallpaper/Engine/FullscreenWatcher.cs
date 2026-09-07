using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Shell;

namespace NNA.Wallpaper.Engine;

/// <summary>
/// Decides which monitors are covered by a fullscreen application so their wallpaper can be paused.
/// Global: SHQueryUserNotificationState says a D3D fullscreen app or presentation is running.
/// Per monitor: the foreground window (not part of the shell) covers the whole monitor rectangle.
/// </summary>
public static class FullscreenWatcher
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ApplicationFrameWindow",
        "Windows.UI.Input.InputSite.WindowClass", "SearchHost", "StartMenuSizingFrame",
    };

    public static bool GlobalFullscreen()
    {
        try
        {
            if (PInvoke.SHQueryUserNotificationState(out var state).Succeeded)
            {
                return state is QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                    or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Returns the foreground window rectangle when it is a normal app window; null for shell windows.</summary>
    public static unsafe RECT? ForegroundAppRect(out HWND foreground)
    {
        foreground = PInvoke.GetForegroundWindow();
        if (foreground == HWND.Null || !PInvoke.IsWindowVisible(foreground) || PInvoke.IsIconic(foreground)) return null;
        var cls = DesktopHost.ClassName(foreground);
        if (ShellClasses.Contains(cls)) return null;

        RECT rect;
        var hr = PInvoke.DwmGetWindowAttribute(foreground, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT));
        if (hr.Failed && !PInvoke.GetWindowRect(foreground, out rect)) return null;
        return rect;
    }

    public static bool Covers(RECT r, MonitorInfo m, int tolerance = 2)
    {
        return r.left <= m.Left + tolerance && r.top <= m.Top + tolerance
            && r.right >= m.Left + m.Width - tolerance && r.bottom >= m.Top + m.Height - tolerance;
    }
}
