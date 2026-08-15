using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;

namespace BeBoosted.Application.Calendar;

public interface ICalendarBlockRepository
{
    void Add(CalendarBlock block);

    void Update(CalendarBlock block);

    void Delete(CalendarBlockId id);

    CalendarBlock? GetById(CalendarBlockId id);

    IReadOnlyList<CalendarBlock> GetAll();

    /// <summary>Blocks that may produce an occurrence in [from, to]: dated in range or recurring.</summary>
    IReadOnlyList<CalendarBlock> GetCandidatesBetween(DateOnly from, DateOnly to);

    /// <summary>Blocks linked to the given task.</summary>
    IReadOnlyList<CalendarBlock> GetForTask(TaskId taskId);

    /// <summary>Blocks directly linked to the given project (fixed commitments).</summary>
    IReadOnlyList<CalendarBlock> GetForProject(ProjectId projectId);

    /// <summary>Task blocks whose occurrence has fully elapsed but have no recorded outcome.</summary>
    IReadOnlyList<CalendarBlock> GetElapsedWithoutOutcome(DateOnly today, TimeOnly now);

    /// <summary>Task ids that have at least one block with no recorded outcome (pending work).</summary>
    IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks();
}
