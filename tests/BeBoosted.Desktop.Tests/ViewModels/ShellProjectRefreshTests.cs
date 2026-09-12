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

        var row = Assert.Single(shell.Projects.Detail!.Tasks);
        Assert.Equal(new TimeOnly(16, 0), row.SessionStart);
        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");
        return blocks.GetForTask(task.Id).Single().Id;
    }

    /// <summary>
    /// A second task on the same project, left unscheduled so it sits in the Inbox.
    /// Seeds the Inbox by announcing through the same central chain the production
    /// code uses — otherwise nothing has ever told the Inbox to load this task, and a
    /// later lookup fails on a missing row instead of pinning a stale label.
    /// </summary>
    private static void AddUnscheduledProjectTask(
        ShellViewModel shell, InMemoryTaskRepository tasks, string title)
    {
        var project = shell.Projects.Projects.Single().Project;
        var task = TaskItem.Create(title, TestShell.DesignDate.ToDateTime(TimeOnly.MinValue), projectId: project.Id);
        tasks.Add(task);
        shell.Calendar.NotifyTasksMutated();
    }

    [Fact]
    public void RenamingAProject_RelabelsEverySurfaceWithoutAManualReload()
    {
        var (shell, blocks, tasks) = CreateShell();
        CreateProjectWithScheduledTask(shell, blocks, tasks);
        AddUnscheduledProjectTask(shell, tasks, "Read chapter 4");

        // Seed the Daily list — and every other surface — with the OLD label before
        // the rename: the Daily list defaults to today, but the session is tomorrow.
        // Setting this now (not after the mutation) means Rebuild only ever sees
        // "Schoolwork" here; the assertions below can only pass if the rename chain
        // itself refreshes these rows.
        shell.Calendar.VisibleDate = Tomorrow;

        shell.NavigateCommand.Execute(AppSection.Projects);
        var detail = shell.Projects.Detail!;
        detail.BeginRename();
        detail.RenameName = "Coursework";
        Assert.True(detail.TryCommitRename());

        // No ReloadList(), no re-navigation: the chain must have done it.
        Assert.Equal("Coursework", detail.Name);
        Assert.Equal("Coursework", shell.Projects.Projects.Single().Name);

        shell.NavigateCommand.Execute(AppSection.Calendar);

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

        // Seed every surface with the project still attached, before the delete.
        shell.Calendar.VisibleDate = Tomorrow;

        shell.NavigateCommand.Execute(AppSection.Projects);
        var detail = shell.Projects.Detail!;
        detail.RequestDeleteCommand.Execute(null);
        detail.ConfirmPromptCommand.Execute(null);

        Assert.Empty(shell.Projects.Projects);

        shell.NavigateCommand.Execute(AppSection.Calendar);

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
        var updated = Assert.Single(shell.Projects.Detail!.Tasks);
        Assert.Equal(new TimeOnly(18, 0), updated.SessionStart);
    }

    [Fact]
    public void ResizingALinkedTask_RefreshesTheDetail_ButFailuresStayQuiet()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks);
        var detail = shell.Projects.Detail!;

        var (changes, detailRefreshes) = (0, 0);
        shell.Calendar.DataChanged += () => changes++;
        detail.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectDetailViewModel.HasTasks))
            {
                detailRefreshes++;
            }
        };

        shell.Calendar.ResizeBlockTo(blockId, new TimeOnly(19, 0));
        Assert.Equal(1, changes);
        Assert.Equal(1, detailRefreshes);

        shell.NavigateCommand.Execute(AppSection.Projects);
        // A resize does not move the session, so the row's affix reads the same; what
        // the refresh has to reach is the resized block itself, still the one the
        // project's single row names.
        var updated = Assert.Single(shell.Projects.Detail!.Tasks);
        Assert.Equal(blockId, updated.SessionBlockId);
        Assert.Equal(TimeSpan.FromHours(3), blocks.GetById(blockId)!.Duration);

        // A rejected resize (end before start) must not announce a successful change.
        shell.Calendar.ResizeBlockTo(blockId, new TimeOnly(15, 0));
        Assert.Equal(1, changes);
        Assert.Equal(1, detailRefreshes);
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
        // A done occurrence is no longer a row of its own: the task's one row follows
        // the completion by naming the next occurrence instead.
        Assert.Equal(
            Tomorrow.AddDays(7),
            Assert.Single(shell.Projects.Detail!.Tasks).SessionDate);

        // Reopening from the calendar updates both surfaces again.
        CalendarBlockFor(shell, blockId, Tomorrow).ToggleOccurrenceDoneCommand.Execute(null);
        Assert.Equal(2, changes);
        Assert.False(CalendarBlockFor(shell, blockId, Tomorrow).IsDone);
        Assert.Equal(Tomorrow, Assert.Single(shell.Projects.Detail.Tasks).SessionDate);
    }

    /// <summary>
    /// Occurrence completion through the project detail announces through the same
    /// central chain as every other mutation: the calendar reloads, the Inbox and
    /// card counts refresh, and the open detail refreshes exactly once — never
    /// eagerly plus again through the shared event. The detail no longer renders a
    /// row per occurrence, so the path is driven directly rather than from a row.
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
            if (e.PropertyName == nameof(ProjectDetailViewModel.HasTasks))
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

        Assert.Equal(Tomorrow, Assert.Single(detail.Tasks).SessionDate);
        detail.SetOccurrenceCompletion(blockId, Tomorrow, completed: true);

        Assert.Equal(1, changes);
        Assert.Equal(1, detailRefreshes);
        Assert.Equal(1, inboxResets);
        Assert.True(CalendarBlockFor(shell, blockId, Tomorrow).IsDone);
        Assert.Equal(Tomorrow.AddDays(7), Assert.Single(detail.Tasks).SessionDate);

        // Reopening through the project detail flows through the same chain again.
        detail.SetOccurrenceCompletion(blockId, Tomorrow, completed: false);
        Assert.Equal(2, changes);
        Assert.Equal(2, detailRefreshes);
        Assert.False(CalendarBlockFor(shell, blockId, Tomorrow).IsDone);
        Assert.Equal(Tomorrow, Assert.Single(detail.Tasks).SessionDate);

        // A no-op request emits no success notification anywhere.
        detail.SetOccurrenceCompletion(blockId, Tomorrow, completed: false);
        Assert.Equal(2, changes);
        Assert.Equal(2, detailRefreshes);
    }

    /// <summary>
    /// The project detail completes a one-off session against its block. Routing it
    /// through the occurrence path would throw — a one-off has no occurrences. Two
    /// sessions of one task are one row now, so the proof that only the named session
    /// resolved is that the row moves on to the sibling and the sibling is untouched.
    /// </summary>
    [Fact]
    public void CompletingAOneOffSessionFromTheProjectDetail_ResolvesThatSessionOnly()
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
        var row = Assert.Single(shell.Projects.Detail!.Tasks);
        Assert.Equal(blockId, row.SessionBlockId); // the earlier of the two sessions

        shell.Projects.Detail.SetSessionCompletion(blockId, completed: true);

        Assert.Equal(BlockOutcome.Done, blocks.GetById(blockId)!.Outcome);
        Assert.Equal(BlockOutcome.None, blocks.GetById(sibling.Id)!.Outcome);
        Assert.False(tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(
            sibling.Id, Assert.Single(shell.Projects.Detail!.Tasks).SessionBlockId);
    }

    /// <summary>
    /// A one-off session is resolved Done because its parent Task was completed as a
    /// whole. Undoing that session must reopen the TASK: clearing this session's
    /// outcome alone would leave the task done (a dead click) and strand an
    /// unresolved session on a completed task.
    /// </summary>
    [Fact]
    public void UndoingAOneOffSessionOfACompletedTask_ReopensTheTask()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks);
        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.Detail!.Tasks.Single(t => t.Title == "Stats HW")
            .CompleteCommand.Execute(null);
        Assert.Equal(BlockOutcome.Done, blocks.GetById(blockId)!.Outcome);
        Assert.Equal(
            ProjectTaskStatus.Done, Assert.Single(shell.Projects.Detail!.Tasks).Status);

        shell.Projects.Detail!.SetSessionCompletion(blockId, completed: false);

        Assert.False(tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, blocks.GetById(blockId)!.Outcome);
        var reopened = Assert.Single(shell.Projects.Detail!.Tasks);
        Assert.Equal(ProjectTaskStatus.Scheduled, reopened.Status);
        Assert.Equal(blockId, reopened.SessionBlockId);
    }

    /// <summary>
    /// A repeating task completes per occurrence and never as a whole: its row offers
    /// no whole-task control, and the occurrence path leaves both the Task and the
    /// block's own outcome alone while still moving the row to the next occurrence.
    /// </summary>
    [Fact]
    public void CompletingARepeatingSessionFromTheProjectDetail_StillCompletesPerOccurrence()
    {
        var (shell, blocks, tasks) = CreateShell();
        var blockId = CreateProjectWithScheduledTask(shell, blocks, tasks, repeating: true);
        shell.NavigateCommand.Execute(AppSection.Projects);
        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");

        var row = Assert.Single(shell.Projects.Detail!.Tasks);
        Assert.False(row.CanComplete);
        Assert.Equal(Tomorrow, row.SessionDate);

        shell.Projects.Detail!.SetOccurrenceCompletion(blockId, Tomorrow, completed: true);

        Assert.False(tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, blocks.GetById(blockId)!.Outcome);
        Assert.Equal(
            Tomorrow.AddDays(7), Assert.Single(shell.Projects.Detail!.Tasks).SessionDate);

        shell.Projects.Detail!.SetOccurrenceCompletion(blockId, Tomorrow, completed: false);
        Assert.Equal(Tomorrow, Assert.Single(shell.Projects.Detail!.Tasks).SessionDate);
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

        var row = shell.Projects.Detail!.Tasks.Single(t => t.Title == "Stats HW");
        row.CompleteCommand.Execute(null);

        var task = tasks.GetAll().Single(t => t.Title == "Stats HW");
        Assert.True(task.IsCompleted);
        Assert.Equal(BlockOutcome.Done, blocks.GetById(blockId)!.Outcome);
        Assert.Equal(1, changes);
    }

    /// <summary>
    /// The sibling of the completion case, for deletion. Closing the detail rebuilds the
    /// card list, and the announcement that has to follow it rebuilds the list again
    /// through RefreshActive's unconditional tail — two rebuilds for one user action.
    /// Exactly one, and the close still has to happen before the announcement so nothing
    /// refreshes the project that was just deleted.
    /// </summary>
    [Fact]
    public void DeletingAProject_RebuildsTheCardListExactlyOnce()
    {
        var (shell, _, _) = CreateShell();
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.NewProjectName = "Schoolwork";
        Assert.True(shell.Projects.TryCreateProject());
        var detail = shell.Projects.Detail!;
        detail.RequestDeleteCommand.Execute(null);

        var cardResets = 0;
        shell.Projects.Projects.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                cardResets++;
            }
        };

        detail.ConfirmPromptCommand.Execute(null);

        Assert.Equal(1, cardResets);
        Assert.Empty(shell.Projects.Projects);
        Assert.Null(shell.Projects.Detail);
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
            if (e.PropertyName == nameof(ProjectDetailViewModel.HasTasks))
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

        detail.Tasks.Single(t => t.Title == "Essay plan").CompleteCommand.Execute(null);

        Assert.Equal(1, detailRefreshes);
        Assert.Equal(1, changes);
        Assert.Equal(1, inboxResets);
        Assert.Equal(1, cardResets);
        Assert.Equal(
            ProjectTaskStatus.Done, detail.Tasks.Single(t => t.Title == "Essay plan").Status);
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
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(shell.Projects.Detail!.Tasks).SessionStart);

        var blockId = blocks.GetAll().Single(b => b.TaskId == task.Id).Id;
        shell.Calendar.MoveBlock(blockId, Tomorrow, new TimeOnly(11, 0));

        shell.NavigateCommand.Execute(AppSection.Projects);
        var updated = Assert.Single(shell.Projects.Detail!.Tasks);
        Assert.Equal(new TimeOnly(11, 0), updated.SessionStart);
    }

    /// <summary>
    /// TDD phase 14, the reported failure: edit a task (PIQ2), assign it to a project
    /// (CAPPs), save — the already-open project must list it immediately, its scheduled
    /// session must arrive with it as the row's affix, and card counts must update.
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
        var row = Assert.Single(shell.Projects.Detail.Tasks);
        Assert.Equal("PIQ2", row.Title);
        Assert.Equal(ProjectTaskStatus.Scheduled, row.Status);
        Assert.Equal(Tomorrow, row.SessionDate);

        // "No project" removes it again — still through the same chain.
        shell.Calendar.OpenTaskEditorForTask(piq2.Id);
        var reopened = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        reopened.SelectedProject = reopened.ProjectOptions.Single(o => o.Name == "No project");
        reopened.SaveCommand.Execute(null);
        Assert.DoesNotContain(shell.Projects.Detail.Tasks, t => t.Title == "PIQ2");

        // The project index card counts refresh through the same notification chain.
        shell.Calendar.OpenTaskEditorForTask(piq2.Id);
        var again = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        again.SelectedProject = again.ProjectOptions.Single(o => o.Name == "CAPPs");
        again.SaveCommand.Execute(null);
        shell.Projects.CloseDetailCommand.Execute(null);
        Assert.Contains("1 open task", shell.Projects.Projects.Single().MetaText);
    }
}
