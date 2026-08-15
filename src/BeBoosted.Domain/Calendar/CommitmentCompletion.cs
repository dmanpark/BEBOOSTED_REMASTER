namespace BeBoosted.Domain.Calendar;

/// <summary>
/// A checked-off occurrence of a local fixed commitment. Completion is always
/// per-occurrence — one-off commitments have exactly one occurrence (their date),
/// recurring series one per expanded date — and never touches the block itself or
/// the task-block outcome rules. Reopening simply removes the record.
/// </summary>
public sealed record CommitmentCompletion(
    CalendarBlockId BlockId,
    DateOnly OccurrenceDate,
    DateTimeOffset CompletedAt)
{
    public static CommitmentCompletion Create(CalendarBlock block, DateOnly occurrenceDate, DateTimeOffset now)
    {
        block.EnsureOccurrenceCompletable(occurrenceDate);
        return new CommitmentCompletion(block.Id, occurrenceDate, now);
    }
}
