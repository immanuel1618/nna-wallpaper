using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Planner;
using NNA.Wallpaper.Host.Services;

namespace NNA.Wallpaper;

/// <summary>
/// Global push-to-talk hotkey for the planner voice block (docs/PLANNER.md). Registers
/// app.json's planner.hotkey (default "Ctrl+Shift+Space") with RegisterHotKey on a hidden
/// message-only window; WM_HOTKEY only fires once per physical press (MOD_NOREPEAT) and carries
/// no key-up, so "still held" is polled with GetAsyncKeyState every 50ms (a DispatcherTimer) until
/// it releases — recording starts on WM_HOTKEY and stops on the first poll that finds the key up.
/// Captured audio goes through <see cref="VoiceCaptureService"/> (host-side WASAPI, NAudio) and
/// then <see cref="PlannerService.CaptureVoiceAsync"/> via <see cref="PlannerService.Current"/>,
/// which broadcasts the same {"type":"planner","event":"captured",...} shape a browser-side mic
/// capture already produces, so the TASKS block's recognized-text/undo panel shows either the
/// same way. A "voice-rec" {on:bool} broadcast (EventsService) drives the top bar's rec indicator
/// (topbar/modules.js "rec"), if the current top bar module list includes it.
///
/// --test-audio &lt;wav path&gt; (an unrecognized CLI flag, so CliArgs.Unknown carries it through
/// unmodified — see CliArgs.cs) swaps the microphone for a fixed WAV file: useful for a manual
/// check of the whole hotkey -&gt; capture -&gt; block round trip on a machine with no working mic.
/// One hotkey press with this flag set sends the file immediately (no hold-to-record wait).
///
/// Not wired to WallpaperEngine's user-pause (that would need a pause-changed event Engine does
/// not currently raise — out of scope here); the hotkey stays registered for the app's lifetime
/// and is only unregistered by <see cref="Dispose"/> on exit, or by <see cref="Unregister"/> if a
/// caller wants to free it explicitly.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x1618;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly HostContext _ctx;
    private readonly Dispatcher _dispatcher;
    private readonly string? _testAudioPath;

    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private bool _registered;
    private uint _vk;

    private VoiceCaptureService? _voice;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _safetyTimer;
    private int _finishing;

    /// <summary>Belt-and-braces on top of VoiceCaptureService's own 60s buffer cap (which stops
    /// growing the recording and asks to finish, but still depends on the poll timer/dispatcher
    /// running to actually tear things down): if a press somehow hasn't finished 5s after the cap
    /// should have fired, force it closed rather than leave the mic open and the rec indicator lit.</summary>
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(65);

    public Hotkeys(HostContext ctx, Dispatcher dispatcher)
    {
        _ctx = ctx;
        _dispatcher = dispatcher;
        _testAudioPath = ReadTestAudioArg();
    }

    private static string? ReadTestAudioArg()
    {
        var unknown = App.Args.Unknown;
        for (var i = 0; i < unknown.Count; i++)
        {
            if (string.Equals(unknown[i], "--test-audio", StringComparison.OrdinalIgnoreCase) && i + 1 < unknown.Count)
            {
                return unknown[i + 1];
            }
        }
        return null;
    }

    /// <summary>Creates the message-only window and calls RegisterHotKey for app.json's planner.hotkey. Safe to call once; failures are logged and reflected in /planner/status.hotkey, never thrown.</summary>
    public void Register()
    {
        try
        {
            var spec = _ctx.Config.App.Planner.Hotkey;
            if (!TryParseHotkey(spec, out var mods, out var vk))
            {
                _ctx.Log.Warn("hotkey: could not parse '" + spec + "'");
                PlannerService.SetHotkeyState(false, "bad hotkey spec: " + spec);
                return;
            }

            var parms = new HwndSourceParameters("NNAPlannerHotkey")
            {
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE: message-only window, no UI, no taskbar entry
            };
            _source = new HwndSource(parms);
            _hook = WndProc;
            _source.AddHook(_hook);

            _vk = vk;
            if (RegisterHotKey(_source.Handle, HOTKEY_ID, mods | MOD_NOREPEAT, vk))
            {
                _registered = true;
                PlannerService.SetHotkeyState(true, null);
                _ctx.Log.Info("hotkey registered: " + spec);
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                _ctx.Log.Warn($"hotkey registration failed for '{spec}' (win32 error {err}, likely already bound by another app)");
                PlannerService.SetHotkeyState(false, $"registration failed (win32 error {err})");
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("hotkey register", ex);
            PlannerService.SetHotkeyState(false, ex.Message);
        }
    }

    /// <summary>Frees the hotkey without tearing down the message window (Register() can re-claim it later with a new spec).</summary>
    public void Unregister()
    {
        try
        {
            if (_registered && _source is not null) UnregisterHotKey(_source.Handle, HOTKEY_ID);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("hotkey unregister: " + ex.Message);
        }
        _registered = false;
        PlannerService.SetHotkeyState(false, null);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            OnHotkeyPressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnHotkeyPressed()
    {
        if (_pollTimer is not null) return; // a press is already being handled

        if (_testAudioPath is not null)
        {
            byte[]? bytes = null;
            try { if (File.Exists(_testAudioPath)) bytes = File.ReadAllBytes(_testAudioPath); }
            catch (Exception ex) { _ctx.Log.Warn("hotkey --test-audio read failed: " + ex.Message); }
            SendCaptured(bytes, "desktop-hotkey-test");
            return;
        }

        if (_voice is null)
        {
            _voice = new VoiceCaptureService(_ctx);
            // VoiceCaptureService's own 60s buffer cap notifies on a thread-pool thread (never
            // synchronously from its WASAPI callback); wire it to the exact same "finish" path
            // releasing the key uses, so hitting the cap behaves like an ordinary key-up.
            _voice.MaxDurationReached += OnMaxDurationReached;
        }
        if (!_voice.Start(out var err))
        {
            _ctx.Log.Warn("hotkey: mic start failed: " + err);
            return;
        }

        _finishing = 0;
        BroadcastRec(true);
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        _pollTimer.Tick += (_, _) => PollKeyState();
        _pollTimer.Start();

        // Safety net in case the cap notification above is ever lost or delayed (e.g. the dispatcher
        // is backed up): forces the same finish path 5s past where VoiceCaptureService's own cutoff
        // should have already fired, so a press can never keep the mic open indefinitely.
        _safetyTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = SafetyTimeout };
        _safetyTimer.Tick += (_, _) =>
        {
            _ctx.Log.Warn("hotkey: safety timeout (" + SafetyTimeout.TotalSeconds + "s) hit, forcing capture to finish");
            FinishCapture("desktop-hotkey-maxlen");
        };
        _safetyTimer.Start();
    }

    private void PollKeyState()
    {
        var state = GetAsyncKeyState((int)_vk);
        var stillHeld = (state & 0x8000) != 0;
        if (stillHeld) return;
        FinishCapture("desktop-hotkey");
    }

    /// <summary>Called off-thread (thread pool) when VoiceCaptureService's 60s buffer cap fires.</summary>
    private void OnMaxDurationReached() => FinishCapture("desktop-hotkey-maxlen");

    /// <summary>Stops the poll/safety timers and the capture, and sends whatever was recorded — the
    /// single path shared by an ordinary key-up (<see cref="PollKeyState"/>), the mic's own max-length
    /// cutoff (<see cref="OnMaxDurationReached"/>) and the safety-net timer, guarded so only the first
    /// caller for a given press does anything (the other two are exactly the backstops for this one).</summary>
    private void FinishCapture(string source)
    {
        if (Interlocked.Exchange(ref _finishing, 1) == 1) return;

        void StopTimers()
        {
            _pollTimer?.Stop();
            _pollTimer = null;
            _safetyTimer?.Stop();
            _safetyTimer = null;
        }
        if (_dispatcher.CheckAccess()) StopTimers();
        else _dispatcher.Invoke(StopTimers);

        var bytes = _voice?.Stop();
        BroadcastRec(false);
        SendCaptured(bytes, source);
    }

    private static void BroadcastRec(bool on) => EventsService.Current?.Broadcast(new { type = "voice-rec", on });

    private void SendCaptured(byte[]? bytes, string source)
    {
        // WAV header alone is 44 bytes: nothing shorter is real audio, don't spend an AI capture call on it.
        if (bytes is null || bytes.Length <= 44) return;
        _ = Task.Run(async () =>
        {
            try { await (PlannerService.Current?.CaptureVoiceAsync(bytes, source) ?? Task.FromResult<System.Text.Json.Nodes.JsonObject?>(null)).ConfigureAwait(false); }
            catch (Exception ex) { _ctx.Log.Error("hotkey capture send", ex); }
        });
    }

    /// <summary>"Ctrl+Shift+Space" -&gt; MOD_CONTROL|MOD_SHIFT + VK_SPACE. Modifier tokens are Ctrl/Control, Shift, Alt, Win/Windows (case-insensitive); the remaining token is parsed as a <see cref="Key"/> name (same names the settings page's hotkey capture field writes, e.g. "Space", "F9", "A", "D1").</summary>
    private static bool TryParseHotkey(string? spec, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        string? mainToken = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= MOD_CONTROL;
                    break;
                case "shift":
                    mods |= MOD_SHIFT;
                    break;
                case "alt":
                    mods |= MOD_ALT;
                    break;
                case "win":
                case "windows":
                    mods |= MOD_WIN;
                    break;
                default:
                    mainToken = part;
                    break;
            }
        }
        if (mainToken is null) return false;
        if (!Enum.TryParse<Key>(mainToken, true, out var key)) return false;

        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public void Dispose()
    {
        try { _pollTimer?.Stop(); } catch { /* best-effort */ }
        _pollTimer = null;
        try { _safetyTimer?.Stop(); } catch { /* best-effort */ }
        _safetyTimer = null;
        try { if (_voice is not null) _voice.MaxDurationReached -= OnMaxDurationReached; } catch { /* best-effort */ }
        try { _voice?.Stop(); } catch { /* best-effort */ }
        Unregister();
        try
        {
            if (_source is not null && _hook is not null) _source.RemoveHook(_hook);
            _source?.Dispose();
        }
        catch { /* best-effort */ }
        _source = null;
    }
}
