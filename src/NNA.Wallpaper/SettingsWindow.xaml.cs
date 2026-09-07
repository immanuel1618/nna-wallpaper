using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Themes;

namespace NNA.Wallpaper;

/// <summary>
/// Settings window: a WebView2 pointed at the host's own /settings/ page. Single instance:
/// <see cref="Open"/> shows the existing window or creates one; <see cref="NavigateTab"/> asks the
/// page (already loaded) to switch tabs instead of reloading.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _current;
    private readonly HostContext _ctx;
    private string? _pendingTab;
    private readonly HttpClient _health = new() { Timeout = TimeSpan.FromSeconds(3) };
    private DispatcherTimer? _healthTimer;

    public SettingsWindow(HostContext ctx, string? tab)
    {
        _ctx = ctx;
        InitializeComponent();
        // Title stays the fixed brand caption ("NNA WALLPAPER", set in XAML) drawn by BrandWindow's
        // own title bar and used for the taskbar/Alt+Tab entry alike — the per-language distinction
        // ("Настройки" vs "Settings") lives inside the settings page itself, not the window chrome.
        Chrome.SetVersion(this, "v" + HostInfo.Version);

        BrandChrome.Attach(this);
        WindowSettingsStore.Restore(this, ctx, "settings");
        WindowSettingsStore.SaveOnClose(this, ctx, "settings");

        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; StopHealthPolling(); };
        Loaded += async (_, _) => await InitAsync(tab).ConfigureAwait(true);
        StartHealthPolling();

        // The WPF WebView2 control re-raises unhandled browser accelerator keys (Alt+F4 included) as
        // ordinary WPF KeyDown on itself, which bubbles up here — so Alt+F4 closes the window even
        // while the WebView2 content has keyboard focus, not just when the title bar/chrome does.
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.System
                && e.SystemKey == System.Windows.Input.Key.F4
                && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Alt)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    public static void Open(HostContext ctx, string? tab)
    {
        if (_current is null)
        {
            _current = new SettingsWindow(ctx, tab);
            _current.Show();
        }
        else
        {
            _current.NavigateTab(tab);
            if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
            _current.Activate();
        }
    }

    public void NavigateTab(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return;
        if (Browser.CoreWebView2 is null)
        {
            _pendingTab = tab;
            return;
        }
        var script = "window.nnaSettings && window.nnaSettings.showTab(" + JsonSerializer.Serialize(tab) + ")";
        _ = Browser.ExecuteScriptAsync(script);
    }

    private async System.Threading.Tasks.Task InitAsync(string? tab)
    {
        try
        {
            var userDataDir = _ctx.Paths.WebView2UserDataDir + "-settings";
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir).ConfigureAwait(true);
            await Browser.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            var settings = Browser.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = App.Args.DevTools;

            // Microphone for the planner page's "test microphone" control (settings/pages/planner.js);
            // everything else denied. Same allow/deny split as the wallpaper window's own handler
            // (Engine/WallpaperWindow.cs), just for this separate WebView2 environment/window.
            Browser.CoreWebView2.PermissionRequested += (_, e) =>
            {
                e.State = e.PermissionKind == CoreWebView2PermissionKind.Microphone
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
            };

            var lang = string.IsNullOrEmpty(_ctx.Config.App.Language) ? "ru" : _ctx.Config.App.Language;
            var query = "?token=" + Uri.EscapeDataString(_ctx.Config.App.ApiToken)
                        + "&lang=" + Uri.EscapeDataString(lang)
                        + (string.IsNullOrEmpty(tab) ? "" : "&tab=" + Uri.EscapeDataString(tab));
            Browser.CoreWebView2.Navigate(_ctx.BaseUrl + "/settings/" + query);

            if (!string.IsNullOrEmpty(_pendingTab))
            {
                NavigateTab(_pendingTab);
                _pendingTab = null;
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("settings window init failed", ex);
        }
    }

    /// <summary>Polls the host's own /health every 5s to drive the status dot in the title bar.</summary>
    private void StartHealthPolling()
    {
        _healthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _healthTimer.Tick += async (_, _) => await PollHealthAsync().ConfigureAwait(true);
        _healthTimer.Start();
        _ = PollHealthAsync();
    }

    private void StopHealthPolling()
    {
        _healthTimer?.Stop();
        _healthTimer = null;
        _health.Dispose();
    }

    private async System.Threading.Tasks.Task PollHealthAsync()
    {
        var ok = false;
        try
        {
            using var resp = await _health.GetAsync(_ctx.BaseUrl + "/health").ConfigureAwait(true);
            ok = resp.IsSuccessStatusCode;
        }
        catch
        {
            ok = false;
        }
        Chrome.SetHealthOk(this, ok);
    }
}
