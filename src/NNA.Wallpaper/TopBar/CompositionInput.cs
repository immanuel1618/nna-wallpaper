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
    /// <summary>Left/middle button currently down, per <c>SetCapture</c> — needed so a
    /// <c>WM_CAPTURECHANGED</c> (some other window/menu stole capture mid-drag) can synthesize the
    /// matching Up into WebView2 instead of leaving it thinking the button is still held.</summary>
    private bool _leftDown, _middleDown;

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
                // During a captured drag the pointer legitimately leaves the window; a Leave here
                // would kill the gesture inside the page. The button-up ends it instead.
                if (_leftDown || _middleDown) return true;
                _comp.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, default);
                return true;

            case PInvoke.WM_LBUTTONDOWN:
                // SetCapture so drag gestures (sliders, drag-to-reorder in the dock) keep receiving
                // WM_MOUSEMOVE/up even if the pointer leaves this HWND mid-drag — without it, the
                // page's own pointer capture (setPointerCapture) has no real HWND-level backing here
                // the way it would in windowed WebView2 hosting.
                PInvoke.SetCapture(_hwnd);
                _leftDown = true;
                _comp.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonDown, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;
            case PInvoke.WM_LBUTTONUP:
                _leftDown = false;
                if (!_middleDown) PInvoke.ReleaseCapture();
                _comp.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonUp, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;

            case PInvoke.WM_MBUTTONDOWN:
                PInvoke.SetCapture(_hwnd);
                _middleDown = true;
                _comp.SendMouseInput(CoreWebView2MouseEventKind.MiddleButtonDown, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;
            case PInvoke.WM_MBUTTONUP:
                _middleDown = false;
                if (!_leftDown) PInvoke.ReleaseCapture();
                _comp.SendMouseInput(CoreWebView2MouseEventKind.MiddleButtonUp, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;

            // Right button is forwarded too (unlike InputBridge's desktop-icon raw-input path, which
            // deliberately drops it so the real desktop context menu keeps working) — dock/topbar
            // pages need a real "contextmenu" DOM event for their own right-click menus, which only
            // fires in Chromium off a right button up it actually received.
            case PInvoke.WM_RBUTTONDOWN:
                _comp.SendMouseInput(CoreWebView2MouseEventKind.RightButtonDown, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;
            case PInvoke.WM_RBUTTONUP:
                _comp.SendMouseInput(CoreWebView2MouseEventKind.RightButtonUp, KeysFromWParam(wParam), 0, ClientPoint(lParam));
                return true;

            case PInvoke.WM_CAPTURECHANGED:
                // Something else (a system menu, another window) took capture out from under us —
                // WebView2 must not keep thinking a button is held, or hover/drag state inside the
                // page gets stuck until the next real click.
                if (_leftDown)
                {
                    _leftDown = false;
                    _comp.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, default);
                }
                if (_middleDown)
                {
                    _middleDown = false;
                    _comp.SendMouseInput(CoreWebView2MouseEventKind.MiddleButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, default);
                }
                return false; // do not mark handled: WM_CAPTURECHANGED must reach DefWindowProc too

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
