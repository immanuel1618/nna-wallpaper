using System.IO;
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

namespace NNA.Wallpaper.Dock;

/// <summary>
/// One floating dock at the bottom edge of a monitor (mac-like dock), hosting dock/index.html.
/// Modelled on <see cref="TopBar.TopBarWindow"/> (AppBar plumbing, surface style, WS_EX_TOOLWINDOW)
/// with two differences: the window is sized to its content (the page posts
/// <c>{"type":"size","width":..,"height":..}</c> in CSS px == WPF DIUs, since WebView2 runs at the
/// window's own per-monitor DPI) and stays centred on the bottom edge instead of spanning it, and
/// it takes WS_EX_NOACTIVATE so a click on it never steals focus from the foreground app — the
/// click still reaches WebView2 (child HWND gets mouse input regardless of NOACTIVATE on the
/// top-level owner).
///
/// Window transparency: WebView2's own transparent background only composes correctly with
/// DirectComposition hosting, not the WPF child-HWND control used here, so true window
/// transparency was not attempted. Instead the window is exactly content-sized (dock/dock.js
/// reports its real size, padding already baked into the page's own layout) with an opaque Base
/// background — the page draws the rounded dock bar; the plain rectangle around it blends with the
/// Base-coloured wallpaper behind it. This is an approximation, not true transparency (see
/// docs/DOCK.md); with an acrylic/blur style the blur material covers the whole rectangle, not
/// just the rounded bar, which is a known visual seam.
/// </summary>
public partial class DockWindow : Window
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint ABE_BOTTOM = 3;

    private readonly HostContext _ctx;
    private DockSettings _cfg;
    private HWND _hwnd;
    private uint _callbackMsg;
    private bool _registered;
    private bool _closing;
    private bool _hiddenByRule;
    private double _widthDip;
    private double _heightDip;

    public MonitorInfo Monitor { get; private set; }

    public DockWindow(HostContext ctx, MonitorInfo monitor, DockSettings cfg)
    {
        _ctx = ctx;
        _cfg = cfg;
        Monitor = monitor;
        InitializeComponent();
        var (r, g, b) = TaskbarStyler.ParseColor(cfg.Style.Color);
        Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        // Fallback size before the page's first "size" message, so the window is never a 0x0 flash.
        _widthDip = Math.Max(233, cfg.Size * 8);
        _heightDip = cfg.Size * cfg.MagnifyMax + 21;
        Width = _widthDip;
        Height = _heightDip;
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitBrowserAsync();
        Closing += (_, _) => { _closing = true; Unregister(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var ex = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        PositionOnMonitor();
        ApplyStyle();
        if (_cfg.ReserveSpace) Register();
        HwndSource.FromHwnd((nint)_hwnd)?.AddHook(WndProc);
    }

    /// <summary>Recomputes window placement (physical px via SetWindowPos) from the current
    /// <see cref="_widthDip"/>/<see cref="_heightDip"/> and <see cref="Monitor"/>: centred
    /// horizontally, flush to the bottom edge.</summary>
    private void PositionOnMonitor()
    {
        var scale = Monitor.Scale <= 0 ? 1.0 : Monitor.Scale;
        Width = _widthDip;
        Height = _heightDip;
        var widthPx = Math.Max(1, (int)Math.Round(_widthDip * scale));
        var heightPx = Math.Max(1, (int)Math.Round(_heightDip * scale));
        var left = Monitor.Left + (Monitor.Width - widthPx) / 2;
        var top = Monitor.Top + Monitor.Height - heightPx;
        Left = left / scale;
        Top = top / scale;
        if (_hwnd != HWND.Null)
        {
            PInvoke.SetWindowPos(_hwnd, new HWND(-1), left, top, widthPx, heightPx,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }
        if (_registered) SetPos(heightPx);
    }

    /// <summary>Called from the WebView2 "size" message: the page's real content size in CSS px
    /// (== WPF DIUs at this window's own per-monitor DPI, unlike the physical-pixel Monitor rect).</summary>
    public void ApplyContentSize(double widthDip, double heightDip)
    {
        _widthDip = Math.Max(21, widthDip);
        _heightDip = Math.Max(21, heightDip);
        PositionOnMonitor();
    }

    public void ApplyStyle()
    {
        if (_hwnd == HWND.Null) return;
        var (r, g, b) = TaskbarStyler.ParseColor(_cfg.Style.Color);
        Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        var mode = (_cfg.Style.Mode ?? "acrylic").ToLowerInvariant();
        if (mode is "acrylic" or "blur" or "clear")
        {
            TaskbarStyler.ApplyAccent((nint)_hwnd, _cfg.Style);
        }
        else
        {
            TaskbarStyler.ApplyAccent((nint)_hwnd, new SurfaceStyle { Mode = "normal" });
        }
    }

    public void UpdateConfig(DockSettings cfg)
    {
        _cfg = cfg;
        ApplyStyle();
        if (cfg.ReserveSpace) Register(); else Unregister();
    }

    // ---- AppBar (optional: ReserveSpace) --------------------------------------------------------

    private unsafe void Register()
    {
        if (_registered) return;
        _callbackMsg = PInvoke.RegisterWindowMessage("NNAWallpaperDock");
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd, uCallbackMessage = _callbackMsg };
        PInvoke.SHAppBarMessage(PInvoke.ABM_NEW, &d);
        _registered = true;
        var scale = Monitor.Scale <= 0 ? 1.0 : Monitor.Scale;
        SetPos((int)Math.Round(_heightDip * scale));
    }

    /// <summary>Reserves a full-width strip at the bottom edge sized to the dock's own height —
    /// same convention as TopBarWindow's ABE_TOP strip, even though the visible dock is narrower
    /// and centred; a partial-width reservation is not a thing AppBar understands.</summary>
    private unsafe void SetPos(int heightPx)
    {
        if (!_registered) return;
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd, uEdge = ABE_BOTTOM };
        d.rc = new RECT { left = Monitor.Left, top = Monitor.Top + Monitor.Height - heightPx, right = Monitor.Left + Monitor.Width, bottom = Monitor.Top + Monitor.Height };
        PInvoke.SHAppBarMessage(PInvoke.ABM_QUERYPOS, &d);
        d.rc.top = d.rc.bottom - heightPx;
        PInvoke.SHAppBarMessage(PInvoke.ABM_SETPOS, &d);
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
            if ((int)wParam == 1) PositionOnMonitor(); // ABN_POSCHANGED
            handled = true;
        }
        return 0;
    }

    // ---- content -------------------------------------------------------------------------------

    private async Task InitBrowserAsync()
    {
        try
        {
            var userDataDir = _ctx.Paths.WebView2UserDataDir + "-dock";
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
            Browser.CoreWebView2.Navigate(_ctx.BaseUrl + "/dock/?monitor=" + Uri.EscapeDataString(Monitor.Id));
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("dock init failed", ex);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var node = Host.Json.ParseNode(e.WebMessageAsJson) as System.Text.Json.Nodes.JsonObject;
            if (node is null) return;
            var type = (string?)node["type"];
            if (type == "size")
            {
                var w = node["width"]?.GetValue<double>() ?? _widthDip;
                var h = node["height"]?.GetValue<double>() ?? _heightDip;
                Dispatcher.BeginInvoke(() => ApplyContentSize(w, h));
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("dock web message: " + ex.Message);
        }
    }

    public void Reload()
    {
        try { Browser.CoreWebView2?.Reload(); } catch { }
    }

    public void PostJson(string json)
    {
        try { Browser.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
    }

    /// <summary>Hide under fullscreen apps (and for auto-hide when the pointer is away from the bottom edge).</summary>
    public void SetHidden(bool hidden)
    {
        if (_closing || hidden == _hiddenByRule) return;
        _hiddenByRule = hidden;
        if (_hwnd == HWND.Null) return;
        PInvoke.ShowWindow(_hwnd, hidden ? SHOW_WINDOW_CMD.SW_HIDE : SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    /// <summary>Bottom edge of this dock's bar in physical screen px — used by DockManager's
    /// auto-hide check (pointer near the bottom edge of the monitor shows the dock again).</summary>
    public int BottomEdgeY => Monitor.Top + Monitor.Height;

    public int HeightPx
    {
        get
        {
            var scale = Monitor.Scale <= 0 ? 1.0 : Monitor.Scale;
            return Math.Max(1, (int)Math.Round(_heightDip * scale));
        }
    }

    public void UpdateMonitor(MonitorInfo monitor)
    {
        Monitor = monitor;
        PositionOnMonitor();
    }
}
