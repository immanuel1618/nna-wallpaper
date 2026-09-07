using NNA.Wallpaper.Host;
using Velopack;
using Velopack.Sources;

namespace NNA.Wallpaper;

/// <summary>Update check and apply through Velopack against GitHub Releases of this repository.</summary>
public static class Updates
{
    public const string RepoUrl = "https://github.com/immanuel1618/nna-wallpaper";

    public sealed record Result(bool ok, bool? installed, string current, string? available, string message, string? error = null);

    /// <summary>Direct release download URL: no GitHub API calls, so no anonymous rate limit (shared VPN exits hit it easily).</summary>
    public const string DirectFeedUrl = RepoUrl + "/releases/latest/download/";

    private static UpdateManager DirectManager() => new(new SimpleWebSource(DirectFeedUrl));
    private static UpdateManager ApiManager() => new(new GithubSource(RepoUrl, null, false));

    /// <summary>Check through the direct feed first; fall back to the GitHub API source when the feed is unreachable.</summary>
    private static async Task<(UpdateManager mgr, UpdateInfo? info)> CheckWithFallbackAsync(HostContext ctx)
    {
        var direct = DirectManager();
        try
        {
            return (direct, await direct.CheckForUpdatesAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            ctx.Log.Warn("update check via direct feed failed, trying GitHub API: " + ex.Message);
            var api = ApiManager();
            return (api, await api.CheckForUpdatesAsync().ConfigureAwait(false));
        }
    }

    public static async Task<Result> CheckAsync(HostContext ctx)
    {
        try
        {
            if (!DirectManager().IsInstalled)
            {
                return new Result(true, false, HostInfo.Version, null, "not installed (dev or unpacked build): updates are managed by the installer");
            }
            var (mgr, info) = await CheckWithFallbackAsync(ctx).ConfigureAwait(false);
            var available = info?.TargetFullRelease?.Version?.ToString();
            ctx.Log.Info("update check: current " + mgr.CurrentVersion + ", available " + (available ?? "none"));
            return new Result(true, true, mgr.CurrentVersion?.ToString() ?? HostInfo.Version, available, available is null ? "no updates" : "update available");
        }
        catch (Exception ex)
        {
            ctx.Log.Error("update check failed", ex);
            return new Result(false, null, HostInfo.Version, null, "check failed", ex.Message);
        }
    }

    /// <summary>Download the newest release and restart into it. Returns before the restart when nothing is available.</summary>
    public static async Task<Result> ApplyAsync(HostContext ctx)
    {
        try
        {
            if (!DirectManager().IsInstalled) return new Result(true, false, HostInfo.Version, null, "not installed: nothing to apply");
            var (mgr, info) = await CheckWithFallbackAsync(ctx).ConfigureAwait(false);
            if (info is null) return new Result(true, true, mgr.CurrentVersion?.ToString() ?? HostInfo.Version, null, "no updates");
            var target = info.TargetFullRelease.Version.ToString();
            ctx.Log.Info("update: downloading " + target);
            await mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
            ctx.Log.Info("update: applying " + target + " and restarting");
            mgr.ApplyUpdatesAndRestart(info);
            return new Result(true, true, mgr.CurrentVersion?.ToString() ?? HostInfo.Version, target, "restarting into " + target);
        }
        catch (Exception ex)
        {
            ctx.Log.Error("update apply failed", ex);
            return new Result(false, null, HostInfo.Version, null, "apply failed", ex.Message);
        }
    }
}
