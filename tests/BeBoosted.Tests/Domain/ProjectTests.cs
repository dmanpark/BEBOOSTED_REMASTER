using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// A project's folder segment is claimed in a later step than construction: <see
/// cref="Project.Create"/> leaves it at the empty "not yet claimed" sentinel so the
/// id it generates can seed the sanitized name (<c>ResourceLayout.FolderFor</c>),
/// and only <see cref="Project.RelocateTo"/> — called once a reservation has
/// succeeded — records the claimed segment.
/// </summary>
public sealed class ProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

    private static Project NewProject() => Project.Create("DECA", "#5B8DEF", Now);

    [Fact]
    public void Create_StartsWithTheEmptyFolderSegmentSentinel()
    {
        var project = NewProject();

        Assert.Equal(string.Empty, project.FolderSegment);
    }

    [Fact]
    public void RelocateTo_RecordsTheSegment_AndTouchesTheProject()
    {
        var project = NewProject();

        project.RelocateTo("DECA", Now.AddMinutes(5));

        Assert.Equal("DECA", project.FolderSegment);
        Assert.Equal(Now.AddMinutes(5), project.ModifiedAt);
    }

    [Fact]
    public void RelocateTo_RejectsABlankSegment()
    {
        var project = NewProject();

        Assert.Throws<DomainException>(() => project.RelocateTo("   ", Now));

        Assert.Equal(string.Empty, project.FolderSegment);
    }

    [Fact]
    public void Rehydrate_CarriesTheStoredFolderSegment()
    {
        var project = Project.Rehydrate(
            ProjectId.New(), "DECA", "#5B8DEF", "DECA-2", Now, Now);

        Assert.Equal("DECA-2", project.FolderSegment);
    }
}
