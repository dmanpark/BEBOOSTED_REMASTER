using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
    /// bound to. This renders a Done row and finds that Border in the visual tree.
    /// </summary>
    [AvaloniaFact]
    public void ACompletedRow_RendersWithTheDoneClass()
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
        // Classes.done, sits further up the visual tree.
        var row = title.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("done"));

        Assert.NotNull(row);
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
    /// Adding a task while standing in a project should not make the user re-pick the
    /// project they are already looking at.
    /// </summary>
    [AvaloniaFact]
    public void NewTaskFromAProject_PrefillsThatProject()
    {
        var (window, detail) = ShowProjectWithScheduledTask();
        var shell = (ShellViewModel)window.DataContext!;

        detail.NewTaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(detail.Project.Id, editor.SelectedProject?.Id);
    }
}
