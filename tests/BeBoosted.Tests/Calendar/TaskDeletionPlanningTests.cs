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

namespace BeBoosted.Tests.Calendar;

/// <summary>
/// Deleting a Task reconciles planning proposals in the same transaction: its
/// proposal blocks are withdrawn, drafts left empty are normalized, and approval
/// validates every Task reference before the first write — a legacy orphan can
/// never partially approve a batch or crash through the calendar foreign key.
/// </summary>
public sealed class TaskDeletionPlanningTests : IDisposable
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
    private readonly MigrationRunner _runner;
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqlitePlanningProposalRepository _proposals;

    public TaskDeletionPlanningTests()
    {
        _runner = new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance);
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _proposals = new SqlitePlanningProposalRepository(_database.Factory);
    }

    private void ApplyAllMigrations() => _runner.Apply(EmbeddedMigrations.Load());

    /// <summary>A database as shipped before the proposal-integrity backstop.</summary>
    private void ApplyLegacyMigrations()
        => _runner.Apply(EmbeddedMigrations.Load().Where(m => m.Version <= 10).ToList());

    private CalendarService CreateService(ICalendarMutations? mutations = null)
        => new(
            _blocks, new SqliteOccurrenceCompletionRepository(_database.Factory),
            mutations ?? new SqliteCalendarMutations(_database.Factory), _tasks, _clock);

    private PlanningService CreatePlanning(CalendarService calendar)
        => new(
            _proposals, new InboxQueryService(_tasks, _blocks),
            new SqlitePrioritizationRepository(_database.Factory), calendar,
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

    private PlanningProposal SaveDraft(params ProposedBlock[] blocks)
    {
        var proposal = PlanningProposal.CreateDraft(
            PlanningPeriod.ForToday(Tuesday), blocks, _clock.Now);
        _proposals.Save(proposal);
        return proposal;
    }

    // ---- Phase 1: deletion withdraws proposal blocks in the same transaction ----

    [Fact]
    public void DeleteTask_WithdrawsItsProposalBlocks_AndNormalizesEmptyDrafts_AcrossRestart()
    {
        ApplyAllMigrations();
        var essay = AddTask("Essay outline");
        var vocab = AddTask("Vocab review");
        var draft = SaveDraft(Propose(essay.Id, 15), Propose(vocab.Id, 16));
        var service = CreateService();

        service.DeleteTask(essay.Id);

        // Only the surviving task's block remains — verified through a fresh repo.
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        var survivor = Assert.Single(reloaded.Blocks);
        Assert.Equal(vocab.Id, survivor.TaskId);
        Assert.Equal(ProposalState.Draft, reloaded.State);

        // Deleting the last planned task leaves no empty active draft behind.
        service.DeleteTask(vocab.Id);
        var restarted = new SqlitePlanningProposalRepository(_database.Factory);
        Assert.Null(restarted.GetActiveDraft());
        Assert.Empty(restarted.GetById(draft.Id)!.Blocks);
    }

    [Fact]
    public void DeleteTask_FailingTaskDelete_RollsBackTheProposalReconciliationToo()
    {
        ApplyAllMigrations();
        var essay = AddTask("Essay outline");
        var session = CalendarBlock.CreateTaskSession(
            essay.Id, Tuesday, new TimeOnly(9, 0), new TimeOnly(10, 0), _clock.Now);
        _blocks.Add(session);
        var draft = SaveDraft(Propose(essay.Id, 15));
        var service = CreateService(new FailingTaskDeleteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteTask(essay.Id));

        // Task, session, and the proposal block all survive together.
        Assert.NotNull(_tasks.GetById(essay.Id));
        Assert.NotNull(_blocks.GetById(session.Id));
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.Equal(essay.Id, Assert.Single(reloaded.Blocks).TaskId);
        Assert.Equal(ProposalState.Draft, reloaded.State);
    }

    // ---- Phase 3: approval validates every Task reference before the first write ----

    /// <summary>A legacy (pre-backstop) database holding an orphan proposal block.</summary>
    private (PlanningProposal Draft, TaskItem Valid, CalendarBlockId OrphanBlockId) BuildLegacyOrphan()
    {
        ApplyLegacyMigrations();
        var valid = AddTask("Essay outline");
        var ghost = AddTask("Ghost");
        var validBlock = Propose(valid.Id, 15);
        var orphanBlock = Propose(ghost.Id, 16);
        var draft = SaveDraft(validBlock, orphanBlock);
        _tasks.Delete(ghost.Id); // legacy schema: no FK stops the orphan
        return (draft, valid, orphanBlock.Id);
    }

    [Fact]
    public void ApproveBlock_WhoseTaskWasDeleted_IsRejected_BeforeAnyWrite()
    {
        var (draft, _, orphanBlockId) = BuildLegacyOrphan();
        var planning = CreatePlanning(CreateService());

        Assert.Throws<DomainException>(() => planning.ApproveBlock(draft.Id, orphanBlockId));

        Assert.Empty(_blocks.GetAll());
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.All(reloaded.Blocks, b => Assert.Equal(ProposedBlockStatus.Pending, b.Status));
    }

    [Fact]
    public void ApproveAll_WithAnOrphanInTheBatch_ApprovesNothing()
    {
        var (draft, _, _) = BuildLegacyOrphan();
        var planning = CreatePlanning(CreateService());

        Assert.Throws<DomainException>(() => planning.ApproveAll(draft.Id));

        // Never a partial approval: no calendar block, no status change — and the
        // rejection is just as clean after reopening the database.
        Assert.Empty(_blocks.GetAll());
        Assert.Empty(new SqliteCalendarBlockRepository(_database.Factory).GetAll());
        var reloaded = new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!;
        Assert.All(reloaded.Blocks, b => Assert.Equal(ProposedBlockStatus.Pending, b.Status));
        Assert.Equal(ProposalState.Draft, reloaded.State);
    }

    /// <summary>A real single-connection transaction whose task Delete fails.</summary>
    private sealed class FailingTaskDeleteMutations(SqliteConnectionFactory factory) : ICalendarMutations
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
                new FailingTaskDeletes(new SqliteTaskRepository(connection, transaction)),
                new SqlitePlanningProposalRepository(connection, transaction));
            transaction.Commit();
        }
    }

    private sealed class FailingTaskDeletes(ITaskRepository inner) : ITaskRepository
    {
        public void Add(TaskItem task) => inner.Add(task);

        public void Update(TaskItem task) => inner.Update(task);

        public void Delete(TaskId id) => throw new InvalidOperationException("injected failure");

        public TaskItem? GetById(TaskId id) => inner.GetById(id);

        public IReadOnlyList<TaskItem> GetAll() => inner.GetAll();

        public IReadOnlyList<TaskItem> GetOpen() => inner.GetOpen();
    }

    public void Dispose() => _database.Dispose();
}
