using System.Collections.Specialized;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// Mutating a project's task on the calendar — or assigning a task to a project from
/// any editor entry point — must refresh an open Project detail through one logical
/// notification chain. Its rows are snapshots, so a stale detail would keep showing
/// the old schedule or miss the newly assigned task.
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

    /// <summary>
    /// Creates the project (leaving its detail open) and a linked scheduled task
    /// 16:00–17:00 tomorrow through the unified editor — repeating when asked.
    /// </summary>
    private static CalendarBlockId CreateProjectWithScheduledTask(
        ShellViewModel shell, InMemoryCalendarBlockRepository blocks,
        InMemoryTaskRepository tasks, bool repeating = false)
    {
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.NewProjectName = "Schoolwork";
        Assert.True(shell.Projects.TryCreateProject());

        shell.NavigateCommand.Execute(AppSection.Calendar);
        shell.Calendar.OpenNewTaskEditorCommand.Execute(null);
        var editor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        editor.Title = "Stats HW";
        editor.AddSessionCommand.Execute(null); // create mode: reveal the first session
        editor.InlineSchedule.Date = new DateTimeOffset(Tomorrow.ToDateTime(TimeOnly.MinValue));
        editor.InlineSchedule.Start = new TimeSpan(16, 0, 0);
        editor.InlineSchedule.End = new TimeSpan(17, 0, 0);
        editor.SelectedProject = editor.ProjectOptions.Single(o => o.Name == "Schoolwork");
        if (repeating)
        {
            editor.InlineSchedule.RepeatsWeekly = true;
            editor.InlineSchedule.Days.Single(d => d.Day == DayOfWeek.Wednesday).IsSelected = true;
        }

        editor.SaveCommand.Execute(null);
        Assert.Null(shell.Calendar.ActiveTaskEditor);

        var row = Assert.Single(shell.Projects.Detail!.ScheduledBlocks);
        Assert.Equal(new TimeOnly(16, 0), row.Start);
        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");
        return blocks.GetForTask(task.Id).Single().Id;
    }

    [Fact]
    public void MovingALinkedTask_RefreshesTheOpenProjectDetail()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks);

        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;
        shell.Calendar.MoveBlock(blockId, Tomorrow, new TimeOnly(18, 0));
        Assert.Equal(1, changes);

        shell.NavigateCommand.Execute(AppSection.Projects);
        var updated = Assert.Single(shell.Projects.Detail!.ScheduledBlocks);
        Assert.Equal(new TimeOnly(18, 0), updated.Start);
    }

    [Fact]
    public void ResizingALinkedTask_RefreshesTheDetail_ButFailuresStayQuiet()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks);

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
    public void CompletingAnOccurrenceFromCalendar_RefreshesTheOpenProjectDetail_Once()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks, repeating: true);
        shell.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;

        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;
        CalendarBlockFor(shell, blockId, Tomorrow).ToggleOccurrenceDoneCommand.Execute(null);

        Assert.Equal(1, changes);
        Assert.True(CalendarBlockFor(shell, blockId, Tomorrow).IsDone);
        var done = Assert.Single(shell.Projects.Detail!.CompletedScheduledBlocks);
        Assert.True(done.IsDone);
        Assert.Equal(Tomorrow, done.Date);

        // Reopening from the calendar updates both surfaces again.
        CalendarBlockFor(shell, blockId, Tomorrow).ToggleOccurrenceDoneCommand.Execute(null);
        Assert.Equal(2, changes);
        Assert.False(CalendarBlockFor(shell, blockId, Tomorrow).IsDone);
        Assert.Empty(shell.Projects.Detail.CompletedScheduledBlocks);
    }

    /// <summary>
    /// Occurrence completion from the project page announces through the same
    /// central chain as every other mutation: the calendar reloads, the Inbox and
    /// card counts refresh, and the open detail refreshes exactly once — never
    /// eagerly plus again through the shared event.
    /// </summary>
    [Fact]
    public void CompletingAnOccurrenceFromProjectDetail_AnnouncesThroughTheOneChain()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks, repeating: true);
        shell.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;
        var detail = shell.Projects.Detail!;
        var (changes, detailRefreshes, inboxResets) = (0, 0, 0);
        shell.Calendar.DataChanged += () => changes++;
        detail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectDetailViewModel.HasOpenTasks))
            {
                detailRefreshes++;
            }
        };
        shell.Inbox.Tasks.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                inboxResets++;
            }
        };

        var row = detail.ScheduledBlocks.Single(r => r.Date == Tomorrow);
        row.ToggleCompletionCommand.Execute(null);

        Assert.Equal(1, changes);
        Assert.Equal(1, detailRefreshes);
        Assert.Equal(1, inboxResets);
        Assert.True(CalendarBlockFor(shell, blockId, Tomorrow).IsDone);
        var done = Assert.Single(detail.CompletedScheduledBlocks);

        // Reopening from the project page flows through the same chain again.
        done.ToggleCompletionCommand.Execute(null);
        Assert.Equal(2, changes);
        Assert.Equal(2, detailRefreshes);
        Assert.False(CalendarBlockFor(shell, blockId, Tomorrow).IsDone);

        // A no-op request emits no success notification anywhere.
        detail.SetOccurrenceCompletion(blockId, Tomorrow, completed: false);
        Assert.Equal(2, changes);
        Assert.Equal(2, detailRefreshes);
    }

    /// <summary>
    /// Completing a scheduled one-off from the project detail must reconcile the
    /// Task and its session outcome together, exactly like the canonical editor.
    /// </summary>
    [Fact]
    public void CompletingAScheduledOneOff_FromProjectDetail_MarksItsSessionDone()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks);
        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;

        var row = shell.Projects.Detail!.OpenTasks.Single(t => t.Title == "Stats HW");
        row.CompleteCommand.Execute(null);

        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");
        Assert.True(task.IsCompleted);
        Assert.Equal(BlockOutcome.Done, blocks.GetById(blockId)!.Outcome);
        Assert.Equal(1, changes);
    }

    /// <summary>
    /// One central chain, each dependent exactly once: the open detail must not
    /// refresh eagerly and then again through the shared event.
    /// </summary>
    [Fact]
    public void CompletingATask_FromProjectDetail_RefreshesEachDependentExactlyOnce()
    {
        var (shell, blocks, tasks) = CreateShell();
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.NewProjectName = "Schoolwork";
        Assert.True(shell.Projects.TryCreateProject());
        var detail = shell.Projects.Detail!;
        var clock = new FakeClock(TestShell.DesignDate);
        var task = TaskItem.Create("Essay plan", clock.Now, projectId: detail.Project.Id);
        tasks.Add(task);
        detail.Refresh();

        var (changes, detailRefreshes, inboxResets, cardResets) = (0, 0, 0, 0);
        shell.Calendar.DataChanged += () => changes++;
        detail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectDetailViewModel.HasOpenTasks))
            {
                detailRefreshes++;
            }
        };
        shell.Inbox.Tasks.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                inboxResets++;
            }
        };
        shell.Projects.Projects.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                cardResets++;
            }
        };

        detail.OpenTasks.Single(t => t.Title == "Essay plan").CompleteCommand.Execute(null);

        Assert.Equal(1, detailRefreshes);
        Assert.Equal(1, changes);
        Assert.Equal(1, inboxResets);
        Assert.Equal(1, cardResets);
        Assert.Contains(detail.RecentlyCompleted, t => t.Title == "Essay plan");
    }

    [Fact]
    public void NoOpCompletionRequests_EmitNoChangeNotifications()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks, repeating: true);

        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;
        shell.Calendar.SetOccurrenceDone(blockId, Tomorrow, done: false); // already open

        Assert.Equal(0, changes);
    }

    private static CalendarBlockViewModel CalendarBlockFor(
        ShellViewModel shell, CalendarBlockId id, DateOnly? date = null)
        => shell.Calendar.Days
            .Where(d => date is null || d.Date == date)
            .SelectMany(d => d.Blocks)
            .First(b => !b.IsProposal && b.Id == id);

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

    /// <summary>
    /// TDD phase 14, the reported failure: edit a task (PIQ2), assign it to a project
    /// (CAPPs), save — the already-open project must list it immediately, its scheduled
    /// session must appear in the Scheduled section, and card counts must update.
    /// </summary>
    [Fact]
    public void AssigningAProjectThroughTheTaskEditor_RefreshesTheOpenProject()
    {
        var (shell, blocks, tasks) = CreateShell();
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.NewProjectName = "CAPPs";
        Assert.True(shell.Projects.TryCreateProject());

        var clock = new FakeClock(TestShell.DesignDate);
        var piq2 = TaskItem.Create("PIQ2", clock.Now);
        tasks.Add(piq2);
        shell.Calendar.ScheduleTask(piq2.Id, Tomorrow, new TimeOnly(10, 0));

        // Edit through the one canonical editor while the CAPPs detail stays open.
        shell.Calendar.OpenTaskEditorForTask(piq2.Id);
        var editor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        editor.SelectedProject = editor.ProjectOptions.Single(o => o.Name == "CAPPs");
        editor.SaveCommand.Execute(null);

        Assert.Equal(shell.Projects.Detail!.Project.Id, tasks.GetById(piq2.Id)!.ProjectId);
        Assert.Contains(shell.Projects.Detail.OpenTasks, t => t.Title == "PIQ2");
        Assert.Contains(shell.Projects.Detail.ScheduledBlocks, r => r.Title == "PIQ2");

        // "No project" removes it again — still through the same chain.
        shell.Calendar.OpenTaskEditorForTask(piq2.Id);
        var reopened = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        reopened.SelectedProject = reopened.ProjectOptions.Single(o => o.Name == "No project");
        reopened.SaveCommand.Execute(null);
        Assert.DoesNotContain(shell.Projects.Detail.OpenTasks, t => t.Title == "PIQ2");

        // The project index card counts refresh through the same notification chain.
        shell.Calendar.OpenTaskEditorForTask(piq2.Id);
        var again = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        again.SelectedProject = again.ProjectOptions.Single(o => o.Name == "CAPPs");
        again.SaveCommand.Execute(null);
        shell.Projects.CloseDetailCommand.Execute(null);
        Assert.Contains("1 open task", shell.Projects.Projects.Single().MetaText);
    }
}
