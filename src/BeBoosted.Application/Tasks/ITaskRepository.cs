using BeBoosted.Domain;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Application.Tasks;

/// <summary>
/// Task persistence port. Synchronous by design — the store is a local SQLite file and
/// operations complete in microseconds; keeping the port synchronous keeps ViewModels honest.
/// </summary>
public interface ITaskRepository
{
    void Add(TaskItem task);

    void Update(TaskItem task);

    void Delete(TaskId id);

    TaskItem? GetById(TaskId id);

    IReadOnlyList<TaskItem> GetAll();

    /// <summary>Open (incomplete) tasks in capture order.</summary>
    IReadOnlyList<TaskItem> GetOpen();
}
