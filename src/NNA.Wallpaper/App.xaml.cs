using System.Windows;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;

namespace NNA.Wallpaper;

public partial class App : Application
{
    public static CliArgs Args { get; private set; } = CliArgs.Parse(Array.Empty<string>());
    public static HostContext? Host { get; private set; }
    public static HostServices? Services { get; private set; }
    public static Log? Log { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = CliArgs.Parse(e.Args);

        var paths = Paths.Resolve(Args.DataDir);
        paths.EnsureDirectories();
        var log = new Log(paths);
        Log = log;
        var config = new ConfigStore(paths, log);
        config.Load();
        var port = Args.Port ?? config.App.ApiPort;

        IHostApp hostApp = new NullHostApp();
        Host = new HostContext(paths, config, log, port, hostApp);
        try
        {
            Services = new HostServices(Host);
            Services.Start();
        }
        catch (Exception ex)
        {
            log.Error("host start failed", ex);
            Shutdown(3);
            return;
        }
        log.Info($"{HostInfo.AppName} {HostInfo.Version} started, port {port}, data {paths.DataDir}, headless={Args.Headless}");

        if (Args.Headless)
        {
            return; // API only; the engine is not started (used by tests and the stage 2/3 gates)
        }

        // Engine and tray arrive in later stages; until then a non-headless start behaves like headless.
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Dispose();
        Log?.Info("exit " + e.ApplicationExitCode);
        base.OnExit(e);
    }
}
