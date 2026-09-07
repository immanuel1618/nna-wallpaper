using Microsoft.Web.WebView2.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.TopBar;

/// <summary>
/// Shared plumbing for hosting WebView2 through <see cref="Engine.CompositionHost"/> (DirectComposition)
/// directly on a top-level, interactive WPF window's own HWND — used by <see cref="TopBarWindow"/> and
/// <see cref="Dock.DockWindow"/> instead of the WPF <c>Microsoft.Web.WebView2.Wpf.WebView2</c> control.
///
/// Why: the WPF WebView2 control hosts Chromium in "windowed" mode, which creates its own
/// <c>Chrome_WidgetWin_1</c> child HWND to receive mouse/keyboard input. Even though the owner window
/// carries <c>WS_EX_NOACTIVATE</c>, that only vetoes the *default* click-to-activate path
/// (<c>WM_MOUSEACTIVATE</c>); Chromium's own input handling calls <c>SetFocus</c> on its child HWND on
/// pointer-down (for IME/accessibility), and giving a child window keyboard focus forces its top-level
/// owner active regardless of the owner's own ex-style — that is the observed "clicking the bar steals
/// focus from the foreground app" bug. Composition hosting has no such child HWND: WebView2 renders into
/// a DirectComposition visual and receives input exclusively through
/// <c>ICoreWebView2CompositionController::SendMouseInput</c>, so there is nothing left that can call
/// <c>SetFocus</c> on our behalf. This class is the piece that feeds that API from the window's own
/// <c>WM_MOUSE*</c> messages (which arrive at the top-level HWND directly once there is no child HWND
/// intercepting them first), plus <c>WM_SETCURSOR</c> (from
/// <see cref="CoreWebView2CompositionController.CursorChanged"/>) and an explicit
/// <c>WM_MOUSEACTIVATE</c> -&gt; <c>MA_NOACTIVATE</c> veto as belt-and-braces alongside the window's own
/// <c>WS_EX_NOACTIVATE</c> style.
///
/// Keyboard is intentionally not forwarded — neither the bar nor the dock take text/keyboard input
/// (see docs/TOPBAR.md "хостинг и фокус"); the one control that does (<see cref="PopupWindow"/>) stays
/// in ordinary windowed hosting, where it can hold real focus.
/// </summary>
public sealed class CompositionInput
{
    private const nint MA_NOACTIVATE = 3;

    private readonly HWND _hwnd;
    private readonly CoreWebView2CompositionController _comp;
    private HCURSOR _cursor;
    private bool _tracking;

    public CompositionInput(HWND hwnd, CoreWebView2CompositionController comp)
    {
        _hwnd = hwnd;
        _comp = comp;
        _cursor = (HCURSOR)comp.Cursor;
        comp.CursorChanged += (_, _) => _cursor = (HCURSOR)_comp.Cursor;
    }

    /// <summary>Call from the window's own <c>HwndSource</c> hook. Returns true when the message was
    /// handled here (caller should set <c>handled = true</c> and use <paramref name="result"/> as the
    /// WndProc return value).</summary>
    public unsafe bool Handle(int msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch ((uint)msg)
        {
            case PInvoke.WM_MOUSEACTIVATE:
                result = MA_NOACTIVATE;
                return true;

            case PInvoke.WM_SETCURSOR:
                PInvoke.SetCursor(_cursor);
                result = 1;
                return true;

            case PInvoke.WM_MOUSEMOVE:
                EnsureTracking();
                _comp.SendMouseInput(CoreWebView2MouseEventKind.Move, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;

            case PInvoke.WM_MOUSELEAVE:
                _tracking = false;
                _comp.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, default);
                return true;

            case PInvoke.WM_LBUTTONDOWN:
                _comp.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonDown, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;
            case PInvoke.WM_LBUTTONUP:
                _comp.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonUp, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;

            case PInvoke.WM_MBUTTONDOWN:
                _comp.SendMouseInput(CoreWebView2MouseEventKind.MiddleButtonDown, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;
            case PInvoke.WM_MBUTTONUP:
                _comp.SendMouseInput(CoreWebView2MouseEventKind.MiddleButtonUp, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;

            case PInvoke.WM_MOUSEWHEEL:
            {
                // WM_MOUSEWHEEL carries SCREEN coordinates (unlike every other WM_MOUSE* message);
                // SendMouseInput always wants client coordinates, wheel included.
                var screen = new System.Drawing.Point(Signed16(lParam, 0), Signed16(lParam, 16));
                var client = screen;
                PInvoke.ScreenToClient(_hwnd, ref client);
                var delta = unchecked((uint)(int)Signed16(wParam, 16));
                _comp.SendMouseInput(CoreWebView2MouseEventKind.Wheel, KeysFromWParam(wParam & 0xFFFF), delta, client);
                return true;
            }

            default:
                return false;
        }
    }

    private unsafe void EnsureTracking()
    {
        if (_tracking) return;
        _tracking = true;
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = (uint)sizeof(TRACKMOUSEEVENT),
            dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
            hwndTrack = _hwnd,
            dwHoverTime = 0,
        };
        PInvoke.TrackMouseEvent(ref tme);
    }

    private static System.Drawing.Point ClientPoint(nint lParam) => new(Signed16(lParam, 0), Signed16(lParam, 16));

    private static short Signed16(nint value, int shift) => unchecked((short)((value >> shift) & 0xFFFF));

    private static CoreWebView2MouseEventVirtualKeys KeysFromWParam(nint wParam)
    {
        var flags = (MODIFIERKEYS_FLAGS)(nuint)wParam;
        var keys = CoreWebView2MouseEventVirtualKeys.None;
        if ((flags & MODIFIERKEYS_FLAGS.MK_LBUTTON) != 0) keys |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        if ((flags & MODIFIERKEYS_FLAGS.MK_RBUTTON) != 0) keys |= CoreWebView2MouseEventVirtualKeys.RightButton;
        if ((flags & MODIFIERKEYS_FLAGS.MK_MBUTTON) != 0) keys |= CoreWebView2MouseEventVirtualKeys.MiddleButton;
        if ((flags & MODIFIERKEYS_FLAGS.MK_SHIFT) != 0) keys |= CoreWebView2MouseEventVirtualKeys.Shift;
        if ((flags & MODIFIERKEYS_FLAGS.MK_CONTROL) != 0) keys |= CoreWebView2MouseEventVirtualKeys.Control;
        return keys;
    }
}
