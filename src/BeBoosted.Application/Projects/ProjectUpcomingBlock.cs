using BeBoosted.Domain.Calendar;

namespace BeBoosted.Application.Projects;

public enum ProjectBlockState
{
    Upcoming = 0,

    /// <summary>The occurrence's end time passed but it was never completed.</summary>
    Overdue = 1,

    Done = 2,
}

/// <summary>
/// One row in a project's scheduled-work area: a session of one of the project's
/// tasks, titled by the task. <paramref name="Date"/> is the occurrence date — for
/// repeating sessions a concrete occurrence, not the series anchor. Elapsed
/// incomplete sessions stay listed (Overdue) until they are resolved or deleted.
/// </summary>
public sealed record ProjectScheduledBlock(
    CalendarBlock Block, DateOnly Date, string Title, ProjectBlockState State);
