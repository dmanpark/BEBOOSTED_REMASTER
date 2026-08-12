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
    /// Persists a finished (or early-exited) session: its decisions append to the
    /// period's comparison history and its ranking replaces the period's ranks.
    /// </summary>
    public IReadOnlyList<PriorityRank> Complete(ComparisonSession session)
    {
        var decisions = session.Decisions
            .Select(d => new ComparisonDecision(
                ComparisonId.New(), session.Period, d.Left, d.Right, d.Result, clock.Now))
            .ToList();
        repository.SaveDecisions(decisions);

        var ranking = session.BuildRanking();
        repository.ReplaceRanks(session.Period.Key, ranking);
        return ranking;
    }

    public IReadOnlyList<PriorityRank> GetRanks(PlanningPeriod period)
        => repository.GetRanks(period.Key);

    public IReadOnlyDictionary<TaskId, PriorityRank> GetRankLookup(PlanningPeriod period)
        => repository.GetRanks(period.Key).ToDictionary(r => r.TaskId);
}
