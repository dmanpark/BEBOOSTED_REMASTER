using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Tasks;

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

    /// <summary>
    /// The real reconciler over this fixture's world. The two optional arguments exist so
    /// a test can substitute a sabotaged storage or resource repository and still keep
    /// every other collaborator — the project, file and group repositories above all —
    /// pointed at the same durable database.
    /// </summary>
    public ResourceLayoutReconciler Reconciler(
        IResourceStorage? storage = null, IResourceRepository? resources = null)
        => new(Projects, Files, resources ?? Resources, storage ?? Storage, this, Groups);

    /// <summary>
    /// The real <see cref="ProjectService"/> over this fixture's world, wired to the same
    /// durable database as every repository above. The three optional arguments are the
    /// seams a test needs to sabotage: a mutations double that rolls back, a storage
    /// double that refuses a reservation or a move, and an invalidator that records. The
    /// reconciler is built over whichever storage the service itself uses, so a sabotaged
    /// storage is sabotaged for both — anything else would let the reconcile quietly
    /// repair what the service was prevented from doing.
    /// </summary>
    public ProjectService CreateService(IProjectMutations? mutations = null,
        IResourceStorage? storage = null, IProvenanceInvalidator? invalidator = null)
    {
        var bytes = storage ?? Storage;
        return new ProjectService(Projects, Files, Resources, bytes,
            mutations ?? Mutations,
            new SimpleLocalIndexer(Resources, bytes, this),
            new SqliteTaskRepository(Database.Factory),
            new SqliteCalendarBlockRepository(Database.Factory),
            new SqliteOccurrenceCompletionRepository(Database.Factory), this, Groups,
            invalidator, Reconciler(bytes));
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
    /// A file on disk OUTSIDE the resources root, ready to be imported or stored. Each one
    /// gets its own guid directory, so two sources may share a name without the second
    /// overwriting the first — which matters because the name is what the layout sanitizes
    /// and collides on, and a shared source directory would hide a collision the test is
    /// trying to provoke.
    /// </summary>
    public string SourceFile(string name, string content)
    {
        var source = Path.Combine(DataDirectory, "inputs", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        System.IO.File.WriteAllText(source, content);
        return source;
    }

    /// <summary>
    /// A persisted document with real bytes on disk and real index text, so a test can
    /// tell "the row survived" apart from "the row survived and its bytes did too".
    /// </summary>
    public Resource Document(string name = "source.txt", string content = "sentinel")
    {
        var source = SourceFile(name, content);

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

    /// <summary>
    /// Files a resource into a group, or back to loose, by id.
    ///
    /// Deliberately NOT by entity. <c>Update</c> writes the whole row, so assigning a
    /// caller-held instance writes back every field that instance still holds — including
    /// a <c>StoredPath</c> the reconciler has since changed. A test that reconciles and
    /// then re-assigns would silently restore the pre-reconcile path and go on to assert
    /// the path the fixture itself had just written back. Re-reading here makes that
    /// impossible rather than merely documented, which matters because assign-then-
    /// reconcile-then-assign is the ordinary shape of a group move test.
    /// </summary>
    public void Assign(ResourceId resourceId, ResourceGroupId? groupId)
    {
        var resource = Resources.GetById(resourceId)
            ?? throw new InvalidOperationException($"No resource {resourceId} to assign.");
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
