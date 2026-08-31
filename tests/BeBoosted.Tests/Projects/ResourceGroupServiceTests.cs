using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// The three actions a user actually triggers on a group — create it, rename it, file a
/// resource into or out of it — over a real database and a real resources directory. The
/// service is the only layer that can enforce group/File coherence and refuse to claim a
/// folder beneath an unclaimed parent, so those refusals are pinned here rather than
/// assumed of anything below.
/// </summary>
public sealed class ResourceGroupServiceTests
{
    private static string Read(ResourceGroupFixture f, string? storedPath)
        => System.IO.File.ReadAllText(f.Storage.ResolvePath(storedPath!));

    /// <summary>
    /// Delegates everything, with the two failures a group action has to survive: a
    /// reservation that cannot create its directory, and a byte move that cannot happen.
    /// <see cref="Exists"/> is forwarded unchanged and stays file-only — teaching it to
    /// count directories would quietly disable the reconciler's adoption probe, which is
    /// the very thing these tests rely on to converge afterwards.
    /// </summary>
    private sealed class SabotagedStorage(IResourceStorage inner) : IResourceStorage
    {
        public bool RefuseReservations { get; set; }

        public bool RefuseMoves { get; set; }

        public string Store(
            string relativeFolder, string preferredFileName, string sourcePath,
            IReadOnlySet<string> claimedFolders)
            => inner.Store(relativeFolder, preferredFileName, sourcePath, claimedFolders);

        public string? MoveInto(
            string currentStoredPath, string relativeFolder, string preferredFileName,
            IReadOnlySet<string> claimedFolders)
            => RefuseMoves
                ? null
                : inner.MoveInto(currentStoredPath, relativeFolder, preferredFileName, claimedFolders);

        public string ReserveFolderSegment(
            string relativeParent, string preferredSegment, IReadOnlySet<string> claimed, string? ownedSegment = null)
            => RefuseReservations
                ? throw new IOException("the folder could not be created")
                : inner.ReserveFolderSegment(relativeParent, preferredSegment, claimed, ownedSegment);

        public string ResolvePath(string storedPath) => inner.ResolvePath(storedPath);

        public bool Exists(string storedPath) => inner.Exists(storedPath);

        public void Delete(string storedPath) => inner.Delete(storedPath);
    }

    /// <summary>
    /// Delegates everything; <see cref="GetForFile"/> throws once armed. The reconciler
    /// reads it while building its cross-owner claimed-path set, which is outside its
    /// per-resource recovery, so this is the shape that makes a whole reconcile pass throw.
    /// </summary>
    private sealed class FaultingResources(IResourceRepository inner) : IResourceRepository
    {
        public bool Fault { get; set; }

        public void Add(Resource resource) => inner.Add(resource);

        public void Update(Resource resource) => inner.Update(resource);

        public void Delete(ResourceId id) => inner.Delete(id);

        public Resource? GetById(ResourceId id) => inner.GetById(id);

        public IReadOnlyList<Resource> GetForFile(ProjectFileId fileId)
            => Fault
                ? throw new InvalidOperationException("resource read rejected")
                : inner.GetForFile(fileId);

        public int CountForFile(ProjectFileId fileId) => inner.CountForFile(fileId);

        public void SetIndexText(ResourceId id, string text) => inner.SetIndexText(id, text);

        public IReadOnlyList<Resource> SearchInProject(ProjectId projectId, string query)
            => inner.SearchInProject(projectId, query);
    }

    /// <summary>
    /// The same service the fixture builds, with one substituted reconciler. Spelled out
    /// here rather than added to the fixture because only the AfterCommit test needs a
    /// reconciler that fails on purpose.
    /// </summary>
    private static ProjectService ServiceWith(
        ResourceGroupFixture f, ResourceLayoutReconciler reconciler)
        => new(f.Projects, f.Files, f.Resources, f.Storage,
            new SqliteProjectMutations(f.Database.Factory),
            new SimpleLocalIndexer(f.Resources, f.Storage, f),
            new SqliteTaskRepository(f.Database.Factory),
            new SqliteCalendarBlockRepository(f.Database.Factory),
            new SqliteOccurrenceCompletionRepository(f.Database.Factory), f, f.Groups,
            provenanceInvalidator: null, reconciler: reconciler);

    /// <summary>
    /// A rename whose sanitized form differs only in case resolves to the segment this
    /// group already owns — because reservation *creates* directories, the group's own
    /// folder would otherwise read as occupied and displace it to "notes (2)", moving
    /// every byte in the group for no reason at all.
    /// </summary>
    [Fact]
    public void SanitizationEquivalentRename_RetainsItsOwnedSegment()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var group = service.CreateGroup(f.File.Id, "Notes");
        var r = f.Document();
        service.MoveResourceToGroup(r.Id, group.Id);
        var stored = f.Resources.GetById(r.Id)!.StoredPath;
        var renamed = service.RenameGroup(group.Id, "notes");
        Assert.Equal("notes", renamed.Title);
        Assert.Equal(group.FolderSegment, renamed.FolderSegment);
        Assert.Equal(stored, f.Resources.GetById(r.Id)!.StoredPath);
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// A resource crossing group boundaries keeps its identity, its bytes and its place in
    /// the index. Membership is a single-row change; nothing here re-imports, re-indexes,
    /// or mints a new resource.
    ///
    /// The per-hop stored-path assertion is what makes this a test of the move *operation*
    /// rather than only of what the move preserves: every other assertion here stays true
    /// if the bytes never follow the membership at all. It iterates the groups rather than
    /// their ids so the expected folder can be built with the three-argument
    /// <c>FolderFor</c> — which resolves the loose folder for the trailing null hop.
    /// </summary>
    [Fact]
    public void MoveIntoBetweenAndOut_KeepsIdentityBytesAndSearchText()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var a = service.CreateGroup(f.File.Id, "Unit 3");
        var b = service.CreateGroup(f.File.Id, "Unit 4");
        var r = f.Document("source.txt", "search-only-token");
        foreach (var destination in new ResourceGroup?[] { a, b, null })
        {
            service.MoveResourceToGroup(r.Id, destination?.Id);
            var current = f.Resources.GetById(r.Id)!;
            Assert.Equal(destination?.Id, current.GroupId);
            Assert.Equal(r.Id, current.Id);
            Assert.Equal(ResourceIndexState.Indexed, current.IndexState);
            Assert.Equal(
                Path.Combine(ResourceLayout.FolderFor(f.Project, f.File, destination), "source.txt"),
                current.StoredPath);
            Assert.Equal("search-only-token",
                System.IO.File.ReadAllText(f.Storage.ResolvePath(current.StoredPath!)));
            Assert.Equal(r.Id, Assert.Single(
                f.Resources.SearchInProject(f.Project.Id, "search-only-token")).Id);
        }

        Assert.Equal(Path.Combine(f.Project.FolderSegment, f.File.FolderSegment),
            Path.GetDirectoryName(f.Resources.GetById(r.Id)!.StoredPath));
    }

    /// <summary>
    /// Nothing below the service can catch this. <c>Resource.MoveToGroup</c> takes a bare
    /// id, and <c>resources.group_id</c> only requires that the group row exist — not that
    /// it belong to this resource's File — so a cross-File assignment is foreign-key legal
    /// and commits happily. What follows is silent and permanent: the reconciler refuses to
    /// resolve a group from another File, so the document is skipped on every run from then
    /// on and its bytes never move again.
    /// </summary>
    [Fact]
    public void CrossFileMove_ThrowsBeforeAnyMutation()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var history = service.CreateFile(f.Project.Id, "History", null);
        var foreign = service.CreateGroup(history.Id, "Notes");
        var r = f.Document("essay.txt", "essay bytes");
        var before = f.Resources.GetById(r.Id)!.StoredPath;

        Assert.Throws<DomainException>(() => service.MoveResourceToGroup(r.Id, foreign.Id));

        var after = f.Resources.GetById(r.Id)!;
        Assert.Null(after.GroupId);
        Assert.Equal(before, after.StoredPath);
        Assert.Equal("essay bytes", Read(f, after.StoredPath));
    }

    /// <summary>
    /// Filing into a group that is already gone must be refused in the service's own
    /// vocabulary, before the row write. Left to the foreign key it surfaces as a raw
    /// SQLite constraint failure the UI has no message for.
    /// </summary>
    [Fact]
    public void MissingTarget_ThrowsAndPreservesSource()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var group = service.CreateGroup(f.File.Id, "Notes");
        var r = f.Document("essay.txt", "essay bytes");
        var before = f.Resources.GetById(r.Id)!.StoredPath;
        f.Groups.Delete(group.Id);

        Assert.Throws<DomainException>(() => service.MoveResourceToGroup(r.Id, group.Id));

        var after = f.Resources.GetById(r.Id)!;
        Assert.Null(after.GroupId);
        Assert.Equal(before, after.StoredPath);
        Assert.Equal("essay bytes", Read(f, after.StoredPath));
    }

    /// <summary>
    /// A group folder must never be claimed beneath a parent that has claimed nothing.
    /// <c>Path.Combine</c> swallows an empty part, so an unclaimed File would silently put
    /// the group's directory straight into the Project folder — or, with an unclaimed
    /// Project, into the resources root — and the row would then name a folder no part of
    /// the layout agrees with. Both halves of the guard are exercised, because checking
    /// only one leaves the other flattening.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnclaimedParent_CreateRenameMoveDoNotTouchRowsOrBytes(bool unclaimTheProject)
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var group = service.CreateGroup(f.File.Id, "Notes");
        var member = f.Document("member.txt", "member bytes");
        service.MoveResourceToGroup(member.Id, group.Id);
        var loose = f.Document("loose.txt", "loose bytes");
        var memberPath = f.Resources.GetById(member.Id)!.StoredPath;
        var loosePath = f.Resources.GetById(loose.Id)!.StoredPath;

        if (unclaimTheProject)
        {
            f.Projects.Update(Project.Rehydrate(
                f.Project.Id, f.Project.Name, f.Project.AccentColor, f.Now, f.Now, string.Empty));
        }
        else
        {
            f.Files.Update(ProjectFile.Rehydrate(
                f.File.Id, f.Project.Id, f.File.Title, f.File.Description, f.Now, f.Now, string.Empty));
        }

        Assert.Throws<DomainException>(() => service.CreateGroup(f.File.Id, "Sources"));
        Assert.Throws<DomainException>(() => service.RenameGroup(group.Id, "Renamed"));
        Assert.Throws<DomainException>(() => service.MoveResourceToGroup(loose.Id, group.Id));
        Assert.Throws<DomainException>(() => service.MoveResourceToGroup(member.Id, null));

        var reloaded = Assert.Single(f.Groups.GetForFile(f.File.Id));
        Assert.Equal("Notes", reloaded.Title);
        Assert.Equal(group.FolderSegment, reloaded.FolderSegment);
        Assert.Equal(group.Id, f.Resources.GetById(member.Id)!.GroupId);
        Assert.Null(f.Resources.GetById(loose.Id)!.GroupId);
        Assert.Equal(memberPath, f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal(loosePath, f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("member bytes", Read(f, memberPath));
        Assert.Equal("loose bytes", Read(f, loosePath));
    }

    /// <summary>
    /// A group with no members yet owns nothing on disk but the directory itself, and that
    /// directory is the whole claim: it is what a later extensionless import collides with
    /// instead of taking the group's exact path as a file. Both halves are asserted — the
    /// directory before any resource exists, and the import yielding to it.
    /// </summary>
    [Fact]
    public void CreateEmptyGroup_ClaimsDirectoryBeforeImport()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);

        var group = service.CreateGroup(f.File.Id, "Notes");

        Assert.Equal("Notes", group.FolderSegment);
        Assert.True(Directory.Exists(f.Storage.ResolvePath(Path.Combine(parent, "Notes"))));
        Assert.Empty(f.Resources.GetForFile(f.File.Id));

        var imported = service.ImportFile(
            f.File.Id, ResourceKind.Document, f.SourceFile("Notes", "loose bytes"));

        Assert.Null(imported.GroupId);
        Assert.Equal(Path.Combine(parent, "Notes (2)"), imported.StoredPath);
        Assert.Equal("loose bytes", Read(f, imported.StoredPath));
        Assert.True(Directory.Exists(f.Storage.ResolvePath(Path.Combine(parent, "Notes"))));
    }

    /// <summary>
    /// Two groups may share a title; identity is the row and the segment it claimed. The
    /// clock never moves here, so the ordering rests on sort_order alone.
    /// </summary>
    [Fact]
    public void SameTitleGroups_StayDistinct()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);

        var first = service.CreateGroup(f.File.Id, "Notes");
        var second = service.CreateGroup(f.File.Id, "Notes");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Notes", first.FolderSegment);
        Assert.Equal("Notes (2)", second.FolderSegment);
        Assert.Equal(0, first.SortOrder);
        Assert.Equal(1, second.SortOrder);
        Assert.Equal(
            new[] { first.Id, second.Id },
            service.GetGroups(f.File.Id).Select(g => g.Id).ToArray());

        var alpha = f.Document("alpha.txt", "alpha bytes");
        service.MoveResourceToGroup(alpha.Id, first.Id);
        var beta = f.Document("beta.txt", "beta bytes");
        service.MoveResourceToGroup(beta.Id, second.Id);

        var alphaPath = Path.Combine(parent, "Notes", "alpha.txt");
        var betaPath = Path.Combine(parent, "Notes (2)", "beta.txt");
        Assert.Equal(alphaPath, f.Resources.GetById(alpha.Id)!.StoredPath);
        Assert.Equal(betaPath, f.Resources.GetById(beta.Id)!.StoredPath);
        Assert.Equal("alpha bytes", Read(f, alphaPath));
        Assert.Equal("beta bytes", Read(f, betaPath));
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// A rename that genuinely resolves to a new segment moves every member's bytes and
    /// nothing else's — not the loose document beside them, and not the other group's.
    /// The member note has no bytes at all and must simply keep its membership.
    /// </summary>
    [Fact]
    public void RealRename_MovesEveryMemberOnly()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var unit = service.CreateGroup(f.File.Id, "Unit 3");
        var other = service.CreateGroup(f.File.Id, "Sources");

        var one = f.Document("one.txt", "one bytes");
        var two = f.Document("two.txt", "two bytes");
        service.MoveResourceToGroup(one.Id, unit.Id);
        service.MoveResourceToGroup(two.Id, unit.Id);
        var note = service.AddNote(f.File.Id, "Reading note", "chapter three");
        service.MoveResourceToGroup(note.Id, unit.Id);
        var loose = f.Document("loose.txt", "loose bytes");
        var neighbour = f.Document("neighbour.txt", "neighbour bytes");
        service.MoveResourceToGroup(neighbour.Id, other.Id);

        var loosePath = f.Resources.GetById(loose.Id)!.StoredPath;
        var neighbourPath = f.Resources.GetById(neighbour.Id)!.StoredPath;
        Assert.Equal(Path.Combine(parent, "Unit 3", "one.txt"), f.Resources.GetById(one.Id)!.StoredPath);
        Assert.Equal(Path.Combine(parent, "Sources", "neighbour.txt"), neighbourPath);

        var renamed = service.RenameGroup(unit.Id, "Unit 4");

        Assert.Equal("Unit 4", renamed.Title);
        Assert.Equal("Unit 4", renamed.FolderSegment);
        var onePath = Path.Combine(parent, "Unit 4", "one.txt");
        var twoPath = Path.Combine(parent, "Unit 4", "two.txt");
        Assert.Equal(onePath, f.Resources.GetById(one.Id)!.StoredPath);
        Assert.Equal(twoPath, f.Resources.GetById(two.Id)!.StoredPath);
        Assert.Equal("one bytes", Read(f, onePath));
        Assert.Equal("two bytes", Read(f, twoPath));

        var reloadedNote = f.Resources.GetById(note.Id)!;
        Assert.Equal(unit.Id, reloadedNote.GroupId);
        Assert.Null(reloadedNote.StoredPath);
        Assert.Equal("chapter three", reloadedNote.Content);

        Assert.Equal(loosePath, f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("loose bytes", Read(f, loosePath));
        Assert.Equal(neighbourPath, f.Resources.GetById(neighbour.Id)!.StoredPath);
        Assert.Equal("neighbour bytes", Read(f, neighbourPath));
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// A rename whose desired name is already taken on disk resolves to one disambiguated
    /// segment, and every member lands in that same one — the failure this rules out is
    /// two members disambiguating independently and splitting the group across folders.
    /// </summary>
    [Fact]
    public void RenameIntoCollision_KeepsOneSharedDestination()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var group = service.CreateGroup(f.File.Id, "Unit 3");
        var one = f.Document("one.txt", "one bytes");
        var two = f.Document("two.txt", "two bytes");
        service.MoveResourceToGroup(one.Id, group.Id);
        service.MoveResourceToGroup(two.Id, group.Id);

        // An extensionless loose document already occupies the exact name the rename wants.
        var occupant = f.Document("Unit 4", "occupant bytes");
        Assert.Equal(Path.Combine(parent, "Unit 4"), occupant.StoredPath);

        var renamed = service.RenameGroup(group.Id, "Unit 4");

        Assert.Equal("Unit 4", renamed.Title);
        Assert.Equal("Unit 4 (2)", renamed.FolderSegment);
        Assert.Equal("Unit 4 (2)", f.Groups.GetById(group.Id)!.FolderSegment);
        var onePath = Path.Combine(parent, "Unit 4 (2)", "one.txt");
        var twoPath = Path.Combine(parent, "Unit 4 (2)", "two.txt");
        Assert.Equal(onePath, f.Resources.GetById(one.Id)!.StoredPath);
        Assert.Equal(twoPath, f.Resources.GetById(two.Id)!.StoredPath);
        Assert.Equal("one bytes", Read(f, onePath));
        Assert.Equal("two bytes", Read(f, twoPath));
        Assert.Equal(Path.Combine(parent, "Unit 4"), f.Resources.GetById(occupant.Id)!.StoredPath);
        Assert.Equal("occupant bytes", Read(f, occupant.StoredPath));
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// Groups existing in a File changes nothing about how resources arrive: every
    /// creation API still produces a loose resource stored in the File's own folder.
    /// Filing is a separate, explicit act.
    /// </summary>
    [Fact]
    public void CreationApis_StillProduceLooseResources()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        service.CreateGroup(f.File.Id, "Notes");
        service.CreateGroup(f.File.Id, "Sources");

        var link = service.AddLink(f.File.Id, "SAT scores", "https://collegeboard.org/scores");
        var note = service.AddNote(f.File.Id, "Reading note", "chapter three");
        var document = service.ImportFile(
            f.File.Id, ResourceKind.Document, f.SourceFile("Transcript.pdf", "pdf bytes"));
        var image = service.ImportFile(
            f.File.Id, ResourceKind.Image, f.SourceFile("cert.png", "png bytes"));

        foreach (var id in new[] { link.Id, note.Id, document.Id, image.Id })
        {
            Assert.Null(f.Resources.GetById(id)!.GroupId);
        }

        Assert.Equal(Path.Combine(parent, "Transcript.pdf"), document.StoredPath);
        Assert.Equal(Path.Combine(parent, "cert.png"), image.StoredPath);
        Assert.Equal("pdf bytes", Read(f, document.StoredPath));
        Assert.Equal("png bytes", Read(f, image.StoredPath));
        Assert.DoesNotContain(f.Resources.GetForFile(f.File.Id), r => r.GroupId is not null);
    }

    /// <summary>
    /// Filing a resource into a group, renaming that group, and filing it back out change
    /// nothing an AI answer could have cited — same resource, same bytes, same text.
    /// Invalidating anyway would mark live derived items "Needs review" for a filing
    /// change, leaving the user to clear noise by hand.
    /// </summary>
    [Fact]
    public void MembershipAndRename_DoNotInvalidate()
    {
        using var f = new ResourceGroupFixture();
        var invalidator = new RecordingGroupInvalidator();
        var service = f.CreateService(invalidator: invalidator);
        var group = service.CreateGroup(f.File.Id, "Notes");
        var member = f.Document("member.txt", "member bytes");

        service.MoveResourceToGroup(member.Id, group.Id);
        service.RenameGroup(group.Id, "Sources");
        service.MoveResourceToGroup(member.Id, null);

        Assert.Empty(invalidator.Calls);
        Assert.Equal(ResourceIndexState.Indexed, f.Resources.GetById(member.Id)!.IndexState);
        Assert.Equal("member bytes", Read(f, f.Resources.GetById(member.Id)!.StoredPath));
    }

    /// <summary>
    /// A reservation that cannot create its directory must leave no row behind. The
    /// segment is persisted rather than recomputed, so a group row written without one
    /// would name a folder nothing ever claimed — and the repository refuses it precisely
    /// so that can never be papered over.
    /// </summary>
    [Fact]
    public void CreateGroup_WhenTheReservationFails_PersistsNothing()
    {
        using var f = new ResourceGroupFixture();
        var storage = new SabotagedStorage(f.Storage) { RefuseReservations = true };
        var service = f.CreateService(storage: storage);

        Assert.Throws<IOException>(() => service.CreateGroup(f.File.Id, "Notes"));

        Assert.Empty(f.Groups.GetForFile(f.File.Id));
    }

    /// <summary>
    /// The same for rename: the title change is only committed alongside the segment the
    /// reservation returned. A row left holding the new title and the old folder would
    /// display one thing and store another, permanently.
    /// </summary>
    [Fact]
    public void RenameGroup_WhenTheReservationFails_KeepsTitleAndSegment()
    {
        using var f = new ResourceGroupFixture();
        var storage = new SabotagedStorage(f.Storage);
        var service = f.CreateService(storage: storage);
        var group = service.CreateGroup(f.File.Id, "Notes");
        var member = f.Document("member.txt", "member bytes");
        service.MoveResourceToGroup(member.Id, group.Id);
        var settled = f.Resources.GetById(member.Id)!.StoredPath;

        storage.RefuseReservations = true;
        Assert.Throws<IOException>(() => service.RenameGroup(group.Id, "Sources"));

        var reloaded = f.Groups.GetById(group.Id)!;
        Assert.Equal("Notes", reloaded.Title);
        Assert.Equal(group.FolderSegment, reloaded.FolderSegment);
        Assert.Equal(settled, f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal("member bytes", Read(f, settled));
    }

    /// <summary>
    /// Membership is one row and commits on its own. A byte move that then fails must not
    /// undo it — the resource is filed, keeps its old path, stays openable, and the next
    /// healthy reconcile converges in one step. Reporting the move as failed would be the
    /// worse answer: the filing genuinely happened.
    /// </summary>
    [Fact]
    public void MoveResourceToGroup_WhenTheByteMoveFails_KeepsMembershipAndTheOldUsablePath()
    {
        using var f = new ResourceGroupFixture();
        var storage = new SabotagedStorage(f.Storage);
        var service = f.CreateService(storage: storage);
        var group = service.CreateGroup(f.File.Id, "Notes");
        var member = f.Document("member.txt", "member bytes");
        var loosePath = f.Resources.GetById(member.Id)!.StoredPath;

        storage.RefuseMoves = true;
        service.MoveResourceToGroup(member.Id, group.Id);

        var stalled = f.Resources.GetById(member.Id)!;
        Assert.Equal(group.Id, stalled.GroupId);
        Assert.Equal(loosePath, stalled.StoredPath);
        Assert.Equal("member bytes", Read(f, loosePath));

        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));

        var settled = f.Resources.GetById(member.Id)!;
        Assert.Equal(
            Path.Combine(ResourceLayout.FolderFor(f.Project, f.File, group), "member.txt"),
            settled.StoredPath);
        Assert.Equal("member bytes", Read(f, settled.StoredPath));
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// Re-issuing a move that already landed is the recovery path, not a no-op. It is
    /// reached from the state the test above builds: the byte relocation failed, so the
    /// resource is correctly filed but still sitting at its old path. The user sees it in
    /// the wrong place and files it into the same group again — and returning early on
    /// "already a member" would answer that with silence, leaving the bytes stranded until
    /// the next app start reconciles them.
    /// </summary>
    [Fact]
    public void ReIssuingAMoveWhoseBytesNeverFollowed_RetriesTheRelocation()
    {
        using var f = new ResourceGroupFixture();
        var storage = new SabotagedStorage(f.Storage);
        var service = f.CreateService(storage: storage);
        var group = service.CreateGroup(f.File.Id, "Notes");
        var member = f.Document("member.txt", "member bytes");
        var loosePath = f.Resources.GetById(member.Id)!.StoredPath;

        storage.RefuseMoves = true;
        service.MoveResourceToGroup(member.Id, group.Id);
        Assert.Equal(group.Id, f.Resources.GetById(member.Id)!.GroupId);
        Assert.Equal(loosePath, f.Resources.GetById(member.Id)!.StoredPath);

        // The same move again, with the filesystem healthy this time.
        storage.RefuseMoves = false;
        service.MoveResourceToGroup(member.Id, group.Id);

        var settled = f.Resources.GetById(member.Id)!;
        Assert.Equal(group.Id, settled.GroupId);
        Assert.Equal(
            Path.Combine(ResourceLayout.FolderFor(f.Project, f.File, group), "member.txt"),
            settled.StoredPath);
        Assert.Equal("member bytes", Read(f, settled.StoredPath));
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// Both post-mutation reconciles go through <c>AfterCommit</c>, which swallows. The row
    /// write has already committed by then, so a throw from the reconcile cannot undo the
    /// rename or the filing — it can only report an operation that fully succeeded as
    /// failed, and leave the user retrying something already done. Calling the reconciler
    /// directly, as RenameFile and RenameProject still do, is what this rules out; that
    /// divergence is deliberate and recorded in the phase-1 plan.
    /// </summary>
    [Fact]
    public void WhenTheReconcileThrows_TheCommittedRenameAndFilingStillStand()
    {
        using var f = new ResourceGroupFixture();
        var service = f.CreateService();
        var group = service.CreateGroup(f.File.Id, "Notes");
        var member = f.Document("member.txt", "member bytes");
        var loosePath = f.Resources.GetById(member.Id)!.StoredPath;

        var faulting = new FaultingResources(f.Resources) { Fault = true };
        var brittle = ServiceWith(f, f.Reconciler(resources: faulting));

        brittle.MoveResourceToGroup(member.Id, group.Id);
        var renamed = brittle.RenameGroup(group.Id, "Sources");

        Assert.Equal("Sources", renamed.Title);
        Assert.Equal("Sources", f.Groups.GetById(group.Id)!.Title);
        Assert.Equal(group.Id, f.Resources.GetById(member.Id)!.GroupId);
        Assert.Equal(loosePath, f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal("member bytes", Read(f, loosePath));

        // Nothing was lost: a healthy reconcile afterwards converges in one step.
        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));
        var settled = f.Resources.GetById(member.Id)!;
        Assert.Equal(
            Path.Combine(ResourceLayout.FolderFor(f.Project, f.File, renamed), "member.txt"),
            settled.StoredPath);
        Assert.Equal("member bytes", Read(f, settled.StoredPath));
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }
}
