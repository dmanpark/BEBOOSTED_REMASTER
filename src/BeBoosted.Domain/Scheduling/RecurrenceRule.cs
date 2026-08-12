namespace BeBoosted.Domain.Scheduling;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
}

/// <summary>
/// A simple forward recurrence: every N days, or selected weekdays every N weeks.
/// Recurring work stays ordinary tasks/blocks — this must never grow into a habit system.
/// </summary>
public sealed class RecurrenceRule
{
    private readonly HashSet<DayOfWeek> _daysOfWeek;

    private RecurrenceRule(RecurrenceFrequency frequency, int interval, IEnumerable<DayOfWeek> daysOfWeek)
    {
        if (interval < 1)
        {
            throw new DomainException("A recurrence interval must be at least 1.");
        }

        Frequency = frequency;
        Interval = interval;
        _daysOfWeek = [.. daysOfWeek];

        if (frequency == RecurrenceFrequency.Weekly && _daysOfWeek.Count == 0)
        {
            throw new DomainException("A weekly recurrence needs at least one weekday.");
        }
    }

    public RecurrenceFrequency Frequency { get; }

    public int Interval { get; }

    public IReadOnlyCollection<DayOfWeek> DaysOfWeek => _daysOfWeek;

    public static RecurrenceRule Daily(int interval = 1) => new(RecurrenceFrequency.Daily, interval, []);

    public static RecurrenceRule Weekly(int interval, params DayOfWeek[] daysOfWeek)
        => new(RecurrenceFrequency.Weekly, interval, daysOfWeek);

    /// <summary>Whether an occurrence falls on <paramref name="date"/> for a series anchored at <paramref name="seriesStart"/>.</summary>
    public bool OccursOn(DateOnly date, DateOnly seriesStart)
    {
        if (date < seriesStart)
        {
            return false;
        }

        if (Frequency == RecurrenceFrequency.Daily)
        {
            return (date.DayNumber - seriesStart.DayNumber) % Interval == 0;
        }

        if (!_daysOfWeek.Contains(date.DayOfWeek))
        {
            return false;
        }

        var weeksBetween = (MondayOf(date).DayNumber - MondayOf(seriesStart).DayNumber) / 7;
        return weeksBetween % Interval == 0;
    }

    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
