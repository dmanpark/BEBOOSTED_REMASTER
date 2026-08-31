using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// Where a grouped resource's bytes belong, and — just as important — where they must
/// never be sent. A group owns a real directory whose name was reserved once and
/// persisted; the reconciler reads that persisted name rather than deriving one, so a
/// restart with fresh repositories reaches the same folder and moves nothing.
/// </summary>
public sealed class ResourceGroupLayoutTests
{
    /// <summary>
    /// Delegates everything; <see cref="GetForFile"/> answers with whatever the test hands
    /// it, which is the only way to *construct* these shapes in one step. It is not
    /// evidence that they are unreachable, and the guards it exercises must not be pruned
    /// as defence against nothing:
    ///
    /// <list type="bullet">
    /// <item>A group the read does not return is genuinely reachable. The foreign key
    /// requires only that the group row exist, not that it belong to this resource's File,
    /// so a cross-File assignment is FK-legal and lands in exactly this branch.</item>
    /// <item>An unclaimed parent needs no lying at all — it is live today, and
    /// <see cref="Reconcile_SkipsAGroupedResource_WhenItsProjectAndFileNeverClaimedSegments"/>
    /// builds it with real repositories.</item>
    /// <item>Only the blank segment is genuinely blocked, by the domain's
    /// <c>RelocateTo</c> and the repository's own refusal.</item>
    /// </list>
    /// </summary>
    private sealed class LyingGroups(
        IResourceGroupRepository inner,
        Func<ProjectFileId, IReadOnlyList<ResourceGroup>> forFile) : IResourceGroupRepository
    {
        public void Add(ResourceGroup group) => inner.Add(group);

        public void Update(ResourceGroup group) => inner.Update(group);

        public void Delete(ResourceGroupId id) => inner.Delete(id);

        public ResourceGroup? GetById(ResourceGroupId id) => inner.GetById(id);

        public IReadOnlyList<ResourceGroup> GetForFile(ProjectFileId fileId) => forFile(fileId);
    }

    /// <summary>
    /// Delegates everything; Update throws for chosen resources — the same wrapper shape
    /// <c>ResourceLayoutReconcilerTests</c> uses. Every read is forwarded, including the
    /// global <see cref="GetForFile"/> walk the reconciler's cross-owner claimed-path set
    /// is built from: a wrapper that answered that one narrowly would hide exactly the
    /// protection these tests exist to check.
    /// </summary>
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

    /// <summary>
    /// A document still parked at a legacy guid path, so a reconcile that reaches it has
    /// real work to do. Every skip test below keeps one of these loose alongside the
    /// corrupt resource: without it, a reconciler that skipped the whole File — or did
    /// nothing at all — would satisfy the same "unchanged" assertions.
    /// </summary>
    private static Resource LegacyDocument(
        ResourceGroupFixture f, ProjectFileId fileId, string originalName, string content = "legacy")
    {
        var resource = Resource.CreateStored(
            fileId, ResourceKind.Document, Path.GetFileNameWithoutExtension(originalName),
            originalName, Guid.NewGuid().ToString("N") + ".pdf", f.Now);
        Directory.CreateDirectory(f.ResourcesDirectory);
        System.IO.File.WriteAllText(f.Storage.ResolvePath(resource.StoredPath!), content);
        f.Resources.Add(resource);
        return resource;
    }

    private static string Read(ResourceGroupFixture f, string? storedPath)
        => System.IO.File.ReadAllText(f.Storage.ResolvePath(storedPath!));

    [Fact]
    public void Reconcile_UsesPersistedGroupSegment_AfterRestart_AndThenMovesNothing()
    {
        using var f = new ResourceGroupFixture();
        var obstacle = f.Document("Notes", "loose file");
        var group = f.Group("Notes");
        Assert.Equal("Notes (2)", group.FolderSegment);
        var one = f.Document("one.txt", "one");
        var two = f.Document("two.txt", "two");
        f.Assign(one.Id, group.Id);
        f.Assign(two.Id, group.Id);
        Assert.Equal(2, f.Reconciler().ReconcileProject(f.Project.Id));
        var destination = Path.Combine(f.Project.FolderSegment, f.File.FolderSegment, "Notes (2)");
        foreach (var id in new[] { one.Id, two.Id })
            Assert.Equal(destination, Path.GetDirectoryName(f.Resources.GetById(id)!.StoredPath));
        Assert.Equal("loose file", System.IO.File.ReadAllText(f.Storage.ResolvePath(obstacle.StoredPath!)));
        // New repositories/reconciler over the same durable database, not the same group objects.
        var restarted = new ResourceLayoutReconciler(
            new SqliteProjectRepository(f.Database.Factory),
            new SqliteProjectFileRepository(f.Database.Factory),
            new SqliteResourceRepository(f.Database.Factory), f.Storage, f,
            new SqliteResourceGroupRepository(f.Database.Factory));
        Assert.Equal(0, restarted.ReconcileProject(f.Project.Id));
        Assert.Equal(0, restarted.Reconcile());
    }

    /// <summary>
    /// A resource whose group the repository does not return. Treating the missing group
    /// as "loose" would relocate a user's document out of the folder it was filed in and
    /// into the File root — a silent, wrong move dressed up as a migration. The only safe
    /// reading is "I cannot tell where this belongs", which means leave it alone.
    /// </summary>
    [Fact]
    public void Reconcile_SkipsAGroupedResource_WhoseGroupTheRepositoryCannotSee()
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group("Notes");
        var stranded = LegacyDocument(f, f.File.Id, "Stranded.pdf", "stranded bytes");
        f.Assign(stranded.Id, group.Id);
        var loose = LegacyDocument(f, f.File.Id, "Loose.pdf", "loose bytes");

        var blind = new LyingGroups(f.Groups, _ => []);
        var moved = new ResourceLayoutReconciler(
            f.Projects, f.Files, f.Resources, f.Storage, f, blind).ReconcileProject(f.Project.Id);

        Assert.Equal(1, moved);
        Assert.Equal(stranded.StoredPath, f.Resources.GetById(stranded.Id)!.StoredPath);
        Assert.Equal("stranded bytes", Read(f, stranded.StoredPath));
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        Assert.Equal(Path.Combine(parent, "Loose.pdf"), f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.False(f.Storage.Exists(Path.Combine(parent, "Stranded.pdf")));
    }

    /// <summary>
    /// A group row that names another File. Its claimed directory lives under that other
    /// File, so combining its segment with this File's would name a directory no
    /// reservation ever created — and move the bytes there.
    /// </summary>
    [Fact]
    public void Reconcile_SkipsAGroupedResource_WhoseGroupBelongsToAnotherFile()
    {
        using var f = new ResourceGroupFixture();
        var other = ProjectFile.Create(f.Project.Id, "History", null, f.Now);
        other.RelocateTo(
            f.Storage.ReserveFolderSegment(f.Project.FolderSegment, "History", new HashSet<string>()), f.Now);
        f.Files.Add(other);
        var foreign = ResourceGroup.Create(other.Id, "Notes", 0, f.Now);
        foreign.RelocateTo(
            f.Storage.ReserveFolderSegment(
                ResourceLayout.FolderFor(f.Project, other), "Notes", new HashSet<string>()), f.Now);
        f.Groups.Add(foreign);

        var stranded = LegacyDocument(f, f.File.Id, "Stranded.pdf", "stranded bytes");
        f.Assign(stranded.Id, foreign.Id);
        var loose = LegacyDocument(f, f.File.Id, "Loose.pdf", "loose bytes");

        var lying = new LyingGroups(
            f.Groups, fileId => fileId == f.File.Id ? [foreign] : f.Groups.GetForFile(fileId));
        var moved = new ResourceLayoutReconciler(
            f.Projects, f.Files, f.Resources, f.Storage, f, lying).ReconcileProject(f.Project.Id);

        Assert.Equal(1, moved);
        Assert.Equal(stranded.StoredPath, f.Resources.GetById(stranded.Id)!.StoredPath);
        Assert.Equal("stranded bytes", Read(f, stranded.StoredPath));
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        Assert.Equal(Path.Combine(parent, "Loose.pdf"), f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.False(f.Storage.Exists(Path.Combine(parent, "Notes", "Stranded.pdf")));
    }

    /// <summary>
    /// A group holding the empty sentinel — no segment was ever reserved for it.
    /// <c>Path.Combine</c> swallows an empty final part, so the desired folder silently
    /// collapses to the File root and the member is flattened into loose storage. That is
    /// the quietest of all the failures here: every path is valid, nothing throws, and the
    /// document simply leaves its group.
    /// </summary>
    [Fact]
    public void Reconcile_SkipsAGroupedResource_WhoseGroupNeverClaimedASegment()
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group("Notes");
        var stranded = LegacyDocument(f, f.File.Id, "Stranded.pdf", "stranded bytes");
        f.Assign(stranded.Id, group.Id);
        var loose = LegacyDocument(f, f.File.Id, "Loose.pdf", "loose bytes");

        var unclaimed = ResourceGroup.Rehydrate(
            group.Id, f.File.Id, group.Title, group.SortOrder, f.Now, f.Now, string.Empty);
        var lying = new LyingGroups(
            f.Groups, fileId => fileId == f.File.Id ? [unclaimed] : f.Groups.GetForFile(fileId));
        var moved = new ResourceLayoutReconciler(
            f.Projects, f.Files, f.Resources, f.Storage, f, lying).ReconcileProject(f.Project.Id);

        Assert.Equal(1, moved);
        Assert.Equal(stranded.StoredPath, f.Resources.GetById(stranded.Id)!.StoredPath);
        Assert.Equal("stranded bytes", Read(f, stranded.StoredPath));
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        Assert.Equal(Path.Combine(parent, "Loose.pdf"), f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.False(f.Storage.Exists(Path.Combine(parent, "Stranded.pdf")));
    }

    /// <summary>
    /// The pre-0012 legacy shape, which the File-level guard deliberately lets through:
    /// both the Project and the File still hold the empty sentinel. A loose resource there
    /// must still reconcile — that is the whole point of not widening that guard — but a
    /// grouped one must not, because its group's segment would be combined against nothing
    /// and land in a top-level directory of the resources root that no reservation created.
    /// </summary>
    [Fact]
    public void Reconcile_SkipsAGroupedResource_WhenItsProjectAndFileNeverClaimedSegments()
    {
        using var f = new ResourceGroupFixture();
        var project = Project.Create("Legacy", "#ffffff", f.Now);
        f.Projects.Add(project);
        var file = ProjectFile.Create(project.Id, "Unclaimed", null, f.Now);
        f.Files.Add(file);
        Assert.Equal(string.Empty, project.FolderSegment);
        Assert.Equal(string.Empty, file.FolderSegment);

        var group = ResourceGroup.Create(file.Id, "Notes", 0, f.Now);
        group.RelocateTo("Notes", f.Now);
        f.Groups.Add(group);

        var stranded = LegacyDocument(f, file.Id, "Stranded.pdf", "stranded bytes");
        f.Assign(stranded.Id, group.Id);
        var loose = LegacyDocument(f, file.Id, "Loose.pdf", "loose bytes");

        Assert.Equal(1, f.Reconciler().ReconcileProject(project.Id));

        Assert.Equal(stranded.StoredPath, f.Resources.GetById(stranded.Id)!.StoredPath);
        Assert.Equal("stranded bytes", Read(f, stranded.StoredPath));
        Assert.Equal("Loose.pdf", f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.False(Directory.Exists(f.Storage.ResolvePath("Notes")));
    }

    /// <summary>
    /// Crash recovery across a group boundary. A resource leaving a group wants the bare
    /// name "Notes" in the File root, but a sibling group already reserved that exact name
    /// as a *directory*, so the mover parked the bytes at "Notes (2)" — and then the record
    /// failed. The repair pass has to find them.
    ///
    /// It cannot, if the adoption probe stops at the first candidate that is not a file:
    /// <c>IResourceStorage.Exists</c> is file-only by design (a directory at a probed path
    /// genuinely means "no adoptable file here", which is what stops
    /// <c>FindUnrecordedPlacement</c> from adopting a folder), so candidate 1 reads as
    /// missing and ends the contiguous probe one slot short of the bytes. Passing the
    /// File's known group directories in lets the probe step over a reserved name it can
    /// prove is a directory claim, without teaching Exists to see directories.
    ///
    /// Both cheap escapes are excluded by the assertions: skipping the File adopts
    /// nothing, and broadening Exists adopts candidate 1 — the group's own directory —
    /// recording "Notes" instead of "Notes (2)".
    /// </summary>
    [Fact]
    public void Reconcile_AfterFailedMoveOut_SkipsGroupDirectoryAndAdoptsNumberedFile()
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var obstacle = f.Group("Notes");
        var sources = f.Group("Sources");
        Assert.Equal("Notes", obstacle.FolderSegment);
        Assert.Equal("Sources", sources.FolderSegment);

        var document = f.Document("Notes", "payload");
        f.Assign(document.Id, sources.Id);
        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));
        var settled = Path.Combine(parent, "Sources", "Notes");
        Assert.Equal(settled, f.Resources.GetById(document.Id)!.StoredPath);

        // Assign re-reads, so the path the reconcile just recorded survives this.
        f.Assign(document.Id, null);

        var sabotaged = new SabotagedResources(f.Resources, r => r.Id == document.Id);
        Assert.Equal(0, f.Reconciler(resources: sabotaged).ReconcileProject(f.Project.Id));

        Assert.True(Directory.Exists(f.Storage.ResolvePath(Path.Combine(parent, "Notes"))));
        Assert.Equal("payload", Read(f, Path.Combine(parent, "Notes (2)")));
        Assert.Equal(settled, f.Resources.GetById(document.Id)!.StoredPath);

        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));
        var repaired = f.Resources.GetById(document.Id)!;
        Assert.Equal(Path.Combine(parent, "Notes (2)"), repaired.StoredPath);
        Assert.Equal("payload", Read(f, repaired.StoredPath));
        Assert.True(Directory.Exists(f.Storage.ResolvePath(Path.Combine(parent, "Notes"))));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// The claim is a hint about a directory, never a substitute for looking. The resources
    /// tree is browsable on purpose, so a user can delete a group's empty directory from
    /// outside the app — the group row goes on claiming the segment while nothing occupies
    /// it. A loose extensionless document named "Notes" then moves into the now-free path,
    /// and its record fails in the documented crash window.
    ///
    /// The bytes are now a real *file* at the exact path the group still claims. A probe
    /// that consults the claim before looking at the disk steps straight over them, and the
    /// damage is not merely a missed repair: the next candidate is an unrelated unrecorded
    /// orphan, so the row is pointed at another document's bytes and its own are left
    /// stranded. Physical reality has to win — the claim may only explain an absence.
    /// </summary>
    [Fact]
    public void Reconcile_WhenAClaimedGroupDirectoryIsGone_AdoptsTheRealFileNowAtThatPath()
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var group = f.Group("Notes");
        Assert.Equal("Notes", group.FolderSegment);

        // Deleted from outside the app while browsing; the row still claims "Notes".
        Directory.Delete(f.Storage.ResolvePath(Path.Combine(parent, "Notes")));

        // An unrelated orphan at the next candidate — what a claim-first probe would reach.
        System.IO.File.WriteAllText(f.Storage.ResolvePath(Path.Combine(parent, "Notes (2)")), "not mine");

        var document = LegacyDocument(f, f.File.Id, "Notes", "my bytes");
        var sabotaged = new SabotagedResources(f.Resources, r => r.Id == document.Id);
        Assert.Equal(0, f.Reconciler(resources: sabotaged).ReconcileProject(f.Project.Id));

        // The bytes reached the freed path as a file; the row still names the legacy path.
        Assert.Equal("my bytes", Read(f, Path.Combine(parent, "Notes")));
        Assert.False(Directory.Exists(f.Storage.ResolvePath(Path.Combine(parent, "Notes"))));
        Assert.Equal(document.StoredPath, f.Resources.GetById(document.Id)!.StoredPath);

        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));

        var repaired = f.Resources.GetById(document.Id)!;
        Assert.Equal(Path.Combine(parent, "Notes"), repaired.StoredPath);
        Assert.Equal("my bytes", Read(f, repaired.StoredPath));
        Assert.Equal("not mine", Read(f, Path.Combine(parent, "Notes (2)")));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// The group read obeys the same per-resource recovery contract as everything else in
    /// the loop. Loading it once per File, outside the try, would turn a single repository
    /// fault into an abort of every remaining File and Project — including the loose
    /// resources in this very File, which never needed the groups at all.
    /// </summary>
    [Fact]
    public void Reconcile_WhenTheGroupReadFaults_LosesOnlyTheGroupedResource()
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group("Notes");
        var stranded = LegacyDocument(f, f.File.Id, "Stranded.pdf", "stranded bytes");
        f.Assign(stranded.Id, group.Id);
        var loose = LegacyDocument(f, f.File.Id, "Loose.pdf", "loose bytes");

        var faulting = new LyingGroups(
            f.Groups, _ => throw new InvalidOperationException("group read rejected"));
        var moved = new ResourceLayoutReconciler(
            f.Projects, f.Files, f.Resources, f.Storage, f, faulting).ReconcileProject(f.Project.Id);

        Assert.Equal(1, moved);
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        Assert.Equal(Path.Combine(parent, "Loose.pdf"), f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal(stranded.StoredPath, f.Resources.GetById(stranded.Id)!.StoredPath);
        Assert.Equal("stranded bytes", Read(f, stranded.StoredPath));
    }

    /// <summary>
    /// A File whose resources are all loose AND all already placed never needs its groups,
    /// so it must not pay for the query. Adoption reads the claims for loose resources too,
    /// so this holds only while nothing here is misplaced. Lazy rather than eager, which is
    /// also what puts the read inside the per-resource try above.
    /// </summary>
    [Fact]
    public void Reconcile_WithNoGroupedResources_NeverReadsTheGroups()
    {
        using var f = new ResourceGroupFixture();
        f.Group("Notes"); // a group exists; nothing is filed into it
        var loose = LegacyDocument(f, f.File.Id, "Loose.pdf", "loose bytes");

        var reads = 0;
        var counting = new LyingGroups(f.Groups, fileId =>
        {
            reads++;
            return f.Groups.GetForFile(fileId);
        });
        var moved = new ResourceLayoutReconciler(
            f.Projects, f.Files, f.Resources, f.Storage, f, counting).ReconcileProject(f.Project.Id);

        Assert.Equal(1, moved);
        Assert.Equal(
            Path.Combine(ResourceLayout.FolderFor(f.Project, f.File), "Loose.pdf"),
            f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal(0, reads);
    }

    /// <summary>
    /// A group directory and an extensionless document want the same name, and whichever
    /// arrives first keeps it. This works in both directions only because the reservation
    /// physically creates the directory: an advisory check would leave nothing on disk for
    /// the later import's collision probe to see, and the import would take the group's
    /// path as a file. The directory assertions before the import are the point — they are
    /// what a check-only reservation fails.
    /// </summary>
    [Theory]
    [InlineData(true, "Notes", "Notes (2)")]
    [InlineData(false, "Notes (2)", "Notes")]
    public void AGroupDirectoryAndAnExtensionlessImport_EachYieldToWhicheverCameFirst(
        bool groupFirst, string expectedGroupSegment, string expectedLooseName)
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var contested = f.Storage.ResolvePath(Path.Combine(parent, "Notes"));

        ResourceGroup group;
        Resource loose;
        if (groupFirst)
        {
            group = f.Group("Notes");
            Assert.True(Directory.Exists(contested));
            loose = f.Document("Notes", "loose bytes");
        }
        else
        {
            loose = f.Document("Notes", "loose bytes");
            Assert.False(Directory.Exists(contested));
            Assert.True(System.IO.File.Exists(contested));
            group = f.Group("Notes");
        }

        Assert.Equal(expectedGroupSegment, group.FolderSegment);
        var member = f.Document("member.txt", "member bytes");
        f.Assign(member.Id, group.Id);

        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));

        var loosePath = Path.Combine(parent, expectedLooseName);
        Assert.Equal(loosePath, f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("loose bytes", Read(f, loosePath));
        var memberPath = Path.Combine(parent, expectedGroupSegment, "member.txt");
        Assert.Equal(memberPath, f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal("member bytes", Read(f, memberPath));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// The sharpest case for creating the directory at reservation time: a group with no
    /// members yet owns nothing on disk except the directory itself. If reservation only
    /// checked the name, a later extensionless import would take the group's exact path as
    /// a file, and the group's persisted segment would then name a file rather than a
    /// folder — every member it ever gained would be unplaceable.
    /// </summary>
    [Fact]
    public void AnEmptyGroupsDirectory_SurvivesALaterExtensionlessImport_AndTheReconcile()
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var group = f.Group("Notes");
        Assert.Equal("Notes", group.FolderSegment);
        Assert.True(Directory.Exists(f.Storage.ResolvePath(Path.Combine(parent, "Notes"))));

        var loose = f.Document("Notes", "loose bytes");
        Assert.Equal(Path.Combine(parent, "Notes (2)"), loose.StoredPath);
        var legacy = LegacyDocument(f, f.File.Id, "Legacy.pdf");

        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));

        Assert.Equal(Path.Combine(parent, "Notes (2)"), f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("loose bytes", Read(f, Path.Combine(parent, "Notes (2)")));
        Assert.True(Directory.Exists(f.Storage.ResolvePath(Path.Combine(parent, "Notes"))));
        Assert.Equal(Path.Combine(parent, "Legacy.pdf"), f.Resources.GetById(legacy.Id)!.StoredPath);

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// Two groups may share a title — nothing forbids it — but not a directory. Identity
    /// is the row's id and the segment it claimed, never the title, so their members go to
    /// different folders and stay there.
    /// </summary>
    [Fact]
    public void TwoGroupsSharingATitle_KeepDistinctDirectories_AndTheirMembersDoNotMix()
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var first = f.Group("Notes");
        var second = f.Group("Notes");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal("Notes", first.FolderSegment);
        Assert.Equal("Notes (2)", second.FolderSegment);

        var alpha = f.Document("alpha.txt", "alpha bytes");
        f.Assign(alpha.Id, first.Id);
        var beta = f.Document("beta.txt", "beta bytes");
        f.Assign(beta.Id, second.Id);

        Assert.Equal(2, f.Reconciler().ReconcileProject(f.Project.Id));

        var alphaPath = Path.Combine(parent, "Notes", "alpha.txt");
        var betaPath = Path.Combine(parent, "Notes (2)", "beta.txt");
        Assert.Equal(alphaPath, f.Resources.GetById(alpha.Id)!.StoredPath);
        Assert.Equal(betaPath, f.Resources.GetById(beta.Id)!.StoredPath);
        Assert.Equal("alpha bytes", Read(f, alphaPath));
        Assert.Equal("beta bytes", Read(f, betaPath));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// The move/record split, ported to a group destination: the bytes reach the group's
    /// folder and the row that names them fails to save. The promised repair is that the
    /// next run adopts the already-moved file rather than stranding it — and it must find
    /// it inside the group, not in the File root.
    /// </summary>
    [Fact]
    public void AFailingRecord_DoesNotAbortThePass_AndTheNextRunAdoptsTheMovedGroupMember()
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var group = f.Group("Notes");
        var victim = f.Document("victim.txt", "victim bytes");
        f.Assign(victim.Id, group.Id);
        var sibling = f.Document("sibling.txt", "sibling bytes");
        f.Assign(sibling.Id, group.Id);

        var sabotaged = new SabotagedResources(f.Resources, r => r.Id == victim.Id);
        Assert.Equal(1, f.Reconciler(resources: sabotaged).ReconcileProject(f.Project.Id));

        var stale = f.Resources.GetById(victim.Id)!;
        Assert.Equal(Path.Combine(parent, "victim.txt"), stale.StoredPath);
        Assert.False(f.Storage.Exists(stale.StoredPath!));
        Assert.Equal(
            Path.Combine(parent, "Notes", "sibling.txt"), f.Resources.GetById(sibling.Id)!.StoredPath);

        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));
        var repaired = f.Resources.GetById(victim.Id)!;
        Assert.Equal(Path.Combine(parent, "Notes", "victim.txt"), repaired.StoredPath);
        Assert.Equal("victim bytes", Read(f, repaired.StoredPath));

        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// Adoption inside a group directory is still bounded by the global claimed set.
    /// <c>ReconcileProject</c> walks one Project's rows, so the only thing between this
    /// group member's repair and another owner's document sitting at the very candidate it
    /// would adopt is that the claimed set is collected across every Project — not just
    /// the one being reconciled.
    /// </summary>
    [Fact]
    public void ReconcileProject_NeverAdoptsAnotherOwnersFile_SittingInThisGroupsDirectory()
    {
        using var f = new ResourceGroupFixture();
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var group = f.Group("Notes");
        var member = f.Document("shared.txt", "mine");
        f.Assign(member.Id, group.Id);

        // Its own bytes vanish: the exact shape that sends the reconciler looking for an
        // already-moved file to adopt at the desired location.
        f.Storage.Delete(member.StoredPath!);

        var otherProject = Project.Create("Debate", "#ffffff", f.Now);
        otherProject.RelocateTo(
            f.Storage.ReserveFolderSegment(string.Empty, "Debate", new HashSet<string>()), f.Now);
        f.Projects.Add(otherProject);
        var otherFile = ProjectFile.Create(otherProject.Id, "Cases", null, f.Now);
        otherFile.RelocateTo(
            f.Storage.ReserveFolderSegment(otherProject.FolderSegment, "Cases", new HashSet<string>()), f.Now);
        f.Files.Add(otherFile);

        var candidate = Path.Combine(parent, "Notes", "shared.txt");
        var theirs = Resource.CreateStored(
            otherFile.Id, ResourceKind.Document, "shared", "shared.txt", candidate, f.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Storage.ResolvePath(candidate))!);
        System.IO.File.WriteAllText(f.Storage.ResolvePath(candidate), "theirs");
        f.Resources.Add(theirs);

        var legacy = LegacyDocument(f, f.File.Id, "Legacy.pdf");

        Assert.Equal(1, f.Reconciler().ReconcileProject(f.Project.Id));

        Assert.Equal(Path.Combine(parent, "shared.txt"), f.Resources.GetById(member.Id)!.StoredPath);
        Assert.Equal(candidate, f.Resources.GetById(theirs.Id)!.StoredPath);
        Assert.Equal("theirs", Read(f, candidate));
        Assert.Equal(Path.Combine(parent, "Legacy.pdf"), f.Resources.GetById(legacy.Id)!.StoredPath);
    }
}
