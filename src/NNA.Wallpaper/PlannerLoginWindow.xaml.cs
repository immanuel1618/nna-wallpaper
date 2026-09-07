using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Planner;
using NNA.Wallpaper.Themes;

namespace NNA.Wallpaper;

/// <summary>
/// Telegram login window for NNA Planner: a WebView2 pointed at the desktop login page
/// (<see cref="Config.PlannerSettings.LoginUrl"/>?port=&lt;port&gt;). The page's Telegram Login
/// Widget redirects, inside this same WebView2, to http://127.0.0.1:&lt;port&gt;/planner/callback?...
/// which the host itself serves — no external browser involved. Single instance, like SettingsWindow.
/// </summary>
public partial class PlannerLoginWindow : Window
{
    private static PlannerLoginWindow? _current;
    private readonly HostContext _ctx;
    private bool _initStarted;

    public PlannerLoginWindow(HostContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BrandChrome.Attach(this);
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
        Loaded += async (_, _) => await InitBrowserAsync();

        // Esc cancels the login, Alt+F4 closes it like any other window; the WPF WebView2 control
        // re-raises unhandled browser accelerator keys as ordinary KeyDown on itself, which bubbles
        // up here, so both work even while the WebView2 content (the Telegram widget) has focus.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.System && e.SystemKey == Key.F4 && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    public static void Open(HostContext ctx)
    {
        if (_current is null)
        {
            _current = new PlannerLoginWindow(ctx);
            _current.Show();
        }
        else
        {
            _current.Activate();
        }
    }

    public static void CloseIfOpen() => _current?.Close();

    private async Task InitBrowserAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _ctx.Paths.WebView2UserDataDir + "-login").ConfigureAwait(true);
            await Browser.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            var settings = Browser.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;

            // The Telegram widget opens oauth.telegram.org in a popup; let WebView2 create a real
            // window for it rather than swallowing the navigation (e.Handled = true would block login).
            Browser.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = false;

            Browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                // Telegram (login widget + oauth.telegram.org popup target) and our own callback are
                // the only URLs this window ever navigates to; nothing here needs to be blocked.
                _ctx.Log.Info("planner login navigating: " + SafeHost(e.Uri));
            };

            // The callback HTML page (served by the host itself) posts "done" once login succeeded.
            Browser.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() == "done") Dispatcher.BeginInvoke(Close);
            };

            var loginUrl = _ctx.Config.App.Planner.LoginUrl;
            var sep = loginUrl.Contains('?') ? "&" : "?";
            var url = loginUrl + sep + "port=" + _ctx.Port;
            // Session-fixation guard: a fresh one-time nonce, threaded through the login page and
            // back on /planner/callback?state=; PlannerService.Callback refuses anything else. If
            // PlannerService.Current isn't up yet (should not happen — the host starts it before any
            // window can open), fall back to no state rather than throw; the callback will then 403
            // and the failure page tells the user to retry, same as an expired/foreign state would.
            var state = PlannerService.Current?.CreateLoginState();
            if (!string.IsNullOrEmpty(state)) url += "&state=" + Uri.EscapeDataString(state);
            Browser.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("planner login window init", ex);
        }
    }

    /// <summary>Host+path only — never log query strings, which could carry Telegram widget fields.</summary>
    private static string SafeHost(string uri)
    {
        try
        {
            var u = new Uri(uri);
            return u.Scheme + "://" + u.Host + (u.Port is 80 or 443 or -1 ? "" : ":" + u.Port) + u.AbsolutePath;
        }
        catch { return "?"; }
    }
}
