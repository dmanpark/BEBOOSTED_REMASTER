using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The rendered half of the single-list change: one task with two sessions must show
/// one row on screen, and the three section headings must be gone.
/// </summary>
public sealed class ProjectDetailSingleListTests
{
    private static readonly DateOnly Today = TestShell.DesignDate;

    private static (MainWindow Window, ProjectDetailViewModel Detail) ShowProjectWithScheduledTask()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);

        var projects = shell.Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        var detail = projects.Detail!;

        var task = TaskItem.Create("Stats HW", DateTimeOffset.Now, projectId: detail.Project.Id);
        tasks.Add(task);
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0), DateTimeOffset.Now));
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(2), new TimeOnly(16, 0), new TimeOnly(17, 0), DateTimeOffset.Now));
        detail.Refresh();
        window.CaptureRenderedFrame();
        return (window, detail);
    }

    [AvaloniaFact]
    public void ATaskWithTwoSessions_RendersOneRow()
    {
        var (window, _) = ShowProjectWithScheduledTask();

        var titles = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text == "Stats HW")
            .ToList();

        Assert.Single(titles);
    }

    [AvaloniaFact]
    public void TheThreeSectionHeadings_AreGone()
    {
        var (window, _) = ShowProjectWithScheduledTask();

        var headings = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible
                && t.Text is "OPEN TASKS" or "SCHEDULED" or "RECENTLY COMPLETED")
            .Select(t => t.Text!)
            .ToList();

        Assert.True(headings.Count == 0, $"still rendering: {string.Join(", ", headings)}");
    }

    [AvaloniaFact]
    public void TheRow_ShowsItsNextSessionAsAnAffix()
    {
        var (window, detail) = ShowProjectWithScheduledTask();
        var row = Assert.Single(detail.Tasks);

        Assert.Equal(ProjectTaskStatus.Scheduled, row.Status);
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == row.StatusText);
    }

    /// <summary>
    /// A compiled binding to <see cref="ProjectTaskRowViewModel.IsCompletedRow"/> proves
    /// the member exists; it does not prove the row's own Border is the element it is
    /// bound to, nor that the class reaches the style that reads it. This renders a Done
    /// row, finds that Border in the visual tree, and checks the title it carries is
    /// struck through — 55% opacity and the word alone were doing all the work.
    /// </summary>
    [AvaloniaFact]
    public void ACompletedRow_RendersStruckThrough_WithTheDoneClass()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);

        var projects = shell.Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        var detail = projects.Detail!;

        var task = TaskItem.Create("Essay plan", DateTimeOffset.Now, projectId: detail.Project.Id);
        task.Complete(DateTimeOffset.Now);
        tasks.Add(task);
        detail.Refresh();
        window.CaptureRenderedFrame();

        Assert.Equal(ProjectTaskStatus.Done, Assert.Single(detail.Tasks).Status);

        var title = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(t => t.IsEffectivelyVisible && t.Text == "Essay plan");

        // The nearest Border ancestor belongs to a Button's own template (the row's
        // title and edit affordance are both Buttons); the row's own Border, carrying
        // Classes.taskDone, sits further up the visual tree.
        var row = title.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("taskDone"));

        Assert.NotNull(row);
        Assert.Equal(TextDecorations.Strikethrough, title.TextDecorations);
        Assert.StartsWith("✓", Assert.Single(detail.Tasks).StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// When a session elapses with no outcome, the row's status carries the block id and
    /// <see cref="ProjectTaskRowViewModel.HasSessionAffix"/> switches the affix from plain
    /// text to a clickable Button. Rendered, not just read off the view model.
    /// </summary>
    [AvaloniaFact]
    public void TheNeedsOutcomeAffix_RendersAsAClickableButton()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);

        var projects = shell.Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        var detail = projects.Detail!;

        var task = TaskItem.Create("Vocab review", DateTimeOffset.Now, projectId: detail.Project.Id);
        tasks.Add(task);
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(-2), new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now));
        detail.Refresh();
        window.CaptureRenderedFrame();

        var row = Assert.Single(detail.Tasks);
        Assert.True(row.HasSessionAffix);

        var affix = window.GetVisualDescendants()
            .OfType<Button>()
            .SingleOrDefault(b => b.IsEffectivelyVisible
                && b.Content is TextBlock text && text.Text == row.StatusText);

        Assert.NotNull(affix);
        Assert.True(affix!.IsEnabled);
    }

    /// <summary>
    /// A project with one task, rendered, so a test can reach into the gutter.
    /// </summary>
    private static (MainWindow Window, ProjectDetailViewModel Detail) ShowProject(
        InMemoryTaskRepository tasks,
        InMemoryCalendarBlockRepository blocks,
        InMemoryOccurrenceCompletionRepository completions)
    {
        var shell = TestShell.Create(tasks: tasks, blocks: blocks, completions: completions);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);

        var projects = shell.Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        return (window, projects.Detail!);
    }

    /// <summary>The row's 18px gutter control, found the way a screen reader finds it.</summary>
    private static Button? GutterCheck(MainWindow window, string accessibleName)
        => window.GetVisualDescendants()
            .OfType<Button>()
            .SingleOrDefault(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == accessibleName);

    /// <summary>
    /// A view-model flag proves nothing about the gutter: this renders a completed row,
    /// finds the circle by its accessible name, and checks it carries the `checked`
    /// class and shows the tick Path inside it — the same treatment the Daily list uses.
    /// </summary>
    [AvaloniaFact]
    public void ACompletedRow_RendersItsCircleChecked()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var completions = new InMemoryOccurrenceCompletionRepository();
        var (window, detail) = ShowProject(tasks, blocks, completions);

        var task = TaskItem.Create("Essay plan", DateTimeOffset.Now, projectId: detail.Project.Id);
        task.Complete(DateTimeOffset.Now);
        tasks.Add(task);
        detail.Refresh();
        window.CaptureRenderedFrame();

        var circle = GutterCheck(window, "Reopen Essay plan");

        Assert.NotNull(circle);
        Assert.True(circle!.IsEnabled);
        Assert.Contains("checked", circle.Classes);
        var tick = circle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        Assert.True(tick.IsEffectivelyVisible, "a done circle must render its tick");
    }

    /// <summary>
    /// A repeating task's row used to render an empty gutter, because the task cannot
    /// be completed as a whole. The circle now stands for the occurrence the affix
    /// names, so it must actually be on screen and clickable.
    /// </summary>
    [AvaloniaFact]
    public void ARepeatingRow_RendersACircleForTheOccurrenceItNames()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var completions = new InMemoryOccurrenceCompletionRepository();
        var (window, detail) = ShowProject(tasks, blocks, completions);

        var task = TaskItem.Create("Weekly review", DateTimeOffset.Now, projectId: detail.Project.Id);
        tasks.Add(task);
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today, new TimeOnly(16, 0), new TimeOnly(17, 0), DateTimeOffset.Now,
            BeBoosted.Domain.Scheduling.RecurrenceRule.Weekly(1, Today.DayOfWeek)));
        detail.Refresh();
        window.CaptureRenderedFrame();

        Assert.True(
            Assert.Single(detail.Tasks).ShowCheck,
            "a repeating task never completes as a whole, so its circle has to be the "
                + "occurrence's or there is no circle at all");

        // Named for the occurrence, not the task: rendered and read back the way a
        // screen reader would, so the scope the control announces is the scope it has.
        var circle = GutterCheck(window, $"Complete Weekly review on {Today:ddd d MMM}");

        Assert.NotNull(circle);
        Assert.True(circle!.IsEnabled);
        Assert.DoesNotContain("checked", circle.Classes);
    }

    /// <summary>
    /// The rendered half of the future-occurrence state: clicking the circle on a row
    /// naming a session three days out must leave a checked circle on screen, named for
    /// that same day. Read off the visual tree after the click, because a row that
    /// re-renders naming the week after would still satisfy any view-model assertion
    /// made before it.
    /// </summary>
    [AvaloniaFact]
    public void TickingAFutureOccurrence_LeavesACheckedCircleOnTheSameDay()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var completions = new InMemoryOccurrenceCompletionRepository();
        var (window, detail) = ShowProject(tasks, blocks, completions);

        var task = TaskItem.Create("Weekly review", DateTimeOffset.Now, projectId: detail.Project.Id);
        tasks.Add(task);
        var friday = Today.AddDays(3);
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, friday, new TimeOnly(16, 0), new TimeOnly(17, 0), DateTimeOffset.Now,
            BeBoosted.Domain.Scheduling.RecurrenceRule.Weekly(1, friday.DayOfWeek)));
        detail.Refresh();
        window.CaptureRenderedFrame();

        var circle = GutterCheck(window, $"Complete Weekly review on {friday:ddd d MMM}");
        Assert.NotNull(circle);

        circle!.Command!.Execute(circle.CommandParameter);
        window.CaptureRenderedFrame();

        var ticked = GutterCheck(window, $"Reopen Weekly review on {friday:ddd d MMM}");
        Assert.NotNull(ticked);
        Assert.Contains("checked", ticked!.Classes);
        Assert.True(
            ticked.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single()
                .IsEffectivelyVisible,
            "the tick must be on screen after the click");
    }

    /// <summary>
    /// Reversibility from the keyboard, which is the whole deliverable: the circle is a
    /// checkbox, so the second press has to be able to reach the same control. Refresh()
    /// clears Tasks and rebuilds every row, which destroys the container the press came
    /// from — without a restoration path Avalonia focuses nothing in its place and the
    /// undo is mouse-only. Asserted on the focused element after the rebuild, because
    /// every view-model assertion about the ticked occurrence stays green either way.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TickingFromTheKeyboard_LeavesFocusOnTheSameRowsCircle(bool repeating)
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var completions = new InMemoryOccurrenceCompletionRepository();
        var (window, detail) = ShowProject(tasks, blocks, completions);

        var task = TaskItem.Create("Weekly review", DateTimeOffset.Now, projectId: detail.Project.Id);
        tasks.Add(task);
        if (repeating)
        {
            blocks.Add(CalendarBlock.CreateTaskSession(
                task.Id, Today, new TimeOnly(16, 0), new TimeOnly(17, 0), DateTimeOffset.Now,
                BeBoosted.Domain.Scheduling.RecurrenceRule.Weekly(1, Today.DayOfWeek)));
        }

        detail.Refresh();
        window.CaptureRenderedFrame();

        var name = repeating
            ? $"Complete Weekly review on {Today:ddd d MMM}"
            : "Complete Weekly review";
        var circle = GutterCheck(window, name);
        Assert.NotNull(circle);

        Assert.True(circle!.Focus(), "the gutter circle must be focusable at all");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(circle, window.FocusManager!.GetFocusedElement());

        // Both halves: a Button clicks on Space's key UP (ClickMode.Release), and the
        // headless KeyPress helper is press-only.
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, keySymbol: null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, keySymbol: null);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        // The premise: the press really did tick the row, so what follows is about focus
        // and not about a key that did nothing.
        Assert.True(Assert.Single(detail.Tasks).IsDone);

        var focused = window.FocusManager!.GetFocusedElement() as Control;
        Assert.True(
            focused is Button { DataContext: ProjectTaskRowViewModel row } && row.TaskId == task.Id,
            $"focus landed on {focused?.GetType().Name ?? "nothing"} "
                + $"({(focused is null ? "null" : AutomationProperties.GetName(focused) ?? "unnamed")})");
    }

    /// <summary>
    /// Adding a task while standing in a project should not make the user re-pick the
    /// project they are already looking at.
    /// </summary>
    [AvaloniaFact]
    public void NewTaskFromAProject_PrefillsThatProject()
    {
        var (window, detail) = ShowProjectWithScheduledTask();
        var shell = (ShellViewModel)window.DataContext!;

        // A second project, named so it sorts ahead of "Schoolwork": the options list is
        // ordered by name, so the project we are standing in is no longer the first real
        // entry. Picking the right one now has to mean matching the id, not taking [1].
        shell.Projects.NewProjectName = "Admin";
        Assert.True(shell.Projects.TryCreateProject());
        shell.Projects.OpenProject(detail.Project.Id);

        detail.NewTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(detail.Project.Id, editor.SelectedProject?.Id);
        Assert.Equal("Schoolwork", editor.SelectedProject?.Name);
    }
}
