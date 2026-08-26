using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// A stored resource records where its bytes now live after the layout reconciler
/// moves them. Links and notes have no bytes and can never be relocated.
/// </summary>
public sealed class ResourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-7));

    private static Resource StoredDocument()
        => Resource.CreateStored(
            ProjectFileId.New(), ResourceKind.Document, "Transcript",
            "Transcript.pdf", "old-guid.pdf", Now);

    [Fact]
    public void RelocateTo_RecordsTheNewPath_AndTouchesTheResource()
    {
        var resource = StoredDocument();

        resource.RelocateTo("College/Metric Proof/Transcript.pdf", Now.AddMinutes(5));

        Assert.Equal("College/Metric Proof/Transcript.pdf", resource.StoredPath);
        Assert.Equal(Now.AddMinutes(5), resource.ModifiedAt);
    }

    [Fact]
    public void RelocateTo_RejectsABlankPath()
    {
        var resource = StoredDocument();

        Assert.Throws<DomainException>(() => resource.RelocateTo("  ", Now));

        Assert.Equal("old-guid.pdf", resource.StoredPath);
    }

    [Fact]
    public void RelocateTo_RejectsAResourceThatHasNoStoredBytes()
    {
        var link = Resource.CreateLink(ProjectFileId.New(), "Source", "https://example.com", Now);

        Assert.Throws<DomainException>(() => link.RelocateTo("Anywhere/thing.pdf", Now));

        Assert.Null(link.StoredPath);
    }
}
