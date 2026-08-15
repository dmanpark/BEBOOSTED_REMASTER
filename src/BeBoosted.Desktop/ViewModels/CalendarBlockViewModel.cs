using System.Globalization;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// One rendered block on the timeline: an approved/fixed calendar block, or a pending
/// draft proposal (lime wash, dashed) that is movable, resizable, removable, and
/// individually approvable before it ever touches the approved calendar.
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
        => new(owner, title, occurrence.Date, occurrence.Block, null, isConflicted, isDone, needsOutcome);

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

    public bool IsFixed => _block?.Kind == BlockKind.FixedCommitment;

    public bool IsTaskBlock => _block?.Kind == BlockKind.TaskBlock;

    public bool IsExternal => _block?.IsExternal == true;

    public bool IsRecurring => _block?.Recurrence is not null;

    private bool IsLocalFixedCommitment => IsFixed && !IsExternal;

    // ---- Capabilities by kind and provider ----
    // Local fixed commitments are fully editable; external (imported/synced)
    // commitments are never mutated by BeBoosted; task blocks and proposals
    // keep their move/resize/delete behavior but open no editor.

    /// <summary>A normal click opens the commitment editor.</summary>
    public bool CanEdit => IsLocalFixedCommitment;

    public bool CanMove => IsTaskBlock || IsProposal || IsLocalFixedCommitment;

    public bool CanResize => IsTaskBlock || IsProposal || IsLocalFixedCommitment;

    public bool CanDelete => IsTaskBlock || IsProposal || IsLocalFixedCommitment;

    /// <summary>External commitments show the lock icon and reject every mutation.</summary>
    public bool IsLocked => IsFixed && IsExternal;

    public bool ShowCompletionControl => IsTaskBlock && !IsDone;

    /// <summary>
    /// The single-click done circle for local fixed commitments — never for task
    /// blocks (they keep the multi-outcome flyout), proposals, or locked externals.
    /// </summary>
    public bool ShowCommitmentCompletionControl => IsLocalFixedCommitment;

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

            return IsFixed
                ? $"{start} – {end} · Fixed"
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
                : IsLocked ? ", external commitment — locked, BeBoosted never edits it"
                : IsFixed ? ", fixed commitment"
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
    /// Delete dispatch by kind: proposals leave the draft, task blocks unschedule
    /// (reopening their task), local commitments route through the editor's
    /// confirmation, and external commitments are never mutated.
    /// </summary>
    [RelayCommand]
    private void Unschedule()
    {
        if (IsProposal)
        {
            _owner.RemoveProposalBlock(Id);
        }
        else if (IsTaskBlock)
        {
            _owner.UnscheduleBlock(Id);
        }
        else if (CanDelete)
        {
            _owner.RequestDeleteCommitment(Id);
        }
    }

    /// <summary>Opens the commitment editor for a local fixed commitment.</summary>
    public void Edit()
    {
        if (CanEdit)
        {
            _owner.OpenCommitmentEditorFor(Id, Date);
        }
    }

    /// <summary>Checks this occurrence off (or reopens it) without opening the editor.</summary>
    [RelayCommand]
    private void ToggleCommitmentDone()
    {
        if (ShowCommitmentCompletionControl)
        {
            _owner.SetCommitmentOccurrenceDone(Id, Date, !IsDone);
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

        // A recurring commitment moves as a whole series: the time change applies to
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
