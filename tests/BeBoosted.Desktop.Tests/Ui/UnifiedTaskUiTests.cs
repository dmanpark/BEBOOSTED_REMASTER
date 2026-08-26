using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The unified Task product surface: empty Week-slot clicks create prefilled scheduled
/// tasks, every entry point opens the one Task editor, and no user-facing surface says
/// "commitment", FIXED, or FLEX anymore.
/// </summary>
public sealed class UnifiedTaskUiTests
{
    private static (MainWindow Window, ShellViewModel Shell, InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks)
        CreateWeekShellWindow()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        TestShell.SeedDesignCalendar(tasks, blocks, clock);
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.Calendar.ViewKind = CalendarViewKind.Week;
        window.CaptureRenderedFrame();
        return (window, shell, tasks, blocks);
    }

    private static TimelineSurfaceView Surface(MainWindow window)
        => window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();

    private static void ScrollTo(MainWindow window, double offsetY)
    {
        Surface(window).FindControl<ScrollViewer>("Scroller")!.Offset = new Vector(0, offsetY);
        window.CaptureRenderedFrame();
    }

    /// <summary>Window point inside the given day column at the given timeline minutes.</summary>
    private static Point SlotPoint(MainWindow window, int column, double minutes)
    {
        var surface = Surface(window);
        var host = surface.DaysHost;
        var x = (column + 0.5) * surface.ColumnWidth;
        var y = minutes / 60.0 * TimelineSurfaceView.DefaultHourHeight;
        return host.TranslatePoint(new Point(x, y), window)!.Value;
    }

    private static void Click(MainWindow window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();
    }

    /// <summary>
    /// TDD phase 9: a primary click on an empty Week slot opens New task prefilled with
    /// the clicked column's date and the snapped time, scheduled for one hour.
    /// </summary>
    [AvaloniaFact]
    public void EmptyWeekSlotClick_OpensNewTask_PrefilledWithSnappedSlot()
    {
        var (window, shell, _, _) = CreateWeekShellWindow();
        ScrollTo(window, 600);

        // Thursday column (index 3) at ~13:07 → snapped to 13:00 with the 15-minute grid.
        Click(window, SlotPoint(window, 3, (13 * 60) + 7));

        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Null(editor.TaskId); // create mode
        Assert.True(editor.ShowInlineSchedule);
        Assert.Equal(
            TestShell.DesignDate.AddDays(2), // Thursday, Aug 13
            DateOnly.FromDateTime(editor.InlineSchedule.Date!.Value.Date));
        Assert.Equal(new TimeSpan(13, 0, 0), editor.InlineSchedule.Start);
        Assert.Equal(new TimeSpan(14, 0, 0), editor.InlineSchedule.End);
        window.Close();
    }

    /// <summary>TDD phase 9: near midnight the prefilled slot stays inside 00:00–23:59.</summary>
    [AvaloniaFact]
    public void EmptyWeekSlotClick_NearMidnight_ClampsTheHourWithinTheDay()
    {
        var (window, shell, _, _) = CreateWeekShellWindow();
        Surface(window).FindControl<ScrollViewer>("Scroller")!.ScrollToEnd();
        window.CaptureRenderedFrame();

        // Monday column at ~23:40 → snapped 23:45; the one-hour end clamps to 23:59.
        Click(window, SlotPoint(window, 0, (23 * 60) + 40));

        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(new TimeSpan(23, 45, 0), editor.InlineSchedule.Start);
        Assert.Equal(new TimeSpan(23, 59, 0), editor.InlineSchedule.End);
        window.Close();
    }

    /// <summary>TDD phase 9 guards: dragging on an empty slot never opens New task.</summary>
    [AvaloniaFact]
    public void EmptySlotDragBeyondThreshold_DoesNotOpenNewTask()
    {
        var (window, shell, _, _) = CreateWeekShellWindow();
        ScrollTo(window, 600);

        var start = SlotPoint(window, 3, 13 * 60);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + 30, start.Y + 30));
        window.MouseUp(new Point(start.X + 30, start.Y + 30), MouseButton.Left);
        window.CaptureRenderedFrame();

        Assert.Null(shell.Calendar.ActiveTaskEditor);
        window.Close();
    }

    /// <summary>TDD phase 9 guards: the Week header is not a creation surface.</summary>
    [AvaloniaFact]
    public void WeekHeaderClick_DoesNotOpenNewTask()
    {
        var (window, shell, _, _) = CreateWeekShellWindow();

        // The day-header strip sits above the scrolling timeline.
        var surface = Surface(window);
        var headerPoint = surface.TranslatePoint(new Point(surface.Bounds.Width / 2, 10), window)!.Value;
        Click(window, headerPoint);

        Assert.Null(shell.Calendar.ActiveTaskEditor);
        window.Close();
    }

    /// <summary>
    /// TDD phase 9 guards: clicking an existing block edits that task — it never
    /// opens a New task for the slot underneath.
    /// </summary>
    [AvaloniaFact]
    public void ClickOnAnExistingBlock_EditsThatTask_NotANewOne()
    {
        var (window, shell, _, _) = CreateWeekShellWindow();
        ScrollTo(window, 600);

        var lunch = window.GetVisualDescendants()
            .OfType<CalendarBlockView>()
            .First(view => (view.DataContext as CalendarBlockViewModel)?.Title == "Lunch");
        var point = lunch.TranslatePoint(
            new Point(lunch.Bounds.Width / 2, lunch.Bounds.Height / 2), window)!.Value;
        Click(window, point);

        var editor = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal("Lunch", editor.TaskTitle);
        window.Close();
    }

    /// <summary>
    /// TDD phase 14, the reported failure surface: editing a task from the Inbox must
    /// go through the one canonical editor, so an open project refreshes on save.
    /// </summary>
    [AvaloniaFact]
    public void InboxEdit_OpensTheCanonicalEditor_AndProjectAssignmentRefreshes()
    {
        var (window, shell, tasks, _) = CreateWeekShellWindow();
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.NewProjectName = "CAPPs";
        Assert.True(shell.Projects.TryCreateProject());
        Assert.Empty(shell.Projects.Detail!.OpenTasks);

        shell.NavigateCommand.Execute(AppSection.Calendar);
        shell.Inbox.CaptureText = "PIQ2";
        shell.Inbox.CaptureCommand.Execute(null);
        shell.IsInboxOpen = true;
        window.CaptureRenderedFrame();

        var edit = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Edit task PIQ2");
        var point = edit.TranslatePoint(
            new Point(edit.Bounds.Width / 2, edit.Bounds.Height / 2), window)!.Value;
        Click(window, point);

        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal("PIQ2", editor.Title);
        editor.SelectedProject = editor.ProjectOptions.Single(o => o.Name == "CAPPs");
        editor.SaveCommand.Execute(null);
        window.CaptureRenderedFrame();

        // The persisted assignment and the already-open project both reflect it.
        Assert.Equal(
            shell.Projects.Detail!.Project.Id,
            tasks.GetAll().Single(t => t.Title == "PIQ2").ProjectId);
        Assert.Contains(shell.Projects.Detail.OpenTasks, t => t.Title == "PIQ2");
        window.Close();
    }

    /// <summary>
    /// TDD phase 7: no user-facing surface says "commitment" anymore — the calendar
    /// header offers "New task" and rendered text stays task-centered.
    /// </summary>
    [AvaloniaFact]
    public void RenderedCalendar_ContainsNoCommitmentTerminology()
    {
        var (window, shell, _, _) = CreateWeekShellWindow();

        AssertNoCommitmentText(window);

        shell.Calendar.ViewKind = CalendarViewKind.Today;
        window.CaptureRenderedFrame();
        AssertNoCommitmentText(window);
        window.Close();
    }

    private static void AssertNoCommitmentText(MainWindow window)
    {
        string[] banned = ["commitment", "flexible"];
        var offenders = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text is { } text
                && banned.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Text)
            .ToList();
        Assert.Empty(offenders);
    }
}
