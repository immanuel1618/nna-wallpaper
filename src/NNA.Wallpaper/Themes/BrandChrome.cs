using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace NNA.Wallpaper.Themes;

/// <summary>
/// Wires a Window using the "BrandWindow" style (<c>Chrome.xaml</c>) up to the native bits XAML alone
/// cannot do: dark-mode DWM caption/border colors, and Windows 11 Snap Layouts on the custom maximize
/// button. Call <see cref="Attach"/> once, right after <c>InitializeComponent()</c>, before <c>Show()</c>.
/// </summary>
public static class BrandChrome
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int HTMAXBUTTON = 9;

    public static void Attach(Window window)
    {
        Button? btnMin = null, btnMax = null, btnClose = null;
        UIElement? maximizeGlyph = null, restoreGlyph = null;

        window.SourceInitialized += (_, _) =>
        {
            var hwnd = (HWND)new WindowInteropHelper(window).Handle;
            ApplyDarkTitleBar(hwnd);

            // Our hook must be added to the HwndSource *before* WindowChrome's own hook, or
            // WindowChrome's WM_NCHITTEST handling (which marks the message handled for anything
            // under IsHitTestVisibleInChrome, i.e. our own title bar buttons) wins and we never see
            // the message. WindowChrome is therefore applied here, from code, right after our hook,
            // rather than via a Style Setter (which would run earlier, during template application).
            HwndSource.FromHwnd((nint)hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                WndProc(window, () => btnMax, msg, wParam, lParam, ref handled));

            if (window.TryFindResource("BrandWindowChrome") is WindowChrome chrome)
                WindowChrome.SetWindowChrome(window, chrome);
        };

        window.Loaded += (_, _) =>
        {
            window.ApplyTemplate();
            btnMin = window.Template?.FindName("PART_Minimize", window) as Button;
            btnMax = window.Template?.FindName("PART_Maximize", window) as Button;
            btnClose = window.Template?.FindName("PART_Close", window) as Button;
            maximizeGlyph = window.Template?.FindName("PART_MaximizeGlyph", window) as UIElement;
            restoreGlyph = window.Template?.FindName("PART_RestoreGlyph", window) as UIElement;

            if (btnMin is not null) btnMin.Click += (_, _) => SystemCommands.MinimizeWindow(window);
            if (btnMax is not null)
            {
                btnMax.Click += (_, _) => ToggleMaximize(window);
            }
            if (btnClose is not null) btnClose.Click += (_, _) => SystemCommands.CloseWindow(window);

            void SyncGlyph()
            {
                var maximized = window.WindowState == WindowState.Maximized;
                if (maximizeGlyph is not null) maximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
                if (restoreGlyph is not null) restoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
            }
            SyncGlyph();
            window.StateChanged += (_, _) => SyncGlyph();
        };
    }

    private static void ToggleMaximize(Window window)
    {
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }

    /// <summary>
    /// Reports the maximize button's own screen rectangle as HTMAXBUTTON so Windows 11 draws its
    /// native hover highlight and Snap Layouts flyout there (see Microsoft's "Support snap layouts
    /// for desktop apps on Windows 11"), and performs the actual maximize/restore toggle from the
    /// resulting WM_NCLBUTTONUP — that click never reaches the WPF Button because the point is
    /// reported as non-client. The ordinary Button.Click handler (wired in <see cref="Attach"/>)
    /// stays as a plain-click fallback for any pixel this hook doesn't cover.
    /// </summary>
    private static IntPtr WndProc(Window window, Func<Button?> maxButton, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var btn = maxButton();
        if (btn is null || !btn.IsVisible) return IntPtr.Zero;

        if (msg == WM_NCHITTEST)
        {
            if (HitTestButton(btn, lParam))
            {
                handled = true;
                Chrome.SetNcHover(btn, true);
                return (IntPtr)HTMAXBUTTON;
            }
            Chrome.SetNcHover(btn, false);
            return IntPtr.Zero;
        }

        if (msg == WM_NCMOUSEMOVE && wParam.ToInt32() == HTMAXBUTTON)
        {
            Chrome.SetNcHover(btn, true);
            return IntPtr.Zero;
        }

        if (msg == WM_NCMOUSELEAVE)
        {
            Chrome.SetNcHover(btn, false);
            return IntPtr.Zero;
        }

        if ((msg == WM_NCLBUTTONDOWN || msg == WM_NCLBUTTONUP) && wParam.ToInt32() == HTMAXBUTTON)
        {
            handled = true;
            if (msg == WM_NCLBUTTONUP) ToggleMaximize(window);
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private static bool HitTestButton(FrameworkElement el, IntPtr lParam)
    {
        try
        {
            int x = unchecked((short)(long)lParam);
            int y = unchecked((short)((long)lParam >> 16));
            var topLeft = el.PointToScreen(new Point(0, 0));
            var bottomRight = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE (20) + DWMWA_CAPTION_COLOR (35) + DWMWA_BORDER_COLOR (34).
    /// Deliberately leaves DWMWA_WINDOW_CORNER_PREFERENCE alone: Windows 11 rounds top-level window
    /// corners on its own once the frame is custom-drawn, and we must not fight that.</summary>
    private static unsafe void ApplyDarkTitleBar(HWND hwnd)
    {
        try
        {
            int darkMode = 1;
            PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &darkMode, (uint)sizeof(int));

            uint caption = ToColorRef(0x0B, 0x0B, 0x0B); // Base
            PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CAPTION_COLOR, &caption, (uint)sizeof(uint));

            uint border = ToColorRef(0x43, 0x43, 0x43); // Slate
            PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &border, (uint)sizeof(uint));
        }
        catch
        {
            // Best-effort: pre-Win11-22H2 systems don't support caption/border color and throw here;
            // dark mode alone (or nothing) is an acceptable fallback rather than crashing the window.
        }
    }

    /// <summary>COLORREF is 0x00BBGGRR, not RGB.</summary>
    private static uint ToColorRef(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
