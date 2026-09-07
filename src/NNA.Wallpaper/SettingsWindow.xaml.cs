using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NNA.Wallpaper.Host;

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

    public SettingsWindow(HostContext ctx, string? tab)
    {
        _ctx = ctx;
        InitializeComponent();
        Title = ctx.Config.App.Language == "ru" ? "NNA Wallpaper — Настройки" : "NNA Wallpaper — Settings";
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
        Loaded += async (_, _) => await InitAsync(tab).ConfigureAwait(true);
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
}
