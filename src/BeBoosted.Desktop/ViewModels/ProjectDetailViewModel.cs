using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using BeBoosted.Application.Projects;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>Frame 05: a deliberately sparse project — tasks, upcoming blocks, and Files.</summary>
public sealed partial class ProjectDetailViewModel : ViewModelBase
{
    private readonly ProjectsViewModel _owner;
    private readonly ProjectService _service;
    private readonly IProjectFileRepository _files;
    private readonly TaskService _taskService;

    public ProjectDetailViewModel(
        ProjectsViewModel owner,
        Project project,
        ProjectService service,
        IProjectFileRepository files,
        TaskService taskService)
    {
        _owner = owner;
        _service = service;
        _files = files;
        _taskService = taskService;
        Project = project;
        Refresh();
    }

    public Project Project { get; }

    public string Name => Project.Name;

    public IBrush AccentBrush => ProjectsViewModel.BrushFor(Project.AccentColor);

    public ObservableCollection<ProjectTaskRowViewModel> OpenTasks { get; } = [];

    public ObservableCollection<ProjectTaskRowViewModel> RecentlyCompleted { get; } = [];

    public ObservableCollection<UpcomingBlockRowViewModel> UpcomingBlocks { get; } = [];

    public ObservableCollection<FolioCardViewModel> Files { get; } = [];

    public bool HasOpenTasks => OpenTasks.Count > 0;

    public bool HasRecentlyCompleted => RecentlyCompleted.Count > 0;

    public bool HasUpcomingBlocks => UpcomingBlocks.Count > 0;

    public bool HasFiles => Files.Count > 0;

    [ObservableProperty]
    public partial string NewFileTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewFileDescription { get; set; } = string.Empty;

    public void Refresh()
    {
        var (open, recent) = _service.GetProjectTasks(Project.Id);
        OpenTasks.Clear();
        foreach (var task in open)
        {
            OpenTasks.Add(new ProjectTaskRowViewModel(task, _taskService, Refresh));
        }

        RecentlyCompleted.Clear();
        foreach (var task in recent)
        {
            RecentlyCompleted.Add(new ProjectTaskRowViewModel(task, _taskService, Refresh));
        }

        UpcomingBlocks.Clear();
        foreach (var (block, task) in _service.GetUpcomingBlocks(Project.Id))
        {
            UpcomingBlocks.Add(new UpcomingBlockRowViewModel(
                block.Date, block.StartTime, block.Duration, task.Title, Project.AccentColor));
        }

        Files.Clear();
        foreach (var file in _files.GetForProject(Project.Id))
        {
            Files.Add(new FolioCardViewModel(this, file, _service.CountResources(file.Id), Project.AccentColor));
        }

        OnPropertyChanged(nameof(HasOpenTasks));
        OnPropertyChanged(nameof(HasRecentlyCompleted));
        OnPropertyChanged(nameof(HasUpcomingBlocks));
        OnPropertyChanged(nameof(HasFiles));
    }

    /// <summary>Opens the composer scoped to this project.</summary>
    [RelayCommand]
    private void AskBeBoosted() => _owner.AskRequested?.Invoke();

    /// <summary>Returns true when the File was created (the view closes its flyout).</summary>
    public bool TryCreateFile()
    {
        if (string.IsNullOrWhiteSpace(NewFileTitle))
        {
            return false;
        }

        var file = _service.CreateFile(Project.Id, NewFileTitle, NewFileDescription);
        NewFileTitle = string.Empty;
        NewFileDescription = string.Empty;
        Refresh();
        _owner.OpenFile(file.Id);
        return true;
    }

    public void OpenFile(Domain.ProjectFileId id) => _owner.OpenFile(id);
}

public sealed partial class ProjectTaskRowViewModel(TaskItem task, TaskService taskService, Action refresh)
    : ViewModelBase
{
    public string Title => task.Title;

    public bool IsCompleted => task.IsCompleted;

    public bool IsAiOrigin => task.Origin == TaskOrigin.Ai;

    public string MetaText
    {
        get
        {
            if (task.IsCompleted)
            {
                return task.CompletedAt is { } at
                    ? $"done {at.LocalDateTime:ddd}".ToLowerInvariant()
                    : "done";
            }

            var parts = new List<string>(2);
            if (task.Deadline is { } deadline)
            {
                parts.Add(deadline.ToString("ddd", CultureInfo.CurrentCulture));
            }

            if (task.EstimatedDuration is { } duration)
            {
                parts.Add(TaskRowViewModel.FormatDuration(duration));
            }

            return string.Join(" · ", parts);
        }
    }

    [RelayCommand]
    private void Complete()
    {
        taskService.Complete(task.Id);
        refresh();
    }
}

public sealed record UpcomingBlockRowViewModel(
    DateOnly Date, TimeOnly Start, TimeSpan Duration, string Title, string AccentColor)
{
    public string WhenText => string.Create(
        CultureInfo.CurrentCulture, $"{Date:ddd} {Start:h\\:mm tt}");

    public string DurationText => TaskRowViewModel.FormatDuration(Duration);

    /// <summary>Lazy: brushes are composition resources and must be created on the UI thread.</summary>
    public IBrush AccentBrush => ProjectsViewModel.BrushFor(AccentColor);
}

public sealed partial class FolioCardViewModel(
    ProjectDetailViewModel owner, ProjectFile file, int resourceCount, string accentColor) : ViewModelBase
{
    public ProjectFile File { get; } = file;

    public string Title => File.Title;

    public string? Description => File.Description;

    public bool HasDescription => File.Description is not null;

    /// <summary>Lazy: brushes are composition resources and must be created on the UI thread.</summary>
    public IBrush AccentBrush => ProjectsViewModel.BrushFor(accentColor);

    public string CountText => $"{resourceCount} resource{(resourceCount == 1 ? string.Empty : "s")}";

    [RelayCommand]
    private void Open() => owner.OpenFile(File.Id);
}
