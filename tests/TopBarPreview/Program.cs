using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;
using NNA.Wallpaper.TopBar;

namespace NNA.Wallpaper.TopBarPreview;

/// <summary>
/// Stage 8C dev helper (not part of NNA.Wallpaper.sln): shows the real, composition-hosted
/// <see cref="TopBarWindow"/> + <see cref="PopupWindow"/> on a monitor that never overlaps the
/// owner's live top bar — the owner's is <c>monitors:"primary"</c> (\\.\DISPLAY2, 3440x1440), this
/// helper always targets the vertical \\.\DISPLAY1 (1440x2560, screen coords x -1440..0, y
/// -603..1957, see /health on :1618). Pages are served read-only from the owner's already-running
/// instance on :1618 (same pattern as tests/WindowPreview: GET-only — /topbar/, /topbar/popup/,
/// /config — nothing is ever posted/saved), and <see cref="TopBarSettings.ReserveSpace"/> is false
/// so the bar never registers as an AppBar on that monitor (the owner has real windows — VS Code —
/// open there right now; AppBar registration would resize them).
///
/// Drives two click-and-check cycles with real SendInput (clock -&gt; calendar popup, nna -&gt; the
/// NNA menu popup), checking after each: the popup window exists (FindWindow "NNA Wallpaper Popup")
/// and the bar itself never became the foreground window (that was the stage 8C bug: a click
/// anywhere on the bar made "NNA Wallpaper Top Bar" GetForegroundWindow(), even though the window
/// carries WS_EX_NOACTIVATE — see docs/TOPBAR.md "хостинг и фокус"). Saves one screenshot of the
/// area under the bar with the first popup open. Prints one PASS/FAIL line at the end;
/// tests/topbar-live-probe.ps1 runs this and parses that output.
///
/// Usage: TopBarPreview.exe [shotsDir]
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var shotsDir = args.Length > 0 ? args[0] : @"H:\night-runs\nna-wallpaper-2\shots";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var paths = Paths.Resolve(null); // owner's real data dir — read-only use here, like WindowPreview
        var log = new Log(paths);
        var config = new ConfigStore(paths, log);
        config.Load();
        var ctx = new HostContext(paths, config, log, 1618, new NullHostApp());

        // The owner's real vertical monitor (see /health on :1618) — never where the owner's own
        // top bar shows (that one is primary-only, i.e. the 3440x1440 monitor).
        var monitor = new MonitorInfo
        {
            Device = @"\\.\DISPLAY1",
            Left = -1440,
            Top = -603,
            Width = 1440,
            Height = 2560,
            Primary = false,
            Dpi = 96,
        };

        var cfg = new TopBarSettings
        {
            Enabled = true,
            Monitors = "primary",
            Height = 30,
            Style = new SurfaceStyle { Mode = "acrylic", Color = "#0B0B0B", Opacity = 0.6 },
            FontSize = 12,
            AutoHide = false,
            ReserveSpace = false, // never AppBar-reserve here — the owner has real windows on this monitor
            Modules = new System.Collections.Generic.List<TopBarModule>
            {
                new() { Id = "nna", Side = "left" },
                new() { Id = "clock", Side = "center" },
                new() { Id = "control", Side = "right" },
                new() { Id = "volume", Side = "right" },
            },
        };

        var bar = new TopBarWindow(ctx, monitor, cfg);
        PopupWindow? popup = null;
        // Minimal stand-in for TopBarManager.TogglePopup (out of scope to reuse here — it reads the
        // owner's real, saved topbar config/monitor list, not this helper's isolated one): same
        // anchor-centring math, always replaces whatever popup is open.
        bar.PopupRequested += (b, module, anchorXCss, anchorWCss) =>
        {
            var scale = b.Monitor.Scale <= 0 ? 1.0 : b.Monitor.Scale;
            var anchorCenterXPhysical = b.Monitor.Left + (int)Math.Round((anchorXCss + anchorWCss / 2.0) * scale);
            var topYPhysical = b.Monitor.Top + b.HeightPx;
            try { popup?.Close(); } catch { }
            popup = new PopupWindow(ctx, b.Monitor, module, anchorCenterXPhysical, topYPhysical);
            popup.Show();
        };

        var exitCode = 1;
        bar.Loaded += async (_, _) =>
        {
            try
            {
                exitCode = await RunSequenceAsync(bar, monitor, shotsDir) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SEQUENCE ERROR: " + ex);
            }
            finally
            {
                try { popup?.Close(); } catch { }
                try { bar.Close(); } catch { }
                app.Shutdown();
            }
        };

        bar.Show();
        app.Run();
        Environment.Exit(exitCode);
    }

    private static async Task<bool> RunSequenceAsync(TopBarWindow bar, MonitorInfo monitor, string shotsDir)
    {
        // WebView2 environment creation + navigation + module mount.
        await Task.Delay(2500);

        var baselineFg = Native.ForegroundTitle();
        Console.WriteLine("baseline foreground: " + baselineFg);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
        Console.WriteLine("bar hwnd=0x" + hwnd.ToString("X") + " rect=" + Native.WindowRectString(hwnd));

        // How the page mounts whatever modules the owner's real, saved /config returns (this
        // helper's own TopBarSettings.Modules only drives C#-side hosting/sizing, not page content —
        // bar.js fetches /config itself) — normalizeModules() there also injects control/volume/
        // network/battery/layout next to whatever "right" modules already exist. So: do not guess
        // pixel positions from the module list or from "the bar's geometric centre" (that guess is
        // exactly the owner's field bug — see report: their config has "date" ahead of "clock" in
        // the same centered zone, so the true centre pixel lands on "date", not "clock"). Ask the
        // page itself for each element's real screen rect instead — fetched right before each click,
        // not once upfront: live modules to the right of "volume" (media/weather/stats) can change
        // width from WS push updates (now-playing text, live stats) between clicks, which reflows
        // the whole right zone and moves "volume" — a rect fetched too early goes stale.
        var clockRect = await GetRectAsync(bar, ".tb-clock");
        Console.WriteLine("tb-clock rect: " + clockRect);
        if (clockRect is null)
        {
            Console.WriteLine("FAIL: .tb-clock not found on the page");
            return false;
        }

        var (clockX, clockY) = ToScreenPoint(monitor, clockRect.Value);
        Console.WriteLine("click clock at " + clockX + "," + clockY);
        Native.Click(clockX, clockY);
        await Task.Delay(1000);

        var popupFound1 = Native.FindPopup();
        var fg1 = Native.ForegroundTitle();
        Console.WriteLine("popup=" + popupFound1 + " foreground=" + fg1);

        Directory.CreateDirectory(shotsDir);
        var shotPath = Path.Combine(shotsDir, "stage8c-popup-live.png");
        Native.Screenshot(monitor.Left, monitor.Top, monitor.Width, bar.HeightPx + 420, shotPath);
        Console.WriteLine("saved: " + shotPath);

        Native.PressEscape();
        await Task.Delay(600);

        // Second target: "nna" (the owner's real "brand" module, renamed client-side — see above),
        // not "volume". Both open a PopupWindow, but "nna" sits first in the *left* zone
        // (justify-content:flex-start, packed at a fixed small offset from the bar's own edge) so it
        // is never affected by the right zone's overflow — at 1440px this monitor's real right zone
        // (control/volume/network/battery/layout/media/weather/stats, all from the owner's live
        // config) does not fit and "overflow:hidden" clips its *earlier* items, "volume" included,
        // off-screen (confirmed live: getBoundingClientRect() still reports a plausible rect for a
        // clipped element, but elementFromPoint() at that same point resolves to the zone's own empty
        // trailing space, not the module — a pre-existing bar.js/CSS capacity issue on narrow
        // monitors, unrelated to the composition-hosting/popup-wiring fix this stage is about).
        var nnaRect = await GetRectAsync(bar, ".tb-nna");
        Console.WriteLine("tb-nna rect: " + nnaRect);
        if (nnaRect is null)
        {
            Console.WriteLine("FAIL: .tb-nna not found on the page");
            return false;
        }
        var (nnaX, nnaY) = ToScreenPoint(monitor, nnaRect.Value);
        Console.WriteLine("click nna at " + nnaX + "," + nnaY);
        Native.Click(nnaX, nnaY);
        await Task.Delay(1000);

        var popupFound2 = Native.FindPopup();
        var fg2 = Native.ForegroundTitle();
        Console.WriteLine("popup=" + popupFound2 + " foreground=" + fg2);

        Native.PressEscape();
        await Task.Delay(400);

        var barStoleFocus1 = string.Equals(fg1, "NNA Wallpaper Top Bar", StringComparison.Ordinal);
        var barStoleFocus2 = string.Equals(fg2, "NNA Wallpaper Top Bar", StringComparison.Ordinal);
        Console.WriteLine("RESULT calendar: popup=" + popupFound1 + " bar-stole-focus=" + barStoleFocus1);
        Console.WriteLine("RESULT nna: popup=" + popupFound2 + " bar-stole-focus=" + barStoleFocus2);

        var pass = popupFound1 && popupFound2 && !barStoleFocus1 && !barStoleFocus2;
        Console.WriteLine(pass ? "PASS" : "FAIL");
        return pass;
    }

    /// <summary>CSS-px client rect of the first element matching <paramref name="selector"/> in the
    /// bar's own page, or null if not found. CSS px == physical px here (Dpi=96, scale=1).</summary>
    private static async Task<(double X, double Y, double W, double H)?> GetRectAsync(TopBarWindow bar, string selector)
    {
        var js = "(function(){var el=document.querySelector('" + selector + "');" +
                 "if(!el) return null; var r=el.getBoundingClientRect();" +
                 "return {x:r.left,y:r.top,w:r.width,h:r.height};})()";
        var json = await bar.ExecuteScriptAsync(js);
        if (string.IsNullOrEmpty(json) || json == "null") return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble(),
                root.GetProperty("w").GetDouble(), root.GetProperty("h").GetDouble());
    }

    private static (int X, int Y) ToScreenPoint(MonitorInfo monitor, (double X, double Y, double W, double H) rect)
    {
        var scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;
        var x = monitor.Left + (int)Math.Round((rect.X + rect.W / 2.0) * scale);
        var y = monitor.Top + (int)Math.Round((rect.Y + rect.H / 2.0) * scale);
        return (x, y);
    }
}

/// <summary>Bare Win32 SendInput/FindWindow/screenshot plumbing — deliberately not CsWin32 (this
/// helper project has no NativeMethods.txt of its own; the handful of calls needed here are not
/// worth wiring up a second generator config for).</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int count);

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_ESCAPE = 0x1B;

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        var down = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
        var up = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
        SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(60);
        SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void PressEscape()
    {
        var down = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_ESCAPE } };
        var up = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_ESCAPE, dwFlags = KEYEVENTF_KEYUP } };
        SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(40);
        SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
    }

    public static bool FindPopup() => FindWindow(null, "NNA Wallpaper Popup") != IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public static string WindowRectString(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return "(GetWindowRect failed)";
        return "(" + r.Left + "," + r.Top + ")-(" + r.Right + "," + r.Bottom + ")";
    }

    public static string ForegroundTitle()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "(none)";
        var len = GetWindowTextLength(h);
        if (len <= 0) return "(untitled)";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static void Screenshot(int left, int top, int width, int height, string outPath)
    {
        using var bmp = new Bitmap(Math.Max(width, 1), Math.Max(height, 1));
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(left, top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        bmp.Save(outPath, ImageFormat.Png);
    }
}
