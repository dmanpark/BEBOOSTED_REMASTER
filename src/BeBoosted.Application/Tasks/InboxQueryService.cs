using BeBoosted.Application.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Application.Tasks;

/// <summary>
/// The universal Inbox: open tasks with no pending calendar block. Tasks with an elapsed
/// block that lacks an outcome stay out of the Inbox until the user resolves the block
/// through the quiet review notice — elapsed time never implies completed work.
/// </summary>
public sealed class InboxQueryService(ITaskRepository tasks, ICalendarBlockRepository blocks)
{
    public IReadOnlyList<TaskItem> GetInboxTasks()
    {
        var pending = blocks.GetTaskIdsWithPendingBlocks();
        return tasks.GetOpen().Where(task => !pending.Contains(task.Id)).ToList();
    }
}
