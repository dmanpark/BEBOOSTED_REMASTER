using System.Globalization;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>Which single block the session editor is scoped to.</summary>
public enum SessionEditorMode
{
    OneOff,
    Repeating,
    New,
}

/// <summary>
/// The session-scoped editor: one block's schedule, and (for a repeating
/// schedule) the opened occurrence's completion. Task fields are read-only
/// context here, and task deletion never appears — the whole-task editor owns
/// both. Cancel and Escape never persist anything.
/// </summary>
public sealed partial class SessionEditorViewModel : ViewModelBase
{
    private readonly CalendarViewModel _owner;
    private readonly int _totalBlockCount;
    private readonly int _position;
    private readonly int _oneOffCount;
    // The persisted block as opened: removal copy names what will actually be
    // removed, never unsaved draft fields.
    private readonly DateOnly? _persistedDate;
    private readonly TimeOnly? _persistedStart;
    private readonly TimeOnly? _persistedEnd;
    private ScheduleSnapshot _snapshot = new(null, null, null, false, []);
    private bool _savedOccurrenceCompleted;
    private string _savedSessionTitle = string.Empty;
    private Action? _pendingConfirmedAction;

    internal SessionEditorViewModel(
        CalendarViewModel owner,
        SessionEditorMode mode,
        TaskItem task,
        string taskContext,
        CalendarBlock? session,
        DateOnly? occurrenceDate,
        bool isOccurrenceCompleted,
        IReadOnlyList<CalendarBlock> allSessions)
    {
        _owner = owner;
        Mode = mode;
        TaskId = task.Id;
        TaskTitle = task.Title;
        TaskContext = taskContext;
        SessionId = session?.Id;
        OccurrenceDate = occurrenceDate;
        _totalBlockCount = allSessions.Count;
        if (session is not null)
        {
            Schedule.Load(session);
            _persistedDate = session.Date;
            _persistedStart = session.StartTime;
            _persistedEnd = session.EndTime;
            SessionTitle = session.Title ?? string.Empty;
        }

        IsOccurrenceCompleted = isOccurrenceCompleted;

        if (mode == SessionEditorMode.OneOff)
        {
            (_position, _oneOffCount) = SessionListBuilder.PositionOf(allSessions, session!.Id);
            ScopeLabel = string.Create(
                CultureInfo.InvariantCulture, $"THIS SESSION · {_position} OF {_oneOffCount}");
            if (session.Outcome != BlockOutcome.None)
            {
                ResolvedNote = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Resolved: {OutcomeText(session.Outcome)} · {session.Date:MMM d}");
            }

            var repeatingSiblings = allSessions.Count(s => s.Recurrence is not null);
            if (repeatingSiblings > 0)
            {
                NumberingNote = repeatingSiblings == 1
                    ? "Session numbers count one-off sessions only; the repeating schedule has no number."
                    : "Session numbers count one-off sessions only; repeating schedules have no number.";
            }
        }
        else
        {
            ScopeLabel = mode == SessionEditorMode.Repeating ? "REPEATING SCHEDULE" : "NEW SESSION";
        }

        // Unticking Repeats converts to a one-off: the occurrence section hides,
        // its staged value is discarded on save, and the date field reveals
        // prefilled with the opened occurrence.
        Schedule.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScheduleFieldsViewModel.HasEndBeforeStart))
            {
                OnPropertyChanged(nameof(CanSave));
            }

            if (e.PropertyName == nameof(ScheduleFieldsViewModel.RepeatsWeekly))
            {
                if (Mode == SessionEditorMode.Repeating && !Schedule.RepeatsWeekly)
                {
                    // The prospective one-off date is the opened occurrence — a
                    // temporary conversion value, never the series anchor.
                    if (OccurrenceDate is { } occurrence)
                    {
                        Schedule.Date = new DateTimeOffset(occurrence.ToDateTime(TimeOnly.MinValue));
                    }

                    // The occurrence section is gone with its checkbox: the staged
                    // value is discarded now, so rechecking cannot resurrect it.
                    IsOccurrenceCompleted = false;
                }
                else if (Mode == SessionEditorMode.Repeating && _persistedDate is { } anchor)
                {
                    // Rechecking Repeats returns to the series: the persisted
                    // anchor comes back, never a silent rebase to the occurrence.
                    Schedule.Date = new DateTimeOffset(anchor.ToDateTime(TimeOnly.MinValue));
                }

                OnPropertyChanged(nameof(ShowOccurrenceSection));
                OnPropertyChanged(nameof(ShowDateField));
            }
        };

        MarkSaved();
    }

    internal TaskId TaskId { get; }

    internal CalendarBlockId? SessionId { get; }

    /// <summary>The occurrence the completion checkbox applies to (repeating mode).</summary>
    internal DateOnly? OccurrenceDate { get; }

    public SessionEditorMode Mode { get; }

    /// <summary>"THIS SESSION · 2 OF 3" · "REPEATING SCHEDULE" · "NEW SESSION".</summary>
    public string ScopeLabel { get; }

    /// <summary>Card automation name, word-spelled per the spec's Accessibility section.</summary>
    public string AccessibleName => Mode switch
    {
        SessionEditorMode.OneOff => string.Create(
            CultureInfo.InvariantCulture, $"Session {_position} of {_oneOffCount} — {TaskTitle}"),
        SessionEditorMode.Repeating => $"Repeating schedule — {TaskTitle}",
        _ => $"New session — {TaskTitle}",
    };

    /// <summary>The parent task's title — read-only context, never editable here.</summary>
    public string TaskTitle { get; }

    /// <summary>
    /// This sitting's own name ("Jane Eyre 1-10"). Empty keeps the Task's title,
    /// which is what the placeholder shows.
    /// </summary>
    [ObservableProperty]
    public partial string SessionTitle { get; set; } = string.Empty;

    public string TitlePlaceholder => TaskTitle;

    /// <summary>"DECA · due Sun, Aug 16" — project and deadline context, possibly empty.</summary>
    public string TaskContext { get; }

    public bool ShowEditWholeTask => Mode != SessionEditorMode.New;

    /// <summary>Mixed-task honesty: numbering counts one-off sessions only.</summary>
    public string? NumberingNote { get; }

    /// <summary>"Resolved: Didn't happen · Aug 18" for explicitly opened history.</summary>
    public string? ResolvedNote { get; }

    public ScheduleFieldsViewModel Schedule { get; } = new();

    /// <summary>Repeating mode hides the anchor date; unticking Repeats reveals it.</summary>
    public bool ShowDateField => Mode != SessionEditorMode.Repeating || !Schedule.RepeatsWeekly;

    public bool ShowOccurrenceSection => Mode == SessionEditorMode.Repeating && Schedule.RepeatsWeekly;

    public string OccurrenceSectionLabel => OccurrenceDate is { } occurrence
        ? "THIS OCCURRENCE · "
            + occurrence.ToString("ddd, MMM d", CultureInfo.InvariantCulture).ToUpperInvariant()
        : string.Empty;

    [ObservableProperty]
    public partial bool IsOccurrenceCompleted { get; set; }

    public string OccurrenceCheckboxText => "Mark this occurrence complete";

    public string OccurrenceNote => OccurrenceDate is { } occurrence
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"Only {occurrence:ddd, MMM d}. Other occurrences aren't affected.")
        : string.Empty;

    public string SeriesNote =>
        "Time and weekday changes apply to every occurrence of this schedule.";

    public string SaveButtonText => Mode switch
    {
        SessionEditorMode.Repeating => "Save schedule",
        SessionEditorMode.New => "Add session",
        _ => "Save session",
    };

    public string? RemoveButtonText => Mode switch
    {
        SessionEditorMode.OneOff => "Remove this session",
        SessionEditorMode.Repeating => "Remove schedule",
        _ => null,
    };

    [ObservableProperty]
    public partial bool IsStale { get; internal set; }

    /// <summary>Save is held while stale, or while END sits at/before START (frame 4n).</summary>
    public bool CanSave => !IsStale && !Schedule.HasEndBeforeStart;

    public string StaleNotice =>
        "This session no longer exists — it was removed elsewhere. Cancel to go back.";

    [ObservableProperty]
    public partial ConfirmationPrompt? Confirmation { get; private set; }

    [ObservableProperty]
    public partial GatePrompt? Gate { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; internal set; }

    /// <summary>The body dims and goes inert while a confirmation or gate is open.</summary>
    public bool HasActivePrompt => Confirmation is not null || Gate is not null;

    /// <summary>Stale fields are inert too — Cancel is the emphasized exit (frame 4m left).</summary>
    public bool BodyEnabled => !IsStale && !HasActivePrompt;

    partial void OnConfirmationChanged(ConfirmationPrompt? value)
    {
        OnPropertyChanged(nameof(HasActivePrompt));
        OnPropertyChanged(nameof(BodyEnabled));
    }

    partial void OnGateChanged(GatePrompt? value)
    {
        OnPropertyChanged(nameof(HasActivePrompt));
        OnPropertyChanged(nameof(BodyEnabled));
    }

    partial void OnIsStaleChanged(bool value)
    {
        OnPropertyChanged(nameof(BodyEnabled));
        OnPropertyChanged(nameof(CanSave));
    }

    internal bool IsDirty
        => Schedule.IsDirtyAgainst(_snapshot)
            || IsOccurrenceCompleted != _savedOccurrenceCompleted
            || SessionTitle != _savedSessionTitle;

    /// <summary>The dirty snapshot advances to the just-persisted values.</summary>
    internal void MarkSaved()
    {
        _snapshot = Schedule.Capture();
        _savedOccurrenceCompleted = IsOccurrenceCompleted;
        _savedSessionTitle = SessionTitle;
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
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            // Frame 4n: the END-pinned message is authoritative; a direct
            // command execution performs nothing at all.
            return;
        }

        _owner.SaveSession(this);
    }

    [RelayCommand]
    private void Cancel() => _owner.CancelSession(this);

    /// <summary>
    /// Promotes to the whole-task editor — the one path up a scope level, gated
    /// on a dirty session draft. Promotion has no return leg: closing the
    /// whole-task editor restores focus to the original invoker.
    /// </summary>
    [RelayCommand]
    private void EditWholeTask()
    {
        if (Mode == SessionEditorMode.New)
        {
            return;
        }

        if (!IsDirty)
        {
            _owner.PromoteToWholeTask(this);
            return;
        }

        Gate = new GatePrompt(
            "You have unsaved session changes.",
            Mode == SessionEditorMode.Repeating ? "Save schedule and continue" : "Save session and continue");
    }

    [RelayCommand]
    private void GateSaveAndContinue()
    {
        if (!CanSave)
        {
            // The same frame-4n restriction as Save itself: the gate holds until
            // the pinned field error is corrected (its button is disabled too).
            return;
        }

        Gate = null;
        if (_owner.TrySaveSession(this, out _))   // persists + announces once; no return/close
        {
            _owner.PromoteToWholeTask(this);
        }

        // Failure: Error/IsStale is set and the session editor stays put.
    }

    /// <summary>The discarded draft vanishes with the editor — promotion replaces it.</summary>
    [RelayCommand]
    private void GateDiscardAndContinue()
    {
        Gate = null;
        _owner.PromoteToWholeTask(this);
    }

    [RelayCommand]
    private void GateKeepEditing() => Gate = null;

    [RelayCommand]
    private void RequestRemove()
    {
        if (Mode == SessionEditorMode.New)
        {
            return;
        }

        Confirmation = BuildRemoveConfirmation();
        _pendingConfirmedAction = () => _owner.RemoveSessionFromSessionEditor(this);
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

    private ConfirmationPrompt BuildRemoveConfirmation()
    {
        if (Mode == SessionEditorMode.Repeating)
        {
            return new ConfirmationPrompt(
                "Remove the repeating schedule? Every occurrence and its completion history "
                + "go with it. The task stays.",
                "Remove schedule",
                IsTaskDeletion: false);
        }

        var date = _persistedDate is { } d
            ? d.ToString("ddd, MMM d", CultureInfo.InvariantCulture)
            : string.Empty;
        var times = _persistedStart is { } start && _persistedEnd is { } end
            ? CopyTimeRange(start, end)
            : string.Empty;
        var consequence = WholeTaskEditorViewModel.KeepConsequence(_totalBlockCount - 1);
        return new ConfirmationPrompt(
            $"Remove this session — {date} · {times}? {consequence}",
            "Remove session",
            IsTaskDeletion: false);
    }

    /// <summary>Confirmation copy uses the tight "9:00–10:00 AM" range (frames 4i–4k).</summary>
    internal static string CopyTimeRange(TimeOnly start, TimeOnly end)
    {
        var sameMeridiem = start.Hour < 12 == end.Hour < 12;
        var startText = start.ToString(sameMeridiem ? "h:mm" : "h:mm tt", CultureInfo.InvariantCulture);
        return $"{startText}–{end.ToString("h:mm tt", CultureInfo.InvariantCulture)}";
    }

    private static string OutcomeText(BlockOutcome outcome) => outcome switch
    {
        BlockOutcome.Done => "Done",
        BlockOutcome.NeedsMoreTime => "Needs more time",
        _ => "Didn't happen",
    };
}
