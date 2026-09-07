using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;
using NNA.Wallpaper.Taskbar;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.TopBar;

/// <summary>
/// One always-on-top strip at the top edge of a monitor (mac-like menu bar) hosting topbar/index.html.
/// Registers itself as an AppBar so maximized windows start below it, gets the same surface style
/// (acrylic/blur/clear/opaque) as the taskbar, and never takes keyboard focus.
/// </summary>
public partial class TopBarWindow : Window
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint ABE_TOP = 1;

    private readonly HostContext _ctx;
    private readonly TopBarSettings _cfg;
    private HWND _hwnd;
    private uint _callbackMsg;
    private bool _registered;
    private bool _closing;
    private bool _hiddenByRule;

    public MonitorInfo Monitor { get; private set; }
    public int HeightPx => Math.Clamp(_cfg.Height, 20, 64);

    /// <summary>Raised when the page asks to open/toggle a popover: {type:'popup', module, anchorX,
    /// anchorW} (CSS px, relative to this bar's own client area). anchorX/anchorW let the caller
    /// (TopBarManager) compute a physical screen X to centre the popup under, without this window
    /// needing to know anything about popups itself.</summary>
    public event Action<TopBarWindow, string, double, double>? PopupRequested;

    public TopBarWindow(HostContext ctx, MonitorInfo monitor, TopBarSettings cfg)
    {
        _ctx = ctx;
        _cfg = cfg;
        Monitor = monitor;
        InitializeComponent();
        var (r, g, b) = TaskbarStyler.ParseColor(cfg.Style.Color);
        Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitBrowserAsync();
        Closing += (_, _) => { _closing = true; Unregister(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var ex = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        Place();
        ApplyStyle();
        if (_cfg.ReserveSpace) Register();
        HwndSource.FromHwnd((nint)_hwnd)?.AddHook(WndProc);
    }

    /// <summary>Physical monitor rectangle → DIUs of this window (per-monitor DPI).</summary>
    private void Place()
    {
        var scale = Monitor.Scale <= 0 ? 1.0 : Monitor.Scale;
        Left = Monitor.Left / scale;
        Top = Monitor.Top / scale;
        Width = Monitor.Width / scale;
        Height = HeightPx / scale;
        if (_hwnd != HWND.Null)
        {
            PInvoke.SetWindowPos(_hwnd, new HWND(-1), Monitor.Left, Monitor.Top, Monitor.Width, HeightPx,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }
    }

    public void ApplyStyle()
    {
        if (_hwnd == HWND.Null) return;
        var (r, g, b) = TaskbarStyler.ParseColor(_cfg.Style.Color);
        Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        var mode = (_cfg.Style.Mode ?? "opaque").ToLowerInvariant();
        if (mode is "acrylic" or "blur" or "clear")
        {
            // The window paints its solid brush first; the accent policy replaces it with the composed surface.
            TaskbarStyler.ApplyAccent((nint)_hwnd, _cfg.Style);
        }
        else
        {
            TaskbarStyler.ApplyAccent((nint)_hwnd, new SurfaceStyle { Mode = "normal" });
        }
    }

    // ---- AppBar --------------------------------------------------------------------------------

    private unsafe void Register()
    {
        if (_registered) return;
        _callbackMsg = PInvoke.RegisterWindowMessage("NNAWallpaperTopBar");
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd, uCallbackMessage = _callbackMsg };
        PInvoke.SHAppBarMessage(PInvoke.ABM_NEW, &d);
        _registered = true;
        SetPos();
    }

    private unsafe void SetPos()
    {
        if (!_registered) return;
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd, uEdge = ABE_TOP };
        d.rc = new RECT { left = Monitor.Left, top = Monitor.Top, right = Monitor.Left + Monitor.Width, bottom = Monitor.Top + HeightPx };
        PInvoke.SHAppBarMessage(PInvoke.ABM_QUERYPOS, &d);
        d.rc.bottom = d.rc.top + HeightPx;
        PInvoke.SHAppBarMessage(PInvoke.ABM_SETPOS, &d);
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), d.rc.left, d.rc.top, d.rc.right - d.rc.left, d.rc.bottom - d.rc.top,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private unsafe void Unregister()
    {
        if (!_registered) return;
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd };
        PInvoke.SHAppBarMessage(PInvoke.ABM_REMOVE, &d);
        _registered = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_callbackMsg != 0 && msg == (int)_callbackMsg)
        {
            // ABN_POSCHANGED = 1: another app bar or the taskbar moved; re-assert our strip.
            if ((int)wParam == 1) SetPos();
            handled = true;
        }
        return 0;
    }

    // ---- content -------------------------------------------------------------------------------

    private async Task InitBrowserAsync()
    {
        try
        {
            var userDataDir = _ctx.Paths.WebView2UserDataDir + "-topbar";
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir).ConfigureAwait(true);
            await Browser.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            Browser.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            var s = Browser.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = App.Args.DevTools;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            Browser.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessage;
            Browser.CoreWebView2.Navigate(_ctx.BaseUrl + "/topbar/?monitor=" + Uri.EscapeDataString(Monitor.Id));
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("top bar init failed", ex);
        }
    }

    public void Reload()
    {
        try { Browser.CoreWebView2?.Reload(); } catch { }
    }

    /// <summary>Only handles {type:'popup', ...} — everything else the page sends the host is
    /// handled elsewhere (currently nothing else posts from the topbar page to the host).</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? d;
        try { d = JsonNode.Parse(e.WebMessageAsJson); }
        catch { return; }
        if (d?["type"]?.GetValue<string>() != "popup") return;
        var module = d["module"]?.GetValue<string>();
        if (string.IsNullOrEmpty(module)) return;
        var anchorX = d["anchorX"]?.GetValue<double>() ?? 0;
        var anchorW = d["anchorW"]?.GetValue<double>() ?? 0;
        PopupRequested?.Invoke(this, module, anchorX, anchorW);
    }

    public void PostJson(string json)
    {
        try { Browser.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
    }

    /// <summary>Hide under fullscreen apps (and for auto-hide when the pointer is away from the top edge).</summary>
    public void SetHidden(bool hidden)
    {
        if (_closing || hidden == _hiddenByRule) return;
        _hiddenByRule = hidden;
        if (_hwnd == HWND.Null) return;
        PInvoke.ShowWindow(_hwnd, hidden ? SHOW_WINDOW_CMD.SW_HIDE : SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    public void UpdateMonitor(MonitorInfo monitor)
    {
        Monitor = monitor;
        Place();
        SetPos();
    }
}
