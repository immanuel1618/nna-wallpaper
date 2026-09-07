using System.Diagnostics;
using System.Net.WebSockets;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Live audio spectrum over WebSocket (/audio): WASAPI loopback capture of the default (or
/// configured) render device, 1024-point FFT with a Hamming window, 64 log-spaced bands per
/// channel (40 Hz - 16 kHz), soft AGC to 0..1. Capture is lazy: it starts with the first client
/// and stops five seconds after the last one leaves. No third-party JS talks to this file; it is
/// pure server-side DSP feeding a small binary protocol.
/// </summary>
public sealed class AudioService : IHostService, IDisposable
{
    private const int Bands = 64;
    private const int FftSize = 1024;
    private const int FftPow2 = 10; // log2(FftSize)
    private const int RingSize = FftSize * 8;
    private const double MinHz = 40.0;
    private const double MaxHz = 16000.0;
    private const double FrameHz = 30.0;
    private const double SilenceMs = 300.0;
    private const double StopAfterIdleS = 5.0;
    private const double RetryBackoffS = 30.0;

    private static readonly double[] BandEdgesHz = BuildBandEdges();

    private readonly HostContext _ctx;
    private readonly bool _enabled;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    private readonly object _clientsLock = new();
    private readonly HashSet<WebSocket> _clients = new();

    private readonly object _captureLock = new();
    private WasapiLoopbackCapture? _capture;
    private volatile bool _capturing;
    private bool _errorLogged;
    private DateTime? _lastErrorAtUtc;
    private int _sampleRate = 48000;

    private readonly object _ringLock = new();
    private readonly float[] _ringL = new float[RingSize];
    private readonly float[] _ringR = new float[RingSize];
    private int _ringPos;
    private int _ringFilled;
    private long _lastDataTicksUtc;

    private DateTime _lastClientAtUtc = DateTime.UtcNow;
    private float[] _lastFrame = new float[Bands * 2];
    private float _agcLevelL = 1e-4f;
    private float _agcLevelR = 1e-4f;

    public AudioService(HostContext ctx)
    {
        _ctx = ctx;
        _enabled = ctx.Config.App.Audio.Enabled;
        _ctx.Health["audio"] = () => _enabled && !_errorLogged;
        _lastDataTicksUtc = 0;
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Register(LocalApi api)
    {
        api.MapWebSocket("/audio", HandleAudioWs);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loopTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        StopCapture();
        lock (_clientsLock)
        {
            foreach (var ws in _clients)
            {
                try { ws.Abort(); } catch { }
            }
            _clients.Clear();
        }
        try { _cts.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ WebSocket

    private async Task HandleAudioWs(WebSocket socket, ApiRequest req, CancellationToken ct)
    {
        if (!_enabled)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "audio disabled", ct).ConfigureAwait(false); }
            catch { }
            return;
        }

        AddClient(socket);
        try
        {
            var hello = Json.Serialize(new { type = "hello", bands = Bands, channels = 2, rate = (int)FrameHz });
            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            var buffer = new byte[64];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch
        {
            // client disconnects are not errors
        }
        finally
        {
            RemoveClient(socket);
        }
    }

    private void AddClient(WebSocket socket)
    {
        lock (_clientsLock) _clients.Add(socket);
    }

    private void RemoveClient(WebSocket socket)
    {
        lock (_clientsLock) _clients.Remove(socket);
    }

    // ------------------------------------------------------------------ main loop (~30 fps)

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / FrameHz);
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                ManageCapture();
                var frame = ComputeFrame();
                await BroadcastFrameAsync(frame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("audio loop", ex);
            }
            var wait = interval - sw.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void ManageCapture()
    {
        if (!_enabled)
        {
            if (_capturing) StopCapture();
            return;
        }

        bool hasClients;
        lock (_clientsLock) hasClients = _clients.Count > 0;

        if (hasClients)
        {
            _lastClientAtUtc = DateTime.UtcNow;
            if (!_capturing)
            {
                var canRetry = _lastErrorAtUtc is null || (DateTime.UtcNow - _lastErrorAtUtc.Value).TotalSeconds >= RetryBackoffS;
                if (canRetry) TryStartCapture();
            }
        }
        else if (_capturing && (DateTime.UtcNow - _lastClientAtUtc).TotalSeconds >= StopAfterIdleS)
        {
            StopCapture();
        }
    }

    // ------------------------------------------------------------------ capture lifecycle

    private void TryStartCapture()
    {
        lock (_captureLock)
        {
            if (_capturing) return;
            try
            {
                MMDevice? device = null;
                var deviceId = _ctx.Config.App.Audio.Device;
                using (var enumerator = new MMDeviceEnumerator())
                {
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        try { device = enumerator.GetDevice(deviceId); }
                        catch { device = null; }
                    }
                    device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }

                var capture = new WasapiLoopbackCapture(device);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                _sampleRate = capture.WaveFormat.SampleRate > 0 ? capture.WaveFormat.SampleRate : 48000;
                lock (_ringLock)
                {
                    _ringPos = 0;
                    _ringFilled = 0;
                }
                capture.StartRecording();
                _capture = capture;
                _capturing = true;
                _errorLogged = false;
                _lastErrorAtUtc = null;
            }
            catch (Exception ex)
            {
                _capture = null;
                _capturing = false;
                _lastErrorAtUtc = DateTime.UtcNow;
                if (!_errorLogged)
                {
                    _ctx.Log.Error("audio capture start failed", ex);
                    _errorLogged = true;
                }
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _lastErrorAtUtc = DateTime.UtcNow;
            if (!_errorLogged)
            {
                _ctx.Log.Error("audio capture stopped unexpectedly", e.Exception);
                _errorLogged = true;
            }
        }
        _capturing = false;
    }

    private void StopCapture()
    {
        lock (_captureLock)
        {
            _capturing = false;
            if (_capture is null) return;
            var capture = _capture;
            _capture = null;
            try { capture.DataAvailable -= OnDataAvailable; } catch { }
            try { capture.RecordingStopped -= OnRecordingStopped; } catch { }
            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
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

        lock (_ringLock)
        {
            for (var i = 0; i < frames; i++)
            {
                var baseIdx = i * frameBytes;
                float l, r;
                if (channels >= 2)
                {
                    l = ReadSample(e.Buffer, baseIdx, isFloat, bytesPerSample);
                    r = ReadSample(e.Buffer, baseIdx + bytesPerSample, isFloat, bytesPerSample);
                }
                else
                {
                    l = r = ReadSample(e.Buffer, baseIdx, isFloat, bytesPerSample);
                }
                _ringL[_ringPos] = l;
                _ringR[_ringPos] = r;
                _ringPos = (_ringPos + 1) % RingSize;
                if (_ringFilled < RingSize) _ringFilled++;
            }
        }
        Interlocked.Exchange(ref _lastDataTicksUtc, DateTime.UtcNow.Ticks);
    }

    private static float ReadSample(byte[] buf, int offset, bool isFloat, int bytesPerSample)
    {
        if (isFloat && bytesPerSample == 4) return BitConverter.ToSingle(buf, offset);
        if (bytesPerSample == 2) return BitConverter.ToInt16(buf, offset) / 32768f;
        if (bytesPerSample == 4) return BitConverter.ToInt32(buf, offset) / 2147483648f;
        return 0f;
    }

    // ------------------------------------------------------------------ spectrum

    private float[] ComputeFrame()
    {
        var lastDataTicks = Interlocked.Read(ref _lastDataTicksUtc);
        var sinceData = lastDataTicks == 0 ? double.MaxValue : (DateTime.UtcNow.Ticks - lastDataTicks) / (double)TimeSpan.TicksPerMillisecond;
        bool haveFreshData;
        lock (_ringLock) haveFreshData = _capturing && _ringFilled >= FftSize && sinceData <= SilenceMs;

        if (haveFreshData)
        {
            var sampleRate = _sampleRate;
            var winL = ExtractWindow(_ringL);
            var winR = ExtractWindow(_ringR);
            var bandsL = Normalize(ComputeBands(winL, sampleRate), ref _agcLevelL);
            var bandsR = Normalize(ComputeBands(winR, sampleRate), ref _agcLevelR);
            var frame = new float[Bands * 2];
            Array.Copy(bandsL, 0, frame, 0, Bands);
            Array.Copy(bandsR, 0, frame, Bands, Bands);
            _lastFrame = frame;
            return frame;
        }

        // No fresh data: fade the previous frame smoothly toward silence.
        var decayed = new float[Bands * 2];
        for (var i = 0; i < decayed.Length; i++)
        {
            var v = _lastFrame[i] * 0.8f;
            decayed[i] = v < 0.001f ? 0f : v;
        }
        _lastFrame = decayed;
        return decayed;
    }

    private float[] ExtractWindow(float[] ring)
    {
        var outp = new float[FftSize];
        lock (_ringLock)
        {
            var filled = Math.Min(_ringFilled, FftSize);
            var start = (_ringPos - filled + RingSize) % RingSize;
            for (var i = 0; i < filled; i++)
            {
                outp[FftSize - filled + i] = ring[(start + i) % RingSize];
            }
        }
        return outp;
    }

    private static float[] ComputeBands(float[] window, int sampleRate)
    {
        var complex = new Complex[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            var w = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1));
            complex[i].X = (float)(window[i] * w);
            complex[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, FftPow2, complex);

        var bands = new float[Bands];
        for (var b = 0; b < Bands; b++)
        {
            var bin0 = Math.Max(1, (int)Math.Floor(BandEdgesHz[b] * FftSize / (double)sampleRate));
            var bin1 = Math.Min(FftSize / 2 - 1, (int)Math.Ceiling(BandEdgesHz[b + 1] * FftSize / (double)sampleRate));
            if (bin1 < bin0) bin1 = bin0;
            double sum = 0;
            var count = 0;
            for (var k = bin0; k <= bin1; k++)
            {
                var mag = Math.Sqrt(complex[k].X * complex[k].X + complex[k].Y * complex[k].Y);
                sum += mag;
                count++;
            }
            bands[b] = (float)(count > 0 ? sum / count : 0.0);
        }
        return bands;
    }

    /// <summary>Soft AGC: tracks a slow-moving reference level (fast attack, slow release) and
    /// compresses toward 0..1 so quiet passages read near 0 and loud passages approach 1 without
    /// a hard clip.</summary>
    private static float[] Normalize(float[] raw, ref float agcLevel)
    {
        var maxVal = 0f;
        for (var i = 0; i < raw.Length; i++) if (raw[i] > maxVal) maxVal = raw[i];
        var target = Math.Max(maxVal, 1e-4f);
        agcLevel += target > agcLevel ? (target - agcLevel) * 0.3f : (target - agcLevel) * 0.02f;
        var safeLevel = Math.Max(agcLevel, 1e-6f);

        var outp = new float[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var v = raw[i] / safeLevel;
            outp[i] = (float)(1.0 - Math.Exp(-2.0 * v));
        }
        return outp;
    }

    private static double[] BuildBandEdges()
    {
        var edges = new double[Bands + 1];
        var logMin = Math.Log(MinHz);
        var logMax = Math.Log(MaxHz);
        for (var i = 0; i < edges.Length; i++)
        {
            var t = (double)i / (edges.Length - 1);
            edges[i] = Math.Exp(logMin + t * (logMax - logMin));
        }
        return edges;
    }

    // ------------------------------------------------------------------ broadcast

    private async Task BroadcastFrameAsync(float[] frame)
    {
        List<WebSocket> snapshot;
        lock (_clientsLock)
        {
            if (_clients.Count == 0) return;
            snapshot = new List<WebSocket>(_clients);
        }

        var bytes = new byte[frame.Length * sizeof(float)];
        Buffer.BlockCopy(frame, 0, bytes, 0, bytes.Length);

        foreach (var ws in snapshot)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                RemoveClient(ws);
            }
        }
    }
}
