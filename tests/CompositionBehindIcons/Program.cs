using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.CompositionBehindIcons;

/// <summary>
/// Incident helper: "no interaction with wallpaper" (composition hosting). See the .csproj comment
/// for the reproduction rationale. Isolates variables against the already-passing
/// tests/CompositionProbe (top-level window, no real OS cursor movement, pure SendMouseInput) by
/// hosting the same CompositionHost/CoreWebView2CompositionController combo on a REAL child HWND of
/// the owner's real WorkerW, behind the desktop icon layer, with the exact WS_EX_NOACTIVATE |
/// WS_EX_TOOLWINDOW ex-style + WS_CHILD style that Engine/WallpaperWindow.cs uses in production.
///
/// RESULT (see H:\night-runs\nna-wallpaper-2 incident report for the full writeup): every
/// composition-hosting variant below FAILS on a WorkerW-child window -- input demonstrably reaches
/// the DOM (JS event counters increment, the onclick handler's style change executes) but the
/// compositor never flips a new frame to screen, UNLESS the child window's top-level ancestor belongs
/// to THIS process ("ownparent" variant -- PASS, exact expected brightness values). Styles, focus and
/// Chromium throttling flags make no difference. "windowmode" (the fix: default engine.hosting is now
/// "window") PASSES the raw-input pipeline check but could not get a live on-screen hover/flicker
/// confirmation in this run -- see the report for why (a real window unexpectedly covered the entire
/// pre-approved test rectangle).
///
/// Composition variants (baseline/nostyle/movefocus/...) never move the real OS cursor or touch the
/// owner's live instance on :1618: isolated data dir per variant, hover/click driven purely by
/// SendMouseInput with a computed client point. "windowmode" is the one exception: it must move the
/// real OS cursor briefly (SendInput, not SetCursorPos -- see that method's comment for why) to
/// exercise the real Raw Input pipeline, and restores the original cursor position afterward.
///
/// Usage: dotnet run against tests/CompositionBehindIcons/CompositionBehindIcons.csproj -- [variant]
///   variant "baseline"          (default) real WallpaperWindow, hosting=composition, unmodified. FAIL.
///   variant "nostyle"           manual window, same as baseline but ex-style=0 (no NOACTIVATE/TOOLWINDOW). FAIL.
///   variant "movefocus"         manual window, prod ex-style, calls CoreWebView2Controller.MoveFocus
///                                (Programmatic) once before sending input. FAIL.
///   variant "*-nooc"            appends --disable-features=CalculateNativeWinOcclusion. FAIL.
///   variant "*-nobg"            appends --disable-backgrounding-occluded-windows
///                                --disable-renderer-backgrounding --disable-background-timer-throttling
///                                (+ the nooc feature flag). FAIL.
///   variant "*-redraw"          forces RedrawWindow(RDW_INVALIDATE|RDW_UPDATENOW|...) after each
///                                interaction. FAIL (DirectComposition surfaces ignore GDI invalidate).
///   variant "*-ownparent"       same child window, but parented under a top-level window THIS
///                                process owns instead of WorkerW. PASS -- the actual root cause.
///   variant "windowmode"        real WallpaperWindow, hosting=window, + a real InputBridge scoped to
///                                just this window; moves the real cursor into the helper's square.
/// Prints brightness BEFORE / HOVER / CLICK and one PASS/FAIL line per run.
/// </summary>
internal static class Program
{
    private const string ScratchDir =
        @"C:\Users\imman\AppData\Local\Temp\claude\h--\177acbd5-5f45-4de3-8283-018df2861388\scratchpad\comp-behind-icons";

    private const string ShotsDir = @"H:\night-runs\nna-wallpaper-2\shots";

    // Fake monitor: top-left strip of the owner's real portrait monitor (\\.\DISPLAY1), well above
    // any real window per the incident brief (safe area: y up to about -353, i.e. 250px from the
    // real top at y=-603). Never overlaps desktop icons (icons start further down that monitor).
    private const int MonLeft = -1440, MonTop = -603, MonWidth = 300, MonHeight = 200;

    // Square in the test page, in the fake monitor's client coordinates.
    private const int SqLeft = 50, SqTop = 50, SqWidth = 150, SqHeight = 100;

    private static readonly string Page =
        "<!doctype html><html><head><meta charset='utf-8'><style>" +
        "html,body{margin:0;height:100%;background:#111;overflow:hidden}" +
        "#sq{position:absolute;left:" + SqLeft + "px;top:" + SqTop + "px;width:" + SqWidth + "px;height:" + SqHeight + "px;background:#333}" +
        "#sq:hover{background:#fff}" +
        "</style></head><body>" +
        "<div id='sq' onclick=\"this.style.background='#777';window.__ev.click++\"></div>" +
        "<script>" +
        "window.__ev={move:0,down:0,up:0,click:0,mmove:0,mdown:0,mup:0};" +
        "document.addEventListener('pointermove',function(){window.__ev.move++;});" +
        "document.addEventListener('pointerdown',function(){window.__ev.down++;});" +
        "document.addEventListener('pointerup',function(){window.__ev.up++;});" +
        "document.addEventListener('mousemove',function(){window.__ev.mmove++;});" +
        "document.addEventListener('mousedown',function(){window.__ev.mdown++;});" +
        "document.addEventListener('mouseup',function(){window.__ev.mup++;});" +
        "</script>" +
        "</body></html>";

    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [STAThread]
    private static void Main(string[] args)
    {
        var variant = args.Length > 0 ? args[0] : "baseline";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                exitCode = await RunAsync(variant) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("PROBE FAILED: " + ex);
                exitCode = 1;
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();
        Environment.Exit(exitCode);
    }

    private static async Task<bool> RunAsync(string variant)
    {
        Console.WriteLine("variant=" + variant);
        // Per-variant profile dir: a fresh browser process must actually pick up this run's
        // AdditionalBrowserArguments (e.g. --disable-features=CalculateNativeWinOcclusion) rather
        // than silently reusing an already-running WebView2 browser process bound to a shared profile.
        var scratchDir = ScratchDir + "-" + variant.Replace("+", "-");
        Directory.CreateDirectory(scratchDir);
        Directory.CreateDirectory(ShotsDir);

        var paths = new Paths(scratchDir, AppContext.BaseDirectory);
        paths.EnsureDirectories();
        var log = new Log(paths);

        var desktop = new DesktopHost(log);
        if (!desktop.Attach())
        {
            Console.WriteLine("FAIL: desktop layer not found (DesktopHost.Attach)");
            return false;
        }
        Console.WriteLine($"desktop: mode={desktop.Mode} progman=0x{(nint)desktop.Progman:X} workerw=0x{(nint)desktop.WorkerW:X} parent=0x{(nint)desktop.Parent:X}");

        var monitor = new MonitorInfo
        {
            Device = @"\\.\CompBehindIcons",
            Left = MonLeft,
            Top = MonTop,
            Width = MonWidth,
            Height = MonHeight,
            Primary = false,
            Dpi = 96,
        };

        // "nooc" variants add --disable-features=CalculateNativeWinOcclusion: hypothesis (from the
        // DOM-event-counters diagnostic -- move/down/up/click all fire but the screen never repaints)
        // that Chromium's native-window-occlusion throttling is suppressing paint submission for this
        // HWND, because DWM's occlusion test treats the desktop icon-list window's rectangle as fully
        // opaque above us regardless of its actual per-pixel transparency.
        var disableFeatures = variant.Contains("nooc") || variant.Contains("nobg")
            ? "HardwareMediaKeyHandling,CalculateNativeWinOcclusion"
            : "HardwareMediaKeyHandling";
        var extraSwitches = variant.Contains("nobg")
            ? " --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling"
            : "";
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --disable-features=" + disableFeatures + extraSwitches,
        };
        var env = await CoreWebView2Environment.CreateAsync(null, paths.WebView2UserDataDir, options);
        Console.WriteLine("webview2 runtime: " + env.BrowserVersionString);

        return variant switch
        {
            "baseline" => await RunBaselineAsync(env, desktop, monitor, log),
            "windowmode" => await RunWindowModeAsync(env, desktop, monitor, log),
            _ => await RunManualAsync(env, desktop, monitor, variant),
        };
    }

    /// <summary>Real WallpaperWindow, hosting="composition", completely unmodified -- exactly what
    /// production creates for a wallpaper monitor.</summary>
    private static async Task<bool> RunBaselineAsync(CoreWebView2Environment env, DesktopHost desktop, MonitorInfo monitor, Log log)
    {
        var w = new WallpaperWindow(monitor, desktop.Parent, log, "composition");
        w.NavigateToString(Page);
        await w.InitAsync(env);

        Console.WriteLine("hwnd=0x" + ((nint)w.Hwnd).ToString("X") + " usesComposition=" + w.UsesComposition + " ready=" + w.Ready);
        DescribeWindow(w.Hwnd, desktop.Parent);

        if (!w.UsesComposition)
        {
            Console.WriteLine("FAIL: composition hosting did not initialize (fell back to window hosting)");
            return false;
        }

        await Task.Delay(800);

        var (before, hover, click) = await MeasureAsync(
            "baseline",
            move: p => w.SendMouse(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, p),
            down: p => w.SendMouse(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, p),
            up: p => w.SendMouse(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, p),
            monitor);

        w.Dispose();
        return Report("baseline", before, hover, click);
    }

    /// <summary>Validates the fallback fix (default engine.hosting="window" + InputBridge.HoverKeepAlive)
    /// with a real WallpaperWindow + a real InputBridge scoped to this one window, behind the real
    /// WorkerW. Needs genuine OS cursor movement (the flicker race is between real WM_MOUSEMOVE and
    /// Windows resolving "window under the cursor") -- moves the real cursor briefly into the helper's
    /// own safe-zone square, jitters it like tests/hover-probe.ps1 does, then restores the original
    /// cursor position. 20 frames @100ms; PASS when the square is "hovering" (bright) in &gt;=90%.</summary>
    private static async Task<bool> RunWindowModeAsync(CoreWebView2Environment env, DesktopHost desktop, MonitorInfo monitor, Log log)
    {
        var w = new WallpaperWindow(monitor, desktop.Parent, log, "window");
        w.NavigateToString(Page);
        await w.InitAsync(env);
        Console.WriteLine("hwnd=0x" + ((nint)w.Hwnd).ToString("X") + " usesComposition=" + w.UsesComposition + " ready=" + w.Ready);

        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var bridge = new InputBridge(desktop, () => new[] { w }, log, dispatcher);
        if (!bridge.Start())
        {
            Console.WriteLine("FAIL: InputBridge.Start() failed (raw input registration)");
            w.Dispose();
            return false;
        }

        await Task.Delay(800);

        var center = new System.Drawing.Point(SqLeft + SqWidth / 2, SqTop + SqHeight / 2);
        var screenX = monitor.Left + center.X;
        var screenY = monitor.Top + center.Y;

        GetCursorPos(out var origin);
        Console.WriteLine($"cursor origin=({origin.X},{origin.Y}) moving to ({screenX},{screenY}) briefly");

        var before = CaptureSquareMean(monitor, "windowmode-before");
        Console.WriteLine($"brightness BEFORE: {before:F2}");

        var means = new List<double>();
        try
        {
            // SendInput (not SetCursorPos): SetCursorPos only warps the cursor's absolute screen
            // position, it does not inject a motion event into the system's unified input stream, so
            // Raw Input (RIDEV_INPUTSINK, what InputBridge listens to) never sees it -- confirmed
            // empirically (SetCursorPos-only version of this test: zero hover frames, InputBridge's
            // WndProc simply never receives WM_INPUT). SendInput injects synthetic input at the same
            // level a real device would, which both raw input listeners and legacy WM_MOUSEMOVE see.
            MoveTo(screenX, screenY);
            await Task.Delay(150);
            var toggle = 0;
            for (var i = 0; i < 20; i++)
            {
                // 1px jitter = genuine motion, not just a warp -- this is what re-triggers the
                // TrackMouseEvent/WM_MOUSELEAVE race (same technique as tests/hover-probe.ps1, but via
                // SendInput here since that script targets legacy WM_MOUSEMOVE only, not raw input).
                MoveTo(screenX + toggle, screenY);
                toggle = 1 - toggle;
                await Task.Delay(100);
                means.Add(CaptureSquareMean(monitor, "windowmode-frame" + i.ToString("D2")));
            }
        }
        finally
        {
            MoveTo(origin.X, origin.Y);
            Console.WriteLine("cursor restored to origin");
        }

        bridge.Dispose();
        w.Dispose();

        var hoveringFrames = means.Count(m => m > before + 40); // clearly brighter than the dark #333 baseline
        var ratio = means.Count == 0 ? 0 : (double)hoveringFrames / means.Count;
        Console.WriteLine("frame means: " + string.Join(", ", means.Select(m => m.ToString("F1"))));
        Console.WriteLine($"hoveringFrames={hoveringFrames}/{means.Count} ratio={ratio:P0}");
        var pass = ratio >= 0.9;
        Console.WriteLine(pass ? "PASS [windowmode] hover holds >=90% of frames" : "FAIL [windowmode] hover flickers (<90% of frames)");
        return pass;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point pt);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    /// <summary>Absolute-position SendInput move, normalized against the full virtual desktop (so
    /// negative screen coordinates on \\.\DISPLAY1 work) -- see the call site for why SetCursorPos
    /// alone does not work here.</summary>
    private static void MoveTo(int x, int y)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        var nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(vw - 1, 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(vh - 1, 1));
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    /// <summary>Manual replication of WallpaperWindow.Create()+InitCompositionAsync() with tunable
    /// ex-style / MoveFocus, to isolate hypotheses (в)/(д) from the task without touching prod code.</summary>
    private static async Task<bool> RunManualAsync(CoreWebView2Environment env, DesktopHost desktop, MonitorInfo monitor, string variant)
    {
        var noStyle = variant.Contains("nostyle");
        var moveFocus = variant.Contains("movefocus");
        var ownParent = variant.Contains("ownparent");
        var exStyle = noStyle ? 0 : (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // "ownparent" variants isolate cross-process WS_CHILD parenting (WorkerW belongs to
        // explorer.exe, a different process than ours) from "behind the icon layer": parent the same
        // composition child window under a real TOP-LEVEL window this process itself owns instead of
        // WorkerW, at the same screen rectangle. Every composition-hosting case that already works in
        // this codebase (tests/CompositionProbe, TopBar/CompositionInput.cs) uses a top-level window
        // owned by this same process; nothing had tested a foreign-process ancestor until this helper.
        HwndSource? ownerSrc = null;
        HWND parentHwnd;
        if (ownParent)
        {
            var op = new HwndSourceParameters("NNA CompBehindIcons Owner " + variant)
            {
                WindowStyle = unchecked((int)0x80000000) /*WS_POPUP*/ | 0x10000000 /*WS_VISIBLE*/,
                ExtendedWindowStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                PositionX = MonLeft,
                PositionY = MonTop,
                Width = MonWidth,
                Height = MonHeight,
                UsesPerPixelOpacity = false,
            };
            ownerSrc = new HwndSource(op);
            parentHwnd = (HWND)ownerSrc.Handle;
            Console.WriteLine("owner top-level hwnd=0x" + ((nint)parentHwnd).ToString("X") + " (this process)");
        }
        else
        {
            parentHwnd = desktop.Parent;
        }

        var (px, py) = ownParent ? (0, 0) : MapToParent(desktop.Parent, MonLeft, MonTop);
        var p = new HwndSourceParameters("NNA CompBehindIcons Manual " + variant)
        {
            ParentWindow = (nint)parentHwnd,
            WindowStyle = 0x40000000 /*WS_CHILD*/ | 0x10000000 /*WS_VISIBLE*/ | 0x02000000 /*WS_CLIPCHILDREN*/ | 0x04000000 /*WS_CLIPSIBLINGS*/,
            ExtendedWindowStyle = exStyle,
            PositionX = px,
            PositionY = py,
            Width = MonWidth,
            Height = MonHeight,
            UsesPerPixelOpacity = false,
        };
        using var src = new HwndSource(p);
        var hwnd = (HWND)src.Handle;
        PInvoke.SetWindowPos(hwnd, new HWND(1) /*HWND_BOTTOM*/, px, py, MonWidth, MonHeight,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

        Console.WriteLine("hwnd=0x" + ((nint)hwnd).ToString("X"));
        DescribeWindow(hwnd, parentHwnd);

        var host = CompositionHost.Create(hwnd);
        var comp = await env.CreateCoreWebView2CompositionControllerAsync((nint)hwnd);
        comp.RootVisualTarget = host.RootVisual;
        host.Commit();
        comp.Bounds = new Rectangle(0, 0, MonWidth, MonHeight);
        comp.IsVisible = true;
        comp.DefaultBackgroundColor = Color.FromArgb(255, 5, 5, 5);

        var navDone = new TaskCompletionSource();
        comp.CoreWebView2.NavigationCompleted += (_, _) => navDone.TrySetResult();
        comp.CoreWebView2.NavigateToString(Page);
        await navDone.Task;
        await Task.Delay(400);

        if (moveFocus)
        {
            Console.WriteLine("calling comp.MoveFocus(Programmatic)");
            try { comp.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); }
            catch (Exception ex) { Console.WriteLine("MoveFocus threw: " + ex.Message); }
        }

        Console.WriteLine("comp.Bounds=" + comp.Bounds + " comp.RasterizationScale=" + comp.RasterizationScale + " comp.IsVisible=" + comp.IsVisible);

        // "redraw" variants force a repaint of the HWND (RedrawWindow RDW_INVALIDATE|RDW_UPDATENOW|
        // RDW_ALLCHILDREN|RDW_FRAME) after each interaction, testing whether DWM only flips a WS_CHILD
        // composition target to screen following an explicit repaint request, unlike a top-level
        // window (tests/CompositionProbe) which composites continuously regardless.
        Action? pump = variant.Contains("redraw")
            ? () => RedrawWindow((nint)hwnd, 0, 0, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_FRAME)
            : null;

        var (before, hover, click) = await MeasureAsync(
            variant,
            move: pt => comp.SendMouseInput(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, pt),
            down: pt => comp.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, pt),
            up: pt => comp.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, pt),
            monitor,
            pump);

        try
        {
            var json = await comp.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__ev)");
            Console.WriteLine("DOM event counters: " + json);
        }
        catch (Exception ex) { Console.WriteLine("ExecuteScriptAsync failed: " + ex.Message); }

        try { comp.Close(); } catch { }
        host.Dispose();
        try { ownerSrc?.Dispose(); } catch { }

        return Report(variant, before, hover, click);
    }

    private static async Task<(double before, double hover, double click)> MeasureAsync(
        string tag, Action<System.Drawing.Point> move, Action<System.Drawing.Point> down, Action<System.Drawing.Point> up, MonitorInfo monitor, Action? pump = null)
    {
        pump?.Invoke();
        var before = CaptureSquareMean(monitor, tag + "-before");
        Console.WriteLine($"brightness BEFORE: {before:F2}");

        var center = new System.Drawing.Point(SqLeft + SqWidth / 2, SqTop + SqHeight / 2);
        move(center);
        await Task.Delay(1000);
        pump?.Invoke();

        var hover = CaptureSquareMean(monitor, tag + "-hover");
        Console.WriteLine($"brightness HOVER: {hover:F2}");

        down(center);
        await Task.Delay(80);
        up(center);
        await Task.Delay(500);
        pump?.Invoke();

        var click = CaptureSquareMean(monitor, tag + "-click");
        Console.WriteLine($"brightness CLICK: {click:F2}");

        return (before, hover, click);
    }

    private static bool Report(string tag, double before, double hover, double click)
    {
        var hoverOk = hover > before + 10;
        var clickOk = Math.Abs(click - 119) < 30; // #777 = rgb(119,119,119)
        Console.WriteLine($"[{tag}] hoverOk={hoverOk} clickOk={clickOk}");
        var pass = hoverOk && clickOk;
        Console.WriteLine(pass
            ? $"PASS [{tag}] composition input works"
            : $"FAIL [{tag}] composition input broken (see {ShotsDir})");
        return pass;
    }

    private static double CaptureSquareMean(MonitorInfo monitor, string tag)
    {
        using var bmp = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        }
        var path = Path.Combine(ShotsDir, "comp-behind-icons-" + tag + ".png");
        bmp.Save(path, ImageFormat.Png);

        long total = 0;
        var count = 0;
        for (var y = SqTop + 15; y < SqTop + SqHeight - 15; y += 3)
        {
            for (var x = SqLeft + 15; x < SqLeft + SqWidth - 15; x += 3)
            {
                var c = bmp.GetPixel(x, y);
                total += c.R + c.G + c.B;
                count++;
            }
        }
        return count == 0 ? 0 : total / (3.0 * count);
    }

    private static unsafe (int x, int y) MapToParent(HWND parent, int screenX, int screenY)
    {
        if (parent == HWND.Null) return (screenX, screenY);
        var pt = new System.Drawing.Point(screenX, screenY);
        PInvoke.MapWindowPoints(HWND.Null, parent, &pt, 1);
        return (pt.X, pt.Y);
    }

    private static void DescribeWindow(HWND hwnd, HWND expectedParent)
    {
        var realParent = GetParent((nint)hwnd);
        var ex = GetWindowLongPtr((nint)hwnd, GWL_EXSTYLE);
        var style = GetWindowLongPtr((nint)hwnd, GWL_STYLE);
        var visible = IsWindowVisible((nint)hwnd);
        Console.WriteLine($"window: realParent=0x{realParent:X} expectedParent=0x{(nint)expectedParent:X} match={realParent == (nint)expectedParent}");
        Console.WriteLine($"window: style=0x{style:X} exStyle=0x{ex:X} visible={visible}");
        Console.WriteLine($"window:   WS_CHILD={(style & 0x40000000) != 0} WS_VISIBLE={(style & 0x10000000) != 0}");
        Console.WriteLine($"window:   WS_EX_NOACTIVATE={(ex & 0x08000000) != 0} WS_EX_TOOLWINDOW={(ex & 0x00000080) != 0}");
    }

    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;

    private const uint RDW_INVALIDATE = 0x0001, RDW_UPDATENOW = 0x0100, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint hWnd, nint lprcUpdate, nint hrgnUpdate, uint flags);

    [DllImport("user32.dll")] private static extern nint GetParent(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLongPtr32(nint hWnd, int nIndex);

    private static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
}
