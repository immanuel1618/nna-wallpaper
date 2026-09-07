using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// POST /open?path=&lt;path&gt; — open a file/folder in the configured editor (or via shell) if it is
/// inside one of the configured OpenRoots. POST /edit?what=&lt;tab&gt; — open a settings tab in the app.
/// </summary>
public sealed class OpenService : IHostService
{
    private static readonly string[] EditableTabs = { "launch", "events", "config", "nna-config" };

    private readonly HostContext _ctx;

    public OpenService(HostContext ctx)
    {
        _ctx = ctx;
    }

    public void Register(LocalApi api)
    {
        api.Map("POST", "/open", HandleOpen);
        api.Map("POST", "/edit", HandleEdit);
    }

    private Task HandleOpen(ApiRequest req)
    {
        var path = req.Query("path") ?? "";
        return req.Json(OpenPath(path));
    }

    private JsonObject OpenPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return new JsonObject { ["ok"] = false, ["error"] = "empty path" };

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }

        var roots = _ctx.Config.App.OpenRoots;
        var allowed = roots.Count > 0 && roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
            return new JsonObject { ["ok"] = false, ["error"] = "outside roots" };

        if (!File.Exists(full) && !Directory.Exists(full))
            return new JsonObject { ["ok"] = false, ["error"] = "not found" };

        try
        {
            var driveKey = full.Length >= 2 ? full[..2] : full;
            if (_ctx.Config.App.OpenWith.TryGetValue(driveKey, out var tool) && tool.Length > 0)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = tool[0],
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                for (var i = 1; i < tool.Length; i++) psi.ArgumentList.Add(tool[i]);
                psi.ArgumentList.Add(full);
                Process.Start(psi);
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });
            }
            _ctx.Log.Info("open " + full);
            return new JsonObject { ["ok"] = true };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
    }

    private Task HandleEdit(ApiRequest req)
    {
        var what = req.Query("what") ?? "";
        if (!EditableTabs.Contains(what))
            return req.Json(new { ok = false, error = "unknown file" });

        _ctx.App.OpenSettings(what);
        return req.Json(new { ok = true });
    }
}
