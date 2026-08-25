# Per-Session Completion and Session Titles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Marking a scheduled session done resolves that session alone, leaving the parent task open until every session is resolved, and lets each session carry its own optional title.

**Architecture:** `RecordOutcome(Done)` stops applying the whole-task aggregate transition and simply records the outcome on its own block, matching what `NeedsMoreTime` and `DidntHappen` already do. The task→sessions direction is untouched. `InboxQueryService.GetInboxTasks` already excludes tasks with any `outcome = 0` block, so a task returns to Unscheduled once its last session resolves with no query change. Session titles reuse the existing, already-persisted `calendar_blocks.title` column.

**Tech Stack:** C# / .NET 10, Avalonia 12, CommunityToolkit.Mvvm, SQLite (Microsoft.Data.Sqlite), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-25-per-session-completion-and-titles-design.md`

## Global Constraints

- `TreatWarningsAsErrors` is on solution-wide. A warning fails the build.
- Nullable reference types are enabled. No `!` suppressions in new production code.
- `AvaloniaUseCompiledBindingsByDefault` is on. Every new binding needs a correct `x:DataType` in scope.
- **No database migration.** `calendar_blocks.title` exists (`0003_calendar_blocks.sql`) and `SqliteCalendarBlockRepository` already binds it on insert and update. Do not add a migration.
- A blank or whitespace-only session title normalizes to `null`, which falls back to the parent task's title.
- The parent task never auto-completes. It completes only through `CompleteTask` / `UpdateTaskDetails`.
- `CompleteTask` / `ReopenTask` / `UpdateTaskDetails` keep their existing aggregate behavior and their repeating-sibling guard. Only `RecordOutcome` changes.
- Run the full suite with `dotnet test BeBoosted.slnx`. Close the running app first — it locks `BeBoosted.exe` and the build will fail with MSB3027.

---

### Task 1: Session titles in the domain

**Files:**
- Modify: `src/BeBoosted.Domain/Calendar/CalendarBlock.cs:73` (`Title` property), `:106-119` (`CreateTaskSession`), `:198-210` (`EnsureOccurrenceCompletable` message)
- Test: `tests/BeBoosted.Tests/Domain/CalendarBlockTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `CalendarBlock.Retitle(string? title, DateTimeOffset now)` — trims; blank/whitespace → `null`; throws `DomainException` for external events.
  - `CalendarBlock.CreateTaskSession(TaskId taskId, DateOnly date, TimeOnly startTime, TimeOnly endTime, DateTimeOffset now, RecurrenceRule? recurrence = null, string? title = null)`
  - `CalendarBlock.Title` becomes `{ get; private set; }`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/BeBoosted.Tests/Domain/CalendarBlockTests.cs`:

```csharp
    [Fact]
    public void Retitle_TrimsTheTitle_AndTouchesModifiedAt()
    {
        var session = CalendarBlock.CreateTaskSession(
            TaskId.New(), new DateOnly(2026, 8, 25),
            new TimeOnly(9, 0), new TimeOnly(10, 0), Now);

        session.Retitle("  Jane Eyre 1-10  ", Now.AddMinutes(5));

        Assert.Equal("Jane Eyre 1-10", session.Title);
        Assert.Equal(Now.AddMinutes(5), session.ModifiedAt);
    }

    [Fact]
    public void Retitle_BlankBecomesNull_SoTheRowFallsBackToTheTaskTitle()
    {
        var session = CalendarBlock.CreateTaskSession(
            TaskId.New(), new DateOnly(2026, 8, 25),
            new TimeOnly(9, 0), new TimeOnly(10, 0), Now, null, "Jane Eyre 1-10");
        Assert.Equal("Jane Eyre 1-10", session.Title);

        session.Retitle("   ", Now);

        Assert.Null(session.Title);
    }

    [Fact]
    public void Retitle_OnAnExternalEvent_IsRejected()
    {
        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "Dentist", new DateOnly(2026, 8, 25),
            new TimeOnly(9, 0), new TimeOnly(10, 0), BlockKind.ExternalEvent, null,
            "google", "evt-1", 0, BlockOutcome.None, null, Now, Now);

        Assert.Throws<DomainException>(() => external.Retitle("Mine now", Now));
        Assert.Equal("Dentist", external.Title);
    }
```

If `CalendarBlockTests` has no `Now` field, add `private static readonly DateTimeOffset Now = new(2026, 8, 25, 9, 0, 0, TimeSpan.FromHours(-7));` to the class.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~Retitle"`
Expected: FAIL to compile — `'CalendarBlock' does not contain a definition for 'Retitle'`.

- [ ] **Step 3: Make `Title` settable**

In `src/BeBoosted.Domain/Calendar/CalendarBlock.cs`, change line 73:

```csharp
    /// <summary>
    /// External events carry their own title. A session may carry an optional one of
    /// its own; when null it displays its Task's title.
    /// </summary>
    public string? Title { get; private set; }
```

- [ ] **Step 4: Add the `title` parameter to `CreateTaskSession`**

Replace the factory:

```csharp
    public static CalendarBlock CreateTaskSession(
        TaskId taskId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        DateTimeOffset now,
        RecurrenceRule? recurrence = null,
        string? title = null)
    {
        ValidateTimes(startTime, endTime);
        return new CalendarBlock(
            CalendarBlockId.New(), taskId, NormalizeTitle(title), date, startTime, endTime,
            BlockKind.TaskSession, recurrence, LocalProvider, null, 0,
            BlockOutcome.None, null, now, now);
    }
```

- [ ] **Step 5: Add `Retitle` and `NormalizeTitle`**

Add directly below `ClearOutcome` (after line 188):

```csharp
    /// <summary>
    /// Names this session for the day it covers ("Jane Eyre 1-10"). Blank clears the
    /// name, and the row falls back to the Task's title.
    /// </summary>
    public void Retitle(string? title, DateTimeOffset now)
    {
        EnsureLocalSession();
        Title = NormalizeTitle(title);
        Touch(now);
    }

    private static string? NormalizeTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? null : title.Trim();
```

- [ ] **Step 6: Correct the stale occurrence-guard message**

In `EnsureOccurrenceCompletable`, replace the one-off rejection message — its current wording asserts the aggregate rule this plan removes:

```csharp
        if (Recurrence is null)
        {
            throw new DomainException(
                "A one-off session records an outcome, not an occurrence completion.");
        }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~Retitle"`
Expected: PASS, 3 tests.

Then run the full suite: `dotnet test BeBoosted.slnx`
Expected: all pass. `EnsureLocalSession` already guards external events, so `Retitle`'s rejection needs no new code.

- [ ] **Step 8: Commit**

```bash
git add src/BeBoosted.Domain/Calendar/CalendarBlock.cs tests/BeBoosted.Tests/Domain/CalendarBlockTests.cs
git commit -m "feat: sessions carry an optional title"
```

---

### Task 2: Decouple the Done outcome from task completion

This is the core behavioral change. It inverts one existing test on purpose.

**Files:**
- Modify: `src/BeBoosted.Application/Calendar/CalendarService.cs:522-578` (`RecordOutcome`)
- Test: `tests/BeBoosted.Tests/Calendar/TaskCompletionAuthorityTests.cs:236-260`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `RecordOutcome(CalendarBlockId, BlockOutcome, TimeSpan?)` keeps its signature; only its `Done` behavior changes. `ApplyAggregateCompletion` is untouched and still serves `CompleteTask` / `ReopenTask` / `UpdateTaskDetails`.

- [ ] **Step 1: Invert the existing sibling test**

In `tests/BeBoosted.Tests/Calendar/TaskCompletionAuthorityTests.cs`, replace `RecordOutcomeDone_ResolvesEveryPendingOneOffSibling` entirely (including its doc comment) with:

```csharp
    /// <summary>
    /// Recording one session Done is a statement about that session alone: its
    /// siblings stay pending and the Task stays open until the user completes it.
    /// </summary>
    [Fact]
    public void RecordOutcomeDone_LeavesSiblingsPending_AndTheTaskOpen()
    {
        var task = AddTask("College essay");
        var recorded = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var sibling = AddSession(task.Id, dayOffset: 1, startHour: 15);

        _service.RecordOutcome(recorded.Id, BlockOutcome.Done);

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(recorded.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(sibling.Id)!.Outcome);

        var restarted = new SqliteCalendarBlockRepository(_database.Factory);
        Assert.Equal(BlockOutcome.None, restarted.GetById(sibling.Id)!.Outcome);
        Assert.False(new SqliteTaskRepository(_database.Factory).GetById(task.Id)!.IsCompleted);
    }

    /// <summary>Sessions resolve independently, in whichever order the day went.</summary>
    [Fact]
    public void RecordOutcomeDone_OnBothSessions_ResolvesEachIndependently()
    {
        var task = AddTask("College essay");
        var first = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var second = AddSession(task.Id, dayOffset: 1, startHour: 15);

        _service.RecordOutcome(second.Id, BlockOutcome.Done);
        _service.RecordOutcome(first.Id, BlockOutcome.Done);

        Assert.Equal(BlockOutcome.Done, _blocks.GetById(first.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(second.Id)!.Outcome);
        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
    }

    /// <summary>
    /// The guard existed only to protect whole-task completion. A local session
    /// outcome is compatible with a repeating sibling series.
    /// </summary>
    [Fact]
    public void RecordOutcomeDone_WithARepeatingSibling_IsAllowed()
    {
        var task = AddTask("Reading");
        var oneOff = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var repeating = CalendarBlock.CreateTaskSession(
            task.Id, Tuesday, new TimeOnly(18, 0), new TimeOnly(19, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(repeating);

        _service.RecordOutcome(oneOff.Id, BlockOutcome.Done);

        Assert.Equal(BlockOutcome.Done, _blocks.GetById(oneOff.Id)!.Outcome);
        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
    }
```

`RecurrenceRule.Weekly(int interval, params DayOfWeek[] days)` is the signature used elsewhere in the suite.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~RecordOutcomeDone"`
Expected: FAIL. `..._LeavesSiblingsPending_AndTheTaskOpen` fails on `Assert.False(...IsCompleted)` — the task is currently completed. `..._WithARepeatingSibling_IsAllowed` fails with `DomainException: A repeating task completes per occurrence, not as a whole.`

- [ ] **Step 3: Remove the aggregate transition from the Done branch**

In `src/BeBoosted.Application/Calendar/CalendarService.cs`, update the doc comment on `RecordOutcome`:

```csharp
    /// <summary>
    /// Records a one-off session outcome and applies its task effect as one atomic
    /// mutation. Done resolves that session alone — the Task stays open and its
    /// sibling sessions stay pending, so a Task with several sessions is finished
    /// one sitting at a time. Needs more time updates the remaining estimate and
    /// sends the task back to the Inbox; Didn't happen leaves the task open for
    /// replanning. Everything validates before anything persists.
    /// </summary>
```

Delete the repeating-sibling guard (the `if (outcome == BlockOutcome.Done && siblings.Any(...)) throw` block) and replace the `switch` with:

```csharp
        block.RecordOutcome(outcome, clock.Now); // local one-off validation in the domain

        switch (outcome)
        {
            case BlockOutcome.Done:
                // A session's Done is local to that session. Whole-task completion
                // stays with CompleteTask / UpdateTaskDetails.
                break;
            case BlockOutcome.NeedsMoreTime:
                task!.RecordNeedsMoreTime(
                    remaining ?? task.EstimatedDuration ?? DefaultTaskBlockDuration,
                    clock.Now);
                break;
            case BlockOutcome.DidntHappen:
                break;
        }

        mutations.Execute((blockRepo, _, taskRepo) =>
        {
            blockRepo.Update(block);
            if (outcome == BlockOutcome.NeedsMoreTime)
            {
                taskRepo.Update(task!);
            }
        });
```

Note the persistence condition changed from `outcome != BlockOutcome.DidntHappen` to `outcome == BlockOutcome.NeedsMoreTime` — `Done` no longer touches the task, so writing it back would be a pointless write. Delete the now-unused `touchedSiblings` local. The `siblings` local is still needed for nothing else, so delete its assignment too and keep only the `task` lookup and its null check.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~RecordOutcomeDone"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Run the full suite and expect known failures**

Run: `dotnet test BeBoosted.slnx`

Expected: `BeBoosted.Tests` passes in full — in particular `CompleteTask_MultipleOneOffSessions_ResolvesEveryPendingSession_PreservingHistory` and `UpdateTaskDetails_TaskCompletion_ResolvesEveryPendingOneOffSession` must still pass. They are the regression signal that only the session→task direction was cut. If either fails, you removed too much.

`BeBoosted.Desktop.Tests` may now fail where tests assert a session's Done completed its task. Record which tests fail; Tasks 4 and 6 fix them. Do not fix them here.

- [ ] **Step 6: Commit**

```bash
git add src/BeBoosted.Application/Calendar/CalendarService.cs tests/BeBoosted.Tests/Calendar/TaskCompletionAuthorityTests.cs
git commit -m "feat: a session's Done outcome no longer completes its task"
```

---

### Task 3: Per-session undo in the service

**Files:**
- Modify: `src/BeBoosted.Application/Calendar/CalendarService.cs` (add after `RecordOutcome`)
- Test: `tests/BeBoosted.Tests/Calendar/TaskCompletionAuthorityTests.cs`

**Interfaces:**
- Consumes: Task 2's decoupled `RecordOutcome`.
- Produces: `bool CalendarService.ClearSessionOutcome(CalendarBlockId id)` — clears one block's outcome; returns `false` when already `None`; throws `DomainException` for external events or a missing block.

- [ ] **Step 1: Write the failing tests**

Append to `tests/BeBoosted.Tests/Calendar/TaskCompletionAuthorityTests.cs`:

```csharp
    [Fact]
    public void ClearSessionOutcome_RestoresOneSession_WithoutDisturbingItsSibling()
    {
        var task = AddTask("College essay");
        var first = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var second = AddSession(task.Id, dayOffset: 1, startHour: 15);
        _service.RecordOutcome(first.Id, BlockOutcome.Done);
        _service.RecordOutcome(second.Id, BlockOutcome.Done);

        Assert.True(_service.ClearSessionOutcome(first.Id));

        Assert.Equal(BlockOutcome.None, _blocks.GetById(first.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(second.Id)!.Outcome);
        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void ClearSessionOutcome_OnAnAlreadyClearSession_IsAQuietNoOp()
    {
        var task = AddTask();
        var session = AddSession(task.Id, dayOffset: 0, startHour: 9);

        Assert.False(_service.ClearSessionOutcome(session.Id));
        Assert.Equal(BlockOutcome.None, _blocks.GetById(session.Id)!.Outcome);
    }

    [Fact]
    public void ClearSessionOutcome_SurvivesRestart()
    {
        var task = AddTask();
        var session = AddSession(task.Id, dayOffset: 0, startHour: 9);
        _service.RecordOutcome(session.Id, BlockOutcome.Done);

        Assert.True(_service.ClearSessionOutcome(session.Id));

        var restarted = new SqliteCalendarBlockRepository(_database.Factory);
        Assert.Equal(BlockOutcome.None, restarted.GetById(session.Id)!.Outcome);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ClearSessionOutcome"`
Expected: FAIL to compile — `'CalendarService' does not contain a definition for 'ClearSessionOutcome'`.

- [ ] **Step 3: Implement it**

Add directly after `RecordOutcome` in `src/BeBoosted.Application/Calendar/CalendarService.cs`:

```csharp
    /// <summary>
    /// Takes back one session's outcome, leaving its siblings and its Task alone —
    /// the per-session inverse of RecordOutcome. Returns false when the session
    /// carried no outcome, so callers announce nothing.
    /// </summary>
    public bool ClearSessionOutcome(CalendarBlockId id)
    {
        var block = Require(id);
        if (block.Outcome == BlockOutcome.None)
        {
            return false;
        }

        block.ClearOutcome(clock.Now); // rejects external events in the domain
        mutations.Execute((blockRepo, _, _) => blockRepo.Update(block));
        return true;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~ClearSessionOutcome"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/BeBoosted.Application/Calendar/CalendarService.cs tests/BeBoosted.Tests/Calendar/TaskCompletionAuthorityTests.cs
git commit -m "feat: add per-session outcome undo"
```

---

### Task 4: Per-session undo in the daily list, and remove the dead repeating gates

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs:395-409` (`ReopenRow`), `:284-285` (`TaskRepeats` assignment)
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs:284-311`, `:396`
- Modify: `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs` (add `ClearSessionOutcome`)
- Modify: `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml`
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs`

**Interfaces:**
- Consumes: `CalendarService.ClearSessionOutcome(CalendarBlockId)` from Task 3.
- Produces: `CalendarViewModel.ClearSessionOutcome(CalendarBlockId id)` (void; reloads and raises `DataChanged` only when something changed). `DailyRowViewModel.CanToggleSessionDone`, `CanRecordDone`, `ShowRepeatingCompletionNote`, `RepeatingCompletionNote`, `ShowSessionCheckBlockedNote` and `TaskRepeats` are **deleted**.

- [ ] **Step 1: Write the failing test**

Append to `tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs`, using that file's `Create()` / `AddTask(context, title)` helpers and `context.Service.ScheduleTask`:

```csharp
    [Fact]
    public void MarkingOneSessionDone_LeavesTheOtherScheduled_AndTheTaskOutOfUnscheduled()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var morning = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(19, 0));
        context.Calendar.Reload();
        Assert.Equal(2, context.Daily.ScheduledRows.Count);

        context.Daily.ScheduledRows.Single(r => r.BlockId == morning.Id)
            .ToggleDoneCommand.Execute(null);

        Assert.Single(context.Daily.ScheduledRows);
        Assert.Single(context.Daily.CompletedRows);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
        Assert.Empty(context.Daily.UnscheduledRows); // still held by the evening session
    }

    [Fact]
    public void UndoingOneSession_ReturnsItToScheduled_WithoutDisturbingItsSibling()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var morning = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        var evening = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(19, 0));
        context.Calendar.Reload();
        context.Daily.ScheduledRows.Single(r => r.BlockId == morning.Id)
            .ToggleDoneCommand.Execute(null);
        context.Daily.ScheduledRows.Single(r => r.BlockId == evening.Id)
            .ToggleDoneCommand.Execute(null);
        Assert.Equal(2, context.Daily.CompletedRows.Count);
        Assert.Single(context.Daily.UnscheduledRows); // both resolved: the task is back

        context.Daily.CompletedRows.Single(r => r.BlockId == morning.Id)
            .ToggleDoneCommand.Execute(null);

        Assert.Single(context.Daily.ScheduledRows);
        Assert.Single(context.Daily.CompletedRows);
        Assert.Equal(BlockOutcome.Done, context.Blocks.GetById(evening.Id)!.Outcome);
        Assert.Empty(context.Daily.UnscheduledRows);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~MarkingOneSessionDone|FullyQualifiedName~UndoingOneSession"`
Expected: FAIL — undo currently calls `ReopenTask`, which clears the sibling's outcome too, so the second test finds 2 scheduled rows and 0 completed.

- [ ] **Step 3: Add the view-model plumbing for the per-session clear**

In `src/BeBoosted.Desktop/ViewModels/CalendarViewModel.cs`, add beside `RecordOutcome` (near line 1099):

```csharp
    /// <summary>Takes back one session's outcome; a no-op announces nothing.</summary>
    public void ClearSessionOutcome(CalendarBlockId id)
    {
        try
        {
            if (!_calendar.ClearSessionOutcome(id))
            {
                return;
            }
        }
        catch (DomainException exception)
        {
            ShowNotice(exception.Message);
            return;
        }

        Reload();
        DataChanged?.Invoke();
    }
```

- [ ] **Step 4: Split the Session branch out of `ReopenRow`**

In `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs`, replace `ReopenRow`:

```csharp
    internal void ReopenRow(DailyRowViewModel row)
    {
        if (row.Kind == DailyRowKind.Obligation && row.BlockId is { } occurrenceId)
        {
            _owner.SetOccurrenceDone(occurrenceId, row.Date, done: false);
        }
        else if (row.Kind == DailyRowKind.Session && row.BlockId is { } sessionId)
        {
            // Per-session: this session's outcome only. Its siblings and its Task
            // are untouched.
            _owner.ClearSessionOutcome(sessionId);
        }
        else if (row.Kind == DailyRowKind.Task
            && row.TaskId is { } taskId
            && _calendar.ReopenTask(taskId))
        {
            // Reopening a Task still clears every Done one-off session of it — the
            // inverse of completing the Task, which resolves them all together.
            _owner.NotifyTasksMutated();
        }
    }
```

- [ ] **Step 5: Delete the dead repeating gates**

In `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs`, delete `TaskRepeats`, `CanRecordDone`, `CanToggleSessionDone`, `ShowRepeatingCompletionNote`, `RepeatingCompletionNote`, and `ShowSessionCheckBlockedNote` with their doc comments (lines 284-311). They exist only because a session's Done was a whole-task statement.

Change the toggle command attribute (line 396) from `[RelayCommand(CanExecute = nameof(CanToggleSessionDone))]` to `[RelayCommand]`, and update its `Session` branch comment:

```csharp
            case DailyRowKind.Session:
                // A session's Done is local to that session; the Task stays open.
                _owner.RecordOutcome(this, BlockOutcome.Done, null);
                break;
```

In `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs`, delete the `row.TaskRepeats = ...` assignment at lines 284-285.

- [ ] **Step 6: Remove the disabled affordances from the view**

In `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml`, remove the element bound to `ShowSessionCheckBlockedNote` and the `Button.dailyCheck:disabled` style block. Remove any `IsEnabled` binding to `CanToggleSessionDone` on the session checkbox. Search the file for `CanToggleSessionDone`, `ShowSessionCheckBlockedNote`, `RepeatingCompletionNote` and `ShowRepeatingCompletionNote` and remove every hit — compiled bindings turn a stale reference into a build error, so none may remain.

- [ ] **Step 7: Fix the tests that asserted the deleted members**

Run: `dotnet test BeBoosted.slnx`

Delete assertions on the removed members. `DailyListViewModelTests.MixedScheduleSessionRow_OffersOutcomes_ButNeverGlobalDone` (line ~1049) asserts the old contract in its name and its body — it checks `CanToggleSessionDone` is false, `ShowSessionCheckBlockedNote` is true, `CanRecordDone` is false, and the exact `RepeatingCompletionNote` string. Replace the whole test with:

```csharp
    /// <summary>
    /// A one-off session's Done is local, so a repeating sibling no longer blocks it.
    /// The gate and its explanatory note are gone with the aggregate rule.
    /// </summary>
    [Fact]
    public void MixedScheduleSessionRow_CompletesNormally_DespiteARepeatingSibling()
    {
        var context = Create();
        var task = AddTask(context, "Mixed work");
        var session = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Date, new TimeOnly(16, 0), new TimeOnly(17, 0), context.Clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)));
        context.Calendar.Reload();

        var row = context.Daily.ScheduledRows.Single(r => r.BlockId == session.Id);
        Assert.True(row.ShowSessionCheck);
        Assert.True(row.ShowSessionOutcomeAction);
        Assert.True(row.ToggleDoneCommand.CanExecute(null));
        Assert.True(row.RecordDoneCommand.CanExecute(null));
        Assert.True(row.RecordNeedsMoreTimeCommand.CanExecute(null));
        Assert.True(row.RecordDidntHappenCommand.CanExecute(null));

        row.ToggleDoneCommand.Execute(null);

        Assert.Contains(context.Daily.CompletedRows, r => r.BlockId == session.Id);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    }
```

`RecordDoneCommand` had `CanExecute = nameof(CanRecordDone)`; drop that attribute argument when you delete `CanRecordDone` in Step 5, or this assertion cannot pass.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test BeBoosted.slnx`
Expected: all pass, 0 warnings.

- [ ] **Step 9: Commit**

```bash
git add src/BeBoosted.Desktop tests/BeBoosted.Desktop.Tests
git commit -m "feat: undo a session without reopening its task"
```

---

### Task 5: Carry a session title through the service

**Files:**
- Modify: `src/BeBoosted.Application/Calendar/CalendarService.cs:11-12` (`TaskScheduleRequest`), `:181-200` (`AddSession`), `:115-178` (`UpdateSessionSchedule`), `CreateTask`'s session construction
- Test: `tests/BeBoosted.Tests/Calendar/SessionScheduleEditingTests.cs`

**Interfaces:**
- Consumes: `CalendarBlock.Retitle` and the `title` parameter on `CreateTaskSession` from Task 1.
- Produces: `TaskScheduleRequest(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, RecurrenceRule? Recurrence, string? Title = null)`. `Title` is trailing and optional, so no existing construction site changes.

- [ ] **Step 1: Write the failing tests**

Append to `tests/BeBoosted.Tests/Calendar/SessionScheduleEditingTests.cs`, using that file's existing fixture:

```csharp
    [Fact]
    public void AddSession_WithATitle_PersistsIt()
    {
        var task = AddTask("Read Jane Eyre 1-20");

        var session = _service.AddSession(
            task.Id,
            new TaskScheduleRequest(
                Tuesday, new TimeOnly(9, 0), new TimeOnly(10, 0), null, "Jane Eyre 1-10"));

        Assert.Equal("Jane Eyre 1-10", session.Title);
        var restarted = new SqliteCalendarBlockRepository(_database.Factory);
        Assert.Equal("Jane Eyre 1-10", restarted.GetById(session.Id)!.Title);
    }

    [Fact]
    public void UpdateSessionSchedule_RetitlesTheSession_AndBlankClearsIt()
    {
        var task = AddTask("Read Jane Eyre 1-20");
        var session = _service.AddSession(
            task.Id,
            new TaskScheduleRequest(
                Tuesday, new TimeOnly(9, 0), new TimeOnly(10, 0), null, "Jane Eyre 1-10"));

        _service.UpdateSessionSchedule(
            task.Id, session.Id,
            new TaskScheduleRequest(
                Tuesday, new TimeOnly(9, 0), new TimeOnly(10, 0), null, "Jane Eyre 1-12"));
        Assert.Equal("Jane Eyre 1-12", _blocks.GetById(session.Id)!.Title);

        _service.UpdateSessionSchedule(
            task.Id, session.Id,
            new TaskScheduleRequest(
                Tuesday, new TimeOnly(9, 0), new TimeOnly(10, 0), null, "   "));

        Assert.Null(_blocks.GetById(session.Id)!.Title);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~WithATitle|FullyQualifiedName~RetitlesTheSession"`
Expected: FAIL to compile — `TaskScheduleRequest` takes 4 arguments.

- [ ] **Step 3: Extend the request record**

In `src/BeBoosted.Application/Calendar/CalendarService.cs`:

```csharp
/// <summary>
/// A session's schedule. <paramref name="Title"/> names this one sitting
/// ("Jane Eyre 1-10"); null or blank falls back to the Task's title.
/// </summary>
public sealed record TaskScheduleRequest(
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, RecurrenceRule? Recurrence,
    string? Title = null);
```

- [ ] **Step 4: Pass the title through `AddSession`**

In `AddSession`, replace the block construction:

```csharp
        var block = CalendarBlock.CreateTaskSession(
            taskId, schedule.Date, schedule.StartTime, schedule.EndTime, clock.Now,
            schedule.Recurrence, schedule.Title);
```

Apply the same change wherever `CreateTask` builds its initial session from a `TaskScheduleRequest`.

- [ ] **Step 5: Apply the title in `UpdateSessionSchedule`**

In `UpdateSessionSchedule`, directly after the existing `session.SetRecurrence(schedule.Recurrence, now);`:

```csharp
        session.Retitle(schedule.Title, now);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/BeBoosted.Tests/BeBoosted.Tests.csproj --filter "FullyQualifiedName~WithATitle|FullyQualifiedName~RetitlesTheSession"`
Expected: PASS, 2 tests.

Then: `dotnet test BeBoosted.slnx` — all pass.

- [ ] **Step 7: Commit**

```bash
git add src/BeBoosted.Application/Calendar/CalendarService.cs tests/BeBoosted.Tests/Calendar/SessionScheduleEditingTests.cs
git commit -m "feat: carry a session title through scheduling"
```

---

### Task 6: Show the parent task as row subtext

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs:54-80` (`ForOccurrence`)
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs:248-270` (`BuildOccurrenceRow`)
- Modify: `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml`
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs`

**Interfaces:**
- Consumes: Task 5's persisted session title.
- Produces: `DailyRowViewModel.ParentTitle` (`string?`, null unless the block carries its own title) and `HasParentTitle` (`bool`).

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void ATitledSessionRow_LeadsWithItsOwnTitle_AndNamesTheParentTask()
    {
        var shell = TestShell.Create();
        var calendar = shell.Calendar;
        var task = calendar.Service.CreateTask(
            new TaskDetailsRequest("Read Jane Eyre 1-20", null, null, null),
            new TaskScheduleRequest(
                TestShell.DesignDate, new TimeOnly(9, 0), new TimeOnly(10, 0), null,
                "Jane Eyre 1-10"));
        var list = calendar.DailyList;
        list.Reload();

        var row = list.ScheduledRows.Single();

        Assert.Equal("Jane Eyre 1-10", row.Title);
        Assert.Equal("Read Jane Eyre 1-20", row.ParentTitle);
        Assert.True(row.HasParentTitle);
    }

    [Fact]
    public void AnUntitledSessionRow_ShowsTheTaskTitle_AndNoParentSubtext()
    {
        var shell = TestShell.Create();
        var calendar = shell.Calendar;
        calendar.Service.CreateTask(
            new TaskDetailsRequest("Read Jane Eyre 1-20", null, null, null),
            new TaskScheduleRequest(
                TestShell.DesignDate, new TimeOnly(9, 0), new TimeOnly(10, 0), null));
        var list = calendar.DailyList;
        list.Reload();

        var row = list.ScheduledRows.Single();

        Assert.Equal("Read Jane Eyre 1-20", row.Title);
        Assert.Null(row.ParentTitle);
        Assert.False(row.HasParentTitle);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~SessionRow_LeadsWith|FullyQualifiedName~AnUntitledSessionRow"`
Expected: FAIL to compile — `'DailyRowViewModel' does not contain a definition for 'ParentTitle'`.

- [ ] **Step 3: Add the properties**

In `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs`, add near `Title`:

```csharp
    /// <summary>
    /// The owning Task's title, set only when this session carries its own title —
    /// the row then leads with the session and names its parent beneath.
    /// </summary>
    public string? ParentTitle { get; internal set; }

    public bool HasParentTitle => ParentTitle is not null;
```

Add `ParentTitle` to the object initializer in `ForOccurrence` by way of a new parameter:

```csharp
    internal static DailyRowViewModel ForOccurrence(
        DailyListViewModel owner,
        BlockOccurrence occurrence,
        string title,
        string? parentTitle,
        string? projectName,
        PriorityRank? rank,
        TaskItem? task,
        bool isConflicted,
        bool isDone,
        bool needsOutcome)
```

and set `ParentTitle = parentTitle,` in the initializer. Update the single call site in `BuildOccurrenceRow`.

- [ ] **Step 4: Populate it**

In `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs`, in `BuildOccurrenceRow`, directly below the existing `var title = block.Title ?? task?.Title ?? "(deleted task)";`:

```csharp
        // Only a session with its own title needs its parent named beneath it.
        var parentTitle = block.Title is not null && !block.IsExternal ? task?.Title : null;
```

and pass `parentTitle` as the new argument.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~SessionRow_LeadsWith|FullyQualifiedName~AnUntitledSessionRow"`
Expected: PASS, 2 tests.

- [ ] **Step 6: Render the subtext**

In `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml`, find the row's existing secondary/meta line (the one carrying project name and deadline) and prepend a `TextBlock` bound to `ParentTitle` with `IsVisible="{Binding HasParentTitle}"`, using the same `Classes` as the neighbouring meta text and a `·` separator consistent with the rest of the file.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test BeBoosted.slnx`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/BeBoosted.Desktop tests/BeBoosted.Desktop.Tests
git commit -m "feat: a titled session names its parent task beneath it"
```

---

### Task 7: Count progress in sessions

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs:136-225` (the counting locals and `ProgressText`)
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs`

**Interfaces:**
- Consumes: Task 4's independent session completion.
- Produces: no new members. `ProgressText` changes meaning: `done` counts resolved-as-Done sessions plus tasks completed today that no session represents; `total` adds unresolved sessions and open unscheduled tasks.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void ProgressCountsSessions_SoATaskWithTwoSessionsIsTwoUnits()
    {
        var shell = TestShell.Create();
        var calendar = shell.Calendar;
        var task = calendar.Service.CreateTask(
            new TaskDetailsRequest("Read Jane Eyre 1-20", null, null, null),
            new TaskScheduleRequest(
                TestShell.DesignDate, new TimeOnly(9, 0), new TimeOnly(10, 0), null));
        calendar.Service.AddSession(
            task.Id,
            new TaskScheduleRequest(
                TestShell.DesignDate, new TimeOnly(19, 0), new TimeOnly(20, 0), null));
        var list = calendar.DailyList;
        list.Reload();
        Assert.Equal("0 of 2 complete", list.ProgressText);

        list.ScheduledRows.First(r => r.StartTime == new TimeOnly(9, 0))
            .ToggleDoneCommand.Execute(null);

        Assert.Equal("1 of 2 complete", list.ProgressText);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~ProgressCountsSessions"`
Expected: FAIL — currently both sessions belong to one task, so the day reports `0 of 1` then `1 of 1`.

- [ ] **Step 3: Count sessions instead of task ids**

In `Reload()`, replace the one-off arms of the counting logic. `doneTaskIds` still exists, but only to stop a task completed today being double-counted by the later `_tasks.GetAll()` sweep — it no longer drives the numerator for sessions.

In the `row.IsDone` arm, replace the `else if (!block.IsExternal && block.TaskId is { } doneId)` branch with:

```csharp
                else if (!block.IsExternal)
                {
                    doneSessionCount++;
                    if (block.TaskId is { } doneId)
                    {
                        // Suppresses the later completed-task sweep for this task, so a
                        // task finished today is not counted twice.
                        doneTaskIds.Add(doneId);
                    }
                }
```

In the `else` (open) arm, replace `else if (block.TaskId is { } openId) { openTaskIds.Add(openId); }` with:

```csharp
                else
                {
                    openSessionCount++;
                }
```

Declare `var doneSessionCount = 0;` and `var openSessionCount = 0;` beside the existing occurrence counters, and replace the totals:

```csharp
        openTaskIds.ExceptWith(doneTaskIds);
        var done = doneOccurrenceCount + doneSessionCount + doneTaskIds.Count;
        var total = done + openOccurrenceCount + openSessionCount + openTaskIds.Count;
```

`doneTaskIds.Count` must not include ids added by the session arm above, or a finished session counts twice. Track the completed-task sweep separately:

```csharp
        var completedTaskCount = 0;
```

increment it in the `_tasks.GetAll()` loop that adds to `completed`, and use `completedTaskCount` in place of `doneTaskIds.Count` in the `done` expression. Keep `doneTaskIds` purely as the suppression set.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~ProgressCountsSessions"`
Expected: PASS.

- [ ] **Step 5: Restate existing progress assertions**

Run: `dotnet test BeBoosted.slnx`

Any daily-list test asserting `ProgressText` for a scheduled task now reports in sessions. Update each expected string to the session count. This is the deliberate change, not a regression — but check each one: if a number changes for a day with exactly one session per task, something is wrong, because those totals should be unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs tests/BeBoosted.Desktop.Tests
git commit -m "feat: the day's progress counts sessions, not tasks"
```

---

### Task 8: A title field in the session editor

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/SessionEditorViewModel.cs`
- Modify: `src/BeBoosted.Desktop/Views/SessionEditorView.axaml`
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/SessionEditorViewModelTests.cs`

**Interfaces:**
- Consumes: `TaskScheduleRequest.Title` from Task 5.
- Produces: `SessionEditorViewModel.SessionTitle` (`string`, two-way bound, empty when the session has none) and `TitlePlaceholder` (`string`, the parent task's title).

Do **not** add a completion control for one-off sessions here — the spec lists that as a non-goal, and `UpdateSessionSchedule` applies `occurrenceCompletion` only when `session.Recurrence is not null`.

- [ ] **Step 1: Write the failing test**

Append to `tests/BeBoosted.Desktop.Tests/ViewModels/SessionEditorViewModelTests.cs`, using its `Create()` / `AddTask` / `AddSession` / `Open` helpers:

```csharp
    [Fact]
    public void SavingASessionTitle_PersistsIt_AndBlankClearsIt()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));

        var editor = Open(context, session);
        editor.SessionTitle = "Jane Eyre 1-10";
        editor.SaveCommand.Execute(null);

        Assert.Equal("Jane Eyre 1-10", context.Blocks.GetById(session.Id)!.Title);

        var reopened = Open(context, context.Blocks.GetById(session.Id)!);
        Assert.Equal("Jane Eyre 1-10", reopened.SessionTitle);
        reopened.SessionTitle = "   ";
        reopened.SaveCommand.Execute(null);

        Assert.Null(context.Blocks.GetById(session.Id)!.Title);
    }

    [Fact]
    public void TheTitlePlaceholder_IsTheParentTaskTitle_SoTheFieldReadsAsOptional()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));

        var editor = Open(context, session);

        Assert.Equal("Read Jane Eyre 1-20", editor.TitlePlaceholder);
        Assert.Equal(string.Empty, editor.SessionTitle); // untitled session
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~SessionTitle|FullyQualifiedName~TitlePlaceholder"`
Expected: FAIL to compile — `SessionTitle` is not defined.

- [ ] **Step 3: Add the properties**

In `src/BeBoosted.Desktop/ViewModels/SessionEditorViewModel.cs`:

```csharp
    /// <summary>
    /// This sitting's own name ("Jane Eyre 1-10"). Empty keeps the Task's title,
    /// which is what the placeholder shows.
    /// </summary>
    [ObservableProperty]
    public partial string SessionTitle { get; set; } = string.Empty;

    public string TitlePlaceholder => TaskTitle;
```

Seed `SessionTitle` in the constructor from the session's existing `Title` (`?? string.Empty`), beside where the other schedule fields are seeded.

- [ ] **Step 4: Send it on save**

Find where the editor builds its `TaskScheduleRequest` for save and add `SessionTitle` as the trailing argument. Because the record's `Title` is optional and trailing, no other construction site changes.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~SessionTitle|FullyQualifiedName~TitlePlaceholder"`
Expected: PASS, 2 tests.

- [ ] **Step 6: Add the field to the view**

In `src/BeBoosted.Desktop/Views/SessionEditorView.axaml`, add above the date/time fields:

```xml
<TextBox Text="{Binding SessionTitle}"
         Watermark="{Binding TitlePlaceholder}"
         AutomationProperties.Name="Session title" />
```

Match the surrounding field styling — read the neighbouring `TextBox` elements and copy their `Classes`, `Margin`, and label treatment rather than dropping a bare control in.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test BeBoosted.slnx`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/BeBoosted.Desktop tests/BeBoosted.Desktop.Tests
git commit -m "feat: name a session in the session editor"
```

---

### Task 9: Complete a one-off session from the project page

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/ProjectDetailViewModel.cs` (`ScheduledBlockRowViewModel.HasCompletionControl`, `ToggleCompletion`, and a new `ProjectDetailViewModel.SetSessionCompletion`)
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/ShellProjectRefreshTests.cs`

**Interfaces:**
- Consumes: `CalendarService.ClearSessionOutcome` (Task 3) and the decoupled `RecordOutcome` (Task 2).
- Produces: `ProjectDetailViewModel.SetSessionCompletion(CalendarBlockId blockId, bool completed)` (internal) and `ScheduledBlockRowViewModel.IsRepeating` (public).

- [ ] **Step 1: Write the failing test**

Append to `tests/BeBoosted.Desktop.Tests/ViewModels/ShellProjectRefreshTests.cs`, which already has `CreateShell()` and `CreateProjectWithScheduledTask(shell, blocks, tasks, repeating)`:

```csharp
    /// <summary>
    /// The project page completes a one-off session against its block. Routing it
    /// through the occurrence path would throw — a one-off has no occurrences.
    /// </summary>
    [Fact]
    public void CompletingAOneOffSessionFromTheProjectPage_ResolvesThatSessionOnly()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks);
        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");
        var sibling = shell.Calendar.Service.AddSession(
            task.Id,
            new BeBoosted.Application.Calendar.TaskScheduleRequest(
                Tomorrow, new TimeOnly(19, 0), new TimeOnly(20, 0), null));
        shell.NavigateCommand.Execute(AppSection.Projects);
        Assert.Equal(2, shell.Projects.Detail!.ScheduledBlocks.Count);

        shell.Projects.Detail.ScheduledBlocks.Single(r => r.BlockId == blockId)
            .ToggleCompletionCommand.Execute(null);

        var done = Assert.Single(shell.Projects.Detail!.CompletedScheduledBlocks);
        Assert.Equal(blockId, done.BlockId);
        Assert.Equal(BlockOutcome.None, blocks.GetById(sibling.Id)!.Outcome);
        Assert.False(tasks.GetById(task.Id)!.IsCompleted);
    }
```

If `shell.Calendar` does not expose `Service`, add the sibling session by constructing a `CalendarBlock.CreateTaskSession(...)` and calling `blocks.Add(...)` directly, as `DailyListViewModelTests` does.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~CompletingAOneOffSessionFromTheProjectPage"`

Expected: FAIL. `HasCompletionControl` is currently false for a one-off row, so no toggle happens. **If you instead see a `DomainException` reading "A one-off session records an outcome, not an occurrence completion", you have flipped `HasCompletionControl` without changing the routing — that is the bug this task exists to avoid.**

- [ ] **Step 3: Open the control to one-off sessions**

In `ScheduledBlockRowViewModel`:

```csharp
    /// <summary>Every local session completes here; external events never do.</summary>
    public bool HasCompletionControl => !_row.Block.IsExternal;

    /// <summary>A repeating session completes per occurrence; a one-off by outcome.</summary>
    public bool IsRepeating => _row.Block.Recurrence is not null;
```

- [ ] **Step 4: Route one-off rows to the outcome path**

`CalendarBlock.EnsureOccurrenceCompletable` throws for a one-off session, so `SetOccurrenceCompletion` must not receive one. In `ScheduledBlockRowViewModel.ToggleCompletion`:

```csharp
    [RelayCommand]
    private void ToggleCompletion()
    {
        if (IsRepeating)
        {
            _owner.SetOccurrenceCompletion(_row.Block.Id, Date, !IsDone);
        }
        else
        {
            _owner.SetSessionCompletion(_row.Block.Id, !IsDone);
        }
    }
```

And on `ProjectDetailViewModel`, beside the existing `SetOccurrenceCompletion`:

```csharp
    /// <summary>
    /// One one-off session's completion, recorded against the block. The Task stays
    /// open — only the Task's own control completes it.
    /// </summary>
    internal void SetSessionCompletion(Domain.CalendarBlockId blockId, bool completed)
    {
        try
        {
            if (completed)
            {
                _calendar.RecordOutcome(blockId, BlockOutcome.Done);
            }
            else if (!_calendar.ClearSessionOutcome(blockId))
            {
                return;
            }
        }
        catch (DomainException)
        {
            return; // a stale row: the service mutated nothing
        }

        _owner.NotifyTasksMutated();
    }
```

Add the `BeBoosted.Domain.Calendar` and `BeBoosted.Domain` usings if they are not already present.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj --filter "FullyQualifiedName~CompletingAOneOffSessionFromTheProjectPage"`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test BeBoosted.slnx`
Expected: all pass, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/BeBoosted.Desktop tests/BeBoosted.Desktop.Tests
git commit -m "feat: complete a one-off session from the project page"
```

---

### Task 10: Verify in the running app

Tests do not catch XAML binding failures or layout regressions. This task exists because a previous change in this repo shipped a copy bug that only a screenshot caught.

**Files:** none modified unless a defect is found.

- [ ] **Step 1: Build and launch**

```bash
dotnet build BeBoosted.slnx
./bb
```

If the build fails with MSB3027 (`BeBoosted.exe` locked), close the running app first.

- [ ] **Step 2: Schedule two sessions of one task**

Create or pick a task, schedule two sessions on the same day, and give one a distinct title through the session editor.

- [ ] **Step 3: Check the titled row**

Confirm the titled row leads with the session title and names the parent task beneath it, and that the untitled row still shows the task's title with no subtext line.

- [ ] **Step 4: Mark one session done**

Confirm: that row moves to Completed, the other stays in Scheduled, the task does **not** appear in Unscheduled, and the task is not struck through or marked complete anywhere.

- [ ] **Step 5: Mark the second done**

Confirm the task now appears in Unscheduled, and the progress counter reads 2 of 2 for those sessions.

- [ ] **Step 6: Undo one**

Confirm that session returns to Scheduled, its sibling stays in Completed, and the task leaves Unscheduled again.

- [ ] **Step 7: Check the project page**

Open the task's project and toggle a one-off session row's completion. Confirm it completes without an error notice.

- [ ] **Step 8: Commit any fixes**

If any step reveals a defect, fix it with a failing test first, then commit.

---

## Self-Review

**Spec coverage:**

| Spec requirement | Task |
| --- | --- |
| Session Done resolves that session only | 2 |
| Task returns to Unscheduled when all sessions resolve | inherited from `GetInboxTasks`; asserted in 4 |
| Multiple sessions complete independently | 2, 4 |
| Optional session title, parent as subtext | 1, 5, 6, 8 |
| Per-session undo | 3, 4 |
| Repeating-sibling gate removed, dead members deleted | 4 |
| `EnsureOccurrenceCompletable` message corrected | 1 |
| Progress counts sessions | 7 |
| Project page one-off completion (without throwing) | 9 |
| Task→sessions cascade preserved | asserted in 2, step 5 |
| No migration | Global Constraints |
| Session editor gains no one-off completion control | 8, stated explicitly |

**Placeholder scan:** Clean. An earlier draft left comment-only test skeletons in Tasks 4, 8, and 9; the fixtures in `DailyListViewModelTests` (`Create()` / `AddTask` / `context.Service.ScheduleTask`), `SessionEditorViewModelTests` (`Create` / `AddTask` / `AddSession` / `Open`), and `ShellProjectRefreshTests` (`CreateShell` / `CreateProjectWithScheduledTask`) were then read and every skeleton replaced with literal, compiling test code. No step now says "similar to" or defers a decision.

Two steps still carry a conditional fallback — Task 4 step 1 and Task 9 step 1 name the exact helper to fall back to if an accessor is not exposed under the assumed name. These are hedges against a member's visibility, not missing content: the primary code is complete either way.

**Type consistency:** `ClearSessionOutcome(CalendarBlockId) -> bool` is used identically in Tasks 3, 4, and 9. `Retitle(string?, DateTimeOffset)` in Tasks 1 and 5. `TaskScheduleRequest`'s trailing `string? Title = null` in Tasks 5, 6, 7, and 8. `ParentTitle` / `HasParentTitle` in Task 6 only. `SessionTitle` / `TitlePlaceholder` in Task 8 only.
