using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Microsoft.Win32;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Taskbar;

/// <summary>
/// Styles the Windows taskbar(s): a per-state accent policy (clear/blur/acrylic/opaque) on the
/// taskbar windows, and the Windows toggles (alignment, buttons, clock, transparency, auto-hide)
/// through the user's registry with a backup so everything can be put back.
/// The accent path is the same undocumented SetWindowCompositionAttribute that desktop tools use;
/// newer Windows 11 builds paint the XAML taskbar background over it, so the result is reported
/// honestly through <see cref="Status"/> rather than promised.
/// </summary>
public sealed class TaskbarStyler : IDisposable
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly (string key, string name, string setting)[] RegistryMap =
    {
        (AdvancedKey, "TaskbarAl", "centered"),
        (SearchKey, "SearchboxTaskbarMode", "hideSearch"),
        (AdvancedKey, "ShowTaskViewButton", "hideTaskView"),
        (AdvancedKey, "TaskbarDa", "hideWidgets"),
        (AdvancedKey, "ShowSystrayDateTimeValueName", "hideClock"),
        (AdvancedKey, "TaskbarSi", "small"),
        (PersonalizeKey, "EnableTransparency", "transparency"),
        (AdvancedKey, "UseOLEDTaskbarTransparency", "oledTransparency"),
    };

    private readonly HostContext _ctx;
    private readonly Log _log;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<nint, string> _appliedKey = new();
    private readonly string _backupFile;
    private bool _accentEverApplied;
    private bool _disposed;
    public string? LastNote { get; private set; }
    public bool AccentSupported { get; private set; } = true;

    public TaskbarStyler(HostContext ctx, Dispatcher dispatcher)
    {
        _ctx = ctx;
        _log = ctx.Log;
        _backupFile = Path.Combine(ctx.Paths.DataDir, "taskbar-backup.json");
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    // ---- accent policy -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State; public int Flags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinCompAttrData { public int Attribute; public nint Data; public int Size; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WinCompAttrData data);

    /// <summary>Apply a surface style to any top-level window (taskbar or our own bars).</summary>
    public static bool ApplyAccent(nint hwnd, SurfaceStyle style)
    {
        var (state, color) = Translate(style);
        var policy = new AccentPolicy { State = state, Flags = 2, GradientColor = color, AnimationId = 0 };
        var size = Marshal.SizeOf(policy);
        var mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, mem, false);
            var data = new WinCompAttrData { Attribute = 19, Data = mem, Size = size };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    private static (int state, uint abgr) Translate(SurfaceStyle style)
    {
        var (r, g, b) = ParseColor(style.Color);
        var a = (uint)Math.Clamp((int)Math.Round(style.Opacity * 255), 0, 255);
        uint abgr = (a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
        return (style.Mode?.ToLowerInvariant()) switch
        {
            "clear" => (2, 0u),
            "blur" => (3, abgr),
            "acrylic" => (4, abgr),
            "opaque" => (1, 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r),
            _ => (0, 0u),
        };
    }

    public static (byte r, byte g, byte b) ParseColor(string? hex)
    {
        try
        {
            var h = (hex ?? "#0B0B0B").TrimStart('#');
            if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
            return (Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
        }
        catch { return (11, 11, 11); }
    }

    private static List<HWND> TaskbarWindows(bool secondary)
    {
        var list = new List<HWND>();
        var primary = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (primary != HWND.Null) list.Add(primary);
        if (!secondary) return list;
        var h = PInvoke.FindWindowEx(HWND.Null, HWND.Null, "Shell_SecondaryTrayWnd", null);
        while (h != HWND.Null)
        {
            list.Add(h);
            h = PInvoke.FindWindowEx(HWND.Null, h, "Shell_SecondaryTrayWnd", null);
        }
        return list;
    }

    private static string StateFor(HWND taskbar)
    {
        var mon = PInvoke.MonitorFromWindow(taskbar, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (FullscreenWatcher.GlobalFullscreen()) return "fullscreen";
        var rect = FullscreenWatcher.ForegroundAppRect(out var fg);
        if (rect is null || fg == HWND.Null) return "normal";
        var fgMon = PInvoke.MonitorFromWindow(fg, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (fgMon != mon) return "normal";
        var mi = DisplayMonitors.Enumerate().FirstOrDefault(m => m.Handle == mon);
        if (mi is not null && FullscreenWatcher.Covers(rect.Value, mi)) return "fullscreen";
        return PInvoke.IsZoomed(fg) ? "maximized" : "normal";
    }

    private void Tick()
    {
        if (_disposed) return;
        try
        {
            var cfg = _ctx.Config.App.Taskbar;
            if (!cfg.Enabled)
            {
                if (_accentEverApplied) ClearAccent();
                return;
            }
            foreach (var hwnd in TaskbarWindows(cfg.Secondary))
            {
                var state = StateFor(hwnd);
                var style = state switch { "fullscreen" => cfg.Fullscreen, "maximized" => cfg.Maximized, _ => cfg.Normal };
                var key = state + "|" + style.Mode + "|" + style.Color + "|" + style.Opacity;
                if (_appliedKey.TryGetValue((nint)hwnd, out var prev) && prev == key) continue;
                var ok = ApplyAccent((nint)hwnd, style);
                _appliedKey[(nint)hwnd] = key;
                _accentEverApplied = true;
                if (!ok) { AccentSupported = false; _log.Warn("taskbar accent apply failed: " + Marshal.GetLastWin32Error()); }
            }
        }
        catch (Exception ex)
        {
            _log.Error("taskbar tick", ex);
        }
    }

    private void ClearAccent()
    {
        foreach (var hwnd in TaskbarWindows(true))
        {
            ApplyAccent((nint)hwnd, new SurfaceStyle { Mode = "normal" });
        }
        _appliedKey.Clear();
        _accentEverApplied = false;
    }

    // ---- Windows toggles (registry + AppBar state) -------------------------------------------

    public void Apply()
    {
        var cfg = _ctx.Config.App.Taskbar;
        _appliedKey.Clear();
        if (!cfg.Enabled)
        {
            if (_accentEverApplied) ClearAccent();
            if (!_timer.IsEnabled) _timer.Start();
            return;
        }
        EnsureBackup();
        var w = cfg.Windows;
        var changed = false;
        changed |= WriteToggle(AdvancedKey, "TaskbarAl", w.Centered, onValue: 1, offValue: 0);
        changed |= WriteToggle(SearchKey, "SearchboxTaskbarMode", w.HideSearch, onValue: 0, offValue: 1);
        changed |= WriteToggle(AdvancedKey, "ShowTaskViewButton", w.HideTaskView, onValue: 0, offValue: 1);
        changed |= WriteToggle(AdvancedKey, "TaskbarDa", w.HideWidgets, onValue: 0, offValue: 1);
        changed |= WriteToggle(AdvancedKey, "ShowSystrayDateTimeValueName", w.HideClock, onValue: 0, offValue: 1);
        changed |= WriteToggle(AdvancedKey, "TaskbarSi", w.Small, onValue: 0, offValue: 1);
        changed |= WriteToggle(PersonalizeKey, "EnableTransparency", w.Transparency, onValue: 1, offValue: 0);
        changed |= WriteToggle(AdvancedKey, "UseOLEDTaskbarTransparency", w.OledTransparency, onValue: 1, offValue: 0);
        if (w.AutoHide is bool autoHide) SetAutoHide(autoHide);
        if (changed) Broadcast();
        LastNote = AccentSupported ? null : "accent policy is ignored by this Windows build";
        if (!_timer.IsEnabled) _timer.Start();
        Tick();
        _log.Info("taskbar: applied (registry changed=" + changed + ")");
    }

    private static bool WriteToggle(string key, string name, bool? setting, int onValue, int offValue)
    {
        if (setting is null) return false;
        var value = setting.Value ? onValue : offValue;
        using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
        var current = k?.GetValue(name);
        if (current is int i && i == value) return false;
        k?.SetValue(name, value, RegistryValueKind.DWord);
        return true;
    }

    private void EnsureBackup()
    {
        if (File.Exists(_backupFile)) return;
        var backup = new JsonObject();
        foreach (var (key, name, _) in RegistryMap)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            var v = k?.GetValue(name);
            backup[key + "\\" + name] = v is int i ? JsonValue.Create(i) : null;
        }
        backup["autoHide"] = JsonValue.Create(GetAutoHide());
        Json.WriteFileAtomic(_backupFile, backup.ToJsonString(Json.Config));
        _log.Info("taskbar: backup written " + _backupFile);
    }

    /// <summary>Put the Windows settings back exactly as they were before the first Apply.</summary>
    public bool Reset()
    {
        ClearAccent();
        var node = Json.LoadFile(_backupFile) as JsonObject;
        if (node is null) return false;
        foreach (var (key, name, _) in RegistryMap)
        {
            var id = key + "\\" + name;
            if (!node.ContainsKey(id)) continue;
            using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
            if (k is null) continue;
            var v = node[id];
            if (v is null) { try { k.DeleteValue(name, throwOnMissingValue: false); } catch { } }
            else k.SetValue(name, (int)v!, RegistryValueKind.DWord);
        }
        if (node["autoHide"] is JsonValue ah && ah.TryGetValue<bool>(out var auto)) SetAutoHide(auto);
        Broadcast();
        _log.Info("taskbar: reset to backup");
        return true;
    }

    private static unsafe void Broadcast()
    {
        foreach (var area in new[] { "TraySettings", "ImmersiveColorSet", "Policy" })
        {
            fixed (char* p = area)
            {
                nuint result;
                PInvoke.SendMessageTimeout(new HWND(0xFFFF), PInvoke.WM_SETTINGCHANGE, (WPARAM)0, (LPARAM)(nint)p,
                    SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 2000, &result);
            }
        }
    }

    private static unsafe bool GetAutoHide()
    {
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        var state = PInvoke.SHAppBarMessage(PInvoke.ABM_GETSTATE, &d);
        return ((uint)state & PInvoke.ABS_AUTOHIDE) != 0;
    }

    private static unsafe void SetAutoHide(bool on)
    {
        var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        d.hWnd = PInvoke.FindWindow("Shell_TrayWnd", null);
        d.lParam = (LPARAM)(nint)(on ? PInvoke.ABS_AUTOHIDE : 0u);
        PInvoke.SHAppBarMessage(PInvoke.ABM_SETSTATE, &d);
    }

    public static void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { }
        }
        Thread.Sleep(800);
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
    }

    public JsonObject Status()
    {
        var cfg = _ctx.Config.App.Taskbar;
        var reg = new JsonObject();
        foreach (var (key, name, setting) in RegistryMap)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            reg[setting] = k?.GetValue(name) is int i ? JsonValue.Create(i) : null;
        }
        reg["autoHide"] = JsonValue.Create(GetAutoHide());
        return new JsonObject
        {
            ["enabled"] = cfg.Enabled,
            ["preset"] = cfg.Preset,
            ["supported"] = new JsonObject { ["accent"] = AccentSupported, ["accentVisible"] = JsonValue.Create(false) },
            ["note"] = "Windows 11 builds with the XAML taskbar (24H2 and later) paint their own background: accent transparency may have no visible effect. Windows toggles and the top bar work regardless.",
            ["windows"] = reg,
            ["taskbars"] = TaskbarWindows(true).Count,
            ["backup"] = File.Exists(_backupFile),
            ["explorerPid"] = Process.GetProcessesByName("explorer").FirstOrDefault()?.Id,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try { if (_accentEverApplied) ClearAccent(); } catch { }
    }
}
