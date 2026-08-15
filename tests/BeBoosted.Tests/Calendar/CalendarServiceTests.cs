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
    private readonly SqliteCommitmentCompletionRepository _completions;
    private readonly CalendarService _service;
    private readonly InboxQueryService _inbox;

    public CalendarServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _completions = new SqliteCommitmentCompletionRepository(_database.Factory);
        _service = new CalendarService(_blocks, _completions, new SqliteCalendarMutations(_database.Factory), _tasks, _clock);
        _inbox = new InboxQueryService(_tasks, _blocks);
    }

    private TaskItem AddTask(string title, TimeSpan? duration = null)
    {
        var task = TaskItem.Create(title, _clock.Now, estimatedDuration: duration);
        _tasks.Add(task);
        return task;
    }

    [Fact]
    public void ScheduleTask_UsesEstimate_AndRemovesTaskFromInbox()
    {
        var task = AddTask("Practice DECA role-play", TimeSpan.FromMinutes(90));

        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(15, 30));

        Assert.Equal(new TimeOnly(17, 0), block.EndTime);
        Assert.Equal(BlockKind.TaskBlock, block.Kind);
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
    public void RecordOutcome_Done_CompletesTheTask()
    {
        var task = AddTask("Review economics chapter", TimeSpan.FromMinutes(45));
        var block = _service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));

        _service.RecordOutcome(block.Id, BlockOutcome.Done);

        Assert.True(_tasks.GetById(task.Id)!.IsCompleted);
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
        _service.CreateFixedCommitment(
            "AP Economics", Date.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday));
        _service.CreateFixedCommitment("Family dinner", new DateOnly(2026, 8, 16), new TimeOnly(18, 0), new TimeOnly(19, 30));

        var occurrences = _service.GetOccurrences(new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 16));

        Assert.Equal(6, occurrences.Count); // Mon–Fri classes + Sunday dinner
        Assert.Equal(new DateOnly(2026, 8, 10), occurrences[0].Date);
        Assert.Equal(new DateOnly(2026, 8, 16), occurrences[^1].Date);
    }

    [Fact]
    public void GetBlocksNeedingOutcome_ReturnsOnlyElapsedTaskBlocks()
    {
        var task = AddTask("Elapsed work", TimeSpan.FromMinutes(60));
        var elapsed = _service.ScheduleTask(task.Id, Date, new TimeOnly(9, 0));    // ended 10:00 < now 14:10
        _service.ScheduleTask(AddTask("Future work", TimeSpan.FromMinutes(60)).Id, Date, new TimeOnly(18, 0));
        _service.CreateFixedCommitment("Lunch", Date, new TimeOnly(12, 0), new TimeOnly(12, 45));

        var needing = _service.GetBlocksNeedingOutcome();

        Assert.Single(needing);
        Assert.Equal(elapsed.Id, needing[0].Id);

        _service.RecordOutcome(elapsed.Id, BlockOutcome.Done);
        Assert.Empty(_service.GetBlocksNeedingOutcome());
    }

    [Fact]
    public void CreateFixedCommitment_PersistsAnOptionalProjectLink()
    {
        var projectId = AddProject("Schoolwork");

        var block = _service.CreateFixedCommitment(
            "AP Economics", Date, new TimeOnly(8, 30), new TimeOnly(9, 45), projectId: projectId);

        Assert.Equal(projectId, _blocks.GetById(block.Id)!.ProjectId);
    }

    [Fact]
    public void UpdateFixedCommitment_PersistsEveryField()
    {
        var block = _service.CreateFixedCommitment(
            "Lunch", Date, new TimeOnly(12, 0), new TimeOnly(12, 45));
        var projectId = AddProject("Math");
        var recurrence = RecurrenceRule.Weekly(1, DayOfWeek.Wednesday);

        _service.UpdateFixedCommitment(
            block.Id, "Study hall", Date.AddDays(1), new TimeOnly(13, 0), new TimeOnly(14, 30),
            recurrence, projectId);

        var loaded = _blocks.GetById(block.Id);
        Assert.Equal("Study hall", loaded!.Title);
        Assert.Equal(Date.AddDays(1), loaded.Date);
        Assert.Equal(new TimeOnly(13, 0), loaded.StartTime);
        Assert.Equal(new TimeOnly(14, 30), loaded.EndTime);
        Assert.Equal([DayOfWeek.Wednesday], loaded.Recurrence!.DaysOfWeek);
        Assert.Equal(projectId, loaded.ProjectId);
    }

    [Fact]
    public void UpdateFixedCommitment_RejectsExternalCommitments()
    {
        var external = AddExternalCommitment();

        Assert.Throws<DomainException>(() => _service.UpdateFixedCommitment(
            external.Id, "Hijacked", Date, new TimeOnly(9, 0), new TimeOnly(10, 0), null, null));
        Assert.Equal("External", _blocks.GetById(external.Id)!.Title);
    }

    [Fact]
    public void DeleteLocalCommitment_DeletesOnlyLocalFixedCommitments()
    {
        var local = _service.CreateFixedCommitment(
            "Lunch", Date, new TimeOnly(12, 0), new TimeOnly(12, 45));
        _service.DeleteLocalCommitment(local.Id);
        Assert.Null(_blocks.GetById(local.Id));

        var external = AddExternalCommitment();
        Assert.Throws<DomainException>(() => _service.DeleteLocalCommitment(external.Id));
        Assert.NotNull(_blocks.GetById(external.Id));

        var taskBlock = _service.ScheduleTask(AddTask("Work").Id, Date, new TimeOnly(9, 0));
        Assert.Throws<DomainException>(() => _service.DeleteLocalCommitment(taskBlock.Id));
        Assert.NotNull(_blocks.GetById(taskBlock.Id));
    }

    [Fact]
    public void UnscheduleTaskBlock_DeletesOnlyLocalTaskBlocks()
    {
        var taskBlock = _service.ScheduleTask(AddTask("Work").Id, Date, new TimeOnly(9, 0));
        _service.UnscheduleTaskBlock(taskBlock.Id);
        Assert.Null(_blocks.GetById(taskBlock.Id));

        var commitment = _service.CreateFixedCommitment(
            "Lunch", Date, new TimeOnly(12, 0), new TimeOnly(12, 45));
        Assert.Throws<DomainException>(() => _service.UnscheduleTaskBlock(commitment.Id));
        Assert.NotNull(_blocks.GetById(commitment.Id));

        var external = AddExternalCommitment();
        Assert.Throws<DomainException>(() => _service.UnscheduleTaskBlock(external.Id));
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

    [Fact]
    public void CompleteAndReopen_CommitmentOccurrence_PersistAndReportRealChanges()
    {
        var block = _service.CreateFixedCommitment(
            "Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0));

        Assert.True(_service.CompleteCommitmentOccurrence(block.Id, Date));
        Assert.True(_service.IsCommitmentOccurrenceCompleted(block.Id, Date));
        var occurrence = Assert.Single(_service.GetOccurrences(Date, Date), o => o.Block.Id == block.Id);
        Assert.True(occurrence.IsCompleted);

        // Completing an already-complete occurrence is a quiet no-op.
        Assert.False(_service.CompleteCommitmentOccurrence(block.Id, Date));

        Assert.True(_service.ReopenCommitmentOccurrence(block.Id, Date));
        Assert.False(_service.IsCommitmentOccurrenceCompleted(block.Id, Date));
        Assert.False(_service.ReopenCommitmentOccurrence(block.Id, Date));
    }

    [Fact]
    public void CommitmentCompletion_SurvivesApplicationRestart()
    {
        var block = _service.CreateFixedCommitment(
            "Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0));
        _service.CompleteCommitmentOccurrence(block.Id, Date);

        // A brand-new service graph over the same database file.
        var restarted = new CalendarService(
            new SqliteCalendarBlockRepository(_database.Factory),
            new SqliteCommitmentCompletionRepository(_database.Factory),
            new SqliteCalendarMutations(_database.Factory),
            new SqliteTaskRepository(_database.Factory),
            _clock);
        Assert.True(restarted.IsCommitmentOccurrenceCompleted(block.Id, Date));
        Assert.True(restarted.GetOccurrences(Date, Date).Single(o => o.Block.Id == block.Id).IsCompleted);
    }

    [Fact]
    public void CompletingACommitment_NeverTouchesTaskItems()
    {
        var task = AddTask("Stats HW", TimeSpan.FromMinutes(60));
        var taskBlock = _service.ScheduleTask(task.Id, Date, new TimeOnly(18, 0));
        var commitment = _service.CreateFixedCommitment(
            "Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0));

        _service.CompleteCommitmentOccurrence(commitment.Id, Date);

        Assert.False(_tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(taskBlock.Id)!.Outcome);
        Assert.Equal(BlockOutcome.None, _blocks.GetById(commitment.Id)!.Outcome);
    }

    [Fact]
    public void CompletionAndReopen_RejectExternalAndTaskBlocks_WithoutMutation()
    {
        var external = AddExternalCommitment();
        Assert.Throws<DomainException>(() => _service.CompleteCommitmentOccurrence(external.Id, Date));
        Assert.Throws<DomainException>(() => _service.ReopenCommitmentOccurrence(external.Id, Date));
        Assert.False(_service.IsCommitmentOccurrenceCompleted(external.Id, Date));

        var taskBlock = _service.ScheduleTask(AddTask("Work").Id, Date, new TimeOnly(9, 0));
        Assert.Throws<DomainException>(() => _service.CompleteCommitmentOccurrence(taskBlock.Id, Date));
    }

    [Fact]
    public void RecurringCompletion_AffectsOnlyTheSelectedOccurrence()
    {
        var block = _service.CreateFixedCommitment(
            "AP Economics", Date.AddDays(-14), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));

        // Complete only today's (Tuesday's) occurrence.
        Assert.True(_service.CompleteCommitmentOccurrence(block.Id, Date));

        var week = _service.GetOccurrences(Date, Date.AddDays(8))
            .Where(o => o.Block.Id == block.Id)
            .ToList();
        Assert.True(week.Single(o => o.Date == Date).IsCompleted);            // this Tuesday
        Assert.False(week.Single(o => o.Date == Date.AddDays(1)).IsCompleted); // Wednesday
        Assert.False(week.Single(o => o.Date == Date.AddDays(7)).IsCompleted); // next Tuesday

        // Completing on a non-occurrence date is rejected.
        Assert.Throws<DomainException>(
            () => _service.CompleteCommitmentOccurrence(block.Id, Date.AddDays(2)));
    }

    [Fact]
    public void UpdateFixedCommitment_RemovingAWeekday_PurgesItsObsoleteCompletion()
    {
        var series = _service.CreateFixedCommitment(
            "AP Economics", Date.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        _service.CompleteCommitmentOccurrence(series.Id, Date); // this Tuesday

        _service.UpdateFixedCommitment(
            series.Id, "AP Economics", Date.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday), null);

        // No completion row may survive for a date that no longer occurs.
        Assert.Null(_completions.Get(series.Id, Date));
    }

    [Fact]
    public void MovingACompletedOneOffCommitment_CarriesItsCompletionToTheNewDate()
    {
        var block = _service.CreateFixedCommitment(
            "Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0));
        _service.CompleteCommitmentOccurrence(block.Id, Date);

        _service.MoveBlock(block.Id, Date.AddDays(2), new TimeOnly(16, 0));
        Assert.True(_service.IsCommitmentOccurrenceCompleted(block.Id, Date.AddDays(2)));
        Assert.Null(_completions.Get(block.Id, Date));

        // The editor's date change does the same.
        _service.UpdateFixedCommitment(
            block.Id, "Stats HW", Date.AddDays(3), new TimeOnly(16, 0), new TimeOnly(17, 0), null, null);
        Assert.True(_service.IsCommitmentOccurrenceCompleted(block.Id, Date.AddDays(3)));
        Assert.Null(_completions.Get(block.Id, Date.AddDays(2)));
    }

    private ProjectId AddProject(string name)
    {
        var project = BeBoosted.Domain.Projects.Project.Create(name, "#5B8DEF", _clock.Now);
        new SqliteProjectRepository(_database.Factory).Add(project);
        return project.Id;
    }

    private CalendarBlock AddExternalCommitment()
    {
        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, null, "External", Date, new TimeOnly(9, 0),
            new TimeOnly(10, 0), BlockKind.FixedCommitment, null, "google", "evt-1", 0,
            BlockOutcome.None, null, _clock.Now, _clock.Now);
        _blocks.Add(external);
        return external;
    }

    public void Dispose() => _database.Dispose();
}
