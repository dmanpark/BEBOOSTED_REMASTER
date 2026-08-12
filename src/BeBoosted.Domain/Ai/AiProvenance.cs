namespace BeBoosted.Domain.Ai;

public enum AiOperationKind
{
    TaskExtraction = 0,
    MetadataSuggestion = 1,
    PlanningProposal = 2,
    ProjectAnswer = 3,
}

/// <summary>
/// The provenance record behind every AI-derived item: which operation produced it and
/// which project resources fed it. When a source changes or disappears, the record —
/// and therefore everything derived from it — becomes Needs review.
/// </summary>
public sealed class AiProvenance
{
    private AiProvenance(
        AiProvenanceId id,
        AiOperationKind operation,
        IReadOnlyList<ResourceId> sourceResourceIds,
        bool needsReview,
        DateTimeOffset createdAt)
    {
        Id = id;
        Operation = operation;
        SourceResourceIds = sourceResourceIds;
        NeedsReview = needsReview;
        CreatedAt = createdAt;
    }

    public AiProvenanceId Id { get; }

    public AiOperationKind Operation { get; }

    public IReadOnlyList<ResourceId> SourceResourceIds { get; }

    public bool NeedsReview { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public static AiProvenance Create(
        AiOperationKind operation, IEnumerable<ResourceId> sourceResourceIds, DateTimeOffset now)
        => new(AiProvenanceId.New(), operation, [.. sourceResourceIds], needsReview: false, now);

    public static AiProvenance Rehydrate(
        AiProvenanceId id,
        AiOperationKind operation,
        IReadOnlyList<ResourceId> sourceResourceIds,
        bool needsReview,
        DateTimeOffset createdAt)
        => new(id, operation, sourceResourceIds, needsReview, createdAt);

    public void MarkNeedsReview() => NeedsReview = true;

    public void ClearNeedsReview() => NeedsReview = false;
}

/// <summary>A persisted project-scoped answer, citing the exact resources it used.</summary>
public sealed record AiAnswer(
    Guid Id,
    AiProvenanceId ProvenanceId,
    ProjectId ProjectId,
    string Question,
    string AnswerText,
    DateTimeOffset CreatedAt);
