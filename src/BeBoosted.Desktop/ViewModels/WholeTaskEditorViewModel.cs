using System.Collections.ObjectModel;
using System.Globalization;
using BeBoosted.Application.Calendar;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// The whole-task editor: task fields as a draft until Save task, the complete
/// schedule as a live section of individually confirmed operations, and
/// whole-task completion where the authority allows it. Opening a session row
/// pushes the session editor; Cancel discards only the task draft.
/// </summary>
public sealed partial class WholeTaskEditorViewModel : ViewModelBase
{
    private sealed record TaskFieldsSnapshot(
        string Title, ProjectId? ProjectId, DateTimeOffset? Deadline,
        decimal? DurationMinutes, bool IsCompleted);

    private readonly CalendarViewModel _owner;
    private TaskFieldsSnapshot _snapshot = new(string.Empty, null, null, null, false);
    private IReadOnlyList<CalendarBlock> _lastSessions = [];
    private Action? _pendingConfirmedAction;
    private Action? _pendingGateAction;

    /// <summary>Edit mode over an existing task and its full session list.</summary>
    internal WholeTaskEditorViewModel(
        CalendarViewModel owner,
        IReadOnlyList<ProjectOptionViewModel> projectOptions,
        TaskItem task,
        IReadOnlyList<CalendarBlock> sessions)
    {
        _owner = owner;
        ProjectOptions = [.. projectOptions];
        TaskId = task.Id;
        Title = task.Title;
        SelectedProject = ProjectOptions.FirstOrDefault(o => o.Id == task.ProjectId)
            ?? ProjectOptions[0];
        Deadline = task.Deadline is { } deadline
            ? new DateTimeOffset(deadline.ToDateTime(TimeOnly.MinValue))
            : null;
        DurationMinutes = task.EstimatedDuration is { } estimate
            ? (decimal)estimate.TotalMinutes
            : null;
        IsCompleted = task.IsCompleted;
        ApplySessions(sessions);
        WatchInlineValidation();
        MarkSaved();
    }

    /// <summary>
    /// Create mode — the New-task editor. The Schedule section holds at most one
    /// inline first session so creation stays one atomic CreateTask; more
    /// sessions are added after the first save.
    /// </summary>
    internal WholeTaskEditorViewModel(
        CalendarViewModel owner,
        IReadOnlyList<ProjectOptionViewModel> projectOptions,
        DateOnly defaultDate,
        TimeOnly? prefilledStart,
        TimeOnly? prefilledEnd,
        bool scheduled)
    {
        _owner = owner;
        ProjectOptions = [.. projectOptions];
        SelectedProject = ProjectOptions[0];
        InlineSchedule.LoadDefaults(
            defaultDate, prefilledStart ?? new TimeOnly(9, 0), prefilledEnd ?? new TimeOnly(10, 0));
        ShowInlineSchedule = scheduled;
        ApplySessions([]);
        WatchInlineValidation();
        MarkSaved();
    }

    /// <summary>Frame 4n: an invalid inline first session pins its error and holds Save.</summary>
    private void WatchInlineValidation()
        => InlineSchedule.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScheduleFieldsViewModel.HasEndBeforeStart))
            {
                OnPropertyChanged(nameof(CanSaveTask));
            }
        };

    /// <summary>Save is held only while the visible inline schedule is invalid.</summary>
    public bool CanSaveTask => !ShowInlineSchedule || !InlineSchedule.HasEndBeforeStart;

    partial void OnShowInlineScheduleChanged(bool value)
        => OnPropertyChanged(nameof(CanSaveTask));

    internal TaskId? TaskId { get; }

    public bool IsEditMode => TaskId is not null;

    public bool IsCreateMode => TaskId is null;

    public string ScopeLabel => "WHOLE TASK";

    /// <summary>Card automation name: "Whole task — {title}".</summary>
    public string AccessibleName => $"Whole task — {Title}";

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(AccessibleName));

    // ---- Task fields (a draft until Save task) ----

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ProjectOptionViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? Deadline { get; set; }

    [ObservableProperty]
    public partial decimal? DurationMinutes { get; set; }

    public ObservableCollection<ProjectOptionViewModel> ProjectOptions { get; }

    public bool HasProjects => ProjectOptions.Count > 1;

    // ---- Completion (whole-task authority; absent under any repeating schedule) ----

    [ObservableProperty]
    public partial bool ShowCompletion { get; private set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    public string CompletionCheckboxText => "Mark whole task complete";

    [ObservableProperty]
    public partial string? AggregateNote { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> ScheduleNotes { get; private set; } = [];

    // ---- Schedule section (live; every operation individually confirmed) ----

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    [ObservableProperty]
    public partial string ScheduleSummary { get; private set; } = "0 sessions";

    [ObservableProperty]
    public partial bool ShowEmptyState { get; private set; }

    /// <summary>
    /// The schedule-row list (with its Add session footer) belongs to edit mode
    /// only: create mode holds at most the ONE inline first session, so the
    /// list must never render there — `!ShowEmptyState` alone would conflate
    /// the two once the inline form is revealed.
    /// </summary>
    public bool ShowScheduleList => IsEditMode && Sessions.Count > 0;

    public string EmptyStateText =>
        "No sessions scheduled. The task stays in your Inbox until you add one.";

    [ObservableProperty]
    public partial bool ShowUnscheduleAll { get; private set; }

    [ObservableProperty]
    public partial bool CanAddSession { get; private set; } = true;

    [ObservableProperty]
    public partial string? AddSessionBlockedNote { get; private set; }

    // ---- Prompts and errors ----

    [ObservableProperty]
    public partial ConfirmationPrompt? Confirmation { get; private set; }

    [ObservableProperty]
    public partial GatePrompt? Gate { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; internal set; }

    /// <summary>The body dims and goes inert while a confirmation or gate is open.</summary>
    public bool HasActivePrompt => Confirmation is not null || Gate is not null;

    partial void OnConfirmationChanged(ConfirmationPrompt? value)
        => OnPropertyChanged(nameof(HasActivePrompt));

    partial void OnGateChanged(GatePrompt? value) => OnPropertyChanged(nameof(HasActivePrompt));

    /// <summary>The quiet stale line above the Schedule section (frame 4m right).</summary>
    [ObservableProperty]
    public partial string? ScheduleNotice { get; internal set; }

    // ---- Create-mode inline first session (activated in the create-mode ctor) ----

    public ScheduleFieldsViewModel InlineSchedule { get; } = new();

    [ObservableProperty]
    public partial bool ShowInlineSchedule { get; private set; }

    public string SaveButtonText => IsEditMode ? "Save task" : "Add task";

    // ---- Commands ----

    [RelayCommand]
    private void Save()
    {
        if (!CanSaveTask)
        {
            // Frame 4n: the inline END-pinned message is authoritative; a
            // direct command execution performs nothing at all.
            return;
        }

        _owner.SaveWholeTask(this);
    }

    [RelayCommand]
    private void Cancel() => _owner.CloseActiveEditor();

    /// <summary>
    /// The gate precedes EVERY immediately persisted schedule operation while
    /// the draft is dirty; a clean draft runs the operation at once. Delete task
    /// is the sole exception — its confirmation supersedes the draft.
    /// </summary>
    private void RunGated(Action pending)
    {
        if (!IsDirty)
        {
            pending();
            return;
        }

        _pendingGateAction = pending;
        Gate = new GatePrompt("You have unsaved task changes.", "Save task and continue");
    }

    [RelayCommand]
    private void GateSaveAndContinue()
    {
        var pending = _pendingGateAction;
        Gate = null;
        _pendingGateAction = null;
        if (_owner.TrySaveWholeTask(this))   // persists + announces once; the editor STAYS ACTIVE
        {
            RefreshSessions();
            pending?.Invoke();               // navigation or the operation's own confirmation
        }

        // Failure: Error is set, the editor stays open, the pending action is
        // discarded, nothing navigated, nothing announced.
    }

    [RelayCommand]
    private void GateDiscardAndContinue()
    {
        var pending = _pendingGateAction;
        Gate = null;
        _pendingGateAction = null;
        RestoreDraftFromSnapshot();
        pending?.Invoke();
    }

    [RelayCommand]
    private void GateKeepEditing()
    {
        Gate = null;
        _pendingGateAction = null;
    }

    private void RestoreDraftFromSnapshot()
    {
        Title = _snapshot.Title;
        SelectedProject = ProjectOptions.FirstOrDefault(o => o.Id == _snapshot.ProjectId)
            ?? ProjectOptions[0];
        Deadline = _snapshot.Deadline;
        DurationMinutes = _snapshot.DurationMinutes;
        IsCompleted = _snapshot.IsCompleted;
    }

    /// <summary>Opens the session editor for one row, behind the gate when dirty.</summary>
    internal void EditRow(SessionRowViewModel row)
        => RunGated(() =>
        {
            if (_owner.OpenSessionEditorFromWholeTask(this, row.Data.Id) is null)
            {
                NoticeRowVanished();
            }
        });

    /// <summary>Row Remove: the gate first when dirty, then the scope-named confirmation.</summary>
    internal void RequestRemoveRow(SessionRowViewModel row)
        => RunGated(() =>
        {
            if (RowRemoveConfirmation(row.Data.Id) is not { } confirmation)
            {
                NoticeRowVanished();
                return;
            }

            Confirmation = confirmation;
            _pendingConfirmedAction = () => _owner.RemoveSessionFromEditor(this, row.Data.Id);
        });

    /// <summary>A targeted row's block is gone: the quiet notice, and a fresh list.</summary>
    private void NoticeRowVanished()
    {
        ScheduleNotice = "That session was already removed — the list has been updated.";
        RefreshSessions();
    }

    /// <summary>Create mode reveals the inline first session; edit mode pushes the New editor.</summary>
    [RelayCommand]
    private void AddSession()
    {
        if (IsCreateMode)
        {
            ShowInlineSchedule = true;
            ShowEmptyState = false;
            return;
        }

        RunGated(() => _owner.OpenAddSessionEditor(this));
    }

    /// <summary>The quiet Remove link inside the create-mode first-session group.</summary>
    [RelayCommand]
    private void ClearInlineSchedule()
    {
        ShowInlineSchedule = false;
        ShowEmptyState = Sessions.Count == 0;
    }

    [RelayCommand]
    private void RequestUnscheduleAll()
        => RunGated(() =>
        {
            Confirmation = UnscheduleAllConfirmation();
            _pendingConfirmedAction = () => _owner.UnscheduleAllFromEditor(this);
        });

    /// <summary>Delete task needs no gate: its confirmation supersedes the draft.</summary>
    [RelayCommand]
    private void RequestDelete()
    {
        Confirmation = DeleteConfirmation();
        _pendingConfirmedAction = () => _owner.DeleteTaskFromWholeTaskEditor(this);
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

    /// <summary>Null when the row's block is no longer in the refreshed list.</summary>
    private ConfirmationPrompt? RowRemoveConfirmation(CalendarBlockId sessionId)
    {
        var block = _lastSessions.FirstOrDefault(s => s.Id == sessionId);
        if (block is null)
        {
            return null;
        }

        if (block.Recurrence is not null)
        {
            return new ConfirmationPrompt(
                "Remove the repeating schedule? Every occurrence and its completion history "
                + "go with it. The task stays.",
                "Remove schedule",
                IsTaskDeletion: false);
        }

        var date = block.Date.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
        var times = SessionEditorViewModel.CopyTimeRange(block.StartTime, block.EndTime);
        var consequence = KeepConsequence(_lastSessions.Count - 1);
        return new ConfirmationPrompt(
            $"Remove this session — {date} · {times}? {consequence}",
            "Remove session",
            IsTaskDeletion: false);
    }

    /// <summary>"The task keeps its other 1 session." / "… other {N} sessions."</summary>
    internal static string KeepConsequence(int remaining) => remaining switch
    {
        <= 0 => "The task stays, unscheduled.",
        1 => "The task keeps its other 1 session.",
        _ => string.Create(
            CultureInfo.InvariantCulture, $"The task keeps its other {remaining} sessions."),
    };

    private ConfirmationPrompt UnscheduleAllConfirmation()
    {
        var repeating = _lastSessions.Count(s => s.Recurrence is not null);
        var oneOffs = _lastSessions.Count - repeating;
        string message;
        if (repeating == 0)
        {
            message = string.Create(
                CultureInfo.InvariantCulture,
                $"Remove all {oneOffs} sessions? The task itself stays.");
        }
        else if (oneOffs == 0)
        {
            // Unschedule all needs two rows, so a repeating-only task has R >= 2.
            message = string.Create(
                CultureInfo.InvariantCulture,
                $"Remove {repeating} repeating schedules? Their completion history goes with them. The task stays.");
        }
        else
        {
            var oneOffPart = oneOffs == 1
                ? "the one-off session"
                : string.Create(CultureInfo.InvariantCulture, $"{oneOffs} one-off sessions");
            var repeatingPart = repeating == 1
                ? "the repeating schedule? The schedule's completion history goes with it."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{repeating} repeating schedules? Their completion history goes with them.");
            message = $"Remove {oneOffPart} and {repeatingPart} The task stays.";
        }

        return new ConfirmationPrompt(message, "Remove all", IsTaskDeletion: false);
    }

    private ConfirmationPrompt DeleteConfirmation()
    {
        var repeating = _lastSessions.Count(s => s.Recurrence is not null);
        var oneOffs = _lastSessions.Count - repeating;
        var schedulePhrase = repeating == 1
            ? "Its repeating schedule"
            : string.Create(CultureInfo.InvariantCulture, $"Its {repeating} repeating schedules");
        var message = (oneOffs, repeating) switch
        {
            (0, 0) => "Delete this task?",
            (1, 0) => "Delete this task? Its session goes with it.",
            (_, 0) => string.Create(
                CultureInfo.InvariantCulture,
                $"Delete this task? Its {oneOffs} sessions go with it."),
            (0, _) => $"Delete this task? {schedulePhrase} and completed occurrences go with it.",
            (1, _) => $"Delete this task? {schedulePhrase} and 1 one-off session go with it.",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"Delete this task? {schedulePhrase} and {oneOffs} one-off sessions go with it."),
        };
        return new ConfirmationPrompt(message, "Delete task", IsTaskDeletion: true);
    }

    // ---- Draft bookkeeping ----

    internal bool IsDirty
        => Title != _snapshot.Title
            || SelectedProject?.Id != _snapshot.ProjectId
            || Deadline != _snapshot.Deadline
            || DurationMinutes != _snapshot.DurationMinutes
            || IsCompleted != _snapshot.IsCompleted;

    /// <summary>The dirty snapshot advances to the just-persisted values.</summary>
    internal void MarkSaved()
    {
        _snapshot = new TaskFieldsSnapshot(
            Title, SelectedProject?.Id, Deadline, DurationMinutes, IsCompleted);
        Error = null; // an earlier failure never outlives this success
    }

    /// <summary>Escape's first stop: an open confirmation or gate dismisses before anything navigates.</summary>
    internal bool DismissActivePrompt()
    {
        if (Confirmation is null && Gate is null)
        {
            return false;
        }

        Confirmation = null;
        Gate = null;
        _pendingConfirmedAction = null;
        _pendingGateAction = null;
        return true;
    }

    /// <summary>The editor's TaskDetailsRequest, mirroring the legacy field mapping.</summary>
    internal TaskDetailsRequest BuildDetails()
        => new(
            Title,
            SelectedProject?.Id,
            Deadline is { } deadline ? DateOnly.FromDateTime(deadline.Date) : null,
            DurationMinutes is { } minutes and > 0 ? TimeSpan.FromMinutes((double)minutes) : null);

    /// <summary>
    /// Re-reads the schedule and recomputes EVERY schedule-derived property —
    /// schedule mutations change them while the editor stays open, so none of
    /// them is constructor-frozen.
    /// </summary>
    internal void RefreshSessions()
    {
        if (TaskId is { } taskId)
        {
            ApplySessions(_owner.SessionsForTask(taskId));
        }
    }

    private void ApplySessions(IReadOnlyList<CalendarBlock> sessions)
    {
        _lastSessions = sessions;
        Sessions.Clear();
        foreach (var row in SessionListBuilder.Build(sessions))
        {
            Sessions.Add(new SessionRowViewModel(this, row));
        }

        ScheduleSummary = SessionListBuilder.SummaryFor(sessions);
        OnPropertyChanged(nameof(ShowScheduleList));
        ShowEmptyState = sessions.Count == 0 && !ShowInlineSchedule;
        ShowUnscheduleAll = sessions.Count >= 2;

        var repeatingCount = sessions.Count(s => s.Recurrence is not null);
        var oneOffCount = sessions.Count - repeatingCount;
        ShowCompletion = IsEditMode && repeatingCount == 0;
        AggregateNote = ShowCompletion && oneOffCount >= 2
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Completing or reopening applies to all {oneOffCount} sessions.")
            : null;
        ScheduleNotes = ScheduleNotesFor(repeatingCount, oneOffCount);
        RefreshAddSession();
    }

    private static IReadOnlyList<string> ScheduleNotesFor(int repeatingCount, int oneOffCount)
    {
        if (repeatingCount == 0)
        {
            return [];
        }

        var notes = new List<string>
        {
            "This task repeats — complete each occurrence from the calendar or its session view.",
        };
        if (oneOffCount > 0)
        {
            notes.Add("One-off sessions can't be marked Done while a repeating schedule remains.");
            notes.Add(repeatingCount == 1
                ? "Session numbers count one-off sessions only; the repeating schedule has no number."
                : "Session numbers count one-off sessions only; repeating schedules have no number.");
        }

        return notes;
    }

    private void RefreshAddSession()
    {
        CanAddSession = IsCreateMode || !IsCompleted;
        AddSessionBlockedNote = CanAddSession
            ? null
            : "Task complete — reopen it to schedule more sessions.";
    }

    partial void OnIsCompletedChanged(bool value) => RefreshAddSession();
}
