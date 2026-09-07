namespace NNA.Wallpaper.Host.Planner;

/// <summary>
/// Builds the morning-briefing text shown at the top of the block, entirely from data the host
/// already fetched for /planner/today (mirrors the shape of the server's briefing/index.ts, but is
/// never stored or sent anywhere — it is regenerated locally on every cache refresh).
/// </summary>
public static class Briefing
{
    private static readonly string[] RuWeekdays = { "воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота" };
    private static readonly string[] RuMonthsGen = { "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря" };
    private static readonly string[] EnWeekdays = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
    private static readonly string[] EnMonths = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    public static string Build(
        PlannerProfile profile,
        IReadOnlyList<PlannerTaskItem> tasks,
        IReadOnlyList<PlannerMeetingItem> meetings,
        long spentMinor,
        int habitsChecked,
        int habitsTotal,
        DateTimeOffset nowLocal)
    {
        var en = string.Equals(profile.Lang, "en", StringComparison.OrdinalIgnoreCase);
        var open = tasks.Count;
        var overdue = tasks.Count(t => t.Overdue);

        var lines = new List<string> { DateHeader(nowLocal, en) };

        if (open == 0 && meetings.Count == 0 && spentMinor == 0)
        {
            lines.Add(en ? "Nothing due today, no meetings." : "Сегодня без задач и встреч.");
        }
        else
        {
            if (open > 0)
            {
                var names = string.Join(", ", tasks.Take(3).Select(t => t.Title));
                var overduePart = overdue > 0 ? (en ? $", {overdue} overdue" : $", просрочено {overdue}") : "";
                lines.Add((en ? $"Tasks today: {open}{overduePart}. " : $"Задач сегодня: {open}{overduePart}. ") + names + ".");
            }
            if (meetings.Count > 0)
            {
                var m = string.Join("; ", meetings.Select(x => x.StartsAt.ToString("HH:mm") + " — " + x.Title));
                lines.Add((en ? "Meetings: " : "Встречи: ") + m + ".");
            }
            if (spentMinor > 0)
            {
                var amount = Math.Round(spentMinor / 100.0).ToString("0");
                lines.Add((en ? $"Spent today: {amount} RUB." : $"Траты за день: {amount} ₽."));
            }
        }

        if (habitsTotal > 0)
        {
            lines.Add(en ? $"Habits: {habitsChecked}/{habitsTotal} checked." : $"Привычки: отмечено {habitsChecked} из {habitsTotal}.");
        }

        var text = string.Join(" ", lines).Trim();
        return text.Length > 0 ? text : (en ? "Nothing due today, no meetings." : "Сегодня без задач и встреч.");
    }

    private static string DateHeader(DateTimeOffset d, bool en)
    {
        if (en)
        {
            return EnWeekdays[(int)d.DayOfWeek] + ", " + EnMonths[d.Month - 1] + " " + d.Day + ".";
        }
        var weekday = RuWeekdays[(int)d.DayOfWeek];
        var capitalized = char.ToUpperInvariant(weekday[0]) + weekday.Substring(1);
        return capitalized + ", " + d.Day + " " + RuMonthsGen[d.Month - 1] + ".";
    }
}
