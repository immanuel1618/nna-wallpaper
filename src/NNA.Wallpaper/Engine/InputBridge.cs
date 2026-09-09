using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
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
/// Raw Input (RIDEV_INPUTSINK) and, when the pointer is over the bare desktop, re-dispatches the
/// mouse event to the wallpaper window under the cursor: for a "window"-hosted target (the default —
/// see <see cref="Host.Config.EngineSettings"/>) that means <c>PostMessage</c> into its Chromium child
/// (<see cref="WallpaperWindow.InputTarget"/>), plus <see cref="HoverKeepAlive"/> re-sending
/// WM_MOUSEMOVE on a timer to fight the hover-flicker race documented there; for a
/// "composition"-hosted target (opt-in fallback — see <see cref="CompositionHost"/>) it means
/// <see cref="WallpaperWindow.SendMouse"/>, WebView2's own <c>SendMouseInput</c> API, since those
/// windows have no Chromium child HWND to post into. Right button is never forwarded (desktop
/// context menu must keep working). Own design from the Raw Input documentation.
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
    /// <summary>Re-sends WM_MOUSEMOVE to the current window-mode hover target every 60ms while the
    /// cursor sits still over it — see <see cref="HoverKeepAlive"/>.</summary>
    private readonly DispatcherTimer _hoverKeepAlive;
    /// <summary>Capture (press/release pairing) and hover (composition-hosted Leave bookkeeping)
    /// state — see <see cref="PointerState{T}"/> for why this is split into its own, Win32-free
    /// class.</summary>
    private readonly PointerState<WallpaperWindow> _state = new();
    public long Forwarded { get; private set; }
    public bool Enabled { get; set; } = true;

    public InputBridge(DesktopHost desktop, Func<IReadOnlyList<WallpaperWindow>> windows, Log log, Dispatcher dispatcher)
    {
        _desktop = desktop;
        _windows = windows;
        _log = log;
        _hoverKeepAlive = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => HoverKeepAlive(), dispatcher);
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
        _hoverKeepAlive.Start();
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
        if (_state.Captured is not null && _state.Left)
        {
            target = _state.Captured; // keep the press/release pair on the same window
        }
        else
        {
            if (!PointerOnDesktop(pt)) { ReleaseIfNeeded(pt); return; }
            target = FindWindow(pt.X, pt.Y);
        }
        if (target is null || !target.Ready || target.Paused)
        {
            // The captured/hovered window went away mid-gesture (paused, closing, mid-reattach): do
            // not leave a stuck button-down or an undelivered hover Leave behind for it.
            ReleaseIfNeeded(pt);
            return;
        }

        SetHover(target);

        if (target.UsesComposition)
        {
            OnMouseComposition(target, pt, flags, data);
            return;
        }

        var hwnd = target.InputTarget();
        if (hwnd == HWND.Null) return;

        var client = pt;
        PInvoke.ScreenToClient(hwnd, ref client);
        var lparamClient = MakeLParam(client.X, client.Y);
        var lparamScreen = MakeLParam(pt.X, pt.Y);

        if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0)
        {
            _state.Press(target);
            Post(hwnd, PInvoke.WM_LBUTTONDOWN, Keys(), lparamClient);
        }
        else if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0)
        {
            Post(hwnd, PInvoke.WM_LBUTTONUP, Keys(exceptLeft: true), lparamClient);
            _state.Release();
        }
        else if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
        {
            _state.PressMiddle();
            Post(hwnd, PInvoke.WM_MBUTTONDOWN, Keys(), lparamClient);
        }
        else if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0)
        {
            _state.ReleaseMiddle();
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

    /// <summary>
    /// Fights the window-mode hover-flicker race from <see cref="CompositionHost"/>'s doc comment:
    /// Chromium's own <c>TrackMouseEvent(TME_LEAVE)</c> on the forwarded-to Chromium child window
    /// races against Windows resolving "window under the cursor" against the real, unclipped desktop
    /// (the icon list), which fires <c>WM_MOUSELEAVE</c> right after every real cursor move re-arms
    /// tracking — the block's <c>:hover</c> state flips on/off. This does not eliminate the race (that
    /// needs composition hosting, which does not work behind the icon layer for a different reason —
    /// see <see cref="Host.Config.EngineSettings"/>); it shortens the flicker window by re-asserting
    /// <c>WM_MOUSEMOVE</c> to the same client point every 60ms while the pointer sits still over the
    /// same window, so a spurious Leave gets a fresh Enter again quickly instead of only on the next
    /// real cursor motion. Ticks from <see cref="_hoverKeepAlive"/> (started in <see cref="Start"/>).
    /// A no-op whenever nothing is currently hovered, the hover target is composition-hosted (no
    /// Chromium child HWND to re-post into), not ready, paused, or the real cursor has actually left
    /// it or the desktop (real WM_MOUSEMOVE forwarding in <see cref="OnMouse"/> already handles those
    /// transitions — this only refreshes an unchanged hover).
    /// </summary>
    private void HoverKeepAlive()
    {
        if (!Enabled) return;
        var target = _state.HoverWindow;
        if (target is null || target.UsesComposition || !target.Ready || target.Paused) return;
        if (!PInvoke.GetCursorPos(out var pt)) return;
        if (!PointerOnDesktop(pt) || !ReferenceEquals(FindWindow(pt.X, pt.Y), target)) return;

        var hwnd = target.InputTarget();
        if (hwnd == HWND.Null) return;
        var client = pt;
        PInvoke.ScreenToClient(hwnd, ref client);
        Post(hwnd, PInvoke.WM_MOUSEMOVE, Keys(), MakeLParam(client.X, client.Y));
    }

    /// <summary>Same dispatch as the window-mode branch of <see cref="OnMouse"/>, but through
    /// <see cref="WallpaperWindow.SendMouse"/> instead of <c>PostMessage</c>. All points passed to
    /// SendMouseInput are client coordinates (including for the wheel — unlike WM_MOUSEWHEEL, which
    /// is screen coordinates).</summary>
    private void OnMouseComposition(WallpaperWindow target, System.Drawing.Point screenPt, ushort flags, short data)
    {
        var client = screenPt;
        PInvoke.ScreenToClient(target.Hwnd, ref client);
        var point = new System.Drawing.Point(client.X, client.Y);

        if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0)
        {
            _state.Press(target);
            target.SendMouse(CoreWebView2MouseEventKind.LeftButtonDown, CompKeys(), 0, point);
        }
        else if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0)
        {
            target.SendMouse(CoreWebView2MouseEventKind.LeftButtonUp, CompKeys(exceptLeft: true), 0, point);
            _state.Release();
        }
        else if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
        {
            _state.PressMiddle();
            target.SendMouse(CoreWebView2MouseEventKind.MiddleButtonDown, CompKeys(), 0, point);
        }
        else if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0)
        {
            _state.ReleaseMiddle();
            target.SendMouse(CoreWebView2MouseEventKind.MiddleButtonUp, CompKeys(), 0, point);
        }
        else if ((flags & RI_MOUSE_WHEEL) != 0)
        {
            target.SendMouse(CoreWebView2MouseEventKind.Wheel, CompKeys(), unchecked((uint)(int)data), point);
        }
        else if ((flags & RI_MOUSE_HWHEEL) != 0)
        {
            target.SendMouse(CoreWebView2MouseEventKind.HorizontalWheel, CompKeys(), unchecked((uint)(int)data), point);
        }
        else
        {
            target.SendMouse(CoreWebView2MouseEventKind.Move, CompKeys(), 0, point);
        }
        Forwarded++;
    }

    /// <summary>Sends COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE to the previous hover target exactly once,
    /// when the pointer moves to a different window (or off the desktop). Only composition-hosted
    /// windows need this — a "window"-hosted target has a real HWND and TrackMouseEvent does its own
    /// WM_MOUSELEAVE bookkeeping. Idempotent when called with the same or no target.</summary>
    private void SetHover(WallpaperWindow? target)
    {
        var prev = _state.Hover(target);
        if (prev is { UsesComposition: true, Ready: true })
        {
            prev.SendMouse(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, default);
        }
    }

    private CoreWebView2MouseEventVirtualKeys CompKeys(bool exceptLeft = false)
    {
        var k = CoreWebView2MouseEventVirtualKeys.None;
        if (_state.Left && !exceptLeft) k |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        if (_state.Middle) k |= CoreWebView2MouseEventVirtualKeys.MiddleButton;
        return k;
    }

    /// <summary>Pointer left the desktop, or the captured/hovered window stopped being usable
    /// mid-gesture: resolves via <see cref="PointerState{T}.TargetLost"/> and sends whatever release
    /// (WM_LBUTTONUP/LeftButtonUp) and hover-leave the outgoing state still owed, so neither the page
    /// nor WebView2's own hover tracking gets stuck.</summary>
    private void ReleaseIfNeeded(System.Drawing.Point pt)
    {
        var (up, leave) = _state.TargetLost();
        if (up is not null && up.Ready)
        {
            if (up.UsesComposition)
            {
                var client = pt;
                PInvoke.ScreenToClient(up.Hwnd, ref client);
                up.SendMouse(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, client);
            }
            else
            {
                var hwnd = up.InputTarget();
                var client = pt;
                PInvoke.ScreenToClient(hwnd, ref client);
                Post(hwnd, PInvoke.WM_LBUTTONUP, 0, MakeLParam(client.X, client.Y));
            }
        }
        if (leave is { UsesComposition: true, Ready: true })
        {
            leave.SendMouse(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, default);
        }
    }

    private void Post(HWND hwnd, uint msg, nuint wparam, nint lparam)
    {
        PInvoke.PostMessage(hwnd, msg, (WPARAM)wparam, (LPARAM)lparam);
        Forwarded++;
    }

    private nuint Keys(bool exceptLeft = false)
    {
        nuint k = 0;
        if (_state.Left && !exceptLeft) k |= (nuint)MODIFIERKEYS_FLAGS.MK_LBUTTON;
        if (_state.Middle) k |= (nuint)MODIFIERKEYS_FLAGS.MK_MBUTTON;
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

    /// <summary>
    /// Drops all capture/hover state without synthesizing any message — for callers that already
    /// know the target windows are gone or about to stop receiving input: <see
    /// cref="WallpaperEngine.ReattachAsync"/> calls this right after disposing/clearing the old
    /// window list (so a stale <c>_captured</c>/<c>_hoverWindow</c> from before the reattach never
    /// gets posted/sent to), and <see cref="WallpaperEngine.SetUserPause"/> calls this when pausing
    /// (so a press held down at the moment of pausing does not stay "captured" against a window that
    /// will ignore further input until resumed).
    /// </summary>
    public void Reset() => _state.Reset();

    public void Dispose()
    {
        try { _hoverKeepAlive.Stop(); } catch { }
        try { _sink?.Dispose(); } catch { }
        _sink = null;
    }
}
