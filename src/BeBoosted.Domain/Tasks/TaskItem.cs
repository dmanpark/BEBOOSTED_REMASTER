using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Domain.Tasks;

/// <summary>
/// A unit of work. Named TaskItem to avoid colliding with System.Threading.Tasks.Task.
/// Scheduling state is derived from calendar blocks referencing the task, not stored here.
/// </summary>
public sealed class TaskItem
{
    private TaskItem(
        TaskId id,
        string title,
        TimeSpan? estimatedDuration,
        DateOnly? deadline,
        ProjectId? projectId,
        SchedulingConstraints? constraints,
        RecurrenceRule? recurrence,
        TaskOrigin origin,
        AiProvenanceId? provenanceId,
        bool isCompleted,
        DateTimeOffset? completedAt,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
    {
        Id = id;
        Title = title;
        EstimatedDuration = estimatedDuration;
        Deadline = deadline;
        ProjectId = projectId;
        Constraints = constraints;
        Recurrence = recurrence;
        Origin = origin;
        ProvenanceId = provenanceId;
        IsCompleted = isCompleted;
        CompletedAt = completedAt;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    public TaskId Id { get; }

    public string Title { get; private set; }

    public TimeSpan? EstimatedDuration { get; private set; }

    public DateOnly? Deadline { get; private set; }

    public ProjectId? ProjectId { get; private set; }

    public SchedulingConstraints? Constraints { get; private set; }

    public RecurrenceRule? Recurrence { get; private set; }

    public TaskOrigin Origin { get; }

    public AiProvenanceId? ProvenanceId { get; private set; }

    public bool IsCompleted { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ModifiedAt { get; private set; }

    public static TaskItem Create(
        string title,
        DateTimeOffset now,
        TaskOrigin origin = TaskOrigin.User,
        TimeSpan? estimatedDuration = null,
        DateOnly? deadline = null,
        ProjectId? projectId = null,
        SchedulingConstraints? constraints = null,
        RecurrenceRule? recurrence = null,
        AiProvenanceId? provenanceId = null)
    {
        var task = new TaskItem(
            TaskId.New(),
            ValidateTitle(title),
            null,
            deadline,
            projectId,
            constraints,
            recurrence,
            origin,
            provenanceId,
            isCompleted: false,
            completedAt: null,
            createdAt: now,
            modifiedAt: now);
        if (estimatedDuration is { } duration)
        {
            task.SetEstimatedDuration(duration, now);
        }

        return task;
    }

    /// <summary>Reconstructs a task from persisted state without validation side effects.</summary>
    public static TaskItem Rehydrate(
        TaskId id,
        string title,
        TimeSpan? estimatedDuration,
        DateOnly? deadline,
        ProjectId? projectId,
        SchedulingConstraints? constraints,
        RecurrenceRule? recurrence,
        TaskOrigin origin,
        AiProvenanceId? provenanceId,
        bool isCompleted,
        DateTimeOffset? completedAt,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
        => new(
            id, title, estimatedDuration, deadline, projectId, constraints, recurrence,
            origin, provenanceId, isCompleted, completedAt, createdAt, modifiedAt);

    public void Rename(string title, DateTimeOffset now)
    {
        Title = ValidateTitle(title);
        Touch(now);
    }

    public void SetEstimatedDuration(TimeSpan? duration, DateTimeOffset now)
    {
        if (duration is { } value && value <= TimeSpan.Zero)
        {
            throw new DomainException("An estimated duration must be positive.");
        }

        EstimatedDuration = duration;
        Touch(now);
    }

    public void SetDeadline(DateOnly? deadline, DateTimeOffset now)
    {
        Deadline = deadline;
        Touch(now);
    }

    public void AssignToProject(ProjectId? projectId, DateTimeOffset now)
    {
        ProjectId = projectId;
        Touch(now);
    }

    public void SetConstraints(SchedulingConstraints? constraints, DateTimeOffset now)
    {
        Constraints = constraints;
        Touch(now);
    }

    public void SetRecurrence(RecurrenceRule? recurrence, DateTimeOffset now)
    {
        Recurrence = recurrence;
        Touch(now);
    }

    public void Complete(DateTimeOffset now)
    {
        if (IsCompleted)
        {
            return;
        }

        IsCompleted = true;
        CompletedAt = now;
        Touch(now);
    }

    public void Reopen(DateTimeOffset now)
    {
        if (!IsCompleted)
        {
            return;
        }

        IsCompleted = false;
        CompletedAt = null;
        Touch(now);
    }

    /// <summary>
    /// A block ended with "Needs more time": the remaining duration becomes the new estimate
    /// and the task stays open, returning to the Inbox for replanning.
    /// </summary>
    public void RecordNeedsMoreTime(TimeSpan remaining, DateTimeOffset now)
    {
        if (IsCompleted)
        {
            throw new DomainException("A completed task cannot need more time.");
        }

        if (remaining <= TimeSpan.Zero)
        {
            throw new DomainException("The remaining duration must be positive.");
        }

        EstimatedDuration = remaining;
        Touch(now);
    }

    private static string ValidateTitle(string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("A task needs a title.");
        }

        return trimmed;
    }

    private void Touch(DateTimeOffset now) => ModifiedAt = now;
}
