namespace BeBoosted.Domain.Projects;

/// <summary>
/// A "File": a curated reference collection inside one Project. Flat — Files never nest.
/// </summary>
public sealed class ProjectFile
{
    private ProjectFile(
        ProjectFileId id,
        ProjectId projectId,
        string title,
        string? description,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
    {
        Id = id;
        ProjectId = projectId;
        Title = title;
        Description = description;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    public ProjectFileId Id { get; }

    public ProjectId ProjectId { get; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ModifiedAt { get; private set; }

    public static ProjectFile Create(ProjectId projectId, string title, string? description, DateTimeOffset now)
        => new(ProjectFileId.New(), projectId, ValidateTitle(title), Normalize(description), now, now);

    public static ProjectFile Rehydrate(
        ProjectFileId id,
        ProjectId projectId,
        string title,
        string? description,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
        => new(id, projectId, title, description, createdAt, modifiedAt);

    public void Rename(string title, DateTimeOffset now)
    {
        Title = ValidateTitle(title);
        ModifiedAt = now;
    }

    public void SetDescription(string? description, DateTimeOffset now)
    {
        Description = Normalize(description);
        ModifiedAt = now;
    }

    private static string ValidateTitle(string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? throw new DomainException("A File needs a title.") : trimmed;
    }

    private static string? Normalize(string? description)
        => string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
