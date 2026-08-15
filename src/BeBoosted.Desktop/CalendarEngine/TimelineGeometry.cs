namespace BeBoosted.Desktop.CalendarEngine;

/// <summary>
/// Pure time↔vertical-position math for the timeline surface. No Avalonia types —
/// fully unit-testable. Bounds are minutes from midnight so a full 00:00–24:00 day
/// (an exclusive 24:00 end, which <see cref="TimeOnly"/> cannot express) stays
/// representable. Hour height is configurable so dense layouts can use shorter rows.
/// </summary>
public sealed record TimelineGeometry(int StartMinutes, int EndMinutes, double HourHeight)
{
    public double TotalHeight => (EndMinutes - StartMinutes) / 60.0 * HourHeight;

    public double YFromMinutes(double minutes) => (minutes - StartMinutes) / 60.0 * HourHeight;

    public double YFromTime(TimeOnly time) => YFromMinutes(time.ToTimeSpan().TotalMinutes);

    public double HeightForDuration(TimeSpan duration) => duration.TotalHours * HourHeight;

    /// <summary>
    /// Converts a vertical position back to a time, snapped and clamped to the range —
    /// to 23:59 at a midnight end bound, since <see cref="TimeOnly"/> cannot express 24:00.
    /// </summary>
    public TimeOnly TimeFromY(double y, int snapMinutes)
    {
        var minutes = (y / HourHeight * 60.0) + StartMinutes;
        var snapped = Math.Round(minutes / snapMinutes) * snapMinutes;
        snapped = Math.Clamp(snapped, StartMinutes, Math.Min(EndMinutes, (24 * 60) - 1));
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(snapped));
    }

    /// <summary>Hour marks in the range (inclusive start, exclusive end).</summary>
    public IEnumerable<TimeOnly> HourMarks()
    {
        var hour = (StartMinutes + 59) / 60;
        var endHour = Math.Min((EndMinutes + 59) / 60, 24);
        for (; hour < endHour; hour++)
        {
            yield return new TimeOnly(hour, 0);
        }
    }
}
