namespace BeBoosted.Desktop.CalendarEngine;

/// <summary>
/// Pure time↔vertical-position math for the timeline surface. No Avalonia types —
/// fully unit-testable. Hour height and visible range are configurable so the
/// 1280×800 layout can use denser rows than 1440×960.
/// </summary>
public sealed record TimelineGeometry(TimeOnly VisibleStart, TimeOnly VisibleEnd, double HourHeight)
{
    public double TotalHeight => (VisibleEnd - VisibleStart).TotalHours * HourHeight;

    public double YFromTime(TimeOnly time)
        => (time - VisibleStart).TotalHours * HourHeight;

    public double HeightForDuration(TimeSpan duration) => duration.TotalHours * HourHeight;

    /// <summary>Converts a vertical position back to a time, snapped and clamped to the visible range.</summary>
    public TimeOnly TimeFromY(double y, int snapMinutes)
    {
        var minutes = (y / HourHeight * 60.0) + VisibleStart.ToTimeSpan().TotalMinutes;
        var snapped = Math.Round(minutes / snapMinutes) * snapMinutes;
        var min = VisibleStart.ToTimeSpan().TotalMinutes;
        var max = VisibleEnd.ToTimeSpan().TotalMinutes;
        snapped = Math.Clamp(snapped, min, max);
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(snapped));
    }

    /// <summary>Hour marks in the visible range (inclusive start, exclusive end).</summary>
    public IEnumerable<TimeOnly> HourMarks()
    {
        var hour = VisibleStart.Hour + (VisibleStart.Minute > 0 ? 1 : 0);
        var endHour = VisibleEnd.Hour + (VisibleEnd.Minute > 0 ? 1 : 0);
        for (; hour < endHour; hour++)
        {
            yield return new TimeOnly(hour, 0);
        }
    }
}
