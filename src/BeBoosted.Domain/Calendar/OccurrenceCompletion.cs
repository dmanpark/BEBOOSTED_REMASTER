namespace BeBoosted.Domain.Calendar;

/// <summary>
/// A checked-off occurrence of a repeating local task session. Completion is always
/// per-occurrence for repeating schedules — one row per expanded date — and never
/// touches the session itself or its Task; one-off sessions complete their Task
/// instead. Reopening simply removes the record.
/// </summary>
public sealed record OccurrenceCompletion(
    CalendarBlockId BlockId,
    DateOnly OccurrenceDate,
    DateTimeOffset CompletedAt)
{
    public static OccurrenceCompletion Create(CalendarBlock block, DateOnly occurrenceDate, DateTimeOffset now)
    {
        block.EnsureOccurrenceCompletable(occurrenceDate);
        return new OccurrenceCompletion(block.Id, occurrenceDate, now);
    }
}
