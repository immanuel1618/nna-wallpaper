using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;
using NNA.Wallpaper.Taskbar;
using NNA.Wallpaper.TopBar;
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
/// window's own per-monitor DPI) and stays centred on the bottom edge instead of spanning it.
///
/// Hosting: like <see cref="TopBar.TopBarWindow"/>, WebView2 is attached through
/// <see cref="CompositionHost"/> straight onto this window's own HWND instead of the WPF WebView2
/// control, and mouse input is forwarded by <see cref="CompositionInput"/> (see that class for why:
/// the WPF control's own child HWND is what stole foreground activation on click, not
/// WM_MOUSEACTIVATE — WS_EX_NOACTIVATE alone did not stop it). This also gets proper WebView2
/// transparency for free (composition hosting supports a truly transparent DefaultBackgroundColor,
/// unlike the windowed WPF control) — the window is still sized exactly to content as before
/// (dock/dock.js reports its real size), so this does not change the known blur-covers-the-whole-
/// rectangle seam described in docs/DOCK.md, only the focus-stealing behaviour.
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
    private uint _taskbarCreatedMsg;
    private bool _registered;
    private bool _closing;
    private bool _hiddenByRule;
    private double _widthDip;
    private double _heightDip;
    /// <summary>Last rect actually granted by the shell in <see cref="SetPos"/> (ABM_QUERYPOS). The
    /// ABN_POSCHANGED handler only re-applies this via SetWindowPos — see TopBarWindow.SetPos for why
    /// it must never re-issue ABM_QUERYPOS/SETPOS itself.</summary>
    private RECT _lastRect;
    private CompositionHost? _compHost;
    private CoreWebView2CompositionController? _comp;
    private CompositionInput? _input;

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
        Closing += (_, _) => { _closing = true; Unregister(); DisposeComposition(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var ex = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        // Broadcast whenever Explorer (re)starts — see TopBarWindow.OnSourceInitialized for why the
        // dock must listen for this too (an explorer crash/restart drops every AppBar registration).
        _taskbarCreatedMsg = PInvoke.RegisterWindowMessage("TaskbarCreated");
        PositionOnMonitor();
        ApplyStyle();
        if (_cfg.ReserveSpace) Register();
        HwndSource.FromHwnd((nint)_hwnd)?.AddHook(WndProc);
    }

    /// <summary>Recomputes window placement (physical px via SetWindowPos) from the current
    /// <see cref="_widthDip"/>/<see cref="_heightDip"/> and <see cref="Monitor"/>: centred
    /// horizontally, flush to the bottom edge. Also re-queries the AppBar reservation (SetPos) — call
    /// this only for an actual change in monitor/size/registration (<see cref="Register"/>,
    /// <see cref="UpdateMonitor"/>, <see cref="ApplyContentSize"/>), never from the ABN_POSCHANGED
    /// handler, which must only replay <see cref="_lastRect"/> (<see cref="ApplyLastRect"/>) — see
    /// TopBarWindow.SetPos for why re-querying there causes a ping-pong between this process's own
    /// two AppBars.</summary>
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
        _lastRect = new RECT { left = left, top = top, right = left + widthPx, bottom = top + heightPx };
        if (_hwnd != HWND.Null)
        {
            ApplyLastRect();
        }
        if (_registered) SetPos(heightPx);
        if (_comp is not null)
        {
            _comp.RasterizationScale = scale;
            _comp.Bounds = new System.Drawing.Rectangle(0, 0, widthPx, heightPx);
            _comp.NotifyParentWindowPositionChanged();
        }
    }

    /// <summary>ABN_POSCHANGED handler's half of placement: just re-asserts the last window rect this
    /// window computed for itself in <see cref="PositionOnMonitor"/> — no ABM_QUERYPOS/SETPOS round
    /// trip, and no dependency on Monitor/_widthDip/_heightDip having stayed the same in the
    /// meantime (they have not changed just because another AppBar moved).</summary>
    private void ApplyLastRect()
    {
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), _lastRect.left, _lastRect.top,
            _lastRect.right - _lastRect.left, _lastRect.bottom - _lastRect.top,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
    }

    /// <summary>Called from the WebView2 "size" message: the page's real content size in CSS px
    /// (== WPF DIUs at this window's own per-monitor DPI, unlike the physical-pixel Monitor rect).
    /// Ignores a non-numeric/NaN report outright (a malformed or adversarial page message must not
    /// blow up window placement), and clamps to the monitor's own DIU size on top of the existing
    /// floor — a page reporting a bogus huge size must not grow the dock past its monitor.</summary>
    public void ApplyContentSize(double widthDip, double heightDip)
    {
        if (double.IsNaN(widthDip) || double.IsNaN(heightDip) || double.IsInfinity(widthDip) || double.IsInfinity(heightDip)) return;
        var scale = Monitor.Scale <= 0 ? 1.0 : Monitor.Scale;
        var monitorWidthDiu = Monitor.Width / scale;
        var monitorHeightDiu = Monitor.Height / scale;
        _widthDip = Math.Clamp(widthDip, 21, Math.Max(21, monitorWidthDiu));
        _heightDip = Math.Clamp(heightDip, 21, Math.Max(21, monitorHeightDiu));
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
            // ABN_POSCHANGED = 1: replay our own last rect only — never re-query the shell here (see
            // PositionOnMonitor/ApplyLastRect).
            if ((int)wParam == 1) ApplyLastRect();
            handled = true;
            return 0;
        }
        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            // Explorer just (re)started: it forgot every AppBar registration — see
            // TopBarWindow.WndProc for the same handling and the reason for the 1s delay.
            _registered = false;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_closing && _cfg.ReserveSpace) Register();
            };
            timer.Start();
            handled = true;
            return 0;
        }
        if ((uint)msg == PInvoke.WM_DPICHANGED)
        {
            PositionOnMonitor();
            handled = true;
            return 0;
        }
        if (_input is not null && _input.Handle(msg, wParam, lParam, out var result))
        {
            handled = true;
            return result;
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
            if (_closing) return;

            var host = CompositionHost.Create(_hwnd);
            CoreWebView2CompositionController comp;
            try
            {
                comp = await env.CreateCoreWebView2CompositionControllerAsync((nint)_hwnd).ConfigureAwait(true);
            }
            catch
            {
                host.Dispose();
                throw;
            }
            if (_closing) { comp.Close(); host.Dispose(); return; }

            comp.RootVisualTarget = host.RootVisual;
            host.Commit(); // required right after RootVisualTarget or nothing renders (see CompositionHost.Commit)
            _compHost = host;
            _comp = comp;

            comp.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            var scale = Monitor.Scale <= 0 ? 1.0 : Monitor.Scale;
            comp.RasterizationScale = scale;
            var widthPx = Math.Max(1, (int)Math.Round(_widthDip * scale));
            var heightPx = Math.Max(1, (int)Math.Round(_heightDip * scale));
            comp.Bounds = new System.Drawing.Rectangle(0, 0, widthPx, heightPx);

            var s = comp.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = App.Args.DevTools;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            comp.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            comp.CoreWebView2.WebMessageReceived += OnWebMessage;

            _input = new CompositionInput(_hwnd, comp);

            comp.CoreWebView2.Navigate(_ctx.BaseUrl + "/dock/?monitor=" + Uri.EscapeDataString(Monitor.Id));
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

    private void DisposeComposition()
    {
        _input = null;
        try { _comp?.Close(); } catch { }
        _comp = null;
        try { _compHost?.Dispose(); } catch { }
        _compHost = null;
    }

    public void Reload()
    {
        try { _comp?.CoreWebView2?.Reload(); } catch { }
    }

    public void PostJson(string json)
    {
        try { _comp?.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
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
