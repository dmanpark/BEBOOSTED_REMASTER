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
/// The whole-task editor's schedule-section operations: adding one more session
/// (the only entry that may create a second repeating schedule) and removing
/// every session in one transaction while the task survives.
/// </summary>
public sealed class SessionAdditionAndUnscheduleAllTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqliteOccurrenceCompletionRepository _completions;
    private readonly CalendarService _service;

    public SessionAdditionAndUnscheduleAllTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
        _service = new CalendarService(
            _blocks, _completions, new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
    }

    private TaskItem AddTask(string title)
    {
        var task = TaskItem.Create(title, _clock.Now);
        _tasks.Add(task);
        return task;
    }

    [Fact]
    public void AddSession_CreatesAOneOffBlock_ForTheTask()
    {
        var task = AddTask("Split work");

        var block = _service.AddSession(task.Id, new TaskScheduleRequest(
            new DateOnly(2026, 8, 14), new TimeOnly(15, 0), new TimeOnly(16, 0), null));

        var saved = _blocks.GetById(block.Id)!;
        Assert.Equal(task.Id, saved.TaskId);
        Assert.Equal(new DateOnly(2026, 8, 14), saved.Date);
        Assert.Equal(new TimeOnly(15, 0), saved.StartTime);
        Assert.Equal(new TimeOnly(16, 0), saved.EndTime);
        Assert.Null(saved.Recurrence);
    }

    [Fact]
    public void AddSession_WithRecurrence_CreatesARepeatingBlock()
    {
        var task = AddTask("Morning reading");

        var block = _service.AddSession(task.Id, new TaskScheduleRequest(
            new DateOnly(2026, 8, 10), new TimeOnly(7, 0), new TimeOnly(7, 30),
            RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Friday)));

        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Friday],
            _blocks.GetById(block.Id)!.Recurrence!.DaysOfWeek);
    }

    [Fact]
    public void AddSession_OnACompletedTask_IsRejected()
    {
        var task = AddTask("Finished");
        _service.CompleteTask(task.Id);

        var exception = Assert.Throws<DomainException>(() => _service.AddSession(
            task.Id, new TaskScheduleRequest(
                new DateOnly(2026, 8, 14), new TimeOnly(15, 0), new TimeOnly(16, 0), null)));

        Assert.Equal(
            "That task is already complete — reopen it before scheduling more work.", exception.Message);
        Assert.Empty(_blocks.GetForTask(task.Id));
    }

    [Fact]
    public void AddSession_EndBeforeStart_IsRejected()
    {
        var task = AddTask("Backwards");

        var exception = Assert.Throws<DomainException>(() => _service.AddSession(
            task.Id, new TaskScheduleRequest(
                new DateOnly(2026, 8, 14), new TimeOnly(16, 0), new TimeOnly(15, 0), null)));

        Assert.Equal("A block must end after it starts.", exception.Message);
        Assert.Empty(_blocks.GetForTask(task.Id));
    }

    [Fact]
    public void UnscheduleAllSessions_RemovesEveryBlock_AndItsCompletionRows_KeepingTheTask()
    {
        var task = AddTask("Mixed schedule");
        var oneOff = CalendarBlock.CreateTaskSession(
            task.Id, new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0), _clock.Now);
        _blocks.Add(oneOff);
        var repeating = CalendarBlock.CreateTaskSession(
            task.Id, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(repeating);
        _completions.Add(new OccurrenceCompletion(repeating.Id, new DateOnly(2026, 8, 11), _clock.Now));

        _service.UnscheduleAllSessions(task.Id);

        // Verified through fresh repositories over the same factory.
        Assert.Empty(new SqliteCalendarBlockRepository(_database.Factory).GetForTask(task.Id));
        Assert.Empty(new SqliteOccurrenceCompletionRepository(_database.Factory).GetForBlock(repeating.Id));
        var survivor = new SqliteTaskRepository(_database.Factory).GetById(task.Id)!;
        Assert.False(survivor.IsCompleted);
    }

    [Fact]
    public void UnscheduleAllSessions_FailingMidway_RollsBackEveryRemoval()
    {
        var task = AddTask("Mixed schedule");
        var first = CalendarBlock.CreateTaskSession(
            task.Id, new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0), _clock.Now);
        _blocks.Add(first);
        var second = CalendarBlock.CreateTaskSession(
            task.Id, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(second);
        _completions.Add(new OccurrenceCompletion(second.Id, new DateOnly(2026, 8, 11), _clock.Now));
        var service = new CalendarService(
            _blocks, _completions, new FailOnSecondDeleteMutations(_database.Factory), _tasks, _clock);

        Assert.Throws<InvalidOperationException>(() => service.UnscheduleAllSessions(task.Id));

        Assert.Equal(2, _blocks.GetForTask(task.Id).Count);
        Assert.NotNull(_completions.Get(second.Id, new DateOnly(2026, 8, 11)));
    }

    /// <summary>A real single-connection transaction whose second block Delete fails.</summary>
    private sealed class FailOnSecondDeleteMutations(SqliteConnectionFactory factory) : ICalendarMutations
    {
        public void Execute(
            Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
                IPlanningProposalRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new FailOnSecondDelete(new SqliteCalendarBlockRepository(connection, transaction)),
                new SqliteOccurrenceCompletionRepository(connection, transaction),
                new SqliteTaskRepository(connection, transaction),
                new SqlitePlanningProposalRepository(connection, transaction));
            transaction.Commit();
        }
    }

    private sealed class FailOnSecondDelete(ICalendarBlockRepository inner) : ICalendarBlockRepository
    {
        private int _deletes;

        public void Add(CalendarBlock block) => inner.Add(block);

        public void Update(CalendarBlock block) => inner.Update(block);

        public void Delete(CalendarBlockId id)
        {
            if (++_deletes >= 2)
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

    public void Dispose() => _database.Dispose();
}
