using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// The F-03 entry-point rule: every list row (Inbox, Daily, Projects) is
/// task-scoped and opens the whole-task editor; only calendar blocks and the
/// whole-task editor's own schedule rows open the session editor. No entry
/// point silently picks a session on the caller's behalf.
/// </summary>
public sealed class EditorScopeSelectionTests
{
    private static readonly DateOnly Today = TestShell.DesignDate; // Tue, Aug 11 2026

    private sealed record Fixture(
        ShellViewModel Shell,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        TaskItem Essay,
        CalendarBlock EssaySession,
        TaskItem Read,
        TaskItem Piq2,
        TaskItem StatsHw,
        CalendarBlock StatsSession,
        ProjectId ProjectId);

    /// <summary>
    /// One shell covering every list surface: "Essay" (one-off session today),
    /// "Read" (bare unscheduled task), and a "Schoolwork" project holding "PIQ2"
    /// (unscheduled) plus "Stats HW" (repeating Tuesday session).
    /// </summary>
    private static Fixture CreateFixture()
    {
        var clock = new FakeClock(Today);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var projects = new InMemoryProjectRepository();
        var schoolwork = Project.Create("Schoolwork", "#5B8DEF", clock.Now);
        projects.Add(schoolwork);

        var essay = TaskItem.Create("Essay", clock.Now);
        tasks.Add(essay);
        var essaySession = CalendarBlock.CreateTaskSession(
            essay.Id, Today, new TimeOnly(9, 0), new TimeOnly(10, 0), clock.Now);
        blocks.Add(essaySession);

        var read = TaskItem.Create("Read", clock.Now);
        tasks.Add(read);

        var piq2 = TaskItem.Create("PIQ2", clock.Now, projectId: schoolwork.Id);
        tasks.Add(piq2);
        var statsHw = TaskItem.Create("Stats HW", clock.Now, projectId: schoolwork.Id);
        tasks.Add(statsHw);
        var statsSession = CalendarBlock.CreateTaskSession(
            statsHw.Id, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            clock.Now, RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        blocks.Add(statsSession);

        var shell = TestShell.Create(tasks: tasks, blocks: blocks, projects: projects);
        return new Fixture(
            shell, tasks, blocks, essay, essaySession, read, piq2, statsHw, statsSession,
            schoolwork.Id);
    }

    private static WholeTaskEditorViewModel AssertWholeTaskEditor(
        ShellViewModel shell, TaskId expectedTaskId)
    {
        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(expectedTaskId, editor.TaskId);
        return editor;
    }

    [Fact]
    public void EveryListRow_OpensTheWholeTaskEditor()
    {
        var fixture = CreateFixture();
        var shell = fixture.Shell;

        // Inbox row (bare task).
        shell.Inbox.Tasks.First(r => r.Task.Id == fixture.Read.Id).EditCommand.Execute(null);
        AssertWholeTaskEditor(shell, fixture.Read.Id);
        shell.Calendar.EscapeTaskEditor();

        // Daily list: a session-backed row is still task-scoped (spec, Approach).
        shell.Calendar.Daily.ScheduledRows.First(r => r.Title == "Essay")
            .EditRowCommand.Execute(null);
        AssertWholeTaskEditor(shell, fixture.Essay.Id);
        shell.Calendar.EscapeTaskEditor();

        // Daily list: a bare unscheduled task row.
        shell.Calendar.Daily.UnscheduledRows.First(r => r.Title == "Read")
            .EditRowCommand.Execute(null);
        AssertWholeTaskEditor(shell, fixture.Read.Id);
        shell.Calendar.EscapeTaskEditor();

        // Projects: a task row.
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.OpenProject(fixture.ProjectId);
        shell.Projects.Detail!.OpenTasks.First(t => t.Title == "PIQ2")
            .EditCommand.Execute(null);
        AssertWholeTaskEditor(shell, fixture.Piq2.Id);
        shell.Calendar.EscapeTaskEditor();

        // Projects: a scheduled-session row resolves to its owning task.
        shell.Projects.Detail!.ScheduledBlocks.First(r => r.Title == "Stats HW")
            .EditCommand.Execute(null);
        AssertWholeTaskEditor(shell, fixture.StatsHw.Id);
    }

    [Fact]
    public void CalendarBlocks_AndScheduleRows_OpenTheSessionEditor()
    {
        var fixture = CreateFixture();
        var shell = fixture.Shell;

        // A calendar block opens the session editor scoped to the clicked occurrence.
        var block = shell.Calendar.Days.SelectMany(d => d.Blocks)
            .First(b => b.Id == fixture.EssaySession.Id);
        block.Edit();
        var sessionEditor = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(fixture.EssaySession.Id, sessionEditor.SessionId);
        Assert.Equal(block.Date, sessionEditor.OccurrenceDate);
        shell.Calendar.EscapeTaskEditor();

        // A whole-task Schedule row pushes the session editor for that row.
        var wholeTask = shell.Calendar.OpenWholeTaskEditor(fixture.Essay.Id)!;
        wholeTask.Sessions.First().EditCommand.Execute(null);
        var pushed = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(fixture.EssaySession.Id, pushed.SessionId);
    }

    /// <summary>
    /// Deliberate behavior change (spec): the Delete key on a repeating block now
    /// removes the schedule via a pre-armed confirmation, never the whole task.
    /// </summary>
    [Fact]
    public void RepeatingDeleteKey_OpensRemoveScheduleConfirmation_WithTheRenderedOccurrence()
    {
        var fixture = CreateFixture();
        var shell = fixture.Shell;

        var block = shell.Calendar.Days.SelectMany(d => d.Blocks)
            .First(b => b.Id == fixture.StatsSession.Id);
        block.UnscheduleCommand.Execute(null);

        var editor = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(SessionEditorMode.Repeating, editor.Mode);
        Assert.Equal(block.Date, editor.OccurrenceDate);
        Assert.NotNull(editor.Confirmation);
        Assert.Equal("Remove schedule", editor.Confirmation.ConfirmLabel);
    }

    /// <summary>F-03's root cause is gone: task entry lists every session, picks none.</summary>
    [Fact]
    public void TaskRowEntry_NeedsNoSessionPick_EvenWithResolvedHistory()
    {
        var fixture = CreateFixture();
        var shell = fixture.Shell;
        var elapsed = CalendarBlock.CreateTaskSession(
            fixture.Essay.Id, Today.AddDays(-3), new TimeOnly(9, 0), new TimeOnly(10, 0),
            new FakeClock(Today).Now);
        fixture.Blocks.Add(elapsed);
        shell.Calendar.RecordOutcome(elapsed.Id, BlockOutcome.Done, null);

        shell.Calendar.OpenTaskEditorForTask(fixture.Essay.Id);

        var editor = AssertWholeTaskEditor(shell, fixture.Essay.Id);
        Assert.Equal(2, editor.Sessions.Count);
        Assert.Contains(editor.Sessions, r => r.Data.Id == elapsed.Id);
        Assert.Contains(editor.Sessions, r => r.Data.Id == fixture.EssaySession.Id);
    }

    /// <summary>Repeating Schedule-row entry keeps the F-15 occurrence rule.</summary>
    [Fact]
    public void F15_OccurrenceResolution_StillHoldsForScheduleRowEntry()
    {
        var fixture = CreateFixture();
        var shell = fixture.Shell;
        var clock = new FakeClock(Today);

        // Occurs today (Tuesday): the resolved occurrence is today.
        var wholeTask = shell.Calendar.OpenWholeTaskEditor(fixture.StatsHw.Id)!;
        wholeTask.Sessions.First().EditCommand.Execute(null);
        var editor = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(Today, editor.OccurrenceDate);

        // A Monday series: the most recent elapsed occurrence (yesterday, Aug 10).
        var mondayTask = TaskItem.Create("Monday reps", clock.Now);
        fixture.Tasks.Add(mondayTask);
        fixture.Blocks.Add(CalendarBlock.CreateTaskSession(
            mondayTask.Id, new DateOnly(2026, 8, 3), new TimeOnly(7, 0), new TimeOnly(8, 0),
            clock.Now, RecurrenceRule.Weekly(1, DayOfWeek.Monday)));
        wholeTask = shell.Calendar.OpenWholeTaskEditor(mondayTask.Id)!;
        wholeTask.Sessions.First().EditCommand.Execute(null);
        editor = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(new DateOnly(2026, 8, 10), editor.OccurrenceDate);

        // A series that only starts in the future: its anchor date.
        var futureTask = TaskItem.Create("Future prep", clock.Now);
        fixture.Tasks.Add(futureTask);
        fixture.Blocks.Add(CalendarBlock.CreateTaskSession(
            futureTask.Id, new DateOnly(2026, 8, 21), new TimeOnly(7, 0), new TimeOnly(8, 0),
            clock.Now, RecurrenceRule.Weekly(1, DayOfWeek.Friday)));
        wholeTask = shell.Calendar.OpenWholeTaskEditor(futureTask.Id)!;
        wholeTask.Sessions.First().EditCommand.Execute(null);
        editor = Assert.IsType<SessionEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Equal(new DateOnly(2026, 8, 21), editor.OccurrenceDate);
    }

    [Fact]
    public void NewTaskPaths_OpenCreateMode()
    {
        var fixture = CreateFixture();
        var shell = fixture.Shell;

        // Plain New task.
        shell.Calendar.OpenNewTaskEditorCommand.Execute(null);
        var editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Null(editor.TaskId);
        shell.Calendar.EscapeTaskEditor();

        // Slot-prefilled (empty Week-slot click): the inline schedule carries the slot.
        shell.Calendar.OpenNewTaskEditorAt(Today, new TimeOnly(9, 0), new TimeOnly(10, 30));
        editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Null(editor.TaskId);
        Assert.True(editor.ShowInlineSchedule);
        Assert.Equal(Today, DateOnly.FromDateTime(editor.InlineSchedule.Date!.Value.Date));
        Assert.Equal(new TimeSpan(9, 0, 0), editor.InlineSchedule.Start);
        Assert.Equal(new TimeSpan(10, 30, 0), editor.InlineSchedule.End);
        shell.Calendar.EscapeTaskEditor();

        // Unscheduled New task: no inline schedule until the user reveals one.
        shell.Calendar.OpenNewUnscheduledTaskEditor(Today);
        editor = Assert.IsType<WholeTaskEditorViewModel>(shell.Calendar.ActiveTaskEditor);
        Assert.Null(editor.TaskId);
        Assert.False(editor.ShowInlineSchedule);
    }
}
