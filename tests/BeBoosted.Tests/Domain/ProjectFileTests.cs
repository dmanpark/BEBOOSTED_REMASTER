using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// Mirrors <see cref="ProjectTests"/>: a File's folder segment is claimed after
/// construction, once the reservation against its owning project's directory has
/// succeeded.
/// </summary>
public sealed class ProjectFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

    private static ProjectFile NewFile() => ProjectFile.Create(ProjectId.New(), "Transcripts", null, Now);

    [Fact]
    public void Create_StartsWithTheEmptyFolderSegmentSentinel()
    {
        var file = NewFile();

        Assert.Equal(string.Empty, file.FolderSegment);
    }

    [Fact]
    public void RelocateTo_RecordsTheSegment_AndTouchesTheFile()
    {
        var file = NewFile();

        file.RelocateTo("Transcripts", Now.AddMinutes(5));

        Assert.Equal("Transcripts", file.FolderSegment);
        Assert.Equal(Now.AddMinutes(5), file.ModifiedAt);
    }

    [Fact]
    public void RelocateTo_RejectsABlankSegment()
    {
        var file = NewFile();

        Assert.Throws<DomainException>(() => file.RelocateTo("   ", Now));

        Assert.Equal(string.Empty, file.FolderSegment);
    }

    [Fact]
    public void Rehydrate_CarriesTheStoredFolderSegment()
    {
        var file = ProjectFile.Rehydrate(
            ProjectFileId.New(), ProjectId.New(), "Transcripts", null, "Transcripts-2", Now, Now);

        Assert.Equal("Transcripts-2", file.FolderSegment);
    }
}
