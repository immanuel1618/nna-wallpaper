using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DirectComposition;

namespace NNA.Wallpaper.Engine;

/// <summary>
/// One DirectComposition target + root visual for one wallpaper HWND, used to host WebView2 through
/// <c>CoreWebView2CompositionController.RootVisualTarget</c> instead of letting WebView2 own its own
/// input-receiving child HWND.
///
/// Why: in the classic (non-composition) hosting mode a <c>CoreWebView2Controller</c> creates its own
/// <c>Chrome_WidgetWin_1</c> child window and that window is the one Chromium tracks with
/// <c>TrackMouseEvent(TME_LEAVE)</c> after every mouse-move it receives. Our wallpaper windows sit
/// behind the desktop icon layer (<see cref="WallpaperWindow"/>), so real mouse messages never reach
/// them — <see cref="InputBridge"/> re-posts Raw Input mouse messages into that Chromium child by
/// hand. But the moment a *real* mouse move happens anywhere on the desktop, Windows resolves
/// "window under the cursor" against the actual (unclipped, top-most) window at that screen point,
/// which is <c>SysListView32</c> (the icon list), not the tracked Chromium window — so Windows fires
/// <c>WM_MOUSELEAVE</c> at Chromium right after our synthetic <c>WM_MOUSEMOVE</c> made it call
/// <c>TrackMouseEvent</c>. Hover state flips on and off on every real cursor motion: the flicker the
/// owner reported. Composition hosting has no such child HWND for input — WebView2 renders into a
/// DirectComposition visual and every pointer event arrives only through
/// <c>ICoreWebView2CompositionController::SendMouseInput</c>, so there is nothing for
/// <c>TrackMouseEvent</c>/<c>WM_MOUSELEAVE</c> to race with.
///
/// One <see cref="IDCompositionDevice"/> per process (created lazily, thread-affine to the WPF UI
/// thread like everything else in Engine/); one target + one visual per <see cref="WallpaperWindow"/>.
/// Modelled on Microsoft's WebView2APISample (<c>CompositionHost.cpp</c>) and the
/// "Render WebView2 with DirectComposition" documentation: <c>DCompositionCreateDevice2(nullptr, ...)</c>
/// -&gt; <c>CreateTargetForHwnd</c> -&gt; <c>CreateVisual</c> -&gt; <c>target.SetRoot(visual)</c> -&gt;
/// <c>device.Commit()</c>; the visual is then handed to
/// <c>CoreWebView2CompositionController.RootVisualTarget</c>.
/// </summary>
public sealed class CompositionHost : IDisposable
{
    private static IDCompositionDevice? s_device;

    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private bool _disposed;

    /// <summary>The visual to hand to <c>CoreWebView2CompositionController.RootVisualTarget</c>.</summary>
    public object RootVisual => _visual!;

    /// <summary>
    /// Commits the shared DirectComposition device. Per WebView2.idl on
    /// <c>RootVisualTarget</c>: "WebView will connect its visual tree to the provided visual before
    /// returning from the property setter. The app needs to commit on its device [after] setting the
    /// RootVisualTarget property." <see cref="WallpaperWindow"/> calls this right after assigning
    /// <c>RootVisualTarget = host.RootVisual</c> — skipping it leaves the window blank (WebView2 never
    /// paints anything, confirmed with <c>tests/CompositionProbe</c>): the visual-tree attach WebView2
    /// makes into our tree is not itself flushed to DWM until our device commits.
    /// </summary>
    public void Commit() => s_device?.Commit();

    private CompositionHost(IDCompositionTarget target, IDCompositionVisual visual)
    {
        _target = target;
        _visual = visual;
    }

    /// <summary>
    /// Creates a DirectComposition target and root visual for <paramref name="hwnd"/> and commits the
    /// device. Throws on failure (HRESULT via <c>ThrowOnFailure</c>) — the caller falls back to
    /// classic window hosting when this does not work on the current machine/driver.
    /// </summary>
    public static CompositionHost Create(HWND hwnd)
    {
        var device = GetOrCreateDevice();
        device.CreateTargetForHwnd(hwnd, true, out var target);
        device.CreateVisual(out var visual);
        target.SetRoot(visual);
        device.Commit();
        return new CompositionHost(target, visual);
    }

    /// <summary>
    /// Variant A from the task: let DirectComposition pick its own rendering device
    /// (<c>DCompositionCreateDevice2(nullptr, ...)</c>). If this ever needs to fall back to variant B
    /// (build a D3D11 BGRA device -&gt; IDXGIDevice -&gt; DCompositionCreateDevice) that goes here.
    /// </summary>
    private static IDCompositionDevice GetOrCreateDevice()
    {
        if (s_device is not null) return s_device;
        var hr = PInvoke.DCompositionCreateDevice2(null, out IDCompositionDevice device);
        hr.ThrowOnFailure();
        s_device = device;
        return device;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _target?.SetRoot(null); } catch { }
        try { s_device?.Commit(); } catch { }
        if (_target is not null) { try { Marshal.ReleaseComObject(_target); } catch { } }
        if (_visual is not null) { try { Marshal.ReleaseComObject(_visual); } catch { } }
        _target = null;
        _visual = null;
    }
}
