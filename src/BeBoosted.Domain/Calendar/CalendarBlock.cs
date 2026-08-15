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
        ProjectId? projectId,
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
        ProjectId = projectId;
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

    /// <summary>
    /// Direct project link for fixed commitments. Task blocks derive their project from
    /// the backing task instead — never duplicate that ownership here.
    /// </summary>
    public ProjectId? ProjectId { get; private set; }

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
        RecurrenceRule? recurrence = null,
        ProjectId? projectId = null)
    {
        ValidateTimes(startTime, endTime);
        var trimmed = ValidateTitle(title);
        return new CalendarBlock(
            CalendarBlockId.New(), null, projectId, trimmed, date, startTime, endTime,
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
            CalendarBlockId.New(), taskId, null, null, date, startTime, endTime,
            BlockKind.TaskBlock, null, LocalProvider, null, 0,
            BlockOutcome.None, null, now, now);
    }

    public static CalendarBlock Rehydrate(
        CalendarBlockId id,
        TaskId? taskId,
        ProjectId? projectId,
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
            id, taskId, projectId, title, date, startTime, endTime, kind, recurrence, provider,
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
        EnsureLocalFixedCommitment();
        Title = ValidateTitle(title);
        Touch(now);
    }

    /// <summary>Links a local fixed commitment to a project (or clears the link).</summary>
    public void AssignToProject(ProjectId? projectId, DateTimeOffset now)
    {
        EnsureLocalFixedCommitment();
        ProjectId = projectId;
        Touch(now);
    }

    /// <summary>
    /// Edits a local fixed commitment in one step. Validates everything before
    /// mutating anything, so a rejected edit leaves the block untouched.
    /// </summary>
    public void UpdateCommitment(
        string title,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        RecurrenceRule? recurrence,
        ProjectId? projectId,
        DateTimeOffset now)
    {
        EnsureLocalFixedCommitment();
        ValidateTimes(startTime, endTime);
        var trimmed = ValidateTitle(title);

        Title = trimmed;
        Date = date;
        StartTime = startTime;
        EndTime = endTime;
        Recurrence = recurrence;
        ProjectId = projectId;
        Touch(now);
    }

    public void SetRecurrence(RecurrenceRule? recurrence, DateTimeOffset now)
    {
        EnsureLocalFixedCommitment();
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

    /// <summary>
    /// Validates that the occurrence on the given date can be completed or reopened:
    /// local fixed commitments only, and only on dates the block actually occurs.
    /// </summary>
    public void EnsureOccurrenceCompletable(DateOnly occurrenceDate)
    {
        EnsureLocalFixedCommitment();
        if (!OccursOn(occurrenceDate))
        {
            throw new DomainException("That commitment has no occurrence on that date.");
        }
    }

    private static void ValidateTimes(TimeOnly startTime, TimeOnly endTime)
    {
        if (endTime <= startTime)
        {
            throw new DomainException("A block must end after it starts.");
        }
    }

    private static string ValidateTitle(string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        return trimmed.Length == 0
            ? throw new DomainException("A commitment needs a title.")
            : trimmed;
    }

    private void EnsureLocalFixedCommitment()
    {
        if (Kind != BlockKind.FixedCommitment)
        {
            throw new DomainException("Only fixed commitments can be edited this way.");
        }

        if (IsExternal)
        {
            throw new DomainException("External events are never edited by BeBoosted.");
        }
    }

    private void Touch(DateTimeOffset now) => ModifiedAt = now;
}
