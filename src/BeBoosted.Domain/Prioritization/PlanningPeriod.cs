using System.Globalization;

namespace BeBoosted.Domain.Prioritization;

public enum PlanningPeriodKind
{
    Today = 0,
    Week = 1,
}

/// <summary>
/// The scope a ranking applies to. Ranks never outlive their period: Today ranks are
/// anchored to a date, Week ranks to that week's Monday. Custom ranges reuse the week
/// engine per the approved defaults.
/// </summary>
public readonly record struct PlanningPeriod(PlanningPeriodKind Kind, DateOnly Anchor)
{
    public static PlanningPeriod ForToday(DateOnly today) => new(PlanningPeriodKind.Today, today);

    public static PlanningPeriod ForWeek(DateOnly anyDayInWeek)
    {
        var monday = anyDayInWeek.AddDays(-(((int)anyDayInWeek.DayOfWeek + 6) % 7));
        return new PlanningPeriod(PlanningPeriodKind.Week, monday);
    }

    /// <summary>Stable persistence key, e.g. "today:2026-08-11" or "week:2026-08-10".</summary>
    public string Key => string.Create(
        CultureInfo.InvariantCulture,
        $"{(Kind == PlanningPeriodKind.Today ? "today" : "week")}:{Anchor:yyyy-MM-dd}");
}
