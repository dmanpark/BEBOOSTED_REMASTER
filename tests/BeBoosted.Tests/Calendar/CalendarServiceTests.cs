using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Calendar;

public sealed class CalendarServiceTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private static readonly DateOnly Date = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqliteOccurrenceCompletionRepository _completions;
    private readonly CalendarService _service;
    private readonly InboxQueryService _inbox;

    public CalendarServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
        _service = new CalendarService(_blocks, _completions, new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
        _inbox = new InboxQueryService(_tasks, _blocks);
    }

    private TaskItem AddTask(string title, TimeSpan? duration = null)
    {
        var task = TaskItem.Create(title, _clock.Now, estimatedDuration: duration);
        _tasks.Add(task);
        return task;
    }

    private static TaskDetailsRequest Details(
        string title, ProjectId? projectId = null, DateOnly? deadline = null, TimeSpan? duration = null)
        => new(title, projectId, deadline, duration);

    /// <summary>Creates a scheduled task through the unified editor path.</summary>
    private (TaskItem Task, CalendarBlock Session) AddScheduledTask(
        string title,
        DateOnly date,
        TimeOnly start,
        TimeOnly end,
        RecurrenceRule? recurrence = null,
        ProjectId? projectId = null)
    {
        var task = _service.CreateTask(
            Details(title, projectId), new TaskScheduleRequest(date, start, end, recurrence));
        var session = Assert.Single(_blocks.GetForTask(task.Id));
        return (task, session);
    }

    [Fact]
    public void ScheduleTask_UsesEstimate_AndRemovesTaskFromInbox()
    {
        var task = AddTask("Practice DECA role-play", TimeSpan.FromMinutes(90));

        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(15, 30));

        Assert.Equal(new TimeOnly(17, 0), block.EndTime);
        Assert.Equal(BlockKind.TaskSession, block.Kind);
        Assert.DoesNotContain(_inbox.GetInboxTasks(), t => t.Id == task.Id);
    }

    [Fact]
    public void ScheduleTask_WithoutEstimate_DefaultsTo30Minutes()
    {
        var task = AddTask("Email recommendation request");
        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(15, 0));
        Assert.Equal(TimeSpan.FromMinutes(30), block.Duration);
    }

    [Fact]
    public void ScheduleTask_WithExplicitDuration_UsesItInsteadOfTheEstimate()
    {
        var task = AddTask("Practice DECA role-play", TimeSpan.FromMinutes(90));

        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(15, 30), TimeSpan.FromMinutes(45));

        Assert.Equal(new TimeOnly(16, 15), block.EndTime);
        Assert.DoesNotContain(_inbox.GetInboxTasks(), t => t.Id == task.Id);
    }

    [Fact]
    public void ScheduleTask_WithExplicitDuration_ClampsAtMidnight()
    {
        var task = AddTask("Late work");
        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(23, 50), TimeSpan.FromMinutes(30));
        Assert.Equal(new TimeOnly(23, 59), block.EndTime);
    }

    [Fact]
    public void ScheduleTask_WithNonPositiveDuration_Throws()
    {
        var task = AddTask("Zero work");
        Assert.Throws<DomainException>(
            () => _service.ScheduleTask(task.Id, Date, new TimeOnly(15, 0), TimeSpan.Zero));
    }

    [Fact]
    public void MoveAndResize_PersistAcrossReload()
    {
        var task = AddTask("Draft essay outline", TimeSpan.FromMinutes(60));
        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(15, 0));

        _service.MoveBlock(block.Id, Date.AddDays(1), new TimeOnly(9, 0));
        _service.ResizeBlock(block.Id, new TimeOnly(10, 30));

        var loaded = _blocks.GetById(block.Id);
        Assert.Equal(Date.AddDays(1), loaded!.Date);
        Assert.Equal(new TimeOnly(9, 0), loaded.StartTime);
        Assert.Equal(new TimeOnly(10, 30), loaded.EndTime);
    }

    [Fact]
    public void RecordOutcome_Done_ResolvesTheSessionWithoutCompletingTheTask()
    {
        var task = AddTask("Review economics chapter", TimeSpan.FromMinutes(45));
        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));

        _service.RecordOutcome(block.Id, BlockOutcome.Done);

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(block.Id)!.Outcome);
    }

    [Fact]
    public void RecordOutcome_NeedsMoreTime_UpdatesEstimateAndReturnsTaskToInbox()
    {
        var task = AddTask("Finish DECA presentation", TimeSpan.FromMinutes(90));
        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));
        Assert.DoesNotContain(_inbox.GetInboxTasks(), t => t.Id == task.Id);

        _service.RecordOutcome(block.Id, BlockOutcome.NeedsMoreTime, TimeSpan.FromMinutes(30));

        var reloaded = _tasks.GetById(task.Id)!;
        Assert.False(reloaded.IsCompleted);
        Assert.Equal(TimeSpan.FromMinutes(30), reloaded.EstimatedDuration);
        Assert.Contains(_inbox.GetInboxTasks(), t => t.Id == task.Id);
    }

    [Fact]
    public void RecordOutcome_DidntHappen_LeavesTaskOpenForReplanning()
    {
        var task = AddTask("Draft personal statement", TimeSpan.FromMinutes(60));
        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));

        _service.RecordOutcome(block.Id, BlockOutcome.DidntHappen);

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Contains(_inbox.GetInboxTasks(), t => t.Id == task.Id);
    }

    [Fact]
    public void GetOccurrences_ExpandsWeeklyRecurrenceAcrossTheWeek()
    {
        AddScheduledTask(
            "AP Economics", Date.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday));
        AddScheduledTask(
            "Family dinner", new DateOnly(2026, 8, 16), new TimeOnly(18, 0), new TimeOnly(19, 30));

        var occurrences = _service.GetOccurrences(new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 16));

        Assert.Equal(6, occurrences.Count); // Mon–Fri classes + Sunday dinner
        Assert.Equal(new DateOnly(2026, 8, 10), occurrences[0].Date);
        Assert.Equal(new DateOnly(2026, 8, 16), occurrences[^1].Date);
    }

    [Fact]
    public void GetBlocksNeedingOutcome_ReturnsOnlyElapsedOneOffSessionsOfOpenTasks()
    {
        var task = AddTask("Elapsed work", TimeSpan.FromMinutes(60));
        var elapsed = _service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));    // ended 10:00 < now 14:10
        _service.ScheduleTask(AddTask("Future work", TimeSpan.FromMinutes(60)).Id, Date, new TimeOnly(18, 0));
        // A repeating session completes per occurrence — it never needs an outcome.
        AddScheduledTask(
            "AP Economics", Date.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Tuesday));
        // A session whose task was completed in the editor no longer nags either.
        var (doneTask, _) = AddScheduledTask("Lunch", Date, new TimeOnly(12, 0), new TimeOnly(12, 45));
        _service.UpdateTaskDetails(
            doneTask.Id, Details("Lunch"), new TaskCompletionRequest(Date, Completed: true));

        var needing = _service.GetBlocksNeedingOutcome();

        Assert.Single(needing);
        Assert.Equal(elapsed.Id, needing[0].Id);

        _service.RecordOutcome(elapsed.Id, BlockOutcome.Done);
        Assert.Empty(_service.GetBlocksNeedingOutcome());
    }

    // ---- The unified Task editor's persistence path ----

    [Fact]
    public void CreateTask_PersistsProjectDeadlineAndEstimate_OnTheTask()
    {
        var projectId = AddProject("Schoolwork");

        var task = _service.CreateTask(
            Details("AP Economics", projectId, Date.AddDays(3), TimeSpan.FromMinutes(75)),
            new TaskScheduleRequest(Date, new TimeOnly(8, 30), new TimeOnly(9, 45), null));

        var loaded = _tasks.GetById(task.Id)!;
        Assert.Equal(projectId, loaded.ProjectId);
        Assert.Equal(Date.AddDays(3), loaded.Deadline);
        Assert.Equal(TimeSpan.FromMinutes(75), loaded.EstimatedDuration);
        var session = Assert.Single(_blocks.GetForTask(task.Id));
        Assert.Null(session.Title); // the Task owns the title
        Assert.Equal(BlockKind.TaskSession, session.Kind);
    }

    [Fact]
    public void CreateTask_WithoutSchedule_LeavesTheTaskUnscheduled()
    {
        var task = _service.CreateTask(Details("Email recommendation request"));
        Assert.Empty(_blocks.GetForTask(task.Id));
        Assert.Contains(_inbox.GetInboxTasks(), t => t.Id == task.Id);
    }

    [Fact]
    public void ScopedUpdates_TogetherPersistEveryField()
    {
        var (task, session) = AddScheduledTask("Lunch", Date, new TimeOnly(12, 0), new TimeOnly(12, 45));
        var projectId = AddProject("Math");
        var recurrence = RecurrenceRule.Weekly(1, DayOfWeek.Wednesday);

        _service.UpdateTaskDetails(task.Id, Details("Study hall", projectId));
        _service.UpdateSessionSchedule(
            task.Id, session.Id,
            new TaskScheduleRequest(Date.AddDays(1), new TimeOnly(13, 0), new TimeOnly(14, 30), recurrence));

        var loadedTask = _tasks.GetById(task.Id)!;
        Assert.Equal("Study hall", loadedTask.Title);
        Assert.Equal(projectId, loadedTask.ProjectId);
        var loaded = _blocks.GetById(session.Id)!;
        Assert.Equal(Date.AddDays(1), loaded.Date);
        Assert.Equal(new TimeOnly(13, 0), loaded.StartTime);
        Assert.Equal(new TimeOnly(14, 30), loaded.EndTime);
        Assert.Equal([DayOfWeek.Wednesday], loaded.Recurrence!.DaysOfWeek);
        Assert.Null(loaded.Title);
    }

    [Fact]
    public void AddSession_OnAnUnscheduledTask_CreatesTheSession()
    {
        var task = _service.CreateTask(Details("Lunch"));

        _service.AddSession(
            task.Id, new TaskScheduleRequest(Date, new TimeOnly(12, 0), new TimeOnly(12, 45), null));

        var session = Assert.Single(_blocks.GetForTask(task.Id));
        Assert.Equal(new TimeOnly(12, 0), session.StartTime);
    }

    [Fact]
    public void DeleteTask_RemovesTheTaskItsSessionsAndTheirCompletions()
    {
        var (task, session) = AddScheduledTask(
            "AP Economics", Date.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _service.CompleteOccurrence(session.Id, Date);

        _service.DeleteTask(task.Id);

        Assert.Null(_tasks.GetById(task.Id));
        Assert.Null(_blocks.GetById(session.Id));
        Assert.Empty(_completions.GetForBlock(session.Id));
    }

    [Fact]
    public void UnscheduleSession_DeletesOnlyLocalSessions()
    {
        var taskBlock = _service.ScheduleTask(AddTask("Work").Id, Date, new TimeOnly(9, 0));
        _service.UnscheduleSession(taskBlock.Id);
        Assert.Null(_blocks.GetById(taskBlock.Id));

        var external = AddExternalEvent();
        Assert.Throws<DomainException>(() => _service.UnscheduleSession(external.Id));
        Assert.NotNull(_blocks.GetById(external.Id));
    }

    [Fact]
    public void CalendarService_ExposesNoUnrestrictedDeletionApi()
    {
        // Every public deletion path validates kind and provider; the old
        // pass-through DeleteBlock must stay gone.
        Assert.Null(typeof(CalendarService).GetMethod("DeleteBlock"));
        Assert.Null(typeof(CalendarService).GetMethod("DeleteLocalBlock"));
    }

    // ---- Completion: one-off completes the Task; repeating per occurrence ----

    [Fact]
    public void UpdateTaskDetails_CompletingAOneOff_CompletesTheTask_AndReopeningReverts()
    {
        var (task, session) = AddScheduledTask("Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0));

        _service.UpdateTaskDetails(
            task.Id, Details("Stats HW"), new TaskCompletionRequest(Date, true));
        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, _blocks.GetById(session.Id)!.Outcome);

        _service.UpdateTaskDetails(
            task.Id, Details("Stats HW"), new TaskCompletionRequest(Date, false));
        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(session.Id)!.Outcome);
    }

    [Fact]
    public void CompleteAndReopen_RepeatingOccurrence_PersistAndReportRealChanges()
    {
        var (_, session) = AddScheduledTask(
            "Stats HW", Date.AddDays(-7), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        Assert.True(_service.CompleteOccurrence(session.Id, Date));
        Assert.True(_service.IsOccurrenceCompleted(session.Id, Date));
        var occurrence = Assert.Single(
            _service.GetOccurrences(Date, Date), o => o.Block.Id == session.Id);
        Assert.True(occurrence.IsCompleted);

        // Completing an already-complete occurrence is a quiet no-op.
        Assert.False(_service.CompleteOccurrence(session.Id, Date));

        Assert.True(_service.ReopenOccurrence(session.Id, Date));
        Assert.False(_service.IsOccurrenceCompleted(session.Id, Date));
        Assert.False(_service.ReopenOccurrence(session.Id, Date));
    }

    [Fact]
    public void OccurrenceCompletion_SurvivesApplicationRestart()
    {
        var (_, session) = AddScheduledTask(
            "Stats HW", Date.AddDays(-7), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _service.CompleteOccurrence(session.Id, Date);

        // A brand-new service graph over the same database file.
        var restarted = new CalendarService(
            new SqliteCalendarBlockRepository(_database.Factory),
            new SqliteOccurrenceCompletionRepository(_database.Factory),
            new SqliteCalendarMutations(_database.Factory),
            new SqliteTaskRepository(_database.Factory),
            _clock);
        Assert.True(restarted.IsOccurrenceCompleted(session.Id, Date));
        Assert.True(restarted.GetOccurrences(Date, Date).Single(o => o.Block.Id == session.Id).IsCompleted);
    }

    [Fact]
    public void CompletingARepeatingOccurrence_NeverCompletesTheTask()
    {
        var (task, session) = AddScheduledTask(
            "AP Economics", Date.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        _service.CompleteOccurrence(session.Id, Date);

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(session.Id)!.Outcome);
    }

    [Fact]
    public void OccurrenceCompletion_RejectsExternalAndOneOffSessions_WithoutMutation()
    {
        var external = AddExternalEvent();
        Assert.Throws<DomainException>(() => _service.CompleteOccurrence(external.Id, Date));
        Assert.Throws<DomainException>(() => _service.ReopenOccurrence(external.Id, Date));
        Assert.False(_service.IsOccurrenceCompleted(external.Id, Date));

        // A one-off session completes its Task, never an occurrence.
        var oneOff = _service.ScheduleTask(AddTask("Work").Id, Date, new TimeOnly(9, 0));
        Assert.Throws<DomainException>(() => _service.CompleteOccurrence(oneOff.Id, Date));
    }

    /// <summary>TDD phase 13: repeating completion affects only the selected day.</summary>
    [Fact]
    public void RepeatingCompletion_AffectsOnlyTheSelectedOccurrence()
    {
        var (_, session) = AddScheduledTask(
            "AP Economics", Date.AddDays(-14), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));

        // Complete only today's (Tuesday's) occurrence.
        Assert.True(_service.CompleteOccurrence(session.Id, Date));

        var week = _service.GetOccurrences(Date, Date.AddDays(8))
            .Where(o => o.Block.Id == session.Id)
            .ToList();
        Assert.True(week.Single(o => o.Date == Date).IsCompleted);            // this Tuesday
        Assert.False(week.Single(o => o.Date == Date.AddDays(1)).IsCompleted); // Wednesday
        Assert.False(week.Single(o => o.Date == Date.AddDays(7)).IsCompleted); // next Tuesday

        // Completing on a non-occurrence date is rejected.
        Assert.Throws<DomainException>(
            () => _service.CompleteOccurrence(session.Id, Date.AddDays(2)));
    }

    [Fact]
    public void MovingACompletedOneOffSession_KeepsTheTaskCompleted()
    {
        var (task, session) = AddScheduledTask("Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0));
        _service.UpdateTaskDetails(
            task.Id, Details("Stats HW"), new TaskCompletionRequest(Date, true));

        _service.MoveBlock(session.Id, Date.AddDays(2), new TimeOnly(16, 0));

        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(Date.AddDays(2), _blocks.GetById(session.Id)!.Date);
    }

    /// <summary>
    /// External synced events reject every mutation at the service layer without
    /// changing state, and keep their provider identifiers through persistence.
    /// </summary>
    [Fact]
    public void ExternalEvent_RejectsEveryMutation_WithoutChangingState()
    {
        var external = AddExternalEvent();

        Assert.Throws<DomainException>(() => _service.MoveBlock(
            external.Id, Date.AddDays(1), new TimeOnly(10, 0)));
        Assert.Throws<DomainException>(() => _service.ResizeBlock(external.Id, new TimeOnly(11, 0)));
        Assert.Throws<DomainException>(() => _service.UnscheduleSession(external.Id));
        Assert.Throws<DomainException>(() => _service.RecordOutcome(external.Id, BlockOutcome.Done));
        Assert.Throws<DomainException>(() => _service.CompleteOccurrence(external.Id, Date));
        Assert.Throws<DomainException>(() => _service.ReopenOccurrence(external.Id, Date));

        var persisted = _blocks.GetById(external.Id)!;
        Assert.Equal("External", persisted.Title);
        Assert.Equal(Date, persisted.Date);
        Assert.Equal(new TimeOnly(9, 0), persisted.StartTime);
        Assert.Equal(new TimeOnly(10, 0), persisted.EndTime);
        Assert.Equal(BlockOutcome.None, persisted.Outcome);
        Assert.Equal("google", persisted.Provider);
        Assert.Equal("evt-1", persisted.ExternalId);
        Assert.Null(persisted.TaskId); // never assigned to a task or project
    }

    private ProjectId AddProject(string name)
    {
        var project = BeBoosted.Domain.Projects.Project.Create(name, "#5B8DEF", _clock.Now);
        new SqliteProjectRepository(_database.Factory).Add(project);
        return project.Id;
    }

    private CalendarBlock AddExternalEvent()
    {
        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "External", Date, new TimeOnly(9, 0),
            new TimeOnly(10, 0), BlockKind.ExternalEvent, null, "google", "evt-1", 0,
            BlockOutcome.None, null, _clock.Now, _clock.Now);
        _blocks.Add(external);
        return external;
    }

    public void Dispose() => _database.Dispose();
}
