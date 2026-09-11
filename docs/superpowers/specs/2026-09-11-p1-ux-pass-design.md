# P1 UX pass: one task list, one way to finish, one focused drawer

Date: 2026-09-11

Status: Approved design, ready for implementation planning

## Problem

Three findings from the 2026-08-20 audit's P1 backlog share a shape: the app shows
the same thing more than once, or shows the same thing differently depending on where
you are standing. Each is individually survivable. Together they make the app feel
like several apps that agree on the data and disagree on everything else.

**Project detail lists one task up to three times.** The screen holds four
collections — `OpenTasks`, `ScheduledBlocks`, `RecentlyCompleted`,
`CompletedScheduledBlocks` — across two row types. Tasks and the calendar sessions
that serve them are peers on the same screen, so a task with two scheduled sessions
produces three rows: itself, and one per session. "Stats HW" appearing three times on
one screen is the recorded example. The header's count counts rows, so it does not
answer "how much is in this project".

**The same session finishes two different ways.** On Today a session has a checkbox.
On the Week timeline a *one-off* session has a flyout offering Done, Needs more time
(with a remaining-minutes field), Didn't happen, and Remove from calendar — and
`ShowCompletionControl` is `IsLocalSession && !IsRecurring && !IsDone`, so once it is
done the control disappears and there is no way back. A *repeating* occurrence on the
same surface uses a different control again: a circle that toggles done and reopens.
Three interaction models for one verb, and the one you meet most often is the only one
you cannot undo.

**The Inbox drawer shows you what is already behind it.** The drawer floats over the
Today list without dimming it, so the unscheduled tasks appear twice on screen at
once. Its footer reads "drag onto the calendar" while Today — a list, not a grid —
sits underneath.

## Goals

- A project's tasks appear **once each**, in one list, with their state readable
  without opening anything.
- **One way to finish a session**, identical on Today and on Week, and reversible
  everywhere.
- The Inbox drawer reads as the focused surface, and never claims an interaction the
  surface behind it does not support.
- No new concepts: this is presentation over the tasks and blocks that already exist.

## Non-goals

- **No data model change.** No new table, no new domain type, no migration. Every
  change here is view-model and view.
- **No change to the Week timeline's layout or geometry.** The P2 items — narrow
  column clipping, the compact card variant, "jump to now" — stay out.
- **No change to what the outcomes mean.** Done, Needs more time (with its remaining
  minutes returning the task to the Inbox), and Didn't happen keep their current
  semantics and their current service calls. Only how they are reached changes.
- **No change to the editors.** The scope-led whole-task and session editors built in
  F-03 are used as they are.
- **No project auto-association, no onboarding tour**, and none of the remaining
  small findings from the 2026-09-10 dogfooding run.

## Behavior

### Project detail: one row per task

The four collections collapse into one `Tasks` collection of a single row type. A
task appears exactly once regardless of how many sessions serve it. Sessions stop
being top-level rows and become the status shown on their parent's row.

Each row shows its title and exactly one **status affix**, chosen in this order:

| Priority | Condition | Affix |
| --- | --- | --- |
| 1 | A session has elapsed without an outcome | `needs outcome` |
| 2 | A session is scheduled | `Thu 4:00 PM · 1h` — the **next** one |
| 3 | Open, nothing scheduled | `unscheduled · 1h` (estimate omitted when unknown) |
| 4 | Completed | `✓ done Tue` |

Rows sort by that same priority, so what is stuck rises and what is finished settles.
Completed rows stay in the list, dimmed, rather than moving to a separate section —
the project's history stays legible without a second heading. The header count
becomes the number of tasks, so "6 tasks" means six things.

**What opens.** Clicking the row opens the **whole-task editor**, which already lists
every session. Clicking the **time affix** opens that one **session editor**. This is
the distinction F-03 already draws — list rows are whole-task scope, calendar blocks
are session scope — extended to the one row that can mean either.

### One way to finish a session

Both surfaces present the same two controls:

- A **checkbox** completes in one click and, checked, reopens in one click. This is
  the common case and it is always reversible.
- A **secondary control** opens the outcomes that are not a simple finish:
  **Needs more time** with its remaining-minutes field, **Didn't happen**, and
  **Remove from calendar**.

Concretely: Today keeps its checkbox and gains the secondary control on the same rows
that can have outcomes. On Week, the one-off session's flyout button becomes a
checkbox plus a secondary control, and — the point of the change — the checkbox stays
visible when the session is done, so it can be unchecked. The repeating-occurrence
circle already behaves this way and is left alone; it simply stops being the odd one
out.

Chip copy unifies on **"Needs outcome"**. The Week surface's "outcome?" is retired.

### The Inbox drawer

A scrim dims the surface behind the open drawer, so the duplicate list recedes and
the drawer reads as the thing in focus rather than a panel floating mid-page. The
footer's "drag onto the calendar" hint appears only when the Week surface is behind
it, because only Week has a grid to drop onto.

### Two smaller items, carried along

A **New task** action on the project detail header, prefilling the project so the task
lands where the user already is. And the row action icons (pencil, clock, close,
overflow) get their resting contrast raised — at their current opacity they are
discoverable only by hovering, which makes them invisible to anyone who does not
already know they are there.

## Components

### `BeBoosted.Desktop` — `ProjectDetailViewModel`

`OpenTasks`, `ScheduledBlocks`, `RecentlyCompleted` and `CompletedScheduledBlocks`
are replaced by one `Tasks` collection. The rebuild walks the project's tasks, finds
each task's sessions, and produces one row carrying the derived status and the
identity of the session the affix refers to (when there is one).

### `BeBoosted.Desktop` — the project row

One row type replaces `ProjectTaskRowViewModel` and `ScheduledBlockRowViewModel` as
top-level rows. It keeps `TaskId`, gains the affix's `BlockId` and occurrence date
when a session is shown, and exposes the status as a discriminated set the view binds
to rather than a formatted string, so the view decides presentation and the tests can
assert state rather than copy.

**`ScheduledBlockRowViewModel` is not deleted** — it is still used elsewhere — but it
no longer appears as a top-level row on this screen.

### `BeBoosted.Desktop` — `MainWindow.InvokerIdentity`

Focus restoration keys on row view-model types, including a `project-block:` case
that exists because sessions were their own rows. That case must now resolve from the
single row's affix identity instead, or focus silently stops returning after a save
made from this screen. This is the change most likely to regress quietly.

### `BeBoosted.Desktop` — `CalendarBlockView` / `CalendarBlockViewModel`

`ShowCompletionControl` drops its `&& !IsDone` term so the control survives
completion, and the control becomes a checkbox that toggles. The existing flyout
contents move behind a secondary control. `RecordDoneCommand`,
`RecordNeedsMoreTimeCommand`, `RecordDidntHappenCommand` and `UnscheduleCommand` are
unchanged and keep their current behavior.

The chip copy lives in the views, not the view models: `CalendarBlockView.axaml:177`
reads "outcome?" and `DailyTaskListView.axaml:331` reads "Needs outcome". The former
adopts the latter.

### `BeBoosted.Desktop` — `DailyRowViewModel` / `DailyTaskListView`

The secondary outcome control is shown consistently rather than conditionally, so the
two surfaces match. `ShowSessionOutcomeAction`'s current condition becomes the rule
for both.

### `BeBoosted.Desktop` — `MainWindow.axaml`

A scrim border behind the Inbox drawer, visible with it. The footer hint's visibility
binds to whether the Week surface is active.

## Risks

- **Focus restoration regressing silently.** The `project-block:` identity case is
  the specific hazard; a rendered focus test covers it.
- **A task with several sessions hiding the ones not shown.** The affix names the
  next session only. The whole-task editor lists them all, which is why the row opens
  it — but a user who only reads the row sees one of three. Accepted: the alternative
  is the multi-row layout this change exists to remove.
- **Losing a fast path.** Week's one-off flyout currently reaches Done in one click on
  an already-open flyout; it becomes a checkbox (still one click) with the rarer
  outcomes one level deeper. That is the intended trade.
- **Dimming reading as a modal.** The drawer is dismissible and does not trap focus;
  the scrim must not imply otherwise. It stays light.

## Testing

Rendered UI tests, because every one of these is a visual claim:

- A task with two sessions produces **exactly one** row in project detail.
- Each of the four statuses renders its affix, and the priority order holds when a
  task qualifies for more than one.
- Row click opens the whole-task editor; time-affix click opens the session editor for
  the session named.
- Focus returns to the originating row after a save made from project detail,
  including from the time affix.
- A one-off Week session's checkbox is **still present and unchecks** once done.
- The same session reaches Needs more time and Didn't happen from both surfaces.
- The scrim is present exactly when the drawer is open.
- The "drag onto the calendar" hint is absent on Today and present on Week.
- Screenshots at 1280×800 and 1440×960 for the reorganised project detail and both
  completion surfaces, reviewed before the branch closes.

## Known limitations

- The affix shows the next session only; the rest live in the editor.
- Completed tasks accumulate in the list. There is no archive, and a long-running
  project's list will grow — a cap or a fold is deliberately left for when someone
  actually hits it.
- The scrim dims the surface but does not prevent interaction with it, matching the
  drawer's existing dismissible behavior.
