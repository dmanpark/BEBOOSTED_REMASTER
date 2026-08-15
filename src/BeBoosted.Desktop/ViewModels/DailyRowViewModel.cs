using System.Globalization;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

public enum DailyRowKind
{
    /// <summary>A local or external fixed commitment occurrence (obligation, never ranked).</summary>
    FixedCommitment = 0,

    /// <summary>An approved task block (open, needs-outcome, or done).</summary>
    TaskBlock = 1,

    /// <summary>A pending plan-draft proposal.</summary>
    Proposal = 2,

    /// <summary>A bare task: unscheduled open work, or a directly completed task.</summary>
    Task = 3,
}

/// <summary>
/// One row of the Daily priority-first list. Time is metadata here, never a layout axis.
/// Priorities come from Priority Sort ranks; fixed commitments never fabricate one.
/// </summary>
public sealed partial class DailyRowViewModel : ViewModelBase
{
    private readonly DailyListViewModel _owner;

    private DailyRowViewModel(
        DailyListViewModel owner,
        DailyRowKind kind,
        string title,
        DateOnly date,
        string? projectName,
        PriorityRank? rank)
    {
        _owner = owner;
        Kind = kind;
        Title = title;
        Date = date;
        ProjectName = projectName;
        Rank = rank;
    }

    internal static DailyRowViewModel ForOccurrence(
        DailyListViewModel owner,
        BlockOccurrence occurrence,
        string title,
        string? projectName,
        PriorityRank? rank,
        TaskItem? task,
        bool isConflicted,
        bool isDone,
        bool needsOutcome)
        => new(
            owner,
            occurrence.Block.Kind == BlockKind.FixedCommitment
                ? DailyRowKind.FixedCommitment
                : DailyRowKind.TaskBlock,
            title,
            occurrence.Date,
            projectName,
            occurrence.Block.Kind == BlockKind.FixedCommitment ? null : rank)
        {
            BlockId = occurrence.Block.Id,
            TaskId = occurrence.Block.TaskId,
            Task = task,
            StartTime = occurrence.StartTime,
            EndTime = occurrence.EndTime,
            IsExternal = occurrence.Block.IsExternal,
            IsRecurring = occurrence.Block.Recurrence is not null,
            IsConflicted = isConflicted,
            IsDone = isDone,
            NeedsOutcome = needsOutcome,
        };

    internal static DailyRowViewModel ForProposal(
        DailyListViewModel owner,
        ProposedBlock proposal,
        string title,
        string? projectName,
        PriorityRank? rank,
        TaskItem? task,
        bool isConflicted)
        => new(owner, DailyRowKind.Proposal, title, proposal.Date, projectName, rank)
        {
            BlockId = proposal.Id,
            TaskId = proposal.TaskId,
            Task = task,
            StartTime = proposal.StartTime,
            EndTime = proposal.EndTime,
            IsConflicted = isConflicted,
            Why = proposal.Why,
            SessionLabel = proposal.SessionLabel,
        };

    internal static DailyRowViewModel ForTask(
        DailyListViewModel owner,
        TaskItem task,
        DateOnly date,
        string? projectName,
        PriorityRank? rank,
        TaskRowViewModel taskRow,
        bool isDone,
        bool needsReview = false)
        => new(owner, DailyRowKind.Task, task.Title, date, projectName, rank)
        {
            TaskId = task.Id,
            Task = task,
            TaskRow = taskRow,
            IsDone = isDone,
            CompletedAt = task.CompletedAt,
            NeedsReview = needsReview,
        };

    public DailyRowKind Kind { get; }

    public string Title { get; }

    public DateOnly Date { get; }

    public string? ProjectName { get; }

    public bool HasProject => ProjectName is not null;

    public PriorityRank? Rank { get; }

    public TaskId? TaskId { get; private init; }

    public CalendarBlockId? BlockId { get; private init; }

    internal TaskItem? Task { get; private init; }

    public TimeOnly? StartTime { get; private init; }

    public TimeOnly? EndTime { get; private init; }

    public DateTimeOffset? CompletedAt { get; private init; }

    public bool IsExternal { get; private init; }

    public bool IsRecurring { get; private init; }

    public bool IsConflicted { get; private init; }

    public bool IsDone { get; private init; }

    public bool NeedsOutcome { get; private init; }

    public WhyEvidence? Why { get; private init; }

    public string? SessionLabel { get; private init; }

    public bool HasSessionLabel => SessionLabel is not null;

    /// <summary>The shared task-edit flyout DataContext (task-backed rows only).</summary>
    public TaskRowViewModel? TaskRow { get; internal set; }

    /// <summary>Schedule / Change-time flyout state; attached by the list after construction.</summary>
    public ScheduleFlyoutViewModel? ScheduleEditor { get; internal set; }

    public bool IsAiOrigin => Task?.Origin == TaskOrigin.Ai;

    public bool NeedsReview { get; internal init; }

    // ---- Priority marker ----

    public string PriorityText => Kind == DailyRowKind.FixedCommitment
        ? string.Empty
        : Rank is { } rank ? TierLabel(rank.Tier) : "–";

    public string? PriorityAccessibleText => Kind == DailyRowKind.FixedCommitment
        ? null
        : Rank is { } rank ? TierAccessibleLabel(rank.Tier) : "Not ranked for this day.";

    public bool IsP1 => Rank?.Tier == PlanningTier.ProtectNow;

    public bool IsP2 => Rank?.Tier == PlanningTier.AdvanceNext;

    public bool IsP3 => Rank?.Tier == PlanningTier.CanWait;

    internal static string TierLabel(PlanningTier tier) => tier switch
    {
        PlanningTier.ProtectNow => "P1",
        PlanningTier.AdvanceNext => "P2",
        _ => "P3",
    };

    internal static string TierAccessibleLabel(PlanningTier tier) => tier switch
    {
        PlanningTier.ProtectNow => "Priority 1 — Protect now",
        PlanningTier.AdvanceNext => "Priority 2 — Advance next",
        _ => "Priority 3 — Can wait",
    };

    // ---- Status ----

    public string StatusText => Kind switch
    {
        DailyRowKind.FixedCommitment => "FIXED",
        DailyRowKind.TaskBlock => "FLEX",
        DailyRowKind.Proposal => "PROPOSED",
        _ => string.Empty,
    };

    public bool HasStatus => StatusText.Length > 0;

    public bool IsProposal => Kind == DailyRowKind.Proposal;

    /// <summary>The unscheduled row metadata line: project · deadline · duration.</summary>
    public string MetaText => TaskRow?.MetaText ?? string.Empty;

    public bool HasMeta => MetaText.Length > 0;

    // ---- Time (metadata, right-aligned) ----

    public string TimeText => Kind switch
    {
        DailyRowKind.FixedCommitment when StartTime is { } start && EndTime is { } end
            => FormatRange(start, end),
        DailyRowKind.TaskBlock or DailyRowKind.Proposal when StartTime is { } start
            => FormatTime(start),
        _ => string.Empty,
    };

    internal static string FormatTime(TimeOnly time)
        => time.ToString("h:mm tt", CultureInfo.CurrentCulture);

    internal static string FormatRange(TimeOnly start, TimeOnly end)
    {
        var culture = CultureInfo.CurrentCulture;
        var startSuffix = start.ToString("tt", culture);
        var endSuffix = end.ToString("tt", culture);
        return startSuffix == endSuffix
            ? $"{start.ToString("h:mm", culture)} – {end.ToString("h:mm", culture)} {endSuffix}"
            : $"{start.ToString("h:mm tt", culture)} – {end.ToString("h:mm tt", culture)}";
    }

    // ---- Capabilities ----

    public bool ShowCommitmentCheck => Kind == DailyRowKind.FixedCommitment && !IsExternal;

    public bool ShowTaskCheck => Kind == DailyRowKind.Task;

    public bool ShowOutcomeControl => Kind == DailyRowKind.TaskBlock && !IsDone;

    /// <summary>A done task block keeps its recorded outcome — no invented binary reopen.</summary>
    public bool ShowDoneBlockMarker => Kind == DailyRowKind.TaskBlock && IsDone;

    public bool CanReopen => IsDone
        && (Kind == DailyRowKind.Task || (Kind == DailyRowKind.FixedCommitment && !IsExternal));

    public bool CanEditCommitment => Kind == DailyRowKind.FixedCommitment && !IsExternal;

    public bool ShowScheduleAction => Kind == DailyRowKind.Task && !IsDone;

    public bool ShowChangeTimeAction => Kind == DailyRowKind.TaskBlock && !IsDone && !IsExternal;

    public bool ShowUnscheduleAction => ShowChangeTimeAction;

    public bool ShowTaskEditAction => TaskRow is not null && !IsDone;

    public bool ShowProposalActions => Kind == DailyRowKind.Proposal;

    public bool IsLocked => Kind == DailyRowKind.FixedCommitment && IsExternal;

    // ---- Accessible names ----

    public string CompletionControlName => IsDone ? $"Reopen {Title}" : $"Mark {Title} done";

    public string OutcomeControlName => $"Record outcome for {Title}";

    public string ScheduleActionName => $"Schedule {Title}";

    public string ChangeTimeActionName => $"Change time for {Title}";

    public string AccessibleName
    {
        get
        {
            var parts = new List<string>(6) { Title, Date.ToString("MMMM d", CultureInfo.CurrentCulture) };
            if (StartTime is { } start && EndTime is { } end)
            {
                parts.Add(FormatRange(start, end));
            }

            parts.Add(Kind switch
            {
                DailyRowKind.FixedCommitment when IsExternal => "External commitment — locked",
                DailyRowKind.FixedCommitment => "fixed commitment",
                DailyRowKind.TaskBlock => "scheduled block",
                DailyRowKind.Proposal => "proposed — not yet on your calendar",
                _ => IsDone ? "completed task" : "unscheduled task",
            });
            if (IsDone)
            {
                parts.Add("done");
            }
            else if (NeedsOutcome)
            {
                parts.Add("needs an outcome");
            }

            if (IsConflicted)
            {
                parts.Add("conflict");
            }

            if (PriorityAccessibleText is { } priority)
            {
                parts.Add(priority);
            }

            return string.Join(", ", parts);
        }
    }

    // ---- Commands (all delegate to the list, which guarantees one refresh) ----

    /// <summary>The row's checkbox: complete/reopen a task, or check off a commitment occurrence.</summary>
    [RelayCommand]
    private void ToggleDone()
    {
        if (Kind == DailyRowKind.FixedCommitment)
        {
            _owner.ToggleCommitmentDone(this);
        }
        else if (Kind == DailyRowKind.Task)
        {
            if (IsDone)
            {
                _owner.ReopenRow(this);
            }
            else
            {
                _owner.CompleteTask(this);
            }
        }
    }

    [RelayCommand]
    private void Reopen() => _owner.ReopenRow(this);

    [RelayCommand]
    private void EditCommitment() => _owner.EditCommitment(this);

    [RelayCommand]
    private void Unschedule() => _owner.Unschedule(this);

    [ObservableProperty]
    public partial decimal RemainingMinutes { get; set; } = 30;

    [RelayCommand]
    private void RecordDone() => _owner.RecordOutcome(this, BlockOutcome.Done, null);

    [RelayCommand]
    private void RecordNeedsMoreTime()
        => _owner.RecordOutcome(
            this, BlockOutcome.NeedsMoreTime, TimeSpan.FromMinutes((double)Math.Max(5, RemainingMinutes)));

    [RelayCommand]
    private void RecordDidntHappen() => _owner.RecordOutcome(this, BlockOutcome.DidntHappen, null);

    [RelayCommand]
    private void ApproveProposal() => _owner.ApproveProposal(this);

    [RelayCommand]
    private void RemoveProposal() => _owner.RemoveProposal(this);
}
