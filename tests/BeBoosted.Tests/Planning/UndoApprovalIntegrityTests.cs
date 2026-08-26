using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Planning;
using BeBoosted.Infrastructure.Prioritization;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Planning;

/// <summary>
/// Approval Undo may never damage the plan that replaced it, nor the calendar it
/// points at. A replacement plan makes older approvals un-undoable (reverting one
/// would put a second active draft beside the new plan), and a session that has
/// since become a recurring series is never deleted by an Undo. Every rejection
/// happens before the first write, so the calendar block, the proposal, and the
/// active draft all survive a restart unchanged.
/// </summary>
public sealed class UndoApprovalIntegrityTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    /// <summary>Tuesday, 2026-08-11.</summary>
    private static readonly DateOnly Tuesday = new(2026, 8, 11);

    private static readonly PlanningPeriod Today = PlanningPeriod.ForToday(Tuesday);

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqlitePlanningProposalRepository _proposals;

    public UndoApprovalIntegrityTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _proposals = new SqlitePlanningProposalRepository(_database.Factory);
    }

    private CalendarService CreateCalendar() => new(
        _blocks, new SqliteOccurrenceCompletionRepository(_database.Factory),
        new SqliteCalendarMutations(_database.Factory), _tasks, _clock);

    private PlanningService CreatePlanning() => new(
        _proposals, new InboxQueryService(_tasks, _blocks),
        new SqlitePrioritizationRepository(_database.Factory), CreateCalendar(),
        new SqliteCalendarMutations(_database.Factory), _clock);

    private TaskItem AddTask(string title)
    {
        var task = TaskItem.Create(
            title, _clock.Now, estimatedDuration: TimeSpan.FromMinutes(30));
        _tasks.Add(task);
        return task;
    }

    private static ProposedBlock Propose(TaskId taskId, int startHour)
        => new(
            CalendarBlockId.New(), taskId, Tuesday,
            new TimeOnly(startHour, 0), new TimeOnly(startHour + 1, 0),
            new WhyEvidence(null, "1 h", null, "free", null));

    /// <summary>A draft with one pending block per given task.</summary>
    private PlanningProposal SaveDraft(params TaskId[] taskIds)
    {
        var proposal = PlanningProposal.CreateDraft(
            Today,
            taskIds.Select((id, index) => Propose(id, 15 + index)),
            _clock.Now);
        _proposals.Save(proposal);
        return proposal;
    }

    /// <summary>Reads through fresh repositories — the state after reopening the app.</summary>
    private SqlitePlanningProposalRepository RestartedProposals() => new(_database.Factory);

    private SqliteCalendarBlockRepository RestartedBlocks() => new(_database.Factory);

    /// <summary>The replacement plan is the one and only active draft.</summary>
    private void AssertOnlyActiveDraftIs(PlanningProposalId expected)
    {
        var restarted = RestartedProposals();
        var active = restarted.GetActiveDraft();
        Assert.NotNull(active);
        Assert.Equal(expected, active.Id);
        Assert.Equal(
            [expected],
            restarted.GetAll().Where(p => p.State == ProposalState.Draft).Select(p => p.Id));
    }

    // ---- A replacement plan makes older approvals un-undoable ----

    [Fact]
    public void UndoApproval_OfAFullyApprovedPlan_IsRejectedWhileAReplacementDraftIsActive()
    {
        var planned = AddTask("Essay outline");
        var first = SaveDraft(planned.Id);
        var approved = CreatePlanning().ApproveAll(first.Id).Single();
        AddTask("Vocab review"); // the only Inbox work left for the replacement plan
        var replacement = CreatePlanning().CreateDraft(Today).Proposal;
        Assert.NotEmpty(replacement.Blocks);

        Assert.Throws<DomainException>(() => CreatePlanning().UndoApproval(first.Id, [approved]));

        // The physical block was never deleted…
        Assert.NotNull(RestartedBlocks().GetById(approved));
        // …the replacement stays the only active plan…
        AssertOnlyActiveDraftIs(replacement.Id);
        // …and the older plan was not resurrected.
        var reloaded = RestartedProposals().GetById(first.Id)!;
        Assert.Equal(ProposalState.Approved, reloaded.State);
        Assert.Equal(ProposedBlockStatus.Approved, reloaded.GetBlock(approved).Status);
    }

    [Fact]
    public void UndoApproval_OfAPlanDiscardedByAReplacement_IsRejected()
    {
        var planned = AddTask("Essay outline");
        var second = AddTask("Vocab review");
        var first = SaveDraft(planned.Id, second.Id);
        var approved = first.Blocks[0].Id;
        CreatePlanning().ApproveBlock(first.Id, approved); // partial approval: still a Draft
        var replacement = CreatePlanning().CreateDraft(Today).Proposal;
        Assert.Equal(
            ProposalState.Discarded, RestartedProposals().GetById(first.Id)!.State);

        Assert.Throws<DomainException>(() => CreatePlanning().UndoApproval(first.Id, [approved]));

        Assert.NotNull(RestartedBlocks().GetById(approved));
        AssertOnlyActiveDraftIs(replacement.Id);
        var reloaded = RestartedProposals().GetById(first.Id)!;
        Assert.Equal(ProposalState.Discarded, reloaded.State); // never resurrected as a Draft
        Assert.Equal(ProposedBlockStatus.Approved, reloaded.GetBlock(approved).Status);
    }

    /// <summary>
    /// The guard rejects only obsolete plans: the plan the user is still working on
    /// stays fully undoable after a partial approval.
    /// </summary>
    [Fact]
    public void UndoApproval_OfAPartialApproval_StillWorks_WhileThatPlanIsTheActiveDraft()
    {
        var planned = AddTask("Essay outline");
        var second = AddTask("Vocab review");
        var draft = SaveDraft(planned.Id, second.Id);
        var approved = draft.Blocks[0].Id;
        CreatePlanning().ApproveBlock(draft.Id, approved);

        CreatePlanning().UndoApproval(draft.Id, [approved]);

        Assert.Null(RestartedBlocks().GetById(approved));
        AssertOnlyActiveDraftIs(draft.Id);
        Assert.Equal(
            ProposedBlockStatus.Pending,
            RestartedProposals().GetById(draft.Id)!.GetBlock(approved).Status);
    }

    // ---- Undo never deletes a recurring series ----

    [Fact]
    public void UndoApproval_AfterTheApprovedSessionBecameARecurringSeries_IsRejected()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var approved = CreatePlanning().ApproveAll(draft.Id).Single();
        var session = _blocks.GetById(approved)!;

        // The user edits that very session in the session editor and makes it
        // weekly: same block id, but it now stands for a whole series.
        CreateCalendar().UpdateSessionSchedule(
            task.Id,
            approved,
            new TaskScheduleRequest(
                session.Date, session.StartTime, session.EndTime,
                RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)));

        Assert.Throws<DomainException>(() => CreatePlanning().UndoApproval(draft.Id, [approved]));

        // The series survives intact…
        var survivor = RestartedBlocks().GetById(approved);
        Assert.NotNull(survivor);
        Assert.NotNull(survivor.Recurrence);
        Assert.Equal(session.Date, survivor.Date);
        Assert.Equal(session.StartTime, survivor.StartTime);
        // …and the proposal was not partially mutated.
        var reloaded = RestartedProposals().GetById(draft.Id)!;
        Assert.Equal(ProposalState.Approved, reloaded.State);
        Assert.Equal(ProposedBlockStatus.Approved, reloaded.GetBlock(approved).Status);
        Assert.Null(RestartedProposals().GetActiveDraft());
    }

    /// <summary>
    /// Ordinary rescheduling of the original one-off session keeps it undoable —
    /// only turning it into a series withdraws that.
    /// </summary>
    [Fact]
    public void UndoApproval_AfterAnOrdinaryMoveAndResize_StillSucceeds()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var approved = CreatePlanning().ApproveAll(draft.Id).Single();

        CreateCalendar().MoveBlock(approved, Tuesday.AddDays(1), new TimeOnly(9, 0));
        CreateCalendar().ResizeBlock(approved, new TimeOnly(11, 0));

        CreatePlanning().UndoApproval(draft.Id, [approved]);

        Assert.Null(RestartedBlocks().GetById(approved));
        var reverted = RestartedProposals().GetById(draft.Id)!;
        Assert.Equal(ProposalState.Draft, reverted.State);
        Assert.Equal(ProposedBlockStatus.Pending, reverted.GetBlock(approved).Status);
    }

    public void Dispose() => _database.Dispose();
}
