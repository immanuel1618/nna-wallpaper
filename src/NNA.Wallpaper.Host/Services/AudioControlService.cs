using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Win32;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET/PUT /audio/volume, /audio/outputs, /audio/output, /audio/sessions, /audio/session,
/// /audio/session-icon, /audio/mic — output volume/mute/device, per-app session mixer, and
/// microphone volume/mute, all through NAudio's CoreAudioApi (WASAPI). Device switching goes
/// through the undocumented IPolicyConfig COM interface (see PolicyConfig.cs).
///
/// Live updates: AudioEndpointVolume.OnVolumeNotification pushes "audio-changed"/"mic-changed"
/// over /events (EventsService.Broadcast), throttled to one message per 200 ms per event type.
/// "session-changed" is emitted when a 1 s poll notices the session id set changed (new app
/// started/stopped playing). An MMNotificationClient watches for the system default device
/// changing (e.g. the owner switches speakers from Windows itself) and re-resolves the cached
/// MMDevice/AudioEndpointVolume so this service never talks to a stale endpoint.
/// </summary>
public sealed class AudioControlService : IHostService, IDisposable
{
    private const int IconSize = 32;
    private const int ThrottleMs = 200;

    private readonly HostContext _ctx;
    private readonly EventsService _events;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationClient _notify;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollTask;

    private readonly object _renderLock = new();
    private MMDevice? _renderDevice;
    private AudioEndpointVolume? _renderVolume;

    private readonly object _captureLock = new();
    private MMDevice? _captureDevice;
    private AudioEndpointVolume? _captureVolume;

    private readonly object _throttleLock = new();
    private readonly Dictionary<string, DateTime> _lastEventAt = new();

    private HashSet<string> _lastSessionIds = new();

    public AudioControlService(HostContext ctx, EventsService events)
    {
        _ctx = ctx;
        _events = events;
        _notify = new NotificationClient(this);
        try { _enumerator.RegisterEndpointNotificationCallback(_notify); }
        catch (Exception ex) { _ctx.Log.Warn("audio: register default-device notifications failed: " + ex.Message); }
        _pollTask = Task.Run(() => SessionPollLoopAsync(_cts.Token));
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/audio/volume", GetVolume);
        api.Map("PUT", "/audio/volume", PutVolume);
        api.Map("GET", "/audio/outputs", GetOutputs);
        api.Map("PUT", "/audio/output", PutOutput);
        api.Map("GET", "/audio/sessions", GetSessions);
        api.Map("PUT", "/audio/session", PutSession);
        api.Map("GET", "/audio/session-icon", GetSessionIcon);
        api.Map("GET", "/audio/mic", GetMic);
        api.Map("PUT", "/audio/mic", PutMic);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _pollTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _enumerator.UnregisterEndpointNotificationCallback(_notify); } catch { }

        lock (_renderLock)
        {
            UnsubscribeRenderUnlocked();
            try { _renderDevice?.Dispose(); } catch { }
            _renderDevice = null;
        }
        lock (_captureLock)
        {
            UnsubscribeCaptureUnlocked();
            try { _captureDevice?.Dispose(); } catch { }
            _captureDevice = null;
        }
        try { _enumerator.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ render device (output volume)

    private MMDevice GetRenderDevice(out AudioEndpointVolume volume)
    {
        lock (_renderLock)
        {
            if (_renderDevice is null)
            {
                var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _renderVolume = device.AudioEndpointVolume;
                _renderVolume.OnVolumeNotification += OnRenderVolumeNotification;
                _renderDevice = device;
            }
            volume = _renderVolume!;
            return _renderDevice;
        }
    }

    private void UnsubscribeRenderUnlocked()
    {
        if (_renderVolume is not null)
        {
            try { _renderVolume.OnVolumeNotification -= OnRenderVolumeNotification; } catch { }
            _renderVolume = null;
        }
    }

    /// <summary>Called by <see cref="NotificationClient"/> when Windows reports a new default render device (Multimedia role).</summary>
    private void InvalidateRenderDevice()
    {
        lock (_renderLock)
        {
            UnsubscribeRenderUnlocked();
            try { _renderDevice?.Dispose(); } catch { }
            _renderDevice = null;
        }
        try
        {
            var device = GetRenderDevice(out var volume);
            BroadcastThrottled("audio-changed", () => new JsonObject
            {
                ["type"] = "audio-changed",
                ["volume"] = (int)Math.Round(volume.MasterVolumeLevelScalar * 100),
                ["muted"] = volume.Mute,
            });
            _ = device;
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("audio: re-resolve default render device failed: " + ex.Message);
        }
    }

    private void OnRenderVolumeNotification(AudioVolumeNotificationData data)
    {
        BroadcastThrottled("audio-changed", () => new JsonObject
        {
            ["type"] = "audio-changed",
            ["volume"] = (int)Math.Round(data.MasterVolume * 100),
            ["muted"] = data.Muted,
        });
    }

    private Task GetVolume(ApiRequest req)
    {
        try
        {
            var device = GetRenderDevice(out var volume);
            return req.Json(RenderVolumeBody(device, volume));
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("GET /audio/volume", ex);
            return req.Error(500, ex.Message);
        }
    }

    private async Task PutVolume(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject;
        try
        {
            var device = GetRenderDevice(out var volume);
            lock (_renderLock)
            {
                if (node?["volume"] is JsonValue vv && vv.TryGetValue(out int vol))
                    volume.MasterVolumeLevelScalar = Math.Clamp(vol, 0, 100) / 100f;
                if (node?["muted"] is JsonValue mv && mv.TryGetValue(out bool muted))
                    volume.Mute = muted;
            }
            await req.Json(RenderVolumeBody(device, volume)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("PUT /audio/volume", ex);
            await req.Error(500, ex.Message).ConfigureAwait(false);
        }
    }

    private static JsonObject RenderVolumeBody(MMDevice device, AudioEndpointVolume volume) => new()
    {
        ["volume"] = (int)Math.Round(volume.MasterVolumeLevelScalar * 100),
        ["muted"] = volume.Mute,
        ["device"] = new JsonObject { ["id"] = device.ID, ["name"] = device.FriendlyName },
    };

    // ------------------------------------------------------------------ /audio/outputs, /audio/output

    private Task GetOutputs(ApiRequest req)
    {
        string defaultId = "";
        try
        {
            using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
        }
        catch { /* no default render device — leave defaultId empty, nothing will match */ }

        var arr = new JsonArray();
        var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var i = 0; i < collection.Count; i++)
        {
            using var d = collection[i];
            arr.Add(new JsonObject
            {
                ["id"] = d.ID,
                ["name"] = d.FriendlyName,
                ["default"] = d.ID == defaultId,
            });
        }
        return req.Json(new JsonObject { ["devices"] = arr });
    }

    private async Task PutOutput(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject;
        var id = node?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
        {
            await req.Error(400, "missing id").ConfigureAwait(false);
            return;
        }

        if (!PolicyConfig.TrySetDefaultEndpoint(id, out var error))
        {
            _ctx.Log.Warn("PUT /audio/output unsupported: " + error);
            await req.Json(new { ok = false, error = "unsupported" }).ConfigureAwait(false);
            return;
        }

        string? name = null;
        try
        {
            using var dev = _enumerator.GetDevice(id);
            name = dev.FriendlyName;
        }
        catch { /* device vanished right after switching to it — id still echoed back */ }

        InvalidateRenderDevice();
        await req.Json(new { ok = true, device = new { id, name } }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ /audio/sessions, /audio/session

    private Task GetSessions(ApiRequest req)
    {
        try
        {
            var arr = new JsonArray();
            foreach (var s in EnumerateSessions())
            {
                arr.Add(SessionJson(s));
                s.Control.Dispose();
            }
            return req.Json(new JsonObject { ["sessions"] = arr });
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("GET /audio/sessions", ex);
            return req.Error(500, ex.Message);
        }
    }

    private async Task PutSession(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject;
        var id = node?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
        {
            await req.Error(400, "missing id").ConfigureAwait(false);
            return;
        }

        try
        {
            foreach (var s in EnumerateSessions())
            {
                if (s.Id != id) { s.Control.Dispose(); continue; }

                var simple = s.Control.SimpleAudioVolume;
                if (node?["volume"] is JsonValue vv && vv.TryGetValue(out int vol))
                    simple.Volume = Math.Clamp(vol, 0, 100) / 100f;
                if (node?["muted"] is JsonValue mv && mv.TryGetValue(out bool muted))
                    simple.Mute = muted;

                var refreshed = SessionJson(s);
                s.Control.Dispose();
                BroadcastThrottled("session-changed", () => new JsonObject { ["type"] = "session-changed" });
                await req.Json(refreshed).ConfigureAwait(false);
                return;
            }
            await req.Error(404, "session not found").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("PUT /audio/session", ex);
            await req.Error(500, ex.Message).ConfigureAwait(false);
        }
    }

    private sealed record SessionEntry(string Id, int Pid, string Name, bool System, AudioSessionControl Control);

    private JsonObject SessionJson(SessionEntry s)
    {
        var simple = s.Control.SimpleAudioVolume;
        float peak = 0f;
        try { peak = s.Control.AudioMeterInformation.MasterPeakValue; } catch { /* meter can throw for a just-expired session */ }
        return new JsonObject
        {
            ["id"] = s.Id,
            ["pid"] = s.Pid,
            ["name"] = s.Name,
            ["icon"] = "/audio/session-icon?pid=" + s.Pid,
            ["volume"] = (int)Math.Round(simple.Volume * 100),
            ["muted"] = simple.Mute,
            ["peak"] = Math.Clamp(peak, 0f, 1f),
            ["system"] = s.System,
        };
    }

    /// <summary>
    /// Live AudioSessionControl handles for every non-expired session on the default render
    /// device. Caller owns disposing each entry's Control once done with it (SessionJson only
    /// reads; PutSession disposes after mutating).
    /// </summary>
    private List<SessionEntry> EnumerateSessions()
    {
        var device = GetRenderDevice(out _);
        var mgr = device.AudioSessionManager;
        mgr.RefreshSessions();
        var sessions = mgr.Sessions;
        var result = new List<SessionEntry>(sessions.Count);
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.State == AudioSessionState.AudioSessionStateExpired) { s.Dispose(); continue; }

            var pid = (int)s.GetProcessID;
            var isSystem = s.IsSystemSoundsSession;
            var name = s.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@'))
            {
                name = isSystem ? "Система" : QueryProcessName(pid) ?? ("pid " + pid);
            }
            var id = s.GetSessionInstanceIdentifier;
            if (string.IsNullOrEmpty(id)) id = pid + ":" + s.GetSessionIdentifier;

            result.Add(new SessionEntry(id, pid, name, isSystem, s));
        }
        return result;
    }

    private static string? QueryProcessName(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    private async Task SessionPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ids = new HashSet<string>();
                foreach (var s in EnumerateSessions())
                {
                    ids.Add(s.Id);
                    s.Control.Dispose();
                }
                if (!ids.SetEquals(_lastSessionIds))
                {
                    _lastSessionIds = ids;
                    BroadcastThrottled("session-changed", () => new JsonObject { ["type"] = "session-changed" });
                }
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("audio session poll", ex);
            }
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    // ------------------------------------------------------------------ /audio/session-icon

    private async Task GetSessionIcon(ApiRequest req)
    {
        if (!int.TryParse(req.Query("pid"), out var pid) || pid <= 0)
        {
            await req.Error(400, "bad pid").ConfigureAwait(false);
            return;
        }

        var outFile = Path.Combine(_ctx.Paths.IconsDir, "audio-session-" + pid + ".png");
        if (!File.Exists(outFile))
        {
            var exePath = QueryExePath(pid);
            var ok = exePath is not null && await Task.Run(() => ExtractSessionIcon(exePath, outFile)).ConfigureAwait(false);
            if (!ok)
            {
                await req.Error(404, "no icon").ConfigureAwait(false);
                return;
            }
        }

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(outFile).ConfigureAwait(false); }
        catch (IOException) { await req.Error(404, "no icon").ConfigureAwait(false); return; }
        await req.Bytes(bytes, "image/png", 200, "max-age=3600").ConfigureAwait(false);
    }

    /// <summary>Extracts the exe's icon through the existing IconExtractor (which always writes 64x64), then
    /// downsamples to <see cref="IconSize"/> and re-saves atomically through the same helper.</summary>
    private static bool ExtractSessionIcon(string exePath, string outFile)
    {
        var tmp = outFile + ".src.png";
        try
        {
            if (!IconExtractor.TryExtract(exePath, null, tmp)) return false;
            using var src = new Bitmap(tmp);
            using var resized = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, IconSize, IconSize));
            }
            return IconExtractor.SaveAtomic(resized, outFile);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Same QueryFullProcessImageName approach as WindowsService's icon fallback (PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    private static string? QueryExePath(int pid)
    {
        using var hProcess = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProcess is null || hProcess.IsInvalid) return null;
        try
        {
            Span<char> buf = stackalloc char[1024];
            uint size = (uint)buf.Length;
            if (PInvoke.QueryFullProcessImageName(hProcess, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buf, ref size) && size > 0)
                return new string(buf[..(int)size]);
        }
        catch { /* access denied (elevated/protected process) */ }
        return null;
    }

    // ------------------------------------------------------------------ capture device (microphone)

    private MMDevice GetCaptureDevice(out AudioEndpointVolume volume)
    {
        lock (_captureLock)
        {
            if (_captureDevice is null)
            {
                var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _captureVolume = device.AudioEndpointVolume;
                _captureVolume.OnVolumeNotification += OnCaptureVolumeNotification;
                _captureDevice = device;
            }
            volume = _captureVolume!;
            return _captureDevice;
        }
    }

    private void UnsubscribeCaptureUnlocked()
    {
        if (_captureVolume is not null)
        {
            try { _captureVolume.OnVolumeNotification -= OnCaptureVolumeNotification; } catch { }
            _captureVolume = null;
        }
    }

    private void InvalidateCaptureDevice()
    {
        lock (_captureLock)
        {
            UnsubscribeCaptureUnlocked();
            try { _captureDevice?.Dispose(); } catch { }
            _captureDevice = null;
        }
        try
        {
            var device = GetCaptureDevice(out var volume);
            BroadcastThrottled("mic-changed", () => new JsonObject
            {
                ["type"] = "mic-changed",
                ["volume"] = (int)Math.Round(volume.MasterVolumeLevelScalar * 100),
                ["muted"] = volume.Mute,
            });
            _ = device;
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("audio: re-resolve default capture device failed: " + ex.Message);
        }
    }

    private void OnCaptureVolumeNotification(AudioVolumeNotificationData data)
    {
        BroadcastThrottled("mic-changed", () => new JsonObject
        {
            ["type"] = "mic-changed",
            ["volume"] = (int)Math.Round(data.MasterVolume * 100),
            ["muted"] = data.Muted,
        });
    }

    private Task GetMic(ApiRequest req)
    {
        try
        {
            var device = GetCaptureDevice(out var volume);
            float peak = 0f;
            try { peak = device.AudioMeterInformation.MasterPeakValue; } catch { /* not every device exposes a meter */ }
            return req.Json(MicBody(device, volume, peak));
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("GET /audio/mic", ex);
            return req.Error(500, ex.Message);
        }
    }

    private async Task PutMic(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject;
        try
        {
            var device = GetCaptureDevice(out var volume);
            lock (_captureLock)
            {
                if (node?["volume"] is JsonValue vv && vv.TryGetValue(out int vol))
                    volume.MasterVolumeLevelScalar = Math.Clamp(vol, 0, 100) / 100f;
                if (node?["muted"] is JsonValue mv && mv.TryGetValue(out bool muted))
                    volume.Mute = muted;
            }
            float peak = 0f;
            try { peak = device.AudioMeterInformation.MasterPeakValue; } catch { }
            await req.Json(MicBody(device, volume, peak)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("PUT /audio/mic", ex);
            await req.Error(500, ex.Message).ConfigureAwait(false);
        }
    }

    private static JsonObject MicBody(MMDevice device, AudioEndpointVolume volume, float peak) => new()
    {
        ["volume"] = (int)Math.Round(volume.MasterVolumeLevelScalar * 100),
        ["muted"] = volume.Mute,
        ["device"] = new JsonObject { ["id"] = device.ID, ["name"] = device.FriendlyName },
        ["peak"] = Math.Clamp(peak, 0f, 1f),
    };

    // ------------------------------------------------------------------ events throttling

    /// <summary>At most one broadcast per <paramref name="type"/> every <see cref="ThrottleMs"/>; extra
    /// notifications inside the window (e.g. dragging a volume slider) are dropped, not queued.</summary>
    private void BroadcastThrottled(string type, Func<JsonObject> buildPayload)
    {
        lock (_throttleLock)
        {
            var now = DateTime.UtcNow;
            if (_lastEventAt.TryGetValue(type, out var last) && (now - last).TotalMilliseconds < ThrottleMs) return;
            _lastEventAt[type] = now;
        }
        try { _events.Broadcast(buildPayload()); }
        catch (Exception ex) { _ctx.Log.Error("audio broadcast " + type, ex); }
    }

    // ------------------------------------------------------------------ default-device notifications

    /// <summary>Watches for the system default render/capture device changing (from Windows itself,
    /// not through our own PutOutput) so the cached MMDevice/AudioEndpointVolume never goes stale.</summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioControlService _owner;
        public NotificationClient(AudioControlService owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Never call back into MMDeviceEnumerator/other audio COM APIs synchronously from inside
            // this callback. It is delivered reentrantly on whatever thread is currently inside a
            // blocking audio-service RPC call — including our own thread when the change was caused
            // by PolicyConfig.SetDefaultEndpoint() a few frames up the same call stack — and a nested
            // call back into that same RPC channel from that thread deadlocks forever (confirmed:
            // every /audio/* request hung permanently after PUT /audio/output re-resolved the device
            // inline here). Hop to a fresh threadpool thread first so the callback returns immediately
            // and the audio service's own call can complete.
            if (flow == DataFlow.Render && role == Role.Multimedia) Task.Run(_owner.InvalidateRenderDevice);
            else if (flow == DataFlow.Capture && role == Role.Communications) Task.Run(_owner.InvalidateCaptureDevice);
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
