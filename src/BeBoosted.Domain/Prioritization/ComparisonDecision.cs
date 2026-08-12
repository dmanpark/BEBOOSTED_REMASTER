namespace BeBoosted.Domain.Prioritization;

public enum ComparisonResult
{
    LeftWins = 0,
    RightWins = 1,

    /// <summary>"Too tough to decide" — a real answer. Neither task loses.</summary>
    Tie = 2,
}

/// <summary>One recorded pairwise choice within a planning period.</summary>
public sealed record ComparisonDecision(
    ComparisonId Id,
    PlanningPeriod Period,
    TaskId LeftTaskId,
    TaskId RightTaskId,
    ComparisonResult Result,
    DateTimeOffset DecidedAt);
