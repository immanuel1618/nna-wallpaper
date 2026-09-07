using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Brings a window (possibly minimized, possibly belonging to another process) to the
/// foreground from a background process. Windows normally refuses <c>SetForegroundWindow</c>
/// from a process that is not itself in the foreground, so this tries several well-known
/// workarounds in order and logs which one actually worked (or that none did).
/// </summary>
internal static class WindowActivator
{
    /// <summary>Restores/raises <paramref name="hwnd"/> and tries to make it the foreground window.</summary>
    public static bool Activate(HWND hwnd, Log log)
    {
        if (hwnd == default)
        {
            log.Warn("window activate: null hwnd");
            return false;
        }

        if (PInvoke.IsIconic(hwnd))
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        // 1) the simple way — works when our own process already owns the foreground window
        //    (e.g. this call originates from a UI action) or when Windows otherwise permits it.
        PInvoke.SetForegroundWindow(hwnd);
        if (Verify(hwnd, log, "direct")) return true;

        // 2) classic trick: attach our input queue to the current foreground window's thread,
        //    which is allowed to set the foreground window, then bring ours to the top instead.
        uint currentThread = PInvoke.GetCurrentThreadId();
        HWND fg = PInvoke.GetForegroundWindow();
        uint fgThread = fg == default ? 0 : PInvoke.GetWindowThreadProcessId(fg, out _);

        bool attached = false;
        try
        {
            if (fgThread != 0 && fgThread != currentThread)
            {
                attached = PInvoke.AttachThreadInput(currentThread, fgThread, true);
            }
            PInvoke.BringWindowToTop(hwnd);
            PInvoke.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) PInvoke.AttachThreadInput(currentThread, fgThread, false);
        }
        if (Verify(hwnd, log, "attach-thread-input")) return true;

        // 3) reserve: tapping Alt resets the "last input was from the user" lock that
        //    SetForegroundWindow otherwise enforces against background callers.
        PInvoke.keybd_event((byte)VIRTUAL_KEY.VK_MENU, 0, default, 0);
        PInvoke.keybd_event((byte)VIRTUAL_KEY.VK_MENU, 0, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP, 0);
        PInvoke.SetForegroundWindow(hwnd);
        if (Verify(hwnd, log, "alt-key-trick")) return true;

        // 4) last resort: SwitchToThisWindow (used internally by the taskbar for alt-tab).
        PInvoke.SwitchToThisWindow(hwnd, true);
        return Verify(hwnd, log, "switch-to-this-window");
    }

    private static bool Verify(HWND hwnd, Log log, string method)
    {
        Thread.Sleep(150);
        bool ok = PInvoke.GetForegroundWindow() == hwnd;
        log.Info($"window activate via {method}: {(ok ? "ok" : "failed")} hwnd={(nint)hwnd}");
        return ok;
    }
}
