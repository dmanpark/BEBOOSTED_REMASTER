using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// A resource whose stored file is missing from disk (a reconcile that was interrupted
/// mid-move, or bytes deleted externally) must surface a notice — never hand the dead
/// path to the shell, which throws.
/// </summary>
public sealed class FileDetailViewModelTests
{
    private static (FileDetailViewModel File, FakeFileReveal Reveal) FileWithImportedDocument()
    {
        var reveal = new FakeFileReveal();
        var shell = TestShell.Create(reveal: reveal);
        var projects = shell.Projects;
        projects.NewProjectName = "College Admissions";
        projects.TryCreateProject();
        projects.Detail!.NewFileTitle = "Metric Proof";
        projects.Detail.TryCreateFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        return (file, reveal);
    }

    [Fact]
    public void OpeningAResourceWhoseFileIsMissing_SetsANoticeInsteadOfLaunching()
    {
        var (file, reveal) = FileWithImportedDocument();
        var row = file.Resources.Single();
        Assert.False(System.IO.File.Exists(row.StoredAbsolutePath!)); // fake storage writes no bytes

        row.OpenExternallyCommand.Execute(null);

        Assert.Empty(reveal.Opened);
        Assert.Contains("missing", row.OpenNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RevealingAResourceWhoseFileIsMissing_SetsANoticeInsteadOfLaunching()
    {
        var (file, reveal) = FileWithImportedDocument();
        var row = file.Resources.Single();

        row.RevealInFolderCommand.Execute(null);

        Assert.Empty(reveal.Opened);
        Assert.Contains("missing", row.OpenNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpeningAResourceWhoseFileExists_Launches()
    {
        var (file, reveal) = FileWithImportedDocument();
        var row = file.Resources.Single();
        var real = Path.Combine(Path.GetTempPath(), $"beboosted-open-{Guid.NewGuid():N}.pdf");
        System.IO.File.WriteAllText(real, "bytes");
        try
        {
            row.StoredAbsolutePath = real;

            row.OpenExternallyCommand.Execute(null);

            Assert.Equal([real], reveal.Opened);
            Assert.Null(row.OpenNotice);
        }
        finally
        {
            System.IO.File.Delete(real);
        }
    }

    /// <summary>Store throws for one chosen file name; everything else succeeds.</summary>
    private sealed class SabotagedStorage(string failingSourceName) : BeBoosted.Application.Projects.IResourceStorage
    {
        private readonly FakeResourceStorage _inner = new();

        public string Store(string relativeFolder, string preferredFileName, string sourcePath)
            => Path.GetFileName(sourcePath) == failingSourceName
                ? throw new IOException("disk full")
                : _inner.Store(relativeFolder, preferredFileName, sourcePath);

        public string? MoveInto(string currentStoredPath, string relativeFolder, string preferredFileName)
            => _inner.MoveInto(currentStoredPath, relativeFolder, preferredFileName);

        public string ResolvePath(string storedPath) => _inner.ResolvePath(storedPath);

        public string ReserveFolderSegment(
            string relativeParent, string preferredSegment, IReadOnlySet<string> claimed, string? ownedSegment = null)
            => _inner.ReserveFolderSegment(relativeParent, preferredSegment, claimed, ownedSegment);

        public bool Exists(string storedPath) => _inner.Exists(storedPath);

        public void Delete(string storedPath) => _inner.Delete(storedPath);
    }

    /// <summary>
    /// One failing file must not abort the batch (or throw through the async click
    /// handler): its siblings import, and the failure is named in a notice.
    /// </summary>
    [Fact]
    public void ImportBatch_SurvivesOneFailingFile_AndNamesItInANotice()
    {
        var shell = TestShell.Create(resourceStorage: new SabotagedStorage("bad.pdf"));
        var projects = shell.Projects;
        projects.NewProjectName = "College Admissions";
        projects.TryCreateProject();
        projects.Detail!.NewFileTitle = "Metric Proof";
        projects.Detail.TryCreateFile();
        var file = projects.FileDetail!;

        file.Import(ResourceKind.Document, [@"C:\in\good.pdf", @"C:\in\bad.pdf", @"C:\in\also.pdf"]);

        Assert.Equal(2, file.Resources.Count);
        Assert.Contains("bad.pdf", file.ImportNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportBatch_WithNoFailures_LeavesNoNotice()
    {
        var (file, _) = FileWithImportedDocument();
        Assert.Null(file.ImportNotice);
    }

    [Fact]
    public void OpeningALink_StillLaunchesTheBrowser()
    {
        var reveal = new FakeFileReveal();
        var shell = TestShell.Create(reveal: reveal);
        var projects = shell.Projects;
        projects.NewProjectName = "College Admissions";
        projects.TryCreateProject();
        projects.Detail!.NewFileTitle = "Metric Proof";
        projects.Detail.TryCreateFile();
        var file = projects.FileDetail!;
        file.NewLinkUrl = "https://example.com";
        file.NewLinkTitle = "Example";
        file.TryAddLink();

        file.Resources.Single().OpenExternallyCommand.Execute(null);

        Assert.Single(reveal.Opened);
    }
}
