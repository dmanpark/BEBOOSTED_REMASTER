namespace BeBoosted.Domain.Projects;

public enum ResourceKind
{
    Document = 0,
    Link = 1,
    Note = 2,
    Image = 3,
}

public enum ResourceIndexState
{
    Pending = 0,
    Indexed = 1,
    Failed = 2,
}

/// <summary>
/// One item inside a File: an uploaded document, a web link, a short note, or an image.
/// Documents/images keep their bytes in app-controlled storage under a stable id-based
/// path so citations survive reopening the application.
/// </summary>
public sealed class Resource
{
    private Resource(
        ResourceId id,
        ProjectFileId fileId,
        ResourceKind kind,
        string title,
        string? url,
        string? content,
        string? originalFileName,
        string? storedPath,
        DateTimeOffset addedAt,
        ResourceIndexState indexState,
        DateTimeOffset modifiedAt,
        ResourceGroupId? groupId)
    {
        Id = id;
        FileId = fileId;
        Kind = kind;
        Title = title;
        Url = url;
        Content = content;
        OriginalFileName = originalFileName;
        StoredPath = storedPath;
        AddedAt = addedAt;
        IndexState = indexState;
        ModifiedAt = modifiedAt;
        GroupId = groupId;
    }

    public ResourceId Id { get; }

    public ProjectFileId FileId { get; }

    public ResourceKind Kind { get; }

    public string Title { get; private set; }

    /// <summary>Links only.</summary>
    public string? Url { get; private set; }

    /// <summary>Notes only.</summary>
    public string? Content { get; private set; }

    /// <summary>Documents/images: the user's original file name.</summary>
    public string? OriginalFileName { get; }

    /// <summary>Documents/images: path relative to the resources directory.</summary>
    public string? StoredPath { get; private set; }

    public DateTimeOffset AddedAt { get; }

    public ResourceIndexState IndexState { get; private set; }

    public DateTimeOffset ModifiedAt { get; private set; }

    /// <summary>The group this resource belongs to, or null when it is loose.</summary>
    public ResourceGroupId? GroupId { get; private set; }

    public static Resource CreateLink(ProjectFileId fileId, string title, string url, DateTimeOffset now)
    {
        var trimmedUrl = url?.Trim() ?? string.Empty;
        if (trimmedUrl.Length == 0)
        {
            throw new DomainException("A link needs a URL.");
        }

        return new Resource(
            ResourceId.New(), fileId, ResourceKind.Link, ValidateTitle(title),
            trimmedUrl, null, null, null, now, ResourceIndexState.Pending, now, null);
    }

    public static Resource CreateNote(ProjectFileId fileId, string title, string content, DateTimeOffset now)
        => new(
            ResourceId.New(), fileId, ResourceKind.Note, ValidateTitle(title),
            null, content ?? string.Empty, null, null, now, ResourceIndexState.Pending, now, null);

    public static Resource CreateStored(
        ProjectFileId fileId,
        ResourceKind kind,
        string title,
        string originalFileName,
        string storedPath,
        DateTimeOffset now)
    {
        if (kind is not (ResourceKind.Document or ResourceKind.Image))
        {
            throw new DomainException("Only documents and images use local storage.");
        }

        return new Resource(
            ResourceId.New(), fileId, kind, ValidateTitle(title),
            null, null, originalFileName, storedPath, now, ResourceIndexState.Pending, now, null);
    }

    public static Resource Rehydrate(
        ResourceId id,
        ProjectFileId fileId,
        ResourceKind kind,
        string title,
        string? url,
        string? content,
        string? originalFileName,
        string? storedPath,
        DateTimeOffset addedAt,
        ResourceIndexState indexState,
        DateTimeOffset modifiedAt,
        // Deliberately not defaulted. A mapper that forgets this argument would compile
        // and silently unfile the resource — the read/write mismatch this feature is
        // most exposed to, and one no test near the mapper would catch.
        ResourceGroupId? groupId)
        => new(id, fileId, kind, title, url, content, originalFileName, storedPath, addedAt, indexState, modifiedAt, groupId);

    public void Rename(string title, DateTimeOffset now)
    {
        Title = ValidateTitle(title);
        Touch(now);
    }

    /// <summary>
    /// Records a new location after the bytes were moved on disk. Called only once the
    /// move has succeeded, so the stored path never names a file that is not there.
    /// </summary>
    public void RelocateTo(string storedPath, DateTimeOffset now)
    {
        if (StoredPath is null)
        {
            throw new DomainException("Only a stored document or image has a location to change.");
        }

        var trimmed = storedPath?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("A stored resource needs a path.");
        }

        StoredPath = trimmed;
        Touch(now);
    }

    public void UpdateNoteContent(string content, DateTimeOffset now)
    {
        if (Kind != ResourceKind.Note)
        {
            throw new DomainException("Only notes carry inline content.");
        }

        Content = content ?? string.Empty;
        IndexState = ResourceIndexState.Pending; // content changed → re-index
        Touch(now);
    }

    public void MarkIndexed(DateTimeOffset now)
    {
        IndexState = ResourceIndexState.Indexed;
        Touch(now);
    }

    public void MarkIndexFailed(DateTimeOffset now)
    {
        IndexState = ResourceIndexState.Failed;
        Touch(now);
    }

    /// <summary>Moves this resource into a group, or back to loose when null.</summary>
    public void MoveToGroup(ResourceGroupId? groupId, DateTimeOffset now)
    {
        GroupId = groupId;
        Touch(now);
    }

    private static string ValidateTitle(string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? throw new DomainException("A resource needs a title.") : trimmed;
    }

    private void Touch(DateTimeOffset now) => ModifiedAt = now;
}
