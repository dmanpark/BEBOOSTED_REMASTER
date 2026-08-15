using System.Collections.ObjectModel;
using Avalonia.Media;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>One project choice in the commitment editor: "No project" or a real project.</summary>
public sealed class ProjectOptionViewModel(ProjectId? id, string name, string? accentColor)
{
    public ProjectId? Id { get; } = id;

    public string Name { get; } = name;

    public bool HasAccent => accentColor is not null;

    /// <summary>Lazy: brushes are composition resources and must be created on the UI thread.</summary>
    public IBrush? AccentBrush => accentColor is null ? null : ProjectsViewModel.BrushFor(accentColor);
}

/// <summary>
/// The one commitment editor, shown as a centered modal over the calendar. New and Edit
/// modes share this state, validation, and persistence path — there is no second form.
/// Recurring commitments are always edited and deleted as a whole series.
/// </summary>
public sealed partial class CommitmentEditorViewModel : ViewModelBase
{
    private readonly CalendarViewModel _owner;

    internal CommitmentEditorViewModel(
        CalendarViewModel owner,
        IReadOnlyList<ProjectOptionViewModel> projectOptions,
        CalendarBlock? block,
        DateOnly defaultDate,
        bool isCompleted = false,
        DateOnly? occurrenceDate = null)
    {
        _owner = owner;
        ProjectOptions = [.. projectOptions];
        SelectedProject = ProjectOptions[0];

        if (block is null)
        {
            // New commitments always start incomplete.
            Date = new DateTimeOffset(defaultDate.ToDateTime(TimeOnly.MinValue));
            Start = new TimeSpan(9, 0, 0);
            End = new TimeSpan(10, 0, 0);
            return;
        }

        BlockId = block.Id;
        OccurrenceDate = occurrenceDate ?? block.Date;
        IsCompleted = isCompleted;
        IsRecurringSeries = block.Recurrence is not null;
        Title = block.Title ?? string.Empty;
        Date = new DateTimeOffset(block.Date.ToDateTime(TimeOnly.MinValue));
        Start = block.StartTime.ToTimeSpan();
        End = block.EndTime.ToTimeSpan();
        SelectedProject = ProjectOptions.FirstOrDefault(o => o.Id == block.ProjectId)
            ?? ProjectOptions[0];
        if (block.Recurrence is { } recurrence)
        {
            RepeatsWeekly = true;
            foreach (var day in Days)
            {
                day.IsSelected = recurrence.DaysOfWeek.Contains(day.Day);
            }
        }
    }

    internal CalendarBlockId? BlockId { get; }

    /// <summary>The occurrence the completion checkbox applies to (edit mode only).</summary>
    internal DateOnly? OccurrenceDate { get; }

    public bool IsEditMode => BlockId is not null;

    /// <summary>Edits of a recurring commitment always apply to the whole series.</summary>
    public bool IsRecurringSeries { get; }

    public string Heading => !IsEditMode
        ? "New commitment"
        : IsRecurringSeries ? "Edit repeating commitment" : "Edit commitment";

    public string SaveButtonText => IsEditMode ? "Save changes" : "Add commitment";

    public string DeleteButtonText => IsRecurringSeries ? "Delete series" : "Delete commitment";

    public string DeleteConfirmationText => IsRecurringSeries
        ? "Delete this repeating commitment? Every occurrence goes with it."
        : "Delete this commitment?";

    /// <summary>
    /// Requested completion state, applied atomically with the other fields on Save.
    /// Cancel and Escape discard it like every other edit.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    /// <summary>Series edits complete only the opened occurrence — say so quietly.</summary>
    public string? CompletionScopeNote => IsRecurringSeries ? "Applies to this occurrence only." : null;

    /// <summary>"No project" first, then every project sorted by name.</summary>
    public ObservableCollection<ProjectOptionViewModel> ProjectOptions { get; }

    public bool HasProjects => ProjectOptions.Count > 1;

    public ObservableCollection<DayToggleViewModel> Days { get; } =
    [
        new(DayOfWeek.Monday), new(DayOfWeek.Tuesday), new(DayOfWeek.Wednesday),
        new(DayOfWeek.Thursday), new(DayOfWeek.Friday), new(DayOfWeek.Saturday), new(DayOfWeek.Sunday),
    ];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ProjectOptionViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? Date { get; set; }

    [ObservableProperty]
    public partial TimeSpan? Start { get; set; }

    [ObservableProperty]
    public partial TimeSpan? End { get; set; }

    [ObservableProperty]
    public partial bool RepeatsWeekly { get; set; }

    [ObservableProperty]
    public partial string? Error { get; internal set; }

    /// <summary>Deletion is a two-step interaction: request, then confirm inline.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; private set; }

    [RelayCommand]
    private void Save() => _owner.SaveCommitment(this);

    [RelayCommand]
    private void Cancel() => _owner.CloseCommitmentEditor();

    [RelayCommand]
    private void RequestDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete() => _owner.DeleteCommitment(this);
}
