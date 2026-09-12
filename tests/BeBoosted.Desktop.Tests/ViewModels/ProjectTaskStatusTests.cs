using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
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

        var done = AddTask(detail, tasks, "Done one");
        done.Complete(DateTimeOffset.Now);
        tasks.Update(done);

        detail.Refresh();

        Assert.Equal(
            new[] { "Stuck one", "Scheduled one", "Unscheduled one", "Done one" },
            detail.Tasks.Select(r => r.Title).ToArray());
    }

    /// <summary>
    /// GetScheduledBlocks only expands a repeating series across a +/-14-day window, so a
    /// series with no occurrence in that window must not be mistaken for a non-repeating
    /// task - it still completes per occurrence, never as a whole, so the project row's
    /// whole-task completion control must stay unavailable.
    /// </summary>
    [Fact]
    public void ARepeatingSeriesOutsideTheWindow_StillForbidsWholeTaskCompletion()
    {
        var (detail, tasks, blocks) = OpenProject();
        var task = AddTask(detail, tasks, "Weekly review");
        var farAnchor = Today.AddDays(30);
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, farAnchor, new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now,
            RecurrenceRule.Weekly(1, farAnchor.DayOfWeek)));

        detail.Refresh();

        var row = Assert.Single(detail.Tasks);
        Assert.False(row.CanComplete);
    }

    /// <summary>
    /// The other half of the same window, missed when the first was fixed: with no
    /// occurrence inside +/-14 days the windowed session list is empty, and falling
    /// through to Unscheduled tells the user the opposite of the truth about their own
    /// data. A recurrence has no end date, so a task that has one always has a next
    /// occurrence. The row cannot name a time without expanding the series itself, so
    /// it says it is scheduled and stops there.
    /// </summary>
    [Fact]
    public void ARepeatingSeriesOutsideTheWindow_IsScheduled_NotUnscheduled()
    {
        var (detail, tasks, blocks) = OpenProject();
        var task = AddTask(detail, tasks, "Weekly review");
        var farAnchor = Today.AddDays(30);
        blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, farAnchor, new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now,
            RecurrenceRule.Weekly(1, farAnchor.DayOfWeek)));

        detail.Refresh();

        var row = Assert.Single(detail.Tasks);
        Assert.Equal(ProjectTaskStatus.Scheduled, row.Status);
        Assert.DoesNotContain("unscheduled", row.StatusText, StringComparison.Ordinal);
        // No date to name, so no affix to click - the affix opens the session it names.
        Assert.Null(row.SessionDate);
        Assert.False(row.HasSessionAffix);
    }

    /// <summary>
    /// A one-off session is never windowed out, however far ahead it sits, so the
    /// Unscheduled fall-through still has to mean what it says for those: this pins
    /// that the repeating fix above did not turn every session-less row scheduled.
    /// </summary>
    [Fact]
    public void AOneOffSessionResolvedAsDidntHappen_LeavesItsTaskUnscheduled()
    {
        var (detail, tasks, blocks) = OpenProject();
        var task = AddTask(detail, tasks, "Essay outline");
        var session = CalendarBlock.CreateTaskSession(
            task.Id, Today.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now);
        session.RecordOutcome(BlockOutcome.DidntHappen, DateTimeOffset.Now);
        blocks.Add(session);

        detail.Refresh();

        // The block still exists on the task; the schedule has let it go and the task
        // is back in the open lists, which is exactly what "unscheduled" means here.
        Assert.Equal(ProjectTaskStatus.Unscheduled, Assert.Single(detail.Tasks).Status);
    }

    /// <summary>
    /// The header count is about the project, not about the rows: Tasks holds the open
    /// tasks plus at most three recently completed, so counting rows made a project
    /// with four open and many done report a number true of neither. It is worded the
    /// way the project card words it, so the two agree one click apart.
    /// </summary>
    [Fact]
    public void TheHeaderCount_CountsOpenTasks_NotRows()
    {
        var (detail, tasks, _) = OpenProject();
        for (var i = 0; i < 4; i++)
        {
            AddTask(detail, tasks, $"Open {i}");
        }

        // More completed tasks than GetProjectTasks will ever surface as rows.
        for (var i = 0; i < 6; i++)
        {
            var done = AddTask(detail, tasks, $"Done {i}");
            done.Complete(DateTimeOffset.Now);
            tasks.Update(done);
        }

        detail.Refresh();

        Assert.True(detail.Tasks.Count > 4, "the completed sweep must be putting rows on screen");
        Assert.NotEqual($"{detail.Tasks.Count} tasks", detail.TaskCountText);
        Assert.Equal("4 open tasks", detail.TaskCountText);
    }

    [Fact]
    public void TheHeaderCount_SaysTaskInTheSingular()
    {
        var (detail, tasks, _) = OpenProject();
        AddTask(detail, tasks, "Essay outline");
        detail.Refresh();
        Assert.Equal("1 open task", detail.TaskCountText);

        AddTask(detail, tasks, "Stats HW");
        detail.Refresh();
        Assert.Equal("2 open tasks", detail.TaskCountText);
    }

    /// <summary>
    /// The card and the detail are one click apart and used to report different
    /// numbers for the same project. Whatever the wording becomes, it must be one
    /// wording - so this compares them rather than restating a literal.
    /// </summary>
    [Fact]
    public void TheHeaderCount_AgreesWithTheProjectCard()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var projects = shell.Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        var detail = projects.Detail!;

        AddTask(detail, tasks, "Essay outline");
        AddTask(detail, tasks, "Stats HW");
        var done = AddTask(detail, tasks, "Reading");
        done.Complete(DateTimeOffset.Now);
        tasks.Update(done);
        detail.Refresh();

        projects.CloseDetailCommand.Execute(null);
        var card = projects.Projects.Single(p => p.Name == "Schoolwork");

        Assert.Equal("2 open tasks", detail.TaskCountText);
        Assert.StartsWith(detail.TaskCountText, card.MetaText, StringComparison.Ordinal);
    }
}
