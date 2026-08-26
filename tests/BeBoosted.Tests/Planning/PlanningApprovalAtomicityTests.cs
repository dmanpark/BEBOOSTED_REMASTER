using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Prioritization;
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
/// Approval and Undo are single transactions: the calendar block writes and the
/// proposal save commit together or not at all. Failures injected at any write
/// position (a block insert or delete, or the proposal save) leave a clean state
/// that survives reopening the database, and a plain retry then succeeds — no
/// surviving block may collide with a retried approval's id.
/// </summary>
public sealed class PlanningApprovalAtomicityTests : IDisposable
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

    public PlanningApprovalAtomicityTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _proposals = new SqlitePlanningProposalRepository(_database.Factory);
    }

    /// <summary>
    /// A PlanningService whose failure seams cover both worlds: the directly
    /// injected repositories (the legacy independent-commit path) and the
    /// transaction-bound repositories inside the shared unit of work.
    /// </summary>
    private PlanningService CreatePlanning(
        IPlanningProposalRepository? proposals = null,
        ICalendarBlockRepository? blocks = null,
        ICalendarMutations? mutations = null)
    {
        var blockRepo = blocks ?? _blocks;
        var calendar = new CalendarService(
            blockRepo, new SqliteOccurrenceCompletionRepository(_database.Factory),
            new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
        return new PlanningService(
            proposals ?? _proposals, new InboxQueryService(_tasks, blockRepo),
            new SqlitePrioritizationRepository(_database.Factory), calendar,
            mutations ?? new SqliteCalendarMutations(_database.Factory), _clock);
    }

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

    private void AssertCleanPendingDraft(PlanningProposalId proposalId, int blockCount)
    {
        // Verified through fresh repositories — the state after reopening the app.
        Assert.Empty(new SqliteCalendarBlockRepository(_database.Factory).GetAll());
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(proposalId)!;
        Assert.Equal(ProposalState.Draft, reloaded.State);
        Assert.Equal(blockCount, reloaded.Blocks.Count);
        Assert.All(reloaded.Blocks, b => Assert.Equal(ProposedBlockStatus.Pending, b.Status));
        Assert.NotNull(new SqlitePlanningProposalRepository(_database.Factory).GetActiveDraft());
    }

    [Fact]
    public void ApproveBlock_FailingProposalSave_RollsBackTheCalendarBlock()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var blockId = draft.Blocks.Single().Id;
        var service = CreatePlanning(
            proposals: new FailingSaveProposals(_proposals),
            mutations: new FailingProposalSaveMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.ApproveBlock(draft.Id, blockId));

        AssertCleanPendingDraft(draft.Id, blockCount: 1);

        // A plain retry succeeds — nothing half-written collides with the block id.
        var created = CreatePlanning().ApproveBlock(draft.Id, blockId);
        Assert.Equal(blockId, created);
        Assert.NotNull(_blocks.GetById(blockId));
        Assert.Equal(
            ProposedBlockStatus.Approved,
            new SqlitePlanningProposalRepository(_database.Factory)
                .GetById(draft.Id)!.GetBlock(blockId).Status);
    }

    [Fact]
    public void ApproveAll_FailingSecondBlockInsert_RollsBackTheWholeBatch()
    {
        var first = AddTask("Essay outline");
        var second = AddTask("Vocab review");
        var draft = SaveDraft(first.Id, second.Id);
        var service = CreatePlanning(
            blocks: new NthFailingBlockAdds(_blocks, failOnAdd: 2),
            mutations: new NthFailingBlockAddMutations(_database.Factory, failOnAdd: 2));

        Assert.Throws<InvalidOperationException>(() => service.ApproveAll(draft.Id));

        AssertCleanPendingDraft(draft.Id, blockCount: 2);

        var created = CreatePlanning().ApproveAll(draft.Id);
        Assert.Equal(2, created.Count);
        Assert.Equal(2, _blocks.GetAll().Count);
    }

    [Fact]
    public void ApproveAll_FailingFinalProposalSave_RollsBackEveryBlock()
    {
        var first = AddTask("Essay outline");
        var second = AddTask("Vocab review");
        var draft = SaveDraft(first.Id, second.Id);
        var service = CreatePlanning(
            proposals: new FailingSaveProposals(_proposals),
            mutations: new FailingProposalSaveMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.ApproveAll(draft.Id));

        AssertCleanPendingDraft(draft.Id, blockCount: 2);

        var created = CreatePlanning().ApproveAll(draft.Id);
        Assert.Equal(2, created.Count);
        Assert.Equal(2, _blocks.GetAll().Count);
    }

    [Fact]
    public void UndoApproval_FailingSecondBlockDelete_RestoresEverything()
    {
        var first = AddTask("Essay outline");
        var second = AddTask("Vocab review");
        var draft = SaveDraft(first.Id, second.Id);
        var approved = CreatePlanning().ApproveAll(draft.Id);
        var service = CreatePlanning(
            blocks: new NthFailingBlockDeletes(_blocks, failOnDelete: 2),
            mutations: new NthFailingBlockDeleteMutations(_database.Factory, failOnDelete: 2));

        Assert.Throws<InvalidOperationException>(() => service.UndoApproval(draft.Id, approved));

        // Both calendar blocks and both approved statuses survive the failed undo.
        Assert.Equal(2, new SqliteCalendarBlockRepository(_database.Factory).GetAll().Count);
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.All(reloaded.Blocks, b => Assert.Equal(ProposedBlockStatus.Approved, b.Status));

        // A plain retry undoes the whole approval.
        CreatePlanning().UndoApproval(draft.Id, approved);
        AssertCleanPendingDraft(draft.Id, blockCount: 2);
    }

    [Fact]
    public void UndoApproval_FailingProposalSave_RestoresBlocksAndStatuses()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var approved = CreatePlanning().ApproveAll(draft.Id);
        var service = CreatePlanning(
            proposals: new FailingSaveProposals(_proposals),
            mutations: new FailingProposalSaveMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.UndoApproval(draft.Id, approved));

        Assert.Single(new SqliteCalendarBlockRepository(_database.Factory).GetAll());
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.Equal(ProposedBlockStatus.Approved, Assert.Single(reloaded.Blocks).Status);

        CreatePlanning().UndoApproval(draft.Id, approved);
        AssertCleanPendingDraft(draft.Id, blockCount: 1);
    }

    // ---- Failure seams: directly injected repositories ----

    private sealed class FailingSaveProposals(IPlanningProposalRepository inner)
        : IPlanningProposalRepository
    {
        public void Save(PlanningProposal proposal)
            => throw new InvalidOperationException("injected failure");

        public PlanningProposal? GetById(PlanningProposalId id) => inner.GetById(id);

        public PlanningProposal? GetActiveDraft() => inner.GetActiveDraft();

        public IReadOnlyList<PlanningProposal> GetAll() => inner.GetAll();

        public void Delete(PlanningProposalId id) => inner.Delete(id);
    }

    private class NthFailingBlockWrites(ICalendarBlockRepository inner, int failOnAdd, int failOnDelete)
        : ICalendarBlockRepository
    {
        private int _adds;
        private int _deletes;

        public void Add(CalendarBlock block)
        {
            if (++_adds == failOnAdd)
            {
                throw new InvalidOperationException("injected failure");
            }

            inner.Add(block);
        }

        public void Update(CalendarBlock block) => inner.Update(block);

        public void Delete(CalendarBlockId id)
        {
            if (++_deletes == failOnDelete)
            {
                throw new InvalidOperationException("injected failure");
            }

            inner.Delete(id);
        }

        public CalendarBlock? GetById(CalendarBlockId id) => inner.GetById(id);

        public IReadOnlyList<CalendarBlock> GetAll() => inner.GetAll();

        public IReadOnlyList<CalendarBlock> GetCandidatesBetween(DateOnly from, DateOnly to)
            => inner.GetCandidatesBetween(from, to);

        public IReadOnlyList<CalendarBlock> GetForTask(TaskId taskId) => inner.GetForTask(taskId);

        public IReadOnlyList<CalendarBlock> GetElapsedWithoutOutcome(DateOnly today, TimeOnly now)
            => inner.GetElapsedWithoutOutcome(today, now);

        public IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks()
            => inner.GetTaskIdsWithPendingBlocks();
    }

    private sealed class NthFailingBlockAdds(ICalendarBlockRepository inner, int failOnAdd)
        : NthFailingBlockWrites(inner, failOnAdd, failOnDelete: 0);

    private sealed class NthFailingBlockDeletes(ICalendarBlockRepository inner, int failOnDelete)
        : NthFailingBlockWrites(inner, failOnAdd: 0, failOnDelete);

    // ---- Failure seams: the shared unit of work ----

    private sealed class FailingProposalSaveMutations(SqliteConnectionFactory factory)
        : ICalendarMutations
    {
        public void Execute(
            Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
                IPlanningProposalRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new SqliteCalendarBlockRepository(connection, transaction),
                new SqliteOccurrenceCompletionRepository(connection, transaction),
                new SqliteTaskRepository(connection, transaction),
                new FailingSaveProposals(new SqlitePlanningProposalRepository(connection, transaction)));
            transaction.Commit();
        }
    }

    private sealed class NthFailingBlockAddMutations(SqliteConnectionFactory factory, int failOnAdd)
        : ICalendarMutations
    {
        public void Execute(
            Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
                IPlanningProposalRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new NthFailingBlockAdds(
                    new SqliteCalendarBlockRepository(connection, transaction), failOnAdd),
                new SqliteOccurrenceCompletionRepository(connection, transaction),
                new SqliteTaskRepository(connection, transaction),
                new SqlitePlanningProposalRepository(connection, transaction));
            transaction.Commit();
        }
    }

    private sealed class NthFailingBlockDeleteMutations(SqliteConnectionFactory factory, int failOnDelete)
        : ICalendarMutations
    {
        public void Execute(
            Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
                IPlanningProposalRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new NthFailingBlockDeletes(
                    new SqliteCalendarBlockRepository(connection, transaction), failOnDelete),
                new SqliteOccurrenceCompletionRepository(connection, transaction),
                new SqliteTaskRepository(connection, transaction),
                new SqlitePlanningProposalRepository(connection, transaction));
            transaction.Commit();
        }
    }

    public void Dispose() => _database.Dispose();
}
