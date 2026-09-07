using System.IO;
using Microsoft.Win32;

namespace NNA.Wallpaper;

/// <summary>
/// Manages the per-user "launch at sign-in" registration (HKCU Run key). Points at the Velopack
/// update stub when the app is installed, so the Run entry keeps working across updates (the stub
/// lives next to the "current" version folder and never moves); falls back to the running
/// executable's own path otherwise (dev runs, portable use).
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NNA Wallpaper";

    /// <summary>Adds or removes the Run key entry to match <paramref name="enabled"/>.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                key.SetValue(ValueName, ExePath(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // registry access can fail under odd security policies; autostart is best-effort
        }
    }

    /// <summary>Whether the Run key currently has our entry (regardless of what path it points to).</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The path the Run key should point at: the Velopack stub (<c>&lt;install dir&gt;\NNA.Wallpaper.exe</c>,
    /// one level above the versioned "current" folder, next to "Update.exe") when installed, else the
    /// currently running executable.
    /// </summary>
    public static string ExePath()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            if (dir.Name.Equals("current", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null
                && File.Exists(Path.Combine(dir.Parent.FullName, "Update.exe")))
            {
                return "\"" + Path.Combine(dir.Parent.FullName, "NNA.Wallpaper.exe") + "\"";
            }
        }
        catch
        {
            // fall through to the process path
        }
        var processPath = Environment.ProcessPath;
        return "\"" + (string.IsNullOrEmpty(processPath) ? Path.Combine(AppContext.BaseDirectory, "NNA.Wallpaper.exe") : processPath) + "\"";
    }
}
