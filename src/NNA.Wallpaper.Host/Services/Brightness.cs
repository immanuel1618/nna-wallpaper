using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// DDC/CI monitor brightness (dxva2.dll): enumerate display monitors the same way
/// src/NNA.Wallpaper/Engine/Monitors.cs does ("{szDevice}|{Width}x{Height}" ids, so results line
/// up with the ids reported by /health.monitors), get each one's physical monitor handle(s), and
/// read/write brightness through them. Laptop-panel-only setups (no external monitor answers DDC)
/// come back as <c>supported:false</c> rather than failing — this project has no System.Management
/// reference, so the WMI (root\wmi WmiMonitorBrightness) fallback mentioned for laptop panels is
/// intentionally not implemented; DDC/CI is the only path.
/// </summary>
internal static class Brightness
{
    public sealed record MonitorBrightness(string Id, string Name, int Value);

    public static unsafe List<MonitorBrightness> ReadAll()
    {
        var result = new List<MonitorBrightness>();
        foreach (var mon in EnumerateMonitors())
        {
            foreach (var physical in OpenPhysicalMonitors(mon.Handle))
            {
                try
                {
                    if (TryReadBrightness(physical.hPhysicalMonitor, out var min, out var current, out var max) && max > min)
                    {
                        var value = (int)Math.Round((current - min) * 100.0 / (max - min));
                        result.Add(new MonitorBrightness(mon.Id, mon.Name, Math.Clamp(value, 0, 100)));
                    }
                }
                catch { /* this physical monitor does not answer DDC/CI brightness — skip it */ }
                finally
                {
                    DestroyPhysicalMonitor(physical);
                }
            }
        }
        return result;
    }

    /// <summary>Sets brightness on one monitor (by id) or every monitor that answers DDC/CI (id null/empty).</summary>
    public static unsafe bool TrySet(string? id, int value, out string? error)
    {
        value = Math.Clamp(value, 0, 100);
        var any = false;
        var lastError = (string?)null;
        foreach (var mon in EnumerateMonitors())
        {
            if (!string.IsNullOrEmpty(id) && mon.Id != id) continue;
            foreach (var physical in OpenPhysicalMonitors(mon.Handle))
            {
                try
                {
                    if (!TryReadBrightness(physical.hPhysicalMonitor, out var min, out _, out var max) || max <= min)
                    {
                        lastError = "GetMonitorBrightness failed";
                        continue;
                    }
                    var raw = (uint)Math.Round(min + value * (max - min) / 100.0);
                    if (PInvoke.SetMonitorBrightness(physical.hPhysicalMonitor, raw) != 0) any = true;
                    else lastError = "SetMonitorBrightness failed";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
                finally
                {
                    DestroyPhysicalMonitor(physical);
                }
            }
        }
        error = any ? null : (lastError ?? "no DDC/CI monitor responded");
        return any;
    }

    /// <summary>Calls the raw-HANDLE overload of GetMonitorBrightness explicitly (the friendly "out uint"
    /// overload CsWin32 generates only accepts a SafeHandle, which PHYSICAL_MONITOR.hPhysicalMonitor is not).</summary>
    private static unsafe bool TryReadBrightness(HANDLE handle, out uint min, out uint current, out uint max)
    {
        uint mn = 0, cur = 0, mx = 0;
        var ok = PInvoke.GetMonitorBrightness(handle, &mn, &cur, &mx) != 0;
        min = mn; current = cur; max = mx;
        return ok;
    }

    private sealed record MonitorHandle(string Id, string Name, HMONITOR Handle);

    private static unsafe List<MonitorHandle> EnumerateMonitors()
    {
        var list = new List<MonitorHandle>();
        PInvoke.EnumDisplayMonitors(HDC.Null, null, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFOEXW();
            mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(hMon, (MONITORINFO*)&mi))
            {
                var r = mi.monitorInfo.rcMonitor;
                var device = mi.szDevice.ToString();
                var width = r.right - r.left;
                var height = r.bottom - r.top;
                list.Add(new MonitorHandle(device + "|" + width + "x" + height, device, hMon));
            }
            return true;
        }, (LPARAM)0);
        return list;
    }

    private static unsafe List<PHYSICAL_MONITOR> OpenPhysicalMonitors(HMONITOR hMonitor)
    {
        var result = new List<PHYSICAL_MONITOR>();
        try
        {
            if (!PInvoke.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) return result;
            var buf = new PHYSICAL_MONITOR[count];
            if (!PInvoke.GetPhysicalMonitorsFromHMONITOR(hMonitor, buf)) return result;
            result.AddRange(buf);
        }
        catch { /* dxva2 not available / no DDC-capable monitor on this HMONITOR */ }
        return result;
    }

    private static unsafe void DestroyPhysicalMonitor(PHYSICAL_MONITOR physical)
    {
        try { PInvoke.DestroyPhysicalMonitors(new[] { physical }); } catch { }
    }
}
