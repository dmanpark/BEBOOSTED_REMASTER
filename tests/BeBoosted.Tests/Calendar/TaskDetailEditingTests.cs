using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Calendar;

/// <summary>
/// The whole-task editor's save path: task fields plus aggregate completion,
/// never a session's date, time, or recurrence.
/// </summary>
public sealed class TaskDetailEditingTests : IDisposable
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

    public TaskDetailEditingTests()
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

    private CalendarBlock AddSessionFor(
        TaskItem task, DateOnly date, TimeOnly start, TimeOnly end, RecurrenceRule? recurrence = null)
    {
        var session = CalendarBlock.CreateTaskSession(task.Id, date, start, end, _clock.Now, recurrence);
        _blocks.Add(session);
        return session;
    }

    [Fact]
    public void UpdateTaskDetails_PersistsEveryField_WithoutTouchingAnySchedule()
    {
        var task = AddTask("Draft essay");
        var session = AddSessionFor(task, new DateOnly(2026, 8, 25), new TimeOnly(9, 0), new TimeOnly(10, 0));

        _service.UpdateTaskDetails(task.Id, new TaskDetailsRequest(
            "Draft essay v2", null, new DateOnly(2026, 8, 30), TimeSpan.FromMinutes(90)));

        // Persistence proven through fresh repositories over the same factory.
        var saved = new SqliteTaskRepository(_database.Factory).GetById(task.Id)!;
        Assert.Equal("Draft essay v2", saved.Title);
        Assert.Equal(new DateOnly(2026, 8, 30), saved.Deadline);
        Assert.Equal(TimeSpan.FromMinutes(90), saved.EstimatedDuration);
        var untouched = new SqliteCalendarBlockRepository(_database.Factory).GetById(session.Id)!;
        Assert.Equal(new DateOnly(2026, 8, 25), untouched.Date);
        Assert.Equal(new TimeOnly(9, 0), untouched.StartTime);
        Assert.Equal(new TimeOnly(10, 0), untouched.EndTime);
        Assert.Null(untouched.Recurrence);
    }

    [Fact]
    public void UpdateTaskDetails_Completing_ResolvesEveryPendingOneOff_AsDone()
    {
        var task = AddTask("Split work");
        var first = AddSessionFor(task, new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSessionFor(task, new DateOnly(2026, 8, 13), new TimeOnly(9, 0), new TimeOnly(10, 0));

        _service.UpdateTaskDetails(
            task.Id, new TaskDetailsRequest("Split work", null, null, null),
            new TaskCompletionRequest(_clock.Today, Completed: true));

        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(first.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(second.Id)!.Outcome);
    }

    [Fact]
    public void UpdateTaskDetails_Reopening_ClearsEveryDoneOneOff()
    {
        var task = AddTask("Split work");
        var first = AddSessionFor(task, new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSessionFor(task, new DateOnly(2026, 8, 13), new TimeOnly(9, 0), new TimeOnly(10, 0));
        _service.CompleteTask(task.Id);

        _service.UpdateTaskDetails(
            task.Id, new TaskDetailsRequest("Split work", null, null, null),
            new TaskCompletionRequest(_clock.Today, Completed: false));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(first.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(second.Id)!.Outcome);
    }

    [Fact]
    public void UpdateTaskDetails_CompletingUnderARepeatingSchedule_IsRejected_ChangingNothing()
    {
        var task = AddTask("Morning reading");
        AddSessionFor(
            task, new DateOnly(2026, 8, 4), new TimeOnly(7, 0), new TimeOnly(7, 30),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        var exception = Assert.Throws<DomainException>(() => _service.UpdateTaskDetails(
            task.Id, new TaskDetailsRequest("Renamed", null, new DateOnly(2026, 9, 1), null),
            new TaskCompletionRequest(_clock.Today, Completed: true)));

        Assert.Equal("A repeating task completes per occurrence, not as a whole.", exception.Message);
        var unchanged = _tasks.GetById(task.Id)!;
        Assert.Equal("Morning reading", unchanged.Title);
        Assert.Null(unchanged.Deadline);
        Assert.False(unchanged.IsCompleted);
    }

    [Fact]
    public void UpdateTaskDetails_EmptyTitle_IsRejected()
    {
        var task = AddTask("Keep me");

        var exception = Assert.Throws<DomainException>(() => _service.UpdateTaskDetails(
            task.Id, new TaskDetailsRequest("   ", null, null, null)));

        Assert.Equal("A task needs a title.", exception.Message);
        Assert.Equal("Keep me", _tasks.GetById(task.Id)!.Title);
    }

    [Fact]
    public void UpdateTaskDetails_NonPositiveEstimate_IsRejected()
    {
        var task = AddTask("Keep me");

        var exception = Assert.Throws<DomainException>(() => _service.UpdateTaskDetails(
            task.Id, new TaskDetailsRequest("Keep me", null, null, TimeSpan.Zero)));

        Assert.Equal("An estimated duration must be positive.", exception.Message);
    }

    [Fact]
    public void UpdateTaskDetails_MissingTask_Throws_TaskNoLongerExists()
    {
        var exception = Assert.Throws<DomainException>(() => _service.UpdateTaskDetails(
            TaskId.New(), new TaskDetailsRequest("Ghost", null, null, null)));

        Assert.Contains("no longer exists", exception.Message);
    }

    public void Dispose() => _database.Dispose();
}
