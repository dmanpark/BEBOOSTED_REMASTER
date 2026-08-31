namespace BeBoosted.Domain.Projects;

/// <summary>
/// One level of container inside a File: a named group that holds resources
/// alongside a File's loose ones. Flat — groups never nest.
/// </summary>
public sealed class ResourceGroup
{
    private ResourceGroup(
        ResourceGroupId id,
        ProjectFileId fileId,
        string title,
        int sortOrder,
        string folderSegment,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
    {
        Id = id;
        FileId = fileId;
        Title = title;
        SortOrder = sortOrder;
        FolderSegment = folderSegment;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    public ResourceGroupId Id { get; }

    public ProjectFileId FileId { get; }

    public string Title { get; private set; }

    public int SortOrder { get; private set; }

    /// <summary>
    /// The claimed, disambiguated directory name under which this group's resources are
    /// stored. The empty string means "not yet claimed" — the state a freshly created
    /// group holds in memory until a segment is reserved and <see cref="RelocateTo"/>
    /// records it.
    /// </summary>
    public string FolderSegment { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ModifiedAt { get; private set; }

    public static ResourceGroup Create(ProjectFileId fileId, string title, int sortOrder, DateTimeOffset now)
        => new(ResourceGroupId.New(), fileId, ValidateTitle(title), ValidateOrder(sortOrder), string.Empty, now, now);

    public static ResourceGroup Rehydrate(
        ResourceGroupId id,
        ProjectFileId fileId,
        string title,
        int sortOrder,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt,
        string folderSegment)
        => new(id, fileId, title, sortOrder, folderSegment, createdAt, modifiedAt);

    public void Rename(string title, DateTimeOffset now)
    {
        Title = ValidateTitle(title);
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
            throw new DomainException("A group needs a claimed folder segment.");
        }

        FolderSegment = trimmed;
        ModifiedAt = now;
    }

    public void Reorder(int sortOrder, DateTimeOffset now)
    {
        SortOrder = ValidateOrder(sortOrder);
        ModifiedAt = now;
    }

    private static string ValidateTitle(string title)
        => string.IsNullOrWhiteSpace(title)
            ? throw new DomainException("A group needs a title.") : title.Trim();

    private static int ValidateOrder(int order)
        => order < 0 ? throw new DomainException("Group order cannot be negative.") : order;
}
