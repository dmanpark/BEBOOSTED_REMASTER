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
        string folderSegment,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
    {
        Id = id;
        ProjectId = projectId;
        Title = title;
        Description = description;
        FolderSegment = folderSegment;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    public ProjectFileId Id { get; }

    public ProjectId ProjectId { get; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    /// <summary>
    /// The claimed, disambiguated directory name under which this File's resources are
    /// stored. The empty string means "not yet claimed" — the state a freshly created
    /// File holds in memory until a segment is reserved and <see cref="RelocateTo"/>
    /// records it, and the state Task 7's backfill looks for on rows persisted before
    /// this column existed.
    /// </summary>
    public string FolderSegment { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ModifiedAt { get; private set; }

    public static ProjectFile Create(ProjectId projectId, string title, string? description, DateTimeOffset now)
        => new(ProjectFileId.New(), projectId, ValidateTitle(title), Normalize(description), string.Empty, now, now);

    public static ProjectFile Rehydrate(
        ProjectFileId id,
        ProjectId projectId,
        string title,
        string? description,
        string folderSegment,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
        => new(id, projectId, title, description, folderSegment, createdAt, modifiedAt);

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

    /// <summary>
    /// Records a claimed folder segment. Called only after a reservation for it has
    /// succeeded, so the row never names a directory that was never claimed.
    /// </summary>
    public void RelocateTo(string folderSegment, DateTimeOffset now)
    {
        var trimmed = folderSegment?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("A File needs a claimed folder segment.");
        }

        FolderSegment = trimmed;
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
