using System.IO;
using System.Text.Json;
using System.Windows;
using NNA.Wallpaper.Engine;
using NNA.Wallpaper.Host;

namespace NNA.Wallpaper.Themes;

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string State { get; set; } = "Normal"; // Normal | Maximized
}

/// <summary>
/// Remembers a window's size/position across restarts in <c>data/window-settings.json</c> — not
/// <c>app.json</c>, which is the owner-editable config file touched by unrelated code (ConfigStore).
/// Best-effort throughout: any I/O or parse failure is swallowed and treated as "nothing saved yet",
/// since a lost window position is never worth crashing a window over.
/// </summary>
public static class WindowSettingsStore
{
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath(HostContext ctx) => Path.Combine(ctx.Paths.DataRoot, "window-settings.json");

    /// <summary>
    /// Restores <paramref name="window"/>'s bounds under <paramref name="key"/> if a saved rect exists
    /// and its center still lands on a currently connected monitor; otherwise leaves the window's own
    /// XAML-declared size/startup location untouched. Call before <c>Show()</c>.
    /// </summary>
    public static void Restore(Window window, HostContext ctx, string key)
    {
        var saved = Load(ctx, key);
        if (saved is null || saved.Width <= 0 || saved.Height <= 0) return;

        var centerX = (int)(saved.Left + saved.Width / 2);
        var centerY = (int)(saved.Top + saved.Height / 2);
        var onScreen = DisplayMonitors.Enumerate().Any(m => m.Contains(centerX, centerY));
        if (!onScreen) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = saved.Left;
        window.Top = saved.Top;
        window.Width = saved.Width;
        window.Height = saved.Height;
        if (saved.State == "Maximized") window.WindowState = WindowState.Maximized;
    }

    /// <summary>Saves current bounds under <paramref name="key"/> when the window closes.</summary>
    public static void SaveOnClose(Window window, HostContext ctx, string key)
    {
        window.Closing += (_, _) =>
        {
            var maximized = window.WindowState == WindowState.Maximized;
            // RestoreBounds tracks the pre-maximize rect even while maximized; that is what we want
            // to persist so a restart restores a sane, on-screen "Normal" size.
            var bounds = new WindowBounds
            {
                Left = maximized ? window.RestoreBounds.Left : window.Left,
                Top = maximized ? window.RestoreBounds.Top : window.Top,
                Width = maximized ? window.RestoreBounds.Width : window.Width,
                Height = maximized ? window.RestoreBounds.Height : window.Height,
                State = maximized ? "Maximized" : "Normal",
            };
            if (bounds.Width > 0 && bounds.Height > 0) Save(ctx, key, bounds);
        };
    }

    private static WindowBounds? Load(HostContext ctx, string key)
    {
        try
        {
            lock (Lock)
            {
                var path = FilePath(ctx);
                if (!File.Exists(path)) return null;
                var root = JsonSerializer.Deserialize<Dictionary<string, WindowBounds>>(File.ReadAllText(path));
                return root is not null && root.TryGetValue(key, out var b) ? b : null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void Save(HostContext ctx, string key, WindowBounds bounds)
    {
        try
        {
            lock (Lock)
            {
                var path = FilePath(ctx);
                Dictionary<string, WindowBounds> root;
                try
                {
                    root = File.Exists(path)
                        ? JsonSerializer.Deserialize<Dictionary<string, WindowBounds>>(File.ReadAllText(path)) ?? new()
                        : new();
                }
                catch
                {
                    root = new();
                }
                root[key] = bounds;
                Directory.CreateDirectory(ctx.Paths.DataRoot);
                File.WriteAllText(path, JsonSerializer.Serialize(root, Options));
            }
        }
        catch
        {
            // best-effort, see class remarks
        }
    }
}
