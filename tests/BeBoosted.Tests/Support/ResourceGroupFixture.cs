using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;

namespace BeBoosted.Tests.Support;

/// <summary>
/// A real SQLite database, a real resources directory, and one Project holding one File
/// — the smallest world in which a group and its members can be persisted. Real, not an
/// in-memory imitation, because the behaviour under test here (foreign keys, SET NULL,
/// CASCADE, rollback) exists only in the database.
/// </summary>
public sealed class ResourceGroupFixture : IDisposable, IAppDataPaths, IClock
{
    public TempDatabase Database { get; } = new();

    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), $"beboosted-groups-{Guid.NewGuid():N}");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");

    public DateTimeOffset Now { get; set; } =
        DateTimeOffset.Parse("2026-08-30T09:00:00-07:00", CultureInfo.InvariantCulture);

    public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);

    public SqliteProjectRepository Projects { get; }

    public SqliteProjectFileRepository Files { get; }

    public SqliteResourceRepository Resources { get; }

    public SqliteResourceGroupRepository Groups { get; }

    public LocalResourceStorage Storage { get; }

    /// <summary>
    /// The real transaction seam, on this fixture's database. Here so an atomic-delete
    /// test does not have to hand-roll one and risk pointing it at a different database.
    /// </summary>
    public SqliteProjectMutations Mutations { get; }

    public Project Project { get; }

    public ProjectFile File { get; }

    public ResourceGroupFixture()
    {
        Directory.CreateDirectory(DataDirectory);
        new MigrationRunner(Database.Factory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        Projects = new(Database.Factory);
        Files = new(Database.Factory);
        Resources = new(Database.Factory);
        Groups = new(Database.Factory);
        Storage = new(this);
        Mutations = new(Database.Factory);
        Project = BeBoosted.Domain.Projects.Project.Create("Schoolwork", "#5B8DEF", Now);
        Project.RelocateTo(Storage.ReserveFolderSegment("", "Schoolwork",
            new HashSet<string>()), Now);
        Projects.Add(Project);
        File = ProjectFile.Create(Project.Id, "Spanish", null, Now);
        File.RelocateTo(Storage.ReserveFolderSegment(Project.FolderSegment, "Spanish",
            new HashSet<string>()), Now);
        Files.Add(File);
    }

    /// <summary>A persisted group with a genuinely claimed folder segment.</summary>
    public ResourceGroup Group(string title = "Notes")
    {
        var siblings = Groups.GetForFile(File.Id);
        var group = ResourceGroup.Create(File.Id, title,
            siblings.Count == 0 ? 0 : siblings.Max(g => g.SortOrder) + 1, Now);
        group.RelocateTo(Storage.ReserveFolderSegment(
            ResourceLayout.FolderFor(Project, File),
            ResourceLayout.Sanitize(title, group.Id.ToString()),
            siblings.Select(g => g.FolderSegment).ToHashSet(StringComparer.OrdinalIgnoreCase)), Now);
        Groups.Add(group);
        return group;
    }

    /// <summary>
    /// A persisted document with real bytes on disk and real index text, so a test can
    /// tell "the row survived" apart from "the row survived and its bytes did too".
    /// </summary>
    public Resource Document(string name = "source.txt", string content = "sentinel")
    {
        var source = Path.Combine(DataDirectory, "inputs", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        System.IO.File.WriteAllText(source, content);

        // The id is minted first and the name routed through ResourceLayout.FileNameFor,
        // exactly as ProjectService.ImportFile does. Storing under the raw name is
        // identical for "source.txt" but diverges the moment a name needs sanitizing,
        // and a resource parked at a path the layout would never choose is one the
        // reconciler judges misplaced on every run — a failure far from its cause.
        var id = ResourceId.New();
        var stored = Storage.Store(
            ResourceLayout.FolderFor(Project, File),
            ResourceLayout.FileNameFor(name, id.ToString()),
            source);
        var resource = Resource.Rehydrate(
            id, File.Id, ResourceKind.Document, name, null, null, name, stored,
            Now, ResourceIndexState.Pending, Now, null);
        resource.MarkIndexed(Now);
        Resources.Add(resource);
        Resources.SetIndexText(resource.Id, content);
        return resource;
    }

    public void Assign(Resource resource, ResourceGroupId? groupId)
    {
        resource.MoveToGroup(groupId, Now);
        Resources.Update(resource);
    }

    public void Dispose()
    {
        Database.Dispose();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Matches every existing temp-paths helper: a held handle must not fail the run.
        }
    }
}
