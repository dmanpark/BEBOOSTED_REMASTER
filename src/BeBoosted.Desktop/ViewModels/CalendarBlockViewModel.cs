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

    /// <summary>Task blocks and proposals are draggable/resizable; fixed commitments are locked.</summary>
    public bool IsInteractive => IsTaskBlock || IsProposal;

    public bool ShowCompletionControl => IsTaskBlock && !IsDone;

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

    /// <summary>Delete: unschedules an approved block, or removes a proposal from the draft.</summary>
    [RelayCommand]
    private void Unschedule()
    {
        if (IsProposal)
        {
            _owner.RemoveProposalBlock(Id);
        }
        else
        {
            _owner.UnscheduleBlock(Id);
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
        }
        else
        {
            _owner.MoveBlock(Id, date, start);
        }
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

    public void NudgeDays(int days) => _owner.NudgeBlockDays(this, days);

    public void ResizeBy(int minutes) => _owner.ResizeBlockBy(this, minutes);
}
