using System.Collections.ObjectModel;
using Avalonia.Media;
using BeBoosted.Application.Projects;
using BeBoosted.Desktop.Platform;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// The Projects destination: a simple index, a deliberately sparse project view, and the
/// File reading surface. Navigation is a two-level stack (list → project → File).
/// </summary>
public sealed partial class ProjectsViewModel : ViewModelBase
{
    private readonly ProjectService _service;
    private readonly IProjectRepository _projects;
    private readonly IProjectFileRepository _files;
    private readonly IResourceRepository _resources;
    private readonly Application.Calendar.CalendarService _calendar;
    private readonly IFileRevealService _opener;
    private readonly Application.Ai.AiService _ai;

    public ProjectsViewModel(
        ProjectService service,
        IProjectRepository projects,
        IProjectFileRepository files,
        IResourceRepository resources,
        Application.Calendar.CalendarService calendar,
        IFileRevealService opener,
        Application.Ai.AiService ai)
    {
        _service = service;
        _projects = projects;
        _files = files;
        _resources = resources;
        _calendar = calendar;
        _opener = opener;
        _ai = ai;
        ReloadList();
    }

    /// <summary>Wired by the shell: "Ask BeBoosted about this project" expands the composer.</summary>
    public Action? AskRequested { get; set; }

    /// <summary>Raised when a project row asks for the one canonical Task editor.</summary>
    public event Action<Domain.TaskId>? TaskEditRequested;

    /// <summary>Raised for a scheduled-session row, carrying its concrete occurrence date.</summary>
    public event Action<Domain.CalendarBlockId, DateOnly>? SessionEditRequested;

    /// <summary>
    /// Raised after any project-originated mutation (whole-task completion or an
    /// occurrence toggle) — the shell's one central post-mutation chain.
    /// </summary>
    public event Action? TasksMutated;

    internal void RequestTaskEdit(Domain.TaskId taskId) => TaskEditRequested?.Invoke(taskId);

    internal void RequestSessionEdit(Domain.CalendarBlockId blockId, DateOnly occurrenceDate)
        => SessionEditRequested?.Invoke(blockId, occurrenceDate);

    internal void NotifyTasksMutated() => TasksMutated?.Invoke();

    public string? GetProjectName(Domain.ProjectId? id)
        => id is { } projectId ? _projects.GetById(projectId)?.Name : null;

    /// <summary>Navigates to the File containing the resource and selects it (citation click).</summary>
    public void ShowResource(Domain.ResourceId resourceId)
    {
        if (_resources.GetById(resourceId) is not { } resource
            || _files.GetById(resource.FileId) is not { } file)
        {
            return;
        }

        OpenProject(file.ProjectId);
        OpenFile(file.Id);
        FileDetail?.SelectResource(resourceId);
    }

    public ObservableCollection<ProjectCardViewModel> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible))]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    public partial ProjectDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible))]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    [NotifyPropertyChangedFor(nameof(IsFileVisible))]
    public partial FileDetailViewModel? FileDetail { get; private set; }

    public bool IsListVisible => Detail is null && FileDetail is null;

    public bool IsDetailVisible => Detail is not null && FileDetail is null;

    public bool IsFileVisible => FileDetail is not null;

    [ObservableProperty]
    public partial string NewProjectName { get; set; } = string.Empty;

    public void ReloadList()
    {
        Projects.Clear();
        foreach (var project in _projects.GetAll())
        {
            var (open, _) = _service.GetProjectTasks(project.Id);
            var fileCount = _files.GetForProject(project.Id).Count;
            Projects.Add(new ProjectCardViewModel(this, project, open.Count, fileCount));
        }

        OnPropertyChanged(nameof(HasProjects));
    }

    /// <summary>Returns true when created (the view closes its flyout).</summary>
    public bool TryCreateProject()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName))
        {
            return false;
        }

        var project = _service.CreateProject(NewProjectName);
        NewProjectName = string.Empty;
        ReloadList();
        OpenProject(project.Id);
        return true;
    }

    public void OpenProject(ProjectId id)
    {
        if (_projects.GetById(id) is { } project)
        {
            Detail = new ProjectDetailViewModel(this, project, _service, _files, _calendar);
        }
    }

    public void OpenFile(ProjectFileId id)
    {
        if (_files.GetById(id) is { } file && Detail is { } detail)
        {
            FileDetail = new FileDetailViewModel(this, detail.Project, file, _service, _opener, _ai);
        }
    }

    [RelayCommand]
    internal void CloseFile()
    {
        FileDetail = null;
        Detail?.Refresh();
    }

    [RelayCommand]
    internal void CloseDetail()
    {
        FileDetail = null;
        Detail = null;
        ReloadList();
    }

    /// <summary>Refreshes whichever surface is visible (called when tasks/blocks change).</summary>
    public void RefreshActive()
    {
        if (FileDetail is not null)
        {
            FileDetail.Refresh();
        }
        else if (Detail is not null)
        {
            Detail.Refresh();
            ReloadList(); // keep the card counts behind the detail current too
        }
        else
        {
            ReloadList();
        }
    }

    public static IBrush BrushFor(string hexColor) => new SolidColorBrush(Color.Parse(hexColor));
}

public sealed partial class ProjectCardViewModel(
    ProjectsViewModel owner, Project project, int openTaskCount, int fileCount) : ViewModelBase
{
    public Project Project { get; } = project;

    public string Name => Project.Name;

    public IBrush AccentBrush => ProjectsViewModel.BrushFor(Project.AccentColor);

    public string MetaText
    {
        get
        {
            var tasks = $"{openTaskCount} open task{(openTaskCount == 1 ? string.Empty : "s")}";
            var files = $"{fileCount} File{(fileCount == 1 ? string.Empty : "s")}";
            return $"{tasks} · {files}";
        }
    }

    [RelayCommand]
    private void Open() => owner.OpenProject(Project.Id);
}
