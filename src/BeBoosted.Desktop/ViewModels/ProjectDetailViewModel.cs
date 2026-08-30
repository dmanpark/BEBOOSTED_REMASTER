using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
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
    private readonly Application.Calendar.CalendarService _calendar;

    public ProjectDetailViewModel(
        ProjectsViewModel owner,
        Project project,
        ProjectService service,
        IProjectFileRepository files,
        Application.Calendar.CalendarService calendar)
    {
        _owner = owner;
        _service = service;
        _files = files;
        _calendar = calendar;
        Project = project;
        Refresh();
    }

    public Project Project { get; private set; }

    public string Name => Project.Name;

    public IBrush AccentBrush => ProjectsViewModel.BrushFor(Project.AccentColor);

    public ObservableCollection<ProjectTaskRowViewModel> OpenTasks { get; } = [];

    public ObservableCollection<ProjectTaskRowViewModel> RecentlyCompleted { get; } = [];

    /// <summary>Active scheduled work: upcoming and overdue rows, soonest first.</summary>
    public ObservableCollection<ScheduledBlockRowViewModel> ScheduledBlocks { get; } = [];

    /// <summary>Recently completed sessions, shown quietly below the active rows.</summary>
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

    // Rename-this-project flyout
    [ObservableProperty]
    public partial string RenameName { get; set; } = string.Empty;

    /// <summary>Seeds the flyout so the field opens on the current name.</summary>
    public void BeginRename() => RenameName = Project.Name;

    /// <summary>Returns true when the rename committed (the view closes its flyout).</summary>
    public bool TryCommitRename()
    {
        if (string.IsNullOrWhiteSpace(RenameName))
        {
            return false;
        }

        Project = _service.RenameProject(Project.Id, RenameName);
        OnPropertyChanged(nameof(Name));

        // Project labels are cached in Inbox, Daily, and Calendar snapshots; the one
        // central chain refreshes every dependent surface — and comes back through
        // RefreshActive, which rebuilds the cards holding the old name. One rebuild.
        _owner.NotifyTasksMutated();
        return true;
    }

    /// <summary>The open two-step delete confirmation, or null when nothing is pending.</summary>
    [ObservableProperty]
    public partial ConfirmationPrompt? Confirmation { get; private set; }

    private Action? _pendingConfirmedAction;

    /// <summary>
    /// Deleting a project takes every File and stored document with it. Its tasks
    /// survive and fall back to unassigned, which the prompt says plainly.
    /// </summary>
    [RelayCommand]
    private void RequestDelete()
    {
        var count = Files.Count;
        var scope = count == 0
            ? "It has no Files"
            : $"Its {count} File{(count == 1 ? string.Empty : "s")} and any stored documents are deleted";
        Confirmation = new ConfirmationPrompt(
            $"Delete '{Name}'? {scope}. Tasks in this project are kept, and become unassigned.",
            "Delete project",
            IsTaskDeletion: false);
        _pendingConfirmedAction = () =>
        {
            _service.DeleteProject(Project.Id);

            // Tear the detail down before ringing the refresh chain: NotifyTasksMutated
            // reaches RefreshActive, which refreshes whichever surface is open — and while
            // this detail is still open, that means refreshing the project just deleted.
            // ClearDetail rather than CloseDetail because that chain ends in ReloadList,
            // so closing must not rebuild the card list a second time.
            _owner.ClearDetail();
            _owner.NotifyTasksMutated();
        };
    }

    [RelayCommand]
    private void ConfirmPrompt()
    {
        var pending = _pendingConfirmedAction;
        Confirmation = null;
        _pendingConfirmedAction = null;
        pending?.Invoke();
    }

    [RelayCommand]
    private void KeepPrompt()
    {
        Confirmation = null;
        _pendingConfirmedAction = null;
    }

    public void Refresh()
    {
        var (open, recent) = _service.GetProjectTasks(Project.Id);
        OpenTasks.Clear();
        foreach (var task in open)
        {
            // A repeating task completes per occurrence (in Scheduled below), never
            // as a whole from this list.
            var repeating = _calendar.GetSessionsForTask(task.Id)
                .Any(session => session.Recurrence is not null);
            OpenTasks.Add(new ProjectTaskRowViewModel(
                task, CompleteTaskRow, !repeating, RequestTaskEdit));
        }

        RecentlyCompleted.Clear();
        foreach (var task in recent)
        {
            RecentlyCompleted.Add(new ProjectTaskRowViewModel(
                task, onCompleteRequested: null, canComplete: false, RequestTaskEdit));
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
    /// Occurrence-completion toggle from a project row (repeating sessions): persists
    /// through the same service path as the calendar control, then announces through
    /// the one central chain — which refreshes this detail. No-ops stay silent.
    /// </summary>
    internal void SetOccurrenceCompletion(
        Domain.CalendarBlockId blockId, DateOnly occurrenceDate, bool completed)
    {
        if (_calendar.SetOccurrenceCompletion(blockId, occurrenceDate, completed))
        {
            _owner.NotifyTasksMutated();
        }
    }

    /// <summary>
    /// One one-off session's completion, recorded against the block. The Task stays
    /// open — only the Task's own control completes it. Undo is the exception: a row
    /// of a task completed as a whole also renders Done, so reopening it there means
    /// reopening the Task (see <see cref="CompletedParentTaskOf"/>).
    /// </summary>
    internal void SetSessionCompletion(Domain.CalendarBlockId blockId, bool completed)
    {
        try
        {
            if (completed)
            {
                _calendar.RecordOutcome(blockId, BlockOutcome.Done);
            }
            else if (CompletedParentTaskOf(blockId) is { } completedTaskId)
            {
                if (!_calendar.ReopenTask(completedTaskId))
                {
                    return;
                }
            }
            else if (!_calendar.ClearSessionOutcome(blockId))
            {
                return;
            }
        }
        catch (DomainException)
        {
            return; // a stale row: the service mutated nothing
        }

        _owner.NotifyTasksMutated();
    }

    /// <summary>
    /// This session's task id when that task is completed as a whole, else null.
    /// A row of such a task renders Done whatever its own outcome, so clearing the
    /// session alone would change nothing visible and would strand an unresolved
    /// session on a completed task; the aggregate inverse — reopening the Task,
    /// which clears its Done sessions with it — is what undo means there.
    /// </summary>
    private TaskId? CompletedParentTaskOf(Domain.CalendarBlockId blockId)
        => _calendar.GetBlock(blockId)?.TaskId is { } taskId
            && _calendar.GetTask(taskId)?.IsCompleted == true
                ? taskId
                : null;

    /// <summary>
    /// Whole-task completion from a project row: the authoritative service path
    /// reconciles the Task with its one-off sessions, then one announcement through
    /// the central chain refreshes every dependent — including this detail — exactly
    /// once. No-ops and failures announce nothing.
    /// </summary>
    internal void CompleteTaskRow(TaskItem task)
    {
        if (_calendar.CompleteTask(task.Id))
        {
            _owner.NotifyTasksMutated();
        }
    }

    /// <summary>Every edit affordance routes to the one canonical Task editor.</summary>
    internal void RequestTaskEdit(TaskItem task) => _owner.RequestTaskEdit(task.Id);

    internal void RequestSessionEdit(Domain.CalendarBlockId blockId, DateOnly occurrenceDate)
        => _owner.RequestSessionEdit(blockId, occurrenceDate);

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

public sealed partial class ProjectTaskRowViewModel(
    TaskItem task,
    Action<TaskItem>? onCompleteRequested = null,
    bool canComplete = true,
    Action<TaskItem>? onEditRequested = null)
    : ViewModelBase
{
    public string Title => task.Title;

    /// <summary>Stable row identity for keyboard-focus restoration.</summary>
    public Domain.TaskId TaskId => task.Id;

    /// <summary>Whole-task completion; repeating tasks complete per occurrence instead.</summary>
    public bool CanComplete => canComplete;

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

    /// <summary>Completes through the owner's one authoritative service path.</summary>
    [RelayCommand]
    private void Complete() => onCompleteRequested?.Invoke(task);

    public bool CanEdit => onEditRequested is not null;

    public string EditControlName => $"Edit task {task.Title}";

    /// <summary>Opens the one canonical Task editor for this task.</summary>
    [RelayCommand]
    private void Edit() => onEditRequested?.Invoke(task);
}

/// <summary>
/// One scheduled-work row: a session of one of the project's tasks. Repeating
/// sessions carry a per-occurrence completion toggle that shares the calendar's
/// persistence path; one-off sessions resolve through their Task instead.
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

    /// <summary>Stable row identity for keyboard-focus restoration.</summary>
    public Domain.CalendarBlockId BlockId => _row.Block.Id;

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

    /// <summary>Every local session completes here; external events never do.</summary>
    public bool HasCompletionControl => !_row.Block.IsExternal;

    /// <summary>A repeating session completes per occurrence; a one-off by outcome.</summary>
    public bool IsRepeating => _row.Block.Recurrence is not null;

    public string CompletionControlName => IsDone ? $"Reopen {Title}" : $"Mark {Title} done";

    [RelayCommand]
    private void ToggleCompletion()
    {
        if (IsRepeating)
        {
            _owner.SetOccurrenceCompletion(_row.Block.Id, Date, !IsDone);
        }
        else
        {
            _owner.SetSessionCompletion(_row.Block.Id, !IsDone);
        }
    }

    /// <summary>External events sync in read-only; only task sessions open the editor.</summary>
    public bool CanEdit => !_row.Block.IsExternal;

    public string EditControlName => $"Edit session {Title}";

    /// <summary>Opens the canonical Task editor scoped to this row's occurrence date.</summary>
    [RelayCommand]
    private void Edit() => _owner.RequestSessionEdit(_row.Block.Id, Date);
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
