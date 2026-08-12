using BeBoosted.Domain.Projects;

namespace BeBoosted.Application.Projects;

/// <summary>
/// Local indexing port. Implementations extract searchable text from a resource and
/// record it via <see cref="IResourceRepository.SetIndexText"/>, then mark the resource
/// Indexed or Failed. Project-scoped retrieval (Phase 7 AI) searches only this index.
/// </summary>
public interface IResourceIndexer
{
    void Index(Resource resource);
}
