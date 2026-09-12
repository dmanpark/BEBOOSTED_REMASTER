using System.Collections.ObjectModel;
using Avalonia.Media;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// A project task's one visible state. The numeric values ARE the priority and the
/// sort order - a task qualifying for more than one takes the lowest number - so
/// reordering these members silently reorders the screen. They are written down for
/// that reason.
/// </summary>
public enum ProjectTaskStatus
{
    /// <summary>A session's end time passed without an outcome. Needs a decision.</summary>
    NeedsOutcome = 0,

    Scheduled = 1,

    Unscheduled = 2,

    Done = 3,
}

/// <summary>
/// A row's status plus whatever the affix needs to render it. The session identity
/// travels with it because clicking the affix opens that session's editor, and
/// keyboard focus has to find its way back to the same row afterwards.
/// </summary>
public sealed record ProjectTaskStatusInfo(
    ProjectTaskStatus Kind,
    DateOnly? SessionDate = null,
    TimeOnly? SessionStart = null,
    Domain.CalendarBlockId? SessionBlockId = null,
    DateOnly? CompletedOn = null);

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

    /// <summary>
    /// Every task in this project, exactly once. A task's sessions are folded into its
    /// row as a status rather than appearing as rows of their own - which is what used
    /// to let one task render three times on this screen.
    /// </summary>
    public ObservableCollection<ProjectTaskRowViewModel> Tasks { get; } = [];

    public ObservableCollection<FolioCardViewModel> Files { get; } = [];

    public bool HasTasks => Tasks.Count > 0;

    private int _openTaskCount;

    /// <summary>
    /// Open tasks, worded exactly as the project card words it — deliberately NOT the
    /// number of rows. Tasks holds the open ones plus at most three recently completed
    /// (GetProjectTasks' recentCount), so counting rows made a project with 4 open and
    /// 20 done read "7 tasks": true of neither the screen nor the project. Counting
    /// every task would need a service method this pass may not add, and "how much is
    /// left" is the question worth answering while standing in a project anyway. Saying
    /// it the card's way makes the two agree instead of differing one click apart.
    /// </summary>
    public string TaskCountText
        => $"{_openTaskCount} open task{(_openTaskCount == 1 ? string.Empty : "s")}";

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
        _openTaskCount = open.Count;
        var sessionsByTask = _service.GetScheduledBlocks(Project.Id)
            .Where(row => row.Block.TaskId is not null)
            .GroupBy(row => row.Block.TaskId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var rows = new List<ProjectTaskRowViewModel>();
        foreach (var task in open)
        {
            var sessions = sessionsByTask.GetValueOrDefault(task.Id) ?? [];

            // Unwindowed on purpose: GetScheduledBlocks only expands a repeating series
            // across a +/-14-day window, so a series with no occurrence in that window
            // (starting three weeks out, say) would otherwise read as non-repeating and
            // let a repeating task be completed as a whole from this list - a task that
            // completes per occurrence, never as a whole, must never offer that control.
            var repeating = _calendar.GetSessionsForTask(task.Id).Any(b => b.Recurrence is not null);
            rows.Add(new ProjectTaskRowViewModel(
                task, StatusForOpenTask(sessions, repeating), CompleteTaskRow, !repeating,
                RequestTaskEdit, RequestSessionEdit));
        }

        foreach (var task in recent)
        {
            rows.Add(new ProjectTaskRowViewModel(
                task,
                new ProjectTaskStatusInfo(
                    ProjectTaskStatus.Done,
                    CompletedOn: task.CompletedAt is { } at
                        ? DateOnly.FromDateTime(at.LocalDateTime)
                        : null),
                onCompleteRequested: null, canComplete: false, RequestTaskEdit));
        }

        Tasks.Clear();
        foreach (var row in rows
            .OrderBy(r => (int)r.Status)
            .ThenBy(r => r.SessionDate ?? DateOnly.MaxValue)
            .ThenBy(r => r.SessionStart ?? TimeOnly.MaxValue)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            Tasks.Add(row);
        }

        Files.Clear();
        foreach (var file in _files.GetForProject(Project.Id))
        {
            Files.Add(new FolioCardViewModel(this, file, _service.CountResources(file.Id), Project.AccentColor));
        }

        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(TaskCountText));
        OnPropertyChanged(nameof(HasFiles));
    }

    /// <summary>
    /// The one state an open task shows. Overdue outranks scheduled because a session
    /// that elapsed without an outcome is the only one asking the user for something.
    /// </summary>
    /// <param name="sessions">
    /// The windowed sessions from GetScheduledBlocks — the only source that can name a
    /// concrete date and time.
    /// </param>
    /// <param name="repeating">
    /// Whether the task has a repeating series at all, computed unwindowed by the
    /// caller. GetScheduledBlocks expands a series across +/-14 days only, so a weekly
    /// task whose next occurrence falls outside that window arrives here with an empty
    /// list — and falling through to Unscheduled would state the opposite of the truth
    /// about the user's own data. One-off sessions are never windowed out, so this is
    /// the whole of the gap. A recurrence has no end date, so a task that has one
    /// always has a next occurrence: saying "scheduled" without naming a time is the
    /// cheapest honest answer, and naming one would mean expanding the series here,
    /// duplicating in the view model what GetScheduledBlocks exists to do.
    /// </param>
    private static ProjectTaskStatusInfo StatusForOpenTask(
        IReadOnlyList<Application.Projects.ProjectScheduledBlock> sessions, bool repeating)
    {
        if (sessions
            .Where(s => s.State == Application.Projects.ProjectBlockState.Overdue)
            .OrderBy(s => s.Date)
            .ThenBy(s => s.Block.StartTime)
            .FirstOrDefault()
            is { } overdue)
        {
            return new ProjectTaskStatusInfo(
                ProjectTaskStatus.NeedsOutcome,
                overdue.Date, overdue.Block.StartTime, overdue.Block.Id);
        }

        var next = sessions
            .Where(s => s.State == Application.Projects.ProjectBlockState.Upcoming)
            .OrderBy(s => s.Date)
            .ThenBy(s => s.Block.StartTime)
            .FirstOrDefault();

        if (next is not null)
        {
            return new ProjectTaskStatusInfo(
                ProjectTaskStatus.Scheduled, next.Date, next.Block.StartTime, next.Block.Id);
        }

        return repeating
            ? new ProjectTaskStatusInfo(ProjectTaskStatus.Scheduled)
            : new ProjectTaskStatusInfo(ProjectTaskStatus.Unscheduled);
    }

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

    /// <summary>
    /// Opens the whole-task editor with this project already chosen. Standing in a
    /// project is itself the statement of where the task belongs.
    /// </summary>
    [RelayCommand]
    private void NewTask() => _owner.RequestNewTaskInProject(Project.Id);

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
    ProjectTaskStatusInfo status,
    Action<TaskItem>? onCompleteRequested = null,
    bool canComplete = true,
    Action<TaskItem>? onEditRequested = null,
    Action<Domain.CalendarBlockId, DateOnly>? onSessionRequested = null)
    : ViewModelBase
{
    public string Title => task.Title;

    /// <summary>Stable row identity for keyboard-focus restoration.</summary>
    public Domain.TaskId TaskId => task.Id;

    /// <summary>Whole-task completion; repeating tasks complete per occurrence instead.</summary>
    public bool CanComplete => canComplete;

    public ProjectTaskStatus Status => status.Kind;

    public DateOnly? SessionDate => status.SessionDate;

    public TimeOnly? SessionStart => status.SessionStart;

    /// <summary>The session the affix names, when there is one. Null otherwise.</summary>
    public Domain.CalendarBlockId? SessionBlockId => status.SessionBlockId;

    public DateOnly? CompletedOn => status.CompletedOn;

    /// <summary>
    /// Both halves, because opening the session needs both. Checking only the block
    /// would let a half-built status render an affix that silently does nothing.
    /// </summary>
    public bool HasSessionAffix => status.SessionBlockId is not null && status.SessionDate is not null;

    /// <summary>Completed rows stay in place and recede rather than moving away.</summary>
    public bool IsCompletedRow => status.Kind == ProjectTaskStatus.Done;

    public string SessionControlName => $"Edit session for {task.Title}";

    public string CompleteControlName => $"Complete {task.Title}";

    /// <summary>
    /// Display only. Tests assert <see cref="Status"/> instead, so rewording this
    /// cannot fail a test about behavior.
    /// </summary>
    public string StatusText => status.Kind switch
    {
        ProjectTaskStatus.NeedsOutcome => "needs outcome",
        ProjectTaskStatus.Scheduled => WithEstimate(
            status.SessionDate is null
                // Scheduled, but the series' next occurrence sits outside the window
                // GetScheduledBlocks expands, so there is no date to name.
                ? "scheduled"
                : $"{status.SessionDate:ddd} {status.SessionStart:h:mm tt}"),
        ProjectTaskStatus.Done => status.CompletedOn is { } on
            ? $"✓ done {on:ddd}"
            : "✓ done",
        _ => WithEstimate("unscheduled"),
    };

    private string WithEstimate(string when)
        => task.EstimatedDuration is { } estimate
            ? $"{when} · {TaskRowViewModel.FormatDuration(estimate)}"
            : when;

    /// <summary>Completes through the owner's one authoritative service path.</summary>
    [RelayCommand]
    private void Complete() => onCompleteRequested?.Invoke(task);

    public string EditControlName => $"Edit task {task.Title}";

    /// <summary>Opens the one canonical Task editor for this task.</summary>
    [RelayCommand]
    private void Edit() => onEditRequested?.Invoke(task);

    /// <summary>
    /// The affix opens the one session it names — the session scope, where the row
    /// itself is whole-task scope. A row with no affix has nothing to open. Both the
    /// block id and its date are always populated together (<see cref="ProjectTaskStatusInfo"/>),
    /// so requiring both here rather than tolerating a missing date is not a narrowing.
    /// </summary>
    [RelayCommand]
    private void OpenSession()
    {
        if (status.SessionBlockId is { } blockId && status.SessionDate is { } date)
        {
            onSessionRequested?.Invoke(blockId, date);
        }
    }
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
