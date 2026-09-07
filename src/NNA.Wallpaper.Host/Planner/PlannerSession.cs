using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Planner;

/// <summary>
/// The signed-in Planner session: Supabase tokens plus the minimal profile fields the widget needs.
/// Persisted to <see cref="Paths.PlannerSessionFile"/> DPAPI-protected for the current Windows user
/// (<see cref="DataProtectionScope.CurrentUser"/>) so the file is useless if copied elsewhere.
/// Never write the access/refresh token to the log.
/// </summary>
public sealed class PlannerSession
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public PlannerProfile Profile { get; set; } = new();

    /// <summary>True once the access token is within 5 minutes of expiry (or already expired).</summary>
    public bool NeedsRefresh => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(5);

    public static PlannerSession FromLoginResponse(JsonNode root)
    {
        var s = new PlannerSession
        {
            AccessToken = (string?)root["access_token"] ?? "",
            RefreshToken = (string?)root["refresh_token"] ?? "",
            Profile = PlannerProfile.FromJson(root["profile"]),
        };
        s.ExpiresAt = ReadExpiresAt(root);
        return s;
    }

    /// <summary>Apply a GoTrue refresh-token response in place. The profile itself never changes here.</summary>
    public void ApplyRefresh(JsonNode root)
    {
        AccessToken = (string?)root["access_token"] ?? AccessToken;
        RefreshToken = (string?)root["refresh_token"] ?? RefreshToken;
        ExpiresAt = ReadExpiresAt(root);
    }

    private static DateTimeOffset ReadExpiresAt(JsonNode root)
    {
        var atSeconds = (long?)root["expires_at"];
        if (atSeconds is long unixSeconds) return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var inSeconds = (double?)root["expires_in"];
        if (inSeconds is double s) return DateTimeOffset.UtcNow.AddSeconds(s);
        // Conservative default: forces a refresh attempt soon rather than trusting a token indefinitely.
        return DateTimeOffset.UtcNow.AddMinutes(10);
    }

    private JsonObject ToStorageJson() => new()
    {
        ["access_token"] = AccessToken,
        ["refresh_token"] = RefreshToken,
        ["expires_at"] = ExpiresAt.ToUnixTimeSeconds(),
        ["profile"] = Profile.ToJson(),
    };

    /// <summary>DPAPI-protect and write atomically. Never throws — a save failure just means the user logs in again next launch.</summary>
    public void Save(HostContext ctx)
    {
        try
        {
            var plain = Encoding.UTF8.GetBytes(ToStorageJson().ToJsonString());
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var file = ctx.Paths.PlannerSessionFile;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            ctx.Log.Error("planner session save failed", ex);
        }
    }

    public static PlannerSession? Load(HostContext ctx)
    {
        try
        {
            var file = ctx.Paths.PlannerSessionFile;
            if (!File.Exists(file)) return null;
            var protectedBytes = File.ReadAllBytes(file);
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var root = Json.ParseNode(Encoding.UTF8.GetString(plain));
            if (root is null) return null;
            var s = new PlannerSession
            {
                AccessToken = (string?)root["access_token"] ?? "",
                RefreshToken = (string?)root["refresh_token"] ?? "",
                Profile = PlannerProfile.FromJson(root["profile"]),
            };
            var atSeconds = (long?)root["expires_at"];
            s.ExpiresAt = atSeconds is long u ? DateTimeOffset.FromUnixTimeSeconds(u) : DateTimeOffset.UtcNow;
            return string.IsNullOrEmpty(s.AccessToken) ? null : s;
        }
        catch (Exception ex)
        {
            ctx.Log.Error("planner session load failed", ex);
            return null;
        }
    }

    public static void Delete(HostContext ctx)
    {
        try
        {
            if (File.Exists(ctx.Paths.PlannerSessionFile)) File.Delete(ctx.Paths.PlannerSessionFile);
        }
        catch (Exception ex)
        {
            ctx.Log.Error("planner session delete failed", ex);
        }
    }
}
