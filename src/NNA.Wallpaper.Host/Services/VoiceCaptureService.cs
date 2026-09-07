using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Host-side push-to-talk microphone recorder for NNA.Wallpaper.Hotkeys (global hotkey capture,
/// distinct from the wallpaper block's own browser-side MediaRecorder). Wraps NAudio's
/// <see cref="WasapiCapture"/> on the device named by app.json's audio.captureDevice — the same
/// preference AudioDevicesService/PutCaptureDevice already own, so picking a mic in the settings
/// page's voice card affects both the block and the hotkey identically — falling back to the
/// system default capture endpoint when unset or not found. Not an IHostService: it registers no
/// routes and is owned directly by Hotkeys, one instance per app run.
///
/// Recording accumulates mono float samples at the device's own mix format/rate (mirrors the
/// downmix approach AudioService already uses for loopback capture), capped at
/// <see cref="MaxSeconds"/> of audio; <see cref="Stop"/> resamples (linear interpolation — good
/// enough for speech-to-text, not a hi-fi requirement) down to 16 kHz mono and returns a standard
/// 16-bit PCM WAV byte array, or null if nothing was recorded.
/// </summary>
public sealed class VoiceCaptureService
{
    public const int TargetSampleRate = 16000;
    private const int MaxSeconds = 60;

    private readonly HostContext _ctx;
    private readonly object _lock = new();

    private WasapiCapture? _capture;
    private List<float> _mono = new();
    private int _sourceRate = TargetSampleRate;

    public VoiceCaptureService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public bool IsRecording
    {
        get { lock (_lock) return _capture is not null; }
    }

    /// <summary>Starts recording from the configured (or default) capture device. False + <paramref name="error"/> on any failure — never throws.</summary>
    public bool Start(out string? error)
    {
        error = null;
        lock (_lock)
        {
            if (_capture is not null)
            {
                error = "already recording";
                return false;
            }

            try
            {
                var wantName = _ctx.Config.App.Audio.CaptureDevice;
                MMDevice? device = null;
                using (var enumerator = new MMDeviceEnumerator())
                {
                    if (!string.IsNullOrWhiteSpace(wantName))
                    {
                        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                        {
                            if (string.Equals(d.FriendlyName, wantName, StringComparison.OrdinalIgnoreCase)) { device = d; break; }
                        }
                    }
                    device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                }

                var capture = new WasapiCapture(device);
                _sourceRate = capture.WaveFormat.SampleRate > 0 ? capture.WaveFormat.SampleRate : 48000;
                _mono = new List<float>(_sourceRate * 2);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null) _ctx.Log.Warn("voice capture stopped unexpectedly: " + e.Exception.Message);
                };
                capture.StartRecording();
                _capture = capture;
                return true;
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("voice capture start failed", ex);
                error = ex.Message;
                _capture = null;
                return false;
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var capture = _capture;
        if (capture is null) return;

        var fmt = capture.WaveFormat;
        var channels = Math.Max(fmt.Channels, 1);
        var bytesPerSample = fmt.BitsPerSample / 8;
        if (bytesPerSample <= 0) return;
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        var frameBytes = bytesPerSample * channels;
        if (frameBytes <= 0) return;
        var frames = e.BytesRecorded / frameBytes;
        if (frames <= 0) return;

        lock (_lock)
        {
            if (_capture is null) return; // Stop() already claimed the buffer
            var cap = (long)_sourceRate * MaxSeconds;
            if (_mono.Count >= cap) return; // safety cutoff hit: keep the device running, stop growing the buffer

            for (var i = 0; i < frames && _mono.Count < cap; i++)
            {
                var baseIdx = i * frameBytes;
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += ReadSample(e.Buffer, baseIdx + c * bytesPerSample, isFloat, bytesPerSample);
                _mono.Add(sum / channels);
            }
        }
    }

    private static float ReadSample(byte[] buffer, int offset, bool isFloat, int bytesPerSample)
    {
        if (isFloat && bytesPerSample == 4) return BitConverter.ToSingle(buffer, offset);
        if (bytesPerSample == 2) return BitConverter.ToInt16(buffer, offset) / 32768f;
        if (bytesPerSample == 4) return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        if (bytesPerSample == 3)
        {
            var v = buffer[offset] | (buffer[offset + 1] << 8) | (sbyte)buffer[offset + 2] << 16;
            return v / 8388608f;
        }
        return 0f;
    }

    /// <summary>Stops recording and returns a 16kHz mono 16-bit PCM WAV, or null if nothing usable was captured.</summary>
    public byte[]? Stop()
    {
        WasapiCapture? capture;
        List<float> mono;
        int sourceRate;
        lock (_lock)
        {
            capture = _capture;
            _capture = null;
            mono = _mono;
            _mono = new List<float>();
            sourceRate = _sourceRate;
        }
        if (capture is null) return null;

        try { capture.StopRecording(); } catch { /* best-effort */ }
        try { capture.Dispose(); } catch { /* best-effort */ }

        if (mono.Count == 0) return null;
        var pcm16 = Resample(mono, sourceRate, TargetSampleRate);
        return pcm16.Length == 0 ? null : BuildWav(pcm16, TargetSampleRate);
    }

    private static short[] Resample(List<float> src, int srcRate, int dstRate)
    {
        if (srcRate == dstRate)
        {
            var direct = new short[src.Count];
            for (var i = 0; i < src.Count; i++) direct[i] = FloatToInt16(src[i]);
            return direct;
        }

        var ratio = (double)srcRate / dstRate;
        var outLen = Math.Max(0, (int)(src.Count / ratio));
        var result = new short[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var pos = i * ratio;
            var idx = (int)pos;
            var frac = pos - idx;
            var a = src[Math.Min(idx, src.Count - 1)];
            var b = src[Math.Min(idx + 1, src.Count - 1)];
            result[i] = FloatToInt16((float)(a + (b - a) * frac));
        }
        return result;
    }

    private static short FloatToInt16(float v)
    {
        var scaled = (int)Math.Round(Math.Clamp(v, -1f, 1f) * short.MaxValue);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private static byte[] BuildWav(short[] pcm, int sampleRate)
    {
        const short bitsPerSample = 16;
        const short channels = 1;
        var dataLen = pcm.Length * 2;
        using var ms = new MemoryStream(44 + dataLen);
        using (var w = new BinaryWriter(ms))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataLen);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1); // PCM
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(sampleRate * channels * (bitsPerSample / 8)); // byte rate
            w.Write((short)(channels * (bitsPerSample / 8))); // block align
            w.Write(bitsPerSample);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(dataLen);
            foreach (var s in pcm) w.Write(s);
        }
        return ms.ToArray();
    }
}
