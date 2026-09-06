using NNA.Wallpaper.Host.Config;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Picks the layout for a monitor: by exact id, then by size, then a default by orientation
/// (landscape = "main" layout, portrait = "vertical" layout). Aliases "main" / "vertical" are
/// accepted for testing without real monitors.
/// </summary>
public static class LayoutResolver
{
    public static MonitorLayout Resolve(ConfigStore cfg, IReadOnlyList<MonitorStatus> monitors, string? monitorId)
    {
        var all = cfg.Monitors.Monitors;
        if (!string.IsNullOrEmpty(monitorId))
        {
            var exact = all.FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var status = monitors.FirstOrDefault(m => string.Equals(m.id, monitorId, StringComparison.OrdinalIgnoreCase));
            if (status is not null)
            {
                var size = "|" + status.width + "x" + status.height;
                var bySize = all.FirstOrDefault(m => m.Id.EndsWith(size, StringComparison.OrdinalIgnoreCase));
                if (bySize is not null) return bySize;
                return status.height > status.width ? DefaultVertical(status.id) : DefaultMain(status.id);
            }

            if (monitorId.Equals("vertical", StringComparison.OrdinalIgnoreCase))
                return all.FirstOrDefault(m => IsPortrait(m)) ?? DefaultVertical("vertical");
            if (monitorId.Equals("main", StringComparison.OrdinalIgnoreCase))
                return all.FirstOrDefault(m => !IsPortrait(m)) ?? DefaultMain("main");
        }
        return all.FirstOrDefault() ?? DefaultMain("main");
    }

    public static bool IsPortrait(MonitorLayout m)
    {
        var bar = m.Id.LastIndexOf('|');
        if (bar < 0) return false;
        var parts = m.Id[(bar + 1)..].Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && h > w;
    }

    public static MonitorLayout DefaultMain(string id) => new()
    {
        Id = id,
        Name = "Main",
        Enabled = true,
        Grid = new GridSpec { Cols = 3, Rows = 16, ColWeights = new List<double> { 2.2, 1, 2.2 }, Gap = 24, Pad = 48 },
        Blocks = new List<BlockSpec>
        {
            new() { Widget = "eq", Col = 1, ColSpan = 1, Row = 1, RowSpan = 4 },
            new() { Widget = "photos", Col = 1, ColSpan = 1, Row = 5, RowSpan = 12 },
            new() { Widget = "focus", Col = 2, ColSpan = 1, Row = 1, RowSpan = 8 },
            new() { Widget = "weather", Col = 2, ColSpan = 1, Row = 9, RowSpan = 8 },
            new() { Widget = "events", Col = 3, ColSpan = 1, Row = 1, RowSpan = 4 },
            new() { Widget = "launch", Col = 3, ColSpan = 1, Row = 5, RowSpan = 4, SettingsOverride = new System.Text.Json.Nodes.JsonObject { ["compact"] = true } },
            new() { Widget = "graph", Col = 3, ColSpan = 1, Row = 9, RowSpan = 8 },
        },
    };

    public static MonitorLayout DefaultVertical(string id) => new()
    {
        Id = id,
        Name = "Vertical",
        Enabled = true,
        Grid = new GridSpec { Cols = 1, Rows = 4, Gap = 24, Pad = 48 },
        Blocks = new List<BlockSpec>
        {
            new() { Widget = "stats", Col = 1, ColSpan = 1, Row = 1, RowSpan = 1 },
            new() { Widget = "planner", Col = 1, ColSpan = 1, Row = 2, RowSpan = 1 },
            new() { Widget = "player", Col = 1, ColSpan = 1, Row = 3, RowSpan = 1 },
            new() { Widget = "launch", Col = 1, ColSpan = 1, Row = 4, RowSpan = 1 },
        },
    };
}
