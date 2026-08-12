namespace BeBoosted.Domain.Scheduling;

/// <summary>Optional user scheduling constraints for a task.</summary>
public sealed record SchedulingConstraints
{
    public SchedulingConstraints(DateOnly? notBefore = null, TimeOnly? earliestTime = null, TimeOnly? latestTime = null)
    {
        if (earliestTime is { } earliest && latestTime is { } latest && earliest >= latest)
        {
            throw new DomainException("The earliest allowed time must be before the latest allowed time.");
        }

        NotBefore = notBefore;
        EarliestTime = earliestTime;
        LatestTime = latestTime;
    }

    /// <summary>Earliest date the task may be scheduled on.</summary>
    public DateOnly? NotBefore { get; }

    /// <summary>Earliest time of day the task may start.</summary>
    public TimeOnly? EarliestTime { get; }

    /// <summary>Latest time of day the task may end.</summary>
    public TimeOnly? LatestTime { get; }

    public bool IsEmpty => NotBefore is null && EarliestTime is null && LatestTime is null;
}
