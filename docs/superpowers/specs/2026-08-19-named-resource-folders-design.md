# Named documents in per-file resource folders

Date: 2026-08-19
Status: Approved, ready for implementation planning

## Problem

Imported documents are stored as `resources/<guid>.pdf`. The resources directory is a
flat pile of unreadable names, so browsing it outside the app — which the Reveal in
Folder action invites — is useless.

`LocalResourceStorage.Store` builds the name from the resource id:

```csharp
var storedPath = id + Path.GetExtension(sourcePath).ToLowerInvariant();
```

Nothing is lost, though: `Resource.OriginalFileName` already captures the user's real
file name at import. It is persisted on every document and, today, read nowhere in the
application.

## Goals

- Stored documents keep their original file names on disk.
- Each Project gets a folder, and each File within it gets a subfolder.
- Documents already imported are moved into the new layout, not stranded.
- Renaming a Project moves its folder and keeps stored paths correct.

## Non-goals

- No database schema change and no SQL migration. `StoredPath` is already a relative
  path, so nesting needs no column change.
- No change to links or notes, which have no bytes.
- No change to indexing, citation, or provenance behavior.
- No renaming of a document when its resource Title is edited. The disk name follows
  `OriginalFileName`, which never changes.

## Approach

One mechanism serves both migration and rename-sync: a **reconciler** that computes
where every stored resource should live and moves anything that is not already there.

```
desired path = <sanitized project name>/<sanitized file title>/<original file name>
```

It runs at startup, immediately after the SQL migrations in `App.axaml.cs`, and again
after a project rename. The first run is the migration: every `<guid>.pdf` moves to its
real name and folder and its `StoredPath` is updated. Later runs are no-ops except
where something drifted.

A separate one-time migration plus a separate rename handler would be two code paths
that must agree. One reconciler cannot disagree with itself.

## Why not a transaction

A filesystem move and a database write cannot commit atomically. Adding a project-side
transaction seam would make only the database half atomic while the folder move sat
outside it, which buys nothing and hides the real failure mode.

Instead the design makes failure benign:

- A title rename **always succeeds**. It never fails because a PDF is open in another
  program.
- Each resource is moved and then recorded independently. A failure on one resource
  leaves earlier moves correctly recorded and later ones simply pending.
- A resource whose file is locked or missing keeps its current `StoredPath`, stays
  fully usable through `ResolveStoredPath`, and is picked up on the next run.

The move is performed first and `StoredPath` is written only after it succeeds, so the
database never points at a path that does not exist. The tolerable failure is the
reverse — bytes moved, row not yet updated — which the next reconcile repairs, because
the file is then already in its desired location.

## Naming rules

`ResourceLayout` is a pure static helper, unit-testable without a filesystem.

Sanitizing one path segment:

- Replace `< > : " / \ | ? *` and control characters with `-`.
- Collapse runs of whitespace; trim leading and trailing whitespace.
- Trim trailing dots and spaces (Windows rejects both).
- Suffix reserved device names with `_`: `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`,
  `LPT1`–`LPT9`, matched case-insensitively and ignoring any extension.
- Cap each segment at 80 characters. For a file name the cap applies to the stem so the
  extension always survives; folder segments have no extension to protect.
- A segment that sanitizes to empty falls back to the owning entity's id.

Folder segments are joined with `Path.Combine`, so `StoredPath` uses the platform
separator — matching `ResolvePath`, which already combines it against the resources
root.

Collisions are resolved by the storage layer, which is the only component that can see
the filesystem. It probes `report.pdf`, then `report (2).pdf`, `report (3).pdf`, until
a free slot. Two projects whose names sanitize identically get the same treatment at
folder level.

## Idempotence

The reconciler must not shuffle files on every launch. A resource is considered already
placed when its current `StoredPath` sits in the desired folder **and** its file name is
either the desired name or a numbered variant of it (`report (2).pdf` for a desired
`report.pdf`). Such a resource is skipped entirely — no move, no write.

That makes a second run a no-op even where the first run had to disambiguate.

## Components

### Domain: `Resource`

`StoredPath` becomes `{ get; private set; }` and gains:

```csharp
/// <summary>Records a new location after its bytes were moved on disk.</summary>
public void RelocateTo(string storedPath, DateTimeOffset now)
```

It rejects a blank path, and rejects being called on a resource that has no
`StoredPath` (links and notes never move).

### Application: `IResourceStorage`

```csharp
/// <summary>
/// Copies the source file into <paramref name="relativeFolder"/> under
/// <paramref name="preferredFileName"/>, disambiguating on collision. Returns the
/// stored path actually used, relative to the resources root.
/// </summary>
string Store(string relativeFolder, string preferredFileName, string sourcePath);

/// <summary>
/// Moves an already-stored file into <paramref name="relativeFolder"/> under
/// <paramref name="preferredFileName"/>. Returns the stored path actually used, or
/// null when the move could not be performed — a locked or missing file leaves the
/// resource exactly where it was.
/// </summary>
string? MoveInto(string currentStoredPath, string relativeFolder, string preferredFileName);
```

`ResolvePath`, `Exists`, and `Delete` are unchanged. The old
`Store(ResourceId, string)` overload is removed; `ResourceId` is no longer part of the
naming scheme.

### Application: `ResourceLayout`

Pure static: `Sanitize(string segment, string fallback)` and
`FolderFor(Project project, ProjectFile file)`, plus `IsAlreadyPlaced(string storedPath,
string desiredFolder, string desiredFileName)` backing the idempotence rule.

### Application: `ResourceLayoutReconciler`

Depends on `IProjectRepository`, `IProjectFileRepository`, `IResourceRepository`,
`IResourceStorage`, `IClock`.

- `int Reconcile()` — every project.
- `int ReconcileProject(ProjectId id)` — one project.

Both walk projects → files → resources, skip resources with no `StoredPath`, skip
already-placed ones, and otherwise `MoveInto` then `RelocateTo` + `Update`. They return
the number of resources actually moved. Neither ever throws for a per-resource failure.

Enumeration needs no repository change: `IProjectRepository.GetAll()` →
`IProjectFileRepository.GetForProject` → `IResourceRepository.GetForFile`.

### Application: `ProjectService`

`ImportFile` resolves the file and its project, composes the folder through
`ResourceLayout`, and calls the new `Store`. `RenameProject` calls
`ReconcileProject(id)` after the rename is persisted. The reconciler is injected as an
optional trailing constructor dependency, exactly as `IProvenanceInvalidator? = null`
already is, so existing construction sites and tests stay valid.

### Desktop: `App.axaml.cs`

After `MigrationRunner.Apply(...)`, resolve `ResourceLayoutReconciler` and call
`Reconcile()`. Failures are logged, never fatal — a reconcile problem must not stop the
app from starting.

## Testing

**Pure naming (`ResourceLayout`)** — invalid characters, control characters, trailing
dots and spaces, reserved device names with and without extensions, over-long segments,
empty-after-sanitizing falling back to the id, and `IsAlreadyPlaced` accepting both the
exact name and numbered variants while rejecting a different folder.

**Storage (`LocalResourceStorage`, real temp directory)** — `Store` creates nested
folders and preserves the name; a second `Store` of the same name yields ` (2)`;
`MoveInto` relocates bytes and returns the new relative path; `MoveInto` returns null
for a missing source; `ResolvePath` and `Exists` agree with the returned path.

**Reconciler** — migrates GUID-named files to named paths and updates `StoredPath`; a
second run moves nothing; a failing move leaves that resource's `StoredPath` untouched
while its siblings migrate; links and notes are ignored; two documents sharing an
original name land as `report.pdf` and `report (2).pdf`.

**Service / SQLite** — `ImportFile` writes into the project/file folder with the
original name; a project rename moves the folder and the new paths survive reopening
the database; `ResolveStoredPath` resolves correctly after import, after rename, and
after a reconcile.

## Known limitations

- Renaming a **File** does not move its folder, because no rename path for
  `ProjectFile` exists in the service or the UI. `ProjectFile.Rename` is defined in the
  domain and never called. When such a path is added it should call
  `ReconcileProject`; until then a File folder keeps its title from creation time.
- Renaming a resource's Title does not rename the file on disk. Disk names follow
  `OriginalFileName` by design.
- Documents imported before this change keep their `AddedAt` ordering but their moved
  files get a fresh filesystem timestamp.
- Two projects whose names sanitize to the same string share a numbered folder pair;
  which one gets the bare name depends on reconcile order, which is `GetAll()` order.
- A resource whose file is missing from disk entirely is left alone and reported only
  by count, not individually surfaced in the UI.
- Deleting every document in a File leaves its now-empty folder behind. `Delete` removes
  bytes, not directories, and pruning empty folders is not part of this change.
