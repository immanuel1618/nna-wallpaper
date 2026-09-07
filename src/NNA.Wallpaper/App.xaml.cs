using System.IO;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
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

    private Mutex? _instanceMutex;
    private TrayIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = CliArgs.Parse(e.Args);

        // Single instance: a second launch forwards its flags to the running instance and exits
        // (see SingleInstance.cs). It never gets here, so nothing below runs twice.
        var mutex = SingleInstance.TryAcquire();
        if (mutex is null)
        {
            Shutdown(SingleInstance.ForwardAndExit(Args));
            return;
        }
        _instanceMutex = mutex;

        var paths = Paths.Resolve(Args.DataDir);
        paths.EnsureDirectories();
        var log = new Log(paths);
        Log = log;

        // --import with no running instance: import straight into this data dir and exit. It never
        // starts the host API or the engine (there is nothing to reload, and the CLI gate expects a
        // plain exit code: 0 on success, 2 when the source folder does not exist).
        if (Args.Import is not null)
        {
            Shutdown(RunStandaloneImport(paths, log, Args.Import));
            return;
        }

        // Every other control flag needs a running instance to act on. We just became the running
        // instance (we hold the mutex), so there is nothing listening yet: exit quietly.
        if (Args.Exit || Args.Settings || Args.Reload || Args.Pause || Args.Resume || Args.CheckUpdates)
        {
            log.Info("no running instance to receive this flag; exiting");
            Shutdown(0);
            return;
        }

        var config = new ConfigStore(paths, log);
        config.Load();
        var port = Args.Port ?? config.App.ApiPort;

        Host = new HostContext(paths, config, log, port, new HeadlessHostApp(Dispatcher, Shutdown));
        try
        {
            Services = new HostServices(Host);
            RegisterAppRoutes(Services);
            Services.Start();
        }
        catch (Exception ex)
        {
            log.Error("host start failed", ex);
            Shutdown(3);
            return;
        }
        log.Info($"{HostInfo.AppName} {HostInfo.Version} started, port {port}, data {paths.DataDir}, headless={Args.Headless} test={Args.TestEngine}");

        config.Changed += what => OnConfigChanged(config, what);

        if (Args.Headless)
        {
            return; // API only; the engine is not started (used by tests and the stage 2/3 gates)
        }

        string? engineError = null;
        try
        {
            Engine = new WallpaperEngine(Host, Dispatcher, Args.TestEngine) { DevTools = Args.DevTools };
            Engine.ExitRequested += () => Shutdown(0);
            Engine.SettingsRequested += tab => SettingsWindow.Open(Host!, tab);
            Engine.LoginRequested += () => PlannerLoginWindow.Open(Host!);
            Host.App = Engine;
            InputWindow.Attach(Engine, Host);
            await Engine.StartAsync();
            log.Info($"engine started: {Engine.Windows.Count} monitor(s), desktop {Engine.DesktopMode}");
        }
        catch (Exception ex)
        {
            log.Error("engine start failed", ex);
            engineError = ex.Message;
            Engine = null; // Host.App stays on the HeadlessHostApp fallback so --exit still works
        }

        _tray = new TrayIcon(Host, Engine);
        _tray.Show();
        if (engineError is not null)
        {
            var ru = config.App.Language.Equals("ru", StringComparison.OrdinalIgnoreCase);
            _tray.ShowBalloon("NNA Wallpaper", (ru ? "Обои не запустились: " : "Wallpaper failed to start: ") + engineError, BalloonIcon.Error);
        }

        Autostart.Apply(config.App.Autostart);
    }

    /// <summary>--import with no running instance: import, log the report, return the process exit code.</summary>
    private static int RunStandaloneImport(Paths paths, Log log, string sourceDir)
    {
        // Check the source before touching config at all: ConfigStore.Load() writes default
        // app.json/monitors.json as a side effect when they don't exist yet, and a failed import
        // must leave config untouched.
        if (!Directory.Exists(Path.GetFullPath(sourceDir)))
        {
            log.Error("import failed: source not found: " + sourceDir);
            return 2;
        }

        var config = new ConfigStore(paths, log);
        config.Load();
        try
        {
            var report = NNA.Wallpaper.Host.Import.Run(paths, config, log, sourceDir, Array.Empty<MonitorStatus>());
            log.Info("import: " + report);
            return 0;
        }
        catch (DirectoryNotFoundException ex)
        {
            log.Error("import failed: source not found", ex);
            return 2;
        }
        catch (Exception ex)
        {
            log.Error("import failed", ex);
            return 2;
        }
    }

    private void OnConfigChanged(ConfigStore config, string what)
    {
        if (what == "monitors" || what == "app" || what.StartsWith("widget:", StringComparison.Ordinal))
        {
            Engine?.ReloadWallpaper();
        }
        if (what == "app" && !Args.Headless)
        {
            Autostart.Apply(config.App.Autostart);
        }
    }

    /// <summary>
    /// Routes owned by the WPF shell rather than the host library (which we must not modify):
    /// opening windows and pause/resume need the UI thread and live objects that only exist here.
    /// /app/exit and /app/reload are already registered by <see cref="HostServices"/>.
    /// </summary>
    private void RegisterAppRoutes(HostServices services)
    {
        var api = services.Api;

        api.Map("POST", "/app/settings", req =>
        {
            var tab = req.Query("tab");
            Dispatcher.BeginInvoke(() => SettingsWindow.Open(Host!, tab));
            return req.Json(new { ok = true });
        });

        api.Map("POST", "/app/pause", req =>
        {
            var engine = Engine;
            Dispatcher.BeginInvoke(() => engine?.SetUserPause(true));
            return req.Json(new { ok = engine is not null });
        });

        api.Map("POST", "/app/resume", req =>
        {
            var engine = Engine;
            Dispatcher.BeginInvoke(() => engine?.SetUserPause(false));
            return req.Json(new { ok = engine is not null });
        });

        api.Map("POST", "/app/check-updates", async req => await req.Json(await Updates.CheckAsync(Host!).ConfigureAwait(false)).ConfigureAwait(false));

        api.Map("POST", "/app/login", req =>
        {
            Dispatcher.BeginInvoke(() => PlannerLoginWindow.Open(Host!));
            return req.Json(new { ok = true });
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        try { Engine?.Dispose(); } catch { }
        Services?.Dispose();
        Log?.Info("exit " + e.ApplicationExitCode);
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
