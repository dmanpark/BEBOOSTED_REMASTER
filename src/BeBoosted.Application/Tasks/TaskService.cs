using BeBoosted.Application.Abstractions;
using BeBoosted.Domain;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Application.Tasks;

/// <summary>Task capture, editing, and completion use cases.</summary>
public sealed class TaskService(ITaskRepository repository, IClock clock)
{
    public TaskItem Capture(
        string title,
        TimeSpan? estimatedDuration = null,
        DateOnly? deadline = null,
        ProjectId? projectId = null)
    {
        var task = TaskItem.Create(
            title,
            clock.Now,
            estimatedDuration: estimatedDuration,
            deadline: deadline,
            projectId: projectId);
        repository.Add(task);
        return task;
    }

    public TaskItem UpdateDetails(
        TaskId id, string title, TimeSpan? estimatedDuration, DateOnly? deadline, ProjectId? projectId = null)
    {
        var task = Require(id);
        task.Rename(title, clock.Now);
        task.SetEstimatedDuration(estimatedDuration, clock.Now);
        task.SetDeadline(deadline, clock.Now);
        task.AssignToProject(projectId, clock.Now);
        repository.Update(task);
        return task;
    }

    public TaskItem Complete(TaskId id)
    {
        var task = Require(id);
        task.Complete(clock.Now);
        repository.Update(task);
        return task;
    }

    public TaskItem Reopen(TaskId id)
    {
        var task = Require(id);
        task.Reopen(clock.Now);
        repository.Update(task);
        return task;
    }

    public TaskItem RecordNeedsMoreTime(TaskId id, TimeSpan remaining)
    {
        var task = Require(id);
        task.RecordNeedsMoreTime(remaining, clock.Now);
        repository.Update(task);
        return task;
    }

    public void Delete(TaskId id) => repository.Delete(id);

    private TaskItem Require(TaskId id)
        => repository.GetById(id) ?? throw new DomainException($"Task {id} no longer exists.");
}
