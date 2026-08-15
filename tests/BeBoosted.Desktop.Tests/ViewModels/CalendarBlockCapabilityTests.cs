using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// Block capabilities by kind and provider: local fixed commitments are fully editable,
/// external commitments stay locked, and task blocks/proposals keep their behavior.
/// Recurring commitments always mutate as a whole series.
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
        var planning = new PlanningService(
            new InMemoryPlanningProposalRepository(), blocks,
            new InboxQueryService(tasks, blocks), new InMemoryPrioritizationRepository(),
            service, clock);
        var calendar = TestShell.CreateCalendarViewModel(
            new InMemorySettingsStore(), clock, tasks, blocks,
            new InMemoryProjectRepository(), service, planning);
        return new Context(calendar, tasks, blocks, clock, service);
    }

    private static CalendarBlockViewModel FindBlock(Context context, CalendarBlockId id)
        => context.Calendar.Days.SelectMany(d => d.Blocks).First(b => !b.IsProposal && b.Id == id);

    private static CalendarBlock AddExternal(Context context)
    {
        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, null, "Imported standup", TestShell.DesignDate,
            new TimeOnly(9, 0), new TimeOnly(9, 30), BlockKind.FixedCommitment, null,
            "google", "evt-1", 0, BlockOutcome.None, null, context.Clock.Now, context.Clock.Now);
        context.Blocks.Add(external);
        return external;
    }

    [Fact]
    public void LocalFixedCommitment_HasEveryCapability_AndNoLock()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));
        context.Calendar.Reload();

        var vm = FindBlock(context, block.Id);
        Assert.True(vm.CanEdit);
        Assert.True(vm.CanMove);
        Assert.True(vm.CanResize);
        Assert.True(vm.CanDelete);
        Assert.False(vm.IsLocked);
    }

    [Fact]
    public void ExternalCommitment_IsLockedWithNoCapabilities()
    {
        var context = Create();
        var external = AddExternal(context);
        context.Calendar.Reload();

        var vm = FindBlock(context, external.Id);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanMove);
        Assert.False(vm.CanResize);
        Assert.False(vm.CanDelete);
        Assert.True(vm.IsLocked);
        Assert.Contains("locked", vm.AccessibleName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaskBlocksAndProposals_KeepTheirCapabilities()
    {
        var context = Create();
        var task = TaskItem.Create("Work", context.Clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
        context.Tasks.Add(task);
        var block = context.Service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(15, 0));
        context.Calendar.Reload();

        var vm = FindBlock(context, block.Id);
        Assert.False(vm.CanEdit);
        Assert.True(vm.CanMove);
        Assert.True(vm.CanResize);
        Assert.True(vm.CanDelete);
        Assert.False(vm.IsLocked);
    }

    [Fact]
    public void EditingALocalCommitment_OpensTheEditorInEditMode()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));
        context.Calendar.Reload();

        FindBlock(context, block.Id).Edit();

        Assert.NotNull(context.Calendar.CommitmentEditor);
        Assert.True(context.Calendar.CommitmentEditor.IsEditMode);
        Assert.Equal("Lunch", context.Calendar.CommitmentEditor.Title);
    }

    [Fact]
    public void EditingAnExternalCommitment_DoesNothing()
    {
        var context = Create();
        var external = AddExternal(context);
        context.Calendar.Reload();

        FindBlock(context, external.Id).Edit();

        Assert.Null(context.Calendar.CommitmentEditor);
    }

    [Fact]
    public void MovingARecurringCommitment_ShiftsTheSeriesTime_KeepingItsAnchorDate()
    {
        var context = Create();
        var anchor = TestShell.DesignDate.AddDays(-7);
        var block = context.Service.CreateFixedCommitment(
            "AP Economics", anchor, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        context.Calendar.Reload();

        // Today's occurrence (Tue) is dragged; the whole series shifts, the anchor stays.
        var vm = FindBlock(context, block.Id);
        vm.MoveTo(TestShell.DesignDate, new TimeOnly(10, 0));

        var saved = context.Blocks.GetById(block.Id)!;
        Assert.Equal(anchor, saved.Date);
        Assert.Equal(new TimeOnly(10, 0), saved.StartTime);
        Assert.Equal(new TimeOnly(11, 15), saved.EndTime);
    }

    [Fact]
    public void DayNudgingARecurringCommitment_NeverRebasesTheSeries()
    {
        var context = Create();
        var anchor = TestShell.DesignDate.AddDays(-7);
        var block = context.Service.CreateFixedCommitment(
            "AP Economics", anchor, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        context.Calendar.Reload();

        var vm = FindBlock(context, block.Id);
        vm.NudgeDays(1);

        var saved = context.Blocks.GetById(block.Id)!;
        Assert.Equal(anchor, saved.Date);
        Assert.Equal(new TimeOnly(8, 30), saved.StartTime);
    }

    [Fact]
    public void OneOffLocalCommitment_MovesToAnotherDay()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Dentist", TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0));
        context.Calendar.Reload();

        FindBlock(context, block.Id).MoveTo(TestShell.DesignDate.AddDays(2), new TimeOnly(16, 0));

        var saved = context.Blocks.GetById(block.Id)!;
        Assert.Equal(TestShell.DesignDate.AddDays(2), saved.Date);
    }

    [Fact]
    public void DeleteDispatch_RoutesLocalCommitmentsThroughConfirmation()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));
        context.Calendar.Reload();

        FindBlock(context, block.Id).UnscheduleCommand.Execute(null);

        // Never a silent delete: the editor opens already asking for confirmation.
        Assert.NotNull(context.Blocks.GetById(block.Id));
        var editor = context.Calendar.CommitmentEditor;
        Assert.NotNull(editor);
        Assert.True(editor.IsConfirmingDelete);

        editor.ConfirmDeleteCommand.Execute(null);
        Assert.Null(context.Blocks.GetById(block.Id));
    }

    [Fact]
    public void DeleteDispatch_LeavesExternalCommitmentsUntouched()
    {
        var context = Create();
        var external = AddExternal(context);
        context.Calendar.Reload();

        FindBlock(context, external.Id).UnscheduleCommand.Execute(null);

        Assert.NotNull(context.Blocks.GetById(external.Id));
        Assert.Null(context.Calendar.CommitmentEditor);
    }

    [Fact]
    public void DeleteDispatch_StillUnschedulesTaskBlocks()
    {
        var context = Create();
        var task = TaskItem.Create("Work", context.Clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
        context.Tasks.Add(task);
        var block = context.Service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(15, 0));
        context.Calendar.Reload();

        FindBlock(context, block.Id).UnscheduleCommand.Execute(null);

        Assert.Null(context.Blocks.GetById(block.Id));
        Assert.Null(context.Calendar.CommitmentEditor);
    }

    [Fact]
    public void CompletionControl_AppearsOnlyOnLocalFixedCommitments()
    {
        var context = Create();
        var local = context.Service.CreateFixedCommitment(
            "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));
        var external = AddExternal(context);
        var task = TaskItem.Create("Work", context.Clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
        context.Tasks.Add(task);
        var taskBlock = context.Service.ScheduleTask(task.Id, TestShell.DesignDate, new TimeOnly(15, 0));
        context.Calendar.Reload();

        var localVm = FindBlock(context, local.Id);
        Assert.True(localVm.ShowCommitmentCompletionControl);
        Assert.Equal("Mark Lunch done", localVm.CompletionControlName);

        Assert.False(FindBlock(context, external.Id).ShowCommitmentCompletionControl);

        var taskVm = FindBlock(context, taskBlock.Id);
        Assert.False(taskVm.ShowCommitmentCompletionControl);
        Assert.True(taskVm.ShowCompletionControl); // the multi-outcome flyout stays task-only
    }

    [Fact]
    public void ToggleCommitmentDone_MarksAndReopens_EmittingOneChangeEach()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));
        context.Calendar.Reload();
        var changes = 0;
        context.Calendar.DataChanged += () => changes++;

        FindBlock(context, block.Id).ToggleCommitmentDoneCommand.Execute(null);
        Assert.Equal(1, changes);
        var done = FindBlock(context, block.Id);
        Assert.True(done.IsDone);
        Assert.Equal("Reopen Lunch", done.CompletionControlName);
        Assert.True(context.Service.IsCommitmentOccurrenceCompleted(block.Id, TestShell.DesignDate));

        done.ToggleCommitmentDoneCommand.Execute(null);
        Assert.Equal(2, changes);
        Assert.False(FindBlock(context, block.Id).IsDone);
        Assert.False(context.Service.IsCommitmentOccurrenceCompleted(block.Id, TestShell.DesignDate));
    }

    [Fact]
    public void ToggleCommitmentDone_OnARecurringSeries_TargetsTheOccurrence()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "AP Economics", TestShell.DesignDate.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        context.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;

        // Toggle Wednesday's occurrence; Tuesday's stays open.
        var wednesday = context.Calendar.Days
            .Single(d => d.Date == TestShell.DesignDate.AddDays(1)).Blocks
            .Single(b => b.Id == block.Id);
        wednesday.ToggleCommitmentDoneCommand.Execute(null);

        Assert.True(context.Service.IsCommitmentOccurrenceCompleted(
            block.Id, TestShell.DesignDate.AddDays(1)));
        Assert.False(context.Service.IsCommitmentOccurrenceCompleted(block.Id, TestShell.DesignDate));
        var tuesday = context.Calendar.Days
            .Single(d => d.Date == TestShell.DesignDate).Blocks
            .Single(b => b.Id == block.Id);
        Assert.False(tuesday.IsDone);
    }

    [Fact]
    public void SavingARecurringEdit_UpdatesTheWholeSeries()
    {
        var context = Create();
        var anchor = TestShell.DesignDate.AddDays(-7);
        var block = context.Service.CreateFixedCommitment(
            "AP Economics", anchor, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        context.Calendar.Reload();

        FindBlock(context, block.Id).Edit();
        var editor = context.Calendar.CommitmentEditor!;
        Assert.Equal("Edit repeating commitment", editor.Heading);
        editor.Start = new TimeSpan(9, 0, 0);
        editor.End = new TimeSpan(10, 0, 0);
        editor.SaveCommand.Execute(null);

        var saved = context.Blocks.GetById(block.Id)!;
        Assert.Equal(new TimeOnly(9, 0), saved.StartTime);
        Assert.Equal(
            [DayOfWeek.Tuesday, DayOfWeek.Wednesday],
            saved.Recurrence!.DaysOfWeek.OrderBy(d => d));
    }
}
