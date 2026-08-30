using BeBoosted.Application.Tasks;

namespace BeBoosted.Application.Projects;

/// <summary>
/// Runs a project mutation as one atomic unit: everything commits, or an exception
/// rolls the whole mutation back and rethrows. Implementations provide repositories
/// bound to that single unit of work, so deleting a project can remove its Files,
/// resources, and task links together or not at all.
/// </summary>
public interface IProjectMutations
{
    void Execute(
        Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository> mutation);
}
