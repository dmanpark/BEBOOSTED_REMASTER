using System.Collections.ObjectModel;
using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Projects;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Desktop.ViewModels;

public sealed partial class CalendarViewModel : ViewModelBase
{
    /// <summary>Default drag/keyboard snap; fine snap is used with the platform modifier held.</summary>
    public const int SnapMinutes = 15;
    public const int FineSnapMinutes = 5;

    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly CalendarService _calendar;
    private readonly ITaskRepository _tasks;
    private readonly PlanningService _planning;
    private readonly IProjectRepository _projects;
    private readonly bool _initialized;

    private PlanningProposal? _activeDraft;
    private IReadOnlyList<UnplacedTask>? _lastUnplaced;
    private readonly List<(long Sequence, PlanningProposalId ProposalId, IReadOnlyList<CalendarBlockId> BlockIds)> _approvalUndoStack = [];
    private long _approvalSequence;

    /// <summary>The approval the visible undo toast refers to (-1 = none).</summary>
    private long _visibleToastSequence = -1;

    public CalendarViewModel(
        AppSettings settings,
        IClock clock,
        CalendarService calendar,
        ITaskRepository tasks,
        PlanningService planning,
        IProjectRepository projects,
        InboxQueryService inboxQuery,
        PrioritySortService prioritySort,
        AiService ai)
    {
        _settings = settings;
        _clock = clock;
        _calendar = calendar;
        _tasks = tasks;
        _planning = planning;
        _projects = projects;
        Daily = new DailyListViewModel(
            this, inboxQuery, prioritySort, ai, calendar, tasks, projects, clock);
        VisibleDate = clock.Today;
        ViewKind = settings.GetLastCalendarView();
        _initialized = true;
        Reload();
    }

    /// <summary>The Today view's priority-first list (replaces the hourly timeline there).</summary>
    public DailyListViewModel Daily { get; }

    /// <summary>Raised when blocks changed in a way that can affect the Inbox queue.</summary>
    public event Action? DataChanged;

    /// <summary>Every open task — the set eligible to hold a rank this period.</summary>
    internal IReadOnlyList<TaskItem> OpenTasks => _tasks.GetOpen();

    /// <summary>Raised when a ranked row asks to be re-placed; the shell owns the surface.</summary>
    public event Action<TaskId>? RerankRequested;

    internal void RequestRerank(TaskId taskId) => RerankRequested?.Invoke(taskId);

    public ObservableCollection<DayColumnViewModel> Days { get; } = [];

    public ObservableCollection<CalendarBlockViewModel> ReviewBlocks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTodayView))]
    [NotifyPropertyChangedFor(nameof(IsWeekView))]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    [NotifyPropertyChangedFor(nameof(HeaderMeta))]
    public partial CalendarViewKind ViewKind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    [NotifyPropertyChangedFor(nameof(HeaderMeta))]
    public partial DateOnly VisibleDate { get; set; }

    [ObservableProperty]
    public partial int ReviewNoticeCount { get; private set; }

    public bool HasReviewNotice => ReviewNoticeCount > 0;

    public string ReviewNoticeText => ReviewNoticeCount == 1
        ? "1 previous block needs an outcome."
        : $"{ReviewNoticeCount} previous blocks need an outcome.";

    public bool IsTodayView
    {
        get => ViewKind == CalendarViewKind.Today;
        set
        {
            if (value)
            {
                ViewKind = CalendarViewKind.Today;
            }
        }
    }

    public bool IsWeekView
    {
        get => ViewKind == CalendarViewKind.Week;
        set
        {
            if (value)
            {
                ViewKind = CalendarViewKind.Week;
            }
        }
    }

    public string HeaderTitle
    {
        get
        {
            if (ViewKind == CalendarViewKind.Today)
            {
                return VisibleDate.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
            }

            var (monday, sunday) = WeekRange(VisibleDate);
            return monday.Month == sunday.Month
                ? string.Create(
                    CultureInfo.CurrentCulture,
                    $"{monday.ToString("MMMM d", CultureInfo.CurrentCulture)} – {sunday.Day}")
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"{monday.ToString("MMM d", CultureInfo.CurrentCulture)} – {sunday.ToString("MMM d", CultureInfo.CurrentCulture)}");
        }
    }

    /// <summary>The ISO week number (Week view). Today stays quiet — the Daily list owns progress.</summary>
    public string HeaderMeta
    {
        get
        {
            if (ViewKind == CalendarViewKind.Week)
            {
                var (monday, _) = WeekRange(VisibleDate);
                return $"Week {ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue))}";
            }

            return string.Empty;
        }
    }

    // ---- Task editor (centered modal, shared by every New/Edit entry point) ----

    /// <summary>The scrim shows while a scope-led editor is open.</summary>
    public bool IsTaskEditorOpen => ActiveTaskEditor is not null;

    [RelayCommand]
    private void OpenNewTaskEditor()
        => OpenNewWholeTaskEditor(VisibleDate, start: null, end: null, scheduled: false);

    /// <summary>New task prefilled with a schedule slot (empty Week-slot click, Today's Add task).</summary>
    public void OpenNewTaskEditorAt(DateOnly date, TimeOnly start, TimeOnly end)
        => OpenNewWholeTaskEditor(date, start, end, scheduled: true);

    /// <summary>New unscheduled task; the given date prefills if scheduling is turned on.</summary>
    public void OpenNewUnscheduledTaskEditor(DateOnly date)
        => OpenNewWholeTaskEditor(date, start: null, end: null, scheduled: false);

    /// <summary>
    /// Opens the session editor from a calendar block, scoped to the clicked
    /// occurrence (local sessions only).
    /// </summary>
    public void OpenTaskEditorForBlock(CalendarBlockId id, DateOnly? occurrenceDate = null)
    {
        if (_calendar.GetBlock(id) is not { Kind: BlockKind.TaskSession, IsExternal: false } session)
        {
            return;
        }

        OpenSessionEditorForBlock(id, occurrenceDate ?? session.Date);
    }

    /// <summary>
    /// Opens the whole-task editor from a task row (Inbox, Daily list, Projects):
    /// every session listed, none silently picked (the F-03 rule).
    /// </summary>
    public void OpenTaskEditorForTask(TaskId taskId) => OpenWholeTaskEditor(taskId);

    /// <summary>
    /// A scheduled-session row in a list is still task-scoped: resolves the
    /// block's owning task and opens the whole-task editor. External and
    /// orphaned blocks quietly no-op.
    /// </summary>
    internal void OpenTaskEditorForBlockOwner(CalendarBlockId id)
    {
        if (_calendar.GetBlock(id) is { Kind: BlockKind.TaskSession, IsExternal: false, TaskId: { } taskId })
        {
            OpenWholeTaskEditor(taskId);
        }
    }

    /// <summary>
    /// The occurrence a task-level edit means: today's when the series occurs today,
    /// otherwise the most recent elapsed occurrence, otherwise the anchor (a series
    /// that only starts in the future). A one-off session is its own occurrence —
    /// completing it from the anchor date was never wrong for those.
    /// </summary>
    internal DateOnly EditorOccurrenceFor(Domain.Calendar.CalendarBlock session)
    {
        if (session.Recurrence is null)
        {
            return session.Date;
        }

        var today = _clock.Today;
        var horizon = today.AddDays(-366);
        for (var date = today; date >= session.Date && date >= horizon; date = date.AddDays(-1))
        {
            if (session.OccursOn(date))
            {
                return date;
            }
        }

        return session.Date;
    }

    /// <summary>
    /// The one per-occurrence completion path (repeating sessions): persists the
    /// requested state and, only when something actually changed, reloads and
    /// announces it exactly once.
    /// </summary>
    public void SetOccurrenceDone(CalendarBlockId id, DateOnly occurrenceDate, bool done)
    {
        if (_calendar.SetOccurrenceCompletion(id, occurrenceDate, done))
        {
            Reload();
            DataChanged?.Invoke();
        }
    }

    // ---- Scope-led editors (F-03): the ActiveTaskEditor slot ----

    /// <summary>The open scope-led editor: WholeTaskEditorViewModel or SessionEditorViewModel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskEditorOpen))]
    public partial object? ActiveTaskEditor { get; private set; }

    /// <summary>The task whose row shows the "Editing" chip behind the scrim (frame 3a).</summary>
    internal TaskId? EditingTaskId { get; private set; }

    /// <summary>The block that keeps its lime halo behind the scrim (frame 3b).</summary>
    internal CalendarBlockId? EditingBlockId { get; private set; }

    partial void OnActiveTaskEditorChanged(object? value)
    {
        EditingTaskId = value switch
        {
            WholeTaskEditorViewModel wholeTask => wholeTask.TaskId,
            SessionEditorViewModel session => session.TaskId,
            _ => null,
        };
        EditingBlockId = value is SessionEditorViewModel scoped ? scoped.SessionId : null;
        foreach (var day in Days)
        {
            foreach (var block in day.Blocks)
            {
                block.IsBeingEdited = block.Id == EditingBlockId;
            }
        }

        Daily.SetEditingTask(EditingTaskId);
    }

    /// <summary>Non-null only while a session editor is pushed from the whole-task editor.</summary>
    internal EditorNavigation? Navigation { get; private set; }

    /// <summary>Raised on return to the whole-task editor: the row whose Edit button regains focus.</summary>
    public event Action<CalendarBlockId?>? EditorRowFocusRequested;

    internal void CloseActiveEditor()
    {
        Navigation = null;
        ActiveTaskEditor = null;
    }

    /// <summary>Opens the whole-task editor: every session listed, no silent selection.</summary>
    internal WholeTaskEditorViewModel? OpenWholeTaskEditor(TaskId taskId)
    {
        if (_calendar.GetTask(taskId) is not { } task)
        {
            return null;
        }

        Navigation = null;
        var editor = new WholeTaskEditorViewModel(
            this, BuildProjectOptions(), task, _calendar.GetSessionsForTask(taskId));
        ActiveTaskEditor = editor;
        return editor;
    }

    internal IReadOnlyList<CalendarBlock> SessionsForTask(TaskId taskId)
        => _calendar.GetSessionsForTask(taskId);

    /// <summary>
    /// Pushes the session editor for one Schedule row. A repeating row resolves
    /// its occurrence by the F-15 rule (today's, else most recent elapsed, else
    /// the anchor); the THIS OCCURRENCE label always names the resolved date.
    /// </summary>
    internal SessionEditorViewModel? OpenSessionEditorFromWholeTask(
        WholeTaskEditorViewModel parent, CalendarBlockId sessionId)
    {
        if (_calendar.GetBlock(sessionId) is not { } block)
        {
            return null;
        }

        var occurrence = block.Recurrence is null ? block.Date : EditorOccurrenceFor(block);
        var editor = OpenSessionEditorForBlock(sessionId, occurrence);
        if (editor is not null)
        {
            Navigation = new EditorNavigation(parent, sessionId);
        }

        return editor;
    }

    /// <summary>
    /// The gated promotion out of a session editor: the whole-task editor takes
    /// over with no return leg — closing it restores the original invoker.
    /// </summary>
    internal void PromoteToWholeTask(SessionEditorViewModel editor)
    {
        Navigation = null;
        OpenWholeTaskEditor(editor.TaskId);
    }

    /// <summary>
    /// Escape steps out one level: an open confirmation or gate dismisses first;
    /// a pushed session editor returns to its whole-task parent; the top level
    /// closes (the window restores the invoker's focus).
    /// </summary>
    public void EscapeTaskEditor()
    {
        switch (ActiveTaskEditor)
        {
            case WholeTaskEditorViewModel wholeTask when wholeTask.DismissActivePrompt():
                return;
            case SessionEditorViewModel session when session.DismissActivePrompt():
                return;
            case SessionEditorViewModel when Navigation is { Parent: not null } navigation:
                ReturnToWholeTask(refreshed: true, navigation.ReturnRowId);
                return;
            default:
                CloseActiveEditor();
                return;
        }
    }

    /// <summary>Pops the push: the parent whole-task editor returns, refreshed, with row focus.</summary>
    internal void ReturnToWholeTask(bool refreshed, CalendarBlockId? focusRowId)
    {
        var parent = Navigation?.Parent;
        Navigation = null;
        if (parent is null)
        {
            CloseActiveEditor();
            return;
        }

        ActiveTaskEditor = parent;
        if (refreshed)
        {
            parent.RefreshSessions();
        }

        EditorRowFocusRequested?.Invoke(focusRowId);
    }

    /// <summary>
    /// Persists only. Reloads and announces exactly once on success and advances
    /// the editor's dirty snapshot. NEVER navigates — callers decide what follows.
    /// </summary>
    internal bool TrySaveWholeTask(WholeTaskEditorViewModel editor)
    {
        if (editor.ShowInlineSchedule && editor.InlineSchedule.HasEndBeforeStart)
        {
            // Frame 4n (create mode): the END-pinned field message is
            // authoritative — no repository call, no announcement, no Error.
            return false;
        }

        try
        {
            if (editor.TaskId is { } taskId)
            {
                _calendar.UpdateTaskDetails(
                    taskId, editor.BuildDetails(),
                    editor.ShowCompletion
                        ? new TaskCompletionRequest(_clock.Today, editor.IsCompleted)
                        : null);
            }
            else
            {
                TaskScheduleRequest? schedule = null;
                if (editor.ShowInlineSchedule)
                {
                    schedule = editor.InlineSchedule.TryBuildSchedule(out var scheduleError);
                    if (schedule is null)
                    {
                        editor.Error = scheduleError;
                        return false;
                    }
                }

                _calendar.CreateTask(editor.BuildDetails(), schedule);
            }

            editor.MarkSaved();
            NotifyTasksMutated();
            return true;
        }
        catch (DomainException exception)
        {
            editor.Error = exception.Message;
            return false;
        }
        catch (SqliteException)
        {
            editor.Error = "Couldn't save — nothing was changed. Try again.";
            return false;
        }
    }

    internal void SaveWholeTask(WholeTaskEditorViewModel editor)
    {
        if (TrySaveWholeTask(editor))
        {
            CloseActiveEditor();
        }
    }

    /// <summary>Create mode: one atomic CreateTask, with the inline first session when revealed.</summary>
    internal WholeTaskEditorViewModel OpenNewWholeTaskEditor(
        DateOnly date, TimeOnly? start, TimeOnly? end, bool scheduled)
    {
        Navigation = null;
        var editor = new WholeTaskEditorViewModel(
            this, BuildProjectOptions(), date, start, end, scheduled);
        ActiveTaskEditor = editor;
        return editor;
    }

    /// <summary>Add session (edit mode): pushes the session editor in New mode.</summary>
    internal SessionEditorViewModel? OpenAddSessionEditor(WholeTaskEditorViewModel parent)
    {
        if (parent.TaskId is not { } taskId || _calendar.GetTask(taskId) is not { } task)
        {
            return null;
        }

        var editor = new SessionEditorViewModel(
            this, SessionEditorMode.New, task, TaskContextFor(task), session: null,
            occurrenceDate: null, isOccurrenceCompleted: false,
            _calendar.GetSessionsForTask(taskId));
        editor.Schedule.LoadDefaults(VisibleDate, new TimeOnly(9, 0), new TimeOnly(10, 0));
        editor.MarkSaved();
        ActiveTaskEditor = editor;
        Navigation = new EditorNavigation(parent, ReturnRowId: null);
        return editor;
    }

    /// <summary>One confirmed row removal; the whole-task editor stays open and refreshes.</summary>
    internal void RemoveSessionFromEditor(WholeTaskEditorViewModel editor, CalendarBlockId sessionId)
    {
        try
        {
            _calendar.UnscheduleSession(sessionId);
            editor.ScheduleNotice = null;
            editor.Error = null; // an earlier failure never outlives this success
            NotifyTasksMutated();
            editor.RefreshSessions();
        }
        catch (DomainException) when (_calendar.GetBlock(sessionId) is null)
        {
            // Stale is a fact about the block, not about exception wording.
            editor.ScheduleNotice = "That session was already removed — the list has been updated.";
            editor.RefreshSessions();
        }
        catch (DomainException exception)
        {
            editor.Error = exception.Message;
        }
        catch (SqliteException)
        {
            editor.Error = "Couldn't save — nothing was changed. Try again.";
        }
    }

    /// <summary>Every block of the task in one transaction; the task and the editor survive.</summary>
    internal void UnscheduleAllFromEditor(WholeTaskEditorViewModel editor)
    {
        try
        {
            _calendar.UnscheduleAllSessions(editor.TaskId!.Value);
            editor.ScheduleNotice = null;
            editor.Error = null; // an earlier failure never outlives this success
            NotifyTasksMutated();
            editor.RefreshSessions();
        }
        catch (DomainException exception)
        {
            editor.Error = exception.Message;
        }
        catch (SqliteException)
        {
            editor.Error = "Couldn't save — nothing was changed. Try again.";
        }
    }

    /// <summary>Deletes the task with every schedule; only the whole-task editor offers this.</summary>
    internal void DeleteTaskFromWholeTaskEditor(WholeTaskEditorViewModel editor)
    {
        try
        {
            _calendar.DeleteTask(editor.TaskId!.Value);
            NotifyTasksMutated();
            CloseActiveEditor();
        }
        catch (DomainException exception)
        {
            editor.Error = exception.Message;
        }
        catch (SqliteException)
        {
            editor.Error = "Couldn't save — nothing was changed. Try again.";
        }
    }

    /// <summary>
    /// Opens the session editor for a calendar block, scoped to the clicked
    /// occurrence. External and orphaned blocks quietly no-op, like the legacy path.
    /// </summary>
    internal SessionEditorViewModel? OpenSessionEditorForBlock(CalendarBlockId id, DateOnly occurrenceDate)
    {
        if (_calendar.GetBlock(id) is not { Kind: BlockKind.TaskSession, IsExternal: false } session
            || session.TaskId is not { } taskId
            || _calendar.GetTask(taskId) is not { } task)
        {
            return null;
        }

        var mode = session.Recurrence is null ? SessionEditorMode.OneOff : SessionEditorMode.Repeating;
        var completed = mode == SessionEditorMode.Repeating
            && _calendar.IsOccurrenceCompleted(id, occurrenceDate);
        var editor = new SessionEditorViewModel(
            this, mode, task, TaskContextFor(task), session, occurrenceDate, completed,
            _calendar.GetSessionsForTask(taskId));
        // A top-level open has no push to return to; a pushed open (the whole-task
        // Schedule row) re-establishes Navigation right after this call.
        Navigation = null;
        ActiveTaskEditor = editor;
        return editor;
    }

    /// <summary>"DECA · due Sun, Aug 16" — project and deadline context for the session editor.</summary>
    private string TaskContextFor(TaskItem task)
    {
        var parts = new List<string>();
        if (task.ProjectId is { } projectId
            && _projects.GetAll().FirstOrDefault(p => p.Id == projectId) is { } project)
        {
            parts.Add(project.Name);
        }

        if (task.Deadline is { } due)
        {
            parts.Add($"due {due.ToString("ddd, MMM d", CultureInfo.InvariantCulture)}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Stale is a fact about the block being gone — never inferred from text.</summary>
    private bool SessionIsGone(SessionEditorViewModel editor)
        => editor.SessionId is { } id && _calendar.GetBlock(id) is null;

    internal void CancelSession(SessionEditorViewModel editor)
    {
        if (Navigation is { Parent: not null } navigation)
        {
            ReturnToWholeTask(refreshed: true, navigation.ReturnRowId);
        }
        else
        {
            CloseActiveEditor();
        }
    }

    /// <summary>
    /// Persists only. Reloads and announces exactly once on success and advances
    /// the editor's dirty snapshot. NEVER navigates — callers decide what follows.
    /// savedSessionId: the edited block's id, or the id AddSession returned in New
    /// mode, so the caller can focus the exact new row.
    /// </summary>
    internal bool TrySaveSession(SessionEditorViewModel editor, out CalendarBlockId? savedSessionId)
    {
        savedSessionId = null;
        if (editor.Schedule.HasEndBeforeStart)
        {
            // Frame 4n: the END-pinned field message is authoritative — no
            // repository call, no announcement, and no generic Error line.
            return false;
        }

        var schedule = editor.Schedule.TryBuildSchedule(out var fieldError);
        if (schedule is null)
        {
            editor.Error = fieldError;
            return false;
        }

        try
        {
            if (editor.Mode == SessionEditorMode.New)
            {
                savedSessionId = _calendar.AddSession(editor.TaskId, schedule).Id;
            }
            else
            {
                var occurrenceCompletion =
                    editor.Mode == SessionEditorMode.Repeating && editor.Schedule.RepeatsWeekly
                        ? new TaskCompletionRequest(
                            editor.OccurrenceDate!.Value, editor.IsOccurrenceCompleted)
                        : null;
                _calendar.UpdateSessionSchedule(
                    editor.TaskId, editor.SessionId!.Value, schedule, occurrenceCompletion);
                savedSessionId = editor.SessionId;
            }

            editor.MarkSaved();
            NotifyTasksMutated();
            return true;
        }
        catch (DomainException) when (SessionIsGone(editor))
        {
            editor.IsStale = true;
            return false;
        }
        catch (DomainException exception)
        {
            editor.Error = exception.Message;
            return false;
        }
        catch (SqliteException)
        {
            editor.Error = "Couldn't save — nothing was changed. Try again.";
            return false;
        }
    }

    internal void SaveSession(SessionEditorViewModel editor)
    {
        if (TrySaveSession(editor, out var savedSessionId))
        {
            if (Navigation is { Parent: not null } navigation)
            {
                // A successful child mutation supersedes the parent's earlier
                // failure line; Cancel/Escape returns never touch it.
                navigation.Parent.Error = null;
                // The saved id (the edited row, or the block AddSession created)
                // is exactly the row whose Edit button regains focus.
                ReturnToWholeTask(refreshed: true, savedSessionId);
            }
            else
            {
                CloseActiveEditor();
            }
        }
    }

    /// <summary>Remove this session / Remove schedule: one confirmed block removal.</summary>
    internal void RemoveSessionFromSessionEditor(SessionEditorViewModel editor)
    {
        try
        {
            _calendar.UnscheduleSession(editor.SessionId!.Value);
            NotifyTasksMutated();
            if (Navigation is { Parent: not null } navigation)
            {
                // A successful child removal supersedes the parent's earlier
                // failure line; failed removals never reach this branch.
                navigation.Parent.Error = null;
                // The removed row is gone; focus falls back to the editor default.
                ReturnToWholeTask(refreshed: true, focusRowId: null);
            }
            else
            {
                CloseActiveEditor();
            }
        }
        catch (DomainException) when (SessionIsGone(editor))
        {
            editor.IsStale = true;
        }
        catch (DomainException exception)
        {
            editor.Error = exception.Message;
        }
        catch (SqliteException)
        {
            editor.Error = "Couldn't save — nothing was changed. Try again.";
        }
    }

    /// <summary>
    /// Delete path for a repeating block (Delete key or UI): opens the session
    /// editor on the rendered occurrence with the Remove-schedule confirmation
    /// already active — removal is never silent, and never deletes the task.
    /// </summary>
    public void RequestDeleteBlock(CalendarBlockId id, DateOnly occurrenceDate)
    {
        var editor = OpenSessionEditorForBlock(id, occurrenceDate);
        editor?.RequestRemoveCommand.Execute(null);
    }

    private List<ProjectOptionViewModel> BuildProjectOptions()
    {
        var options = new List<ProjectOptionViewModel> { new(null, "No project", null) };
        options.AddRange(_projects.GetAll()
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ProjectOptionViewModel(p.Id, p.Name, p.AccentColor)));
        return options;
    }

    // ---- Navigation ----

    partial void OnViewKindChanged(CalendarViewKind value)
    {
        if (_initialized)
        {
            _settings.SetLastCalendarView(value);
            Reload();
        }

        OnPropertyChanged(nameof(IsTodayView));
        OnPropertyChanged(nameof(IsWeekView));
    }

    partial void OnVisibleDateChanged(DateOnly value)
    {
        if (_initialized)
        {
            Reload();
        }
    }

    [RelayCommand]
    private void GoPrevious() => VisibleDate = VisibleDate.AddDays(ViewKind == CalendarViewKind.Week ? -7 : -1);

    [RelayCommand]
    private void GoNext() => VisibleDate = VisibleDate.AddDays(ViewKind == CalendarViewKind.Week ? 7 : 1);

    [RelayCommand]
    private void GoToToday() => VisibleDate = _clock.Today;

    // ---- Plan drafts ----

    public bool HasDraft => _activeDraft is { State: ProposalState.Draft } draft && draft.PendingBlocks.Any();

    /// <summary>Week keeps the floating draft card; Today shows an inline banner instead.</summary>
    public bool ShowFloatingDraftCard => HasDraft && IsWeekView;

    public string DraftTitle => _activeDraft?.Period.Kind == PlanningPeriodKind.Today
        ? "Plan draft · Today"
        : "Plan draft · This week";

    public string DraftSummaryText
    {
        get
        {
            if (_activeDraft is not { } draft)
            {
                return string.Empty;
            }

            var pending = draft.PendingBlocks.ToList();
            var taskCount = pending.Select(b => b.TaskId).Distinct().Count();
            var unscheduled = _lastUnplaced?.Count ?? 0;
            var text = $"{pending.Count} block{(pending.Count == 1 ? string.Empty : "s")} proposed · "
                + $"{taskCount} task{(taskCount == 1 ? string.Empty : "s")} scheduled";
            return unscheduled > 0
                ? $"{text} · {unscheduled} remain{(unscheduled == 1 ? "s" : string.Empty)} unscheduled"
                : text;
        }
    }

    public string? DraftLeftoverNote => _lastUnplaced is [{ } first, ..]
        ? $"{first.Title} stays in Inbox — {first.Reason}."
        : null;

    public bool HasDraftLeftoverNote => DraftLeftoverNote is not null;

    /// <summary>Creates a fresh draft for the period and shows it on the matching view.</summary>
    public void CreateDraft(PlanningPeriod period)
    {
        var result = _planning.CreateDraft(period);
        _lastUnplaced = result.Unplaced;

        // A new planning session supersedes every earlier plan: reverting one of
        // their approvals would resurrect a replaced plan beside this one, so the
        // service rejects it. Withdraw those offers instead of letting Ctrl+Z
        // retry an obsolete plan over and over.
        _approvalUndoStack.RemoveAll(entry => entry.ProposalId != result.Proposal.Id);
        ViewKind = period.Kind == PlanningPeriodKind.Week ? CalendarViewKind.Week : CalendarViewKind.Today;
        VisibleDate = _clock.Today;
        Reload();
    }

    [RelayCommand]
    private void ApproveDraft()
    {
        if (_activeDraft is not { } draft)
        {
            return;
        }

        IReadOnlyList<CalendarBlockId> created;
        try
        {
            created = _planning.ApproveAll(draft.Id);
        }
        catch (DomainException exception)
        {
            // The service rejected the whole batch before any write (for
            // example a legacy orphan block): surface why, change nothing.
            ShowNotice(exception.Message);
            return;
        }

        if (created.Count == 0)
        {
            return;
        }

        _approvalUndoStack.Add((++_approvalSequence, draft.Id, created));
        _visibleToastSequence = _approvalSequence;
        ShowUndoToast(created.Count == 1 ? "Block approved" : $"Plan approved · {created.Count} blocks");
        _lastUnplaced = null;
        Reload();
        DataChanged?.Invoke();
    }

    public void ApproveProposalBlock(CalendarBlockId blockId)
    {
        if (_activeDraft is not { } draft)
        {
            return;
        }

        CalendarBlockId created;
        try
        {
            created = _planning.ApproveBlock(draft.Id, blockId);
        }
        catch (DomainException exception)
        {
            ShowNotice(exception.Message);
            return;
        }

        _approvalUndoStack.Add((++_approvalSequence, draft.Id, [created]));
        _visibleToastSequence = _approvalSequence;
        ShowUndoToast("Block approved");
        Reload();
        DataChanged?.Invoke();
    }

    public void RemoveProposalBlock(CalendarBlockId blockId)
    {
        if (_activeDraft is not { } draft)
        {
            return;
        }

        _planning.RemoveBlock(draft.Id, blockId);
        _lastUnplaced = null;
        Reload();
    }

    public void MoveProposalBlock(CalendarBlockId blockId, DateOnly date, TimeOnly start)
    {
        if (_activeDraft is not { } draft)
        {
            return;
        }

        _planning.MoveBlock(draft.Id, blockId, date, start);
        Reload();
    }

    public void ResizeProposalBlockTo(CalendarBlockId blockId, TimeOnly end)
    {
        if (_activeDraft is not { } draft)
        {
            return;
        }

        try
        {
            _planning.ResizeBlock(draft.Id, blockId, end);
        }
        catch (DomainException)
        {
            // Resizing below the minimum keeps the previous size.
        }

        Reload();
    }

    [RelayCommand]
    private void DiscardDraft()
    {
        if (_activeDraft is not { } draft)
        {
            return;
        }

        try
        {
            _planning.DiscardDraft(draft.Id);
        }
        catch (DomainException exception)
        {
            // Stale UI or a race: the plan is no longer the active draft and the
            // service discarded nothing. Say why and leave the view as it was.
            ShowNotice(exception.Message);
            return;
        }

        _lastUnplaced = null;
        Reload();
    }

    /// <summary>Session-level undo (Ctrl+Z / ⌘Z) for approvals; also driven by the 10s toast.</summary>
    [RelayCommand]
    public void UndoLastApproval()
    {
        if (_approvalUndoStack.Count == 0)
        {
            return;
        }

        // Peek only: the recovery entry leaves the stack after the undo commits,
        // so a failed undo keeps it available for a retry and announces nothing.
        var (_, proposalId, blockIds) = _approvalUndoStack[^1];
        try
        {
            _planning.UndoApproval(proposalId, blockIds);
        }
        catch (DomainException exception)
        {
            ShowNotice(exception.Message);
            return;
        }

        _approvalUndoStack.RemoveAt(_approvalUndoStack.Count - 1);
        IsUndoToastVisible = false;
        Reload();
        DataChanged?.Invoke();
    }

    /// <summary>The toast's Undo action: shown for approvals, hidden for plain notices.</summary>
    [ObservableProperty]
    public partial bool IsUndoAvailable { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsUndoToastVisible { get; private set; }

    [ObservableProperty]
    public partial string UndoToastText { get; private set; } = string.Empty;

    /// <summary>Hides the toast after its 10-second window; Ctrl+Z keeps working afterwards.</summary>
    public void ExpireUndoToast() => IsUndoToastVisible = false;

    private void ShowUndoToast(string text)
    {
        IsUndoAvailable = true;
        ShowToast(text);
    }

    /// <summary>The same transient toast, carrying a plain message with no Undo offer.</summary>
    private void ShowNotice(string text)
    {
        IsUndoAvailable = false;
        ShowToast(text);
    }

    private void ShowToast(string text)
    {
        UndoToastText = text;
        IsUndoToastVisible = false; // retrigger the view timer even for back-to-back toasts
        IsUndoToastVisible = true;
    }

    /// <summary>
    /// Undo can never resurrect a deleted Task, nor delete a repeating series: an
    /// approval entry's created blocks vanish with the Task's deletion, and one
    /// edited into a weekly series is no longer the one-off session the approval
    /// added. Entries are pruned to the block ids still undoable — per id, not
    /// wholesale — and an undo offer with nothing left to undo is withdrawn.
    /// Ordinary moves and resizes of the original session change nothing here.
    /// </summary>
    private void PruneApprovalUndoStack()
    {
        for (var i = _approvalUndoStack.Count - 1; i >= 0; i--)
        {
            var (sequence, proposalId, blockIds) = _approvalUndoStack[i];
            var alive = blockIds.Where(id => _calendar.GetBlock(id) is { Recurrence: null }).ToList();
            if (alive.Count == blockIds.Count)
            {
                continue;
            }

            if (alive.Count == 0)
            {
                _approvalUndoStack.RemoveAt(i);
            }
            else
            {
                _approvalUndoStack[i] = (sequence, proposalId, alive);
            }
        }

        // The visible toast is bound to the approval that raised it: when that
        // entry is fully gone, hide the toast rather than let a click silently
        // undo an older approval (Ctrl+Z still reaches the older entries).
        if (IsUndoToastVisible && IsUndoAvailable
            && _approvalUndoStack.All(entry => entry.Sequence != _visibleToastSequence))
        {
            IsUndoToastVisible = false;
        }
    }

    // ---- Block operations (called by block VMs and the timeline surface) ----

    public void ScheduleTask(TaskId taskId, DateOnly date, TimeOnly start)
    {
        _calendar.ScheduleTask(taskId, date, start);
        Reload();
        DataChanged?.Invoke();
    }

    public void MoveBlock(CalendarBlockId id, DateOnly date, TimeOnly start)
    {
        _calendar.MoveBlock(id, date, start);
        Reload();
        DataChanged?.Invoke();
    }

    public void ResizeBlockTo(CalendarBlockId id, TimeOnly end)
    {
        var resized = true;
        try
        {
            _calendar.ResizeBlock(id, end);
        }
        catch (DomainException)
        {
            // Resizing below the minimum keeps the previous size.
            resized = false;
        }

        Reload();

        // Announce only real changes — a rejected resize must not refresh listeners.
        if (resized)
        {
            DataChanged?.Invoke();
        }
    }

    public void RecordOutcome(CalendarBlockId id, BlockOutcome outcome, TimeSpan? remaining)
    {
        try
        {
            _calendar.RecordOutcome(id, outcome, remaining);
        }
        catch (DomainException exception)
        {
            // Stale UI or a race: the service rejected the outcome and mutated
            // nothing. Surface the reason as a notice — no reload, no announcement.
            ShowNotice(exception.Message);
            return;
        }

        Reload();
        DataChanged?.Invoke();
    }

    public void UnscheduleBlock(CalendarBlockId id)
    {
        _calendar.UnscheduleSession(id);
        Reload();
        DataChanged?.Invoke();
    }

    public void NudgeBlock(CalendarBlockViewModel block, int minutes)
    {
        var start = block.StartTime.AddMinutes(minutes);
        var duration = block.EndTime - block.StartTime;
        if (start.ToTimeSpan() + duration > TimeSpan.FromHours(24) || (minutes < 0 && block.StartTime.ToTimeSpan().TotalMinutes + minutes < 0))
        {
            return;
        }

        block.MoveTo(block.Date, start);
    }

    public void NudgeBlockDays(CalendarBlockViewModel block, int days)
        => block.MoveTo(block.Date.AddDays(days), block.StartTime);

    public void ResizeBlockBy(CalendarBlockViewModel block, int minutes)
    {
        var end = block.EndTime.AddMinutes(minutes);
        if (end <= block.StartTime || end.ToTimeSpan() >= TimeSpan.FromHours(24))
        {
            return;
        }

        block.ResizeTo(end);
    }

    // ---- Loading ----

    public void Reload()
    {
        var (from, to) = ViewKind == CalendarViewKind.Week
            ? WeekRange(VisibleDate)
            : (VisibleDate, VisibleDate);

        var occurrences = _calendar.GetOccurrences(from, to);
        _activeDraft = _planning.GetActiveDraft();
        var pendingProposals = _activeDraft?.PendingBlocks.ToList() ?? [];

        // Conflicts consider both approved occurrences and pending proposals.
        var timed = occurrences
            .Select(o => new TimedItem(o.Block.Id, o.Date, o.StartTime, o.EndTime))
            .Concat(pendingProposals.Select(p => new TimedItem(p.Id, p.Date, p.StartTime, p.EndTime)))
            .ToList();
        var conflicts = ConflictDetector.FindConflicts(timed);

        var titles = new Dictionary<TaskId, (string Title, bool IsDone)>();
        foreach (var task in _tasks.GetAll())
        {
            titles[task.Id] = (task.Title, task.IsCompleted);
        }

        var now = _clock.Now;
        var today = _clock.Today;
        var nowMinutes = TimeOnly.FromDateTime(now.LocalDateTime).ToTimeSpan().TotalMinutes;

        Days.Clear();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var day = new DayColumnViewModel(date, date == today, date == today ? nowMinutes : -1);
            foreach (var occurrence in occurrences.Where(o => o.Date == date))
            {
                day.Blocks.Add(CreateBlockViewModel(occurrence, titles, conflicts, today, nowMinutes));
            }

            foreach (var proposal in pendingProposals.Where(p => p.Date == date))
            {
                var title = titles.TryGetValue(proposal.TaskId, out var info) ? info.Title : "(deleted task)";
                day.Blocks.Add(CalendarBlockViewModel.ForProposal(
                    this, proposal, title, conflicts.Contains(proposal.Id)));
            }

            Days.Add(day);
        }

        Daily.IsActive = ViewKind == CalendarViewKind.Today;
        if (Daily.IsActive)
        {
            Daily.Rebuild(VisibleDate, occurrences, pendingProposals, conflicts);
        }

        PruneApprovalUndoStack();
        RefreshReviewNotice(titles);
        OnPropertyChanged(nameof(HeaderMeta));
        OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(ShowFloatingDraftCard));
        OnPropertyChanged(nameof(DraftTitle));
        OnPropertyChanged(nameof(DraftSummaryText));
        OnPropertyChanged(nameof(DraftLeftoverNote));
        OnPropertyChanged(nameof(HasDraftLeftoverNote));
    }

    /// <summary>
    /// One announcement per user action: reloads the calendar (and Daily list) and tells
    /// dependents (Inbox, Projects) that task or block data changed.
    /// </summary>
    internal void NotifyTasksMutated()
    {
        Reload();
        DataChanged?.Invoke();
    }

    /// <summary>
    /// Refreshes the current-time indicator in place (called by a view timer) —
    /// deliberately not a full reload so open flyouts and focus are undisturbed.
    /// </summary>
    public void RefreshNow()
    {
        var today = _clock.Today;
        var nowMinutes = TimeOnly.FromDateTime(_clock.Now.LocalDateTime).ToTimeSpan().TotalMinutes;
        foreach (var day in Days)
        {
            day.NowMinutes = day.Date == today ? nowMinutes : -1;
        }
    }

    private CalendarBlockViewModel CreateBlockViewModel(
        BlockOccurrence occurrence,
        Dictionary<TaskId, (string Title, bool IsDone)> titles,
        IReadOnlySet<CalendarBlockId> conflicts,
        DateOnly today,
        double nowMinutes)
    {
        var block = occurrence.Block;
        var taskInfo = block.TaskId is { } taskId && titles.TryGetValue(taskId, out var info)
            ? info
            : ((string Title, bool IsDone)?)null;
        var title = block.Title ?? taskInfo?.Title ?? "(deleted task)";
        // A repeating session is done per occurrence; a one-off through its Task.
        var isDone = block.Recurrence is not null
            ? occurrence.IsCompleted
            : block.Outcome == BlockOutcome.Done || taskInfo?.IsDone == true;
        var elapsed = occurrence.Date < today
            || (occurrence.Date == today && block.EndTime.ToTimeSpan().TotalMinutes <= nowMinutes);
        var needsOutcome = block is { Kind: BlockKind.TaskSession, IsExternal: false, Recurrence: null }
            && block.Outcome == BlockOutcome.None && taskInfo?.IsDone != true && elapsed;
        return CalendarBlockViewModel.ForBlock(
            this, occurrence, title, conflicts.Contains(block.Id), isDone, needsOutcome,
            TaskRepeatsFor(block));
    }

    /// <summary>
    /// Whether a one-off session's Task also repeats somewhere — Done is then
    /// occurrence-scoped and this session may not complete the whole Task.
    /// </summary>
    private bool TaskRepeatsFor(CalendarBlock block)
        => block is { Kind: BlockKind.TaskSession, IsExternal: false, Recurrence: null }
            && block.TaskId is { } taskId
            && _calendar.GetSessionsForTask(taskId).Any(s => s.Recurrence is not null);

    private void RefreshReviewNotice(Dictionary<TaskId, (string Title, bool IsDone)> titles)
    {
        ReviewBlocks.Clear();
        foreach (var block in _calendar.GetBlocksNeedingOutcome())
        {
            var title = block.TaskId is { } taskId && titles.TryGetValue(taskId, out var info)
                ? info.Title
                : block.Title ?? "(deleted task)";
            ReviewBlocks.Add(CalendarBlockViewModel.ForBlock(
                this,
                new BlockOccurrence(block, block.Date),
                title,
                isConflicted: false,
                isDone: false,
                needsOutcome: true,
                TaskRepeatsFor(block)));
        }

        ReviewNoticeCount = ReviewBlocks.Count;
        OnPropertyChanged(nameof(HasReviewNotice));
        OnPropertyChanged(nameof(ReviewNoticeText));
    }

    /// <summary>Weeks start on Monday, matching the approved design frames.</summary>
    internal static (DateOnly Monday, DateOnly Sunday) WeekRange(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        var monday = date.AddDays(-offset);
        return (monday, monday.AddDays(6));
    }
}

/// <summary>
/// One pushed navigation level: the whole-task editor a session editor returns
/// to, and the row whose Edit button regains focus on return.
/// </summary>
internal sealed record EditorNavigation(
    WholeTaskEditorViewModel? Parent,
    CalendarBlockId? ReturnRowId);

/// <summary>Weekday toggle used by the Task editor's repeating schedule.</summary>
public sealed partial class DayToggleViewModel(DayOfWeek day) : ViewModelBase
{
    public DayOfWeek Day { get; } = day;

    public string Label => Day.ToString()[..2].ToUpperInvariant();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
