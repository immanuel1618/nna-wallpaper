using NNA.Wallpaper.Host;
using Velopack;
using Velopack.Sources;

namespace NNA.Wallpaper;

/// <summary>Update check through Velopack against GitHub Releases of this repository.</summary>
public static class Updates
{
    public const string RepoUrl = "https://github.com/immanuel1618/nna-wallpaper";

    public static async Task<object> CheckAsync(HostContext ctx)
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!mgr.IsInstalled)
            {
                return new { ok = true, installed = false, current = HostInfo.Version, available = (string?)null, message = "not installed (dev or unpacked build): updates are managed by the installer" };
            }
            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            var available = info?.TargetFullRelease?.Version?.ToString();
            ctx.Log.Info("update check: current " + mgr.CurrentVersion + ", available " + (available ?? "none"));
            return new { ok = true, installed = true, current = mgr.CurrentVersion?.ToString() ?? HostInfo.Version, available, message = available is null ? "no updates" : "update available" };
        }
        catch (Exception ex)
        {
            ctx.Log.Error("update check failed", ex);
            return new { ok = false, installed = (bool?)null, current = HostInfo.Version, available = (string?)null, error = ex.Message };
        }
    }
}
