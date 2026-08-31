using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Tests.Support;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// A resource's membership of a group, through every path that reads or writes a
/// resource row. The resource repository writes its column list in three separate
/// places — the shared Columns constant, the UPDATE's own parameter block, and
/// SearchInProject's explicit SELECT list — so each is pinned separately: missing one is
/// a silent read/write mismatch, never a compile error.
/// </summary>
public sealed class ResourceGroupMembershipTests : IDisposable
{
    /// <summary>
    /// Appears in the indexed text and nowhere in any title, so a search that finds it
    /// can only have gone through index_text. A title-matching token would pass even if
    /// the index were lost.
    /// </summary>
    private const string IndexOnlyToken = "quetzalcoatlus";

    private readonly ResourceGroupFixture _fixture = new();

    /// <summary>The INSERT path: Columns, the VALUES list, and Bind.</summary>
    [Fact]
    public void Add_ForAResourceAlreadyInAGroup_PersistsTheMembership()
    {
        var group = _fixture.Group();
        var resource = Resource.CreateNote(_fixture.File.Id, "Marbury notes", "body", _fixture.Now);
        resource.MoveToGroup(group.Id, _fixture.Now);

        _fixture.Resources.Add(resource);

        Assert.Equal(group.Id, _fixture.Resources.GetById(resource.Id)!.GroupId);
    }

    [Fact]
    public void Add_ForALooseResource_LeavesMembershipNull()
    {
        var resource = _fixture.Document();

        Assert.Null(_fixture.Resources.GetById(resource.Id)!.GroupId);
    }

    /// <summary>The UPDATE path, whose parameter block is written out separately.</summary>
    [Fact]
    public void Update_MovingAResourceIntoAGroup_PersistsTheMembership()
    {
        var group = _fixture.Group();
        var resource = _fixture.Document();

        _fixture.Assign(resource, group.Id);

        Assert.Equal(group.Id, _fixture.Resources.GetById(resource.Id)!.GroupId);
    }

    [Fact]
    public void Update_MovingAResourceBackToLoose_ClearsTheMembership()
    {
        var group = _fixture.Group();
        var resource = _fixture.Document();
        _fixture.Assign(resource, group.Id);

        _fixture.Assign(resource, null);

        Assert.Null(_fixture.Resources.GetById(resource.Id)!.GroupId);
    }

    [Fact]
    public void Update_MovingBetweenGroups_PersistsTheNewMembership()
    {
        var first = _fixture.Group("Unit 3");
        var second = _fixture.Group("Unit 4");
        var resource = _fixture.Document();
        _fixture.Assign(resource, first.Id);

        _fixture.Assign(resource, second.Id);

        Assert.Equal(second.Id, _fixture.Resources.GetById(resource.Id)!.GroupId);
    }

    /// <summary>
    /// Moving a resource between groups is not a content change, so its indexed text must
    /// survive. index_text is written only by SetIndexText and must stay out of the
    /// membership UPDATE.
    /// </summary>
    [Fact]
    public void Update_MovingAResourceIntoAGroup_LeavesItsIndexedTextIntact()
    {
        var group = _fixture.Group();
        var resource = _fixture.Document("source.txt", IndexOnlyToken);

        _fixture.Assign(resource, group.Id);

        var hit = Assert.Single(_fixture.Resources.SearchInProject(_fixture.Project.Id, IndexOnlyToken));
        Assert.Equal(resource.Id, hit.Id);
    }

    /// <summary>
    /// The read/write round trip every service edit makes: load the resource, change
    /// something unrelated, write it back. If the read did not carry group_id, the write
    /// binds null and the rename silently unfiles the resource — no error, no failed
    /// assertion anywhere near the rename.
    /// </summary>
    [Fact]
    public void ReadingAResourceAndWritingItBackForAnUnrelatedEdit_KeepsItsMembership()
    {
        var group = _fixture.Group();
        var resource = _fixture.Document();
        _fixture.Assign(resource, group.Id);

        var reloaded = _fixture.Resources.GetById(resource.Id)!;
        reloaded.Rename("Renamed", _fixture.Now.AddMinutes(5));
        _fixture.Resources.Update(reloaded);

        var after = _fixture.Resources.GetById(resource.Id)!;
        Assert.Equal("Renamed", after.Title);
        Assert.Equal(group.Id, after.GroupId);
    }

    [Fact]
    public void GetForFile_CarriesMembership()
    {
        var group = _fixture.Group();
        var grouped = _fixture.Document("grouped.txt");
        var loose = _fixture.Document("loose.txt");
        _fixture.Assign(grouped, group.Id);

        var byId = _fixture.Resources.GetForFile(_fixture.File.Id).ToDictionary(r => r.Id, r => r.GroupId);

        Assert.Equal(group.Id, byId[grouped.Id]);
        Assert.Null(byId[loose.Id]);
    }

    /// <summary>SearchInProject spells its SELECT list out by hand, so it is pinned by hand.</summary>
    [Fact]
    public void SearchInProject_CarriesMembership()
    {
        var group = _fixture.Group();
        var resource = _fixture.Document("source.txt", IndexOnlyToken);
        _fixture.Assign(resource, group.Id);

        var hit = Assert.Single(_fixture.Resources.SearchInProject(_fixture.Project.Id, IndexOnlyToken));

        Assert.Equal(resource.Id, hit.Id);
        Assert.Equal(group.Id, hit.GroupId);
    }

    /// <summary>
    /// Membership names a real group. Foreign keys are enforced, so a resource cannot be
    /// filed into a group that is not there.
    /// </summary>
    [Fact]
    public void Add_ForAGroupThatDoesNotExist_IsRejectedByTheForeignKey()
    {
        var resource = Resource.CreateNote(_fixture.File.Id, "Orphan", "body", _fixture.Now);
        resource.MoveToGroup(ResourceGroupId.New(), _fixture.Now);

        Assert.Throws<SqliteException>(() => _fixture.Resources.Add(resource));
    }

    [Fact]
    public void Update_IntoAGroupThatDoesNotExist_IsRejectedByTheForeignKey()
    {
        var resource = _fixture.Document();

        Assert.Throws<SqliteException>(() => _fixture.Assign(resource, ResourceGroupId.New()));
    }

    /// <summary>
    /// SET NULL, deliberately not CASCADE. Removing the grouping is what Ungroup does, and
    /// it must never destroy the documents in it — the bytes and the row both stay.
    /// </summary>
    [Fact]
    public void DirectGroupDelete_UngroupsWithoutDeletingTheResource()
    {
        using var f = new ResourceGroupFixture();
        var g = f.Group();
        var r = f.Document();
        f.Assign(r, g.Id);
        f.Groups.Delete(g.Id);
        var remaining = Assert.IsType<Resource>(f.Resources.GetById(r.Id));
        Assert.Null(remaining.GroupId);
        Assert.True(f.Storage.Exists(remaining.StoredPath!));
        Assert.Null(f.Groups.GetById(g.Id));
    }

    /// <summary>Deleting the File removes both, through two independent CASCADEs.</summary>
    [Fact]
    public void DeletingTheFile_CascadesToItsGroupAndItsResources()
    {
        var group = _fixture.Group();
        var resource = _fixture.Document();
        _fixture.Assign(resource, group.Id);

        _fixture.Files.Delete(_fixture.File.Id);

        Assert.Null(_fixture.Groups.GetById(group.Id));
        Assert.Null(_fixture.Resources.GetById(resource.Id));
    }

    [Fact]
    public void DeletingTheProject_CascadesToItsFilesGroupsAndResources()
    {
        var group = _fixture.Group();
        var resource = _fixture.Document();
        _fixture.Assign(resource, group.Id);

        _fixture.Projects.Delete(_fixture.Project.Id);

        Assert.Null(_fixture.Groups.GetById(group.Id));
        Assert.Null(_fixture.Resources.GetById(resource.Id));
    }

    /// <summary>
    /// The fifth callback repository is bound to the mutation's own connection and
    /// transaction, not to a fresh one of its own. Handed a factory-opened repository
    /// instead, the group delete would commit on its own connection while the resource
    /// delete rolled back — and the reads inside the callback would still look right, so
    /// only the assertions after the throw catch it.
    /// </summary>
    [Fact]
    public void GroupAndResourceWrites_ActuallyRollBackTogether()
    {
        using var f = new ResourceGroupFixture();
        var g = f.Group();
        var r = f.Document();
        f.Assign(r, g.Id);
        Assert.Throws<InvalidOperationException>(() =>
            new SqliteProjectMutations(f.Database.Factory).Execute((_, _, resources, _, groups) =>
            {
                resources.Delete(r.Id);
                groups.Delete(g.Id);
                Assert.Null(resources.GetById(r.Id));
                Assert.Null(groups.GetById(g.Id));
                throw new InvalidOperationException("before commit");
            }));
        Assert.NotNull(f.Groups.GetById(g.Id));
        Assert.Equal(g.Id, f.Resources.GetById(r.Id)!.GroupId);
        Assert.True(f.Storage.Exists(r.StoredPath!));
    }

    public void Dispose() => _fixture.Dispose();
}
