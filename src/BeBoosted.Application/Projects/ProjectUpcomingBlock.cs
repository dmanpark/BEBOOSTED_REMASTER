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
/// One row in a project's scheduled-work area: a task-backed block (titled by its
/// task) or a directly linked fixed commitment (titled by the block itself).
/// <paramref name="Date"/> is the occurrence date — for recurring commitments a
/// concrete occurrence, not the series anchor. Elapsed incomplete commitments stay
/// listed (Overdue) until they are completed or deleted.
/// </summary>
public sealed record ProjectScheduledBlock(
    CalendarBlock Block, DateOnly Date, string Title, ProjectBlockState State);
