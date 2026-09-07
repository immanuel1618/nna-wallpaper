using System.Windows;
using NNA.Wallpaper.Host;

namespace NNA.Wallpaper;

/// <summary>
/// Telegram login window for NNA Planner (stage 7 replaces the body with a WebView2 on the login page).
/// </summary>
public partial class PlannerLoginWindow : Window
{
    private static PlannerLoginWindow? _current;
    private readonly HostContext _ctx;

    public PlannerLoginWindow(HostContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
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
}
