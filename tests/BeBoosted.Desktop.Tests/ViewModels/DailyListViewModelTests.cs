using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// The Daily priority-first list: classification, ordering, priority mapping, progress,
/// mutations with a single-refresh guarantee, manual scheduling, and add-task flows.
/// </summary>
public sealed class DailyListViewModelTests
{
    private static readonly DateOnly Date = TestShell.DesignDate; // Tuesday, clock at 14:10

    private sealed record Context(
        CalendarViewModel Calendar,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        InMemoryProjectRepository Projects,
        InMemoryPrioritizationRepository Ranks,
        InMemoryPlanningProposalRepository Proposals,
        FakeClock Clock,
        CalendarService Service,
        TaskService TaskService)
    {
        public DailyListViewModel Daily => Calendar.Daily;
    }

    /// <summary>Delegates to the shared in-memory store; Add throws while armed.</summary>
    private sealed class FailingBlockRepository(InMemoryCalendarBlockRepository inner) : ICalendarBlockRepository
    {
        public InMemoryCalendarBlockRepository Inner => inner;

        public bool FailNextAdd { get; set; }

        public void Add(CalendarBlock block)
        {
            if (FailNextAdd)
            {
                FailNextAdd = false;
                throw new DomainException("The calendar rejected that block.");
            }

            inner.Add(block);
        }

        public void Update(CalendarBlock block) => inner.Update(block);

        public void Delete(CalendarBlockId id) => inner.Delete(id);

        public CalendarBlock? GetById(CalendarBlockId id) => inner.GetById(id);

        public IReadOnlyList<CalendarBlock> GetAll() => inner.GetAll();

        public IReadOnlyList<CalendarBlock> GetCandidatesBetween(DateOnly from, DateOnly to)
            => inner.GetCandidatesBetween(from, to);

        public IReadOnlyList<CalendarBlock> GetForTask(TaskId taskId) => inner.GetForTask(taskId);

        public IReadOnlyList<CalendarBlock> GetForProject(ProjectId projectId) => inner.GetForProject(projectId);

        public IReadOnlyList<CalendarBlock> GetElapsedWithoutOutcome(DateOnly today, TimeOnly now)
            => inner.GetElapsedWithoutOutcome(today, now);

        public IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks() => inner.GetTaskIdsWithPendingBlocks();
    }

    private static Context Create(FailingBlockRepository? failingBlocks = null)
    {
        var clock = new FakeClock(Date);
        var tasks = new InMemoryTaskRepository();
        var blocks = failingBlocks?.Inner ?? new InMemoryCalendarBlockRepository();
        ICalendarBlockRepository blockPort = failingBlocks is null
            ? blocks
            : failingBlocks;
        var completions = new InMemoryCommitmentCompletionRepository();
        var service = new CalendarService(
            blockPort, completions, new InMemoryCalendarMutations(blockPort, completions), tasks, clock);
        var ranks = new InMemoryPrioritizationRepository();
        var proposals = new InMemoryPlanningProposalRepository();
        var planning = new PlanningService(
            proposals, blockPort, new InboxQueryService(tasks, blockPort), ranks, service, clock);
        var projects = new InMemoryProjectRepository();
        var calendar = TestShell.CreateCalendarViewModel(
            new InMemorySettingsStore(), clock, tasks, blocks, projects, service, planning, ranks);
        return new Context(
            calendar, tasks, blocks, projects, ranks, proposals, clock, service,
            new TaskService(tasks, clock));
    }

    private static TaskItem AddTask(
        Context context, string title, TimeSpan? duration = null, DateOnly? deadline = null,
        ProjectId? projectId = null)
    {
        var task = TaskItem.Create(
            title, context.Clock.Now,
            estimatedDuration: duration, deadline: deadline, projectId: projectId);
        context.Tasks.Add(task);
        return task;
    }

    private static void SetRank(Context context, DateOnly date, params PriorityRank[] ranks)
        => context.Ranks.ReplaceRanks(PlanningPeriod.ForToday(date).Key, ranks);

    private static PlanningProposal SaveDraft(Context context, params ProposedBlock[] blocks)
    {
        var proposal = PlanningProposal.CreateDraft(
            PlanningPeriod.ForToday(Date), blocks, context.Clock.Now);
        context.Proposals.Save(proposal);
        return proposal;
    }

    private static ProposedBlock Propose(TaskItem task, DateOnly date, TimeOnly start, TimeOnly end)
        => new(
            CalendarBlockId.New(), task.Id, date, start, end,
            new WhyEvidence(null, "45 min", null, "open 3–5 PM", null));

    // ---- Classification ----

    [Fact]
    public void Rebuild_ClassifiesRows()
    {
        var context = Create();
        var localFixed = context.Service.CreateFixedCommitment(
            "Stats homework", Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        context.Blocks.Add(CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, null, "Imported standup", Date,
            new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.FixedCommitment, null,
            "google", "evt-1", 0, BlockOutcome.None, null, context.Clock.Now, context.Clock.Now));
        var doneCommitment = context.Service.CreateFixedCommitment(
            "Morning run", Date, new TimeOnly(7, 0), new TimeOnly(7, 30));
        context.Service.CompleteCommitmentOccurrence(doneCommitment.Id, Date);

        var openBlockTask = AddTask(context, "Scholarship review", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(openBlockTask.Id, Date, new TimeOnly(16, 0));
        var doneBlockTask = AddTask(context, "Morning reading", TimeSpan.FromMinutes(40));
        var doneBlock = context.Service.ScheduleTask(doneBlockTask.Id, Date, new TimeOnly(7, 10));
        context.Service.RecordOutcome(doneBlock.Id, BlockOutcome.Done);

        var proposalTask = AddTask(context, "Econ chapter 6", TimeSpan.FromMinutes(45));
        SaveDraft(context, Propose(proposalTask, Date, new TimeOnly(15, 0), new TimeOnly(15, 45)));

        var inboxTask = AddTask(context, "Email advisor");
        var directDone = AddTask(context, "Order textbooks");
        context.TaskService.Complete(directDone.Id);

        context.Calendar.Reload();
        var daily = context.Daily;

        // Fixed obligations first (by start), then unranked flex by start.
        Assert.Equal(
            ["Stats homework", "Imported standup", "Econ chapter 6", "Scholarship review"],
            daily.ScheduledRows.Select(r => r.Title).ToArray());
        Assert.Equal(["Email advisor"], daily.UnscheduledRows.Select(r => r.Title).ToArray());
        Assert.Equal(
            ["Morning reading", "Morning run", "Order textbooks"],
            daily.CompletedRows.Select(r => r.Title).OrderBy(t => t).ToArray());

        var external = daily.ScheduledRows.Single(r => r.Title == "Imported standup");
        Assert.True(external.IsLocked);
        Assert.False(external.ShowCommitmentCheck);
        Assert.Equal("FIXED", external.StatusText);
        Assert.Equal("FLEX", daily.ScheduledRows.Single(r => r.Title == "Scholarship review").StatusText);
        Assert.Equal("PROPOSED", daily.ScheduledRows.Single(r => r.Title == "Econ chapter 6").StatusText);
        Assert.True(daily.ScheduledRows.Single(r => r.Title == "Stats homework").ShowCommitmentCheck);
    }

    [Fact]
    public void ElapsedBlockWithoutOutcome_ShowsNeedsOutcome()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0)); // ended 10:00 < now 14:10
        context.Calendar.Reload();

        var row = context.Daily.ScheduledRows.Single(r => r.Title == "Elapsed work");
        Assert.True(row.NeedsOutcome);
        Assert.True(row.ShowOutcomeControl);
    }

    // ---- Ordering ----

    [Fact]
    public void ScheduledOrdering_FixedFirst_ThenRankedByTier_ThenUnrankedByStart()
    {
        var context = Create();
        context.Service.CreateFixedCommitment("Afternoon class", Date, new TimeOnly(14, 0), new TimeOnly(15, 0));
        context.Service.CreateFixedCommitment("Morning class", Date, new TimeOnly(9, 0), new TimeOnly(10, 0));

        var p2Task = AddTask(context, "Advance work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(p2Task.Id, Date, new TimeOnly(8, 0)); // earliest start, lower tier
        var p1Task = AddTask(context, "Protect work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(p1Task.Id, Date, new TimeOnly(18, 0)); // latest start, top tier
        var unrankedTask = AddTask(context, "Unranked work", TimeSpan.FromMinutes(30));
        context.Service.ScheduleTask(unrankedTask.Id, Date, new TimeOnly(10, 30));

        SetRank(
            context, Date,
            new PriorityRank(p1Task.Id, 1, PlanningTier.ProtectNow),
            new PriorityRank(p2Task.Id, 2, PlanningTier.AdvanceNext));
        context.Calendar.Reload();

        Assert.Equal(
            ["Morning class", "Afternoon class", "Protect work", "Advance work", "Unranked work"],
            context.Daily.ScheduledRows.Select(r => r.Title).ToArray());
    }

    [Fact]
    public void UnscheduledOrdering_RankedThenDeadlineThenCapture()
    {
        var context = Create();
        var noDeadline = AddTask(context, "No deadline");
        var overdue = AddTask(context, "Overdue", deadline: Date.AddDays(-1));
        var future = AddTask(context, "Future deadline", deadline: Date.AddDays(3));
        var p2 = AddTask(context, "Ranked P2");
        var p1 = AddTask(context, "Ranked P1");

        SetRank(
            context, Date,
            new PriorityRank(p1.Id, 1, PlanningTier.ProtectNow),
            new PriorityRank(p2.Id, 2, PlanningTier.AdvanceNext));
        context.Calendar.Reload();

        Assert.Equal(
            ["Ranked P1", "Ranked P2", "Overdue", "Future deadline", "No deadline"],
            context.Daily.UnscheduledRows.Select(r => r.Title).ToArray());
    }

    // ---- Priority mapping ----

    [Fact]
    public void PriorityMapping_TiersUnrankedAndFixed()
    {
        var context = Create();
        context.Service.CreateFixedCommitment("Class", Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var p1 = AddTask(context, "Protect");
        var p2 = AddTask(context, "Advance");
        var p3 = AddTask(context, "Wait");
        var unranked = AddTask(context, "Unranked");
        SetRank(
            context, Date,
            new PriorityRank(p1.Id, 1, PlanningTier.ProtectNow),
            new PriorityRank(p2.Id, 2, PlanningTier.AdvanceNext),
            new PriorityRank(p3.Id, 3, PlanningTier.CanWait));
        context.Calendar.Reload();
        var daily = context.Daily;

        Assert.Equal("P1", daily.UnscheduledRows.Single(r => r.Title == "Protect").PriorityText);
        Assert.Equal(
            "Priority 1 — Protect now",
            daily.UnscheduledRows.Single(r => r.Title == "Protect").PriorityAccessibleText);
        Assert.Equal("P2", daily.UnscheduledRows.Single(r => r.Title == "Advance").PriorityText);
        Assert.Equal(
            "Priority 2 — Advance next",
            daily.UnscheduledRows.Single(r => r.Title == "Advance").PriorityAccessibleText);
        Assert.Equal("P3", daily.UnscheduledRows.Single(r => r.Title == "Wait").PriorityText);
        Assert.Equal(
            "Priority 3 — Can wait",
            daily.UnscheduledRows.Single(r => r.Title == "Wait").PriorityAccessibleText);

        var unrankedRow = daily.UnscheduledRows.Single(r => r.Title == "Unranked");
        Assert.Equal("–", unrankedRow.PriorityText);
        Assert.Equal("Not ranked for this day.", unrankedRow.PriorityAccessibleText);

        var fixedRow = daily.ScheduledRows.Single(r => r.Title == "Class");
        Assert.Equal(string.Empty, fixedRow.PriorityText);
        Assert.Null(fixedRow.PriorityAccessibleText);
    }

    // ---- Proposal deduplication ----

    [Fact]
    public void ProposalTaskIds_AreSubtractedFromUnscheduled()
    {
        var context = Create();
        var sameDay = AddTask(context, "Proposed today");
        var otherDay = AddTask(context, "Proposed tomorrow");
        var free = AddTask(context, "Still free");
        SaveDraft(
            context,
            Propose(sameDay, Date, new TimeOnly(15, 0), new TimeOnly(15, 45)),
            Propose(otherDay, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(9, 45)));
        context.Calendar.Reload();
        var daily = context.Daily;

        Assert.Equal(["Still free"], daily.UnscheduledRows.Select(r => r.Title).ToArray());
        var proposalRow = daily.ScheduledRows.Single(r => r.Title == "Proposed today");
        Assert.True(proposalRow.IsProposal);
        Assert.True(proposalRow.ShowProposalActions);
        Assert.NotNull(proposalRow.Why);
        Assert.DoesNotContain(daily.ScheduledRows, r => r.Title == "Proposed tomorrow");
    }

    // ---- Progress ----

    [Fact]
    public void Progress_CountsAndDeduplicatesByTaskId()
    {
        var context = Create();
        // Open local commitment (counts 1 open) + completed one (counts 1 done).
        context.Service.CreateFixedCommitment("Open class", Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var run = context.Service.CreateFixedCommitment("Run", Date, new TimeOnly(7, 0), new TimeOnly(7, 30));
        context.Service.CompleteCommitmentOccurrence(run.Id, Date);
        // External commitment: excluded entirely.
        context.Blocks.Add(CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, null, "Imported", Date,
            new TimeOnly(13, 0), new TimeOnly(13, 30), BlockKind.FixedCommitment, null,
            "google", "evt-2", 0, BlockOutcome.None, null, context.Clock.Now, context.Clock.Now));
        // One task with two open blocks: one open item, not two.
        var split = AddTask(context, "Split work", TimeSpan.FromMinutes(30));
        context.Service.ScheduleTask(split.Id, Date, new TimeOnly(15, 0));
        context.Service.ScheduleTask(split.Id, Date, new TimeOnly(19, 0));
        // Proposal: excluded from progress.
        var proposed = AddTask(context, "Proposed");
        SaveDraft(context, Propose(proposed, Date, new TimeOnly(17, 0), new TimeOnly(17, 30)));
        // One inbox task (open), one directly completed task (done).
        AddTask(context, "Inbox item");
        var done = AddTask(context, "Done item");
        context.TaskService.Complete(done.Id);

        context.Calendar.Reload();

        // Done: run occurrence + "Done item" = 2. Open: class + split + inbox = 3. Total 5.
        Assert.Equal("2 of 5 complete", context.Daily.ProgressText);
    }

    // ---- Selected-date rank refresh, heading, empty states ----

    [Fact]
    public void Ranks_FollowTheVisibleDate()
    {
        var context = Create();
        var task = AddTask(context, "Tomorrow's priority");
        SetRank(context, Date.AddDays(1), new PriorityRank(task.Id, 1, PlanningTier.ProtectNow));
        context.Calendar.Reload();
        Assert.Equal("–", context.Daily.UnscheduledRows.Single().PriorityText);

        context.Calendar.GoNextCommand.Execute(null);

        Assert.Equal("P1", context.Daily.UnscheduledRows.Single().PriorityText);
    }

    [Fact]
    public void Heading_TodayVersusOtherDay()
    {
        var context = Create();
        Assert.Equal("Today's tasks", context.Daily.HeadingText);

        context.Calendar.GoNextCommand.Execute(null);
        Assert.Equal("Tasks for Wednesday", context.Daily.HeadingText);

        context.Calendar.GoToTodayCommand.Execute(null);
        Assert.Equal("Today's tasks", context.Daily.HeadingText);
    }

    [Fact]
    public void EmptyStates_TrackSectionContents()
    {
        var context = Create();
        context.Calendar.Reload();
        var daily = context.Daily;

        Assert.True(daily.IsScheduledEmpty);
        Assert.True(daily.IsUnscheduledEmpty);
        Assert.False(daily.HasCompleted);
        Assert.False(daily.IsAllComplete);

        var task = AddTask(context, "Only work");
        context.Calendar.Reload();
        Assert.False(daily.IsUnscheduledEmpty);

        context.TaskService.Complete(task.Id);
        context.Calendar.Reload();
        Assert.True(daily.IsAllComplete);
        Assert.True(daily.HasCompleted);
        Assert.False(daily.IsCompletedExpanded); // collapsed by default
    }

    // ---- Mutations (single-refresh guarantee) ----

    [Fact]
    public void CompletingUnscheduledTask_MovesItToCompleted_WithOneAnnouncement()
    {
        var context = Create();
        AddTask(context, "Finish draft");
        context.Calendar.Reload();
        var changed = 0;
        context.Calendar.DataChanged += () => changed++;

        context.Daily.UnscheduledRows.Single().ToggleDoneCommand.Execute(null);

        Assert.Equal(1, changed);
        Assert.Empty(context.Daily.UnscheduledRows);
        var completedRow = context.Daily.CompletedRows.Single();
        Assert.Equal("Finish draft", completedRow.Title);
        Assert.True(completedRow.CanReopen);
    }

    [Fact]
    public void ReopeningCompletedCommitmentOccurrence_ReturnsItToScheduled()
    {
        var context = Create();
        var commitment = context.Service.CreateFixedCommitment(
            "Stats homework", Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        context.Service.CompleteCommitmentOccurrence(commitment.Id, Date);
        context.Calendar.Reload();

        context.Daily.CompletedRows.Single().ToggleDoneCommand.Execute(null);

        Assert.Empty(context.Daily.CompletedRows);
        Assert.Equal("Stats homework", context.Daily.ScheduledRows.Single().Title);
    }

    [Fact]
    public void ReopeningCompletedTask_ReturnsItToUnscheduled()
    {
        var context = Create();
        var task = AddTask(context, "Reopened work");
        context.TaskService.Complete(task.Id);
        context.Calendar.Reload();

        context.Daily.CompletedRows.Single().ReopenCommand.Execute(null);

        Assert.Empty(context.Daily.CompletedRows);
        Assert.Equal("Reopened work", context.Daily.UnscheduledRows.Single().Title);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void UnschedulingBlock_ReturnsOpenTaskToUnscheduled()
    {
        var context = Create();
        var task = AddTask(context, "Flexible work", TimeSpan.FromMinutes(45));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(16, 0));
        context.Calendar.Reload();

        context.Daily.ScheduledRows.Single().UnscheduleCommand.Execute(null);

        Assert.Empty(context.Daily.ScheduledRows);
        Assert.Equal("Flexible work", context.Daily.UnscheduledRows.Single().Title);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void RecordingDone_MovesBlockRowToCompleted()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();

        context.Daily.ScheduledRows.Single().RecordDoneCommand.Execute(null);

        var completedRow = context.Daily.CompletedRows.Single();
        Assert.Equal("Elapsed work", completedRow.Title);
        Assert.True(completedRow.ShowDoneBlockMarker);
        Assert.False(completedRow.CanReopen); // outcomes have no invented binary reopen
        Assert.True(context.Tasks.GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void ProposalApproveAndRemove_KeepSectionsConsistent()
    {
        var context = Create();
        var task = AddTask(context, "Proposed work", TimeSpan.FromMinutes(45));
        SaveDraft(context, Propose(task, Date, new TimeOnly(15, 0), new TimeOnly(15, 45)));
        context.Calendar.Reload();

        context.Daily.ScheduledRows.Single().ApproveProposalCommand.Execute(null);
        var approved = context.Daily.ScheduledRows.Single();
        Assert.Equal("FLEX", approved.StatusText);
        Assert.Empty(context.Daily.UnscheduledRows);

        approved.UnscheduleCommand.Execute(null);
        SaveDraft(context, Propose(task, Date, new TimeOnly(16, 0), new TimeOnly(16, 45)));
        context.Calendar.Reload();
        context.Daily.ScheduledRows.Single().RemoveProposalCommand.Execute(null);

        Assert.Empty(context.Daily.ScheduledRows);
        Assert.Equal("Proposed work", context.Daily.UnscheduledRows.Single().Title);
    }

    // ---- Manual scheduling ----

    [Fact]
    public void ScheduleEditor_Defaults_TodayUsesNextQuarterHour()
    {
        var context = Create();
        AddTask(context, "Soon work", TimeSpan.FromMinutes(45));
        context.Calendar.Reload();

        var editor = context.Daily.UnscheduledRows.Single().ScheduleEditor!;
        Assert.Equal(new TimeSpan(14, 15, 0), editor.Start); // clock is at 14:10
        Assert.Equal(45, editor.DurationMinutes);
        Assert.Equal(Date, DateOnly.FromDateTime(editor.Date!.Value.Date));
    }

    [Fact]
    public void ScheduleEditor_Defaults_OtherDatesUseNineAm()
    {
        var context = Create();
        AddTask(context, "Later work");
        context.Calendar.GoNextCommand.Execute(null);

        var editor = context.Daily.UnscheduledRows.Single().ScheduleEditor!;
        Assert.Equal(new TimeSpan(9, 0, 0), editor.Start);
        Assert.Equal(30, editor.DurationMinutes); // no estimate → the 30-minute default
        Assert.Equal(Date.AddDays(1), DateOnly.FromDateTime(editor.Date!.Value.Date));
    }

    [Fact]
    public void ScheduleEditor_WarnsOnOverlapAndConstraints()
    {
        var context = Create();
        context.Service.CreateFixedCommitment("Class", Date, new TimeOnly(15, 0), new TimeOnly(16, 0));
        var task = AddTask(context, "Constrained work", TimeSpan.FromMinutes(30));
        task.SetConstraints(new SchedulingConstraints(latestTime: new TimeOnly(12, 0)), context.Clock.Now);
        context.Tasks.Update(task);
        context.Calendar.Reload();
        var editor = context.Daily.UnscheduledRows.Single().ScheduleEditor!;

        editor.Start = new TimeSpan(15, 30, 0);

        Assert.Contains("Overlaps Class", editor.WarningText);
        Assert.Contains("Outside this task's scheduling constraints.", editor.WarningText);

        editor.Start = new TimeSpan(10, 0, 0);
        Assert.Null(editor.WarningText);
    }

    [Fact]
    public void ConfirmSchedule_MovesTaskIntoScheduled_AndRequestsFocus()
    {
        var context = Create();
        var task = AddTask(context, "Manual schedule", TimeSpan.FromMinutes(45));
        context.Calendar.Reload();
        var changed = 0;
        context.Calendar.DataChanged += () => changed++;
        TaskId? focused = null;
        context.Daily.RowFocusRequested += id => focused = id;
        var editor = context.Daily.UnscheduledRows.Single().ScheduleEditor!;
        editor.Start = new TimeSpan(16, 0, 0);

        Assert.True(editor.Confirm());

        Assert.Equal(1, changed);
        Assert.Equal(task.Id, focused);
        Assert.Empty(context.Daily.UnscheduledRows);
        var scheduledRow = context.Daily.ScheduledRows.Single();
        Assert.Equal("Manual schedule", scheduledRow.Title);
        var block = context.Blocks.GetForTask(task.Id).Single();
        Assert.Equal(new TimeOnly(16, 0), block.StartTime);
        Assert.Equal(new TimeOnly(16, 45), block.EndTime);
    }

    [Fact]
    public void ConfirmSchedule_DomainError_KeepsTaskUnscheduledWithInlineError()
    {
        var context = Create();
        var task = AddTask(context, "Doomed schedule");
        context.Calendar.Reload();
        var editor = context.Daily.UnscheduledRows.Single().ScheduleEditor!;
        context.Tasks.Delete(task.Id); // force the domain failure

        Assert.False(editor.Confirm());

        Assert.NotNull(editor.Error);
        Assert.Single(context.Daily.UnscheduledRows); // no refresh happened; nothing lost
        Assert.Empty(context.Blocks.GetAll());
    }

    [Fact]
    public void ChangeTime_MovesAndResizesTheBlock()
    {
        var context = Create();
        var task = AddTask(context, "Rescheduled work", TimeSpan.FromMinutes(60));
        var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(15, 0));
        context.Calendar.Reload();
        var editor = context.Daily.ScheduledRows.Single().ScheduleEditor!;
        Assert.Equal("Change time", editor.Heading);

        editor.Start = new TimeSpan(16, 0, 0);
        editor.DurationMinutes = 45;
        Assert.True(editor.Confirm());

        var moved = context.Blocks.GetById(block.Id)!;
        Assert.Equal(new TimeOnly(16, 0), moved.StartTime);
        Assert.Equal(new TimeOnly(16, 45), moved.EndTime);
    }

    // ---- Add task ----

    [Fact]
    public void AddUnscheduled_CapturesOnConfirm_BlankDoesNothing_CancelCloses()
    {
        var context = Create();
        context.Calendar.Reload();
        var daily = context.Daily;

        daily.BeginAddUnscheduledCommand.Execute(null);
        Assert.True(daily.IsAddingUnscheduled);

        daily.NewUnscheduledTitle = "   ";
        daily.ConfirmAddUnscheduled();
        Assert.True(daily.IsAddingUnscheduled); // blank does nothing
        Assert.Empty(context.Tasks.GetAll());

        daily.NewUnscheduledTitle = "Captured inline";
        daily.ConfirmAddUnscheduled();
        Assert.False(daily.IsAddingUnscheduled);
        Assert.Equal("Captured inline", daily.UnscheduledRows.Single().Title);
        Assert.Equal("–", daily.UnscheduledRows.Single().PriorityText); // starts unranked

        daily.BeginAddUnscheduledCommand.Execute(null);
        daily.CancelAddUnscheduled();
        Assert.False(daily.IsAddingUnscheduled);
    }

    [Fact]
    public void AddScheduled_CreatesAndSchedulesOnTheVisibleDate()
    {
        var context = Create();
        var project = Project.Create("Applications", "#C2803F", context.Clock.Now);
        context.Projects.Add(project);
        context.Calendar.Reload();
        var daily = context.Daily;

        daily.BeginAddScheduledCommand.Execute(null);
        daily.NewScheduledTitle = "Planned directly";
        daily.NewScheduledStart = new TimeSpan(17, 0, 0);
        daily.NewScheduledDurationMinutes = 45;
        daily.NewScheduledProject = daily.ProjectChoices.Single(c => c.Name == "Applications");
        daily.ConfirmAddScheduled();

        Assert.False(daily.IsAddingScheduled);
        Assert.Null(daily.ScheduledAddNotice);
        var row = daily.ScheduledRows.Single();
        Assert.Equal("Planned directly", row.Title);
        Assert.Equal("Applications", row.ProjectName);
        Assert.Equal("–", row.PriorityText); // new tasks begin unranked
        var block = context.Blocks.GetAll().Single();
        Assert.Equal(new TimeOnly(17, 0), block.StartTime);
        Assert.Equal(new TimeOnly(17, 45), block.EndTime);
    }

    [Fact]
    public void AddScheduled_ScheduleFailure_KeepsCapturedTaskWithReport()
    {
        var inner = new InMemoryCalendarBlockRepository();
        var failing = new FailingBlockRepository(inner);
        var context = Create(failing);
        context.Calendar.Reload();
        var daily = context.Daily;

        daily.BeginAddScheduledCommand.Execute(null);
        daily.NewScheduledTitle = "Never lost";
        daily.NewScheduledStart = new TimeSpan(17, 0, 0);
        failing.FailNextAdd = true;
        daily.ConfirmAddScheduled();

        Assert.Contains("Never lost", daily.ScheduledAddNotice);
        Assert.Contains("Unscheduled", daily.ScheduledAddNotice);
        Assert.Empty(daily.ScheduledRows);
        Assert.Equal("Never lost", daily.UnscheduledRows.Single().Title);
    }
}
