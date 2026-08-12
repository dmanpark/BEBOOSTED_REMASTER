using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Application.Projects;

/// <summary>Project, File, and resource use cases.</summary>
public sealed class ProjectService(
    IProjectRepository projects,
    IProjectFileRepository files,
    IResourceRepository resources,
    IResourceStorage storage,
    IResourceIndexer indexer,
    ITaskRepository tasks,
    ICalendarBlockRepository blocks,
    IClock clock)
{
    public Project CreateProject(string name)
    {
        var accent = ProjectPalette.ColorFor(projects.GetAll().Count);
        var project = Project.Create(name, accent, clock.Now);
        projects.Add(project);
        return project;
    }

    public void RenameProject(ProjectId id, string name)
    {
        var project = Require(id);
        project.Rename(name, clock.Now);
        projects.Update(project);
    }

    /// <summary>Deletes the project, its Files/resources (and stored bytes), and unlinks its tasks.</summary>
    public void DeleteProject(ProjectId id)
    {
        foreach (var file in files.GetForProject(id))
        {
            DeleteFile(file.Id);
        }

        foreach (var task in tasks.GetAll().Where(t => t.ProjectId == id))
        {
            task.AssignToProject(null, clock.Now);
            tasks.Update(task);
        }

        projects.Delete(id);
    }

    public ProjectFile CreateFile(ProjectId projectId, string title, string? description)
    {
        _ = Require(projectId);
        var file = ProjectFile.Create(projectId, title, description, clock.Now);
        files.Add(file);
        return file;
    }

    public void DeleteFile(ProjectFileId id)
    {
        foreach (var resource in resources.GetForFile(id))
        {
            DeleteResource(resource.Id);
        }

        files.Delete(id);
    }

    public Resource AddLink(ProjectFileId fileId, string title, string url)
        => AddAndIndex(Resource.CreateLink(fileId, title, url, clock.Now));

    public Resource AddNote(ProjectFileId fileId, string title, string content)
        => AddAndIndex(Resource.CreateNote(fileId, title, content, clock.Now));

    /// <summary>Imports a document or image: bytes are copied into app-controlled storage.</summary>
    public Resource ImportFile(ProjectFileId fileId, ResourceKind kind, string sourcePath, string? title = null)
    {
        var originalName = Path.GetFileName(sourcePath);
        var id = ResourceId.New();
        var storedPath = storage.Store(id, sourcePath);
        var resource = Resource.Rehydrate(
            id, fileId, kind,
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(originalName) : title.Trim(),
            null, null, originalName, storedPath, clock.Now, ResourceIndexState.Pending, clock.Now);
        return AddAndIndex(resource);
    }

    public void DeleteResource(ResourceId id)
    {
        if (resources.GetById(id) is { } resource)
        {
            if (resource.StoredPath is { } storedPath)
            {
                storage.Delete(storedPath);
            }

            resources.Delete(id);
        }
    }

    public IReadOnlyList<Resource> GetResources(ProjectFileId fileId) => resources.GetForFile(fileId);

    /// <summary>Absolute path of a stored document/image, or null for links/notes.</summary>
    public string? ResolveStoredPath(Resource resource)
        => resource.StoredPath is { } storedPath ? storage.ResolvePath(storedPath) : null;

    public int CountResources(ProjectFileId fileId) => resources.CountForFile(fileId);

    /// <summary>Open and recently completed tasks for the project view.</summary>
    public (IReadOnlyList<TaskItem> Open, IReadOnlyList<TaskItem> RecentlyCompleted) GetProjectTasks(
        ProjectId projectId, int recentCount = 3)
    {
        var all = tasks.GetAll().Where(t => t.ProjectId == projectId).ToList();
        var open = all.Where(t => !t.IsCompleted).OrderBy(t => t.CreatedAt).ToList();
        var recent = all.Where(t => t.IsCompleted)
            .OrderByDescending(t => t.CompletedAt)
            .Take(recentCount)
            .ToList();
        return (open, recent);
    }

    /// <summary>Upcoming (pending-outcome) blocks for the project's tasks, soonest first.</summary>
    public IReadOnlyList<(CalendarBlock Block, TaskItem Task)> GetUpcomingBlocks(ProjectId projectId)
    {
        var today = clock.Today;
        var nowTime = TimeOnly.FromDateTime(clock.Now.LocalDateTime);
        var projectTasks = tasks.GetAll().Where(t => t.ProjectId == projectId).ToDictionary(t => t.Id);
        var upcoming = new List<(CalendarBlock, TaskItem)>();
        foreach (var (taskId, task) in projectTasks)
        {
            foreach (var block in blocks.GetForTask(taskId))
            {
                var isFuture = block.Date > today || (block.Date == today && block.EndTime > nowTime);
                if (block.Outcome == BlockOutcome.None && isFuture)
                {
                    upcoming.Add((block, task));
                }
            }
        }

        return upcoming.OrderBy(pair => pair.Item1.Date).ThenBy(pair => pair.Item1.StartTime).ToList();
    }

    private Resource AddAndIndex(Resource resource)
    {
        resources.Add(resource);
        indexer.Index(resource);
        resources.Update(resource);
        return resource;
    }

    private Project Require(ProjectId id)
        => projects.GetById(id) ?? throw new DomainException("That project no longer exists.");
}
