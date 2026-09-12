using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
}
