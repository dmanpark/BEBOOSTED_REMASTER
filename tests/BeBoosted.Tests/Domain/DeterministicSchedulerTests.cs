using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Tests.Domain;

public sealed class DeterministicSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));
    private static readonly DateOnly Today = new(2026, 8, 11);
    private static readonly TimeOnly NowTime = new(14, 10);

    private static TaskItem Task(string title, int minutes, DateOnly? deadline = null, SchedulingConstraints? constraints = null)
        => TaskItem.Create(title, Now, estimatedDuration: TimeSpan.FromMinutes(minutes), deadline: deadline, constraints: constraints);

    private static SchedulerResult Plan(
        IReadOnlyList<TaskItem> tasks,
        IReadOnlyList<BlockOccurrence>? existing = null,
        IReadOnlyDictionary<TaskId, PriorityRank>? ranks = null,
        DateOnly? start = null,
        DateOnly? end = null)
        => DeterministicScheduler.Plan(
            tasks,
            existing ?? [],
            ranks ?? new Dictionary<TaskId, PriorityRank>(),
            start ?? Today,
            end ?? Today.AddDays(5),
            Today,
            NowTime);

    private static BlockOccurrence Fixed(DateOnly date, int fromHour, int fromMin, int toHour, int toMin)
    {
        var block = CalendarBlock.CreateFixedCommitment(
            "Busy", date, new TimeOnly(fromHour, fromMin), new TimeOnly(toHour, toMin), Now);
        return new BlockOccurrence(block, date);
    }

    [Theory]
    [InlineData(30, new[] { 30 })]
    [InlineData(90, new[] { 90 })]
    [InlineData(180, new[] { 90, 90 })]
    [InlineData(200, new[] { 90, 90, 20 })]
    [InlineData(100, new[] { 100 })] // 10-minute remainder folds into the last session
    public void SplitIntoSessions_CapsAt90AndFoldsTinyRemainders(int total, int[] expected)
        => Assert.Equal(
            expected.Select(m => TimeSpan.FromMinutes(m)),
            DeterministicScheduler.SplitIntoSessions(TimeSpan.FromMinutes(total)));

    [Fact]
    public void Plan_NeverStartsBeforeNowOnToday_AndSnapsToQuarterHours()
    {
        var result = Plan([Task("First", 60)]);

        var block = result.Blocks.Single();
        Assert.Equal(Today, block.Date);
        Assert.Equal(new TimeOnly(14, 15), block.StartTime); // now 14:10 snapped up
    }

    [Fact]
    public void Plan_SkipsBusyTime_FirstFit()
    {
        var busy = new[]
        {
            Fixed(Today, 14, 15, 16, 0),
            Fixed(Today, 16, 30, 17, 0),
        };

        var result = Plan([Task("Work", 60)], busy);

        var block = result.Blocks.Single();
        // 14:15–16:00 busy; 16:00–16:30 gap is too short for 60 min; next fit is 17:00.
        Assert.Equal(new TimeOnly(17, 0), block.StartTime);
    }

    [Fact]
    public void Plan_SchedulesHigherRankedTasksFirst()
    {
        var high = Task("High", 60);
        var low = Task("Low", 60);
        var ranks = new Dictionary<TaskId, PriorityRank>
        {
            [low.Id] = new(low.Id, 2, PlanningTier.AdvanceNext),
            [high.Id] = new(high.Id, 1, PlanningTier.ProtectNow),
        };

        var result = Plan([low, high], ranks: ranks);

        Assert.Equal("High", TitleOf(result, 0, [low, high]));
        Assert.True(result.Blocks[0].StartTime < result.Blocks[1].StartTime);
        Assert.Contains("Rank #1", result.Blocks[0].Why.Priority);
        Assert.Contains("above Low", result.Blocks[0].Why.Priority);
    }

    [Fact]
    public void Plan_SplitsLongTasksIntoLabeledSessions_WithoutOverlap()
    {
        var result = Plan([Task("Big project", 180)]);

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("Session 1 of 2", result.Blocks[0].SessionLabel);
        Assert.Equal("Session 2 of 2", result.Blocks[1].SessionLabel);
        Assert.True(result.Blocks[0].EndTime <= result.Blocks[1].StartTime
            || result.Blocks[0].Date != result.Blocks[1].Date);
    }

    [Fact]
    public void Plan_RespectsDeadlines_AndReportsUnplacedWithReason()
    {
        // Fill today completely after "now"; deadline is today, so nothing fits.
        var busy = new[] { Fixed(Today, 8, 0, 21, 0) };
        var result = Plan([Task("Urgent", 60, deadline: Today)], busy);

        Assert.Empty(result.Blocks);
        var unplaced = Assert.Single(result.Unplaced);
        Assert.Equal("Urgent", unplaced.Title);
        Assert.Contains("no open slot before", unplaced.Reason);
        Assert.Contains("60 min", unplaced.Reason);
    }

    [Fact]
    public void Plan_RespectsTimeOfDayConstraints()
    {
        var constrained = Task(
            "Evening only", 60,
            constraints: new SchedulingConstraints(earliestTime: new TimeOnly(18, 0)));

        var result = Plan([constrained]);

        Assert.Equal(new TimeOnly(18, 0), result.Blocks.Single().StartTime);
    }

    [Fact]
    public void Plan_OverflowsToTheNextDayWhenTodayIsFull()
    {
        var busy = new[] { Fixed(Today, 8, 0, 21, 0) };
        var result = Plan([Task("Flexible", 60)], busy);

        var block = result.Blocks.Single();
        Assert.Equal(Today.AddDays(1), block.Date);
        Assert.Equal(new TimeOnly(8, 0), block.StartTime);
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var tasks = new[] { Task("A", 45), Task("B", 90), Task("C", 30) };
        var busy = new[] { Fixed(Today, 15, 0, 16, 0) };

        var first = Plan(tasks, busy);
        var second = Plan(tasks, busy);

        Assert.Equal(
            first.Blocks.Select(b => (b.TaskId, b.Date, b.StartTime, b.EndTime)),
            second.Blocks.Select(b => (b.TaskId, b.Date, b.StartTime, b.EndTime)));
    }

    [Fact]
    public void Plan_ProposalsNeverOverlapEachOther()
    {
        var tasks = Enumerable.Range(0, 6).Select(i => Task($"T{i}", 90)).ToList();
        var result = Plan(tasks);

        var byDay = result.Blocks.GroupBy(b => b.Date);
        foreach (var day in byDay)
        {
            var ordered = day.OrderBy(b => b.StartTime).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                Assert.True(ordered[i].StartTime >= ordered[i - 1].EndTime);
            }
        }
    }

    private static string TitleOf(SchedulerResult result, int index, IReadOnlyList<TaskItem> tasks)
        => tasks.First(t => t.Id == result.Blocks[index].TaskId).Title;
}
