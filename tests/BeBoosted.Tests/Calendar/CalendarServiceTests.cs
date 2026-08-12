using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
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
    private readonly CalendarService _service;
    private readonly InboxQueryService _inbox;

    public CalendarServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _service = new CalendarService(_blocks, _tasks, _clock);
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

    public void Dispose() => _database.Dispose();
}
