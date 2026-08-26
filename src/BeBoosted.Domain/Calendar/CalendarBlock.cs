using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Domain.Calendar;

public enum BlockKind
{
    /// <summary>A read-only event imported or synced from an external calendar provider.</summary>
    ExternalEvent = 0,

    /// <summary>A schedule session of a local Task — the only user-created block shape.</summary>
    TaskSession = 1,
}

public enum BlockOutcome
{
    None = 0,
    Done = 1,
    NeedsMoreTime = 2,
    DidntHappen = 3,
}

/// <summary>
/// One item on the calendar: a Task's schedule session (always task-backed; the Task
/// owns title, project, deadline, and completion) or a read-only external event (which
/// keeps its provider identifiers and its own title). Proposals never live here — they
/// stay in a PlanningProposal until approved. Blocks are wall-clock local and never
/// cross midnight; recurring sessions expand into occurrences at query time.
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

    /// <summary>The owning Task for sessions; always null for external events.</summary>
    public TaskId? TaskId { get; }

    /// <summary>
    /// External events carry their own title. A session may carry an optional one of
    /// its own; when null it displays its Task's title.
    /// </summary>
    public string? Title { get; private set; }

    public DateOnly Date { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    public BlockKind Kind { get; }

    /// <summary>A repeating session expands into occurrences at query time.</summary>
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

    /// <summary>Creates a Task's schedule session — the only creatable local block.</summary>
    public static CalendarBlock CreateTaskSession(
        TaskId taskId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        DateTimeOffset now,
        RecurrenceRule? recurrence = null,
        string? title = null)
    {
        ValidateTimes(startTime, endTime);
        return new CalendarBlock(
            CalendarBlockId.New(), taskId, NormalizeTitle(title), date, startTime, endTime,
            BlockKind.TaskSession, recurrence, LocalProvider, null, 0,
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
        EnsureLocalSession();
        ValidateTimes(startTime, endTime);
        Date = date;
        StartTime = startTime;
        EndTime = endTime;
        Touch(now);
    }

    /// <summary>Sets or clears the session's repeating schedule (whole series).</summary>
    public void SetRecurrence(RecurrenceRule? recurrence, DateTimeOffset now)
    {
        EnsureLocalSession();
        Recurrence = recurrence;
        Touch(now);
    }

    /// <summary>
    /// Records what actually happened in a one-off session. Repeating sessions complete
    /// per occurrence instead, and blocks are never auto-completed by elapsing.
    /// </summary>
    public void RecordOutcome(BlockOutcome outcome, DateTimeOffset now)
    {
        EnsureLocalSession();
        if (Recurrence is not null)
        {
            throw new DomainException("A repeating session completes per occurrence, not by outcome.");
        }

        if (outcome == BlockOutcome.None)
        {
            throw new DomainException("An outcome must be Done, Needs more time, or Didn't happen.");
        }

        Outcome = outcome;
        OutcomeRecordedAt = now;
        Touch(now);
    }

    /// <summary>Clears a one-off session's outcome when its Task is reopened.</summary>
    public void ClearOutcome(DateTimeOffset now)
    {
        EnsureLocalSession();
        Outcome = BlockOutcome.None;
        OutcomeRecordedAt = null;
        Touch(now);
    }

    /// <summary>
    /// Names this session for the day it covers ("Jane Eyre 1-10"). Blank clears the
    /// name, and the row falls back to the Task's title.
    /// </summary>
    public void Retitle(string? title, DateTimeOffset now)
    {
        EnsureLocalSession();
        Title = NormalizeTitle(title);
        Touch(now);
    }

    private static string? NormalizeTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? null : title.Trim();

    /// <summary>Whether an occurrence of this block lands on the given date.</summary>
    public bool OccursOn(DateOnly date)
        => Recurrence is { } recurrence ? recurrence.OccursOn(date, Date) : Date == date;

    /// <summary>
    /// Validates that the occurrence on the given date can be completed or reopened:
    /// repeating local sessions only, and only on dates the session actually occurs.
    /// </summary>
    public void EnsureOccurrenceCompletable(DateOnly occurrenceDate)
    {
        EnsureLocalSession();
        if (Recurrence is null)
        {
            throw new DomainException(
                "A one-off session records an outcome, not an occurrence completion.");
        }

        if (!OccursOn(occurrenceDate))
        {
            throw new DomainException("That repeating task has no occurrence on that date.");
        }
    }

    private static void ValidateTimes(TimeOnly startTime, TimeOnly endTime)
    {
        if (endTime <= startTime)
        {
            throw new DomainException("A block must end after it starts.");
        }
    }

    private void EnsureLocalSession()
    {
        if (IsExternal || Kind != BlockKind.TaskSession)
        {
            throw new DomainException("External events are never edited by BeBoosted.");
        }
    }

    private void Touch(DateTimeOffset now) => ModifiedAt = now;
}
