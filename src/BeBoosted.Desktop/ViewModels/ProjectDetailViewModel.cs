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
/// <param name="SessionDone">
/// Whether the named session is a repeating occurrence that is already ticked off.
/// The row needs it so the gutter circle can render checked and untick rather than
/// re-complete. Only ever true for a repeating occurrence: a one-off session resolved
/// Done belongs to a task the whole-task control speaks for, and saying "done" of that
/// circle while the task is still open would be a lie about the task.
/// </param>
public sealed record ProjectTaskStatusInfo(
    ProjectTaskStatus Kind,
    DateOnly? SessionDate = null,
    TimeOnly? SessionStart = null,
    Domain.CalendarBlockId? SessionBlockId = null,
    DateOnly? CompletedOn = null,
    bool SessionDone = false);

/// <summary>
/// The one occurrence of a repeating series a row is holding on to: which session, which
/// day, and whether the completion store holds a tick for it. Derived from the task's own
/// blocks rather than from any windowed or capped projection of the project's schedule,
/// so that ticking it cannot make it disappear.
/// </summary>
internal sealed record HeldOccurrence(
    Domain.CalendarBlockId BlockId, DateOnly Date, TimeOnly StartTime, bool Done);

/// <summary>Frame 05: a deliberately sparse project — tasks, upcoming blocks, and Files.</summary>
public sealed partial class ProjectDetailViewModel : ViewModelBase
{
    private readonly ProjectsViewModel _owner;
    private readonly ProjectService _service;
    private readonly IProjectFileRepository _files;
    private readonly Application.Calendar.CalendarService _calendar;
    private readonly Application.Abstractions.IClock _clock;

    public ProjectDetailViewModel(
        ProjectsViewModel owner,
        Project project,
        ProjectService service,
        IProjectFileRepository files,
        Application.Calendar.CalendarService calendar,
        Application.Abstractions.IClock clock)
    {
        _owner = owner;
        _service = service;
        _files = files;
        _calendar = calendar;
        _clock = clock;
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
            var taskBlocks = _calendar.GetSessionsForTask(task.Id);
            var repeating = taskBlocks.Any(b => b.Recurrence is not null);
            var status = StatusForOpenTask(sessions, taskBlocks, repeating);
            rows.Add(new ProjectTaskRowViewModel(
                task, status, OnSetDoneFor(taskBlocks, task, status, repeating),
                RequestTaskEdit, RequestSessionEdit, CheckOccurrenceFor(taskBlocks, status)?.Date));
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

                // A completed row keeps its circle so the completion can be undone
                // here; whole-task COMPLETION is what it no longer offers.
                done => SetTaskRowDone(task, done), RequestTaskEdit));
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
    /// The occurrence a repeating row holds on to, and whether it is ticked — resolved
    /// from the task's OWN sessions and the completion store, never from
    /// GetScheduledBlocks' rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two distinct reasons the projection cannot answer this. It keeps at most
    /// <see cref="ProjectService.RecentlyCompletedLimit"/> completed entries for the
    /// whole PROJECT, so in a project where several other tasks were finished today this
    /// task's ticked occurrence is simply absent from them. And it drops a completed
    /// occurrence from its active rows entirely (<c>nextUpcoming</c> skips done dates),
    /// so a row deriving its state from the projection loses the occurrence at the exact
    /// moment the user ticks it — the affix jumps forward, the tick cannot be undone,
    /// and that is the design the user was shown and turned down.
    /// </para>
    /// <para>
    /// The rule is the one the user stated: a row keeps naming an occurrence it ticked
    /// until that occurrence's day has passed. So this returns today's occurrence, done
    /// or not, and otherwise the earliest already-ticked occurrence lying between today
    /// and <paramref name="nextIncomplete"/> — the next thing the projection still has
    /// something to say about, which bounds the scan so the series is never expanded
    /// open-endedly here. An ELAPSED occurrence is deliberately not held: its day has
    /// passed, and pinning a past date to the row would leave the upcoming session
    /// unreachable from the affix for the rest of the series' period.
    /// </para>
    /// <para>
    /// Known limitation, recorded rather than fixed: today's occurrence is taken as the
    /// EARLIEST one today, unconditionally. A task with two occurrences today — or one
    /// today and a one-off tomorrow — therefore pins the row to the earlier one even
    /// after it is ticked, and the later session is unreachable from the affix until
    /// tomorrow. Narrow, self-correcting the next day, and the alternative (advancing off
    /// a ticked occurrence) is exactly the one-way design the user was shown and turned
    /// down, so the reversibility this exists for would be the thing paying for it.
    /// </para>
    /// </remarks>
    private HeldOccurrence? HeldOccurrenceOf(
        IReadOnlyList<Domain.Calendar.CalendarBlock> taskBlocks, DateOnly? nextIncomplete)
    {
        var today = _clock.Today;
        var series = taskBlocks.Where(IsCompletableSeries).ToList();

        if (series.Where(b => b.OccursOn(today)).OrderBy(b => b.StartTime).FirstOrDefault()
            is { } todays)
        {
            return new HeldOccurrence(
                todays.Id, today, todays.StartTime, _calendar.IsOccurrenceCompleted(todays.Id, today));
        }

        if (nextIncomplete is not { } limit)
        {
            return null;
        }

        HeldOccurrence? held = null;
        foreach (var block in series)
        {
            for (var date = today.AddDays(1); date < limit; date = date.AddDays(1))
            {
                if (!block.OccursOn(date) || !_calendar.IsOccurrenceCompleted(block.Id, date))
                {
                    continue;
                }

                if (held is null || date < held.Date
                    || (date == held.Date && block.StartTime < held.StartTime))
                {
                    held = new HeldOccurrence(block.Id, date, block.StartTime, true);
                }

                break;
            }
        }

        return held;
    }

    /// <summary>
    /// A session whose occurrences can be ticked at all: repeating, local, and a task
    /// session. Anything else makes <c>EnsureOccurrenceCompletable</c> throw.
    /// </summary>
    private static bool IsCompletableSeries(Domain.Calendar.CalendarBlock block)
        => block.Recurrence is not null
            && !block.IsExternal
            && block.Kind == Domain.Calendar.BlockKind.TaskSession;

    /// <summary>
    /// The occurrence this row's circle acts on, or null when it acts on the whole task
    /// (or on nothing). A repeating task completes per occurrence, so its circle is only
    /// ever about the one its affix names — including an overdue one, where ticking it
    /// is exactly the outcome being asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The named session has to be looked up and checked, not assumed: the overdue and
    /// upcoming branches read GetScheduledBlocks' rows, and that projection includes the
    /// task's ONE-OFF sessions. A weekly task that also has a one-off dated sooner names
    /// that one-off — and pointing SetOccurrenceCompletion at it throws
    /// "A one-off session records an outcome, not an occurrence completion", which
    /// neither this view model nor anything else in the Desktop project catches. Same
    /// rule as everywhere else here: no circle rather than one that cannot be clicked
    /// safely.
    /// </para>
    /// <para>
    /// Internal rather than private so a test can hold this against
    /// <c>CalendarBlock.EnsureOccurrenceCompletable</c> case for case. The two state one
    /// rule in two layers, which is deliberate — the Desktop layer must never wire a
    /// circle to a call that throws — but nothing else locks them together, and a fourth
    /// condition added to the domain guard would otherwise leave a crashing circle behind
    /// in silence.
    /// </remarks>
    internal static (Domain.CalendarBlockId BlockId, DateOnly Date)? CheckOccurrenceFor(
        IReadOnlyList<Domain.Calendar.CalendarBlock> taskBlocks, ProjectTaskStatusInfo status)
    {
        if (status.SessionBlockId is not { } blockId || status.SessionDate is not { } date)
        {
            return null;
        }

        var block = taskBlocks.FirstOrDefault(b => b.Id == blockId);
        return block is not null && IsCompletableSeries(block) && block.OccursOn(date)
            ? (blockId, date)
            : null;
    }

    /// <summary>
    /// What this row's gutter circle stands for, or null when it stands for nothing and
    /// must therefore not be drawn. A one-off task's circle is the task; a repeating
    /// task's is the occurrence its affix names, because a repeating task never
    /// completes as a whole. A repeating series naming no tickable occurrence — none in
    /// the window, or a one-off dated sooner — gets no circle rather than one that does
    /// nothing or throws.
    /// </summary>
    private Action<bool>? OnSetDoneFor(
        IReadOnlyList<Domain.Calendar.CalendarBlock> taskBlocks,
        TaskItem task,
        ProjectTaskStatusInfo status,
        bool repeating)
    {
        if (CheckOccurrenceFor(taskBlocks, status) is { } occurrence)
        {
            return done => SetOccurrenceRowDone(occurrence.BlockId, occurrence.Date, done);
        }

        return repeating ? null : done => SetTaskRowDone(task, done);
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
    /// <param name="taskBlocks">
    /// The task's own sessions, unwindowed and uncapped, from which the occurrence a
    /// repeating row holds is resolved (see <see cref="HeldOccurrenceOf"/>).
    /// </param>
    private ProjectTaskStatusInfo StatusForOpenTask(
        IReadOnlyList<Application.Projects.ProjectScheduledBlock> sessions,
        IReadOnlyList<Domain.Calendar.CalendarBlock> taskBlocks,
        bool repeating)
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

        // A repeating row holds the occurrence it names - today's whether or not it is
        // ticked, and any occurrence between today and `next` that is already ticked -
        // in preference to the projection's next INCOMPLETE one. Not a new top-level
        // priority (overdue still outranks it) but a refinement within the scheduled
        // case, and the whole of what makes the circle reversible: ticking must leave
        // the row naming what it just acted on, with the circle checked, so a second
        // click undoes it. Letting the affix advance the instant you tick is the design
        // the user was shown and turned down, and the defect the Week timeline had
        // removed - reintroducing it here would put it back one screen over.
        if (HeldOccurrenceOf(taskBlocks, next?.Date) is { } held)
        {
            return new ProjectTaskStatusInfo(
                ProjectTaskStatus.Scheduled,
                held.Date, held.StartTime, held.BlockId, SessionDone: held.Done);
        }

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
    /// Whole-task completion, and its undo, from a project row: the authoritative
    /// service path reconciles the Task with its one-off sessions either way, then one
    /// announcement through the central chain refreshes every dependent — including
    /// this detail — exactly once. No-ops and failures announce nothing.
    /// </summary>
    internal void SetTaskRowDone(TaskItem task, bool done)
    {
        if (done ? _calendar.CompleteTask(task.Id) : _calendar.ReopenTask(task.Id))
        {
            _owner.NotifyTasksMutated();
        }
    }

    /// <summary>
    /// The occurrence half: a repeating task completes per occurrence, so its row ticks
    /// off the one its affix names. The same service call the Today list and the Week
    /// timeline already make — only the way it is reached is new.
    /// </summary>
    internal void SetOccurrenceRowDone(Domain.CalendarBlockId blockId, DateOnly date, bool done)
    {
        if (_calendar.SetOccurrenceCompletion(blockId, date, done))
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

/// <param name="onSetDone">
/// Flips whatever this row's circle stands for, to the state passed in — the whole task
/// for a one-off row, the named occurrence for a repeating one. One callback because the
/// circle is a checkbox, not a one-way button: the same control must undo what it did.
/// Null means the row has nothing to tick, and the circle is not drawn at all.
/// </param>
/// <param name="checkOccurrenceDate">
/// The day the circle acts on, when it acts on one occurrence rather than on the whole
/// task. It is only used to say so out loud: a control announced as "Complete Stats HW"
/// over a row that means Tuesday's session states the wrong scope to anyone who cannot
/// see the affix beside it.
/// </param>
public sealed partial class ProjectTaskRowViewModel(
    TaskItem task,
    ProjectTaskStatusInfo status,
    Action<bool>? onSetDone = null,
    Action<TaskItem>? onEditRequested = null,
    Action<Domain.CalendarBlockId, DateOnly>? onSessionRequested = null,
    DateOnly? checkOccurrenceDate = null)
    : ViewModelBase
{
    public string Title => task.Title;

    /// <summary>Stable row identity for keyboard-focus restoration.</summary>
    public Domain.TaskId TaskId => task.Id;

    /// <summary>
    /// Whether the row's gutter circle is on screen at all. Tied to the callback rather
    /// than to a separate flag, so a circle can never be drawn over nothing — and so
    /// that what the circle STANDS for is the one thing that decides whether it exists:
    /// a completed row offers it so the completion can be undone, a repeating row offers
    /// it for the occurrence its affix names, and a repeating row naming no tickable
    /// occurrence offers nothing. A parallel "can this task be completed as a whole"
    /// flag used to sit beside this; it answered a question no control asks any more.
    /// </summary>
    public bool ShowCheck => onSetDone is not null;

    /// <summary>Whether the circle reads as checked — what a second click would undo.</summary>
    public bool IsDone => status.Kind == ProjectTaskStatus.Done || status.SessionDone;

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

    /// <summary>
    /// The gutter circle's accessible name. It says which way the click goes, because
    /// the same control now both finishes and undoes — and, for a repeating row, which
    /// occurrence it goes for, because there the circle is not about the whole task.
    /// </summary>
    public string CheckControlName
        => (IsDone ? "Reopen" : "Complete") + (checkOccurrenceDate is { } on
            ? $" {task.Title} on {on:ddd d MMM}"
            : $" {task.Title}");

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
                // A ticked occurrence says so in words, not only in a lime circle. The
                // row is not IsCompletedRow - the TASK is still open, correctly - so
                // none of the completed row's recession applies here, and on Week the
                // same occurrence already reads struck-through and recessed. The tick
                // is the Done branch's own, a couple of lines below.
                : $"{(status.SessionDone ? "✓ " : string.Empty)}"
                    + $"{status.SessionDate:ddd} {status.SessionStart:h:mm tt}"),
        ProjectTaskStatus.Done => status.CompletedOn is { } on
            ? $"✓ done {on:ddd}"
            : "✓ done",
        _ => WithEstimate("unscheduled"),
    };

    private string WithEstimate(string when)
        => task.EstimatedDuration is { } estimate
            ? $"{when} · {TaskRowViewModel.FormatDuration(estimate)}"
            : when;

    /// <summary>
    /// Asks for the opposite of what the circle currently shows, through the owner's one
    /// authoritative service path. Completing and undoing are the same click.
    /// </summary>
    [RelayCommand]
    private void ToggleDone() => onSetDone?.Invoke(!IsDone);

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
