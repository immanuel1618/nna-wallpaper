using System.Drawing;
using System.IO;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Engine;

/// <summary>
/// One wallpaper surface: a child HWND inside the desktop layer covering one monitor, hosting
/// WebView2. Must be used on the WPF UI thread.
///
/// Hosting mode ("app.json" -&gt; <c>engine.hosting</c>, see <see cref="Host.Config.EngineSettings"/>):
///  - "composition" (default): a <see cref="CompositionHost"/> DirectComposition visual is set as
///    <c>CoreWebView2CompositionController.RootVisualTarget</c> and all mouse input is delivered
///    through <c>SendMouseInput</c> (see <see cref="SendMouse"/>). WebView2 owns no input-receiving
///    HWND in this mode, so Chromium never calls <c>TrackMouseEvent</c> on a child window that the
///    OS can independently decide the real cursor has left — see <see cref="CompositionHost"/> for
///    the full explanation of the hover-flicker bug this avoids.
///  - "window" (fallback/rollback): the original <c>CoreWebView2Controller</c> path, which creates a
///    <c>Chrome_WidgetWin_1</c> child window that <see cref="InputBridge"/> forwards Raw Input mouse
///    messages into with <c>PostMessage</c>.
/// A per-window failure to set up composition hosting (rare — e.g. no DirectComposition support)
/// falls back to window hosting for that monitor only, logged as a warning.
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
    private CoreWebView2CompositionController? _comp;
    private CompositionHost? _compHost;
    private bool _useComposition;
    private bool _disposed;
    private string? _url;
    private string? _html;

    public MonitorInfo Monitor { get; private set; }
    public HWND Hwnd { get; private set; }
    public HWND Parent { get; private set; }
    public bool Paused { get; private set; }
    public bool Ready => _controller is not null;
    public bool DevTools { get; set; }
    /// <summary>True when this window hosts WebView2 through DirectComposition (no input HWND); mouse
    /// input must go through <see cref="SendMouse"/> instead of <see cref="InputTarget"/>.</summary>
    public bool UsesComposition => _comp is not null;

    public event Action<string>? WebMessage;

    public WallpaperWindow(MonitorInfo monitor, HWND parent, Log log, string hosting = "composition")
    {
        Monitor = monitor;
        Parent = parent;
        _log = log;
        _useComposition = !string.Equals(hosting, "window", StringComparison.OrdinalIgnoreCase);
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
        _log.Info($"window {Monitor.Id}: hwnd=0x{(nint)Hwnd:X} parent=0x{(nint)Parent:X} at ({x},{y}) {Monitor.Width}x{Monitor.Height} dpi={Monitor.Dpi} hosting={(_useComposition ? "composition" : "window")}");
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

        if (_useComposition)
        {
            try
            {
                await InitCompositionAsync(env);
                return;
            }
            catch (Exception ex)
            {
                _log.Warn($"composition hosting failed on {Monitor.Id}, falling back to window hosting: {ex.Message}");
                _compHost?.Dispose();
                _compHost = null;
                _comp = null;
                _useComposition = false;
                if (_disposed) return;
            }
        }

        var controller = await env.CreateCoreWebView2ControllerAsync((nint)Hwnd);
        if (_disposed) { controller.Close(); return; }
        _controller = controller;
        ConfigureController(controller);
    }

    private async Task InitCompositionAsync(CoreWebView2Environment env)
    {
        var host = CompositionHost.Create(Hwnd);
        CoreWebView2CompositionController comp;
        try
        {
            comp = await env.CreateCoreWebView2CompositionControllerAsync((nint)Hwnd);
        }
        catch
        {
            host.Dispose();
            throw;
        }
        if (_disposed)
        {
            comp.Close();
            host.Dispose();
            return;
        }
        comp.RootVisualTarget = host.RootVisual;
        host.Commit(); // see CompositionHost.Commit: required after RootVisualTarget or nothing renders
        _compHost = host;
        _comp = comp;
        _controller = comp;
        ConfigureController(comp);
    }

    /// <summary>Settings shared by both hosting modes: background colour, DPI, WebView2 settings,
    /// event wiring and the initial navigation. Kept identical between modes on purpose.</summary>
    private void ConfigureController(CoreWebView2Controller controller)
    {
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
            _log.Info($"mic permission {e.PermissionKind} -> {e.State} on {Monitor.Id}");
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

    /// <summary>
    /// Captures the whole window as a PNG via <c>CoreWebView2.CapturePreviewAsync</c> (used by
    /// <c>GET /widgets/&lt;id&gt;/preview.png</c> for a live block preview instead of the static
    /// fallback — see Services/WidgetsService.cs, which crops this full-window image down to one
    /// grid cell). Returns null when the controller isn't ready yet or the capture itself fails
    /// (e.g. the page is paused/suspended); callers fall back to the static preview in that case.
    /// </summary>
    public async Task<byte[]?> CapturePngAsync()
    {
        if (_controller is null) return null;
        try
        {
            using var stream = new MemoryStream();
            await _controller.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            _log.Warn($"capture preview {Monitor.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Forwards one mouse event to WebView2 through SendMouseInput (composition hosting
    /// only — see <see cref="UsesComposition"/>). <paramref name="clientPoint"/> is in the window's
    /// client coordinates, not screen coordinates.</summary>
    public void SendMouse(CoreWebView2MouseEventKind kind, CoreWebView2MouseEventVirtualKeys keys, uint mouseData, Point clientPoint)
    {
        if (_comp is null) return;
        try { _comp.SendMouseInput(kind, keys, mouseData, clientPoint); }
        catch (Exception ex) { _log.Error("send mouse input", ex); }
    }

    private bool _pauseBusy;

    public async void SetPaused(bool paused)
    {
        if (_controller is null || Paused == paused || _pauseBusy) return;
        _pauseBusy = true;
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
        finally
        {
            _pauseBusy = false;
        }
    }

    /// <summary>The Chromium child window that receives forwarded mouse input in "window" hosting
    /// mode. Returns <see cref="HWND.Null"/> when <see cref="UsesComposition"/> is true — there is no
    /// such window, use <see cref="SendMouse"/> instead.</summary>
    public HWND InputTarget()
    {
        if (UsesComposition) return HWND.Null;
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
            if (_useComposition) _controller.NotifyParentWindowPositionChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _controller?.Close(); } catch { }
        _controller = null;
        _comp = null;
        try { _compHost?.Dispose(); } catch { }
        _compHost = null;
        try { _source?.Dispose(); } catch { }
        _source = null;
        Hwnd = HWND.Null;
    }
}
