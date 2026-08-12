using BeBoosted.Domain;
using BeBoosted.Domain.Ai;

namespace BeBoosted.Application.Ai;

public interface IAiProvenanceRepository
{
    void Add(AiProvenance provenance);

    void Update(AiProvenance provenance);

    AiProvenance? GetById(AiProvenanceId id);

    /// <summary>Provenance records that used the given resource as a source.</summary>
    IReadOnlyList<AiProvenance> GetBySourceResource(ResourceId resourceId);

    void AddAnswer(AiAnswer answer);

    IReadOnlyList<AiAnswer> GetAnswersForProject(ProjectId projectId);

    AiAnswer? GetAnswerByProvenance(AiProvenanceId provenanceId);
}
