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

public sealed class SqliteCalendarBlockRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateOnly Date = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly SqliteCalendarBlockRepository _repository;
    private readonly SqliteTaskRepository _tasks;

    public SqliteCalendarBlockRepositoryTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _repository = new SqliteCalendarBlockRepository(_database.Factory);
        _tasks = new SqliteTaskRepository(_database.Factory);
    }

    private TaskId AddTask(string title)
    {
        var task = TaskItem.Create(title, Now);
        _tasks.Add(task);
        return task.Id;
    }

    [Fact]
    public void AddAndGetById_RoundTripsEveryField()
    {
        var block = CalendarBlock.CreateFixedCommitment(
            "AP Economics", Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Friday));

        _repository.Add(block);
        var loaded = _repository.GetById(block.Id);

        Assert.NotNull(loaded);
        Assert.Equal("AP Economics", loaded.Title);
        Assert.Null(loaded.TaskId);
        Assert.Equal(Date, loaded.Date);
        Assert.Equal(new TimeOnly(8, 30), loaded.StartTime);
        Assert.Equal(new TimeOnly(9, 45), loaded.EndTime);
        Assert.Equal(BlockKind.FixedCommitment, loaded.Kind);
        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Friday],
            loaded.Recurrence!.DaysOfWeek.OrderBy(d => d));
        Assert.Equal("local", loaded.Provider);
        Assert.Null(loaded.ExternalId);
        Assert.Equal(BlockOutcome.None, loaded.Outcome);
        Assert.Equal(block.CreatedAt, loaded.CreatedAt);
    }

    [Fact]
    public void Update_PersistsOutcome()
    {
        var block = CalendarBlock.CreateForTask(AddTask("Task"), Date, new TimeOnly(9, 0), new TimeOnly(10, 0), Now);
        _repository.Add(block);

        block.RecordOutcome(BlockOutcome.NeedsMoreTime, Now.AddHours(2));
        _repository.Update(block);

        var loaded = _repository.GetById(block.Id);
        Assert.Equal(BlockOutcome.NeedsMoreTime, loaded!.Outcome);
        Assert.Equal(Now.AddHours(2), loaded.OutcomeRecordedAt);
    }

    [Fact]
    public void GetCandidatesBetween_IncludesDatedInRangeAndEarlierRecurring()
    {
        var inRange = CalendarBlock.CreateForTask(AddTask("A"), Date, new TimeOnly(9, 0), new TimeOnly(10, 0), Now);
        var outOfRange = CalendarBlock.CreateForTask(AddTask("B"), Date.AddDays(30), new TimeOnly(9, 0), new TimeOnly(10, 0), Now);
        var recurringEarlier = CalendarBlock.CreateFixedCommitment(
            "Class", Date.AddDays(-30), new TimeOnly(8, 0), new TimeOnly(9, 0), Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _repository.Add(inRange);
        _repository.Add(outOfRange);
        _repository.Add(recurringEarlier);

        var candidates = _repository.GetCandidatesBetween(Date.AddDays(-1), Date.AddDays(6));

        Assert.Contains(candidates, b => b.Id == inRange.Id);
        Assert.Contains(candidates, b => b.Id == recurringEarlier.Id);
        Assert.DoesNotContain(candidates, b => b.Id == outOfRange.Id);
    }

    [Fact]
    public void GetElapsedWithoutOutcome_UsesEndTimeBoundary()
    {
        var endedEarlier = CalendarBlock.CreateForTask(AddTask("A"), Date, new TimeOnly(9, 0), new TimeOnly(10, 0), Now);
        var endsAtNow = CalendarBlock.CreateForTask(AddTask("B"), Date, new TimeOnly(13, 0), new TimeOnly(14, 0), Now);
        var future = CalendarBlock.CreateForTask(AddTask("C"), Date, new TimeOnly(18, 0), new TimeOnly(19, 0), Now);
        var yesterday = CalendarBlock.CreateForTask(AddTask("D"), Date.AddDays(-1), new TimeOnly(18, 0), new TimeOnly(19, 0), Now);
        foreach (var block in new[] { endedEarlier, endsAtNow, future, yesterday })
        {
            _repository.Add(block);
        }

        var elapsed = _repository.GetElapsedWithoutOutcome(Date, new TimeOnly(14, 0));

        Assert.Equal(3, elapsed.Count);
        Assert.DoesNotContain(elapsed, b => b.Id == future.Id);
    }

    [Fact]
    public void GetTaskIdsWithPendingBlocks_ExcludesResolvedBlocks()
    {
        var pendingTask = AddTask("Pending");
        var resolvedTask = AddTask("Resolved");
        var pending = CalendarBlock.CreateForTask(pendingTask, Date, new TimeOnly(9, 0), new TimeOnly(10, 0), Now);
        var resolved = CalendarBlock.CreateForTask(resolvedTask, Date, new TimeOnly(10, 0), new TimeOnly(11, 0), Now);
        resolved.RecordOutcome(BlockOutcome.Done, Now);
        _repository.Add(pending);
        _repository.Add(resolved);

        var ids = _repository.GetTaskIdsWithPendingBlocks();

        Assert.Contains(pendingTask, ids);
        Assert.DoesNotContain(resolvedTask, ids);
    }

    [Fact]
    public void Delete_RemovesBlock()
    {
        var block = CalendarBlock.CreateForTask(AddTask("A"), Date, new TimeOnly(9, 0), new TimeOnly(10, 0), Now);
        _repository.Add(block);
        _repository.Delete(block.Id);
        Assert.Null(_repository.GetById(block.Id));
    }

    [Fact]
    public void DeletingTask_CascadesToItsBlocks()
    {
        var taskId = AddTask("Doomed");
        var block = CalendarBlock.CreateForTask(taskId, Date, new TimeOnly(9, 0), new TimeOnly(10, 0), Now);
        _repository.Add(block);

        _tasks.Delete(taskId);

        Assert.Null(_repository.GetById(block.Id));
    }

    public void Dispose() => _database.Dispose();
}
