using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class ProjectsViewModelTests
{
    [Fact]
    public void CreateProject_OpensItsDetailView()
    {
        var shell = TestShell.Create();
        var projects = shell.Projects;
        Assert.True(projects.IsListVisible);

        projects.NewProjectName = "College Admissions";
        Assert.True(projects.TryCreateProject());

        Assert.True(projects.IsDetailVisible);
        Assert.Equal("College Admissions", projects.Detail!.Name);
        Assert.False(projects.TryCreateProject()); // blank name rejected
    }

    [Fact]
    public void CreateFile_OpensTheFileSurface_AndBreadcrumbsNavigateBack()
    {
        var shell = TestShell.Create();
        var projects = shell.Projects;
        projects.NewProjectName = "College Admissions";
        projects.TryCreateProject();

        projects.Detail!.NewFileTitle = "Metric Proof";
        projects.Detail.NewFileDescription = "Scores, certificates, and verified numbers";
        Assert.True(projects.Detail.TryCreateFile());

        Assert.True(projects.IsFileVisible);
        Assert.Equal("Metric Proof", projects.FileDetail!.Title);
        Assert.Equal("FILE · COLLEGE ADMISSIONS", projects.FileDetail.TabLabel);

        projects.CloseFileCommand.Execute(null);
        Assert.True(projects.IsDetailVisible);
        Assert.Single(projects.Detail.Files);
        Assert.Equal("Metric Proof", projects.Detail.Files[0].Title);

        projects.CloseDetailCommand.Execute(null);
        Assert.True(projects.IsListVisible);
        Assert.Single(projects.Projects);
    }

    [Fact]
    public void AddingResources_UpdatesListSelectionAndCounts()
    {
        var shell = TestShell.Create();
        var projects = shell.Projects;
        projects.NewProjectName = "College Admissions";
        projects.TryCreateProject();
        projects.Detail!.NewFileTitle = "Metric Proof";
        projects.Detail.TryCreateFile();
        var file = projects.FileDetail!;

        file.NewLinkUrl = "https://collegeboard.org/scores";
        file.NewLinkTitle = "SAT Score Report";
        Assert.True(file.TryAddLink());

        file.NewNoteTitle = "Leadership metrics";
        file.NewNoteContent = "Led three teams.";
        Assert.True(file.TryAddNote());

        Assert.Equal(2, file.Resources.Count);
        Assert.Equal("2 resources", file.CountText);
        Assert.NotNull(file.Selected);
        Assert.Equal("URL", file.Resources[0].KindChip);
        Assert.Equal("NOTE", file.Resources[1].KindChip);
        Assert.Contains("Indexed", file.Resources[0].MetaText);

        file.DeleteResource(file.Resources[0]);
        Assert.Single(file.Resources);
    }

    [Fact]
    public void ProjectDetail_ShowsTasksAndCompletionWorks()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var projectRepo = new InMemoryProjectRepository();
        var project = Project.Create("DECA", ProjectPalette.Colors[0], clock.Now);
        projectRepo.Add(project);
        tasks.Add(TaskItem.Create("Practice role-play", clock.Now, projectId: project.Id));
        var shell = TestShell.Create(tasks: tasks, projects: projectRepo);

        shell.Projects.OpenProject(project.Id);
        var detail = shell.Projects.Detail!;
        Assert.Single(detail.OpenTasks);

        detail.OpenTasks[0].CompleteCommand.Execute(null);
        Assert.Empty(detail.OpenTasks);
        Assert.Single(detail.RecentlyCompleted);
    }

    [Fact]
    public void InboxRows_ShowProjectNames_AndEditCanAssignProjects()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var projectRepo = new InMemoryProjectRepository();
        var project = Project.Create("DECA", ProjectPalette.Colors[0], clock.Now);
        projectRepo.Add(project);
        var shell = TestShell.Create(projects: projectRepo);

        shell.Inbox.CaptureText = "Practice role-play";
        shell.Inbox.CaptureCommand.Execute(null);
        var row = shell.Inbox.Tasks[0];
        Assert.Equal(2, row.ProjectChoices.Count); // "No project" + DECA

        row.EditProject = row.ProjectChoices[1];
        row.CommitEditCommand.Execute(null);

        Assert.Equal(project.Id, row.Task.ProjectId);
        shell.Inbox.Reload();
        Assert.StartsWith("DECA", shell.Inbox.Tasks[0].MetaText, StringComparison.Ordinal);
    }
}
