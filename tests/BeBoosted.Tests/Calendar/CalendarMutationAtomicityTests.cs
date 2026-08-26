using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Planning;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Calendar;

/// <summary>
/// Task, session, and completion writes must share one real SQLite transaction: any
/// failure mid-mutation leaves the persisted task, block, and every completion row
/// exactly as they were.
/// </summary>
public sealed class CalendarMutationAtomicityTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private static readonly DateOnly Date = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqliteOccurrenceCompletionRepository _completions;

    public CalendarMutationAtomicityTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
    }

    /// <summary>A repeating Tuesday session with this Tuesday's occurrence checked off.</summary>
    private (TaskItem Task, CalendarBlock Session) AddRepeatingTaskWithCompletion()
    {
        var task = TaskItem.Create("Stats HW", _clock.Now);
        _tasks.Add(task);
        var session = CalendarBlock.CreateTaskSession(
            task.Id, Date.AddDays(-7), new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(session);
        _completions.Add(new OccurrenceCompletion(session.Id, Date, _clock.Now));
        return (task, session);
    }

    [Fact]
    public void SqliteCalendarMutations_CommitsEverything_OnSuccess()
    {
        var (task, session) = AddRepeatingTaskWithCompletion();
        var mutations = new SqliteCalendarMutations(_database.Factory);

        mutations.Execute((blocks, completions, tasks, _) =>
        {
            task.Rename("Stats homework", _clock.Now);
            tasks.Update(task);
            session.Reschedule(session.Date, new TimeOnly(15, 0), new TimeOnly(16, 0), _clock.Now);
            blocks.Update(session);
            completions.Remove(session.Id, Date);
            completions.Add(new OccurrenceCompletion(session.Id, Date, _clock.Now.AddHours(1)));
        });

        Assert.Equal("Stats homework", _tasks.GetById(task.Id)!.Title);
        Assert.Equal(new TimeOnly(15, 0), _blocks.GetById(session.Id)!.StartTime);
        Assert.Equal(_clock.Now.AddHours(1), _completions.Get(session.Id, Date)!.CompletedAt);
    }

    [Fact]
    public void SqliteCalendarMutations_RollsBackEverything_WhenTheMutationThrows()
    {
        var (task, session) = AddRepeatingTaskWithCompletion();
        var mutations = new SqliteCalendarMutations(_database.Factory);

        Assert.Throws<InvalidOperationException>(() => mutations.Execute((blocks, completions, tasks, _) =>
        {
            task.Rename("Stats homework", _clock.Now);
            tasks.Update(task);
            session.Reschedule(session.Date, new TimeOnly(15, 0), new TimeOnly(16, 0), _clock.Now);
            blocks.Update(session);
            completions.Remove(session.Id, Date);
            throw new InvalidOperationException("injected failure");
        }));

        // Every write inside the transaction is gone.
        Assert.Equal("Stats HW", _tasks.GetById(task.Id)!.Title);
        Assert.Equal(new TimeOnly(16, 0), _blocks.GetById(session.Id)!.StartTime);
        Assert.NotNull(_completions.Get(session.Id, Date));
    }

    [Fact]
    public void UnscheduleSession_FailureWhileRemovingCompletions_RollsBackTheUnschedule()
    {
        var (_, session) = AddRepeatingTaskWithCompletion();
        var service = CreateService(new FailingCompletionWriteMutations(_database.Factory));

        // Removing a session deletes it and its completion rows in one transaction;
        // the completion delete fails, so the session must survive untouched.
        Assert.Throws<InvalidOperationException>(() => service.UnscheduleSession(session.Id));

        Assert.NotNull(_blocks.GetById(session.Id));
        Assert.NotNull(_completions.Get(session.Id, Date));
    }

    /// <summary>An open task with an elapsed one-off session awaiting its outcome.</summary>
    private (TaskItem Task, CalendarBlock Session) AddOpenTaskWithSession()
    {
        var task = TaskItem.Create("Elapsed work", _clock.Now, estimatedDuration: TimeSpan.FromMinutes(60));
        _tasks.Add(task);
        var session = CalendarBlock.CreateTaskSession(
            task.Id, Date, new TimeOnly(9, 0), new TimeOnly(10, 0), _clock.Now);
        _blocks.Add(session);
        return (task, session);
    }

    /// <summary>
    /// Needs more time must persist the session outcome and the task effect as one
    /// transaction: a failing task write leaves the session's outcome unrecorded too.
    /// </summary>
    [Fact]
    public void RecordOutcome_NeedsMoreTime_FailingTaskWrite_RollsBackBothHalves()
    {
        var (task, session) = AddOpenTaskWithSession();
        var service = CreateService(new FailingTaskWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(
            () => service.RecordOutcome(session.Id, BlockOutcome.NeedsMoreTime, TimeSpan.FromMinutes(25)));

        Assert.Equal(BlockOutcome.None, _blocks.GetById(session.Id)!.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(60), _tasks.GetById(task.Id)!.EstimatedDuration);
    }

    /// <summary>
    /// Creating a scheduled task is one transaction: when the session write fails,
    /// no orphaned Task survives either.
    /// </summary>
    [Fact]
    public void CreateTask_FailingSessionWrite_PersistsNeitherRecord()
    {
        var service = CreateService(new FailingBlockWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.CreateTask(
            new TaskDetailsRequest("Evening workout", null, null, null),
            new TaskScheduleRequest(Date, new TimeOnly(18, 30), new TimeOnly(19, 15), null)));

        Assert.Empty(_tasks.GetAll());
        Assert.Empty(_blocks.GetAll());
    }

    /// <summary>
    /// The session editor's save is one transaction: a failing completion purge
    /// rolls the reschedule and recurrence change back with it.
    /// </summary>
    [Fact]
    public void UpdateSessionSchedule_FailureDuringCompletionReconciliation_RollsBackTheReschedule()
    {
        var (task, session) = AddRepeatingTaskWithCompletion();
        var service = CreateService(new FailingCompletionWriteMutations(_database.Factory));

        // Removing Tuesday purges the obsolete completion row; that purge fails, so
        // the new time and the Wednesday weekday must both roll back.
        Assert.Throws<InvalidOperationException>(() => service.UpdateSessionSchedule(
            task.Id, session.Id,
            new TaskScheduleRequest(
                Date.AddDays(-7), new TimeOnly(15, 0), new TimeOnly(16, 0),
                RecurrenceRule.Weekly(1, DayOfWeek.Wednesday))));

        var unchanged = _blocks.GetById(session.Id)!;
        Assert.Equal(new TimeOnly(16, 0), unchanged.StartTime);
        Assert.Equal([DayOfWeek.Tuesday], unchanged.Recurrence!.DaysOfWeek);
        Assert.NotNull(_completions.Get(session.Id, Date));
    }

    /// <summary>
    /// The whole-task editor's save is one transaction: a failing sibling-outcome
    /// write rolls the task completion and every resolved outcome back together.
    /// </summary>
    [Fact]
    public void UpdateTaskDetails_FailingSiblingOutcomeWrite_RollsBackTaskAndOutcomes()
    {
        var (task, first) = AddOpenTaskWithSession();
        var second = CalendarBlock.CreateTaskSession(
            task.Id, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0), _clock.Now);
        _blocks.Add(second);
        var service = CreateService(new FailingBlockWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.UpdateTaskDetails(
            task.Id, new TaskDetailsRequest("Elapsed work", null, null, TimeSpan.FromMinutes(60)),
            new TaskCompletionRequest(_clock.Today, Completed: true)));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(first.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(second.Id)!.Outcome);
    }

    /// <summary>A local session with no live owning Task fails without changing the block.</summary>
    [Fact]
    public void RecordOutcome_OrphanedLocalSession_Fails_WithoutChangingTheBlock()
    {
        var orphan = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), taskId: null, title: null, Date,
            new TimeOnly(9, 0), new TimeOnly(10, 0), BlockKind.TaskSession, null,
            CalendarBlock.LocalProvider, null, 0, BlockOutcome.None, null, _clock.Now, _clock.Now);
        _blocks.Add(orphan);
        var service = CreateService(new SqliteCalendarMutations(_database.Factory));

        Assert.Throws<DomainException>(() => service.RecordOutcome(orphan.Id, BlockOutcome.Done));

        Assert.Equal(BlockOutcome.None, _blocks.GetById(orphan.Id)!.Outcome);
    }

    private CalendarService CreateService(ICalendarMutations mutations)
        => new(_blocks, _completions, mutations, _tasks, _clock);

    /// <summary>
    /// A real single-connection transaction whose completion writes fail — proves the
    /// service performs the task and block writes inside the same transaction, because
    /// the failure must roll them back.
    /// </summary>
    private sealed class FailingCompletionWriteMutations(SqliteConnectionFactory factory) : ICalendarMutations
    {
        public void Execute(
            Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
                IPlanningProposalRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new SqliteCalendarBlockRepository(connection, transaction),
                new FailingWrites(new SqliteOccurrenceCompletionRepository(connection, transaction)),
                new SqliteTaskRepository(connection, transaction),
                new SqlitePlanningProposalRepository(connection, transaction));
            transaction.Commit();
        }
    }

    /// <summary>
    /// A real single-connection transaction whose task writes fail — proves the
    /// service performs the block write inside the same transaction as the task
    /// effect, because the failure must roll it back.
    /// </summary>
    private sealed class FailingTaskWriteMutations(SqliteConnectionFactory factory) : ICalendarMutations
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
                new FailingTaskWrites(new SqliteTaskRepository(connection, transaction)),
                new SqlitePlanningProposalRepository(connection, transaction));
            transaction.Commit();
        }
    }

    /// <summary>A real single-connection transaction whose block writes fail.</summary>
    private sealed class FailingBlockWriteMutations(SqliteConnectionFactory factory) : ICalendarMutations
    {
        public void Execute(
            Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
                IPlanningProposalRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new FailingBlockWrites(new SqliteCalendarBlockRepository(connection, transaction)),
                new SqliteOccurrenceCompletionRepository(connection, transaction),
                new SqliteTaskRepository(connection, transaction),
                new SqlitePlanningProposalRepository(connection, transaction));
            transaction.Commit();
        }
    }

    private sealed class FailingBlockWrites(ICalendarBlockRepository inner) : ICalendarBlockRepository
    {
        public void Add(CalendarBlock block) => throw new InvalidOperationException("injected failure");

        public void Update(CalendarBlock block) => throw new InvalidOperationException("injected failure");

        public void Delete(CalendarBlockId id) => throw new InvalidOperationException("injected failure");

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

    private sealed class FailingTaskWrites(ITaskRepository inner) : ITaskRepository
    {
        public void Add(TaskItem task) => throw new InvalidOperationException("injected failure");

        public void Update(TaskItem task) => throw new InvalidOperationException("injected failure");

        public void Delete(TaskId id) => throw new InvalidOperationException("injected failure");

        public TaskItem? GetById(TaskId id) => inner.GetById(id);

        public IReadOnlyList<TaskItem> GetAll() => inner.GetAll();

        public IReadOnlyList<TaskItem> GetOpen() => inner.GetOpen();
    }

    private sealed class FailingWrites(IOccurrenceCompletionRepository inner) : IOccurrenceCompletionRepository
    {
        public void Add(OccurrenceCompletion completion)
            => throw new InvalidOperationException("injected failure");

        public void Remove(CalendarBlockId blockId, DateOnly occurrenceDate)
            => throw new InvalidOperationException("injected failure");

        public OccurrenceCompletion? Get(CalendarBlockId blockId, DateOnly occurrenceDate)
            => inner.Get(blockId, occurrenceDate);

        public IReadOnlyList<OccurrenceCompletion> GetForBlock(CalendarBlockId blockId)
            => inner.GetForBlock(blockId);

        public IReadOnlyList<OccurrenceCompletion> GetBetween(DateOnly from, DateOnly to)
            => inner.GetBetween(from, to);
    }

    public void Dispose() => _database.Dispose();
}
