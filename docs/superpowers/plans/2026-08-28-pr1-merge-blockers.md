# PR #1 Merge Blockers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair the four merge blockers found by whole-range review of PR #1, with tests that would have caught each, so the branch can merge.

**Architecture:** Destructive project operations move their database work into one real SQLite transaction and delete stored bytes only after it commits, mirroring `ICalendarMutations`. Project and File folders gain a claimed, persisted `folder_segment` — the same durable-identity mechanism the approved resource-groups spec defines — so folders are never shared and byte adoption cannot cross owners. Two view-model gaps (missing refresh notification, missing delete confirmation) are wired into plumbing that already exists.

**Tech Stack:** C# / .NET 10, Avalonia 12, CommunityToolkit.Mvvm, SQLite (Microsoft.Data.Sqlite), xUnit.

**Spec:** No spec document — this plan implements four confirmed review findings, restated in full in each task. The folder-identity mechanism is specified in `docs/superpowers/specs/2026-08-28-resource-groups-design.md` under "Durable folder identity for groups"; this plan applies it one level up, to Projects and Files.

## Global Constraints

- `TreatWarningsAsErrors` is on solution-wide. A warning fails the build.
- Nullable reference types are enabled. No `!` suppressions in new production code.
- `AvaloniaUseCompiledBindingsByDefault` is on: a binding to a member that does not exist is a build error.
- **Foreign keys ARE enforced.** `SqliteConnectionFactory` sets `ForeignKeys = true`, which Microsoft.Data.Sqlite issues as `PRAGMA foreign_keys` on open. `SqliteCalendarBlockRepositoryTests.DeletingTask_CascadesToItsBlocks` and two others depend on it. Do not add compensating deletes for what a cascade already does; do walk children when bytes or provenance must also go.
- Migration `0012` is the next free number.
- Run the full suite with `dotnet test BeBoosted.slnx`. Close the running app first — it locks `BeBoosted.exe` and the build fails with MSB3027.
- Baseline before any change: `BeBoosted.Tests` 355 passing, `BeBoosted.Desktop.Tests` 486 passing / 3 skipped.
- This work lands on `feature/daily-priority-list`, the branch PR #1 tracks. Do not branch.

---

### Task 1: Transaction-bound project repositories

Pure refactor, no behavior change. Task 2 needs repositories that can be bound to one connection and transaction; the three project repositories currently open their own connection per call.

**Files:**
- Modify: `src/BeBoosted.Infrastructure/Projects/SqliteProjectRepositories.cs` (all three classes)
- Test: no new tests — the existing suite is the regression check

**Interfaces:**
- Consumes: nothing.
- Produces: `internal SqliteProjectRepository(SqliteConnection, SqliteTransaction)`, and the same on `SqliteProjectFileRepository` and `SqliteResourceRepository`.

- [ ] **Step 1: Read the pattern to mirror**

`src/BeBoosted.Infrastructure/Calendar/SqliteCalendarBlockRepository.cs` lines 10-28 already does exactly this:

```csharp
public sealed class SqliteCalendarBlockRepository : ICalendarBlockRepository
{
    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SqliteConnection? _sharedConnection;
    private readonly SqliteTransaction? _transaction;

    public SqliteCalendarBlockRepository(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory;

    /// <summary>Binds every operation to one shared connection and transaction.</summary>
    internal SqliteCalendarBlockRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _sharedConnection = connection;
        _transaction = transaction;
    }

    private SqliteSession OpenSession() => _sharedConnection is not null
        ? new SqliteSession(_sharedConnection, _transaction, ownsConnection: false)
        : new SqliteSession(_connectionFactory!.Open(), null, ownsConnection: true);
}
```

- [ ] **Step 2: Convert all three project repositories**

Each currently uses the primary-constructor form and opens its own connection per method:

```csharp
public sealed class SqliteProjectRepository(SqliteConnectionFactory connectionFactory) : IProjectRepository
{
    public void Add(Project project)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        ...
    }
}
```

Convert each to the explicit-constructor form above, and replace every method body's opening two lines with:

```csharp
        using var session = OpenSession();
        using var command = session.CreateCommand();
```

Do this for **every** method in `SqliteProjectRepository`, `SqliteProjectFileRepository`, and `SqliteResourceRepository`. Change nothing else — no SQL, no binding, no signatures on the public interfaces.

- [ ] **Step 3: Verify no behavior changed**

Run: `dotnet test BeBoosted.slnx`
Expected: `BeBoosted.Tests` 355, `BeBoosted.Desktop.Tests` 486 / 3 skipped — identical to baseline. A refactor that changes a number has changed behavior; find out why before continuing.

- [ ] **Step 4: Commit**

```bash
git add src/BeBoosted.Infrastructure/Projects/SqliteProjectRepositories.cs
git commit -m "refactor: let project repositories bind to a shared transaction"
```

---

### Task 2: Atomic destructive operations (Critical)

**The finding.** `ProjectService.DeleteResource` deletes stored bytes BEFORE deleting the row:

```csharp
if (resource.StoredPath is { } storedPath)
{
    storage.Delete(storedPath);   // bytes gone
}

resources.Delete(id);             // if this throws, the row survives
```

A repository failure leaves a live row pointing at permanently deleted bytes — the UI shows a resource whose file cannot be opened, forever. `DeleteFile` and `DeleteProject` compound it: each issues many independent writes across separate connections, so a mid-way failure leaves the project partially deleted.

**The fix inverts the failure mode.** All database work happens in one transaction; bytes are deleted only after it commits. A failure then orphans bytes on disk — invisible, harmless, and already tolerated by the reconciler — instead of breaking a row.

**Files:**
- Create: `src/BeBoosted.Application/Projects/IProjectMutations.cs`
- Create: `src/BeBoosted.Infrastructure/Persistence/SqliteProjectMutations.cs`
- Modify: `src/BeBoosted.Application/Projects/ProjectService.cs` (`DeleteResource`, `DeleteFile`, `DeleteProject`, constructor)
- Modify: `src/BeBoosted.Infrastructure/ServiceCollectionExtensions.cs` (registration)
- Test: `tests/BeBoosted.Tests/Projects/ProjectServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's transaction-bound repositories.
- Produces: `IProjectMutations.Execute(Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository>)`. `ProjectService` gains a required `IProjectMutations` parameter — place it immediately after `IResourceStorage storage` so the optional trailing parameters keep their positions.

- [ ] **Step 1: Write the failing tests**

Append to `tests/BeBoosted.Tests/Projects/ProjectServiceTests.cs`. The file already has `_database`, `_projects`, `_files`, `_resources`, `_storage`, `_paths`, `_clock`, and `_service`.

```csharp
    /// <summary>Delegates to the real repository, but throws on Delete while armed.</summary>
    private sealed class FailingResourceDelete(SqliteConnectionFactory factory) : IResourceRepository
    {
        private readonly SqliteResourceRepository _inner = new(factory);

        public void Add(Resource resource) => _inner.Add(resource);

        public void Update(Resource resource) => _inner.Update(resource);

        public void Delete(ResourceId id)
            => throw new InvalidOperationException("injected failure");

        public Resource? GetById(ResourceId id) => _inner.GetById(id);

        public IReadOnlyList<Resource> GetForFile(ProjectFileId fileId) => _inner.GetForFile(fileId);

        public int CountForFile(ProjectFileId fileId) => _inner.CountForFile(fileId);

        public void SetIndexText(ResourceId id, string text) => _inner.SetIndexText(id, text);

        public IReadOnlyList<Resource> SearchInProject(ProjectId projectId, string query)
            => _inner.SearchInProject(projectId, query);
    }

    /// <summary>
    /// A failed row delete must not have destroyed the bytes first. Orphaned bytes are
    /// recoverable; a live row pointing at a deleted file is not.
    /// </summary>
    [Fact]
    public void DeleteResource_WhenTheRowDeleteFails_LeavesTheBytesOnDisk()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        var service = CreateServiceWith(new FailingResourceDelete(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteResource(resource.Id));

        Assert.NotNull(_resources.GetById(resource.Id));
        Assert.True(_storage.Exists(storedPath));
        Assert.Equal("fake pdf bytes", File.ReadAllText(_storage.ResolvePath(storedPath)));
    }

    /// <summary>
    /// A File delete that fails partway must roll its whole database half back: no
    /// half-deleted File with some resources gone and others surviving.
    /// </summary>
    [Fact]
    public void DeleteFile_WhenARowDeleteFails_RollsBackEveryRow()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var first = _service.AddLink(file.Id, "SAT", "https://collegeboard.org");
        var second = _service.AddLink(file.Id, "ACT", "https://act.org");

        var service = CreateServiceWith(new FailingResourceDelete(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteFile(file.Id));

        Assert.NotNull(_files.GetById(file.Id));
        Assert.NotNull(_resources.GetById(first.Id));
        Assert.NotNull(_resources.GetById(second.Id));
    }
```

Add the fixture helper beside the existing constructor:

```csharp
    /// <summary>The same service, with one repository swapped for a failing double.</summary>
    private ProjectService CreateServiceWith(IResourceRepository resources)
        => new(
            _projects, _files, resources, _storage,
            new SqliteProjectMutations(_database.Factory),
            new SimpleLocalIndexer(_resources, _storage, _clock), _tasks,
            new SqliteCalendarBlockRepository(_database.Factory), _completions, _clock);
```

Adjust the argument order to match the constructor you write in Step 3.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~WhenTheRowDeleteFails|FullyQualifiedName~WhenARowDeleteFails"`

Expected: FAIL to compile — `IProjectMutations` and `SqliteProjectMutations` do not exist.

Once they compile (after Step 3's interface exists but before the service is rewritten), the first test fails on `Assert.True(_storage.Exists(storedPath))` — the bytes are already gone. That is the defect.

- [ ] **Step 3: Add the mutations abstraction**

`src/BeBoosted.Application/Projects/IProjectMutations.cs`:

```csharp
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
```

`src/BeBoosted.Infrastructure/Persistence/SqliteProjectMutations.cs`, mirroring `SqliteCalendarMutations`:

```csharp
using BeBoosted.Application.Projects;
using BeBoosted.Application.Tasks;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Tasks;

namespace BeBoosted.Infrastructure.Persistence;

/// <summary>
/// Runs a project mutation as one real SQLite transaction on one connection: the
/// repositories handed to the mutation are bound to that transaction, so a thrown
/// exception rolls back every project, File, resource, and task write together.
/// </summary>
public sealed class SqliteProjectMutations(SqliteConnectionFactory connectionFactory)
    : IProjectMutations
{
    public void Execute(
        Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository> mutation)
    {
        using var connection = connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        mutation(
            new SqliteProjectRepository(connection, transaction),
            new SqliteProjectFileRepository(connection, transaction),
            new SqliteResourceRepository(connection, transaction),
            new SqliteTaskRepository(connection, transaction));
        transaction.Commit();
    }
}
```

Register it in `ServiceCollectionExtensions` beside `SqliteCalendarMutations`.

- [ ] **Step 4: Rewrite the three destructive operations**

Each collects the paths to remove, does all database work in one `Execute`, and deletes bytes only after it returns.

```csharp
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

    public void DeleteFile(ProjectFileId id)
    {
        var doomed = resources.GetForFile(id);
        var paths = doomed.Select(r => r.StoredPath).OfType<string>().ToList();

        mutations.Execute((_, fileRepo, resourceRepo, _) =>
        {
            foreach (var resource in doomed)
            {
                resourceRepo.Delete(resource.Id);
            }

            fileRepo.Delete(id);
        });

        foreach (var path in paths)
        {
            storage.Delete(path);
        }

        foreach (var resource in doomed)
        {
            provenanceInvalidator?.InvalidateForResource(resource.Id);
        }
    }

    /// <summary>
    /// Deletes the project and its Files/resources (and stored bytes), and unlinks
    /// its tasks — the tasks and their schedules survive. Every row change is one
    /// transaction; bytes follow only once it commits.
    /// </summary>
    public void DeleteProject(ProjectId id)
    {
        var doomedFiles = files.GetForProject(id);
        var doomedResources = doomedFiles.SelectMany(f => resources.GetForFile(f.Id)).ToList();
        var paths = doomedResources.Select(r => r.StoredPath).OfType<string>().ToList();
        var orphaned = tasks.GetAll().Where(t => t.ProjectId == id).ToList();

        mutations.Execute((projectRepo, fileRepo, resourceRepo, taskRepo) =>
        {
            foreach (var resource in doomedResources)
            {
                resourceRepo.Delete(resource.Id);
            }

            foreach (var file in doomedFiles)
            {
                fileRepo.Delete(file.Id);
            }

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
```

Add `IProjectMutations mutations` to the constructor after `IResourceStorage storage`, and fix every construction site the compiler flags.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~WhenTheRowDeleteFails|FullyQualifiedName~WhenARowDeleteFails"`
Expected: PASS, 2 tests.

Then `dotnet test BeBoosted.slnx` — everything else still green. `ProjectServiceTests.DeleteProject_*` and `DeleteFile_*` exercise the rewritten paths and must still pass unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/ tests/
git commit -m "fix: delete project rows atomically before their stored bytes"
```

---

### Task 3: Shell refresh and Remove-from-File confirmation

Two small view-model gaps, batched because both wire an existing pattern into a view model that already has the plumbing.

**Finding 3.** `ProjectDetailViewModel.TryCommitRename` calls only `OnPropertyChanged(nameof(Name))`. It never calls `_owner.NotifyTasksMutated()`, so the Inbox, Daily list, and Calendar keep rendering the old project label until something else refreshes them. Project deletion has the same gap. **The existing rename test hides this by calling `projects.ReloadList()` by hand** — that line must go, or the test cannot fail.

**Finding 4.** "Remove from File" deletes the row and the stored document in one click with no confirmation, while File and Project deletion both use a two-step `ConfirmationPrompt`.

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/ProjectDetailViewModel.cs`
- Modify: `src/BeBoosted.Desktop/ViewModels/FileDetailViewModel.cs`
- Modify: `src/BeBoosted.Desktop/Views/ProjectsView.axaml`
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/ProjectRenameDeleteViewModelTests.cs`

**Interfaces:**
- Consumes: `ProjectsViewModel.NotifyTasksMutated()` and the `ConfirmationPrompt` / `_pendingConfirmedAction` plumbing already on `FileDetailViewModel`.
- Produces: no new public members.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void RenamingAProject_AnnouncesThroughTheShellChain()
    {
        var projects = WithProjectAndFile();
        var detail = projects.Detail!;
        var announcements = 0;
        projects.TasksMutated += () => announcements++;

        detail.BeginRename();
        detail.RenameName = "College Apps";
        Assert.True(detail.TryCommitRename());

        Assert.Equal(1, announcements);
    }

    [Fact]
    public void DeletingAProject_AnnouncesThroughTheShellChain()
    {
        var projects = WithProjectAndFile();
        var detail = projects.Detail!;
        var announcements = 0;
        projects.TasksMutated += () => announcements++;

        detail.RequestDeleteCommand.Execute(null);
        detail.ConfirmPromptCommand.Execute(null);

        Assert.Equal(1, announcements);
    }

    [Fact]
    public void RemovingAResource_AsksFirstAndNamesTheStoredDocument()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        var row = file.Resources.Single();

        row.DeleteCommand.Execute(null);

        Assert.NotNull(file.Confirmation);
        Assert.Contains("Transcript", file.Confirmation!.Message, StringComparison.Ordinal);
        Assert.Single(file.Resources); // nothing removed yet
    }

    [Fact]
    public void ConfirmingAResourceRemoval_RemovesIt()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        file.Resources.Single().DeleteCommand.Execute(null);

        file.ConfirmPromptCommand.Execute(null);

        Assert.Empty(file.Resources);
    }

    [Fact]
    public void DismissingAResourceRemoval_KeepsIt()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        file.Resources.Single().DeleteCommand.Execute(null);

        file.KeepPromptCommand.Execute(null);

        Assert.Null(file.Confirmation);
        Assert.Single(file.Resources);
    }
```

**Also remove the masking line.** In `RenamingAProject_ShowsTheNewNameOnTheHeaderAndInTheList`, delete the `projects.ReloadList();` call before the `projects.Projects.Single().Name` assertion. With finding 3 fixed the list refreshes through the chain; with it unfixed that test must fail. Leaving the manual call in is what let this ship.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~AnnouncesThroughTheShellChain|FullyQualifiedName~RemovingAResource|FullyQualifiedName~AResourceRemoval|FullyQualifiedName~ShowsTheNewNameOnTheHeaderAndInTheList"`

Expected: the two announcement tests fail with `0` announcements; the three removal tests fail because `Confirmation` is null and the resource is already gone; the de-masked rename test fails on the stale list.

- [ ] **Step 3: Announce project rename and delete**

In `ProjectDetailViewModel.TryCommitRename`, after `OnPropertyChanged(nameof(Name))`:

```csharp
        // Project labels are cached in Inbox, Daily, and Calendar snapshots; the one
        // central chain refreshes every dependent surface.
        _owner.NotifyTasksMutated();
```

In `RequestDelete`'s `_pendingConfirmedAction`, after `_service.DeleteProject(Project.Id)` and before `_owner.CloseDetail()`, add the same call.

- [ ] **Step 4: Put the resource removal behind the existing confirmation**

In `FileDetailViewModel`, add beside `RequestDelete`:

```csharp
    /// <summary>
    /// Removing a resource deletes its stored document too, so it asks first — the
    /// same two-step prompt a File or Project deletion uses.
    /// </summary>
    internal void RequestDeleteResource(ResourceRowViewModel row)
    {
        Confirmation = new ConfirmationPrompt(
            $"Remove '{row.Title}' from this File? Its stored document is deleted too.",
            "Remove",
            IsTaskDeletion: false);
        _pendingConfirmedAction = () => DeleteResource(row);
    }
```

Change `ResourceRowViewModel.Delete()` from `_owner.DeleteResource(this)` to `_owner.RequestDeleteResource(this)`. Leave `DeleteResource` itself as the confirmed action.

For a link or note there is no stored document; use a message without that clause when `row.Resource.StoredPath is null`. Keep both wordings accurate.

- [ ] **Step 5: Run the tests to verify they pass**

Run the Step 2 command. Expected: all pass, including the de-masked rename test.

- [ ] **Step 6: Check the view**

`ProjectsView.axaml` binds "Remove from File" to `DeleteCommand`, which now opens the prompt rather than deleting. The confirmation card for `FileDetailViewModel` already exists from the earlier rename/delete work — confirm it renders for this path too and needs no change.

- [ ] **Step 7: Full suite and commit**

Run: `dotnet test BeBoosted.slnx` — all green.

```bash
git add src/ tests/
git commit -m "fix: announce project rename/delete and confirm resource removal"
```

---

### Task 4: Migration 0012 — persisted folder segments

**Finding 2, part one.** `ResourceLayout.FolderFor` derives a folder purely from the sanitized Project name and File title, with no disambiguation. Two Projects whose names sanitize identically — `Q1: Report` and `Q1- Report` both become `Q1- Report` — share one physical directory. This task gives both entities a persisted, claimed segment; Tasks 5-7 claim and use it.

**Files:**
- Create: `src/BeBoosted.Infrastructure/Persistence/Migrations/0012_folder_segments.sql`
- Modify: `src/BeBoosted.Domain/Projects/Project.cs`, `ProjectFile.cs`
- Modify: `src/BeBoosted.Infrastructure/Projects/SqliteProjectRepositories.cs`
- Test: `tests/BeBoosted.Tests/Persistence/` (new file), `tests/BeBoosted.Tests/Domain/`

**Interfaces:**
- Consumes: Task 1's repository shape.
- Produces: `Project.FolderSegment` and `ProjectFile.FolderSegment` (`string`, non-null), each with `RelocateTo(string folderSegment, DateTimeOffset now)`. `Create` and `Rehydrate` gain the parameter.

- [ ] **Step 1: Write the migration**

`0012_folder_segments.sql`:

```sql
ALTER TABLE projects ADD COLUMN folder_segment TEXT NOT NULL DEFAULT '';

ALTER TABLE project_files ADD COLUMN folder_segment TEXT NOT NULL DEFAULT '';
```

Empty string means "not yet claimed" and is what Task 7's backfill looks for. A `DEFAULT ''` keeps the column non-null without needing the sanitizing logic, which lives in C# and cannot run in SQL.

- [ ] **Step 2: Write the failing tests**

A persistence test that the column round-trips, and a domain test that `RelocateTo` records a segment and touches `ModifiedAt`. Follow the shape of `tests/BeBoosted.Tests/Persistence/UnifiedTaskMigrationTests.cs` for migration tests and `CalendarBlockTests` for the domain half.

- [ ] **Step 3: Run to verify they fail, then implement**

Add `FolderSegment` and `RelocateTo` to both domain types, mirroring `Resource.RelocateTo`'s contract — called only after a reservation succeeded, so the row never names a folder that was never claimed. Then read and write the column in both repositories.

- [ ] **Step 4: Full suite and commit**

```bash
git add src/ tests/
git commit -m "feat: persist a folder segment on projects and Files"
```

---

### Task 5: Claim folders instead of guessing them

**Finding 2, part two — the storage contract.** Two symmetric defects, both real today:

- `LocalResourceStorage.ReserveFreePath` tests only `File.Exists`, so a **directory** at a candidate path reads as free. A resource whose name matches a Project or File folder gets handed that path, and `File.Copy` onto a directory throws — uncaught in `Store`, so it reaches the user.
- Nothing claims a folder name, so a name checked as free can be taken before it is used.

**Files:**
- Modify: `src/BeBoosted.Application/Projects/IResourceStorage.cs`
- Modify: `src/BeBoosted.Infrastructure/Projects/LocalResourceStorage.cs`
- Test: `tests/BeBoosted.Tests/Projects/LocalResourceStorageTests.cs`

**Interfaces:**
- Produces: `string IResourceStorage.ReserveFolderSegment(string relativeParent, string preferredSegment, IReadOnlySet<string> claimed, string? ownedSegment = null)`.

- [ ] **Step 1: Write the failing tests**

Four cases, each of which fails under a different plausible shortcut:

1. **File first, then folder** — a file named `Notes` exists; reserving a folder segment `Notes` returns something else.
2. **Folder first, then file** — a directory named `Notes` exists; `Store` of a resource whose file name is `Notes` does NOT target the directory and does not throw. *This one fails today.*
3. **Reservation claims** — after `ReserveFolderSegment` returns `X`, the directory `X` exists on disk.
4. **Owned segment is kept** — reserving with `ownedSegment: "Notes"` and preferred `Notes` returns `Notes` unchanged, even though that directory exists, rather than advancing to `Notes (2)`.

- [ ] **Step 2: Run to verify they fail**

Expected: 1, 3, 4 fail to compile (`ReserveFolderSegment` missing); 2 fails with `UnauthorizedAccessException` from `File.Copy`.

- [ ] **Step 3: Make file reservation directory-aware**

In `ReserveFreePath`, the loop currently returns the first candidate for which `!File.Exists(...)`. It must also reject a candidate occupied by a directory:

```csharp
            var absolute = ResolvePath(storedPath);
            if (!File.Exists(absolute) && !Directory.Exists(absolute))
            {
                return storedPath;
            }
```

Leave `Exists` alone — it answers "are this resource's bytes still here", and the reconciler's `FindUnrecordedPlacement` depends on that reading.

- [ ] **Step 4: Implement `ReserveFolderSegment`**

Probe `preferredSegment`, then `preferredSegment (2)`, `(3)`, … using `ResourceLayout.CandidateName`'s suffix shape. Skip any candidate that is in `claimed`, or that exists on disk as a file or directory — **except** when it equals `ownedSegment`, which is this group/File/Project's own directory and is returned unchanged. Create the directory before returning: creating it IS the claim.

- [ ] **Step 5: Run to verify they pass, then full suite, then commit**

```bash
git add src/ tests/
git commit -m "fix: claim folder names and treat directories as occupied"
```

---

### Task 6: Use the claimed segment everywhere

**Files:**
- Modify: `src/BeBoosted.Application/Projects/ResourceLayout.cs`, `ResourceLayoutReconciler.cs`, `ProjectService.cs`
- Test: `tests/BeBoosted.Tests/Projects/ResourceLayoutTests.cs`, `ResourceLayoutReconcilerTests.cs`

- [ ] **Step 1: Write the failing tests**

- Two Projects whose names sanitize identically resolve to **different** folders, and each one's resources land in its own.
- **A reconcile run twice moves nothing the second time** — the churn regression.
- The reconciler cannot adopt a file belonging to another Project or File.

- [ ] **Step 2: Run to verify they fail, then implement**

`FolderFor(Project, ProjectFile)` returns `Path.Combine(project.FolderSegment, file.FolderSegment)` — the persisted values, no sanitizing at this point, because both were sanitized and claimed when stored.

`CreateProject`, `RenameProject`, `CreateFile`, and `RenameFile` call `ReserveFolderSegment` and persist the result via `RelocateTo`, passing the sibling segments as `claimed` and the current segment as `ownedSegment` on rename.

In `ResourceLayoutReconciler`, widen `claimed` so adoption cannot cross owners: build it from every resource's stored path, not just the current File's.

- [ ] **Step 3: Full suite and commit**

```bash
git add src/ tests/
git commit -m "fix: resolve resource folders from claimed segments"
```

---

### Task 7: Backfill existing rows

Every Project and File created before migration `0012` has `folder_segment = ''`. They need a claimed segment matching where their bytes already are, or the reconciler will try to move everything.

**Files:**
- Create: `src/BeBoosted.Application/Projects/FolderIdentityBackfill.cs`
- Modify: `src/BeBoosted.Desktop/App.axaml.cs`, `ServiceCollectionExtensions.cs`
- Test: `tests/BeBoosted.Tests/Projects/FolderIdentityBackfillTests.cs`

- [ ] **Step 1: Write the failing tests**

- A Project with an empty segment gets one derived from its current name, and its resources do not move.
- Two Projects that sanitize identically get **different** segments, and the second one's bytes are relocated into it.
- A Project that already has a segment is left alone — the backfill is idempotent and running it twice changes nothing.

- [ ] **Step 2: Run to verify they fail, then implement**

For each Project then each File with `FolderSegment == ""`: compute the preferred segment with `ResourceLayout.Sanitize`, reserve it (passing siblings already backfilled as `claimed`), and persist. Run it at startup **before** `ResourceLayoutReconciler.Reconcile()` in `App.axaml.cs`, inside the same try/catch — layout is cosmetic and a failure must not block startup.

- [ ] **Step 3: Full suite and commit**

```bash
git add src/ tests/
git commit -m "feat: backfill folder segments for existing projects and Files"
```

---

### Task 8: Verify in the running app

Tests do not catch XAML binding failures, and three of these four fixes are user-visible.

- [ ] **Step 1: Build and launch** — `dotnet build BeBoosted.slnx` then `./bb`. If MSB3027 says `BeBoosted.exe` is locked, close the running app first.

- [ ] **Step 2: Rename a project** and confirm the new name appears on the Daily list's project labels and in the Inbox **without** navigating away and back. That is finding 3.

- [ ] **Step 3: Remove a resource from a File** and confirm a confirmation appears naming it, that Keep leaves it, and that confirming removes it. That is finding 4.

- [ ] **Step 4: Check the resources directory on disk** — `%LOCALAPPDATA%`'s BeBoosted data directory. Confirm existing Projects and Files still resolve to their folders after the backfill and that no documents went missing.

- [ ] **Step 5: Delete a project** with at least one File and confirm its tasks survive as unassigned.

- [ ] **Step 6: Commit any fixes** with a failing test first.

---

## Self-Review

**Spec coverage:**

| Finding | Task |
| --- | --- |
| 1 Critical — bytes deleted before rows; no transaction | 1, 2 |
| 2 Important — folder identity not disambiguated; adoption crosses owners | 4, 5, 6, 7 |
| 3 Important — rename/delete bypass the Shell chain | 3 |
| 4 Important — one-click permanent resource removal | 3 |
| The test that masked finding 3 | 3, Step 1 |
| Deferred minors (session titles on Project surface; rerank button on completed tasks) | Out of scope — deferred by the reviewer |

**Placeholder scan:** Tasks 4, 6, and 7 describe their tests rather than listing literal code, because their fixtures were not read while writing this plan and inventing them would produce code that does not compile. Each names the file to follow. Tasks 2, 3, and 5 carry literal test code.

**Type consistency:** `IProjectMutations.Execute` takes the same four repositories in Tasks 2 and 3. `ReserveFolderSegment`'s four parameters are identical in Tasks 5, 6, and 7. `FolderSegment` / `RelocateTo` appear on `Project` and `ProjectFile` in Task 4 and are consumed in 6 and 7.
