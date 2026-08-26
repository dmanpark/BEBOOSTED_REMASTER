using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
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
        projects.ReloadList();
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
