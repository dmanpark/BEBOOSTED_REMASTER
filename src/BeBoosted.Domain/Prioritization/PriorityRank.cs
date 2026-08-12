namespace BeBoosted.Domain.Prioritization;

public enum PlanningTier
{
    ProtectNow = 0,
    AdvanceNext = 1,
    CanWait = 2,
}

/// <summary>
/// A period-scoped ordinal rank. Ranks express ordering, not distance — tied tasks share
/// an ordinal and ranks are dense (1, 2, 2, 3). No decimal scores exist anywhere.
/// </summary>
public sealed record PriorityRank(TaskId TaskId, int Rank, PlanningTier Tier);
