using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;

namespace NNA.Wallpaper;

/// <summary>
/// Small top-level text input used by the planner block's "+ ДОБАВИТЬ" field (and any other
/// keyboard-only capture flow): keyboard focus cannot be delivered reliably into a wallpaper-layer
/// WebView2 sitting behind desktop icons, so the host pops this window near the clicked point instead.
/// </summary>
public partial class InputWindow : Window
{
    private readonly HostContext _ctx;
    private readonly int _screenX;
    private readonly int _screenY;
    private readonly Action<string> _onSubmit;
    private bool _placeholderShown;
    private bool _closing;

    private InputWindow(HostContext ctx, int screenX, int screenY, string? placeholder, Action<string> onSubmit)
    {
        _ctx = ctx;
        _screenX = screenX;
        _screenY = screenY;
        _onSubmit = onSubmit;
        InitializeComponent();

        if (!string.IsNullOrEmpty(placeholder))
        {
            Input.Text = placeholder;
            Input.Foreground = Brushes.Gray;
            _placeholderShown = true;
        }

        Input.GotFocus += OnInputGotFocus;
        Input.PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => SafeClose();
        Closing += (_, _) => _closing = true;
        ContentRendered += (_, _) => Reposition();
        Loaded += (_, _) => Input.Focus();
    }

    private void OnInputGotFocus(object sender, RoutedEventArgs e)
    {
        if (!_placeholderShown) return;
        _placeholderShown = false;
        Input.Text = "";
        Input.Foreground = Brushes.White;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Submit(); }
        else if (e.Key == Key.Escape) { e.Handled = true; SafeClose(); }
    }

    private void Submit()
    {
        if (_closing) return;
        var text = _placeholderShown ? "" : Input.Text.Trim();
        SafeClose();
        if (text.Length > 0) _onSubmit(text);
    }

    /// <summary>Close once: Deactivated fires again while the window is already closing, and WPF throws on a second Close().</summary>
    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); }
        catch (InvalidOperationException ex) { _ctx.Log.Warn("input window close: " + ex.Message); }
    }

    /// <summary>
    /// Screen coordinates arrive in physical pixels; WPF Left/Top are DIUs — convert using this
    /// window's own DPI (available only once it has a PresentationSource, i.e. after ContentRendered),
    /// then clamp to the monitor that contains the point so the window never lands off-screen.
    /// </summary>
    private void Reposition()
    {
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (scale <= 0) scale = 1.0;

        var w = ActualWidth * scale;
        var h = ActualHeight * scale;

        var mon = _ctx.App.Monitors.FirstOrDefault(m =>
            _screenX >= m.x && _screenX < m.x + m.width && _screenY >= m.y && _screenY < m.y + m.height)
            ?? _ctx.App.Monitors.FirstOrDefault();

        double left = mon?.x ?? 0;
        double top = mon?.y ?? 0;
        double right = left + (mon?.width ?? (int)(w + _screenX));
        double bottom = top + (mon?.height ?? (int)(h + _screenY));

        var px = _screenX - w / 2;
        var py = _screenY - h / 2;
        px = Math.Max(left, Math.Min(px, right - w));
        py = Math.Max(top, Math.Min(py, bottom - h));

        Left = px / scale;
        Top = py / scale;
    }

    /// <summary>Wires the engine's InputRequested event so the host can show this window on demand.</summary>
    public static void Attach(WallpaperEngine engine, HostContext ctx)
    {
        engine.InputRequested += (_, screenX, screenY, placeholder, onSubmit) =>
        {
            try
            {
                var win = new InputWindow(ctx, screenX, screenY, placeholder, onSubmit);
                win.Show();
            }
            catch (Exception ex)
            {
                ctx.Log.Error("input window", ex);
            }
        };
    }
}
