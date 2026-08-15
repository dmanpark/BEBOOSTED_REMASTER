using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Calendar;

/// <summary>
/// Block mutation and completion reconciliation must share one real SQLite
/// transaction: any failure mid-mutation leaves the persisted block and every
/// completion row exactly as they were.
/// </summary>
public sealed class CalendarMutationAtomicityTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private static readonly DateOnly Date = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteCalendarBlockRepository _blocks;
    private readonly SqliteCommitmentCompletionRepository _completions;

    public CalendarMutationAtomicityTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _completions = new SqliteCommitmentCompletionRepository(_database.Factory);
    }

    private CalendarBlock AddCompletedCommitment()
    {
        var block = CalendarBlock.CreateFixedCommitment(
            "Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0), _clock.Now);
        _blocks.Add(block);
        _completions.Add(new CommitmentCompletion(block.Id, Date, _clock.Now));
        return block;
    }

    [Fact]
    public void SqliteCalendarMutations_CommitsEverything_OnSuccess()
    {
        var block = AddCompletedCommitment();
        var mutations = new SqliteCalendarMutations(_database.Factory);

        mutations.Execute((blocks, completions) =>
        {
            block.Rename("Stats homework", _clock.Now);
            blocks.Update(block);
            completions.Remove(block.Id, Date);
            completions.Add(new CommitmentCompletion(block.Id, Date, _clock.Now.AddHours(1)));
        });

        Assert.Equal("Stats homework", _blocks.GetById(block.Id)!.Title);
        Assert.Equal(_clock.Now.AddHours(1), _completions.Get(block.Id, Date)!.CompletedAt);
    }

    [Fact]
    public void SqliteCalendarMutations_RollsBackEverything_WhenTheMutationThrows()
    {
        var block = AddCompletedCommitment();
        var mutations = new SqliteCalendarMutations(_database.Factory);

        Assert.Throws<InvalidOperationException>(() => mutations.Execute((blocks, completions) =>
        {
            block.Rename("Stats homework", _clock.Now);
            blocks.Update(block);
            completions.Remove(block.Id, Date);
            throw new InvalidOperationException("injected failure");
        }));

        // Both writes inside the transaction are gone.
        Assert.Equal("Stats HW", _blocks.GetById(block.Id)!.Title);
        Assert.NotNull(_completions.Get(block.Id, Date));
    }

    [Fact]
    public void UpdateFixedCommitment_FailureDuringReconciliation_RollsBackTheBlockEdit()
    {
        var block = AddCompletedCommitment();
        var service = CreateService(new FailingCompletionWriteMutations(_database.Factory));

        // A date move + rename: the block write succeeds inside the transaction,
        // then carrying the completion to the new date fails.
        Assert.Throws<InvalidOperationException>(() => service.UpdateFixedCommitment(
            block.Id, "Renamed", Date.AddDays(2), new TimeOnly(16, 0), new TimeOnly(17, 0),
            null, null));

        var persisted = _blocks.GetById(block.Id)!;
        Assert.Equal("Stats HW", persisted.Title);
        Assert.Equal(Date, persisted.Date);
        Assert.NotNull(_completions.Get(block.Id, Date));
        Assert.Null(_completions.Get(block.Id, Date.AddDays(2)));
    }

    [Fact]
    public void MoveBlock_FailureWhileCarryingTheCompletion_RollsBackTheMove()
    {
        var block = AddCompletedCommitment();
        var service = CreateService(new FailingCompletionWriteMutations(_database.Factory));

        Assert.Throws<InvalidOperationException>(
            () => service.MoveBlock(block.Id, Date.AddDays(2), new TimeOnly(16, 0)));

        var persisted = _blocks.GetById(block.Id)!;
        Assert.Equal(Date, persisted.Date);
        Assert.NotNull(_completions.Get(block.Id, Date));
    }

    private CalendarService CreateService(ICalendarMutations mutations)
        => new(_blocks, _completions, mutations, new SqliteTaskRepository(_database.Factory), _clock);

    /// <summary>
    /// A real single-connection transaction whose completion writes fail — proves the
    /// service performs the block write inside the same transaction, because the
    /// failure must roll it back.
    /// </summary>
    private sealed class FailingCompletionWriteMutations(SqliteConnectionFactory factory) : ICalendarMutations
    {
        public void Execute(Action<ICalendarBlockRepository, ICommitmentCompletionRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new SqliteCalendarBlockRepository(connection, transaction),
                new FailingWrites(new SqliteCommitmentCompletionRepository(connection, transaction)));
            transaction.Commit();
        }
    }

    private sealed class FailingWrites(ICommitmentCompletionRepository inner) : ICommitmentCompletionRepository
    {
        public void Add(CommitmentCompletion completion)
            => throw new InvalidOperationException("injected failure");

        public void Remove(CalendarBlockId blockId, DateOnly occurrenceDate)
            => throw new InvalidOperationException("injected failure");

        public CommitmentCompletion? Get(CalendarBlockId blockId, DateOnly occurrenceDate)
            => inner.Get(blockId, occurrenceDate);

        public IReadOnlyList<CommitmentCompletion> GetForBlock(CalendarBlockId blockId)
            => inner.GetForBlock(blockId);

        public IReadOnlyList<CommitmentCompletion> GetBetween(DateOnly from, DateOnly to)
            => inner.GetBetween(from, to);
    }

    public void Dispose() => _database.Dispose();
}
