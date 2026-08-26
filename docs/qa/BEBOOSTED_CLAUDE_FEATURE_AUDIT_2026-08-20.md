# BeBoosted Claude Feature Audit — 2026-08-20 (second pass)

## 1. Purpose and boundary

This document is the current project memory for the uncommitted feature work sitting on
branch `feature/daily-priority-list` above commit `2baa100`. It supersedes the first
2026-08-20 pass of this audit: every claim in that pass was re-verified from fresh
evidence, several were expanded, and new defects were found. Nothing in this document
rests on the previous pass alone; where prior evidence is retained it is labeled as such.

## 2. Data-safety boundary

The real BeBoosted profiles at `C:\Users\daria\AppData\Local\BeBoosted` and
`C:\Users\daria\AppData\Roaming\beboosted` were **not opened, read, queried, copied,
migrated, moved, or launched against** during this audit. Unlike the first pass, this
pass also did **not** read the production log. Every runtime check used a freshly
generated disposable directory under the system temp folder with `BEBOOSTED_DATA_DIR`
set explicitly before launch; the one packaged startup verified that the running
process's executable path was the disposable audit build, tracked its PID, and stopped
exactly that process. Static reading confirmed the env var is the only data-dir
override and is read once at startup (`src/BeBoosted.Infrastructure/Storage/DefaultAppDataPaths.cs:27-31`),
so an isolated run cannot fall back to the live profile.

Carried forward from the first pass (not re-verified, historical): the production log
recorded that a prior production run moved four stored resources into named folders on
2026-08-19 with no accompanying error. That proves the startup reconciler has already
run once against the live profile; it does not prove the moved rows or files are correct.

No production source, test, migration, settings, or UI file was changed by this audit.
No git write commands were run. The only intended repository change is this document.

## 3. Executive verdict

**Audit-time verdict:** suitable for continued isolated development and careful
disposable-profile dogfooding; not ready for a build pointed at valuable data until
the two High findings (F-01 re-rank partial save, F-02 resource reconciliation
recovery) are fixed with regression tests, together with the resource-open crash
(F-11) and the wrong-occurrence completion scoping (F-15).

**Post-stabilization update (§3a):** all four of those findings — and ten more —
were fixed the same day with failing-test-first regressions; the valuable-data gate
now rests on the design-decision items (F-03 editor scope foremost) rather than on
silent-damage defects.

The verdict of the first pass stands, with more precision: the service and persistence
layers of this feature set are in strong shape — planning approval/undo/replacement,
the unified task migration, completion authority, and calendar mutations are
transaction-atomic with genuine failure-injection tests. The defects cluster at the
edges the tests never render or never interrupt: a view file that was never edited
(F-01), a filesystem/database seam whose promised recovery does not exist (F-02), a
task editor that hides multi-session scope (F-03), and refresh/classification gaps in
the new Daily list (F-09, F-10).

## 3a. Stabilization pass (2026-08-20, same day, after the audit)

A TDD stabilization pass fixed the audit's mechanical findings in the user-approved
order — every fix began with a failing regression test, and every batch ended with the
full format/build/test/screenshot/publish/disposable-startup chain green. The live
profile was never touched.

**Fixed with regression tests (29 new tests: application 321→329, desktop 324→345):**

| Batch | Findings | Fix essence |
| --- | --- | --- |
| 1 | F-01 | `ShowBuildPlanNow` bound in `PrioritySortView.axaml`; `BuildPlanNow()` refuses in re-rank mode; rendered-UI tests both modes. |
| 2 | F-02, F-12, F-11 | Reconciler adopts already-moved bytes at the desired (or unclaimed numbered) destination when the source is gone; per-resource guard so a failing record never aborts the pass; Open/Reveal guard missing files with a row notice instead of throwing through `Process.Start`. |
| 3 | F-15 | Task-row editor entry resolves today's / most-recent-elapsed occurrence for repeating tasks, never the anchor. |
| 4 | F-09, F-10 | Resolved-not-done sessions ("Needs more time"/"Didn't happen") move to the day's history with a status chip — a task never sits in Scheduled and Unscheduled at once; Inbox capture and chat captures announce through the shared refresh chain exactly once. |
| 5 | F-06 | A zero-block plan writes nothing: no hidden active draft, and the previous visible draft survives. |
| 6 | F-13, F-14, F-07, F-16, F-18, F-22 | Decisions+ranks persist in one transaction (`SaveSessionResult`, rollback-proven); sort gate re-asks on period change and calendar mutations, stale execute is a quiet no-op; `ResolvePath` enforces containment (Exists/Delete/Move treat escapes as absent; BB-QA-011 closed); import batches survive one failing file and name it; the unranked dash carries a hit-testable "Not ranked for this day." overlay; a zero-comparison re-rank explains itself on the results screen. |

**Deliberately not fixed (need an approved interaction design first):** F-03 task-vs-
session editor scope, F-04 folder identity/collision naming, F-05 project menu
(rename/delete entry points), and the P0/P1 remaster items in §9. Historical BB-QA
003/004/005/006/008/009/010/012/013/015 also remain open (BB-QA-011 is now closed).
Severity framing in §7 reflects the audit-time state; this table is the current truth.

## 4. Repository and baseline state

| Item | Value |
| --- | --- |
| Branch / HEAD | `feature/daily-priority-list` @ `2baa1008b1f013168a29f3abfee4e376973acf22` |
| Working tree | 87 tracked files modified or deleted (+4,893 / −3,763), 45 untracked additions; verified with read-only `git status`/`git diff` only |
| Tracked deletions | `CommitmentCompletion.cs`, `ICommitmentCompletionRepository.cs`, `SqliteCommitmentCompletionRepository.cs`, `CommitmentEditorViewModel.cs`, `TaskEditFlyoutView.axaml(.cs)`, and their test files — the commitment model is fully retired |
| Untracked feature code | migrations `0010_unify_tasks.sql` + `0011_proposed_block_integrity.sql`, `OccurrenceCompletion` domain/repo, `TaskEditorViewModel/View`, `ResourceLayout`, `ResourceLayoutReconciler`, 24 new test files |
| Repo instructions | No CLAUDE.md / AGENTS.md / .claude in the repository |

### Feature grouping of the change set

1. **Unified Task model & completion authority** — Domain `CalendarBlock`, `OccurrenceCompletion` (new, replaces `CommitmentCompletion`); Application `CalendarService`, `TaskService`, `IOccurrenceCompletionRepository`; Infrastructure repositories + migrations 0010/0011.
2. **Unified task editor** — `TaskEditorViewModel/View` (new modal), deletions of the commitment editor and inbox edit flyout, `MainWindow` modal hosting.
3. **Daily priority list** — `DailyListViewModel`, `DailyRowViewModel`, `DailyTaskListView`, `CalendarViewModel` plumbing, session done-toggle work.
4. **Calendar views** — `TimelineSurfaceView` full-day range, `CalendarBlockView` interactions, `CalendarView` conditional surface.
5. **Planning lifecycle** — `PlanningService`, `PlanningProposal` (`SettleEmptiedDraft`), `SqlitePlanningProposalRepository`, `SqliteCalendarMutations`, migration 0011.
6. **Incremental Priority Sort / re-rank** — `ComparisonSession` seeding, `PrioritySortService` factories, `PrioritySortViewModel`, shell/inbox/daily wiring. `PrioritySortView.axaml` itself is untouched (root cause of F-01).
7. **Projects & named resource folders** — `ResourceLayout`, `ResourceLayoutReconciler`, `LocalResourceStorage`, `ProjectService`, `Resource.RelocateTo`, `App.axaml.cs` startup hook.
8. **Shell/styles/infrastructure** — `ShellViewModel` rank-period wiring, style token rename (`Fixed*` → `Synced*`, same hex), new `IconMore` glyph, DI registration.

## 5. Verification evidence (fresh, exact)

| Check | Command essence | Result |
| --- | --- | --- |
| Restore | `dotnet restore BeBoosted.slnx` | exit 0, 1.9 s |
| Formatting | `dotnet format BeBoosted.slnx --verify-no-changes --no-restore` | exit 0, 16.0 s |
| Warning-free build | `dotnet build BeBoosted.slnx -warnaserror --no-restore` | exit 0, 12.6 s, 0 warnings / 0 errors |
| Application tests | `dotnet test tests\BeBoosted.Tests --no-build` | **321 passed, 0 failed, 0 skipped** |
| Desktop tests | `dotnet test tests\BeBoosted.Desktop.Tests --no-build` | **324 passed, 0 failed, 3 skipped** (opt-in screenshot gates) |
| Opt-in screenshots | filter `FullyQualifiedName~ScreenshotCapture` with disposable `BEBOOSTED_SCREENSHOT_DIR` + `BEBOOSTED_DATA_DIR` | **3/3 passed**, 48 PNGs at 1440×960, 1280×800, and 1100×720 |
| Windows publish | `dotnet publish src\BeBoosted.Desktop -c Release -r win-x64 --self-contained true -o <temp>` | exit 0, 7.9 s, **245 files, 218,529,207 bytes**, `BeBoosted.exe` present |
| Packaged startup | Start published exe with disposable `BEBOOSTED_DATA_DIR` | PID path-verified to the audit publish dir; alive after 10 s; created `beboosted.db` (+wal/shm), `logs\`, `resources\`; stopped; confirmed dead |
| Post-publish recovery | `dotnet restore` then Debug `dotnet build -warnaserror --no-restore` | both exit 0, 0 warnings — the documented Release/RID asset caveat reproduces and the documented recovery works |
| Diff hygiene | `git diff --check` | no whitespace errors (repository's usual LF→CRLF notices only) |

Total scenario count this pass: **648** automated test scenarios ran green (321 + 324
+ 3 opt-in screenshot scenarios).

## 6. End-to-end feature status matrix

Statuses: Verified working (static+tests) · Working with limitations · Partially
implemented · Broken · Service-only · Unsafe · Not verified.

| Feature | Status | Notes (evidence in findings) |
| --- | --- | --- |
| Migration 0010 (unify tasks) | Verified working | Converts local commitments to Task+session, transfers title/project/completions, idempotent via `schema_migrations` + per-migration transaction (`0010_unify_tasks.sql:9-57`, `MigrationRunner.cs:48-72`). Live DB intentionally not inspected. Edge concerns on hand-corrupted rows only (§13). |
| Migration 0011 (proposed-block integrity) | Working with limitations | FK backstop + orphan cleanup + one-time empty-draft settlement correct; a proposal-orphan row (unreachable via app history) would abort startup (§13). |
| Completion authority — one-off | Verified working | Task owns done; complete/reopen/RecordOutcome(Done) share one transactional aggregate transition (`CalendarService.cs:363-442, 598-656`). |
| Completion authority — repeating | Verified working | Occurrence rows own done; global completion rejected; reopen-always-allowed recovery path preserved (`CalendarService.cs:373-378`; `CalendarBlock.cs:198-210`). |
| Session done toggle + undo (Today) | Verified working | Implements the 2026-08-16 spec exactly, incl. the `!TaskRepeats || IsDone` gate and a better-than-spec hit-testable note overlay. Aggregate sibling-clearing undo is spec-acknowledged but unstated in UI (F-17 group). |
| Unified task editor — create/edit/cancel/delete | Verified working core / Working with limitations overall | Atomic create+session; cancel/Escape never mutate; delete is two-step and transactional with stale-race handling. Multi-session opacity is F-03; anchor-occurrence completion is F-15. |
| Recurrence editing | Verified working | Whole-series edit purges obsolete occurrence rows in-transaction (`CalendarService.cs:105-110,156,325-335`; 7 reconciliation tests). Constraints (not-before etc.) still not editable (BB-QA-009 partial). |
| Multi-session (split) tasks | Working with limitations | Deterministic, tested service rules; zero UI disclosure (F-03). |
| Daily list classification/ordering/progress | Working with limitations | Ordering, copy, progress, rank-follows-visible-date all verified; resolved-not-done sessions break the two-sections invariant (F-09); capture paths go stale (F-10). |
| Schedule flyout / change time / add-task | Verified working | Change time is one atomic `RescheduleSession` (better than plan); add-task failure persists nothing and keeps the modal open. |
| Proposal rows in Daily | Verified working | PROPOSED chip, approve/remove/Why, all pending-proposal TaskIds subtracted incl. other-day. |
| Full-day timeline (Week) | Verified working | `VisibleStartHour = 0`, `VisibleEndHour = 24` (`TimelineSurfaceView.axaml.cs:22-23`); one geometry drives render/scroll/drag/resize/hit-test with 23:59 clamps; regression tests present. Closes BB-QA-001. |
| Calendar block manipulation | Verified working | Pointer drag/snap/cross-day/resize/keyboard/capture-loss covered by 16 real-input facts. |
| Plan draft create/replace | Working with limitations | Replacement transactional and failure-tested; zero-block result persists as an invisible active draft (F-06). |
| Approve / partial approve / undo / discard | Verified working | Validate-then-mutate inside one transaction; rollback and guard tests are genuinely adversarial. Strongest-verified subsystem. |
| Deletion cleanup (tasks in drafts) | Verified working | One transaction prunes sessions, proposal blocks, occurrence rows, task; FK `ON DELETE CASCADE` backstop. |
| Incremental Priority Sort | Verified working | Seeded sessions; scheduled-but-open tasks keep ranks (repairs the old wipe defect), SQLite-restart tested. |
| Re-rank single task | Working with limitations | Correct per design incl. instant completion for the only ranked task; UX under-communicates (F-22); entry chips/markers are real buttons. |
| Finish/Complete persistence | Unsafe | F-01 (view renders forbidden exit) + F-13 (two-transaction save). |
| CanStart gating | Working with limitations | Logic matches contract; goes stale on period change with one crash-shaped edge (F-14). |
| Resource import w/ named folders | Verified working core | Picker→service→storage with original names and collision numbering; unguarded mid-batch IO failure is F-16. |
| Startup reconciliation | Working with limitations | Runs post-migrations, log-and-continue per design; per-resource record failure both unrecoverable (F-02) and pass-aborting (F-12). |
| Reconcile failure recovery | **Broken** | The design's promised repair does not exist (F-02). |
| Project rename sync | Service-only | Tested `RenameProject`, zero Desktop callers (F-05). Project/File delete equally unreachable. |
| Folder collision handling | Working with limitations | File-level numbering only; identically-sanitizing projects/Files silently share one directory (F-04). |
| Stored-path containment | Unsafe (defense absent, low exploitability) | Bare `Path.Combine` feeding Exists/Delete/Move/`Process.Start` (F-07 / BB-QA-011). |
| Project detail | Working with limitations | Sections/refresh verified; one task legitimately renders in up to three lists (UX backlog P1); rename/create-task/delete actions absent. |
| Project scheduled blocks | Working with limitations | Correct, tested; 29-day per-occurrence query fan-out with an unused batch API (F-08). |
| Inbox capture/edit | Verified working | Capture trims/rejects blanks; rows open the canonical editor; drawer stale-refresh is F-10. |
| Batch selection | Broken (absent) | No selection model anywhere (BB-QA-008). |
| Chat / AI extraction | Working with limitations | Review-first, provenance, auto-plan permission split all verified; context-sentence repro still creates a spurious task (BB-QA-003 partial). |
| Settings | Verified working | Two AI permission pairs persist independently to SQLite, live-read, no restart needed. |
| Startup error boundary | Partially implemented | Only migrations show the error window; paths/logging/DI/window remain unprotected (BB-QA-004 open). |
| Window geometry clamp | Verified working in code — native pending | All-edge physical-pixel clamp, per-screen scaling, live re-clamp; 30+ tests incl. the exact audited 2880×1704@200% case (BB-QA-002). |
| Task editor modal focus | Working with limitations | Initial focus, Escape, and invoker restoration tested; no keyboard Tab trap (F-23). |
| Inbox focus restoration | Broken | Close path restores nothing (BB-QA-006 open). |
| Windows publish + disposable startup | Verified working | See §5. |

## 7. Findings (ordered by severity)

Finding IDs are stable: F-01…F-08 keep the meaning of the first pass; F-09+ are new
in this pass. Every finding here is a **deterministic static defect** unless labeled
otherwise. None is an observed runtime data-loss event.

### F-01 — High — Re-rank renders "Build my plan now" and a click persists a demoted ranking

- **Subsystem:** Priority Sort re-rank. **Blocks:** valuable-data use, dogfooding with real ranks, Windows release.
- **User impact:** During a re-rank, the one visible "exit" control silently buries the task being re-ranked at the bottom of the saved order and rewrites the period's ranks and tiers wholesale.
- **Expected:** Design (2026-08-17 spec): re-rank has *no* partial-save path; "Build my plan now" hidden; abandoning changes nothing.
- **Actual:** The button binds `BuildPlanNowCommand` with no `IsVisible`/CanExecute gate (`src/BeBoosted.Desktop/Views/PrioritySortView.axaml:140-144`); `BuildPlanNow()` → unguarded `Finish()` (`PrioritySortViewModel.cs:150-151, 190-201`) → `Complete()` → `BuildRanking()` puts the still-inserting target in the trailing group (`ComparisonSession.cs:124-138`) → `ReplaceRanks` persists it last with tiers recomputed (`PrioritySortService.cs:56-67`).
- **Root cause:** The feature was implemented VM-only. `ShowBuildPlanNow` exists (`PrioritySortViewModel.cs:98`) but `PrioritySortView.axaml` was never edited — it is byte-identical to the phase-4 commit that predates re-rank.
- **Proof method:** Static trace above; grep shows `ShowBuildPlanNow` referenced nowhere outside the VM and its VM-level test. The keyboard shortcut set never maps to the command, so the trigger is pointer click or Tab+Enter.
- **Coverage:** `Rerank_…HidesTheEarlyExit` asserts only the VM property. **Missing:** a rendered-UI test (the suite proves this style works in `DailyListUiTests`) asserting the button is not effectively visible in re-rank mode.
- **Remediation:** Bind `IsVisible="{Binding ShowBuildPlanNow}"`, add a defense-in-depth guard in `Finish()`/`BuildPlanNow()` for re-rank-incomplete sessions, add the rendered regression test.

### F-02 — High — Resource reconciliation cannot recover after a database-write failure, and the failure aborts the pass

- **Subsystem:** Named resource folders / startup reconciler. **Blocks:** valuable-data use, dogfooding with real resources, Windows release.
- **User impact:** If a file move succeeds and the row update fails, the database points at a path with no file behind it, forever. The document shows "Index failed", **Open likely crashes the app** (F-11), Reveal opens a wrong folder, and Delete silently leaks the moved bytes. The design's claim that "the next reconcile repairs it" is false.
- **Expected:** Design (2026-08-19 spec, "Why not a transaction"): the bytes-moved/row-stale state is the tolerable failure *because* the next run repairs it.
- **Actual:** The reconciler keys everything on the recorded `StoredPath`: `IsAlreadyPlaced(stale, folder, desired)` is false (`ResourceLayout.cs:85-90`), then `MoveInto` checks only the stale source — `File.Exists(source)` fails → `null` → `continue` (`LocalResourceStorage.cs:31-35`; `ResourceLayoutReconciler.cs:51-53`). No code path ever probes the desired destination to adopt already-moved bytes. **Additionally** the record step is unguarded: `resources.Update` (`ResourceLayoutReconciler.cs:57`) can throw (`SqliteProjectRepositories.cs:221-224`), aborting the remainder of the whole reconcile pass — violating the component's own "never throws per-resource" contract (tracked as F-12).
- **Root cause:** Move-first-then-record with recovery keyed on the source path instead of the destination.
- **Proof method:** Static trace; the reconciler tests cover missing/locked *sources* (`ResourceLayoutReconcilerTests.cs:131-146`) but no failing-update-after-successful-move case.
- **Coverage missing:** repository failure after a successful move; unguarded-update abort; locked destination.
- **Remediation:** When `MoveInto` returns null, detect bytes already at the desired (or numbered) destination with the source gone and `RelocateTo` without moving; wrap the record step per-resource; add both failure-injection tests.

### F-03 — High (UX, deterministic) — Task-level editing silently targets one of several sessions

- **Subsystem:** Unified task editor. **Blocks:** dogfooding trust in the editor; release.
- **User impact:** For a task the planner split into N sessions, the editor silently edits the earliest pending session; toggling **Scheduled** off deletes only that session while siblings keep the task on the calendar — the user saves what looks like an unscheduled task and it is still scheduled. Delete, by contrast, removes the task and *all* sessions behind a singular-worded confirmation.
- **Evidence:** selection `CalendarService.GetEditableSessionForTask` (`CalendarService.cs:451-456`, earliest-pending-else-latest); single-session removal (`CalendarService.cs:149-151, 311-322`); no session count anywhere in `TaskEditorView.axaml`; whole-task delete `CalendarService.cs:187-208`; confirm copy `TaskEditorViewModel.cs:122-124`.
- **Root cause:** The editor's data model has exactly one Date/Start/End/Repeats slot (`TaskEditorViewModel.cs:172-185`) while planning can create many sessions per task. Service semantics are consistent and tested; the defect is UI opacity.
- **Coverage missing:** any test opening the editor on a 2+-pending-session task asserting what Scheduled-off leaves; any scope-disclosure UI test.
- **Remediation (design decision required):** distinguish *Edit task* from *Edit this session*; show "session X of N"; name removal actions by real scope ("Remove this session" vs "Unschedule all sessions").

### F-11 — Medium/High — Opening a resource whose stored file is missing crashes the app (new)

- **Subsystem:** Project Files. **Blocks:** valuable-data dogfooding (reachable via F-02, or bytes deleted externally).
- **Expected:** A friendly error for a missing file. **Actual:** `OpenExternally` → `Process.Start(new ProcessStartInfo(path){UseShellExecute=true})` throws `Win32Exception` for a nonexistent path; no try/catch on the command path and no global exception handler (`FileDetailViewModel.cs:263-274`; `src/BeBoosted.Desktop/Platform/IFileRevealService.cs:17-27`; `Program.cs:10-11`).
- **Root cause:** Trust that stored paths always resolve. **Coverage:** none. **Remediation:** guard open/reveal with existence check + notice; add a global last-chance handler.

### F-09 — Medium — A "Didn't happen"/"Needs more time" session leaves its task in both Scheduled and Unscheduled (new)

- **Subsystem:** Daily list. **Blocks:** dogfooding comprehension of the day; violates the plan's explicit invariant.
- **Expected (2026-08-14 plan):** "No task may appear in Scheduled and Unscheduled simultaneously."
- **Actual:** Occurrence classification keys only on `IsDone` (`DailyListViewModel.cs:142-175`), so a resolved-not-done session stays a Scheduled row with live controls; the resolved outcome removes the block from the pending filter (`SqliteCalendarBlockRepository.cs:127-140`), so `GetInboxTasks()` returns the task and a second Unscheduled row is added (`DailyListViewModel.cs:191-201`). Progress totals survive only via TaskId dedup.
- **Root cause:** The classification never enumerates resolved-not-done outcomes. **Coverage:** outcome tests assert repository state, never section membership. **Remediation:** treat resolved sessions as their own row state (or exclude the task from Unscheduled while a resolved session row renders); add the section-membership regression test.

### F-10 — Medium — Daily list goes stale after Inbox-drawer and chat captures (new)

- **Subsystem:** Refresh chain. **Expected:** every mutation refreshes calendar/counts exactly once (plan constraint).
- **Actual:** `InboxViewModel.Capture()` adds the task and its row but raises no `TasksMutated` (`InboxViewModel.cs:109-121` — only `RemoveRow` announces, `:123-129`); chat's `onTasksChanged` reloads Inbox + Projects only (`ShellViewModel.cs:44-48`). The Today list behind the drawer keeps stale Unscheduled/progress until an unrelated mutation or navigation.
- **Root cause:** capture paths predate the Daily list; the chain was extended only for complete/delete. **Coverage missing:** daily-list state after capture. **Remediation:** announce capture through the same chain; regression test.

### F-15 — Medium — Editing a repeating task from a task row scopes "Completed" to the series anchor occurrence (new)

- **Subsystem:** Unified task editor. **Blocks:** valuable-data dogfooding (records completion against the wrong date — data misrepresentation).
- **Actual:** `OpenTaskEditorForTask` passes the session's *anchor date* as the occurrence and initializes Completed from that date (`CalendarViewModel.cs:221-224`); ticking it completes the anchor occurrence, possibly weeks old, not today's. Block-click paths pass the clicked occurrence correctly (`CalendarViewModel.cs:194-207`; `DailyListViewModel.cs:420-423`).
- **Coverage:** all occurrence tests open via block paths; none via task rows. **Remediation:** resolve "today's occurrence" (or the next occurrence on/after today) when entering from a task-level surface; regression test asserting which occurrence completes.

### F-06 — Medium — A zero-block planning result persists as a hidden active draft (creation path still open; adjacent paths fixed)

- **Subsystem:** Planning. **Expected:** the codebase's own doctrine — settled "rather than surviving as an empty active draft" (`PlanningProposal.cs:207-212`).
- **Actual:** `CreateDraft` saves the proposal unconditionally, zero blocks included, after discarding the previous active draft in the same transaction (`PlanningService.cs:54-64`). `HasDraft` requires a pending block (`CalendarViewModel.cs:382`) so the draft is invisible; both Discard entry points are hidden behind `HasDraft`, so the prescribed recovery is unreachable; the in-session undo stack is wiped; the draft survives restart (migration 0011's settlement runs once, not per startup). Reachable whenever the window is fully booked (`DeterministicScheduler` returns all-unplaced; `PlanningServiceTests.cs:181-196` constructs the exact repro without asserting the persisted draft).
- **Fixed since first pass:** removal-emptied drafts and deletion-emptied drafts now settle (`PlanningProposal.cs:159-164, 214-241`), and 0011 normalizes legacy rows once.
- **Remediation:** normalize the empty result at creation (skip save or settle immediately); assert `GetActiveDraft() == null` in the existing repro test.

### F-13 — Medium — Priority decisions and ranks persist in two separate transactions (H9)

- **Subsystem:** Priority Sort persistence. `Complete()` runs `SaveDecisions` then `ReplaceRanks`, each on its own connection and transaction (`PrioritySortService.cs:56-67`; `SqlitePrioritizationRepository.cs:12-41, 69-98`). A crash between them commits history for a ranking that was never saved; the session's outcome is silently lost and later incremental sorts seed from stale ranks. Each half is individually atomic, so no partial-rank corruption. **Missing:** kill-between-writes test; a unit-of-work on `IPrioritizationRepository`. **Remediation:** one transaction for both writes.

### F-14 — Medium — StartPrioritySort CanExecute goes stale; one edge throws out of a command (new)

- **Subsystem:** Shell gating. `NotifyCanExecuteChanged` fires only on `Inbox.OpenCount` change and after a completed sort (`ShellViewModel.cs:76-83, 198`) — not on ViewKind/VisibleDate change (`:84-90`) though `CurrentPlanningPeriod` is an input to `CanStartPrioritySort` (`:154-172`). Stale-disabled: navigate to an unranked tomorrow. Stale-enabled: completing scheduled tasks from the Daily list never touches `OpenCount`, and executing with an empty live set throws `DomainException` from the session constructor (`ComparisonSession.cs:44-47`) out of a RelayCommand. **Remediation:** notify on period change and calendar mutations; guard the empty set.

### F-12 — Medium — Reconciler record-step throw violates its never-throws contract (companion to F-02)

- `resources.Update` is outside the per-resource guard (`ResourceLayoutReconciler.cs:56-57`); a `DomainException`/`SqliteException` aborts the remaining projects for that launch (contained by the startup catch, `App.axaml.cs:84-89`) and would surface to a rename caller through `ProjectService.RenameProject:42` if a rename UI ever ships. **Remediation:** per-resource try around move+record.

### F-04 — Medium — Sanitized project/File-folder collisions merge unrelated folders; the design's own limitation text describes numbering that does not exist

- `ResourceLayout.FolderFor` (`ResourceLayout.cs:60-63`) maps names differing only by invalid characters/whitespace/truncation to the same folder; `Store`/`MoveInto` pass the folder verbatim and number **file names only** (`LocalResourceStorage.cs:22, 37, 71-86`). Unrelated projects' documents intermix in one browsable directory (per-resource DB rows keep app behavior correct; no oscillation — `IsAlreadyPlaced` compares the shared folder). The spec's "numbered folder pair" (design lines 96-97, 212-213) is not implemented. **Coverage missing:** cross-project and cross-File folder collision tests. **Remediation:** give folders identity (numbering or id suffix) or amend the design text; test both projects.

### F-05 — Medium — Project rename (and project/File delete) have no user-facing entry point

- `RenameProject` (`ProjectService.cs:34-43`) is tested and calls `ReconcileProject`, but a grep of `src/BeBoosted.Desktop` finds zero callers of `Rename`, `DeleteProject`, or `DeleteFile`; `ProjectFile.Rename` and `Resource.Rename` are called nowhere in `src/`. The rename-sync half of the named-folders feature is dead code from the user's perspective. **Remediation:** small project menu (rename/delete) or explicitly de-scope in the design.

### F-16 — Medium/Low — Import failure mid-batch throws through an async-void handler (new)

- `Store` guards only a missing source; a `File.Copy` IOException (disk full, locked destination) propagates through `ProjectService.ImportFile` → the `FileDetailViewModel.Import` loop (earlier files stay imported) → `async void OnAddDocumentClick` (`ProjectsView.axaml.cs:56-63`) — unobserved exception, likely crash. Picker results with no local path are silently dropped (`:81-85`). **Remediation:** per-file try with a per-file error surface; test.

### F-07 — Low (hardening) — Stored resource paths lack containment validation (historical BB-QA-011)

- `ResolvePath` is bare `Path.Combine` (`LocalResourceStorage.cs:54`); a rooted or `..` stored path from a corrupted/tampered DB escapes the resources root into `Exists`/`Delete`/`MoveInto`/`Process.Start`. Generated paths cannot escape (sanitizer strips separators), so exploitability requires DB corruption; it remains zero defense-in-depth where `Delete` could remove an arbitrary user file. **Remediation:** canonicalize + prefix assertion; tests.

### F-08 — Low (performance risk) — Project detail query fan-out

- `GetScheduledBlocks` probes occurrence completion per date over a 29-day window (`ProjectService.cs:161, 224-233`), one connection per probe; a batch API exists and is unused (`IOccurrenceCompletionRepository.GetForBlock`, used by `CalendarService.cs:316,328`). Additionally the Projects *list* calls `tasks.GetAll()` once per project card and refreshes on every `Calendar.DataChanged` (`ProjectsViewModel.cs:108-119, 168-183`). No failure observed; no benchmark exists. **Remediation:** batch the completion reads; measure before calling it scalable.

### F-17 — Low (grouped) — Stale-row and non-domain exceptions escape view-model commands

- `ReopenRow`, `CompleteTask`, `UnscheduleBlock`, and `TaskRowViewModel.Delete` let `DomainException` escape for a row whose task/block vanished behind the view (`DailyListViewModel.cs:376-399`; `CalendarViewModel.cs:714-719`; `TaskRowViewModel.cs:77-84`) — the first is spec-acknowledged, the rest uncatalogued. Editor Save/Delete handlers catch `DomainException` only; a `SqliteException` mid-save propagates (rollback itself is safe) (`CalendarViewModel.cs:311-314, 326-332`). The aggregate undo (unchecking a session clears Done on every one-off sibling) is intentional but appears in no UI copy. **Remediation:** one guarded mutation helper for row commands; a UI sentence for aggregate undo.

### F-20 — Low (grouped) — Planning edge inconsistencies (new)

- Move/Resize/Remove proposal mutations never validate proposal state (Draft/active) at service level (`PlanningService.cs:70-89`) — UI happens to guard. Unscheduling an approved session leaves a dangling Approved-status proposed block (no reconciliation outside `DeleteTask`; undo correctly rejects and the UI prunes, but the stale row persists) (`CalendarService.cs:148-157` vs `:198-204`). `ApproveAll` silently no-ops on a zero-pending draft (`PlanningService.cs:143-146`). Proposals are never deleted in production — `IPlanningProposalRepository.Delete` has no caller; history grows unboundedly (intentional forensics, unbounded).

### F-22 — Low (UX) — Single-ranked-task re-rank completes with zero explanation (H10)

- Correct per design: empty seed → instant completion at rank 1; the user does see the results screen ("Re-rank · Today · 0 comparisons"). But nothing explains why no comparison happened — an instant full-screen flash that reads like a glitch. **Remediation:** one sentence of copy on the results screen when comparisons == 0.

### F-18 — Low (accessibility) — The unranked dash's promised tooltip is unreachable

- The plan promises tooltip "Not ranked for this day."; the marker is a disabled Button and the codebase's own documented model is that disabled controls raise no tooltip — the exact reason the session checkbox got a hit-testable overlay (`DailyTaskListView.axaml:269-273`; `DailyRowViewModel.cs:289-294`). Screen readers still get the text via `AccessibleName`. **Remediation:** same overlay pattern, or drop the tooltip promise.

### F-19 — Low (UX) — "Needs outcome" never appears in real time

- The 60-second timer updates only the Week now-line; Daily `NeedsOutcome` is computed at `Rebuild` (`CalendarView.axaml.cs:15-17`; `DailyListViewModel.cs:257-259`), so a session elapsing while Today sits idle shows no chip until the next mutation/navigation.

### F-23 — Low (accessibility) — The task-editor modal is not a keyboard focus trap

- The scrim blocks pointers (tested) but background controls stay Tab-reachable; no `KeyboardNavigation` containment (`MainWindow.axaml:344-351`). Initial focus, Escape, and invoker restoration are tested and work. Native verification pending.

## 8. Design-versus-implementation mismatches

| Design source | Contract | Implementation reality |
| --- | --- | --- |
| 2026-08-17 incremental sort | "Build my plan now is hidden" in re-rank | Not rendered into the view at all — F-01. |
| 2026-08-19 named folders | Bytes-moved/row-stale failure "the next reconcile repairs" | Repair does not exist — F-02. |
| 2026-08-19 named folders | "Neither ever throws for a per-resource failure" | Record step can throw and abort the pass — F-12. |
| 2026-08-19 named folders | Identically-sanitizing projects "share a numbered folder pair" | No folder-level numbering exists anywhere — F-04. |
| 2026-08-14 daily plan | "No task may appear in Scheduled and Unscheduled simultaneously" | Violated by resolved-not-done sessions — F-09. |
| 2026-08-14 daily plan | "Every mutation refreshes … exactly once" | Capture paths refresh zero times — F-10. |
| 2026-08-14 daily plan | Unranked dash has a tooltip | Tooltip unreachable on a disabled control — F-18. |
| 2026-08-14 daily plan | FIXED/FLEX status chips; inline add-forms; commitment editor | Deliberately superseded by tests: kinds renamed Obligation/Session, chips reduced to Synced/PROPOSED, add-flows route to the unified modal (all-or-nothing failure — better than the planned partial-capture), commitment machinery deleted with a rendered no-commitment-terminology guard test. Redesign, not drift. |
| 2026-08-16 done toggle | `RepeatingCompletionNote` as disabled-checkbox tooltip | Implemented as a hit-testable overlay instead — an improvement (tooltips don't surface on disabled controls). All seven spec scenarios implemented. |
| 2026-08-17 incremental sort | Daily marker "visible only when ranked" | Always visible, disabled when unranked; doubles as the tier label. Defensible deviation. |

## 9. UI/UX remaster backlog

Preserve the cream/graphite/lime identity; structure before decoration. Fresh renders
confirm the visual language remains coherent, the unified editor is centered and
stable with an obvious Cancel, the Daily list fits 1100×720 without horizontal
scroll, and the plan banner/undo toast read clearly.

### P0 — prevents mistakes or data misunderstanding

1. Separate **task** vs **session** editing scope in the modal ("session X of N"; removal actions named by real scope) — F-03/F-15.
2. Hide the re-rank early exit and rename the regular sort's early exit (**Build my plan now** → *Use these priorities*): ranking does not build a plan — F-01 companion copy fix.
3. Resolve the dual-listing of a task after *Didn't happen*/*Needs more time* (dedicated row state, one location) — F-09.
4. Label or replace the unranked `–` marker (reads as stray punctuation; its explanatory tooltip cannot surface) — F-18.
5. A visible error surface (not a crash) for missing resource files — F-11.

### P1 — materially simplifies a core workflow

1. Project detail around one task list: "Stats HW" can currently render three times on one screen (Open Tasks, Scheduled, Recently Completed — confirmed in fresh `project-scheduled-states` render). Show next session/status inline.
2. **New task in this project** action on project detail; a small project menu exposing rename/delete (unlocks the dead rename-sync feature — F-05).
3. Raise resting contrast of the row action icons (pencil/clock/×/… at ~0.45 opacity are hover-discovery-only; verified faint in all fresh Daily renders).
4. Reconcile Today vs Week completion affordances for the same session (Today: checkbox with undo; Week: outcome flyout with **no undo once done**) and the chip copy ("Needs outcome" vs "outcome?").
5. Inbox drawer duplicates the Unscheduled list behind it; the footer hint says "drag onto the calendar" while Today (a list, not a grid) is behind — dim the background or scope the hint to Week.

### P2 — accessibility, responsiveness, polish

1. Date/deadline pickers render cramped placeholder text ("monthdayyear", "August112026"); the per-occurrence completion note is tiny monospace — plain-language sentence instead.
2. Week narrow columns and overlapping blocks truncate titles hard (fresh 1100/1280/1440 renders); a compact card variant is still missing (BB-QA-012/013).
3. Keyboard: Tab containment for the modal (F-23), a shortcut to open the Inbox drawer, focus restoration on drawer close (BB-QA-006).
4. "Jump to now" affordance for the 24-hour Week canvas (initial scroll exists; no button).
5. Real-time "Needs outcome" chip (F-19); completed-row focus indication is dimmed with the row (container opacity 0.62).
6. Re-rank results copy for the zero-comparison case (F-22).

## 10. Historical QA reconciliation (BB-QA-001…015)

| ID | Status after this pass | Fresh evidence |
| --- | --- | --- |
| 001 full-day timeline | **Closed.** | `VisibleStartHour=0/VisibleEndHour=24`, one geometry chain with 23:59 clamps, regression tests (`TimelineSurfaceView.axaml.cs:22-23`; `TimelineSurfaceTests.cs:48-100`); fresh Week renders scroll past 10 PM. |
| 002 high-DPI cutoff | **Closed in code with regression tests; native DPI matrix not re-verified.** | All-edge physical-pixel clamp incl. the exact audited case (`WindowPlacementMath.cs:99-215`; `WindowStateService.cs:24-154`; 30+ tests). |
| 003 context sentence → task | **Improved but still partial — the reported repro still fails.** | The fix strips the clause only mid-sentence (`LocalHeuristicAiProvider.cs:141` requires leading whitespace); the standalone "It probably needs…" sentence still becomes a draft — visible in the fresh `chat-task-review` render. No repro regression test. |
| 004 data-root recovery | **Still open.** | `EnsureDirectoriesExist()` at `App.axaml.cs:31`, outside the try at `:62`; `Program.cs` has no catch. |
| 005 unsupported import types | **Still open.** | No validation below the picker (`ProjectService.cs:90-106`; `FileDetailViewModel.cs:127-135`). |
| 006 Inbox focus restoration | **Still open.** | Open-path focus only (`MainWindow.axaml.cs:19-25`); close paths restore nothing. The task editor got exactly this treatment; the drawer did not. |
| 007 task-editor Cancel | **Closed.** | Cancel + Escape + no-mutation + focus-restore tests (`TaskEditorView.axaml:148`; `TaskEditorModalTests.cs:55-136`). |
| 008 Inbox batch selection | **Still open.** No selection model exists. |
| 009 recurrence/constraints UI | **Improved but still partial.** | Weekly recurrence editable (`TaskEditorView.axaml:100-120`); scheduling constraints surface only as warnings (`DailyListViewModel.cs:525-528`). |
| 010 document/page citations | **Still open (documented v1 limit).** | `SimpleLocalIndexer.cs:27-39` indexes title+filename only. |
| 011 path containment | **Still open — F-07.** |
| 012 short-block clipping | **Still open.** | 18 px floor (`TimelinePanel.cs:106-107`) vs two-row template; no compact variant. |
| 013 Week narrow clipping | **Still open / marginally improved.** | Title ellipsis only; truncation visible in fresh 1100/1280 renders and in overlap at 1440. |
| 014 Projects empty centering | **Closed.** | Panel-centered layout (`ProjectsView.axaml:47-56`), size-specific tests (`ProjectsEmptyStateTests.cs:31-61`), fresh 1280 render confirms. |
| 015 macOS distributable | **Still open; not re-verified beyond statics.** | No `.plist`/`.entitlements`/bundle artifacts exist in the repo. |

## 11. Release and dogfooding gates

- **Isolated development / disposable-profile dogfooding:** open now. Suite green, publish and packaged startup verified.
- **Dogfooding against valuable data:** blocked by **F-01** and **F-02** (both can silently damage or orphan real state), plus **F-11** (crash on a reachable state) and **F-15** (records completion against the wrong occurrence). Fix all four with regression tests first.
- **Windows alpha:** the above + F-03 scope decision, F-09/F-10 daily-list correctness, BB-QA-004 (silent startup death) fixed.
- **Windows public:** + BB-QA-003/005/006, F-13/F-14, containment F-07, and the P0/P1 backlog.
- **macOS:** BB-QA-015 unchanged — no bundle/signing/notarization work exists; requires real Mac execution.

## 12. Recommended implementation sequence

1. **F-01**: bind `ShowBuildPlanNow`, guard `Finish()`, rendered re-rank UI test. (Small, one-file + test.)
2. **F-02 + F-12 + F-11**: destination-adoption recovery, per-resource guard around record, missing-file guards on Open/Reveal, failure-injection tests both sides of the mutation.
3. **F-09 + F-10**: resolved-session row state and capture-path refresh, with section-membership tests.
4. **F-06**: normalize empty planning results at creation (extend the existing repro test with `GetActiveDraft()` assertion).
5. **F-15** with the **F-03** design decision: define task-vs-session editor scope, then implement occurrence resolution for task-row entry.
6. **F-13 + F-14**: single-transaction Complete; CanExecute notification on period change.
7. **F-04 + F-07** together (folder identity + containment), then **F-05** (project menu) which also unlocks rename-sync.
8. BB-QA-004 startup boundary; BB-QA-003 parser sentence classification; BB-QA-005 import validation; BB-QA-006 drawer focus.
9. P1 remaster items (project detail single list, icon contrast, Today/Week completion parity), then P2 polish.

Steps 5 and 9 need an approved interaction design before code.

## 13. Not verified in this audit

- The contents or relational integrity of the real production database, and the four production resources moved by the reconciler on 2026-08-19 (log evidence from the first pass; the log was not re-read).
- Native high-DPI/taskbar behavior of the new window clamp (code+tests only), real screen-reader output, and full Tab-order behavior of the modal.
- macOS anything (compile-only claims from the earlier full QA report; nothing re-run).
- Week view fit at 1100×720 is captured but not assert-tested (Daily is).
- Long-run concurrency, power-loss recovery, large recurring workloads; F-08's fan-out has no benchmark.
- Migration 0010/0011 edge cases on hand-corrupted databases: a one-off completion row whose date mismatches its block is silently dropped (`0010:29-39`); a proposal-orphan `proposed_blocks` row would abort startup (`0011:33` + FK); a stale kind-0 flip keeps title/project shadowing (`0010:49`). All unreachable via healthy app history; none tested.
- The unbounded growth of retained Discarded/Approved proposals (no production caller of `IPlanningProposalRepository.Delete`).

## 14. Project Memory Update (for future sessions)

- Working tree = HEAD `2baa100` + large uncommitted set: unified Task model (migrations 0010/0011), unified task editor modal, Daily list + session done toggle, full-day timeline, seeded Priority Sort + re-rank, named resource folders + startup reconciler. All 645 normal + 3 screenshot tests green; publish + isolated packaged startup verified 2026-08-20.
- **Do not** trust `PrioritySortView.axaml` — it predates re-rank; F-01 (visible "Build my plan now" persists a demoted re-rank) is the top blocker with F-02 (reconciler cannot recover a moved-but-unrecorded file; design's repair claim is false).
- New this pass: F-09 (dual-section after Didn't happen/Needs more time), F-10 (capture doesn't refresh the Daily list), F-11 (Open on missing file crashes), F-15 (task-row edit completes the anchor occurrence), F-14 (stale sort gating incl. a throwing edge).
- Fixed and verified since the full QA report: BB-QA-001 (00:00–24:00), 002 (in code + tests), 007 (Cancel), 014 (empty-state centering). Still open: 003 (partial), 004, 005, 006, 008, 009 (partial), 010, 011, 012, 013, 015.
- Service/persistence layers are transaction-atomic and adversarially tested; prefer extending that discipline (rendered-UI assertions, failure injection at the repository seam) over re-testing happy paths.
- Runtime isolation recipe that provably avoids the live profile: set `BEBOOSTED_DATA_DIR` to a fresh temp dir before any launch; it is the sole override, read once at startup.
