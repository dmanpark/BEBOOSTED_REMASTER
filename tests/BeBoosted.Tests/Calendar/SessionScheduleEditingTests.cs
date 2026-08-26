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
/// The session editor's save path: exactly one named block's schedule plus the
/// staged occurrence completion, atomically — task detail fields untouched, and
/// a conversion never completes anything.
/// </summary>
public sealed class SessionScheduleEditingTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    /// <summary>A Tuesday, matching the repeating anchors below.</summary>
    private static readonly DateOnly Anchor = new(2026, 8, 4);

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqliteOccurrenceCompletionRepository _completions;
    private readonly CalendarService _service;

    public SessionScheduleEditingTests()
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

    private static TaskScheduleRequest OneOff(DateOnly date, TimeOnly start, TimeOnly end)
        => new(date, start, end, null);

    [Fact]
    public void UpdateSessionSchedule_ReschedulesExactlyTheNamedSession()
    {
        var task = AddTask("Split work");
        var first = AddSessionFor(task, new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSessionFor(task, new DateOnly(2026, 8, 13), new TimeOnly(9, 0), new TimeOnly(10, 0));

        _service.UpdateSessionSchedule(
            task.Id, second.Id, OneOff(new DateOnly(2026, 8, 15), new TimeOnly(14, 0), new TimeOnly(15, 30)));

        var movedSecond = _blocks.GetById(second.Id)!;
        Assert.Equal(new DateOnly(2026, 8, 15), movedSecond.Date);
        Assert.Equal(new TimeOnly(14, 0), movedSecond.StartTime);
        Assert.Equal(new TimeOnly(15, 30), movedSecond.EndTime);
        var untouchedFirst = _blocks.GetById(first.Id)!;
        Assert.Equal(new DateOnly(2026, 8, 12), untouchedFirst.Date);
        Assert.Equal(new TimeOnly(9, 0), untouchedFirst.StartTime);
        Assert.Equal(new TimeOnly(10, 0), untouchedFirst.EndTime);
    }

    [Fact]
    public void UpdateSessionSchedule_SessionOfAnotherTask_IsRejected()
    {
        var mine = AddTask("Mine");
        var other = AddTask("Other");
        var otherSession = AddSessionFor(
            other, new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0));

        var exception = Assert.Throws<DomainException>(() => _service.UpdateSessionSchedule(
            mine.Id, otherSession.Id,
            OneOff(new DateOnly(2026, 8, 15), new TimeOnly(14, 0), new TimeOnly(15, 0))));

        Assert.Equal("That session belongs to a different task.", exception.Message);
        Assert.Equal(new DateOnly(2026, 8, 12), _blocks.GetById(otherSession.Id)!.Date);
    }

    [Fact]
    public void UpdateSessionSchedule_EndBeforeStart_IsRejected()
    {
        var task = AddTask("One");
        var session = AddSessionFor(task, new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0));

        var exception = Assert.Throws<DomainException>(() => _service.UpdateSessionSchedule(
            task.Id, session.Id,
            OneOff(new DateOnly(2026, 8, 12), new TimeOnly(16, 0), new TimeOnly(15, 30))));

        Assert.Equal("A block must end after it starts.", exception.Message);
        Assert.Equal(new TimeOnly(9, 0), _blocks.GetById(session.Id)!.StartTime);
    }

    [Fact]
    public void UpdateSessionSchedule_WeekdayChange_PurgesObsoleteOccurrenceRows()
    {
        var task = AddTask("Stats HW");
        var session = AddSessionFor(
            task, Anchor, new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Thursday));
        var thursday = new DateOnly(2026, 8, 6);
        _completions.Add(new OccurrenceCompletion(session.Id, thursday, _clock.Now));

        _service.UpdateSessionSchedule(task.Id, session.Id, new TaskScheduleRequest(
            Anchor, new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)));

        Assert.Null(_completions.Get(session.Id, thursday));
        Assert.Equal([DayOfWeek.Tuesday], _blocks.GetById(session.Id)!.Recurrence!.DaysOfWeek);
    }

    [Fact]
    public void UpdateSessionSchedule_CompletingAnOccurrenceTheEditRemoves_IsRejected()
    {
        var task = AddTask("Stats HW");
        var session = AddSessionFor(
            task, Anchor, new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Thursday));
        var thursday = new DateOnly(2026, 8, 6);

        var exception = Assert.Throws<DomainException>(() => _service.UpdateSessionSchedule(
            task.Id, session.Id,
            new TaskScheduleRequest(
                Anchor, new TimeOnly(16, 0), new TimeOnly(17, 0),
                RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)),
            new TaskCompletionRequest(thursday, Completed: true)));

        Assert.Equal(
            "That occurrence no longer exists after this change — untick Completed or keep its weekday.",
            exception.Message);
        Assert.Equal(
            [DayOfWeek.Tuesday, DayOfWeek.Thursday],
            _blocks.GetById(session.Id)!.Recurrence!.DaysOfWeek);
    }

    [Fact]
    public void UpdateSessionSchedule_StagedOccurrenceCompletion_UpsertsTheRow_Atomically()
    {
        var task = AddTask("Stats HW");
        var session = AddSessionFor(
            task, Anchor, new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var occurrence = new DateOnly(2026, 8, 11);
        var keepRepeating = new TaskScheduleRequest(
            Anchor, new TimeOnly(16, 0), new TimeOnly(17, 0), RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        _service.UpdateSessionSchedule(
            task.Id, session.Id, keepRepeating, new TaskCompletionRequest(occurrence, Completed: true));
        Assert.True(_service.IsOccurrenceCompleted(session.Id, occurrence));

        _service.UpdateSessionSchedule(
            task.Id, session.Id, keepRepeating, new TaskCompletionRequest(occurrence, Completed: false));
        Assert.False(_service.IsOccurrenceCompleted(session.Id, occurrence));
    }

    [Fact]
    public void UpdateSessionSchedule_CompletedOneOffToRepeating_ReopensTheTask_AndClearsTheOutcome()
    {
        var task = AddTask("Vocab review");
        var session = AddSessionFor(task, new DateOnly(2026, 8, 10), new TimeOnly(9, 0), new TimeOnly(10, 0));
        _service.CompleteTask(task.Id);

        _service.UpdateSessionSchedule(task.Id, session.Id, new TaskScheduleRequest(
            new DateOnly(2026, 8, 10), new TimeOnly(9, 0), new TimeOnly(10, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Monday)));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        var converted = _blocks.GetById(session.Id)!;
        Assert.Equal(BlockOutcome.None, converted.Outcome);
        Assert.NotNull(converted.Recurrence);
    }

    [Fact]
    public void UpdateSessionSchedule_RepeatingToOneOff_NeverPromotesOccurrenceCompletion()
    {
        var task = AddTask("Morning reading");
        var session = AddSessionFor(
            task, Anchor, new TimeOnly(7, 0), new TimeOnly(7, 30),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var occurrence = new DateOnly(2026, 8, 11);
        _completions.Add(new OccurrenceCompletion(session.Id, occurrence, _clock.Now));

        _service.UpdateSessionSchedule(
            task.Id, session.Id, OneOff(new DateOnly(2026, 8, 11), new TimeOnly(7, 0), new TimeOnly(7, 30)));

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        var converted = _blocks.GetById(session.Id)!;
        Assert.Null(converted.Recurrence);
        Assert.Equal(BlockOutcome.None, converted.Outcome);
        Assert.Empty(_completions.GetForBlock(session.Id));
    }

    [Fact]
    public void UpdateSessionSchedule_MissingSession_Throws_NoLongerExists()
    {
        var task = AddTask("Ghost target");

        var exception = Assert.Throws<DomainException>(() => _service.UpdateSessionSchedule(
            task.Id, CalendarBlockId.New(),
            OneOff(new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0))));

        Assert.Contains("no longer exists", exception.Message);
    }

    public void Dispose() => _database.Dispose();
}
