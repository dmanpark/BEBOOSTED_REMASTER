using BeBoosted.Domain.Prioritization;

namespace BeBoosted.Application.Prioritization;

public interface IPrioritizationRepository
{
    void SaveDecisions(IReadOnlyList<ComparisonDecision> decisions);

    IReadOnlyList<ComparisonDecision> GetDecisions(string periodKey);

    /// <summary>Replaces the ranking for the period atomically.</summary>
    void ReplaceRanks(string periodKey, IReadOnlyList<PriorityRank> ranks);

    IReadOnlyList<PriorityRank> GetRanks(string periodKey);
}
