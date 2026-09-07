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
///
/// Hosting: WebView2 is attached through <see cref="CompositionHost"/> straight onto this window's own
/// HWND instead of the WPF WebView2 control — see <see cref="CompositionInput"/> for why (the WPF
/// control's internal child HWND is what stole foreground activation on click, not WM_MOUSEACTIVATE).
/// Mouse messages arrive at this window's own WndProc (nothing else is left to receive them) and are
/// forwarded to WebView2 by <see cref="CompositionInput"/>; keyboard is not forwarded, the bar needs
/// none.
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
    private uint _taskbarCreatedMsg;
    private bool _registered;
    private bool _closing;
    private bool _hiddenByRule;
    /// <summary>Last rect this window was actually placed at by <see cref="SetPos"/> (ABM_QUERYPOS
    /// result), physical screen px. ABN_POSCHANGED re-asserts exactly this rect via SetWindowPos
    /// only — it must never re-issue ABM_QUERYPOS/SETPOS itself, or two app bars in this one process
    /// (top bar and dock) ping-pong POSCHANGED notifications back and forth at each other forever.</summary>
    private RECT _lastRect;
    private CompositionHost? _compHost;
    private CoreWebView2CompositionController? _comp;
    private CompositionInput? _input;

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
        Closing += (_, _) => { _closing = true; Unregister(); DisposeComposition(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var ex = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        // Broadcast whenever Explorer (re)starts — a crash/restart of explorer.exe forgets every
        // AppBar registration, so without this the top bar would silently stop reserving space
        // (ABM_SETPOS would still "succeed" locally but the shell no longer knows about it).
        _taskbarCreatedMsg = PInvoke.RegisterWindowMessage("TaskbarCreated");
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
        if (_comp is not null)
        {
            _comp.RasterizationScale = scale;
            _comp.Bounds = new System.Drawing.Rectangle(0, 0, Monitor.Width, HeightPx);
            _comp.NotifyParentWindowPositionChanged();
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

    /// <summary>
    /// Asks the shell where this AppBar's strip should actually go (ABM_QUERYPOS/SETPOS) and moves
    /// the window there. Only ever called from <see cref="Register"/> and <see cref="UpdateMonitor"/>
    /// (monitor/config change) — never from the ABN_POSCHANGED handler in <see cref="WndProc"/>,
    /// which must only replay <see cref="_lastRect"/> via SetWindowPos. Re-querying on every
    /// POSCHANGED would make the top bar and the dock (both AppBars, both living in this one
    /// process) perpetually re-notify each other: each SETPOS triggers a POSCHANGED broadcast to
    /// every other registered AppBar, and both handlers used to answer it with another SETPOS.
    /// </summary>
    private unsafe void SetPos()
    {
        if (!_registered) return;
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd, uEdge = ABE_TOP };
        d.rc = new RECT { left = Monitor.Left, top = Monitor.Top, right = Monitor.Left + Monitor.Width, bottom = Monitor.Top + HeightPx };
        PInvoke.SHAppBarMessage(PInvoke.ABM_QUERYPOS, &d);
        d.rc.bottom = d.rc.top + HeightPx;
        PInvoke.SHAppBarMessage(PInvoke.ABM_SETPOS, &d);
        _lastRect = d.rc;
        ApplyLastRect();
    }

    /// <summary>ABN_POSCHANGED handler's half of placement: just re-asserts the last rect the shell
    /// actually granted us via SetWindowPos — no ABM_QUERYPOS/SETPOS round trip (see <see cref="SetPos"/>).</summary>
    private void ApplyLastRect()
    {
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), _lastRect.left, _lastRect.top,
            _lastRect.right - _lastRect.left, _lastRect.bottom - _lastRect.top, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
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
            // ABN_POSCHANGED = 1: another app bar or the taskbar moved; re-assert our own last
            // granted rect only (see ApplyLastRect) — never re-query the shell here.
            if ((int)wParam == 1) ApplyLastRect();
            handled = true;
            return 0;
        }
        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            // Explorer just (re)started: every AppBar registration it knew about is gone. Re-register
            // after a short delay — explorer.exe is still finishing its own startup (taskbar HWNDs not
            // necessarily up yet) right when it broadcasts this.
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
            // Per-monitor DPI changed under this window (moved to another monitor, or the user
            // changed scaling): re-derive the composition scale and re-place from Monitor's own
            // Scale (kept in sync with the OS by DisplayMonitors.Enumerate/OnDisplayChanged), rather
            // than trusting a one-off value out of wParam that Monitor would disagree with later.
            Place();
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
            var userDataDir = _ctx.Paths.WebView2UserDataDir + "-topbar";
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
            comp.Bounds = new System.Drawing.Rectangle(0, 0, Monitor.Width, HeightPx);

            var s = comp.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = App.Args.DevTools;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            comp.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            comp.CoreWebView2.WebMessageReceived += OnWebMessage;

            _input = new CompositionInput(_hwnd, comp);

            comp.CoreWebView2.Navigate(_ctx.BaseUrl + "/topbar/?monitor=" + Uri.EscapeDataString(Monitor.Id));
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("top bar init failed", ex);
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

    /// <summary>Debug/automation hook (tests/TopBarPreview): runs script in the page and returns its
    /// JSON-serialized result, or "null" if the page is not ready yet.</summary>
    public Task<string> ExecuteScriptAsync(string script) =>
        _comp?.CoreWebView2?.ExecuteScriptAsync(script) ?? Task.FromResult("null");

    /// <summary>Only handles {type:'popup', ...} — everything else the page sends the host is
    /// handled elsewhere (currently nothing else posts from the topbar page to the host).</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        _ctx.Log.Info("top bar: web message " + e.WebMessageAsJson);
        JsonNode? d;
        try { d = JsonNode.Parse(e.WebMessageAsJson); }
        catch (Exception ex) { _ctx.Log.Warn("top bar: web message parse failed: " + ex.Message); return; }
        if (d?["type"]?.GetValue<string>() != "popup") return;
        var module = d["module"]?.GetValue<string>();
        if (string.IsNullOrEmpty(module)) return;
        var anchorX = d["anchorX"]?.GetValue<double>() ?? 0;
        var anchorW = d["anchorW"]?.GetValue<double>() ?? 0;
        PopupRequested?.Invoke(this, module, anchorX, anchorW);
    }

    public void PostJson(string json)
    {
        try { _comp?.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
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
