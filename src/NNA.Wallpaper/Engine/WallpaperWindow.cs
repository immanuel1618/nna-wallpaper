using System.Drawing;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Engine;

/// <summary>
/// One wallpaper surface: a child HWND inside the desktop layer covering one monitor,
/// hosting a CoreWebView2Controller. Must be used on the WPF UI thread.
/// </summary>
public sealed class WallpaperWindow : IDisposable
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly HWND HwndBottom = new(1);

    private readonly Log _log;
    private HwndSource? _source;
    private CoreWebView2Controller? _controller;
    private bool _disposed;
    private string? _url;
    private string? _html;

    public MonitorInfo Monitor { get; private set; }
    public HWND Hwnd { get; private set; }
    public HWND Parent { get; private set; }
    public bool Paused { get; private set; }
    public bool Ready => _controller is not null;
    public bool DevTools { get; set; }

    public event Action<string>? WebMessage;

    public WallpaperWindow(MonitorInfo monitor, HWND parent, Log log)
    {
        Monitor = monitor;
        Parent = parent;
        _log = log;
        Create();
    }

    private void Create()
    {
        var (x, y) = MapToParent(Monitor.Left, Monitor.Top);
        var p = new HwndSourceParameters("NNA Wallpaper " + Monitor.Device)
        {
            ParentWindow = (nint)Parent,
            WindowStyle = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            ExtendedWindowStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            PositionX = x,
            PositionY = y,
            Width = Monitor.Width,
            Height = Monitor.Height,
            UsesPerPixelOpacity = false,
        };
        _source = new HwndSource(p);
        Hwnd = (HWND)_source.Handle;
        _source.AddHook(WndProc);
        PInvoke.SetWindowPos(Hwnd, HwndBottom, x, y, Monitor.Width, Monitor.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        _log.Info($"window {Monitor.Id}: hwnd=0x{(nint)Hwnd:X} parent=0x{(nint)Parent:X} at ({x},{y}) {Monitor.Width}x{Monitor.Height} dpi={Monitor.Dpi}");
    }

    private unsafe (int x, int y) MapToParent(int screenX, int screenY)
    {
        if (Parent == HWND.Null) return (screenX, screenY);
        var pt = new Point(screenX, screenY);
        PInvoke.MapWindowPoints(HWND.Null, Parent, &pt, 1);
        return (pt.X, pt.Y);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == (int)PInvoke.WM_SIZE && _controller is not null)
        {
            var w = (int)(lParam & 0xFFFF);
            var h = (int)((lParam >> 16) & 0xFFFF);
            if (w > 0 && h > 0) _controller.Bounds = new Rectangle(0, 0, w, h);
        }
        return 0;
    }

    public async Task InitAsync(CoreWebView2Environment env)
    {
        if (_disposed) return;
        var controller = await env.CreateCoreWebView2ControllerAsync((nint)Hwnd);
        if (_disposed)
        {
            controller.Close();
            return;
        }
        _controller = controller;
        controller.DefaultBackgroundColor = Color.FromArgb(255, 5, 5, 5);
        controller.ShouldDetectMonitorScaleChanges = false;
        controller.RasterizationScale = Monitor.Scale;
        controller.Bounds = new Rectangle(0, 0, Monitor.Width, Monitor.Height);
        controller.AllowExternalDrop = false;

        var s = controller.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = DevTools;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;

        controller.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            try { WebMessage?.Invoke(e.WebMessageAsJson); } catch (Exception ex) { _log.Error("web message", ex); }
        };
        controller.CoreWebView2.PermissionRequested += (_, e) =>
        {
            // Microphone for voice capture in the planner block; everything else denied.
            e.State = e.PermissionKind == CoreWebView2PermissionKind.Microphone
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };
        controller.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        controller.CoreWebView2.ProcessFailed += (_, e) => _log.Warn($"webview process failed on {Monitor.Id}: {e.ProcessFailedKind} {e.Reason}");
        controller.IsVisible = true;

        if (_html is not null) controller.CoreWebView2.NavigateToString(_html);
        else if (_url is not null) controller.CoreWebView2.Navigate(_url);
    }

    public void Navigate(string url)
    {
        _url = url;
        _html = null;
        _controller?.CoreWebView2.Navigate(url);
    }

    public void NavigateToString(string html)
    {
        _html = html;
        _url = null;
        _controller?.CoreWebView2.NavigateToString(html);
    }

    public void Reload()
    {
        if (_controller is null) return;
        if (_html is not null) _controller.CoreWebView2.NavigateToString(_html);
        else _controller.CoreWebView2.Reload();
    }

    public void PostJson(string json)
    {
        try { _controller?.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception ex) { _log.Error("post message", ex); }
    }

    public async void SetPaused(bool paused)
    {
        if (_controller is null || Paused == paused) return;
        Paused = paused;
        try
        {
            if (paused)
            {
                _controller.IsVisible = false;
                PostJson("{\"type\":\"pause\",\"value\":true}");
                await _controller.CoreWebView2.TrySuspendAsync();
            }
            else
            {
                if (_controller.CoreWebView2.IsSuspended) _controller.CoreWebView2.Resume();
                _controller.IsVisible = true;
                PostJson("{\"type\":\"pause\",\"value\":false}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"pause {Monitor.Id}: {ex.Message}");
        }
    }

    /// <summary>The Chromium child window that receives forwarded mouse input.</summary>
    public HWND InputTarget()
    {
        var w0 = PInvoke.FindWindowEx(Hwnd, HWND.Null, "Chrome_WidgetWin_0", null);
        if (w0 == HWND.Null) return Hwnd;
        var w1 = PInvoke.FindWindowEx(w0, HWND.Null, "Chrome_WidgetWin_1", null);
        return w1 != HWND.Null ? w1 : w0;
    }

    public bool IsAlive => Hwnd != HWND.Null && PInvoke.IsWindow(Hwnd);

    /// <summary>Re-assert the bottom z-order position without moving or activating the window.</summary>
    public void EnsureBottom()
    {
        if (!IsAlive) return;
        PInvoke.SetWindowPos(Hwnd, HwndBottom, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>Re-read the monitor rectangle (after display change) and move the window accordingly.</summary>
    public void Update(MonitorInfo monitor)
    {
        Monitor = monitor;
        var (x, y) = MapToParent(monitor.Left, monitor.Top);
        PInvoke.SetWindowPos(Hwnd, HwndBottom, x, y, monitor.Width, monitor.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        if (_controller is not null)
        {
            _controller.RasterizationScale = monitor.Scale;
            _controller.Bounds = new Rectangle(0, 0, monitor.Width, monitor.Height);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _controller?.Close(); } catch { }
        _controller = null;
        try { _source?.Dispose(); } catch { }
        _source = null;
        Hwnd = HWND.Null;
    }
}
