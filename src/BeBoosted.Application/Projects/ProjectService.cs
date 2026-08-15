using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
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
    ICommitmentCompletionRepository completions,
    IClock clock,
    IProvenanceInvalidator? provenanceInvalidator = null)
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

    /// <summary>
    /// Deletes the project, its Files/resources (and stored bytes), and unlinks its
    /// tasks and directly linked commitments — the commitments themselves survive.
    /// </summary>
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

        foreach (var block in blocks.GetForProject(id).Where(b => !b.IsExternal))
        {
            block.AssignToProject(null, clock.Now);
            blocks.Update(block);
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

            // A removed source flags everything derived from it as Needs review.
            provenanceInvalidator?.InvalidateForResource(id);
        }
    }

    /// <summary>Edits a note's content: re-indexes it and flags derived items for review.</summary>
    public Resource UpdateNote(ResourceId id, string content)
    {
        var resource = resources.GetById(id)
            ?? throw new DomainException("That resource no longer exists.");
        resource.UpdateNoteContent(content, clock.Now);
        indexer.Index(resource);
        resources.Update(resource);
        provenanceInvalidator?.InvalidateForResource(id);
        return resource;
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

    /// <summary>How many completed rows the scheduled-work area keeps visible.</summary>
    public const int RecentlyCompletedLimit = 5;

    /// <summary>How far a recurring series is expanded around today.</summary>
    private const int RecurringWindowDays = 14;

    /// <summary>
    /// The project's scheduled work: pending-outcome blocks of its tasks (future only,
    /// as before) plus directly linked fixed commitments. Elapsed incomplete
    /// commitments stay listed as Overdue until completed or deleted; completed
    /// occurrences trail the active rows as a restrained recently-completed set.
    /// </summary>
    public IReadOnlyList<ProjectScheduledBlock> GetScheduledBlocks(ProjectId projectId)
    {
        var today = clock.Today;
        var nowTime = TimeOnly.FromDateTime(clock.Now.LocalDateTime);
        var rows = new List<ProjectScheduledBlock>();

        foreach (var task in tasks.GetAll().Where(t => t.ProjectId == projectId))
        {
            foreach (var block in blocks.GetForTask(task.Id))
            {
                if (block.Outcome == BlockOutcome.None && IsUpcoming(block.Date, block.EndTime, today, nowTime))
                {
                    rows.Add(new ProjectScheduledBlock(
                        block, block.Date, task.Title, ProjectBlockState.Upcoming));
                }
            }
        }

        foreach (var block in blocks.GetForProject(projectId))
        {
            rows.AddRange(CommitmentRows(block, today, nowTime));
        }

        var active = rows
            .Where(row => row.State != ProjectBlockState.Done)
            .OrderBy(row => row.Date)
            .ThenBy(row => row.Block.StartTime);
        var completed = rows
            .Where(row => row.State == ProjectBlockState.Done)
            .OrderByDescending(row => row.Date)
            .ThenByDescending(row => row.Block.StartTime)
            .Take(RecentlyCompletedLimit);
        return active.Concat(completed).ToList();
    }

    private IEnumerable<ProjectScheduledBlock> CommitmentRows(
        CalendarBlock block, DateOnly today, TimeOnly nowTime)
    {
        var title = block.Title ?? string.Empty;
        if (block.Recurrence is null)
        {
            // One-offs are never dropped: incomplete elapsed rows turn Overdue.
            var state = completions.Get(block.Id, block.Date) is not null
                ? ProjectBlockState.Done
                : IsUpcoming(block.Date, block.EndTime, today, nowTime)
                    ? ProjectBlockState.Upcoming
                    : ProjectBlockState.Overdue;
            yield return new ProjectScheduledBlock(block, block.Date, title, state);
            yield break;
        }

        // Recurring series stay sparse: the next incomplete upcoming occurrence, recently
        // completed occurrences, and — only when the most recent elapsed occurrence is
        // still incomplete — one Overdue row for it. Completing that occurrence lets the
        // older ones go quietly instead of resurfacing them one by one.
        DateOnly? latestElapsed = null;
        var latestElapsedDone = false;
        DateOnly? nextUpcoming = null;
        for (var date = today.AddDays(-RecurringWindowDays);
             date <= today.AddDays(RecurringWindowDays);
             date = date.AddDays(1))
        {
            if (!block.OccursOn(date))
            {
                continue;
            }

            var isDone = completions.Get(block.Id, date) is not null;
            if (isDone)
            {
                yield return new ProjectScheduledBlock(block, date, title, ProjectBlockState.Done);
            }

            if (IsUpcoming(date, block.EndTime, today, nowTime))
            {
                if (!isDone)
                {
                    nextUpcoming ??= date;
                }
            }
            else
            {
                latestElapsed = date;
                latestElapsedDone = isDone;
            }
        }

        if (latestElapsed is { } overdue && !latestElapsedDone)
        {
            yield return new ProjectScheduledBlock(block, overdue, title, ProjectBlockState.Overdue);
        }

        if (nextUpcoming is { } upcoming)
        {
            yield return new ProjectScheduledBlock(block, upcoming, title, ProjectBlockState.Upcoming);
        }
    }

    private static bool IsUpcoming(DateOnly date, TimeOnly endTime, DateOnly today, TimeOnly now)
        => date > today || (date == today && endTime > now);

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
