using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
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
/// Approval and Undo validate their complete request against live state before the
/// first write: a completed or missing Task, a proposal that is no longer an active
/// draft, or a calendar block that no longer matches its approval rejects the whole
/// batch — and the rejection leaves nothing behind that reopening the database could
/// reveal.
/// </summary>
public sealed class PlanningApprovalGuardTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    /// <summary>Tuesday, 2026-08-11.</summary>
    private static readonly DateOnly Tuesday = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqlitePlanningProposalRepository _proposals;

    public PlanningApprovalGuardTests()
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
        var task = TaskItem.Create(title, _clock.Now);
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
            PlanningPeriod.ForToday(Tuesday),
            taskIds.Select((id, index) => Propose(id, 15 + index)),
            _clock.Now);
        _proposals.Save(proposal);
        return proposal;
    }

    /// <summary>The approval survives untouched — verified through fresh repositories.</summary>
    private void AssertApprovedIntactOnRestart(PlanningProposalId proposalId, CalendarBlockId blockId)
    {
        Assert.NotNull(new SqliteCalendarBlockRepository(_database.Factory).GetById(blockId));
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(proposalId)!;
        Assert.Equal(ProposedBlockStatus.Approved, reloaded.GetBlock(blockId).Status);
    }

    // ---- Completed Tasks can never be approved onto the calendar ----

    [Fact]
    public void ApprovingACompletedTasksBlock_IsRejected_UntilTheTaskIsReopened()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var blockId = draft.Blocks.Single().Id;
        CreateCalendar().CompleteTask(task.Id); // completed after planning, before approval

        var rejection = Assert.Throws<DomainException>(
            () => CreatePlanning().ApproveBlock(draft.Id, blockId));

        Assert.Contains("reopen", rejection.Message, StringComparison.OrdinalIgnoreCase);
        // Verified through fresh repositories — the state after reopening the app.
        Assert.Null(new SqliteCalendarBlockRepository(_database.Factory).GetById(blockId));
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.Equal(ProposalState.Draft, reloaded.State);
        Assert.Equal(ProposedBlockStatus.Pending, reloaded.GetBlock(blockId).Status);

        // Reopening the Task makes a plain retry succeed with the same block id.
        CreateCalendar().ReopenTask(task.Id);
        Assert.Equal(blockId, CreatePlanning().ApproveBlock(draft.Id, blockId));
        Assert.NotNull(_blocks.GetById(blockId));
    }

    [Fact]
    public void ApproveAll_ContainingOneCompletedTask_ApprovesNothing()
    {
        var open = AddTask("Essay outline");
        var completed = AddTask("Vocab review");
        var draft = SaveDraft(open.Id, completed.Id);
        CreateCalendar().CompleteTask(completed.Id);

        Assert.Throws<DomainException>(() => CreatePlanning().ApproveAll(draft.Id));

        // The whole mixed batch was rejected before any write — restart-verified.
        Assert.All(
            draft.Blocks,
            b => Assert.Null(new SqliteCalendarBlockRepository(_database.Factory).GetById(b.Id)));
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.Equal(ProposalState.Draft, reloaded.State);
        Assert.All(reloaded.Blocks, b => Assert.Equal(ProposedBlockStatus.Pending, b.Status));

        // Reopening the Task makes a plain retry approve both with the same ids.
        CreateCalendar().ReopenTask(completed.Id);
        var created = CreatePlanning().ApproveAll(draft.Id);
        Assert.Equal(2, created.Count);
        Assert.All(created, id => Assert.NotNull(_blocks.GetById(id)));
    }

    // ---- Only an active Draft can be approved ----

    [Fact]
    public void ADiscardedProposal_CannotMaterializeCalendarBlocks()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var blockId = draft.Blocks.Single().Id;
        CreatePlanning().DiscardDraft(draft.Id);

        // The discarded proposal still holds a pending row — a stale or direct
        // caller must not be able to approve through it.
        Assert.Throws<DomainException>(() => CreatePlanning().ApproveBlock(draft.Id, blockId));
        Assert.Throws<DomainException>(() => CreatePlanning().ApproveAll(draft.Id));

        Assert.Null(new SqliteCalendarBlockRepository(_database.Factory).GetById(blockId));
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.Equal(ProposalState.Discarded, reloaded.State);
        Assert.Equal(ProposedBlockStatus.Pending, reloaded.GetBlock(blockId).Status);
    }

    [Fact]
    public void AFullyApprovedProposal_CannotBeApprovedAgain()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var blockId = draft.Blocks.Single().Id;
        CreatePlanning().ApproveAll(draft.Id);

        Assert.Throws<DomainException>(() => CreatePlanning().ApproveAll(draft.Id));
        Assert.Throws<DomainException>(() => CreatePlanning().ApproveBlock(draft.Id, blockId));

        // Exactly the one original block; nothing was double-materialized.
        Assert.Single(new SqliteCalendarBlockRepository(_database.Factory).GetAll());
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.Equal(ProposalState.Approved, reloaded.State);
        Assert.Equal(ProposedBlockStatus.Approved, reloaded.GetBlock(blockId).Status);
    }

    // ---- Undo validates its complete request before deleting or reverting ----

    [Fact]
    public void UndoApproval_WithAnEmptyRequest_IsRejected()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var blockId = draft.Blocks.Single().Id;
        CreatePlanning().ApproveAll(draft.Id);

        Assert.Throws<DomainException>(() => CreatePlanning().UndoApproval(draft.Id, []));

        AssertApprovedIntactOnRestart(draft.Id, blockId);
    }

    [Fact]
    public void UndoApproval_RepeatingABlockId_IsRejectedAsDuplicate()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var blockId = draft.Blocks.Single().Id;
        CreatePlanning().ApproveAll(draft.Id);

        var rejection = Assert.Throws<DomainException>(
            () => CreatePlanning().UndoApproval(draft.Id, [blockId, blockId]));

        Assert.Contains("twice", rejection.Message, StringComparison.OrdinalIgnoreCase);
        AssertApprovedIntactOnRestart(draft.Id, blockId);
    }

    [Fact]
    public void UndoApproval_NamingABlockOutsideTheProposal_IsRejectedBeforeAnyWrite()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var blockId = draft.Blocks.Single().Id;
        CreatePlanning().ApproveAll(draft.Id);

        Assert.Throws<DomainException>(
            () => CreatePlanning().UndoApproval(draft.Id, [blockId, CalendarBlockId.New()]));

        AssertApprovedIntactOnRestart(draft.Id, blockId);
    }

    [Fact]
    public void UndoApproval_WhenACalendarBlockIsAlreadyGone_IsRejected()
    {
        var first = AddTask("Essay outline");
        var second = AddTask("Vocab review");
        var draft = SaveDraft(first.Id, second.Id);
        var approved = CreatePlanning().ApproveAll(draft.Id);
        _blocks.Delete(approved[0]); // vanished outside the undo path

        Assert.Throws<DomainException>(() => CreatePlanning().UndoApproval(draft.Id, approved));

        // The surviving block and both approved statuses stay untouched.
        AssertApprovedIntactOnRestart(draft.Id, approved[1]);
        Assert.Equal(
            ProposedBlockStatus.Approved,
            new SqlitePlanningProposalRepository(_database.Factory)
                .GetById(draft.Id)!.GetBlock(approved[0]).Status);
    }

    [Fact]
    public void UndoApproval_WhenTheCalendarBlockIsNoLongerALocalSession_IsRejected()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        CreatePlanning().ApproveAll(draft.Id);
        var blockId = draft.Blocks.Single().Id;
        // Corruption: the id now names a synced external event, not our session.
        _blocks.Delete(blockId);
        _blocks.Add(CalendarBlock.Rehydrate(
            blockId, taskId: null, title: "Synced elsewhere", Tuesday,
            new TimeOnly(15, 0), new TimeOnly(16, 0), BlockKind.ExternalEvent,
            recurrence: null, "google", "evt-1", syncState: 0,
            BlockOutcome.None, outcomeRecordedAt: null, _clock.Now, _clock.Now));

        Assert.Throws<DomainException>(
            () => CreatePlanning().UndoApproval(draft.Id, [blockId]));

        // The foreign block was not deleted and the approval status is untouched.
        var survivor = new SqliteCalendarBlockRepository(_database.Factory).GetById(blockId);
        Assert.NotNull(survivor);
        Assert.Equal(BlockKind.ExternalEvent, survivor.Kind);
        Assert.Equal(
            ProposedBlockStatus.Approved,
            new SqlitePlanningProposalRepository(_database.Factory)
                .GetById(draft.Id)!.GetBlock(blockId).Status);
    }

    [Fact]
    public void UndoApproval_WhenTheBlockBelongsToADifferentTask_IsRejected()
    {
        var task = AddTask("Essay outline");
        var stranger = AddTask("Someone else's work");
        var draft = SaveDraft(task.Id);
        CreatePlanning().ApproveAll(draft.Id);
        var blockId = draft.Blocks.Single().Id;
        var physical = _blocks.GetById(blockId)!;
        // Corruption: the same id is now another Task's session.
        _blocks.Delete(blockId);
        _blocks.Add(CalendarBlock.Rehydrate(
            blockId, stranger.Id, title: null, physical.Date,
            physical.StartTime, physical.EndTime, BlockKind.TaskSession,
            recurrence: null, CalendarBlock.LocalProvider, externalId: null, syncState: 0,
            BlockOutcome.None, outcomeRecordedAt: null, _clock.Now, _clock.Now));

        Assert.Throws<DomainException>(
            () => CreatePlanning().UndoApproval(draft.Id, [blockId]));

        var survivor = new SqliteCalendarBlockRepository(_database.Factory).GetById(blockId);
        Assert.NotNull(survivor);
        Assert.Equal(stranger.Id, survivor.TaskId);
        Assert.Equal(
            ProposedBlockStatus.Approved,
            new SqlitePlanningProposalRepository(_database.Factory)
                .GetById(draft.Id)!.GetBlock(blockId).Status);
    }

    public void Dispose() => _database.Dispose();
}
