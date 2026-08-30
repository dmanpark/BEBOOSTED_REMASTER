using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Prioritization;
using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Prioritization;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Prioritization;

/// <summary>
/// A repeat sort asks only about unranked work, and a re-rank re-places one task,
/// both by seeding the session from the saved ordering. Verified against real
/// SQLite and re-read through fresh repositories.
/// </summary>
public sealed class PrioritySortServiceTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private static readonly PlanningPeriod Period = PlanningPeriod.ForToday(new DateOnly(2026, 8, 11));

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqlitePrioritizationRepository _ranks;

    public PrioritySortServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _ranks = new SqlitePrioritizationRepository(_database.Factory);
    }

    private PrioritySortService CreateService() => new(_ranks, _clock);

    private static void AnswerAll(ComparisonSession session, ComparisonResult result)
    {
        while (!session.IsComplete)
        {
            session.Record(result);
        }
    }

    /// <summary>Ranks re-read through a fresh repository — the state after a restart.</summary>
    private Dictionary<TaskId, int> RestartedRanks()
        => new SqlitePrioritizationRepository(_database.Factory)
            .GetRanks(Period.Key)
            .ToDictionary(r => r.TaskId, r => r.Rank);

    private void SaveRanks(params TaskId[] inOrder)
        => _ranks.ReplaceRanks(
            Period.Key,
            [.. inOrder.Select((id, index) => new PriorityRank(
                id,
                index + 1,
                index == 0 ? PlanningTier.ProtectNow
                    : index == 1 ? PlanningTier.AdvanceNext
                        : PlanningTier.CanWait))]);

    [Fact]
    public void StartIncrementalSession_WithNoSavedRanks_BehavesLikeAFullSort()
    {
        var ids = new[] { TaskId.New(), TaskId.New(), TaskId.New() };

        var session = CreateService().StartIncrementalSession(Period, ids);

        Assert.False(session.IsComplete); // every task still needs placing
        AnswerAll(session, ComparisonResult.RightWins);
        CreateService().Complete(session);
        Assert.Equal(3, RestartedRanks().Count);
    }

    [Fact]
    public void StartIncrementalSession_AsksOnlyAboutUnrankedTasks()
    {
        var ranked = new[] { TaskId.New(), TaskId.New(), TaskId.New() };
        SaveRanks(ranked);
        var captured = TaskId.New();

        var session = CreateService().StartIncrementalSession(Period, [.. ranked, captured]);

        // Every question must involve the new task — the settled three are never re-asked.
        var asked = 0;
        while (session.CurrentComparison is { } comparison)
        {
            Assert.Equal(captured, comparison.Left);
            asked++;
            session.Record(ComparisonResult.RightWins); // the new task keeps losing
        }

        Assert.True(asked <= 2); // log2 of three groups, not a full re-sort
        CreateService().Complete(session);
        var after = RestartedRanks();
        Assert.Equal(1, after[ranked[0]]);
        Assert.Equal(2, after[ranked[1]]);
        Assert.Equal(3, after[ranked[2]]);
        Assert.Equal(4, after[captured]);
    }

    [Fact]
    public void StartIncrementalSession_KeepsRanksOfTasksItDoesNotAskAbout()
    {
        var scheduled = TaskId.New();
        var inbox = TaskId.New();
        SaveRanks(scheduled, inbox);
        var captured = TaskId.New();

        var session = CreateService().StartIncrementalSession(Period, [scheduled, inbox, captured]);
        AnswerAll(session, ComparisonResult.RightWins);
        CreateService().Complete(session);

        // The already-scheduled task keeps its rank instead of being wiped.
        var after = RestartedRanks();
        Assert.Equal(1, after[scheduled]);
        Assert.Equal(2, after[inbox]);
    }

    [Fact]
    public void StartIncrementalSession_DropsRanksOfTasksThatAreNoLongerLive()
    {
        var gone = TaskId.New();
        var alive = TaskId.New();
        SaveRanks(gone, alive);
        var captured = TaskId.New();

        // `gone` was completed or deleted, so it is not in the live set.
        var session = CreateService().StartIncrementalSession(Period, [alive, captured]);
        AnswerAll(session, ComparisonResult.RightWins);
        CreateService().Complete(session);

        var after = RestartedRanks();
        Assert.DoesNotContain(gone, after.Keys);
        Assert.Equal(1, after[alive]);
        Assert.Equal(2, after[captured]);
    }

    [Fact]
    public void StartRerankSession_MovesOneTaskUp_AndLeavesTheRestInOrder()
    {
        var ids = new[] { TaskId.New(), TaskId.New(), TaskId.New() };
        SaveRanks(ids);

        // Re-rank the last task and let it win everything → it becomes #1.
        var session = CreateService().StartRerankSession(Period, ids[2], ids);
        AnswerAll(session, ComparisonResult.LeftWins);
        CreateService().Complete(session);

        var after = RestartedRanks();
        Assert.Equal(1, after[ids[2]]);
        Assert.Equal(2, after[ids[0]]);
        Assert.Equal(3, after[ids[1]]);
    }

    [Fact]
    public void StartRerankSession_MovesOneTaskDown()
    {
        var ids = new[] { TaskId.New(), TaskId.New(), TaskId.New() };
        SaveRanks(ids);

        var session = CreateService().StartRerankSession(Period, ids[0], ids);
        AnswerAll(session, ComparisonResult.RightWins); // the target keeps losing → last
        CreateService().Complete(session);

        var after = RestartedRanks();
        Assert.Equal(1, after[ids[1]]);
        Assert.Equal(2, after[ids[2]]);
        Assert.Equal(3, after[ids[0]]);
    }

    [Fact]
    public void StartRerankSession_NeverComparesTheTargetWithItself()
    {
        var ids = new[] { TaskId.New(), TaskId.New(), TaskId.New() };
        SaveRanks(ids);

        var session = CreateService().StartRerankSession(Period, ids[1], ids);

        while (session.CurrentComparison is { } comparison)
        {
            Assert.Equal(ids[1], comparison.Left);
            Assert.NotEqual(ids[1], comparison.Right);
            session.Record(ComparisonResult.LeftWins);
        }
    }

    public void Dispose() => _database.Dispose();
}
