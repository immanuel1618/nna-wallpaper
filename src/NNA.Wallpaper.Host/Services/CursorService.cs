using System.Text.Json.Nodes;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// GET /cursor/status, POST /cursor/apply, POST /cursor/reset — installs one of the three brand
/// cursor schemes (presets/cursors/&lt;variant&gt;.json + presets/cursors/&lt;variant&gt;/*.cur|*.ani,
/// built by build/cursors.py, see docs/CURSORS.md) under the per-user registry key
/// <c>HKCU\Control Panel\Cursors</c>, with a full backup/restore so the owner's original scheme
/// (or lack of one) can always be put back exactly.
///
/// Files are copied into <c>&lt;data&gt;\cursors\&lt;variant&gt;\</c> (registry values must point at a
/// stable path, and the shipped presets folder can be replaced/updated independently of what is
/// currently installed). The very first successful apply snapshots *every* value currently under
/// the registry key (not just the roles we touch) into <c>&lt;data&gt;\cursors-backup.json</c>; that
/// file is left alone by later applies (so switching variants never clobbers the original scheme)
/// and is deleted once a reset consumes it — a second reset with no backup left reports
/// <c>{ok:false, error:"no backup"}</c> instead of silently doing nothing.
/// </summary>
public sealed class CursorService : IHostService
{
    private const string CursorsKeyPath = @"Control Panel\Cursors";
    private static readonly string[] VariantIds = { "mark", "line", "mono" };
    private static readonly int[] ValidSizes = { 32, 48, 64 };

    private readonly HostContext _ctx;
    private string BackupFile => Path.Combine(_ctx.Paths.DataDir, "cursors-backup.json");

    private sealed record CursorFileEntry(string Role, string File, string RegistryRole);

    public CursorService(HostContext ctx)
    {
        _ctx = ctx;
        ReapplyIfNeeded();
    }

    public void Register(LocalApi api)
    {
        api.Map("GET", "/cursor/status", Status);
        api.Map("POST", "/cursor/apply", Apply);
        api.Map("POST", "/cursor/reset", Reset);
    }

    // --------------------------------------------------------------------------- GET /cursor/status

    private Task Status(ApiRequest req)
    {
        var active = GetActiveVariant();
        var variants = new JsonArray();
        foreach (var id in VariantIds)
        {
            var manifest = LoadManifestNode(id);
            variants.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = manifest?["name"]?.DeepClone(),
                ["description"] = (string?)manifest?["description"],
                ["files"] = (manifest?["files"] as JsonArray)?.Count ?? 0,
            });
        }

        string? scheme = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CursorsKeyPath);
            scheme = key?.GetValue("") as string;
        }
        catch { /* leave null */ }

        var obj = new JsonObject
        {
            ["active"] = active,
            ["size"] = ValidSizes.Contains(_ctx.Config.App.Cursor.Size) ? _ctx.Config.App.Cursor.Size : 32,
            ["variants"] = variants,
            ["backup"] = File.Exists(BackupFile),
            ["scheme"] = scheme,
        };
        return req.Json(obj);
    }

    // --------------------------------------------------------------------------- POST /cursor/apply

    private async Task Apply(ApiRequest req)
    {
        var body = await req.ReadBodyAsync().ConfigureAwait(false);
        var obj = Json.ParseNode(body) as JsonObject;
        var variant = (string?)obj?["variant"];
        if (string.IsNullOrEmpty(variant) || !VariantIds.Contains(variant))
        {
            await req.Json(new { ok = false, error = "bad variant" }, 400).ConfigureAwait(false);
            return;
        }

        var size = ValidSizes.Contains(_ctx.Config.App.Cursor.Size) ? _ctx.Config.App.Cursor.Size : 32;
        if (obj?["size"] is JsonValue sv && sv.TryGetValue<int>(out var requestedSize) && ValidSizes.Contains(requestedSize))
            size = requestedSize;

        var manifest = LoadManifest(variant);
        if (manifest is null)
        {
            await req.Json(new { ok = false, error = "manifest not found" }, 500).ConfigureAwait(false);
            return;
        }

        int applied;
        try
        {
            applied = ApplyVariant(variant, size, manifest);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("cursor apply failed", ex);
            await req.Json(new { ok = false, error = ex.Message }, 500).ConfigureAwait(false);
            return;
        }

        _ctx.Config.App.Cursor.Variant = variant;
        _ctx.Config.App.Cursor.Size = size;
        _ctx.Config.SaveApp();

        await req.Json(new { ok = true, active = variant, applied }).ConfigureAwait(false);
    }

    private int ApplyVariant(string variant, int size, List<CursorFileEntry> files)
    {
        var destDir = Path.Combine(_ctx.Paths.DataDir, "cursors", variant);
        Directory.CreateDirectory(destDir);
        var srcDir = Path.Combine(_ctx.Paths.InstallDir, "presets", "cursors", variant);

        using var key = Registry.CurrentUser.CreateSubKey(CursorsKeyPath, writable: true)
            ?? throw new InvalidOperationException("could not open HKCU\\Control Panel\\Cursors");

        EnsureBackup(key);

        var applied = 0;
        foreach (var f in files)
        {
            var src = Path.Combine(srcDir, f.File);
            var dst = Path.Combine(destDir, f.File);
            if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
            if (!File.Exists(dst)) continue;
            key.SetValue(f.RegistryRole, dst, RegistryValueKind.String);
            applied++;
        }

        key.SetValue("", "NNA " + variant, RegistryValueKind.String);
        key.SetValue("Scheme Source", 1, RegistryValueKind.DWord);
        if (key.GetValue("CursorBaseSize") is not null)
            key.SetValue("CursorBaseSize", size, RegistryValueKind.DWord);

        SetCursors();
        return applied;
    }

    // --------------------------------------------------------------------------- POST /cursor/reset

    private async Task Reset(ApiRequest req)
    {
        var snapshot = Json.LoadFile(BackupFile) as JsonObject;
        if (snapshot is null)
        {
            await req.Json(new { ok = false, error = "no backup" }).ConfigureAwait(false);
            return;
        }

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(CursorsKeyPath, writable: true)
                ?? throw new InvalidOperationException("could not open HKCU\\Control Panel\\Cursors"))
            {
                RestoreKey(key, snapshot);
            }
            SetCursors();
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("cursor reset failed", ex);
            await req.Json(new { ok = false, error = ex.Message }, 500).ConfigureAwait(false);
            return;
        }

        try { File.Delete(BackupFile); } catch { /* best effort */ }

        _ctx.Config.App.Cursor.Variant = null;
        _ctx.Config.SaveApp();

        await req.Json(new { ok = true, restored = snapshot.Count }).ConfigureAwait(false);
    }

    // --------------------------------------------------------------------------- startup re-apply

    /// <summary>
    /// Idempotent: if a variant is configured and the registry already points into our own
    /// &lt;data&gt;\cursors\&lt;variant&gt; folder with all files present, nothing is written. Otherwise
    /// (fresh install with an old app.json, files missing, or another app/scheme took over the
    /// registry key) the configured variant is silently re-applied.
    /// </summary>
    private void ReapplyIfNeeded()
    {
        var variant = _ctx.Config.App.Cursor.Variant;
        if (string.IsNullOrEmpty(variant) || !VariantIds.Contains(variant)) return;

        try
        {
            var manifest = LoadManifest(variant);
            if (manifest is null) return;

            var destDir = Path.Combine(_ctx.Paths.DataDir, "cursors", variant);
            var filesOk = manifest.All(f => File.Exists(Path.Combine(destDir, f.File)));
            var active = GetActiveVariant();
            if (filesOk && active == variant) return; // already ours: idempotent, write nothing

            var size = ValidSizes.Contains(_ctx.Config.App.Cursor.Size) ? _ctx.Config.App.Cursor.Size : 32;
            ApplyVariant(variant, size, manifest);
            _ctx.Log.Info("cursor: re-applied '" + variant + "' on startup (filesOk=" + filesOk + ", activeWas=" + (active ?? "null") + ")");
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn("cursor: startup re-apply failed: " + ex.Message);
        }
    }

    // --------------------------------------------------------------------------- helpers

    private string? GetActiveVariant()
    {
        string? arrow;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CursorsKeyPath);
            arrow = key?.GetValue("Arrow") as string;
        }
        catch { return null; }

        if (string.IsNullOrEmpty(arrow)) return null;

        var cursorsRoot = Path.Combine(_ctx.Paths.DataDir, "cursors") + Path.DirectorySeparatorChar;
        if (!arrow.StartsWith(cursorsRoot, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = arrow[cursorsRoot.Length..];
        var slash = rest.IndexOfAny(new[] { '\\', '/' });
        if (slash <= 0) return null;

        var variant = rest[..slash];
        return VariantIds.Contains(variant) ? variant : null;
    }

    private JsonObject? LoadManifestNode(string variant) =>
        Json.LoadFile(Path.Combine(_ctx.Paths.InstallDir, "presets", "cursors", variant + ".json")) as JsonObject;

    private List<CursorFileEntry>? LoadManifest(string variant)
    {
        var node = LoadManifestNode(variant);
        if (node?["files"] is not JsonArray filesNode) return null;

        var list = new List<CursorFileEntry>();
        foreach (var f in filesNode)
        {
            if (f is not JsonObject fo) continue;
            var role = (string?)fo["role"] ?? "";
            var file = (string?)fo["file"] ?? "";
            var regRole = (string?)fo["registryRole"] ?? "";
            if (role.Length == 0 || file.Length == 0 || regRole.Length == 0) continue;
            list.Add(new CursorFileEntry(role, file, regRole));
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>Snapshots every value currently under the key (not just the roles we're about to
    /// touch) so reset can put back anything we changed, including values we didn't know about.
    /// No-op if a backup already exists (never overwrite an existing backup).</summary>
    private void EnsureBackup(RegistryKey key)
    {
        if (File.Exists(BackupFile)) return;
        var snapshot = SnapshotKey(key);
        Json.WriteFileAtomic(BackupFile, snapshot.ToJsonString(Json.Config));
        _ctx.Log.Info("cursor: backup written " + BackupFile);
    }

    private static JsonObject SnapshotKey(RegistryKey key)
    {
        var obj = new JsonObject();
        foreach (var rawName in key.GetValueNames())
        {
            RegistryValueKind kind;
            object? value;
            try
            {
                kind = key.GetValueKind(rawName);
                value = key.GetValue(rawName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            catch { continue; }

            var entry = new JsonObject { ["kind"] = kind.ToString() };
            entry["value"] = kind switch
            {
                RegistryValueKind.DWord or RegistryValueKind.QWord => JsonValue.Create(Convert.ToInt64(value)),
                RegistryValueKind.MultiString => new JsonArray((value as string[] ?? Array.Empty<string>()).Select(s => (JsonNode)s).ToArray()),
                RegistryValueKind.Binary => JsonValue.Create(Convert.ToBase64String((byte[])(value ?? Array.Empty<byte>()))),
                _ => JsonValue.Create(value as string ?? ""),
            };

            var name = rawName.Length == 0 ? "(Default)" : rawName;
            obj[name] = entry;
        }
        return obj;
    }

    /// <summary>Restores the key to exactly the given snapshot: values present in the snapshot are
    /// written back with their original kind, values present now but absent from the snapshot
    /// (i.e. we created them) are deleted.</summary>
    private static void RestoreKey(RegistryKey key, JsonObject snapshot)
    {
        var wanted = new HashSet<string>(
            snapshot.Select(kv => kv.Key == "(Default)" ? "" : kv.Key),
            StringComparer.OrdinalIgnoreCase);

        foreach (var current in key.GetValueNames())
        {
            if (!wanted.Contains(current)) key.DeleteValue(current, throwOnMissingValue: false);
        }

        foreach (var kv in snapshot)
        {
            if (kv.Value is not JsonObject entry) continue;
            var name = kv.Key == "(Default)" ? "" : kv.Key;
            var kindStr = (string?)entry["kind"] ?? nameof(RegistryValueKind.String);
            if (!Enum.TryParse<RegistryValueKind>(kindStr, out var kind) || kind == RegistryValueKind.Unknown)
                kind = RegistryValueKind.String;

            object valueToSet = kind switch
            {
                RegistryValueKind.DWord => (int)(entry["value"]?.GetValue<long>() ?? 0),
                RegistryValueKind.QWord => entry["value"]?.GetValue<long>() ?? 0L,
                RegistryValueKind.MultiString => (entry["value"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>(),
                RegistryValueKind.Binary => Convert.FromBase64String((string?)entry["value"] ?? ""),
                _ => (string?)entry["value"] ?? "",
            };
            key.SetValue(name, valueToSet, kind);
        }
    }

    private static unsafe void SetCursors()
    {
        PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETCURSORS, 0, null,
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS.SPIF_UPDATEINIFILE | SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS.SPIF_SENDCHANGE);
    }
}
