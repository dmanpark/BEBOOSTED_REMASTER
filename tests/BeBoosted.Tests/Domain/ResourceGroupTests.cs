using System.Globalization;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// Mirrors <see cref="ProjectFileTests"/>: a group's folder segment is claimed after
/// construction, once a reservation against its owning File's directory has succeeded.
/// <see cref="ResourceGroup.Create"/> takes no folderSegment — the service reserves one
/// and calls <see cref="ResourceGroup.RelocateTo"/> before adding it, the same
/// correction PR #1 already applied to Project/ProjectFile.
/// </summary>
public sealed class ResourceGroupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

    private static ResourceGroup NewGroup() => ResourceGroup.Create(ProjectFileId.New(), "Notes", 0, Now);

    [Fact]
    public void Create_StartsWithTheEmptyFolderSegmentSentinel()
    {
        var group = NewGroup();

        Assert.Equal(string.Empty, group.FolderSegment);
    }

    [Fact]
    public void Create_TrimsTheTitle()
    {
        var group = ResourceGroup.Create(ProjectFileId.New(), "  Notes  ", 0, Now);

        Assert.Equal("Notes", group.Title);
    }

    [Fact]
    public void Create_SetsCreatedAtAndModifiedAtToNow()
    {
        var group = NewGroup();

        Assert.Equal(Now, group.CreatedAt);
        Assert.Equal(Now, group.ModifiedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_RejectsABlankTitle(string? title)
    {
        Assert.Throws<DomainException>(() => ResourceGroup.Create(ProjectFileId.New(), title!, 0, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rename_RejectsABlankTitle(string? title)
    {
        var group = NewGroup();

        Assert.Throws<DomainException>(() => group.Rename(title!, Now.AddMinutes(1)));

        Assert.Equal("Notes", group.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RelocateTo_RejectsABlankSegment(string? segment)
    {
        var group = NewGroup();

        Assert.Throws<DomainException>(() => group.RelocateTo(segment!, Now.AddMinutes(1)));

        Assert.Equal(string.Empty, group.FolderSegment);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Create_RejectsANegativeOrder(int order)
    {
        Assert.Throws<DomainException>(() => ResourceGroup.Create(ProjectFileId.New(), "Notes", order, Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Reorder_RejectsANegativeOrder(int order)
    {
        var group = NewGroup();

        Assert.Throws<DomainException>(() => group.Reorder(order, Now.AddMinutes(1)));

        Assert.Equal(0, group.SortOrder);
    }

    [Fact]
    public void Reorder_RecordsTheOrder_AndTouchesTheGroup()
    {
        var group = NewGroup();

        group.Reorder(3, Now.AddMinutes(1));

        Assert.Equal(3, group.SortOrder);
        Assert.Equal(Now.AddMinutes(1), group.ModifiedAt);
    }

    [Fact]
    public void RelocateTo_RecordsTheSegment_AndTouchesTheGroup()
    {
        var group = NewGroup();

        group.RelocateTo("Notes", Now.AddMinutes(1));

        Assert.Equal("Notes", group.FolderSegment);
        Assert.Equal(Now.AddMinutes(1), group.ModifiedAt);
    }

    [Fact]
    public void Rename_DoesNotChangeTheClaimedFolder()
    {
        var now = DateTimeOffset.Parse("2026-08-30T09:00:00-07:00", CultureInfo.InvariantCulture);
        var group = ResourceGroup.Create(ProjectFileId.New(), "  Notes  ", 0, now);
        Assert.Equal("Notes", group.Title);
        Assert.Equal(string.Empty, group.FolderSegment);
        group.RelocateTo("Notes (2)", now);
        group.Rename("References", now.AddMinutes(1));
        Assert.Equal("References", group.Title);
        Assert.Equal("Notes (2)", group.FolderSegment);
        Assert.Equal(now, group.CreatedAt);
        Assert.Equal(now.AddMinutes(1), group.ModifiedAt);
    }

    [Fact]
    public void Rehydrate_CarriesAllPersistedFields()
    {
        var id = ResourceGroupId.New();
        var fileId = ProjectFileId.New();
        var createdAt = Now;
        var modifiedAt = Now.AddDays(1);

        var group = ResourceGroup.Rehydrate(id, fileId, "References", 2, createdAt, modifiedAt, "References-2");

        Assert.Equal(id, group.Id);
        Assert.Equal(fileId, group.FileId);
        Assert.Equal("References", group.Title);
        Assert.Equal("References-2", group.FolderSegment);
        Assert.Equal(2, group.SortOrder);
        Assert.Equal(createdAt, group.CreatedAt);
        Assert.Equal(modifiedAt, group.ModifiedAt);
    }

    [Theory]
    [InlineData(ResourceKind.Document)]
    [InlineData(ResourceKind.Image)]
    [InlineData(ResourceKind.Link)]
    [InlineData(ResourceKind.Note)]
    public void MembershipChanges_KeepContentPathAndIndexState(ResourceKind kind)
    {
        var now = DateTimeOffset.Parse("2026-08-30T09:00:00-07:00", CultureInfo.InvariantCulture);
        var fileId = ProjectFileId.New();
        var resource = kind switch
        {
            ResourceKind.Link => Resource.CreateLink(fileId, "Source", "https://example.com", now),
            ResourceKind.Note => Resource.CreateNote(fileId, "Source", "body", now),
            _ => Resource.CreateStored(fileId, kind, "Source", "source.txt", "old/source.txt", now),
        };
        resource.MarkIndexed(now);
        var before = (resource.Id, resource.FileId, resource.Title, resource.StoredPath,
            resource.Content, resource.Url, resource.IndexState, resource.AddedAt);
        Assert.Null(resource.GroupId);
        var groupId = ResourceGroupId.New();
        resource.MoveToGroup(groupId, now.AddMinutes(1));
        Assert.Equal(groupId, resource.GroupId);
        resource.MoveToGroup(null, now.AddMinutes(2));
        Assert.Null(resource.GroupId);
        Assert.Equal(before, (resource.Id, resource.FileId, resource.Title, resource.StoredPath,
            resource.Content, resource.Url, resource.IndexState, resource.AddedAt));
        Assert.Equal(now.AddMinutes(2), resource.ModifiedAt);
    }

    [Fact]
    public void Rehydrate_AcceptsANonNullGroupId()
    {
        var groupId = ResourceGroupId.New();

        var resource = Resource.Rehydrate(
            ResourceId.New(), ProjectFileId.New(), ResourceKind.Note, "Source",
            null, "body", null, null, Now, ResourceIndexState.Pending, Now, groupId);

        Assert.Equal(groupId, resource.GroupId);
    }

    /// <summary>
    /// Loose membership is stated, never inferred from an omitted argument: groupId is a
    /// required parameter, so every mapper has to say which it means.
    /// </summary>
    [Fact]
    public void Rehydrate_WithAnExplicitNullGroupId_LeavesTheResourceLoose()
    {
        var resource = Resource.Rehydrate(
            ResourceId.New(), ProjectFileId.New(), ResourceKind.Note, "Source",
            null, "body", null, null, Now, ResourceIndexState.Pending, Now, null);

        Assert.Null(resource.GroupId);
    }
}
