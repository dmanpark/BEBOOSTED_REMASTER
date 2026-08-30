using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;

namespace BeBoosted.Tests.Domain;

public sealed class ComparisonSessionTests
{
    private static readonly PlanningPeriod Period = PlanningPeriod.ForWeek(new DateOnly(2026, 8, 11));

    private static List<TaskId> Ids(int count)
        => Enumerable.Range(0, count).Select(_ => TaskId.New()).ToList();

    private static ComparisonSession Run(IReadOnlyList<TaskId> ids, params ComparisonResult[] answers)
    {
        var session = new ComparisonSession(Period, ids);
        foreach (var answer in answers)
        {
            session.Record(answer);
        }

        return session;
    }

    [Fact]
    public void TwoTasks_ResolveWithOneComparison()
    {
        var ids = Ids(2);
        var session = new ComparisonSession(Period, ids);

        Assert.False(session.IsComplete);
        Assert.Equal((ids[1], ids[0]), session.CurrentComparison);

        session.Record(ComparisonResult.LeftWins); // the second task wins
        Assert.True(session.IsComplete);

        var ranking = session.BuildRanking();
        Assert.Equal([(ids[1], 1), (ids[0], 2)], ranking.Select(r => (r.TaskId, r.Rank)));
    }

    [Fact]
    public void Tie_SharesAnOrdinalRank_AndRanksStayDense()
    {
        var ids = Ids(4);
        var session = Run(
            ids,
            ComparisonResult.RightWins,   // B vs A → A stays first
            ComparisonResult.LeftWins,    // C vs B → C above B
            ComparisonResult.Tie,         // C vs A → real tie, C joins A's group
            ComparisonResult.RightWins);  // D vs B → D last

        Assert.True(session.IsComplete);
        var ranking = session.BuildRanking();
        var byTask = ranking.ToDictionary(r => r.TaskId, r => r.Rank);
        Assert.Equal(1, byTask[ids[0]]);
        Assert.Equal(1, byTask[ids[2]]); // tied with the first task
        Assert.Equal(2, byTask[ids[1]]);
        Assert.Equal(3, byTask[ids[3]]); // dense: 1,1,2,3
    }

    [Fact]
    public void TooToughToDecide_ContinuesImmediately_WithNeitherTaskLosing()
    {
        var ids = Ids(3);
        var session = new ComparisonSession(Period, ids);

        session.Record(ComparisonResult.Tie); // B ties A immediately
        Assert.False(session.IsComplete);     // C still needs placing
        Assert.Equal(ids[2], session.CurrentComparison!.Value.Left);

        session.Record(ComparisonResult.Tie); // C ties too
        Assert.True(session.IsComplete);
        Assert.All(session.BuildRanking(), r => Assert.Equal(1, r.Rank));
    }

    [Fact]
    public void Undo_RestoresThePreviousQuestionExactly()
    {
        var ids = Ids(4);
        var session = new ComparisonSession(Period, ids);
        session.Record(ComparisonResult.LeftWins);
        var questionBefore = session.CurrentComparison;
        session.Record(ComparisonResult.RightWins);

        Assert.True(session.Undo());
        Assert.Equal(questionBefore, session.CurrentComparison);
        Assert.Equal(1, session.AnsweredCount);

        // Redo differently: state is a pure function of the answer log.
        session.Record(ComparisonResult.RightWins);
        var replay = Run(ids, ComparisonResult.LeftWins, ComparisonResult.RightWins);
        Assert.Equal(replay.CurrentComparison, session.CurrentComparison);
    }

    [Fact]
    public void Undo_WithNoAnswers_ReturnsFalse()
    {
        var session = new ComparisonSession(Period, Ids(2));
        Assert.False(session.Undo());
    }

    [Fact]
    public void BuildPlanNow_RanksUnplacedTasksAtTheTrailingSharedOrdinal()
    {
        var ids = Ids(5);
        var session = new ComparisonSession(Period, ids);
        session.Record(ComparisonResult.LeftWins); // B placed above A; C, D, E never compared

        var ranking = session.BuildRanking();
        var byTask = ranking.ToDictionary(r => r.TaskId, r => r.Rank);
        Assert.Equal(1, byTask[ids[1]]);
        Assert.Equal(2, byTask[ids[0]]);
        Assert.Equal(3, byTask[ids[2]]);
        Assert.Equal(3, byTask[ids[3]]);
        Assert.Equal(3, byTask[ids[4]]);
    }

    [Fact]
    public void Tiers_SplitIntoThirds_WithoutSplittingTiedGroups()
    {
        var ids = Ids(3);
        var session = Run(ids, ComparisonResult.RightWins, ComparisonResult.RightWins);
        // Order: A, B, C → one per tier.
        var tiers = session.BuildRanking().OrderBy(r => r.Rank).Select(r => r.Tier).ToList();
        Assert.Equal([PlanningTier.ProtectNow, PlanningTier.AdvanceNext, PlanningTier.CanWait], tiers);

        // All tied → all share rank 1 and the top tier.
        var tied = Run(Ids(3), ComparisonResult.Tie, ComparisonResult.Tie);
        Assert.All(tied.BuildRanking(), r => Assert.Equal(PlanningTier.ProtectNow, r.Tier));
    }

    [Fact]
    public void Progress_StaysInBounds_AndCompletesAtOne()
    {
        var ids = Ids(6);
        var session = new ComparisonSession(Period, ids);
        var first = session.Progress;
        Assert.InRange(first, 0, 1);

        var random = new Random(42);
        while (!session.IsComplete)
        {
            session.Record((ComparisonResult)random.Next(3));
            Assert.InRange(session.Progress, 0, 1);
        }

        Assert.Equal(1, session.Progress);
        Assert.True(session.Progress > first);
    }

    [Fact]
    public void QuestionCount_StaysNearNLogN()
    {
        var ids = Ids(8);
        var session = new ComparisonSession(Period, ids);
        var count = 0;
        while (!session.IsComplete)
        {
            session.Record(ComparisonResult.RightWins); // worst-ish case: strictly ordered input
            count++;
        }

        Assert.InRange(count, 7, 8 * 3); // n-1 minimum; well under n·log₂n·bound
    }

    [Fact]
    public void SameAnswers_ProduceTheSameQuestionSequence()
    {
        var ids = Ids(5);
        var answers = new[]
        {
            ComparisonResult.LeftWins, ComparisonResult.Tie, ComparisonResult.RightWins,
            ComparisonResult.LeftWins,
        };

        var first = new ComparisonSession(Period, ids);
        var second = new ComparisonSession(Period, ids);
        foreach (var answer in answers)
        {
            Assert.Equal(first.CurrentComparison, second.CurrentComparison);
            if (first.IsComplete)
            {
                break;
            }

            first.Record(answer);
            second.Record(answer);
        }

        Assert.Equal(
            first.BuildRanking().Select(r => (r.TaskId, r.Rank)),
            second.BuildRanking().Select(r => (r.TaskId, r.Rank)));
    }

    [Fact]
    public void SingleCandidate_CompletesInstantlyWithRankOne()
    {
        var session = new ComparisonSession(Period, Ids(1));
        Assert.True(session.IsComplete);
        Assert.Equal(1, session.BuildRanking().Single().Rank);
    }

    [Fact]
    public void EmptyCandidates_AreRejected()
        => Assert.Throws<DomainException>(() => new ComparisonSession(Period, []));

    [Fact]
    public void PlanningPeriod_KeysAreScopedAndStable()
    {
        Assert.Equal("today:2026-08-11", PlanningPeriod.ForToday(new DateOnly(2026, 8, 11)).Key);
        Assert.Equal("week:2026-08-10", PlanningPeriod.ForWeek(new DateOnly(2026, 8, 13)).Key);
        Assert.NotEqual(
            PlanningPeriod.ForToday(new DateOnly(2026, 8, 11)).Key,
            PlanningPeriod.ForWeek(new DateOnly(2026, 8, 11)).Key);
    }

    // ---- Seeded sessions: insert into an existing order ----

    private static IReadOnlyList<IReadOnlyList<TaskId>> Seed(params TaskId[] ids)
        => ids.Select(id => (IReadOnlyList<TaskId>)new[] { id }).ToList();

    [Fact]
    public void SeededSession_WithNothingToInsert_AsksNothing_AndRanksTheSeed()
    {
        var ids = Ids(3);
        var session = new ComparisonSession(Period, Seed(ids[0], ids[1], ids[2]), []);

        Assert.True(session.IsComplete);
        var byTask = session.BuildRanking().ToDictionary(r => r.TaskId, r => r.Rank);
        Assert.Equal(1, byTask[ids[0]]);
        Assert.Equal(2, byTask[ids[1]]);
        Assert.Equal(3, byTask[ids[2]]);
    }

    [Fact]
    public void SeededSession_InsertingOneTask_PreservesTheExistingOrder()
    {
        var ids = Ids(4); // A, B, C already ranked in that order; D is new
        var session = new ComparisonSession(Period, Seed(ids[0], ids[1], ids[2]), [ids[3]]);

        // Bisect: D vs B (mid of [0,3)). D loses, so it sits below B.
        Assert.Equal(ids[3], session.CurrentComparison!.Value.Left);
        Assert.Equal(ids[1], session.CurrentComparison.Value.Right);
        while (!session.IsComplete)
        {
            session.Record(ComparisonResult.RightWins); // D keeps losing -> lands last
        }

        var byTask = session.BuildRanking().ToDictionary(r => r.TaskId, r => r.Rank);
        Assert.Equal(1, byTask[ids[0]]);
        Assert.Equal(2, byTask[ids[1]]);
        Assert.Equal(3, byTask[ids[2]]);
        Assert.Equal(4, byTask[ids[3]]);
    }

    [Fact]
    public void SeededSession_KeepsTiedGroupsSharingAnOrdinal()
    {
        var ids = Ids(3);
        IReadOnlyList<IReadOnlyList<TaskId>> seed = [new[] { ids[0], ids[1] }]; // tied at #1
        var session = new ComparisonSession(Period, seed, [ids[2]]);

        session.Record(ComparisonResult.RightWins); // C loses to the tied group

        var byTask = session.BuildRanking().ToDictionary(r => r.TaskId, r => r.Rank);
        Assert.Equal(1, byTask[ids[0]]);
        Assert.Equal(1, byTask[ids[1]]);
        Assert.Equal(2, byTask[ids[2]]);
    }

    [Fact]
    public void SeededSession_IgnoresACandidateAlreadyInTheSeed()
    {
        var ids = Ids(2);
        var session = new ComparisonSession(Period, Seed(ids[0], ids[1]), [ids[0]]);

        Assert.True(session.IsComplete); // the seed already places it
        Assert.Equal(2, session.BuildRanking().Count);
    }

    [Fact]
    public void SeededSession_RejectsOnlyWhenSeedAndCandidatesAreBothEmpty()
    {
        Assert.Throws<DomainException>(() => new ComparisonSession(Period, [], []));

        var ids = Ids(1);
        var seeded = new ComparisonSession(Period, Seed(ids[0]), []); // legal
        Assert.True(seeded.IsComplete);
    }

    [Fact]
    public void SeededSession_SupportsUndo()
    {
        var ids = Ids(4);
        var session = new ComparisonSession(Period, Seed(ids[0], ids[1], ids[2]), [ids[3]]);
        var first = session.CurrentComparison;

        session.Record(ComparisonResult.LeftWins);
        Assert.True(session.Undo());

        Assert.Equal(first, session.CurrentComparison);
        Assert.Equal(0, session.AnsweredCount);
    }
}
