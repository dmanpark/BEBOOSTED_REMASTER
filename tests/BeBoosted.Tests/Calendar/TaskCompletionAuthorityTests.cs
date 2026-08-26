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
/// The product's one authoritative task-completion path: completing or reopening a
/// Task reconciles its one-off session outcomes in the same transaction. Unscheduled
/// tasks touch only the Task; scheduled one-offs resolve their pending sessions as
/// Done (and reopening clears them); repeating tasks are never globally completed
/// here. Every write shares one real SQLite transaction.
/// </summary>
public sealed class TaskCompletionAuthorityTests : IDisposable
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
    private readonly CalendarService _service;

    public TaskCompletionAuthorityTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _service = CreateService(new SqliteCalendarMutations(_database.Factory));
    }

    private CalendarService CreateService(ICalendarMutations mutations)
        => new(
            _blocks, new SqliteOccurrenceCompletionRepository(_database.Factory),
            mutations, _tasks, _clock);

    private TaskItem AddTask(string title = "Evening workout")
    {
        var task = TaskItem.Create(title, _clock.Now);
        _tasks.Add(task);
        return task;
    }

    private CalendarBlock AddSession(TaskId taskId, int dayOffset, int startHour)
    {
        var session = CalendarBlock.CreateTaskSession(
            taskId, Tuesday.AddDays(dayOffset),
            new TimeOnly(startHour, 0), new TimeOnly(startHour + 1, 0), _clock.Now);
        _blocks.Add(session);
        return session;
    }

    [Fact]
    public void CompleteTask_ScheduledOneOff_CompletesTheTaskAndItsSessionTogether()
    {
        var task = AddTask();
        var session = AddSession(task.Id, dayOffset: 0, startHour: 18);

        Assert.True(_service.CompleteTask(task.Id));

        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(session.Id)!.Outcome);

        // Both halves survive an application restart.
        Assert.True(new SqliteTaskRepository(_database.Factory).GetById(task.Id)!.IsCompleted);
        Assert.Equal(
            BlockOutcome.Done,
            new SqliteCalendarBlockRepository(_database.Factory).GetById(session.Id)!.Outcome);
    }

    [Fact]
    public void ReopenTask_ClearsTheSessionOutcome_AndSurvivesRestart()
    {
        var task = AddTask();
        var session = AddSession(task.Id, dayOffset: 0, startHour: 18);
        Assert.True(_service.CompleteTask(task.Id));

        Assert.True(_service.ReopenTask(task.Id));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(session.Id)!.Outcome);
        Assert.False(new SqliteTaskRepository(_database.Factory).GetById(task.Id)!.IsCompleted);
        Assert.Equal(
            BlockOutcome.None,
            new SqliteCalendarBlockRepository(_database.Factory).GetById(session.Id)!.Outcome);
    }

    [Fact]
    public void CompleteTask_Unscheduled_TouchesOnlyTheTask_AndNoOpsWhenAlreadyDone()
    {
        var task = AddTask("Inbox idea");

        Assert.True(_service.CompleteTask(task.Id));
        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);

        // Requesting the state it is already in is a quiet no-op.
        Assert.False(_service.CompleteTask(task.Id));

        Assert.True(_service.ReopenTask(task.Id));
        Assert.False(_service.ReopenTask(task.Id));
        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
    }

    /// <summary>
    /// Deterministic multi-session rule: completing resolves every still-pending
    /// one-off session as Done; reopening clears exactly the Done outcomes; sessions
    /// resolved another way keep their history through both directions.
    /// </summary>
    [Fact]
    public void CompleteTask_MultipleOneOffSessions_ResolvesEveryPendingSession_PreservingHistory()
    {
        var task = AddTask("College essay");
        var missed = AddSession(task.Id, dayOffset: -1, startHour: 9);
        var today = AddSession(task.Id, dayOffset: 0, startHour: 15);
        var tomorrow = AddSession(task.Id, dayOffset: 1, startHour: 15);
        _service.RecordOutcome(missed.Id, BlockOutcome.DidntHappen);

        Assert.True(_service.CompleteTask(task.Id));

        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.DidntHappen, _blocks.GetById(missed.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(today.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(tomorrow.Id)!.Outcome);

        Assert.True(_service.ReopenTask(task.Id));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.DidntHappen, _blocks.GetById(missed.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(today.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(tomorrow.Id)!.Outcome);
    }

    [Fact]
    public void CompleteTask_RepeatingTask_IsRejected()
    {
        var task = AddTask("Stats HW");
        var session = CalendarBlock.CreateTaskSession(
            task.Id, Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(session);

        Assert.Throws<DomainException>(() => _service.CompleteTask(task.Id));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void CompleteTask_FailingTaskWrite_RollsBackTheSessionOutcome()
    {
        var task = AddTask();
        var session = AddSession(task.Id, dayOffset: 0, startHour: 18);
        var service = CreateService(new FailingTaskWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.CompleteTask(task.Id));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(session.Id)!.Outcome);
    }

    [Fact]
    public void CompleteTask_FailingSessionWrite_RollsBackTheTaskCompletion()
    {
        var task = AddTask();
        var session = AddSession(task.Id, dayOffset: 0, startHour: 18);
        var service = CreateService(new FailingBlockWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.CompleteTask(task.Id));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(session.Id)!.Outcome);
    }

    [Fact]
    public void ReopenTask_FailingSessionWrite_RollsBackTheReopen()
    {
        var task = AddTask();
        var session = AddSession(task.Id, dayOffset: 0, startHour: 18);
        Assert.True(_service.CompleteTask(task.Id));
        var service = CreateService(new FailingBlockWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.ReopenTask(task.Id));

        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(session.Id)!.Outcome);
    }

    /// <summary>
    /// The whole-task editor shares the same task-level semantics: completing
    /// through it resolves every pending one-off session, and unchecking clears
    /// them again.
    /// </summary>
    [Fact]
    public void UpdateTaskDetails_TaskCompletion_ResolvesEveryPendingOneOffSession()
    {
        var task = AddTask("College essay");
        var edited = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var sibling = AddSession(task.Id, dayOffset: 1, startHour: 15);
        var details = new TaskDetailsRequest("College essay", null, null, null);

        _service.UpdateTaskDetails(
            task.Id, details, new TaskCompletionRequest(Tuesday, Completed: true));

        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(edited.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(sibling.Id)!.Outcome);

        _service.UpdateTaskDetails(
            task.Id, details, new TaskCompletionRequest(Tuesday, Completed: false));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(edited.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(sibling.Id)!.Outcome);
    }

    // ---- RecordOutcome(Done) shares the authoritative aggregate transition ----

    /// <summary>
    /// Recording one pending session Done completes the Task through the same
    /// aggregate rule as CompleteTask: every pending one-off sibling resolves too.
    /// </summary>
    [Fact]
    public void RecordOutcomeDone_ResolvesEveryPendingOneOffSibling()
    {
        var task = AddTask("College essay");
        var recorded = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var sibling = AddSession(task.Id, dayOffset: 1, startHour: 15);

        _service.RecordOutcome(recorded.Id, BlockOutcome.Done);

        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(recorded.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(sibling.Id)!.Outcome);

        // The whole aggregate survives an application restart.
        var restarted = new SqliteCalendarBlockRepository(_database.Factory);
        Assert.Equal(BlockOutcome.Done, restarted.GetById(sibling.Id)!.Outcome);
        Assert.True(new SqliteTaskRepository(_database.Factory).GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void RecordOutcomeDone_ResolvesOnlyPendingSiblings_PreservingResolvedHistory()
    {
        var task = AddTask("College essay");
        var missed = AddSession(task.Id, dayOffset: -1, startHour: 9);
        _service.RecordOutcome(missed.Id, BlockOutcome.DidntHappen);
        var recorded = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var pending = AddSession(task.Id, dayOffset: 1, startHour: 15);

        _service.RecordOutcome(recorded.Id, BlockOutcome.Done);

        Assert.Equal(BlockOutcome.DidntHappen, _blocks.GetById(missed.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(recorded.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(pending.Id)!.Outcome);
    }

    /// <summary>
    /// A repeating sibling forbids global completion, so recording the one-off Done
    /// must be rejected before anything — block, Task, or rows — changes.
    /// </summary>
    [Fact]
    public void RecordOutcomeDone_WithARepeatingSibling_IsRejected_ChangingNothing()
    {
        var task = AddTask("Mixed task");
        var oneOff = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var repeating = CalendarBlock.CreateTaskSession(
            task.Id, Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(repeating);
        _service.CompleteOccurrence(repeating.Id, Tuesday);

        Assert.Throws<DomainException>(
            () => _service.RecordOutcome(oneOff.Id, BlockOutcome.Done));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(oneOff.Id)!.Outcome);
        Assert.NotNull(_blocks.GetById(repeating.Id)!.Recurrence);
        Assert.True(_service.IsOccurrenceCompleted(repeating.Id, Tuesday));

        // Still open and untouched after an application restart.
        Assert.False(new SqliteTaskRepository(_database.Factory).GetById(task.Id)!.IsCompleted);
        Assert.Equal(
            BlockOutcome.None,
            new SqliteCalendarBlockRepository(_database.Factory).GetById(oneOff.Id)!.Outcome);
    }

    [Fact]
    public void RecordOutcomeDone_FailingSelectedSessionWrite_RollsBackEverything()
    {
        var task = AddTask("College essay");
        var recorded = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var sibling = AddSession(task.Id, dayOffset: 1, startHour: 15);
        var service = CreateService(new FailingNthBlockWriteMutations(_database.Factory, failOnUpdate: 1));

        Assert.Throws<InvalidOperationException>(
            () => service.RecordOutcome(recorded.Id, BlockOutcome.Done));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(recorded.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(sibling.Id)!.Outcome);
    }

    [Fact]
    public void RecordOutcomeDone_FailingSiblingSessionWrite_RollsBackEverything()
    {
        var task = AddTask("College essay");
        var recorded = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var sibling = AddSession(task.Id, dayOffset: 1, startHour: 15);
        var service = CreateService(new FailingNthBlockWriteMutations(_database.Factory, failOnUpdate: 2));

        Assert.Throws<InvalidOperationException>(
            () => service.RecordOutcome(recorded.Id, BlockOutcome.Done));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(recorded.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(sibling.Id)!.Outcome);
    }

    [Fact]
    public void RecordOutcomeDone_FailingTaskWrite_RollsBackEverySessionWrite()
    {
        var task = AddTask("College essay");
        var recorded = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var sibling = AddSession(task.Id, dayOffset: 1, startHour: 15);
        var service = CreateService(new FailingTaskWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(
            () => service.RecordOutcome(recorded.Id, BlockOutcome.Done));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(recorded.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(sibling.Id)!.Outcome);
    }

    // ---- Aggregate completion respects the task's actual schedule ----

    /// <summary>
    /// Checking Completed on a task that still repeats must be rejected before
    /// any mutation — a repeating Task stays occurrence-scoped.
    /// </summary>
    [Fact]
    public void UpdateTaskDetails_CompletingATask_WithARepeatingSession_IsRejectedWithoutMutation()
    {
        var task = AddTask("Mixed task");
        var oneOff = AddSession(task.Id, dayOffset: 0, startHour: 9);
        var repeating = CalendarBlock.CreateTaskSession(
            task.Id, Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(repeating);

        Assert.Throws<DomainException>(() => _service.UpdateTaskDetails(
            task.Id, new TaskDetailsRequest("Renamed", null, null, null),
            new TaskCompletionRequest(Tuesday, Completed: true)));

        Assert.Equal("Mixed task", _tasks.GetById(task.Id)!.Title);
        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(oneOff.Id)!.Outcome);
        Assert.False(new SqliteTaskRepository(_database.Factory).GetById(task.Id)!.IsCompleted);
    }

    // ---- Aggregate-defined no-ops, repair, and legacy-state recovery ----

    /// <summary>
    /// "Already complete" is an aggregate statement: a completed Task with a pending
    /// one-off session is inconsistent and must be repaired, not reported as a no-op.
    /// </summary>
    [Fact]
    public void CompleteTask_CompletedTaskWithAPendingSession_RepairsTheAggregate()
    {
        var task = AddTask("Corrupted");
        task.Complete(_clock.Now);
        _tasks.Update(task);
        var pending = AddSession(task.Id, dayOffset: 1, startHour: 15);

        Assert.True(_service.CompleteTask(task.Id));

        Assert.Equal(BlockOutcome.Done, _blocks.GetById(pending.Id)!.Outcome);
        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(
            BlockOutcome.Done,
            new SqliteCalendarBlockRepository(_database.Factory).GetById(pending.Id)!.Outcome);
    }

    [Fact]
    public void CompleteTask_FullyConsistentAggregate_IsAQuietNoOp()
    {
        var task = AddTask();
        var session = AddSession(task.Id, dayOffset: 0, startHour: 18);
        Assert.True(_service.CompleteTask(task.Id));
        var settled = _blocks.GetById(session.Id)!;

        Assert.False(_service.CompleteTask(task.Id));

        Assert.Equal(settled.ModifiedAt, _blocks.GetById(session.Id)!.ModifiedAt);
        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
    }

    /// <summary>
    /// Corrupted legacy state: a globally completed Task that still repeats can be
    /// reopened back to the valid open state — completing it globally stays forbidden.
    /// </summary>
    [Fact]
    public void ReopenTask_OnAGloballyCompletedRepeatingTask_RestoresTheOpenState()
    {
        var task = AddTask("Corrupted repeating");
        task.Complete(_clock.Now);
        _tasks.Update(task);
        var repeating = CalendarBlock.CreateTaskSession(
            task.Id, Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(repeating);
        _service.CompleteOccurrence(repeating.Id, Tuesday);

        Assert.True(_service.ReopenTask(task.Id));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.NotNull(_blocks.GetById(repeating.Id)!.Recurrence);
        Assert.True(_service.IsOccurrenceCompleted(repeating.Id, Tuesday)); // untouched
    }

    /// <summary>
    /// "Completed Task + pending session" can no longer be produced by scheduling:
    /// AddSession rejects a completed Task outright (reopen it first).
    /// </summary>
    [Fact]
    public void AddSession_OnACompletedTask_IsRejectedWithoutMutation()
    {
        var task = AddTask("Wrap-up notes");
        Assert.True(_service.CompleteTask(task.Id));

        Assert.Throws<DomainException>(() => _service.AddSession(
            task.Id, new TaskScheduleRequest(Tuesday, new TimeOnly(9, 0), new TimeOnly(10, 0), null)));

        Assert.Empty(_blocks.GetForTask(task.Id));
        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
    }

    /// <summary>A real single-connection transaction whose task writes fail.</summary>
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

    // ---- Direct scheduling schedules active work only ----

    /// <summary>
    /// Direct scheduling represents scheduling active work: a completed Task is
    /// rejected before anything is created or touched. (The editor's atomic
    /// UpdateTask path remains the one way to add a Done session deliberately.)
    /// </summary>
    [Fact]
    public void ScheduleTask_ACompletedTask_IsRejected_WithoutCreatingASession()
    {
        var task = AddTask("Wrapped up");
        Assert.True(_service.CompleteTask(task.Id));
        var before = _tasks.GetById(task.Id)!.ModifiedAt;

        Assert.Throws<DomainException>(
            () => _service.ScheduleTask(task.Id, Tuesday, new TimeOnly(9, 0)));

        Assert.Empty(_blocks.GetForTask(task.Id));
        var after = _tasks.GetById(task.Id)!;
        Assert.True(after.IsCompleted);
        Assert.Equal(before, after.ModifiedAt);

        // Clean after reopening the database: no session, still completed.
        Assert.Empty(new SqliteCalendarBlockRepository(_database.Factory).GetForTask(task.Id));
        Assert.True(new SqliteTaskRepository(_database.Factory).GetById(task.Id)!.IsCompleted);
    }

    [Fact]
    public void ScheduleTask_WithExplicitDuration_ACompletedTask_IsRejected()
    {
        var task = AddTask("Wrapped up");
        Assert.True(_service.CompleteTask(task.Id));

        Assert.Throws<DomainException>(() => _service.ScheduleTask(
            task.Id, Tuesday, new TimeOnly(9, 0), TimeSpan.FromMinutes(45)));

        Assert.Empty(_blocks.GetForTask(task.Id));
        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
    }

    // ---- Deletion is transactional across the whole aggregate ----

    /// <summary>A task with resolved history, a repeating series, and an occurrence row.</summary>
    private (TaskItem Task, CalendarBlock Resolved, CalendarBlock Repeating) AddDeletableAggregate()
    {
        var task = AddTask("Doomed");
        var resolved = AddSession(task.Id, dayOffset: -1, startHour: 9);
        _service.RecordOutcome(resolved.Id, BlockOutcome.DidntHappen);
        var repeating = CalendarBlock.CreateTaskSession(
            task.Id, Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(repeating);
        _service.CompleteOccurrence(repeating.Id, Tuesday);
        return (task, resolved, repeating);
    }

    [Fact]
    public void DeleteTask_RemovesTheWholeAggregate_AndSurvivesRestart()
    {
        var (task, resolved, repeating) = AddDeletableAggregate();

        _service.DeleteTask(task.Id);

        Assert.Null(_tasks.GetById(task.Id));
        Assert.Null(_blocks.GetById(resolved.Id));
        Assert.Null(_blocks.GetById(repeating.Id));
        var completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
        Assert.Empty(completions.GetForBlock(repeating.Id));
        Assert.Null(new SqliteTaskRepository(_database.Factory).GetById(task.Id));
        Assert.Null(new SqliteCalendarBlockRepository(_database.Factory).GetById(repeating.Id));
    }

    [Fact]
    public void DeleteTask_FailingTaskDelete_RollsBackEverySessionRemoval()
    {
        var (task, resolved, repeating) = AddDeletableAggregate();
        var service = CreateService(new FailingTaskWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteTask(task.Id));

        Assert.NotNull(_tasks.GetById(task.Id));
        Assert.NotNull(_blocks.GetById(resolved.Id));
        Assert.NotNull(_blocks.GetById(repeating.Id));
        Assert.True(_service.IsOccurrenceCompleted(repeating.Id, Tuesday));
    }

    /// <summary>
    /// A real single-connection transaction whose Nth block Update fails — pins the
    /// write position (selected session vs. sibling) inside the one transaction.
    /// </summary>
    private sealed class FailingNthBlockWriteMutations(
        SqliteConnectionFactory factory, int failOnUpdate) : ICalendarMutations
    {
        public void Execute(
            Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
                IPlanningProposalRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new NthFailingBlockWrites(
                    new SqliteCalendarBlockRepository(connection, transaction), failOnUpdate),
                new SqliteOccurrenceCompletionRepository(connection, transaction),
                new SqliteTaskRepository(connection, transaction),
                new SqlitePlanningProposalRepository(connection, transaction));
            transaction.Commit();
        }
    }

    private sealed class NthFailingBlockWrites(ICalendarBlockRepository inner, int failOn)
        : ICalendarBlockRepository
    {
        private int _updates;

        public void Add(CalendarBlock block) => inner.Add(block);

        public void Update(CalendarBlock block)
        {
            if (++_updates == failOn)
            {
                throw new InvalidOperationException("injected failure");
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

        public IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks()
            => inner.GetTaskIdsWithPendingBlocks();
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

    private sealed class FailingTaskWrites(ITaskRepository inner) : ITaskRepository
    {
        public void Add(TaskItem task) => throw new InvalidOperationException("injected failure");

        public void Update(TaskItem task) => throw new InvalidOperationException("injected failure");

        public void Delete(TaskId id) => throw new InvalidOperationException("injected failure");

        public TaskItem? GetById(TaskId id) => inner.GetById(id);

        public IReadOnlyList<TaskItem> GetAll() => inner.GetAll();

        public IReadOnlyList<TaskItem> GetOpen() => inner.GetOpen();
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

    public void Dispose() => _database.Dispose();
}
