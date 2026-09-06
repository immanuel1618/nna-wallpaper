namespace NNA.Wallpaper.Host;

/// <summary>
/// Where things live on disk. Data directory defaults to %LOCALAPPDATA%\NNA Wallpaper and can be
/// overridden with --data (portable mode). Web assets ship next to the executable.
/// </summary>
public sealed class Paths
{
    public string DataDir { get; }
    public string InstallDir { get; }

    public string ConfigDir => Path.Combine(DataDir, "config");
    public string WidgetSettingsDir => Path.Combine(ConfigDir, "widgets");
    public string AppConfigFile => Path.Combine(ConfigDir, "app.json");
    public string MonitorsConfigFile => Path.Combine(ConfigDir, "monitors.json");
    public string LaunchFile => Path.Combine(ConfigDir, "launch.json");
    public string EventsFile => Path.Combine(ConfigDir, "events.json");
    public string DataRoot => Path.Combine(DataDir, "data");
    public string IconsDir => Path.Combine(DataRoot, "icons");
    public string PinsDir => Path.Combine(DataRoot, "pins");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string LogFile => Path.Combine(LogsDir, "app.log");
    public string UserWidgetsDir => Path.Combine(DataDir, "widgets");
    public string PlannerSessionFile => Path.Combine(DataDir, "planner-session.json");
    public string WebView2UserDataDir => Path.Combine(DataDir, "WebView2");

    /// <summary>Built-in web roots shipped with the app: wallpaper, widgets, settings.</summary>
    public string WebRoot(string name) => Path.Combine(InstallDir, name);
    public string BuiltInWidgetsDir => WebRoot("widgets");

    public Paths(string dataDir, string installDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        InstallDir = Path.GetFullPath(installDir);
    }

    public static Paths Resolve(string? dataDirOverride)
    {
        var install = AppContext.BaseDirectory;
        var data = string.IsNullOrWhiteSpace(dataDirOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NNA Wallpaper")
            : dataDirOverride;
        return new Paths(data, install);
    }

    public void EnsureDirectories()
    {
        foreach (var d in new[] { DataDir, ConfigDir, WidgetSettingsDir, DataRoot, IconsDir, LogsDir, UserWidgetsDir })
        {
            Directory.CreateDirectory(d);
        }
    }
}
