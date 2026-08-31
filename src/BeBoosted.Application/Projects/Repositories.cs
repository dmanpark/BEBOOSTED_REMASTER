using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Application.Projects;

public interface IProjectRepository
{
    void Add(Project project);

    void Update(Project project);

    void Delete(ProjectId id);

    Project? GetById(ProjectId id);

    IReadOnlyList<Project> GetAll();
}

public interface IProjectFileRepository
{
    void Add(ProjectFile file);

    void Update(ProjectFile file);

    void Delete(ProjectFileId id);

    ProjectFile? GetById(ProjectFileId id);

    IReadOnlyList<ProjectFile> GetForProject(ProjectId projectId);
}

/// <summary>
/// The one level of containers inside a File. Deliberately the same surface
/// <see cref="IProjectFileRepository"/> exposes: a group is a row of its own, so rename,
/// delete, and ordering work the way the rest of the model already works.
/// </summary>
public interface IResourceGroupRepository
{
    void Add(ResourceGroup group);

    void Update(ResourceGroup group);

    /// <summary>
    /// Removes the group row only. Its resources survive and become loose — the database
    /// clears their membership with ON DELETE SET NULL. Destructive deletion of the
    /// members is a separate, explicit act.
    /// </summary>
    void Delete(ResourceGroupId id);

    ResourceGroup? GetById(ResourceGroupId id);

    IReadOnlyList<ResourceGroup> GetForFile(ProjectFileId fileId);
}

public interface IResourceRepository
{
    void Add(Resource resource);

    void Update(Resource resource);

    void Delete(ResourceId id);

    Resource? GetById(ResourceId id);

    IReadOnlyList<Resource> GetForFile(ProjectFileId fileId);

    int CountForFile(ProjectFileId fileId);

    /// <summary>Stores/updates the searchable text extracted by the indexer.</summary>
    void SetIndexText(ResourceId id, string text);

    /// <summary>Case-insensitive text search across every resource in the project.</summary>
    IReadOnlyList<Resource> SearchInProject(ProjectId projectId, string query);
}
