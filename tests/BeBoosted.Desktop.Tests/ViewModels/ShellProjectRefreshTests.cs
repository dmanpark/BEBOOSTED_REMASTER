using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// Moving or resizing a project's block on the calendar must refresh an open Project
/// detail through Calendar.DataChanged — its upcoming rows are snapshots, so a stale
/// detail would keep showing the old schedule.
/// </summary>
public sealed class ShellProjectRefreshTests
{
    private static readonly DateOnly Tomorrow = TestShell.DesignDate.AddDays(1);

    private static (ShellViewModel Shell, InMemoryCalendarBlockRepository Blocks, InMemoryTaskRepository Tasks)
        CreateShell()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(
            tasks: tasks, blocks: blocks, projects: new InMemoryProjectRepository());
        return (shell, blocks, tasks);
    }

    /// <summary>Creates the project (leaving its detail open) and a linked commitment 16:00–17:00 tomorrow.</summary>
    private static CalendarBlockId CreateProjectWithLinkedCommitment(
        ShellViewModel shell, InMemoryCalendarBlockRepository blocks)
    {
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.NewProjectName = "Schoolwork";
        Assert.True(shell.Projects.TryCreateProject());

        shell.NavigateCommand.Execute(AppSection.Calendar);
        shell.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var editor = shell.Calendar.CommitmentEditor!;
        editor.Title = "Stats HW";
        editor.Date = new DateTimeOffset(Tomorrow.ToDateTime(TimeOnly.MinValue));
        editor.Start = new TimeSpan(16, 0, 0);
        editor.End = new TimeSpan(17, 0, 0);
        editor.SelectedProject = editor.ProjectOptions.Single(o => o.Name == "Schoolwork");
        editor.SaveCommand.Execute(null);
        Assert.Null(shell.Calendar.CommitmentEditor);

        var row = Assert.Single(shell.Projects.Detail!.ScheduledBlocks);
        Assert.Equal(new TimeOnly(16, 0), row.Start);
        return blocks.GetAll().Single(b => b.Title == "Stats HW").Id;
    }

    [Fact]
    public void MovingALinkedCommitment_RefreshesTheOpenProjectDetail()
    {
        var (shell, blocks, _) = CreateShell();
        var blockId = CreateProjectWithLinkedCommitment(shell, blocks);

        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;
        shell.Calendar.MoveBlock(blockId, Tomorrow, new TimeOnly(18, 0));
        Assert.Equal(1, changes);

        shell.NavigateCommand.Execute(AppSection.Projects);
        var updated = Assert.Single(shell.Projects.Detail!.ScheduledBlocks);
        Assert.Equal(new TimeOnly(18, 0), updated.Start);
    }

    [Fact]
    public void ResizingALinkedCommitment_RefreshesTheDetail_ButFailuresStayQuiet()
    {
        var (shell, blocks, _) = CreateShell();
        var blockId = CreateProjectWithLinkedCommitment(shell, blocks);

        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;
        shell.Calendar.ResizeBlockTo(blockId, new TimeOnly(19, 0));
        Assert.Equal(1, changes);

        shell.NavigateCommand.Execute(AppSection.Projects);
        var updated = Assert.Single(shell.Projects.Detail!.ScheduledBlocks);
        Assert.Equal(TimeSpan.FromHours(3), updated.Duration);

        // A rejected resize (end before start) must not announce a successful change.
        shell.Calendar.ResizeBlockTo(blockId, new TimeOnly(15, 0));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void CompletingFromCalendar_RefreshesTheOpenProjectDetail_Once()
    {
        var (shell, blocks, _) = CreateShell();
        var blockId = CreateProjectWithLinkedCommitment(shell, blocks);
        shell.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;

        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;
        CalendarBlockFor(shell, blockId).ToggleCommitmentDoneCommand.Execute(null);

        Assert.Equal(1, changes);
        Assert.True(CalendarBlockFor(shell, blockId).IsDone);
        Assert.Empty(shell.Projects.Detail!.ScheduledBlocks);
        var done = Assert.Single(shell.Projects.Detail.CompletedScheduledBlocks);
        Assert.True(done.IsDone);

        // Reopening from the calendar updates both surfaces again.
        CalendarBlockFor(shell, blockId).ToggleCommitmentDoneCommand.Execute(null);
        Assert.Equal(2, changes);
        Assert.False(CalendarBlockFor(shell, blockId).IsDone);
        Assert.Single(shell.Projects.Detail.ScheduledBlocks);
        Assert.Empty(shell.Projects.Detail.CompletedScheduledBlocks);
    }

    [Fact]
    public void CompletingFromProjectDetail_RefreshesTheCalendar_Once()
    {
        var (shell, blocks, _) = CreateShell();
        var blockId = CreateProjectWithLinkedCommitment(shell, blocks);
        shell.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;

        var calendarChanges = 0;
        shell.Projects.CalendarDataChanged += () => calendarChanges++;

        var row = Assert.Single(shell.Projects.Detail!.ScheduledBlocks);
        row.ToggleCompletionCommand.Execute(null);

        Assert.Equal(1, calendarChanges);
        Assert.True(CalendarBlockFor(shell, blockId).IsDone);
        var done = Assert.Single(shell.Projects.Detail.CompletedScheduledBlocks);

        // Reopening from the project page updates the calendar again.
        done.ToggleCompletionCommand.Execute(null);
        Assert.Equal(2, calendarChanges);
        Assert.False(CalendarBlockFor(shell, blockId).IsDone);
    }

    [Fact]
    public void NoOpCompletionRequests_EmitNoChangeNotifications()
    {
        var (shell, blocks, _) = CreateShell();
        var blockId = CreateProjectWithLinkedCommitment(shell, blocks);

        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;
        shell.Calendar.SetCommitmentOccurrenceDone(blockId, Tomorrow, done: false); // already open

        Assert.Equal(0, changes);
    }

    private static CalendarBlockViewModel CalendarBlockFor(ShellViewModel shell, CalendarBlockId id)
        => shell.Calendar.Days.SelectMany(d => d.Blocks).First(b => !b.IsProposal && b.Id == id);

    [Fact]
    public void MovingATaskBackedProjectBlock_RefreshesTheOpenProjectDetail()
    {
        var (shell, blocks, tasks) = CreateShell();
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.NewProjectName = "Schoolwork";
        Assert.True(shell.Projects.TryCreateProject());
        var projectId = shell.Projects.Detail!.Project.Id;

        var clock = new FakeClock(TestShell.DesignDate);
        var task = TaskItem.Create(
            "Practice", clock.Now, estimatedDuration: TimeSpan.FromMinutes(60), projectId: projectId);
        tasks.Add(task);
        shell.Calendar.ScheduleTask(task.Id, Tomorrow, new TimeOnly(9, 0));
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(shell.Projects.Detail!.ScheduledBlocks).Start);

        var blockId = blocks.GetAll().Single(b => b.TaskId == task.Id).Id;
        shell.Calendar.MoveBlock(blockId, Tomorrow, new TimeOnly(11, 0));

        shell.NavigateCommand.Execute(AppSection.Projects);
        var updated = Assert.Single(shell.Projects.Detail!.ScheduledBlocks);
        Assert.Equal(new TimeOnly(11, 0), updated.Start);
    }
}
