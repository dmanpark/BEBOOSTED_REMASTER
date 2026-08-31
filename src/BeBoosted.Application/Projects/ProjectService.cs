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
    IResourceGroupRepository groups,
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

        mutations.Execute((projectRepo, _, _, taskRepo, _) =>
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
            AfterCommit(() => storage.Delete(path));
        }

        foreach (var resource in doomedResources)
        {
            AfterCommit(() => provenanceInvalidator?.InvalidateForResource(resource.Id));
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

        // Same contract as RenameProject: the rename has already succeeded, and moving
        // the folder to match is best-effort that never undoes it.
        //
        // Except when the Project itself was never claimed — a row the backfill skipped.
        // Reconciling walks this Project's OTHER Files, which are still both-empty, and
        // the reconciler's guard lets that shape through by design because it is the pure
        // pre-0012 state. Their folder would resolve to "" and their documents would be
        // moved into the resources root. Unlike RenameProject, which claims a segment
        // before it reconciles, nothing here has made the Project safe to sweep.
        //
        // Deferring converges: this File's documents stay where they are, and once the
        // backfill claims the Project a later reconcile moves them to their real folder in
        // one step.
        if (project.FolderSegment.Length > 0)
        {
            reconciler?.ReconcileProject(file.ProjectId);
        }

        return file;
    }

    /// <summary>
    /// Runs one side effect that follows a committed transaction, and swallows anything it
    /// throws. The rows are already gone and nothing here can undo that, so a failure can
    /// only do two things, both worse than the failure itself: skip the side effects after
    /// it — including the provenance invalidation that flags derived items as needing
    /// review, leaving them citing sources that no longer exist — and escape to a caller
    /// that will report a delete which fully succeeded as a failure. An orphaned file on
    /// disk is the lesser outcome, and the reconciler already tolerates one.
    ///
    /// Per side effect, deliberately: one path that refuses must not cost the next one.
    ///
    /// This cannot be pushed down into <see cref="IResourceStorage.Delete"/> or
    /// <see cref="IProvenanceInvalidator"/>. Both are interfaces, so any implementation may
    /// throw; hardening the ones shipped today would leave the guarantee resting on every
    /// future implementation remembering. The isolation belongs where the sequencing is.
    ///
    /// Nothing before the commit goes through here — <c>mutations.Execute</c> must still
    /// throw and still roll back.
    /// </summary>
    private static void AfterCommit(Action sideEffect)
    {
        try
        {
            sideEffect();
        }
        catch (Exception)
        {
            // Deliberately unreported at this layer: the service takes no logger, and the
            // delete it belongs to has already succeeded.
        }
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

        mutations.Execute((_, fileRepo, _, _, _) => fileRepo.Delete(id));

        foreach (var path in paths)
        {
            AfterCommit(() => storage.Delete(path));
        }

        foreach (var resource in doomed)
        {
            AfterCommit(() => provenanceInvalidator?.InvalidateForResource(resource.Id));
        }
    }

    /// <summary>
    /// The Project and File a group may be claimed under, refusing while either still
    /// holds the empty sentinel. Nothing else stops it: <c>Path.Combine</c> swallows an
    /// empty part, so an unclaimed File would put a group's directory straight into the
    /// Project folder and an unclaimed Project would put it in the resources root — a
    /// folder no part of the layout agrees with, recorded on the row for good.
    ///
    /// Deliberately group-specific. <see cref="ImportFile"/> keeps storing a document
    /// somewhere findable under an unclaimed parent, because refusing an import outright
    /// is a worse answer than a folder the next reconcile tidies. A group's segment is
    /// persisted rather than recomputed, so the same tolerance would be permanent.
    /// </summary>
    private (Project Project, ProjectFile File) RequireClaimedGroupParent(ProjectFileId fileId)
    {
        var file = files.GetById(fileId)
            ?? throw new DomainException("That file no longer exists.");
        var project = Require(file.ProjectId);
        if (project.FolderSegment.Length == 0 || file.FolderSegment.Length == 0)
        {
            throw new DomainException(
                "This File's storage folders are not ready. Reopen the app and try again.");
        }

        return (project, file);
    }

    public IReadOnlyList<ResourceGroup> GetGroups(ProjectFileId fileId)
        => groups.GetForFile(fileId);

    public ResourceGroup CreateGroup(ProjectFileId fileId, string title)
    {
        var (project, file) = RequireClaimedGroupParent(fileId);
        var siblings = groups.GetForFile(fileId);
        var order = siblings.Count == 0 ? 0 : checked(siblings.Max(g => g.SortOrder) + 1);
        var group = ResourceGroup.Create(fileId, title, order, clock.Now);
        var claimed = siblings.Select(g => g.FolderSegment).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Two-argument FolderFor on purpose: this is the group's PARENT folder, and the
        // group's own directory is what the reservation is about to create inside it.
        // FolderFor's group parameter is optional and omitting it silently yields the
        // loose folder, so every call site has to be read deliberately.
        var segment = storage.ReserveFolderSegment(ResourceLayout.FolderFor(project, file),
            ResourceLayout.Sanitize(group.Title, group.Id.ToString()), claimed);
        group.RelocateTo(segment, clock.Now);
        groups.Add(group);
        return group;
    }

    public ResourceGroup RenameGroup(ResourceGroupId id, string title)
    {
        var group = groups.GetById(id) ?? throw new DomainException("That group no longer exists.");
        var (project, file) = RequireClaimedGroupParent(group.FileId);

        // Read before RelocateTo overwrites it. Reservation creates the directory it
        // returns, so without this the group's OWN folder reads as occupied and a
        // case-only or otherwise sanitization-equivalent rename is displaced to " (2)" —
        // moving every byte in the group to say the same thing a different way.
        var owned = group.FolderSegment;
        group.Rename(title, clock.Now);
        // Only the OTHER groups' segments; this one's is passed as `owned` instead.
        var claimed = groups.GetForFile(file.Id).Where(g => g.Id != id)
            .Select(g => g.FolderSegment).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Two-argument FolderFor again: the parent this group's directory sits in.
        var segment = storage.ReserveFolderSegment(ResourceLayout.FolderFor(project, file),
            ResourceLayout.Sanitize(group.Title, group.Id.ToString()), claimed, owned);
        group.RelocateTo(segment, clock.Now);
        groups.Update(group);

        // AfterCommit, deliberately unlike RenameFile/RenameProject: the row write has
        // already committed, so a throw from the reconcile cannot undo the rename — it can
        // only misreport an operation that fully succeeded. Recorded in the phase-1 plan
        // under "Two execution decisions ruled before Task 1"; aligning RenameFile with
        // this is a follow-up, not part of this branch.
        AfterCommit(() => reconciler?.ReconcileProject(project.Id));
        return group;
    }

    public void MoveResourceToGroup(ResourceId id, ResourceGroupId? groupId)
    {
        var resource = resources.GetById(id)
            ?? throw new DomainException("That resource no longer exists.");
        var (project, _) = RequireClaimedGroupParent(resource.FileId);

        // Only the service can see both sides. Resource.MoveToGroup takes a bare id, and
        // resources.group_id requires only that the group row exist — not that it belong to
        // this resource's File — so a cross-File assignment commits happily and is then
        // silent and permanent: the reconciler refuses to resolve a group from another
        // File, so this document is skipped on every run and its bytes never move again.
        if (groupId is { } target)
        {
            var group = groups.GetById(target)
                ?? throw new DomainException("That group no longer exists.");
            if (group.FileId != resource.FileId)
            {
                throw new DomainException("Resources can only move to groups in the same File.");
            }
        }

        if (resource.GroupId == groupId)
        {
            return;
        }

        resource.MoveToGroup(groupId, clock.Now);
        resources.Update(resource);

        // No indexer and no provenance invalidation, deliberately: filing changes neither
        // the bytes, the extracted text, nor the resource's identity, so anything derived
        // from it still cites exactly what it cited before.
        //
        // The single-row membership write is atomic on its own and has already committed.
        // Moving the bytes to match is best-effort from here: a failure leaves the resource
        // filed, at its old and still-openable path, for the next reconcile to converge —
        // reporting a filing that genuinely happened as failed would be the worse answer.
        AfterCommit(() => reconciler?.ReconcileProject(project.Id));
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

        // The last place an unclaimed parent still reaches FolderFor. If the startup
        // backfill skipped this Project, its segment is empty and the document lands in
        // the resources root instead of a named folder. That is untidy, not lossy: Store
        // disambiguates on collision, the row records where the bytes actually went, and
        // the reconcile that runs once the Project is claimed relocates it. Guarding here
        // would mean refusing the import outright, which is a worse answer than storing it
        // somewhere findable.
        var storedPath = storage.Store(
            ResourceLayout.FolderFor(project, file),
            ResourceLayout.FileNameFor(originalName, id.ToString()),
            sourcePath);
        var resource = Resource.Rehydrate(
            id, fileId, kind,
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(originalName) : title.Trim(),
            null, null, originalName, storedPath, clock.Now, ResourceIndexState.Pending, clock.Now,
            groupId: null); // A newly imported resource arrives loose in the File.
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

        mutations.Execute((_, _, resourceRepo, _, _) => resourceRepo.Delete(id));

        if (resource.StoredPath is { } storedPath)
        {
            AfterCommit(() => storage.Delete(storedPath));
        }

        // A removed source flags everything derived from it as Needs review. Isolated
        // from the byte delete above so a file that refuses to go cannot leave derived
        // items citing a source that is already gone.
        AfterCommit(() => provenanceInvalidator?.InvalidateForResource(id));
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
