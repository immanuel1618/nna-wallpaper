namespace NNA.Wallpaper.Host;

/// <summary>Static facts about the host library: product name and version.</summary>
public static class HostInfo
{
    public const string AppName = "NNA Wallpaper";

    public static string Version =>
        typeof(HostInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
