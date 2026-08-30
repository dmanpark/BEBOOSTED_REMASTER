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

**Read this before writing them.** An earlier draft of this plan injected a failing
`IResourceRepository` into the service and expected the transaction to blow up. It
cannot: `SqliteProjectMutations.Execute` constructs its OWN transaction-bound
repositories and hands them to the callback, so the service's injected repository is
used for *reads* only and is never invoked inside `Execute`. Those tests would have
passed while proving nothing.

The failure has to be injected at the **mutations** seam instead. Append to
`tests/BeBoosted.Tests/Projects/ProjectServiceTests.cs`, which already has
`_database`, `_projects`, `_files`, `_resources`, `_storage`, `_paths`, `_clock`,
and `_service`:

```csharp
    /// <summary>
    /// Runs the real mutation inside the real transaction, then throws before commit —
    /// so the callback's writes are genuinely rolled back, not merely never attempted.
    /// </summary>
    private sealed class FailAfterMutation(SqliteConnectionFactory factory) : IProjectMutations
    {
        public void Execute(
            Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new SqliteProjectRepository(connection, transaction),
                new SqliteProjectFileRepository(connection, transaction),
                new SqliteResourceRepository(connection, transaction),
                new SqliteTaskRepository(connection, transaction));
            throw new InvalidOperationException("injected failure");
        }
    }

    /// <summary>
    /// The transaction is real: work done inside a mutation that then throws must leave
    /// no trace. This pins SqliteProjectMutations itself, independently of the service.
    /// </summary>
    [Fact]
    public void ProjectMutations_WhenTheMutationThrows_RollsBackEveryWrite()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var link = _service.AddLink(file.Id, "SAT", "https://collegeboard.org");

        var mutations = new SqliteProjectMutations(_database.Factory);

        Assert.Throws<InvalidOperationException>(() =>
            mutations.Execute((_, fileRepo, resourceRepo, _) =>
            {
                resourceRepo.Delete(link.Id);
                fileRepo.Delete(file.Id);
                throw new InvalidOperationException("injected failure");
            }));

        Assert.NotNull(new SqliteResourceRepository(_database.Factory).GetById(link.Id));
        Assert.NotNull(new SqliteProjectFileRepository(_database.Factory).GetById(file.Id));
    }

    /// <summary>
    /// Bytes go only after the transaction commits. A failed mutation must leave the
    /// file on disk — orphaned bytes are recoverable, a row pointing at a deleted file
    /// is not.
    /// </summary>
    [Fact]
    public void DeleteResource_WhenTheMutationFails_LeavesTheRowAndTheBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        var service = CreateServiceWith(new FailAfterMutation(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteResource(resource.Id));

        Assert.NotNull(_resources.GetById(resource.Id));
        Assert.True(_storage.Exists(storedPath));
        Assert.Equal("fake pdf bytes", File.ReadAllText(_storage.ResolvePath(storedPath)));
    }

    [Fact]
    public void DeleteFile_WhenTheMutationFails_LeavesTheFileItsResourcesAndTheirBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        var service = CreateServiceWith(new FailAfterMutation(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteFile(file.Id));

        Assert.NotNull(_files.GetById(file.Id));
        Assert.NotNull(_resources.GetById(resource.Id));
        Assert.True(_storage.Exists(storedPath));
    }

    /// <summary>
    /// The widest rollback: a failed project delete must leave the project, its File,
    /// its resources, their bytes, AND the task's project assignment exactly as they
    /// were. The task unlink shares the transaction, so it must roll back too.
    /// </summary>
    [Fact]
    public void DeleteProject_WhenTheMutationFails_LeavesEveryRowTheAssignmentAndTheBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        var task = TaskItem.Create("Essay", _clock.Now, projectId: project.Id);
        _tasks.Add(task);

        var service = CreateServiceWith(new FailAfterMutation(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteProject(project.Id));

        Assert.NotNull(_projects.GetById(project.Id));
        Assert.NotNull(_files.GetById(file.Id));
        Assert.NotNull(_resources.GetById(resource.Id));
        Assert.True(_storage.Exists(storedPath));
        Assert.Equal(project.Id, _tasks.GetById(task.Id)!.ProjectId);
    }

    /// <summary>The happy path still removes the bytes — after the commit, not before.</summary>
    [Fact]
    public void DeleteFile_OnSuccess_RemovesTheRowsAndTheBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        _service.DeleteFile(file.Id);

        Assert.Null(_files.GetById(file.Id));
        Assert.Null(_resources.GetById(resource.Id));
        Assert.False(_storage.Exists(storedPath));
    }
```

Add the fixture helper beside the existing constructor, swapping the **mutations**
seam rather than a repository:

```csharp
    /// <summary>The same service, with the mutations seam swapped for a failing double.</summary>
    private ProjectService CreateServiceWith(IProjectMutations mutations)
        => new(
            _projects, _files, _resources, _storage, mutations,
            new SimpleLocalIndexer(_resources, _storage, _clock), _tasks,
            new SqliteCalendarBlockRepository(_database.Factory), _completions, _clock);
```

Adjust the argument order to match the constructor you write in Step 3.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ProjectMutations_WhenTheMutationThrows|FullyQualifiedName~WhenTheMutationFails|FullyQualifiedName~DeleteFile_OnSuccess"`

**Confirm the filter matched 6 tests.** A `--filter` naming tests that do not exist
runs zero and reports success — a green result that proves nothing. If the run says
"No test matches", the filter is stale: fix it before reading anything into the
result.

Expected: FAIL to compile — `IProjectMutations` and `SqliteProjectMutations` do not
exist.

Once they compile (after Step 3's interface exists but before the service is
rewritten), the failure tests fail on `Assert.True(_storage.Exists(storedPath))` — the
bytes are already gone. That is the defect.

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

**Let the cascades do the relational work.** `project_files.project_id` and
`resources.file_id` are both `ON DELETE CASCADE`, and foreign keys are enforced (see
Global Constraints). Deleting the root row therefore removes its children. An earlier
draft of this plan deleted every child row explicitly, which contradicted that
constraint and added failure points for work the database already does correctly.

What the service must still do by hand is the part no foreign key can: collect the
stored paths **before** the transaction, delete those bytes **after** it commits, and
invalidate provenance for each removed resource.

```csharp
    /// <summary>
    /// Removes one resource. The row goes first, inside a transaction; the bytes go
    /// only once that commits. A failure therefore orphans bytes - invisible and
    /// tolerated by the reconciler - rather than leaving a row pointing at a file
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

    /// <summary>
    /// Deletes the project and its Files/resources (and stored bytes), and unlinks
    /// its tasks - the tasks and their schedules survive. Files and resources go
    /// through the cascade; the task unlink and the root delete share one transaction.
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

Add `IProjectMutations mutations` to the constructor after `IResourceStorage storage`,
and fix every construction site the compiler flags.

**A cascade test is required**, because nothing else now proves the children go:
deleting a File removes its resource rows, and deleting a Project removes its Files
and their resources. If foreign keys were ever disabled these fail loudly, rather than
silently leaking rows.

**Aggregate provenance needs its own coverage, split across two files.**
`AiServiceTests.DeletingACitedResource_FlagsDerivedItems` (line 148) already pins the
single-resource path through `DeleteResource`. Nothing covers the aggregate paths, and
this rewrite moved that invalidation to *after* the transaction, where a missed loop is
silent.

**First, fix the fixture you are about to break.** `AiServiceTests` constructs
`ProjectService` positionally at line 66:

```csharp
        _projectService = new ProjectService(
            projects, files, _resources, storage,
            new SimpleLocalIndexer(_resources, storage, _clock),
            _tasks, new SqliteCalendarBlockRepository(_database.Factory),
            new SqliteOccurrenceCompletionRepository(_database.Factory), _clock, _service);
```

Inserting `IProjectMutations` after `storage` breaks this call. Update it to pass
`new SqliteProjectMutations(_database.Factory)` in that position. It is a real
mutations implementation, which is what the success-path tests want.

**In `AiServiceTests`, the two success paths** — its fixture already wires `_service`
as the provenance invalidator, so derived items are observable there:

- Deleting a **File** flags the derived items of **every** resource it held. Build two
  cited resources in one File and assert both are flagged, so a loop that invalidates
  only the first is caught.
- Deleting a **Project** does the same across its Files.

**In `ProjectServiceTests`, the rollback path**, because that is where
`FailAfterMutation` lives and `AiServiceTests` has no injectable seam. Extend the
existing helper to take an invalidator as well:

```csharp
    /// <summary>Records every invalidation so a test can assert none happened.</summary>
    private sealed class RecordingInvalidator : IProvenanceInvalidator
    {
        public List<ResourceId> Invalidated { get; } = [];

        public void InvalidateForResource(ResourceId id) => Invalidated.Add(id);
    }

    private ProjectService CreateServiceWith(
        IProjectMutations mutations, IProvenanceInvalidator? invalidator = null)
        => new(
            _projects, _files, _resources, _storage, mutations,
            new SimpleLocalIndexer(_resources, _storage, _clock), _tasks,
            new SqliteCalendarBlockRepository(_database.Factory), _completions, _clock,
            invalidator);

    /// <summary>
    /// Invalidation must not run ahead of a commit that never happened, or a
    /// rolled-back delete permanently marks live items "Needs review".
    /// </summary>
    [Fact]
    public void DeleteFile_WhenTheMutationFails_InvalidatesNothing()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        _service.AddLink(file.Id, "SAT", "https://collegeboard.org");
        _service.AddLink(file.Id, "ACT", "https://act.org");

        var recorder = new RecordingInvalidator();
        var service = CreateServiceWith(new FailAfterMutation(_database.Factory), recorder);

        Assert.Throws<InvalidOperationException>(() => service.DeleteFile(file.Id));

        Assert.Empty(recorder.Invalidated);
    }
```

Match `IProvenanceInvalidator`'s real member name and signature rather than the sketch
above if they differ.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ProjectMutations_WhenTheMutationThrows|FullyQualifiedName~WhenTheMutationFails|FullyQualifiedName~DeleteFile_OnSuccess"`
Expected: **PASS, 6 tests** — the count matters as much as the colour. Fewer than 6
means the filter missed one, not that one was fixed.

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

**Counting announcements is not enough.** `TasksMutated` firing proves the plumbing is
wired, not that any surface refreshed. The required outcome is that the Project list,
the Inbox, and the Daily/Calendar project labels all show the new state **without a
manual reload**. Write these in
`tests/BeBoosted.Desktop.Tests/ViewModels/ShellProjectRefreshTests.cs`, which already
builds a full shell with a project-linked scheduled task via `CreateShell()` and
`CreateProjectWithScheduledTask(...)`. Use that fixture, not the Projects-only one.

**Assert the new value, not the absence of the old one.** `DoesNotContain("Schoolwork")`
passes if the row vanished, if the label went null, or if the collection is empty —
none of which is the required outcome. Each surface must positively show "Coursework".

**The Inbox needs a task to hold.** `CreateProjectWithScheduledTask` produces a
*scheduled* task, so it lands in Scheduled and the Inbox is empty — a fixture that
cannot exercise the Inbox at all. Add a second, unscheduled task linked to the same
project.

**`Daily.UnscheduledRows` is not the Inbox.** They are separate surfaces:
`ShellViewModel.Inbox` is an `InboxViewModel` holding
`ObservableCollection<TaskRowViewModel> Tasks`. Finding 3 names three surfaces —
Projects list, Inbox, Daily/Calendar — so assert all three. Keep the
`Daily.UnscheduledRows` checks AND add separate ones against `shell.Inbox.Tasks`.

The Inbox row exposes `MetaText`, which composes `project · deadline · duration`. The
extra task is deliberately created with no deadline and no estimate, so that string is
exactly the project name and nothing else — which makes it an exact-equality assertion
rather than a substring check:

- after rename, the surviving Inbox row's `MetaText` equals `"Coursework"`
- after delete, the surviving Inbox row's `MetaText` is empty

```csharp
    /// <summary>A second task on the same project, left unscheduled so it sits in the Inbox.</summary>
    private static void AddUnscheduledProjectTask(
        ShellViewModel shell, InMemoryTaskRepository tasks, string title)
    {
        var project = shell.Projects.Projects.Single().Project;
        var task = TaskItem.Create(title, TestShell.DesignDate.ToDateTime(TimeOnly.MinValue), projectId: project.Id);
        tasks.Add(task);
    }

    [Fact]
    public void RenamingAProject_RelabelsEverySurfaceWithoutAManualReload()
    {
        var (shell, blocks, tasks) = CreateShell();
        CreateProjectWithScheduledTask(shell, blocks, tasks);
        AddUnscheduledProjectTask(shell, tasks, "Read chapter 4");

        shell.NavigateCommand.Execute(AppSection.Projects);
        var detail = shell.Projects.Detail!;
        detail.BeginRename();
        detail.RenameName = "Coursework";
        Assert.True(detail.TryCommitRename());

        // No ReloadList(), no re-navigation: the chain must have done it.
        Assert.Equal("Coursework", detail.Name);
        Assert.Equal("Coursework", shell.Projects.Projects.Single().Name);

        shell.NavigateCommand.Execute(AppSection.Calendar);

        // Positively: the scheduled row now carries the new label.
        var scheduled = shell.Calendar.Daily.ScheduledRows.Single(r => r.Title == "Stats HW");
        Assert.Equal("Coursework", scheduled.ProjectName);

        // Positively: the Daily list's unscheduled row does too.
        var unscheduled = shell.Calendar.Daily.UnscheduledRows.Single(r => r.Title == "Read chapter 4");
        Assert.Equal("Coursework", unscheduled.ProjectName);

        // And the Inbox proper, which is a different surface with its own snapshot.
        var inboxRow = shell.Inbox.Tasks.Single(r => r.Title == "Read chapter 4");
        Assert.Equal("Coursework", inboxRow.MetaText);
    }

    [Fact]
    public void DeletingAProject_ClearsItsLabelsOnEverySurfaceWithoutAManualReload()
    {
        var (shell, blocks, tasks) = CreateShell();
        CreateProjectWithScheduledTask(shell, blocks, tasks);
        AddUnscheduledProjectTask(shell, tasks, "Read chapter 4");

        shell.NavigateCommand.Execute(AppSection.Projects);
        var detail = shell.Projects.Detail!;
        detail.RequestDeleteCommand.Execute(null);
        detail.ConfirmPromptCommand.Execute(null);

        Assert.Empty(shell.Projects.Projects);

        shell.NavigateCommand.Execute(AppSection.Calendar);

        // Both tasks survive, both unassigned — asserted on the rows themselves, so a
        // vanished row cannot pass for a cleared label.
        var scheduled = shell.Calendar.Daily.ScheduledRows.Single(r => r.Title == "Stats HW");
        Assert.Null(scheduled.ProjectName);

        var unscheduled = shell.Calendar.Daily.UnscheduledRows.Single(r => r.Title == "Read chapter 4");
        Assert.Null(unscheduled.ProjectName);

        // The Inbox proper: no project, so MetaText collapses to empty.
        var inboxRow = shell.Inbox.Tasks.Single(r => r.Title == "Read chapter 4");
        Assert.Equal(string.Empty, inboxRow.MetaText);
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

    /// <summary>
    /// A link has no stored document, so the prompt must not claim one is deleted.
    /// Both branches of the wording need a pinned case.
    /// </summary>
    [Fact]
    public void RemovingALink_AsksWithoutTheStoredDocumentWarning()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.NewLinkUrl = "https://collegeboard.org/scores";
        file.NewLinkTitle = "SAT Scores";
        Assert.True(file.TryAddLink());
        var row = file.Resources.Single(r => r.Title == "SAT Scores");

        row.DeleteCommand.Execute(null);

        Assert.NotNull(file.Confirmation);
        Assert.Contains("SAT Scores", file.Confirmation!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stored document", file.Confirmation.Message, StringComparison.OrdinalIgnoreCase);
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

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~WithoutAManualReload|FullyQualifiedName~RemovingAResource|FullyQualifiedName~AResourceRemoval|FullyQualifiedName~RemovingALink|FullyQualifiedName~ShowsTheNewNameOnTheHeaderAndInTheList"`

**Confirm the filter matched 7 tests** before reading anything into the result — a
filter naming tests that do not exist runs zero and reports success.

Expected, and check each failure reason rather than just the count:

- `RenamingAProject_RelabelsEverySurfaceWithoutAManualReload` fails on the Projects
  list still holding "Schoolwork" — the chain never fired.
- `DeletingAProject_ClearsItsLabelsOnEverySurfaceWithoutAManualReload` fails the same way.
- The three removal tests fail because `Confirmation` is null and the resource is
  already gone — it was deleted on the first click.
- `RenamingAProject_ShowsTheNewNameOnTheHeaderAndInTheList` fails on the stale list
  once its manual `ReloadList()` is removed. **If it still passes, you did not delete
  that line** — it is the line that hid this defect through a full review.

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

Add `FolderSegment` and `RelocateTo` to both domain types, mirroring
`Resource.RelocateTo`'s contract — called only after a reservation succeeded, so the
row never names a folder that was never claimed. Then read and write the column in
both repositories.

**Construction order is forced, and it is not obvious.** `ResourceLayout.FolderFor`
sanitizes with the entity's own id as the fallback:

```csharp
Sanitize(project.Name, project.Id.ToString())
```

So the id must exist *before* a segment can be computed — which means `Create` cannot
require a real segment. The order is:

1. `Create(...)` with `FolderSegment` set to the empty sentinel `""`. The factory
   generates the id.
2. Compute the preferred segment using that id as the sanitize fallback.
3. Reserve it (Task 5), which claims the directory.
4. `RelocateTo(reserved, now)`.
5. Persist the row.

`Create` therefore takes no `folderSegment` parameter; only `Rehydrate` does, since it
reads a row that already has one. A row briefly holding `""` in memory before step 4
is expected — a row persisted with `""` is what Task 7's backfill looks for.

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

- Two Projects whose names sanitize identically resolve to **different** folders, and
  each one's resources land in its own.
- **A reconcile run twice moves nothing the second time** — the churn regression.
- **The reconciler cannot adopt a file belonging to another Project or File — driven
  through `ReconcileProject(targetProjectId)`, not `Reconcile()`.** This matters: the
  rename path calls the single-project overload, and `claimed` is assembled from the
  entities that overload walks. A test that only exercises the full `Reconcile()` sweep
  can pass while the narrower, more common path stays vulnerable, because the full
  sweep happens to visit the other owner's resources and the scoped one does not.
  Build two Projects that sanitize to the same legacy folder with bytes sitting in it,
  then reconcile **only the second** and assert it did not adopt the first's file.

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

**Naive reservation relocates everything, which is the opposite of the goal.** An
existing Project's bytes already sit in `resources/College Admissions/`. If the
backfill calls `ReserveFolderSegment` with no `ownedSegment`, that directory reads as
occupied and the Project is handed `College Admissions (2)` — so the reconciler then
moves every file the migration was supposed to leave alone.

The backfill must offer the derived legacy segment as **provisionally owned**: pass it
as `ownedSegment` so an existing directory of that name is treated as this entity's
own rather than as an obstacle.

**A sibling claim outranks provisional ownership.** Two Projects that sanitize to the
same segment cannot both own it. Whichever the backfill reaches first claims it for
real; the second finds it in `claimed` and must advance to `(2)` even though it would
also derive that name. So `claimed` is checked before `ownedSegment` is honoured, and
the backfill adds each segment to `claimed` as it goes.

- [ ] **Step 1: Write the failing tests**

- A Project with an empty segment gets the segment derived from its current name, and
  a subsequent reconcile moves **nothing** — its resources stay exactly where they are.
- Two Projects that sanitize identically get **different** segments: the first keeps
  the legacy directory, the second is relocated into its own.
- A File is backfilled within its Project's folder, and `claimed` is scoped to that
  Project's Files — two Files of *different* Projects may hold the same segment.
- **Order is forced: Projects before Files.** Starting with a Project and its File
  BOTH holding the empty sentinel, the File must be claimed beneath its Project's
  newly backfilled segment. A File backfilled first has no parent folder to resolve
  against and would claim beneath the wrong path — or beneath `""`. This test fails if
  the loops are ever reordered or flattened.
- Running the backfill twice changes nothing: it is idempotent, and an entity that
  already has a non-empty segment is skipped entirely.

- [ ] **Step 2: Run to verify they fail, then implement**

For each Project, then each File within it, whose `FolderSegment == ""`:

1. Derive the preferred segment with `ResourceLayout.Sanitize(name, id.ToString())`.
2. Call `ReserveFolderSegment(parent, preferred, claimed, ownedSegment: preferred)` —
   the derived name is offered as provisionally owned.
3. Add the returned segment to `claimed` before moving to the next entity.
4. `RelocateTo` and persist.

Run it at startup **before** `ResourceLayoutReconciler.Reconcile()` in `App.axaml.cs`,
inside the same try/catch — layout is cosmetic and a failure must not block startup.

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
