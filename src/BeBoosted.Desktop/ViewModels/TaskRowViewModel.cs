using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>A project option in the task edit flyout ("No project" uses a null id).</summary>
public sealed record ProjectChoiceViewModel(Domain.ProjectId? Id, string Name)
{
    public static readonly ProjectChoiceViewModel None = new(null, "No project");
}

/// <summary>One Inbox row wrapping a task, with inline edit state for its flyout.</summary>
public sealed partial class TaskRowViewModel : ViewModelBase
{
    private readonly TaskService _service;
    private readonly IClock _clock;
    private readonly Action<TaskRowViewModel> _onRemoved;
    private readonly Action<TaskRowViewModel>? _onEdited;
    private readonly string? _projectName;

    public TaskRowViewModel(
        TaskItem task,
        TaskService service,
        IClock clock,
        Action<TaskRowViewModel> onRemoved,
        IReadOnlyList<ProjectChoiceViewModel>? projectChoices = null,
        string? projectName = null,
        Action<TaskRowViewModel>? onEdited = null)
    {
        Task = task;
        _service = service;
        _clock = clock;
        _onRemoved = onRemoved;
        _onEdited = onEdited;
        _projectName = projectName;
        ProjectChoices = projectChoices ?? [ProjectChoiceViewModel.None];
        ResetEditFields();
    }

    public IReadOnlyList<ProjectChoiceViewModel> ProjectChoices { get; }

    [ObservableProperty]
    public partial ProjectChoiceViewModel? EditProject { get; set; }

    public TaskItem Task { get; private set; }

    public string Title => Task.Title;

    public bool IsAiOrigin => Task.Origin == TaskOrigin.Ai;

    /// <summary>An AI source behind this task changed or disappeared.</summary>
    public bool NeedsReview { get; init; }

    /// <summary>Only the useful metadata: project, deadline, and estimated duration.</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>(3);
            if (_projectName is { } project)
            {
                parts.Add(project);
            }

            if (Task.Deadline is { } deadline)
            {
                parts.Add(DescribeDeadline(deadline));
            }

            if (Task.EstimatedDuration is { } duration)
            {
                parts.Add(FormatDuration(duration));
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasMeta => MetaText.Length > 0;

    /// <summary>Ordinal rank chip for the active planning period ("#1"); null when unranked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRank))]
    public partial string? RankText { get; set; }

    public bool HasRank => RankText is not null;

    [ObservableProperty]
    public partial string EditTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? EditDeadline { get; set; }

    [ObservableProperty]
    public partial decimal? EditDurationMinutes { get; set; }

    [RelayCommand]
    private void Complete()
    {
        _service.Complete(Task.Id);
        _onRemoved(this);
    }

    [RelayCommand]
    private void Delete()
    {
        _service.Delete(Task.Id);
        _onRemoved(this);
    }

    [RelayCommand]
    private void CommitEdit()
    {
        if (string.IsNullOrWhiteSpace(EditTitle))
        {
            ResetEditFields();
            return;
        }

        Task = _service.UpdateDetails(
            Task.Id,
            EditTitle,
            EditDurationMinutes is { } minutes and > 0 ? TimeSpan.FromMinutes((double)minutes) : null,
            EditDeadline is { } deadline ? DateOnly.FromDateTime(deadline.Date) : null,
            EditProject?.Id);
        ResetEditFields();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(HasMeta));
        _onEdited?.Invoke(this);
    }

    [RelayCommand]
    private void ResetEdit() => ResetEditFields();

    private void ResetEditFields()
    {
        EditTitle = Task.Title;
        EditDeadline = Task.Deadline is { } deadline
            ? new DateTimeOffset(deadline.ToDateTime(TimeOnly.MinValue))
            : null;
        EditDurationMinutes = Task.EstimatedDuration is { } duration ? (decimal)duration.TotalMinutes : null;
        EditProject = ProjectChoices.FirstOrDefault(choice => choice.Id == Task.ProjectId)
            ?? ProjectChoices[0];
    }

    private string DescribeDeadline(DateOnly deadline)
    {
        var today = _clock.Today;
        if (deadline == today)
        {
            return "Today";
        }

        if (deadline == today.AddDays(1))
        {
            return "Tomorrow";
        }

        if (deadline > today && deadline <= today.AddDays(6))
        {
            return deadline.ToString("ddd", CultureInfo.CurrentCulture);
        }

        return deadline.ToString("MMM d", CultureInfo.CurrentCulture);
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        var minutes = (int)duration.TotalMinutes;
        return minutes switch
        {
            < 60 => string.Create(CultureInfo.CurrentCulture, $"{minutes} min"),
            _ when minutes % 60 == 0 => string.Create(CultureInfo.CurrentCulture, $"{minutes / 60} h"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{minutes / 60} h {minutes % 60} min"),
        };
    }
}
