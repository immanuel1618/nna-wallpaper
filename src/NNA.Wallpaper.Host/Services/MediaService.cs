using System.Globalization;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// "Now playing" via Windows GlobalSystemMediaTransportControlsSessionManager (GSMTC).
/// Mirrors the Python helper's Media class (helper.py): GET /media returns the current session's
/// track info, POST /media/&lt;action&gt; controls playback. Session/property changes are pushed by
/// WinRT events (no polling); position is interpolated from the last timeline update on every read.
/// </summary>
public sealed class MediaService : IHostService, IDisposable
{
    private readonly HostContext _ctx;
    private readonly object _lock = new();
    private readonly object _sessionLock = new();
    private readonly object _thumbLock = new();

    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private volatile bool _ok;
    private string? _err;

    private string? _thumbKey;
    private string? _thumbData;

    private MediaState _state;

    public MediaService(HostContext ctx)
    {
        _ctx = ctx;
        _state = new MediaState { Active = false, Ts = NowUnix() };
        _ctx.Health["media"] = () => _ok;
        _ctx.Health["media_error"] = () => _err;
        _ = InitializeAsync();
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/media", GetMedia);
        api.Map("POST", "/media/toggle", req => Control(req, "toggle"));
        api.Map("POST", "/media/play", req => Control(req, "play"));
        api.Map("POST", "/media/pause", req => Control(req, "pause"));
        api.Map("POST", "/media/next", req => Control(req, "next"));
        api.Map("POST", "/media/prev", req => Control(req, "prev"));
        api.Map("POST", "/media/seek", Seek);
    }

    public void Dispose()
    {
        if (_mgr is not null)
        {
            try { _mgr.SessionsChanged -= OnSessionsChanged; } catch { }
        }
        HookSession(null);
    }

    // ------------------------------------------------------------------ init / events

    private async Task InitializeAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _ok = true;
            _err = null;
            _mgr.SessionsChanged += OnSessionsChanged;
            HookSession(_mgr.GetCurrentSession());
            await UpdateAsync(_session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ok = false;
            _err = ex.Message;
            _ctx.Log.Error("media init failed", ex);
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        HookSession(sender.GetCurrentSession());
        _ = UpdateAsync(_session);
    }

    private void HookSession(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_sessionLock)
        {
            if (ReferenceEquals(_session, session)) return;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            _session = session;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
        }
        if (session is null)
        {
            lock (_lock) { _state = new MediaState { Active = false, Ts = NowUnix() }; }
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = UpdateAsync(sender);
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = UpdateAsync(sender);
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _ = UpdateAsync(sender);

    // ------------------------------------------------------------------ state snapshot

    private sealed class MediaState
    {
        public bool Active;
        public double Ts;
        public string? Source;
        public string Status = "other";
        public bool Playing;
        public string? Title;
        public string? Artist;
        public string? Album;
        public double PositionBaseS;
        public double DurationS;
        public DateTimeOffset LastUpdated;
        public bool CanSeek;
        public bool CanNext;
        public bool CanPrev;
        public string? Thumbnail;
    }

    private async Task UpdateAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            lock (_lock) { _state = new MediaState { Active = false, Ts = NowUnix() }; }
            return;
        }
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var statusVal = (int)playback.PlaybackStatus;
            var statusStr = statusVal switch { 4 => "playing", 5 => "paused", 3 => "stopped", _ => "other" };
            var playing = statusVal == 4;

            var start = timeline.StartTime.TotalSeconds;
            var end = timeline.EndTime.TotalSeconds;
            var duration = Math.Max(end - start, 0.0);
            var posRaw = timeline.Position.TotalSeconds - start;
            var positionBase = duration > 0 ? Math.Clamp(posRaw, 0.0, duration) : 0.0;

            var controls = playback.Controls;
            var canSeek = duration > 0 && controls.IsPlaybackPositionEnabled;

            var source = session.SourceAppUserModelId;
            var thumbKey = (props.Title ?? "") + "|" + (props.Artist ?? "") + "|" + (source ?? "");
            var thumb = await GetThumbnailAsync(props, thumbKey).ConfigureAwait(false);

            var state = new MediaState
            {
                Active = true,
                Ts = NowUnix(),
                Source = source,
                Status = statusStr,
                Playing = playing,
                Title = props.Title,
                Artist = props.Artist,
                Album = props.AlbumTitle,
                PositionBaseS = positionBase,
                DurationS = Math.Round(duration, 1),
                LastUpdated = timeline.LastUpdatedTime,
                CanSeek = canSeek,
                CanNext = controls.IsNextEnabled,
                CanPrev = controls.IsPreviousEnabled,
                Thumbnail = thumb,
            };
            lock (_lock) { _state = state; }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("media update failed", ex);
        }
    }

    private async Task<string?> GetThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props, string key)
    {
        lock (_thumbLock)
        {
            if (_thumbKey == key) return _thumbData;
        }
        string? dataUrl = null;
        try
        {
            var thumbRef = props.Thumbnail;
            if (thumbRef is not null)
            {
                using var stream = await thumbRef.OpenReadAsync();
                var size = (int)stream.Size;
                if (size > 0 && size < 8_000_000)
                {
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)size);
                    var bytes = new byte[size];
                    reader.ReadBytes(bytes);
                    var mime = DetectImageMime(bytes);
                    dataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
                }
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("media thumbnail failed", ex);
        }
        lock (_thumbLock) { _thumbKey = key; _thumbData = dataUrl; }
        return dataUrl;
    }

    private static string DetectImageMime(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        return "image/jpeg";
    }

    private static double NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ------------------------------------------------------------------ HTTP handlers

    private Task GetMedia(ApiRequest req)
    {
        MediaState state;
        lock (_lock) state = _state;

        if (!state.Active) return req.Json(new { active = false, ts = state.Ts });

        double positionS;
        if (state.DurationS <= 0)
        {
            positionS = 0.0;
        }
        else if (state.Playing)
        {
            var elapsed = Math.Max((DateTimeOffset.UtcNow - state.LastUpdated).TotalSeconds, 0.0);
            positionS = Math.Round(Math.Min(state.PositionBaseS + elapsed, state.DurationS), 1);
        }
        else
        {
            positionS = Math.Round(state.PositionBaseS, 1);
        }

        return req.Json(new
        {
            active = true,
            ts = state.Ts,
            source = state.Source,
            status = state.Status,
            playing = state.Playing,
            title = state.Title,
            artist = state.Artist,
            album = state.Album,
            position_s = positionS,
            duration_s = state.DurationS,
            can_seek = state.CanSeek,
            can_next = state.CanNext,
            can_prev = state.CanPrev,
            thumbnail = state.Thumbnail,
        });
    }

    private async Task Control(ApiRequest req, string action)
    {
        if (!_ok)
        {
            await req.Json(new { ok = false, error = _err ?? "media not ready" }).ConfigureAwait(false);
            return;
        }
        var session = _mgr?.GetCurrentSession();
        if (session is null)
        {
            await req.Json(new { ok = false, error = "no session" }).ConfigureAwait(false);
            return;
        }
        try
        {
            var result = action switch
            {
                "toggle" => await session.TryTogglePlayPauseAsync(),
                "play" => await session.TryPlayAsync(),
                "pause" => await session.TryPauseAsync(),
                "next" => await session.TrySkipNextAsync(),
                "prev" => await session.TrySkipPreviousAsync(),
                _ => false,
            };
            await req.Json(new { ok = result }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await req.Json(new { ok = false, error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task Seek(ApiRequest req)
    {
        if (!_ok)
        {
            await req.Json(new { ok = false, error = _err ?? "media not ready" }).ConfigureAwait(false);
            return;
        }
        var posStr = req.Query("pos");
        if (!double.TryParse(posStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var posSeconds))
        {
            await req.Json(new { ok = false, error = "bad pos" }).ConfigureAwait(false);
            return;
        }
        var session = _mgr?.GetCurrentSession();
        if (session is null)
        {
            await req.Json(new { ok = false, error = "no session" }).ConfigureAwait(false);
            return;
        }

        GlobalSystemMediaTransportControlsSessionPlaybackInfo playback;
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline;
        try
        {
            playback = session.GetPlaybackInfo();
            timeline = session.GetTimelineProperties();
        }
        catch (Exception ex)
        {
            await req.Json(new { ok = false, error = ex.Message }).ConfigureAwait(false);
            return;
        }

        if (!playback.Controls.IsPlaybackPositionEnabled)
        {
            await req.Json(new { ok = false, error = "seek not supported" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var ticks = (long)((timeline.StartTime.TotalSeconds + posSeconds) * 10_000_000.0);
            var result = await session.TryChangePlaybackPositionAsync(ticks);
            await req.Json(new { ok = result }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await req.Json(new { ok = false, error = ex.Message }).ConfigureAwait(false);
        }
    }
}
