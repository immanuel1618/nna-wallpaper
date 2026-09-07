using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Device pickers for audio: GET /audio/devices lists render (loopback, see <see cref="AudioService"/>)
/// and capture (microphone, used by the planner voice block) endpoints; GET/PUT /audio/capture-device
/// reads and sets the preferred microphone (persisted at app.json's audio.captureDevice). The actual
/// recording happens in the WebView2 page via getUserMedia/MediaRecorder, and WebView2 only exposes
/// device *labels* to script (not WASAPI ids) once permission has been granted once — so the stored
/// preference is the device's friendly name, which the page matches against
/// navigator.mediaDevices.enumerateDevices() labels, falling back to the system default on no match.
/// </summary>
public sealed class AudioDevicesService : IHostService
{
    private readonly HostContext _ctx;

    public AudioDevicesService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/audio/devices", GetDevices);
        api.Map("GET", "/audio/capture-device", GetCaptureDevice);
        api.Map("PUT", "/audio/capture-device", PutCaptureDevice);
    }

    private Task GetDevices(ApiRequest req)
    {
        var capture = new JsonArray();
        var render = new JsonArray();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultCaptureId = null, defaultRenderId = null;
            try { defaultCaptureId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID; } catch { }
            try { defaultRenderId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }

            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                capture.Add(new JsonObject
                {
                    ["id"] = d.ID,
                    ["name"] = d.FriendlyName,
                    ["default"] = d.ID == defaultCaptureId,
                });
            }
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                render.Add(new JsonObject
                {
                    ["id"] = d.ID,
                    ["name"] = d.FriendlyName,
                    ["default"] = d.ID == defaultRenderId,
                });
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("audio devices enumeration failed: " + ex.Message);
        }

        return req.Json(new JsonObject { ["capture"] = capture, ["render"] = render });
    }

    private Task GetCaptureDevice(ApiRequest req) =>
        req.Json(new { name = _ctx.Config.App.Audio.CaptureDevice });

    private async Task PutCaptureDevice(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var node = Json.ParseNode(body) as JsonObject ?? new JsonObject();
        var name = (string?)node["name"];

        var app = _ctx.Config.App;
        app.Audio.CaptureDevice = string.IsNullOrWhiteSpace(name) ? null : name;
        _ctx.Config.SaveApp();

        await req.Json(new { ok = true, name = app.Audio.CaptureDevice }).ConfigureAwait(false);
    }
}
