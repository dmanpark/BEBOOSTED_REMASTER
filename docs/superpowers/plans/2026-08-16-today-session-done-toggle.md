# Today Session Done Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give one-off scheduled task sessions on the Today page a one-click done checkbox that also un-does, without losing the *Needs more time* and *Didn't happen* outcomes.

**Architecture:** Pure presentation change. `CalendarService.RecordOutcome(id, Done, null)` and `CalendarService.ReopenTask(taskId)` already implement both directions and are already used elsewhere; the Today page simply never wired them to a `Session` row. Work is confined to two view models, one view, and the shared icon dictionary.

**Tech Stack:** .NET 10, C#, Avalonia 12, CommunityToolkit.Mvvm (`[RelayCommand]`, `[ObservableProperty]`), xUnit, Avalonia.Headless for UI tests.

Spec: `docs/superpowers/specs/2026-08-16-today-session-done-toggle-design.md`

## Global Constraints

- No service, domain, repository, or persistence changes. No migration.
- Do not touch `docs/qa/` or screenshot baselines. The three screenshot-capture tests stay skipped.
- Strict TDD: every production edit is preceded by a test watched failing for the right reason.
- Existing validation and transaction boundaries stay as they are.
- Preserve imported calendar events as read-only (`IsExternal` rows are never given a checkbox).
- `dotnet format BeBoosted.slnx --verify-no-changes --no-restore` must stay clean; build runs with `-warnaserror`.
- Repeating-completion copy, verbatim: `This task repeats — complete each repeating occurrence separately.`
- Do not stage, commit, or push unless the user asks.

---

### Task 1: Session rows toggle done and undone

The behavioral core. A `Session` row's checkbox marks the session done (before or after its slot) and takes it back.

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs:267` (`CanRecordDone` area), `:277` (`CanReopen`), `:349-367` (`ToggleDone`)
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs:386-397` (`ReopenRow`)
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs`

**Interfaces:**
- Consumes: `CalendarService.RecordOutcome(CalendarBlockId, BlockOutcome, TimeSpan?)`, `CalendarService.ReopenTask(TaskId) -> bool`, `CalendarViewModel.RecordOutcome`, `CalendarViewModel.NotifyTasksMutated`, existing `DailyRowViewModel.TaskRepeats`.
- Produces: `DailyRowViewModel.CanToggleSessionDone -> bool` (used by Task 2's tests and Task 3's `IsEnabled` binding); `ToggleDoneCommand` now accepting `DailyRowKind.Session`; `CanReopen -> bool` true for done Session rows.

- [x] **Step 1: Write the failing tests**

Add to `tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs`, in the row-mutations region (after `RecordingDone_MovesBlockRowToCompleted`):

```csharp
// ---- Scheduled sessions: one-click done and undo ----

[Fact]
public void ElapsedSessionRow_TogglesDoneInOneClick()
{
    var context = Create();
    var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
    context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0)); // ended 10:00 < now 14:10
    context.Calendar.Reload();
    Assert.True(context.Daily.ScheduledRows.Single().NeedsOutcome);

    context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);

    Assert.Empty(context.Daily.ScheduledRows);
    var completed = context.Daily.CompletedRows.Single();
    Assert.Equal("Elapsed work", completed.Title);
    Assert.True(completed.IsDone);
    Assert.True(context.Tasks.GetById(task.Id)!.IsCompleted);
}

[Fact]
public void UpcomingSessionRow_TogglesDoneBeforeItsSlotStarts()
{
    var context = Create();
    var task = AddTask(context, "Evening work", TimeSpan.FromMinutes(60));
    context.Service.ScheduleTask(task.Id, Date, new TimeOnly(20, 0)); // starts 20:00 > now 14:10
    context.Calendar.Reload();
    var row = context.Daily.ScheduledRows.Single();
    Assert.False(row.NeedsOutcome); // not elapsed — and elapsing was never a gate

    row.ToggleDoneCommand.Execute(null);

    Assert.True(context.Daily.CompletedRows.Single().IsDone);
    Assert.True(context.Tasks.GetById(task.Id)!.IsCompleted);
}

[Fact]
public void DoneSessionRow_TogglesBackToScheduled_ClearingItsOutcome()
{
    var context = Create();
    var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
    var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
    context.Calendar.Reload();
    context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);
    var completed = context.Daily.CompletedRows.Single();
    Assert.True(completed.CanReopen);

    completed.ToggleDoneCommand.Execute(null);

    Assert.Empty(context.Daily.CompletedRows);
    var reopened = context.Daily.ScheduledRows.Single();
    Assert.Equal("Elapsed work", reopened.Title);
    Assert.False(reopened.IsDone);
    Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    Assert.Equal(BlockOutcome.None, context.Blocks.GetById(block.Id)!.Outcome);
}

[Fact]
public void SessionRow_SurvivesADoneUndoneDoneRoundTrip()
{
    var context = Create();
    var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
    var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
    context.Calendar.Reload();

    var changes = 0;
    context.Calendar.DataChanged += () => changes++;

    context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);
    context.Daily.CompletedRows.Single().ToggleDoneCommand.Execute(null);
    context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);

    Assert.Empty(context.Daily.ScheduledRows);
    Assert.True(context.Daily.CompletedRows.Single().IsDone);
    Assert.True(context.Tasks.GetById(task.Id)!.IsCompleted);
    Assert.Equal(BlockOutcome.Done, context.Blocks.GetById(block.Id)!.Outcome);
    Assert.Equal(3, changes); // exactly one announcement per click, both directions
}

/// <summary>
/// Done is a whole-Task statement, so a repeating sibling forbids it. The
/// service would reject it anyway; the row must not even offer it.
/// </summary>
[Fact]
public void SessionRowWhoseTaskAlsoRepeats_CannotToggleDone()
{
    var context = Create();
    var task = AddTask(context, "Mixed work");
    var session = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
    context.Blocks.Add(CalendarBlock.CreateTaskSession(
        task.Id, Date, new TimeOnly(16, 0), new TimeOnly(17, 0), context.Clock.Now,
        RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)));
    context.Calendar.Reload();

    var row = context.Daily.ScheduledRows.Single(r => r.BlockId == session.Id);
    Assert.False(row.CanToggleSessionDone);
    Assert.False(row.ToggleDoneCommand.CanExecute(null));

    row.ToggleDoneCommand.Execute(null); // a direct invocation records nothing

    Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    Assert.Equal(BlockOutcome.None, context.Blocks.GetById(session.Id)!.Outcome);
}

/// <summary>
/// Undo is never gated on the repeating rule: SetTaskCompletion allows reopening
/// unconditionally so a globally-completed repeating Task can recover, and the
/// row must not seal that path off.
/// </summary>
[Fact]
public void GloballyCompletedTaskWithARepeatingSibling_CanStillBeToggledBack()
{
    var context = Create();
    var task = AddTask(context, "Mixed work");
    var session = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
    context.Calendar.Reload();
    context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null); // done while solo
    // A repeating sibling appears afterwards: the Task is now globally complete
    // AND repeating — the corrupted shape reopening exists to rescue.
    context.Blocks.Add(CalendarBlock.CreateTaskSession(
        task.Id, Date, new TimeOnly(16, 0), new TimeOnly(17, 0), context.Clock.Now,
        RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)));
    context.Calendar.Reload();

    var completed = context.Daily.CompletedRows.Single(r => r.BlockId == session.Id);
    Assert.True(completed.CanToggleSessionDone); // undo stays open
    completed.ToggleDoneCommand.Execute(null);

    Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    Assert.Equal(BlockOutcome.None, context.Blocks.GetById(session.Id)!.Outcome);
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj \
  --filter "FullyQualifiedName~DailyListViewModelTests"
```
Expected: compile errors — `DailyRowViewModel` has no `CanToggleSessionDone`. After adding only that property (Step 3a), the remaining failures must be: `ElapsedSessionRow_TogglesDoneInOneClick` and `UpcomingSessionRow_TogglesDoneBeforeItsSlotStarts` leaving `ScheduledRows` non-empty (`ToggleDone` ignores `Session`); `DoneSessionRow_...` and `SessionRow_SurvivesA...` failing on `CanReopen`/`ScheduledRows`; `GloballyCompletedTask...` failing to reopen.

- [x] **Step 3: Implement**

In `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs`, next to `CanRecordDone`, add:

```csharp
/// <summary>
/// Done is a whole-Task statement: while any sibling session repeats, this one-off
/// may not record it. Undo stays open even then — reopening is deliberately
/// unconditional so a globally-completed repeating Task can recover. Always true
/// for non-Session rows, which never carry a repeating sibling.
/// </summary>
public bool CanToggleSessionDone => !TaskRepeats || IsDone;
```

Replace `CanReopen`:

```csharp
public bool CanReopen => IsDone
    && (Kind is DailyRowKind.Task or DailyRowKind.Session
        || (Kind == DailyRowKind.Obligation && !IsExternal));
```

Replace `ToggleDone` and its attribute:

```csharp
/// <summary>The row's checkbox: complete/reopen a task or a scheduled session,
/// or check off one occurrence of a repeating schedule.</summary>
[RelayCommand(CanExecute = nameof(CanToggleSessionDone))]
private void ToggleDone()
{
    switch (Kind)
    {
        case DailyRowKind.Obligation:
            _owner.ToggleOccurrenceDone(this);
            break;
        case DailyRowKind.Task or DailyRowKind.Session when IsDone:
            _owner.ReopenRow(this);
            break;
        case DailyRowKind.Task:
            _owner.CompleteTask(this);
            break;
        case DailyRowKind.Session:
            // Done on a session is the same aggregate transition as completing the
            // Task, recorded against the block that carried the work.
            _owner.RecordOutcome(this, BlockOutcome.Done, null);
            break;
    }
}
```

In `src/BeBoosted.Desktop/ViewModels/DailyListViewModel.cs`, replace `ReopenRow`:

```csharp
internal void ReopenRow(DailyRowViewModel row)
{
    if (row.Kind == DailyRowKind.Obligation && row.BlockId is { } blockId)
    {
        _owner.SetOccurrenceDone(blockId, row.Date, done: false);
    }
    else if (row.Kind is DailyRowKind.Task or DailyRowKind.Session
        && row.TaskId is { } taskId
        && _calendar.ReopenTask(taskId))
    {
        // Reopening clears every Done one-off session of the Task — the exact
        // inverse of marking one done, which resolves them all together.
        _owner.NotifyTasksMutated();
    }
}
```

- [x] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj \
  --filter "FullyQualifiedName~DailyListViewModelTests"
```
Expected: the six new tests PASS. `RecordingDone_MovesBlockRowToCompleted` now FAILS on `Assert.False(completedRow.CanReopen)` — that is the deliberate reversal, fixed in Task 2. Leave it failing until then.

---

### Task 2: Completion-cluster surface properties

Swap the outcome-flyout button for a checkbox at the view-model level, and demote the other two outcomes to a side action.

**Files:**
- Modify: `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs:258` (`ShowOutcomeControl`), `:275` (`ShowDoneBlockMarker`)
- Test: `tests/BeBoosted.Desktop.Tests/ViewModels/DailyListViewModelTests.cs:213`, `:486-493`, `:781`

**Interfaces:**
- Consumes: `CanToggleSessionDone` from Task 1.
- Produces: `DailyRowViewModel.ShowSessionCheck -> bool`, `DailyRowViewModel.ShowSessionOutcomeAction -> bool`, `DailyRowViewModel.ShowSessionCheckBlockedNote -> bool` (all bound by Task 3). `ShowOutcomeControl` and `ShowDoneBlockMarker` no longer exist.

- [x] **Step 1: Write the failing tests and update the three that assert the old contract**

Add to `DailyListViewModelTests.cs` after the Task 1 tests:

```csharp
[Fact]
public void SessionRow_ShowsACheckbox_AndKeepsTheOtherOutcomesAsASideAction()
{
    var context = Create();
    var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
    context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
    context.Calendar.Reload();

    var row = context.Daily.ScheduledRows.Single();
    Assert.True(row.ShowSessionCheck);
    Assert.True(row.ShowSessionOutcomeAction);
    Assert.True(row.CanToggleSessionDone);

    row.ToggleDoneCommand.Execute(null);

    var completed = context.Daily.CompletedRows.Single();
    Assert.True(completed.ShowSessionCheck);          // the undo affordance
    Assert.False(completed.ShowSessionOutcomeAction); // nothing left to record
}

[Fact]
public void SideActionOutcomes_StillWork_FromTheirNewHome()
{
    var context = Create();
    var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
    var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
    context.Calendar.Reload();
    var row = context.Daily.ScheduledRows.Single();
    Assert.True(row.ShowSessionOutcomeAction);
    row.RemainingMinutes = 45;

    row.RecordNeedsMoreTimeCommand.Execute(null);

    Assert.Equal(BlockOutcome.NeedsMoreTime, context.Blocks.GetById(block.Id)!.Outcome);
    Assert.Equal(TimeSpan.FromMinutes(45), context.Tasks.GetById(task.Id)!.EstimatedDuration);
    Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
}

[Fact]
public void DidntHappen_StillWorks_FromTheSideAction()
{
    var context = Create();
    var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
    var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
    context.Calendar.Reload();

    context.Daily.ScheduledRows.Single().RecordDidntHappenCommand.Execute(null);

    Assert.Equal(BlockOutcome.DidntHappen, context.Blocks.GetById(block.Id)!.Outcome);
    Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
}
```

Then update the three existing assertions:

In `ElapsedBlockWithoutOutcome_ShowsNeedsOutcome` (~line 213), replace
`Assert.True(row.ShowOutcomeControl);` with:
```csharp
        Assert.True(row.ShowSessionCheck);
        Assert.True(row.ShowSessionOutcomeAction);
```

In `RecordingDone_MovesBlockRowToCompleted` (~lines 490-491), replace
`Assert.True(completedRow.ShowDoneBlockMarker);` and
`Assert.False(completedRow.CanReopen); // outcomes have no invented binary reopen` with:
```csharp
        Assert.True(completedRow.ShowSessionCheck);
        Assert.True(completedRow.CanReopen); // a mis-click is taken back from this page
```

In `MixedScheduleSessionRow_OffersOutcomes_ButNeverGlobalDone` (~line 781), replace
`Assert.True(row.ShowOutcomeControl);` with:
```csharp
        Assert.True(row.ShowSessionCheck);
        Assert.False(row.CanToggleSessionDone); // present but disabled
        Assert.True(row.ShowSessionOutcomeAction);
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj \
  --filter "FullyQualifiedName~DailyListViewModelTests"
```
Expected: compile errors — `ShowSessionCheck` and `ShowSessionOutcomeAction` are not defined on `DailyRowViewModel`.

- [x] **Step 3: Implement**

In `src/BeBoosted.Desktop/ViewModels/DailyRowViewModel.cs`, replace

```csharp
public bool ShowOutcomeControl => Kind == DailyRowKind.Session && !IsDone;
```

with

```csharp
/// <summary>
/// A one-off scheduled session carries the same checkbox as every other row kind:
/// checked marks it done (before or after its slot), unchecked takes it back.
/// </summary>
public bool ShowSessionCheck => Kind == DailyRowKind.Session;

/// <summary>The quiet side action holding "Needs more time" and "Didn't happen".</summary>
public bool ShowSessionOutcomeAction => Kind == DailyRowKind.Session && !IsDone;

/// <summary>
/// A disabled control raises no tooltip, so the blocked checkbox needs its own
/// hit-testable surface to carry the explanation. Scoped to the blocked case only —
/// a done repeating-sibling row keeps its checkbox live for the undo.
/// </summary>
public bool ShowSessionCheckBlockedNote => ShowSessionCheck && !CanToggleSessionDone;
```

Delete `ShowDoneBlockMarker` and its doc comment:

```csharp
/// <summary>A done task block keeps its recorded outcome — no invented binary reopen.</summary>
public bool ShowDoneBlockMarker => Kind == DailyRowKind.Session && IsDone;
```

- [x] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj \
  --filter "FullyQualifiedName~DailyListViewModelTests"
```
Expected: all PASS. The build will still fail for `DailyTaskListView.axaml`, which binds the deleted names — fixed in Task 3. If the XAML compiler does not fail the build, Task 3's UI test catches it.

---

### Task 3: View — checkbox, disabled style, and the side outcome action

**Files:**
- Modify: `src/BeBoosted.Desktop/Styles/Icons.axaml` (add `IconMore`)
- Modify: `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml:52` (styles), `:180-228` (completion cluster), `:344-388` (actions strip)
- Test: `tests/BeBoosted.Desktop.Tests/Ui/DailyListUiTests.cs`

**Interfaces:**
- Consumes: `ShowSessionCheck`, `ShowSessionOutcomeAction`, `ShowSessionCheckBlockedNote`, `CanToggleSessionDone`, `ToggleDoneCommand`, `CompletionControlName`, `OutcomeControlName`, `RepeatingCompletionNote`, `RemainingMinutes`, `RecordNeedsMoreTimeCommand`, `RecordDidntHappenCommand` — all from Tasks 1-2 or already present.
- Produces: nothing consumed by later tasks.

- [x] **Step 1: Write the failing UI test**

Add to `tests/BeBoosted.Desktop.Tests/Ui/DailyListUiTests.cs`, after `TaskBlockRow_OutcomeMenu_RecordsDone`:

```csharp
[AvaloniaFact]
public void TaskBlockRow_CheckboxMarksDone_AndTakesItBack()
{
    var (window, shell, tasks, blocks, clock) = CreateShellWindow();
    var task = TaskItem.Create("Elapsed work", clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
    tasks.Add(task);
    var service = TestShell.CreateCalendarService(blocks, tasks, clock);
    service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(9, 0));
    shell.Calendar.Reload();
    window.CaptureRenderedFrame();

    // The scheduled row renders an enabled checkbox, not a flyout-only control.
    var check = DailyCheckFor(window, "Mark Elapsed work done");
    Assert.NotNull(check);
    Assert.True(check.IsEffectivelyVisible);
    Assert.True(check.IsEnabled);

    shell.Calendar.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);
    shell.Calendar.Daily.IsCompletedExpanded = true;
    window.CaptureRenderedFrame();

    // Done: the row is in Completed and its control now offers the undo.
    Assert.Empty(shell.Calendar.Daily.ScheduledRows);
    var reopen = DailyCheckFor(window, "Reopen Elapsed work");
    Assert.NotNull(reopen);
    Assert.True(reopen.IsEffectivelyVisible);

    shell.Calendar.Daily.CompletedRows.Single().ToggleDoneCommand.Execute(null);
    window.CaptureRenderedFrame();

    Assert.Single(shell.Calendar.Daily.ScheduledRows);
    Assert.False(tasks.GetById(task.Id)!.IsCompleted);
}

[AvaloniaFact]
public void TaskBlockRow_KeepsNeedsMoreTimeAndDidntHappen_AsASideAction()
{
    var (window, shell, tasks, blocks, clock) = CreateShellWindow();
    var task = TaskItem.Create("Elapsed work", clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
    tasks.Add(task);
    var service = TestShell.CreateCalendarService(blocks, tasks, clock);
    service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(9, 0));
    shell.Calendar.Reload();
    window.CaptureRenderedFrame();

    var outcome = window.GetVisualDescendants()
        .OfType<Button>()
        .FirstOrDefault(b => AutomationProperties.GetName(b) == "Record outcome for Elapsed work");
    Assert.NotNull(outcome);
    Assert.True(outcome.IsEffectivelyVisible);
    Assert.NotNull(outcome.Flyout);
}
```

`AutomationProperties` lives in `Avalonia.Automation`, which this file does not yet
import. Add to the using block at the top:

```csharp
using Avalonia.Automation;
```

Add this helper beside `FindText` at the top of the class:

```csharp
private static Button? DailyCheckFor(MainWindow window, string accessibleName)
    => window.GetVisualDescendants()
        .OfType<Button>()
        .FirstOrDefault(b => b.Classes.Contains("dailyCheck")
            && AutomationProperties.GetName(b) == accessibleName);
```

- [x] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj \
  --filter "FullyQualifiedName~DailyListUiTests"
```
Expected: FAIL — the view still binds `ShowOutcomeControl` / `ShowDoneBlockMarker`, so either the XAML load throws or no `dailyCheck` button carries the name `Mark Elapsed work done`.

- [x] **Step 3: Add the icon**

In `src/BeBoosted.Desktop/Styles/Icons.axaml`, after `IconClock`:

```xml
  <StreamGeometry x:Key="IconMore">M3.1,6 A0.65,0.65 0 1 1 1.8,6 A0.65,0.65 0 1 1 3.1,6 M6.65,6 A0.65,0.65 0 1 1 5.35,6 A0.65,0.65 0 1 1 6.65,6 M10.2,6 A0.65,0.65 0 1 1 8.9,6 A0.65,0.65 0 1 1 10.2,6</StreamGeometry>
```

- [x] **Step 4: Add the disabled checkbox style**

In `src/BeBoosted.Desktop/Views/DailyTaskListView.axaml`, after the `Button.dailyCheck.checked` style:

```xml
    <Style Selector="Button.dailyCheck:disabled">
      <Setter Property="Opacity" Value="0.4" />
      <Setter Property="BorderBrush" Value="{DynamicResource BrushRuleMedium}" />
    </Style>
```

- [x] **Step 5: Replace the completion cluster's session controls**

In `DailyTaskListView.axaml`, delete the whole `<!-- Approved task block: the outcome choices, never a binary checkbox -->` button (with its `Button.Flyout`) and the `<!-- A resolved block keeps its outcome: static marker, no invented reopen -->` `Border`, replacing both with:

```xml
            <!-- Scheduled task session: the same checkbox as every other row -->
            <Button Classes="dailyCheck" Classes.checked="{Binding IsDone}"
                    IsVisible="{Binding ShowSessionCheck}"
                    IsEnabled="{Binding CanToggleSessionDone}"
                    Command="{Binding ToggleDoneCommand}"
                    AutomationProperties.Name="{Binding CompletionControlName}"
                    ToolTip.Tip="{Binding CompletionControlName}">
              <Path Classes="icon" Data="{StaticResource IconCheck}" Width="9" Height="9"
                    StrokeThickness="1.8" IsVisible="{Binding IsDone}" />
            </Button>
```

Note: `IsEnabled` is bound as well as the command's `CanExecute` so the disabled visual applies even where the binding evaluates first.

For the repeating case the tooltip must explain itself. Immediately after that button, add:

```xml
            <!-- Repeating sibling: the checkbox is disabled and says why -->
            <Border Width="18" Height="18" Background="Transparent"
                    IsVisible="{Binding ShowSessionCheckBlockedNote}"
                    ToolTip.Tip="{Binding RepeatingCompletionNote}" />
```

- [x] **Step 6: Add the side outcome action**

In the `StackPanel Grid.Column="5"` actions strip, after the Unschedule button:

```xml
            <!-- The other outcomes: quiet, never competing with the checkbox -->
            <Button Classes="icon" Width="26" Height="26"
                    IsVisible="{Binding ShowSessionOutcomeAction}"
                    AutomationProperties.Name="{Binding OutcomeControlName}"
                    ToolTip.Tip="Needs more time, or didn't happen">
              <Path Classes="icon" Data="{StaticResource IconMore}" Width="12" Height="12"
                    StrokeThickness="1.3" Stroke="{StaticResource BrushPencilGray}" />
              <Button.Flyout>
                <Flyout Placement="BottomEdgeAlignedRight" ShowMode="Standard">
                  <StackPanel Width="228" Spacing="6" Margin="2">
                    <Border BorderBrush="{StaticResource BrushRuleMedium}" BorderThickness="1"
                            CornerRadius="{StaticResource RadiusControl}" Padding="8,7">
                      <StackPanel Spacing="6">
                        <TextBlock Text="Needs more time" FontWeight="Medium" />
                        <DockPanel>
                          <Button DockPanel.Dock="Right" Content="Save" Margin="6,0,0,0"
                                  Command="{Binding RecordNeedsMoreTimeCommand}"
                                  Click="OnFlyoutActionClick" />
                          <NumericUpDown Value="{Binding RemainingMinutes}" Minimum="5" Maximum="720"
                                         Increment="5" FormatString="0"
                                         AutomationProperties.Name="Remaining minutes" />
                        </DockPanel>
                        <TextBlock Classes="meta" Text="returns to Inbox with the time left" />
                      </StackPanel>
                    </Border>
                    <Button HorizontalAlignment="Stretch" Content="Didn't happen"
                            Command="{Binding RecordDidntHappenCommand}" Click="OnFlyoutActionClick" />
                  </StackPanel>
                </Flyout>
              </Button.Flyout>
            </Button>
```

- [x] **Step 7: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/BeBoosted.Desktop.Tests/BeBoosted.Desktop.Tests.csproj \
  --filter "FullyQualifiedName~DailyListUiTests"
```
Expected: PASS.

- [x] **Step 8: Full verification**

Run, in order:
```bash
dotnet format BeBoosted.slnx --verify-no-changes --no-restore
dotnet build BeBoosted.slnx --no-restore -warnaserror
dotnet test BeBoosted.slnx --no-restore --no-build
git diff --check
```
Expected: format clean, 0 warnings / 0 errors, all tests pass (3 screenshot tests still skipped), `git diff --check` exit 0.

---

## Notes for the implementer

- `RecordDoneCommand` and `CanRecordDone` stay on `DailyRowViewModel`. `CanRecordDone` still gates `RecordDoneCommand`, which `CalendarBlockCapabilityTests` and `DailyListUiTests.TaskBlockRow_OutcomeMenu_RecordsDone` exercise. Do not delete them — the checkbox is an additional caller of the same service operation, not a replacement for the command.
- Rows are rebuilt on every `Reload()`, so `CanToggleSessionDone` needs no `PropertyChanged` notification. This matches how `CanRecordDone` already gates `RecordDoneCommand`.
- `ShowChangeTimeAction`, `ShowUnscheduleAction`, and `ShowTaskEditAction` are all gated on `!IsDone`, so a done row hides them and an undone row gets them back automatically. No change needed.
- `DailyListViewModel.BuildOccurrenceRow` only builds `ScheduleEditor` when `!isDone`; undo triggers a rebuild, so the editor returns on its own.
