using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;
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

public sealed class PlanningServiceTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private static readonly PlanningPeriod Week = PlanningPeriod.ForWeek(new DateOnly(2026, 8, 11));

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqlitePlanningProposalRepository _proposals;
    private readonly PlanningService _service;
    private readonly InboxQueryService _inbox;

    public PlanningServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _proposals = new SqlitePlanningProposalRepository(_database.Factory);
        _inbox = new InboxQueryService(_tasks, _blocks);
        var calendar = new CalendarService(_blocks, new SqliteOccurrenceCompletionRepository(_database.Factory), new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
        _service = new PlanningService(
            _proposals, _inbox,
            new SqlitePrioritizationRepository(_database.Factory), calendar,
            new SqliteCalendarMutations(_database.Factory), _clock);
    }

    private TaskItem AddTask(string title, int minutes, DateOnly? deadline = null)
    {
        var task = TaskItem.Create(
            title, _clock.Now, estimatedDuration: TimeSpan.FromMinutes(minutes), deadline: deadline);
        _tasks.Add(task);
        return task;
    }

    [Fact]
    public void CreateDraft_PersistsProposalWithEvidence_AndSurvivesReload()
    {
        AddTask("Finish DECA presentation", 90, new DateOnly(2026, 8, 14));
        AddTask("Draft essay outline", 60, new DateOnly(2026, 8, 16));

        var result = _service.CreateDraft(Week);

        Assert.Equal(2, result.Proposal.Blocks.Count);
        Assert.Empty(result.Unplaced);

        var reloaded = _proposals.GetActiveDraft();
        Assert.NotNull(reloaded);
        Assert.Equal(result.Proposal.Id, reloaded.Id);
        Assert.Equal(Week, reloaded.Period);
        var block = reloaded.Blocks[0];
        Assert.Equal("90 min", block.Why.Duration);
        Assert.NotNull(block.Why.Deadline);
        Assert.Equal("First uninterrupted open block", block.Why.Availability);
        Assert.Equal(ProposedBlockStatus.Pending, block.Status);
    }

    [Fact]
    public void CreateDraft_ReplacesThePreviousDraft()
    {
        AddTask("A", 30);
        var first = _service.CreateDraft(Week).Proposal;
        var second = _service.CreateDraft(Week).Proposal;

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, _proposals.GetActiveDraft()!.Id);
        Assert.Equal(ProposalState.Discarded, _proposals.GetById(first.Id)!.State);
    }

    [Fact]
    public void MoveResizeAndRemove_PersistAcrossReload()
    {
        AddTask("A", 60);
        AddTask("B", 30);
        var draft = _service.CreateDraft(Week).Proposal;
        var target = draft.Blocks[0];
        var other = draft.Blocks[1];

        _service.MoveBlock(draft.Id, target.Id, new DateOnly(2026, 8, 13), new TimeOnly(9, 0));
        _service.ResizeBlock(draft.Id, target.Id, new TimeOnly(10, 30));
        _service.RemoveBlock(draft.Id, other.Id);

        var reloaded = _proposals.GetById(draft.Id)!;
        var moved = reloaded.GetBlock(target.Id);
        Assert.Equal(new DateOnly(2026, 8, 13), moved.Date);
        Assert.Equal(new TimeOnly(9, 0), moved.StartTime);
        Assert.Equal(new TimeOnly(10, 30), moved.EndTime);
        Assert.Equal(ProposedBlockStatus.Removed, reloaded.GetBlock(other.Id).Status);
    }

    [Fact]
    public void ApproveBlock_MaterializesACalendarBlockWithTheSameId()
    {
        var task = AddTask("A", 60);
        var draft = _service.CreateDraft(Week).Proposal;
        var proposed = draft.Blocks.Single();

        var createdId = _service.ApproveBlock(draft.Id, proposed.Id);

        Assert.Equal(proposed.Id, createdId);
        var calendarBlock = _blocks.GetById(createdId);
        Assert.NotNull(calendarBlock);
        Assert.Equal(task.Id, calendarBlock.TaskId);
        Assert.Equal(proposed.Date, calendarBlock.Date);
        Assert.Equal(proposed.StartTime, calendarBlock.StartTime);
        Assert.Equal(BlockKind.TaskSession, calendarBlock.Kind);

        // Fully-approved drafts flip to Approved and stop being the active draft.
        Assert.Equal(ProposalState.Approved, _proposals.GetById(draft.Id)!.State);
        Assert.Null(_proposals.GetActiveDraft());
    }

    [Fact]
    public void ApproveAll_ApprovesEveryPendingBlock_AndEmptiesTheInboxQueue()
    {
        AddTask("A", 60);
        AddTask("B", 30);
        var draft = _service.CreateDraft(Week).Proposal;

        var created = _service.ApproveAll(draft.Id);

        Assert.Equal(2, created.Count);
        Assert.Empty(_inbox.GetInboxTasks());
        Assert.All(created, id => Assert.NotNull(_blocks.GetById(id)));
    }

    [Fact]
    public void UndoApproval_RemovesBlocksAndRevertsTheDraft()
    {
        AddTask("A", 60);
        var draft = _service.CreateDraft(Week).Proposal;
        var created = _service.ApproveAll(draft.Id);

        _service.UndoApproval(draft.Id, created);

        Assert.All(created, id => Assert.Null(_blocks.GetById(id)));
        var reverted = _proposals.GetById(draft.Id)!;
        Assert.Equal(ProposalState.Draft, reverted.State);
        Assert.All(reverted.Blocks, b => Assert.Equal(ProposedBlockStatus.Pending, b.Status));
        Assert.Single(_inbox.GetInboxTasks());
    }

    [Fact]
    public void DiscardDraft_LeavesNoActiveDraft()
    {
        AddTask("A", 60);
        var draft = _service.CreateDraft(Week).Proposal;

        _service.DiscardDraft(draft.Id);

        Assert.Null(_proposals.GetActiveDraft());
        Assert.Equal(ProposalState.Discarded, _proposals.GetById(draft.Id)!.State);
    }

    [Fact]
    public void CreateDraft_ReportsUnplacedTasks()
    {
        // A scheduled task fills the whole planning window today; deadline today → unplaceable.
        var calendar = new CalendarService(_blocks, new SqliteOccurrenceCompletionRepository(_database.Factory), new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
        calendar.CreateTask(
            new TaskDetailsRequest("All-day", null, null, null),
            new TaskScheduleRequest(
                new DateOnly(2026, 8, 11), new TimeOnly(8, 0), new TimeOnly(21, 0), null));
        AddTask("Urgent today", 45, new DateOnly(2026, 8, 11));

        var result = _service.CreateDraft(Week);

        Assert.Empty(result.Proposal.Blocks);
        var unplaced = Assert.Single(result.Unplaced);
        Assert.Equal("Urgent today", unplaced.Title);
    }

    /// <summary>
    /// A plan that places nothing writes nothing: no invisible zero-block draft may
    /// survive (it would block approval-undo and hide the Discard remedy forever).
    /// </summary>
    [Fact]
    public void CreateDraft_WithNothingPlaceable_LeavesNoActiveDraft()
    {
        var calendar = new CalendarService(_blocks, new SqliteOccurrenceCompletionRepository(_database.Factory), new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
        calendar.CreateTask(
            new TaskDetailsRequest("All-day", null, null, null),
            new TaskScheduleRequest(
                new DateOnly(2026, 8, 11), new TimeOnly(8, 0), new TimeOnly(21, 0), null));
        AddTask("Urgent today", 45, new DateOnly(2026, 8, 11));

        var result = _service.CreateDraft(Week);

        Assert.Empty(result.Proposal.Blocks);
        Assert.Null(_proposals.GetActiveDraft());
        // The state after reopening the app agrees.
        Assert.Null(new SqlitePlanningProposalRepository(_database.Factory).GetActiveDraft());
    }

    /// <summary>An empty result must not destroy the draft the user can see.</summary>
    [Fact]
    public void CreateDraft_WithNothingPlaceable_PreservesThePreviousActiveDraft()
    {
        AddTask("Urgent today", 45, new DateOnly(2026, 8, 11));
        var good = _service.CreateDraft(Week).Proposal;
        Assert.NotEmpty(good.Blocks);

        // The calendar fills up after that draft was proposed; a re-plan now fits nothing.
        var calendar = new CalendarService(_blocks, new SqliteOccurrenceCompletionRepository(_database.Factory), new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
        calendar.CreateTask(
            new TaskDetailsRequest("All-day", null, null, null),
            new TaskScheduleRequest(
                new DateOnly(2026, 8, 11), new TimeOnly(8, 0), new TimeOnly(21, 0), null));

        var result = _service.CreateDraft(Week);

        Assert.Empty(result.Proposal.Blocks);
        Assert.Single(result.Unplaced);
        Assert.Equal(good.Id, _proposals.GetActiveDraft()!.Id);
    }

    public void Dispose() => _database.Dispose();
}
