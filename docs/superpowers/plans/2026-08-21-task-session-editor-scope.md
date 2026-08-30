# Task and Session Editor Scope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single one-session-shaped task editor with two scope-led editors — a 480px whole-task editor listing every session, and a 408px session editor for one block — so no schedule edit ever silently targets a session the user cannot see (audit finding F-03, with the F-15 occurrence-resolution follow-through).

**Architecture:** Four focused `CalendarService` operations replace the combined `UpdateTask` path. Two new view models — `WholeTaskEditorViewModel` and `SessionEditorViewModel` — share `ScheduleFieldsViewModel`, `SessionRowViewModel`, and a pure `SessionListBuilder`. `CalendarViewModel` hosts one `ActiveTaskEditor` slot (DataTemplates in the existing `TaskModalScrim`) and owns navigation, gate ordering, and focus-return bookkeeping. Persistence is split into result-returning `TrySave…` primitives (no navigation) and thin `Save…` wrappers (navigation on success), so gated saves can keep the editor active and run a pending operation.

**Tech Stack:** .NET 10, C#, Avalonia 12.1.1 (CommunityToolkit.Mvvm partial observable properties, compiled bindings), xunit.v3, Avalonia.Headless.XUnit + Skia, SQLite (`Microsoft.Data.Sqlite`). No schema change, no migration.

**Spec:** `docs/superpowers/specs/2026-08-20-task-session-editor-scope-design.md` (behavior + exact copy authority) with its **Approved visual handoff** appendix (frames 3a, 3b, 4a–4p — layout, widths, treatments authority).

**Locked authority decisions (do not reopen):**
1. New-session mode's primary action is **"Add session"** — the spec's exact-copy authority wins over frame 4h's "Save session".
2. The COMPLETION section renders **after** SCHEDULE — frame 3a is the layout authority over the spec's prose ordering.

## Global Constraints

- Behavior and *exact user-facing copy* come verbatim from the spec's Copy section. Layout, hierarchy, widths, and interaction/focus/disabled/error/confirmation treatments come from approved frames 3a/3b/4a–4p only; every other Design frame is out of scope and must not be implemented.
- Whole-task editor card: **480px** wide. Session/repeating/new-session cards: **408px**. Fixed scope header and action footer; only the body scrolls, vertically; no editor scrolls horizontally; 1100×720 keeps header and footer visible.
- No new colors, fonts, radii, or shadows — reuse `Tokens.axaml`. Frame-color mapping (binding): card `#FFFFFF` → `BrushPaperWhite`; confirm cream `#F7F4E4` → `BrushFolioUnderlay` (#F6F3E4); disabled fill `#EDEBDD` → `BrushSyncedCream`; disabled text `#B9BBAF` → `BrushGraphite40`; destructive rust `#8A3B2E`/`#FBF0EC` → `BrushAccentUmber` + `BrushFolioUnderlay`; lime wash rows → `BrushLimeWash`; hover/pressed limes → existing `Button.primary` states. One new *icon geometry* is permitted (`IconRepeat` — no repeat glyph exists in `Icons.axaml`); icons are not colors/fonts/radii/shadows.
- Keyboard focus = 2px graphite outline + lime halo (`ColorLimeHalo` exists), implemented **editor-locally** (styles scoped inside the two editor views — the global `Button` styles in `Controls.axaml` are NOT restyled; see Task 10). Never color alone. Every icon control ≥ 32×32 hit area. 11px metadata floor; IBM Plex Mono via `TextBlock.mono`/`.meta`/`.metaLabel` classes.
- Completion authority is untouched: aggregate one-off completion, per-occurrence repeating completion, reopen-always-allowed. `CompletionApiSurfaceTests`, `TaskCompletionAuthorityTests`, `SessionRecurrenceReconciliationTests`, `NoLegacyCommitmentPathTests` stay green except the cases Task 12 explicitly retires.
- Every successful mutation reloads and announces `DataChanged` exactly once (`CalendarViewModel.NotifyTasksMutated`, `CalendarViewModel.cs:843-847`). A failure — `DomainException` or `SqliteException` — never announces, never navigates, never closes the editor, never persists partially. This applies to **every** editor mutation: whole-task Save, row Remove, Unschedule all, Delete task, session Save, Add session, Remove this session, Remove schedule, and create-with-first-session (Tasks 6–8 carry one failure-injection fact per path).
- Strict TDD: every production change is preceded by a test watched failing for the right reason (a compile error on a missing member is a valid RED for new surface; record the exact message). **Every task's completion checkpoint compiles and both full suites are green** — no task ends with a knowingly red suite; reds exist only inside a task between its RED and GREEN steps.
- `dotnet format BeBoosted.slnx --verify-no-changes --no-restore` stays clean; builds run `-warnaserror` (`TreatWarningsAsErrors=true` is repo-wide).
- Tests use only in-memory doubles (`tests/BeBoosted.Desktop.Tests/Support/TestDoubles.cs`) or `TempDatabase` SQLite files. **Never touch `C:\Users\daria\AppData\Local\BeBoosted` or `C:\Users\daria\AppData\Roaming\beboosted`.** Every runtime launch sets a fresh disposable `BEBOOSTED_DATA_DIR` (sole override, read once at startup — `DefaultAppDataPaths.cs:27`).
- No commits, no staging, no worktree, no stash/reset/clean/checkout — at any point. See **Execution prerequisites** and the **Checkpoint procedure**.

## Execution prerequisites (require user approval before Task 1)

1. **Dirty-tree / commit strategy (selected: no git write operations).** The
   working tree carries extensive pre-existing daily-priority-list work.
   This plan prescribes no commits, no staging, no worktree, and no
   stash/reset/clean/checkout. Suggested commit *boundaries* are annotations
   for whenever the user separately approves a baseline strategy; executing
   any commit requires that explicit approval first.
2. **Allowed-path union + external baseline (required before Task 1).** A
   status snapshot alone cannot detect further edits to files that were
   already modified or untracked, so capture a hash-and-copy baseline
   OUTSIDE the repository. Counts and entry lists are captured fresh at
   execution time — never assume this plan's authoring-time numbers.

   **(a) Define `$PlanAllowed` first** — the authoritative allowed-path
   union of every Files section in this plan (paste before anything else;
   the baseline block and every checkpoint use it):

```powershell
$PlanAllowed = @(
  'src/BeBoosted.Application/Calendar/CalendarService.cs',
  'src/BeBoosted.Desktop/BeBoosted.Desktop.csproj',
  'src/BeBoosted.Desktop/Styles/Icons.axaml',
  'src/BeBoosted.Desktop/ViewModels/CalendarBlockViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/EditorPrompts.cs',
  'src/BeBoosted.Desktop/ViewModels/ProjectOptionViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/ScheduleFieldsViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/SessionEditorViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/SessionListBuilder.cs',
  'src/BeBoosted.Desktop/ViewModels/SessionRowViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/TaskEditorViewModel.cs',
  'src/BeBoosted.Desktop/ViewModels/WholeTaskEditorViewModel.cs',
  'src/BeBoosted.Desktop/Views/CalendarBlockView.axaml',
  'src/BeBoosted.Desktop/Views/DailyTaskListView.axaml',
  'src/BeBoosted.Desktop/Views/MainWindow.axaml',
  'src/BeBoosted.Desktop/Views/MainWindow.axaml.cs',
  'src/BeBoosted.Desktop/Views/SessionEditorView.axaml',
  'src/BeBoosted.Desktop/Views/SessionEditorView.axaml.cs',
  'src/BeBoosted.Desktop/Views/TaskEditorView.axaml',
  'src/BeBoosted.Desktop/Views/TaskEditorView.axaml.cs',
  'src/BeBoosted.Desktop/Views/WholeTaskEditorView.axaml',
  'src/BeBoosted.Desktop/Views/WholeTaskEditorView.axaml.cs',
  'tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj',
  'tests/BeBoosted.Desktop.Tests/Ui/CalendarBlockInteractionTests.cs',
  'tests/BeBoosted.Desktop.Tests/Ui/DailyListUiTests.cs',
  'tests/BeBoosted.Desktop.Tests/Ui/FeatureScreenshotCaptureTests.cs',
  'tests/BeBoosted.Desktop.Tests/Ui/OccurrenceCompletionUiTests.cs',
  'tests/BeBoosted.Desktop.Tests/Ui/ProjectEntryPointTests.cs',
  'tests/BeBoosted.Desktop.Tests/Ui/ScreenshotCaptureTests.cs',
  'tests/BeBoosted.Desktop.Tests/Ui/TaskEditorModalTests.cs',
  'tests/BeBoosted.Desktop.Tests/Ui/TaskEditorScopeUiTests.cs',
  'tests/BeBoosted.Desktop.Tests/Ui/UnifiedTaskUiTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/CalendarBlockCapabilityTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/CalendarViewModelCalendarTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/EditorScopeSelectionTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/PlanDraftLifecycleTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/PlanDraftViewModelTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/PlanningDeletionTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/ProjectsViewModelTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/ScheduleFieldsViewModelTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/SessionEditorViewModelTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/SessionListBuilderTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/ShellProjectRefreshTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/TaskEditorViewModelTests.cs',
  'tests/BeBoosted.Desktop.Tests/ViewModels/WholeTaskEditorViewModelTests.cs',
  'tests/BeBoosted.Tests/Calendar/CalendarMutationAtomicityTests.cs',
  'tests/BeBoosted.Tests/Calendar/CalendarServiceTests.cs',
  'tests/BeBoosted.Tests/Calendar/EditorScopeGuardTests.cs',
  'tests/BeBoosted.Tests/Calendar/SessionAdditionAndUnscheduleAllTests.cs',
  'tests/BeBoosted.Tests/Calendar/SessionRecurrenceReconciliationTests.cs',
  'tests/BeBoosted.Tests/Calendar/SessionScheduleEditingTests.cs',
  'tests/BeBoosted.Tests/Calendar/TaskCompletionAuthorityTests.cs',
  'tests/BeBoosted.Tests/Calendar/TaskDetailEditingTests.cs',
  'tests/BeBoosted.Tests/Calendar/TaskSessionSelectionTests.cs'
)
```

   **(b) Capture the baseline** (direct assignment — never `Out-String`,
   which appends a CRLF-only record and breaks `Substring(3)`; only valid
   porcelain records are accepted, rename/copy records are rejected, and
   every record stores XY status, path, existence, and SHA-256 when the
   file exists):

```powershell
Set-Location C:\Users\daria\BeBoosted_Remaster\BEBOOSTED_REMASTER
$base = Join-Path $env:TEMP ("f03-baseline-" + [guid]::NewGuid())
New-Item -ItemType Directory $base | Out-Null
$raw = git status --porcelain=v1 -z -uall
if ($raw -is [object[]]) { throw "unexpected newline inside a path — stop and ask the user" }
[IO.File]::WriteAllText((Join-Path $base "status-z.txt"), $raw)
$records = ($raw -split "`0") | Where-Object { $_ }
$bad = $records | Where-Object { $_.Length -lt 4 -or $_ -notmatch '^[ MADRCU?!]{2} ' }
if ($bad) { throw "unparseable porcelain record(s): $($bad -join ' | ')" }
if ($records | Where-Object { $_.Substring(0, 2) -match '[RC]' }) {
    throw "rename/copy record present — stop and ask the user" }
$manifest = foreach ($r in $records) {
    $p = $r.Substring(3)
    $exists = Test-Path $p -PathType Leaf
    [pscustomobject]@{
        Xy = $r.Substring(0, 2); Path = $p; Exists = $exists
        Sha256 = if ($exists) { (Get-FileHash $p -Algorithm SHA256).Hash } else { '' } } }
$manifest | Export-Csv (Join-Path $base "manifest.csv") -NoTypeInformation
# Byte-for-byte copies of every pre-existing dirty file this plan may modify:
$existingPaths = @($manifest | Where-Object Exists).Path
foreach ($p in ($PlanAllowed | Where-Object { $existingPaths -contains $_ })) {
    $dest = Join-Path $base ("copies\" + $p)
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $p $dest }
"BASELINE=$base"   # record this path; every checkpoint and the final audit use it
```

   **(c) Mandatory dry run.** Immediately run the Checkpoint procedure
   below, unchanged, against this fresh baseline. It must finish with
   `checkpoint clean` before Task 1 begins. (Authoring-time proof against
   the then-current tree, 2026-08-21: 147 records parsed, 135 existing
   files hashed, 0 failures — re-verify with the fresh run.)

3. **Package references.** Tasks 6–9 catch
   `Microsoft.Data.Sqlite.SqliteException` in the Desktop layer, and the
   Desktop test project constructs it (`new SqliteException("locked", 5)`)
   in failure-injection decorators. If not already referenced, add
   `<PackageReference Include="Microsoft.Data.Sqlite" />` to
   `src/BeBoosted.Desktop/BeBoosted.Desktop.csproj` and
   `tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj`
   (version 10.0.11 flows from `Directory.Packages.props`). These two
   `.csproj` files are in `$PlanAllowed` and in Task 6's Files section —
   this is the plan's only dependency change.

## Checkpoint procedure (run at every task's final step)

Uses `$PlanAllowed` and `$base` from Execution prerequisites §2. The
checkpoint is parameterless — it always verifies everything so far, on
baseline *records* (status identity + existence + hash), never on status
codes alone:

```powershell
$raw = git status --porcelain=v1 -z -uall
if ($raw -is [object[]]) { throw "unexpected newline inside a path — stop and ask the user" }
$nowRecords = ($raw -split "`0") | Where-Object { $_ }
if ($nowRecords | Where-Object { $_.Length -lt 4 -or $_ -notmatch '^[ MADRCU?!]{2} ' }) {
    throw "unparseable porcelain record" }
if ($nowRecords | Where-Object { $_.Substring(0, 2) -match '[RC]' }) {
    throw "rename/copy record present — stop and ask the user" }
$now = @{}
foreach ($r in $nowRecords) { $now[$r.Substring(3)] = $r.Substring(0, 2) }
$baseline = Import-Csv (Join-Path $base "manifest.csv")
$basePaths = $baseline.Path
# 1. New paths since the baseline must all be plan-created files:
$foreignNew = $now.Keys | Where-Object { $basePaths -notcontains $_ -and $PlanAllowed -notcontains $_ }
if ($foreignNew) { throw "FOREIGN NEW PATHS: $($foreignNew -join ', ')" }
# 2. Every baseline record OUTSIDE the plan's allowed list keeps its identity —
#    a missing previously-existing file is a FOREIGN DELETION, never skipped:
foreach ($row in $baseline) {
    if ($PlanAllowed -contains $row.Path) { continue }
    $existsNow = Test-Path $row.Path -PathType Leaf
    if ($row.Exists -eq 'True') {
        if (-not $existsNow) { throw "FOREIGN DELETION: $($row.Path)" }
        if ((Get-FileHash $row.Path -Algorithm SHA256).Hash -ne $row.Sha256) {
            throw "FOREIGN DRIFT: $($row.Path)" }
    } elseif ($existsNow) { throw "FOREIGN REAPPEARANCE: $($row.Path)" }
    if ($now.ContainsKey($row.Path)) {
        if ($now[$row.Path] -ne $row.Xy) {
            throw "FOREIGN STATUS CHANGE: $($row.Path) '$($row.Xy)' -> '$($now[$row.Path])'" }
    } else { throw "FOREIGN STATUS DISAPPEARANCE: $($row.Path)" }
}
# 3. Plan-owned files that were already dirty at baseline: inspect ONLY the
#    feature delta against the external copy (never against HEAD):
foreach ($p in ($PlanAllowed | Where-Object { $basePaths -contains $_ })) {
    $copy = Join-Path $base ("copies\" + $p)
    if ((Test-Path $p) -and (Test-Path $copy)) { git diff --no-index -- $copy $p }
}
"checkpoint clean"
```

Each task's final step means: run this block, review the step-3 deltas and
the plan-created files for exactly this task's named changes, and record the
noted suggested-commit boundary. No git write operations.

## File-responsibility map

**Application:**
- `src/BeBoosted.Application/Calendar/CalendarService.cs` — Tasks 1–3 add `UpdateTaskDetails`, `UpdateSessionSchedule`, `AddSession`, `UnscheduleAllSessions`; Task 12 deletes `UpdateTask` (`:67-179`), `ApplyTaskCompletion` (`:211-290`), `ResolveSession` (`:298-309`), `GetEditableSessionForTask` (`:451-456`). Helpers `RemoveSession` (`:311-322`), `RemoveObsoleteCompletions` (`:325-335`), `ApplyOccurrenceCompletion` (`:337-352`), `ApplyAggregateCompletion` (`:407-442`), `RequireTask`, `Require` are reused as-is.
- New tests: `TaskDetailEditingTests.cs` (T1), `SessionScheduleEditingTests.cs` (T2), `SessionAdditionAndUnscheduleAllTests.cs` (T3), `EditorScopeGuardTests.cs` (T12). Modified: `CalendarMutationAtomicityTests.cs` (T1–T3, T12), `CalendarServiceTests.cs` + `TaskCompletionAuthorityTests.cs` + `SessionRecurrenceReconciliationTests.cs` (T12 re-targets). Deleted: `TaskSessionSelectionTests.cs` (T12).

**Desktop shared models (create):** `SessionListBuilder.cs` + `SessionRowData` (T4); `ScheduleFieldsViewModel.cs`, `ProjectOptionViewModel.cs` (relocation), `EditorPrompts.cs` (T5); `SessionRowViewModel.cs` (T7 — same task as its owner, so it compiles at creation).

**Desktop editors (create):** `SessionEditorViewModel.cs` (T6), `WholeTaskEditorViewModel.cs` (T7), views `SessionEditorView.axaml(+.cs)` and `WholeTaskEditorView.axaml(+.cs)` (T10).

**Routing/hosting (modify):** `CalendarViewModel.cs` (T6–T12), `MainWindow.axaml(+.cs)` (T10 bridge, T12 collapse), `ShellViewModel.cs` + `DailyListViewModel.cs` + `CalendarBlockViewModel.cs` (T11), `DailyRowViewModel.cs` + `DailyTaskListView.axaml` + `CalendarBlockView.axaml` (T10 markers), `Icons.axaml` (T10), both `.csproj` files (T6, prerequisite §3).

**Desktop tests:** created — `SessionListBuilderTests.cs` (T4), `ScheduleFieldsViewModelTests.cs` (T5), `SessionEditorViewModelTests.cs` (T6), `WholeTaskEditorViewModelTests.cs` (T7), `TaskEditorScopeUiTests.cs` (T10), `EditorScopeSelectionTests.cs` (T11). Re-targeted (T11, the complete verified hit list): `UnifiedTaskUiTests.cs`, `ProjectEntryPointTests.cs`, `DailyListUiTests.cs`, `OccurrenceCompletionUiTests.cs`, `CalendarBlockInteractionTests.cs`, `ScreenshotCaptureTests.cs`, `DailyListViewModelTests.cs`, `CalendarBlockCapabilityTests.cs`, `CalendarViewModelCalendarTests.cs`, `ShellProjectRefreshTests.cs`, `PlanningDeletionTests.cs`, `ProjectsViewModelTests.cs`, `PlanDraftViewModelTests.cs`, `PlanDraftLifecycleTests.cs`. Unchanged (name-only match, assertion still valid): `InboxViewModelTests.cs`. Deleted (T11, after replacement coverage is green): `TaskEditorViewModelTests.cs`, `TaskEditorModalTests.cs`. Minimally re-targeted (T11) then rewritten (T13): `FeatureScreenshotCaptureTests.cs`.

**Deleted in T12 (after every caller has moved):** `TaskEditorViewModel.cs`, `TaskEditorView.axaml`, `TaskEditorView.axaml.cs`.

**Never touched:** live profiles, `docs/qa/**`, the approved spec and PNG, unrelated Design frames, `TaskItem.Recurrence`, planning/proposal behavior (F-20), quick unschedule affordances, `Controls.axaml` global styles, `Tokens.axaml`, `TestDoubles.cs`.

## Task sequence (deterministic — no optional orderings)

1 `UpdateTaskDetails` → 2 `UpdateSessionSchedule` → 3 `AddSession`/`UnscheduleAllSessions` → 4 `SessionListBuilder` (pure) → 5 `ScheduleFieldsViewModel` + prompts → 6 `SessionEditorViewModel` (block-entry modes) → 7 `WholeTaskEditorViewModel` + `SessionRowViewModel` + push/return navigation → 8 whole-task schedule mutations + add mode + create mode → 9 gates + promotion + Escape/prompt depth → 10 views + hosting bridge + markers + rendered proofs (RED-first) → 11 entry-point rewiring + full test re-targeting (one task, ends green) → 12 legacy retirement + service re-targets + guards → 13 screenshots → 14 final verification chain.

---

### Task 1: `CalendarService.UpdateTaskDetails`

**Files:**
- Modify: `src/BeBoosted.Application/Calendar/CalendarService.cs` (add after `CreateTask`, near line 60)
- Create: `tests/BeBoosted.Tests/Calendar/TaskDetailEditingTests.cs`
- Modify: `tests/BeBoosted.Tests/Calendar/CalendarMutationAtomicityTests.cs` (one rollback fact)

**Interfaces:**
- Consumes: existing `TaskDetailsRequest(string Title, ProjectId? ProjectId, DateOnly? Deadline, TimeSpan? EstimatedDuration)`, `TaskCompletionRequest(DateOnly OpenedOccurrence, bool Completed)` (`CalendarService.cs:19-23`), `ApplyAggregateCompletion` (`:407-442`), `mutations.Execute` 3-arg overload.
- Produces: `public TaskItem UpdateTaskDetails(TaskId taskId, TaskDetailsRequest details, TaskCompletionRequest? completion = null)` — task fields + aggregate completion; never changes any session's date, time, or recurrence (it may resolve/reopen one-off siblings' *outcomes*); one transaction.

- [ ] **Step 1: Write the failing tests**

Create `tests/BeBoosted.Tests/Calendar/TaskDetailEditingTests.cs` (fixture prologue copied from `CalendarServiceTests`: `TempDatabase` + `MigrationRunner` + real Sqlite repositories + `SqliteCalendarMutations` + `FakeClock`):

```csharp
using BeBoosted.Application.Calendar;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Calendar;

/// <summary>
/// The whole-task editor's save path: task fields plus aggregate completion,
/// never a session's date, time, or recurrence.
/// </summary>
public sealed class TaskDetailEditingTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public TaskDetailEditingTests()
        => new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());

    // Build the service exactly as CalendarServiceTests does (same repos + mutations + clock).

    [Fact]
    public void UpdateTaskDetails_PersistsEveryField_WithoutTouchingAnySchedule()
    {
        var service = CreateService(out var tasks, out var blocks, out var clock);
        var task = TaskItem.Create("Draft essay", clock.Now);
        tasks.Add(task);
        var session = CalendarBlock.CreateTaskSession(
            task.Id, new DateOnly(2026, 8, 25), new TimeOnly(9, 0), new TimeOnly(10, 0), clock.Now);
        blocks.Add(session);

        service.UpdateTaskDetails(task.Id, new TaskDetailsRequest(
            "Draft essay v2", null, new DateOnly(2026, 8, 30), TimeSpan.FromMinutes(90)));

        var saved = tasks.GetById(task.Id)!;
        Assert.Equal("Draft essay v2", saved.Title);
        Assert.Equal(new DateOnly(2026, 8, 30), saved.Deadline);
        Assert.Equal(TimeSpan.FromMinutes(90), saved.EstimatedDuration);
        var untouched = blocks.GetById(session.Id)!;
        Assert.Equal(new DateOnly(2026, 8, 25), untouched.Date);
        Assert.Equal(new TimeOnly(9, 0), untouched.StartTime);
        Assert.Null(untouched.Recurrence);
    }

    [Fact]
    public void UpdateTaskDetails_Completing_ResolvesEveryPendingOneOff_AsDone()
    {
        // Two pending one-off sessions; completion=true → task complete and both Done.
        // Asserts saved.IsCompleted, both blocks' Outcome == BlockOutcome.Done.
    }

    [Fact]
    public void UpdateTaskDetails_Reopening_ClearsEveryDoneOneOff()
    {
        // Completed task + Done sessions; completion=false → task open, outcomes None.
    }

    [Fact]
    public void UpdateTaskDetails_CompletingUnderARepeatingSchedule_IsRejected_ChangingNothing()
    {
        // Repeating session present; completion=true →
        // DomainException "A repeating task completes per occurrence, not as a whole."
        // and title/deadline stay untouched (validate-before-mutate).
    }

    [Fact]
    public void UpdateTaskDetails_EmptyTitle_IsRejected()
    {
        // DomainException "A task needs a title."; nothing persisted.
    }

    [Fact]
    public void UpdateTaskDetails_NonPositiveEstimate_IsRejected()
    {
        // DomainException "An estimated duration must be positive."
    }

    [Fact]
    public void UpdateTaskDetails_MissingTask_Throws_TaskNoLongerExists()
    {
        // DomainException containing "no longer exists".
    }

    public void Dispose() => _database.Dispose();
}
```

Write the commented facts as full code in the same style as the first — each
seeds via the real repositories, calls the service, and asserts through a
**fresh repository over the same factory** for persistence claims (the
restart idiom from `TaskSessionSelectionTests`).

Add to `CalendarMutationAtomicityTests.cs`:

```csharp
[Fact]
public void UpdateTaskDetails_FailingSiblingOutcomeWrite_RollsBackTaskAndOutcomes()
{
    // Failing block-repository decorator (FailOnUpdate) inside the real
    // SqliteCalendarMutations; completion=true over two pending one-offs.
    // Assert: task still open, both outcomes still None after the throw.
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests\BeBoosted.Tests --filter "FullyQualifiedName~TaskDetailEditingTests"`
Expected: build FAILS with `CS1061: 'CalendarService' does not contain a definition for 'UpdateTaskDetails'`. Record it.

- [ ] **Step 3: Write the minimal implementation**

In `CalendarService.cs`, after `CreateTask`:

```csharp
/// <summary>
/// The whole-task editor's save: task fields plus the aggregate completion
/// transition. Never changes any session's date, time, or recurrence —
/// aggregate completion may still resolve or reopen one-off siblings'
/// outcomes, exactly like the task-row checkbox.
/// </summary>
public TaskItem UpdateTaskDetails(
    TaskId taskId, TaskDetailsRequest details, TaskCompletionRequest? completion = null)
{
    var task = RequireTask(taskId);
    var sessions = blocks.GetForTask(taskId);

    if (string.IsNullOrWhiteSpace(details.Title))
    {
        throw new DomainException("A task needs a title.");
    }

    if (details.EstimatedDuration is { } estimate && estimate <= TimeSpan.Zero)
    {
        throw new DomainException("An estimated duration must be positive.");
    }

    if (completion is { Completed: true } && sessions.Any(s => s.Recurrence is not null))
    {
        throw new DomainException("A repeating task completes per occurrence, not as a whole.");
    }

    var now = clock.Now;
    task.Rename(details.Title, now);
    task.SetEstimatedDuration(details.EstimatedDuration, now);
    task.SetDeadline(details.Deadline, now);
    task.AssignToProject(details.ProjectId, now);

    var touched = completion is { } request
        ? ApplyAggregateCompletion(task, sessions, request.Completed, now, out _)
        : [];

    mutations.Execute((blockRepo, _, taskRepo) =>
    {
        taskRepo.Update(task);
        foreach (var session in touched)
        {
            blockRepo.Update(session);
        }
    });
    return task;
}
```

- [ ] **Step 4: Focused tests PASS** — `dotnet test tests\BeBoosted.Tests --filter "FullyQualifiedName~TaskDetailEditingTests|FullyQualifiedName~UpdateTaskDetails_Failing"`
- [ ] **Step 5: Both full suites green** — `dotnet test tests\BeBoosted.Tests` and `dotnet test tests\BeBoosted.Desktop.Tests`.
- [ ] **Step 6: Checkpoint procedure.** This task's delta: `CalendarService.cs`, `TaskDetailEditingTests.cs`, `CalendarMutationAtomicityTests.cs`. Suggested boundary: `feat: focused whole-task save path (UpdateTaskDetails)`.

---

### Task 2: `CalendarService.UpdateSessionSchedule`

**Files:**
- Modify: `src/BeBoosted.Application/Calendar/CalendarService.cs` (after `UpdateTaskDetails`)
- Create: `tests/BeBoosted.Tests/Calendar/SessionScheduleEditingTests.cs`
- Modify: `tests/BeBoosted.Tests/Calendar/CalendarMutationAtomicityTests.cs` (one rollback fact)

**Interfaces:**
- Consumes: `TaskScheduleRequest(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, RecurrenceRule? Recurrence)` (`CalendarService.cs:11-12`), helpers `RemoveObsoleteCompletions`, `ApplyOccurrenceCompletion`, `Require`, `RequireTask`.
- Produces: `public CalendarBlock UpdateSessionSchedule(TaskId taskId, CalendarBlockId sessionId, TaskScheduleRequest schedule, TaskCompletionRequest? occurrenceCompletion = null)` — one block's schedule + staged occurrence completion, atomically; never touches task detail fields; conversion reconciliation preserved (a completed one-off converted to repeating reopens the task and clears the outcome; converting to one-off never promotes occurrence completion; a conversion never completes anything).

- [ ] **Step 1: Write the failing tests** — `SessionScheduleEditingTests.cs` (same fixture prologue; full bodies for each):

```csharp
[Fact] public void UpdateSessionSchedule_ReschedulesExactlyTheNamedSession()
{ /* two one-offs; reschedule the second; the first stays bit-identical (date/start/end) */ }
[Fact] public void UpdateSessionSchedule_SessionOfAnotherTask_IsRejected()
{ /* DomainException "That session belongs to a different task."; nothing changes */ }
[Fact] public void UpdateSessionSchedule_EndBeforeStart_IsRejected()
{ /* DomainException "A block must end after it starts." */ }
[Fact] public void UpdateSessionSchedule_WeekdayChange_PurgesObsoleteOccurrenceRows()
{ /* weekly Tue+Thu with a completed Thu; save Tue-only → the Thu row is gone */ }
[Fact] public void UpdateSessionSchedule_CompletingAnOccurrenceTheEditRemoves_IsRejected()
{ /* occurrenceCompletion on a Thursday while the new weekday set drops Thursday →
     "That occurrence no longer exists after this change — untick Completed or keep
     its weekday." */ }
[Fact] public void UpdateSessionSchedule_StagedOccurrenceCompletion_UpsertsTheRow_Atomically()
{ /* kept repeating: (date, true) → IsOccurrenceCompleted; (date, false) → row removed */ }
[Fact] public void UpdateSessionSchedule_CompletedOneOffToRepeating_ReopensTheTask_AndClearsTheOutcome()
{ /* completed task + Done one-off; save with Recurrence → task open, Outcome None, repeating */ }
[Fact] public void UpdateSessionSchedule_RepeatingToOneOff_NeverPromotesOccurrenceCompletion()
{ /* repeating with a completed occurrence; save without Recurrence, no occurrenceCompletion →
     task open; one-off, Outcome None; all occurrence rows gone */ }
[Fact] public void UpdateSessionSchedule_MissingSession_Throws_NoLongerExists()
{ /* DomainException containing "no longer exists" */ }
```

Atomicity addition:

```csharp
[Fact]
public void UpdateSessionSchedule_FailureDuringCompletionReconciliation_RollsBackTheReschedule()
{
    // Failing completion repository (throw on Remove); weekday-purge save →
    // after the throw the block keeps its original weekday set and the row survives.
}
```

- [ ] **Step 2: RED** — `dotnet test tests\BeBoosted.Tests --filter "FullyQualifiedName~SessionScheduleEditingTests"` → `CS1061 ... 'UpdateSessionSchedule'`.
- [ ] **Step 3: Minimal implementation**

```csharp
/// <summary>
/// The session editor's save: one block's schedule plus the staged occurrence
/// completion, atomically. Task detail fields are never touched here; the only
/// task effect is conversion reconciliation (a completed one-off converted to
/// repeating reopens the Task — a repeating task is never globally complete).
/// </summary>
public CalendarBlock UpdateSessionSchedule(
    TaskId taskId,
    CalendarBlockId sessionId,
    TaskScheduleRequest schedule,
    TaskCompletionRequest? occurrenceCompletion = null)
{
    var task = RequireTask(taskId);
    var session = Require(sessionId);
    if (session.TaskId != taskId)
    {
        throw new DomainException("That session belongs to a different task.");
    }

    if (schedule.EndTime <= schedule.StartTime)
    {
        throw new DomainException("A block must end after it starts.");
    }

    if (occurrenceCompletion is { Completed: true } && schedule.Recurrence is { } recurrence
        && !recurrence.OccursOn(occurrenceCompletion.OpenedOccurrence, schedule.Date))
    {
        throw new DomainException(
            "That occurrence no longer exists after this change — untick Completed or keep its weekday.");
    }

    var now = clock.Now;
    session.Reschedule(schedule.Date, schedule.StartTime, schedule.EndTime, now);
    session.SetRecurrence(schedule.Recurrence, now);

    // Conversion reconciliation: a repeating schedule forbids global completion,
    // and a conversion never completes anything (spec, Behavior — session editor).
    var taskTouched = false;
    if (schedule.Recurrence is not null)
    {
        if (task.IsCompleted)
        {
            task.Reopen(now);
            taskTouched = true;
        }

        if (session.Outcome != BlockOutcome.None)
        {
            session.ClearOutcome(now);
        }
    }

    mutations.Execute((blockRepo, completionRepo, taskRepo) =>
    {
        if (taskTouched)
        {
            taskRepo.Update(task);
        }

        blockRepo.Update(session);
        RemoveObsoleteCompletions(completionRepo, session);
        if (occurrenceCompletion is { } request && session.Recurrence is not null
            && session.OccursOn(request.OpenedOccurrence))
        {
            ApplyOccurrenceCompletion(
                completionRepo, session, request.OpenedOccurrence, request.Completed);
        }
    });
    return session;
}
```

- [ ] **Step 4: Focused PASS** (Step 2 filter plus the atomicity fact).
- [ ] **Step 5: Both full suites green.**
- [ ] **Step 6: Checkpoint procedure.** Delta: `CalendarService.cs`, `SessionScheduleEditingTests.cs`, `CalendarMutationAtomicityTests.cs`. Boundary: `feat: focused session-schedule save path`.

---

### Task 3: `CalendarService.AddSession` + `UnscheduleAllSessions`

**Files:**
- Modify: `src/BeBoosted.Application/Calendar/CalendarService.cs`
- Create: `tests/BeBoosted.Tests/Calendar/SessionAdditionAndUnscheduleAllTests.cs`

**Interfaces:**
- Produces: `public CalendarBlock AddSession(TaskId taskId, TaskScheduleRequest schedule)` (recurrence allowed — unlike `ScheduleTask`; rejects a completed task with the existing message; single-row insert, atomic by construction) and `public void UnscheduleAllSessions(TaskId taskId)` (every block + its completion rows in one `mutations.Execute`; the task survives).

- [ ] **Step 1: Failing tests** (full bodies, same fixture):

```csharp
[Fact] public void AddSession_CreatesAOneOffBlock_ForTheTask() { /* date/start/end persisted; TaskId set */ }
[Fact] public void AddSession_WithRecurrence_CreatesARepeatingBlock() { /* Recurrence round-trips */ }
[Fact] public void AddSession_OnACompletedTask_IsRejected()
{ /* "That task is already complete — reopen it before scheduling more work."; no block added */ }
[Fact] public void AddSession_EndBeforeStart_IsRejected() { /* "A block must end after it starts." */ }
[Fact] public void UnscheduleAllSessions_RemovesEveryBlock_AndItsCompletionRows_KeepingTheTask()
{ /* one-off + repeating-with-completed-occurrence; after: GetForTask empty, completion
     rows gone (fresh repo over the same factory), task present and open */ }
[Fact] public void UnscheduleAllSessions_FailingMidway_RollsBackEveryRemoval()
{ /* failing block repo throws on the second Delete; both blocks and the row survive */ }
```

- [ ] **Step 2: RED** — `dotnet test tests\BeBoosted.Tests --filter "FullyQualifiedName~SessionAdditionAndUnscheduleAllTests"` → `CS1061` for `AddSession`.
- [ ] **Step 3: Minimal implementation**

```csharp
/// <summary>A new session for an existing task; the only entry that may create a second repeating schedule.</summary>
public CalendarBlock AddSession(TaskId taskId, TaskScheduleRequest schedule)
{
    var task = RequireTask(taskId);
    if (task.IsCompleted)
    {
        throw new DomainException(
            "That task is already complete — reopen it before scheduling more work.");
    }

    if (schedule.EndTime <= schedule.StartTime)
    {
        throw new DomainException("A block must end after it starts.");
    }

    var block = CalendarBlock.CreateTaskSession(
        taskId, schedule.Date, schedule.StartTime, schedule.EndTime, clock.Now,
        schedule.Recurrence);
    blocks.Add(block);
    return block;
}

/// <summary>Removes every session of a task (with completion rows) in one transaction; the task survives.</summary>
public void UnscheduleAllSessions(TaskId taskId)
{
    _ = RequireTask(taskId);
    mutations.Execute((blockRepo, completionRepo, _) =>
    {
        foreach (var session in blockRepo.GetForTask(taskId))
        {
            RemoveSession(blockRepo, completionRepo, session);
        }
    });
}
```

- [ ] **Step 4: Focused PASS.** **Step 5: Both full suites green.**
- [ ] **Step 6: Checkpoint procedure.** Delta: `CalendarService.cs`, `SessionAdditionAndUnscheduleAllTests.cs`. Boundary: `feat: add-session and unschedule-all service operations`.

---

### Task 4: `SessionListBuilder` + `SessionRowData` (pure)

**Files:**
- Create: `src/BeBoosted.Desktop/ViewModels/SessionListBuilder.cs`
- Create: `tests/BeBoosted.Desktop.Tests/ViewModels/SessionListBuilderTests.cs`

(`SessionRowViewModel` is **not** created here — it needs its owner type and
is created in Task 7 together with `WholeTaskEditorViewModel`, so every
checkpoint compiles.)

**Interfaces:**
- Consumes: `CalendarBlock` (`Date`, `StartTime`, `EndTime`, `Duration`, `Recurrence`, `Outcome`, `CreatedAt`, `Id`), `TaskRowViewModel.FormatDuration(TimeSpan)` (existing formatter, see `ProjectDetailViewModel.cs:260`).
- Produces (consumed by Tasks 6–10):

```csharp
public sealed record SessionRowData(
    CalendarBlockId Id,
    bool IsRepeating,
    string PrimaryText,      // "Wed, Aug 26" | "Mon · Wed · Fri" (Monday-first)
    string SecondaryText,    // "9:00 – 10:00 AM · 1 h"
    string? PositionText,    // "SESSION 2 OF 3" (one-off only, resolved history included)
    string? StatusChip,      // null | "DONE" | "NEEDS MORE TIME" | "DIDN'T HAPPEN"
    string AccessibleName,   // "Session 2 of 3 — Wednesday, August 26, 9:00 AM to 10:00 AM"
                             //   (+ ", done" etc.); series: "Repeating schedule — Monday,
                             //   Wednesday, 9:00 AM to 10:00 AM" — words, no "·" glyphs
    string EditControlName,  // "Edit session 2 of 3" | "Edit repeating schedule"
    string RemoveControlName);

public static class SessionListBuilder
{
    /// <summary>Rows in (Date, StartTime, CreatedAt, Id) order; X of N counts one-off blocks only.</summary>
    public static IReadOnlyList<SessionRowData> Build(IReadOnlyList<CalendarBlock> sessions);

    /// <summary>Position of one block among the one-offs; (0, count) for a repeating block.</summary>
    public static (int Position, int OneOffCount) PositionOf(
        IReadOnlyList<CalendarBlock> sessions, CalendarBlockId id);

    /// <summary>"0 sessions" · "3 sessions · 4 h" · "1 session · 1 h" · "3 sessions · all done"
    /// (every one-off Done) · "repeating · 30 min" · "2 one-off · repeating" ·
    /// "2 one-off · 2 repeating".</summary>
    public static string SummaryFor(IReadOnlyList<CalendarBlock> sessions);
}
```

- [ ] **Step 1: Write the failing tests** — `SessionListBuilderTests` (plain `[Fact]`s, no Avalonia; full bodies seeding `CalendarBlock.CreateTaskSession` through a `FakeClock` and asserting the exact strings):

```csharp
[Fact] public void Build_OrdersByDateStartCreatedThenId() { /* three one-offs shuffled → sorted */ }
[Fact] public void Build_NumbersOneOffsOnly_SkippingRepeatingRows()
{ /* one-off, repeating, one-off → "SESSION 1 OF 2", null, "SESSION 2 OF 2" (frame 4f) */ }
[Fact] public void Build_ResolvedHistoryKeepsItsPosition_AndChips()
{ /* Outcome DidntHappen → PositionText intact, StatusChip "DIDN'T HAPPEN" */ }
[Fact] public void Build_AccessibleNames_SpellDatesAndTimes_WithoutGlyphs()
{ /* exact automation strings incl. ", done"; Assert.DoesNotContain("·", name) */ }
[Fact] public void PositionOf_ReturnsTheOneOffOrdinal_AndZeroForRepeating() { }
[Fact] public void SummaryFor_CoversEveryShape()
{ /* empty → "0 sessions"; 1 one-off → "1 session · 1 h"; 3 one-offs → "3 sessions · 4 h";
     all Done → "3 sessions · all done"; repeating only → "repeating · 30 min";
     mixed → "2 one-off · repeating"; two repeating → "2 one-off · 2 repeating" */ }
[Fact] public void Build_RepeatingPrimaryText_ListsWeekdaysMondayFirst() { /* "Mon · Wed · Fri" */ }
```

- [ ] **Step 2: RED** — `dotnet test tests\BeBoosted.Desktop.Tests --filter "FullyQualifiedName~SessionListBuilderTests"` → `CS0246: The type or namespace name 'SessionListBuilder' could not be found`.
- [ ] **Step 3: Minimal implementation** — pure static LINQ over the ordered
  list; time-range text `$"{start:h:mm} – {end:h:mm tt}"` plus the shared
  duration formatter; chips map `BlockOutcome` to the uppercase display
  strings (frames 4l/3a) while accessible names use the lowercase phrases
  from the spec's Accessibility section.
- [ ] **Step 4: Focused PASS.** **Step 5: Both full suites green.**
- [ ] **Step 6: Checkpoint procedure.** Delta: the two new files. Boundary: `feat: session list builder`.

---

### Task 5: `ScheduleFieldsViewModel`, `ProjectOptionViewModel` relocation, `EditorPrompts`

**Files:**
- Create: `src/BeBoosted.Desktop/ViewModels/ScheduleFieldsViewModel.cs`
- Create: `src/BeBoosted.Desktop/ViewModels/ProjectOptionViewModel.cs` (moved verbatim from `TaskEditorViewModel.cs:12-22`; delete the original class from `TaskEditorViewModel.cs` in the same step so the type exists exactly once)
- Create: `src/BeBoosted.Desktop/ViewModels/EditorPrompts.cs`
- Modify: `src/BeBoosted.Desktop/ViewModels/TaskEditorViewModel.cs` (only the 11-line class removal)
- Create: `tests/BeBoosted.Desktop.Tests/ViewModels/ScheduleFieldsViewModelTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed partial class ScheduleFieldsViewModel : ViewModelBase
{
    [ObservableProperty] public partial DateTimeOffset? Date { get; set; }
    [ObservableProperty] public partial TimeSpan? Start { get; set; }
    [ObservableProperty] public partial TimeSpan? End { get; set; }
    [ObservableProperty] public partial bool RepeatsWeekly { get; set; }

    /// <summary>Sunday-first, matching approved frames 4b/4g (S M T W T F S).</summary>
    public ObservableCollection<DayToggleViewModel> Days { get; } =
    [
        new(DayOfWeek.Sunday), new(DayOfWeek.Monday), new(DayOfWeek.Tuesday),
        new(DayOfWeek.Wednesday), new(DayOfWeek.Thursday), new(DayOfWeek.Friday),
        new(DayOfWeek.Saturday),
    ];

    public void LoadDefaults(DateOnly date, TimeOnly start, TimeOnly end);
    public void Load(CalendarBlock session);          // date/times/recurrence into the fields

    /// <summary>Null with the spec error when incomplete; otherwise the request.
    /// Zero ticked weekdays default to the date's weekday (rule moved verbatim
    /// from CalendarViewModel.SaveTaskEditor, CalendarViewModel.cs:305-309).</summary>
    public TaskScheduleRequest? TryBuildSchedule(out string? error);

    public ScheduleSnapshot Capture();                // value record for dirty-compare
    public bool IsDirtyAgainst(ScheduleSnapshot snapshot);
}

public sealed record ScheduleSnapshot(
    DateTimeOffset? Date, TimeSpan? Start, TimeSpan? End,
    bool RepeatsWeekly, IReadOnlyList<DayOfWeek> SelectedDays);

// EditorPrompts.cs
public sealed record ConfirmationPrompt(string Message, string ConfirmLabel, bool IsTaskDeletion);
public sealed record GatePrompt(string Title, string SaveLabel);
// The gate sub-line is fixed frame copy: "Save or discard before continuing."
// (frame 4o); the other two actions are always "Discard changes and continue"
// and "Keep editing" — rendered by the views, not stored per prompt.
```

- [ ] **Step 1: Failing tests** — `ScheduleFieldsViewModelTests` (full bodies; the VM is constructed directly, no owner):

```csharp
[Fact] public void TryBuildSchedule_MissingAnyField_ReturnsTheSpecError()
{ /* null date → (null, "Pick a date, start, and end.") */ }
[Fact] public void TryBuildSchedule_RepeatsWithNoTickedDay_DefaultsToTheDatesWeekday()
{ /* Tue date, Repeats on, nothing ticked → Weekly(1, Tuesday) */ }
[Fact] public void Load_RoundTripsARepeatingSession() { /* days ticked from recurrence */ }
[Fact] public void IsDirtyAgainst_DetectsEveryFieldAndDayChange() { /* snapshot compare */ }
[Fact] public void Days_AreSundayFirst() { /* frame 4b order */ }
```

- [ ] **Step 2: RED** — `CS0246 'ScheduleFieldsViewModel'`.
- [ ] **Step 3: Minimal implementation** as declared (logic lifted from
  `TaskEditorViewModel`'s edit ctor `:88-99` and `SaveTaskEditor` `:294-316`).
- [ ] **Step 4: Focused PASS.** **Step 5: Both full suites green** (the
  `ProjectOptionViewModel` move is compile-neutral).
- [ ] **Step 6: Checkpoint procedure.** Delta: four new files + `TaskEditorViewModel.cs`. Boundary: `feat: shared editor field and prompt models`.

---

### Task 6: `SessionEditorViewModel` — block-entry one-off and repeating modes

**Files:**
- Create: `src/BeBoosted.Desktop/ViewModels/SessionEditorViewModel.cs`
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` — add the `ActiveTaskEditor` slot, `OpenSessionEditorForBlock(CalendarBlockId, DateOnly)`, `CloseActiveEditor()`, `TrySaveSession`, `SaveSession`, `RemoveSessionFromSessionEditor` (legacy `TaskEditor` and every legacy entry point stay untouched until Task 11)
- Modify: `src/BeBoosted.Desktop/BeBoosted.Desktop.csproj` + `tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj` — `Microsoft.Data.Sqlite` reference (prerequisite §3), only if absent
- Create: `tests/BeBoosted.Desktop.Tests/ViewModels/SessionEditorViewModelTests.cs`

**Interfaces:**
- Consumes: `UpdateSessionSchedule` (T2), `UnscheduleSession` (existing, transactional, `CalendarService.cs:503-513`), `IsOccurrenceCompleted`, `ScheduleFieldsViewModel` + prompts (T5), `SessionListBuilder.PositionOf` (T4).
- Produces:

```csharp
public enum SessionEditorMode { OneOff, Repeating, New }   // New activates in Task 8

public sealed partial class SessionEditorViewModel : ViewModelBase
{
    internal TaskId TaskId { get; }
    internal CalendarBlockId? SessionId { get; }        // null in New mode (Task 8)
    internal DateOnly? OccurrenceDate { get; }          // repeating: the opened occurrence
    public SessionEditorMode Mode { get; }

    public string ScopeLabel { get; }   // "THIS SESSION · {X} OF {N}" | "REPEATING SCHEDULE" | "NEW SESSION"
    public string TaskTitle { get; }                    // read-only context, trimmed w/ tooltip
    public string TaskContext { get; }                  // "DECA · due Sun, Aug 16" | project | due-only | ""
    public bool ShowEditWholeTask => Mode != SessionEditorMode.New;  // command arrives in Task 9
    public string? NumberingNote { get; }               // mixed one-off only (exact spec sentence)
    public string? ResolvedNote { get; }                // "Resolved: Didn't happen · Aug 18"

    public ScheduleFieldsViewModel Schedule { get; }
    public bool ShowDateField { get; }                  // hidden while Mode==Repeating && RepeatsWeekly;
                                                        // unticking reveals it prefilled with OccurrenceDate
    public bool ShowOccurrenceSection { get; }          // Repeating && RepeatsWeekly
    public string OccurrenceSectionLabel { get; }       // "THIS OCCURRENCE · {SAT, AUG 15}"
    [ObservableProperty] public partial bool IsOccurrenceCompleted { get; set; }
    public string OccurrenceCheckboxText => "Mark this occurrence complete";
    public string OccurrenceNote { get; }               // "Only {Sat, Aug 15}. Other occurrences aren't affected."
    public string SeriesNote =>
        "Time and weekday changes apply to every occurrence of this schedule.";

    public string SaveButtonText { get; }               // "Save session" | "Save schedule" | "Add session"
    public string? RemoveButtonText { get; }            // "Remove this session" | "Remove schedule" | null

    [ObservableProperty] public partial bool IsStale { get; internal set; }
    public string StaleNotice =>
        "This session no longer exists — it was removed elsewhere. Cancel to go back.";
    [ObservableProperty] public partial ConfirmationPrompt? Confirmation { get; private set; }
    [ObservableProperty] public partial GatePrompt? Gate { get; private set; }   // used from Task 9
    [ObservableProperty] public partial string? Error { get; internal set; }

    // Commands this task: Save, Cancel, RequestRemove, ConfirmPrompt, KeepPrompt.
    internal bool IsDirty { get; }                      // Schedule vs snapshot + IsOccurrenceCompleted
    internal void MarkSaved();                          // snapshot advances to the current values
    internal bool DismissActivePrompt();                // Confirmation/Gate → null; true if dismissed
}
```

`CalendarViewModel` additions — the gate-safe persistence split:

```csharp
[ObservableProperty]
public partial object? ActiveTaskEditor { get; private set; }   // new-world slot

internal SessionEditorViewModel OpenSessionEditorForBlock(CalendarBlockId id, DateOnly occurrenceDate);
internal void CloseActiveEditor();                              // ActiveTaskEditor = null

/// <summary>Persists only. Reloads and announces exactly once on success and
/// advances the editor's dirty snapshot (MarkSaved). NEVER navigates — callers
/// decide what follows. savedSessionId: the edited block's id, or, in New mode
/// (Task 8), the id returned by AddSession — so the caller can focus the exact
/// new row. False + inline error and null id on failure.</summary>
internal bool TrySaveSession(SessionEditorViewModel editor, out CalendarBlockId? savedSessionId)
{
    savedSessionId = null;
    try
    {
        // OneOff/Repeating: _calendar.UpdateSessionSchedule(taskId, sessionId,
        //     schedule, Mode==Repeating ? new TaskCompletionRequest(OccurrenceDate!.Value,
        //     editor.IsOccurrenceCompleted) : null);
        //     savedSessionId = editor.SessionId;
        // New (Task 8): savedSessionId = _calendar.AddSession(taskId, schedule).Id;
        editor.MarkSaved();
        Reload();
        DataChanged?.Invoke();
        return true;
    }
    catch (DomainException e) when (e.Message.Contains("no longer exists"))
    { editor.IsStale = true; return false; }
    catch (DomainException e) { editor.Error = e.Message; return false; }
    catch (SqliteException) { editor.Error = "Couldn't save — nothing was changed. Try again."; return false; }
}

internal void SaveSession(SessionEditorViewModel editor)
{
    if (TrySaveSession(editor, out _))
    {
        CloseActiveEditor();   // Task 7 extends: a pushed editor returns to its parent instead
    }
}

internal void RemoveSessionFromSessionEditor(SessionEditorViewModel editor);
// same try/catch shape over _calendar.UnscheduleSession(sessionId); success closes
// (Task 7 extends to parent-return); stale → IsStale; Sqlite → generic line; failure
// never navigates, never announces.
```

- [ ] **Step 1: Failing tests** (full bodies; fixture `TestShell.CreateCalendarViewModel`; failure decorators are private nested classes in the test file, per house pattern):

```csharp
[Fact] public void BlockEntry_BuildsTheOneOffEditor_WithPositionLabel()
{ /* 2nd of 3 → "THIS SESSION · 2 OF 3"; single session → "· 1 OF 1" (spec test 10) */ }
[Fact] public void OneOffEditor_ExposesNoCompletionControl()
{ /* ShowOccurrenceSection false in OneOff (spec test 11) */ }
[Fact] public void RepeatingEditor_StagesOccurrenceCompletion_AndSavesItAtomically()
{ /* toggle + weekday change in one Save → one announcement, row upserted (spec test 11) */ }
[Fact] public void UntickingRepeats_HidesTheOccurrenceSection_AndDiscardsItsValue()
{ /* staged true + untick + Save → one-off, task NOT completed, no row (spec test 12) */ }
[Fact] public void CompletedOneOffMadeRepeating_ReopensTheTask() { /* (spec test 12) */ }
[Fact] public void RemoveThisSession_Confirms_ThenKeepsTaskAndSiblings()
{ /* exact copy w/ date+time; block gone; siblings + task alive; closes (spec test 13) */ }
[Fact] public void RemoveSchedule_Confirms_ThenKeepsTaskAndUnrelatedSessions()
{ /* repeating copy; completion rows gone; one-off sibling untouched (spec test 13) */ }
[Fact] public void MixedTask_OneOffEditor_ShowsTheNumberingNote() { /* exact text (spec test 10) */ }
[Fact] public void SaveSession_Success_ClosesAndAnnouncesOnce() { /* ActiveTaskEditor null, count 1 */ }
[Fact] public void StaleSave_ShowsTheFixedCopy_AndGoesInert()
{ /* delete the block behind the editor; Save → IsStale, editor OPEN, nothing persisted,
     nothing announced (spec test 15 / frame 4m left) */ }
[Fact] public void StaleRemove_BehavesTheSameWay() { }
[Fact] public void ValidationError_PinsInsideTheSessionEditor()
{ /* end <= start → "A block must end after it starts.", editor open (frame 4n left) */ }
[Fact] public void SqliteFailure_OnSave_MapsToTheGenericLine_NoNavigationNoAnnouncement()
{ /* block-repo decorator throwing new SqliteException("locked", 5) → generic line,
     ActiveTaskEditor unchanged, zero announcements (spec state 16) */ }
[Fact] public void SqliteFailure_OnRemoveThisSession_AndOnRemoveSchedule_BehaveTheSameWay()
{ /* both removal paths, same assertions (per-path failure coverage) */ }
[Fact] public void CancelAndEscape_NeverPersist() { /* incl. staged occurrence completion */ }
```

- [ ] **Step 2: RED** — `dotnet test tests\BeBoosted.Desktop.Tests --filter "FullyQualifiedName~SessionEditorViewModelTests"` → `CS0246 'SessionEditorViewModel'`.
- [ ] **Step 3: Minimal implementation** — the class + the `CalendarViewModel` members above (New-mode branches compile but are unreachable until Task 8's factory exists).
- [ ] **Step 4: Focused PASS.** **Step 5: Both full suites green** (legacy editor untouched; `IsTaskEditorOpen` still legacy — nothing binds `ActiveTaskEditor` yet).
- [ ] **Step 6: Checkpoint procedure.** Delta: `SessionEditorViewModel.cs`, `CalendarViewModel.cs`, both `.csproj` files (if edited), `SessionEditorViewModelTests.cs`. Boundary: `feat: session editor block-entry modes`.

---

### Task 7: `WholeTaskEditorViewModel` + `SessionRowViewModel` + push/return navigation

**Files:**
- Create: `src/BeBoosted.Desktop/ViewModels/WholeTaskEditorViewModel.cs`
- Create: `src/BeBoosted.Desktop/ViewModels/SessionRowViewModel.cs` (same task as its owner — it references `WholeTaskEditorViewModel` and compiles the moment it exists)
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` — `OpenWholeTaskEditor`, `TrySaveWholeTask`, `SaveWholeTask`, `EditorNavigation`, `OpenSessionEditorFromWholeTask`, `ReturnToWholeTask`, `EditorRowFocusRequested`; extend `SaveSession`/`RemoveSessionFromSessionEditor` so a pushed session editor returns to its parent instead of closing; make `EditorOccurrenceFor` (`CalendarViewModel.cs:234-252`) `internal` (the F-15 resolver survives for repeating Schedule-row entry)
- Create: `tests/BeBoosted.Desktop.Tests/ViewModels/WholeTaskEditorViewModelTests.cs`
- Modify: `tests/BeBoosted.Desktop.Tests/ViewModels/SessionEditorViewModelTests.cs` (parent-return facts)

**Interfaces:**
- Consumes: `UpdateTaskDetails` (T1), `SessionListBuilder` (T4), T5's shared models, T6's session editor.
- Produces:

```csharp
public sealed partial class SessionRowViewModel(WholeTaskEditorViewModel owner, SessionRowData data)
    : ViewModelBase
{
    public SessionRowData Data { get; } = data;

    [RelayCommand] private void Edit() => owner.EditRow(this);
    [RelayCommand] private void Remove() => owner.RequestRemoveRow(this);   // flow lands in Task 8
}

public sealed partial class WholeTaskEditorViewModel : ViewModelBase
{
    internal TaskId? TaskId { get; }                 // null in create mode (Task 8)
    public bool IsEditMode => TaskId is not null;
    public bool IsCreateMode => TaskId is null;
    public string ScopeLabel => "WHOLE TASK";

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial ProjectOptionViewModel? SelectedProject { get; set; }
    [ObservableProperty] public partial DateTimeOffset? Deadline { get; set; }
    [ObservableProperty] public partial decimal? DurationMinutes { get; set; }
    public ObservableCollection<ProjectOptionViewModel> ProjectOptions { get; }
    public bool HasProjects => ProjectOptions.Count > 1;

    // Schedule-derived members are OBSERVABLE, not constructor-only: schedule
    // mutations change them while the editor stays open, so RefreshSessions()
    // recomputes and notifies every one of them.
    [ObservableProperty] public partial bool ShowCompletion { get; private set; }   // edit mode && no repeating block
    [ObservableProperty] public partial bool IsCompleted { get; set; }
    public string CompletionCheckboxText => "Mark whole task complete";
    [ObservableProperty] public partial string? AggregateNote { get; private set; } // N>=2: "Completing or reopening applies to all {N} sessions."
    [ObservableProperty] public partial IReadOnlyList<string> ScheduleNotes { get; private set; } // repeating/mixed sentences + numbering note

    public ObservableCollection<SessionRowViewModel> Sessions { get; }
    [ObservableProperty] public partial string ScheduleSummary { get; private set; }   // SessionListBuilder.SummaryFor
    [ObservableProperty] public partial bool ShowEmptyState { get; private set; }
    public string EmptyStateText =>
        "No sessions scheduled. The task stays in your Inbox until you add one.";
    [ObservableProperty] public partial bool ShowUnscheduleAll { get; private set; }   // rows >= 2
    [ObservableProperty] public partial bool CanAddSession { get; private set; }       // edit: !task.IsCompleted; create: true
    [ObservableProperty] public partial string? AddSessionBlockedNote { get; private set; } // "Task complete — reopen it to schedule more sessions."

    [ObservableProperty] public partial ConfirmationPrompt? Confirmation { get; private set; }
    [ObservableProperty] public partial GatePrompt? Gate { get; private set; }
    [ObservableProperty] public partial string? Error { get; internal set; }
    [ObservableProperty] public partial string? ScheduleNotice { get; internal set; }

    public ScheduleFieldsViewModel InlineSchedule { get; }          // create mode (Task 8)
    [ObservableProperty] public partial bool ShowInlineSchedule { get; private set; }

    public string SaveButtonText => IsEditMode ? "Save task" : "Add task";

    // This task's commands/methods: Save, Cancel; EditRow (direct push — the
    // gate wraps it in Task 9); RefreshSessions; MarkSaved; DismissActivePrompt.
    internal void EditRow(SessionRowViewModel row)
        => _owner.OpenSessionEditorFromWholeTask(this, row.Data.Id);
    internal void RequestRemoveRow(SessionRowViewModel row);   // Task 8 fills the flow; this task: no-op body + doc comment
    internal bool IsDirty { get; }
    internal void MarkSaved();       // dirty snapshot advances to the current values
    internal bool DismissActivePrompt();
    internal void RefreshSessions(); // recomputes rows + EVERY schedule-derived observable above
}
```

`CalendarViewModel` — the persistence split and the push/return leg:

```csharp
internal sealed record EditorNavigation(
    WholeTaskEditorViewModel? Parent,      // non-null = pushed from the whole-task editor
    CalendarBlockId? ReturnRowId);         // row whose Edit button regains focus on return

internal EditorNavigation? Navigation { get; private set; }   // null except during a push

public event Action<CalendarBlockId?>? EditorRowFocusRequested;

internal WholeTaskEditorViewModel OpenWholeTaskEditor(TaskId taskId);

internal SessionEditorViewModel OpenSessionEditorFromWholeTask(
    WholeTaskEditorViewModel parent, CalendarBlockId sessionId);
// Repeating rows resolve their occurrence with EditorOccurrenceFor (F-15 rule:
// today's occurrence, else most recent elapsed, else anchor); sets
// Navigation = new(parent, sessionId).

internal void ReturnToWholeTask(bool refreshed, CalendarBlockId? focusRowId);
// ActiveTaskEditor = Navigation!.Parent; parent.RefreshSessions();
// EditorRowFocusRequested?.Invoke(focusRowId); Navigation = null.

/// <summary>Persists only; reloads + announces exactly once on success and
/// advances the editor's dirty snapshot (MarkSaved); never navigates.</summary>
internal bool TrySaveWholeTask(WholeTaskEditorViewModel editor)
{
    try
    {
        // edit mode: _calendar.UpdateTaskDetails(taskId, details,
        //            editor.ShowCompletion ? new TaskCompletionRequest(_clock.Today, editor.IsCompleted) : null)
        // create mode (Task 8): _calendar.CreateTask(details, inlineScheduleOrNull)
        editor.MarkSaved();   // the next gate check sees a clean draft
        Reload();
        DataChanged?.Invoke();
        return true;
    }
    catch (DomainException e) { editor.Error = e.Message; return false; }
    catch (SqliteException) { editor.Error = "Couldn't save — nothing was changed. Try again."; return false; }
}

internal void SaveWholeTask(WholeTaskEditorViewModel editor)
{
    if (TrySaveWholeTask(editor)) { CloseActiveEditor(); }
}

// SaveSession success (savedSessionId from TrySaveSession — the edited id, or
// the id AddSession returned in New mode, so the exact new row gets focused):
//   if (TrySaveSession(editor, out var savedSessionId))
//       if (Navigation is { Parent: not null }) ReturnToWholeTask(refreshed: true, savedSessionId);
//       else CloseActiveEditor();
// RemoveSessionFromSessionEditor success: ReturnToWholeTask(true, focusRowId: null)
// when pushed (the row is gone), else CloseActiveEditor().
```

- [ ] **Step 1: Failing tests** (full bodies; clean-draft wording so the facts survive Task 9's gate):

```csharp
[Fact] public void OpenWholeTaskEditor_ListsEverySession_Ordered_WithPositions()
{ /* 3 one-offs → "SESSION 1 OF 3".."3 OF 3", summary "3 sessions · 4 h" (spec test 1) */ }
[Fact] public void SaveTask_PersistsFields_AnnouncesOnce_ClosesAndNeverTouchesSchedules()
{ /* count == 1; ActiveTaskEditor null; block dates/times/recurrence unchanged (spec test 2) */ }
[Fact] public void CompletionControl_AbsentUnderRepeating_WithTheSentences() { /* spec test 3, frames 4e/4f */ }
[Fact] public void AggregateNote_AppearsAtTwoOrMoreOneOffs() { /* N=1 null; N=2 exact text */ }
[Fact] public void SaveTask_Completing_UsesAggregateAuthority() { /* task+siblings Done, one announcement */ }
[Fact] public void Cancel_NeverPersistsAnything() { /* fields + IsCompleted; ModifiedAt unchanged; count 0 */ }
[Fact] public void SaveTask_ValidationFailure_KeepsTheEditorOpenWithTheError_NoNavigation()
{ /* empty title → "A task needs a title.", ActiveTaskEditor unchanged, count 0 */ }
[Fact] public void SaveTask_SqliteFailure_MapsToTheGenericLine_AndStaysOpen()
{ /* task-repo decorator → generic line, open, count 0 (spec state 16 / frame 4n right) */ }
[Fact] public void ExistingUnscheduledTask_ShowsTheEmptyState_WithCompletionAvailable() { /* state 3 / 4c */ }
[Fact] public void CompletedTask_DisablesAddSession_WithTheNote() { /* spec test 9 / frame 4l */ }
[Fact] public void EditRow_WithACleanDraft_PushesTheSessionEditor()
{ /* ActiveTaskEditor becomes SessionEditorViewModel; Navigation.Parent == the editor */ }
[Fact] public void EditRow_OnARepeatingRow_ResolvesTheOccurrence_ByTheF15Rule()
{ /* occurs today → today; else most recent elapsed; future-only → anchor (spec test 10) */ }
// appended to SessionEditorViewModelTests:
[Fact] public void SaveInAPushedSessionEditor_ReturnsToTheRefreshedParent_AndRequestsRowFocus()
{ /* ActiveTaskEditor == the SAME WholeTaskEditorViewModel instance; rows renumbered;
     EditorRowFocusRequested fired with the edited row id; one announcement */ }
[Fact] public void CancelInAPushedSessionEditor_ReturnsWithoutPersisting() { }
[Fact] public void BlockEntrySessionEditor_StillClosesToTheInvoker() { /* Navigation null path intact */ }
```

- [ ] **Step 2: RED** — `dotnet test tests\BeBoosted.Desktop.Tests --filter "FullyQualifiedName~WholeTaskEditorViewModelTests"` → `CS0246 'WholeTaskEditorViewModel'`.
- [ ] **Step 3: Minimal implementation** — both classes + the `CalendarViewModel` members; the internal ctor `(CalendarViewModel owner, IReadOnlyList<ProjectOptionViewModel> options, TaskItem task, IReadOnlyList<CalendarBlock> sessions)` captures the dirty-compare snapshot; `RefreshSessions()` re-reads `GetSessionsForTask` and rebuilds rows/summary/notes/flags.
- [ ] **Step 4: Focused PASS.** **Step 5: Both full suites green.**
- [ ] **Step 6: Checkpoint procedure.** Delta: `WholeTaskEditorViewModel.cs`, `SessionRowViewModel.cs`, `CalendarViewModel.cs`, both editor test files. Boundary: `feat: whole-task editor core and push navigation`.

---

### Task 8: Whole-task schedule mutations, add mode, create mode

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/WholeTaskEditorViewModel.cs`
- Modify: `src/BeBoosted.Desktop/ViewModels/SessionEditorViewModel.cs` (New mode activation)
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` — `RemoveSessionFromEditor`, `UnscheduleAllFromEditor`, `DeleteTaskFromWholeTaskEditor`, `OpenAddSessionEditor(parent)`, `OpenNewWholeTaskEditor(DateOnly date, TimeOnly? start, TimeOnly? end, bool scheduled)`, the create branch of `TrySaveWholeTask`, the New branch of `TrySaveSession`
- Modify: both editor test files

**Interfaces:**
- Consumes: `UnscheduleSession`, `UnscheduleAllSessions`, `DeleteTask`, `AddSession`, `CreateTask` (all existing/T3).
- Produces on `WholeTaskEditorViewModel`: `RequestRemoveRow` (real flow), `AddSessionCommand`, `ClearInlineScheduleCommand`, `RequestUnscheduleAllCommand`, `RequestDeleteCommand`, `ConfirmPromptCommand`, `KeepPromptCommand`. Confirmation copy builders produce the exact spec strings (all variants unit-tested):
  - Remove row (one-off): `"Remove this session — {Wed, Aug 26 · 9:00–10:00 AM}? The task keeps its other {N−1} sessions."`; last session: `"… The task stays, unscheduled."`; confirm `"Remove session"`.
  - Remove row (repeating): `"Remove the repeating schedule? Every occurrence and its completion history go with it. The task stays."`; confirm `"Remove schedule"`.
  - Unschedule all: `"Remove all {N} sessions? The task itself stays."` / `"Remove {R} repeating schedules? Their completion history goes with them. The task stays."` / `"Remove {N} one-off sessions and the repeating schedule? The schedule's completion history goes with it. The task stays."` (singulars per the spec's Copy notes); confirm `"Remove all"`.
  - Delete task (`IsTaskDeletion = true`, umber treatment): the spec's five-variant table + `{R}` pluralization; confirm `"Delete task"`.
- `CalendarViewModel` mutation callbacks all follow the T6/T7 try/catch shape: success → `Reload()` + one `DataChanged` + `editor.RefreshSessions()` (editor stays open for row-level operations; Delete task closes); stale `DomainException` → `editor.ScheduleNotice = "That session was already removed — the list has been updated."` + refresh, no announcement; `SqliteException` → generic line, no navigation, no announcement.
- Create mode: `OpenNewWholeTaskEditor` builds create-mode editors for all three legacy new-task shapes (plain, slot-prefilled → `ShowInlineSchedule=true` + fields loaded, unscheduled → empty state); `AddSession` in create mode reveals `InlineSchedule` (no nested editor before the task exists); Save (`"Add task"`) → `CreateTask(details, InlineSchedule.TryBuildSchedule(...))` atomically.
- Add mode: `AddSessionCommand` in edit mode calls `_owner.OpenAddSessionEditor(this)` → `SessionEditorViewModel` Mode `New` (scope `"NEW SESSION"`, primary `"Add session"` — locked decision 1; no remove button; Repeats allowed); its save goes through `TrySaveSession`'s `AddSession` branch and returns to the parent with focus on the new row.

- [ ] **Step 1: Failing tests** (full bodies; representative list — every fact asserts announcement count, open/closed state, and persistence):

```csharp
[Fact] public void RemoveRow_Confirms_ThenRemovesOneBlock_KeepingSiblings_AndRenumbering() { /* spec test 4 */ }
[Fact] public void RemoveRow_LastSession_UsesTheUnscheduledVariant() { }
[Fact] public void RemoveRow_Repeating_UsesTheScheduleCopy_AndRemovesCompletionHistory() { }
[Fact] public void UnscheduleAll_CopyVariants_AndFullRemoval() { /* three copy shapes; task survives (spec test 4) */ }
[Fact] public void UnscheduleAll_HiddenBelowTwoRows() { }
[Fact] public void RemovingTheFinalRepeatingSchedule_MakesWholeTaskCompletionAvailable()
{ /* mixed task, editor open: confirm-remove the repeating row → ShowCompletion
     flips true, the repeating/mixed ScheduleNotes disappear, ScheduleSummary and
     ShowUnscheduleAll/CanAddSession recompute — RefreshSessions notifies every
     schedule-derived property, none is constructor-frozen */ }
[Fact] public void DeleteConfirmation_MatchesEveryScheduleShape_ThenDeletesAndCloses() { /* five variants (spec test 5) */ }
[Fact] public void StaleRowRemove_ShowsTheNotice_Refreshes_NoAnnouncement() { /* spec test 7 / frame 4m right */ }
[Fact] public void SqliteFailure_OnRowRemove_OnUnscheduleAll_AndOnDeleteTask_MapsToTheGenericLine()
{ /* one fact per path: generic line, editor open, zero announcements, no navigation */ }
[Fact] public void AddSession_EditMode_OpensTheNewModeEditor_AndFocusesTheCreatedRow()
{ /* scope "NEW SESSION", button "Add session"; save → parent active;
     EditorRowFocusRequested carries the EXACT block id AddSession returned
     (TrySaveSession's savedSessionId out value) */ }
[Fact] public void AddSession_NewMode_SqliteFailure_KeepsTheNewModeEditorOpen() { /* per-path failure coverage */ }
[Fact] public void CreateMode_AddTask_WithRevealedFields_CreatesTaskAndSessionAtomically() { /* spec test 8 */ }
[Fact] public void CreateMode_PrefilledEntry_ArrivesRevealed_UnscheduledEntry_ArrivesEmpty() { /* frames 4a/4b */ }
[Fact] public void CreateMode_SqliteFailure_KeepsTheDraft_AndCreatesNothing() { /* per-path failure coverage */ }
```

- [ ] **Step 2: RED** — `CS1061` for `RequestRemoveRowCommand` / `OpenNewWholeTaskEditor`.
- [ ] **Step 3: Minimal implementation** as specified (no gate yet — operations run directly; Task 9 tightens).
- [ ] **Step 4: Focused PASS.** **Step 5: Both full suites green.**
- [ ] **Step 6: Checkpoint procedure.** Delta: the three view models + both test files. Boundary: `feat: whole-task schedule mutations, add and create modes`.

---

### Task 9: Gates, promotion, Escape and prompt depth

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/WholeTaskEditorViewModel.cs` (`RunGated`, gate commands)
- Modify: `src/BeBoosted.Desktop/ViewModels/SessionEditorViewModel.cs` (`EditWholeTaskCommand`, gate commands)
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` (`PromoteToWholeTask`, `EscapeTaskEditor`)
- Modify: both editor test files

**Interfaces:**
- Produces:

```csharp
// WholeTaskEditorViewModel — the gate wraps EVERY immediately persisted schedule
// operation and every scope navigation while the draft is dirty (Delete task is
// the sole spec exception — its confirmation supersedes the draft):
private void RunGated(Action pending)
{
    if (!IsDirty) { pending(); return; }
    _pendingGateAction = pending;
    Gate = new GatePrompt("You have unsaved task changes.", "Save task and continue");
}
// EditRow, AddSession (edit mode), RequestRemoveRow, RequestUnscheduleAll all call
// RunGated(...); RequestDelete does not.
[RelayCommand] private void GateSaveAndContinue()
{
    var pending = _pendingGateAction;
    Gate = null;
    _pendingGateAction = null;
    if (_owner.TrySaveWholeTask(this))   // persists + announces once; editor STAYS ACTIVE
    {
        RefreshSessions();
        pending?.Invoke();               // navigation or the operation's own confirmation
    }
    // else: Error is set, editor open, pending discarded, no navigation, no announcement.
}
[RelayCommand] private void GateDiscardAndContinue();   // reset draft to snapshot; run pending
[RelayCommand] private void GateKeepEditing();          // Gate = null; pending dropped

// SessionEditorViewModel:
[RelayCommand] private void EditWholeTask()
{
    if (!IsDirty) { _owner.PromoteToWholeTask(this); return; }
    Gate = new GatePrompt("You have unsaved session changes.",
        Mode == SessionEditorMode.Repeating ? "Save schedule and continue" : "Save session and continue");
}
[RelayCommand] private void GateSaveAndContinue()
{
    Gate = null;
    if (_owner.TrySaveSession(this, out _))   // persists + announces once; does NOT return/close
    {
        _owner.PromoteToWholeTask(this);      // replaces the editor; ORIGINAL invoker focus preserved
    }
}

// CalendarViewModel:
internal void PromoteToWholeTask(SessionEditorViewModel editor);
// ActiveTaskEditor = OpenWholeTaskEditor-built VM for editor.TaskId; Navigation = null
// (no return leg); MainWindow's captured invoker focus target is untouched.

public void EscapeTaskEditor()
{
    switch (ActiveTaskEditor)
    {
        case WholeTaskEditorViewModel w when w.DismissActivePrompt(): return;   // 1. prompt first
        case SessionEditorViewModel s when s.DismissActivePrompt(): return;
        case SessionEditorViewModel when Navigation is { Parent: not null } nav:
            ReturnToWholeTask(refreshed: true, nav.ReturnRowId); return;        // 2. leave the push
        default: CloseActiveEditor(); return;                                   // 3. close top level
    }
}
```

`DismissActivePrompt()` (both editors): an open `Confirmation` or `Gate` becomes null (pending action dropped) and the method returns true; otherwise false. Prompt focus restoration is view behavior (Task 10).

- [ ] **Step 1: Failing tests** (full bodies — these are the revision-item-1 and -2 proofs):

```csharp
// Whole-task gate:
[Fact] public void Gate_DirtyDraft_PrecedesEveryScheduleOperation_AndDeleteIsExempt()
{ /* dirty title: EditRow/AddSession/RequestRemoveRow/RequestUnscheduleAll each open the
     Gate, not their confirmation and with no navigation; RequestDelete opens its
     confirmation directly (spec test 6) */ }
[Fact] public void GateSaveAndContinue_KeepsTheEditorActive_ThenRunsThePending()
{ /* dirty + EditRow → gate save → announcement count 1; ActiveTaskEditor transitions to
     the pushed SessionEditorViewModel whose Navigation.Parent is THE SAME whole-task
     instance; title persisted */ }
[Fact] public void GateSaveAndContinue_BeforeARemove_ShowsTheConfirmationAfterTheSave()
{ /* pending == RequestRemoveRow flow: after save the remove Confirmation is open,
     editor still active */ }
[Fact] public void GateSave_Failure_DiscardsPending_NoNavigationNoAnnouncement()
{ /* empty title + dirty → GateSaveAndContinue → Error set, Gate null, pending dropped,
     ActiveTaskEditor unchanged, count 0 */ }
[Fact] public void GateDiscardAndContinue_ResetsTheDraft_ThenRunsThePending() { }
[Fact] public void GateKeepEditing_DropsThePending() { }
[Fact] public void GateSaveAndContinue_LeavesACleanSnapshot_NoRegating()
{ /* dirty title + RequestRemoveRow → gate save (one announcement) → the remove
     confirmation opens; dismiss it; start Unschedule all WITHOUT a new edit →
     its confirmation opens directly, no gate — MarkSaved advanced the snapshot */ }
[Fact] public void SessionSave_AlsoAdvancesItsSnapshot()
{ /* after a successful TrySaveSession the session editor's IsDirty is false */ }
// Session gate + promotion:
[Fact] public void EditWholeTask_CleanDraft_PromotesImmediately_NoReturnLeg()
{ /* ActiveTaskEditor is WholeTaskEditorViewModel; Navigation null; Escape now closes */ }
[Fact] public void EditWholeTask_DirtyDraft_GatesWithTheModeSpecificSaveLabel()
{ /* OneOff → "Save session and continue"; Repeating → "Save schedule and continue" */ }
[Fact] public void SessionGateSaveAndContinue_Promotes_WithoutTheNormalReturn()
{ /* pushed editor, dirty: gate save → count 1, NO ReturnToWholeTask, NO row focus
     event, promotion happened, parent instance replaced by a fresh whole-task VM */ }
[Fact] public void SessionGateSave_Failure_StaysInTheSessionEditor_NoPromotion() { }
// Escape depth:
[Fact] public void Escape_DismissesAnOpenConfirmation_BeforeAnyNavigation()
{ /* remove confirmation open → EscapeTaskEditor → Confirmation null, editor unchanged */ }
[Fact] public void Escape_DismissesTheGate_KeepingDraftAndEditor() { }
[Fact] public void Escape_ThenLeavesThePush_ThenCloses()
{ /* pushed session editor: 1st Escape (prompt) → 2nd Escape → parent; 3rd → closed */ }
```

- [ ] **Step 2: RED** — `CS1061` for `GateSaveAndContinueCommand` / `EscapeTaskEditor`.
- [ ] **Step 3: Minimal implementation** as specified (the 8-line gate pattern is duplicated in the session editor rather than abstracted — two call sites do not justify a base class).
- [ ] **Step 4: Focused PASS.** **Step 5: Both full suites green.**
- [ ] **Step 6: Checkpoint procedure.** Delta: the three view models + both test files. Boundary: `feat: editor gates, promotion, Escape depth`.

---

### Task 10: Views, hosting bridge, markers, and rendered proofs (UI tests first)

**Files:**
- Create: `tests/BeBoosted.Desktop.Tests/Ui/TaskEditorScopeUiTests.cs` (**Step 1 — before any view exists**)
- Create: `src/BeBoosted.Desktop/Views/WholeTaskEditorView.axaml` + `.axaml.cs`
- Create: `src/BeBoosted.Desktop/Views/SessionEditorView.axaml` + `.axaml.cs`
- Modify: `src/BeBoosted.Desktop/Styles/Icons.axaml` — add `IconRepeat` (`StreamGeometry` from frame 4e: `M2 5a4 4 0 017-1.5M10 7a4 4 0 01-7 1.5M9 1v2.5H6.5M3 11V8.5h2.5`)
- Modify: `src/BeBoosted.Desktop/Views/MainWindow.axaml` + `.axaml.cs` (hosting **bridge** — legacy editor keeps working)
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` (`EditingTaskId`/`EditingBlockId`, bridged `IsTaskEditorOpen`)
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs` + `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml` (Editing chip), `src/BeBoosted.Desktop/ViewModels/CalendarBlockViewModel.cs` + `src/BeBoosted.Desktop/Views/CalendarBlockView.axaml` (editing halo class)
- Modify: both editor VM test files (marker facts)

**Hosting bridge (keeps every checkpoint green — no view type is referenced before it exists, and the legacy editor keeps rendering until Task 11 reroutes and Task 12 deletes):**

```xml
<!-- MainWindow.axaml:342-351 becomes: -->
<Border Name="TaskModalScrim"
        Background="{StaticResource BrushGraphite40}"
        IsVisible="{Binding Calendar.IsTaskEditorOpen}"
        KeyDown="OnTaskModalKeyDown">
  <Panel HorizontalAlignment="Center" VerticalAlignment="Center" Margin="24">
    <views:TaskEditorView DataContext="{Binding Calendar.TaskEditor}"
                          IsVisible="{Binding Calendar.TaskEditor,
                                      Converter={x:Static ObjectConverters.IsNotNull}}" />
    <ContentControl Content="{Binding Calendar.ActiveTaskEditor}">
      <ContentControl.DataTemplates>
        <DataTemplate DataType="vm:WholeTaskEditorViewModel"><views:WholeTaskEditorView /></DataTemplate>
        <DataTemplate DataType="vm:SessionEditorViewModel"><views:SessionEditorView /></DataTemplate>
      </ContentControl.DataTemplates>
    </ContentControl>
  </Panel>
</Border>
```

`CalendarViewModel.IsTaskEditorOpen` becomes `TaskEditor is not null || ActiveTaskEditor is not null` (both setters notify it). `OnTaskModalKeyDown`: `if (shell.Calendar.ActiveTaskEditor is not null) shell.Calendar.EscapeTaskEditor(); else shell.Calendar.CloseTaskEditor();` — same bridge inside `ShellViewModel.EscapePressed` (`ShellViewModel.cs:244-266`). `MainWindow` watches **both** `TaskEditor` and `ActiveTaskEditor` property changes; `_taskEditorReturnFocus` is captured only on the closed→open transition (both slots empty → one occupied) and restored only when both become empty; initial focus by editor type — whole-task → `TaskTitleBox`, session OneOff/New → `SessionDatePicker`, session Repeating → `SessionStartPicker`; `Calendar.EditorRowFocusRequested` focuses the named row's Edit button inside `WholeTaskEditorView` (the `RowFocusRequested` pattern, `DailyTaskListView.axaml.cs:35-43`).

**View structure** (frames 3a/3b/4a–4p; token mapping from Global Constraints): shared card skeleton — `Border` (`BrushPaperWhite`, `BrushBorderEmphasis`, `RadiusDrawer`, `ShadowDrawer`; `Name` `WholeTaskEditorCard` Width=480 / `SessionEditorCard` Width=408; `MaxHeight` = scrim height − 48) → `DockPanel` with fixed header (12×12 lime square with 1.5 graphite border + `TextBlock.mono` scope label + 32×32 `Button.icon` close), fixed footer (destructive-left / Cancel / primary; global `Error` line right-aligned above it — frame 4n right), and a body `ScrollViewer` (`Vertical=Auto`, `Horizontal=Disabled`). Whole-task body: the four task fields (names carried over: `TaskTitleBox`, `TaskProjectSelector`, `TaskDeadlinePicker`, `TaskEstimateBox`), SCHEDULE header (label + summary + `Unschedule all` link), `ScheduleNotice` cream line, bordered rows group (`IconRepeat` + `BrushLimeWash` for repeating rows; position micro-labels; status chips; 32×32 Edit/Remove per row; trailing `Add session` row with the disabled hit-testable note pattern from `DailyRowViewModel.cs:305-323`), dashed empty state, **COMPLETION after the schedule group** (locked decision 2) with `AggregateNote`/`ScheduleNotes`, create-mode inline FIRST SESSION lime-edged group. Session body: read-only title context (trim + `ToolTip.Tip` full title — frame 4p) + `Edit whole task` link; `ResolvedNote`/`NumberingNote`; umber-bordered `StaleNotice` + inert body when `IsStale` (frame 4m left); **`THIS OCCURRENCE · {date}` section label, `Mark this occurrence complete`, its occurrence note, the `REPEATING SCHEDULE` section label, and the series sentence** (frame 4g — all five pinned by rendered tests below); `SessionDatePicker`/`SessionStartPicker`/`SessionEndPicker` with field-pinned validation (frame 4n left); Repeats + Sunday-first 32×32 weekday chips.

**Prompt presentation and focus:** both views render the confirmation card (`BrushFolioUnderlay`, 1.5 graphite border; `IsTaskDeletion` → `BrushAccentUmber` border + umber confirm button) and the 360-wide gate card (title + `"Save or discard before continuing."` + three stacked actions). While `Confirmation` or `Gate` is non-null the body host panel sets `IsEnabled="False"` and `Opacity="0.45"` — disabled controls cannot take keyboard focus, so focus cannot reach the dimmed body. Code-behind: on prompt open, record `FocusManager.GetFocusedElement()` as `_promptReturnFocus` and focus the prompt's first action; Tab/Shift+Tab wrap **within the active prompt** while one is open, otherwise within the card (the F-23-closing trap); on prompt close without editor close, restore `_promptReturnFocus`.

**Editor-local focus treatment (exact, compile-valid — no global remaster):**
every interactive control in the two editors (icon buttons, footer buttons,
checkboxes, weekday chips, links) is wrapped in a named focus container:
`<Border Classes="focusRing"> … </Border>`. `Border` exposes `BorderBrush`,
`BorderThickness`, `CornerRadius`, and `BoxShadow`, so no property is assigned
to a type that lacks it. Each editor view declares, scoped in its own
`<UserControl.Styles>` (`Controls.axaml` is not modified):

```xml
<Style Selector="#WholeTaskEditorCard Border.focusRing">
  <Setter Property="BorderThickness" Value="2" />
  <Setter Property="BorderBrush" Value="Transparent" />
  <Setter Property="CornerRadius" Value="{StaticResource RadiusControl}" />
</Style>
<Style Selector="#WholeTaskEditorCard Border.focusRing:focus-within">
  <Setter Property="BorderBrush" Value="{StaticResource BrushGraphite}" />
  <!-- The literal below equals ColorLimeHalo (#8CC8F24A) — BoxShadows cannot
       compose a resource color; no new color is introduced. -->
  <Setter Property="BoxShadow" Value="0 0 0 5 #8CC8F24A" />
</Style>
```

(`SessionEditorView` declares the identical pair under
`#SessionEditorCard`.) The constant 2px transparent border prevents layout
shift; `:focus-within` fires when the wrapped control holds keyboard focus.

**Markers:** `CalendarViewModel` sets `EditingTaskId`/`EditingBlockId` on `ActiveTaskEditor` transitions; `DailyListViewModel` flips `row.IsEditing` in place (bordered `"Editing"` chip, `BrushLimeWash` — frame 3a) and `CalendarBlockViewModel.IsBeingEdited` adds the `editing` style class (graphite border + lime halo — frame 3b).

- [ ] **Step 1: Write ALL rendered tests RED-first** — `TaskEditorScopeUiTests` (`[AvaloniaFact]`, `MainWindow` + `TestShell.Create`, editors opened through the **internal factories** — entry-point rewiring is Task 11; full bodies):

```csharp
// Frames / states:
[AvaloniaFact] public void WholeTaskCard_Is480Wide_WithFixedHeaderAndFooter()
{ /* Bounds.Width == 480; header+footer visible while the body ScrollViewer Extent >
     Viewport on an 8-session task (frame 4p) */ }
[AvaloniaFact] public void SessionCards_Are408Wide() { /* one-off, repeating, add mode */ }
[AvaloniaFact] public void ScopeLabels_Render_ForEveryMode()
{ /* "WHOLE TASK", "THIS SESSION · 2 OF 3", "THIS SESSION · 1 OF 1",
     "REPEATING SCHEDULE", "NEW SESSION" */ }
[AvaloniaFact] public void RepeatingEditor_RendersBothSectionLabels_AndTheSeriesSentence()
{ /* "THIS OCCURRENCE · SAT, AUG 15", "Mark this occurrence complete", its note,
     "REPEATING SCHEDULE", "Time and weekday changes apply to every occurrence of this
     schedule." (frame 4g) */ }
[AvaloniaFact] public void ScheduleRows_ShowPositions_Chips_AndRepeatingWash() { /* 4f + numbering note */ }
[AvaloniaFact] public void EmptyState_AndCreateModeInlineSession_Render() { /* 4a/4b/4c */ }
[AvaloniaFact] public void CompletedTask_ShowsDoneChips_AndDisabledAddSession_WithNote() { /* 4l */ }
[AvaloniaFact] public void Confirmations_DimTheBody_AndUseScopeCopy() { /* 4i/4j/4k, opacity 0.45 */ }
[AvaloniaFact] public void StaleAndFailureStates_Render() { /* 4m both variants, 4n both */ }
[AvaloniaFact] public void OneOffEditor_HasNoCompletionControl_RepeatingHasExactlyOne() { }
[AvaloniaFact] public void MinimumWindow_1100x720_NoHorizontalExtent_HeaderFooterVisible()
{ /* 1100×720, 8-session task: every ScrollViewer in the card has
     Extent.Width <= Viewport.Width; header/footer bounds inside the window; the
     "Wednesday, Sep 30 / 11:30 PM – 12:45 AM" row wraps (frame 4p) */ }
[AvaloniaFact] public void LongRows_Wrap_WithEditAndRemoveStillVisible()
{ /* wrapped row's Edit and Remove buttons: IsEffectivelyVisible and right edge
     inside the card bounds (frame 4p) */ }
// Prompt focus depth:
[AvaloniaFact] public void PromptOpens_FocusMovesToItsFirstAction() { /* confirmation AND gate */ }
[AvaloniaFact] public void Tab_IsTrappedInsideTheActivePrompt()
{ /* repeated KeyPress(Key.Tab): focused element always within the prompt panel;
     never a body control */ }
[AvaloniaFact] public void DismissingThePrompt_RestoresFocus_ToTheTriggerControl()
{ /* focus the row Remove button, open the confirmation, Escape → that button focused */ }
[AvaloniaFact] public void Escape_DismissesConfirmationThenGateThenNavigates()
{ /* rendered version of the T9 depth ordering via KeyPress(Key.Escape, ...) */ }
// Focus visuals / a11y:
[AvaloniaFact] public void FocusedEditorControls_ShowTheTwoPixelGraphiteOutline_AndLimeHalo()
{ /* focus a 32×32 row button; its wrapping Border.focusRing has
     BorderThickness 2, BorderBrush == BrushGraphite, and a BoxShadow whose
     color equals ColorLimeHalo (#8CC8F24A); unfocused → transparent brush */ }
[AvaloniaFact] public void EveryIconControl_HasAtLeast32x32HitArea()
{ /* enumerate the card's Button.icon descendants: Bounds.Width/Height >= 32 */ }
[AvaloniaFact] public void ScopeReadsWithoutColor()
{ /* the uppercase scope TextBlock exists and is non-empty for every mode — the lime
     square is reinforcement only */ }
[AvaloniaFact] public void InitialFocus_PerEditorType() { /* TaskTitleBox / SessionDatePicker / SessionStartPicker */ }
[AvaloniaFact] public void Tab_WrapsInsideTheCard_WhenNoPromptIsOpen() { }
[AvaloniaFact] public void AutomationNames_MatchTheSpec()
{ /* "Whole task — {title}" / "Session 2 of 3 — {title}" / "Repeating schedule — {title}"
     / "New session — {title}"; row names word-spelled, no "·" */ }
// Markers:
[AvaloniaFact] public void SourceRow_ShowsEditingChip_AndBlockShowsHalo_BehindTheScrim() { /* 3a/3b */ }
[AvaloniaFact] public void LegacyEditor_StillRenders_WhileTheBridgeIsInPlace()
{ /* OpenNewTaskEditorCommand (legacy) → old TaskEditorView visible — deleted in T12 */ }
```

- [ ] **Step 2: RED** — `dotnet test tests\BeBoosted.Desktop.Tests --filter "FullyQualifiedName~TaskEditorScopeUiTests"` → first failures are `CS0246`/XAML for the missing views; after they compile, each remaining fact is watched failing against the detail it pins.
- [ ] **Step 3:** implement views, bridge, markers, and focus behavior until the filter passes.
- [ ] **Step 4: Focused PASS.** **Step 5: Both full suites green** — the bridge keeps every legacy-editor test passing (`LegacyEditor_StillRenders…` proves it).
- [ ] **Step 6: Checkpoint procedure.** Delta: the four view files, `Icons.axaml`, `MainWindow.axaml(+.cs)`, `CalendarViewModel.cs`, `DailyRowViewModel.cs`, `DailyTaskListView.axaml`, `CalendarBlockViewModel.cs`, `CalendarBlockView.axaml`, `TaskEditorScopeUiTests.cs`, both VM test files. Boundary: `feat: scope-led editor views and hosting bridge`.

---

### Task 11: Entry-point rewiring + full test re-targeting (one task; ends green)

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` — `OpenTaskEditorForTask` (`:214-226`) → whole-task editor (no `GetEditableSessionForTask` call); `OpenTaskEditorForBlock` (`:194-207`) → session editor; `OpenNewTaskEditor`/`OpenNewTaskEditorAt`/`OpenNewUnscheduledTaskEditor` (`:175-187`) → create mode; `RequestDeleteBlock` (`:274-278`) → `(CalendarBlockId id, DateOnly occurrenceDate)` opening the session editor pre-armed on the Remove-schedule confirmation; add `internal void OpenTaskEditorForBlockOwner(CalendarBlockId id)` (resolves `GetBlock(id)?.TaskId`, opens the whole-task editor; external/orphan blocks no-op)
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs:426-436`:

```csharp
internal void EditRow(DailyRowViewModel row)
{
    // A list row is task-scoped even when it shows a session (spec, Approach).
    if (row.TaskId is { } taskId)
    {
        _owner.OpenTaskEditorForTask(taskId);
    }
}
```

- Modify: `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs:74` — `Projects.SessionEditRequested += (id, _) => Calendar.OpenTaskEditorForBlockOwner(id);` (event signature untouched)
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarBlockViewModel.cs:211-214` — repeating delete branch passes the rendered occurrence: `_owner.RequestDeleteBlock(Id, Date);`
- Create: `tests/BeBoosted.Desktop.Tests/ViewModels/EditorScopeSelectionTests.cs`
- Modify: `tests/BeBoosted.Desktop.Tests/Ui/TaskEditorScopeUiTests.cs` — the two rendered routing facts below are added here, and the temporary `LegacyEditor_StillRenders_WhileTheBridgeIsInPlace` fact is **deleted here** (once the public entry points reroute, no path reaches the legacy view)
- Delete (in this task, after their replacement coverage from Tasks 6–10 is green): `tests/BeBoosted.Desktop.Tests/ViewModels/TaskEditorViewModelTests.cs`, `tests/BeBoosted.Desktop.Tests/Ui/TaskEditorModalTests.cs` — both drive the rerouted entry points against legacy expectations and cannot pass unchanged after rewiring
- Modify (re-targets, all in this task — this is the complete, verified `TaskEditor|OpenTaskEditor` hit list for `tests/` as of authoring, re-run at execution time with `git grep -nE --untracked "TaskEditor|OpenTaskEditor" -- tests` and account for every hit; sub-bullet paths are relative to `tests/BeBoosted.Desktop.Tests/`, matching their full `$PlanAllowed` entries):
  - `Ui/UnifiedTaskUiTests.cs`, `Ui/DailyListUiTests.cs`, `ViewModels/DailyListViewModelTests.cs`, `ViewModels/CalendarViewModelCalendarTests.cs` — entry-point and editor-slot assertions move to the `ActiveTaskEditor` equivalents
  - `Ui/ProjectEntryPointTests.cs` — `ScheduledSessionRow_EditButton_OpensTheEditor_ScopedToTheOccurrence` now asserts the **whole-task** editor opens
  - `ViewModels/CalendarBlockCapabilityTests.cs` — `DeleteDispatch_RoutesRepeatingTasksThroughConfirmation` now asserts the pre-armed Remove-schedule confirmation
  - `Ui/OccurrenceCompletionUiTests.cs` — `EditorOpenedFromABlockClick_ScopesCompletionToTheOccurrence` asserts the repeating session editor's staged occurrence checkbox
  - `Ui/CalendarBlockInteractionTests.cs` — block-click facts read `Calendar.TaskEditor` (`:179` and 8 null-assertions) → session-editor `ActiveTaskEditor` equivalents
  - `Ui/ScreenshotCaptureTests.cs` — the one `TaskEditor` read (`:90`) → the create-mode `ActiveTaskEditor`
  - `ViewModels/ShellProjectRefreshTests.cs` — opens/reads/saves through `OpenNewTaskEditorCommand`/`OpenTaskEditorForTask` + `TaskEditor` (`:43-57`, `:310-328`) → whole-task editor equivalents
  - `ViewModels/PlanningDeletionTests.cs` — editor-driven deletes (`TaskEditor!.RequestDeleteCommand/ConfirmDeleteCommand`, `:83-85`, `:121-123`, `:220-222`) → whole-task `RequestDelete`/`ConfirmPrompt`
  - `ViewModels/ProjectsViewModelTests.cs` — `TaskEditor` project-assignment read (`:116`) → whole-task editor
  - `ViewModels/PlanDraftViewModelTests.cs` — `OpenNewTaskEditorCommand` + `TaskEditor` (`:143-150`) → create-mode editor
  - `ViewModels/PlanDraftLifecycleTests.cs` — `OpenTaskEditorForTask` + `TaskEditor!.RepeatsWeekly = true` + Save (`:134-136`) → the repeating schedule is now made in the session editor: retarget to `OpenSessionEditorForBlock` + `Schedule.RepeatsWeekly = true` + Save (or seed the recurrence directly through `UpdateSessionSchedule`), preserving the fact's draft-lifecycle intent
  - `Ui/FeatureScreenshotCaptureTests.cs` — **minimal re-target only** (its full rewrite is Task 13): the four `CloseTaskEditor()` calls (`:57`, `:89`, `:99`, `:119`) become `CloseActiveEditor()` so Task 12's deletion of the legacy member cannot break a green checkpoint
  - `ViewModels/InboxViewModelTests.cs` — **unchanged**: its only hit (`:112`) is the test *name* `Edit_RequestsTheOneCanonicalTaskEditor`; the body asserts the `EditRequested` event, which remains valid

(`TaskSessionSelectionTests.cs` is NOT touched here — it pins the service-level
`GetEditableSessionForTask`/`UpdateTask` behavior that still exists until
Task 12, where it is deleted with that surface. The legacy production types
and the hosting bridge stay compiled through this task.)

- [ ] **Step 1: Failing tests** — `EditorScopeSelectionTests` (full bodies, driven through `TestShell.Create()`):

```csharp
[Fact] public void EveryListRow_OpensTheWholeTaskEditor()
{ /* Inbox row Edit, Daily session row, Daily bare-task row, Projects task row,
     Projects scheduled-session row → WholeTaskEditorViewModel with the right TaskId
     (spec test 16) */ }
[Fact] public void CalendarBlocks_AndScheduleRows_OpenTheSessionEditor()
{ /* CalendarBlockViewModel.Edit → clicked occurrence; whole-task row Edit → pushed
     session editor (spec test 16) */ }
[Fact] public void RepeatingDeleteKey_OpensRemoveScheduleConfirmation_WithTheRenderedOccurrence()
{ /* deliberate behavior change from whole-task delete — spec'd (spec test 16) */ }
[Fact] public void TaskRowEntry_NeedsNoSessionPick_EvenWithResolvedHistory() { /* F-03 root cause gone */ }
[Fact] public void F15_OccurrenceResolution_StillHoldsForScheduleRowEntry() { /* today / elapsed / anchor */ }
[Fact] public void NewTaskPaths_OpenCreateMode() { /* plain, slot-prefilled, unscheduled */ }
// Added to TaskEditorScopeUiTests.cs in this task (rendered, real entry points):
[AvaloniaFact] public void CloseRestoresFocus_ToTheInvokingRowOrBlock()
{ /* Daily row pencil and Week block invokers regain focus after close */ }
[AvaloniaFact] public void DailySessionRowEdit_Rendered_OpensTheWholeTaskEditor() { }
```

- [ ] **Step 2: RED** — the first fact fails with `Assert.IsType<WholeTaskEditorViewModel>` against the legacy `TaskEditorViewModel` — the correct observable failure for rewiring.
- [ ] **Step 3:** rewire per the Files section; delete `TaskEditorViewModelTests.cs`, `TaskEditorModalTests.cs`, and the bridge fact; then walk the `git grep -nE --untracked "TaskEditor|OpenTaskEditor" -- tests` hit list and re-target every remaining affected test in the same step, accounting for each hit against the classification above.
- [ ] **Step 4: Focused PASS.**
- [ ] **Step 5: BOTH full suites green — mandatory.** Any residual failure is a defect of this task, not deferred work.
- [ ] **Step 6: Checkpoint procedure.** Delta: the four production files + the new test file + the fifteen re-targeted test files + the two deleted test files + `TaskEditorScopeUiTests.cs`. Boundary: `feat: scope-led entry-point routing`.

---

### Task 12: Legacy retirement, service re-targets, guards, bridge collapse

**Files:**
- Create: `tests/BeBoosted.Tests/Calendar/EditorScopeGuardTests.cs` (**first — its RED is the task's evidence**)
- Delete: `src/BeBoosted.Desktop/ViewModels/TaskEditorViewModel.cs`, `src/BeBoosted.Desktop/Views/TaskEditorView.axaml`, `src/BeBoosted.Desktop/Views/TaskEditorView.axaml.cs`
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` — delete `TaskEditor`, `SaveTaskEditor` (`:281-342`), `DeleteTaskFromEditor` (`:345-365`), `CloseTaskEditor`; `IsTaskEditorOpen` collapses to `ActiveTaskEditor is not null`
- Modify: `src/BeBoosted.Desktop/Views/MainWindow.axaml` + `.axaml.cs` — remove the legacy half of the bridge (the `Panel` keeps only the `ContentControl`; `OnTaskModalKeyDown` and the property watch drop the legacy branches)
- Modify: `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs` — `EscapePressed` drops the legacy branch (only `Calendar.EscapeTaskEditor()`)
- Modify: `src/BeBoosted.Application/Calendar/CalendarService.cs` — delete `UpdateTask` (`:67-179`), `ApplyTaskCompletion` (`:211-290`), `ResolveSession` (`:298-309`), `GetEditableSessionForTask` (`:451-456`)
- Delete: `tests/BeBoosted.Tests/Calendar/TaskSessionSelectionTests.cs` (the legacy Desktop editor test files and the bridge fact were already removed in Task 11)
- Modify (service-test re-targets, each named): `tests/BeBoosted.Tests/Calendar/CalendarServiceTests.cs` — `UpdateTask_PersistsEveryField` → split across `UpdateTaskDetails`/`UpdateSessionSchedule`; `UpdateTask_TurningSchedulingOff_RemovesTheSession` → delete (covered by `UnscheduleSession` + T3); `UpdateTask_AddingASchedule_CreatesTheSession` → `AddSession`; `UpdateTask_RemovingAWeekday_PurgesItsObsoleteCompletion` → delete (re-pinned by T2). `tests/BeBoosted.Tests/Calendar/TaskCompletionAuthorityTests.cs` — every `UpdateTask(...)`-driven fact re-targets the focused method that owns the behavior; `UpdateTask_ConvertingTheFinalRepeatingSeries_WithAOneOffSibling_CompletesTheAggregate` retires (a conversion never completes anything — spec's deliberately-rewritten list). `tests/BeBoosted.Tests/Calendar/SessionRecurrenceReconciliationTests.cs` — re-target to `UpdateSessionSchedule`; `RepeatingToOneOff_ARequestedCompletionCompletesTheTask_Freshly` retires (same list). `tests/BeBoosted.Tests/Calendar/CalendarMutationAtomicityTests.cs` — the `UpdateTask_*` rollback facts re-target or delete where T1/T2 already cover them.

**Guard (write first):**

```csharp
/// <summary>No silent task-to-session selection path may return (F-03 root cause).</summary>
public sealed class EditorScopeGuardTests
{
    [Fact]
    public void CalendarService_ExposesNoCombinedSave_AndNoEditableSessionSelection()
    {
        var names = typeof(CalendarService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet();
        Assert.DoesNotContain("UpdateTask", names);
        Assert.DoesNotContain("GetEditableSessionForTask", names);
        Assert.Contains("UpdateTaskDetails", names);
        Assert.Contains("UpdateSessionSchedule", names);
        Assert.Contains("AddSession", names);
        Assert.Contains("UnscheduleAllSessions", names);
    }
}
```

- [ ] **Step 1:** add the guard; run `dotnet test tests\BeBoosted.Tests --filter "FullyQualifiedName~EditorScopeGuardTests"` — RED: `Assert.DoesNotContain` fails on `"UpdateTask"`.
- [ ] **Step 2:** re-target the service tests, then delete the legacy production members and files in the order listed (production deletions last). The residue check must end empty — boundary-safe so `WholeTaskEditorViewModel`/`WholeTaskEditorView` never match (verified against this repository):
  `git grep -nP --untracked '\bUpdateTask\(|\bGetEditableSessionForTask\b|\bTaskEditorViewModel\b|\bTaskEditorView\b' -- src tests`
- [ ] **Step 3:** guard GREEN; `dotnet build BeBoosted.slnx -warnaserror --no-restore` clean.
- [ ] **Step 4: BOTH full suites green** — zero unexplained skips; the Global-Constraints architecture guards all pass.
- [ ] **Step 5: Checkpoint procedure.** Delta: the deletion set + service re-target set + bridge-collapse files, nothing else. Boundary: `refactor: retire the combined task editor`.

---

### Task 13: Screenshot captures for the new editors

**Files:**
- Modify: `tests/BeBoosted.Desktop.Tests/Ui/FeatureScreenshotCaptureTests.cs`

**Interfaces:** same gate (`BEBOOSTED_SCREENSHOT_DIR`), same `Capture` helper (`FeatureScreenshotCaptureTests.cs:159-164`), same size loop `(1440, 960), (1280, 800)` plus the 1100×720 fact.

- [ ] **Step 1:** Replace the four `task-editor-*` captures (`:56`, `:88`, `:98`, `:118`) with, driven through real entry points over `TestShell.SeedDesignCalendar` + a three-one-off task, a repeating task, and a mixed task: `whole-task-editor-new-…`, `whole-task-editor-new-session-…` (4a/4b), `whole-task-editor-sessions-…` (3a), `whole-task-editor-mixed-…` (4f), `whole-task-editor-delete-confirm-…` (4k), `session-editor-oneoff-…` (3b), `session-editor-repeating-…` (4g), `session-editor-new-…` (4h); in `CaptureMinimumWindowScreens`: `whole-task-editor-1100x720.png` (8 sessions — 4p) and `session-editor-1100x720.png`.
- [ ] **Step 2 (RED):** run with a disposable dir — the new names are absent until the calls exist; Step 4's inventory is the meaningful check.
- [ ] **Step 3:** implement the capture flow.
- [ ] **Step 4: Verify** (PowerShell):

```powershell
$env:BEBOOSTED_SCREENSHOT_DIR = Join-Path $env:TEMP ("BeBoosted-F03-Shots-" + [guid]::NewGuid())
$env:BEBOOSTED_DATA_DIR = Join-Path $env:TEMP ("BeBoosted-F03-ShotsData-" + [guid]::NewGuid())
try {
    dotnet test tests\BeBoosted.Desktop.Tests --filter "FullyQualifiedName~ScreenshotCapture"
    Get-ChildItem $env:BEBOOSTED_SCREENSHOT_DIR   # every name above at every size
} finally {
    Remove-Item Env:BEBOOSTED_SCREENSHOT_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:BEBOOSTED_DATA_DIR -ErrorAction SilentlyContinue
}
```

Review the PNGs by eye against frames 3a/3b/4a–4p before proceeding.
- [ ] **Step 5: Checkpoint procedure.** Delta: the one test file. Boundary: `test: scope-editor screenshots`.

---

### Task 14: Final verification chain (disposable profile only)

**Files:** none (verification only). Live profiles are prohibited throughout; every launch uses a fresh `BEBOOSTED_DATA_DIR`.

- [ ] **Step 1: Formatting** — `dotnet format BeBoosted.slnx --verify-no-changes --no-restore` → exit 0.
- [ ] **Step 2: Build** — `dotnet build BeBoosted.slnx -warnaserror --no-restore` → exit 0, zero warnings.
- [ ] **Step 3: Application tests** — `dotnet test tests\BeBoosted.Tests --no-build` → all pass.
- [ ] **Step 4: Desktop tests** — `dotnet test tests\BeBoosted.Desktop.Tests --no-build` → all pass (screenshot facts skip without the env var).
- [ ] **Step 5: Opt-in screenshots** — Task 13's Step 4 block verbatim.
- [ ] **Step 6: Publish** —

```powershell
$publish = Join-Path $env:TEMP ("BeBoosted-F03-Publish-" + [guid]::NewGuid())
dotnet publish src\BeBoosted.Desktop -c Release -r win-x64 --self-contained true -o $publish
Test-Path (Join-Path $publish "BeBoosted.exe")   # True
```

- [ ] **Step 7: Packaged startup on a disposable profile (hardened)** —

```powershell
$env:BEBOOSTED_DATA_DIR = Join-Path $env:TEMP ("BeBoosted-F03-Data-" + [guid]::NewGuid())
$exe = Join-Path $publish "BeBoosted.exe"
$proc = $null
try {
    $proc = Start-Process $exe -WindowStyle Hidden -PassThru
    Start-Sleep 10
    $running = Get-Process -Id $proc.Id -ErrorAction Stop
    if ($running.Path -ne $exe) { throw "wrong executable: $($running.Path)" }
    if (-not (Test-Path (Join-Path $env:BEBOOSTED_DATA_DIR "beboosted.db"))) { throw "no db created" }
    "startup verified: $($running.Path)"
} finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Remove-Item Env:BEBOOSTED_DATA_DIR -ErrorAction SilentlyContinue
}
Start-Sleep 2
$stillAlive = $false
try { Get-Process -Id $proc.Id -ErrorAction Stop | Out-Null; $stillAlive = $true } catch { }
if ($stillAlive) { throw "packaged process $($proc.Id) is STILL ALIVE after the stop attempt — verification FAILED" }
"confirmed dead"
```

- [ ] **Step 8: Post-publish recovery** — `dotnet restore BeBoosted.slnx` then `dotnet build BeBoosted.slnx -warnaserror --no-restore` (the documented Release/RID asset caveat) → both exit 0.
- [ ] **Step 9: Diff hygiene** — `git diff --check` → clean (the repository's usual LF→CRLF notices only).
- [ ] **Step 10: Attribution audit (baseline-driven — NOT status codes).** Run the full **Checkpoint procedure** one final time against `$base`: (1) every new path since the baseline is in `$PlanAllowed`; (2) every baseline file outside `$PlanAllowed` is SHA-256-identical to `manifest.csv` — hash equality, not an unchanged status code, is the proof of byte identity; (3) for every plan-owned baseline-dirty file, produce the `git diff --no-index` delta against its external copy in `$base\copies` and present those deltas plus the created/deleted file list to the user as this feature's exact footprint. No commits.

---

## Notes for the implementer

- The two locked authority decisions stand: add-mode primary = **"Add session"**
  (spec copy authority over frame 4h), and COMPLETION renders **after**
  SCHEDULE (frame 3a layout authority).
- `TrySaveWholeTask`/`TrySaveSession` persist-and-announce only; the thin
  `SaveWholeTask`/`SaveSession` wrappers own post-save navigation. Gates call
  the `TrySave…` primitive so the editor stays active for the pending
  operation, and a failed gated save discards the pending action with no
  navigation and no announcement.
- Escape order everywhere: active confirmation/gate first, then leave a
  pushed session editor, then close. Prompts take focus on open, trap Tab,
  disable the dimmed body, and return focus to their trigger on dismissal.
- `EditorOccurrenceFor` (`CalendarViewModel.cs:234-252`) is the only survivor
  of the old selection machinery; it serves repeating Schedule-row entry and
  the F-15 rule. `GetEditableSessionForTask` dies in Task 12.
- `ScheduleFlyoutViewModel` (Daily "Change time") is untouched — a quick
  affordance, not an editor (spec Non-goals).
- The frame token mapping is fixed in Global Constraints; never introduce
  `#8A3B2E` or any other literal hex. The 2px-graphite + lime-halo focus
  treatment lives in editor-local styles only; `Controls.axaml` is off-limits.
- Every task's Desktop tests run against in-memory doubles; every
  `BeBoosted.Tests` fixture uses `TempDatabase`. Nothing in this plan reads,
  writes, launches against, or migrates the live Local/Roaming profiles.
- Search commands in this plan use `git grep` (`--untracked` so untracked
  test files are covered; `-P` word boundaries for the legacy-residue check):
  `rg` is not installed on this machine — verified 2026-08-21 — while
  `git grep` ships with git and both forms were executed against this
  repository during planning. Suggested commit boundaries are annotations
  only — no git write operations without the user-approved baseline strategy
  (Execution prerequisites §1).
