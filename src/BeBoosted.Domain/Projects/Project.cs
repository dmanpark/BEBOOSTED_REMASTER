namespace BeBoosted.Domain.Projects;

/// <summary>
/// The restrained accent palette for projects — muted colors that stay subordinate to
/// the system lime. Assigned round-robin at creation.
/// </summary>
public static class ProjectPalette
{
    public static readonly IReadOnlyList<string> Colors =
    [
        "#C2803F", // umber
        "#7B8FA6", // slate
        "#8B9B6E", // moss
        "#A8756B", // clay
        "#8C7BA6", // heather
        "#B59A4A", // ochre
    ];

    public static string ColorFor(int index) => Colors[((index % Colors.Count) + Colors.Count) % Colors.Count];
}

/// <summary>
/// A broad area of work (DECA, College Admissions, AP Economics). Deliberately simple:
/// tasks, blocks, and Files reference the project — no health scores, no dashboards.
/// </summary>
public sealed class Project
{
    private Project(
        ProjectId id,
        string name,
        string accentColor,
        string folderSegment,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
    {
        Id = id;
        Name = name;
        AccentColor = accentColor;
        FolderSegment = folderSegment;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    public ProjectId Id { get; }

    public string Name { get; private set; }

    /// <summary>Hex accent from <see cref="ProjectPalette"/>.</summary>
    public string AccentColor { get; private set; }

    /// <summary>
    /// The claimed, disambiguated directory name under which this project's Files and
    /// resources are stored. The empty string means "not yet claimed" — the state a
    /// freshly created project holds in memory until a segment is reserved and <see
    /// cref="RelocateTo"/> records it, and the state Task 7's backfill looks for on
    /// rows persisted before this column existed.
    /// </summary>
    public string FolderSegment { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ModifiedAt { get; private set; }

    public static Project Create(string name, string accentColor, DateTimeOffset now)
        => new(ProjectId.New(), ValidateName(name), accentColor, string.Empty, now, now);

    public static Project Rehydrate(
        ProjectId id,
        string name,
        string accentColor,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt,
        string folderSegment)
        => new(id, name, accentColor, folderSegment, createdAt, modifiedAt);

    public void Rename(string name, DateTimeOffset now)
    {
        Name = ValidateName(name);
        ModifiedAt = now;
    }

    public void SetAccentColor(string accentColor, DateTimeOffset now)
    {
        AccentColor = accentColor;
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
            throw new DomainException("A project needs a claimed folder segment.");
        }

        FolderSegment = trimmed;
        ModifiedAt = now;
    }

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? throw new DomainException("A project needs a name.") : trimmed;
    }
}
