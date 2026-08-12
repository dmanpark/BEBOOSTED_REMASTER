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
