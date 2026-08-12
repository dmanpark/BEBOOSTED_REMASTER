using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Domain.Calendar;

public enum BlockKind
{
    /// <summary>A commitment (class, lunch, meeting): visually locked on the calendar.</summary>
    FixedCommitment = 0,

    /// <summary>An approved BeBoosted work block backed by a task.</summary>
    TaskBlock = 1,
}

public enum BlockOutcome
{
    None = 0,
    Done = 1,
    NeedsMoreTime = 2,
    DidntHappen = 3,
}

/// <summary>
/// An approved item on the local calendar. Proposals never live here — they stay in a
/// PlanningProposal until approved. Blocks are wall-clock local and never cross midnight.
/// Provider fields are reserved so external calendars can attach later without remodeling.
/// </summary>
public sealed class CalendarBlock
{
    public const string LocalProvider = "local";

    private CalendarBlock(
        CalendarBlockId id,
        TaskId? taskId,
        string? title,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        BlockKind kind,
        RecurrenceRule? recurrence,
        string provider,
        string? externalId,
        int syncState,
        BlockOutcome outcome,
        DateTimeOffset? outcomeRecordedAt,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
    {
        Id = id;
        TaskId = taskId;
        Title = title;
        Date = date;
        StartTime = startTime;
        EndTime = endTime;
        Kind = kind;
        Recurrence = recurrence;
        Provider = provider;
        ExternalId = externalId;
        SyncState = syncState;
        Outcome = outcome;
        OutcomeRecordedAt = outcomeRecordedAt;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    public CalendarBlockId Id { get; }

    public TaskId? TaskId { get; }

    /// <summary>Fixed commitments carry their own title; task blocks display the task's.</summary>
    public string? Title { get; private set; }

    public DateOnly Date { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    public BlockKind Kind { get; }

    /// <summary>Recurring fixed commitments expand into occurrences at query time.</summary>
    public RecurrenceRule? Recurrence { get; private set; }

    public string Provider { get; }

    public string? ExternalId { get; }

    /// <summary>Reserved for future external-calendar synchronization.</summary>
    public int SyncState { get; }

    public BlockOutcome Outcome { get; private set; }

    public DateTimeOffset? OutcomeRecordedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ModifiedAt { get; private set; }

    public TimeSpan Duration => EndTime - StartTime;

    public bool IsExternal => Provider != LocalProvider;

    public static CalendarBlock CreateFixedCommitment(
        string title,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        DateTimeOffset now,
        RecurrenceRule? recurrence = null)
    {
        ValidateTimes(startTime, endTime);
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("A commitment needs a title.");
        }

        return new CalendarBlock(
            CalendarBlockId.New(), null, trimmed, date, startTime, endTime,
            BlockKind.FixedCommitment, recurrence, LocalProvider, null, 0,
            BlockOutcome.None, null, now, now);
    }

    public static CalendarBlock CreateForTask(
        TaskId taskId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        DateTimeOffset now)
    {
        ValidateTimes(startTime, endTime);
        return new CalendarBlock(
            CalendarBlockId.New(), taskId, null, date, startTime, endTime,
            BlockKind.TaskBlock, null, LocalProvider, null, 0,
            BlockOutcome.None, null, now, now);
    }

    public static CalendarBlock Rehydrate(
        CalendarBlockId id,
        TaskId? taskId,
        string? title,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        BlockKind kind,
        RecurrenceRule? recurrence,
        string provider,
        string? externalId,
        int syncState,
        BlockOutcome outcome,
        DateTimeOffset? outcomeRecordedAt,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
        => new(
            id, taskId, title, date, startTime, endTime, kind, recurrence, provider,
            externalId, syncState, outcome, outcomeRecordedAt, createdAt, modifiedAt);

    public void Reschedule(DateOnly date, TimeOnly startTime, TimeOnly endTime, DateTimeOffset now)
    {
        if (IsExternal)
        {
            throw new DomainException("External events are never edited by BeBoosted.");
        }

        ValidateTimes(startTime, endTime);
        Date = date;
        StartTime = startTime;
        EndTime = endTime;
        Touch(now);
    }

    public void Rename(string title, DateTimeOffset now)
    {
        if (Kind != BlockKind.FixedCommitment)
        {
            throw new DomainException("Only fixed commitments carry their own title.");
        }

        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("A commitment needs a title.");
        }

        Title = trimmed;
        Touch(now);
    }

    public void SetRecurrence(RecurrenceRule? recurrence, DateTimeOffset now)
    {
        if (Kind != BlockKind.FixedCommitment)
        {
            throw new DomainException("Only fixed commitments recur.");
        }

        Recurrence = recurrence;
        Touch(now);
    }

    /// <summary>Records what actually happened. Blocks are never auto-completed by elapsing.</summary>
    public void RecordOutcome(BlockOutcome outcome, DateTimeOffset now)
    {
        if (Kind != BlockKind.TaskBlock)
        {
            throw new DomainException("Only task blocks take completion outcomes.");
        }

        if (outcome == BlockOutcome.None)
        {
            throw new DomainException("An outcome must be Done, Needs more time, or Didn't happen.");
        }

        Outcome = outcome;
        OutcomeRecordedAt = now;
        Touch(now);
    }

    /// <summary>Whether an occurrence of this block lands on the given date.</summary>
    public bool OccursOn(DateOnly date)
        => Recurrence is { } recurrence ? recurrence.OccursOn(date, Date) : Date == date;

    private static void ValidateTimes(TimeOnly startTime, TimeOnly endTime)
    {
        if (endTime <= startTime)
        {
            throw new DomainException("A block must end after it starts.");
        }
    }

    private void Touch(DateTimeOffset now) => ModifiedAt = now;
}
