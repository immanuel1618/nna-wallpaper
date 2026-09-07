using System.Runtime.InteropServices;
using System.Windows.Interop;
using NNA.Wallpaper.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Engine;

/// <summary>
/// Mouse input for wallpaper windows. The desktop icon list sits above the wallpaper layer, so the
/// wallpaper HWNDs never receive mouse messages themselves. A hidden message-only window registers
/// Raw Input (RIDEV_INPUTSINK) and, when the pointer is over the bare desktop, re-posts the mouse
/// messages to the Chromium child of the wallpaper window under the cursor. Right button is never
/// forwarded (desktop context menu must keep working). Own design from the Raw Input documentation.
/// </summary>
public sealed class InputBridge : IDisposable
{
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
    private const ushort RI_MOUSE_WHEEL = 0x0400;
    private const ushort RI_MOUSE_HWHEEL = 0x0800;
    private const nint HWND_MESSAGE = -3;

    private readonly Log _log;
    private readonly Func<IReadOnlyList<WallpaperWindow>> _windows;
    private readonly DesktopHost _desktop;
    private HwndSource? _sink;
    private bool _left, _middle;
    private WallpaperWindow? _captured;
    public long Forwarded { get; private set; }
    public bool Enabled { get; set; } = true;

    public InputBridge(DesktopHost desktop, Func<IReadOnlyList<WallpaperWindow>> windows, Log log)
    {
        _desktop = desktop;
        _windows = windows;
        _log = log;
    }

    public unsafe bool Start()
    {
        var p = new HwndSourceParameters("NNA Wallpaper Input")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _sink = new HwndSource(p);
        _sink.AddHook(WndProc);

        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01,
            usUsage = 0x02, // mouse
            dwFlags = (RAWINPUTDEVICE_FLAGS)RIDEV_INPUTSINK,
            hwndTarget = (HWND)_sink.Handle,
        };
        var ok = PInvoke.RegisterRawInputDevices(new ReadOnlySpan<RAWINPUTDEVICE>(&rid, 1), (uint)sizeof(RAWINPUTDEVICE));
        if (!ok) _log.Error("raw input registration failed: " + Marshal.GetLastWin32Error());
        else _log.Info("raw input registered");
        return ok;
    }

    private unsafe nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != (int)PInvoke.WM_INPUT || !Enabled) return 0;
        try
        {
            uint size = 0;
            var header = (uint)sizeof(RAWINPUTHEADER);
            PInvoke.GetRawInputData((HRAWINPUT)lParam, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, null, &size, header);
            if (size == 0 || size > 1024) return 0;
            var buffer = stackalloc byte[(int)size];
            if (PInvoke.GetRawInputData((HRAWINPUT)lParam, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, buffer, &size, header) != size) return 0;
            var raw = (RAWINPUT*)buffer;
            if (raw->header.dwType != (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE) return 0;
            var flags = raw->data.mouse.Anonymous.Anonymous.usButtonFlags;
            var data = (short)raw->data.mouse.Anonymous.Anonymous.usButtonData;
            OnMouse(flags, data);
        }
        catch (Exception ex)
        {
            _log.Error("raw input", ex);
        }
        return 0;
    }

    private void OnMouse(ushort flags, short data)
    {
        if ((flags & (RI_MOUSE_RIGHT_BUTTON_DOWN | RI_MOUSE_RIGHT_BUTTON_UP)) != 0) return;

        if (!PInvoke.GetCursorPos(out var pt)) return;

        WallpaperWindow? target;
        if (_captured is not null && _left)
        {
            target = _captured; // keep the press/release pair on the same window
        }
        else
        {
            if (!PointerOnDesktop(pt)) { ReleaseIfNeeded(pt); return; }
            target = FindWindow(pt.X, pt.Y);
        }
        if (target is null || !target.Ready || target.Paused) return;

        var hwnd = target.InputTarget();
        if (hwnd == HWND.Null) return;

        var client = pt;
        PInvoke.ScreenToClient(hwnd, ref client);
        var lparamClient = MakeLParam(client.X, client.Y);
        var lparamScreen = MakeLParam(pt.X, pt.Y);

        if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0)
        {
            _left = true;
            _captured = target;
            Post(hwnd, PInvoke.WM_LBUTTONDOWN, Keys(), lparamClient);
        }
        else if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0)
        {
            Post(hwnd, PInvoke.WM_LBUTTONUP, Keys(exceptLeft: true), lparamClient);
            _left = false;
            _captured = null;
        }
        else if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
        {
            _middle = true;
            Post(hwnd, PInvoke.WM_MBUTTONDOWN, Keys(), lparamClient);
        }
        else if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0)
        {
            _middle = false;
            Post(hwnd, PInvoke.WM_MBUTTONUP, Keys(), lparamClient);
        }
        else if ((flags & RI_MOUSE_WHEEL) != 0)
        {
            Post(hwnd, PInvoke.WM_MOUSEWHEEL, MakeWParam(Keys(), data), lparamScreen);
        }
        else if ((flags & RI_MOUSE_HWHEEL) != 0)
        {
            Post(hwnd, PInvoke.WM_MOUSEHWHEEL, MakeWParam(Keys(), data), lparamScreen);
        }
        else
        {
            Post(hwnd, PInvoke.WM_MOUSEMOVE, Keys(), lparamClient);
        }
    }

    private void ReleaseIfNeeded(System.Drawing.Point pt)
    {
        // Pointer left the desktop while a forwarded press was active: send the release so the page does not stick.
        if (_left && _captured is not null && _captured.Ready)
        {
            var hwnd = _captured.InputTarget();
            var client = pt;
            PInvoke.ScreenToClient(hwnd, ref client);
            Post(hwnd, PInvoke.WM_LBUTTONUP, 0, MakeLParam(client.X, client.Y));
        }
        _left = false;
        _captured = null;
    }

    private void Post(HWND hwnd, uint msg, nuint wparam, nint lparam)
    {
        PInvoke.PostMessage(hwnd, msg, (WPARAM)wparam, (LPARAM)lparam);
        Forwarded++;
    }

    private nuint Keys(bool exceptLeft = false)
    {
        nuint k = 0;
        if (_left && !exceptLeft) k |= (nuint)MODIFIERKEYS_FLAGS.MK_LBUTTON;
        if (_middle) k |= (nuint)MODIFIERKEYS_FLAGS.MK_MBUTTON;
        return k;
    }

    private static nint MakeLParam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
    private static nuint MakeWParam(nuint keys, short delta) => (nuint)(((uint)(ushort)delta << 16) | (uint)(keys & 0xFFFF));

    private WallpaperWindow? FindWindow(int x, int y)
    {
        foreach (var w in _windows())
        {
            if (w.Monitor.Contains(x, y)) return w;
        }
        return null;
    }

    /// <summary>True when the window under the cursor is the desktop surface (icon list, WorkerW, Progman or our own windows).</summary>
    private bool PointerOnDesktop(System.Drawing.Point pt)
    {
        var under = PInvoke.WindowFromPoint(pt);
        if (under == HWND.Null) return false;
        var cls = DesktopHost.ClassName(under);
        switch (cls)
        {
            case "Progman":
            case "WorkerW":
            case "SHELLDLL_DefView":
                return true;
            case "SysListView32":
            {
                var parent = PInvoke.GetParent(under);
                return DesktopHost.ClassName(parent) == "SHELLDLL_DefView";
            }
            case "Chrome_WidgetWin_0":
            case "Chrome_WidgetWin_1":
            case "Chrome_RenderWidgetHostHWND":
            case "Intermediate D3D Window":
            {
                var root = PInvoke.GetAncestor(under, GET_ANCESTOR_FLAGS.GA_ROOT);
                foreach (var w in _windows())
                {
                    if (w.Hwnd == under || IsDescendant(under, w.Hwnd)) return true;
                }
                return root == _desktop.Progman || root == _desktop.WorkerW;
            }
            default:
                foreach (var w in _windows())
                {
                    if (w.Hwnd == under) return true;
                }
                return false;
        }
    }

    private static bool IsDescendant(HWND child, HWND ancestor)
    {
        var h = child;
        for (var i = 0; i < 8 && h != HWND.Null; i++)
        {
            h = PInvoke.GetParent(h);
            if (h == ancestor) return true;
        }
        return false;
    }

    public void Dispose()
    {
        try { _sink?.Dispose(); } catch { }
        _sink = null;
    }
}
