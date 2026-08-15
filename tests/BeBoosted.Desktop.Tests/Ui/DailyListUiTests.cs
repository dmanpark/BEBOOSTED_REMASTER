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

    [AvaloniaFact]
    public void AddTask_Unscheduled_CapturesInline()
    {
        var (window, shell, tasks, _, _) = CreateShellWindow();
        var daily = shell.Calendar.Daily;

        daily.BeginAddUnscheduledCommand.Execute(null);
        window.CaptureRenderedFrame();
        var box = DailyView(window).FindControl<TextBox>("UnscheduledAddBox")!;
        Assert.True(box.IsEffectivelyVisible);

        daily.NewUnscheduledTitle = "Organize class notes";
        daily.ConfirmAddUnscheduled();
        window.CaptureRenderedFrame();

        Assert.Single(tasks.GetAll());
        Assert.NotNull(FindText(window, "Organize class notes"));
        Assert.False(daily.IsAddingUnscheduled);
    }

    [AvaloniaFact]
    public void AddTask_Scheduled_CreatesAndSchedules()
    {
        var (window, shell, tasks, blocks, _) = CreateShellWindow();
        var daily = shell.Calendar.Daily;

        daily.BeginAddScheduledCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.True(DailyView(window).FindControl<TextBox>("ScheduledAddTitleBox")!.IsEffectivelyVisible);

        daily.NewScheduledTitle = "Evening workout";
        daily.NewScheduledStart = new TimeSpan(18, 30, 0);
        daily.NewScheduledDurationMinutes = 45;
        daily.ConfirmAddScheduledCommand.Execute(null);
        window.CaptureRenderedFrame();

        Assert.Single(tasks.GetAll());
        Assert.Single(blocks.GetAll());
        Assert.NotNull(FindText(window, "Evening workout"));
    }

    [AvaloniaFact]
    public void CommitmentRow_OpensTheSharedEditor()
    {
        var (window, shell, _, blocks, clock) = CreateShellWindow();
        blocks.Add(CalendarBlock.CreateFixedCommitment(
            "Stats homework", TestShell.DesignDate, new TimeOnly(9, 0), new TimeOnly(10, 0), clock.Now));
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var row = shell.Calendar.Daily.ScheduledRows.Single();
        row.EditCommitmentCommand.Execute(null);
        window.CaptureRenderedFrame();

        Assert.True(shell.Calendar.IsCommitmentEditorOpen);
        Assert.Equal("Stats homework", shell.Calendar.CommitmentEditor!.Title);

        shell.EscapePressedCommand.Execute(null); // Escape precedence closes the modal first
        Assert.False(shell.Calendar.IsCommitmentEditorOpen);
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
        Assert.True(tasks.GetById(task.Id)!.IsCompleted);
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
    public void ExternalCommitment_RemainsLockedInTheDailyList()
    {
        var (window, shell, _, blocks, clock) = CreateShellWindow();
        blocks.Add(CalendarBlock.Rehydrate(
            Domain.CalendarBlockId.New(), null, null, "Imported standup", TestShell.DesignDate,
            new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.FixedCommitment, null,
            "google", "evt-1", 0, BlockOutcome.None, null, clock.Now, clock.Now));
        shell.Calendar.Reload();
        window.CaptureRenderedFrame();

        var row = shell.Calendar.Daily.ScheduledRows.Single();
        Assert.True(row.IsLocked);
        Assert.False(row.ShowCommitmentCheck);
        Assert.False(row.CanEditCommitment);
        Assert.False(row.ShowChangeTimeAction);
        Assert.Contains("External commitment — locked", row.AccessibleName);
        Assert.NotNull(FindText(window, "Imported standup"));
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
