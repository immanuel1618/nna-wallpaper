using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NNA.Wallpaper.Themes;

/// <summary>
/// Attached properties consumed by the shared "BrandWindow" style in <c>Chrome.xaml</c>. A window
/// opts into the status strip (version + health dot, used by SettingsWindow only) by setting
/// <see cref="ShowStatusProperty"/>; <see cref="VersionProperty"/> and <see cref="HealthOkProperty"/>
/// feed its content. Min/maximize buttons are shown or hidden by the style itself, based on the
/// window's own <c>ResizeMode</c> (NoResize = close-only, used by PlannerLoginWindow).
/// </summary>
public static class Chrome
{
    public static readonly DependencyProperty ShowStatusProperty = DependencyProperty.RegisterAttached(
        "ShowStatus", typeof(bool), typeof(Chrome), new PropertyMetadata(false));
    public static void SetShowStatus(DependencyObject d, bool value) => d.SetValue(ShowStatusProperty, value);
    public static bool GetShowStatus(DependencyObject d) => (bool)d.GetValue(ShowStatusProperty);

    public static readonly DependencyProperty VersionProperty = DependencyProperty.RegisterAttached(
        "Version", typeof(string), typeof(Chrome), new PropertyMetadata(""));
    public static void SetVersion(DependencyObject d, string value) => d.SetValue(VersionProperty, value);
    public static string GetVersion(DependencyObject d) => (string)d.GetValue(VersionProperty);

    public static readonly DependencyProperty HealthOkProperty = DependencyProperty.RegisterAttached(
        "HealthOk", typeof(bool), typeof(Chrome), new PropertyMetadata(true));
    public static void SetHealthOk(DependencyObject d, bool value) => d.SetValue(HealthOkProperty, value);
    public static bool GetHealthOk(DependencyObject d) => (bool)d.GetValue(HealthOkProperty);

    /// <summary>
    /// Manual hover flag for the maximize button: once <see cref="BrandChrome"/> reports that pixel
    /// as HTMAXBUTTON to Windows (so Snap Layouts can hook it), mouse-over there stops arriving as
    /// ordinary WM_MOUSEMOVE/IsMouseOver and comes in as WM_NCMOUSEMOVE/WM_NCMOUSELEAVE instead —
    /// this property lets the button's own hover visuals keep working from that non-client feed.
    /// </summary>
    public static readonly DependencyProperty NcHoverProperty = DependencyProperty.RegisterAttached(
        "NcHover", typeof(bool), typeof(Chrome), new PropertyMetadata(false));
    public static void SetNcHover(DependencyObject d, bool value) => d.SetValue(NcHoverProperty, value);
    public static bool GetNcHover(DependencyObject d) => (bool)d.GetValue(NcHoverProperty);
}

/// <summary>
/// Spreads a title string with thin spaces to approximate the brand's 0.14em letter-tracking —
/// WPF's TextBlock has no built-in letter-spacing property.
/// </summary>
public sealed class TrackingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        if (s.Length <= 1) return s;
        return string.Join(" ", s.ToCharArray());
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
