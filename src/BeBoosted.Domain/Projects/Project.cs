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
    private Project(ProjectId id, string name, string accentColor, DateTimeOffset createdAt, DateTimeOffset modifiedAt)
    {
        Id = id;
        Name = name;
        AccentColor = accentColor;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    public ProjectId Id { get; }

    public string Name { get; private set; }

    /// <summary>Hex accent from <see cref="ProjectPalette"/>.</summary>
    public string AccentColor { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ModifiedAt { get; private set; }

    public static Project Create(string name, string accentColor, DateTimeOffset now)
        => new(ProjectId.New(), ValidateName(name), accentColor, now, now);

    public static Project Rehydrate(
        ProjectId id, string name, string accentColor, DateTimeOffset createdAt, DateTimeOffset modifiedAt)
        => new(id, name, accentColor, createdAt, modifiedAt);

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

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? throw new DomainException("A project needs a name.") : trimmed;
    }
}
