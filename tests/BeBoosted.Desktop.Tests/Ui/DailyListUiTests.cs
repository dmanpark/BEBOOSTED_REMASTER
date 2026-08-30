using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The Today view renders the priority-first Daily list (no hourly timeline);
/// Week keeps the existing timeline. Covers navigation, scheduling, add-task,
/// editors, outcomes, completed collapse, locked externals, and draft surfaces.
/// </summary>
public sealed class DailyListUiTests
{
    private static (MainWindow Window, ShellViewModel Shell, InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks, FakeClock Clock) CreateShellWindow(
        bool seed = false, double width = 1440, double height = 960)
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        if (seed)
        {
            TestShell.SeedDesignCalendar(tasks, blocks, clock);
        }

        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        window.CaptureRenderedFrame();
        return (window, shell, tasks, blocks, clock);
    }

    private static DailyTaskListView DailyView(MainWindow window)
        => window.GetVisualDescendants().OfType<DailyTaskListView>().First();

    private static TextBlock? FindText(MainWindow window, string text)
        => window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == text && block.IsEffectivelyVisible);

    /// <summary>
    /// Every completion control in the cluster shares <c>CompletionControlName</c>, so the
    /// rendered one is the only meaningful match — the hidden siblings sit in the same Panel.
    /// </summary>
    private static Button? DailyCheckFor(MainWindow window, string accessibleName)
        => window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("dailyCheck")
                && b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == accessibleName);

    [AvaloniaFact]
    public void Today_ShowsDailyList_AndHidesTheTimeline()
    {
        var (window, shell, _, _, _) = CreateShellWindow(seed: true);

        Assert.True(DailyView(window).IsEffectivelyVisible);
        var timeline = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        Assert.False(timeline.IsEffectivelyVisible);
        Assert.NotNull(FindText(window, "Today's tasks"));

        shell.Calendar.ViewKind = CalendarViewKind.Week;
        window.CaptureRenderedFrame();

        Assert.True(timeline.IsEffectivelyVisible);
        Assert.False(DailyView(window).IsEffectivelyVisible);
        Assert.True(window.GetVisualDescendants().OfType<CalendarBlockView>().Any());
    }

    [AvaloniaFact]
    public void Navigation_PreviousNextToday_UpdatesTheHeading()
    {
        var (window, shell, _, _, _) = CreateShellWindow();

        shell.Calendar.GoNextCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.NotNull(FindText(window, "Tasks for Wednesday"));

        shell.Calendar.GoPreviousCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.NotNull(FindText(window, "Today's tasks"));

        shell.Calendar.GoNextCommand.Execute(null);
        shell.Calendar.GoToTodayCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.NotNull(FindText(window, "Today's tasks"));
        Assert.Equal(TestShell.DesignDate, shell.Calendar.VisibleDate);
    }

    [AvaloniaFact]
    public void ScheduleFlyout_SchedulesTheTask_AndFocusesItsNewRow()
    {
        var (window, shell, tasks, blocks, clock) = CreateShellWindow();
        tasks.Add(TaskItem.Create("Finish personal statement draft", clock.Now,
            estimatedDuration: TimeSpan.FromMinutes(45)));
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var row = shell.Calendar.Daily.UnscheduledRows.Single();
        var editor = row.ScheduleEditor!;
        editor.Start = new TimeSpan(16, 0, 0);
        Assert.True(editor.Confirm());
        window.CaptureRenderedFrame();

        Assert.Single(blocks.GetAll());
        var scheduledRow = shell.Calendar.Daily.ScheduledRows.Single();
        Assert.Equal("Finish personal statement draft", scheduledRow.Title);

        // Focus lands on the freshly scheduled row.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var focused = TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement();
        Assert.True(
            focused is Border { DataContext: DailyRowViewModel vm } && vm.TaskId == scheduledRow.TaskId,
            $"expected the scheduled row to hold focus, got {focused?.GetType().Name ?? "nothing"}");
    }

    private static void ClickByName(MainWindow window, string automationName)
    {
        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && Avalonia.Automation.AutomationProperties.GetName(b) == automationName);
        var point = button.TranslatePoint(
            new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.CaptureRenderedFrame();
    }

    /// <summary>
    /// The Scheduled section's Add task opens the one canonical Task editor,
    /// prefilled with the visible date and the sensible default slot — there is no
    /// separate inline creation form.
    /// </summary>
    [AvaloniaFact]
    public void AddTask_Scheduled_OpensTheCanonicalEditor_Prefilled()
    {
        var (window, shell, tasks, blocks, _) = CreateShellWindow();

        ClickByName(window, "Add scheduled task");

        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Null(editor.TaskId); // create mode
        Assert.True(editor.ShowInlineSchedule);
        Assert.Equal(
            TestShell.DesignDate, DateOnly.FromDateTime(editor.InlineSchedule.Date!.Value.Date));
        Assert.Equal(new TimeSpan(14, 15, 0), editor.InlineSchedule.Start); // next quarter hour at 14:10
        Assert.Equal(new TimeSpan(14, 45, 0), editor.InlineSchedule.End);   // the 30-minute default

        // Saving persists the task and its session through the one atomic path.
        editor.Title = "Evening workout";
        editor.SaveCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.Null(shell.Calendar.ActiveTaskEditor);
        var task = tasks.GetAll().Single(t => t.Title == "Evening workout");
        Assert.Single(blocks.GetForTask(task.Id));
        Assert.NotNull(FindText(window, "Evening workout"));
    }

    /// <summary>The Unscheduled section's Add task opens the same editor, unscheduled.</summary>
    [AvaloniaFact]
    public void AddTask_Unscheduled_OpensTheCanonicalEditor_WithSchedulingOff()
    {
        var (window, shell, tasks, blocks, _) = CreateShellWindow();

        ClickByName(window, "Add unscheduled task");

        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Null(editor.TaskId); // create mode
        Assert.False(editor.ShowInlineSchedule);

        editor.Title = "Organize class notes";
        editor.SaveCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.Null(shell.Calendar.ActiveTaskEditor);
        var task = tasks.GetAll().Single(t => t.Title == "Organize class notes");
        Assert.Empty(blocks.GetForTask(task.Id));
        Assert.NotNull(FindText(window, "Organize class notes"));
    }

    /// <summary>A scheduled row is task-scoped: it opens the whole-task editor (F-03).</summary>
    [AvaloniaFact]
    public void ScheduledRow_OpensTheWholeTaskEditor()
    {
        var (window, shell, tasks, blocks, clock) = CreateShellWindow();
        var stats = TaskItem.Create("Stats homework", clock.Now);
        tasks.Add(stats);
        blocks.Add(CalendarBlock.CreateTaskSession(
            stats.Id, TestShell.DesignDate, new TimeOnly(9, 0), new TimeOnly(10, 0), clock.Now));
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var row = shell.Calendar.Daily.ScheduledRows.Single();
        row.EditRowCommand.Execute(null);
        window.CaptureRenderedFrame();

        Assert.True(shell.Calendar.IsTaskEditorOpen);
        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal("Stats homework", editor.Title);
        Assert.Single(editor.Sessions);

        shell.EscapePressedCommand.Execute(null); // Escape precedence closes the modal first
        Assert.False(shell.Calendar.IsTaskEditorOpen);
    }

    [AvaloniaFact]
    public void TaskBlockRow_OutcomeMenu_RecordsDone()
    {
        var (window, shell, tasks, blocks, clock) = CreateShellWindow();
        var task = TaskItem.Create("Elapsed work", clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
        tasks.Add(task);
        var service = TestShell.CreateCalendarService(blocks, tasks, clock);
        service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(9, 0));
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var row = shell.Calendar.Daily.ScheduledRows.Single();
        Assert.True(row.NeedsOutcome);
        Assert.NotNull(FindText(window, "Needs outcome"));

        row.RecordDoneCommand.Execute(null);
        window.CaptureRenderedFrame();

        Assert.Empty(shell.Calendar.Daily.ScheduledRows);
        Assert.Single(shell.Calendar.Daily.CompletedRows);
        // The menu resolves the session it was opened from, not the whole task.
        Assert.Equal(BlockOutcome.Done, blocks.GetAll().Single().Outcome);
        Assert.False(tasks.GetById(task.Id)!.IsCompleted);
    }

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

    [AvaloniaFact]
    public void DailyRow_PriorityMarker_IsAButtonWhenRanked()
    {
        var (window, shell, tasks, _, clock) = CreateShellWindow();
        tasks.Add(TaskItem.Create("Ranked work", clock.Now, estimatedDuration: TimeSpan.FromMinutes(30)));
        tasks.Add(TaskItem.Create("Other work", clock.Now, estimatedDuration: TimeSpan.FromMinutes(30)));
        shell.Inbox.Reload();
        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseRightCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var marker = window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("priorityMarker")
                && b.IsEffectivelyVisible
                && b.IsEnabled);
        Assert.NotNull(marker);

        marker.Command!.Execute(marker.CommandParameter);
        Assert.NotNull(shell.ActiveSort);
        Assert.True(shell.ActiveSort.IsRerank);
    }

    [AvaloniaFact]
    public void CompletedSection_CollapsedByDefault_TogglesOpen()
    {
        var (window, shell, tasks, _, clock) = CreateShellWindow();
        var task = TaskItem.Create("Morning reading", clock.Now);
        tasks.Add(task);
        task.Complete(clock.Now);
        tasks.Update(task);
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var toggle = DailyView(window).FindControl<ToggleButton>("CompletedToggle")!;
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.False(shell.Calendar.Daily.IsCompletedExpanded);
        Assert.Null(FindText(window, "Morning reading")); // hidden while collapsed

        toggle.IsChecked = true;
        window.CaptureRenderedFrame();
        Assert.NotNull(FindText(window, "Morning reading"));

        toggle.IsChecked = false;
        window.CaptureRenderedFrame();
        Assert.Null(FindText(window, "Morning reading"));
    }

    [AvaloniaFact]
    public void ExternalEvent_RemainsLockedInTheDailyList()
    {
        var (window, shell, _, blocks, clock) = CreateShellWindow();
        blocks.Add(CalendarBlock.Rehydrate(
            Domain.CalendarBlockId.New(), null, "Imported standup", TestShell.DesignDate,
            new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.ExternalEvent, null,
            "google", "evt-1", 0, BlockOutcome.None, null, clock.Now, clock.Now));
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var row = shell.Calendar.Daily.ScheduledRows.Single();
        Assert.True(row.IsLocked);
        Assert.False(row.ShowOccurrenceCheck);
        Assert.False(row.CanEditRow);
        Assert.False(row.ShowChangeTimeAction);
        Assert.Contains("Synced event — locked", row.AccessibleName);
        Assert.NotNull(FindText(window, "Imported standup"));
    }

    /// <summary>
    /// TDD phase 7: the unified product never labels items FIXED or FLEX — a scheduled
    /// time already says the task is scheduled; only external events get a Synced chip.
    /// </summary>
    [AvaloniaFact]
    public void DailyList_ShowsNoFixedOrFlexBadges_OnlySyncedForExternal()
    {
        var (window, shell, _, blocks, clock) = CreateShellWindow(seed: true);
        blocks.Add(CalendarBlock.Rehydrate(
            Domain.CalendarBlockId.New(), null, "Imported standup", TestShell.DesignDate,
            new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.ExternalEvent, null,
            "google", "evt-1", 0, BlockOutcome.None, null, clock.Now, clock.Now));
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        Assert.Null(FindText(window, "FIXED"));
        Assert.Null(FindText(window, "FLEX"));
        Assert.NotNull(FindText(window, "Synced"));

        // Ordinary scheduled rows carry no redundant status chip at all.
        var lunch = shell.Calendar.Daily.ScheduledRows.Single(r => r.Title == "Lunch");
        Assert.False(lunch.HasStatus);
    }

    [AvaloniaFact]
    public void ReviewNotice_AndDraftSurfaces_FollowTheView()
    {
        var (window, shell, tasks, blocks, clock) = CreateShellWindow();
        // An elapsed block produces the quiet review notice.
        var task = TaskItem.Create("Yesterday's work", clock.Now, estimatedDuration: TimeSpan.FromMinutes(30));
        tasks.Add(task);
        var service = TestShell.CreateCalendarService(blocks, tasks, clock);
        service.ScheduleTask(task.Id, TestShell.DesignDate.AddDays(-1), new TimeOnly(9, 0));
        // A second open task lets the planner draft something for today.
        tasks.Add(TaskItem.Create("Draft essay outline", clock.Now, estimatedDuration: TimeSpan.FromMinutes(60)));
        shell.Calendar.Reload();
        shell.PlanCommand.Execute(null);
        window.CaptureRenderedFrame();

        Assert.True(shell.Calendar.HasReviewNotice);
        Assert.NotNull(FindText(window, "1 previous block needs an outcome."));

        // Today: inline banner visible, floating card hidden.
        Assert.True(shell.Calendar.HasDraft);
        Assert.True(shell.Calendar.Daily.ShowDraftBanner);
        Assert.False(shell.Calendar.ShowFloatingDraftCard);
        Assert.NotNull(FindText(window, "Plan draft · Today"));

        shell.Calendar.ViewKind = CalendarViewKind.Week;
        window.CaptureRenderedFrame();
        Assert.True(shell.Calendar.ShowFloatingDraftCard);
    }

    [AvaloniaFact]
    public void MinimumWindow_1100x720_RendersWithoutHorizontalScroll()
    {
        var (window, shell, tasks, blocks, clock) = CreateShellWindow(seed: true, width: 1100, height: 720);
        tasks.Add(TaskItem.Create("Email academic advisor", clock.Now, deadline: TestShell.DesignDate));
        shell.Calendar.Reload();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var scroller = DailyView(window).FindControl<ScrollViewer>("DailyScroller")!;
        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width + 0.5,
            $"horizontal overflow: extent {scroller.Extent.Width} > viewport {scroller.Viewport.Width}");
    }
}
