using System.Collections.ObjectModel;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>The universal Inbox: one capture queue for unscheduled work.</summary>
public sealed partial class InboxViewModel : ViewModelBase
{
    private readonly TaskService _service;
    private readonly IClock _clock;

    private readonly InboxQueryService _query;
    private readonly Application.Projects.IProjectRepository _projects;

    private readonly Application.Ai.AiService _ai;

    public InboxViewModel(
        TaskService service,
        InboxQueryService query,
        Application.Projects.IProjectRepository projects,
        Application.Ai.AiService ai,
        IClock clock)
    {
        _service = service;
        _query = query;
        _projects = projects;
        _ai = ai;
        _clock = clock;
        Reload();

        Tasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OpenCount));
            OnPropertyChanged(nameof(HasTasks));
        };
    }

    private IReadOnlyDictionary<Domain.TaskId, Domain.Prioritization.PriorityRank>? _ranks;

    private List<ProjectChoiceViewModel> _projectChoices = [ProjectChoiceViewModel.None];
    private Dictionary<Domain.ProjectId, string> _projectNames = [];

    /// <summary>Rebuilds the queue from storage — called after scheduling or outcomes change it.</summary>
    public void Reload()
    {
        var projects = _projects.GetAll();
        _projectNames = projects.ToDictionary(p => p.Id, p => p.Name);
        _projectChoices = [ProjectChoiceViewModel.None];
        _projectChoices.AddRange(projects.Select(p => new ProjectChoiceViewModel(p.Id, p.Name)));

        Tasks.Clear();
        foreach (var task in _query.GetInboxTasks())
        {
            Tasks.Add(CreateRow(task));
        }

        ApplyRanks();
    }

    private TaskRowViewModel CreateRow(Domain.Tasks.TaskItem task)
    {
        var projectName = task.ProjectId is { } projectId ? _projectNames.GetValueOrDefault(projectId) : null;
        return new TaskRowViewModel(task, _service, _clock, RemoveRow, _projectChoices, projectName)
        {
            NeedsReview = _ai.TaskNeedsReview(task),
        };
    }

    /// <summary>Shows the current period's ordinal rank chips on the queue rows.</summary>
    public void SetRanks(IReadOnlyDictionary<Domain.TaskId, Domain.Prioritization.PriorityRank>? ranks)
    {
        _ranks = ranks;
        ApplyRanks();
    }

    private void ApplyRanks()
    {
        foreach (var row in Tasks)
        {
            row.RankText = _ranks is not null && _ranks.TryGetValue(row.Task.Id, out var rank)
                ? $"#{rank.Rank}"
                : null;
        }
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    public int OpenCount => Tasks.Count;

    public bool HasTasks => Tasks.Count > 0;

    [ObservableProperty]
    public partial string CaptureText { get; set; } = string.Empty;

    [RelayCommand]
    private void Capture()
    {
        var title = CaptureText.Trim();
        if (title.Length == 0)
        {
            return;
        }

        var task = _service.Capture(title);
        Tasks.Add(CreateRow(task));
        CaptureText = string.Empty;
    }

    private void RemoveRow(TaskRowViewModel row) => Tasks.Remove(row);
}
