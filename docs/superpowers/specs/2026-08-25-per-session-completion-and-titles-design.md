# Per-session completion and session titles

Date: 2026-08-25

Status: Approved design, ready for implementation planning

## Problem

Marking a scheduled session done completes the entire parent task, and resolves
every other pending one-off session of that task along with it. Undoing reverses
the same aggregate. A task with three sessions across three days is finished — all
three — by ticking the first one.

This was known and deliberate. The predecessor spec
(`2026-08-16-today-session-done-toggle-design.md`) shipped the one-click toggle and
recorded the consequence under Known limitations:

> Undoing a session reopens the whole Task, which clears the Done outcome on every
> one-off sibling session of that task. [...] it is aggregate, not per-session.

This spec removes that limitation.

Separately, a session cannot carry its own name. `CalendarBlock.Title` exists but is
populated only for external events; sessions pass `null` and display their task's
title. Two sessions of *Read Jane Eyre 1-20* are indistinguishable on the day.

## Goals

- Marking a session done resolves **that session only**. The parent task stays open.
- The parent task returns to the Unscheduled section once all of its sessions are
  resolved, and not before.
- Multiple open sessions of one task complete independently, in any order.
- A session may carry its own optional title, with the parent task shown as context.
- Undo stays one click and is likewise per-session.

## Non-goals

- The parent task never auto-completes. It completes only when the user ticks the
  task itself. (Explicit product decision.)
- No per-session deadline, estimate, or project. Those stay task-level.
- No change to how repeating occurrences complete — they already complete per
  occurrence through `IOccurrenceCompletionRepository`.
- No change to what *Needs more time* or *Didn't happen* mean.
- No new outcome kinds.
- The session editor does not gain a completion control for one-off sessions. It
  keeps offering completion for repeating occurrences only, as it does today. A
  session is completed from the daily list or the project page. This keeps
  `UpdateSessionSchedule` — which carries careful conversion-reconciliation and
  atomicity guarantees — out of the change.

## Approach

Approach A of three considered: **decouple the `Done` outcome** rather than build a
parallel completion mechanism.

`RecordOutcome(id, Done)` stops applying the aggregate transition and simply records
the outcome on its own block — exactly what `NeedsMoreTime` and `DidntHappen`
already do. The task→sessions direction is deliberately preserved: completing a task
still resolves its pending sessions, because finishing the task makes its remaining
sessions moot, whereas finishing one session says nothing about the task.

The two rejected alternatives:

- **Unify one-off sessions onto `IOccurrenceCompletionRepository`.** Conceptually
  tidier, but creates two representations of "session done" — the `outcome` column
  and the completions table — that must be kept in agreement. The outcome column has
  to survive regardless, since the other two outcomes live there.
  `GetTaskIdsWithPendingBlocks` keys off `outcome = 0`, so the Unscheduled behavior
  this spec depends on would have to be rebuilt rather than inherited.
- **A separate per-session completed flag distinct from outcome.** Leaves `Done`
  untouched, at the cost of two near-synonymous notions of a finished session.

### Why the Unscheduled behavior needs no new code

`InboxQueryService.GetInboxTasks` returns open tasks minus
`blocks.GetTaskIdsWithPendingBlocks()`, which is
`SELECT DISTINCT task_id FROM calendar_blocks WHERE task_id IS NOT NULL AND outcome = 0`.

A task is therefore already excluded from Unscheduled exactly while it has at least
one unresolved session, and returns the moment its last session resolves. With
per-session outcomes this yields the requested behavior with no query change:

| State | Scheduled | Unscheduled |
| --- | --- | --- |
| Two sessions, none resolved | both sessions | — |
| One marked done | remaining session | — (still represented) |
| Both resolved | — | the task |

## Behavior

### Marking a session done

The existing `Button.dailyCheck` on a `Session` row is unchanged in appearance and
placement. Checking it records `Done` against that block. The row moves to
Completed; siblings are untouched; the task's `IsCompleted` is untouched.

### Undoing

Unchecking calls a new `CalendarService.ClearSessionOutcome(blockId)`, which clears
that block's outcome and nothing else. The row returns to Scheduled and regains its
Change time / Unschedule / Edit actions.

This replaces the current `ReopenTask(taskId)` call, which is the aggregate inverse
and the specific behavior being removed.

### The repeating-sibling gate disappears

`RecordOutcome` currently rejects `Done` when any sibling repeats, and
`SetTaskCompletion` does the same, because both meant whole-task completion. Once a
session's Done is local, a task may legitimately hold a repeating series *and* a
finished one-off session, so the guard is dropped **from `RecordOutcome` only**.
`CompleteTask` / `SetTaskCompletion` keep theirs.

Consequently the disabled state and its explanatory note become dead and are
removed: `CanToggleSessionDone`, `CanRecordDone`, `ShowRepeatingCompletionNote`,
`RepeatingCompletionNote`, `ShowSessionCheckBlockedNote`, and the
`Button.dailyCheck:disabled` style added by the predecessor spec. This is a net
deletion.

### Session titles

`CalendarBlock.Title` becomes settable for sessions via `Retitle(string?, now)`,
which trims and maps blank to `null`. External events keep an immutable title;
`Retitle` throws for them.

A titled session's daily row shows the session title as its primary text and the
parent task as subtext, joined with the existing project and deadline context:

```
  ○  9:00a   Jane Eyre 1-10
                Read Jane Eyre 1-20 · Schoolwork · Thu
```

An untitled session renders exactly as it does today — `block.Title ?? task.Title`
is already the rule in `BuildOccurrenceRow`, so the fallback needs no new branch.

### Progress counting

The day's counter changes from tasks to sessions, because sessions are now the unit
of work:

- `done` = sessions marked `Done` + tasks completed today that no session represents
- `total` = `done` + unresolved sessions + open unscheduled tasks

`NeedsMoreTime` and `DidntHappen` sessions remain settled history: counted in
neither, with their task counted through its own Unscheduled row once fully
resolved. This preserves today's intent, applied per session.

This visibly changes the numbers — a task with three sessions now contributes three
units rather than one. Intended.

## Components

### `BeBoosted.Domain` — `CalendarBlock`

- `Title` becomes `{ get; private set; }`.
- New `Retitle(string? title, DateTimeOffset now)`; trims, blank → `null`, throws for
  external events, touches `ModifiedAt`.
- `CreateTaskSession` gains an optional `title` parameter (currently hardcodes
  `null`).
- `EnsureOccurrenceCompletable`'s rejection message for a one-off session becomes
  "A one-off session records an outcome, not an occurrence completion." The guard
  itself stays — occurrence completion genuinely does not apply to one-off sessions —
  but its current wording asserts the aggregate rule this spec removes.

### `BeBoosted.Application` — `CalendarService`

- `RecordOutcome`: the `Done` branch no longer calls `ApplyAggregateCompletion`, and
  the repeating-sibling guard is removed from it. `ApplyAggregateCompletion` itself
  is untouched and still serves `CompleteTask` / `ReopenTask` / `UpdateTaskDetails`.
- New `bool ClearSessionOutcome(CalendarBlockId id)` — clears one block's outcome,
  returns false when already clear, rejects external events.
- `TaskScheduleRequest` gains a trailing optional `string? Title = null`, so the ~15
  existing construction sites are unaffected.
- `AddSession` and `UpdateSessionSchedule` pass the title through to the block.

### `BeBoosted.Desktop` — `DailyRowViewModel`

- New `ParentTitle` (null unless the block carries its own title), driving the
  subtext.
- `ToggleDone`'s `Session` branch comment updated; the `Task or Session when IsDone`
  branch splits so a Session routes to the per-session clear.
- Delete `CanToggleSessionDone`, `CanRecordDone`, `ShowRepeatingCompletionNote`,
  `RepeatingCompletionNote`, `ShowSessionCheckBlockedNote`; the `RelayCommand`
  `CanExecute` on the toggle drops with them.

### `BeBoosted.Desktop` — `DailyListViewModel`

- `ReopenRow`: the `Session` branch calls `ClearSessionOutcome(blockId)`; the `Task`
  branch keeps `ReopenTask`.
- Progress counting reworked per the rule above.
- `BuildOccurrenceRow` populates `ParentTitle`.

### `BeBoosted.Desktop` — `SessionEditorViewModel`

- A `Title` field, its placeholder showing the parent task title so it reads as
  optional. Persisted through `TaskScheduleRequest.Title` on the existing save path.
- The completion section is unchanged (repeating occurrences only — see Non-goals).

### `BeBoosted.Desktop` — `ProjectDetailViewModel`

- `HasCompletionControl` drops its `Recurrence is not null` condition so a one-off
  session row can complete from the project page too.
- `SetOccurrenceCompletion` **must not** be the path for those rows.
  `CalendarBlock.EnsureOccurrenceCompletable` throws for a one-off session ("A
  one-off session completes its Task, not an occurrence"), so routing a one-off
  through `CompleteOccurrence` would raise a `DomainException` at runtime. The row
  branches on `Recurrence`: repeating → `SetOccurrenceCompletion` as today, one-off
  → `RecordOutcome(Done)` / `ClearSessionOutcome`.

### Views

- `DailyTaskListView.axaml`: subtext line for `ParentTitle`; remove the
  `Button.dailyCheck:disabled` style and the blocked-note element.
- `SessionEditorView.axaml`: the title field.

## Data

**No migration.** `calendar_blocks.title` already exists (migration
`0003_calendar_blocks.sql`) and `SqliteCalendarBlockRepository` already binds it on
both insert and update. Existing rows are untouched: a session with `title` NULL
keeps falling back to its task's title, which is current behavior.

No data becomes inconsistent under the new semantics. A task's `IsCompleted` is its
own column, so tasks completed under the old aggregate rule stay completed, and
their sessions keep the `Done` outcome they were given.

## Error handling

`CalendarViewModel.RecordOutcome` already catches `DomainException`, surfaces it as
a plain notice, and skips the reload. `ClearSessionOutcome` returns `bool` so a
no-op announces nothing, matching `ReopenTask`'s shape.

The pre-existing exposure noted in the predecessor spec — `ReopenRow` letting a
`DomainException` escape when the underlying task was deleted behind a stale row —
is unchanged on the Task branch. The new Session branch addresses a block by id and
returns false when it is gone, so it does not add to that exposure.

## Testing

Red→green throughout, service tests in `BeBoosted.Tests`, view-model tests in
`BeBoosted.Desktop.Tests`.

Service:

1. Recording `Done` on one session leaves its pending sibling at `None` and the task
   open. *(This is the inversion of the current contract.)*
2. Two sessions of one task resolve independently, in either order.
3. A task with one unresolved session is absent from `GetInboxTasks`; once its last
   session resolves it is present.
4. `ClearSessionOutcome` restores one block to `None`, leaves siblings alone, and
   returns false on a second call.
5. `RecordOutcome(Done)` succeeds when a repeating sibling exists.
6. `CompleteTask` still resolves every pending one-off session, and `ReopenTask`
   still clears them. *(Unchanged — the main regression signal that only one
   direction was cut.)*
7. A session title round-trips through SQLite; blank normalizes to `null`; `Retitle`
   throws for an external event.

View model:

8. Marking one of two session rows done moves only that row to Completed; the other
   stays in Scheduled.
9. The task appears in Unscheduled only after both sessions resolve.
10. Unchecking a done session returns it to Scheduled without disturbing its sibling.
11. A titled session row exposes the session title with the parent as `ParentTitle`;
    an untitled one leaves `ParentTitle` null and shows the task title.
12. Progress counts sessions: a task with two sessions, one done, reports 1 of 2.
13. A session whose task also repeats now has an enabled checkbox.
14. A one-off session row on the project page completes without throwing, and a
    repeating one still completes per occurrence.

Existing assertions that invert, deliberately:

- `TaskCompletionAuthorityTests.RecordOutcomeDone_ResolvesEveryPendingOneOffSibling`
  becomes the sibling-survives test.
- `DailyListViewModelTests` and `DailyListUiTests` assertions on
  `CanToggleSessionDone` / `ShowSessionCheckBlockedNote` /
  `ShowRepeatingCompletionNote` are removed with those members.
- Any daily-list progress assertion counting tasks is restated in sessions.

## Known limitations

- A task whose sessions are all done still sits in Unscheduled until ticked. That is
  the chosen product behavior, not an oversight, but it means finished work can
  linger visibly.
- The Calendar view's own block controls keep their existing outcome affordances.
  Today and Calendar continue to differ in how a session is completed; unifying them
  remains out of scope, as it was in the predecessor spec.
- Session titles are not searchable and do not participate in AI provenance or
  indexing.
