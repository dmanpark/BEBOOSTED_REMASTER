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

public sealed class SqliteOccurrenceCompletionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateOnly Date = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqliteOccurrenceCompletionRepository _completions;

    public SqliteOccurrenceCompletionRepositoryTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
    }

    private CalendarBlock AddRepeatingSession(
        string title = "Stats HW", DayOfWeek day = DayOfWeek.Tuesday)
    {
        var task = TaskItem.Create(title, Now);
        _tasks.Add(task);
        var session = CalendarBlock.CreateTaskSession(
            task.Id, Date.AddDays(-14), new TimeOnly(16, 0), new TimeOnly(17, 0), Now,
            RecurrenceRule.Weekly(1, day));
        _blocks.Add(session);
        return session;
    }

    [Fact]
    public void AddAndGet_RoundTripTheCompletion()
    {
        var session = AddRepeatingSession();

        _completions.Add(new OccurrenceCompletion(session.Id, Date, Now.AddHours(2)));
        var loaded = _completions.Get(session.Id, Date);

        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded.BlockId);
        Assert.Equal(Date, loaded.OccurrenceDate);
        Assert.Equal(Now.AddHours(2), loaded.CompletedAt);
        Assert.Null(_completions.Get(session.Id, Date.AddDays(1)));
    }

    [Fact]
    public void Add_IsAnUpsertPerOccurrence()
    {
        var session = AddRepeatingSession();
        _completions.Add(new OccurrenceCompletion(session.Id, Date, Now));
        _completions.Add(new OccurrenceCompletion(session.Id, Date, Now.AddHours(3)));

        Assert.Equal(Now.AddHours(3), _completions.Get(session.Id, Date)!.CompletedAt);
        Assert.Single(_completions.GetBetween(Date, Date));
    }

    [Fact]
    public void Remove_ReopensTheOccurrence_AndToleratesMissingRows()
    {
        var session = AddRepeatingSession();
        _completions.Add(new OccurrenceCompletion(session.Id, Date, Now));

        _completions.Remove(session.Id, Date);
        Assert.Null(_completions.Get(session.Id, Date));

        _completions.Remove(session.Id, Date); // no throw for an absent row
    }

    [Fact]
    public void GetBetween_ReturnsOnlyOccurrencesInsideTheRange()
    {
        var tuesday = AddRepeatingSession();
        var economics = AddRepeatingSession("AP Economics");
        _completions.Add(new OccurrenceCompletion(tuesday.Id, Date, Now));
        _completions.Add(new OccurrenceCompletion(economics.Id, Date.AddDays(-7), Now));
        _completions.Add(new OccurrenceCompletion(economics.Id, Date, Now));

        var inRange = _completions.GetBetween(Date.AddDays(-1), Date.AddDays(6));

        Assert.Equal(2, inRange.Count);
        Assert.All(inRange, c => Assert.Equal(Date, c.OccurrenceDate));
    }

    [Fact]
    public void DeletingTheBlock_CascadesToItsCompletions()
    {
        var session = AddRepeatingSession();
        _completions.Add(new OccurrenceCompletion(session.Id, Date, Now));

        _blocks.Delete(session.Id);

        Assert.Null(_completions.Get(session.Id, Date));
    }

    public void Dispose() => _database.Dispose();
}
