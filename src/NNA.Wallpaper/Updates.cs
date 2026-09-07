using NNA.Wallpaper.Host;
using Velopack;
using Velopack.Sources;

namespace NNA.Wallpaper;

/// <summary>Update check and apply through Velopack against GitHub Releases of this repository.</summary>
public static class Updates
{
    public const string RepoUrl = "https://github.com/immanuel1618/nna-wallpaper";

    public sealed record Result(bool ok, bool? installed, string current, string? available, string message, string? error = null);

    private static UpdateManager Manager() => new(new GithubSource(RepoUrl, null, false));

    public static async Task<Result> CheckAsync(HostContext ctx)
    {
        try
        {
            var mgr = Manager();
            if (!mgr.IsInstalled)
            {
                return new Result(true, false, HostInfo.Version, null, "not installed (dev or unpacked build): updates are managed by the installer");
            }
            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
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
            var mgr = Manager();
            if (!mgr.IsInstalled) return new Result(true, false, HostInfo.Version, null, "not installed: nothing to apply");
            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
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
