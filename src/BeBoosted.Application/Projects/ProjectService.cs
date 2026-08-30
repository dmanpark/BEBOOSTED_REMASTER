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
    IProjectMutations mutations,
    IResourceIndexer indexer,
    ITaskRepository tasks,
    ICalendarBlockRepository blocks,
    IOccurrenceCompletionRepository completions,
    IClock clock,
    IProvenanceInvalidator? provenanceInvalidator = null,
    ResourceLayoutReconciler? reconciler = null)
{
    public Project CreateProject(string name)
    {
        var accent = ProjectPalette.ColorFor(projects.GetAll().Count);
        var project = Project.Create(name, accent, clock.Now);

        var preferred = ResourceLayout.Sanitize(project.Name, project.Id.ToString());
        var claimed = SiblingProjectSegments(exclude: null);
        var reserved = storage.ReserveFolderSegment(string.Empty, preferred, claimed);
        project.RelocateTo(reserved, clock.Now);

        projects.Add(project);
        return project;
    }

    /// <summary>Returns the renamed project so callers can refresh from it.</summary>
    public Project RenameProject(ProjectId id, string name)
    {
        var project = Require(id);
        project.Rename(name, clock.Now);

        var preferred = ResourceLayout.Sanitize(project.Name, project.Id.ToString());
        var claimed = SiblingProjectSegments(exclude: id);
        var reserved = storage.ReserveFolderSegment(string.Empty, preferred, claimed, project.FolderSegment);
        project.RelocateTo(reserved, clock.Now);

        projects.Update(project);

        // The rename itself has already succeeded; moving the folder to match is
        // best-effort and never undoes it.
        reconciler?.ReconcileProject(id);
        return project;
    }

    /// <summary>Every other Project's claimed segment, so a reservation can never collide with one.</summary>
    private HashSet<string> SiblingProjectSegments(ProjectId? exclude)
        => projects.GetAll()
            .Where(p => p.Id != exclude)
            .Select(p => p.FolderSegment)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes the project and its Files/resources (and stored bytes), and unlinks
    /// its tasks — the tasks and their schedules survive. Files and resources go
    /// through the foreign-key cascade; the task unlink and the root delete share one
    /// transaction, so a failure leaves the project exactly as it was.
    /// </summary>
    public void DeleteProject(ProjectId id)
    {
        var doomedResources = files.GetForProject(id)
            .SelectMany(f => resources.GetForFile(f.Id))
            .ToList();
        var paths = doomedResources.Select(r => r.StoredPath).OfType<string>().ToList();
        var orphaned = tasks.GetAll().Where(t => t.ProjectId == id).ToList();

        mutations.Execute((projectRepo, _, _, taskRepo) =>
        {
            // A rollback undoes the row but not this in-memory unlink, which is safe
            // only because `orphaned` was read fresh here and is discarded on throw.
            // Do not hoist it to a caller-held list.
            foreach (var task in orphaned)
            {
                task.AssignToProject(null, clock.Now);
                taskRepo.Update(task);
            }

            projectRepo.Delete(id);
        });

        foreach (var path in paths)
        {
            storage.Delete(path);
        }

        foreach (var resource in doomedResources)
        {
            provenanceInvalidator?.InvalidateForResource(resource.Id);
        }
    }

    public ProjectFile CreateFile(ProjectId projectId, string title, string? description)
    {
        var project = Require(projectId);
        var file = ProjectFile.Create(projectId, title, description, clock.Now);

        var preferred = ResourceLayout.Sanitize(file.Title, file.Id.ToString());
        var claimed = SiblingFileSegments(projectId, exclude: null);
        var reserved = storage.ReserveFolderSegment(project.FolderSegment, preferred, claimed);
        file.RelocateTo(reserved, clock.Now);

        files.Add(file);
        return file;
    }

    /// <summary>Returns the renamed File so callers can refresh from it.</summary>
    public ProjectFile RenameFile(ProjectFileId id, string title)
    {
        var file = files.GetById(id)
            ?? throw new DomainException("That file no longer exists.");
        var project = Require(file.ProjectId);
        file.Rename(title, clock.Now);

        var preferred = ResourceLayout.Sanitize(file.Title, file.Id.ToString());
        var claimed = SiblingFileSegments(file.ProjectId, exclude: id);
        var reserved = storage.ReserveFolderSegment(project.FolderSegment, preferred, claimed, file.FolderSegment);
        file.RelocateTo(reserved, clock.Now);

        files.Update(file);

        // Same contract as RenameProject: the rename has already succeeded, and
        // moving the folder to match is best-effort that never undoes it.
        reconciler?.ReconcileProject(file.ProjectId);
        return file;
    }

    /// <summary>Every other File's claimed segment within the same Project.</summary>
    private HashSet<string> SiblingFileSegments(ProjectId projectId, ProjectFileId? exclude)
        => files.GetForProject(projectId)
            .Where(f => f.Id != exclude)
            .Select(f => f.FolderSegment)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes a File. Its resource rows go with it through the foreign-key cascade;
    /// the service collects their bytes first and removes those after the commit.
    /// </summary>
    public void DeleteFile(ProjectFileId id)
    {
        var doomed = resources.GetForFile(id);
        var paths = doomed.Select(r => r.StoredPath).OfType<string>().ToList();

        mutations.Execute((_, fileRepo, _, _) => fileRepo.Delete(id));

        foreach (var path in paths)
        {
            storage.Delete(path);
        }

        foreach (var resource in doomed)
        {
            provenanceInvalidator?.InvalidateForResource(resource.Id);
        }
    }

    public Resource AddLink(ProjectFileId fileId, string title, string url)
        => AddAndIndex(Resource.CreateLink(fileId, title, url, clock.Now));

    public Resource AddNote(ProjectFileId fileId, string title, string content)
        => AddAndIndex(Resource.CreateNote(fileId, title, content, clock.Now));

    /// <summary>Imports a document or image: bytes are copied into app-controlled storage.</summary>
    public Resource ImportFile(ProjectFileId fileId, ResourceKind kind, string sourcePath, string? title = null)
    {
        var file = files.GetById(fileId)
            ?? throw new DomainException("That file no longer exists.");
        var project = Require(file.ProjectId);
        var originalName = Path.GetFileName(sourcePath);
        var id = ResourceId.New();
        var storedPath = storage.Store(
            ResourceLayout.FolderFor(project, file),
            ResourceLayout.FileNameFor(originalName, id.ToString()),
            sourcePath);
        var resource = Resource.Rehydrate(
            id, fileId, kind,
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(originalName) : title.Trim(),
            null, null, originalName, storedPath, clock.Now, ResourceIndexState.Pending, clock.Now);
        return AddAndIndex(resource);
    }

    /// <summary>
    /// Retitles a resource. A stored document's on-disk name comes from its original
    /// file name, not its title, so this never moves bytes and needs no reconcile.
    /// </summary>
    public Resource RenameResource(ResourceId id, string title)
    {
        var resource = resources.GetById(id)
            ?? throw new DomainException("That resource no longer exists.");
        resource.Rename(title, clock.Now);
        resources.Update(resource);
        return resource;
    }

    /// <summary>
    /// Removes one resource. The row goes first, inside a transaction; the bytes go
    /// only once that commits. A failure therefore orphans bytes — invisible and
    /// tolerated by the reconciler — rather than leaving a row pointing at a file
    /// that no longer exists.
    /// </summary>
    public void DeleteResource(ResourceId id)
    {
        if (resources.GetById(id) is not { } resource)
        {
            return;
        }

        mutations.Execute((_, _, resourceRepo, _) => resourceRepo.Delete(id));

        if (resource.StoredPath is { } storedPath)
        {
            storage.Delete(storedPath);
        }

        // A removed source flags everything derived from it as Needs review.
        provenanceInvalidator?.InvalidateForResource(id);
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
    /// The project's scheduled work, resolved entirely through its tasks' sessions.
    /// One-off sessions are never dropped: incomplete elapsed rows turn Overdue until
    /// resolved; completed occurrences trail the active rows as a restrained
    /// recently-completed set. Repeating sessions expand sparsely around today.
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
                rows.AddRange(SessionRows(block, task, today, nowTime));
            }
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

    private IEnumerable<ProjectScheduledBlock> SessionRows(
        CalendarBlock block, Domain.Tasks.TaskItem task, DateOnly today, TimeOnly nowTime)
    {
        var title = task.Title;
        if (block.Recurrence is null)
        {
            // Resolved-but-not-done outcomes (Needs more time, Didn't happen) leave the
            // schedule quietly; the task itself returns to the open lists instead.
            if (block.Outcome is BlockOutcome.NeedsMoreTime or BlockOutcome.DidntHappen)
            {
                yield break;
            }

            var state = block.Outcome == BlockOutcome.Done || task.IsCompleted
                ? ProjectBlockState.Done
                : IsUpcoming(block.Date, block.EndTime, today, nowTime)
                    ? ProjectBlockState.Upcoming
                    : ProjectBlockState.Overdue;
            yield return new ProjectScheduledBlock(block, block.Date, title, state);
            yield break;
        }

        // Repeating sessions stay sparse: the next incomplete upcoming occurrence,
        // recently completed occurrences, and — only when the most recent elapsed
        // occurrence is still incomplete — one Overdue row for it. Completing that
        // occurrence lets the older ones go quietly instead of resurfacing them.
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
