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
        CalendarService Service)
    {
        public DailyListViewModel Daily => Calendar.Daily;
    }

    /// <summary>Delegates to the shared in-memory store; Add throws while armed.</summary>
    private sealed class FailingBlockRepository(InMemoryCalendarBlockRepository inner) : ICalendarBlockRepository
    {
        public InMemoryCalendarBlockRepository Inner => inner;

        public bool FailNextAdd { get; set; }

        /// <summary>1-based write counter; the matching Update throws instead of writing.</summary>
        public int? FailOnUpdateNumber { get; set; }

        private int _updates;

        public void Add(CalendarBlock block)
        {
            if (FailNextAdd)
            {
                FailNextAdd = false;
                throw new DomainException("The calendar rejected that block.");
            }

            inner.Add(block);
        }

        public void Update(CalendarBlock block)
        {
            if (++_updates == FailOnUpdateNumber)
            {
                throw new DomainException("The calendar rejected that change.");
            }

            inner.Update(block);
        }

        public void Delete(CalendarBlockId id) => inner.Delete(id);

        public CalendarBlock? GetById(CalendarBlockId id) => inner.GetById(id);

        public IReadOnlyList<CalendarBlock> GetAll() => inner.GetAll();

        public IReadOnlyList<CalendarBlock> GetCandidatesBetween(DateOnly from, DateOnly to)
            => inner.GetCandidatesBetween(from, to);

        public IReadOnlyList<CalendarBlock> GetForTask(TaskId taskId) => inner.GetForTask(taskId);

        public IReadOnlyList<CalendarBlock> GetElapsedWithoutOutcome(DateOnly today, TimeOnly now)
            => inner.GetElapsedWithoutOutcome(today, now);

        public IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks() => inner.GetTaskIdsWithPendingBlocks();
    }

    private static Context Create(FailingBlockRepository? failingBlocks = null)
    {
        var clock = new FakeClock(Date);
        var tasks = new InMemoryTaskRepository();
        var blocks = failingBlocks?.Inner ?? new InMemoryCalendarBlockRepository();
        blocks.Tasks = tasks;
        ICalendarBlockRepository blockPort = failingBlocks is null
            ? blocks
            : failingBlocks;
        var completions = new InMemoryOccurrenceCompletionRepository();
        var proposals = new InMemoryPlanningProposalRepository();
        var mutations = new InMemoryCalendarMutations(blockPort, completions, tasks, proposals);
        var service = new CalendarService(blockPort, completions, mutations, tasks, clock);
        var ranks = new InMemoryPrioritizationRepository();
        var planning = new PlanningService(
            proposals, new InboxQueryService(tasks, blockPort), ranks, service,
            mutations, clock);
        var projects = new InMemoryProjectRepository();
        var calendar = TestShell.CreateCalendarViewModel(
            new InMemorySettingsStore(), clock, tasks, blocks, projects, service, planning, ranks);
        return new Context(
            calendar, tasks, blocks, projects, ranks, proposals, clock, service);
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

    /// <summary>A scheduled task created through the unified editor path.</summary>
    private static CalendarBlock AddScheduled(
        Context context, string title, TimeOnly start, TimeOnly end,
        RecurrenceRule? recurrence = null, DateOnly? date = null)
    {
        var task = context.Service.CreateTask(
            new TaskDetailsRequest(title, null, null, null),
            new TaskScheduleRequest(date ?? Date, start, end, recurrence));
        return context.Blocks.GetForTask(task.Id).Single();
    }

    /// <summary>A weekly-Tuesday repeating task anchored on the design date (an obligation row).</summary>
    private static CalendarBlock AddRepeating(
        Context context, string title, TimeOnly start, TimeOnly end)
        => AddScheduled(context, title, start, end, RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

    // ---- Classification ----

    [Fact]
    public void Rebuild_ClassifiesRows()
    {
        var context = Create();
        AddRepeating(context, "Stats homework", new TimeOnly(9, 0), new TimeOnly(10, 0));
        context.Blocks.Add(CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "Imported standup", Date,
            new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.ExternalEvent, null,
            "google", "evt-1", 0, BlockOutcome.None, null, context.Clock.Now, context.Clock.Now));
        var doneRepeating = AddRepeating(context, "Morning run", new TimeOnly(7, 0), new TimeOnly(7, 30));
        context.Service.CompleteOccurrence(doneRepeating.Id, Date);

        var openBlockTask = AddTask(context, "Scholarship review", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(openBlockTask.Id, Date, new TimeOnly(16, 0));
        var doneBlockTask = AddTask(context, "Morning reading", TimeSpan.FromMinutes(40));
        var doneBlock = context.Service.ScheduleTask(doneBlockTask.Id, Date, new TimeOnly(7, 10));
        context.Service.RecordOutcome(doneBlock.Id, BlockOutcome.Done);

        var proposalTask = AddTask(context, "Econ chapter 6", TimeSpan.FromMinutes(45));
        SaveDraft(context, Propose(proposalTask, Date, new TimeOnly(15, 0), new TimeOnly(15, 45)));

        var inboxTask = AddTask(context, "Email advisor");
        var directDone = AddTask(context, "Order textbooks");
        context.Service.CompleteTask(directDone.Id);

        context.Calendar.Reload();
        var daily = context.Daily;

        // Time obligations first (by start), then unranked flex by start.
        Assert.Equal(
            ["Stats homework", "Imported standup", "Econ chapter 6", "Scholarship review"],
            daily.ScheduledRows.Select(r => r.Title).ToArray());
        // "Morning reading" holds two rows now: its one session is settled history in
        // Completed, while the task itself is still open and back in Unscheduled.
        Assert.Equal(
            ["Email advisor", "Morning reading"],
            daily.UnscheduledRows.Select(r => r.Title).ToArray());
        Assert.Equal(
            ["Morning reading", "Morning run", "Order textbooks"],
            daily.CompletedRows.Select(r => r.Title).OrderBy(t => t).ToArray());

        var external = daily.ScheduledRows.Single(r => r.Title == "Imported standup");
        Assert.True(external.IsLocked);
        Assert.False(external.ShowOccurrenceCheck);
        // TDD phase 7: no FIXED/FLEX labels — external events say Synced, nothing else.
        Assert.Equal("Synced", external.StatusText);
        Assert.False(daily.ScheduledRows.Single(r => r.Title == "Scholarship review").HasStatus);
        Assert.Equal("PROPOSED", daily.ScheduledRows.Single(r => r.Title == "Econ chapter 6").StatusText);
        Assert.True(daily.ScheduledRows.Single(r => r.Title == "Stats homework").ShowOccurrenceCheck);
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
        Assert.True(row.ShowSessionCheck);
        Assert.True(row.ShowSessionOutcomeAction);
    }

    // ---- Ordering ----

    [Fact]
    public void ScheduledOrdering_FixedFirst_ThenRankedByTier_ThenUnrankedByStart()
    {
        var context = Create();
        AddRepeating(context, "Afternoon class", new TimeOnly(14, 0), new TimeOnly(15, 0));
        AddRepeating(context, "Morning class", new TimeOnly(9, 0), new TimeOnly(10, 0));

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
        AddRepeating(context, "Class", new TimeOnly(9, 0), new TimeOnly(10, 0));
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
        // Open repeating occurrence (counts 1 open) + completed one (counts 1 done).
        AddRepeating(context, "Open class", new TimeOnly(9, 0), new TimeOnly(10, 0));
        var run = AddRepeating(context, "Run", new TimeOnly(7, 0), new TimeOnly(7, 30));
        context.Service.CompleteOccurrence(run.Id, Date);
        // External event: excluded entirely.
        context.Blocks.Add(CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "Imported", Date,
            new TimeOnly(13, 0), new TimeOnly(13, 30), BlockKind.ExternalEvent, null,
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
        context.Service.CompleteTask(done.Id);

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

        context.Service.CompleteTask(task.Id);
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
    public void ReopeningCompletedOccurrence_ReturnsItToScheduled()
    {
        var context = Create();
        var repeating = AddRepeating(context, "Stats homework", new TimeOnly(9, 0), new TimeOnly(10, 0));
        context.Service.CompleteOccurrence(repeating.Id, Date);
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
        context.Service.CompleteTask(task.Id);
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
        Assert.True(completedRow.ShowSessionCheck);
        Assert.True(completedRow.CanReopen); // a mis-click is taken back from this page
        // The session resolved, not the task: the task returns to Unscheduled.
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal("Elapsed work", context.Daily.UnscheduledRows.Single().Title);
    }

    // ---- Scheduled sessions: one-click done and undo ----

    [Fact]
    public void ElapsedSessionRow_TogglesDoneInOneClick()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0)); // ended 10:00 < now 14:10
        context.Calendar.Reload();
        Assert.True(context.Daily.ScheduledRows.Single().NeedsOutcome);

        context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);

        Assert.Empty(context.Daily.ScheduledRows);
        var completed = context.Daily.CompletedRows.Single();
        Assert.Equal("Elapsed work", completed.Title);
        Assert.True(completed.IsDone);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted); // the session only
    }

    [Fact]
    public void UpcomingSessionRow_TogglesDoneBeforeItsSlotStarts()
    {
        var context = Create();
        var task = AddTask(context, "Evening work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(20, 0)); // starts 20:00 > now 14:10
        context.Calendar.Reload();
        var row = context.Daily.ScheduledRows.Single();
        Assert.False(row.NeedsOutcome); // not elapsed — and elapsing was never a gate

        row.ToggleDoneCommand.Execute(null);

        Assert.True(context.Daily.CompletedRows.Single().IsDone);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted); // the session only
    }

    [Fact]
    public void DoneSessionRow_TogglesBackToScheduled_ClearingItsOutcome()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();
        context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);
        var completed = context.Daily.CompletedRows.Single();
        Assert.True(completed.CanReopen);

        completed.ToggleDoneCommand.Execute(null);

        Assert.Empty(context.Daily.CompletedRows);
        var reopened = context.Daily.ScheduledRows.Single();
        Assert.Equal("Elapsed work", reopened.Title);
        Assert.False(reopened.IsDone);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, context.Blocks.GetById(block.Id)!.Outcome);
    }

    [Fact]
    public void SessionRow_SurvivesADoneUndoneDoneRoundTrip()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();
        var changes = 0;
        context.Calendar.DataChanged += () => changes++;

        context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);
        context.Daily.CompletedRows.Single().ToggleDoneCommand.Execute(null);
        context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null);

        Assert.Empty(context.Daily.ScheduledRows);
        Assert.True(context.Daily.CompletedRows.Single().IsDone);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted); // the session only
        Assert.Equal(BlockOutcome.Done, context.Blocks.GetById(block.Id)!.Outcome);
        Assert.Equal(3, changes); // exactly one announcement per click, both directions
    }

    /// <summary>
    /// A one-off session's Done is local, so a repeating sibling no longer blocks it.
    /// The gate and its explanatory note are gone with the aggregate rule.
    /// </summary>
    [Fact]
    public void MixedScheduleSessionRow_CompletesNormally_DespiteARepeatingSibling()
    {
        var context = Create();
        var task = AddTask(context, "Mixed work");
        var session = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Blocks.Add(CalendarBlock.CreateTaskSession(
            task.Id, Date, new TimeOnly(16, 0), new TimeOnly(17, 0), context.Clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)));
        context.Calendar.Reload();

        var row = context.Daily.ScheduledRows.Single(r => r.BlockId == session.Id);
        Assert.True(row.ShowSessionCheck);
        Assert.True(row.ShowSessionOutcomeAction);
        Assert.True(row.ToggleDoneCommand.CanExecute(null));
        Assert.True(row.RecordDoneCommand.CanExecute(null));
        Assert.True(row.RecordNeedsMoreTimeCommand.CanExecute(null));
        Assert.True(row.RecordDidntHappenCommand.CanExecute(null));

        row.ToggleDoneCommand.Execute(null);

        Assert.Contains(context.Daily.CompletedRows, r => r.BlockId == session.Id);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    }

    /// <summary>
    /// Undo is never gated on a repeating sibling either: the done one-off takes its
    /// own outcome back, and the repeating session is not part of that transaction.
    /// </summary>
    [Fact]
    public void DoneSessionRow_WithARepeatingSibling_CanStillBeToggledBack()
    {
        var context = Create();
        var task = AddTask(context, "Mixed work");
        var session = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();
        context.Daily.ScheduledRows.Single().ToggleDoneCommand.Execute(null); // done while solo
        var repeating = CalendarBlock.CreateTaskSession(
            task.Id, Date, new TimeOnly(16, 0), new TimeOnly(17, 0), context.Clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        context.Blocks.Add(repeating); // a repeating sibling appears afterwards
        context.Calendar.Reload();

        var completed = context.Daily.CompletedRows.Single(r => r.BlockId == session.Id);
        Assert.True(completed.ToggleDoneCommand.CanExecute(null)); // undo stays open
        completed.ToggleDoneCommand.Execute(null);

        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, context.Blocks.GetById(session.Id)!.Outcome);
        Assert.Contains(context.Daily.ScheduledRows, r => r.BlockId == session.Id);
    }

    [Fact]
    public void SessionRow_ShowsACheckbox_AndKeepsTheOtherOutcomesAsASideAction()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();

        var row = context.Daily.ScheduledRows.Single();
        Assert.True(row.ShowSessionCheck);
        Assert.True(row.ShowSessionOutcomeAction);
        Assert.True(row.ToggleDoneCommand.CanExecute(null));

        row.ToggleDoneCommand.Execute(null);

        var completed = context.Daily.CompletedRows.Single();
        Assert.True(completed.ShowSessionCheck);          // the undo affordance
        Assert.False(completed.ShowSessionOutcomeAction); // nothing left to record
    }

    [Fact]
    public void SideActionOutcomes_StillWork_FromTheirNewHome()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();
        var row = context.Daily.ScheduledRows.Single();
        Assert.True(row.ShowSessionOutcomeAction);
        row.RemainingMinutes = 45;

        row.RecordNeedsMoreTimeCommand.Execute(null);

        Assert.Equal(BlockOutcome.NeedsMoreTime, context.Blocks.GetById(block.Id)!.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(45), context.Tasks.GetById(task.Id)!.EstimatedDuration);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void DidntHappen_StillWorks_FromTheSideAction()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();

        context.Daily.ScheduledRows.Single().RecordDidntHappenCommand.Execute(null);

        Assert.Equal(BlockOutcome.DidntHappen, context.Blocks.GetById(block.Id)!.Outcome);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    }

    /// <summary>
    /// The unranked dash's explanation must surface even though the re-rank marker
    /// is disabled — a disabled control raises no tooltip, so a hit-testable overlay
    /// carries it (the same pattern as the blocked session checkbox).
    /// </summary>
    [Fact]
    public void UnrankedRows_CarryAHitTestableRankExplanation()
    {
        var context = Create();
        AddTask(context, "Unranked work", TimeSpan.FromMinutes(30));
        context.Calendar.Reload();

        var row = context.Daily.UnscheduledRows.Single();
        Assert.Equal("–", row.PriorityText);
        Assert.False(row.CanRerank);
        Assert.True(row.ShowUnrankedNote);
        Assert.Equal("Not ranked for this day.", row.PriorityAccessibleText);
    }

    /// <summary>
    /// Capture is a mutation like any other: the Daily list behind the drawer must
    /// pick the task up through the shared chain, exactly once.
    /// </summary>
    [Fact]
    public void CapturingFromTheInboxDrawer_RefreshesTheDailyList_ExactlyOnce()
    {
        var shell = TestShell.Create();
        var announced = 0;
        shell.Calendar.DataChanged += () => announced++;

        shell.Inbox.CaptureText = "Fresh capture";
        shell.Inbox.CaptureCommand.Execute(null);

        Assert.Contains(shell.Calendar.Daily.UnscheduledRows, r => r.Title == "Fresh capture");
        Assert.Equal(1, announced);
    }

    [Fact]
    public void ChatCaptures_RefreshTheDailyList()
    {
        var shell = TestShell.Create();
        shell.Chat.InputText = "Draft the essay outline. Also email the recommendation request.";
        shell.Chat.SubmitCommand.Execute(null);
        var review = Assert.IsType<ChatReviewItemViewModel>(shell.Chat.Items[^1]);

        review.AddAllCommand.Execute(null);

        Assert.Contains(
            shell.Calendar.Daily.UnscheduledRows, r => r.Title == "Draft the essay outline");
    }

    /// <summary>
    /// "Didn't happen" settles the session: it leaves Scheduled as resolved history
    /// for the day, and the task itself continues in Unscheduled — never both
    /// sections at once (the plan's explicit invariant).
    /// </summary>
    [Fact]
    public void DidntHappen_MovesTheSessionToResolvedHistory_AndListsTheTaskOnceInUnscheduled()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();

        context.Daily.ScheduledRows.Single().RecordDidntHappenCommand.Execute(null);

        Assert.DoesNotContain(context.Daily.ScheduledRows, r => r.TaskId == task.Id);
        Assert.Single(context.Daily.UnscheduledRows, r => r.TaskId == task.Id);
        var resolved = Assert.Single(context.Daily.CompletedRows);
        Assert.False(resolved.IsDone); // no invented completion, no strikethrough
        Assert.Equal("Didn't happen", resolved.StatusText);
        Assert.False(resolved.ShowSessionOutcomeAction);
        Assert.False(resolved.ShowChangeTimeAction);
        Assert.Equal("0 of 1 complete", context.Daily.ProgressText);
    }

    [Fact]
    public void NeedsMoreTime_MovesTheSessionToResolvedHistory_AndListsTheTaskOnceInUnscheduled()
    {
        var context = Create();
        var task = AddTask(context, "Elapsed work", TimeSpan.FromMinutes(60));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();
        var row = context.Daily.ScheduledRows.Single();
        row.RemainingMinutes = 45;

        row.RecordNeedsMoreTimeCommand.Execute(null);

        Assert.DoesNotContain(context.Daily.ScheduledRows, r => r.TaskId == task.Id);
        Assert.Single(context.Daily.UnscheduledRows, r => r.TaskId == task.Id);
        Assert.Equal("Needs more time", Assert.Single(context.Daily.CompletedRows).StatusText);
        Assert.Equal("0 of 1 complete", context.Daily.ProgressText);
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
        Assert.False(approved.HasStatus); // no FLEX badge — a scheduled time says it all
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
        AddScheduled(context, "Class", new TimeOnly(15, 0), new TimeOnly(16, 0));
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

    /// <summary>
    /// Change time is one operation: whether it succeeds or fails, the session's
    /// date, start, and end always move together — a torn write (moved but not
    /// resized) is never possible.
    /// </summary>
    [Fact]
    public void ChangeTime_IsOneOperation_NeverLeavesATornReschedule()
    {
        var inner = new InMemoryCalendarBlockRepository();
        var failing = new FailingBlockRepository(inner);
        var context = Create(failing);
        var task = AddTask(context, "Rescheduled work", TimeSpan.FromMinutes(60));
        var block = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(15, 0));
        context.Calendar.Reload();
        var editor = context.Daily.ScheduledRows.Single().ScheduleEditor!;
        editor.Date = new DateTimeOffset(Date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        editor.Start = new TimeSpan(16, 0, 0);
        editor.DurationMinutes = 45;
        // If the reschedule needed a second write, this would tear it apart.
        failing.FailOnUpdateNumber = 2;

        var confirmed = editor.Confirm();

        var persisted = inner.GetById(block.Id)!;
        if (confirmed)
        {
            Assert.Equal(Date.AddDays(1), persisted.Date);
            Assert.Equal(new TimeOnly(16, 0), persisted.StartTime);
            Assert.Equal(new TimeOnly(16, 45), persisted.EndTime);
        }
        else
        {
            // A failed reschedule leaves every field exactly as it was.
            Assert.NotNull(editor.Error);
            Assert.Equal(Date, persisted.Date);
            Assert.Equal(new TimeOnly(15, 0), persisted.StartTime);
            Assert.Equal(new TimeOnly(16, 0), persisted.EndTime);
        }
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

    // ---- Add task (both entry points open the one canonical Task editor) ----

    [Fact]
    public void AddScheduledTask_OpensTheCanonicalEditor_PrefilledForTheVisibleDate()
    {
        var context = Create();
        context.Calendar.Reload();

        context.Daily.AddScheduledTaskCommand.Execute(null);

        // Design clock is Tuesday 14:10 → next quarter-hour start, default 30 min.
        var editor = Assert.IsType<WholeTaskEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.Null(editor.TaskId); // create mode
        Assert.True(editor.ShowInlineSchedule);
        Assert.Equal(Date.ToDateTime(TimeOnly.MinValue), editor.InlineSchedule.Date!.Value.Date);
        Assert.Equal(new TimeSpan(14, 15, 0), editor.InlineSchedule.Start);
        Assert.Equal(new TimeSpan(14, 45, 0), editor.InlineSchedule.End);

        editor.Title = "Planned directly";
        editor.SaveCommand.Execute(null);

        Assert.Null(context.Calendar.ActiveTaskEditor);
        var row = context.Daily.ScheduledRows.Single();
        Assert.Equal("Planned directly", row.Title);
        var block = context.Blocks.GetAll().Single();
        Assert.Equal(Date, block.Date);
        Assert.Equal(new TimeOnly(14, 15), block.StartTime);
        Assert.Equal(new TimeOnly(14, 45), block.EndTime);
    }

    [Fact]
    public void AddUnscheduledTask_OpensTheCanonicalEditor_WithSchedulingOff()
    {
        var context = Create();
        context.Calendar.Reload();

        context.Daily.AddUnscheduledTaskCommand.Execute(null);

        var editor = Assert.IsType<WholeTaskEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.Null(editor.TaskId); // create mode
        Assert.False(editor.ShowInlineSchedule);

        editor.Title = "Captured through the editor";
        editor.SaveCommand.Execute(null);

        Assert.Null(context.Calendar.ActiveTaskEditor);
        Assert.Equal("Captured through the editor", context.Daily.UnscheduledRows.Single().Title);
        Assert.Empty(context.Blocks.GetAll());
    }

    [Fact]
    public void AddScheduledTask_ScheduleFailure_KeepsTheEditorOpenWithTheError()
    {
        var inner = new InMemoryCalendarBlockRepository();
        var failing = new FailingBlockRepository(inner);
        var context = Create(failing);
        context.Calendar.Reload();

        context.Daily.AddScheduledTaskCommand.Execute(null);
        var editor = Assert.IsType<WholeTaskEditorViewModel>(context.Calendar.ActiveTaskEditor);
        editor.Title = "Never lost";
        failing.FailNextAdd = true;
        editor.SaveCommand.Execute(null);

        // Nothing is silently dropped: the modal stays open and reports the failure.
        // (The neither-record-persists guarantee is proven against real SQLite in
        // CalendarMutationAtomicityTests.)
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.NotNull(editor.Error);
        Assert.Empty(context.Daily.ScheduledRows);
    }

    // ---- Task completion routes through the one authoritative service path ----

    /// <summary>
    /// Reopening a completed task from the Daily list must clear the matching Done
    /// session outcome too — the same reconciliation the canonical editor performs.
    /// </summary>
    [Fact]
    public void ReopeningACompletedTask_FromTheDailyList_ClearsItsSessionOutcome()
    {
        var context = Create();
        var task = AddTask(context, "Evening run");
        var session = context.Service.ScheduleTask(task.Id, Date.AddDays(1), new TimeOnly(18, 0));
        task.Complete(context.Clock.Now);
        session.RecordOutcome(BlockOutcome.Done, context.Clock.Now);
        context.Tasks.Update(task);
        context.Blocks.Update(session);
        context.Calendar.Reload();

        var row = context.Daily.CompletedRows.Single(r => r.Title == "Evening run");
        row.ReopenCommand.Execute(null);

        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, context.Blocks.GetById(session.Id)!.Outcome);
        // Its session is pending again, so the row leaves today's completed list.
        Assert.DoesNotContain(context.Daily.CompletedRows, r => r.Title == "Evening run");
    }

    // ---- Per-session completion: one session at a time, never the whole task ----

    [Fact]
    public void MarkingOneSessionDone_LeavesTheOtherScheduled_AndTheTaskOutOfUnscheduled()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var morning = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(19, 0));
        context.Calendar.Reload();
        Assert.Equal(2, context.Daily.ScheduledRows.Count);

        context.Daily.ScheduledRows.Single(r => r.BlockId == morning.Id)
            .ToggleDoneCommand.Execute(null);

        Assert.Single(context.Daily.ScheduledRows);
        Assert.Single(context.Daily.CompletedRows);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
        Assert.Empty(context.Daily.UnscheduledRows); // still held by the evening session
    }

    [Fact]
    public void UndoingOneSession_ReturnsItToScheduled_WithoutDisturbingItsSibling()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var morning = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        var evening = context.Service.ScheduleTask(task.Id, Date, new TimeOnly(19, 0));
        context.Calendar.Reload();
        context.Daily.ScheduledRows.Single(r => r.BlockId == morning.Id)
            .ToggleDoneCommand.Execute(null);
        context.Daily.ScheduledRows.Single(r => r.BlockId == evening.Id)
            .ToggleDoneCommand.Execute(null);
        Assert.Equal(2, context.Daily.CompletedRows.Count);
        Assert.Single(context.Daily.UnscheduledRows); // both resolved: the task is back

        context.Daily.CompletedRows.Single(r => r.BlockId == morning.Id)
            .ToggleDoneCommand.Execute(null);

        Assert.Single(context.Daily.ScheduledRows);
        Assert.Single(context.Daily.CompletedRows);
        Assert.Equal(BlockOutcome.Done, context.Blocks.GetById(evening.Id)!.Outcome);
        Assert.Empty(context.Daily.UnscheduledRows);
    }

    // ---- Per-session titles: a titled session leads with its own title and
    // ---- names its parent task beneath it ----

    [Fact]
    public void ATitledSessionRow_LeadsWithItsOwnTitle_AndNamesTheParentTask()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        context.Service.AddSession(
            task.Id,
            new TaskScheduleRequest(Date, new TimeOnly(9, 0), new TimeOnly(10, 0), null, "Jane Eyre 1-10"));
        context.Calendar.Reload();

        var row = context.Daily.ScheduledRows.Single();

        Assert.Equal("Jane Eyre 1-10", row.Title);
        Assert.Equal("Read Jane Eyre 1-20", row.ParentTitle);
        Assert.True(row.HasParentTitle);
    }

    [Fact]
    public void AnUntitledSessionRow_ShowsTheTaskTitle_AndNoParentSubtext()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        context.Service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        context.Calendar.Reload();

        var row = context.Daily.ScheduledRows.Single();

        Assert.Equal("Read Jane Eyre 1-20", row.Title);
        Assert.Null(row.ParentTitle);
        Assert.False(row.HasParentTitle);
    }
}
