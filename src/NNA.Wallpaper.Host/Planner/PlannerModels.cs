using System.Text.Json.Nodes;

namespace NNA.Wallpaper.Host.Planner;

/// <summary>Minimal profile fields the host needs to render the block and compute local time (see auth-telegram-widget response).</summary>
public sealed class PlannerProfile
{
    public string Id { get; set; } = "";
    public long? TgUserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Lang { get; set; } = "ru";
    public string Timezone { get; set; } = "Europe/Moscow";
    public string Tier { get; set; } = "free";

    public static PlannerProfile FromJson(JsonNode? node)
    {
        var p = new PlannerProfile();
        if (node is null) return p;
        p.Id = (string?)node["id"] ?? "";
        p.TgUserId = (long?)node["tg_user_id"];
        p.DisplayName = (string?)node["display_name"] ?? "";
        p.Lang = (string?)node["lang"] ?? "ru";
        p.Timezone = (string?)node["timezone"] ?? "Europe/Moscow";
        p.Tier = (string?)node["tier"] ?? "free";
        return p;
    }

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["tg_user_id"] = TgUserId,
        ["display_name"] = DisplayName,
        ["lang"] = Lang,
        ["timezone"] = Timezone,
        ["tier"] = Tier,
    };
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
