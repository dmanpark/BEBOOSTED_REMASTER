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
    private readonly Application.Calendar.CalendarService _calendar;

    public ProjectDetailViewModel(
        ProjectsViewModel owner,
        Project project,
        ProjectService service,
        IProjectFileRepository files,
        TaskService taskService,
        Application.Calendar.CalendarService calendar)
    {
        _owner = owner;
        _service = service;
        _files = files;
        _taskService = taskService;
        _calendar = calendar;
        Project = project;
        Refresh();
    }

    public Project Project { get; }

    public string Name => Project.Name;

    public IBrush AccentBrush => ProjectsViewModel.BrushFor(Project.AccentColor);

    public ObservableCollection<ProjectTaskRowViewModel> OpenTasks { get; } = [];

    public ObservableCollection<ProjectTaskRowViewModel> RecentlyCompleted { get; } = [];

    /// <summary>Active scheduled work: upcoming and overdue rows, soonest first.</summary>
    public ObservableCollection<ScheduledBlockRowViewModel> ScheduledBlocks { get; } = [];

    /// <summary>Recently completed commitments, shown quietly below the active rows.</summary>
    public ObservableCollection<ScheduledBlockRowViewModel> CompletedScheduledBlocks { get; } = [];

    public ObservableCollection<FolioCardViewModel> Files { get; } = [];

    public bool HasOpenTasks => OpenTasks.Count > 0;

    public bool HasRecentlyCompleted => RecentlyCompleted.Count > 0;

    public bool HasScheduledWork => ScheduledBlocks.Count > 0 || CompletedScheduledBlocks.Count > 0;

    public bool HasCompletedScheduledBlocks => CompletedScheduledBlocks.Count > 0;

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

        ScheduledBlocks.Clear();
        CompletedScheduledBlocks.Clear();
        foreach (var row in _service.GetScheduledBlocks(Project.Id))
        {
            var rowViewModel = new ScheduledBlockRowViewModel(this, row, Project.AccentColor);
            if (row.State == ProjectBlockState.Done)
            {
                CompletedScheduledBlocks.Add(rowViewModel);
            }
            else
            {
                ScheduledBlocks.Add(rowViewModel);
            }
        }

        Files.Clear();
        foreach (var file in _files.GetForProject(Project.Id))
        {
            Files.Add(new FolioCardViewModel(this, file, _service.CountResources(file.Id), Project.AccentColor));
        }

        OnPropertyChanged(nameof(HasOpenTasks));
        OnPropertyChanged(nameof(HasRecentlyCompleted));
        OnPropertyChanged(nameof(HasScheduledWork));
        OnPropertyChanged(nameof(HasCompletedScheduledBlocks));
        OnPropertyChanged(nameof(HasFiles));
    }

    /// <summary>
    /// Completion toggle from a project row: persists through the same service path as
    /// the calendar control, then refreshes this detail and announces the calendar
    /// change exactly once. No-ops stay silent.
    /// </summary>
    internal void SetCommitmentCompletion(
        Domain.CalendarBlockId blockId, DateOnly occurrenceDate, bool completed)
    {
        if (_calendar.SetCommitmentOccurrenceCompletion(blockId, occurrenceDate, completed))
        {
            Refresh();
            _owner.NotifyCalendarChanged();
        }
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

/// <summary>
/// One scheduled-work row: a task-backed block or a directly linked fixed commitment.
/// Local commitments carry a completion toggle that shares the calendar's persistence
/// path; external commitments and task blocks show no completion control here.
/// </summary>
public sealed partial class ScheduledBlockRowViewModel : ViewModelBase
{
    private readonly ProjectDetailViewModel _owner;
    private readonly Application.Projects.ProjectScheduledBlock _row;
    private readonly string _accentColor;

    internal ScheduledBlockRowViewModel(
        ProjectDetailViewModel owner, Application.Projects.ProjectScheduledBlock row, string accentColor)
    {
        _owner = owner;
        _row = row;
        _accentColor = accentColor;
    }

    public string Title => _row.Title;

    public DateOnly Date => _row.Date;

    public TimeOnly Start => _row.Block.StartTime;

    public TimeSpan Duration => _row.Block.Duration;

    public string WhenText => string.Create(
        CultureInfo.CurrentCulture, $"{Date:ddd} {Start:h\\:mm tt}");

    public string DurationText => TaskRowViewModel.FormatDuration(Duration);

    /// <summary>Lazy: brushes are composition resources and must be created on the UI thread.</summary>
    public IBrush AccentBrush => ProjectsViewModel.BrushFor(_accentColor);

    public bool IsDone => _row.State == Application.Projects.ProjectBlockState.Done;

    /// <summary>End time passed without completion — quietly flagged, never hidden.</summary>
    public bool IsOverdue => _row.State == Application.Projects.ProjectBlockState.Overdue;

    public bool HasCompletionControl
        => _row.Block.Kind == Domain.Calendar.BlockKind.FixedCommitment && !_row.Block.IsExternal;

    public string CompletionControlName => IsDone ? $"Reopen {Title}" : $"Mark {Title} done";

    [RelayCommand]
    private void ToggleCompletion()
        => _owner.SetCommitmentCompletion(_row.Block.Id, Date, !IsDone);
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
