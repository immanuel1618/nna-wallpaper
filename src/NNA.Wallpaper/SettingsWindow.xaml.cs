using System.Windows;
using NNA.Wallpaper.Host;

namespace NNA.Wallpaper;

/// <summary>
/// Settings window (stage 8 replaces the body with a WebView2 hosting settings/index.html).
/// Single instance: <see cref="Open"/> shows the existing window or creates one.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _current;
    private readonly HostContext _ctx;

    public SettingsWindow(HostContext ctx, string? tab)
    {
        _ctx = ctx;
        InitializeComponent();
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
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
        // Stage 8: forward to the settings page.
    }
}
