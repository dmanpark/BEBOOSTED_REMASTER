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
    /// Runs one side effect that follows a committed write, and swallows anything it throws.
    /// The write has already landed and nothing here can undo it, so a failure can only do
    /// two things, both worse than the failure itself: skip the side effects after it, and
    /// escape to a caller that will report an operation which fully succeeded as a failure.
    ///
    /// Three kinds of side effect run through here, and the reasoning is the same for each:
    /// <list type="bullet">
    /// <item>Deleting a removed resource's bytes. The row is gone; an orphaned file on disk
    /// is the lesser outcome, and the reconciler already tolerates one.</item>
    /// <item>Invalidating AI provenance, which flags derived items as needing review. Skipping
    /// it would leave them citing sources that no longer exist — which is why it is isolated
    /// from the byte delete above rather than sequenced behind it.</item>
    /// <item>Reconciling after a group rename or a membership move. The row names the new
    /// group or folder either way; the bytes catch up on the next run, and the resource stays
    /// openable at its recorded path meanwhile.</item>
    /// </list>
    ///
    /// Per side effect, deliberately: one path that refuses must not cost the next one.
    ///
    /// This cannot be pushed down into <see cref="IResourceStorage.Delete"/>,
    /// <see cref="IProvenanceInvalidator"/> or <see cref="ResourceLayoutReconciler"/>. The
    /// first two are interfaces, so any implementation may throw, and hardening the ones
    /// shipped today would leave the guarantee resting on every future implementation
    /// remembering. The isolation belongs where the sequencing is.
    ///
    /// Nothing before a commit goes through here — <c>mutations.Execute</c> must still throw
    /// and still roll back, and so must a reservation that fails before its row is written.
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
            // operation this belongs to has already succeeded.
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
    /// The Project and File under which a group action may proceed, refusing while either
    /// still holds the empty sentinel. It guards two different hazards, one per caller.
    ///
    /// For <see cref="CreateGroup"/> and <see cref="RenameGroup"/>, which claim a directory:
    /// <c>Path.Combine</c> swallows an empty part, so an unclaimed File would put the group's
    /// directory straight into the Project folder and an unclaimed Project would put it in
    /// the resources root — a folder no part of the layout agrees with, recorded on the row
    /// for good.
    ///
    /// For <see cref="MoveResourceToGroup"/>, which claims nothing, the load-bearing half is
    /// the Project segment, because the move reconciles the whole Project. The reconciler
    /// deliberately lets a both-empty Project/File through as the pre-0012 legacy shape, so
    /// sweeping an unclaimed Project resolves its Files' folders to "" and flattens their
    /// loose documents into the resources root — the same hazard <see cref="RenameFile"/>
    /// defers around, reached here through a sibling File the caller never touched.
    ///
    /// Deliberately group-specific either way. <see cref="ImportFile"/> keeps storing a
    /// document somewhere findable under an unclaimed parent, because refusing an import
    /// outright is a worse answer than a folder the next reconcile tidies. A group's segment
    /// is persisted rather than recomputed, so the same tolerance would be permanent.
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

        // AfterCommit, deliberately unlike RenameFile/RenameProject, which call the
        // reconciler directly: the row write has already committed, so a throw from the
        // reconcile cannot undo the rename — it can only misreport an operation that fully
        // succeeded. Aligning those two is a follow-up.
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

        // No indexer and no provenance invalidation, deliberately: filing changes neither
        // the bytes, the extracted text, nor the resource's identity, so anything derived
        // from it still cites exactly what it cited before.
        if (resource.GroupId != groupId)
        {
            resource.MoveToGroup(groupId, clock.Now);
            resources.Update(resource);
        }

        // Outside that branch on purpose: re-issuing a move that already landed is the
        // recovery path, not a no-op. The membership row and the bytes commit separately, so
        // a relocation that failed leaves the resource correctly filed at its old path — and
        // the user's response to seeing it in the wrong place is to file it there again.
        // Returning early would answer that with silence until the next app start.
        //
        // The row write, when there was one, is atomic on its own and has already committed.
        // Moving the bytes to match is best-effort from here: a failure leaves the resource
        // filed, at its old and still-openable path, for the next reconcile to converge —
        // reporting a filing that genuinely happened as failed would be the worse answer.
        AfterCommit(() => reconciler?.ReconcileProject(project.Id));
    }

    /// <summary>
    /// Removes the grouping and keeps everything in it: every member becomes loose in the
    /// File, then the group row goes. Nothing is destroyed, so there is no confirmation
    /// and — deliberately — no provenance invalidation: the resources, their bytes and
    /// their indexed text are all exactly what they were, so nothing derived from them has
    /// become stale.
    ///
    /// Membership is cleared explicitly even though <c>resources.group_id</c> is
    /// <c>ON DELETE SET NULL</c> and deleting the group row alone would loosen the members
    /// anyway. Saying it at the call site makes the intent legible and does not rest on the
    /// order the two statements happen to run in. The same <c>SET NULL</c> is why
    /// <see cref="DeleteGroup"/> must delete its member rows itself.
    ///
    /// One transaction for all of it: a failure part-way must not leave some members loose
    /// and others still filed under a group the user was told had gone.
    ///
    /// The group's directory is left on disk, empty, as the phase-1 plan calls for — no
    /// pruning here. Note what that makes routine: <see cref="ResourceLayoutReconciler"/>'s
    /// DirectoryClaims covers only directories a live group ROW claims, so a directory whose
    /// row is gone still ends an adoption probe one slot short. That state was previously
    /// reachable only through a rolled-back create; both removals now produce it on the
    /// happy path.
    /// </summary>
    public void UngroupGroup(ResourceGroupId id)
    {
        var group = groups.GetById(id);
        if (group is null)
        {
            // Already gone — the second click of a double-click, or another window that got
            // there first. Nothing to undo and nothing to report.
            return;
        }

        var file = files.GetById(group.FileId);
        var project = file is null ? null : projects.GetById(file.ProjectId);

        mutations.Execute((_, _, resourceRepo, _, groupRepo) =>
        {
            // Read through the transaction-bound repository, not the service's own: these
            // rows are about to be updated inside this unit of work, and reading them from
            // a second connection would block on the write lock this one already holds.
            foreach (var resource in resourceRepo.GetForFile(group.FileId).Where(r => r.GroupId == id))
            {
                resource.MoveToGroup(null, clock.Now);
                resourceRepo.Update(resource);
            }

            groupRepo.Delete(id);
        });

        // Deferred rather than refused when either parent has claimed nothing. Ungroup is a
        // recovery action and must stay available on data in that state — but reconciling
        // there is the hazard RenameFile also defers around.
        //
        // The Project half is the load-bearing one, and the only one RenameFile checks: the
        // reconciler deliberately lets a both-empty Project/File through as the pre-0012
        // legacy shape, so an unclaimed Project resolves its Files' folders to the File
        // segment alone and drags every newly loosened document out into the resources root.
        // The rows are already correct; the bytes stay put and converge once the backfill
        // claims the Project.
        //
        // The File half is belt-and-braces. The reconciler's own half-backfilled guard
        // already skips a File whose segment is empty under a claimed Project, so this
        // cannot be the thing that saves us — it is here to state the precondition the
        // sibling group actions state, not because removing it is known to break anything.
        if (file is { FolderSegment.Length: > 0 } && project is { FolderSegment.Length: > 0 } owner)
        {
            // AfterCommit for the same reason RenameGroup uses it: the rows have landed and
            // a failed reconcile cannot undo them, only misreport an operation that fully
            // succeeded. The members stay openable at their recorded paths meanwhile.
            AfterCommit(() => reconciler?.ReconcileProject(owner.Id));
        }
    }

    /// <summary>
    /// Removes the group and the resources in it, bytes included. The destructive half of
    /// the pair, behind a confirmation at the UI.
    ///
    /// The member rows are deleted here rather than through <see cref="DeleteResource"/>,
    /// which the original design called for: that method opens and commits its own
    /// transaction per call, so a failure part-way through a group would leave some members
    /// destroyed, some alive, and the group row in either state — with no record of which.
    /// Every row goes in one callback instead, so they commit together or not at all.
    ///
    /// Deleting only the group row would be the opposite failure and a quieter one:
    /// <c>resources.group_id</c> is <c>ON DELETE SET NULL</c>, not <c>CASCADE</c>, so it
    /// would preserve every document the user asked to destroy, loose in the File.
    ///
    /// Bytes and provenance follow the commit, one <see cref="AfterCommit"/> each, so a
    /// path that refuses to be deleted costs only itself — the remaining paths and every
    /// invalidation still run.
    ///
    /// This leaves the group's emptied directory behind too; see
    /// <see cref="UngroupGroup"/> for what that means for the reconciler's probe gap.
    /// </summary>
    public void DeleteGroup(ResourceGroupId id)
    {
        var group = groups.GetById(id);
        if (group is null)
        {
            return;
        }

        // Filled inside the callback below and read after it. The list outlives the
        // callback, so it still does the job a snapshot has to do: once the commit lands the
        // rows are gone, and neither the ids to invalidate nor the paths to delete can be
        // read back.
        var doomed = new List<Resource>();

        mutations.Execute((_, _, resourceRepo, _, groupRepo) =>
        {
            // Read through the transaction-bound repository, exactly as UngroupGroup does. A
            // member filed into this group between an outside read and this commit would be
            // missing from the list — and then not merely skipped: groupRepo.Delete below
            // SET NULLs it, so it survives loose, with its bytes, and with everything derived
            // from it unflagged, inside a group the user was told had been destroyed.
            doomed = resourceRepo.GetForFile(group.FileId).Where(r => r.GroupId == id).ToList();

            foreach (var resource in doomed)
            {
                resourceRepo.Delete(resource.Id);
            }

            groupRepo.Delete(id);
        });

        // Outside the callback: filesystem work never runs inside a transaction, because a
        // rollback cannot put the bytes back.
        foreach (var path in doomed.Select(r => r.StoredPath).OfType<string>())
        {
            AfterCommit(() => storage.Delete(path));
        }

        // Every member, not only the ones with bytes: an answer citing a deleted note is
        // exactly as stale as one citing deleted bytes.
        foreach (var resource in doomed)
        {
            AfterCommit(() => provenanceInvalidator?.InvalidateForResource(resource.Id));
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

        // The last place an unclaimed parent still reaches FolderFor. If the startup
        // backfill skipped this Project, its segment is empty and the document lands in
        // the resources root instead of a named folder. That is untidy, not lossy: Store
        // disambiguates on collision, the row records where the bytes actually went, and
        // the reconcile that runs once the Project is claimed relocates it. Guarding here
        // would mean refusing the import outright, which is a worse answer than storing it
        // somewhere findable.
        //
        // The File's group folders go with it, for the same reason the reconciler passes
        // them: an import arrives loose, and a loose document must never be handed a folder
        // name a group has claimed. Store's own disk probe cannot cover this — the group's
        // directory is missing exactly when it matters, straight after a parent rename or
        // while the group is still empty — and an import that took the name would leave the
        // group unable to create its directory ever again. Under the unclaimed parent above
        // there is normally nothing to name — CreateGroup refuses beneath one — so this
        // costs that path a query and changes nothing about it.
        var storedPath = storage.Store(
            ResourceLayout.FolderFor(project, file),
            ResourceLayout.FileNameFor(originalName, id.ToString()),
            sourcePath,
            ResourceLayout.ClaimedFolders(project, file, groups.GetForFile(fileId)));
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
