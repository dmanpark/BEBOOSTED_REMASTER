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
        bool needsOutcome,
        bool taskRepeats = false)
    {
        _owner = owner;
        _block = block;
        _proposal = proposal;
        Title = title;
        Date = date;
        IsConflicted = isConflicted;
        IsDone = isDone;
        NeedsOutcome = needsOutcome;
        TaskRepeats = taskRepeats;
    }

    public static CalendarBlockViewModel ForBlock(
        CalendarViewModel owner,
        BlockOccurrence occurrence,
        string title,
        bool isConflicted,
        bool isDone,
        bool needsOutcome,
        bool taskRepeats = false)
        => new(
            owner, title, occurrence.Date, occurrence.Block, null, isConflicted, isDone,
            needsOutcome, taskRepeats);

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

    /// <summary>One-off sessions record what happened through the outcome flyout.</summary>
    public bool ShowCompletionControl => IsLocalSession && !IsRecurring && !IsDone;

    /// <summary>This one-off's Task also has a repeating session somewhere.</summary>
    public bool TaskRepeats { get; }

    /// <summary>
    /// Done is a whole-Task statement: while any sibling session repeats, the Task
    /// completes per occurrence, so this one-off may not record Done. (Needs more
    /// time and Didn't happen stay valid — they never complete the Task.)
    /// </summary>
    public bool CanRecordDone => !TaskRepeats;

    public bool ShowRepeatingCompletionNote => TaskRepeats;

    public string RepeatingCompletionNote
        => "This task repeats — complete each repeating occurrence separately.";

    /// <summary>
    /// The single-click done circle for repeating sessions (completes one occurrence)
    /// — never for one-off sessions (they keep the multi-outcome flyout), proposals,
    /// or locked external events.
    /// </summary>
    public bool ShowOccurrenceCompletionControl => IsLocalSession && IsRecurring;

    public string CompletionControlName => IsDone ? $"Reopen {Title}" : $"Mark {Title} done";

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

    [RelayCommand(CanExecute = nameof(CanRecordDone))]
    private void RecordDone() => _owner.RecordOutcome(Id, BlockOutcome.Done, null);

    [RelayCommand]
    private void RecordNeedsMoreTime()
        => _owner.RecordOutcome(
            Id, BlockOutcome.NeedsMoreTime, TimeSpan.FromMinutes((double)Math.Max(5, RemainingMinutes)));

    [RelayCommand]
    private void RecordDidntHappen() => _owner.RecordOutcome(Id, BlockOutcome.DidntHappen, null);

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
