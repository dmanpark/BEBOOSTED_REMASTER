using BeBoosted.Domain.Prioritization;

namespace BeBoosted.Application.Prioritization;

public interface IPrioritizationRepository
{
    /// <summary>
    /// Persists a finished session as one atomic write: the decisions append to the
    /// comparison history and the ranks replace the period's ranking together, so an
    /// interruption can never keep history for a ranking that was not saved.
    /// </summary>
    void SaveSessionResult(
        string periodKey,
        IReadOnlyList<ComparisonDecision> decisions,
        IReadOnlyList<PriorityRank> ranks);

    void SaveDecisions(IReadOnlyList<ComparisonDecision> decisions);

    IReadOnlyList<ComparisonDecision> GetDecisions(string periodKey);

    /// <summary>Replaces the ranking for the period atomically.</summary>
    void ReplaceRanks(string periodKey, IReadOnlyList<PriorityRank> ranks);

    IReadOnlyList<PriorityRank> GetRanks(string periodKey);
}
