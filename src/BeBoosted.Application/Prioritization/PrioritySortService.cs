using BeBoosted.Application.Abstractions;
using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;

namespace BeBoosted.Application.Prioritization;

/// <summary>
/// Priority Sort use cases. Subjective comparisons produce period-scoped ordinal ranks;
/// deadlines and feasibility stay entirely outside this service.
/// </summary>
public sealed class PrioritySortService(IPrioritizationRepository repository, IClock clock)
{
    public ComparisonSession StartSession(PlanningPeriod period, IEnumerable<TaskId> candidates)
        => new(period, candidates);

    /// <summary>
    /// A sort that asks only about work with no rank yet: the saved ordering seeds the
    /// session, so answers place the new tasks among the existing ones. With no saved
    /// ranks this is exactly a full first sort.
    /// </summary>
    public ComparisonSession StartIncrementalSession(
        PlanningPeriod period, IReadOnlyList<TaskId> liveTasks)
    {
        var ranked = repository.GetRanks(period.Key).Select(r => r.TaskId).ToHashSet();
        return new ComparisonSession(
            period,
            SeedFor(period, liveTasks, exclude: null),
            liveTasks.Where(id => !ranked.Contains(id)));
    }

    /// <summary>Re-places one already-ranked task among the others.</summary>
    public ComparisonSession StartRerankSession(
        PlanningPeriod period, TaskId target, IReadOnlyList<TaskId> liveTasks)
        => new(period, SeedFor(period, liveTasks, exclude: target), [target]);

    /// <summary>
    /// The saved ranking as tied groups in rank order, pruned to tasks that are still
    /// live. Deleted and completed work never anchors a comparison.
    /// </summary>
    private List<IReadOnlyList<TaskId>> SeedFor(
        PlanningPeriod period, IReadOnlyList<TaskId> liveTasks, TaskId? exclude)
    {
        var live = liveTasks.ToHashSet();
        return repository.GetRanks(period.Key)
            .Where(r => live.Contains(r.TaskId) && r.TaskId != exclude)
            .GroupBy(r => r.Rank)
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<TaskId>)g.Select(r => r.TaskId).ToList())
            .ToList();
    }

    /// <summary>
    /// Persists a finished (or early-exited) session: its decisions append to the
    /// period's comparison history and its ranking replaces the period's ranks.
    /// </summary>
    public IReadOnlyList<PriorityRank> Complete(ComparisonSession session)
    {
        var decisions = session.Decisions
            .Select(d => new ComparisonDecision(
                ComparisonId.New(), session.Period, d.Left, d.Right, d.Result, clock.Now))
            .ToList();
        var ranking = session.BuildRanking();
        // One atomic write: history must never outlive a ranking that was not saved.
        repository.SaveSessionResult(session.Period.Key, decisions, ranking);
        return ranking;
    }

    public IReadOnlyList<PriorityRank> GetRanks(PlanningPeriod period)
        => repository.GetRanks(period.Key);

    public IReadOnlyDictionary<TaskId, PriorityRank> GetRankLookup(PlanningPeriod period)
        => repository.GetRanks(period.Key).ToDictionary(r => r.TaskId);
}
