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
/// Editing a repeating schedule must reconcile completion rows in the same
/// transaction: no completion may survive for a date that is no longer a valid
/// occurrence, converting to a one-off leaves no occurrence rows at all, and
/// restoring a weekday later must never resurrect an obsolete completion.
/// </summary>
public sealed class SessionRecurrenceReconciliationTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    /// <summary>Tuesday, 2026-08-11.</summary>
    private static readonly DateOnly Tuesday = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly CalendarService _service;

    public SessionRecurrenceReconciliationTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _service = CreateService();
    }

    private CalendarService CreateService()
        => new(
            new SqliteCalendarBlockRepository(_database.Factory),
            new SqliteOccurrenceCompletionRepository(_database.Factory),
            new SqliteCalendarMutations(_database.Factory),
            new SqliteTaskRepository(_database.Factory),
            _clock);

    private (TaskId TaskId, CalendarBlockId SessionId) AddRepeatingTask(
        string title, params DayOfWeek[] days)
    {
        var task = _service.CreateTask(
            new TaskDetailsRequest(title, null, null, null),
            new TaskScheduleRequest(
                Tuesday.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
                RecurrenceRule.Weekly(1, days)));
        var session = Assert.Single(_blocks.GetForTask(task.Id));
        return (task.Id, session.Id);
    }

    private void SaveSchedule(
        TaskId taskId, CalendarBlockId sessionId, DateOnly date,
        RecurrenceRule? recurrence, TaskCompletionRequest? completion = null)
        => _service.UpdateSessionSchedule(
            taskId, sessionId,
            new TaskScheduleRequest(date, new TimeOnly(8, 30), new TimeOnly(9, 45), recurrence),
            completion);

    [Fact]
    public void RemovingACompletedWeekday_PurgesItsRow_AndReAddingStaysIncomplete()
    {
        var (taskId, sessionId) = AddRepeatingTask(
            "AP Economics", DayOfWeek.Tuesday, DayOfWeek.Wednesday);
        Assert.True(_service.CompleteOccurrence(sessionId, Tuesday));

        // Drop Tuesdays from the series: the completed Tuesday no longer occurs.
        SaveSchedule(taskId, sessionId, Tuesday.AddDays(-7),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));

        var completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
        Assert.Null(completions.Get(sessionId, Tuesday));

        // Restart the service graph, restore Tuesdays: no resurrection.
        var restarted = CreateService();
        SaveSchedule(taskId, sessionId, Tuesday.AddDays(-7),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        Assert.False(restarted.IsOccurrenceCompleted(sessionId, Tuesday));
    }

    [Fact]
    public void RepeatingToOneOff_LeavesNoOccurrenceRows_AndNeverCompletesTheTask()
    {
        var (taskId, sessionId) = AddRepeatingTask(
            "AP Economics", DayOfWeek.Tuesday, DayOfWeek.Wednesday);
        _service.CompleteOccurrence(sessionId, Tuesday);
        _service.CompleteOccurrence(sessionId, Tuesday.AddDays(1));

        // Convert the series into a one-off on the completed Tuesday.
        SaveSchedule(taskId, sessionId, Tuesday, recurrence: null);

        // One-off completion lives on the Task; old occurrence rows must all be gone
        // and must never leak into task completion.
        var completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
        Assert.Empty(completions.GetForBlock(sessionId));
        Assert.False(_tasks.GetById(taskId)!.IsCompleted);

        var restarted = CreateService();
        Assert.False(restarted.IsOccurrenceCompleted(sessionId, Tuesday));
    }

    /// <summary>A completed one-off task whose session is about to turn repeating.</summary>
    private (TaskId TaskId, CalendarBlockId SessionId) AddCompletedOneOff()
    {
        var task = _service.CreateTask(
            new TaskDetailsRequest("Stats HW", null, null, null),
            new TaskScheduleRequest(Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0), null));
        var sessionId = Assert.Single(_blocks.GetForTask(task.Id)).Id;
        _service.UpdateTaskDetails(
            task.Id, new TaskDetailsRequest("Stats HW", null, null, null),
            new TaskCompletionRequest(Tuesday, Completed: true));
        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        return (task.Id, sessionId);
    }

    /// <summary>
    /// A repeating task is never globally completed: converting a completed one-off
    /// to repeating reopens the Task, clears the inherited Done outcome, and — with
    /// no completion requested — invents no occurrence rows.
    /// </summary>
    [Fact]
    public void CompletedOneOffToRepeating_ReopensTheTask_WithoutFabricatingRows()
    {
        var (taskId, sessionId) = AddCompletedOneOff();

        _service.UpdateSessionSchedule(
            taskId, sessionId,
            new TaskScheduleRequest(
                Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0),
                RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)));

        var task = _tasks.GetById(taskId)!;
        Assert.False(task.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(sessionId)!.Outcome);
        Assert.Empty(
            new SqliteOccurrenceCompletionRepository(_database.Factory).GetForBlock(sessionId));

        // Every occurrence — today's and future ones — is active.
        var occurrences = _service.GetOccurrences(Tuesday, Tuesday.AddDays(8))
            .Where(o => o.Block.Id == sessionId).ToList();
        Assert.Equal(2, occurrences.Count);
        Assert.All(occurrences, o => Assert.False(o.IsCompleted));

        // The result survives an application restart.
        var restarted = new SqliteTaskRepository(_database.Factory).GetById(taskId)!;
        Assert.False(restarted.IsCompleted);
    }

    /// <summary>
    /// With Completed still checked during the conversion, only the occurrence the
    /// editor was opened for completes — freshly stamped — and the Task stays open
    /// with every future occurrence active.
    /// </summary>
    [Fact]
    public void CompletedOneOffToRepeating_WithCompletedChecked_CompletesOnlyTheOpenedOccurrence()
    {
        var (taskId, sessionId) = AddCompletedOneOff();
        var completedAt = _clock.Now;
        _clock.Now = completedAt.AddDays(1);

        _service.UpdateSessionSchedule(
            taskId, sessionId,
            new TaskScheduleRequest(
                Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0),
                RecurrenceRule.Weekly(1, DayOfWeek.Tuesday)),
            new TaskCompletionRequest(Tuesday, Completed: true));

        Assert.False(_tasks.GetById(taskId)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(sessionId)!.Outcome);
        var completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
        var row = Assert.Single(completions.GetForBlock(sessionId));
        Assert.Equal(Tuesday, row.OccurrenceDate);
        Assert.Equal(_clock.Now, row.CompletedAt); // fresh, not inherited
        Assert.False(_service.IsOccurrenceCompleted(sessionId, Tuesday.AddDays(7)));

        // Persisted across a restart: only the opened date stays complete.
        var restarted = CreateService();
        Assert.True(restarted.IsOccurrenceCompleted(sessionId, Tuesday));
        Assert.False(restarted.IsOccurrenceCompleted(sessionId, Tuesday.AddDays(7)));
        Assert.False(new SqliteTaskRepository(_database.Factory).GetById(taskId)!.IsCompleted);
    }

    /// <summary>
    /// Removing a repeating series must remove the session and its occurrence
    /// rows without ever promoting occurrence completion to the whole Task.
    /// </summary>
    [Fact]
    public void UnschedulingARepeatingSession_WithACompletedOccurrence_NeverCompletesTheTask()
    {
        var (taskId, sessionId) = AddRepeatingTask("AP Economics", DayOfWeek.Tuesday);
        Assert.True(_service.CompleteOccurrence(sessionId, Tuesday));

        _service.UnscheduleSession(sessionId);

        Assert.False(_tasks.GetById(taskId)!.IsCompleted);
        Assert.Null(_blocks.GetById(sessionId));
        Assert.Empty(
            new SqliteOccurrenceCompletionRepository(_database.Factory).GetForBlock(sessionId));

        // The open Task survives an application restart.
        Assert.False(new SqliteTaskRepository(_database.Factory).GetById(taskId)!.IsCompleted);
        Assert.False(CreateService().IsOccurrenceCompleted(sessionId, Tuesday));
    }

    /// <summary>Same removal from an incomplete occurrence: identical outcome.</summary>
    [Fact]
    public void UnschedulingARepeatingSession_FromAnIncompleteOccurrence_LeavesTheTaskOpen()
    {
        var (taskId, sessionId) = AddRepeatingTask("AP Economics", DayOfWeek.Tuesday);

        _service.UnscheduleSession(sessionId);

        Assert.False(_tasks.GetById(taskId)!.IsCompleted);
        Assert.Null(_blocks.GetById(sessionId));
        Assert.False(new SqliteTaskRepository(_database.Factory).GetById(taskId)!.IsCompleted);
    }

    public void Dispose() => _database.Dispose();
}
