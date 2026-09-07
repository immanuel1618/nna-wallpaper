using System.Linq;
using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Planner;

/// <summary>Minimal profile fields the host needs to render the block and compute local time (see
/// auth-telegram-widget response). first_name/username/photo_url/sections were added in the
/// auth-telegram-widget v2 response (2026-09) alongside the original display_name/lang/timezone/tier
/// — a session saved before that upgrade simply has them empty until the next successful token
/// refresh re-populates them (see PlannerSession.ApplyRefresh), or the settings page falls back to
/// initials/no-sections display.</summary>
public sealed class PlannerProfile
{
    public string Id { get; set; } = "";
    public long? TgUserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Telegram-hosted avatar URL (https://t.me/...); only ever downloaded from that host, see PlannerClient.DownloadAvatarAsync.</summary>
    public string PhotoUrl { get; set; } = "";
    public string Lang { get; set; } = "ru";
    public string Timezone { get; set; } = "Europe/Moscow";
    public string Tier { get; set; } = "free";
    public List<string>? Sections { get; set; }

    public static PlannerProfile FromJson(JsonNode? node)
    {
        var p = new PlannerProfile();
        if (node is null) return p;
        p.Id = (string?)node["id"] ?? "";
        p.TgUserId = (long?)node["tg_user_id"];
        p.DisplayName = (string?)node["display_name"] ?? "";
        p.FirstName = (string?)node["first_name"] ?? "";
        p.Username = (string?)node["username"] ?? "";
        p.PhotoUrl = (string?)node["photo_url"] ?? "";
        p.Lang = (string?)node["lang"] ?? "ru";
        p.Timezone = (string?)node["timezone"] ?? "Europe/Moscow";
        p.Tier = (string?)node["tier"] ?? "free";
        p.Sections = (node["sections"] as JsonArray)?
            .Select(x => (string?)x ?? "")
            .Where(x => x.Length > 0)
            .ToList();
        return p;
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["id"] = Id,
            ["tg_user_id"] = TgUserId,
            ["display_name"] = DisplayName,
            ["first_name"] = FirstName,
            ["username"] = Username,
            ["photo_url"] = PhotoUrl,
            ["lang"] = Lang,
            ["timezone"] = Timezone,
            ["tier"] = Tier,
        };
        if (Sections is { Count: > 0 }) obj["sections"] = new JsonArray(Sections.Select(x => (JsonNode)x).ToArray());
        return obj;
    }
}

/// <summary>One row from entries (kind=task, state=open); Overdue is computed by the host from due_at vs now.</summary>
public sealed record PlannerTaskItem(string Id, string Title, DateTimeOffset? DueAt, bool Overdue, string State);

/// <summary>One row from entries (kind=meeting), starting no more than 30 minutes ago.</summary>
public sealed record PlannerMeetingItem(string Id, string Title, DateTimeOffset StartsAt, int? DurationMin);

/// <summary>One row from entries (kind=habit, state=open); CheckedToday reflects meta.checks in the profile's timezone.</summary>
public sealed record PlannerHabitItem(string Id, string Title, bool CheckedToday);

/// <summary>Everything /planner/today needs, already parsed from PostgREST rows.</summary>
public sealed record PlannerTodayData(
    List<PlannerTaskItem> Tasks,
    List<PlannerMeetingItem> Meetings,
    List<PlannerHabitItem> Habits,
    long SpentMinor,
    long IncomeMinor,
    Dictionary<string, long> ByCategory,
    JsonObject? Summary);

/// <summary>Raw status + body of an upstream HTTP call (capture), passed through mostly as-is.</summary>
public sealed record PlannerHttpResult(int Status, JsonObject? Body);
