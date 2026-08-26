# Task and session editor scope

Date: 2026-08-20
Status: Approved, ready for implementation planning

## Problem

The unified task editor silently edits one of a task's N sessions (audit
finding F-03). Its data model has exactly one Date/Start/End/Repeats slot
(`TaskEditorViewModel.cs:169-185`) while planning routinely creates several
sessions per task — split work, "Needs more time" reschedules, and repeating
schedules beside one-offs. Task-row entry picks a session the user never sees:

```csharp
public CalendarBlock? GetEditableSessionForTask(TaskId taskId)
{
    var sessions = blocks.GetForTask(taskId); // ordered by date, then start
    return sessions.FirstOrDefault(s => s.Outcome == BlockOutcome.None)
        ?? sessions.LastOrDefault();
}
```

(`CalendarService.cs:451-456`.) The consequences the audit names:

- Unchecking **Scheduled** deletes only the silently selected session
  (`CalendarService.cs:149-151, 311-322`); siblings keep the task on the
  calendar, so the user saves what looks like an unscheduled task and it is
  still scheduled.
- **Delete task** removes the task and *all* sessions behind singular copy —
  "Delete this task? Its scheduled time goes with it."
  (`TaskEditorViewModel.cs:122-124`).
- No session count appears anywhere in `TaskEditorView.axaml`; the only
  "Session 1 of 2" strings in the product are planner-proposal labels that
  are dropped at approval (`PlanningService.cs:158-167`).

The service layer is not the defect: `CalendarService.UpdateTask` already
reasons about the whole aggregate (`siblingOneOffs`,
`repeatingSiblingRemains`, `CalendarService.cs:77-86`) and is well tested.
The defect is UI opacity: one 408px form conflates four scopes behind one
Save — task identity fields, one hidden session's date/time, the entire
repeating series' rule, and a Completed checkbox whose meaning flips between
occurrence and whole-task aggregate.

The interaction direction was selected in brainstorm
`.superpowers/brainstorm/1347-1787291649/content/task-session-structure.html`
(Option 1, "Scope-led views") and is fixed: task rows open the whole task,
calendar blocks open one session, and a structural scope marker makes the
boundary unmistakable.

## Goals

- Every editing surface states its scope structurally and textually before
  the first field: **WHOLE TASK**, **THIS SESSION · X OF N**, or
  **REPEATING SCHEDULE** — never through color alone.
- The whole-task editor shows the complete schedule: every session as a
  legible row, with add/edit/remove per session, **Unschedule all**, and a
  **Delete task** whose confirmation names what actually goes.
- The session editor edits exactly one block, shows its position among its
  siblings, and never exposes task deletion.
- Every removal verb names its real scope; no confirmation copy implies less
  than what happens.
- Cancel is truthful in both editors: cancelling never leaves behind changes
  that appeared to be part of the same editing flow.

## Non-goals

- No one-off exceptions to a repeating schedule (no detached occurrences, no
  per-occurrence time overrides, no series end dates). The recurrence model
  is unchanged.
- No change to completion authority: non-repeating tasks complete as a whole
  (aggregate over one-off siblings, both directions); repeating work
  completes occurrence by occurrence; whole-task completion stays rejected
  while any repeating schedule remains; reopen stays unconditionally allowed
  (`CalendarService.cs:363-442`).
- No new DB columns, migrations, colors, fonts, radii, or shadows. Session
  positions are derived, never persisted.
- No change to quick affordances outside the editors: calendar "Remove from
  calendar", the Daily list's Unschedule "×", the Delete key on a one-off
  block, drag/resize, and the Daily "Change time" flyout keep their current
  behavior.
- `TaskItem.Recurrence` (vestigial, never read) stays untouched.

## Approach

Two scope-led editors replace the single `TaskEditorViewModel`, matching the
selected direction one-to-one:

| Entry point | Editor opened |
| --- | --- |
| Row Edit — every Inbox, Daily, and Projects row, scheduled or not (`OpenTaskEditorForTask`) | Whole-task editor |
| Calendar block click (`OpenTaskEditorForBlock(id, date)`) | Session editor for that block, scoped to the clicked occurrence |
| Row Edit inside the whole-task editor's Schedule section | Session editor for that row's block |
| New task / empty-slot click / Daily add-task | Whole-task editor, create mode |
| Delete key on a repeating block | Session editor, pre-armed on the **Remove schedule** confirmation |

A row in a list surface is task-scoped even when it represents a scheduled
session: Daily session rows and Projects scheduled-session rows open the
whole-task editor like every other row. The session editor is reached only
through a calendar block or through the whole-task editor's Schedule
section, so the rule stays teachable: **lists edit the task; the calendar
and the task's own schedule list edit a session.**

No tabs hide the scope; no single sheet holds everything. Each editor is a
separate view model with its own view, sharing focused submodels — a
schedule-fields group and a session-row model — so each scope's behavior is
understandable and testable without reading the other's internals.

The persistence boundary follows the scope boundary. The whole-task editor's
**Save task** persists task fields (and whole-task completion when allowed)
and nothing else. Schedule mutations — saving a session, adding one, removing
one, removing a repeating schedule, unscheduling all — are explicit
operations that persist at their own moment: removals behind scope-named
confirmations, session saves behind their own Save button.
**The task fields are a draft until Save task; the Schedule section is
live.** A dirty draft and a live operation never interleave: the
save-or-discard gate resolves the draft before any schedule operation runs.
This split is what keeps Cancel truthful: Cancel discards the task draft,
and every schedule change the user made happened behind its own explicit
action — a Save button or a scope-named confirmation — never silently.

## Why a gate and not staged nesting

The unresolved transition was: the user has unsaved whole-task changes and
opens **Edit** on one of its sessions. Three models were evaluated:

1. *Stage nested session edits until Save task.* Fully truthful Cancel, but
   it needs a draft model of the entire schedule list plus a new aggregate
   diff-and-save service method duplicating `UpdateTask`'s validation matrix
   across N sessions — and the same session editor would persist immediately
   when entered from a calendar block but stage when nested. Two persistence
   semantics for one view is F-03's ambiguity relocated into save behavior.
2. *Persist session changes immediately under the open task draft.* No
   interruption, but cancelling the task editor would silently leave behind
   session changes made in what looked like the same flow — the exact
   untruthful Cancel this design exists to remove.
3. *Save-or-discard gate (chosen).* Navigation between scopes is instant
   when the current draft is clean. When it is dirty, an inline prompt (the
   same visual pattern as the existing inline delete confirmation) offers:
   **Save task and continue** / **Discard changes and continue** /
   **Keep editing** — symmetric on the session side. A save-and-continue
   that fails validation stays in the current editor with the error.

The gate's consequence is structural: a mixed-scope draft never exists.
Cancel is truthful in both editors, each editor keeps exactly one
persistence path, and the session editor behaves identically from every
entry point.

## Behavior

### The whole-task editor

Scope marker: a 7×7px lime square (2px radius) followed by the uppercase
micro-label **WHOLE TASK**. The card's structure, not its color, carries the
scope: marker, task fields, COMPLETION, SCHEDULE, actions.

Fields (unchanged semantics): TITLE, PROJECT, DEADLINE, ESTIMATE. The
`Scheduled` checkbox is gone; the Schedule section replaces it.

COMPLETION (edit mode only):

- No repeating schedule on the task: checkbox **Mark whole task complete**,
  staged and saved with the task fields. When the task has two or more
  one-off sessions, a permanent note beneath it: *"Completing or reopening
  applies to all {N} sessions."* — this also supplies the aggregate-undo
  sentence F-17 found missing from the UI.
- Any repeating schedule on the task: no completion control. In its place:
  *"This task repeats — complete each occurrence from the calendar or its
  session view."* For mixed tasks a second sentence follows: *"One-off
  sessions can't be marked Done while a repeating schedule remains."* (the
  UI restating what the service already enforces —
  `RecordOutcomeDone_WithARepeatingSibling_IsRejected_ChangingNothing`).

SCHEDULE lists every `CalendarBlock` of the task, one compact two-line row
each, separated by fine top rules, ordered by date then start time:

- One-off row: primary line "Wed, Aug 26"; secondary line (mono) "9:00–10:00
  AM · Session 2 of 3". Resolved rows append the existing chip vocabulary —
  Done, Needs more time, Didn't happen — and render slightly muted; they
  stay in the list and stay explicitly editable and removable (only the old
  *silent* targeting of history was the defect).
- Repeating row: primary line "Repeats weekly · Mon, Wed"; secondary line
  "9:00–10:00 AM". Repeating rows carry no position number. On a mixed task
  the numbering note renders beneath the rows, so the one-off positions
  never appear to contradict the row count.
- Row actions, right-aligned, quiet (opacity .45 → 1 on hover/focus-within,
  always hit-testable): **Edit** and **Remove**.

Empty state (unscheduled task): *"No sessions scheduled. The task stays in
your Inbox until you add one."* with **Add session** as the primary action.

Section-level actions:

- **Add session** opens the session editor in add mode (gated by the dirty
  prompt like row Edit). On a completed task the button is disabled with the
  hit-testable note *"Task complete — reopen it to schedule more sessions."*
  (the established disabled-control overlay pattern,
  `DailyRowViewModel.cs:305-323`), matching the service rejection.
- **Unschedule all** appears only when the task has two or more schedule
  rows — with one row it would duplicate that row's Remove. Its inline
  confirmation counts one-off sessions and repeating schedules separately
  and discloses that a repeating schedule's occurrence-completion history is
  removed with it (exact variants in Copy), with **Keep** / **Remove all**.

Footer: **Delete task** (destructive, left) · **Cancel** · **Save task**
(primary lime; **Add task** in create mode). Save task persists title,
project, deadline, estimate, and the staged completion — never a session's
date, time, or recurrence. (Aggregate completion still resolves or reopens
one-off siblings' outcomes in the same transaction, exactly as the task-row
checkbox does today.) Cancel and Escape discard the task draft and close (or
return, per navigation below) without persisting anything.

**Delete task** confirms inline with count-aware copy (see Copy) naming the
sessions and schedules that go with it, with **Keep** / **Delete task**.
Deletion stays the whole-task editor's exclusive verb; the session editor
never offers it.

### Create mode (new task)

The whole-task editor in create mode is the New-task editor. The Schedule
section starts at the empty state; **Add session** here reveals the inline
schedule fields (date, start, end, Repeats, weekday chips) plus a quiet
**Remove** link that clears back to the empty state — a nested session
editor is impossible before the task exists, and creation must stay atomic.
Entry points that prefill a slot (empty Week-slot click, Daily "Add
scheduled task") open with the fields already revealed and filled; Daily
"Add unscheduled task" opens at the empty state. **Add task** persists
through the existing atomic `CreateTask(details, schedule?)` — task plus
optional first session in one transaction. Exactly one session can be
created here; more are added after the first save, through the normal
Add-session flow.

### The session editor

Opened from a calendar block (carrying the concrete clicked occurrence
date), from a row in the whole-task editor's Schedule section, or in add
mode from the whole-task editor. A repeating schedule opened from its
Schedule-section row resolves its occurrence by the F-15 rule
(`EditorOccurrenceFor`, `CalendarViewModel.cs:234-252`): today's occurrence
when the series occurs today, otherwise the most recent elapsed one,
otherwise the anchor — and the THIS OCCURRENCE label always names the
resolved date, so the target is never silent. The parent task's title
renders read-only beneath the scope marker for context; task fields are
never editable here.

**One-off session** — scope marker **THIS SESSION · {X} OF {N}** (always
shown, including "THIS SESSION · 1 OF 1": the count is the disclosure that
no hidden siblings exist, which is precisely the trust F-03 broke). On a
mixed task the numbering note — *"Session numbers count one-off sessions
only; the repeating schedule has no number."* — renders beneath the scope
marker, so "2 OF 2" never appears to contradict a Schedule list that also
shows a repeating row. Fields:
DATE, START, END, and the **Repeats** toggle with weekday chips (ticking it
converts the block to a repeating schedule on save, exactly as today's
conversion semantics). A resolved session shows its status line ("Resolved:
Didn't happen · Aug 18"). There is **no completion control**: a one-off's
completion is whole-task authority, and pretending otherwise inside a
session-scoped view would rebuild the old conflation. Completion routes
through the whole-task editor, the calendar, or the Daily list. Actions:
**Remove this session** (destructive), **Edit whole task** (link),
**Cancel**, **Save session**.

**Repeating schedule** — scope marker **REPEATING SCHEDULE**, then two
explicitly scoped sections:

- **THIS OCCURRENCE · {TUE, AUG 25}** — checkbox **Mark this occurrence
  complete**, staged, with the sentence *"Only {Tue, Aug 25}. Other
  occurrences aren't affected."*
- **REPEATING SCHEDULE** — START, END, the **Repeats** toggle, weekday
  chips, and the sentence *"Time and weekday changes apply to every
  occurrence of this schedule."* There is deliberately no anchor-date field
  (see Known limitations); the design never implies a repeating time edit
  touches only one occurrence.

Unticking **Repeats** converts the series to a one-off: the THIS OCCURRENCE
section hides and its staged value is discarded (a conversion never
completes anything), and a DATE field appears prefilled with the opened
occurrence's date. Actions: **Remove schedule** (destructive), **Edit whole
task** (link), **Cancel**, **Save schedule**.

**Add mode** — scope marker **NEW SESSION**, same schedule fields (Repeats
allowed, so a second repeating schedule can be added deliberately), primary
action **Add session**. Cancel returns to the whole-task editor without
creating anything.

Save session / Save schedule persists that one block's schedule and (for a
still-repeating schedule) the staged occurrence completion, atomically. The
existing guards keep their teeth: end must follow start; a completion
requested for an occurrence the edit removes is rejected with *"That
occurrence no longer exists after this change — untick Completed or keep its
weekday."*; removing a completed weekday purges its row; converting a series
to a one-off never promotes occurrence-derived completion to the task;
converting a completed one-off to repeating reopens the task and clears the
outcome.

### Session counts and ordering

Derived, never persisted. The source is `GetSessionsForTask` (repository
order: date, then start time); a pure list-builder in the Desktop layer
assigns positions with a stable final tiebreak of (date, start time,
CreatedAt, Id). **X of N counts one-off blocks only**, 1-based in that
order, resolved history included — so the whole-task list and the session
editor's label always agree, and a position never depends on completion
state. Repeating blocks are rows and editors of their own kind and are
excluded from the numbering; their scope label is REPEATING SCHEDULE, not a
position. Both editors derive positions through the same helper. Wherever
both kinds coexist, the visible numbering note (see Copy) states the rule,
so the positions never appear to contradict the row count.

### Removal and deletion — the four verbs

| Verb | Where | Effect | Confirmation names |
| --- | --- | --- | --- |
| **Remove** (row) / **Remove this session** | Whole-task row · one-off session editor | That one block and its rows; task and siblings stay | The session's date/time and how many sessions the task keeps |
| **Remove schedule** | Whole-task row (repeating) · repeating session editor | The series block and its completion history; task and unrelated sessions stay | The schedule, its occurrence history, the task's survival |
| **Unschedule all** | Whole-task Schedule section (≥2 rows) | Every block of the task; task stays | One-off and repeating counts separately, the repeating completion history, and the task's survival |
| **Delete task** | Whole-task editor only | Task, every block, every completion row, proposal references | The task plus its exact session/schedule counts |

Every confirmation is the established two-step inline pattern. None of the
copy is generic; each names the actual scope (see Copy). The Delete key on a
repeating calendar block now routes to the session editor pre-armed on the
**Remove schedule** confirmation, carrying the rendered occurrence date — a
block-scoped gesture gets a block-scoped removal and the task survives. This
is a deliberate behavior change from today's whole-task delete routing
(`CalendarViewModel.cs:274-278`), which both destroyed more than the gesture
targeted and lost the clicked occurrence.

### The unsaved-changes gate

Each editor tracks dirtiness against the snapshot taken when it opened
(fields compared by value; the staged completion counts). Scope transitions
— whole-task → row Edit, whole-task → Add session, session → Edit whole
task — check the current draft:

- Clean: navigate immediately. This is the common case.
- Dirty: an inline prompt replaces the action bar, in the whole-task editor
  *"You have unsaved task changes."* with **Save task and continue** /
  **Discard changes and continue** / **Keep editing**; in the session editor
  *"You have unsaved session changes."* with **Save session and continue**
  (or **Save schedule and continue**) / **Discard changes and continue** /
  **Keep editing**. Save-and-continue runs the normal save; if it fails
  validation the editor stays put with the error and does not navigate.

The gate precedes **every** immediately persisted schedule operation while
the task draft is dirty — row Edit, Add session, row Remove, Remove
schedule, and Unschedule all alike. A dirty draft and an immediate persist
must never interleave: the draft is resolved first (saved or discarded),
and only then does the operation's own confirmation appear where one
applies (Remove, Remove schedule, Unschedule all). **Keep editing** aborts
the operation entirely. Delete task is the one exception — it needs no
gate, because its confirmation supersedes the draft, which is discarded
with the task.

### Navigation, Escape, and focus

Navigation depth is at most two, and the rule is one sentence: **Escape
steps out one level; from the top level it closes and returns focus to where
you started.**

- Whole-task → session (row Edit, Add session): a push. Save, Cancel, and
  Escape in the session editor return to the whole-task editor with its
  Schedule list refreshed from the service. Focus lands on the Edit button
  of the row that launched the session editor; after Add, on the new row's
  Edit button (the Daily list's `RowFocusRequested` pattern).
- Session (block entry) → **Edit whole task**: a gated promotion. The
  whole-task editor replaces the session editor; there is no return leg.
  Closing the whole-task editor restores focus to the original invoker —
  the calendar block, Daily row, or Projects row that started the flow.
- The invoking control is captured once when the modal opens (generalizing
  today's `_taskEditorReturnFocus`, `MainWindow.axaml.cs:85-104`) and
  survives in-modal transitions.

Initial focus: whole-task editor → TITLE; one-off session editor → DATE;
repeating session editor → START; add mode → DATE. Tab and Shift+Tab wrap
within the modal card — the new editors are proper focus traps, closing
F-23's gap for this surface. Keyboard traversal order:

- Whole-task: Title → Project → Deadline → Estimate → completion checkbox →
  each schedule row's Edit then Remove, in list order → Add session →
  Unschedule all → Delete task → Cancel → Save task.
- Session: schedule fields in visual order → occurrence checkbox (repeating)
  → Repeats toggle → weekday chips → Remove this session / Remove schedule →
  Edit whole task → Cancel → Save.

While a confirmation or gate prompt is active, focus moves to its first
button and Escape dismisses the prompt (one level) rather than the editor.

### Stale sessions and failures

There is still no live concurrency detection while an editor is open; the
existing validate-then-fail-inline contract extends to both editors, with
the id-bearing service messages mapped to plain copy at the view-model
layer:

- Session editor Save/Remove on a block or task that vanished: the inline
  error *"This session no longer exists — it was removed elsewhere. Cancel
  to go back."* The editor stays open; nothing persisted; nothing announced.
- Whole-task row action on a vanished session: the Schedule list refreshes
  and shows *"That session was already removed — the list has been
  updated."* above the section.
- Whole-task Save/Delete on a vanished task: the existing inline behavior
  (`CalendarViewModel.cs:353-359`) is kept.
- Persistence failures cannot leave partial state: every multi-entity
  mutation is one unit of work, and single-row writes are atomic by
  construction. Both editors catch two exception families on their
  save/remove/delete paths: `DomainException` surfaces its own message, and
  `SqliteException` — the mutation layer's expected failure mode (a locked
  or unwritable database) — maps to the generic inline line *"Couldn't save
  — nothing was changed. Try again."*, which the transaction rollback makes
  literally true. Any other exception type still propagates: swallowing
  unknown failures would hide corruption. This closes F-17's mid-save-crash
  gap for the new editors; the row-action surfaces F-17 names outside them
  remain its own remit.

Validation errors always render inside the editor that owns the field —
guaranteed by construction, since task fields and session fields live in
different view models with separate error slots.

### The sixteen states

| # | State | Behavior |
| --- | --- | --- |
| 1 | New unscheduled task | Create mode; Schedule at empty state; Add task creates the task alone |
| 2 | New task with first session | Create mode with inline schedule fields revealed (or prefilled by the entry point); one atomic create |
| 3 | Existing unscheduled task | Whole-task editor; empty state + Add session; completion checkbox available |
| 4 | Whole task, one session | One row "Session 1 of 1"; Edit/Remove; no Unschedule all |
| 5 | Whole task, several sessions | N rows with positions; Unschedule all appears |
| 6 | Whole task with repeating schedule | Series row; no global completion; repeating sentence |
| 7 | Mixed repeating + one-off | Both row kinds in one list; mixed completion sentences; the numbering note |
| 8 | One-off session editor | THIS SESSION · X OF N; date/time; no completion control; Remove this session; Edit whole task |
| 9 | Repeating session editor | THIS OCCURRENCE + REPEATING SCHEDULE sections; occurrence checkbox; Remove schedule |
| 10 | Add-session flow | NEW SESSION scope from the whole-task editor; Repeats allowed; returns with focus on the new row |
| 11 | Remove-session confirmation | Names the session's date/time and what the task keeps |
| 12 | Unschedule-all confirmation | Counts one-offs and repeating schedules separately; discloses completion-history removal; the task stays |
| 13 | Delete-task confirmation | Names the exact session/schedule counts |
| 14 | Completed non-repeating task | Checkbox checked + aggregate note; rows show Done chips; Add session disabled with note |
| 15 | Stale / concurrently removed session | Inline stale copy; list refresh; nothing persists |
| 16 | Validation / persistence failure | Inline error in the owning editor; the modal never closes on failure |

## Components

### `WholeTaskEditorViewModel` (new)

Task fields (Title, SelectedProject, Deadline, DurationMinutes), staged
`IsCompleted` with `CompletionNote`/gating per Behavior, the Schedule list
(`ObservableCollection<SessionRowViewModel>`), `Error`, dirty tracking,
inline-prompt state (delete confirmation, unschedule-all confirmation, the
gate), create-mode inline schedule fields (a `ScheduleFieldsViewModel`), and
commands: Save, Cancel, RequestDelete/CancelDelete/ConfirmDelete,
AddSession, RequestUnscheduleAll/ConfirmUnscheduleAll, and per-row Edit /
Remove relays. Rebuilds its Schedule list from the service after every
schedule mutation and every return from a nested session editor.

### `SessionEditorViewModel` (new)

Mode (one-off / repeating / add), the block id + task id + opened occurrence
date, a `ScheduleFieldsViewModel`, staged `IsOccurrenceCompleted`
(repeating), position label inputs (X, N), read-only task title, `Error`,
dirty tracking, remove-confirmation and gate state, and commands: Save,
Cancel, RequestRemove/ConfirmRemove, EditWholeTask. Identical behavior from
every entry point.

### `ScheduleFieldsViewModel` (new, shared)

Date, Start, End, RepeatsWeekly, the seven `DayToggleViewModel`s, and the
"weekdays default to the date's weekday when none ticked" rule currently in
`SaveTaskEditor` (`CalendarViewModel.cs:305-309`). Used by the session
editor and the whole-task create mode.

### `SessionRowViewModel` (new, shared)

Date/time text, series summary, status chip, position text, accessible
names, Edit/Remove commands. Built by a pure `SessionListBuilder` helper
(ordering + positions), which both editors and its unit tests share.

### `CalendarViewModel`

`TaskEditor` becomes `ActiveTaskEditor` (object?; the scrim binds its
non-nullness). Entry points keep their names: `OpenTaskEditorForTask`
constructs the whole-task editor (no more `GetEditableSessionForTask` call);
`OpenTaskEditorForBlock(id, date)` constructs the session editor, its
callers narrowing to the calendar block paths and the Delete-key route —
the Daily and Projects row events (`DailyListViewModel.EditRow`,
`Projects.SessionEditRequested`) rewire to `OpenTaskEditorForTask`, because
a list row is task-scoped even when it shows a session;
`OpenNewTaskEditor*` construct create mode. `EditorOccurrenceFor`
(`CalendarViewModel.cs:234-252`) survives as the occurrence resolver when a
repeating Schedule-section row opens the session editor. New internal persistence
callbacks: `SaveWholeTask`, `SaveSession`, `AddSessionFromEditor`,
`RemoveSessionFromEditor`, `RemoveScheduleFromEditor`,
`UnscheduleAllFromEditor`, plus navigation (`OpenSessionFromWholeTask`,
`PromoteToWholeTask`, `ReturnToWholeTask`) holding the `EditorNavigation`
record {invoker focus target, parent editor, return-row id}. Every
successful mutation reloads and announces `DataChanged` exactly once, as
today. `RequestDeleteBlock` becomes the repeating Delete-key route into the
session editor's Remove-schedule confirmation, carrying the rendered
occurrence date.

### Views

`WholeTaskEditorView.axaml` and `SessionEditorView.axaml` replace
`TaskEditorView.axaml`; the MainWindow scrim hosts them through
DataTemplates. The scrim keeps the name `TaskModalScrim`; the cards are
`WholeTaskEditorCard` and `SessionEditorCard`; carried-over field names
(`TaskTitleBox`, `TaskProjectSelector`, `TaskDeadlinePicker`,
`TaskEstimateBox`) stay stable for tests. Scope markers, section labels, and
rows reuse `Tokens.axaml` exclusively — paper card, graphite text, pencil
metadata, lime scope square, fine rules, modest radii, 11px metadata floor,
IBM Plex Mono for times and positions.

### `CalendarService` (Application)

New methods, all keeping validation-before-mutation and living on
`CalendarService` so the `CompletionApiSurfaceTests` reflection guard stays
green:

- `UpdateTaskDetails(taskId, details, completion?)` — task fields plus
  aggregate completion via the existing `ApplyAggregateCompletion`
  (`CalendarService.cs:407-442`); completion=true rejected while any
  repeating schedule exists; may resolve or reopen one-off siblings'
  *outcomes* through the aggregate rules but never changes any session's
  date, time, or recurrence. One `mutations.Execute`.
- `UpdateSessionSchedule(taskId, sessionId, schedule, occurrenceCompletion?)`
  — Reschedule + SetRecurrence + `RemoveObsoleteCompletions` + the
  occurrence-row upsert, inheriting `UpdateTask`'s guards and conversion
  reconciliation; never touches task detail fields. One `mutations.Execute`.
- `AddSession(taskId, schedule)` — a new block for an existing task,
  recurrence allowed (unlike `ScheduleTask`); rejects a completed task with
  the existing message. Single-row insert.
- `UnscheduleAllSessions(taskId)` — `RemoveSession` over every block of the
  task in one `mutations.Execute`; the task survives.

Reused as-is: `CreateTask` (atomic create + first session), `DeleteTask`,
`UnscheduleSession` (already one `mutations.Execute`,
`CalendarService.cs:503-513`), `SetOccurrenceCompletion`, `RecordOutcome`.
Retired with the old editor: `UpdateTask` and `GetEditableSessionForTask`
(the combined-save and silent-selection paths have no caller left); their
shared validation/reconciliation internals move into private helpers used by
the two new update methods.

## Data flow

Whole-task editing: entry → snapshot task + `GetSessionsForTask` →
`SessionListBuilder` rows → user edits task fields (draft) and/or fires
schedule operations (each: gate when the draft is dirty → confirm → service
call → reload + `DataChanged` once → rebuild rows) → Save task →
`UpdateTaskDetails` → close or error.

Session editing: entry (block id + occurrence date) → snapshot block +
position from `SessionListBuilder` → user edits fields / staged occurrence
completion → Save → `UpdateSessionSchedule` → reload + `DataChanged` once →
return to parent whole-task editor (push case) or close to invoker (block
case) — or error inline.

Gate: every schedule operation and scope navigation first checks the
draft → prompt when dirty → Save-and-continue reuses the normal save path
(announcing once) and Discard drops the draft, either then proceeding to
the operation's own confirmation or navigation; Keep editing dismisses the
prompt and aborts the operation.

## Error handling

- All schedule/task validation stays in `CalendarService` and surfaces as
  `DomainException` → the owning editor's inline `Error` (existing
  contract). The view-model layer maps the two id-bearing stale messages to
  the plain-language copy in this spec.
- The whole-task editor additionally refreshes its Schedule list when a row
  action hits a stale session, so the list never keeps showing a block the
  service just said is gone.
- No mutation announces on failure; no editor closes on failure; transactions
  and single-row writes guarantee nothing partial persists.
- Expected persistence failures (`SqliteException` out of the mutation
  layer) map to the generic failure line; unknown exception types propagate
  rather than being swallowed (see Stale sessions and failures).

## Copy

Scope labels (literal uppercase strings, lime square prefix):
"WHOLE TASK" · "THIS SESSION · {X} OF {N}" · "REPEATING SCHEDULE" ·
"NEW SESSION".

Section labels: "SCHEDULE" · "COMPLETION" ·
"THIS OCCURRENCE · {TUE, AUG 25}" · "REPEATING SCHEDULE".

Buttons and links: "Add session" · "Edit" · "Remove" (rows) ·
"Edit whole task" · "Save task" · "Add task" (create) · "Save session" ·
"Save schedule" · "Cancel" · "Keep" · "Remove all" ·
"Remove session" · "Remove schedule" · "Unschedule all" · "Delete task".

Checkboxes: "Mark whole task complete" · "Mark this occurrence complete" ·
"Repeats".

Schedule empty state: "No sessions scheduled. The task stays in your Inbox
until you add one."

Completion notes: aggregate (N≥2 one-off sessions): "Completing or reopening
applies to all {N} sessions." · repeating: "This task repeats — complete
each occurrence from the calendar or its session view." · mixed adds:
"One-off sessions can't be marked Done while a repeating schedule remains."
· occurrence: "Only {Tue, Aug 25}. Other occurrences aren't affected." ·
series scope: "Time and weekday changes apply to every occurrence of this
schedule." · completed task: "Task complete — reopen it to schedule more
sessions."

Resolved-session status line (session editor):
"Resolved: {Done | Needs more time | Didn't happen} · {Aug 18}".

Mixed-task numbering note (whole-task Schedule section and one-off session
editor): "Session numbers count one-off sessions only; the repeating
schedule has no number." (more than one repeating schedule: "…; repeating
schedules have no number.")

Gate: "You have unsaved task changes." / "You have unsaved session changes."
with "Save task and continue" / "Save session and continue" / "Save schedule
and continue" · "Discard changes and continue" · "Keep editing".

Remove-session confirmation: "Remove this session — {Wed, Aug 26 ·
9:00–10:00 AM}? The task keeps its other {N−1} sessions." — exactly one
other session (amended 2026-08-21): "The task keeps its other 1 session."
— last session: "Remove this session — {Wed, Aug 26 · 9:00–10:00 AM}? The
task stays, unscheduled." Buttons "Keep" / "Remove session".

Remove-schedule confirmation: "Remove the repeating schedule? Every
occurrence and its completion history go with it. The task stays." Buttons
"Keep" / "Remove schedule".

Unschedule-all confirmation (buttons "Keep" / "Remove all"):

| Task's schedule | Copy |
| --- | --- |
| One-offs only | "Remove all {N} sessions? The task itself stays." |
| Repeating only | "Remove {R} repeating schedules? Their completion history goes with them. The task stays." |
| Mixed | "Remove {N} one-off sessions and the repeating schedule? The schedule's completion history goes with it. The task stays." |

The repeating-only row is always plural: Unschedule all needs two or more
rows, so a repeating-only task showing it has R ≥ 2. In the mixed row, more
than one repeating schedule pluralizes to "{R} repeating schedules" /
"their completion history goes with them", and a single one-off reads "the
one-off session".

Delete-task confirmation (buttons "Keep" / "Delete task"):

| Task's schedule | Copy |
| --- | --- |
| Nothing scheduled | "Delete this task?" |
| One session | "Delete this task? Its session goes with it." |
| N one-off sessions | "Delete this task? Its {N} sessions go with it." |
| Repeating only | "Delete this task? Its repeating schedule and completed occurrences go with it." |
| Mixed | "Delete this task? Its repeating schedule and {N} one-off sessions go with it." |

Should a task carry more than one repeating schedule, "its repeating
schedule" pluralizes to "its {R} repeating schedules" in both rows above.
In the mixed row a single one-off reads singular (amended 2026-08-21):
"Delete this task? Its repeating schedule and 1 one-off session go with
it."

Stale: "This session no longer exists — it was removed elsewhere. Cancel to
go back." · "That session was already removed — the list has been updated."

Validation (service copy, unchanged): "A task needs a title." · "A block
must end after it starts." · "Pick a date, start, and end." · "That
occurrence no longer exists after this change — untick Completed or keep its
weekday." · "That task is already complete — reopen it before scheduling
more work."

Save failure: "Couldn't save — nothing was changed. Try again."

## Accessibility and responsive behavior

- Card automation names announce the scope: "Whole task — {title}" ·
  "Session {X} of {N} — {title}" · "Repeating schedule — {title}" ·
  "New session — {title}".
- Session-row automation names spell everything in words, no "·" glyphs:
  "Session {X} of {N} — {Wednesday, August 26}, {9:00 AM} to {10:00 AM}",
  appending ", done" / ", needs more time" / ", didn't happen" when
  resolved; row buttons are "Edit session {X} of {N}" and "Remove session
  {X} of {N}"; the series row is "Repeating schedule — {Monday, Wednesday},
  {9:00 AM} to {10:00 AM}".
- Scope is never color-alone: the uppercase label and the card's structure
  carry it; the lime square is reinforcement. Focus states use the existing
  graphite + lime tokens and are always visible.
- Both editors are focus traps (Tab wraps); Escape semantics and focus
  restoration are exactly the Navigation section's rules, down to the
  specific row button or invoking control.
- Card widths: the whole-task editor is 480px wide; the one-off session,
  repeating-schedule, and new-session editors are 408px wide. Maximum height
  is the window height minus margins. The scope header and the action footer
  are fixed; only the body scrolls, vertically — no editor scrolls
  horizontally. At the 1100×720 minimum the header and footer remain visible
  and long date/time content wraps (the existing minimum-window screenshot
  gate).
- Long titles trim with a tooltip in the session editor's context line; long
  dates and times wrap onto the row's second line rather than widening it.
- No new motion; reduced-motion preferences unaffected.

## Testing

New coverage, in house style (sentence-named xunit.v3 facts; in-memory
doubles for Desktop, `TempDatabase` SQLite for Application; never the live
profile):

`BeBoosted.Desktop.Tests/ViewModels/WholeTaskEditorViewModelTests`:
1. Task-row entry builds the whole-task editor with every session listed in
   (date, start) order and correct positions.
2. Save task persists fields and staged completion, announces exactly once,
   and never changes any session's date, time, or recurrence — aggregate
   completion may still resolve or reopen one-off siblings' outcomes.
3. Completion checkbox absent while any repeating schedule exists; mixed and
   repeating sentences render; aggregate note appears at N≥2.
4. Remove (row) removes one block and keeps siblings; last-session copy
   variant; Unschedule all removes every block and keeps the task, its
   confirmation counting one-offs and repeating schedules separately and
   disclosing the completion-history removal; both confirm first; list
   rebuilds after each.
5. Delete-task confirmation copy matches the schedule shape (all five
   variants); confirm deletes everything and announces once.
6. Gate: a clean draft navigates or operates instantly; a dirty draft gates
   every schedule operation — row Edit, Add session, row Remove, Remove
   schedule, Unschedule all — before that operation's own confirmation;
   Save-and-continue that fails validation stays with the error and aborts
   the operation; Discard drops the draft; Keep editing aborts the
   operation; Cancel never persists anything (fields or completion).
7. Stale row action shows the stale line and refreshes the list without
   persisting or announcing; a `SqliteException` during save surfaces the
   generic failure line with everything rolled back and the editor open.
8. Create mode: Add task with revealed fields creates task + session
   atomically; without them creates the task alone; prefilled entry points
   arrive revealed.
9. Completed task: Add session disabled with the note; rows show Done chips.

`BeBoosted.Desktop.Tests/ViewModels/SessionEditorViewModelTests`:
10. Calendar-block entry builds the session editor scoped to the clicked
    occurrence; Schedule-row entry resolves a repeating occurrence by the
    F-15 rule; label "THIS SESSION · {X} OF {N}" (including 1 of 1, plus
    the numbering note on mixed tasks) or "REPEATING SCHEDULE".
11. One-off editor exposes no completion control; repeating editor stages
    the occurrence checkbox and saves it atomically with the schedule.
12. Repeats conversions both directions preserve service reconciliation
    (reopen/clear on one-off→repeating; no promotion on repeating→one-off;
    the occurrence section hides and its staged value is dropped).
13. Remove this session keeps the task and siblings; Remove schedule keeps
    the task and unrelated sessions; both confirm with the specified copy.
14. Edit whole task gates on a dirty session draft; promotion never returns
    to the session editor.
15. Stale save shows the mapped copy; Cancel/Escape never persist.

`BeBoosted.Desktop.Tests/ViewModels/EditorScopeSelectionTests`:
16. Every list row — Inbox, Daily (session rows included), and Projects
    (scheduled-session rows included) — opens the whole-task editor; only
    calendar blocks and Schedule-section rows open the session editor; the
    repeating Delete key opens the session editor pre-armed on the
    Remove-schedule confirmation with the rendered occurrence.

`BeBoosted.Tests/Calendar` (real SQLite):
17. `UpdateTaskDetails` persists fields + aggregate completion, rejects
    completion under a repeating schedule, and rolls back whole on failure
    (extends `CalendarMutationAtomicityTests`).
18. `UpdateSessionSchedule` covers reschedule, recurrence change, weekday
    purge, conversion matrix, occurrence upsert, and rollback.
19. `AddSession` rejects completed tasks and allows recurrence;
    `UnscheduleAllSessions` removes all blocks + completion rows in one
    transaction and keeps the task.
20. `CompletionApiSurfaceTests`, `TaskCompletionAuthorityTests`,
    `SessionRecurrenceReconciliationTests`, `NoLegacyCommitmentPathTests`
    stay green unchanged.

`BeBoosted.Desktop.Tests/Ui` (`[AvaloniaFact]`):
21. Rendered scope labels and session-row counts for states 4–9; automation
    names as specified.
22. Focus: initial per editor; restore to the launching row after a nested
    save/cancel; restore to the invoking block/row after close; Escape steps
    out one level per depth; Tab wraps within the card.
23. 1100×720 renders both editors without clipping or horizontal scroll;
    screenshot captures `whole-task-editor-*`, `session-editor-oneoff-*`,
    `session-editor-repeating-*` at 1440×960, 1280×800, 1100×720
    (`task-editor-*` capture names retire).

Deliberately rewritten or retired with the old editor:

- `TaskEditorViewModelTests` (26 tests) — re-homed across the three new VM
  test classes; the completion-scope save cases move to the
  `UpdateSessionSchedule`/`UpdateTaskDetails` service tests.
- `TaskEditorModalTests` — rewritten for the new card/control names and the
  focus-trap requirement.
- `TaskSessionSelectionTests` — `GetEditableSessionForTask` and the
  implicit-session `UpdateTask` paths retire; the
  "reschedules the pending session after an outcome" behaviors are
  superseded by explicit per-session editing.
- `SessionRecurrenceReconciliationTests.RepeatingToOneOff_ARequestedCompletionCompletesTheTask_Freshly`
  — superseded: a conversion discards the staged occurrence value and never
  completes the task (the one-off editor has no completion control).
- `CalendarBlockCapabilityTests.DeleteDispatch_RoutesRepeatingTasksThroughConfirmation`
  — rewritten for the schedule-scoped Delete-key routing.
- `ProjectEntryPointTests.ScheduledSessionRow_EditButton_OpensTheEditor_ScopedToTheOccurrence`
  — updated to assert the whole-task editor opens (a list row is
  task-scoped even when it shows a session).

## Known limitations

- The series anchor date is no longer editable anywhere in the UI. Editing
  it in the old editor silently rebased the series' `OccursOn` baseline — a
  footgun with no honest one-field presentation. Moving a series is Remove
  schedule + Add session with Repeats. If demand appears, a deliberate
  "Starts on" field with explicit rebasing copy can be designed later.
- The schedule fields still write only `RecurrenceRule.Weekly(1, days)`; a
  Daily or interval>1 rule loaded from persistence would flatten on save
  (pre-existing; no UI writes such rules today).
- The one-off session editor drops the old block-click completion checkbox.
  Completion for one-offs lives with the task (whole-task editor, calendar
  outcome controls, Daily toggle) — one authority, honestly scoped.
- Task fields in the whole-task editor and schedule rows have different
  persistence timing (draft vs. immediate-confirmed). The confirmations name
  their scope and the gate keeps the two from ever interleaving with a dirty
  draft, so nothing is silent — but the asymmetry is real and is the price
  of truthful Cancel without staged-aggregate complexity.
- No live refresh while an editor is open: background mutations (chat/AI)
  still surface only as stale errors on the next action, as today.
- The new editors catch `DomainException` and `SqliteException` only;
  unknown exception types propagate by design. F-17's remaining surfaces
  (row actions outside the editors) keep their current behavior.
- Quick unschedule affordances outside the editors keep their unconfirmed
  immediate behavior; reconciling them with the editor verbs is future work.
- Unscheduling an approved planning session still leaves its Approved
  proposal block unreconciled outside `DeleteTask` (F-20) — unchanged here;
  `UnscheduleAllSessions` shares `UnscheduleSession`'s existing behavior.

## Approved visual handoff

Approved 2026-08-21. Claude Design project:
<https://claude.ai/design/p/fd7f9935-dc5e-4a27-9ca9-72361a41bccf?file=BeBoosted+Remaster.dc.html>
Overview image:
[assets/2026-08-20-task-session-editor-scope/f03-approved-state-overview.png](assets/2026-08-20-task-session-editor-scope/f03-approved-state-overview.png)

Frame authority — these frames, and only these, are authoritative for this
feature's layout, hierarchy, scope markers, schedule-row presentation, and
interaction/focus/disabled/error/confirmation treatments:

- **3a** Whole-task editor (locked master)
- **3b** One-off session editor (locked master)
- **4a–4p** Complete state handoff (states, confirmations, gates, stale,
  failure, minimum window, accessibility proof)

The design project's unrelated full-app frames (calendar, Daily, Projects,
Inbox, Files, planning, settings, navigation, remaster concepts) are
explicitly non-authoritative and out of scope. The project's HTML and
`support.js` are reference artifacts only and must not be ported into the
Avalonia application.

Binding visual rules from the handoff:

- The whole-task editor card is **480px** wide; the one-off session,
  repeating-schedule, and new-session cards are **408px** wide.
- Each card has a fixed scope-marker header and a fixed action footer; only
  the body between them scrolls, vertically. No editor ever scrolls
  horizontally.
- Exact-copy authority remains with this specification's Copy section.
- Keyboard focus renders as a 2px graphite outline plus a lime halo — never
  color alone.
- Every icon control has a minimum 32×32 hit area.
