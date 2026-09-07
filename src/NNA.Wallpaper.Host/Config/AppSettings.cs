using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Config;

/// <summary>app.json — general settings (see docs/SETTINGS.md).</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public int ApiPort { get; set; } = 1618;
    public string ApiToken { get; set; } = "";
    public string Language { get; set; } = "ru";
    public bool Autostart { get; set; } = false;
    public UpdateSettings Updates { get; set; } = new();
    public bool PauseOnFullscreen { get; set; } = true;
    public int FpsCap { get; set; } = 30;
    /// <summary>When true, config changes are patched live on the wallpaper pages over /events instead
    /// of triggering a full <c>ReloadWallpaper()</c> (see App.xaml.cs OnConfigChanged).</summary>
    public bool LiveUpdates { get; set; } = true;
    public ThemeSettings Theme { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();

    /// <summary>Drive root (e.g. "H:") -> editor command line used by /open.</summary>
    public Dictionary<string, string[]> OpenWith { get; set; } = new();
    /// <summary>Roots that /open may touch. Empty on a clean install.</summary>
    public List<string> OpenRoots { get; set; } = new();
    /// <summary>Graph id (e.g. "h") -> path to graphify graph.json.</summary>
    public Dictionary<string, string> Graphs { get; set; } = new();
    public GraphLimits GraphLimits { get; set; } = new();
    public WeatherSettings Weather { get; set; } = new();
    public PlannerSettings Planner { get; set; } = new();
    public TaskbarSettings Taskbar { get; set; } = new();
    public TopBarSettings TopBar { get; set; } = new();
}

/// <summary>One visual style: mode normal|clear|blur|acrylic|opaque, tint colour and opacity 0..1.</summary>
public sealed class SurfaceStyle
{
    public string Mode { get; set; } = "normal";
    public string Color { get; set; } = "#0B0B0B";
    public double Opacity { get; set; } = 0.5;
}

/// <summary>Windows taskbar toggles; null = leave the Windows setting untouched.</summary>
public sealed class TaskbarWindowsSettings
{
    public bool? Centered { get; set; }
    public bool? HideSearch { get; set; }
    public bool? HideTaskView { get; set; }
    public bool? HideWidgets { get; set; }
    public bool? HideClock { get; set; }
    public bool? Small { get; set; }
    public bool? Transparency { get; set; }
    public bool? OledTransparency { get; set; }
    public bool? AutoHide { get; set; }
}

/// <summary>Taskbar styling (Windows taskbar): per-state styles plus Windows toggles.</summary>
public sealed class TaskbarSettings
{
    public bool Enabled { get; set; } = false;
    public string Preset { get; set; } = "windows";
    public SurfaceStyle Normal { get; set; } = new();
    public SurfaceStyle Maximized { get; set; } = new() { Mode = "opaque", Opacity = 1 };
    public SurfaceStyle Fullscreen { get; set; } = new();
    public TaskbarWindowsSettings Windows { get; set; } = new();
    public bool Secondary { get; set; } = true;
}

public sealed class TopBarModule
{
    public string Id { get; set; } = "";
    public string Side { get; set; } = "right";
}

/// <summary>Our own always-on-top bar at the top edge (mac-like menu bar).</summary>
public sealed class TopBarSettings
{
    public bool Enabled { get; set; } = false;
    public string Monitors { get; set; } = "all";
    public int Height { get; set; } = 30;
    public SurfaceStyle Style { get; set; } = new() { Mode = "acrylic", Opacity = 0.6 };
    public int FontSize { get; set; } = 12;
    public bool AutoHide { get; set; } = false;
    public bool ReserveSpace { get; set; } = true;
    public List<TopBarModule> Modules { get; set; } = new()
    {
        new() { Id = "brand", Side = "left" }, new() { Id = "date", Side = "left" },
        new() { Id = "clock", Side = "center" },
        new() { Id = "planner", Side = "right" }, new() { Id = "media", Side = "right" },
        new() { Id = "weather", Side = "right" }, new() { Id = "stats", Side = "right" },
    };
}

public sealed class UpdateSettings
{
    public bool Check { get; set; } = true;
    public string Channel { get; set; } = "stable";
}

/// <summary>
/// theme.palette and theme.fonts are kept for back-compat (older config files, /config/full editing)
/// but the wallpaper page no longer applies them: since the v3 token pass (ui/tokens.css) the owner
/// settled on one fixed brand theme, so wallpaper/layout.js setThemeVars only applies dim/radius/gap/
/// pad/blur now. These defaults exist mainly as the migration target in ConfigStore.Load (old
/// "Kharkiv Tone"/"DM Mono" font values get replaced on load; the palette is left alone since it is
/// unused either way).
/// </summary>
public sealed class ThemeSettings
{
    public string Preset { get; set; } = "nna1618";
    public Dictionary<string, string> Palette { get; set; } = new()
    {
        ["bgPage"] = "#0B0B0B",
        ["bgSurface"] = "#161616",
        ["fg"] = "#FFFFFF",
        ["fgBody"] = "#C8C8C8",
        ["fgMuted"] = "#808080",
        ["fgGhost"] = "#1D1D1D",
        ["border"] = "#434343",
        ["glass"] = "rgba(22,22,22,0.72)",
    };
    public Dictionary<string, string> Fonts { get; set; } = new()
    {
        ["display"] = "Roboto Flex",
        ["mono"] = "JetBrains Mono",
    };
    public int Radius { get; set; } = 21;
    public int Gap { get; set; } = 21;
    public int Pad { get; set; } = 34;
    public int Blur { get; set; } = 14;
    public double Dim { get; set; } = 0.4;
}

public sealed class AudioSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Render device id for loopback capture; null = default device.</summary>
    public string? Device { get; set; }
}

public sealed class GraphLimits
{
    public int MaxNodes { get; set; } = 100;
    public int MaxLinks { get; set; } = 450;
}

public sealed class WeatherSettings
{
    public double Lat { get; set; } = 55.7558;
    public double Lon { get; set; } = 37.6173;
    public string Tz { get; set; } = "Europe/Moscow";
    public string Name { get; set; } = "MOSCOW";
}

public sealed class PlannerSettings
{
    public string SupabaseUrl { get; set; } = "https://tvpmtjidonohpgwhyssm.supabase.co";
    public string PublishableKey { get; set; } = "sb_publishable_HYka0T2lxZndnMainWUNXg_YPWy2dy8";
    public string LoginUrl { get; set; } = "https://planner.nna1618.com/desktop/login.html";
    public List<string> Show { get; set; } = new() { "tasks", "meetings", "habits", "money", "briefing" };
}

/// <summary>monitors.json — per-monitor layouts.</summary>
public sealed class MonitorsConfig
{
    public int Version { get; set; } = 1;
    public List<MonitorLayout> Monitors { get; set; } = new();
}

public sealed class MonitorLayout
{
    /// <summary>"{DeviceName}|{Width}x{Height}", e.g. "\\.\DISPLAY2|3440x1440".</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public GridSpec Grid { get; set; } = new();
    public List<BlockSpec> Blocks { get; set; } = new();
}

public sealed class GridSpec
{
    public int Cols { get; set; } = 3;
    public int Rows { get; set; } = 16;
    public List<double>? ColWeights { get; set; }
    public int Gap { get; set; } = 24;
    public int Pad { get; set; } = 48;
}

public sealed class BlockSpec
{
    public string Widget { get; set; } = "";
    public int Col { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
    public int Row { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public JsonObject? SettingsOverride { get; set; }
}
