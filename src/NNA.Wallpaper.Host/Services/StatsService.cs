using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /stats — same keys and semantics as helper.py's Stats.get(): cpu (overall + per-core),
/// mem, disks, net, uptime_s, gpu. A background sampler updates a snapshot once a second so the
/// HTTP handler always answers instantly from the last reading (mirrors the Python helper's
/// threading.Lock-protected snapshot).
/// </summary>
public sealed class StatsService : IHostService, IDisposable
{
    private const int DiskRefreshSeconds = 10;
    private const int GpuIntervalMs = 2000;

    private readonly HostContext _ctx;
    private readonly CancellationTokenSource _cts = new();
    private readonly PerformanceCounter? _freqCounterOrNull;
    private readonly bool _freqCounterBroken;

    private volatile SnapshotData? _snapshot;
    private volatile GpuBlock _gpu = new(false, error: "not sampled yet");

    // GetSystemTimes deltas (system-wide CPU percent).
    private long? _prevSysIdle, _prevSysKernel, _prevSysUser;

    // NtQuerySystemInformation deltas (per-core CPU percent).
    private NativeStats.CoreTimes[]? _prevCoreTimes;

    // net delta state.
    private DateTime? _netAt;
    private long _netSentPrev, _netRecvPrev;

    // disk listing cache (refreshed every DiskRefreshSeconds).
    private DateTime _diskAt = DateTime.MinValue;
    private List<DiskBlock> _diskCache = new();

    // base clock cache for the frequency fallback/primary formula.
    private int? _maxMhz;

    public StatsService(HostContext ctx)
    {
        _ctx = ctx;
        ctx.Health["gpu"] = () => _gpu.ok;

        try
        {
            _freqCounterOrNull = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            _freqCounterOrNull.NextValue(); // first read is meaningless, primes the counter
        }
        catch
        {
            // Russian Windows may not expose the English category/instance names; the fallback
            // below (CallNtPowerInformation) covers this case.
            _freqCounterOrNull = null;
            _freqCounterBroken = true;
        }

        _ = Task.Run(() => SampleLoop(_cts.Token));
        _ = Task.Run(() => GpuLoop(_cts.Token));
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/stats", req =>
        {
            var s = _snapshot;
            var g = _gpu;
            object body = s is null
                ? new { gpu = g }
                : new { ts = s.ts, cpu = s.cpu, mem = s.mem, disks = s.disks, net = s.net, uptime_s = s.uptime_s, gpu = g };
            return req.Json(body);
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _freqCounterOrNull?.Dispose();
    }

    private async Task SampleLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Sample();
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("stats sample", ex);
            }
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task GpuLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            GpuBlock next;
            try
            {
                next = await SampleGpuAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                next = new GpuBlock(false, error: Truncate(ex.Message, 80));
            }
            _gpu = next;
            try { await Task.Delay(GpuIntervalMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private void Sample()
    {
        var now = DateTimeOffset.UtcNow;
        double ts = now.ToUnixTimeMilliseconds() / 1000.0;

        double overallPercent = SampleOverallCpuPercent();
        int[] perCore = SamplePerCoreCpuPercent();
        int? freqMhz = ReadFrequencyMhz();

        var cpu = new CpuBlock(overallPercent, perCore, freqMhz, perCore.Length);

        MemBlock mem;
        if (NativeStats.TryGetMemory(out var used, out var total, out var memPercent))
            mem = new MemBlock((long)used, (long)total, Math.Round(memPercent, 1));
        else
            mem = new MemBlock(0, 0, 0);

        var disks = SampleDisksCached(now);
        var net = SampleNet(now);
        long uptimeS = Environment.TickCount64 / 1000;

        _snapshot = new SnapshotData(ts, cpu, mem, disks, net, uptimeS);
    }

    private double SampleOverallCpuPercent()
    {
        if (!NativeStats.TryGetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        double percent = 0;
        if (_prevSysIdle is not null)
        {
            long dIdle = idle - _prevSysIdle.Value;
            long dKernel = kernel - _prevSysKernel!.Value;
            long dUser = user - _prevSysUser!.Value;
            long dTotal = dKernel + dUser; // kernel time already includes idle time
            if (dTotal > 0) percent = Math.Round((1.0 - (double)dIdle / dTotal) * 100.0, 1);
        }
        _prevSysIdle = idle;
        _prevSysKernel = kernel;
        _prevSysUser = user;
        return Math.Clamp(percent, 0, 100);
    }

    private int[] SamplePerCoreCpuPercent()
    {
        int n = Environment.ProcessorCount;
        var times = NativeStats.ReadCoreTimes(n);
        if (times is null) return _prevCoreTimes is null ? new int[n] : new int[_prevCoreTimes.Length];

        int[] result = new int[times.Length];
        if (_prevCoreTimes is not null && _prevCoreTimes.Length == times.Length)
        {
            for (int i = 0; i < times.Length; i++)
            {
                long dIdle = times[i].Idle - _prevCoreTimes[i].Idle;
                long dKernel = times[i].Kernel - _prevCoreTimes[i].Kernel;
                long dUser = times[i].User - _prevCoreTimes[i].User;
                long dTotal = dKernel + dUser;
                result[i] = dTotal > 0 ? Math.Clamp((int)Math.Round((1.0 - (double)dIdle / dTotal) * 100.0), 0, 100) : 0;
            }
        }
        _prevCoreTimes = times;
        return result;
    }

    /// <summary>
    /// Current clock speed: "% Processor Performance" (relative to rated speed) times the rated
    /// speed from CallNtPowerInformation's MaxMhz, in a try/catch because the counter's English
    /// category/instance names are not guaranteed to exist on a localized (Russian) Windows.
    /// Falls back straight to CallNtPowerInformation's CurrentMhz for the first core.
    /// </summary>
    private int? ReadFrequencyMhz()
    {
        if (!_freqCounterBroken && _freqCounterOrNull is not null)
        {
            try
            {
                float pct = _freqCounterOrNull.NextValue();
                int? maxMhz = GetMaxMhz();
                if (maxMhz is int mm && mm > 0) return (int)Math.Round(mm * (pct / 100.0));
            }
            catch
            {
                // fall through to the native fallback below
            }
        }
        // CallNtPowerInformation(ProcessorInformation) requires a buffer sized for every logical
        // processor, or the call fails outright — it will not just fill fewer entries.
        var info = NativeStats.QueryProcessorPowerInformation(Environment.ProcessorCount);
        return info is { Length: > 0 } ? (int)info[0].CurrentMhz : null;
    }

    private int? GetMaxMhz()
    {
        if (_maxMhz is int cached) return cached;
        // CallNtPowerInformation(ProcessorInformation) requires a buffer sized for every logical
        // processor, or the call fails outright — it will not just fill fewer entries.
        var info = NativeStats.QueryProcessorPowerInformation(Environment.ProcessorCount);
        if (info is { Length: > 0 }) _maxMhz = (int)info[0].MaxMhz;
        return _maxMhz;
    }

    private NetBlock SampleNet(DateTimeOffset now)
    {
        long sent = 0, recv = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var stats = nic.GetIPv4Statistics();
                sent += stats.BytesSent;
                recv += stats.BytesReceived;
            }
            catch
            {
                // interface can disappear mid-enumeration (USB dongles, VPN adapters); skip it
            }
        }

        long upBps = 0, downBps = 0;
        if (_netAt is DateTime prevAt)
        {
            double dt = Math.Max((now.UtcDateTime - prevAt).TotalSeconds, 1e-3);
            upBps = (long)Math.Round((sent - _netSentPrev) / dt);
            downBps = (long)Math.Round((recv - _netRecvPrev) / dt);
        }
        _netAt = now.UtcDateTime;
        _netSentPrev = sent;
        _netRecvPrev = recv;
        return new NetBlock(Math.Max(upBps, 0), Math.Max(downBps, 0));
    }

    private List<DiskBlock> SampleDisksCached(DateTimeOffset now)
    {
        if ((now.UtcDateTime - _diskAt).TotalSeconds <= DiskRefreshSeconds) return _diskCache;
        var disks = new List<DiskBlock>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            try
            {
                long total = drive.TotalSize;
                long used = total - drive.TotalFreeSpace;
                double percent = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0;
                string mount = drive.Name.Length >= 2 ? drive.Name[..2] : drive.Name;
                disks.Add(new DiskBlock(mount, used, total, percent));
            }
            catch
            {
                // drive can go offline between GetDrives() and the property reads above
            }
        }
        _diskCache = disks;
        _diskAt = now.UtcDateTime;
        return disks;
    }

    private static async Task<GpuBlock> SampleGpuAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                Arguments = "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new GpuBlock(false, error: "nvidia-smi not found");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            string stdout;
            try
            {
                stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new GpuBlock(false, error: "nvidia-smi timeout");
            }

            var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(line)) return new GpuBlock(false, error: "no gpu output");

            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 6) return new GpuBlock(false, error: "unexpected gpu output");

            return new GpuBlock(
                ok: true,
                name: parts[0],
                util: double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                temp: double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                mem_used_mb: double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture),
                mem_total_mb: double.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture),
                power_w: double.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FormatException)
        {
            return new GpuBlock(false, error: Truncate(ex.Message, 80));
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // Response shapes. Property names are written to JSON exactly as declared (Json.Api applies
    // no naming policy), so they use the same snake_case keys as helper.py's Stats.get().
    private sealed record CpuBlock(double percent, int[] per_core, int? freq_mhz, int cores);
    private sealed record MemBlock(long used, long total, double percent);
    private sealed record DiskBlock(string mount, long used, long total, double percent);
    private sealed record NetBlock(long up_bps, long down_bps);
    private sealed record GpuBlock(bool ok, string? name = null, double? util = null, double? temp = null,
        double? mem_used_mb = null, double? mem_total_mb = null, double? power_w = null, string? error = null);
    private sealed record SnapshotData(double ts, CpuBlock cpu, MemBlock mem, List<DiskBlock> disks, NetBlock net, long uptime_s);
}
