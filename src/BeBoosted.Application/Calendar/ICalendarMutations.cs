using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;

namespace BeBoosted.Application.Calendar;

/// <summary>
/// Runs a task-and-schedule mutation as one atomic unit: everything commits, or an
/// exception rolls the whole mutation back and rethrows. Implementations provide
/// repositories bound to that single unit of work — including planning proposals,
/// so deleting a Task can withdraw its proposal blocks in the same transaction.
/// </summary>
public interface ICalendarMutations
{
    void Execute(
        Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
            IPlanningProposalRepository> mutation);

    /// <summary>Convenience overload for mutations that never touch planning proposals.</summary>
    void Execute(
        Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository> mutation)
        => Execute((blocks, completions, tasks, _) => mutation(blocks, completions, tasks));
}
