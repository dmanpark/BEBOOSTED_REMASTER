using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// Block capabilities by kind and provider: local task sessions are fully editable,
/// external synced events stay locked, and proposals keep their behavior. Repeating
/// sessions always mutate as a whole series and complete per occurrence.
/// </summary>
public sealed class CalendarBlockCapabilityTests
{
    private sealed record Context(
        CalendarViewModel Calendar,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        FakeClock Clock,
        CalendarService Service);

    private static Context Create()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var service = TestShell.CreateCalendarService(blocks, tasks, clock);
        var proposals = new InMemoryPlanningProposalRepository();
        var planning = new PlanningService(
            proposals,
            new InboxQueryService(tasks, blocks), new InMemoryPrioritizationRepository(),
            service,
            new InMemoryCalendarMutations(
                blocks, new InMemoryOccurrenceCompletionRepository(), tasks, proposals),
            clock);
        var calendar = TestShell.CreateCalendarViewModel(
            new InMemorySettingsStore(), clock, tasks, blocks,
            new InMemoryProjectRepository(), service, planning);
        return new Context(calendar, tasks, blocks, clock, service);
    }

    private static CalendarBlockViewModel FindBlock(Context context, CalendarBlockId id)
        => context.Calendar.Days.SelectMany(d => d.Blocks).First(b => !b.IsProposal && b.Id == id);

    private static CalendarBlock AddScheduledTask(
        Context context, string title, DateOnly date, TimeOnly start, TimeOnly end,
        RecurrenceRule? recurrence = null)
    {
        var task = context.Service.CreateTask(
            new TaskDetailsRequest(title, null, null, null),
            new TaskScheduleRequest(date, start, end, recurrence));
        var session = context.Blocks.GetForTask(task.Id).Single();
        context.Calendar.Reload();
        return session;
    }

    private static CalendarBlock AddExternal(Context context)
    {
        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "Imported standup", TestShell.DesignDate,
            new TimeOnly(9, 0), new TimeOnly(9, 30), BlockKind.ExternalEvent, null,
            "google", "evt-1", 0, BlockOutcome.None, null, context.Clock.Now, context.Clock.Now);
        context.Blocks.Add(external);
        context.Calendar.Reload();
        return external;
    }

    /// <summary>A one-off session plus a repeating sibling on the same Task.</summary>
    private static (CalendarBlock OneOff, TaskId TaskId) AddMixedScheduleTask(Context context)
    {
        var oneOff = AddScheduledTask(
            context, "Mixed work", TestShell.DesignDate, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var taskId = oneOff.TaskId!.Value;
        var repeating = CalendarBlock.CreateTaskSession(
            taskId, TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0),
            context.Clock.Now, RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        context.Blocks.Add(repeating);
        context.Calendar.Reload();
        return (oneOff, taskId);
    }

    /// <summary>
    /// Done is decided from the whole Task aggregate: with a repeating sibling the
    /// one-off keeps its outcome flyout, but the global Done choice disappears in
    /// favor of an explanatory note. A plain one-off keeps Done.
    /// </summary>
    [Fact]
    public void MixedScheduleOneOff_OffersOutcomes_ButNeverGlobalDone()
    {
        var context = Create();
        var (oneOff, _) = AddMixedScheduleTask(context);

        var vm = FindBlock(context, oneOff.Id);
        Assert.True(vm.ShowCompletionControl); // the flyout itself stays available
        Assert.False(vm.CanRecordDone);
        Assert.False(vm.RecordDoneCommand.CanExecute(null));
        Assert.True(vm.RecordNeedsMoreTimeCommand.CanExecute(null));
        Assert.True(vm.RecordDidntHappenCommand.CanExecute(null));
        Assert.True(vm.ShowRepeatingCompletionNote);
        Assert.Equal(
            "This task repeats — complete each repeating occurrence separately.",
            vm.RepeatingCompletionNote);

        var solo = AddScheduledTask(
            context, "Solo work", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(13, 0));
        var soloVm = FindBlock(context, solo.Id);
        Assert.True(soloVm.CanRecordDone);
        Assert.True(soloVm.RecordDoneCommand.CanExecute(null));
        Assert.False(soloVm.ShowRepeatingCompletionNote);
    }

    /// <summary>
    /// A session's Done is local to that session, so a repeating sibling no longer
    /// makes it a rejected transition: the view-model boundary records it, announces
    /// the change exactly once, leaves the Task open, and raises no notice.
    /// </summary>
    [Fact]
    public void RecordingDone_OnAMixedSchedule_ResolvesOnlyThatSession_AtTheViewModelBoundary()
    {
        var context = Create();
        var (oneOff, taskId) = AddMixedScheduleTask(context);
        var changes = 0;
        context.Calendar.DataChanged += () => changes++;

        var exception = Record.Exception(
            () => context.Calendar.RecordOutcome(oneOff.Id, BlockOutcome.Done, null));

        Assert.Null(exception);
        Assert.Equal(1, changes);
        Assert.Equal(BlockOutcome.Done, context.Blocks.GetById(oneOff.Id)!.Outcome);
        Assert.False(context.Tasks.GetById(taskId)!.IsCompleted);

        // Nothing was rejected, so nothing is announced as a notice.
        Assert.False(context.Calendar.IsUndoToastVisible);
    }

    [Fact]
    public void LocalOneOffSession_HasEveryCapability_AndNoLock()
    {
        var context = Create();
        var session = AddScheduledTask(
            context, "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));

        var vm = FindBlock(context, session.Id);
        Assert.True(vm.CanEdit);
        Assert.True(vm.CanMove);
        Assert.True(vm.CanResize);
        Assert.True(vm.CanDelete);
        Assert.False(vm.IsLocked);
    }

    [Fact]
    public void ExternalEvent_IsLockedWithNoCapabilities()
    {
        var context = Create();
        var external = AddExternal(context);

        var vm = FindBlock(context, external.Id);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanMove);
        Assert.False(vm.CanResize);
        Assert.False(vm.CanDelete);
        Assert.True(vm.IsLocked);
        Assert.Contains("locked", vm.AccessibleName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>View-model layer: editing a block opens the session editor (F-03).</summary>
    [Fact]
    public void EditingASession_OpensTheSessionEditor()
    {
        var context = Create();
        var session = AddScheduledTask(
            context, "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));

        FindBlock(context, session.Id).Edit();

        var editor = Assert.IsType<SessionEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.Equal("Lunch", editor.TaskTitle);
        Assert.Equal(session.Id, editor.SessionId);
    }

    [Fact]
    public void EditingAnExternalEvent_DoesNothing()
    {
        var context = Create();
        var external = AddExternal(context);

        FindBlock(context, external.Id).Edit();

        Assert.Null(context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void MovingARepeatingSession_ShiftsTheSeriesTime_KeepingItsAnchorDate()
    {
        var context = Create();
        var anchor = TestShell.DesignDate.AddDays(-7);
        var session = AddScheduledTask(
            context, "AP Economics", anchor, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));

        // Today's occurrence (Tue) is dragged; the whole series shifts, the anchor stays.
        var vm = FindBlock(context, session.Id);
        vm.MoveTo(TestShell.DesignDate, new TimeOnly(10, 0));

        var saved = context.Blocks.GetById(session.Id)!;
        Assert.Equal(anchor, saved.Date);
        Assert.Equal(new TimeOnly(10, 0), saved.StartTime);
        Assert.Equal(new TimeOnly(11, 15), saved.EndTime);
    }

    [Fact]
    public void DayNudgingARepeatingSession_NeverRebasesTheSeries()
    {
        var context = Create();
        var anchor = TestShell.DesignDate.AddDays(-7);
        var session = AddScheduledTask(
            context, "AP Economics", anchor, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        var vm = FindBlock(context, session.Id);
        vm.NudgeDays(1);

        var saved = context.Blocks.GetById(session.Id)!;
        Assert.Equal(anchor, saved.Date);
        Assert.Equal(new TimeOnly(8, 30), saved.StartTime);
    }

    [Fact]
    public void OneOffSession_MovesToAnotherDay()
    {
        var context = Create();
        var session = AddScheduledTask(
            context, "Dentist", TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0));

        FindBlock(context, session.Id).MoveTo(TestShell.DesignDate.AddDays(2), new TimeOnly(16, 0));

        var saved = context.Blocks.GetById(session.Id)!;
        Assert.Equal(TestShell.DesignDate.AddDays(2), saved.Date);
    }

    [Fact]
    public void DeleteDispatch_UnschedulesOneOffSessions_KeepingTheTask()
    {
        var context = Create();
        var session = AddScheduledTask(
            context, "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));
        var taskId = session.TaskId!.Value;

        FindBlock(context, session.Id).UnscheduleCommand.Execute(null);

        Assert.Null(context.Blocks.GetById(session.Id));
        Assert.NotNull(context.Tasks.GetById(taskId));
        Assert.Null(context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void DeleteDispatch_RoutesRepeatingTasksThroughConfirmation()
    {
        var context = Create();
        var session = AddScheduledTask(
            context, "AP Economics", TestShell.DesignDate.AddDays(-7),
            new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var taskId = session.TaskId!.Value;

        FindBlock(context, session.Id).UnscheduleCommand.Execute(null);

        // Never a silent series delete: the session editor opens pre-armed on the
        // Remove-schedule confirmation for the rendered occurrence.
        Assert.NotNull(context.Blocks.GetById(session.Id));
        var editor = Assert.IsType<SessionEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.Equal(SessionEditorMode.Repeating, editor.Mode);
        Assert.Equal(TestShell.DesignDate, editor.OccurrenceDate);
        Assert.NotNull(editor.Confirmation);
        Assert.Equal("Remove schedule", editor.Confirmation.ConfirmLabel);

        editor.ConfirmPromptCommand.Execute(null);
        Assert.Null(context.Blocks.GetById(session.Id));
        Assert.NotNull(context.Tasks.GetById(taskId)); // the task survives (F-03 scope)
    }

    [Fact]
    public void DeleteDispatch_LeavesExternalEventsUntouched()
    {
        var context = Create();
        var external = AddExternal(context);

        FindBlock(context, external.Id).UnscheduleCommand.Execute(null);

        Assert.NotNull(context.Blocks.GetById(external.Id));
        Assert.Null(context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void OccurrenceCompletionControl_AppearsOnlyOnRepeatingSessions()
    {
        var context = Create();
        var repeating = AddScheduledTask(
            context, "AP Economics", TestShell.DesignDate.AddDays(-7),
            new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var oneOff = AddScheduledTask(
            context, "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));
        var external = AddExternal(context);

        var repeatingVm = FindBlock(context, repeating.Id);
        Assert.True(repeatingVm.ShowOccurrenceCompletionControl);
        Assert.Equal("Mark AP Economics done", repeatingVm.CompletionControlName);
        Assert.False(repeatingVm.ShowCompletionControl);

        var oneOffVm = FindBlock(context, oneOff.Id);
        Assert.False(oneOffVm.ShowOccurrenceCompletionControl);
        Assert.True(oneOffVm.ShowCompletionControl); // the multi-outcome flyout

        Assert.False(FindBlock(context, external.Id).ShowOccurrenceCompletionControl);
        Assert.False(FindBlock(context, external.Id).ShowCompletionControl);
    }

    [Fact]
    public void ToggleOccurrenceDone_MarksAndReopens_EmittingOneChangeEach()
    {
        var context = Create();
        var session = AddScheduledTask(
            context, "AP Economics", TestShell.DesignDate.AddDays(-7),
            new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var changes = 0;
        context.Calendar.DataChanged += () => changes++;

        FindBlock(context, session.Id).ToggleOccurrenceDoneCommand.Execute(null);
        Assert.Equal(1, changes);
        var done = FindBlock(context, session.Id);
        Assert.True(done.IsDone);
        Assert.Equal("Reopen AP Economics", done.CompletionControlName);
        Assert.True(context.Service.IsOccurrenceCompleted(session.Id, TestShell.DesignDate));

        done.ToggleOccurrenceDoneCommand.Execute(null);
        Assert.Equal(2, changes);
        Assert.False(FindBlock(context, session.Id).IsDone);
        Assert.False(context.Service.IsOccurrenceCompleted(session.Id, TestShell.DesignDate));
    }

    /// <summary>TDD phase 13 (view-model layer): only the clicked day's occurrence completes.</summary>
    [Fact]
    public void ToggleOccurrenceDone_OnARepeatingSeries_TargetsTheOccurrence()
    {
        var context = Create();
        var session = AddScheduledTask(
            context, "AP Economics", TestShell.DesignDate.AddDays(-7),
            new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        context.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;

        // Toggle Wednesday's occurrence; Tuesday's stays open.
        var wednesday = context.Calendar.Days
            .Single(d => d.Date == TestShell.DesignDate.AddDays(1)).Blocks
            .Single(b => b.Id == session.Id);
        wednesday.ToggleOccurrenceDoneCommand.Execute(null);

        Assert.True(context.Service.IsOccurrenceCompleted(
            session.Id, TestShell.DesignDate.AddDays(1)));
        Assert.False(context.Service.IsOccurrenceCompleted(session.Id, TestShell.DesignDate));
        var tuesday = context.Calendar.Days
            .Single(d => d.Date == TestShell.DesignDate).Blocks
            .Single(b => b.Id == session.Id);
        Assert.False(tuesday.IsDone);
    }

    [Fact]
    public void SavingARepeatingEdit_UpdatesTheWholeSeries()
    {
        var context = Create();
        var anchor = TestShell.DesignDate.AddDays(-7);
        var session = AddScheduledTask(
            context, "AP Economics", anchor, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));

        FindBlock(context, session.Id).Edit();
        var editor = (SessionEditorViewModel)context.Calendar.ActiveTaskEditor!;
        Assert.Equal(SessionEditorMode.Repeating, editor.Mode);
        editor.Schedule.Start = new TimeSpan(9, 0, 0);
        editor.Schedule.End = new TimeSpan(10, 0, 0);
        editor.SaveCommand.Execute(null);

        var saved = context.Blocks.GetById(session.Id)!;
        Assert.Equal(new TimeOnly(9, 0), saved.StartTime);
        Assert.Equal(
            [DayOfWeek.Tuesday, DayOfWeek.Wednesday],
            saved.Recurrence!.DaysOfWeek.OrderBy(d => d));
    }
}
