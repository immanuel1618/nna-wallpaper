using System.Text;
using NNA.Wallpaper.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Engine;

/// <summary>
/// Finds the desktop layer that sits behind the icons and gives us a parent HWND for wallpaper windows.
/// Two desktop flavours, chosen by a window style rather than by OS version:
///  - "new" desktop (Windows 11 24H2+): Progman has WS_EX_NOREDIRECTIONBITMAP, WorkerW is a child of Progman;
///  - classic: after message 0x052C explorer creates a top-level WorkerW right behind the one that hosts
///    SHELLDLL_DefView.
/// Technique from Microsoft documentation and public research; no third-party code.
/// </summary>
public sealed class DesktopHost
{
    private const uint WM_SPAWN_WORKER = 0x052C;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    private readonly Log _log;

    public HWND Progman { get; private set; }
    public HWND WorkerW { get; private set; }
    public HWND DefView { get; private set; }
    public bool NewDesktop { get; private set; }
    /// <summary>Where wallpaper windows are parented: WorkerW when found, otherwise Progman.</summary>
    public HWND Parent => WorkerW != HWND.Null ? WorkerW : Progman;
    public string Mode => NewDesktop ? "new-desktop" : "classic";

    public DesktopHost(Log log)
    {
        _log = log;
    }

    public bool IsAlive() => Parent != HWND.Null && PInvoke.IsWindow(Parent);

    public unsafe bool Attach()
    {
        Progman = PInvoke.FindWindow("Progman", null);
        if (Progman == HWND.Null)
        {
            _log.Error("desktop: Progman not found");
            return false;
        }

        var ex = PInvoke.GetWindowLong(Progman, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        NewDesktop = (ex & WS_EX_NOREDIRECTIONBITMAP) != 0;

        // Ask explorer to create the wallpaper worker layer (safe in both modes). Retry once after 500 ms.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            nuint result = 0;
            PInvoke.SendMessageTimeout(Progman, WM_SPAWN_WORKER, (WPARAM)0xD, (LPARAM)0x1,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_NORMAL, 1000, &result);
            Locate();
            if (WorkerW != HWND.Null) break;
            Thread.Sleep(500);
        }

        _log.Info($"desktop: mode={Mode} progman=0x{(nint)Progman:X} workerw=0x{(nint)WorkerW:X} defview=0x{(nint)DefView:X}");
        return Parent != HWND.Null;
    }

    private void Locate()
    {
        WorkerW = HWND.Null;
        DefView = HWND.Null;

        if (NewDesktop)
        {
            DefView = PInvoke.FindWindowEx(Progman, HWND.Null, "SHELLDLL_DefView", null);
            // Pick the WorkerW child of Progman that is NOT the one hosting the icons.
            var child = PInvoke.FindWindowEx(Progman, HWND.Null, "WorkerW", null);
            while (child != HWND.Null)
            {
                if (PInvoke.FindWindowEx(child, HWND.Null, "SHELLDLL_DefView", null) == HWND.Null)
                {
                    WorkerW = child;
                    break;
                }
                child = PInvoke.FindWindowEx(Progman, child, "WorkerW", null);
            }
            if (DefView == HWND.Null)
            {
                // Some builds keep DefView inside a WorkerW child even on the new desktop.
                var w = PInvoke.FindWindowEx(Progman, HWND.Null, "WorkerW", null);
                while (w != HWND.Null && DefView == HWND.Null)
                {
                    DefView = PInvoke.FindWindowEx(w, HWND.Null, "SHELLDLL_DefView", null);
                    w = PInvoke.FindWindowEx(Progman, w, "WorkerW", null);
                }
            }
            return;
        }

        // Classic: top-level window that contains SHELLDLL_DefView; the wallpaper WorkerW is its next sibling.
        HWND found = HWND.Null, worker = HWND.Null;
        PInvoke.EnumWindows((hwnd, lparam) =>
        {
            var dv = PInvoke.FindWindowEx(hwnd, HWND.Null, "SHELLDLL_DefView", null);
            if (dv != HWND.Null)
            {
                found = dv;
                worker = PInvoke.FindWindowEx(HWND.Null, hwnd, "WorkerW", null);
                return false;
            }
            return true;
        }, (LPARAM)0);
        DefView = found;
        if (DefView == HWND.Null)
        {
            DefView = PInvoke.FindWindowEx(Progman, HWND.Null, "SHELLDLL_DefView", null);
        }
        WorkerW = worker;
    }

    /// <summary>Refresh the desktop background so stale wallpaper pixels do not show through.</summary>
    public static unsafe void RefreshDesktop()
    {
        try
        {
            PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETDESKWALLPAPER, 0, null,
                SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS.SPIF_UPDATEINIFILE);
        }
        catch { }
    }

    public static string ClassName(HWND hwnd)
    {
        if (hwnd == HWND.Null) return "";
        Span<char> buf = stackalloc char[128];
        var n = PInvoke.GetClassName(hwnd, buf);
        return n > 0 ? new string(buf[..n]) : "";
    }
}
