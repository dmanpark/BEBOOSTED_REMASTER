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

    /// <summary>A second task on the same project, left unscheduled so it sits in the Inbox.</summary>
    private static void AddUnscheduledProjectTask(
        ShellViewModel shell, InMemoryTaskRepository tasks, string title)
    {
        var project = shell.Projects.Projects.Single().Project;
        var task = TaskItem.Create(title, TestShell.DesignDate.ToDateTime(TimeOnly.MinValue), projectId: project.Id);
        tasks.Add(task);
    }

    [Fact]
    public void RenamingAProject_RelabelsEverySurfaceWithoutAManualReload()
    {
        var (shell, blocks, tasks) = CreateShell();
        CreateProjectWithScheduledTask(shell, blocks, tasks);
        AddUnscheduledProjectTask(shell, tasks, "Read chapter 4");

        shell.NavigateCommand.Execute(AppSection.Projects);
        var detail = shell.Projects.Detail!;
        detail.BeginRename();
        detail.RenameName = "Coursework";
        Assert.True(detail.TryCommitRename());

        // No ReloadList(), no re-navigation: the chain must have done it.
        Assert.Equal("Coursework", detail.Name);
        Assert.Equal("Coursework", shell.Projects.Projects.Single().Name);

        shell.NavigateCommand.Execute(AppSection.Calendar);
        shell.Calendar.VisibleDate = Tomorrow; // the Daily list defaults to today; the session is tomorrow

        // Positively: the scheduled row now carries the new label.
        var scheduled = shell.Calendar.Daily.ScheduledRows.Single(r => r.Title == "Stats HW");
        Assert.Equal("Coursework", scheduled.ProjectName);

        // Positively: the Daily list's unscheduled row does too.
        var unscheduled = shell.Calendar.Daily.UnscheduledRows.Single(r => r.Title == "Read chapter 4");
        Assert.Equal("Coursework", unscheduled.ProjectName);

        // And the Inbox proper, which is a different surface with its own snapshot.
        var inboxRow = shell.Inbox.Tasks.Single(r => r.Title == "Read chapter 4");
        Assert.Equal("Coursework", inboxRow.MetaText);
    }

    [Fact]
    public void DeletingAProject_ClearsItsLabelsOnEverySurfaceWithoutAManualReload()
    {
        var (shell, blocks, tasks) = CreateShell();
        CreateProjectWithScheduledTask(shell, blocks, tasks);
        AddUnscheduledProjectTask(shell, tasks, "Read chapter 4");

        shell.NavigateCommand.Execute(AppSection.Projects);
        var detail = shell.Projects.Detail!;
        detail.RequestDeleteCommand.Execute(null);
        detail.ConfirmPromptCommand.Execute(null);

        Assert.Empty(shell.Projects.Projects);

        shell.NavigateCommand.Execute(AppSection.Calendar);
        shell.Calendar.VisibleDate = Tomorrow; // the Daily list defaults to today; the session is tomorrow

        // Both tasks survive, both unassigned — asserted on the rows themselves, so a
        // vanished row cannot pass for a cleared label.
        var scheduled = shell.Calendar.Daily.ScheduledRows.Single(r => r.Title == "Stats HW");
        Assert.Null(scheduled.ProjectName);

        var unscheduled = shell.Calendar.Daily.UnscheduledRows.Single(r => r.Title == "Read chapter 4");
        Assert.Null(unscheduled.ProjectName);

        // The Inbox proper: no project, so MetaText collapses to empty.
        var inboxRow = shell.Inbox.Tasks.Single(r => r.Title == "Read chapter 4");
        Assert.Equal(string.Empty, inboxRow.MetaText);
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
    /// The project page completes a one-off session against its block. Routing it
    /// through the occurrence path would throw — a one-off has no occurrences.
    /// </summary>
    [Fact]
    public void CompletingAOneOffSessionFromTheProjectPage_ResolvesThatSessionOnly()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks);
        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");
        var clock = new FakeClock(TestShell.DesignDate);
        var sibling = CalendarBlock.CreateTaskSession(
            task.Id, Tomorrow, new TimeOnly(19, 0), new TimeOnly(20, 0), clock.Now);
        blocks.Add(sibling);
        shell.Projects.Detail!.Refresh();
        shell.NavigateCommand.Execute(AppSection.Projects);
        Assert.Equal(2, shell.Projects.Detail!.ScheduledBlocks.Count);

        shell.Projects.Detail.ScheduledBlocks.Single(r => r.BlockId == blockId)
            .ToggleCompletionCommand.Execute(null);

        var done = Assert.Single(shell.Projects.Detail!.CompletedScheduledBlocks);
        Assert.Equal(blockId, done.BlockId);
        Assert.Equal(BlockOutcome.None, blocks.GetById(sibling.Id)!.Outcome);
        Assert.False(tasks.GetById(task.Id)!.IsCompleted);
    }

    /// <summary>
    /// A one-off row also renders Done because its parent Task was completed as a
    /// whole. Undoing there must reopen the TASK: clearing this session's outcome
    /// alone would leave the row checked (a dead click) and strand an unresolved
    /// session on a completed task.
    /// </summary>
    [Fact]
    public void UndoingAOneOffSessionOfACompletedTask_FromTheProjectPage_ReopensTheTask()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks);
        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.Detail!.OpenTasks.Single(t => t.Title == "Stats HW")
            .CompleteCommand.Execute(null);
        var done = Assert.Single(shell.Projects.Detail!.CompletedScheduledBlocks);
        Assert.Equal(blockId, done.BlockId);
        Assert.True(done.IsDone);

        done.ToggleCompletionCommand.Execute(null);

        Assert.False(tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, blocks.GetById(blockId)!.Outcome);
        Assert.Empty(shell.Projects.Detail!.CompletedScheduledBlocks);
        var reopened = Assert.Single(shell.Projects.Detail!.ScheduledBlocks);
        Assert.Equal(blockId, reopened.BlockId);
        Assert.False(reopened.IsDone);
    }

    /// <summary>
    /// The branch on the toggle exists to protect this: a repeating row must keep
    /// completing per occurrence through the same path as before, and must never
    /// throw now that one-off rows also carry a completion control.
    /// </summary>
    [Fact]
    public void CompletingARepeatingSessionFromTheProjectPage_StillCompletesPerOccurrence()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks, repeating: true);
        shell.NavigateCommand.Execute(AppSection.Projects);

        var row = shell.Projects.Detail!.ScheduledBlocks.Single(r => r.BlockId == blockId);
        Assert.True(row.IsRepeating);
        row.ToggleCompletionCommand.Execute(null);

        var done = Assert.Single(shell.Projects.Detail!.CompletedScheduledBlocks);
        Assert.Equal(blockId, done.BlockId);
        Assert.True(done.IsRepeating);

        done.ToggleCompletionCommand.Execute(null);
        Assert.Empty(shell.Projects.Detail!.CompletedScheduledBlocks);
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
