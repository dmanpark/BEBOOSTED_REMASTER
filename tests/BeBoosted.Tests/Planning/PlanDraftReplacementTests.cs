using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
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
/// Replacing a plan draft is one transaction: the old draft's discard and the new
/// draft's save commit together. A failure while planning or while saving the
/// replacement leaves the previous draft as the unchanged active draft — never a
/// window with no draft at all. Discarding likewise re-reads live state and only
/// ever discards an active draft.
/// </summary>
public sealed class PlanDraftReplacementTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private static readonly PlanningPeriod Period = PlanningPeriod.ForToday(new DateOnly(2026, 8, 11));

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqlitePlanningProposalRepository _proposals;

    public PlanDraftReplacementTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _proposals = new SqlitePlanningProposalRepository(_database.Factory);
    }

    /// <summary>
    /// Failure seams cover both worlds: the directly injected repositories (the
    /// legacy independent-commit path) and the transaction-bound repositories
    /// inside the shared unit of work.
    /// </summary>
    private PlanningService CreatePlanning(
        IPlanningProposalRepository? proposals = null,
        IPrioritizationRepository? prioritization = null,
        ICalendarMutations? mutations = null)
    {
        var calendar = new CalendarService(
            _blocks, new SqliteOccurrenceCompletionRepository(_database.Factory),
            new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
        return new PlanningService(
            proposals ?? _proposals, new InboxQueryService(_tasks, _blocks),
            prioritization ?? new SqlitePrioritizationRepository(_database.Factory), calendar,
            mutations ?? new SqliteCalendarMutations(_database.Factory), _clock);
    }

    private void AddTask(string title)
        => _tasks.Add(TaskItem.Create(
            title, _clock.Now, estimatedDuration: TimeSpan.FromMinutes(30)));

    /// <summary>The old draft survives a failed replacement — restart-verified.</summary>
    private void AssertOldDraftStillActive(PlanningProposalId oldId, int blockCount)
    {
        var fresh = new SqlitePlanningProposalRepository(_database.Factory);
        var active = fresh.GetActiveDraft();
        Assert.NotNull(active);
        Assert.Equal(oldId, active.Id);
        Assert.Equal(ProposalState.Draft, active.State);
        Assert.Equal(blockCount, active.Blocks.Count);
        Assert.All(active.Blocks, b => Assert.Equal(ProposedBlockStatus.Pending, b.Status));
        Assert.Single(fresh.GetAll()); // no half-written replacement row either
    }

    [Fact]
    public void CreateDraft_FailingWhilePlanning_LeavesTheOldDraftActive()
    {
        AddTask("Essay outline");
        var old = CreatePlanning().CreateDraft(Period).Proposal;
        var service = CreatePlanning(prioritization: new FailingRanks());

        Assert.Throws<InvalidOperationException>(() => service.CreateDraft(Period));

        AssertOldDraftStillActive(old.Id, blockCount: 1);
    }

    [Fact]
    public void CreateDraft_FailingReplacementSave_RollsBackTheOldDraftsDiscard()
    {
        AddTask("Essay outline");
        var old = CreatePlanning().CreateDraft(Period).Proposal;
        var service = CreatePlanning(
            proposals: new NthFailingSaveProposals(_proposals, failOnSave: 2),
            mutations: new NthFailingProposalSaveMutations(_database.Factory, failOnSave: 2));

        Assert.Throws<InvalidOperationException>(() => service.CreateDraft(Period));

        AssertOldDraftStillActive(old.Id, blockCount: 1);

        // A plain retry replaces the draft normally.
        var replacement = CreatePlanning().CreateDraft(Period).Proposal;
        var fresh = new SqlitePlanningProposalRepository(_database.Factory);
        Assert.Equal(replacement.Id, fresh.GetActiveDraft()!.Id);
        Assert.Equal(ProposalState.Discarded, fresh.GetById(old.Id)!.State);
    }

    [Fact]
    public void DiscardDraft_OfANonActiveProposal_IsRejected()
    {
        AddTask("Essay outline");
        var draft = CreatePlanning().CreateDraft(Period).Proposal;
        CreatePlanning().ApproveAll(draft.Id); // the draft is now fully Approved

        Assert.Throws<DomainException>(() => CreatePlanning().DiscardDraft(draft.Id));

        // The approved proposal keeps its state — never clobbered to Discarded.
        Assert.Equal(
            ProposalState.Approved,
            new SqlitePlanningProposalRepository(_database.Factory).GetById(draft.Id)!.State);
    }

    // ---- Failure seams ----

    private sealed class FailingRanks : IPrioritizationRepository
    {
        public void SaveSessionResult(
            string periodKey,
            IReadOnlyList<ComparisonDecision> decisions,
            IReadOnlyList<PriorityRank> ranks)
        {
        }

        public void SaveDecisions(IReadOnlyList<ComparisonDecision> decisions)
        {
        }

        public IReadOnlyList<ComparisonDecision> GetDecisions(string periodKey) => [];

        public void ReplaceRanks(string periodKey, IReadOnlyList<PriorityRank> ranks)
        {
        }

        public IReadOnlyList<PriorityRank> GetRanks(string periodKey)
            => throw new InvalidOperationException("injected failure");
    }

    private sealed class NthFailingSaveProposals(IPlanningProposalRepository inner, int failOnSave)
        : IPlanningProposalRepository
    {
        private int _saves;

        public void Save(PlanningProposal proposal)
        {
            if (++_saves == failOnSave)
            {
                throw new InvalidOperationException("injected failure");
            }

            inner.Save(proposal);
        }

        public PlanningProposal? GetById(PlanningProposalId id) => inner.GetById(id);

        public PlanningProposal? GetActiveDraft() => inner.GetActiveDraft();

        public IReadOnlyList<PlanningProposal> GetAll() => inner.GetAll();

        public void Delete(PlanningProposalId id) => inner.Delete(id);
    }

    private sealed class NthFailingProposalSaveMutations(SqliteConnectionFactory factory, int failOnSave)
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
                new NthFailingSaveProposals(
                    new SqlitePlanningProposalRepository(connection, transaction), failOnSave));
            transaction.Commit();
        }
    }

    public void Dispose() => _database.Dispose();
}
