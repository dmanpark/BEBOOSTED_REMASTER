using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// One invariant, from both ends:
///
/// <para><b>A folder name a group of a File has claimed is never available to a loose file
/// placed in that File's folder — whether or not the directory currently exists on
/// disk.</b></para>
///
/// The disk cannot stand in for the claim. It says nothing in the two moments that decide
/// this: straight after a parent rename, when none of the destination directories exist
/// yet, and for an empty group, whose members cannot create its directory first. A loose
/// file handed the name in either moment splits the group permanently —
/// <c>Directory.CreateDirectory</c> throws onto the file, the member's move returns null,
/// the source still exists so adoption is never reached, and the member is skipped in
/// silence on this and every later reconcile.
///
/// So the rule lives on the claim, in two halves that have to be there together:
/// <list type="bullet">
/// <item><b>Prevention.</b> <c>ReserveFreePath</c> skips a claimed name, so
/// <c>Store</c> and <c>MoveInto</c> can never hand one out.</item>
/// <item><b>Recovery.</b> <c>ResourceLayout.IsAlreadyPlaced</c> refuses to bless a file
/// already sitting on a claimed name, so a state stranded before the rule existed — or by
/// something outside the app — heals on a later reconcile instead of being frozen by the
/// very comparison that says it is exactly where it wants to be.</item>
/// </list>
///
/// Ordering is deliberately not the fix, and these tests exist to keep it from becoming
/// one. The original defect only fired when the loose resource had the earlier
/// <c>AddedAt</c>; sorting grouped resources first would have hidden it and left the empty
/// group — which has no member to move at any position — broken anyway.
/// </summary>
public sealed class ResourceGroupPlacementTests
{
    private static string Read(ResourceGroupFixture f, string? storedPath)
        => System.IO.File.ReadAllText(f.Storage.ResolvePath(storedPath!));

    private static string Resolve(ResourceGroupFixture f, params string[] parts)
        => f.Storage.ResolvePath(Path.Combine(parts));

    /// <summary>
    /// A document still parked at a legacy guid path, so a reconcile that reaches it has
    /// real work to do — and so a test can name the original file name the layout will
    /// derive its desired name from without the fixture having already placed it.
    /// </summary>
    private static Resource LegacyDocument(
        ResourceGroupFixture f, ProjectFileId fileId, string originalName, string content)
    {
        var resource = Resource.CreateStored(
            fileId, ResourceKind.Document, Path.GetFileNameWithoutExtension(originalName),
            originalName, Guid.NewGuid().ToString("N") + ".bin", f.Now);
        Directory.CreateDirectory(f.ResourcesDirectory);
        System.IO.File.WriteAllText(f.Storage.ResolvePath(resource.StoredPath!), content);
        f.Resources.Add(resource);
        return resource;
    }

    /// <summary>A second File in the same Project, with a claimed segment of its own.</summary>
    private static ProjectFile SiblingFile(ResourceGroupFixture f, string title)
    {
        var file = ProjectFile.Create(f.Project.Id, title, null, f.Now);
        file.RelocateTo(
            f.Storage.ReserveFolderSegment(f.Project.FolderSegment, title, new HashSet<string>()), f.Now);
        f.Files.Add(file);
        return file;
    }

    /// <summary>
    /// Delegates everything; <c>ReserveFolderSegment</c> throws. A deterministic wall — an
    /// ACL, a path-length limit, a name the filesystem refuses — rather than a transient one,
    /// so the group is left holding the segment it already had.
    /// </summary>
    private sealed class RefusingReservations(IResourceStorage inner) : IResourceStorage
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
            string relativeParent, string preferredSegment, IReadOnlySet<string> claimed,
            string? ownedSegment = null)
            => throw new IOException("the folder could not be created");

        public string ResolvePath(string storedPath) => inner.ResolvePath(storedPath);

        public bool Exists(string storedPath) => inner.Exists(storedPath);

        public void Delete(string storedPath) => inner.Delete(storedPath);
    }

    /// <summary>
    /// Delegates everything; <see cref="GetForFile"/> throws. The read an import now depends
    /// on to know which folder names are spoken for.
    /// </summary>
    private sealed class FaultingGroups(IResourceGroupRepository inner) : IResourceGroupRepository
    {
        public void Add(ResourceGroup group) => inner.Add(group);

        public void Update(ResourceGroup group) => inner.Update(group);

        public void Delete(ResourceGroupId id) => inner.Delete(id);

        public ResourceGroup? GetById(ResourceGroupId id) => inner.GetById(id);

        public IReadOnlyList<ResourceGroup> GetForFile(ProjectFileId fileId)
            => throw new InvalidOperationException("group read rejected");
    }

    /// <summary>
    /// The behaviour change this fix makes to a user-facing operation, pinned so it is a
    /// decision rather than a side effect.
    ///
    /// <c>ImportFile</c> now reads the File's groups to learn which folder names are spoken
    /// for, so a read that faults makes the import fail where it previously stored the bytes.
    /// That is the right answer and the one the rest of the design already takes: the claims
    /// are the fourth argument to <c>Store</c> and are evaluated before it is called, so the
    /// throw lands before any byte is copied — no row, no bytes, nothing half-done, and a
    /// clean retry. Storing anyway would mean placing blind, which is the permanent damage
    /// this whole change exists to prevent.
    ///
    /// The alternative failure — swallowing the fault and importing with an empty claim set —
    /// is what the assertions here rule out: an import that "succeeded" by taking a group's
    /// folder name as a file leaves that group unable to create its directory ever again.
    ///
    /// The batch importer catches per file and reports a notice, so a user sees one failed
    /// import rather than a crash.
    /// </summary>
    [Fact]
    public void ImportFile_WhenTheGroupReadFaults_LeavesNoBytesAndNoRow()
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var group = f.Group("Notes");
        var existing = f.Resources.GetForFile(f.File.Id).Count;
        var source = f.SourceFile("Notes", "loose bytes");

        var service = f.CreateService(groups: new FaultingGroups(f.Groups));
        var failure = Assert.Throws<InvalidOperationException>(
            () => service.ImportFile(f.File.Id, ResourceKind.Document, source));
        Assert.Equal("group read rejected", failure.Message);

        // Nothing half-done: no row, no bytes anywhere the import could have put them, and
        // above all not on the group's claimed name.
        Assert.Equal(existing, f.Resources.GetForFile(f.File.Id).Count);
        Assert.False(f.Storage.Exists(Path.Combine(parent, "Notes")));
        Assert.False(f.Storage.Exists(Path.Combine(parent, "Notes (2)")));
        Assert.True(Directory.Exists(Resolve(f, parent, group.FolderSegment)));

        // The source is untouched, so the retry the user is left with is a real one.
        Assert.Equal("loose bytes", System.IO.File.ReadAllText(source));
        Assert.Equal(
            Path.Combine(parent, "Notes (2)"),
            f.CreateService().ImportFile(f.File.Id, ResourceKind.Document, source).StoredPath);
    }

    /// <summary>
    /// The defect itself, in all four shapes it takes.
    ///
    /// A group named "Notes" holds its directory; a loose extensionless document of the same
    /// name was pushed to "Notes (2)" beside it; one document is filed into the group. Rename
    /// the File — or the Project above it — and every destination directory is suddenly
    /// missing, because a rename moves nothing until the reconcile does. The loose document's
    /// desired name is then free on disk, and a disk-only check hands it the group's folder
    /// path as a *file*.
    ///
    /// Whether that happens turned entirely on <c>AddedAt</c>: the reconciler walks in that
    /// order, so a loose-first File broke and a grouped-first File converged. Both orderings
    /// are pinned here so the ordering stops mattering — and so a later change that "fixes"
    /// this by sorting instead cannot pass.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void RenamingAParent_KeepsAGroupsFolderClaimed_WhicheverResourceMovesFirst(
        bool renameTheFile, bool looseFirst)
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group("Notes");
        Assert.Equal("Notes", group.FolderSegment);

        // Created in this order either way — the group's directory is what pushes the loose
        // document to "Notes (2)" — but given the AddedAt the walk will actually use.
        var start = f.Now;
        f.Now = start.AddMinutes(looseFirst ? 1 : 2);
        var loose = f.Document("Notes", "loose bytes");
        Assert.Equal(
            Path.Combine(ResourceLayout.FolderFor(f.Project, f.File), "Notes (2)"), loose.StoredPath);

        f.Now = start.AddMinutes(looseFirst ? 2 : 1);
        var member = f.Document("member.txt", "member bytes");
        f.Assign(member.Id, group.Id);
        f.Now = start.AddMinutes(5);

        var service = f.CreateService();
        if (renameTheFile)
        {
            service.RenameFile(f.File.Id, "Spanish II");
        }
        else
        {
            service.RenameProject(f.Project.Id, "Schoolwork II");
        }

        var project = f.Projects.GetById(f.Project.Id)!;
        var file = f.Files.GetById(f.File.Id)!;
        Assert.Equal(renameTheFile ? "Spanish II" : "Spanish", file.FolderSegment);
        Assert.Equal(renameTheFile ? "Schoolwork" : "Schoolwork II", project.FolderSegment);

        var parent = ResourceLayout.FolderFor(project, file);
        Assert.Equal("Notes", f.Groups.GetById(group.Id)!.FolderSegment);

        var memberPath = Path.Combine(parent, "Notes", "member.txt");
        Assert.Equal(memberPath, f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal("member bytes", Read(f, memberPath));
        Assert.True(Directory.Exists(Resolve(f, parent, "Notes")));

        var loosePath = Path.Combine(parent, "Notes (2)");
        Assert.Equal(loosePath, f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("loose bytes", Read(f, loosePath));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// The case no ordering can reach. An empty group has no member to create its directory,
    /// and after a rename its claim exists nowhere but the row — the directory it reserved is
    /// under the old parent and the new one has never been made. An extensionless import of
    /// the same name is then the only thing asking for that path, so a disk check has nothing
    /// to refuse it with, and taking it would leave the group unable to create its directory
    /// ever again: its persisted segment would name a file.
    ///
    /// Imported through the service rather than the fixture on purpose — this is the
    /// production path, and it is the one whose <c>Store</c> call has to carry the claims.
    /// </summary>
    [Fact]
    public void AnEmptyGroupsClaim_SurvivesAParentRename_AndAnExtensionlessImportAfterIt()
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group("Notes");
        f.Document("syllabus.txt", "syllabus bytes");

        var service = f.CreateService();
        service.RenameFile(f.File.Id, "Spanish II");

        var file = f.Files.GetById(f.File.Id)!;
        var parent = ResourceLayout.FolderFor(f.Project, file);
        Assert.Equal("Notes", f.Groups.GetById(group.Id)!.FolderSegment);

        // The claim has no directory to stand on: the group never had a member to make one.
        Assert.False(Directory.Exists(Resolve(f, parent, "Notes")));
        Assert.False(System.IO.File.Exists(Resolve(f, parent, "Notes")));

        var imported = service.ImportFile(
            file.Id, ResourceKind.Document, f.SourceFile("Notes", "loose bytes"));
        Assert.Equal(Path.Combine(parent, "Notes (2)"), imported.StoredPath);
        Assert.Equal("loose bytes", Read(f, imported.StoredPath));
        Assert.False(System.IO.File.Exists(Resolve(f, parent, "Notes")));

        // And the group can still take its directory the moment it gains a member.
        var member = service.ImportFile(
            file.Id, ResourceKind.Document, f.SourceFile("member.txt", "member bytes"));
        service.MoveResourceToGroup(member.Id, group.Id);

        var memberPath = Path.Combine(parent, "Notes", "member.txt");
        Assert.Equal(memberPath, f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal("member bytes", Read(f, memberPath));
        Assert.Equal(Path.Combine(parent, "Notes (2)"), f.Resources.GetById(imported.Id)!.StoredPath);
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// Two groups may share a title; they may never share a directory. Identity is the row's
    /// id and the segment it claimed, so a rename of the parent must carry both segments
    /// through unchanged and put each group's members — exactly those members — back inside
    /// them, with a loose document of the same name pushed past both.
    ///
    /// A claim set that collapsed to one entry per title, or that a rename re-derived rather
    /// than read, would merge these two silently.
    /// </summary>
    [Fact]
    public void TwoGroupsSharingATitle_KeepTheirSegmentsAndContents_AcrossAParentRename()
    {
        using var f = new ResourceGroupFixture();
        var first = f.Group("Notes");
        var second = f.Group("Notes");
        Assert.Equal("Notes", first.FolderSegment);
        Assert.Equal("Notes (2)", second.FolderSegment);

        var loose = f.Document("Notes", "loose bytes");
        var alpha = f.Document("alpha.txt", "alpha bytes");
        var beta = f.Document("beta.txt", "beta bytes");
        f.Assign(alpha.Id, first.Id);
        f.Assign(beta.Id, second.Id);

        f.CreateService().RenameFile(f.File.Id, "Spanish II");

        var file = f.Files.GetById(f.File.Id)!;
        var parent = ResourceLayout.FolderFor(f.Project, file);
        Assert.Equal("Notes", f.Groups.GetById(first.Id)!.FolderSegment);
        Assert.Equal("Notes (2)", f.Groups.GetById(second.Id)!.FolderSegment);

        var alphaPath = Path.Combine(parent, "Notes", "alpha.txt");
        var betaPath = Path.Combine(parent, "Notes (2)", "beta.txt");
        var loosePath = Path.Combine(parent, "Notes (3)");
        Assert.Equal(alphaPath, f.Resources.GetById(alpha.Id)!.StoredPath);
        Assert.Equal(betaPath, f.Resources.GetById(beta.Id)!.StoredPath);
        Assert.Equal(loosePath, f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("alpha bytes", Read(f, alphaPath));
        Assert.Equal("beta bytes", Read(f, betaPath));
        Assert.Equal("loose bytes", Read(f, loosePath));

        // Exactly these members, not merely "a member each".
        var contents = f.Resources.GetForFile(file.Id);
        Assert.Equal([alpha.Id], contents.Where(r => r.GroupId == first.Id).Select(r => r.Id));
        Assert.Equal([beta.Id], contents.Where(r => r.GroupId == second.Id).Select(r => r.Id));
        Assert.Equal([loose.Id], contents.Where(r => r.GroupId is null).Select(r => r.Id));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// Prevention alone would leave every database that has already gone wrong wrong for
    /// good, so the rule has to heal as well as hold.
    ///
    /// The stranded state: a loose file sits at the group's claimed folder path, and the
    /// group's directory therefore cannot exist. <c>IsAlreadyPlaced</c> compares that file's
    /// folder and name against what it wants and finds an exact match — so, untold about the
    /// claim, it blesses the file where it stands, the file never moves, the directory can
    /// never be created, and every member of the group is skipped forever.
    ///
    /// Pinned in both walk orders. With the member first its move throws onto the file and it
    /// is deferred for one pass; with the loose file first it is out of the way before the
    /// member is reached. Either way it converges, and the pass after that moves nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStrandedLooseFile_IsMovedOffAGroupsClaimedName_AndTheGroupRecovers(bool looseFirst)
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var group = f.Group("Notes");

        // Exactly what a pre-fix rename left behind: the group's directory gone from the
        // destination, and a loose extensionless document holding its name as a file.
        Directory.Delete(Resolve(f, parent, "Notes"));
        var start = f.Now;
        f.Now = start.AddMinutes(looseFirst ? 1 : 2);
        var loose = Resource.CreateStored(
            f.File.Id, ResourceKind.Document, "Notes", "Notes", Path.Combine(parent, "Notes"), f.Now);
        System.IO.File.WriteAllText(f.Storage.ResolvePath(loose.StoredPath!), "loose bytes");
        f.Resources.Add(loose);

        f.Now = start.AddMinutes(looseFirst ? 2 : 1);
        var member = f.Document("member.txt", "member bytes");
        f.Assign(member.Id, group.Id);
        f.Now = start.AddMinutes(5);

        // Two passes at most: the deferred member gets its turn once the name is free.
        f.Reconciler().ReconcileProject(f.Project.Id);
        f.Reconciler().ReconcileProject(f.Project.Id);

        var loosePath = Path.Combine(parent, "Notes (2)");
        Assert.Equal(loosePath, f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("loose bytes", Read(f, loosePath));

        var memberPath = Path.Combine(parent, "Notes", "member.txt");
        Assert.Equal(memberPath, f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal("member bytes", Read(f, memberPath));
        Assert.True(Directory.Exists(Resolve(f, parent, "Notes")));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// The recovery half, stated purely. <c>IsAlreadyPlaced</c> is where a stranded file
    /// would otherwise be frozen — it is asked about a file whose folder and name are exactly
    /// what the layout wants, and the honest answer is still "no", because the path belongs
    /// to a group. Both other directions are pinned too: told nothing it must still bless the
    /// file (that is the pre-group reading, and where the defect lived), and a numbered
    /// neighbour that no group claims must stay placed rather than churning every run.
    /// </summary>
    [Fact]
    public void IsAlreadyPlaced_RefusesAFileSittingOnAGroupsClaimedFolder()
    {
        var folder = Path.Combine("Schoolwork", "Spanish II");
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(folder, "Notes"),
        };

        Assert.False(
            ResourceLayout.IsAlreadyPlaced(Path.Combine(folder, "Notes"), folder, "Notes", claimed));

        // Case-insensitively, because osx-arm64 ships too and a group there may be "notes".
        Assert.False(
            ResourceLayout.IsAlreadyPlaced(Path.Combine(folder, "notes"), folder, "notes", claimed));

        // Told nothing, it blesses the same file — which is precisely the defect.
        Assert.True(ResourceLayout.IsAlreadyPlaced(Path.Combine(folder, "Notes"), folder, "Notes"));

        // And the claim covers one name, not the folder around it.
        Assert.True(
            ResourceLayout.IsAlreadyPlaced(Path.Combine(folder, "Notes (2)"), folder, "Notes", claimed));
    }

    /// <summary>
    /// Nothing here may depend on objects the process happens to be holding. Fresh
    /// repositories over the same database — the shape of a restart — must reach the same
    /// folders, because the group's segment is persisted rather than re-derived, and must
    /// then move nothing at all.
    ///
    /// The empty group is the load-bearing part: it owns no bytes, so the only trace of it a
    /// restarted process has is its row.
    /// </summary>
    [Fact]
    public void AfterARestart_EveryResourceIsPlaced_AndAFurtherReconcileMovesNothing()
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group("Notes");
        var empty = f.Group("Sources");
        var loose = f.Document("Notes", "loose bytes");
        var member = f.Document("member.txt", "member bytes");
        f.Assign(member.Id, group.Id);

        f.CreateService().RenameFile(f.File.Id, "Spanish II");

        // New repositories and a new reconciler over the same durable database.
        var restarted = new ResourceLayoutReconciler(
            new SqliteProjectRepository(f.Database.Factory),
            new SqliteProjectFileRepository(f.Database.Factory),
            new SqliteResourceRepository(f.Database.Factory), f.Storage, f,
            new SqliteResourceGroupRepository(f.Database.Factory));
        Assert.Equal(0, restarted.Reconcile());

        var file = f.Files.GetById(f.File.Id)!;
        var parent = ResourceLayout.FolderFor(f.Project, file);
        Assert.Equal("Notes", f.Groups.GetById(group.Id)!.FolderSegment);
        Assert.Equal("Sources", f.Groups.GetById(empty.Id)!.FolderSegment);
        Assert.Equal(
            Path.Combine(parent, "Notes", "member.txt"), f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal("member bytes", Read(f, Path.Combine(parent, "Notes", "member.txt")));
        Assert.Equal(Path.Combine(parent, "Notes (2)"), f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("loose bytes", Read(f, Path.Combine(parent, "Notes (2)")));

        Assert.Equal(0, restarted.Reconcile());
    }

    /// <summary>
    /// A reservation that fails must leave the group holding the segment it already had —
    /// and that segment goes on being claimed. The two failures worth ruling out are
    /// opposite: a rename that half-lands and frees the old name for a loose file to take,
    /// and a failure that stops the sweep and strands an unrelated File's documents.
    ///
    /// The group's directory is deleted first so nothing but the row is defending the name.
    /// A disk check would let the stray straight in.
    /// </summary>
    [Fact]
    public void AFailedGroupReservation_NeitherPlacesUnsafely_NorStopsAnotherFile()
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var group = f.Group("Notes");
        Directory.Delete(Resolve(f, parent, "Notes"));
        var stray = LegacyDocument(f, f.File.Id, "Notes", "stray bytes");

        var healthy = SiblingFile(f, "History");
        var elsewhere = LegacyDocument(f, healthy.Id, "Brief.pdf", "brief bytes");

        var refused = Assert.Throws<IOException>(
            () => f.CreateService(storage: new RefusingReservations(f.Storage)).RenameGroup(group.Id, "Reading"));
        Assert.Equal("the folder could not be created", refused.Message);
        var unchanged = f.Groups.GetById(group.Id)!;
        Assert.Equal("Notes", unchanged.Title);
        Assert.Equal("Notes", unchanged.FolderSegment);

        Assert.Equal(2, f.Reconciler().ReconcileProject(f.Project.Id));

        // The claim held even with nothing on disk behind it.
        Assert.Equal(Path.Combine(parent, "Notes (2)"), f.Resources.GetById(stray.Id)!.StoredPath);
        Assert.Equal("stray bytes", Read(f, Path.Combine(parent, "Notes (2)")));
        Assert.False(System.IO.File.Exists(Resolve(f, parent, "Notes")));

        // And the File that had nothing to do with any of it still got its turn.
        var elsewherePath = Path.Combine(ResourceLayout.FolderFor(f.Project, healthy), "Brief.pdf");
        Assert.Equal(elsewherePath, f.Resources.GetById(elsewhere.Id)!.StoredPath);
        Assert.Equal("brief bytes", Read(f, elsewherePath));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }
}
