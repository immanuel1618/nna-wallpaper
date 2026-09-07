using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace NNA.Wallpaper.Engine;

/// <summary>One physical monitor as seen by the engine (virtual-screen coordinates, may be negative).</summary>
public sealed class MonitorInfo
{
    public string Device { get; init; } = "";
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Primary { get; init; }
    public uint Dpi { get; init; } = 96;
    public HMONITOR Handle { get; init; }

    /// <summary>Stable-ish id: "{Device}|{Width}x{Height}".</summary>
    public string Id => Device + "|" + Width + "x" + Height;
    public string Name => (Primary ? "Main " : "") + Width + "x" + Height + (Height > Width ? " (portrait)" : "");
    public double Scale => Dpi / 96.0;
    public bool Contains(int x, int y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;
}

public static class DisplayMonitors
{
    public static unsafe List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        PInvoke.EnumDisplayMonitors(HDC.Null, null, (hMon, hdc, rect, lparam) =>
        {
            var mi = new MONITORINFOEXW();
            mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!PInvoke.GetMonitorInfo(hMon, (MONITORINFO*)&mi)) return true;
            uint dpiX = 96, dpiY = 96;
            try { PInvoke.GetDpiForMonitor(hMon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY); } catch { }
            var r = mi.monitorInfo.rcMonitor;
            list.Add(new MonitorInfo
            {
                Device = mi.szDevice.ToString(),
                Left = r.left,
                Top = r.top,
                Width = r.right - r.left,
                Height = r.bottom - r.top,
                Primary = (mi.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0,
                Dpi = dpiX == 0 ? 96 : dpiX,
                Handle = hMon,
            });
            return true;
        }, (LPARAM)0);
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Left).ToList();
    }
}
