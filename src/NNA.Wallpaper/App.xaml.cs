using System.Windows;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;

namespace NNA.Wallpaper;

public partial class App : Application
{
    public static CliArgs Args { get; private set; } = CliArgs.Parse(Array.Empty<string>());
    public static HostContext? Host { get; private set; }
    public static HostServices? Services { get; private set; }
    public static WallpaperEngine? Engine { get; private set; }
    public static Log? Log { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
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

        Host = new HostContext(paths, config, log, port, new NullHostApp());
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
        log.Info($"{HostInfo.AppName} {HostInfo.Version} started, port {port}, data {paths.DataDir}, headless={Args.Headless} test={Args.TestEngine}");

        if (Args.Headless)
        {
            return; // API only; the engine is not started (used by tests and the stage 2/3 gates)
        }

        try
        {
            Engine = new WallpaperEngine(Host, Dispatcher, Args.TestEngine) { DevTools = Args.DevTools };
            Engine.ExitRequested += () => Shutdown(0);
            Host.App = Engine;
            await Engine.StartAsync();
            log.Info($"engine started: {Engine.Windows.Count} monitor(s), desktop {Engine.DesktopMode}");
        }
        catch (Exception ex)
        {
            log.Error("engine start failed", ex);
            Shutdown(4);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Engine?.Dispose(); } catch { }
        Services?.Dispose();
        Log?.Info("exit " + e.ApplicationExitCode);
        base.OnExit(e);
    }
}
