using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Calendar;

public sealed class SqliteCommitmentCompletionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateOnly Date = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqliteCommitmentCompletionRepository _completions;

    public SqliteCommitmentCompletionRepositoryTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _completions = new SqliteCommitmentCompletionRepository(_database.Factory);
    }

    private CalendarBlock AddCommitment(string title = "Stats HW")
    {
        var block = CalendarBlock.CreateFixedCommitment(
            title, Date, new TimeOnly(16, 0), new TimeOnly(17, 0), Now);
        _blocks.Add(block);
        return block;
    }

    [Fact]
    public void AddAndGet_RoundTripTheCompletion()
    {
        var block = AddCommitment();

        _completions.Add(new CommitmentCompletion(block.Id, Date, Now.AddHours(2)));
        var loaded = _completions.Get(block.Id, Date);

        Assert.NotNull(loaded);
        Assert.Equal(block.Id, loaded.BlockId);
        Assert.Equal(Date, loaded.OccurrenceDate);
        Assert.Equal(Now.AddHours(2), loaded.CompletedAt);
        Assert.Null(_completions.Get(block.Id, Date.AddDays(1)));
    }

    [Fact]
    public void Add_IsAnUpsertPerOccurrence()
    {
        var block = AddCommitment();
        _completions.Add(new CommitmentCompletion(block.Id, Date, Now));
        _completions.Add(new CommitmentCompletion(block.Id, Date, Now.AddHours(3)));

        Assert.Equal(Now.AddHours(3), _completions.Get(block.Id, Date)!.CompletedAt);
        Assert.Single(_completions.GetBetween(Date, Date));
    }

    [Fact]
    public void Remove_ReopensTheOccurrence_AndToleratesMissingRows()
    {
        var block = AddCommitment();
        _completions.Add(new CommitmentCompletion(block.Id, Date, Now));

        _completions.Remove(block.Id, Date);
        Assert.Null(_completions.Get(block.Id, Date));

        _completions.Remove(block.Id, Date); // no throw for an absent row
    }

    [Fact]
    public void GetBetween_ReturnsOnlyOccurrencesInsideTheRange()
    {
        var block = AddCommitment();
        var recurring = CalendarBlock.CreateFixedCommitment(
            "AP Economics", Date.AddDays(-14), new TimeOnly(8, 30), new TimeOnly(9, 45), Now,
            BeBoosted.Domain.Scheduling.RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _blocks.Add(recurring);
        _completions.Add(new CommitmentCompletion(block.Id, Date, Now));
        _completions.Add(new CommitmentCompletion(recurring.Id, Date.AddDays(-7), Now));
        _completions.Add(new CommitmentCompletion(recurring.Id, Date, Now));

        var inRange = _completions.GetBetween(Date.AddDays(-1), Date.AddDays(6));

        Assert.Equal(2, inRange.Count);
        Assert.All(inRange, c => Assert.Equal(Date, c.OccurrenceDate));
    }

    [Fact]
    public void DeletingTheBlock_CascadesToItsCompletions()
    {
        var block = AddCommitment();
        _completions.Add(new CommitmentCompletion(block.Id, Date, Now));

        _blocks.Delete(block.Id);

        Assert.Null(_completions.Get(block.Id, Date));
    }

    public void Dispose() => _database.Dispose();
}
