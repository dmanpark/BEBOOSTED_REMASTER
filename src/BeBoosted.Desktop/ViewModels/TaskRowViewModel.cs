using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// One Inbox row wrapping a task. Editing always opens the one canonical Task editor
/// (via <paramref name="onEditRequested"/>) — the row keeps no form state of its own.
/// </summary>
public sealed partial class TaskRowViewModel(
    TaskItem task,
    Application.Calendar.CalendarService calendar,
    IClock clock,
    Action<TaskRowViewModel> onRemoved,
    string? projectName = null,
    Action<TaskRowViewModel>? onEditRequested = null,
    Action<TaskRowViewModel>? onRerankRequested = null) : ViewModelBase
{
    public TaskItem Task { get; } = task;

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
            if (projectName is { } project)
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

    [RelayCommand]
    private void Complete()
    {
        // The authoritative path: reconciles any one-off session outcome with the
        // Task in one transaction. A no-op stays silent.
        if (calendar.CompleteTask(Task.Id))
        {
            onRemoved(this);
        }
    }

    [RelayCommand]
    private void Delete()
    {
        // Deletion owns the whole aggregate: the Task, its sessions, and their
        // occurrence rows go together in one transaction — never an orphan.
        calendar.DeleteTask(Task.Id);
        onRemoved(this);
    }

    /// <summary>Opens the one canonical Task editor for this task.</summary>
    [RelayCommand]
    private void Edit() => onEditRequested?.Invoke(this);

    /// <summary>The rank chip: re-place this task among the others.</summary>
    [RelayCommand]
    private void Rerank() => onRerankRequested?.Invoke(this);

    private string DescribeDeadline(DateOnly deadline)
    {
        var today = clock.Today;
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
