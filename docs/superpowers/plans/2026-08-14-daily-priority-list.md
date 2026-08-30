# Daily Priority-First Task List Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hourly timeline in the calendar's **Today** view with the priority-first Daily task list from Claude Design frame `#2a`, while Week view keeps the existing seven-day timeline.

**Architecture:** `CalendarViewModel` gains a child `DailyListViewModel` that rebuilds three sections (Scheduled / Unscheduled / Completed) from the same data `Reload()` already gathers, plus Inbox tasks, per-visible-date priority ranks, and completed tasks. A new `DailyTaskListView.axaml` renders it when `IsTodayView`; `TimelineSurfaceView` stays and renders only when `IsWeekView`. All mutations route through existing services and end in exactly one `Reload()` + `DataChanged` announcement.

**Tech Stack:** Avalonia 12 (headless XUnit for UI tests), .NET 10, CommunityToolkit.Mvvm partial observable properties, xunit.v3.

**Spec:** the user task description (conversation) + `docs/design/daily-redesign-frame-2a.html` (archived frame `#2a` mockup). Design tokens: `src/BeBoosted.Desktop/Styles/Tokens.axaml`, `Typography.axaml`.

## Global Constraints

- Branch: `feature/daily-priority-list`; prior worktree changes are already committed (`e99bcde`) — never reset/revert them.
- No new DB columns or migrations. No new colors/fonts/radii/shadows — reuse `Tokens.axaml` ramp.
- 11 px metadata floor (`TextSizeMeta`), IBM Plex Mono for time/rank/metadata (`TextBlock.meta` / `mono` classes).
- Centered list surface, max width ~880 px, fluid margins; content scrolls vertically only; top bar + composer fixed.
- Window targets: 1440×960, 1280×800 screenshots; 1100×720 minimum without clipping or horizontal scroll.
- Do NOT ship the mockup's six-dot drag grips; rows are auto-sorted (no manual priority).
- Fixed commitments never get fabricated P values. Unranked tasks show a dash marker, tooltip "Not ranked for this day."
- `PlanningTier` → labels: ProtectNow→"P1" ("Priority 1 — Protect now"), AdvanceNext→"P2" ("Priority 2 — Advance next"), CanWait→"P3" ("Priority 3 — Can wait").
- Priority lookups follow the visible period: Today `PlanningPeriod.ForToday(Calendar.VisibleDate)`, Week `ForWeek(Calendar.VisibleDate)` — not `_clock.Today`.
- Every mutation refreshes Inbox, Projects, calendar, counts, progress exactly once.
- No task may appear in Scheduled and Unscheduled simultaneously (subtract all pending-proposal TaskIds from Unscheduled while a draft is active).
- Copy (verbatim): "Nothing scheduled for this day." / "No unscheduled tasks." / "Everything for this day is complete." / "X of Y complete" / "Today's tasks" / "Tasks for {weekday}" / "Not ranked for this day."

---

### Task 1: Baseline + reference commit

**Files:**
- Create: `docs/design/daily-redesign-frame-2a.html` (already extracted)
- Create: `docs/superpowers/plans/2026-08-14-daily-priority-list.md` (this file)

- [x] **Step 1:** Confirm `dotnet build` + `dotnet test` pass on the branch before changes (background task `b0mj7hylr`).
- [x] **Step 2:** Commit the design reference + plan: `git add docs && git commit -m "docs: archive daily redesign frame 2a and implementation plan"`

### Task 2: `CalendarService.ScheduleTask` duration overload

The schedule flyout needs a caller-chosen duration; the existing 3-arg overload uses the task's estimate.

**Files:**
- Modify: `src/BeBoosted.Application/Calendar/CalendarService.cs:209-217`
- Test: `tests/BeBoosted.Tests/Calendar/CalendarServiceTests.cs`

**Interfaces:**
- Produces: `CalendarBlock ScheduleTask(TaskId taskId, DateOnly date, TimeOnly startTime, TimeSpan duration)` — throws `DomainException` for a missing task or non-positive duration; clamps the end to 23:59 via existing `ClampEnd`.

- [ ] **Step 1: Failing tests** in `CalendarServiceTests.cs`:

```csharp
[Fact]
public void ScheduleTask_WithExplicitDuration_UsesItInsteadOfTheEstimate()
{
    // seed a task with a 90-minute estimate, schedule with 45 minutes
    // assert block.EndTime == start.AddMinutes(45)
}

[Fact]
public void ScheduleTask_WithNonPositiveDuration_Throws()
{
    // Assert.Throws<DomainException>(...) for TimeSpan.Zero
}
```

- [ ] **Step 2:** Run: `dotnet test tests/BeBoosted.Tests --filter CalendarServiceTests` → new tests FAIL (no overload).
- [ ] **Step 3: Implement** — replace the existing `ScheduleTask` body:

```csharp
public CalendarBlock ScheduleTask(TaskId taskId, DateOnly date, TimeOnly startTime)
{
    var task = tasks.GetById(taskId) ?? throw new DomainException($"Task {taskId} no longer exists.");
    return ScheduleTask(taskId, date, startTime, task.EstimatedDuration ?? DefaultTaskBlockDuration);
}

/// <summary>Schedules a task with an explicit duration (manual Schedule flyout).</summary>
public CalendarBlock ScheduleTask(TaskId taskId, DateOnly date, TimeOnly startTime, TimeSpan duration)
{
    _ = tasks.GetById(taskId) ?? throw new DomainException($"Task {taskId} no longer exists.");
    if (duration <= TimeSpan.Zero)
    {
        throw new DomainException("A block needs a positive duration.");
    }

    var endTime = ClampEnd(startTime, duration);
    var block = CalendarBlock.CreateForTask(taskId, date, startTime, endTime, clock.Now);
    blocks.Add(block);
    return block;
}
```

- [ ] **Step 4:** Run tests → PASS. **Step 5:** Commit `feat: calendar ScheduleTask overload with explicit duration`.

### Task 3: Rank period follows the visible date (`ShellViewModel`)

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/ShellViewModel.cs:144-146` (`CurrentPlanningPeriod`), `:74-80` (subscribe `VisibleDate`)
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/ShellViewModelTests.cs`

**Interfaces:**
- Produces: `CurrentPlanningPeriod` = `ForWeek(Calendar.VisibleDate)` / `ForToday(Calendar.VisibleDate)`; inbox rank chips refresh when `ViewKind` **or** `VisibleDate` changes.

- [ ] **Step 1: Failing test:** navigate `shell.Calendar.GoNextCommand`, save ranks for `PlanningPeriod.ForToday(DesignDate.AddDays(1))` in the prioritization repo, assert the inbox row's `RankText` updates (and that `CurrentPlanningPeriod.Anchor == DesignDate.AddDays(1)`).
- [ ] **Step 2:** Run → FAIL. **Step 3: Implement:** replace `_clock.Today` with `Calendar.VisibleDate` in `CurrentPlanningPeriod`; extend the `Calendar.PropertyChanged` handler to also call `RefreshInboxRanks()` on `nameof(CalendarViewModel.VisibleDate)`.
- [ ] **Step 4:** Run `ShellViewModelTests` + `PlanDraftViewModelTests` (they use `Plan`/`StartPrioritySort`) → PASS. **Step 5:** Commit `feat: priority ranks follow the visible calendar date`.

### Task 4: Task-edit flyout extraction + `onEdited` callback

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/TaskRowViewModel.cs` (optional `Action<TaskRowViewModel>? onEdited = null` ctor param, invoked at the end of `CommitEdit`)
- Create: `src/BeBoosted.Desktop/Views/TaskEditFlyoutView.axaml` + `.axaml.cs` (the flyout StackPanel extracted from `InboxDrawerView.axaml:68-110`, `x:DataType="vm:TaskRowViewModel"`, own `OnEditActionClick` popup-close handler copied from `InboxDrawerView.axaml.cs:66-74`)
- Modify: `src/BeBoosted.Desktop/Views/InboxDrawerView.axaml` (flyout content → `<views:TaskEditFlyoutView />`), `InboxDrawerView.axaml.cs` (drop now-unused `OnEditActionClick`)
- Test: existing `InboxViewModelTests` + `ShellSmokeTests` must stay green (pure refactor + additive param)

- [ ] **Step 1:** Add `onEdited` param (default null), invoke after `ResetEditFields()` in `CommitEdit`.
- [ ] **Step 2:** Extract the flyout UserControl; swap it into `InboxDrawerView`.
- [ ] **Step 3:** `dotnet build` + run `tests/BeBoosted.Desktop.Tests` → all green (no behavior change).
- [ ] **Step 4:** Commit `refactor: extract shared task-edit flyout, optional onEdited hook`.

### Task 5: `CalendarViewModel` plumbing + `DailyListViewModel` skeleton

**Files:**
- Create: `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs` (skeleton), `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs` (skeleton)
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` (ctor deps, `Daily` child, `Reload` hook, `HeaderMeta`, `ShowFloatingDraftCard`, `NotifyTasksMutated`)
- Modify: `tests/BeBoosted.Desktop.Tests/Support/TestDoubles.cs:367` (pass new ctor args in `TestShell.Create`)
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/CalendarViewModelCalendarTests.cs:125-130` (HeaderMeta change)

**Interfaces (produced, used by every later task):**

```csharp
public CalendarViewModel(
    AppSettings settings, IClock clock, CalendarService calendar, ITaskRepository tasks,
    PlanningService planning, IProjectRepository projects,
    TaskService taskService, InboxQueryService inboxQuery,
    PrioritySortService prioritySort, AiService ai)          // 4 new deps, DI-resolved

public DailyListViewModel Daily { get; }                     // created in ctor before Reload()
public bool ShowFloatingDraftCard => HasDraft && IsWeekView; // Week keeps the floating card
internal void NotifyTasksMutated()                           // Reload(); DataChanged?.Invoke();
```

`Reload()` (after `RefreshReviewNotice`): `Daily.IsActive = ViewKind == CalendarViewKind.Today;` and when active `Daily.Rebuild(VisibleDate, occurrences, pendingProposals, conflicts);`. `HeaderMeta` Today branch returns `string.Empty` (planned-hours line removed). `OnPropertyChanged(nameof(ShowFloatingDraftCard))` alongside the other draft notifications and in `OnViewKindChanged`.

`DailyListViewModel` skeleton ctor:

```csharp
public DailyListViewModel(
    CalendarViewModel owner, TaskService taskService, InboxQueryService inboxQuery,
    PrioritySortService prioritySort, AiService ai, CalendarService calendar,
    ITaskRepository tasks, IProjectRepository projects, IClock clock)
```

with `[ObservableProperty] bool IsActive`, empty `ObservableCollection<DailyRowViewModel> ScheduledRows/UnscheduledRows/CompletedRows`, and a no-op `Rebuild(DateOnly date, IReadOnlyList<BlockOccurrence> occurrences, IReadOnlyList<ProposedBlock> proposals, IReadOnlySet<CalendarBlockId> conflicts)`.

- [ ] **Step 1:** Update `HeaderMeta_SummarizesPlannedTimeForToday` → expect `string.Empty` (rename `HeaderMeta_IsEmptyForToday`); run → FAIL.
- [ ] **Step 2:** Implement all of the above; register nothing new in DI (all four services are already registered).
- [ ] **Step 3:** Update `TestShell.Create` to pass `taskService, inboxQuery, prioritySort, aiService` into `CalendarViewModel`.
- [ ] **Step 4:** Full `BeBoosted.Desktop.Tests` run → green. **Step 5:** Commit `feat: daily list skeleton and calendar plumbing`.

### Task 6: Daily classification, ordering, priority mapping, progress (TDD core)

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs`, `DailyListViewModel.cs`
- Test: Create `tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs`

**Interfaces produced (`DailyRowViewModel`):**

```csharp
public enum DailyRowKind { FixedCommitment, TaskBlock, Proposal, Task }

public DailyRowKind Kind { get; }
public string Title { get; }                  // task title, commitment title, or "(deleted task)"
public string? ProjectName { get; }           // omitted label when null
public TaskId? TaskId { get; }
public CalendarBlockId? BlockId { get; }      // block or proposal id
public DateOnly Date { get; }
public TimeOnly? StartTime { get; }
public TimeOnly? EndTime { get; }
public PriorityRank? Rank { get; }
public string PriorityText { get; }           // "P1"/"P2"/"P3"; "–" unranked task rows; "" fixed rows
public string? PriorityAccessibleText { get; }// "Priority 1 — Protect now" … ; "Not ranked for this day." unranked
public bool IsP1 / IsP2 / IsP3 { get; }       // rail classes
public string StatusText { get; }             // "FIXED" | "FLEX" | "PROPOSED" | ""
public bool IsExternal, IsConflicted, NeedsOutcome, IsDone, IsRecurring { get; }
public bool IsAiOrigin, NeedsReview { get; }  // unscheduled task badges
public string TimeText { get; }               // fixed: "9:00 – 10:00 AM"; flex/proposal: "11:30 AM"; "" unscheduled
public string MetaText { get; }               // unscheduled rows: project · deadline · duration (from TaskRow)
public string AccessibleName { get; }
public TaskRowViewModel? TaskRow { get; }     // edit flyout DataContext (task-backed rows)
public ScheduleFlyoutViewModel? ScheduleEditor { get; } // Task 8
// capabilities:
public bool ShowCommitmentCheck, ShowOutcomeControl, ShowTaskCheck, ShowScheduleAction,
            ShowProposalActions, CanEditCommitment, CanReopen, ShowBlockActions { get; }
```

Ordering (implemented as `int GroupOrder` + `IComparable` keys inside `DailyListViewModel`):

- Scheduled: group 0 fixed (by StartTime), group 1 ranked flex incl. proposals (by `Tier`, `Rank`), group 2 unranked flex (by StartTime); final tiebreak `(Title, BlockId)` everywhere.
- Unscheduled: ranked first `(Tier, Rank)`, then deadline bucket (0: `deadline <= clock.Today`, 1: future, 2: none) → deadline → `CreatedAt` → `(Title, TaskId)`.
- Completed: commitments by StartTime, then done blocks by StartTime, then direct tasks by `CompletedAt`.

Progress (`ProgressText` = `"{X} of {Y} complete"`):

- Occurrence-countables: local fixed occurrences on the date (each counts once; external excluded).
- Task-countables: distinct `TaskId` across scheduled task blocks + unscheduled tasks + done/completed tasks (proposals excluded).
- `X` = completed occurrences + distinct completed TaskIds; `Y` = X + open occurrence count + open distinct TaskIds.

Other outputs: `HeadingText` ("Today's tasks" when `date == clock.Today` else `"Tasks for {date:dddd}"`), `ScheduledCount/UnscheduledCount/CompletedCount`, `HasCompleted`, `IsScheduledEmpty/IsUnscheduledEmpty/IsAllComplete` (all-complete requires ≥1 completed row and both open sections empty), `[ObservableProperty] bool IsCompletedExpanded` (reset to false when `Rebuild` sees a different date).

Rebuild sources: `occurrences` (filter `o.Date == date`), `proposals` (filter `p.Date == date` for Scheduled), `inboxQuery.GetInboxTasks()` minus **all** pending-proposal TaskIds, `tasks.GetAll()` for completed-today tasks (`IsCompleted && DateOnly.FromDateTime(CompletedAt.Value.LocalDateTime.Date) == date`, dedup against done-block TaskIds), `prioritySort.GetRankLookup(PlanningPeriod.ForToday(date))`, `projects.GetAll()` name map. NeedsOutcome for block rows: `date < clock.Today || (date == clock.Today && block.EndTime <= TimeOnly.FromDateTime(clock.Now.LocalDateTime))` and `Outcome == None`.

- [ ] **Step 1: Failing tests** (build scenarios through `TestShell.Create` + `shell.Calendar.Daily`):
  - `Rebuild_ClassifiesRows` — fixed local, fixed external, open task block, done task block, completed occurrence, pending proposal, inbox task, directly-completed-today task → assert each lands in the right section.
  - `ScheduledOrdering_FixedFirst_ThenRankedByTier_ThenUnrankedByStart`.
  - `UnscheduledOrdering_RankedThenDeadlineThenCapture` (incl. overdue-first bucket).
  - `PriorityMapping_TiersAndUnranked` — P1/P2/P3 texts, accessible labels, dash + tooltip for unranked, `""` for fixed.
  - `ProposalTaskIds_AreSubtractedFromUnscheduled` (incl. a proposal on another day of a week draft).
  - `Progress_CountsAndDeduplicatesByTaskId` — two blocks for one task count once; external + proposals excluded.
  - `Ranks_FollowVisibleDate` — save ranks under `ForToday(DesignDate.AddDays(1))`, `GoNext`, assert P text appears.
  - `Heading_TodayVsOtherDay`; `EmptyStates_Flags`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement row factories (`ForOccurrence`, `ForProposal`, `ForInboxTask`, `ForCompletedTask`) + `Rebuild`. **Step 4:** Run → PASS. **Step 5:** Commit `feat: daily list classification, ordering, and progress`.

### Task 7: Daily mutations (complete / reopen / unschedule / outcomes / proposals)

**Files:**
- Modify: `DailyRowViewModel.cs` (commands), `DailyListViewModel.cs` (mutation methods)
- Test: extend `DailyListViewModelTests.cs`

**Interfaces produced (`DailyListViewModel`):**

```csharp
internal void CompleteTask(DailyRowViewModel row);       // TaskService.Complete + owner.NotifyTasksMutated()
internal void ReopenRow(DailyRowViewModel row);          // commitment: owner.SetCommitmentOccurrenceDone(id, date, false)
                                                         // task: TaskService.Reopen + NotifyTasksMutated
internal void ToggleCommitmentDone(DailyRowViewModel row); // owner.SetCommitmentOccurrenceDone(id, row.Date, !row.IsDone)
internal void Unschedule(DailyRowViewModel row);         // owner.UnscheduleBlock(blockId)
internal void EditCommitment(DailyRowViewModel row);     // owner.OpenCommitmentEditorFor(blockId, row.Date)
// outcome + proposal commands delegate to owner.RecordOutcome / ApproveProposalBlock / RemoveProposalBlock
```

Row commands: `ToggleCommitmentDoneCommand`, `CompleteTaskCommand`, `ReopenCommand`, `UnscheduleCommand`, `EditCommitmentCommand`, `RecordDoneCommand`, `RecordNeedsMoreTimeCommand` (uses `[ObservableProperty] decimal RemainingMinutes = 30`), `RecordDidntHappenCommand`, `ApproveProposalCommand`, `RemoveProposalCommand`. Task-backed rows get `TaskRow` built with `onRemoved: _ => owner.NotifyTasksMutated()` and `onEdited: _ => owner.NotifyTasksMutated()`. Done task-block rows: `CanReopen == false` (no invented reopen).

- [ ] **Step 1: Failing tests:** completing an unscheduled task moves it into Completed (visible-date == today) with exactly one `DataChanged` raise (count via `shell.Calendar.DataChanged += ...`); reopening a completed commitment occurrence returns it to Scheduled; reopening a directly-completed task returns it to Unscheduled; unscheduling a block returns its task to Unscheduled and keeps it open; `RecordDone` on a needs-outcome row lands it in Completed; approving a proposal converts it to a FLEX row; removing one returns the task to Unscheduled; done block rows expose no reopen.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** PASS. **Step 5:** Commit `feat: daily list mutations with single-refresh guarantee`.

### Task 8: Schedule flyout VM, change-time, and add-task flows

**Files:**
- Create: `src/BeBoosted.Desktop/ViewModels/ScheduleFlyoutViewModel.cs`
- Modify: `DailyRowViewModel.cs` (`ScheduleEditor`), `DailyListViewModel.cs` (confirm paths, add-task state, `event Action<TaskId>? RowFocusRequested`)
- Test: extend `DailyListViewModelTests.cs`

**Interfaces produced:**

```csharp
public sealed partial class ScheduleFlyoutViewModel : ViewModelBase
{
    public string Heading { get; }                       // "Schedule" or "Change time"
    [ObservableProperty] DateTimeOffset? Date;           // defaults to VisibleDate
    [ObservableProperty] TimeSpan? Start;                // 15-min increments (TimePicker MinuteIncrement=15)
    [ObservableProperty] decimal DurationMinutes;        // task estimate | existing block length | 30
    public string? WarningText { get; }                  // overlap + constraint warnings, recomputed on change
    [ObservableProperty] string? Error;                  // inline domain error, flyout stays open
    public bool Confirm();                               // true → caller closes flyout
    internal static TimeOnly DefaultStartFor(DateOnly targetDate, IClock clock); // next 15-min boundary today, else 9:00
}
```

`DailyListViewModel` additions:

```csharp
// Schedule an inbox task (called from ScheduleFlyoutViewModel.Confirm):
//   calendar.ScheduleTask(taskId, date, start, TimeSpan.FromMinutes(duration));
//   owner.NotifyTasksMutated(); RowFocusRequested?.Invoke(taskId); return true;
//   on DomainException → editor.Error = message; return false (task stays put)
// Change time for an approved block:
//   calendar.MoveBlock(blockId, date, start); calendar.ResizeBlock(blockId, end);
//   owner.NotifyTasksMutated(); RowFocusRequested?.Invoke(taskId)
[ObservableProperty] bool IsAddingUnscheduled; [ObservableProperty] string NewUnscheduledTitle;
public void ConfirmAddUnscheduled();   // blank → no-op; TaskService.Capture(title) + NotifyTasksMutated
public void CancelAddUnscheduled();    // Escape
[ObservableProperty] bool IsAddingScheduled;
[ObservableProperty] string NewScheduledTitle; [ObservableProperty] TimeSpan? NewScheduledStart;
[ObservableProperty] decimal NewScheduledDurationMinutes;  // default 30
[ObservableProperty] ProjectChoiceViewModel? NewScheduledProject;
[ObservableProperty] string? ScheduledAddNotice;           // "Added 'X' to Unscheduled — {error}" on partial failure
public IReadOnlyList<ProjectChoiceViewModel> ProjectChoices { get; }  // rebuilt in Rebuild
public void ConfirmAddScheduled();     // Capture(title, duration, null, project) then ScheduleTask(…, VisibleDate, start, duration);
                                       // schedule failure keeps the captured task, sets ScheduledAddNotice, refreshes once
```

Warnings: overlap = any occurrence on the chosen date (excluding own `BlockId`) intersecting `[start, clampedEnd)` → `"Overlaps {title} ({h:mm} – {h:mm})."`; constraints from `task.Constraints`: `NotBefore` > date, `EarliestTime` > start, `LatestTime` < clampedEnd → `"Outside this task's scheduling constraints."`. Warnings never block Confirm.

- [ ] **Step 1: Failing tests:** default start = next 15-min boundary on the actual today (FakeClock 14:10 → 14:15), 9:00 for other dates; duration initializes from estimate else 30; overlap and constraint warnings appear; `Confirm` success removes row from Unscheduled, inserts sorted into Scheduled, raises `RowFocusRequested` once, one `DataChanged`; forced `DomainException` (e.g., delete the task first) keeps the row and sets `Error`; add-unscheduled captures on confirm, blank no-ops, cancel clears; add-scheduled creates + schedules; schedule-failure path keeps the task in Unscheduled and sets `ScheduledAddNotice`; midnight clamp (23:50 + 30 min → ends 23:59).
- [ ] **Step 2:** FAIL → **Step 3:** implement → **Step 4:** PASS. **Step 5:** Commit `feat: schedule flyout, change time, and add-task flows`.

### Task 9: `DailyTaskListView.axaml` + `CalendarView` conditional surface

**Files:**
- Create: `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml` + `.axaml.cs`
- Modify: `src/BeBoosted.Desktop/Views/CalendarView.axaml:105-151` (conditional surface, floating draft card gated on `ShowFloatingDraftCard`)
- Modify: `src/BeBoosted.Desktop/Views/TimelineSurfaceView.axaml` — no change needed beyond `IsVisible="{Binding IsWeekView}"` applied from CalendarView wrapper

**Structure (real markup, abridged only for repeated row parts):**

```xml
<!-- CalendarView.axaml: replace `<views:TimelineSurfaceView />` -->
<Panel>
  <views:TimelineSurfaceView IsVisible="{Binding IsWeekView}" />
  <views:DailyTaskListView Name="DailyList" DataContext="{Binding Daily}" IsVisible="{Binding IsActive}" />
  <!-- floating draft card: IsVisible="{Binding ShowFloatingDraftCard}" (was HasDraft) -->
  …undo toast unchanged…
</Panel>
```

`DailyTaskListView.axaml` (`x:DataType="vm:DailyListViewModel"`): a `ScrollViewer` (`HorizontalScrollBarVisibility="Disabled"`, `Background=BrushPaperWhite`) wrapping a centered column `<StackPanel MaxWidth="880" Margin="24,40,24,24">` containing:

1. Heading row: `TextBlock` `FontSize={StaticResource TextSizeSectionTitle}` SemiBold `{Binding HeadingText}` + `TextBlock.meta` (FontSize `TextSizeSmall`) `{Binding ProgressText}` on one baseline.
2. Inline draft banner (`IsVisible="{Binding $parent[UserControl].((vm:DailyListViewModel)DataContext).ShowDraftBanner}"` — simpler: expose `ShowDraftBanner` on `DailyListViewModel` mirroring owner `HasDraft`): compact `Border` (workbench cream, `RadiusControl`) with `DraftTitle`, `DraftSummaryText`, Approve plan (primary) + Discard draft buttons bound through `OwnerDraft*` proxy properties on `DailyListViewModel` that delegate to the owner commands.
3. SCHEDULED section header (`metaLabel`-style uppercase + mono count) → rule-top `Border` → `ItemsControl ItemsSource="{Binding ScheduledRows}"` with the row template → Add-task row (44 px `Button.link`-style full-width; when `IsAddingScheduled` a compact editor row: title `TextBox`, `TimePicker MinuteIncrement="15"`, duration `NumericUpDown`, project `ComboBox`, Cancel/Add buttons, notice `TextBlock`).
4. Empty state `TextBlock.secondary` "Nothing scheduled for this day." (`IsVisible="{Binding IsScheduledEmpty}"`).
5. UNSCHEDULED section: same shape; add-task row becomes an inline `TextBox` when `IsAddingUnscheduled` (`KeyBindings`: Enter→`ConfirmAddUnscheduled`; `KeyDown` Escape handled in code-behind → `CancelAddUnscheduled`); empty state "No unscheduled tasks.".
6. All-complete state: "Everything for this day is complete." (`IsVisible="{Binding IsAllComplete}"`).
7. COMPLETED: `ToggleButton` header (chevron rotates, label COMPLETED, mono count, lime check badge; `IsChecked="{Binding IsCompletedExpanded}"`, `IsVisible="{Binding HasCompleted}"`, AutomationProperties.Name="Completed section") → `ItemsControl` `IsVisible="{Binding IsCompletedExpanded}"` with completed rows at `Opacity 0.62` + strikethrough titles.

Row DataTemplate (shared, driven by capability flags): `Border MinHeight="54" Padding="12,0" BorderBrush=BrushRuleFaint BorderThickness="0,0,0,1"` + hover wash style; inner `Grid ColumnDefinitions="3,12,Auto,34,*,Auto,Auto,150"`:

- col0 rail: `Border Width="3"` with classes `p1|p2|p3` → `BrushGraphite` / `BrushGraphite40` / `BrushRuleMedium`, transparent otherwise.
- col2 completion cluster (all real Buttons, 18–20 px, `RadiusSmall`+1): commitment check button (`Classes.checked` lime when done, name `{Binding CompletionControlName}`), task-block outcome button with the Done / Needs-more-time / Didn't-happen flyout copied from `CalendarBlockView.axaml:40-70` (plus Unschedule item), unscheduled-task check button, reopen button on completed rows (`IsVisible="{Binding CanReopen}"`).
- col3 priority marker: `TextBlock.mono` 11 px `{Binding PriorityText}`, `ToolTip.Tip="{Binding PriorityAccessibleText}"`.
- col4 title + project: title weight varies by tier via classes; commitment titles are a `Button.link` invoking `EditCommitmentCommand`; project name `TextBlock.meta`; AI added / Needs review badges (copy the two chip Borders from `InboxDrawerView.axaml:123-137`); unscheduled meta line `{Binding MetaText}`.
- col5 status chip (`TextBlock.meta` inside 1-px chip Border; PROPOSED chip uses `BrushLimeWash` + dashed look via `Rectangle StrokeDashArray="3,2"` consistent with proposal styling), lock icon (`IconLock` + tooltip "Synced from an external calendar — BeBoosted never edits it"), conflict icon (`IconSpark`-free — use hatch-free triangle path already drawn inline in mockup → reuse `HatchOverlay`? No: restrained label) → small chip "conflict" with tooltip, needs-outcome chip "Needs outcome".
- col6 actions: per-kind quiet icon buttons (opacity .45 → 1 on `:pointerover`/`:focus-within`, still always hit-testable): commitment Edit (pencil); task-block Edit task details (pencil → `TaskEditFlyoutView` flyout), Change time (schedule flyout), Unschedule; proposal Approve / Remove / Why (Why flyout reuses the evidence layout of `CalendarBlockView.axaml:116-150`); unscheduled Edit (pencil → `TaskEditFlyoutView`) + underlined `Schedule` link-button opening the schedule flyout.
- col7 time: `TextBlock.mono` `FontSize=TextSizeSmall` right-aligned `{Binding TimeText}`.

Schedule flyout content (shared for Schedule + Change time), inside `Button.Flyout`:

```xml
<Flyout Placement="BottomEdgeAlignedRight" ShowMode="Standard">
  <StackPanel Width="272" Spacing="10" Margin="4" x:DataType="vm:ScheduleFlyoutViewModel">
    <TextBlock Text="{Binding Heading}" FontWeight="SemiBold" />
    <DatePicker SelectedDate="{Binding Date}" AutomationProperties.Name="Date" />
    <TimePicker SelectedTime="{Binding Start}" MinuteIncrement="15" ClockIdentifier="12HourClock"
                AutomationProperties.Name="Start time" />
    <NumericUpDown Value="{Binding DurationMinutes}" Minimum="5" Maximum="720" Increment="15"
                   FormatString="0" AutomationProperties.Name="Duration minutes" />
    <TextBlock Classes="meta" Text="{Binding WarningText}" TextWrapping="Wrap"
               IsVisible="{Binding WarningText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
    <TextBlock Foreground="{StaticResource BrushInkHover}" FontSize="{StaticResource TextSizeSmall}"
               Text="{Binding Error}" TextWrapping="Wrap"
               IsVisible="{Binding Error, Converter={x:Static ObjectConverters.IsNotNull}}" />
    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
      <Button Content="Cancel" Click="OnScheduleCancelClick" />
      <Button Classes="primary" Content="Schedule" Click="OnScheduleConfirmClick"
              AutomationProperties.Name="{Binding ConfirmAccessibleName}" />
    </StackPanel>
  </StackPanel>
</Flyout>
```

Code-behind (`DailyTaskListView.axaml.cs`): `OnScheduleConfirmClick` → `(sender DataContext as ScheduleFlyoutViewModel).Confirm()`; close the hosting popup only on `true` (popup-close helper copied from `InboxDrawerView.axaml.cs:66-74`). `OnScheduleCancelClick` closes unconditionally. Subscribe `DataContext.RowFocusRequested` → `Dispatcher.UIThread.Post` find the row Border whose `DailyRowViewModel.TaskId` matches and `Focus()` it. Escape handling for the inline add TextBox. Also mirror-forward `ShowDraftBanner`: implement as `DailyListViewModel` properties `ShowDraftBanner/DraftTitle/DraftSummaryText` + `ApproveDraftCommand/DiscardDraftCommand` pass-throughs refreshed in `Rebuild` (owner supplies values — simplest: `internal void SyncDraft(bool hasDraft, string title, string summary)` called from owner `Reload`, commands delegate `owner.ApproveDraftCommand` directly via `{Binding Owner.ApproveDraftCommand}` with `public CalendarViewModel Owner { get; }` exposed).

- [ ] **Step 1:** Write the two views + CalendarView changes; build.
- [ ] **Step 2:** Quick smoke: run `ShellSmokeTests` — expect the Today block-count test to fail (adapted in Task 11); everything else compiles/passes.
- [ ] **Step 3:** Commit `feat: daily task list view replaces Today timeline surface`.

### Task 10: New UI tests (`DailyListUiTests`)

**Files:**
- Create: `tests/BeBoosted.Desktop.Tests/Ui/DailyListUiTests.cs`

Scenario coverage (each an `[AvaloniaFact]`, built on `TestShell` seeds):

- [ ] `Today_ShowsDailyListAndHidesTimeline` — `DailyTaskListView.IsEffectivelyVisible` true, `TimelineSurfaceView` not effectively visible; Week inverts both.
- [ ] `Navigation_PreviousNextToday_UpdatesHeading` — GoNext → "Tasks for Wednesday", GoToToday → "Today's tasks".
- [ ] `ScheduleFlyout_SchedulesTask_AndMovesFocus` — open Schedule on an inbox task row, confirm via VM, assert row moved to Scheduled and focus landed on it.
- [ ] `AddTask_Unscheduled_InlineCapture` and `AddTask_Scheduled_CreateAndSchedule`.
- [ ] `CommitmentRow_TitleOpensEditor` — click/execute `EditCommitmentCommand`, assert `IsCommitmentEditorOpen`.
- [ ] `TaskBlockRow_OutcomeMenu_RecordsDone`.
- [ ] `Completed_ExpandCollapse` (collapsed by default, hidden at zero).
- [ ] `ExternalCommitment_RemainsLocked` — no completion/edit/schedule controls in the row.
- [ ] `ReviewNotice_AndDraftBanner_Visible` — seed an elapsed block + a draft; notice text present, inline banner present in Today, floating card absent in Today but present in Week.
- [ ] `EscapeAndFocus_RestoredAfterEditorCloses` (reuse commitment-editor focus pattern assertions).
- [ ] Run new file → PASS. Commit `test: daily list UI coverage`.

### Task 11: Adapt existing Today-timeline tests (Week keeps full coverage)

**Files:**
- Modify: `tests/BeBoosted.Desktop.Tests/Ui/TimelineSurfaceTests.cs` (anchor every timeline assertion on Week: set `ViewKind = Week` before `FindSurface`; drop the `[InlineData(CalendarViewKind.Today)]` case; the view-switch offset test becomes Week→Today→Week and asserts the Week offset survives)
- Modify: `tests/BeBoosted.Desktop.Tests/Ui/CommitmentCompletionUiTests.cs` (set Week view after `window.Show()` so `CalendarBlockView` rows exist)
- Modify: `tests/BeBoosted.Desktop.Tests/Ui/ShellSmokeTests.cs:92-120` (`Calendar_RendersSeededBlocks_TodayAndWeek` → Today asserts daily rows exist + zero `CalendarBlockView`; Week keeps the 12-block assertion)
- Modify: `tests/BeBoosted.Desktop.Tests/Ui/FeatureScreenshotCaptureTests.cs` (timeline scroll captures switch to Week; add a `daily-list-{w}x{h}.png` capture of Today)
- Modify: `tests/BeBoosted.Desktop.Tests/Ui/ScreenshotCaptureTests.cs` (no rename needed; `shell-calendar-today` now captures the daily list — verify it renders)

- [ ] Adapt each file, run the full `BeBoosted.Desktop.Tests` suite → green. Commit `test: re-anchor timeline coverage on Week view`.

### Task 12: Full verification + screenshots

- [ ] **Step 1:** `dotnet build` release-quality: zero warnings introduced.
- [ ] **Step 2:** `dotnet test` (both projects) → all green.
- [ ] **Step 3:** `BEBOOSTED_SCREENSHOT_DIR=<scratchpad>/shots dotnet test --filter Screenshot` → captures at 1440×960 + 1280×800; extend `DailyListUiTests` or a scratch capture for 1100×720 Today + Week.
- [ ] **Step 4:** Read every Today/Week PNG; check against frame `#2a`: centered 880 column, 54 px rows, rails, chips, mono times, collapsed Completed, no clipping/horizontal scroll at 1100×720.
- [ ] **Step 5:** Fix visual defects, re-capture until clean.
- [ ] **Step 6:** Final commit; write the completion summary (files changed, behavior, deviations, tests + screenshots, limitations).

## Self-Review Notes

- Spec coverage checked section-by-section: scope/source-of-truth (T1), visual (T9, T12), recommended structure (T5, T9), date navigation + heading (T6, existing nav untouched), priority model (T6), scheduled section incl. row behaviors (T6, T7, T9), unscheduled + schedule action (T6–T9), add-task rows (T8, T9), completed + progress (T6, T7), empty states (T6, T9), planning/review behavior (T9 banner + notice, proposals T6/T7), accessibility (T9 real controls + names, T10 assertions), testing/verification (T10–T12).
- Intentional deviations from frame `#2a` (report in summary): no six-dot drag grips (spec order); fixed commitments show no P marker (mockup shows P1/P2 on FIXED rows; spec forbids fabricated priorities); scheduled ordering is fixed-first then rank (spec order, mockup interleaves); 10 px chip text raised to the 11 px production floor.
- Type consistency: `NotifyTasksMutated`, `Daily`, `Rebuild(date, occurrences, proposals, conflicts)`, `RowFocusRequested`, `ScheduleFlyoutViewModel.Confirm()` used consistently across tasks.
