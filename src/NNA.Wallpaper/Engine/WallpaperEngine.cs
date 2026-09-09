using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Services;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace NNA.Wallpaper.Engine;

/// <summary>
/// Owns the desktop layer, one WallpaperWindow per monitor, the fullscreen pause logic, the input
/// bridge and the watchdog that re-attaches everything when explorer recreates the desktop.
/// Implements IHostApp so the local API can report and control the engine, and
/// <see cref="ICapturesPreview"/> so <c>GET /widgets/&lt;id&gt;/preview.png</c> (WidgetsService.cs)
/// can ask for a live snapshot instead of always falling back to the static preview image.
/// </summary>
public sealed class WallpaperEngine : IHostApp, ICapturesPreview, IDisposable
{
    private readonly HostContext _ctx;
    private readonly Log _log;
    private readonly Dispatcher _dispatcher;
    private readonly List<WallpaperWindow> _windows = new();
    private readonly DesktopHost _desktop;
    private readonly DispatcherTimer _pauseTimer;
    private readonly DispatcherTimer _watchdog;
    private CoreWebView2Environment? _env;
    private InputBridge? _input;
    private bool _globalPause;
    private bool _userPause;
    private bool _sessionLocked;
    private bool _reattaching;
    private bool _disposed;

    public bool TestMode { get; }
    public bool DevTools { get; set; }
    public bool Installed { get; }
    public event Action<string, string>? PageMessage; // (monitorId, json)
    public event Action? ExitRequested;
    public event Action<string?>? SettingsRequested;
    public event Action? LoginRequested;
    /// <summary>(target, screenX, screenY, placeholder, onSubmit) — the app shows InputWindow.</summary>
    public event Action<string, int, int, string?, Action<string>>? InputRequested;

    public WallpaperEngine(HostContext ctx, Dispatcher dispatcher, bool testMode)
    {
        _ctx = ctx;
        _log = ctx.Log;
        _dispatcher = dispatcher;
        TestMode = testMode;
        _desktop = new DesktopHost(_log);
        Installed = DetectInstalled();
        _pauseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => CheckPause(), dispatcher);
        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Watchdog(), dispatcher);
    }

    private static bool DetectInstalled()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            return dir.Name.Equals("current", StringComparison.OrdinalIgnoreCase)
                && dir.Parent is not null && File.Exists(Path.Combine(dir.Parent.FullName, "Update.exe"));
        }
        catch { return false; }
    }

    public IReadOnlyList<WallpaperWindow> Windows => _windows;
    public string DesktopMode => _desktop.Mode;

    public async Task StartAsync()
    {
        if (!_desktop.Attach()) throw new InvalidOperationException("desktop layer not found");

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling",
        };
        _env = await CoreWebView2Environment.CreateAsync(null, _ctx.Paths.WebView2UserDataDir, options);
        _log.Info("webview2 runtime " + _env.BrowserVersionString);

        await CreateWindowsAsync();

        _input = new InputBridge(_desktop, () => _windows, _log, _dispatcher);
        _input.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _pauseTimer.Start();
        _watchdog.Start();
    }

    private async Task CreateWindowsAsync()
    {
        var hosting = _ctx.Config.App.Engine.Hosting;
        foreach (var m in DisplayMonitors.Enumerate())
        {
            var w = new WallpaperWindow(m, _desktop.Parent, _log, hosting) { DevTools = DevTools };
            var id = m.Id;
            w.WebMessage += json => PageMessage?.Invoke(id, json);
            _windows.Add(w);
            if (TestMode) w.NavigateToString(TestPage(m));
            else w.Navigate(PageUrl(m));
            await w.InitAsync(_env!);
        }
    }

    public string PageUrl(MonitorInfo m) =>
        _ctx.BaseUrl + "/wallpaper/?monitor=" + Uri.EscapeDataString(m.Id);

    private string TestPage(MonitorInfo m)
    {
        var id = System.Net.WebUtility.HtmlEncode(m.Id);
        var token = _ctx.Config.App.ApiToken;
        return "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;height:100%;background:#123456;color:#fff;font:bold 120px system-ui,sans-serif;user-select:none;overflow:hidden}"
             + "#t{display:flex;align-items:center;justify-content:center;height:100%;text-align:center}#c{position:fixed;left:24px;bottom:24px;font-size:40px;opacity:.8}</style></head>"
             + "<body><div id='t'>" + id + "</div><div id='c'>clicks: 0</div><script>"
             + "var n=0,id=" + JsonValue.Create(m.Id)!.ToJsonString() + ",port=" + _ctx.Port + ",token=" + JsonValue.Create(token)!.ToJsonString() + ";"
             + "function send(type,e){fetch('http://127.0.0.1:'+port+'/test/event?t='+encodeURIComponent(token),{method:'POST',body:JSON.stringify({type:type,monitor:id,x:e.clientX,y:e.clientY,button:e.button})}).catch(function(){});}"
             + "document.body.addEventListener('mousedown',function(e){n++;document.getElementById('c').textContent='clicks: '+n+' ('+e.clientX+','+e.clientY+')';send('click',e);});"
             + "var mv=0;document.body.addEventListener('mousemove',function(e){if((++mv%30)===0)send('move',e);});"
             + "</script></body></html>";
    }

    // ---- pause -------------------------------------------------------------------------------------

    private void CheckPause()
    {
        if (_disposed) return;
        try
        {
            var pauseOnFullscreen = _ctx.Config.App.PauseOnFullscreen;
            _globalPause = pauseOnFullscreen && FullscreenWatcher.GlobalFullscreen();
            var rect = pauseOnFullscreen ? FullscreenWatcher.ForegroundAppRect(out _) : null;
            foreach (var w in _windows)
            {
                var covered = rect is RECT r && FullscreenWatcher.Covers(r, w.Monitor);
                w.SetPaused(_userPause || _sessionLocked || _globalPause || covered);
            }
        }
        catch (Exception ex)
        {
            _log.Error("pause check", ex);
        }
    }

    public void SetUserPause(bool paused)
    {
        _userPause = paused;
        // A press held down at the exact moment of pausing must not stay "captured" against a window
        // that will ignore all further input until resumed — see InputBridge.Reset.
        if (paused) _input?.Reset();
        CheckPause();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _sessionLocked = e.Reason switch
        {
            SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => true,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect => false,
            _ => _sessionLocked,
        };
        _dispatcher.BeginInvoke(CheckPause);
    }

    // ---- watchdog ----------------------------------------------------------------------------------

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _log.Info("display settings changed");
        _dispatcher.BeginInvoke(async () => await ReattachAsync("display change"));
    }

    private void Watchdog()
    {
        if (_disposed || _reattaching) return;
        var parentAlive = _desktop.IsAlive();
        var windowsAlive = _windows.Count > 0 && _windows.All(w => w.IsAlive);
        if (parentAlive && windowsAlive)
        {
            foreach (var w in _windows) w.EnsureBottom(); // keep the surface below the icon layer
            return;
        }
        _ = ReattachAsync(parentAlive ? "wallpaper window destroyed" : "desktop layer destroyed");
    }

    public async Task ReattachAsync(string reason)
    {
        if (_reattaching || _disposed) return;
        _reattaching = true;
        try
        {
            _log.Warn("reattach: " + reason);
            foreach (var w in _windows) w.Dispose();
            _windows.Clear();
            // The old WallpaperWindow instances are gone: drop any stale capture/hover state that
            // pointed at them, or InputBridge would post/send into disposed windows once new ones
            // come up (or a phantom Leave would silently vanish since the old target is dead).
            _input?.Reset();
            if (!_desktop.Attach())
            {
                _log.Error("reattach: desktop layer not found, retrying later");
                return;
            }
            await CreateWindowsAsync();
            DesktopHost.RefreshDesktop();
        }
        catch (Exception ex)
        {
            _log.Error("reattach failed", ex);
        }
        finally
        {
            _reattaching = false;
        }
    }

    // ---- IHostApp ----------------------------------------------------------------------------------

    public IReadOnlyList<MonitorStatus> Monitors => _windows.Select(w => new MonitorStatus(
        w.Monitor.Id, w.Monitor.Name, w.Monitor.Width, w.Monitor.Height, w.Monitor.Left, w.Monitor.Top,
        w.Ready && !w.Paused && w.IsAlive, w.Paused, w.Monitor.Scale)).ToList();

    public void OpenSettings(string? tab) => _dispatcher.BeginInvoke(() => SettingsRequested?.Invoke(tab));
    public void OpenPlannerLogin() => _dispatcher.BeginInvoke(() => LoginRequested?.Invoke());
    public void RequestExit() => _dispatcher.BeginInvoke(() => ExitRequested?.Invoke());
    public void RequestTextInput(string target, int screenX, int screenY, string? placeholder, Action<string> onSubmit) =>
        _dispatcher.BeginInvoke(() => InputRequested?.Invoke(target, screenX, screenY, placeholder, onSubmit));

    public void ReloadWallpaper() => _dispatcher.BeginInvoke(() =>
    {
        foreach (var w in _windows)
        {
            if (!TestMode) w.Navigate(PageUrl(w.Monitor));
            else w.Reload();
        }
    });

    public void PostToPages(string json, string? monitorId = null) => _dispatcher.BeginInvoke(() =>
    {
        foreach (var w in _windows)
        {
            if (monitorId is null || string.Equals(w.Monitor.Id, monitorId, StringComparison.OrdinalIgnoreCase)) w.PostJson(json);
        }
    });

    /// <summary>ICapturesPreview: PNG of the whole wallpaper window for one monitor, or null when
    /// that monitor isn't running (unknown id, or the window's WebView2 controller isn't ready
    /// yet). Must hop onto the WPF dispatcher — WallpaperWindow/WebView2 are UI-thread affine, but
    /// this is called from a local-API request handler running on a thread-pool thread.</summary>
    public Task<byte[]?> CapturePreviewAsync(string monitorId)
    {
        var tcs = new TaskCompletionSource<byte[]?>();
        _dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var w = _windows.FirstOrDefault(w => string.Equals(w.Monitor.Id, monitorId, StringComparison.OrdinalIgnoreCase));
                tcs.TrySetResult(w is null ? null : await w.CapturePngAsync());
            }
            catch (Exception ex)
            {
                _log.Error("capture preview " + monitorId, ex);
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pauseTimer.Stop();
        _watchdog.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _input?.Dispose();
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();
        DesktopHost.RefreshDesktop();
    }
}
