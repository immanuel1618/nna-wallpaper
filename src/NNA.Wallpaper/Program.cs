using System;
using Velopack;

namespace NNA.Wallpaper;

/// <summary>
/// Custom entry point: Velopack must run first (it handles install/update/uninstall hooks
/// and exits early when invoked by the installer), then the WPF application starts.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
