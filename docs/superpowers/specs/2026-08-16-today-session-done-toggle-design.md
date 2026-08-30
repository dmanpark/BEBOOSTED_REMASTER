# Today's tasks: one-click done and undo for scheduled sessions

Date: 2026-08-16
Status: Approved, ready for implementation planning

## Problem

On the Today page, a one-off scheduled task session (`DailyRowKind.Session`) is the
only row kind without a completion checkbox.

Marking one done means opening a three-option flyout — *Done*, *Needs more time*
(with a minutes spinner), *Didn't happen* — where every other row kind offers a
single click. The capability is there and is not time-gated, but the friction is
real enough that the common case feels blocked.

Undoing is genuinely impossible. Once a session is done its control becomes a
static, non-clickable marker (`ShowDoneBlockMarker`) and the row drops into the
collapsed Completed section. `CanReopen` excludes Session rows deliberately,
commented "no invented binary reopen". A mis-click cannot be taken back from this
page.

## Goals

- A scheduled session is marked done in one click, before or after its slot elapses.
- A done scheduled session is un-done in one click.
- *Needs more time* and *Didn't happen* survive, reachable without cluttering the
  completion cluster.

## Non-goals

- No change to the Calendar view's block controls (`CalendarBlockViewModel`) or its
  review notice.
- No change to how repeating occurrences or bare tasks complete.
- No new outcome kinds, and no change to what an outcome means.

## Approach

No service, domain, or persistence change. Both directions already exist in
`CalendarService`; the Today page simply never wired them up.

| Direction | Existing call | Notes |
| --- | --- | --- |
| Check | `RecordOutcome(blockId, BlockOutcome.Done, null)` | exactly what the flyout's *Done* button calls today |
| Uncheck | `ReopenTask(taskId)` | the precise inverse — clears every Done session outcome and reopens the Task; already backs the Task-row checkbox |

No migration, no persisted-format change, no new service method. The work is
confined to `DailyRowViewModel`, `DailyListViewModel`, and `DailyTaskListView.axaml`.

## Behavior

### Completion cluster

A `Session` row gets the same `Button.dailyCheck` control that unscheduled tasks
and repeating occurrences already use.

- **Unchecked → click marks done.** Works before and after the slot elapses. There
  was never a time gate in the view model, service, or domain; the "Needs outcome"
  chip only labels an elapsed row and does not gate anything.
- **Checked → click undoes it.** The row leaves Completed, returns to Scheduled,
  and regains its Change time / Unschedule / Edit actions (all gated on `!IsDone`).
- The static `ShowDoneBlockMarker` border is **removed**. The checkbox's checked
  state replaces it.

### Repeating-sibling case

When the session's task also carries a repeating series, `SetTaskCompletion` and
`RecordOutcome` both reject whole-task completion ("A repeating task completes per
occurrence, not as a whole"). The checkbox therefore renders **disabled**, with
`RepeatingCompletionNote` as its tooltip.

The control is enabled when `!TaskRepeats || IsDone` — equivalently, disabled only
when `TaskRepeats && !IsDone`, never on `TaskRepeats` alone. `SetTaskCompletion`
allows reopening unconditionally, by design, "so a corrupted globally-completed
repeating Task can recover". Gating undo on `TaskRepeats` would seal off that
recovery path.

### Secondary outcomes

*Needs more time* (with its minutes spinner) and *Didn't happen* move into the
row's existing quiet actions strip, behind one small icon button placed at the end
of the strip (after Unschedule) and visible when `Kind == Session && !IsDone`.
`RecordNeedsMoreTimeCommand` and `RecordDidntHappenCommand` are unchanged.

## Components

### `DailyRowViewModel`

- `ShowOutcomeControl` → `ShowSessionCheck` (`Kind == Session`, true whether done or
  not — it is the undo affordance too).
- New `ShowSessionOutcomeAction` (`Kind == Session && !IsDone`) for the side flyout.
- `ShowDoneBlockMarker` removed.
- `CanReopen` extended to Session rows.
- New `CanToggleSessionDone` (`!TaskRepeats || IsDone`) driving the disabled state.
- `ToggleDone()` gains a Session branch: done → `ReopenRow`, otherwise
  `RecordOutcome(Done, null)`.

### `DailyListViewModel`

- `ReopenRow` gains a Session branch calling `_calendar.ReopenTask(taskId)`.

### `DailyTaskListView.axaml`

- One `Button.dailyCheck` replaces the outcome-flyout button and the static done
  marker, bound to `ShowSessionCheck` / `IsDone` / `ToggleDoneCommand` /
  `CanToggleSessionDone`.
- A `Button.dailyCheck:disabled` style is added — the style currently has no
  disabled state.
- The outcome icon button is added to the actions strip.

## Data flow

Click → `ToggleDoneCommand` → `DailyListViewModel` → `CalendarViewModel.RecordOutcome`
or `NotifyTasksMutated` → `Reload()` → sections rebuilt. Exactly one refresh and one
`DataChanged` per click, matching every other row mutation. No new plumbing.

## Error handling

`CalendarViewModel.RecordOutcome` already catches `DomainException`, surfaces it as
a plain notice, and skips the reload and the announcement. `ReopenTask` returns
`bool`, so a no-op announces nothing.

`ReopenRow` does not catch `DomainException`. A row referencing a task deleted
behind the view's back would throw. This exposure already exists on the Task-row
branch of the same method and is left unchanged here rather than broadening scope.
See Known limitations.

## Testing

Red→green throughout, in `BeBoosted.Desktop.Tests`.

1. An elapsed scheduled session is marked done in one click; the row moves to
   Completed and the task completes.
2. A session whose slot has not yet started is marked done the same way.
3. A done scheduled session is unchecked; the row returns to Scheduled, the task
   reopens, and the block's Done outcome is cleared.
4. done → undone → done leaves task and block consistent.
5. A session whose task also repeats shows a present-but-disabled checkbox: the
   toggle command reports it cannot execute, records nothing if invoked directly,
   and the row keeps its repeating note.
6. A globally-completed repeating task can still be unchecked (recovery path
   preserved).
7. *Needs more time* and *Didn't happen* still work from the actions strip, with
   unchanged effects — including the task returning to Inbox with the minutes left.

Two existing assertions are rewritten to the new contract. This is the deliberate
reversal the change is about, not a regression:

- `DailyListViewModelTests.RecordingDone_MovesBlockRowToCompleted` (~line 490)
  asserts `ShowDoneBlockMarker` is true and `CanReopen` is false.
- `DailyListViewModelTests.MixedScheduleSessionRow_OffersOutcomes_ButNeverGlobalDone`
  (~line 781) and `ElapsedBlockWithoutOutcome_ShowsNeedsOutcome` (~line 213) assert
  `ShowOutcomeControl`, which is being renamed and re-scoped.

`DailyListUiTests` (~line 219) drives `RecordDoneCommand` and needs the same review.

## Known limitations

- `ReopenRow` still lets a `DomainException` escape when the underlying task has
  been deleted behind a stale row. Pre-existing on the Task-row path; unchanged.
- Undoing a session reopens the whole Task, which clears the Done outcome on every
  one-off sibling session of that task. This is the exact inverse of marking done
  (which resolves every pending one-off sibling as Done) and matches the Task-row
  checkbox, so it is intended rather than surprising — but it is aggregate, not
  per-session.
- The Calendar view's own block controls keep their existing outcome affordances.
  Today and Calendar will differ in how a scheduled session is completed until
  someone decides they should match.
