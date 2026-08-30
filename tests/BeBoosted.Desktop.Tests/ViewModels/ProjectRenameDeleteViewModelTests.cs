using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// Renaming and deleting at all three levels — Project, File, resource. Deletion is
/// irreversible here (stored bytes go with it), so every delete goes through the same
/// two-step confirmation the session editor uses, and the prompt names the exact scope.
/// </summary>
public sealed class ProjectRenameDeleteViewModelTests
{
    private static ProjectsViewModel WithProjectAndFile()
    {
        var projects = TestShell.Create().Projects;
        projects.NewProjectName = "College Admissions";
        projects.TryCreateProject();
        projects.Detail!.NewFileTitle = "Metric Proof";
        projects.Detail.TryCreateFile();
        return projects;
    }

    /// <summary>
    /// The same fixture, but keeping the repositories so a test can assert on the rows
    /// left behind. The view-model surface alone cannot see an orphan: it only ever
    /// lists resources for a File it still has.
    /// </summary>
    private static (ProjectsViewModel Projects, InMemoryProjectFileRepository Files,
        InMemoryResourceRepository Resources) WithRepositories()
    {
        var projectRepo = new InMemoryProjectRepository();
        var projects = TestShell.Create(projects: projectRepo).Projects;
        projects.NewProjectName = "College Admissions";
        projects.TryCreateProject();
        projects.Detail!.NewFileTitle = "Metric Proof";
        projects.Detail.TryCreateFile();

        var files = projectRepo.Files;
        Assert.NotNull(files);
        var resources = files.Resources;
        Assert.NotNull(resources);
        return (projects, files, resources);
    }

    /// <summary>Adds one link to the open File and returns its id.</summary>
    private static ResourceId AddLinkTo(FileDetailViewModel file)
    {
        file.NewLinkTitle = "SAT Score Report";
        file.NewLinkUrl = "https://collegeboard.org/scores";
        Assert.True(file.TryAddLink());
        return file.Resources.Single().Resource.Id;
    }

    // ---- File rename ----

    [Fact]
    public void RenamingAFile_ShowsTheNewTitleOnTheOpenFileAndTheCardBehindIt()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;

        file.BeginRename();
        file.RenameTitle = "Evidence";

        Assert.True(file.TryCommitRename());
        Assert.Equal("Evidence", file.Title);
        Assert.Equal("Evidence", projects.Detail!.Files.Single().Title);
    }

    [Fact]
    public void RenamingAFile_ToBlank_IsRefusedAndKeepsTheOldTitle()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;

        file.BeginRename();
        file.RenameTitle = "   ";

        Assert.False(file.TryCommitRename());
        Assert.Equal("Metric Proof", file.Title);
    }

    [Fact]
    public void BeginningAFileRename_SeedsTheFieldWithTheCurrentTitle()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;

        file.BeginRename();

        Assert.Equal("Metric Proof", file.RenameTitle);
    }

    // ---- File delete ----

    [Fact]
    public void RequestingAFileDeletion_AsksFirstAndNamesWhatGoesWithIt()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);

        file.RequestDeleteCommand.Execute(null);

        Assert.NotNull(file.Confirmation);
        Assert.Contains("Metric Proof", file.Confirmation!.Message, StringComparison.Ordinal);
        Assert.Contains("1 resource", file.Confirmation.Message, StringComparison.Ordinal);
        Assert.Equal("Delete File", file.Confirmation.ConfirmLabel);
        Assert.NotNull(projects.FileDetail); // nothing removed yet
        Assert.Single(projects.Detail!.Files);
    }

    [Fact]
    public void ConfirmingAFileDeletion_RemovesItAndClosesTheFileSurface()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.RequestDeleteCommand.Execute(null);

        file.ConfirmPromptCommand.Execute(null);

        Assert.Null(projects.FileDetail);
        Assert.Empty(projects.Detail!.Files);
    }

    /// <summary>
    /// A deleted File takes its resource rows with it. The service leaves that to the
    /// database's ON DELETE CASCADE, so the doubles must model the cascade too —
    /// otherwise a live resource row survives with its bytes already deleted, which is
    /// exactly the defect the mutations seam exists to make impossible.
    /// ConfirmingAFileDeletion_RemovesItAndClosesTheFileSurface cannot catch this: its
    /// File is empty, so there is no child row to leak.
    /// </summary>
    [Fact]
    public void ConfirmingAFileDeletion_TakesItsResourceRowsWithIt()
    {
        var (projects, _, resources) = WithRepositories();
        var file = projects.FileDetail!;
        var resourceId = AddLinkTo(file);
        Assert.NotNull(resources.GetById(resourceId));

        file.RequestDeleteCommand.Execute(null);
        file.ConfirmPromptCommand.Execute(null);

        Assert.Empty(projects.Detail!.Files);
        Assert.Null(resources.GetById(resourceId));
    }

    [Fact]
    public void DismissingAFileDeletion_KeepsTheFile()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.RequestDeleteCommand.Execute(null);

        file.KeepPromptCommand.Execute(null);

        Assert.Null(file.Confirmation);
        Assert.NotNull(projects.FileDetail);
        Assert.Single(projects.Detail!.Files);
    }

    // ---- Resource rename ----

    [Fact]
    public void RenamingAResource_ShowsTheNewTitleOnItsRow()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        var row = file.Resources.Single();

        row.BeginRename();
        row.RenameTitle = "Final transcript";

        Assert.True(row.TryCommitRename());
        Assert.Equal("Final transcript", file.Resources.Single().Title);
    }

    [Fact]
    public void RenamingAResource_ToBlank_IsRefusedAndKeepsTheOldTitle()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        var row = file.Resources.Single();
        var original = row.Title;

        row.BeginRename();
        row.RenameTitle = "";

        Assert.False(row.TryCommitRename());
        Assert.Equal(original, file.Resources.Single().Title);
    }

    // ---- Resource delete ----

    [Fact]
    public void RemovingAResource_AsksFirstAndNamesTheStoredDocument()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        var row = file.Resources.Single();

        row.DeleteCommand.Execute(null);

        Assert.NotNull(file.Confirmation);
        Assert.Contains("Transcript", file.Confirmation!.Message, StringComparison.Ordinal);
        Assert.Single(file.Resources); // nothing removed yet
    }

    [Fact]
    public void ConfirmingAResourceRemoval_RemovesIt()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        file.Resources.Single().DeleteCommand.Execute(null);

        file.ConfirmPromptCommand.Execute(null);

        Assert.Empty(file.Resources);
    }

    /// <summary>
    /// A link has no stored document, so the prompt must not claim one is deleted.
    /// Both branches of the wording need a pinned case.
    /// </summary>
    [Fact]
    public void RemovingALink_AsksWithoutTheStoredDocumentWarning()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.NewLinkUrl = "https://collegeboard.org/scores";
        file.NewLinkTitle = "SAT Scores";
        Assert.True(file.TryAddLink());
        var row = file.Resources.Single(r => r.Title == "SAT Scores");

        row.DeleteCommand.Execute(null);

        Assert.NotNull(file.Confirmation);
        Assert.Contains("SAT Scores", file.Confirmation!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stored document", file.Confirmation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DismissingAResourceRemoval_KeepsIt()
    {
        var projects = WithProjectAndFile();
        var file = projects.FileDetail!;
        file.Import(ResourceKind.Document, [@"C:\anywhere\Transcript.pdf"]);
        file.Resources.Single().DeleteCommand.Execute(null);

        file.KeepPromptCommand.Execute(null);

        Assert.Null(file.Confirmation);
        Assert.Single(file.Resources);
    }

    // ---- Project rename ----

    [Fact]
    public void RenamingAProject_ShowsTheNewNameOnTheHeaderAndInTheList()
    {
        var projects = WithProjectAndFile();
        var detail = projects.Detail!;

        detail.BeginRename();
        detail.RenameName = "College Apps";

        Assert.True(detail.TryCommitRename());
        Assert.Equal("College Apps", detail.Name);
        Assert.Equal("College Apps", projects.Projects.Single().Name);
    }

    [Fact]
    public void RenamingAProject_ToBlank_IsRefusedAndKeepsTheOldName()
    {
        var projects = WithProjectAndFile();
        var detail = projects.Detail!;

        detail.BeginRename();
        detail.RenameName = " ";

        Assert.False(detail.TryCommitRename());
        Assert.Equal("College Admissions", detail.Name);
    }

    // ---- Project delete ----

    [Fact]
    public void RequestingAProjectDeletion_AsksFirstAndSaysTasksSurvive()
    {
        var projects = WithProjectAndFile();
        var detail = projects.Detail!;

        detail.RequestDeleteCommand.Execute(null);

        Assert.NotNull(detail.Confirmation);
        Assert.Contains("College Admissions", detail.Confirmation!.Message, StringComparison.Ordinal);
        Assert.Contains("1 File", detail.Confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("Tasks", detail.Confirmation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Delete project", detail.Confirmation.ConfirmLabel);
        Assert.NotNull(projects.Detail);
    }

    [Fact]
    public void ConfirmingAProjectDeletion_RemovesItAndReturnsToTheList()
    {
        var projects = WithProjectAndFile();
        var detail = projects.Detail!;
        detail.RequestDeleteCommand.Execute(null);

        detail.ConfirmPromptCommand.Execute(null);

        Assert.Null(projects.Detail);
        Assert.Null(projects.FileDetail);
        Assert.Empty(projects.Projects);
    }

    /// <summary>
    /// The detail has to be torn down before the refresh chain is rung, not after.
    /// <c>TasksMutated</c> reaches <c>RefreshActive</c>, which calls
    /// <c>Detail.Refresh()</c> whenever a detail is still open — so announcing the
    /// mutation first points the refresh at the project that was just deleted. It is
    /// survivable today only because every lookup for a deleted project returns empty;
    /// the ordering, not that accident, is what should be holding it up.
    /// </summary>
    [Fact]
    public void ConfirmingAProjectDeletion_ClosesTheDetail_BeforeAnnouncingTheMutation()
    {
        var projects = WithProjectAndFile();
        var detail = projects.Detail!;
        detail.RequestDeleteCommand.Execute(null);

        var detailWasOpenWhenAnnounced = false;
        projects.TasksMutated += () => detailWasOpenWhenAnnounced = projects.Detail is not null;

        detail.ConfirmPromptCommand.Execute(null);

        Assert.False(detailWasOpenWhenAnnounced);
    }

    /// <summary>
    /// The same one level up: a deleted project leaves neither its Files nor their
    /// resources behind. Both hops of the cascade have to fire.
    /// </summary>
    [Fact]
    public void ConfirmingAProjectDeletion_TakesItsFilesAndTheirResourcesWithIt()
    {
        var (projects, files, resources) = WithRepositories();
        var file = projects.FileDetail!;
        var resourceId = AddLinkTo(file);
        var fileId = file.File.Id;
        var detail = projects.Detail!;

        detail.RequestDeleteCommand.Execute(null);
        detail.ConfirmPromptCommand.Execute(null);

        Assert.Empty(projects.Projects);
        Assert.Null(files.GetById(fileId));
        Assert.Null(resources.GetById(resourceId));
    }

    [Fact]
    public void DismissingAProjectDeletion_KeepsTheProject()
    {
        var projects = WithProjectAndFile();
        var detail = projects.Detail!;
        detail.RequestDeleteCommand.Execute(null);

        detail.KeepPromptCommand.Execute(null);

        Assert.Null(detail.Confirmation);
        Assert.NotNull(projects.Detail);
        projects.ReloadList();
        Assert.Single(projects.Projects);
    }
}
