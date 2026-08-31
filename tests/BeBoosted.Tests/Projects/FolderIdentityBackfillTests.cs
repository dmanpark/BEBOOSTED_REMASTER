using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// Every Project and File persisted before migration 0012 holds <c>folder_segment = ''</c>,
/// and <see cref="ResourceLayout.FolderFor"/> returns those segments verbatim — so an
/// un-backfilled database resolves every folder to the resources root. The backfill hands
/// each such row the segment its bytes already occupy, which is the opposite of a naive
/// reservation: the directory that is already there must read as this entity's own rather
/// than as an obstacle.
/// </summary>
public sealed class FolderIdentityBackfillTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 8, 28, 9, 0, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private sealed class TestPaths : IAppDataPaths
    {
        public TestPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"beboosted-backfilltest-{Guid.NewGuid():N}");
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
    private readonly SqliteResourceGroupRepository _groups;
    private readonly LocalResourceStorage _storage;

    public FolderIdentityBackfillTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _projects = new SqliteProjectRepository(_database.Factory);
        _files = new SqliteProjectFileRepository(_database.Factory);
        _resources = new SqliteResourceRepository(_database.Factory);
        _groups = new SqliteResourceGroupRepository(_database.Factory);
        _storage = new LocalResourceStorage(_paths);
        Directory.CreateDirectory(_paths.ResourcesDirectory);
    }

    private FolderIdentityBackfill CreateBackfill()
        => new(_projects, _files, _storage, _clock);

    /// <summary>
    /// A pre-0012 row: <see cref="Project.Create"/> leaves the segment empty, which is
    /// exactly the sentinel the migration's <c>DEFAULT ''</c> left behind. Distinct
    /// creation instants keep <c>GetAll</c>'s <c>ORDER BY created_at</c> deterministic,
    /// so "first" and "second" mean something in the collision tests.
    /// </summary>
    private Project SeedLegacyProject(string name, int order = 0)
    {
        var project = Project.Create(name, "#ffffff", _clock.Now.AddMinutes(order));
        _projects.Add(project);
        return project;
    }

    private ProjectFile SeedLegacyFile(Project project, string title, int order = 0)
    {
        var file = ProjectFile.Create(project.Id, title, null, _clock.Now.AddMinutes(order));
        _files.Add(file);
        return file;
    }

    /// <summary>A document already recorded at, and physically sitting at, a legacy path.</summary>
    private Resource SeedDocumentAt(ProjectFile file, string storedPath, string? originalFileName = null)
    {
        var fileName = originalFileName ?? Path.GetFileName(storedPath);
        var resource = Resource.CreateStored(
            file.Id, ResourceKind.Document, Path.GetFileNameWithoutExtension(fileName),
            fileName, storedPath, _clock.Now);
        var absolute = _storage.ResolvePath(storedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "payload");
        _resources.Add(resource);
        return resource;
    }

    private ResourceLayoutReconciler CreateReconciler()
        => new(_projects, _files, _resources, _storage, _clock, _groups);

    /// <summary>
    /// The real startup layout pass — the same object <c>App.axaml.cs</c> resolves and
    /// calls, not a copy of its branch. A reimplementation here would pin only itself:
    /// the gate could be deleted from the shipping path and every test would stay green.
    /// </summary>
    private ResourceLayoutStartupResult RunStartupLayoutPass(IResourceStorage storage)
        => new ResourceLayoutStartup(
            new FolderIdentityBackfill(_projects, _files, storage, _clock),
            new ResourceLayoutReconciler(_projects, _files, _resources, storage, _clock, _groups)).Run();

    [Fact]
    public void Backfill_ClaimsTheDirectoryALegacyProjectsBytesAlreadyOccupy()
    {
        var project = SeedLegacyProject("College Admissions");

        // The bytes already live here, so the directory is already on disk — a
        // reservation that reads it as occupied hands back "College Admissions (2)"
        // and the reconciler then moves every document 0012 was meant to leave alone.
        Directory.CreateDirectory(
            Path.Combine(_paths.ResourcesDirectory, "College Admissions", "Metric Proof"));

        CreateBackfill().Backfill();

        Assert.Equal("College Admissions", _projects.GetById(project.Id)!.FolderSegment);
    }

    /// <summary>
    /// Two Projects cannot both own one directory. Whichever the backfill reaches first
    /// claims it for real; the second finds it in <c>claimed</c> and must advance even
    /// though provisional ownership would otherwise hand it the same name.
    /// </summary>
    [Fact]
    public void Backfill_GivesTwoLegacyProjectsThatSanitizeAlike_DifferentSegments()
    {
        var first = SeedLegacyProject("DECA", order: 0);
        var second = SeedLegacyProject("DECA", order: 1);
        Directory.CreateDirectory(Path.Combine(_paths.ResourcesDirectory, "DECA"));

        CreateBackfill().Backfill();

        Assert.Equal("DECA", _projects.GetById(first.Id)!.FolderSegment);
        Assert.Equal("DECA (2)", _projects.GetById(second.Id)!.FolderSegment);
    }

    /// <summary>
    /// The mixed database an upgrade actually produces: one Project created since 0012
    /// already holds "DECA", one legacy row still derives it. Provisional ownership makes
    /// the backfill treat an existing directory as its own, so without the live claim in
    /// <c>claimed</c> the legacy row walks straight into a Project's live folder. Only the
    /// seeding stops it: which row is reached first does not matter, since the live one is
    /// skipped by the non-empty filter either way and never adds itself to the set.
    /// </summary>
    [Fact]
    public void Backfill_NeverTakesASegmentAnotherProjectAlreadyHolds()
    {
        var legacy = SeedLegacyProject("DECA", order: 0);
        var live = SeedLegacyProject("DECA", order: 1);
        live.RelocateTo("DECA", _clock.Now);
        _projects.Update(live);
        Directory.CreateDirectory(Path.Combine(_paths.ResourcesDirectory, "DECA"));

        CreateBackfill().Backfill();

        Assert.Equal("DECA (2)", _projects.GetById(legacy.Id)!.FolderSegment);
    }

    /// <summary>
    /// A Project whose claimed segment deliberately disagrees with what its current name
    /// would derive — a rename since the claim, or a sibling that had to disambiguate.
    /// Re-deriving it would walk a live Project out of the folder holding its documents,
    /// so a non-empty segment is skipped outright rather than reconsidered. That skip is
    /// also what makes a second run a no-op.
    /// </summary>
    [Fact]
    public void Backfill_SkipsEntitiesThatAlreadyHoldASegment_AndIsIdempotent()
    {
        var live = SeedLegacyProject("Model UN", order: 0);
        live.RelocateTo("Model UN (2)", _clock.Now);
        _projects.Update(live);
        Directory.CreateDirectory(Path.Combine(_paths.ResourcesDirectory, "Model UN (2)"));
        var legacy = SeedLegacyProject("Robotics", order: 1);

        Assert.Equal(1, CreateBackfill().Backfill().Claimed);
        Assert.Equal("Model UN (2)", _projects.GetById(live.Id)!.FolderSegment);
        Assert.Equal("Robotics", _projects.GetById(legacy.Id)!.FolderSegment);

        Assert.Equal(0, CreateBackfill().Backfill().Claimed);
        Assert.Equal("Model UN (2)", _projects.GetById(live.Id)!.FolderSegment);
        Assert.Equal("Robotics", _projects.GetById(legacy.Id)!.FolderSegment);
    }

    /// <summary>
    /// The ordering guard. A File's reservation happens inside its Project's claimed
    /// segment, so every Project must be backfilled before any File is reached. Note the
    /// derived segment string is identical either way — a File backfilled first derives
    /// "Metric Proof" too, it just reserves it beneath the empty sentinel, which is the
    /// resources root. Only where the directory was actually created can tell the two
    /// apart, so that is what this test pins.
    /// </summary>
    [Fact]
    public void Backfill_ReservesEachFileBeneathItsProjectsNewlyBackfilledSegment()
    {
        var project = SeedLegacyProject("College Admissions");
        var file = SeedLegacyFile(project, "Metric Proof");

        CreateBackfill().Backfill();

        var reloadedProject = _projects.GetById(project.Id)!;
        var reloadedFile = _files.GetById(file.Id)!;
        Assert.Equal("College Admissions", reloadedProject.FolderSegment);
        Assert.Equal("Metric Proof", reloadedFile.FolderSegment);
        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof"),
            ResourceLayout.FolderFor(reloadedProject, reloadedFile));

        Assert.True(Directory.Exists(
            Path.Combine(_paths.ResourcesDirectory, "College Admissions", "Metric Proof")));
        Assert.False(Directory.Exists(Path.Combine(_paths.ResourcesDirectory, "Metric Proof")));
    }

    /// <summary>
    /// The end the whole task exists for: after the backfill, the reconciler agrees with
    /// where the legacy bytes already are and moves nothing at all.
    /// </summary>
    [Fact]
    public void Backfill_ThenReconcile_LeavesALegacyDocumentExactlyWhereItIs()
    {
        var project = SeedLegacyProject("College Admissions");
        var file = SeedLegacyFile(project, "Metric Proof");
        var legacyPath = Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf");
        var resource = SeedDocumentAt(file, legacyPath);

        Assert.Equal(2, CreateBackfill().Backfill().Claimed);

        Assert.Equal(0, CreateReconciler().Reconcile());
        Assert.Equal(legacyPath, _resources.GetById(resource.Id)!.StoredPath);
        Assert.Equal("payload", File.ReadAllText(_storage.ResolvePath(legacyPath)));
    }

    /// <summary>
    /// A File's segment only has to be unique inside its own Project's folder. Two Files
    /// of different Projects live in different directories, so sharing a segment is not a
    /// collision — one <c>claimed</c> set spanning every Project would invent a "(2)"
    /// suffix for a name nothing is competing for, and move bytes to prove it.
    /// </summary>
    [Fact]
    public void Backfill_ScopesFileSegmentsToTheirOwnProject()
    {
        var alpha = SeedLegacyProject("Alpha", order: 0);
        var alphaNotes = SeedLegacyFile(alpha, "Notes");
        var beta = SeedLegacyProject("Beta", order: 1);
        var betaNotes = SeedLegacyFile(beta, "Notes");

        CreateBackfill().Backfill();

        Assert.Equal("Notes", _files.GetById(alphaNotes.Id)!.FolderSegment);
        Assert.Equal("Notes", _files.GetById(betaNotes.Id)!.FolderSegment);
        Assert.True(Directory.Exists(Path.Combine(_paths.ResourcesDirectory, "Alpha", "Notes")));
        Assert.True(Directory.Exists(Path.Combine(_paths.ResourcesDirectory, "Beta", "Notes")));
    }

    /// <summary>
    /// The File-level twin of <see cref="Backfill_NeverTakesASegmentAnotherProjectAlreadyHolds"/>,
    /// and the reason the per-Project sets are seeded rather than merely accumulated.
    /// Inside one Project, a File created since 0012 already holds "Notes" while a legacy
    /// sibling derives the same name; provisional ownership would hand the legacy row its
    /// live sibling's folder. Unlike the Project-level set this one is rebuilt per Project,
    /// so the seeding is easy to drop on one side and not the other.
    /// </summary>
    [Fact]
    public void Backfill_NeverTakesASegmentASiblingFileAlreadyHolds()
    {
        var project = SeedLegacyProject("DECA");
        project.RelocateTo("DECA", _clock.Now);
        _projects.Update(project);

        var legacy = SeedLegacyFile(project, "Notes", order: 0);
        var live = SeedLegacyFile(project, "Notes", order: 1);
        live.RelocateTo("Notes", _clock.Now);
        _files.Update(live);
        Directory.CreateDirectory(Path.Combine(_paths.ResourcesDirectory, "DECA", "Notes"));

        CreateBackfill().Backfill();

        Assert.Equal("Notes (2)", _files.GetById(legacy.Id)!.FolderSegment);
        Assert.Equal("Notes", _files.GetById(live.Id)!.FolderSegment);
    }

    /// <summary>
    /// Delegates everything; <c>ReserveFolderSegment</c> throws for one chosen segment —
    /// a deterministic wall (a path-length limit, an ACL, a rejected name) rather than a
    /// transient one, so it hits the same row on every single run.
    /// </summary>
    private sealed class SabotagedStorage(IResourceStorage inner, string failOnSegment) : IResourceStorage
    {
        public string Store(
            string relativeFolder, string preferredFileName, string sourcePath,
            IReadOnlySet<string> claimedFolders)
            => inner.Store(relativeFolder, preferredFileName, sourcePath, claimedFolders);

        public string? MoveInto(
            string currentStoredPath, string relativeFolder, string preferredFileName,
            IReadOnlySet<string> claimedFolders)
            => inner.MoveInto(currentStoredPath, relativeFolder, preferredFileName, claimedFolders);

        public string ReserveFolderSegment(
            string relativeParent, string preferredSegment, IReadOnlySet<string> claimed, string? ownedSegment = null)
            => string.Equals(preferredSegment, failOnSegment, StringComparison.Ordinal)
                ? throw new IOException("the filesystem refused this name")
                : inner.ReserveFolderSegment(relativeParent, preferredSegment, claimed, ownedSegment);

        public string ResolvePath(string storedPath) => inner.ResolvePath(storedPath);

        public bool Exists(string storedPath) => inner.Exists(storedPath);

        public void Delete(string storedPath) => inner.Delete(storedPath);
    }

    /// <summary>
    /// A deterministic failure on one folder name must cost exactly that Project and its
    /// Files. Aborting the whole pass would stand every later row up against the same wall
    /// on every launch — permanently unclaimed, and with the reconciler never running
    /// again either, since it sits after the backfill in one shared try.
    ///
    /// The skipped Project's File must not then fall back to reserving in the resources
    /// root. Abort-everything used to prevent that implicitly; per-entity recovery has to
    /// state it, so <c>BackfillFiles</c> skips any Project still holding the sentinel.
    /// </summary>
    [Fact]
    public void Backfill_SkipsOnlyTheProjectWhoseReservationFails_AndItsFiles()
    {
        var doomed = SeedLegacyProject("Boom", order: 0);
        var doomedFile = SeedLegacyFile(doomed, "Stranded Notes");
        var healthy = SeedLegacyProject("Robotics", order: 1);
        var healthyFile = SeedLegacyFile(healthy, "Kickoff");

        var storage = new SabotagedStorage(_storage, failOnSegment: "Boom");
        var outcome = new FolderIdentityBackfill(_projects, _files, storage, _clock).Backfill();

        Assert.Equal(2, outcome.Claimed);

        // The Project and the File it took down with it. The startup pass gates the
        // reconcile on this being zero, so a run that silently reported nothing skipped
        // would let the sweep loose on rows that still hold the sentinel.
        Assert.Equal(2, outcome.Skipped);
        Assert.Equal(string.Empty, _projects.GetById(doomed.Id)!.FolderSegment);
        Assert.Equal("Robotics", _projects.GetById(healthy.Id)!.FolderSegment);
        Assert.Equal("Kickoff", _files.GetById(healthyFile.Id)!.FolderSegment);

        Assert.Equal(string.Empty, _files.GetById(doomedFile.Id)!.FolderSegment);
        Assert.False(Directory.Exists(Path.Combine(_paths.ResourcesDirectory, "Stranded Notes")));
    }

    /// <summary>
    /// The Task 6 / Task 7 seam. Per-entity recovery means a failed row no longer aborts
    /// the pass — but the abort was also what stopped <c>Reconcile()</c> from running
    /// against the row it left unclaimed. With both segments still empty, the reconciler's
    /// guard is deliberately silent (both-empty is the pure legacy state it must process),
    /// <c>FolderFor</c> is <c>Path.Combine("", "")</c> = "", and every document under the
    /// skipped Project is physically moved into the resources root and re-recorded there.
    /// Under a deterministic fault that repeats on every launch and never heals.
    ///
    /// The startup pass must therefore defer the sweep whenever the backfill left anything
    /// behind. Deferring costs nothing — the documents stay where they are and a later
    /// launch picks them up — while sweeping unclaimed rows destroys the layout.
    /// </summary>
    [Fact]
    public void AStartupPass_AfterASkippedRow_DoesNotFlattenThatProjectsDocumentsIntoTheRoot()
    {
        var doomed = SeedLegacyProject("Boom", order: 0);
        var doomedFile = SeedLegacyFile(doomed, "Stranded Notes");
        var legacyPath = Path.Combine("Boom", "Stranded Notes", "Transcript.pdf");
        var resource = SeedDocumentAt(doomedFile, legacyPath);

        RunStartupLayoutPass(new SabotagedStorage(_storage, failOnSegment: "Boom"));

        Assert.Equal(legacyPath, _resources.GetById(resource.Id)!.StoredPath);
        Assert.Equal("payload", File.ReadAllText(_storage.ResolvePath(legacyPath)));
        Assert.False(_storage.Exists("Transcript.pdf"));
    }

    /// <summary>
    /// The other half of that gate, and the guard against it being permanently shut: when
    /// the backfill skips nothing, the sweep must still run and still migrate a legacy
    /// guid-named document into its newly claimed folder.
    /// </summary>
    [Fact]
    public void AStartupPass_WithNothingSkipped_StillRunsTheSweep()
    {
        var project = SeedLegacyProject("College Admissions");
        var file = SeedLegacyFile(project, "Metric Proof");
        var resource = SeedDocumentAt(
            file, Guid.NewGuid().ToString("N") + ".pdf", originalFileName: "Transcript.pdf");

        RunStartupLayoutPass(_storage);

        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            _resources.GetById(resource.Id)!.StoredPath);
    }

    /// <summary>
    /// A deferral the operator cannot act on is barely better than a silent one. The
    /// outcome has to name which entity could not be claimed and carry the exception that
    /// stopped it, because the class takes no logger and the call site can only report
    /// what it is handed.
    /// </summary>
    [Fact]
    public void Backfill_NamesTheEntityAndTheCause_ForEveryRowItCouldNotClaim()
    {
        SeedLegacyProject("Boom");

        var storage = new SabotagedStorage(_storage, failOnSegment: "Boom");
        var outcome = new FolderIdentityBackfill(_projects, _files, storage, _clock).Backfill();

        var failure = Assert.Single(outcome.Failures);
        Assert.Contains("Boom", failure.Entity, StringComparison.Ordinal);
        Assert.IsType<IOException>(failure.Error);
        Assert.Equal("the filesystem refused this name", failure.Error.Message);
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
