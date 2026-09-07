using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.TopBar;

/// <summary>
/// One popover window for the top bar (volume, calendar, the NNA menu, control center). Unlike
/// <see cref="TopBarWindow"/> this window is a normal focusable top-level window (sliders and
/// keyboard need real focus), gets WS_EX_TOOLWINDOW so it stays out of the taskbar/Alt-Tab, and
/// closes itself on <see cref="Window.Deactivated"/> (clicking elsewhere) or on an explicit
/// {type:'close'} message from the page (Esc). The page reports its own size via
/// {type:'size', width, height} after render (and on every ResizeObserver change); this window
/// only ever positions/sizes itself in response to that message — it never guesses.
/// </summary>
public partial class PopupWindow : Window
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly HostContext _ctx;
    private bool _closing;
    private double _scale = 1.0;
    private int _anchorCenterXPhysical;
    private int _topYPhysical;
    private int _monLeftPhysical;
    private int _monRightPhysical;
    private int _monHeightPhysical;

    /// <summary>Popup module id (matches topbar/popup/?module=), not the topbar module that opened it
    /// (e.g. the "clock" topbar module opens the "calendar" popup module).</summary>
    public string Module { get; }
    public string MonitorId { get; }

    /// <summary>Fired once, right before the underlying window actually closes. Named PopupClosed
    /// (not Closed) so it does not hide WPF's own Window.Closed RoutedEvent-backed CLR event.</summary>
    public event Action<PopupWindow>? PopupClosed;

    public PopupWindow(HostContext ctx, MonitorInfo monitor, string module, int anchorCenterXPhysical, int topYPhysical)
    {
        _ctx = ctx;
        Module = module;
        MonitorId = monitor.Id;
        _scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;
        _anchorCenterXPhysical = anchorCenterXPhysical;
        _topYPhysical = topYPhysical;
        _monLeftPhysical = monitor.Left;
        _monRightPhysical = monitor.Left + monitor.Width;
        _monHeightPhysical = monitor.Height;

        InitializeComponent();
        // Off-screen until the page reports a real size, so nothing flashes at the wrong spot/size.
        Left = -32000;
        Top = -32000;
        Width = 32;
        Height = 32;

        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => SafeClose();
        Closing += (_, _) =>
        {
            _closing = true;
            // Popups are short-lived (opened/closed repeatedly for volume/calendar/menu/control
            // center) — without an explicit Dispose each one leaves its WebView2 controller/CoreWebView2
            // process resources alive until GC finalization gets around to it, which under repeated
            // open/close can pile up.
            try { Browser.Dispose(); } catch { }
        };
        Loaded += async (_, _) => await InitBrowserAsync(monitor.Id, module, anchorCenterXPhysical).ConfigureAwait(true);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var ex = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private async Task InitBrowserAsync(string monitorId, string module, int anchorCenterXPhysical)
    {
        try
        {
            _ctx.Log.Info("popup: init " + module + " on " + monitorId + " anchor=" + anchorCenterXPhysical);
            var userDataDir = _ctx.Paths.WebView2UserDataDir + "-topbar-popup";
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir).ConfigureAwait(true);
            await Browser.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x0B, 0x0B, 0x0B);
            var s = Browser.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = App.Args.DevTools;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            Browser.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessage;
            var url = _ctx.BaseUrl + "/topbar/popup/?module=" + Uri.EscapeDataString(module)
                + "&monitor=" + Uri.EscapeDataString(monitorId)
                + "&anchor=" + anchorCenterXPhysical;
            Browser.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("popup init failed (" + module + ")", ex);
            SafeClose();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? d;
        try { d = JsonNode.Parse(e.WebMessageAsJson); }
        catch { return; }
        var type = d?["type"]?.GetValue<string>();
        if (type == "size")
        {
            var w = d!["width"]?.GetValue<double>() ?? 320;
            var h = d["height"]?.GetValue<double>() ?? 200;
            Dispatcher.BeginInvoke(() => Reposition(w, h));
        }
        else if (type == "close")
        {
            Dispatcher.BeginInvoke(SafeClose);
        }
    }

    /// <summary>
    /// Sizes/positions the window from a page-reported size (CSS px == DIU here, this app is
    /// per-monitor-DPI aware and WebView2 already renders at that scale). Horizontally centred on
    /// the anchor, clamped to the monitor; vertically pinned just below the bar.
    /// </summary>
    private void Reposition(double cssWidth, double cssHeight)
    {
        if (_closing) return;
        // A non-numeric/NaN/infinite report (malformed page message) must not corrupt placement —
        // ignore the message outright and keep whatever size/position this popup already has.
        if (double.IsNaN(cssWidth) || double.IsNaN(cssHeight) || double.IsInfinity(cssWidth) || double.IsInfinity(cssHeight)) return;
        var monitorWidthDiu = (_monRightPhysical - _monLeftPhysical) / _scale;
        var monitorHeightDiu = _monHeightPhysical / _scale;
        cssWidth = Math.Clamp(cssWidth, 60, Math.Max(60, monitorWidthDiu));
        cssHeight = Math.Clamp(cssHeight, 40, Math.Max(40, monitorHeightDiu));
        var wPhysical = cssWidth * _scale;
        var hPhysical = cssHeight * _scale;

        var leftPhysical = _anchorCenterXPhysical - wPhysical / 2.0;
        leftPhysical = Math.Max(_monLeftPhysical, Math.Min(leftPhysical, _monRightPhysical - wPhysical));
        var topPhysical = _topYPhysical;

        Width = cssWidth;
        Height = cssHeight;
        Left = leftPhysical / _scale;
        Top = topPhysical / _scale;

        _ctx.Log.Info("popup: shown at " + (int)Math.Round(leftPhysical) + "," + topPhysical
            + " " + (int)Math.Round(wPhysical) + "x" + (int)Math.Round(hPhysical) + " (" + Module + ")");

        // No SWP_NOACTIVATE: the popup must hold keyboard focus (sliders, Esc) unlike the topbar
        // itself. The window is already active after the first Show(); re-asserting activation on
        // later resizes (content growing) is harmless and does not steal WPF keyboard focus from
        // whatever child control has it, since it is the same top-level window.
        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        if (hwnd != HWND.Null)
        {
            var wPx = (int)Math.Round(wPhysical);
            var hPx = (int)Math.Round(hPhysical);
            PInvoke.SetWindowPos(hwnd, new HWND(-1), (int)Math.Round(leftPhysical), topPhysical,
                wPx, hPx, SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            // Rounded corners (13 px CSS, like the cards inside): the window is opaque, so the shape
            // comes from a window region. The system owns the region after SetWindowRgn.
            var r = (int)Math.Round(13 * _scale) * 2;
            var rgn = PInvoke.CreateRoundRectRgn(0, 0, wPx + 1, hPx + 1, r, r);
            if (!rgn.IsNull) PInvoke.SetWindowRgn(hwnd, rgn, true);
        }
    }

    /// <summary>Close once: Deactivated can fire again while already closing, and WPF throws on a
    /// second Close() (same guard as InputWindow.SafeClose / TopBarWindow's own _closing flag).</summary>
    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            PopupClosed?.Invoke(this);
            Close();
        }
        catch (InvalidOperationException ex)
        {
            _ctx.Log.Warn("popup window close: " + ex.Message);
        }
    }
}
