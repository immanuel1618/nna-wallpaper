using System.Windows;

namespace NNA.Wallpaper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Skeleton (stage 1): nothing to show yet. Later stages wire the engine, host and tray here.
        Shutdown(0);
    }
}
