# Resource groups: one level of containers inside a File

Date: 2026-08-28

Status: Approved design, revised after review, ready for implementation planning

## Problem

A File is a flat list. `ProjectFile` says so deliberately — "Flat — Files never
nest" — and `resources` carries only a `file_id`, so every document, link, note, and
image in a File sits in one undifferentiated list.

That works for a File with six items. It stops working for a File that collects a
term's worth of material: there is no way to say *these four belong to Unit 3* short
of creating a separate File per unit, which fragments the thing the File exists to
hold together.

## Goals

- A File may contain **groups** and **loose resources side by side**.
- A group is a real container: it can be renamed, emptied, and deleted as a unit, and
  it can exist while empty.
- A resource can be moved into a group, between groups, or back out to loose.
- Removing a grouping never has to destroy the documents in it.
- The on-disk resources directory keeps mirroring what the app shows, so it stays
  browsable outside the app.

## Non-goals

- **Groups never nest.** A group holds resources only, never another group. One level
  is the whole feature. `ProjectFile`'s own "Files never nest" invariant is unchanged
  and unrelated — this adds a level *inside* a File, it does not make Files nest.
- **No manual reordering UI in phase 1.** The schema carries `sort_order` so ordering
  has somewhere to live, but phase 1 orders groups by creation and ships no reorder
  control. Drag-to-reorder belongs with phase 2's drag-and-drop.
- **No group-targeted import in phase 1.** `AddLink`, `AddNote`, and `ImportFile` take
  only a `fileId` today and keep that signature. A new resource arrives loose in the
  File and is placed with the Move-to flyout. Adding an optional `groupId` to those
  three is phase-2 work, when a UI exists that would actually pass one — until then
  the parameter would have no caller.
- No per-group description, colour, or icon. A group is a title and its contents.
- No change to indexing, search, or AI provenance. `SearchInProject` still searches
  every resource in a project regardless of grouping; a group is not itself indexed.
- No change to how Files or Projects behave, or to `ProjectFile` at all.

## Approach

Approach A of three considered: **a first-class `resource_groups` table with a
nullable `group_id` on `resources`.**

A group is a row of its own, mirroring how `ProjectFile` relates to `Project`. A
resource's `group_id` is null when it is loose in the File. Rename, delete, and
ordering then work the way the rest of the model already works, and a group can exist
before it has contents.

The two rejected alternatives:

- **Reuse `ProjectFile` with a nullable parent**, making a group a nested File. It
  adds no table, but the domain explicitly forbids Files nesting, and a File carries a
  folio card, its own detail surface, and its own tab label. A File inside a File
  inherits all of that, muddying the single concept the Projects page is built on.
- **A `group_title` string column on `resources`.** No new table, but renaming a group
  becomes an UPDATE across rows, an empty group cannot exist, and `sort_order` has
  nowhere to live. This is the "sections, not containers" shape, considered and
  rejected in favour of real containers.

## Behavior

### The File surface

A File's contents render as zero or more groups, each collapsible with an item count,
followed by a "loose in this File" section for ungrouped resources:

```
FILE · Gov Textbook

  ▸ Unit 3 — Federalism        4 items
      Federalist 10.pdf
      Marbury notes
      lecture-slides.pdf
      oyez.com/marbury

  ▸ Unit 4 — Civil Rights      2 items
      Brown v Board.pdf
      timeline notes

  — loose in this File —
      Syllabus.pdf
      Exam date reminder
```

A File with no groups renders exactly as it does today: one flat list, no group
chrome, no "loose in this File" header. The feature is invisible until used.

An empty group renders with its header and a count of 0. That is intended — creating
"Unit 5" before filling it is a normal way to work.

### Removing a group: two distinct actions

Chosen deliberately over a single destructive delete:

- **Ungroup** clears `group_id` on the group's resources, so they become loose in the
  File, then removes the group. Nothing is lost. No confirmation — it is not
  destructive and is trivially reversed by regrouping.
- **Delete group** removes the group *and* its resources, including their stored
  bytes, behind the same two-step `ConfirmationPrompt` a File deletion uses, with the
  message naming the count: *"Delete 'Unit 3 — Federalism'? Its 4 documents and any
  stored files are deleted too."*

### Moving resources

Phase 1: a **Move to…** flyout on each resource row, listing the File's groups plus
"loose in this File". This matches the app's existing vocabulary — every other action
(Rename, Remove from File, Add link) is a button or flyout, and the codebase contains
no drag-and-drop at all.

Phase 2: drag-and-drop as an accelerator — drag a resource onto a group header or the
loose section. The flyout remains, and remains the keyboard-accessible path.

### On disk

| Resource | Folder |
| --- | --- |
| in a group | `<project>/<file>/<group folder segment>` |
| loose | `<project>/<file>` |

The group's folder segment is **persisted on the group row**, not derived from its
title at read time. See "Durable folder identity" below — this is the correction that
makes renames, collisions, and multi-resource groups behave.

## Durable folder identity for groups

A group's on-disk folder name is resolved **once**, when the group is created, and
re-resolved when it is renamed. The result is stored in `resource_groups.folder_segment`
and is what `ResourceLayout.FolderFor` uses.

**Why not derive it from the title.** `ResourceLayout` is pure by design — "every rule
here is decidable without touching the filesystem, which is what makes it testable and
what keeps collision handling (the one genuinely filesystem-dependent part) in the
storage layer." So a derived segment cannot know that the name it wants is already
taken by a loose resource's file (a resource whose original name has no extension, e.g.
`…/Gov Textbook/Notes`) or by a sibling group with the same title.

Deriving it and disambiguating per resource does not work either, and this is the
sharper failure. `ResourceLayout.IsAlreadyPlaced` compares the folder with
`string.Equals(..., OrdinalIgnoreCase)`; its numbered-variant tolerance applies only to
the *file* name, never the folder. So a resource parked in `Notes (2)` while its group
still derives `Notes` is judged out of place on **every** reconcile, and the mover runs
forever. Two resources of one group could also land in different folders, and two
groups sharing a title could split or share one.

**Resolution.** At create and at rename:

1. The service computes the preferred segment as `ResourceLayout.Sanitize(title, id)` —
   pure, unchanged.
2. It passes that, the parent folder (`<project>/<file>`), the folder segments already
   held by *other* groups of the same File, and — on rename — the segment this group
   currently owns, to the storage layer.
3. `IResourceStorage.ReserveFolderSegment` returns a free segment, appending the same
   ` (2)`, ` (3)` suffixes `ResourceLayout.CandidateName` uses for files, skipping any
   name occupied on disk **as a file or a directory** or held by a sibling group.
4. **It claims the segment by creating the directory** before returning it.
5. The service persists the returned segment on the group row.

### Reservation must claim, not merely check

A reservation that only *checks* is advisory, and it fails in both directions:

- **Empty group, later import.** A group reserves `Notes`, but nothing exists on disk
  because the group has no resources yet. A later loose import of an extensionless
  file named `Notes` then takes that exact path as a *file*. The group's persisted
  segment is now permanently unusable: every reconcile tries to create a directory
  where a file sits, fails, and retries forever. Re-reservation only happens on
  rename, so nothing recovers it.
- **Existing group directory, later import.** `LocalResourceStorage.ReserveFreePath`
  tests only `File.Exists`, so a *directory* named `Notes` reads as free. A loose
  import named `Notes` is handed that path and `File.Copy` is attempted onto a
  directory — uncaught in `Store`, so it surfaces to the user; caught in `MoveInto`,
  so it silently retries forever.

Both are fixed by two changes that belong together:

- `ReserveFolderSegment` creates the directory it returns. The directory *is* the
  claim, so nothing else can take the name afterwards.
- `ReserveFreePath` treats a path occupied by **either** a file or a directory as
  taken, so file placement can never target a group's folder.

`IResourceStorage.Exists` keeps its file-only meaning. It answers "are this
resource's bytes still here", and the reconciler's `FindUnrecordedPlacement` depends
on that reading — a directory sitting at a probed path correctly reports "no adoptable
file here".

### Rename may keep the segment it already owns

Because reservation now creates directories, a rename must be told which segment the
group already holds. Otherwise a case-only or sanitization-equivalent rename —
`Notes` → `notes`, or a change that trims to the same characters — finds its *own*
directory occupying the name and pointlessly advances to `notes (2)`, moving every
byte in the group for no reason.

So `ReserveFolderSegment` takes the currently owned segment and returns it unchanged
when the preferred segment resolves to it. `claimed` carries only *other* groups'
segments; the caller filters the group itself out.

`FolderFor` then reads the persisted value, so the desired folder is stable across
restarts, identical for every resource in the group, and distinct between same-titled
groups. `IsAlreadyPlaced` needs no change and stops churning.

A rename that genuinely resolves to a new segment leaves the old directory behind once
the reconciler has moved the bytes out — the same outcome a File rename already
produces.

## Components

### `BeBoosted.Domain` — new `ResourceGroup`

- `Create(ProjectFileId fileId, string title, string folderSegment, int sortOrder, DateTimeOffset now)`
  and `Rehydrate(...)`, matching `ProjectFile`'s shape.
- `Rename(string title, DateTimeOffset now)` — trims; a blank title throws
  `DomainException("A group needs a title.")`, mirroring `ProjectFile.ValidateTitle`.
- `RelocateTo(string folderSegment, DateTimeOffset now)` — records a newly reserved
  segment, mirroring `Resource.RelocateTo`'s contract: called only after the
  reservation succeeded, so the row never names a folder that was never reserved.
- `Reorder(int sortOrder, DateTimeOffset now)` — present for the schema's sake; no UI
  calls it in phase 1.

### `BeBoosted.Domain` — `Resource`

- New `ResourceGroupId? GroupId { get; private set; }`, null when loose.
- New `MoveToGroup(ResourceGroupId? groupId, DateTimeOffset now)`. Accepts null (that
  is the ungrouped case) and touches `ModifiedAt`.
- `Rehydrate` gains the `groupId` parameter.

### Migration `0012_resource_groups.sql`

```sql
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
```

**Foreign keys are enforced.** `SqliteConnectionFactory` sets `ForeignKeys = true` on
the connection string, which Microsoft.Data.Sqlite issues as `PRAGMA foreign_keys` on
every open. Three existing tests depend on it —
`SqliteCalendarBlockRepositoryTests.DeletingTask_CascadesToItsBlocks`,
`SqliteOccurrenceCompletionRepositoryTests.DeletingTheBlock_CascadesToItsCompletions`,
and `ProposedBlockIntegrityMigrationTests.CleanDatabase_DeletingATask_CascadesToItsProposedBlocks`.

So `ON DELETE CASCADE` and `ON DELETE SET NULL` both fire, and deleting a File really
does remove its groups.

The service still acts explicitly, for a different reason than the database:
`DeleteFile` walks its resources to delete **stored bytes** and invalidate
**AI provenance** — neither of which a foreign key can do. `DeleteGroup` walks its
resources for exactly the same reason, through `DeleteResource`. `UngroupGroup` clears
`group_id` explicitly so the intent is legible at the call site and does not depend on
delete order, even though `SET NULL` would also produce it.

Existing rows get `group_id = NULL` and are therefore loose, so every File keeps
rendering as it does today.

### `BeBoosted.Application` — `IResourceGroupRepository`

`Add`, `Update`, `Delete`, `GetById`, `GetForFile` — the surface
`IProjectFileRepository` already exposes.

### `BeBoosted.Application` — `IResourceStorage`

One new member:

```csharp
/// <summary>
/// Claims a folder for a group under <paramref name="relativeParent"/> and returns the
/// segment taken. Skips names occupied on disk by a file OR a directory, and names held
/// by <paramref name="claimed"/> (the File's other groups). Creating the directory IS
/// the claim — a checked-but-uncreated name can be taken by a later import.
/// <paramref name="ownedSegment"/> is the segment this group already holds, if any: it
/// is returned unchanged when the preferred segment resolves to it, so a case-only
/// rename does not advance to "(2)".
/// </summary>
string ReserveFolderSegment(
    string relativeParent,
    string preferredSegment,
    IReadOnlySet<string> claimed,
    string? ownedSegment = null);
```

### `BeBoosted.Application` — `ProjectService`

- `CreateGroup(ProjectFileId fileId, string title)` — reserves and persists the folder
  segment.
- `RenameGroup(ResourceGroupId id, string title)` — renames, re-reserves the segment,
  persists it, then reconciles the file's project so the bytes follow, exactly as
  `RenameFile` does.
- `UngroupGroup(ResourceGroupId id)` — clears `group_id` on its resources, deletes the
  group, reconciles.
- `DeleteGroup(ResourceGroupId id)` — deletes its resources through `DeleteResource`
  (bytes and provenance included), then the group.
- `MoveResourceToGroup(ResourceId id, ResourceGroupId? groupId)` — rejects a group
  belonging to a different File, then reconciles.
- `GetGroups(ProjectFileId fileId)` and a per-group resource count for the headers.

### `BeBoosted.Application` — `ResourceLayout` / `ResourceLayoutReconciler`

- `FolderFor(Project, ProjectFile, ResourceGroup?)` appends the group's **persisted**
  `FolderSegment` when the group is non-null. `ResourceLayout` stays pure; it does no
  sanitizing of the segment at this point because the segment was sanitized and
  reserved when it was stored.
- The reconciler loads the File's groups once per File and resolves each resource's
  group when computing its desired folder.

### `BeBoosted.Infrastructure` — `LocalResourceStorage`

- Implements `ReserveFolderSegment` per the contract above, creating the directory.
- **`ReserveFreePath` changes**: its loop currently returns the first candidate for
  which `!File.Exists(...)`. It must also reject a candidate occupied by a directory,
  so a loose resource can never be handed a group's folder path. This is a two-token
  change with real consequences — `Store` does not catch the `File.Copy` failure that
  results today.
- `Exists` is deliberately unchanged and stays file-only.

### `BeBoosted.Desktop`

- `FileDetailViewModel` exposes `Groups` (each with its resource rows, title, count,
  and collapsed state) alongside `LooseResources`, replacing the single flat
  `Resources` collection. `HasGroups` drives whether any group chrome renders at all.
- `ResourceRowViewModel` gains the Move-to flyout's target list and commit.
- `ProjectsView.axaml` renders group headers, the loose section, and the New group
  affordance, following the file's existing `Classes` and flyout conventions.

## Risks

**Reservation is not transactional with the row write.** `ReserveFolderSegment`
creates the directory, then the service persists the segment. If the row write fails,
an empty directory is orphaned. This is the same shape as `ImportFile`, which copies
bytes before adding the row, and the consequence is smaller — an empty folder rather
than an orphaned file. Not worth a compensating delete.

**A group's directory can still be removed behind the app's back.** If the user
deletes the folder in Explorer, the persisted segment names a directory that no longer
exists. `Store` and `MoveInto` both call `Directory.CreateDirectory` before writing, so
it is recreated on next use and nothing breaks. The claim is lost in the window
between, which is the same exposure every stored path already has.

**Reconciler cost.** The reconciler already walks every project, file, and resource.
This adds one group query per File. Negligible at the scale of a personal planner, and
it runs at startup rather than per interaction.

## Testing

Domain: group title validation and trimming; `MoveToGroup(null)` is the valid
ungrouped case; `RelocateTo` records a reserved segment; `Rename` touches
`ModifiedAt`.

Application: create / rename / ungroup / delete-with-contents; ungroup preserves every
resource and its bytes while delete removes both; moving a resource into a group, to
another group, and back to loose relocates the bytes on disk and survives a restart;
moving a resource into a group of a different File is rejected.

Folder identity — the reason this design exists. Each of these fails under a
plausible-looking simpler implementation, so each needs its own test:

- **File first, then group.** A group whose sanitized title collides with an existing
  loose resource's stored file name reserves a distinct segment, and every resource in
  that group lands in it.
- **Group first, then file.** A loose resource whose file name collides with an
  existing group's directory is placed at a distinct path — never handed the
  directory. Fails today: `ReserveFreePath` tests only `File.Exists`.
- **Empty group, then colliding import.** A group is created but left empty, then a
  loose extensionless resource of the same name is imported. The group keeps its
  directory and the resource goes elsewhere. Fails if reservation only checks and does
  not create — the import would take the name and block the group forever.
- **Two groups in one File with the same title** reserve different segments.
- **A reconcile run twice moves nothing the second time.** The churn regression: with a
  derived-and-disambiguated segment it would move on every run, forever.
- **Rename keeps an owned segment.** A case-only or sanitization-equivalent rename
  returns the same segment and moves no bytes — it must not advance to `(2)`.
- **Rename to a genuinely new title** re-reserves, and the bytes follow.

Persistence: `group_id` and `folder_segment` round-trip; the migration leaves existing
resources loose; **deleting a group row directly sets its resources' `group_id` to
null** (foreign keys are enforced — this test is worth having and will pass); deleting
a File removes its groups.

View model: a File with no groups renders exactly as before; an empty group renders
with a zero count; the Move-to flyout lists the File's groups plus loose and excludes
the resource's current location; deleting a group prompts with the correct count.

Manual: the File surface in the running app, since XAML binding failures do not
surface in tests.

## Known limitations

- Groups do not nest, by design. A File that needs two levels of structure wants a
  second File.
- Creating a group creates its (initially empty) folder on disk immediately. That is
  the claim that makes the segment durable, and it keeps the directory browsable
  outside the app the way the rest of the layout is.
- Deleting or renaming a group leaves its now-empty folder on disk, matching the
  existing behaviour for a deleted or renamed File.
- Phase 1 ships no reordering; groups appear in creation order.
- Phase 1 imports arrive loose and are then moved; there is no group-targeted import.
- A group is not searchable and does not participate in AI provenance — only its
  resources do, exactly as today.
