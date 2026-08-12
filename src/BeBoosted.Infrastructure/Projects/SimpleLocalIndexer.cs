using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Infrastructure.Projects;

/// <summary>
/// Deterministic local indexer: notes index their content, links their URL, and
/// documents/images their title and original file name (no byte-level text extraction
/// in v1). Verifies stored bytes exist for documents/images; missing bytes → Failed.
/// </summary>
public sealed class SimpleLocalIndexer(IResourceRepository resources, IResourceStorage storage, IClock clock)
    : IResourceIndexer
{
    public void Index(Resource resource)
    {
        switch (resource.Kind)
        {
            case ResourceKind.Note:
                resources.SetIndexText(resource.Id, $"{resource.Title}\n{resource.Content}");
                resource.MarkIndexed(clock.Now);
                break;
            case ResourceKind.Link:
                resources.SetIndexText(resource.Id, $"{resource.Title}\n{resource.Url}");
                resource.MarkIndexed(clock.Now);
                break;
            case ResourceKind.Document:
            case ResourceKind.Image:
                if (resource.StoredPath is { } storedPath && storage.Exists(storedPath))
                {
                    resources.SetIndexText(resource.Id, $"{resource.Title}\n{resource.OriginalFileName}");
                    resource.MarkIndexed(clock.Now);
                }
                else
                {
                    resource.MarkIndexFailed(clock.Now);
                }

                break;
        }
    }
}
