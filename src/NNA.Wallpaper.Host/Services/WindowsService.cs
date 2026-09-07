using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /windows, POST /windows/activate|minimize|close, GET /windows/icon — lets the launchpad
/// (and later the dock) raise an already-open window instead of spawning a duplicate. Enumerates
/// top-level, user-facing windows only (visible, unowned or owned-but-invisible-owner, not a tool
/// window, not DWM-cloaked, non-empty title); UWP apps are reported through their outer
/// <c>ApplicationFrameWindow</c> but process/exe/aumid come from the inner CoreWindow's process.
/// </summary>
public sealed class WindowsService : IHostService
{
    /// <summary>Set by the constructor so <see cref="LaunchService"/> can look up/raise windows without a DI container.</summary>
    public static WindowsService? Current { get; private set; }

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(500);

    private readonly HostContext _ctx;
    private readonly object _cacheLock = new();
    private DateTime _cacheAt = DateTime.MinValue;
    private List<WindowInfo> _cache = new();

    public WindowsService(HostContext ctx)
    {
        _ctx = ctx;
        Current = this;
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/windows", GetWindows);
        api.Map("POST", "/windows/activate", req => PostAction(req, "activate", hwnd => WindowActivator.Activate(hwnd, _ctx.Log)));
        api.Map("POST", "/windows/minimize", req => PostAction(req, "minimize", hwnd => PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE)));
        api.Map("POST", "/windows/close", req => PostAction(req, "close", hwnd => PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default)));
        api.Map("GET", "/windows/icon", GetIcon);
    }

    /// <summary>One matched window: hwnd plus everything the launchpad/dock needs to render and act on it.</summary>
    public sealed record WindowInfo(
        long Hwnd, string Title, int Pid, string? ProcessName, string? ExePath,
        string? Aumid, bool Minimized, bool Foreground, string? MonitorId);

    // --------------------------------------------------------------------------- GET /windows

    private Task GetWindows(ApiRequest req)
    {
        var list = GetWindowsCached();
        var arr = new JsonArray(list.Select(w => (JsonNode)new JsonObject
        {
            ["hwnd"] = w.Hwnd,
            ["title"] = w.Title,
            ["pid"] = w.Pid,
            ["processName"] = w.ProcessName,
            ["exePath"] = w.ExePath,
            ["aumid"] = w.Aumid,
            ["minimized"] = w.Minimized,
            ["foreground"] = w.Foreground,
            ["monitorId"] = w.MonitorId,
        }).ToArray());
        return req.Json(new JsonObject { ["windows"] = arr });
    }

    private List<WindowInfo> GetWindowsCached()
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _cacheAt < CacheTtl) return _cache;
        }
        var fresh = EnumerateWindowsNow();
        lock (_cacheLock)
        {
            _cache = fresh;
            _cacheAt = DateTime.UtcNow;
            return _cache;
        }
    }

    // --------------------------------------------------------------------------- POST /windows/activate|minimize|close

    private async Task PostAction(ApiRequest req, string name, Func<HWND, bool> action)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        long hwndValue = 0;
        if (Json.ParseNode(body) is JsonObject obj && obj.TryGetPropertyValue("hwnd", out var hn) && hn is JsonValue v)
        {
            v.TryGetValue(out hwndValue);
        }
        if (hwndValue == 0)
        {
            var q = req.Query("hwnd");
            long.TryParse(q, out hwndValue);
        }
        if (hwndValue == 0)
        {
            await req.Json(new { ok = false, error = "bad hwnd" }, 400).ConfigureAwait(false);
            return;
        }

        bool ok;
        try
        {
            ok = action((HWND)(nint)hwndValue);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"windows/{name} hwnd={hwndValue} failed: {ex.Message}");
            ok = false;
        }
        await req.Json(new { ok, hwnd = hwndValue }).ConfigureAwait(false);
    }

    // --------------------------------------------------------------------------- GET /windows/icon?hwnd=

    private async Task GetIcon(ApiRequest req)
    {
        if (!long.TryParse(req.Query("hwnd"), out var hwndValue) || hwndValue == 0)
        {
            await req.Error(400, "bad hwnd").ConfigureAwait(false);
            return;
        }

        var outFile = Path.Combine(_ctx.Paths.IconsDir, "win-" + hwndValue + ".png");
        if (!File.Exists(outFile))
        {
            var info = EnumerateWindowsNow().FirstOrDefault(w => w.Hwnd == hwndValue);
            var hwnd = (HWND)(nint)hwndValue;
            bool ok = await Task.Run(() => ExtractWindowIcon(hwnd, info, outFile)).ConfigureAwait(false);
            if (!ok)
            {
                await req.Error(404, "no icon").ConfigureAwait(false);
                return;
            }
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(outFile).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await req.Error(404, "no icon").ConfigureAwait(false);
            return;
        }
        await req.Bytes(bytes, "image/png", 200, "max-age=3600").ConfigureAwait(false);
    }

    /// <summary>WM_GETICON (big, then small) then the class icon; falls back to the exe's own icon via IconExtractor.</summary>
    private static bool ExtractWindowIcon(HWND hwnd, WindowInfo? info, string outFile)
    {
        try
        {
            const int IconBig = 1, IconSmall2 = 2;
            nint hIcon = (nint)PInvoke.SendMessage(hwnd, PInvoke.WM_GETICON, new WPARAM((nuint)IconBig), default);
            if (hIcon == 0) hIcon = (nint)PInvoke.SendMessage(hwnd, PInvoke.WM_GETICON, new WPARAM((nuint)IconSmall2), default);
            // GetClassLong (not the -Ptr version, which CsWin32 refuses to generate for AnyCPU) is
            // fine here: HICON values always fit in 32 bits, even on 64-bit Windows.
            if (hIcon == 0) hIcon = (nint)PInvoke.GetClassLong(hwnd, GET_CLASS_LONG_INDEX.GCLP_HICON);
            if (hIcon == 0) hIcon = (nint)PInvoke.GetClassLong(hwnd, GET_CLASS_LONG_INDEX.GCLP_HICONSM);

            if (hIcon != 0)
            {
                using var icon = Icon.FromHandle(hIcon);
                using var bmp = icon.ToBitmap();
                if (IconExtractor.SaveAtomic(bmp, outFile)) return true;
            }
        }
        catch
        {
            // fall through to the exe-icon fallback below
        }

        return info?.ExePath is not null && IconExtractor.TryExtract(info.ExePath, info.Aumid, outFile);
    }

    /// <summary>The current (cached, up to 500ms stale) window list — used by DockService to detect
    /// when the open-window set changes without re-enumerating on every poll tick.</summary>
    public List<WindowInfo> Snapshot() => GetWindowsCached();

    // --------------------------------------------------------------------------- matching (LaunchService, DockService)

    /// <summary>
    /// Finds an already-open window for a launch item: by AUMID first, then by the launch
    /// command's full path (when it is one), then by process name (the command's file name
    /// without extension). Callers must not use this for "open" (document/URL) items.
    /// </summary>
    public WindowInfo? FindForItem(string? aumid, string? cmd)
    {
        var windows = EnumerateWindowsNow();

        if (!string.IsNullOrEmpty(aumid))
        {
            var byAumid = windows.FirstOrDefault(w => string.Equals(w.Aumid, aumid, StringComparison.OrdinalIgnoreCase));
            if (byAumid is not null) return byAumid;
        }

        if (!string.IsNullOrEmpty(cmd))
        {
            if (Path.IsPathRooted(cmd))
            {
                string? full = null;
                try { full = Path.GetFullPath(cmd); } catch { }
                if (full is not null)
                {
                    var byPath = windows.FirstOrDefault(w =>
                        w.ExePath is not null && PathsEqual(w.ExePath, full));
                    if (byPath is not null) return byPath;
                }
            }

            string? name = null;
            try { name = Path.GetFileNameWithoutExtension(cmd); } catch { }
            if (!string.IsNullOrEmpty(name))
            {
                var byName = windows.FirstOrDefault(w => string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase));
                if (byName is not null) return byName;
            }
        }

        return null;
    }

    /// <summary>
    /// Same matching rules as <see cref="FindForItem"/> but returns every match instead of the
    /// first one — the dock needs the full window list of a pinned app (right-click menu, window
    /// count under the running dot), not just one to raise.
    /// </summary>
    public List<WindowInfo> FindAllForItem(string? aumid, string? cmd)
    {
        var windows = EnumerateWindowsNow();

        if (!string.IsNullOrEmpty(aumid))
        {
            var byAumid = windows.Where(w => string.Equals(w.Aumid, aumid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byAumid.Count > 0) return byAumid;
        }

        if (!string.IsNullOrEmpty(cmd))
        {
            if (Path.IsPathRooted(cmd))
            {
                string? full = null;
                try { full = Path.GetFullPath(cmd); } catch { }
                if (full is not null)
                {
                    var byPath = windows.Where(w => w.ExePath is not null && PathsEqual(w.ExePath, full)).ToList();
                    if (byPath.Count > 0) return byPath;
                }
            }

            string? name = null;
            try { name = Path.GetFileNameWithoutExtension(cmd); } catch { }
            if (!string.IsNullOrEmpty(name))
            {
                var byName = windows.Where(w => string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byName.Count > 0) return byName;
            }
        }

        return new List<WindowInfo>();
    }

    /// <summary>Finds a window belonging to <paramref name="rootPid"/> or one of its descendants (up to 3 levels deep).</summary>
    public WindowInfo? FindByPidTree(int rootPid)
    {
        var windows = EnumerateWindowsNow();
        if (windows.Any(w => w.Pid == rootPid)) return windows.First(w => w.Pid == rootPid);

        var pids = new HashSet<int> { rootPid };
        CollectDescendants(rootPid, pids, depth: 3);
        return windows.FirstOrDefault(w => pids.Contains(w.Pid));
    }

    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static void CollectDescendants(int pid, HashSet<int> acc, int depth)
    {
        if (depth <= 0) return;
        foreach (var child in GetChildProcessIds(pid))
        {
            if (acc.Add(child)) CollectDescendants(child, acc, depth - 1);
        }
    }

    private static unsafe List<int> GetChildProcessIds(int parentPid)
    {
        var result = new List<int>();
        var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot is null || snapshot.IsInvalid) return result;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)sizeof(PROCESSENTRY32) };
            if (!PInvoke.Process32First(snapshot, ref entry)) return result;
            do
            {
                if (entry.th32ParentProcessID == (uint)parentPid) result.Add((int)entry.th32ProcessID);
            } while (PInvoke.Process32Next(snapshot, ref entry));
        }
        finally
        {
            snapshot.Dispose();
        }
        return result;
    }

    // --------------------------------------------------------------------------- enumeration

    private List<WindowInfo> EnumerateWindowsNow()
    {
        var result = new List<WindowInfo>();
        var foreground = PInvoke.GetForegroundWindow();

        PInvoke.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (IsCandidateWindow(hwnd))
                {
                    var info = BuildWindowInfo(hwnd, foreground);
                    if (info is not null) result.Add(info);
                }
            }
            catch
            {
                // a window can be destroyed mid-enumeration; skip it
            }
            return true;
        }, default);

        return result;
    }

    private static bool IsCandidateWindow(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd)) return false;

        var owner = PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER);
        if (owner != default && PInvoke.IsWindowVisible(owner)) return false;

        int exStyle = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (((WINDOW_EX_STYLE)exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0) return false;

        if (IsCloaked(hwnd)) return false;

        return true;
    }

    private static unsafe bool IsCloaked(HWND hwnd)
    {
        int cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return hr.Succeeded && cloaked != 0;
    }

    private WindowInfo? BuildWindowInfo(HWND hwnd, HWND foreground)
    {
        var title = GetWindowTextOf(hwnd);
        if (string.IsNullOrEmpty(title)) return null;

        PInvoke.GetWindowThreadProcessId(hwnd, out uint pid);
        int infoPid = (int)pid;

        // ApplicationFrameWindow hosts UWP apps: the window itself belongs to ApplicationFrameHost.exe,
        // but the real app (and its AUMID) lives in the child CoreWindow's process.
        if (string.Equals(GetClassNameOf(hwnd), "ApplicationFrameWindow", StringComparison.Ordinal))
        {
            var core = FindCoreWindowChild(hwnd);
            if (core != default)
            {
                PInvoke.GetWindowThreadProcessId(core, out uint corePid);
                if (corePid != 0) infoPid = (int)corePid;
            }
        }

        var (exePath, processName) = QueryProcessInfo(infoPid);
        var aumid = QueryAumid(infoPid);
        bool minimized = PInvoke.IsIconic(hwnd);
        bool isForeground = hwnd == foreground;
        string? monitorId = ResolveMonitorId(hwnd);

        return new WindowInfo((long)(nint)hwnd, title, infoPid, processName, exePath, aumid, minimized, isForeground, monitorId);
    }

    private static HWND FindCoreWindowChild(HWND parent)
    {
        HWND found = default;
        PInvoke.EnumChildWindows(parent, (hwnd, _) =>
        {
            if (string.Equals(GetClassNameOf(hwnd), "Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, default);
        return found;
    }

    private static string GetWindowTextOf(HWND hwnd)
    {
        Span<char> buf = stackalloc char[512];
        int len = PInvoke.GetWindowText(hwnd, buf);
        return len > 0 ? new string(buf[..len]) : "";
    }

    private static string GetClassNameOf(HWND hwnd)
    {
        Span<char> buf = stackalloc char[256];
        int len = PInvoke.GetClassName(hwnd, buf);
        return len > 0 ? new string(buf[..len]) : "";
    }

    /// <summary>Process name (always, cheap) and full exe path (needs QueryFullProcessImageName; null without access).</summary>
    private static (string? exePath, string? processName) QueryProcessInfo(int pid)
    {
        string? processName = null;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            processName = proc.ProcessName;
        }
        catch
        {
            // process gone, or access denied for its name — leave null
        }

        string? exePath = null;
        using var hProcess = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProcess is not null && !hProcess.IsInvalid)
        {
            try
            {
                Span<char> buf = stackalloc char[1024];
                uint size = (uint)buf.Length;
                if (PInvoke.QueryFullProcessImageName(hProcess, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buf, ref size) && size > 0)
                {
                    exePath = new string(buf[..(int)size]);
                }
            }
            catch
            {
                // no permission (elevated/protected process) — leave null
            }
        }
        return (exePath, processName);
    }

    /// <summary>AUMID of a packaged app's process, or null for classic desktop apps / on any failure.</summary>
    private static string? QueryAumid(int pid)
    {
        using var hProcess = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProcess is null || hProcess.IsInvalid) return null;
        try
        {
            uint length = 0;
            PInvoke.GetApplicationUserModelId(hProcess, ref length, default(Span<char>));
            if (length == 0) return null;

            Span<char> buf = stackalloc char[(int)length];
            var err = PInvoke.GetApplicationUserModelId(hProcess, ref length, buf);
            if (err != WIN32_ERROR.ERROR_SUCCESS) return null;

            int len = buf.IndexOf('\0');
            if (len < 0) len = buf.Length;
            return len > 0 ? new string(buf[..len]) : null;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveMonitorId(HWND hwnd)
    {
        var hMonitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (hMonitor == default) return null;

        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        unsafe
        {
            if (!PInvoke.GetMonitorInfo(hMonitor, &mi)) return null;
        }
        var r = mi.rcMonitor;

        foreach (var m in _ctx.App.Monitors)
        {
            if (m.x == r.left && m.y == r.top && m.width == r.right - r.left && m.height == r.bottom - r.top)
                return m.id;
        }
        int cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
        foreach (var m in _ctx.App.Monitors)
        {
            if (cx >= m.x && cx < m.x + m.width && cy >= m.y && cy < m.y + m.height) return m.id;
        }
        return null;
    }
}
