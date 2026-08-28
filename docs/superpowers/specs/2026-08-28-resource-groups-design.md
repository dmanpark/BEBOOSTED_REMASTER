# Resource groups: one level of containers inside a File

Date: 2026-08-28

Status: Approved design, ready for implementation planning

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
- No per-group description, colour, or icon. A group is a title and its contents.
- No change to indexing, search, or AI provenance. `SearchInProject` still searches
  every resource in a project regardless of grouping; a group is not itself indexed.
- No change to how Files or Projects behave, or to `ProjectFile` at all.

## Approach

Approach A of three considered: **a first-class `resource_groups` table with a
nullable `group_id` on `resources`.**

A group is a row of its own — `id, file_id, title, sort_order, created_at,
modified_at` — mirroring how `ProjectFile` relates to `Project`. A resource's
`group_id` is null when it is loose in the File. Rename, delete, and ordering then
work the way the rest of the model already works, and a group can exist before it has
contents.

The two rejected alternatives:

- **Reuse `ProjectFile` with a nullable parent**, making a group a nested File. It
  adds no table, but the domain explicitly forbids Files nesting, and a File carries a
  folio card, its own detail surface, and its own tab label. A File inside a File
  inherits all of that, muddying the single concept the Projects page is built on.
- **A `group_title` string column on `resources`.** No new table, but renaming a group
  becomes an UPDATE across rows, an empty group cannot exist, and `sort_order` has
  nowhere to live. This is the "sections, not containers" shape, which was considered
  and rejected in favour of real containers.

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
  destructive and is trivially reversible by regrouping.
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

`ResourceLayout.FolderFor` gains an optional group segment:

| Resource | Folder |
| --- | --- |
| in a group | `<project>/<file>/<group>` |
| loose | `<project>/<file>` |

`ResourceLayoutReconciler` resolves each resource's group when computing its desired
folder. Renaming a group, ungrouping, and moving between groups therefore all relocate
the real bytes through the mechanism that already handles Project and File renames —
best-effort, with a locked or missing file staying put and being retried on the next
run.

## Components

### `BeBoosted.Domain` — new `ResourceGroup`

- `Create(ProjectFileId fileId, string title, int sortOrder, DateTimeOffset now)` and
  `Rehydrate(...)`, matching `ProjectFile`'s shape.
- `Rename(string title, DateTimeOffset now)` — trims; a blank title throws
  `DomainException("A group needs a title.")`, mirroring `ProjectFile.ValidateTitle`.
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
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_resource_groups_file ON resource_groups (file_id);

ALTER TABLE resources ADD COLUMN group_id TEXT
    REFERENCES resource_groups (id) ON DELETE SET NULL;

CREATE INDEX idx_resources_group ON resources (group_id);
```

`ON DELETE SET NULL` states the intent, but **it does not enforce it**.
`SqliteConnectionFactory` sets only `PRAGMA journal_mode=WAL` and never
`PRAGMA foreign_keys=ON`, so SQLite's default applies and foreign-key actions do not
fire. The existing `ON DELETE CASCADE` on `resources.file_id` is in the same
position, which is precisely why `DeleteFile` walks its resources explicitly rather
than relying on it.

So both group removal paths do their own work in the service, and the declared action
is documentation that matches the code:

- **Ungroup** explicitly clears `group_id` on the group's resources, then deletes the
  group row.
- **Delete group** explicitly deletes the resources through `DeleteResource`, then the
  group row.

Existing rows get `group_id = NULL` and are therefore loose, so every File keeps
rendering as it does today.

### `BeBoosted.Application` — `IResourceGroupRepository`

`Add`, `Update`, `Delete`, `GetById`, `GetForFile` — the same surface
`IProjectFileRepository` exposes.

### `BeBoosted.Application` — `ProjectService`

- `ResourceGroup CreateGroup(ProjectFileId fileId, string title)`
- `ResourceGroup RenameGroup(ResourceGroupId id, string title)` — reconciles the
  file's project afterwards so the folder follows, exactly as `RenameFile` does.
- `void UngroupGroup(ResourceGroupId id)` — clears `group_id` on its resources, deletes
  the group, reconciles.
- `void DeleteGroup(ResourceGroupId id)` — deletes its resources through the existing
  `DeleteResource` (bytes and provenance included), then the group.
- `Resource MoveResourceToGroup(ResourceId id, ResourceGroupId? groupId)` — validates
  that the group belongs to the resource's File, then reconciles.
- `GetGroups(ProjectFileId fileId)` and a per-group resource count for the headers.

### `BeBoosted.Application` — `ResourceLayout` / `ResourceLayoutReconciler`

- `FolderFor(Project, ProjectFile, ResourceGroup?)` appends a sanitized group segment
  when the group is non-null. `ResourceLayout` stays pure — no filesystem access.
- The reconciler loads the File's groups once per File and resolves each resource's
  group when computing its desired folder.

### `BeBoosted.Infrastructure` — `LocalResourceStorage`

Reserves a free **folder** path the way it already reserves a free file name. See
Risks below.

### `BeBoosted.Desktop`

- `FileDetailViewModel` exposes `Groups` (each with its own resource rows, title,
  count, and collapsed state) alongside `LooseResources`, replacing the single flat
  `Resources` collection. `HasGroups` drives whether any group chrome renders at all.
- `ResourceRowViewModel` gains the Move-to flyout's target list and commit.
- `ProjectsView.axaml` renders group headers, the loose section, and the New group
  affordance, following the file's existing `Classes` and flyout conventions.

## Risks

**A group folder can collide with a loose resource's file name.** `ResourceLayout` is
deliberately pure — "every rule here is decidable without touching the filesystem,
which is what makes it testable and what keeps collision handling (the one genuinely
filesystem-dependent part) in the storage layer." So it cannot see that a loose
resource stored as `…/Gov Textbook/Notes` (an original file name with no extension)
occupies the exact path a group named `Notes` needs for its folder.

`Directory.CreateDirectory` then throws `IOException`. `MoveInto` catches it and
returns null, so a *move* degrades to "stays put, retried next run" — safe but
silently permanent. `Store` does **not** catch it, so a fresh import into a colliding
group would surface the error to the user.

Mitigation, consistent with the stated architecture: `LocalResourceStorage` reserves a
free folder path — appending the same ` (2)`, ` (3)` suffixes `CandidateName` uses for
files — and the reconciler records where the bytes actually went, as it already does.
The pure layout rules stay pure; the filesystem-dependent disambiguation stays in the
storage layer.

**Reconciler cost.** The reconciler already walks every project, file, and resource.
This adds one group query per File. Negligible at the scale of a personal planner, and
the reconciler runs at startup, not per interaction.

## Testing

Domain: group title validation and trimming; `MoveToGroup(null)` is the valid
ungrouped case; `Rename` touches `ModifiedAt`.

Application: create / rename / ungroup / delete-with-contents; ungroup preserves every
resource and its bytes while delete removes both; moving a resource into a group, to
another group, and back to loose relocates the bytes on disk and survives a restart;
moving a resource into a group belonging to a different File is rejected; the folder
collision degrades safely rather than throwing.

Persistence: `group_id` round-trips; the migration leaves existing resources loose.

**Do not** write a test asserting that deleting a group row sets its resources'
`group_id` to null — foreign-key enforcement is off, so it would fail. Assert instead
that `UngroupGroup` leaves every resource present with a null `group_id`, read back
through a fresh repository. That pins the behaviour the service is actually
responsible for.

View model: a File with no groups renders exactly as before; an empty group renders
with a zero count; the Move-to flyout lists the File's groups plus loose and excludes
the resource's current location; deleting a group prompts with the correct count.

Manual: the File surface in the running app, since XAML binding failures do not
surface in tests.

## Known limitations

- Groups do not nest, by design. A File that needs two levels of structure wants a
  second File.
- Deleting a group leaves its now-empty folder on disk, matching the existing
  behaviour for a deleted File.
- Phase 1 ships no reordering; groups appear in creation order.
- A group is not searchable and does not participate in AI provenance — only its
  resources do, exactly as today.
