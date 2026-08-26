# Incremental Priority Sort and per-task re-rank

Date: 2026-08-17
Status: Approved, ready for implementation planning

## Problem

Running Priority Sort a second time in the same period re-asks everything.
`ShellViewModel.StartPrioritySort` hands every open Inbox task to
`PrioritySortService.StartSession`, and `ComparisonSession` always seeds itself with
`_groups = [[candidates[0]]]` before inserting each candidate in turn. The ranking
saved by the previous sort is read for display — the `#3` chip in the Inbox, the
P1/P2/P3 marker on Daily rows — but never fed back into a new session.

There is also no way to move a single task. Changing one task's position means
redoing the whole sort.

## Goals

- A re-sort asks only about tasks that have no rank in the current period.
- Any ranked task can be re-placed on its own, through a short comparison run.
- The existing order survives both operations untouched except where the user's
  answers move something.

## Non-goals

- No change to the comparison mechanic itself. `ComparisonSession` is already
  Beli-style binary insertion with real ties; only its starting state changes.
- No change to tiers, to dense ranking, or to the persisted rank format.
- No cross-period behavior. Ranks stay period-scoped, so a new day still gets a
  full first sort.
- No tier prefilter before comparisons.

## Approach

One mechanic, three uses. Give `ComparisonSession` an explicit seed ordering
alongside its insert list, and every flow becomes the same object:

| Flow | Seed ordering | Tasks to insert |
| --- | --- | --- |
| First sort (no ranks yet) | empty | every live task |
| Re-sort | saved ranking | live tasks with no rank |
| Re-rank one task | saved ranking minus the target | just the target |

No new session type. `PrioritySortService.Complete` already appends decisions and
calls `ReplaceRanks`; because a seeded session's `BuildRanking()` covers the whole
ordering, that replace stays correct with no change.

## Domain: `ComparisonSession`

A new three-argument constructor, with the existing two-argument form delegating to
it so present callers and tests are unaffected:

```csharp
public ComparisonSession(PlanningPeriod period, IEnumerable<TaskId> candidates)
    : this(period, [], candidates) { }

public ComparisonSession(
    PlanningPeriod period,
    IEnumerable<IReadOnlyList<TaskId>> seedOrder,
    IEnumerable<TaskId> candidates)
```

`seedOrder` is a list of tied groups in rank order, which is what preserves ties
across a re-sort.

Rules:

- When the seed is non-empty, `Replay` starts from a copy of it with
  `_nextCandidateIndex = 0`. When empty, it keeps today's bootstrap: the first
  candidate becomes the sole starting group.
- Candidates already present in the seed are ignored — the seed is the authority
  on where they sit.
- Construction is rejected only when seed and candidates are *both* empty. A
  non-empty seed with nothing to insert is legal and completes immediately, its
  ranking equal to the seed. The existing `EmptyCandidates_AreRejected` case
  (empty seed, empty candidates) still throws.
- `Replay`, `Apply`, `AdvanceToNextQuestion`, `BuildRanking`, `Progress`, and undo
  are otherwise unchanged. Undo remains replay-from-answers, so it stays correct
  against a seed for free.

## Application: `PrioritySortService`

Two factory methods. Both take `liveTasks` — every task eligible to hold a rank
this period — because the service has no task repository and cannot decide
liveness itself.

```csharp
/// <summary>
/// A sort that only asks about work not yet ranked. The saved ordering seeds the
/// session. With no saved ranks this degrades exactly to a full first sort.
/// </summary>
public ComparisonSession StartIncrementalSession(
    PlanningPeriod period, IReadOnlyList<TaskId> liveTasks)

/// <summary>Re-places one already-ranked task among the others.</summary>
public ComparisonSession StartRerankSession(
    PlanningPeriod period, TaskId target, IReadOnlyList<TaskId> liveTasks)
```

Both build the seed the same way: read `GetRanks(period.Key)`, keep only ranks
whose task is in `liveTasks`, group by `Rank`, order by `Rank`. `StartRerankSession`
additionally drops the target from the seed. Insert lists are `liveTasks` minus
ranked ids, and `[target]` respectively.

`StartSession` stays as it is for the plain full-sort case and existing tests.

## What counts as live

Every open task — Inbox and already-scheduled alike. Deleted and completed tasks
are pruned from the seed.

Keeping scheduled-but-open tasks as anchors is deliberate. They are still real work
this period, the Daily list already shows their rank, and "is this new task more
important than the thing already on my calendar?" is a fair question to be asked.

This also repairs an existing defect: today a re-sort passes only `Inbox.Tasks` and
then *replaces* every rank for the period, so anything already scheduled silently
loses its rank. Seeding from the saved order and passing the full live set keeps it.

`ShellViewModel` has no task repository, so `CalendarViewModel` exposes its open
tasks:

```csharp
internal IReadOnlyList<TaskItem> OpenTasks => _tasks.GetOpen();
```

`PrioritySortViewModel` receives the full live set (not just the tasks being
placed), because seed anchors appear as comparison cards and need titles.

## What counts as new

An open task with no rank in the current period.

When nothing is new, `StartPrioritySortCommand` reports `CanExecute == false` and
the button disables rather than opening an empty sort. `CanStartPrioritySort`
becomes: at least one unranked live task, or — when the period has no ranks at all
— at least two live tasks.

## The re-rank pathway

The Inbox `#3` chip (`InboxDrawerView.axaml`) and the Daily row's priority marker
(`DailyTaskListView.axaml`) become buttons, visible only when the row carries a
rank. Both open the same full-screen comparison surface via the shell, seeded with
the saved order minus that task and inserting just that task.

For a ten-task list this is three to four questions.

Wiring: `TaskRowViewModel` and `DailyRowViewModel` each gain a `RerankCommand` that
invokes a callback supplied at construction, the same pattern `onEditRequested`
already uses. `InboxViewModel` and `DailyListViewModel` forward it to the shell,
which owns `ActiveSort`. Re-ranking the only ranked task leaves an empty seed, so
the session completes immediately at rank 1 — correct, and no special case.

`PrioritySortViewModel` is reused whole, in a re-rank mode that changes three
things:

- Heading: "Where does this belong now?" instead of the period prompt.
- Status line: `Re-rank · Today · Comparison 2`.
- "Build my plan now" is hidden.

It ends on the same tier-grouped results screen.

## Abandoning a re-rank changes nothing

`BuildRanking()` puts every unplaced task into one trailing group. That is right for
a genuinely unplaced task — at the moment an insertion begins, `_low` is 0 with no
comparisons recorded, so treating the bisection bounds as an estimate would rank an
uncompared task first.

So a re-rank has no partial-save path. Closing it, or pressing Escape, abandons the
run and leaves the saved ranking exactly as it was. Only a completed insertion
calls `Complete()`. `BuildRanking()` is unchanged and
`BuildPlanNow_RanksUnplacedTasksAtTheTrailingSharedOrdinal` keeps passing.

The incremental sort keeps its "Build my plan now" exit, where unplaced *new* tasks
trailing is the correct meaning.

## Testing

**Domain (`ComparisonSessionTests`)**
- A seeded session with an empty insert list asks nothing and ranks exactly the seed.
- Inserting into a seeded order preserves the existing relative order.
- Tied groups in the seed survive as shared ordinals.
- A candidate already present in the seed is ignored.
- Seed and candidates both empty is still rejected; a non-empty seed with no
  candidates is not.
- Undo works against a seeded session.
- Re-rank moves a task up, and down, leaving every other task's relative order fixed.

**Application / SQLite**
- `StartIncrementalSession` with no saved ranks behaves exactly like a full sort.
- A re-sort preserves untouched ranks after reopening the database.
- A scheduled-but-open task keeps its rank through a re-sort.

**View models**
- A second sort offers only the newly captured task.
- `StartPrioritySortCommand` disables when nothing is new.
- The rank chip opens a re-rank seeded with the saved order minus that task.
- Abandoning a re-rank leaves the saved ranking untouched.
- A completed re-rank writes the new position and refreshes the Inbox chips.

## Known limitations

- Ranks remain period-scoped. Moving to a new day still means a full first sort;
  nothing carries yesterday's ordering forward.
- The re-rank seed is the saved ranking, so a task ranked before some newer task
  was captured can be compared only against work that already holds a rank.
- Completing a re-rank replaces the period's ranks wholesale, as `Complete()`
  already does. Concurrent edits from another surface between opening and
  finishing a re-rank would be overwritten.
