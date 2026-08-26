using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
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
/// Removing the last pending block settles the plan instead of leaving an empty
/// active draft behind: approved work makes it Approved, anything else Discarded.
/// Reopening the database must never reveal an empty active draft, and approving
/// such a settled plan is rejected rather than silently doing nothing.
/// </summary>
public sealed class EmptyDraftNormalizationTests : IDisposable
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

    public EmptyDraftNormalizationTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _proposals = new SqlitePlanningProposalRepository(_database.Factory);
    }

    private PlanningService CreatePlanning() => new(
        _proposals, new InboxQueryService(_tasks, _blocks),
        new SqlitePrioritizationRepository(_database.Factory),
        new CalendarService(
            _blocks, new SqliteOccurrenceCompletionRepository(_database.Factory),
            new SqliteCalendarMutations(_database.Factory), _tasks, _clock),
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

    /// <summary>Reads through fresh repositories — the state after reopening the app.</summary>
    private SqlitePlanningProposalRepository Restarted()
        => new(_database.Factory);

    [Fact]
    public void RemovingTheOnlyPendingBlock_LeavesNoActiveDraft_AfterRestart()
    {
        var task = AddTask("Essay outline");
        var draft = SaveDraft(task.Id);
        var blockId = draft.Blocks.Single().Id;

        CreatePlanning().RemoveBlock(draft.Id, blockId);

        var restarted = Restarted();
        Assert.Null(restarted.GetActiveDraft());
        Assert.Equal(ProposalState.Discarded, restarted.GetById(draft.Id)!.State);
        // Removal never touches the calendar.
        Assert.Empty(new SqliteCalendarBlockRepository(_database.Factory).GetAll());
    }

    [Fact]
    public void RemovingTheFinalPendingBlock_AfterAnApproval_SettlesTheProposalApproved()
    {
        var first = AddTask("Essay outline");
        var second = AddTask("Vocab review");
        var draft = SaveDraft(first.Id, second.Id);
        var approvedId = draft.Blocks[0].Id;
        var removedId = draft.Blocks[1].Id;
        CreatePlanning().ApproveBlock(draft.Id, approvedId);

        CreatePlanning().RemoveBlock(draft.Id, removedId);

        var restarted = Restarted();
        Assert.Null(restarted.GetActiveDraft());
        var reloaded = restarted.GetById(draft.Id)!;
        Assert.Equal(ProposalState.Approved, reloaded.State);
        Assert.Equal(ProposedBlockStatus.Approved, reloaded.GetBlock(approvedId).Status);
        Assert.Equal(ProposedBlockStatus.Removed, reloaded.GetBlock(removedId).Status);
        // The approved block itself is untouched by the removal.
        Assert.NotNull(new SqliteCalendarBlockRepository(_database.Factory).GetById(approvedId));
    }

    [Fact]
    public void ApproveAll_OnAPlanEmptiedByRemoval_IsRejectedInsteadOfSilentlyDoingNothing()
    {
        var first = AddTask("Essay outline");
        var second = AddTask("Vocab review");
        var draft = SaveDraft(first.Id, second.Id);
        CreatePlanning().ApproveBlock(draft.Id, draft.Blocks[0].Id);
        CreatePlanning().RemoveBlock(draft.Id, draft.Blocks[1].Id);

        Assert.Throws<DomainException>(() => CreatePlanning().ApproveAll(draft.Id));

        // Exactly the one earlier approval survives — nothing was materialized.
        Assert.Single(new SqliteCalendarBlockRepository(_database.Factory).GetAll());
        Assert.Equal(ProposalState.Approved, Restarted().GetById(draft.Id)!.State);
    }

    public void Dispose() => _database.Dispose();
}
