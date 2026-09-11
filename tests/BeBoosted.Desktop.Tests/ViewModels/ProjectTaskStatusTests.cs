using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// Project detail used to hold four collections across two row types, so a task and
/// the sessions serving it were peers on one screen - a task with two sessions
/// rendered three rows. One row per task, with the session folded in as a status.
/// </summary>
public sealed class ProjectTaskStatusTests
{
    private static readonly DateOnly Today = TestShell.DesignDate;

    private static (ProjectDetailViewModel Detail, InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks) OpenProject()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var projects = shell.Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        return (projects.Detail!, tasks, blocks);
    }

    private static TaskItem AddTask(
        ProjectDetailViewModel detail, InMemoryTaskRepository tasks, string title)
    {
        var task = TaskItem.Create(title, DateTimeOffset.Now, projectId: detail.Project.Id);
        tasks.Add(task);
        return task;
    }

    [Fact]
    public void ATaskWithTwoSessions_ProducesExactlyOneRow()
    {
        var (detail, tasks, blocks) = OpenProject();
        var task = AddTask(detail, tasks, "Stats HW");
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0), DateTimeOffset.Now));
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(2), new TimeOnly(16, 0), new TimeOnly(17, 0), DateTimeOffset.Now));

        detail.Refresh();

        var row = Assert.Single(detail.Tasks);
        Assert.Equal("Stats HW", row.Title);
    }

    [Fact]
    public void AScheduledTask_NamesItsNextSession_NotAnyLaterOne()
    {
        var (detail, tasks, blocks) = OpenProject();
        var task = AddTask(detail, tasks, "Stats HW");
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(3), new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now));
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0), DateTimeOffset.Now));

        detail.Refresh();

        var row = Assert.Single(detail.Tasks);
        Assert.Equal(ProjectTaskStatus.Scheduled, row.Status);
        Assert.Equal(Today.AddDays(1), row.SessionDate);
        Assert.Equal(new TimeOnly(16, 0), row.SessionStart);
    }

    [Fact]
    public void ATaskWithNoSessions_IsUnscheduled()
    {
        var (detail, tasks, _) = OpenProject();
        AddTask(detail, tasks, "Essay outline");

        detail.Refresh();

        Assert.Equal(ProjectTaskStatus.Unscheduled, Assert.Single(detail.Tasks).Status);
    }

    /// <summary>
    /// The whole point of the priority order: a task that is both stuck and scheduled
    /// reads as stuck, because that is the one that needs a decision.
    /// </summary>
    [Fact]
    public void AnElapsedSessionWithNoOutcome_OutranksALaterScheduledOne()
    {
        var (detail, tasks, blocks) = OpenProject();
        var task = AddTask(detail, tasks, "Vocab review");
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(-2), new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now));
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(2), new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now));

        detail.Refresh();

        Assert.Equal(ProjectTaskStatus.NeedsOutcome, Assert.Single(detail.Tasks).Status);
    }

    [Fact]
    public void RowsSortByStatusPriority_StuckFirst_DoneLast()
    {
        var (detail, tasks, blocks) = OpenProject();
        AddTask(detail, tasks, "Unscheduled one");

        var scheduled = AddTask(detail, tasks, "Scheduled one");
        blocks.Add(CalendarBlock.CreateTaskSession(
            scheduled.Id, Today.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now));

        var stuck = AddTask(detail, tasks, "Stuck one");
        blocks.Add(CalendarBlock.CreateTaskSession(
            stuck.Id, Today.AddDays(-1), new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now));

        detail.Refresh();

        Assert.Equal(
            new[] { "Stuck one", "Scheduled one", "Unscheduled one" },
            detail.Tasks.Select(r => r.Title).ToArray());
    }
}
