using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// The reconciler is both the migration for already-imported documents and the
/// rename-sync afterwards. It must be idempotent, and a resource it cannot move must
/// keep working exactly where it is.
/// </summary>
public sealed class ResourceLayoutReconcilerTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private sealed class TestPaths : IAppDataPaths
    {
        public TestPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"beboosted-recontest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }

        public string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");
    }

    private readonly TempDatabase _database = new();
    private readonly TestPaths _paths = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteProjectRepository _projects;
    private readonly SqliteProjectFileRepository _files;
    private readonly SqliteResourceRepository _resources;
    private readonly LocalResourceStorage _storage;

    public ResourceLayoutReconcilerTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _projects = new SqliteProjectRepository(_database.Factory);
        _files = new SqliteProjectFileRepository(_database.Factory);
        _resources = new SqliteResourceRepository(_database.Factory);
        _storage = new LocalResourceStorage(_paths);
    }

    private ResourceLayoutReconciler CreateReconciler()
        => new(_projects, _files, _resources, _storage, _clock);

    /// <summary>A project/file pair plus a legacy guid-named document already on disk.</summary>
    private (Project Project, ProjectFile File, Resource Resource) SeedLegacyDocument(
        string projectName = "College Admissions",
        string fileTitle = "Metric Proof",
        string originalName = "Transcript.pdf",
        string content = "payload")
    {
        var project = Project.Create(projectName, "#ffffff", _clock.Now);
        _projects.Add(project);
        var file = ProjectFile.Create(project.Id, fileTitle, null, _clock.Now);
        _files.Add(file);

        var resource = Resource.CreateStored(
            file.Id, ResourceKind.Document, Path.GetFileNameWithoutExtension(originalName),
            originalName, Guid.NewGuid().ToString("N") + ".pdf", _clock.Now);
        Directory.CreateDirectory(_paths.ResourcesDirectory);
        File.WriteAllText(_storage.ResolvePath(resource.StoredPath!), content);
        _resources.Add(resource);
        return (project, file, resource);
    }

    [Fact]
    public void Reconcile_MigratesGuidNamedFilesIntoNamedFolders()
    {
        var seeded = SeedLegacyDocument();

        var moved = CreateReconciler().Reconcile();

        Assert.Equal(1, moved);
        var reloaded = _resources.GetById(seeded.Resource.Id)!;
        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            reloaded.StoredPath);
        Assert.True(_storage.Exists(reloaded.StoredPath!));
        Assert.Equal("payload", File.ReadAllText(_storage.ResolvePath(reloaded.StoredPath!)));
    }

    [Fact]
    public void Reconcile_IsIdempotent()
    {
        SeedLegacyDocument();
        Assert.Equal(1, CreateReconciler().Reconcile());

        Assert.Equal(0, CreateReconciler().Reconcile());
        Assert.Equal(0, CreateReconciler().Reconcile());
    }

    [Fact]
    public void Reconcile_DisambiguatesTwoDocumentsSharingAName()
    {
        var first = SeedLegacyDocument(content: "first");
        var second = Resource.CreateStored(
            first.File.Id, ResourceKind.Document, "Transcript",
            "Transcript.pdf", Guid.NewGuid().ToString("N") + ".pdf", _clock.Now);
        File.WriteAllText(_storage.ResolvePath(second.StoredPath!), "second");
        _resources.Add(second);

        Assert.Equal(2, CreateReconciler().Reconcile());

        var folder = Path.Combine("College Admissions", "Metric Proof");
        var paths = _resources.GetForFile(first.File.Id).Select(r => r.StoredPath).ToList();
        Assert.Contains(Path.Combine(folder, "Transcript.pdf"), paths);
        Assert.Contains(Path.Combine(folder, "Transcript (2).pdf"), paths);

        // A second run must not shuffle the disambiguated one.
        Assert.Equal(0, CreateReconciler().Reconcile());
    }

    [Fact]
    public void Reconcile_LeavesAResourceWhoseBytesAreMissing_AndStillMigratesItsSiblings()
    {
        var seeded = SeedLegacyDocument();
        var orphan = Resource.CreateStored(
            seeded.File.Id, ResourceKind.Document, "Ghost",
            "Ghost.pdf", Guid.NewGuid().ToString("N") + ".pdf", _clock.Now);
        _resources.Add(orphan); // no bytes ever written

        var moved = CreateReconciler().Reconcile();

        Assert.Equal(1, moved);
        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            _resources.GetById(seeded.Resource.Id)!.StoredPath);
        Assert.Equal(orphan.StoredPath, _resources.GetById(orphan.Id)!.StoredPath);
    }

    [Fact]
    public void Reconcile_IgnoresLinksAndNotes()
    {
        var seeded = SeedLegacyDocument();
        _resources.Add(Resource.CreateLink(seeded.File.Id, "Source", "https://example.com", _clock.Now));
        _resources.Add(Resource.CreateNote(seeded.File.Id, "Idea", "text", _clock.Now));

        Assert.Equal(1, CreateReconciler().Reconcile());
        Assert.All(
            _resources.GetForFile(seeded.File.Id).Where(r => r.Kind != ResourceKind.Document),
            r => Assert.Null(r.StoredPath));
    }

    [Fact]
    public void ReconcileProject_MovesOnlyThatProject()
    {
        var kept = SeedLegacyDocument("Other Project", "Other File", "Other.pdf");
        var target = SeedLegacyDocument();

        Assert.Equal(1, CreateReconciler().ReconcileProject(target.Project.Id));

        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            _resources.GetById(target.Resource.Id)!.StoredPath);
        Assert.Equal(kept.Resource.StoredPath, _resources.GetById(kept.Resource.Id)!.StoredPath);
    }

    /// <summary>Delegates everything; Update throws for chosen resources.</summary>
    private sealed class SabotagedResources(
        IResourceRepository inner, Func<Resource, bool> failOnUpdate) : IResourceRepository
    {
        public void Add(Resource resource) => inner.Add(resource);

        public void Update(Resource resource)
        {
            if (failOnUpdate(resource))
            {
                throw new InvalidOperationException("update rejected");
            }

            inner.Update(resource);
        }

        public void Delete(ResourceId id) => inner.Delete(id);

        public Resource? GetById(ResourceId id) => inner.GetById(id);

        public IReadOnlyList<Resource> GetForFile(ProjectFileId fileId) => inner.GetForFile(fileId);

        public int CountForFile(ProjectFileId fileId) => inner.CountForFile(fileId);

        public void SetIndexText(ResourceId id, string text) => inner.SetIndexText(id, text);

        public IReadOnlyList<Resource> SearchInProject(ProjectId projectId, string query)
            => inner.SearchInProject(projectId, query);
    }

    [Fact]
    public void AFailingRecord_DoesNotAbortThePass_AndTheNextRunRepairsTheMovedFile()
    {
        var victim = SeedLegacyDocument();
        var sibling = Resource.CreateStored(
            victim.File.Id, ResourceKind.Document, "Essay",
            "Essay.pdf", Guid.NewGuid().ToString("N") + ".pdf", _clock.Now);
        File.WriteAllText(_storage.ResolvePath(sibling.StoredPath!), "essay");
        _resources.Add(sibling);

        var sabotaged = new SabotagedResources(_resources, r => r.Id == victim.Resource.Id);
        var firstRun = new ResourceLayoutReconciler(_projects, _files, sabotaged, _storage, _clock)
            .Reconcile();

        // The victim's bytes moved but its row kept the stale path; the sibling still migrated.
        Assert.Equal(1, firstRun);
        var stale = _resources.GetById(victim.Resource.Id)!;
        Assert.Equal(victim.Resource.StoredPath, stale.StoredPath);
        Assert.False(_storage.Exists(stale.StoredPath!));
        var folder = Path.Combine("College Admissions", "Metric Proof");
        Assert.Equal(Path.Combine(folder, "Essay.pdf"), _resources.GetById(sibling.Id)!.StoredPath);

        // The design's promised repair: the next run adopts the already-moved bytes.
        Assert.Equal(1, CreateReconciler().Reconcile());
        var repaired = _resources.GetById(victim.Resource.Id)!;
        Assert.Equal(Path.Combine(folder, "Transcript.pdf"), repaired.StoredPath);
        Assert.Equal("payload", File.ReadAllText(_storage.ResolvePath(repaired.StoredPath!)));

        // And the run after that is a quiet no-op again.
        Assert.Equal(0, CreateReconciler().Reconcile());
    }

    [Fact]
    public void Repair_AdoptsTheUnclaimedNumberedSlot_NeverASiblingsFile()
    {
        // Two documents share an original name. The first is properly recorded at the
        // bare name; the second's bytes reached "Transcript (2).pdf" but its row was
        // never updated (the record step failed mid-reconcile).
        var placed = SeedLegacyDocument(content: "first");
        CreateReconciler().Reconcile();

        var orphan = Resource.CreateStored(
            placed.File.Id, ResourceKind.Document, "Transcript",
            "Transcript.pdf", Guid.NewGuid().ToString("N") + ".pdf", _clock.Now);
        _resources.Add(orphan); // its stale source path has no bytes behind it
        var folder = Path.Combine("College Admissions", "Metric Proof");
        File.WriteAllText(_storage.ResolvePath(Path.Combine(folder, "Transcript (2).pdf")), "second");

        Assert.Equal(1, CreateReconciler().Reconcile());

        Assert.Equal(
            Path.Combine(folder, "Transcript (2).pdf"), _resources.GetById(orphan.Id)!.StoredPath);
        Assert.Equal(
            Path.Combine(folder, "Transcript.pdf"), _resources.GetById(placed.Resource.Id)!.StoredPath);
        Assert.Equal("first", File.ReadAllText(_storage.ResolvePath(Path.Combine(folder, "Transcript.pdf"))));
    }

    [Fact]
    public void Reconcile_OnAnEmptyStore_IsAQuietNoOp()
    {
        Assert.Equal(0, CreateReconciler().Reconcile());
        Assert.Equal(0, CreateReconciler().ReconcileProject(ProjectId.New()));
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
