using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Prioritization;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Prioritization;

public sealed class SqlitePrioritizationRepositoryTests : IDisposable
{
    private static readonly PlanningPeriod Today = PlanningPeriod.ForToday(new DateOnly(2026, 8, 11));
    private static readonly PlanningPeriod Week = PlanningPeriod.ForWeek(new DateOnly(2026, 8, 11));
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));

    private readonly TempDatabase _database = new();
    private readonly SqlitePrioritizationRepository _repository;

    public SqlitePrioritizationRepositoryTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _repository = new SqlitePrioritizationRepository(_database.Factory);
    }

    [Fact]
    public void Decisions_RoundTripPerPeriod()
    {
        var left = TaskId.New();
        var right = TaskId.New();
        _repository.SaveDecisions(
        [
            new ComparisonDecision(ComparisonId.New(), Today, left, right, ComparisonResult.Tie, Now),
            new ComparisonDecision(ComparisonId.New(), Week, left, right, ComparisonResult.LeftWins, Now.AddMinutes(1)),
        ]);

        var todayDecisions = _repository.GetDecisions(Today.Key);
        Assert.Single(todayDecisions);
        Assert.Equal(ComparisonResult.Tie, todayDecisions[0].Result);
        Assert.Equal(Today, todayDecisions[0].Period);
        Assert.Equal(left, todayDecisions[0].LeftTaskId);

        Assert.Single(_repository.GetDecisions(Week.Key));
    }

    [Fact]
    public void Ranks_AreReplacedAtomicallyPerPeriod()
    {
        var a = TaskId.New();
        var b = TaskId.New();
        _repository.ReplaceRanks(Today.Key, [new PriorityRank(a, 1, PlanningTier.ProtectNow)]);
        _repository.ReplaceRanks(Week.Key, [new PriorityRank(b, 1, PlanningTier.ProtectNow)]);

        _repository.ReplaceRanks(
            Today.Key,
            [
                new PriorityRank(b, 1, PlanningTier.ProtectNow),
                new PriorityRank(a, 2, PlanningTier.CanWait),
            ]);

        var todayRanks = _repository.GetRanks(Today.Key);
        Assert.Equal(2, todayRanks.Count);
        Assert.Equal(b, todayRanks[0].TaskId);
        Assert.Equal(PlanningTier.CanWait, todayRanks[1].Tier);

        // Week ranks are untouched: periods are fully independent.
        Assert.Equal(b, _repository.GetRanks(Week.Key).Single().TaskId);
    }

    [Fact]
    public void SaveSessionResult_WritesDecisionsAndRanksTogether()
    {
        var a = TaskId.New();
        var b = TaskId.New();

        _repository.SaveSessionResult(
            Today.Key,
            [new ComparisonDecision(ComparisonId.New(), Today, a, b, ComparisonResult.LeftWins, Now)],
            [new PriorityRank(a, 1, PlanningTier.ProtectNow), new PriorityRank(b, 2, PlanningTier.CanWait)]);

        Assert.Single(_repository.GetDecisions(Today.Key));
        Assert.Equal(2, _repository.GetRanks(Today.Key).Count);
    }

    /// <summary>
    /// One atomic write: if the ranks half fails, the comparison history must not
    /// keep rows for a ranking that was never saved, and the old ranking survives.
    /// </summary>
    [Fact]
    public void SaveSessionResult_IsAllOrNothing()
    {
        var a = TaskId.New();
        var b = TaskId.New();
        _repository.ReplaceRanks(Today.Key, [new PriorityRank(a, 1, PlanningTier.ProtectNow)]);

        // The duplicate task violates the ranks primary key after the decisions inserted.
        Assert.ThrowsAny<Exception>(() => _repository.SaveSessionResult(
            Today.Key,
            [new ComparisonDecision(ComparisonId.New(), Today, a, b, ComparisonResult.LeftWins, Now)],
            [
                new PriorityRank(a, 1, PlanningTier.ProtectNow),
                new PriorityRank(a, 2, PlanningTier.CanWait),
            ]));

        Assert.Empty(_repository.GetDecisions(Today.Key));
        var kept = Assert.Single(_repository.GetRanks(Today.Key));
        Assert.Equal(1, kept.Rank);
    }

    public void Dispose() => _database.Dispose();
}
