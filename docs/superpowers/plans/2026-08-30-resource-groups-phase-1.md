# Resource Groups Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Add one level of durable groups inside a File, with safe Ungroup/Delete group actions and a keyboard-accessible Move to… flyout.

**Architecture:** A ResourceGroup owns a persisted folder segment and belongs to one ProjectFile; Resource has nullable group membership. Extend the existing transaction-bound repository seam for group removal, and reuse the shipped storage reservation and reconciliation mechanisms. Keep one canonical resource selection while presenting groups and loose resources in the File detail.

**Tech Stack:** Existing .NET 10 / C#, SQLite via Microsoft.Data.Sqlite 10.0.11, Avalonia 12.1.1, CommunityToolkit.Mvvm 8.4.2, xunit.v3 3.2.2 and Avalonia.Headless.XUnit. No new packages.

**Spec:** docs/superpowers/specs/2026-08-28-resource-groups-design.md. Read both documents before executing.

## Global Constraints

- **Groups never nest.** A group holds resources only, never another group.
- **No manual reordering UI in phase 1.** Persist sort_order; assign increasing values on creation. No reorder service or control.
- **No group-targeted import in phase 1.** Keep AddLink, AddNote and ImportFile signatures; new resources are loose.
- No per-group description, colour, or icon.
- No change to indexing, search, or AI provenance semantics. Membership/title changes do not re-index or invalidate; actual resource deletion still invalidates.
- No change to how Files or Projects behave, or to ProjectFile at all.
- A File with no groups retains its flat resource list, without group headers or the loose-section heading.
- Ungroup preserves resources and bytes and needs no confirmation. Delete group removes its resources, after confirmation naming the group and count.
- Stored segments are facts, not names re-derived on reconcile. ResourceLayout stays pure; Exists stays file-only.
- Folder reservation creates a directory but is **not atomic or a cross-process lock**. Preserve the single-process sequential-use contract.
- Leave empty folders behind. No pruning, cleanup retries, telemetry, or orphan adoption expansion in this branch.
- Keep the merged transaction/rollback and startup-backfill safety contracts. No broad catches around database mutations.

---

## Starting point and post-merge adjustments

Plan branch: feature/resource-groups-phase-1, based on main at 9626197 (PR #1 merge). The plan is documentation-only; implementing it requires a separate go-ahead. Do not push or merge as part of writing this plan.

The design predates parts of the merged repair. These are explicit implementation adjustments, not new feature scope:

1. ReserveFolderSegment(relativeParent, preferredSegment, claimed, ownedSegment = null), directory-aware ReserveFreePath, and migration 0012 already ship. Reuse them; add only migration 0013. There are no existing groups to backfill.
2. The spec describes DeleteGroup as calling DeleteResource repeatedly. Do **not** do that: each call commits independently. Delete all member rows and the group in one IProjectMutations callback, then attempt each byte deletion and each provenance invalidation separately through AfterCommit.
3. ResourceGroup.Create follows the merged Project/ProjectFile construction pattern: it allocates its ID with an empty, in-memory folder segment, then the service reserves using that ID as sanitization fallback and calls RelocateTo before Add. This replaces the spec's folderSegment parameter on Create. The repository refuses to persist an empty segment; no group backfill sentinel is introduced.
4. FileDetailViewModel.Resources remains an all-resources selection/count index for existing callers and search navigation. The rendered lists become Groups and LooseResources, sharing those row instances rather than creating copies.
5. A new group folder must not be claimed beneath an unclaimed Project/File. Create/Rename/Move validate both parent segments before changes. Ungroup and Delete remain available to recover/remove existing data; Ungroup defers reconciliation when either parent segment is empty.

### Safety rules inherited from PR #1

- Mutations.Execute stays outside AfterCommit. A failed transaction must throw; no byte deletion or invalidation may run.
- Snapshot member IDs and byte paths before commit. AfterCommit surrounds each individual side effect, not a whole loop.
- ResourceLayoutStartup still calls backfill first and defers the whole sweep if Skipped is nonzero.
- Preserve the reconciler's precise half-backfilled guard and its deliberately supported both-empty legacy path for loose resources. Do not make old adoption tests pass by skipping all legacy rows.
- ReconcileProject still protects stored paths claimed by resources in **every** project, not just its target.
- A grouped resource whose group is missing, belongs to another File, or has an empty segment is skipped, not silently flattened into loose storage.
- Post-commit cleanup failures remain unreported at this layer. Cleanup reporting/retry and skipped-provenance repair belong to a follow-up, not this feature.

## File map

All paths below are relative to the repository root. Anchors are symbols, not stale line numbers.

| Files | Responsibility / task |
|---|---|
| src/BeBoosted.Domain/Ids.cs; Projects/Resource.cs; new Projects/ResourceGroup.cs | Identity and membership, Task 1 |
| src/BeBoosted.Application/Projects/Repositories.cs; IProjectMutations.cs | Group repository and atomic callback contract, Task 2 |
| src/BeBoosted.Infrastructure/Persistence/Migrations/0013_resource_groups.sql | Additive schema, Task 2 |
| src/BeBoosted.Infrastructure/Projects/SqliteProjectRepositories.cs; new SqliteResourceGroupRepository.cs | Resource mappings and transaction-bound group persistence, Task 2 |
| src/BeBoosted.Infrastructure/Persistence/SqliteProjectMutations.cs; ServiceCollectionExtensions.cs | Transaction enlistment and DI, Task 2 |
| src/BeBoosted.Application/Projects/ResourceLayout.cs; ResourceLayoutReconciler.cs | Group destinations and conservative retries, Task 3 |
| src/BeBoosted.Application/Projects/ProjectService.cs | Create/rename/move, Task 4; atomic removals, Task 5 |
| src/BeBoosted.Desktop/ViewModels/FileDetailViewModel.cs; new ResourceGroupViewModel.cs; new ResourceMoveTargetViewModel.cs | Grouped projection, selection and actions, Task 6 |
| src/BeBoosted.Desktop/Views/ProjectsView.axaml; ProjectsView.axaml.cs | Shared row template, headers and flyouts, Task 7 |
| tests/BeBoosted.Tests/Support/ResourceGroupFixture.cs (new) | Real SQLite, temporary bytes and injectable service seams, Tasks 2–5 |
| tests/BeBoosted.Tests/Support/RecordingGroupInvalidator.cs; FailGroupMutation.cs (new) | Invalidation recording in Task 4; true precommit failure in Task 5 |
| tests/BeBoosted.Tests/Domain/ResourceGroupTests.cs (new); ResourceTests.cs | Domain invariants, Task 1 |
| tests/BeBoosted.Tests/Persistence/ResourceGroupPersistenceTests.cs (new) | Upgrade, mapping, cascades, real rollback, Task 2 |
| tests/BeBoosted.Tests/Projects/ResourceGroupLayoutTests.cs (new) | Group placement, restart, collision/retry safety, Task 3 |
| tests/BeBoosted.Tests/Projects/ResourceGroupServiceTests.cs (new); ResourceGroupRemovalTests.cs (new) | Use cases and failure ordering, Tasks 4–5 |
| tests/BeBoosted.Tests/Projects/ProjectServiceTests.cs; ResourceLayoutReconcilerTests.cs; FolderIdentityBackfillTests.cs; tests/BeBoosted.Tests/Ai/AiServiceTests.cs | Required constructor/seam updates; existing guards remain real |
| tests/BeBoosted.Desktop.Tests/Support/TestDoubles.cs | Detached resource/group snapshots, cascades and constructor wiring |
| tests/BeBoosted.Desktop.Tests/ViewModels/ResourceGroupsViewModelTests.cs (new) | Visible state without manually supplying refresh, Task 6 |
| tests/BeBoosted.Desktop.Tests/Ui/ResourceGroupsInteractionTests.cs (new) | Rendered controls and actual flyout selection, Task 7 |
| tests/BeBoosted.Desktop.Tests/ResourceGroupsCompositionTests.cs (new) | Production DI in the desktop test project, which already references the full DI package, Task 8 |
| docs/superpowers/plans/2026-08-30-resource-groups-phase-1-verification.md (new during execution) | Final evidence and live-app smoke record, Task 8 |

No changes are planned to LocalResourceStorage, FolderIdentityBackfill, ResourceLayoutStartup, ProjectFile, reading-pane behavior, or existing resource creation signatures.

## Test execution convention

Run from the repository root. Use the core project for Tasks 1–5 and the desktop test project for Tasks 6–7:

~~~powershell
dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ResourceGroup" --verbosity normal
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~ResourceGroups" --verbosity normal
~~~

Before accepting a filtered green run, use the same filter with --list-tests and verify every intended fully qualified test name is listed. A zero-match run is failure, not success. Record actual discovered/executed counts, including Theory cases; do not hand-maintain counts as tests are added.

Each task's first run must fail for its specified missing behavior, not an unrelated fixture failure. Missing new API compilation is an expected first red; after adding API skeletons, verify behavioral assertions fail before completing their implementations. Run the full solution after each public constructor or mutation-callback change.

### Task 1: Domain identity and nullable resource membership

**Files:** Domain files and domain test files in the map.

**Interfaces:**

~~~csharp
// BeBoosted.Domain, following the existing ID structs:
public readonly record struct ResourceGroupId(Guid Value)
{
    public static ResourceGroupId New() => new(Guid.NewGuid());
    public static ResourceGroupId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString();
}

// ResourceGroup: immutable Id, FileId, CreatedAt; private setters on the rest.
public static ResourceGroup Create(ProjectFileId fileId, string title,
    int sortOrder, DateTimeOffset now);
public static ResourceGroup Rehydrate(ResourceGroupId id, ProjectFileId fileId,
    string title, string folderSegment, int sortOrder,
    DateTimeOffset createdAt, DateTimeOffset modifiedAt);
public void Rename(string title, DateTimeOffset now);
public void RelocateTo(string folderSegment, DateTimeOffset now);
public void Reorder(int sortOrder, DateTimeOffset now);

// Resource: append to Rehydrate; don't insert a positional argument.
public ResourceGroupId? GroupId { get; private set; }
public void MoveToGroup(ResourceGroupId? groupId, DateTimeOffset now);
// Last parameter of Resource.Rehydrate:
// ResourceGroupId? groupId = null
~~~

- [ ] Add ResourceGroupTests with the following test and theories for blank title on Create/Rename, blank segment on RelocateTo, negative order on Create/Reorder, and complete Rehydrate roundtrip (ID, FileId, title, segment, order and both timestamps).

~~~csharp
[Fact]
public void Rename_DoesNotChangeTheClaimedFolder()
{
    var now = DateTimeOffset.Parse("2026-08-30T09:00:00-07:00");
    var group = ResourceGroup.Create(ProjectFileId.New(), "  Notes  ", 0, now);
    Assert.Equal("Notes", group.Title);
    Assert.Equal(string.Empty, group.FolderSegment);
    group.RelocateTo("Notes (2)", now);
    group.Rename("References", now.AddMinutes(1));
    Assert.Equal("References", group.Title);
    Assert.Equal("Notes (2)", group.FolderSegment);
    Assert.Equal(now, group.CreatedAt);
    Assert.Equal(now.AddMinutes(1), group.ModifiedAt);
}

[Theory]
[InlineData(ResourceKind.Document)]
[InlineData(ResourceKind.Image)]
[InlineData(ResourceKind.Link)]
[InlineData(ResourceKind.Note)]
public void MembershipChanges_KeepContentPathAndIndexState(ResourceKind kind)
{
    var now = DateTimeOffset.Parse("2026-08-30T09:00:00-07:00");
    var fileId = ProjectFileId.New();
    var resource = kind switch
    {
        ResourceKind.Link => Resource.CreateLink(fileId, "Source", "https://example.com", now),
        ResourceKind.Note => Resource.CreateNote(fileId, "Source", "body", now),
        _ => Resource.CreateStored(fileId, kind, "Source", "source.txt", "old/source.txt", now),
    };
    resource.MarkIndexed(now);
    var before = (resource.Id, resource.FileId, resource.Title, resource.StoredPath,
        resource.Content, resource.Url, resource.IndexState, resource.AddedAt);
    Assert.Null(resource.GroupId);
    var groupId = ResourceGroupId.New();
    resource.MoveToGroup(groupId, now.AddMinutes(1));
    Assert.Equal(groupId, resource.GroupId);
    resource.MoveToGroup(null, now.AddMinutes(2));
    Assert.Null(resource.GroupId);
    Assert.Equal(before, (resource.Id, resource.FileId, resource.Title, resource.StoredPath,
        resource.Content, resource.Url, resource.IndexState, resource.AddedAt));
    Assert.Equal(now.AddMinutes(2), resource.ModifiedAt);
}
~~~

- [ ] Run the domain filter; establish red for the absent types/membership.
- [ ] Implement ResourceGroup with a private constructor assigning all seven fields. Create generates an ID, trims/validates title, validates nonnegative order, and uses empty folderSegment. Rehydrate assigns persisted values without generating identities. Rename and Reorder validate before assignment; RelocateTo rejects blank and stores the trimmed segment. Each mutation updates ModifiedAt only; no filesystem calls.

~~~csharp
private static string ValidateTitle(string title)
    => string.IsNullOrWhiteSpace(title)
        ? throw new DomainException("A group needs a title.") : title.Trim();

private static int ValidateOrder(int order)
    => order < 0 ? throw new DomainException("Group order cannot be negative.") : order;

// Resource method; constructor gains a final nullable groupId and assigns GroupId.
public void MoveToGroup(ResourceGroupId? groupId, DateTimeOffset now)
{
    GroupId = groupId;
    ModifiedAt = now;
}
~~~

- [ ] Thread nullable membership through Resource's private constructor/Rehydrate; factories explicitly leave it null. Test rehydrating a non-null GroupId and a legacy omitted argument. Run ResourceTests and ResourceGroupTests, then the full core suite.
- [ ] Commit only these domain/test files: feat: model resource groups and membership.

### Task 2: Persistence and the transaction-bound group repository

**Files:** Persistence, repository, mutation, DI and test-support files in the map. Update existing ProjectService callback arities and ProjectServiceTests' FailAfterMutation/direct transaction test. No group service behavior yet.

**Consumes:** Task 1 domain interfaces.

**Produces:**

~~~csharp
public interface IResourceGroupRepository
{
    void Add(ResourceGroup group);
    void Update(ResourceGroup group);
    void Delete(ResourceGroupId id);
    ResourceGroup? GetById(ResourceGroupId id);
    IReadOnlyList<ResourceGroup> GetForFile(ProjectFileId fileId);
}

// Append groups; keep the first four repositories in their original order.
void Execute(Action<IProjectRepository, IProjectFileRepository,
    IResourceRepository, ITaskRepository, IResourceGroupRepository> mutation);

// SqliteResourceGroupRepository constructors:
public SqliteResourceGroupRepository(SqliteConnectionFactory connectionFactory);
internal SqliteResourceGroupRepository(SqliteConnection connection, SqliteTransaction transaction);
~~~

- [ ] Create the reusable ResourceGroupFixture in tests/BeBoosted.Tests/Support. Use namespace BeBoosted.Tests.Support, application abstraction/project imports, domain imports and infrastructure persistence/project imports. This fixture is real SQLite, not an in-memory rollback imitation:

~~~csharp
public sealed class ResourceGroupFixture : IDisposable, IAppDataPaths, IClock
{
    public TempDatabase Database { get; } = new();
    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), $"beboosted-groups-{Guid.NewGuid():N}");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");
    public DateTimeOffset Now { get; set; } =
        DateTimeOffset.Parse("2026-08-30T09:00:00-07:00");
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
    public SqliteProjectRepository Projects { get; }
    public SqliteProjectFileRepository Files { get; }
    public SqliteResourceRepository Resources { get; }
    public SqliteResourceGroupRepository Groups { get; }
    public LocalResourceStorage Storage { get; }
    public Project Project { get; }
    public ProjectFile File { get; }

    public ResourceGroupFixture()
    {
        Directory.CreateDirectory(DataDirectory);
        new MigrationRunner(Database.Factory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        Projects = new(Database.Factory);
        Files = new(Database.Factory);
        Resources = new(Database.Factory);
        Groups = new(Database.Factory);
        Storage = new(this);
        Project = BeBoosted.Domain.Projects.Project.Create("Schoolwork", "#5B8DEF", Now);
        Project.RelocateTo(Storage.ReserveFolderSegment("", "Schoolwork",
            new HashSet<string>()), Now);
        Projects.Add(Project);
        File = ProjectFile.Create(Project.Id, "Spanish", null, Now);
        File.RelocateTo(Storage.ReserveFolderSegment(Project.FolderSegment, "Spanish",
            new HashSet<string>()), Now);
        Files.Add(File);
    }

    public ResourceGroup Group(string title = "Notes")
    {
        var siblings = Groups.GetForFile(File.Id);
        var group = ResourceGroup.Create(File.Id, title,
            siblings.Count == 0 ? 0 : siblings.Max(g => g.SortOrder) + 1, Now);
        group.RelocateTo(Storage.ReserveFolderSegment(
            ResourceLayout.FolderFor(Project, File),
            ResourceLayout.Sanitize(title, group.Id.ToString()),
            siblings.Select(g => g.FolderSegment).ToHashSet(StringComparer.OrdinalIgnoreCase)), Now);
        Groups.Add(group);
        return group;
    }

    public Resource Document(string name = "source.txt", string content = "sentinel")
    {
        var source = Path.Combine(DataDirectory, "inputs", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        System.IO.File.WriteAllText(source, content);
        var stored = Storage.Store(ResourceLayout.FolderFor(Project, File), name, source);
        var resource = Resource.CreateStored(File.Id, ResourceKind.Document, name, name, stored, Now);
        resource.MarkIndexed(Now);
        Resources.Add(resource);
        Resources.SetIndexText(resource.Id, content);
        return resource;
    }

    public void Assign(Resource resource, ResourceGroupId? groupId)
    {
        resource.MoveToGroup(groupId, Now);
        Resources.Update(resource);
    }

    public void Dispose()
    {
        Database.Dispose();
        Directory.Delete(DataDirectory, recursive: true);
    }
}
~~~

The fixture property named File is a ProjectFile: use System.IO.File for byte operations within it. Tests reference BeBoosted.Domain.Projects.Resource, not an unrelated resource type.

- [ ] Add persistence tests that roundtrip membership through Add, Update, GetById, GetForFile and SearchInProject. Pin search with an index-only token so title matching cannot mask a lost index. Assert that a standalone group delete sets membership null but leaves the resource, while deleting a File cascades both group and resource rows.

~~~csharp
[Fact]
public void DirectGroupDelete_UngroupsWithoutDeletingTheResource()
{
    using var f = new ResourceGroupFixture();
    var g = f.Group();
    var r = f.Document();
    f.Assign(r, g.Id);
    f.Groups.Delete(g.Id);
    var remaining = Assert.IsType<Resource>(f.Resources.GetById(r.Id));
    Assert.Null(remaining.GroupId);
    Assert.True(f.Storage.Exists(remaining.StoredPath!));
    Assert.Null(f.Groups.GetById(g.Id));
}

[Fact]
public void GroupAndResourceWrites_ActuallyRollBackTogether()
{
    using var f = new ResourceGroupFixture();
    var g = f.Group();
    var r = f.Document();
    f.Assign(r, g.Id);
    Assert.Throws<InvalidOperationException>(() =>
        new SqliteProjectMutations(f.Database.Factory).Execute((_, _, resources, _, groups) =>
        {
            resources.Delete(r.Id);
            groups.Delete(g.Id);
            Assert.Null(resources.GetById(r.Id));
            Assert.Null(groups.GetById(g.Id));
            throw new InvalidOperationException("before commit");
        }));
    Assert.NotNull(f.Groups.GetById(g.Id));
    Assert.Equal(g.Id, f.Resources.GetById(r.Id)!.GroupId);
    Assert.True(f.Storage.Exists(r.StoredPath!));
}
~~~

- [ ] Run the persistence filter; record red. For upgrade coverage, create a separate TempDatabase, apply versions <=12, insert Project/File/Resource using SQL (not the new mapper), then apply all migrations twice. Assert existing resource ID/path/index_text are identical, group_id is NULL, group table empty, and a fresh connection can read it. Existing FolderSegmentMigrationTests demonstrates the required pre-migration SQL seeding pattern.
- [ ] Add the migration exactly as below; no table rebuild, default groups, or parent backfill changes:

~~~sql
CREATE TABLE resource_groups (
    id TEXT PRIMARY KEY NOT NULL,
    file_id TEXT NOT NULL REFERENCES project_files (id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    folder_segment TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
) STRICT;
CREATE INDEX idx_resource_groups_file ON resource_groups (file_id);
ALTER TABLE resources ADD COLUMN group_id TEXT
    REFERENCES resource_groups (id) ON DELETE SET NULL;
CREATE INDEX idx_resources_group ON resources (group_id);
~~~

- [ ] Implement SqliteResourceGroupRepository using the same factory/shared-connection/OpenSession pattern as SqliteProjectFileRepository. All five methods must call OpenSession; only OpenSession may open a factory connection. Use these SQL shapes, parameterize all values, map all seven columns in this order, and reject blank FolderSegment before Add/Update:

~~~sql
INSERT INTO resource_groups (id, file_id, title, folder_segment, sort_order, created_at, modified_at)
VALUES ($id, $fileId, $title, $folderSegment, $sortOrder, $createdAt, $modifiedAt);
UPDATE resource_groups SET title = $title, folder_segment = $folderSegment,
    sort_order = $sortOrder, modified_at = $modifiedAt WHERE id = $id;
DELETE FROM resource_groups WHERE id = $id;
SELECT id, file_id, title, folder_segment, sort_order, created_at, modified_at
FROM resource_groups WHERE id = $id;
SELECT id, file_id, title, folder_segment, sort_order, created_at, modified_at
FROM resource_groups WHERE file_id = $fileId ORDER BY sort_order, created_at, id;
~~~

Bind timestamps with invariant "O", order as integer, IDs as strings. Update affecting zero rows throws DomainException("That group no longer exists."). Delete is idempotent. Map uses ResourceGroup.Rehydrate; reads never generate new IDs.

- [ ] Append group_id to SqliteResourceRepository.Columns, INSERT values, Bind, UPDATE statement and its **separate parameter block**, Map ordinal 11, and the **separate explicit SearchInProject SELECT list**. Keep index_text unchanged on membership updates.

~~~csharp
command.Parameters.AddWithValue("$groupId", (object?)resource.GroupId?.ToString() ?? DBNull.Value);
// Final argument to Resource.Rehydrate in Map:
// reader.IsDBNull(11) ? null : ResourceGroupId.Parse(reader.GetString(11))
~~~

- [ ] Add the fifth callback repository, instantiated with the very same connection and transaction, to SqliteProjectMutations. Update every callback and test double, including FailAfterMutation and the direct transaction test in ProjectServiceTests. Register IResourceGroupRepository → SqliteResourceGroupRepository in AddBeBoostedInfrastructure.
- [ ] Add InMemoryResourceGroupRepository to TestDoubles. Clone on Add/Update/read using Rehydrate; sort by SortOrder/CreatedAt/ID. Make InMemoryResourceRepository clone on Add/Update/read too, including GroupId, so a failed update cannot mutate cached rows. Model group deletion's SET NULL through resource Update; add a Groups property to InMemoryProjectFileRepository and cascade groups on File deletion. TestShell creates/wires one shared group repo and supplies it to InMemoryProjectMutations. In-memory mutations still do not prove rollback.
- [ ] Enumerate all five group methods' session routing and all resource mapping paths. Add tests for missing group FK rejection, File cascade, Project cascade, blank-segment Add/Update rejection, and deterministic order when the clock is fixed. Run persistence tests and the full solution.
- [ ] Commit: feat: persist resource groups in project transactions.

### Task 3: Group-aware layout without changing reservation rules

**Files:** ResourceLayout.cs, ResourceLayoutReconciler.cs, new ResourceGroupLayoutTests.cs and fixture; constructor callers in ResourceLayoutReconcilerTests, ProjectServiceTests, FolderIdentityBackfillTests.

**Consumes:** IResourceGroupRepository.GetForFile and persisted Resource.GroupId.

**Produces:**

~~~csharp
public static string FolderFor(Project project, ProjectFile file, ResourceGroup? group = null)
    => group is null
        ? Path.Combine(project.FolderSegment, file.FolderSegment)
        : Path.Combine(project.FolderSegment, file.FolderSegment, group.FolderSegment);

// Append one required dependency to ResourceLayoutReconciler's constructor:
// (..., IResourceStorage storage, IClock clock, IResourceGroupRepository groups)

// Add to ResourceGroupFixture:
public ResourceLayoutReconciler Reconciler(
    IResourceStorage? storage = null, IResourceRepository? resources = null)
    => new(Projects, Files, resources ?? Resources, storage ?? Storage, this, Groups);
~~~

- [ ] Add this regression before implementing the group destination:

~~~csharp
[Fact]
public void Reconcile_UsesPersistedGroupSegment_AfterRestart_AndThenMovesNothing()
{
    using var f = new ResourceGroupFixture();
    var obstacle = f.Document("Notes", "loose file");
    var group = f.Group("Notes");
    Assert.Equal("Notes (2)", group.FolderSegment);
    var one = f.Document("one.txt", "one");
    var two = f.Document("two.txt", "two");
    f.Assign(one, group.Id);
    f.Assign(two, group.Id);
    Assert.Equal(2, f.Reconciler().ReconcileProject(f.Project.Id));
    var destination = Path.Combine(f.Project.FolderSegment, f.File.FolderSegment, "Notes (2)");
    foreach (var id in new[] { one.Id, two.Id })
        Assert.Equal(destination, Path.GetDirectoryName(f.Resources.GetById(id)!.StoredPath));
    Assert.Equal("loose file", System.IO.File.ReadAllText(f.Storage.ResolvePath(obstacle.StoredPath!)));
    // New repositories/reconciler over the same durable database, not the same group objects.
    var restarted = new ResourceLayoutReconciler(
        new SqliteProjectRepository(f.Database.Factory),
        new SqliteProjectFileRepository(f.Database.Factory),
        new SqliteResourceRepository(f.Database.Factory), f.Storage, f,
        new SqliteResourceGroupRepository(f.Database.Factory));
    Assert.Equal(0, restarted.ReconcileProject(f.Project.Id));
    Assert.Equal(0, restarted.Reconcile());
}
~~~

- [ ] Run ResourceGroupLayoutTests and see failure (two moves are missing under the old layout).
- [ ] Keep the existing file-level legacy guard. Build a group dictionary once per File; resolve each stored resource's group before calculating its desired folder. A wrong/missing/unclaimed group is not loose:

~~~csharp
var fileGroups = groups.GetForFile(file.Id).ToDictionary(g => g.Id);
// Inside the existing resource loop, after the no-StoredPath check:
ResourceGroup? group = null;
if (resource.GroupId is { } groupId)
{
    if (project.FolderSegment.Length == 0 || file.FolderSegment.Length == 0
        || !fileGroups.TryGetValue(groupId, out group)
        || group.FileId != file.Id || group.FolderSegment.Length == 0)
        continue;
}
var folder = ResourceLayout.FolderFor(project, file, group);
~~~

Remove only the old per-File folder local. Keep IsAlreadyPlaced, global claimed-path collection, MoveInto, post-move persistence and per-resource failure isolation intact.

- [ ] Handle one new crash-recovery collision without broadening Exists: a loose file's desired name can be a claimed group directory, with its actual moved bytes at the numbered candidate. The old adoption loop stops at the first file-only Exists == false, so it never reaches that candidate. Pass the File's known group-directory claims to FindUnrecordedPlacement:

~~~csharp
// Once per File, alongside fileGroups:
var directoryClaims = fileGroups.Values.Where(g => g.FolderSegment.Length > 0)
    .Select(g => ResourceLayout.FolderFor(project, file, g))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

// Change the private signature to accept IReadOnlySet<string> directoryClaims.
// In the existing candidate loop, BEFORE the file-only Exists check:
if (directoryClaims.Contains(candidate)) continue;
// Existing checks follow: missing file ends the contiguous probe;
// a globally claimed resource file cannot be adopted.
~~~

Call FindUnrecordedPlacement(folder, desired, claimed, directoryClaims). This only skips known reserved group paths; it does not scan arbitrary orphan files or adopt directories.

Add a regression named Reconcile_AfterFailedMoveOut_SkipsGroupDirectoryAndAdoptsNumberedFile: create empty group Notes and source group Sources; create a resource originally named Notes, assign to Sources and settle; clear membership to loose, then use the existing resource-repository wrapper to throw on Update after MoveInto. Assert the directory Notes survives, bytes now exist at loose Notes (2), and the recorded path still names Sources/Notes. On a fresh healthy ReconcileProject assert one adoption, exact Notes (2) recorded path, original content, and then zero on the next pass. Neither skipping the whole File nor broadening Exists may satisfy the assertions.

- [ ] Add a group/loose theory: group first then extensionless import named Notes gives loose Notes (2); loose Notes first then group gives group Notes (2). Include empty-group-then-import and same-title groups with different IDs/segments. Assert directory existence **before** import, exact resource paths/contents afterward, and zero moves on the second pass.
- [ ] Add corrupt-membership and unclaimed-parent tests using repository/SQL fixtures: wrong-File group, unknown group via a test repository, and grouped resource under empty parent segments all leave StoredPath/content unchanged. Keep an unrelated loose resource requiring a legitimate move in each reconcile test so a blanket no-op cannot pass.
- [ ] Port the existing repository-update-after-move failure/adoption test into a group destination. Fail the first resource Update after bytes move, restart, assert adoption at the group path and then zero moves. Add the cross-project claimed-path case through **ReconcileProject**, with another owner's document at the otherwise adoptable candidate; it must not be adopted. Use the existing delegating IResourceRepository sabotage in ResourceLayoutReconcilerTests as the concrete wrapper pattern; it must forward GetForFile and all global-owner reads.
- [ ] Run group layout tests, existing ResourceLayoutTests, ResourceLayoutReconcilerTests and FolderIdentityBackfillTests. Assert the existing adoption and startup gate tests still execute their intended paths.
- [ ] Commit: feat: reconcile grouped resources using durable folder identities.

### Task 4: Create, rename and move use cases

**Files:** ProjectService.cs; ResourceGroupServiceTests.cs; ResourceGroupFixture.cs. Update ProjectService construction in ProjectServiceTests (including secondary/helper constructors), AiServiceTests and TestShell.

**Consumes:** Tasks 1–3 interfaces and existing ReserveFolderSegment, AfterCommit and ResourceLayoutReconciler.

**Produces:** Append required IResourceGroupRepository groups immediately after IClock clock, before optional provenanceInvalidator/reconciler, in ProjectService's primary constructor. Update positional callers in the same task.

~~~csharp
public ResourceGroup CreateGroup(ProjectFileId fileId, string title);
public ResourceGroup RenameGroup(ResourceGroupId id, string title);
public void MoveResourceToGroup(ResourceId id, ResourceGroupId? groupId);
public IReadOnlyList<ResourceGroup> GetGroups(ProjectFileId fileId);
public int CountResourcesInGroup(ResourceGroupId id);
~~~

- [ ] Extend ResourceGroupFixture with a factory whose mutations, storage and invalidator are genuinely swappable. Add the relevant Application.Ai/Tasks/Calendar and Infrastructure.Tasks/Calendar imports:

~~~csharp
public ProjectService CreateService(IProjectMutations? mutations = null,
    IResourceStorage? storage = null, IProvenanceInvalidator? invalidator = null)
{
    var bytes = storage ?? Storage;
    return new ProjectService(Projects, Files, Resources, bytes,
        mutations ?? new SqliteProjectMutations(Database.Factory),
        new SimpleLocalIndexer(Resources, bytes, this),
        new SqliteTaskRepository(Database.Factory),
        new SqliteCalendarBlockRepository(Database.Factory),
        new SqliteOccurrenceCompletionRepository(Database.Factory), this, Groups,
        invalidator, Reconciler(bytes));
}
~~~

- [ ] Create tests/BeBoosted.Tests/Support/RecordingGroupInvalidator.cs now, so this task's no-invalidation test builds independently of Task 5:

~~~csharp
using BeBoosted.Application.Ai;
using BeBoosted.Domain;

namespace BeBoosted.Tests.Support;

public sealed class RecordingGroupInvalidator : IProvenanceInvalidator
{
    public List<ResourceId> Calls { get; } = [];
    public ResourceId? ThrowFor { get; set; }
    public void InvalidateForResource(ResourceId id)
    {
        Calls.Add(id);
        if (id == ThrowFor) throw new InvalidOperationException("invalidation refused");
    }
}
~~~

- [ ] Add exact-path success tests and validation tests. These examples pin the durable segment and index-only search behavior:

~~~csharp
[Fact]
public void SanitizationEquivalentRename_RetainsItsOwnedSegment()
{
    using var f = new ResourceGroupFixture();
    var service = f.CreateService();
    var group = service.CreateGroup(f.File.Id, "Notes");
    var r = f.Document();
    service.MoveResourceToGroup(r.Id, group.Id);
    var stored = f.Resources.GetById(r.Id)!.StoredPath;
    var renamed = service.RenameGroup(group.Id, "notes");
    Assert.Equal("notes", renamed.Title);
    Assert.Equal(group.FolderSegment, renamed.FolderSegment);
    Assert.Equal(stored, f.Resources.GetById(r.Id)!.StoredPath);
    Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
}

[Fact]
public void MoveIntoBetweenAndOut_KeepsIdentityBytesAndSearchText()
{
    using var f = new ResourceGroupFixture();
    var service = f.CreateService();
    var a = service.CreateGroup(f.File.Id, "Unit 3");
    var b = service.CreateGroup(f.File.Id, "Unit 4");
    var r = f.Document("source.txt", "search-only-token");
    foreach (var destination in new ResourceGroupId?[] { a.Id, b.Id, null })
    {
        service.MoveResourceToGroup(r.Id, destination);
        var current = f.Resources.GetById(r.Id)!;
        Assert.Equal(destination, current.GroupId);
        Assert.Equal(r.Id, current.Id);
        Assert.Equal(ResourceIndexState.Indexed, current.IndexState);
        Assert.Equal("search-only-token",
            System.IO.File.ReadAllText(f.Storage.ResolvePath(current.StoredPath!)));
        Assert.Equal(r.Id, Assert.Single(
            f.Resources.SearchInProject(f.Project.Id, "search-only-token")).Id);
    }
    Assert.Equal(Path.Combine(f.Project.FolderSegment, f.File.FolderSegment),
        Path.GetDirectoryName(f.Resources.GetById(r.Id)!.StoredPath));
}
~~~

- [ ] Run ResourceGroupServiceTests and establish red.
- [ ] Implement the parent guard and create/rename methods. RequireClaimedGroupParent is deliberately new and group-specific; do not retrofit it onto existing import/create-file behavior:

~~~csharp
private (Project Project, ProjectFile File) RequireClaimedGroupParent(ProjectFileId fileId)
{
    var file = files.GetById(fileId)
        ?? throw new DomainException("That file no longer exists.");
    var project = Require(file.ProjectId);
    if (project.FolderSegment.Length == 0 || file.FolderSegment.Length == 0)
        throw new DomainException("This File's storage folders are not ready. Reopen the app and try again.");
    return (project, file);
}

public IReadOnlyList<ResourceGroup> GetGroups(ProjectFileId fileId)
    => groups.GetForFile(fileId);

public int CountResourcesInGroup(ResourceGroupId id)
    => groups.GetById(id) is { } group
        ? resources.GetForFile(group.FileId).Count(r => r.GroupId == id) : 0;

public ResourceGroup CreateGroup(ProjectFileId fileId, string title)
{
    var (project, file) = RequireClaimedGroupParent(fileId);
    var siblings = groups.GetForFile(fileId);
    var order = siblings.Count == 0 ? 0 : checked(siblings.Max(g => g.SortOrder) + 1);
    var group = ResourceGroup.Create(fileId, title, order, clock.Now);
    var claimed = siblings.Select(g => g.FolderSegment).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
    group.Rename(title, clock.Now);
    var claimed = groups.GetForFile(file.Id).Where(g => g.Id != id)
        .Select(g => g.FolderSegment).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var segment = storage.ReserveFolderSegment(ResourceLayout.FolderFor(project, file),
        ResourceLayout.Sanitize(group.Title, group.Id.ToString()), claimed, group.FolderSegment);
    group.RelocateTo(segment, clock.Now);
    groups.Update(group);
    AfterCommit(() => reconciler?.ReconcileProject(project.Id));
    return group;
}

public void MoveResourceToGroup(ResourceId id, ResourceGroupId? groupId)
{
    var resource = resources.GetById(id)
        ?? throw new DomainException("That resource no longer exists.");
    var (project, _) = RequireClaimedGroupParent(resource.FileId);
    if (groupId is { } target)
    {
        var group = groups.GetById(target)
            ?? throw new DomainException("That group no longer exists.");
        if (group.FileId != resource.FileId)
            throw new DomainException("Resources can only move to groups in the same File.");
    }
    if (resource.GroupId == groupId) return;
    resource.MoveToGroup(groupId, clock.Now);
    resources.Update(resource);
    AfterCommit(() => reconciler?.ReconcileProject(project.Id));
}
~~~

The single-row membership Update is atomic on its own. Persistence failure must propagate and no reconciliation may run; a later move failure keeps the new membership and old usable path for retry. Do not call indexer or invalidator here.

- [ ] Complete the service regression matrix with positive assertions:

| Test | Setup and assertions that must hold |
|---|---|
| CreateEmptyGroup_ClaimsDirectoryBeforeImport | Create group; assert Directory.Exists at exact segment before any resource exists; import extensionless matching filename, assert numbered loose path and content |
| SameTitleGroups_StayDistinct | Create two groups with equal title and fixed clock; assert distinct IDs/segments, increasing order, each resource under its own persisted segment after two reconciles |
| RealRename_MovesEveryMemberOnly | Two stored members, one grouped note, one loose document, and another group's document; rename; assert both member paths changed, note membership kept, all unrelated paths/contents unchanged |
| RenameIntoCollision_KeepsOneSharedDestination | Occupy desired folder/name; rename; assert all members use the one newly persisted segment and second reconcile returns zero |
| CrossFileMove_ThrowsBeforeAnyMutation | Two Files in the same Project; attempt wrong target; assert throw, original membership, exact path and content |
| MissingTarget_ThrowsAndPreservesSource | Delete target group first; assert throw, source still exists at original path |
| UnclaimedParent_CreateRenameMoveDoNotTouchRowsOrBytes | Rehydrate Project or File with empty segment, persist; exercise each new action; assert DomainException, original title/membership/path and sibling bytes retained |
| CreationApis_StillProduceLooseResources | Call AddLink, AddNote, ImportFile for Document and Image while groups exist; assert every GroupId null and imported bytes in File folder |
| MembershipAndRename_DoNotInvalidate | Supply this task's RecordingGroupInvalidator; move, rename and move loose; assert zero calls and original index state |

Use raw SQL or Rehydrate to seed an unclaimed parent; RelocateTo intentionally forbids creating the sentinel. For group storage failure use a delegating IResourceStorage whose ReserveFolderSegment throws IOException and whose MoveInto can return null. Assert create/rename do not persist changes on reservation failure; move commits membership despite a null MoveInto, leaves original bytes/path, and a healthy reconciler later converges. The wrapper must forward Store, ResolvePath, Exists and Delete; never change Exists to count directories.

- [ ] Update all constructor callers including AiServiceTests' positional invalidator argument. In TestShell add groups to ProjectService; no optional production dependency. Run full core and desktop suites to catch fixture construction drift.
- [ ] Commit: feat: create rename and move resource groups.

### Task 5: Atomic Ungroup and Delete group

**Files:** ProjectService.cs; new ResourceGroupRemovalTests.cs; ResourceGroupFixture.cs; AiServiceTests.cs for real provenance integration.

**Consumes:** Five-repository mutations, group/member queries, existing AfterCommit helper.

**Produces:** public void UngroupGroup(ResourceGroupId id) and public void DeleteGroup(ResourceGroupId id).

- [ ] Add this failing mutation double in the core test Support namespace. It must execute the real callback **before** throwing, with all repositories enlisted in its transaction:

~~~csharp
public sealed class FailGroupMutation(SqliteConnectionFactory factory) : IProjectMutations
{
    public void Execute(Action<IProjectRepository, IProjectFileRepository,
        IResourceRepository, ITaskRepository, IResourceGroupRepository> mutation)
    {
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction();
        mutation(new SqliteProjectRepository(connection, transaction),
            new SqliteProjectFileRepository(connection, transaction),
            new SqliteResourceRepository(connection, transaction),
            new BeBoosted.Infrastructure.Tasks.SqliteTaskRepository(connection, transaction),
            new SqliteResourceGroupRepository(connection, transaction));
        throw new InvalidOperationException("after mutation, before commit");
    }
}
~~~

Put FailGroupMutation in tests/BeBoosted.Tests/Support/FailGroupMutation.cs with the application project/task and infrastructure persistence/project imports. Reuse RecordingGroupInvalidator from Task 4.

- [ ] Pin both rollback paths with this theory; assert both the exception and persisted state:

~~~csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public void GroupRemovalRollback_PreservesRowsBytesAndProvenance(bool delete)
{
    using var f = new ResourceGroupFixture();
    var group = f.Group();
    var one = f.Document("one.txt", "one");
    var two = f.Document("two.txt", "two");
    f.Assign(one, group.Id);
    f.Assign(two, group.Id);
    f.Reconciler().ReconcileProject(f.Project.Id);
    var before = new[] { one.Id, two.Id }.Select(id => f.Resources.GetById(id)!).ToList();
    var invalidator = new RecordingGroupInvalidator();
    var service = f.CreateService(new FailGroupMutation(f.Database.Factory), invalidator: invalidator);
    Assert.Throws<InvalidOperationException>(() =>
    {
        if (delete) service.DeleteGroup(group.Id); else service.UngroupGroup(group.Id);
    });
    Assert.NotNull(f.Groups.GetById(group.Id));
    foreach (var old in before)
    {
        var current = Assert.IsType<Resource>(f.Resources.GetById(old.Id));
        Assert.Equal(group.Id, current.GroupId);
        Assert.Equal(old.StoredPath, current.StoredPath);
        Assert.Equal(old.Title == "one.txt" ? "one" : "two",
            System.IO.File.ReadAllText(f.Storage.ResolvePath(current.StoredPath!)));
    }
    Assert.Empty(invalidator.Calls);
}
~~~

- [ ] Run ResourceGroupRemovalTests; establish red. Add a happy Ungroup test with two grouped documents, a link and note, a loose same-named document and a separate group. Assert only target memberships clear, all content survives, collision is numbered, no invalidation, and second reconcile is zero. A happy Delete test asserts exact removed IDs, bytes absent, every member invalidated (including links/notes), and loose/other-group data unchanged.
- [ ] Implement both removal actions with the following transaction and side-effect boundaries:

~~~csharp
public void UngroupGroup(ResourceGroupId id)
{
    var group = groups.GetById(id);
    if (group is null) return;
    var file = files.GetById(group.FileId);
    var project = file is null ? null : projects.GetById(file.ProjectId);
    mutations.Execute((_, _, resourceRepo, _, groupRepo) =>
    {
        foreach (var resource in resourceRepo.GetForFile(group.FileId).Where(r => r.GroupId == id))
        {
            resource.MoveToGroup(null, clock.Now);
            resourceRepo.Update(resource);
        }
        groupRepo.Delete(id);
    });
    if (file is not null && project is not null
        && file.FolderSegment.Length > 0 && project.FolderSegment.Length > 0)
        AfterCommit(() => reconciler?.ReconcileProject(project.Id));
}

public void DeleteGroup(ResourceGroupId id)
{
    var group = groups.GetById(id);
    if (group is null) return;
    var doomed = resources.GetForFile(group.FileId).Where(r => r.GroupId == id).ToList();
    var paths = doomed.Select(r => r.StoredPath).OfType<string>().ToList();
    mutations.Execute((_, _, resourceRepo, _, groupRepo) =>
    {
        // group_id is SET NULL, not CASCADE: deleting the root alone would preserve members.
        foreach (var resource in doomed) resourceRepo.Delete(resource.Id);
        groupRepo.Delete(id);
    });
    foreach (var path in paths) AfterCommit(() => storage.Delete(path));
    foreach (var resource in doomed)
        AfterCommit(() => provenanceInvalidator?.InvalidateForResource(resource.Id));
}
~~~

No byte operations occur inside the callback. No calls to public DeleteResource from DeleteGroup. Ungroup uses transaction-bound resource reads/updates and does not invalidate.

- [ ] Add the cross-platform side-effect isolation regression using a standalone delegating IResourceStorage (not a LocalResourceStorage subclass). Throw on the **specific first member's path**, not on an incidental enumeration index. Preserve two real documents, assert the first remains on disk, the second is removed, both rows/group are gone, and invalidation continues despite the first invalidator throwing:

~~~csharp
public sealed class RefusingGroupDeleteStorage(IResourceStorage inner, string refused) : IResourceStorage
{
    public string Store(string folder, string name, string source) => inner.Store(folder, name, source);
    public string? MoveInto(string current, string folder, string name) => inner.MoveInto(current, folder, name);
    public string ReserveFolderSegment(string parent, string preferred,
        IReadOnlySet<string> claimed, string? ownedSegment = null)
        => inner.ReserveFolderSegment(parent, preferred, claimed, ownedSegment);
    public string ResolvePath(string path) => inner.ResolvePath(path);
    public bool Exists(string path) => inner.Exists(path);
    public void Delete(string path)
    {
        if (path == refused) throw new InvalidOperationException("refused byte cleanup");
        inner.Delete(path);
    }
}
~~~

Make the two AddedAt timestamps distinct so resource order is deterministic before selecting the first. Assert recorded invalidator IDs equal the exact member-ID set, not merely Count == 2. Reverting to one wrapper around a loop must fail this test.

- [ ] Extend AiServiceTests with actual derivations from at least two grouped resources, using its existing extraction/provenance setup. DeleteGroup marks both derivations Needs review; UngroupGroup leaves both unchanged. Also exercise existing DeleteFile/DeleteProject with grouped and loose members: groups/resources disappear, all member provenance is invalidated, task unlink/cascade behavior remains unchanged. The recording invalidator proves calls; the AI integration test proves their meaning.
- [ ] Test repeated removal of an already-gone group as no-op. Test Ungroup with an unclaimed parent: group gone, resources positively present/loose at unchanged stored paths, no sweep into root. Existing four deletion rollback tests remain enabled and assert throw/rows/bytes/task state.
- [ ] Run group removal tests, ProjectServiceTests, AiServiceTests and the full solution.
- [ ] Commit: feat: remove resource groups with atomic database mutations.

### Task 6: File-detail groups, move targets and one canonical selection

**Files:** FileDetailViewModel.cs (also contains ResourceRowViewModel), new ResourceGroupViewModel.cs and ResourceMoveTargetViewModel.cs, new ResourceGroupsViewModelTests.cs; TestDoubles.cs only for injection plumbing required by these tests.

**Consumes:** ProjectService group APIs. Keep existing Resources and Selected callers working.

**Produces:**

~~~csharp
// FileDetailViewModel additions:
public ObservableCollection<ResourceGroupViewModel> Groups { get; } = [];
public ObservableCollection<ResourceRowViewModel> LooseResources { get; } = [];
public bool HasGroups => Groups.Count != 0;
public bool HasLooseResources => LooseResources.Count != 0;
public bool ShowEmptyState => !HasGroups && !HasResources;
public bool ShowLooseHeader => HasGroups && HasLooseResources;
// Observable NewGroupTitle (string, initially empty) and GroupNotice (string?).
public bool TryCreateGroup();
internal bool TryRenameGroup(ResourceGroupId id, string title);
internal bool TryMoveResource(ResourceId id, ResourceGroupId? groupId);
internal bool TryUngroup(ResourceGroupId id);
internal void RequestDeleteGroup(ResourceGroupViewModel group);
internal IReadOnlyList<ResourceMoveTargetViewModel> MoveTargetsFor(ResourceRowViewModel row);
public ResourceRowViewModel? LooseSelectedResource { get; set; } // delegated below

// ResourceGroupViewModel : ViewModelBase, sealed partial; constructor:
public ResourceGroupViewModel(FileDetailViewModel owner, ResourceGroup group,
    IReadOnlyList<ResourceRowViewModel> resources);
// Immutable Group, Id => Group.Id, Title => Group.Title, Resources.
// Count => Resources.Count; CountText => $"{Count} item{(Count == 1 ? "" : "s")}".
// Observable IsExpanded (default true), RenameTitle; SelectedResource delegated below.
public void BeginRename();
public bool TryCommitRename();
// [RelayCommand] methods Ungroup and RequestDelete produce:
// UngroupCommand and RequestDeleteCommand.

// ResourceMoveTargetViewModel, immutable target identity/title:
public ResourceMoveTargetViewModel(FileDetailViewModel owner, ResourceId resourceId,
    ResourceGroupId? groupId, string title);
public ResourceGroupId? GroupId { get; }
public string Title { get; }
public bool TryMove();

// ResourceRowViewModel:
public IReadOnlyList<ResourceMoveTargetViewModel> MoveTargets => _owner.MoveTargetsFor(this);
public bool HasMoveTargets => MoveTargets.Count != 0;
~~~

- [ ] Build the VM fixture through TestShell, as existing ProjectRenameDeleteViewModelTests does. This opens a real File detail; don't manufacture a detached FileDetail with a different service:

~~~csharp
private static FileDetailViewModel OpenFile()
{
    var projects = TestShell.Create().Projects;
    projects.NewProjectName = "Schoolwork";
    Assert.True(projects.TryCreateProject());
    projects.Detail!.NewFileTitle = "Spanish";
    Assert.True(projects.Detail.TryCreateFile());
    return projects.FileDetail!;
}

[Fact]
public void MoveTargets_ExcludeCurrentContainer_AndMoveWithoutManualRefresh()
{
    var file = OpenFile();
    file.NewNoteTitle = "Vocab";
    file.NewNoteContent = "hola";
    Assert.True(file.TryAddNote());
    var id = Assert.Single(file.Resources).Resource.Id;
    file.NewGroupTitle = "Unit 3";
    Assert.True(file.TryCreateGroup());
    file.NewGroupTitle = "Unit 4";
    Assert.True(file.TryCreateGroup());
    var firstId = file.Groups[0].Id;
    var secondId = file.Groups[1].Id;
    var looseRow = Assert.Single(file.LooseResources);
    Assert.DoesNotContain(looseRow.MoveTargets, t => t.GroupId is null);
    Assert.True(looseRow.MoveTargets.Single(t => t.GroupId == firstId).TryMove());
    var moved = Assert.Single(file.Groups.Single(g => g.Id == firstId).Resources);
    Assert.Equal(id, moved.Resource.Id);
    Assert.Same(moved, file.Selected);
    Assert.DoesNotContain(moved.MoveTargets, t => t.GroupId == firstId);
    Assert.Contains(moved.MoveTargets, t => t.GroupId == secondId);
    Assert.True(moved.MoveTargets.Single(t => t.GroupId is null).TryMove());
    Assert.Equal(id, Assert.Single(file.LooseResources).Resource.Id);
    Assert.Equal("1 resource", file.CountText);
}
~~~

- [ ] Run ResourceGroupsViewModelTests; establish red.
- [ ] Implement Refresh in this order: snapshot selected resource ID and each group's IsExpanded by ID; load groups and resources; create each ResourceRowViewModel **once** with the existing SafeResolve/derivations; fill canonical Resources, Groups and LooseResources from those same objects; restore expansion by ID (new groups expanded); restore Selected by ID or first resource; notify all count/visibility/selection properties. Use a private bool _refreshingGroups during collection replacement to suppress transient selection callbacks, reset in finally. Do not clear/recreate the view's selected resource repeatedly during intermediate collection notifications.
- [ ] Delegate nested ListBox selection safely. Multiple lists must not write null back to the canonical selection when a row belongs to another list:

~~~csharp
// FileDetailViewModel:
public ResourceRowViewModel? LooseSelectedResource
{
    get => Selected is { } row && LooseResources.Contains(row) ? row : null;
    set { if (!_refreshingGroups && value is not null) Selected = value; }
}
partial void OnSelectedChanged(ResourceRowViewModel? value)
{
    OnPropertyChanged(nameof(LooseSelectedResource));
    foreach (var group in Groups) group.NotifySelectionChanged();
}

// ResourceGroupViewModel holds _owner:
public ResourceRowViewModel? SelectedResource
{
    get => _owner.Selected is { } row && Resources.Contains(row) ? row : null;
    set { if (value is not null) _owner.SelectResource(value.Resource.Id); }
}
internal void NotifySelectionChanged() => OnPropertyChanged(nameof(SelectedResource));
~~~

SelectResource retains its public signature, ignores callbacks while _refreshingGroups, finds the row in canonical Resources, expands its group if present, and sets Selected. After refresh call the selection notifications explicitly even when the selected value stayed null. Collapsing a group does not clear its reading-pane selection. Selecting a search result in a collapsed group expands that group.

- [ ] Implement group actions through a helper that reports a failed mutation as false and leaves existing collections intact; only a successful action triggers Refresh:

~~~csharp
private bool TryGroupMutation(Action mutation)
{
    GroupNotice = null;
    try { mutation(); }
    catch (Exception error)
    {
        // Presentation boundary: return failure, never convert rollback into success.
        GroupNotice = error.Message;
        return false;
    }
    Refresh();
    return true;
}

public bool TryCreateGroup()
{
    if (!TryGroupMutation(() => _service.CreateGroup(File.Id, NewGroupTitle))) return false;
    NewGroupTitle = string.Empty;
    return true;
}
internal bool TryRenameGroup(ResourceGroupId id, string title)
    => TryGroupMutation(() => _service.RenameGroup(id, title));
internal bool TryUngroup(ResourceGroupId id)
    => TryGroupMutation(() => _service.UngroupGroup(id));
internal bool TryMoveResource(ResourceId id, ResourceGroupId? groupId)
{
    if (!TryGroupMutation(() => _service.MoveResourceToGroup(id, groupId))) return false;
    SelectResource(id);
    return true;
}
internal IReadOnlyList<ResourceMoveTargetViewModel> MoveTargetsFor(ResourceRowViewModel row)
{
    var targets = Groups.Where(g => g.Id != row.Resource.GroupId)
        .Select(g => new ResourceMoveTargetViewModel(this, row.Resource.Id, g.Id, g.Title))
        .ToList();
    if (row.Resource.GroupId is not null)
        targets.Add(new ResourceMoveTargetViewModel(this, row.Resource.Id, null, "loose in this File"));
    return targets;
}
internal void RequestDeleteGroup(ResourceGroupViewModel group)
{
    var count = group.Count;
    Confirmation = new ConfirmationPrompt(
        $"Delete '{group.Title}'? Its {count} resource{(count == 1 ? "" : "s")} "
            + "and any stored files are deleted too.",
        "Delete group", IsTaskDeletion: false);
    _pendingConfirmedAction = () => { TryGroupMutation(() => _service.DeleteGroup(group.Id)); };
}
~~~

GroupVM delegates BeginRename (seed current Title), TryCommitRename, UngroupCommand and RequestDeleteCommand to the owner. MoveTarget.TryMove delegates resource ID and nullable destination ID. No per-group service instance, no optimistic membership edits, no new confirmation class. Do not alter existing File/resource deletion confirmation handlers beyond sharing the existing pending-action slot.

- [ ] Cover all projections/actions without calling Refresh after the action:

| Test | Required assertions |
|---|---|
| NoGroups_IsTheExistingFlatList | Resources and LooseResources contain the same row instances; HasGroups/ShowLooseHeader false; CountText unchanged |
| EmptyGroup_HasHeaderAndZeroCount | One group, count 0, no resources, ShowEmptyState false |
| ImportWithGroups_StaysLoose | Add link/note and fake stored document; group resources unchanged, new rows in LooseResources |
| RenameGroup_RefreshesHeaderAndTargets | Rename through group VM; new exact title in header and another row's move target; resources/path selection retained |
| Collapse_SurvivesRefresh_SearchSelectionExpands | Collapse group; add unrelated loose note; collapsed still; SelectResource(member ID) expands correct group and selects exact row |
| SelectingBetweenLists_DoesNotClearTheReader | Select grouped then loose then other group; exactly one non-null delegated selection, same canonical row/derivation |
| Ungroup_RequiresNoConfirmation_AndPreservesRows | Command removes only header, all member IDs remain loose, Confirmation null |
| DeleteGroup_ConfirmAndCancel | Theory over 0/1/2 resources: exact title/count/pluralized prompt; Keep preserves header/resources; Confirm removes only target resources |
| FailedMutation_ShowsNoticeWithoutOptimisticRefresh | TestShell gains optional projectMutations injected at creation; throwing mutation leaves group/resources/Selected intact and GroupNotice nonempty |
| BlankCreateOrRename_KeepsDialogData | Returns false, visible notice, typed text retained, persisted/current group title unchanged |
| TotalCountAndSearchIncludeCollapsedMembers | Collapsed members still counted in File deletion prompt and selectable by resource ID |

For FailedMutation use an IProjectMutations implementation that throws before invoking the callback; SQLite rollback is tested in Task 5, not in these nontransactional doubles. Keep detached resource/group snapshots from Task 2.

- [ ] Run ResourceGroupsViewModelTests plus ProjectsViewModelTests, ProjectRenameDeleteViewModelTests and ShellProjectRefreshTests. Do not add manual ReloadList/Refresh calls to make assertions pass.
- [ ] Commit: feat: expose resource groups and move targets in file detail.

### Task 7: Group headers and Move-to flyouts in the existing view

**Files:** ProjectsView.axaml, ProjectsView.axaml.cs, new ResourceGroupsInteractionTests.cs.

**Consumes:** Task 6 properties, Try methods and commands.

**Produces:** New group flyout; collapsible group header with count, Rename/Ungroup/Delete group actions; resource Move to… flyout; conditional loose heading. Reuse existing typography, spacing, brushes, confirmation panel and resource row styling.

- [ ] Add headless tests first. Host MainWindow using TestShell and navigate to Projects as ProjectEntryPointTests does. Create Schoolwork → Spanish using the existing VM entry points; setup may use service/VM actions, but the action under test must use the rendered control. Verify no loose heading before any group, an empty group's visible header/count after creation, and the new flyout target bindings.
- [ ] Run ResourceGroupsInteractionTests; establish missing-control/binding red. Name controls through AutomationProperties so tests locate meaning, not child indices.
- [ ] Extract the existing resource DataTemplate into UserControl.Resources with x:Key="ResourceRowTemplate" and x:DataType="vm:ResourceRowViewModel". Keep existing kind/title/meta markup. Add the following DockPanel child **before** the filling StackPanel:

~~~xml
<Button DockPanel.Dock="Right" Content="Move to…" Margin="8,0,0,0"
        IsVisible="{Binding HasMoveTargets}"
        AutomationProperties.Name="{Binding Title, StringFormat='Move {0}'}">
  <Button.Flyout>
    <Flyout Placement="BottomEdgeAlignedRight" ShowMode="Standard">
      <ItemsControl ItemsSource="{Binding MoveTargets}">
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:ResourceMoveTargetViewModel">
            <Button Content="{Binding Title}" Click="OnMoveResourceClick"
                    HorizontalAlignment="Stretch"
                    AutomationProperties.Name="{Binding Title, StringFormat='Move to {0}'}" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </Flyout>
  </Button.Flyout>
</Button>
~~~

Keep no-groups rendering flat, using one loose ListBox with ItemTemplate="{StaticResource ResourceRowTemplate}" and SelectedItem bound TwoWay to LooseSelectedResource. For grouped content, place the groups ItemsControl before that loose list in one vertical ScrollViewer:

~~~xml
<ItemsControl ItemsSource="{Binding Groups}" IsVisible="{Binding HasGroups}">
  <ItemsControl.ItemTemplate>
    <DataTemplate x:DataType="vm:ResourceGroupViewModel">
      <Expander IsExpanded="{Binding IsExpanded, Mode=TwoWay}"
                HorizontalAlignment="Stretch"
                AutomationProperties.Name="{Binding Title, StringFormat='Group {0}'}">
        <Expander.Header>
          <StackPanel Orientation="Horizontal" Spacing="8">
            <TextBlock Text="{Binding Title}" FontWeight="SemiBold" />
            <TextBlock Text="{Binding CountText}" Classes="meta" />
            <Button Content="Rename" Click="OnBeginGroupRenameClick"
                    AutomationProperties.Name="{Binding Title, StringFormat='Rename group {0}'}">
              <Button.Flyout>
                <Flyout Placement="BottomEdgeAlignedRight" ShowMode="Standard">
                  <StackPanel Width="260" Spacing="10">
                    <TextBlock Text="Rename group" FontWeight="SemiBold" />
                    <TextBox Text="{Binding RenameTitle}" PlaceholderText="Group title" />
                    <Button Content="Save" Click="OnRenameGroupClick" />
                  </StackPanel>
                </Flyout>
              </Button.Flyout>
            </Button>
            <Button Content="Ungroup" Command="{Binding UngroupCommand}"
                    AutomationProperties.Name="{Binding Title, StringFormat='Ungroup {0}'}" />
            <Button Content="Delete group" Command="{Binding RequestDeleteCommand}"
                    AutomationProperties.Name="{Binding Title, StringFormat='Delete group {0}'}" />
          </StackPanel>
        </Expander.Header>
        <ListBox ItemsSource="{Binding Resources}"
                 SelectedItem="{Binding SelectedResource, Mode=TwoWay}"
                 ItemTemplate="{StaticResource ResourceRowTemplate}"
                 Background="Transparent" Padding="0" />
      </Expander>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
<TextBlock Text="loose in this File" Classes="meta" Margin="26,12"
           IsVisible="{Binding ShowLooseHeader}" />
<ListBox ItemsSource="{Binding LooseResources}"
         SelectedItem="{Binding LooseSelectedResource, Mode=TwoWay}"
         ItemTemplate="{StaticResource ResourceRowTemplate}"
         IsVisible="{Binding HasLooseResources}" Background="Transparent" Padding="0" />
~~~

Use a wrapping/header grid if needed at the app's supported narrow width; don't force controls outside the pane. Group buttons must not toggle expansion as a side effect. Row movement must not bubble into an old ListBoxItem and reset selection after Refresh; handle the event.

- [ ] Add the toolbar New group flyout and GroupNotice. Keep Add document/image/link/note untouched:

~~~xml
<Button Content="New group" AutomationProperties.Name="New resource group">
  <Button.Flyout>
    <Flyout Placement="BottomEdgeAlignedRight" ShowMode="Standard">
      <StackPanel Width="260" Spacing="10">
        <TextBlock Text="New group" FontWeight="SemiBold" />
        <TextBox Text="{Binding NewGroupTitle}" PlaceholderText="Group title"
                 AutomationProperties.Name="New group title" />
        <Button Content="Create group" Click="OnCreateGroupClick"
                AutomationProperties.Name="Create resource group" />
      </StackPanel>
    </Flyout>
  </Button.Flyout>
</Button>
<!-- Dock with the existing ImportNotice; errors stay visible after failed actions. -->
<TextBlock Text="{Binding GroupNotice}" TextWrapping="Wrap"
           IsVisible="{Binding GroupNotice, Converter={x:Static ObjectConverters.IsNotNull}}" />
~~~

Change only the File list's empty-state visibility from !HasResources to ShowEmptyState. Don't repair or redesign the separate reading pane's preexisting null-selection behavior here.

- [ ] Implement codebehind handlers with existing CloseFlyout; return failure keeps a form open:

~~~csharp
private void OnCreateGroupClick(object? sender, RoutedEventArgs e)
{
    e.Handled = true;
    if (Vm?.FileDetail?.TryCreateGroup() == true) CloseFlyout(sender);
}
private void OnBeginGroupRenameClick(object? sender, RoutedEventArgs e)
{
    e.Handled = true;
    if (sender is Control { DataContext: ResourceGroupViewModel group }) group.BeginRename();
}
private void OnRenameGroupClick(object? sender, RoutedEventArgs e)
{
    e.Handled = true;
    if (sender is Control { DataContext: ResourceGroupViewModel group } && group.TryCommitRename())
        CloseFlyout(sender);
}
private void OnMoveResourceClick(object? sender, RoutedEventArgs e)
{
    e.Handled = true;
    if (sender is Control { DataContext: ResourceMoveTargetViewModel target } && target.TryMove())
        CloseFlyout(sender);
}
~~~

- [ ] Implement rendered interaction tests using pointer/keyboard events, not only VM calls. The existing ClickByName pattern in ProjectEntryPointTests supplies window-relative pointer events; use this exact control lookup:

~~~csharp
var button = window.GetVisualDescendants().OfType<Button>()
    .Single(b => b.IsEffectivelyVisible
        && AutomationProperties.GetName(b) == "Move Vocab");
var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2),
    window)!.Value;
window.MouseDown(point, MouseButton.Left);
window.MouseUp(point, MouseButton.Left);
window.CaptureRenderedFrame();
var flyout = Assert.IsType<Flyout>(button.Flyout);
Assert.True(flyout.IsOpen);
var content = Assert.IsAssignableFrom<Control>(flyout.Content);
var target = content.GetVisualDescendants().OfType<Button>()
    .Single(b => AutomationProperties.GetName(b) == "Move to Unit 3");
Assert.IsType<ResourceMoveTargetViewModel>(target.DataContext);
~~~

For target activation in a popup, use its actual TopLevel/focus and keyboard input supported by the headless host. Assert the target becomes focused, activate Enter, then assert exact membership/selected row and flyout closed. A test that invokes TryMove directly does not prove this binding path. Include opening/using the flyout by keyboard, not pointer only; if the headless popup backend cannot exercise it, record that limitation and execute the same path in Task 8's real-app check rather than claiming coverage.

- [ ] Cover visible no-groups flat behavior; empty group; collapse/expand; move into/between/out; rename; cancel/confirm Delete group; Ungroup with no prompt. Check header title/count and actual selected preview resource. Assert unrelated rows remain after every destructive action. Failures must retain typed text and show notice; success closes flyout. Run all desktop tests and a desktop build (compiled binding errors count as failures).
- [ ] Commit: feat: add resource group headers and move flyouts.

### Task 8: Integration gate and running-app verification

**Files:** New verification record in the file map; touch implementation only for failures attributable to phase 1, with a regression test first.

**Consumes:** Completed Tasks 1–7.

**Produces:** Evidence-backed phase-1 handoff; no phase-2 code, no merge.

- [ ] Enumerate new tests before running them; confirm discovered names cover all matrices above. Record tests that are headless, SQLite, storage, or manual. Don't label Daily.UnscheduledRows as Inbox or claim a surface that no assertion touches.
- [ ] Restore/build/test in a consistent configuration. The desktop diagnostics package has configuration-dependent assets: a Release restore followed by Debug --no-restore is not a valid verification sequence.

~~~powershell
dotnet test BeBoosted.slnx --configuration Debug --verbosity normal
dotnet build BeBoosted.slnx --configuration Release --verbosity minimal
dotnet test BeBoosted.slnx --configuration Release --verbosity normal
git diff --check
git status --short
~~~

Do not claim unchanged baseline totals once tests have been added; report actual results and all skips. Any platform skip introduced must be narrowly justified. Group delete isolation tests use ordinary injected exceptions and run everywhere.

- [ ] Add ResourceGroupsCompositionTests to the **desktop** test project (it already has full Microsoft.Extensions.DependencyInjection/Logging through the desktop project; don't add those dependencies to core tests). Use a disposable Paths implementation of IAppDataPaths with DataDirectory under Path.GetTempPath()/beboosted-group-di-{Guid}, LogsDirectory and ResourcesDirectory as children. Its Dispose clears only the pool for DataDirectory/beboosted.db then removes that explicit temporary directory. Resolve the production graph, not a manually assembled service:

~~~csharp
[Fact]
public void ProductionComposition_CanCreateMoveAndDeleteAGroup()
{
    using var paths = new Paths();
    using var provider = new ServiceCollection()
        .AddLogging()
        .AddBeBoostedInfrastructure(paths)
        .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    provider.GetRequiredService<MigrationRunner>().Apply(EmbeddedMigrations.Load());
    var startup = provider.GetRequiredService<ResourceLayoutStartup>().Run();
    Assert.False(startup.ReconcileDeferred);
    var service = provider.GetRequiredService<ProjectService>();
    var project = service.CreateProject("Schoolwork");
    var file = service.CreateFile(project.Id, "Spanish", null);
    var group = service.CreateGroup(file.Id, "Unit 3");
    var source = Path.Combine(paths.DataDirectory, "input.txt");
    File.WriteAllText(source, "composition sentinel");
    var resource = service.ImportFile(file.Id, ResourceKind.Document, source);
    Assert.Null(resource.GroupId);
    service.MoveResourceToGroup(resource.Id, group.Id);
    var grouped = Assert.Single(service.GetResources(file.Id));
    Assert.Equal(group.Id, grouped.GroupId);
    var stored = service.ResolveStoredPath(grouped)!;
    Assert.Equal("composition sentinel", File.ReadAllText(stored));
    service.DeleteGroup(group.Id);
    Assert.Empty(service.GetGroups(file.Id));
    Assert.Empty(service.GetResources(file.Id));
    Assert.False(File.Exists(stored));
}
~~~
- [ ] Run the app using a disposable test profile, never the user's real library for deletion tests. First inspect how AppDataPaths chooses its root; use only an already-supported override. If no safe profile override exists, ask the user for a throwaway sample library before doing destructive live verification. Do not add a profile feature to this branch.
- [ ] Manually verify Schoolwork → Spanish with two same-titled groups, two documents, a link and a note: create empty group; import loose; move into/between/out; collapse; select a grouped search result; rename to a case-equivalent name and a genuinely different name; Ungroup; cancel Delete group; confirm Delete group. Repeat key actions keyboard-only. Check disk paths and bytes in the disposable profile before/after, not just visible rows.
- [ ] Restart the app and verify empty groups, memberships and folder names persist. Run another reconcile and confirm zero moves for settled resources. Rename the parent File/Project and confirm grouped and loose documents remain readable in their correct hierarchy; locked/missing files remain usable at their recorded path when present and are retried without inventing group segments.
- [ ] Record screenshots or concrete observed control state at wide and narrow supported window sizes, including long group titles and Move-to targets. Record any headless/live-app limits honestly.
- [ ] Perform a whole-range review from 9626197 to feature HEAD, concentrating on boundaries: all repository transaction enlistment; both resource mapping write paths and search mapping; all constructor callers; precommit failure propagation; per-side-effect isolation; global cross-owner adoption protection; startup gate; selection under collapsed groups; no optimistic refresh on failed mutations.
- [ ] Confirm no drag/drop handlers, manual reorder controls, groupId creation parameters, empty-folder pruning, or unrelated reading-pane fixes entered the diff. Commit the evidence document and any separately tested in-scope corrections. Request user review before pushing/PR creation; never merge automatically.

## Plan self-review and execution handoff

Coverage mapping: spec Goals/Behavior → Tasks 4–7; durable identity/storage → Tasks 2–4; domain/schema/repositories → Tasks 1–2; two removal actions/provenance → Task 5; no-group UI/empty groups/accessibility → Tasks 6–8; retry/restart/legacy safety → Tasks 3 and 8; Non-goals → Global Constraints and final scope audit.

Planning verification on 2026-08-30: fresh Debug restore/build/test passed 410 core tests and 497 desktop tests, with the existing 3 desktop screenshot skips. The first --no-restore Debug attempt encountered Release-cached diagnostics assets and failed at Program.WithDeveloperTools; the normal Debug restore resolved it without source changes. This is baseline evidence only, not evidence that the future group tests passed.

Self-review covered spec scope, task dependencies, repository method/column counts, constructor callers, Markdown fences and the additional directory-slot adoption case above. No application or test code is changed by this plan commit.

Implementation is not started. After plan review, choose either:

1. **Subagent-driven execution:** fresh implementer per task, then specification review and code-quality review before the next task.
2. **Inline execution:** implement with the executing-plans skill and explicit review checkpoints.

Neither choice includes phase 2, a push, or a merge without further authorization.
