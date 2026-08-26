using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Controls;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// Event-path regressions for pointer interactions on calendar blocks. These drive the
/// real input pipeline (press → move → release) instead of calling view-model methods,
/// so the shared drag/resize handler in <see cref="CalendarBlockView"/> stays covered.
/// </summary>
public sealed class CalendarBlockInteractionTests
{
    private static (MainWindow Window, ShellViewModel Shell, InMemoryCalendarBlockRepository Blocks,
        InMemoryTaskRepository Tasks)
        CreateSeededShellWindow(
            Action<InMemoryTaskRepository, InMemoryCalendarBlockRepository, FakeClock>? extraSeed = null)
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        TestShell.SeedDesignCalendar(tasks, blocks, clock);
        extraSeed?.Invoke(tasks, blocks, clock);
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        // Timeline blocks live on the Week surface; Today shows the Daily list.
        shell.Calendar.ViewKind = CalendarViewKind.Week;
        window.CaptureRenderedFrame();
        return (window, shell, blocks, tasks);
    }

    private static CalendarBlockView FindBlockView(MainWindow window, string title)
        => window.GetVisualDescendants()
            .OfType<CalendarBlockView>()
            .First(view => (view.DataContext as CalendarBlockViewModel)?.Title == title);

    /// <summary>The persisted block whose Task (or own external title) matches.</summary>
    private static CalendarBlock SavedBlock(
        InMemoryCalendarBlockRepository blocks, InMemoryTaskRepository tasks, string title)
        => blocks.GetAll().Single(b => b.Title == title
            || (b.TaskId is { } taskId && tasks.GetById(taskId)?.Title == title));

    private static ScrollViewer ScrollTo(MainWindow window, double offsetY)
    {
        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var scroller = surface.FindControl<ScrollViewer>("Scroller")!;
        scroller.Offset = new Vector(0, offsetY);
        window.CaptureRenderedFrame();
        return scroller;
    }

    private static Point CenterOf(Visual visual, MainWindow window)
        => visual.TranslatePoint(
            new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    private static void Click(MainWindow window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();
    }

    /// <summary>The BrushAccentSlate edge every local task session carries (Tokens.axaml).</summary>
    private static readonly Color SessionAccentEdge = Color.Parse("#7B8FA6");

    /// <summary>The BrushSyncedCream fill of external synced events (Tokens.axaml).</summary>
    private static readonly Color ExternalEventFill = Color.Parse("#ECE9DC");

    private static Rect WindowRectOf(Visual visual, MainWindow window)
        => new(visual.TranslatePoint(default, window)!.Value, visual.Bounds.Size);

    /// <summary>
    /// Renders a fresh frame and reports whether any pixel inside <paramref name="rect"/>
    /// (window coordinates) matches <paramref name="fill"/> — the rendering-level truth of
    /// "a block with this color is visible here", robust to text/border pixels on top.
    /// </summary>
    private static bool RectShowsFill(MainWindow window, Rect rect, Color fill)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        // Tight tolerance: hour rules blended over paper white sit ~6 per channel from the
        // cream fill, so anything looser would misread chrome as a block.
        static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 3;
        var maxX = Math.Min(rect.Right, frame.PixelSize.Width - 1);
        var maxY = Math.Min(rect.Bottom, frame.PixelSize.Height - 1);
        for (var y = Math.Max(rect.Y, 0); y < maxY; y += 2)
        {
            for (var x = Math.Max(rect.X, 0); x < maxX; x += 2)
            {
                var pixel = buffer.Address + ((int)y * buffer.RowBytes) + ((int)x * 4);
                var c0 = Marshal.ReadByte(pixel);
                var c1 = Marshal.ReadByte(pixel + 1);
                var c2 = Marshal.ReadByte(pixel + 2);
                // Match RGBA or BGRA layouts so the check is not framebuffer-format brittle.
                if ((Near(c0, fill.R) && Near(c1, fill.G) && Near(c2, fill.B))
                    || (Near(c0, fill.B) && Near(c1, fill.G) && Near(c2, fill.R)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A visible session block = its slate accent edge renders inside the rect.</summary>
    private static bool RectShowsSession(MainWindow window, Rect rect)
        => RectShowsFill(window, rect.Inflate(new Thickness(2, 0)), SessionAccentEdge);

    private static CalendarBlock ExternalEvent(FakeClock clock)
        => CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "Imported standup", TestShell.DesignDate,
            new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.ExternalEvent, null,
            "google", "evt-1", 0, BlockOutcome.None, null, clock.Now, clock.Now);

    /// <summary>BB-QA-001 blocker: releasing a resize at the 24:00 boundary must not throw.</summary>
    [AvaloniaFact]
    public void PointerResize_ToBottomOfDay_SavesEndAt2359_WithoutThrowing()
    {
        var (window, _, blocks, tasks) = CreateSeededShellWindow();
        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var scroller = surface.FindControl<ScrollViewer>("Scroller")!;
        scroller.ScrollToEnd();
        window.CaptureRenderedFrame();

        // "Draft personal statement" is the interactive 19:00–20:00 session.
        var blockView = FindBlockView(window, "Draft personal statement");
        var grip = blockView.FindControl<Border>("ResizeGrip")!;
        var gripPoint = grip.TranslatePoint(
            new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;

        window.MouseDown(gripPoint, MouseButton.Left);
        var exception = Record.Exception(() =>
        {
            // Drag far past the day's bottom; the handler clamps the duration to 24:00.
            window.MouseMove(new Point(gripPoint.X, gripPoint.Y + 500));
            window.MouseUp(new Point(gripPoint.X, gripPoint.Y + 500), MouseButton.Left);
        });

        Assert.Null(exception);
        var saved = SavedBlock(blocks, tasks, "Draft personal statement");
        Assert.Equal(new TimeOnly(23, 59), saved.EndTime);

        // The saved block must still render inside the day.
        window.CaptureRenderedFrame();
        var reloaded = FindBlockView(window, "Draft personal statement");
        var panelPoint = reloaded.TranslatePoint(
            new Point(0, reloaded.Bounds.Height), surface.DaysHost)!.Value;
        Assert.True(
            panelPoint.Y <= (24 * TimelineSurfaceView.DefaultHourHeight) + 0.5,
            $"Block bottom {panelPoint.Y} extends past the day's end");
        window.Close();
    }

    /// <summary>A task body click opens the session editor for that occurrence (F-03).</summary>
    [AvaloniaFact]
    public void Click_OnScheduledTask_OpensTheSessionEditor()
    {
        var (window, shell, _, _) = CreateSeededShellWindow();
        ScrollTo(window, 600); // bring the 12:00 Lunch session into the viewport

        var lunch = FindBlockView(window, "Lunch");
        Click(window, CenterOf(lunch, window));

        var editor = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal("Lunch", editor.TaskTitle);
        window.Close();
    }

    /// <summary>TDD phase 12: dragging past the threshold moves — it never opens the editor.</summary>
    [AvaloniaFact]
    public void DragBeyondThreshold_MovesTheTask_InsteadOfOpeningTheEditor()
    {
        var (window, shell, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 600);

        var lunch = FindBlockView(window, "Lunch");
        var start = CenterOf(lunch, window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X, start.Y + 28)); // 30 min at 56 px/hour
        window.MouseUp(new Point(start.X, start.Y + 28), MouseButton.Left);
        window.CaptureRenderedFrame();

        Assert.Null(shell.Calendar.ActiveTaskEditor);
        var saved = SavedBlock(blocks, tasks, "Lunch");
        Assert.Equal(new TimeOnly(12, 30), saved.StartTime);
        Assert.Equal(new TimeOnly(13, 15), saved.EndTime);
        window.Close();
    }

    /// <summary>TDD phase 12: resizing records the new end — it never opens the editor.</summary>
    [AvaloniaFact]
    public void ResizeGrip_Resizes_InsteadOfOpeningTheEditor()
    {
        var (window, shell, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 600);

        var lunch = FindBlockView(window, "Lunch");
        var grip = lunch.FindControl<Border>("ResizeGrip")!;
        var gripPoint = CenterOf(grip, window);
        window.MouseDown(gripPoint, MouseButton.Left);
        window.MouseMove(new Point(gripPoint.X, gripPoint.Y + 28));
        window.MouseUp(new Point(gripPoint.X, gripPoint.Y + 28), MouseButton.Left);
        window.CaptureRenderedFrame();

        Assert.Null(shell.Calendar.ActiveTaskEditor);
        var saved = SavedBlock(blocks, tasks, "Lunch");
        Assert.Equal(new TimeOnly(12, 0), saved.StartTime);
        Assert.Equal(new TimeOnly(13, 15), saved.EndTime); // 12:45 + 28px ≈ +30 min, snapped
        window.Close();
    }

    [AvaloniaFact]
    public void WeekView_HorizontalDrag_MovesOneOffTaskToTheNextDay()
    {
        var (window, shell, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 600);

        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var lunch = FindBlockView(window, "Lunch"); // one-off on Tuesday
        var start = CenterOf(lunch, window);
        var target = new Point(start.X + surface.ColumnWidth, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(target);
        window.MouseUp(target, MouseButton.Left);
        window.CaptureRenderedFrame();

        Assert.Null(shell.Calendar.ActiveTaskEditor);
        var saved = SavedBlock(blocks, tasks, "Lunch");
        Assert.Equal(TestShell.DesignDate.AddDays(1), saved.Date); // Wednesday
        Assert.Equal(new TimeOnly(12, 0), saved.StartTime);
        window.Close();
    }

    /// <summary>
    /// BB-QA-003: while the button is still held, a cross-day drag must render a visible
    /// preview in the targeted column at the snapped time, with no duplicate left behind
    /// in the origin column, and release must persist exactly the previewed day and time.
    /// </summary>
    [AvaloniaFact]
    public void WeekView_CrossColumnDragRight_ShowsPreviewInTargetColumn_WhileHeld()
    {
        var (window, _, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 600);

        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var lunch = FindBlockView(window, "Lunch"); // one-off task, Tuesday 12:00
        var originRect = WindowRectOf(lunch, window);
        var start = CenterOf(lunch, window);
        // One column right and 28 px down: 30 minutes at 56 px/hour.
        var target = new Point(start.X + surface.ColumnWidth, start.Y + 28);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(target);

        // Mouse still held: a preview must be visible in the adjacent column at 12:30...
        var previewRect = originRect.Translate(new Vector(surface.ColumnWidth, 28));
        Assert.True(
            RectShowsSession(window, previewRect),
            "No visible drag preview rendered in the target column while the button is held");
        // ...and the origin column must not keep a visible duplicate underneath it.
        Assert.False(
            RectShowsSession(window, originRect),
            "The source block still renders in the origin column during a cross-day drag");

        window.MouseUp(target, MouseButton.Left);
        window.CaptureRenderedFrame();

        var saved = SavedBlock(blocks, tasks, "Lunch");
        Assert.Equal(TestShell.DesignDate.AddDays(1), saved.Date); // Wednesday
        Assert.Equal(new TimeOnly(12, 30), saved.StartTime);

        // Release removes every piece of temporary preview state.
        Assert.Empty(surface.FindControl<Canvas>("DragPreviewCanvas")!.Children);
        Assert.Equal(1, lunch.Opacity);
        Assert.Null(lunch.RenderTransform);
        window.Close();
    }

    /// <summary>BB-QA-003: dragging left across a day boundary previews and saves the same way.</summary>
    [AvaloniaFact]
    public void WeekView_CrossColumnDragLeft_ShowsPreviewInTargetColumn_AndSavesMonday()
    {
        var (window, _, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 600);

        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var lunch = FindBlockView(window, "Lunch");
        var originRect = WindowRectOf(lunch, window);
        var start = CenterOf(lunch, window);
        var target = new Point(start.X - surface.ColumnWidth, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(target);

        var previewRect = originRect.Translate(new Vector(-surface.ColumnWidth, 0));
        Assert.True(
            RectShowsSession(window, previewRect),
            "No visible drag preview rendered in the left-hand column while the button is held");
        Assert.False(
            RectShowsSession(window, originRect),
            "The source block still renders in the origin column during a cross-day drag");

        window.MouseUp(target, MouseButton.Left);
        window.CaptureRenderedFrame();

        var saved = SavedBlock(blocks, tasks, "Lunch");
        Assert.Equal(TestShell.DesignDate.AddDays(-1), saved.Date); // Monday
        Assert.Equal(new TimeOnly(12, 0), saved.StartTime);
        window.Close();
    }

    /// <summary>BB-QA-003 regression: a same-column vertical drag keeps today's behavior.</summary>
    [AvaloniaFact]
    public void WeekView_SameColumnVerticalDrag_KeepsBlockVisibleInItsOwnColumn()
    {
        var (window, _, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 600);

        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var lunch = FindBlockView(window, "Lunch");
        var originRect = WindowRectOf(lunch, window);
        var start = CenterOf(lunch, window);
        var target = new Point(start.X, start.Y + 28);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(target);

        // Mid-drag the block itself stays visible in its own column at the new time...
        var movedRect = originRect.Translate(new Vector(0, 28));
        Assert.True(
            RectShowsSession(window, movedRect),
            "The block is not visible in its own column during a same-day vertical drag");
        // ...with no surface overlay involved.
        var canvas = surface.FindControl<Canvas>("DragPreviewCanvas");
        Assert.True(canvas is null || canvas.Children.Count == 0);

        window.MouseUp(target, MouseButton.Left);
        window.CaptureRenderedFrame();

        var saved = SavedBlock(blocks, tasks, "Lunch");
        Assert.Equal(TestShell.DesignDate, saved.Date);
        Assert.Equal(new TimeOnly(12, 30), saved.StartTime);
        window.Close();
    }

    /// <summary>
    /// BB-QA-003: a repeating task moves as a whole series, so a horizontal drag must
    /// not misleadingly preview a day change — the block stays visibly in its own column
    /// and release keeps the series anchor date.
    /// </summary>
    [AvaloniaFact]
    public void WeekView_RepeatingTask_HorizontalDrag_DoesNotPreviewADayChange()
    {
        var (window, _, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 420); // bring the 8:30 class into the viewport

        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var econ = FindBlockView(window, "AP Economics"); // Monday occurrence (first in tree order)
        var originRect = WindowRectOf(econ, window);
        var start = CenterOf(econ, window);
        var target = new Point(start.X + surface.ColumnWidth, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(target);

        // Mouse still held: the block must remain visible in its own column...
        Assert.True(
            RectShowsSession(window, originRect),
            "The repeating block vanished from its own column during a horizontal drag");
        // ...and no cross-day preview may appear, because release would reject the day change.
        var canvas = surface.FindControl<Canvas>("DragPreviewCanvas");
        Assert.True(
            canvas is null || canvas.Children.Count == 0,
            "A repeating task misleadingly previewed a day change");

        window.MouseUp(target, MouseButton.Left);
        window.CaptureRenderedFrame();

        // Whole-series semantics preserved: anchor date and time unchanged.
        var saved = SavedBlock(blocks, tasks, "AP Economics");
        Assert.Equal(TestShell.DesignDate.AddDays(-8), saved.Date);
        Assert.Equal(new TimeOnly(8, 30), saved.StartTime);
        window.Close();
    }

    /// <summary>
    /// BB-QA-003: losing pointer capture mid-drag must remove the preview, restore the
    /// source block to its original position, and persist nothing.
    /// </summary>
    [AvaloniaFact]
    public void CaptureLost_MidCrossColumnDrag_RemovesPreviewAndRestoresTheSource()
    {
        var (window, _, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 600);

        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var lunch = FindBlockView(window, "Lunch");
        var originRect = WindowRectOf(lunch, window);
        var start = CenterOf(lunch, window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + surface.ColumnWidth, start.Y + 28));

        lunch.RaiseEvent(new PointerCaptureLostEventArgs(lunch, null!));
        window.CaptureRenderedFrame();

        // The source renders again at its original position...
        Assert.True(
            RectShowsSession(window, originRect),
            "The source block was not restored after pointer capture was lost");
        // ...nothing was persisted...
        var saved = SavedBlock(blocks, tasks, "Lunch");
        Assert.Equal(TestShell.DesignDate, saved.Date);
        Assert.Equal(new TimeOnly(12, 0), saved.StartTime);
        // ...and no temporary preview state survives.
        var canvas = surface.FindControl<Canvas>("DragPreviewCanvas");
        Assert.True(canvas is null || canvas.Children.Count == 0);
        Assert.Equal(1, lunch.Opacity);
        Assert.Equal(720d, TimelinePanel.GetStartMinutes((Control)lunch.Parent!));
        window.Close();
    }

    [AvaloniaFact]
    public void PointerResize_OneOffTask_ToBottomOfDay_EndsAt2359()
    {
        var (window, _, blocks, tasks) = CreateSeededShellWindow((taskRepo, repo, clock) =>
        {
            var task = TaskItem.Create("Evening review", clock.Now);
            taskRepo.Add(task);
            repo.Add(CalendarBlock.CreateTaskSession(
                task.Id, TestShell.DesignDate, new TimeOnly(21, 0), new TimeOnly(22, 0), clock.Now));
        });
        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var scroller = surface.FindControl<ScrollViewer>("Scroller")!;
        scroller.ScrollToEnd();
        window.CaptureRenderedFrame();

        var view = FindBlockView(window, "Evening review");
        var grip = view.FindControl<Border>("ResizeGrip")!;
        Assert.True(grip.IsVisible); // local sessions are resizable
        var gripPoint = CenterOf(grip, window);

        window.MouseDown(gripPoint, MouseButton.Left);
        var exception = Record.Exception(() =>
        {
            window.MouseMove(new Point(gripPoint.X, gripPoint.Y + 400));
            window.MouseUp(new Point(gripPoint.X, gripPoint.Y + 400), MouseButton.Left);
        });

        Assert.Null(exception);
        var saved = SavedBlock(blocks, tasks, "Evening review");
        Assert.Equal(new TimeOnly(23, 59), saved.EndTime);
        window.Close();
    }

    /// <summary>Delete on a one-off session unschedules it; its task stays open.</summary>
    [AvaloniaFact]
    public void DeleteKey_OnOneOffSession_Unschedules_KeepingTheTask()
    {
        var (window, shell, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 600);

        var lunch = FindBlockView(window, "Lunch");
        lunch.Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.CaptureRenderedFrame();

        Assert.DoesNotContain(blocks.GetAll(), b =>
            b.TaskId is { } id && tasks.GetById(id)?.Title == "Lunch");
        var task = tasks.GetAll().Single(t => t.Title == "Lunch");
        Assert.False(task.IsCompleted);
        Assert.Null(shell.Calendar.ActiveTaskEditor);
        window.Close();
    }

    /// <summary>
    /// Removing a repeating series is never silent: the session editor opens
    /// pre-armed on the Remove-schedule confirmation, and confirming removes the
    /// schedule while the task survives (deleting a task is the whole-task
    /// editor's job — the F-03 scope rule).
    /// </summary>
    [AvaloniaFact]
    public void DeleteKey_OnRepeatingTask_AsksForConfirmation_ThenRemovesTheSchedule()
    {
        var (window, shell, blocks, tasks) = CreateSeededShellWindow();
        ScrollTo(window, 420);

        var econ = FindBlockView(window, "AP Economics");
        econ.Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.CaptureRenderedFrame();

        Assert.Single(tasks.GetAll(), t => t.Title == "AP Economics");
        var editor = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(SessionEditorMode.Repeating, editor.Mode);
        Assert.NotNull(editor.Confirmation);
        Assert.Equal("Remove schedule", editor.Confirmation.ConfirmLabel);

        editor.ConfirmPromptCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.Single(tasks.GetAll(), t => t.Title == "AP Economics"); // the task stays
        Assert.DoesNotContain(blocks.GetAll(), b => b.Recurrence is not null && !b.IsExternal);
        Assert.Null(shell.Calendar.ActiveTaskEditor);
        window.Close();
    }

    [AvaloniaFact]
    public void ExternalEvent_StaysLocked_ForClickDeleteAndResize()
    {
        var (window, shell, blocks, _) = CreateSeededShellWindow(
            (_, repo, clock) => repo.Add(ExternalEvent(clock)));
        ScrollTo(window, 700); // 13:30 external block into view

        var view = FindBlockView(window, "Imported standup");
        var vm = (CalendarBlockViewModel)view.DataContext!;
        Assert.True(vm.IsLocked);

        // Lock icon shown, resize grip absent.
        var lockIcon = view.GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(p => p.IsVisible
                && ToolTip.GetTip(p) is string tip
                && tip.Contains("never edits", StringComparison.Ordinal));
        Assert.NotNull(lockIcon);
        Assert.False(view.FindControl<Border>("ResizeGrip")!.IsVisible);

        // Click never opens the editor.
        Click(window, CenterOf(view, window));
        Assert.Null(shell.Calendar.ActiveTaskEditor);

        // Delete key never mutates.
        view.Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.CaptureRenderedFrame();
        Assert.NotNull(blocks.GetAll().SingleOrDefault(b => b.Title == "Imported standup"));
        Assert.Null(shell.Calendar.ActiveTaskEditor);
        window.Close();
    }
}
