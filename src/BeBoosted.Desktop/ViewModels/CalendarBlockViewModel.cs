using System.Globalization;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// One rendered block on the timeline: a task session, a read-only external synced
/// event, or a pending draft proposal (lime wash, dashed) that is movable, resizable,
/// removable, and individually approvable before it ever touches the approved calendar.
/// </summary>
public sealed partial class CalendarBlockViewModel : ViewModelBase
{
    private readonly CalendarViewModel _owner;
    private readonly CalendarBlock? _block;
    private readonly ProposedBlock? _proposal;

    private CalendarBlockViewModel(
        CalendarViewModel owner,
        string title,
        DateOnly date,
        CalendarBlock? block,
        ProposedBlock? proposal,
        bool isConflicted,
        bool isDone,
        bool needsOutcome)
    {
        _owner = owner;
        _block = block;
        _proposal = proposal;
        Title = title;
        Date = date;
        IsConflicted = isConflicted;
        IsDone = isDone;
        NeedsOutcome = needsOutcome;
    }

    public static CalendarBlockViewModel ForBlock(
        CalendarViewModel owner,
        BlockOccurrence occurrence,
        string title,
        bool isConflicted,
        bool isDone,
        bool needsOutcome)
        => new(
            owner, title, occurrence.Date, occurrence.Block, null, isConflicted, isDone,
            needsOutcome);

    public static CalendarBlockViewModel ForProposal(
        CalendarViewModel owner,
        ProposedBlock proposal,
        string title,
        bool isConflicted)
        => new(owner, title, proposal.Date, null, proposal, isConflicted, isDone: false, needsOutcome: false);

    public string Title { get; }

    public DateOnly Date { get; }

    public bool IsConflicted { get; }

    public bool IsDone { get; }

    public bool NeedsOutcome { get; }

    public CalendarBlockId Id => _block?.Id ?? _proposal!.Id;

    public CalendarBlock Block => _block
        ?? throw new InvalidOperationException("This view model wraps a proposal.");

    public bool IsProposal => _proposal is not null;

    public WhyEvidence? Why => _proposal?.Why;

    public string? SessionLabel => _proposal?.SessionLabel;

    public bool HasSessionLabel => _proposal?.SessionLabel is not null;

    public TimeOnly StartTime => _block?.StartTime ?? _proposal!.StartTime;

    public TimeOnly EndTime => _block?.EndTime ?? _proposal!.EndTime;

    public bool IsExternal => _block?.IsExternal == true;

    public bool IsRecurring => _block?.Recurrence is not null;

    private bool IsLocalSession => _block is { Kind: BlockKind.TaskSession, IsExternal: false };

    /// <summary>Local task sessions carry the project-accent edge.</summary>
    public bool IsSession => IsLocalSession;

    // ---- Capabilities by kind and provider ----
    // Local task sessions are fully editable; external (imported/synced) events are
    // never mutated by BeBoosted; proposals keep their move/resize/remove behavior
    // but open no editor.

    /// <summary>A normal click opens the Task editor.</summary>
    public bool CanEdit => IsLocalSession;

    public bool CanMove => IsProposal || IsLocalSession;

    public bool CanResize => IsProposal || IsLocalSession;

    public bool CanDelete => IsProposal || IsLocalSession;

    /// <summary>External events show the lock icon and reject every mutation.</summary>
    public bool IsLocked => IsExternal;

    /// <summary>
    /// A settled one-off session — Done, "Needs more time", or "Didn't happen". A
    /// proposal is not a block yet and never reports an outcome. Mirrors
    /// DailyRowViewModel.HasRecordedOutcome.
    /// </summary>
    public bool HasRecordedOutcome => _block is { Outcome: not BlockOutcome.None };

    /// <summary>
    /// The checkbox. It deliberately survives completion: the previous gate excluded
    /// IsDone, so using the control removed it and a done one-off session could not be
    /// reopened from this surface at all. A session settled by another outcome keeps no
    /// checkbox — completing that work again goes through the task's own row. Both
    /// halves are DailyRowViewModel.ShowSessionCheck's rule.
    /// </summary>
    public bool ShowCompletionControl
        => IsLocalSession && !IsRecurring && (IsDone || !HasRecordedOutcome);

    /// <summary>
    /// The quiet side action holding "Needs more time", "Didn't happen" and "Remove
    /// from calendar" — the outcomes that are not a simple finish. Mirrors
    /// DailyRowViewModel.ShowSessionOutcomeAction.
    /// </summary>
    public bool ShowOutcomeAction
        => IsLocalSession && !IsRecurring && !IsDone && !HasRecordedOutcome;

    /// <summary>
    /// The single-click done circle for repeating sessions (completes one occurrence)
    /// — never for one-off sessions (they get <see cref="ShowCompletionControl"/>'s
    /// checkbox instead), proposals, or locked external events.
    /// </summary>
    public bool ShowOccurrenceCompletionControl => IsLocalSession && IsRecurring;

    /// <summary>
    /// The one-off checkbox's name. "Complete", not "Mark … done", so the control names
    /// its two directions with one verb shape each — it already says "Reopen" for the
    /// undo, and the project row's circle says "Complete" for the same act. One verb
    /// across every surface that offers this control (Today's list and the Inbox drawer
    /// word it identically).
    /// </summary>
    public string CompletionControlName => IsDone ? $"Reopen {Title}" : $"Complete {Title}";

    /// <summary>
    /// The occurrence circle's name, which is the checkbox's plus the day it acts on.
    /// The project row's circle states the day for exactly this reason: a repeating
    /// series renders one block per day it falls on, so a control announced as
    /// "Complete Stats HW" over one of them states the scope of the whole task rather
    /// than of the occurrence it actually ticks. Worded identically to
    /// <see cref="ProjectTaskRowViewModel.CheckControlName"/>, so the two circles that
    /// act on one occurrence say the same thing.
    /// </summary>
    public string OccurrenceCompletionControlName
        => (IsDone ? "Reopen" : "Complete") + $" {Title} on {Date:ddd d MMM}";

    public double StartMinutes => StartTime.ToTimeSpan().TotalMinutes;

    public double DurationMinutes => (EndTime - StartTime).TotalMinutes;

    public string TimeText
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var start = StartTime.ToString("h:mm", culture);
            var end = EndTime.ToString("h:mm", culture);
            if (IsProposal)
            {
                return $"{start} – {end} · Proposed";
            }

            return IsExternal
                ? $"{start} – {end} · Synced"
                : $"{start} – {end} · {TaskRowViewModel.FormatDuration(EndTime - StartTime)}";
        }
    }

    public string AccessibleName
    {
        get
        {
            var state = IsProposal ? ", proposed — not yet on your calendar"
                : IsConflicted ? ", conflict"
                : IsDone ? ", done"
                : NeedsOutcome ? ", needs an outcome"
                : IsLocked ? ", synced event — locked, BeBoosted never edits it"
                : IsRecurring ? ", repeating task"
                : string.Empty;
            return $"{Title}, {Date:MMMM d}, {StartTime:h\\:mm} to {EndTime:h\\:mm}{state}";
        }
    }

    [ObservableProperty]
    public partial decimal RemainingMinutes { get; set; } = 30;

    // ---- Outcomes (approved task blocks) ----

    [RelayCommand]
    private void RecordDone() => _owner.RecordOutcome(Id, BlockOutcome.Done, null);

    [RelayCommand]
    private void RecordNeedsMoreTime()
        => _owner.RecordOutcome(
            Id, BlockOutcome.NeedsMoreTime, TimeSpan.FromMinutes((double)Math.Max(5, RemainingMinutes)));

    [RelayCommand]
    private void RecordDidntHappen() => _owner.RecordOutcome(Id, BlockOutcome.DidntHappen, null);

    /// <summary>
    /// One click finishes, another reopens. Reopening is routed through the owner
    /// rather than clearing this session's outcome directly: when a session renders
    /// done because its parent task was completed as a whole, clearing the session
    /// alone would leave the task complete and strand an unresolved session on it.
    /// DailyListViewModel.ReopenRow carries the same reasoning.
    /// </summary>
    [RelayCommand]
    private void ToggleSessionDone()
    {
        if (IsDone)
        {
            _owner.ReopenSession(Id);
        }
        else
        {
            _owner.RecordOutcome(Id, BlockOutcome.Done, null);
        }
    }

    /// <summary>
    /// Delete dispatch by kind: proposals leave the draft, one-off sessions
    /// unschedule (their task stays open), repeating sessions route through the
    /// editor's confirmation, and external events are never mutated.
    /// </summary>
    [RelayCommand]
    private void Unschedule()
    {
        if (IsProposal)
        {
            _owner.RemoveProposalBlock(Id);
        }
        else if (IsLocalSession && !IsRecurring)
        {
            _owner.UnscheduleBlock(Id);
        }
        else if (CanDelete)
        {
            _owner.RequestDeleteBlock(Id, Date);
        }
    }

    /// <summary>Opens the Task editor for a local session.</summary>
    /// <summary>The block being edited keeps its lime halo behind the scrim (frame 3b).</summary>
    [ObservableProperty]
    public partial bool IsBeingEdited { get; internal set; }

    public void Edit()
    {
        if (CanEdit)
        {
            _owner.OpenTaskEditorForBlock(Id, Date);
        }
    }

    /// <summary>Checks this occurrence off (or reopens it) without opening the editor.</summary>
    [RelayCommand]
    private void ToggleOccurrenceDone()
    {
        if (ShowOccurrenceCompletionControl)
        {
            _owner.SetOccurrenceDone(Id, Date, !IsDone);
        }
    }

    // ---- Draft proposal actions ----

    [RelayCommand]
    private void ApproveThis() => _owner.ApproveProposalBlock(Id);

    [RelayCommand]
    private void RemoveFromDraft() => _owner.RemoveProposalBlock(Id);

    // ---- Movement (pointer + keyboard) ----

    public void MoveTo(DateOnly date, TimeOnly start)
    {
        if (IsProposal)
        {
            _owner.MoveProposalBlock(Id, date, start);
            return;
        }

        // A repeating task moves as a whole series: the time change applies to
        // every occurrence and the anchor date never silently follows one occurrence.
        _owner.MoveBlock(Id, IsRecurring ? Block.Date : date, start);
    }

    public void ResizeTo(TimeOnly end)
    {
        if (IsProposal)
        {
            _owner.ResizeProposalBlockTo(Id, end);
        }
        else
        {
            _owner.ResizeBlockTo(Id, end);
        }
    }

    public void Nudge(int minutes) => _owner.NudgeBlock(this, minutes);

    public void NudgeDays(int days)
    {
        // Changing the day of one occurrence would rebase a recurring series — never
        // do that silently. Day changes for a series go through the editor's Date.
        if (!IsRecurring)
        {
            _owner.NudgeBlockDays(this, days);
        }
    }

    public void ResizeBy(int minutes) => _owner.ResizeBlockBy(this, minutes);
}
