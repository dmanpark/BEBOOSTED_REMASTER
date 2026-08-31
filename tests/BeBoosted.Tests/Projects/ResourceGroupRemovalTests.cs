using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Tests.Support;

namespace BeBoosted.Tests.Projects;

/// <summary>
/// The two ways a group stops existing, over a real database and a real resources
/// directory. They are deliberately different acts: Ungroup keeps every document and only
/// forgets the grouping, while Delete group destroys the documents with it. Both remove
/// the group row, so only what happens to the members tells them apart — which is why
/// every test here asserts on the members, not merely on the group.
/// </summary>
public sealed class ResourceGroupRemovalTests
{
    private static string Read(ResourceGroupFixture f, string? storedPath)
        => System.IO.File.ReadAllText(f.Storage.ResolvePath(storedPath!));

    /// <summary>
    /// Delegates everything, and refuses to delete one named path — a read-only file, an
    /// ACL, an AV or sync client holding a handle.
    ///
    /// Deliberately a standalone <see cref="IResourceStorage"/> rather than a subclass of
    /// the local one: the interface is the seam the service depends on, so any
    /// implementation may throw and the isolation cannot be pushed down into the hardened
    /// implementation that ships today. It refuses a *specific stored path* rather than the
    /// first call it happens to receive, so the test names the resource it is about and
    /// does not quietly change meaning if the service ever visits the members in another
    /// order.
    /// </summary>
    private sealed class RefusingGroupDeleteStorage(IResourceStorage inner, string refused)
        : IResourceStorage
    {
        public string Store(string relativeFolder, string preferredFileName, string sourcePath)
            => inner.Store(relativeFolder, preferredFileName, sourcePath);

        public string? MoveInto(string currentStoredPath, string relativeFolder, string preferredFileName)
            => inner.MoveInto(currentStoredPath, relativeFolder, preferredFileName);

        public string ReserveFolderSegment(
            string relativeParent, string preferredSegment, IReadOnlySet<string> claimed,
            string? ownedSegment = null)
            => inner.ReserveFolderSegment(relativeParent, preferredSegment, claimed, ownedSegment);

        public string ResolvePath(string storedPath) => inner.ResolvePath(storedPath);

        public bool Exists(string storedPath) => inner.Exists(storedPath);

        public void Delete(string storedPath)
        {
            if (storedPath == refused)
            {
                throw new InvalidOperationException("refused byte cleanup");
            }

            inner.Delete(storedPath);
        }
    }

    /// <summary>
    /// Resource ids as a comparable set. Ordering both sides makes an equality assertion
    /// about *which* ids, independent of the order the service happened to visit them —
    /// while still catching a duplicate, which a <c>HashSet</c> comparison would not.
    /// </summary>
    private static IEnumerable<string> Ids(IEnumerable<ResourceId> ids)
        => ids.Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal);

    /// <summary>
    /// The two removals under one switch, so a theory can hold both to the same contract
    /// where the contract really is the same one — atomicity, and tolerance of a group that
    /// has already gone.
    /// </summary>
    private static void Remove(ProjectService service, ResourceGroupId id, bool delete)
    {
        if (delete)
        {
            service.DeleteGroup(id);
        }
        else
        {
            service.UngroupGroup(id);
        }
    }

    /// <summary>
    /// Removing a group that is already gone does nothing and says nothing. This is the
    /// second click of a double-click, or a stale flyout in another window — the user asked
    /// for a state that already holds, so there is nothing to report. Throwing instead, the
    /// way <see cref="ProjectService.RenameGroup"/> deliberately does, would surface an
    /// error dialog for an action that succeeded moments ago.
    ///
    /// The surviving member is asserted on both paths, because "no-op" has to mean no
    /// second round of side effects either: no further invalidation, and — after an
    /// Ungroup — no disturbance to a resource that is now loose and no longer this group's
    /// business at all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingAGroupThatIsAlreadyGone_IsASilentNoOp(bool delete)
    {
        using var f = new ResourceGroupFixture();
        var invalidator = new RecordingGroupInvalidator();
        var service = f.CreateService(invalidator: invalidator);
        var group = service.CreateGroup(f.File.Id, "Unit 3");
        var member = f.Document("member.txt", "member bytes");
        service.MoveResourceToGroup(member.Id, group.Id);

        Remove(service, group.Id, delete);
        var afterFirst = f.Resources.GetById(member.Id);
        var callsAfterFirst = invalidator.Calls.ToList();

        Remove(service, group.Id, delete);

        Assert.Null(f.Groups.GetById(group.Id));
        Assert.Equal(callsAfterFirst, invalidator.Calls);
        if (delete)
        {
            Assert.Null(afterFirst);
            Assert.Null(f.Resources.GetById(member.Id));
        }
        else
        {
            var loose = Assert.IsType<Resource>(f.Resources.GetById(member.Id));
            Assert.Null(loose.GroupId);
            Assert.Equal(Assert.IsType<Resource>(afterFirst).StoredPath, loose.StoredPath);
            Assert.Equal("member bytes", Read(f, loose.StoredPath));
        }
    }

    /// <summary>
    /// Ungroup is the non-destructive half: it forgets the grouping and nothing else. Every
    /// member survives with its content — including the link's URL and the note's text,
    /// neither of which has bytes to be moved — and the members of the *other* group are
    /// untouched, which is what rules out an implementation that loosens the whole File.
    ///
    /// The collision is the load-bearing part. A member returning to the File folder can
    /// find its name already taken by a loose document, and the reconciler must number it
    /// rather than overwrite the occupant: "one bytes" and "loose bytes" both have to
    /// survive, at two different paths. The zero-move reconcile afterwards proves the
    /// bytes actually followed the membership instead of merely being scheduled to.
    /// </summary>
    [Fact]
    public void Ungroup_KeepsEveryMemberAndItsContent()
    {
        using var f = new ResourceGroupFixture();
        var invalidator = new RecordingGroupInvalidator();
        var service = f.CreateService(invalidator: invalidator);
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var unit = service.CreateGroup(f.File.Id, "Unit 3");
        var other = service.CreateGroup(f.File.Id, "Sources");

        var one = f.Document("one.txt", "one bytes");
        var two = f.Document("two.txt", "two bytes");
        var link = service.AddLink(f.File.Id, "Oyez", "https://oyez.org/marbury");
        var note = service.AddNote(f.File.Id, "Reading note", "chapter three");
        foreach (var member in new[] { one.Id, two.Id, link.Id, note.Id })
        {
            service.MoveResourceToGroup(member, unit.Id);
        }

        // Only free to take the plain name because the member above has vacated it — which
        // is exactly the collision the ungroup below has to walk back into.
        var loose = f.Document("one.txt", "loose bytes");
        var neighbour = f.Document("neighbour.txt", "neighbour bytes");
        service.MoveResourceToGroup(neighbour.Id, other.Id);
        Assert.Equal(Path.Combine(parent, "one.txt"), f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal(Path.Combine(parent, "Unit 3", "one.txt"), f.Resources.GetById(one.Id)!.StoredPath);

        service.UngroupGroup(unit.Id);

        Assert.Null(f.Groups.GetById(unit.Id));
        Assert.NotNull(f.Groups.GetById(other.Id));
        foreach (var member in new[] { one.Id, two.Id, link.Id, note.Id })
        {
            Assert.Null(Assert.IsType<Resource>(f.Resources.GetById(member)).GroupId);
        }

        var onePath = Path.Combine(parent, "one (2).txt");
        Assert.Equal(onePath, f.Resources.GetById(one.Id)!.StoredPath);
        Assert.Equal(Path.Combine(parent, "two.txt"), f.Resources.GetById(two.Id)!.StoredPath);
        Assert.Equal("one bytes", Read(f, onePath));
        Assert.Equal("two bytes", Read(f, f.Resources.GetById(two.Id)!.StoredPath));
        Assert.Equal("https://oyez.org/marbury", f.Resources.GetById(link.Id)!.Url);
        Assert.Equal("chapter three", f.Resources.GetById(note.Id)!.Content);

        var untouched = f.Resources.GetById(loose.Id)!;
        Assert.Null(untouched.GroupId);
        Assert.Equal(Path.Combine(parent, "one.txt"), untouched.StoredPath);
        Assert.Equal("loose bytes", Read(f, untouched.StoredPath));
        var kept = f.Resources.GetById(neighbour.Id)!;
        Assert.Equal(other.Id, kept.GroupId);
        Assert.Equal(Path.Combine(parent, "Sources", "neighbour.txt"), kept.StoredPath);
        Assert.Equal("neighbour bytes", Read(f, kept.StoredPath));

        Assert.Empty(invalidator.Calls);
        Assert.Equal(0, f.Reconciler().ReconcileProject(f.Project.Id));
    }

    /// <summary>
    /// Delete group is the destructive half, and it must destroy exactly its own members —
    /// no more and no less. "No less" is the sharper half: <c>resources.group_id</c> is
    /// <c>ON DELETE SET NULL</c>, so removing only the group row would quietly *preserve*
    /// every document the user asked to destroy, loose in the File. The surviving-id
    /// assertion is what catches that, and it catches over-deletion of the loose document
    /// and the other group's member in the same breath.
    ///
    /// Every member is invalidated, links and notes included: a derived answer citing a
    /// deleted note is just as stale as one citing deleted bytes, and only the row-bearing
    /// members would be reached by a loop that walked stored paths instead of resources.
    /// </summary>
    [Fact]
    public void DeleteGroup_RemovesItsMembersRowsBytesAndNothingElse()
    {
        using var f = new ResourceGroupFixture();
        var invalidator = new RecordingGroupInvalidator();
        var service = f.CreateService(invalidator: invalidator);
        var parent = ResourceLayout.FolderFor(f.Project, f.File);
        var unit = service.CreateGroup(f.File.Id, "Unit 3");
        var other = service.CreateGroup(f.File.Id, "Sources");

        var one = f.Document("one.txt", "one bytes");
        var two = f.Document("two.txt", "two bytes");
        var link = service.AddLink(f.File.Id, "Oyez", "https://oyez.org/marbury");
        var note = service.AddNote(f.File.Id, "Reading note", "chapter three");
        foreach (var member in new[] { one.Id, two.Id, link.Id, note.Id })
        {
            service.MoveResourceToGroup(member, unit.Id);
        }

        var loose = f.Document("loose.txt", "loose bytes");
        var neighbour = f.Document("neighbour.txt", "neighbour bytes");
        service.MoveResourceToGroup(neighbour.Id, other.Id);
        var onePath = f.Resources.GetById(one.Id)!.StoredPath!;
        var twoPath = f.Resources.GetById(two.Id)!.StoredPath!;
        var loosePath = f.Resources.GetById(loose.Id)!.StoredPath!;
        var neighbourPath = f.Resources.GetById(neighbour.Id)!.StoredPath!;

        service.DeleteGroup(unit.Id);

        Assert.Null(f.Groups.GetById(unit.Id));
        Assert.NotNull(f.Groups.GetById(other.Id));
        Assert.Equal(
            Ids([loose.Id, neighbour.Id]),
            Ids(f.Resources.GetForFile(f.File.Id).Select(r => r.Id)));
        foreach (var member in new[] { one.Id, two.Id, link.Id, note.Id })
        {
            Assert.Null(f.Resources.GetById(member));
        }

        Assert.False(f.Storage.Exists(onePath));
        Assert.False(f.Storage.Exists(twoPath));
        Assert.Equal(Ids([one.Id, two.Id, link.Id, note.Id]), Ids(invalidator.Calls));

        Assert.Equal(loosePath, f.Resources.GetById(loose.Id)!.StoredPath);
        Assert.Equal("loose bytes", Read(f, loosePath));
        var kept = f.Resources.GetById(neighbour.Id)!;
        Assert.Equal(other.Id, kept.GroupId);
        Assert.Equal(neighbourPath, kept.StoredPath);
        Assert.Equal("neighbour bytes", Read(f, neighbourPath));
    }

    /// <summary>
    /// The commit has already happened: both rows and the group are gone and nothing after
    /// it can put them back. So each remaining side effect has to be attempted on its own.
    /// A single wrapper around the byte-deletion loop would abandon the second member's
    /// file the moment the first refuses; a single wrapper around the invalidation loop
    /// would leave the second member's derived items citing a source that no longer exists,
    /// never flagged for review. Both failures are pinned here at once, by arming a refusal
    /// on the first member in *both* collaborators.
    ///
    /// The refusal names the first member's own stored path and the first member's own id,
    /// not an enumeration index, so it stays aimed at the same resource however the service
    /// orders its work. The two documents are added a minute apart because
    /// <c>GetForFile</c> orders by <c>added_at</c> — identical timestamps would make "the
    /// first member" a coin toss and this test intermittent.
    ///
    /// Nothing escapes: the delete fully succeeded, and reporting it as failed because the
    /// cleanup behind it stumbled is the defect, not the report.
    /// </summary>
    [Fact]
    public void DeleteGroup_WhenAByteDeleteAndAnInvalidationRefuse_StillDoesTheRest()
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group();
        var one = f.Document("one.txt", "one bytes");
        f.Now = f.Now.AddMinutes(1);
        var two = f.Document("two.txt", "two bytes");
        f.Assign(one.Id, group.Id);
        f.Assign(two.Id, group.Id);
        f.Reconciler().ReconcileProject(f.Project.Id);

        var members = f.Resources.GetForFile(f.File.Id).Where(r => r.GroupId == group.Id).ToList();
        Assert.Equal(2, members.Count);
        var first = members[0];
        var second = members[1];
        Assert.Equal(one.Id, first.Id);

        var invalidator = new RecordingGroupInvalidator { ThrowFor = first.Id };
        var service = f.CreateService(
            storage: new RefusingGroupDeleteStorage(f.Storage, first.StoredPath!),
            invalidator: invalidator);

        service.DeleteGroup(group.Id);

        Assert.Null(f.Groups.GetById(group.Id));
        Assert.Null(f.Resources.GetById(first.Id));
        Assert.Null(f.Resources.GetById(second.Id));
        Assert.True(f.Storage.Exists(first.StoredPath!));    // the one that refused
        Assert.False(f.Storage.Exists(second.StoredPath!));  // not abandoned behind it
        Assert.Equal(Ids([first.Id, second.Id]), Ids(invalidator.Calls));
    }

    /// <summary>
    /// Ungroup stays available under a parent that has claimed no folder — it is a recovery
    /// action, and refusing it would strand the user's data behind a group they cannot
    /// remove. What it must not do there is reconcile.
    ///
    /// The unclaimed-Project half is the one with teeth. The reconciler deliberately lets a
    /// both-empty Project/File through as the pre-0012 legacy shape, so sweeping a Project
    /// whose segment is empty resolves <c>FolderFor</c> to the File's segment alone and
    /// drags every newly loosened document out of the Project folder and into the resources
    /// root — the same hazard <see cref="ProjectService.RenameFile"/> defers around. The
    /// unclaimed-File half is asserted alongside it because the guard covers both, but the
    /// reconciler's own half-backfilled guard already skips that File, so it holds either
    /// way.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UngroupUnderAnUnclaimedParent_LoosensTheRowsWithoutSweepingTheBytes(
        bool unclaimTheProject)
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group();
        var one = f.Document("one.txt", "one bytes");
        var two = f.Document("two.txt", "two bytes");
        f.Assign(one.Id, group.Id);
        f.Assign(two.Id, group.Id);
        f.Reconciler().ReconcileProject(f.Project.Id);
        var settled = new[] { one.Id, two.Id }
            .ToDictionary(id => id, id => f.Resources.GetById(id)!.StoredPath!);
        Assert.All(settled.Values, path =>
            Assert.Equal(
                Path.Combine(ResourceLayout.FolderFor(f.Project, f.File, group)),
                Path.GetDirectoryName(path)));

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

        f.CreateService().UngroupGroup(group.Id);

        Assert.Null(f.Groups.GetById(group.Id));
        foreach (var (id, path) in settled)
        {
            var current = Assert.IsType<Resource>(f.Resources.GetById(id));
            Assert.Null(current.GroupId);
            Assert.Equal(path, current.StoredPath);
            Assert.True(f.Storage.Exists(path));
        }

        Assert.Equal("one bytes", Read(f, settled[one.Id]));
        Assert.Equal("two bytes", Read(f, settled[two.Id]));
    }

    /// <summary>
    /// Both removals write more than one row, so both must be one transaction. A failure
    /// part-way through has to leave the group exactly as it was — for Ungroup that means
    /// no member half-loosened, and for Delete it means no member destroyed while its
    /// siblings and the group row survive.
    ///
    /// The mutation double runs the real callback against real transaction-bound
    /// repositories before it throws, so this fails against an implementation that gives
    /// each member its own transaction — which is exactly what deleting members through
    /// the public <c>DeleteResource</c> would do.
    ///
    /// Bytes and provenance are asserted too: neither may move ahead of a commit that
    /// never happened. A rolled-back delete that had already removed the bytes would leave
    /// live rows pointing at nothing, and one that had already invalidated would mark live
    /// derived items "Needs review" for a deletion that never occurred.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupRemovalRollback_PreservesRowsBytesAndProvenance(bool delete)
    {
        using var f = new ResourceGroupFixture();
        var group = f.Group();
        var one = f.Document("one.txt", "one");
        var two = f.Document("two.txt", "two");
        f.Assign(one.Id, group.Id);
        f.Assign(two.Id, group.Id);
        f.Reconciler().ReconcileProject(f.Project.Id);
        var before = new[] { one.Id, two.Id }
            .Select(id => Assert.IsType<Resource>(f.Resources.GetById(id)))
            .ToList();
        var invalidator = new RecordingGroupInvalidator();
        var service = f.CreateService(new FailGroupMutation(f.Database.Factory), invalidator: invalidator);

        Assert.Throws<InvalidOperationException>(() => Remove(service, group.Id, delete));

        Assert.NotNull(f.Groups.GetById(group.Id));
        foreach (var old in before)
        {
            var current = Assert.IsType<Resource>(f.Resources.GetById(old.Id));
            Assert.Equal(group.Id, current.GroupId);
            Assert.Equal(old.StoredPath, current.StoredPath);
            Assert.Equal(
                old.Title == "one.txt" ? "one" : "two",
                System.IO.File.ReadAllText(f.Storage.ResolvePath(current.StoredPath!)));
        }

        Assert.Empty(invalidator.Calls);
    }
}
