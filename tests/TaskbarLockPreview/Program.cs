using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Taskbar;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.TaskbarLockPreview;

/// <summary>
/// B5 branch B live preview (tests/TaskbarLockPreview, not part of NNA.Wallpaper.sln): runs the
/// real <see cref="TaskbarLock"/> in win-only mode against the machine's real, live
/// Shell_TrayWnd/Shell_SecondaryTrayWnd, then drives the same scenario the owner reported by hand
/// (hover at the bottom edge, Win tap, Esc, Win+E) with real SendInput, measuring
/// GetWindowRect/IsWindowVisible after each step. Independent of any running NNA.Wallpaper
/// instance (see TaskbarLock's own constructor - Log + Dispatcher only) and of port 1618: it never
/// touches HostContext, the config store, or the registry backup TaskbarStyler owns.
///
/// Safety: every mutating step after <see cref="TaskbarLock.Start"/> is inside try/finally. The
/// finally always calls <see cref="TaskbarLock.Stop"/> (which itself calls ShowWindow(SW_SHOWNA)
/// on every tray it hid, drops the keyboard hook, and releases the IAppVisibility COM object),
/// restores the ABM_SETSTATE auto-hide flag captured before Start(), and does one more direct
/// ShowWindow(SW_SHOWNA) as a belt-and-suspenders check if the tray still reads as hidden. It never
/// calls POST /taskbar/restart-explorer - if the tray does not come back, that is reported instead.
///
/// Usage:
///   TaskbarLockPreview.exe [--variant a|b|c] [--dry]
///   --variant  which TrayHideStrategy TaskbarLock runs the live sequence with (default: a, i.e.
///              EdgeOffset - see TaskbarLock.cs's own class remarks: live measurement so far found
///              only "c" (ShowWindow) actually works on the owner's build, "a"/"b" are kept
///              runnable here specifically so that can be re-checked on demand rather than trusted
///              forever).
///   --dry      does not touch the tray, install the keyboard hook, move the cursor, or send any
///              input at all - it only CoCreateInstance(CLSID_AppVisibility)s a throwaway COM
///              object, calls IsLauncherVisible once (read-only), releases it, and prints
///              "IAppVisibility ok, launcherVisible=&lt;bool&gt;". For verifying the build/COM
///              plumbing when live manipulation of the desktop is not allowed (e.g. the owner is
///              at the keyboard right now).
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var dry = args.Contains("--dry", StringComparer.OrdinalIgnoreCase);
        var variant = ParseVariant(args);

        if (dry)
        {
            // No Application/Dispatcher, no TaskbarLock, no window touched at all - just the COM
            // call, which is inherently read-only (IsLauncherVisible does not change anything).
            Environment.Exit(RunDry() ? 0 : 1);
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var paths = Paths.Resolve(null); // owner's real data dir - Log only, nothing else is read/written here
        var log = new Log(paths);
        var dispatcher = Dispatcher.CurrentDispatcher;

        var exitCode = 1;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { exitCode = await RunSequenceAsync(log, dispatcher, variant) ? 0 : 1; }
            catch (Exception ex)
            {
                Console.WriteLine("SEQUENCE ERROR: " + ex);
                exitCode = 1;
            }
            finally { app.Shutdown(); }
        }));

        app.Run();
        Environment.Exit(exitCode);
    }

    private static TrayHideStrategy ParseVariant(string[] args)
    {
        var idx = Array.FindIndex(args, a => string.Equals(a, "--variant", StringComparison.OrdinalIgnoreCase));
        var v = idx >= 0 && idx + 1 < args.Length ? args[idx + 1].Trim().ToLowerInvariant() : "a";
        return v switch
        {
            "a" => TrayHideStrategy.EdgeOffset,
            "b" => TrayHideStrategy.FullyOff,
            "c" => TrayHideStrategy.ShowWindow,
            _ => throw new ArgumentException("--variant must be a, b, or c (got \"" + v + "\")"),
        };
    }

    /// <summary>Zero-side-effect check: creates the real system IAppVisibility COM object, reads
    /// IsLauncherVisible once, releases it. No hook, no window touched, no cursor moved.</summary>
    private static bool RunDry()
    {
        try
        {
            var visible = ProbeIsLauncherVisible(out var ok);
            if (!ok)
            {
                Console.WriteLine("FAIL IAppVisibility: CoCreateInstance(CLSID_AppVisibility) or IsLauncherVisible failed");
                return false;
            }
            Console.WriteLine($"IAppVisibility ok, launcherVisible={visible}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL IAppVisibility: " + ex);
            return false;
        }
    }

    private static async Task<bool> RunSequenceAsync(Log log, Dispatcher dispatcher, TrayHideStrategy variant)
    {
        Console.WriteLine("variant=" + variant);
        var passCount = 0;
        var failCount = 0;
        void Pass(string name, string detail = "") { passCount++; Console.WriteLine("PASS " + name + (detail.Length > 0 ? ": " + detail : "")); }
        void Fail(string name, string detail = "") { failCount++; Console.WriteLine("FAIL " + name + (detail.Length > 0 ? ": " + detail : "")); }

        var tray = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (tray == HWND.Null || !PInvoke.GetWindowRect(tray, out var baseRect))
        {
            Console.WriteLine("FAIL setup: Shell_TrayWnd not found or GetWindowRect failed");
            return false;
        }
        var screenBottom = baseRect.bottom;
        var centerX = (baseRect.left + baseRect.right) / 2;
        var originalAutoHide = GetAutoHide();
        Console.WriteLine($"baseline: tray=({baseRect.left},{baseRect.top})-({baseRect.right},{baseRect.bottom}) screenBottom={screenBottom} autoHide={originalAutoHide}");

        var trayLock = new TaskbarLock(log, dispatcher, secondaryEnabled: () => true, isPaused: () => false, hideStrategy: variant);
        try
        {
            trayLock.Start();
            await Task.Delay(300); // one watchdog tick (75ms) plus slack so Hide() has definitely run

            // ---- step 1: hover 3s at the bottom edge -> tray must stay hidden ----
            Native.SetCursorPos(centerX, 200);
            await Task.Delay(200);
            Native.SetCursorPos(centerX, screenBottom - 1);
            await Task.Delay(3000);
            var hidden1 = IsHidden(tray, screenBottom);
            if (hidden1) Pass("hover 3s at the bottom edge keeps the tray hidden");
            else Fail("hover 3s at the bottom edge keeps the tray hidden", RectSummary(tray));

            // ---- step 2: Win tap -> tray shown + IsLauncherVisible true within 500ms ----
            Native.SetCursorPos(centerX, 200);
            await Task.Delay(200);
            Native.TapWin();
            await Task.Delay(500);
            var shown2 = !IsHidden(tray, screenBottom);
            var startVisible = ProbeIsLauncherVisible(out _);
            if (shown2 && startVisible) Pass("Win tap shows the tray and Start within 500ms");
            else Fail("Win tap shows the tray and Start within 500ms", $"shown={shown2} launcherVisible={startVisible} {RectSummary(tray)}");

            // ---- step 3: Esc closes Start -> tray hides again within 2s ----
            Native.TapEsc();
            await Task.Delay(2000);
            var hidden3 = IsHidden(tray, screenBottom);
            if (hidden3) Pass("Esc hides the tray again within 2s");
            else Fail("Esc hides the tray again within 2s", RectSummary(tray));

            // ---- step 4: Win+E opens Explorer, tray stays hidden ----
            Native.ComboWinE();
            await Task.Delay(900);
            var cabinet = Native.FindWindowRaw("CabinetWClass", null);
            var hidden4 = IsHidden(tray, screenBottom);
            if (cabinet != IntPtr.Zero && hidden4) Pass("Win+E opens Explorer without showing the tray");
            else Fail("Win+E opens Explorer without showing the tray", $"explorer={cabinet != IntPtr.Zero} hidden={hidden4} {RectSummary(tray)}");
            if (cabinet != IntPtr.Zero) Native.CloseWindow(cabinet);
            await Task.Delay(500);

            // ---- step 5: hover again -> hidden (make sure closing Explorer did not wake it up) ----
            Native.SetCursorPos(centerX, 200);
            await Task.Delay(200);
            Native.SetCursorPos(centerX, screenBottom - 1);
            await Task.Delay(3000);
            var hidden5 = IsHidden(tray, screenBottom);
            if (hidden5) Pass("second hover 3s keeps the tray hidden");
            else Fail("second hover 3s keeps the tray hidden", RectSummary(tray));
            Native.SetCursorPos(centerX, 200);
        }
        finally
        {
            trayLock.Stop();
            trayLock.Dispose();
            await Task.Delay(300);
            SetAutoHide(originalAutoHide);
            if (!PInvoke.IsWindowVisible(tray))
            {
                Console.WriteLine("restore: tray still not visible after Stop() - calling ShowWindow(SW_SHOWNA) directly");
                PInvoke.ShowWindow(tray, SHOW_WINDOW_CMD.SW_SHOWNA);
            }
            Console.WriteLine("after: lock=" + trayLock.Status().ToJsonString());
            Console.WriteLine($"final tray visible={PInvoke.IsWindowVisible(tray)} {RectSummary(tray)} autoHide={GetAutoHide()}");
        }

        Console.WriteLine("---");
        Console.WriteLine($"PASS={passCount} FAIL={failCount}");
        return failCount == 0;
    }

    private static bool IsHidden(HWND tray, int screenBottom)
    {
        if (!PInvoke.IsWindowVisible(tray)) return true;
        if (!PInvoke.GetWindowRect(tray, out var r)) return true;
        return r.top >= screenBottom - 4;
    }

    private static string RectSummary(HWND tray)
    {
        var visible = PInvoke.IsWindowVisible(tray);
        var rectOk = PInvoke.GetWindowRect(tray, out var r);
        return "visible=" + visible + " rect=" + (rectOk ? $"({r.left},{r.top})-({r.right},{r.bottom})" : "(GetWindowRect failed)");
    }

    /// <summary>Independent CoCreateInstance(CLSID_AppVisibility) call, deliberately separate from
    /// TaskbarLock's own instance, so the probe verifies the actual system state rather than
    /// trusting the class under test to report on itself. Purely read-only: creates the COM
    /// object, calls IsLauncherVisible, releases it - no window is touched, nothing is moved.
    /// <paramref name="ok"/> is false if the COM object could not be created at all (e.g. the API
    /// is missing on this Windows build) as opposed to legitimately reporting "not visible".</summary>
    private static unsafe bool ProbeIsLauncherVisible(out bool ok)
    {
        var clsid = new Guid("7E5FE3D9-985F-4908-91F9-EE19F9FD1514");
        var iid = typeof(IAppVisibility).GUID;
        try
        {
            var hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, out var obj);
            if (hr.Failed || obj is not IAppVisibility av) { ok = false; return false; }
            try { av.IsLauncherVisible(out var visible); ok = true; return visible; }
            finally { Marshal.FinalReleaseComObject(av); }
        }
        catch { ok = false; return false; }
    }

    private static unsafe bool GetAutoHide()
    {
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        var state = PInvoke.SHAppBarMessage(PInvoke.ABM_GETSTATE, &d);
        return ((uint)state & PInvoke.ABS_AUTOHIDE) != 0;
    }

    private static unsafe void SetAutoHide(bool on)
    {
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        d.hWnd = PInvoke.FindWindow("Shell_TrayWnd", null);
        d.lParam = (LPARAM)(nint)(on ? PInvoke.ABS_AUTOHIDE : 0u);
        PInvoke.SHAppBarMessage(PInvoke.ABM_SETSTATE, &d);
    }
}

/// <summary>Bare Win32 SendInput/FindWindow plumbing for the parts NNA.Wallpaper's own
/// NativeMethods.txt does not generate (it never needs to synthesize input) - same pattern as
/// tests/TopBarPreview/Program.cs's own Native class.</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowRaw(string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_E = 0x45;
    private const uint WM_CLOSE = 0x0010;

    private static INPUT KeyDown(ushort vk) => new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } };
    private static INPUT KeyUp(ushort vk) => new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } };

    public static void TapWin()
    {
        SendInput(1, new[] { KeyDown(VK_LWIN) }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(120);
        SendInput(1, new[] { KeyUp(VK_LWIN) }, Marshal.SizeOf<INPUT>());
    }

    public static void TapEsc()
    {
        SendInput(1, new[] { KeyDown(VK_ESCAPE) }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(80);
        SendInput(1, new[] { KeyUp(VK_ESCAPE) }, Marshal.SizeOf<INPUT>());
    }

    public static void ComboWinE()
    {
        SendInput(1, new[] { KeyDown(VK_LWIN) }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        SendInput(1, new[] { KeyDown(VK_E) }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        SendInput(1, new[] { KeyUp(VK_E) }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        SendInput(1, new[] { KeyUp(VK_LWIN) }, Marshal.SizeOf<INPUT>());
    }

    public static void CloseWindow(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}
