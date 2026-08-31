using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// The group row itself: every column round-trips, an update reaches the columns it
/// claims to and leaves the rest alone, the order groups come back in is deterministic,
/// and a row can never name a folder that was never reserved.
/// </summary>
public sealed class SqliteResourceGroupRepositoryTests : IDisposable
{
    private readonly ResourceGroupFixture _fixture = new();

    [Fact]
    public void Add_ThenGetById_RoundTripsEveryColumn()
    {
        var group = _fixture.Group("Unit 3 — Federalism");

        var stored = _fixture.Groups.GetById(group.Id);

        Assert.NotNull(stored);
        Assert.Equal(group.Id, stored.Id);
        Assert.Equal(_fixture.File.Id, stored.FileId);
        Assert.Equal("Unit 3 — Federalism", stored.Title);
        Assert.Equal(group.FolderSegment, stored.FolderSegment);
        Assert.Equal(0, stored.SortOrder);
        Assert.Equal(group.CreatedAt, stored.CreatedAt);
        Assert.Equal(group.ModifiedAt, stored.ModifiedAt);
    }

    /// <summary>Reads rehydrate the stored id; they never mint a new one.</summary>
    [Fact]
    public void GetById_ReturnsTheStoredIdentity_NotAFreshOne()
    {
        var group = _fixture.Group();

        Assert.Equal(group.Id, _fixture.Groups.GetById(group.Id)!.Id);
        Assert.Equal(group.Id, Assert.Single(_fixture.Groups.GetForFile(_fixture.File.Id)).Id);
    }

    [Fact]
    public void GetById_ForAnUnknownGroup_ReturnsNull()
        => Assert.Null(_fixture.Groups.GetById(ResourceGroupId.New()));

    [Fact]
    public void Update_PersistsTheRenameTheSegmentAndTheOrder_AndLeavesCreatedAtAlone()
    {
        var group = _fixture.Group("Unit 3");
        var created = group.CreatedAt;
        _fixture.Now = _fixture.Now.AddHours(3);
        group.Rename("Unit 3 — Federalism", _fixture.Now);
        group.Reorder(7, _fixture.Now);
        group.RelocateTo("Unit 3 (2)", _fixture.Now);

        _fixture.Groups.Update(group);

        var stored = _fixture.Groups.GetById(group.Id)!;
        Assert.Equal("Unit 3 — Federalism", stored.Title);
        Assert.Equal("Unit 3 (2)", stored.FolderSegment);
        Assert.Equal(7, stored.SortOrder);
        Assert.Equal(_fixture.Now, stored.ModifiedAt);
        Assert.Equal(created, stored.CreatedAt);
    }

    /// <summary>
    /// A no-op update is a lost write, not a success: the caller believes the rename
    /// landed. The row is gone because something else deleted it.
    /// </summary>
    [Fact]
    public void Update_WhenTheGroupIsAlreadyGone_Throws()
    {
        var group = _fixture.Group();
        _fixture.Groups.Delete(group.Id);

        var error = Assert.Throws<DomainException>(() => _fixture.Groups.Update(group));
        Assert.Equal("That group no longer exists.", error.Message);
    }

    /// <summary>Delete is idempotent — a second removal of the same group is not an error.</summary>
    [Fact]
    public void Delete_RunTwice_RemovesTheGroupWithoutThrowing()
    {
        var group = _fixture.Group();

        _fixture.Groups.Delete(group.Id);
        _fixture.Groups.Delete(group.Id);

        Assert.Null(_fixture.Groups.GetById(group.Id));
    }

    /// <summary>
    /// A group row must never name a directory that was never reserved. The empty
    /// segment is the in-memory "not yet reserved" state a freshly created group holds,
    /// and persisting it would leave a row pointing at a folder nothing claimed.
    /// </summary>
    [Fact]
    public void Add_WithAnUnreservedFolderSegment_IsRefused()
    {
        var group = ResourceGroup.Create(_fixture.File.Id, "Notes", 0, _fixture.Now);

        var error = Assert.Throws<DomainException>(() => _fixture.Groups.Add(group));
        Assert.Equal("A group needs a claimed folder segment.", error.Message);
        Assert.Null(_fixture.Groups.GetById(group.Id));
    }

    [Fact]
    public void Update_WithAnUnreservedFolderSegment_IsRefused()
    {
        _fixture.Group();
        var unreserved = ResourceGroup.Rehydrate(
            ResourceGroupId.New(), _fixture.File.Id, "Notes", 0,
            _fixture.Now, _fixture.Now, string.Empty);

        var error = Assert.Throws<DomainException>(() => _fixture.Groups.Update(unreserved));
        Assert.Equal("A group needs a claimed folder segment.", error.Message);
    }

    /// <summary>
    /// sort_order first, then created_at, then id. With the clock held still every group
    /// shares a timestamp, so only the id tiebreak keeps the order stable — without it
    /// the sequence is whatever SQLite happens to return.
    /// </summary>
    [Fact]
    public void GetForFile_OrdersBySortOrderThenCreatedAtThenId_WithAFixedClock()
    {
        var groups = new List<ResourceGroup>();
        for (var i = 0; i < 6; i++)
        {
            var group = ResourceGroup.Create(_fixture.File.Id, $"Unit {i}", i % 2, _fixture.Now);
            group.RelocateTo($"Unit {i}", _fixture.Now);
            _fixture.Groups.Add(group);
            groups.Add(group);
        }

        var expected = groups
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.Id.ToString(), StringComparer.Ordinal)
            .Select(g => g.Id)
            .ToList();

        Assert.Equal(
            expected,
            _fixture.Groups.GetForFile(_fixture.File.Id).Select(g => g.Id).ToList());
    }

    [Fact]
    public void GetForFile_ReturnsOnlyThatFilesGroups()
    {
        var mine = _fixture.Group("Unit 3");
        var other = ProjectFile.Create(_fixture.Project.Id, "History", null, _fixture.Now);
        other.RelocateTo(
            _fixture.Storage.ReserveFolderSegment(
                _fixture.Project.FolderSegment, "History", new HashSet<string>()),
            _fixture.Now);
        _fixture.Files.Add(other);
        var theirs = ResourceGroup.Create(other.Id, "Unit 3", 0, _fixture.Now);
        theirs.RelocateTo("Unit 3", _fixture.Now);
        _fixture.Groups.Add(theirs);

        Assert.Equal(mine.Id, Assert.Single(_fixture.Groups.GetForFile(_fixture.File.Id)).Id);
        Assert.Equal(theirs.Id, Assert.Single(_fixture.Groups.GetForFile(other.Id)).Id);
    }

    /// <summary>
    /// A group belongs to a File. Foreign keys are enforced, so a row for a File that is
    /// not there is refused rather than silently orphaned.
    /// </summary>
    [Fact]
    public void Add_ForAFileThatDoesNotExist_IsRejectedByTheForeignKey()
    {
        var orphan = ResourceGroup.Create(ProjectFileId.New(), "Nowhere", 0, _fixture.Now);
        orphan.RelocateTo("Nowhere", _fixture.Now);

        Assert.Throws<SqliteException>(() => _fixture.Groups.Add(orphan));
    }

    /// <summary>
    /// Deleting the File takes its groups with it — the CASCADE the migration declares.
    /// </summary>
    [Fact]
    public void DeletingTheFile_CascadesToItsGroups()
    {
        var group = _fixture.Group();

        _fixture.Files.Delete(_fixture.File.Id);

        Assert.Null(_fixture.Groups.GetById(group.Id));
        Assert.Empty(_fixture.Groups.GetForFile(_fixture.File.Id));
    }

    /// <summary>Two cascades deep: project → file → group.</summary>
    [Fact]
    public void DeletingTheProject_CascadesToItsFilesGroups()
    {
        var group = _fixture.Group();

        _fixture.Projects.Delete(_fixture.Project.Id);

        Assert.Null(_fixture.Groups.GetById(group.Id));
    }

    public void Dispose() => _fixture.Dispose();
}
