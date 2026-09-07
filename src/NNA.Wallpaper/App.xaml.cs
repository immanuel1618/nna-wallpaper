using System.IO;
using System.Linq;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using NNA.Wallpaper.Dock;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;
using NNA.Wallpaper.Taskbar;
using NNA.Wallpaper.TopBar;
using System.Text.Json.Nodes;

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
    private TaskbarStyler? _taskbar;
    private TopBarManager? _topBar;
    private DockManager? _dock;
    private Hotkeys? _hotkeys;
    /// <summary>Sorted monitor ids as of the last known-good config, used by <see cref="OnConfigChanged"/>
    /// to tell "a monitor was added/removed" (needs a full reload) from "a block moved" (patched live
    /// by the wallpaper pages over /events).</summary>
    private List<string>? _lastMonitorIds;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = CliArgs.Parse(e.Args);

        // A wallpaper must not die because one window misbehaved: log UI-thread exceptions and carry on.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log?.Error("unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log?.Error("unhandled exception" + (ex.IsTerminating ? " (terminating)" : ""), ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log?.Error("unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        // Single instance: a second launch forwards its flags to the running instance and exits
        // (see SingleInstance.cs). It never gets here, so nothing below runs twice.
        var mutex = SingleInstance.TryAcquire(Paths.Resolve(Args.DataDir).DataDir);
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
        _lastMonitorIds = SortedMonitorIds(config.Monitors);
        var port = Args.Port ?? config.App.ApiPort;

        Host = new HostContext(paths, config, log, port, new HeadlessHostApp(Dispatcher, Shutdown))
        {
            TestEndpoints = Args.Headless || Args.TestEngine,
        };
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

        try
        {
            _taskbar = new TaskbarStyler(Host, Dispatcher);
            _taskbar.Apply();
            _topBar = new TopBarManager(Host, Dispatcher);
            _topBar.Apply();
            _dock = new DockManager(Host, Dispatcher);
            _dock.Apply();
        }
        catch (Exception ex)
        {
            log.Error("taskbar/top bar start failed", ex);
        }

        try
        {
            // Global push-to-talk hotkey for the planner voice block (docs/PLANNER.md). Headless runs
            // never reach this line (they return earlier), so /planner/status.hotkey.registered stays
            // false there by construction, not by a headless-specific branch here.
            _hotkeys = new Hotkeys(Host, Dispatcher);
            _hotkeys.Register();
        }
        catch (Exception ex)
        {
            log.Error("hotkey start failed", ex);
        }
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

    /// <summary>
    /// Decides whether a config change needs a full <c>ReloadWallpaper()</c> (visible flicker) or can
    /// be left to the wallpaper pages, which patch themselves live over the /events WebSocket
    /// (<see cref="Services.EventsService"/> pushes a "config-changed" message for every change).
    /// With LiveUpdates off we fall back to the old behaviour entirely. With it on, only an actual
    /// change to the *set* of monitor ids (a monitor plugged in/out, not a block moved on one) still
    /// forces a reload — every other case (app, monitors-without-id-change, widget:*) is live-patched.
    /// </summary>
    private void OnConfigChanged(ConfigStore config, string what)
    {
        var liveUpdates = config.App.LiveUpdates;
        bool reload;
        if (!liveUpdates)
        {
            reload = what == "monitors" || what == "app" || what.StartsWith("widget:", StringComparison.Ordinal);
        }
        else if (what == "monitors")
        {
            var ids = SortedMonitorIds(config.Monitors);
            reload = _lastMonitorIds is not null && !ids.SequenceEqual(_lastMonitorIds);
        }
        else
        {
            reload = false;
        }
        if (what == "monitors")
        {
            _lastMonitorIds = SortedMonitorIds(config.Monitors);
        }
        if (reload)
        {
            Engine?.ReloadWallpaper();
        }
        if (what == "app")
        {
            Dispatcher.BeginInvoke(() => { try { _taskbar?.Apply(); _topBar?.Apply(); _dock?.Apply(); } catch (Exception ex) { Log?.Error("taskbar/top bar apply", ex); } });
        }
        if (what == "app" && !Args.Headless)
        {
            Autostart.Apply(config.App.Autostart);
        }
    }

    private static List<string> SortedMonitorIds(MonitorsConfig monitors) =>
        (monitors.Monitors ?? new List<MonitorLayout>())
            .Select(m => m.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

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
        api.Map("POST", "/app/update", async req => await req.Json(await Updates.ApplyAsync(Host!).ConfigureAwait(false)).ConfigureAwait(false));

        // ---- taskbar and top bar ----
        api.Map("GET", "/taskbar/status", async req =>
        {
            var status = await Dispatcher.InvokeAsync(() => _taskbar?.Status() ?? new JsonObject { ["enabled"] = false, ["note"] = "taskbar module not started", ["noteCode"] = "not-started" });
            status["topBar"] = new JsonObject { ["enabled"] = Host!.Config.App.TopBar.Enabled, ["bars"] = _topBar?.Bars.Count ?? 0 };
            await req.Text(status.ToJsonString(Json.Api), "application/json; charset=utf-8").ConfigureAwait(false);
        });
        api.Map("POST", "/taskbar/apply", async req =>
        {
            Host!.Config.Load();
            await Dispatcher.InvokeAsync(() => { _taskbar?.Apply(); _topBar?.Apply(); });
            await req.Json(new { ok = true }).ConfigureAwait(false);
        });
        api.Map("POST", "/taskbar/reset", async req =>
        {
            var ok = await Dispatcher.InvokeAsync(() => _taskbar?.Reset() ?? false);
            await req.Json(new { ok, message = ok ? "windows settings restored from backup" : "no backup yet" }).ConfigureAwait(false);
        });
        api.Map("POST", "/taskbar/restart-explorer", async req =>
        {
            await req.Json(new { ok = true, message = "restarting explorer" }).ConfigureAwait(false);
            _ = Task.Run(TaskbarStyler.RestartExplorer);
        });
        api.Map("GET", "/taskbar/presets", req => req.Text(PresetStore.List(Host!).ToJsonString(Json.Api), "application/json; charset=utf-8"));
        api.Map("GET", "/taskbar/preset", async req =>
        {
            var node = PresetStore.Read(Host!, req.Query("id"));
            if (node is null) { await req.Error(404, "no such preset").ConfigureAwait(false); return; }
            await req.Text(node.ToJsonString(Json.Api), "application/json; charset=utf-8").ConfigureAwait(false);
        });
        api.Map("POST", "/taskbar/presets", async req =>
        {
            var body = Json.ParseNode(await req.ReadBodyAsync().ConfigureAwait(false)) as JsonObject;
            var saved = body is not null && PresetStore.SaveUser(Host!, body, out var id) ? id : null;
            if (saved is null) { await req.Error(400, "preset needs id, name, taskbar and topBar").ConfigureAwait(false); return; }
            await req.Json(new { ok = true, id = saved }).ConfigureAwait(false);
        });

        api.Map("POST", "/app/login", req =>
        {
            Dispatcher.BeginInvoke(() => PlannerLoginWindow.Open(Host!));
            return req.Json(new { ok = true });
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _hotkeys?.Dispose(); } catch { }
        try { _dock?.Dispose(); } catch { }
        try { _topBar?.Dispose(); } catch { }
        try { _taskbar?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { Engine?.Dispose(); } catch { }
        Services?.Dispose();
        Log?.Info("exit " + e.ApplicationExitCode);
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
