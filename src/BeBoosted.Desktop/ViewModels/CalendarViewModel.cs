using System.Collections.ObjectModel;
using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Scheduling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    private readonly bool _initialized;

    private PlanningProposal? _activeDraft;
    private IReadOnlyList<UnplacedTask>? _lastUnplaced;
    private readonly List<(PlanningProposalId ProposalId, IReadOnlyList<CalendarBlockId> BlockIds)> _approvalUndoStack = [];

    public CalendarViewModel(
        AppSettings settings,
        IClock clock,
        CalendarService calendar,
        ITaskRepository tasks,
        PlanningService planning)
    {
        _settings = settings;
        _clock = clock;
        _calendar = calendar;
        _tasks = tasks;
        _planning = planning;
        VisibleDate = clock.Today;
        ViewKind = settings.GetLastCalendarView();
        _initialized = true;
        Reload();
    }

    /// <summary>Raised when blocks changed in a way that can affect the Inbox queue.</summary>
    public event Action? DataChanged;

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

    /// <summary>Quiet capacity summary (Today) or the ISO week number (Week).</summary>
    public string HeaderMeta
    {
        get
        {
            if (ViewKind == CalendarViewKind.Week)
            {
                var (monday, _) = WeekRange(VisibleDate);
                return $"Week {ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue))}";
            }

            var day = Days.FirstOrDefault();
            if (day is null || day.Blocks.Count == 0)
            {
                return string.Empty;
            }

            var planned = TimeSpan.FromMinutes(day.Blocks
                .Where(b => b.IsTaskBlock && !b.IsDone)
                .Sum(b => b.DurationMinutes));
            return planned > TimeSpan.Zero
                ? $"{TaskRowViewModel.FormatDuration(planned)} planned"
                : string.Empty;
        }
    }

    // ---- Commitment editor (flyout) state ----

    [ObservableProperty]
    public partial string CommitmentTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? CommitmentDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? CommitmentStart { get; set; }

    [ObservableProperty]
    public partial TimeSpan? CommitmentEnd { get; set; }

    [ObservableProperty]
    public partial bool CommitmentRepeatsWeekly { get; set; }

    public ObservableCollection<DayToggleViewModel> CommitmentDays { get; } =
    [
        new(DayOfWeek.Monday), new(DayOfWeek.Tuesday), new(DayOfWeek.Wednesday),
        new(DayOfWeek.Thursday), new(DayOfWeek.Friday), new(DayOfWeek.Saturday), new(DayOfWeek.Sunday),
    ];

    [ObservableProperty]
    public partial string? CommitmentError { get; private set; }

    [RelayCommand]
    private void PrepareCommitmentEditor()
    {
        CommitmentTitle = string.Empty;
        CommitmentDate = new DateTimeOffset(VisibleDate.ToDateTime(TimeOnly.MinValue));
        CommitmentStart = new TimeSpan(9, 0, 0);
        CommitmentEnd = new TimeSpan(10, 0, 0);
        CommitmentRepeatsWeekly = false;
        CommitmentError = null;
        foreach (var day in CommitmentDays)
        {
            day.IsSelected = false;
        }
    }

    /// <summary>Returns true when the commitment was created (the view closes its flyout).</summary>
    public bool TrySaveCommitment()
    {
        try
        {
            if (CommitmentDate is not { } date || CommitmentStart is not { } start || CommitmentEnd is not { } end)
            {
                CommitmentError = "Pick a date, start, and end.";
                return false;
            }

            RecurrenceRule? recurrence = null;
            if (CommitmentRepeatsWeekly)
            {
                var days = CommitmentDays.Where(d => d.IsSelected).Select(d => d.Day).ToArray();
                if (days.Length == 0)
                {
                    days = [DateOnly.FromDateTime(date.Date).DayOfWeek];
                }

                recurrence = RecurrenceRule.Weekly(1, days);
            }

            _calendar.CreateFixedCommitment(
                CommitmentTitle,
                DateOnly.FromDateTime(date.Date),
                TimeOnly.FromTimeSpan(start),
                TimeOnly.FromTimeSpan(end),
                recurrence);
            CommitmentError = null;
            Reload();
            return true;
        }
        catch (DomainException exception)
        {
            CommitmentError = exception.Message;
            return false;
        }
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
            var flexible = _lastUnplaced?.Count ?? 0;
            var text = $"{pending.Count} block{(pending.Count == 1 ? string.Empty : "s")} proposed · "
                + $"{taskCount} task{(taskCount == 1 ? string.Empty : "s")} scheduled";
            return flexible > 0
                ? $"{text} · {flexible} remain{(flexible == 1 ? "s" : string.Empty)} flexible"
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

        var created = _planning.ApproveAll(draft.Id);
        if (created.Count == 0)
        {
            return;
        }

        _approvalUndoStack.Add((draft.Id, created));
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

        var created = _planning.ApproveBlock(draft.Id, blockId);
        _approvalUndoStack.Add((draft.Id, [created]));
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

        _planning.DiscardDraft(draft.Id);
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

        var (proposalId, blockIds) = _approvalUndoStack[^1];
        _approvalUndoStack.RemoveAt(_approvalUndoStack.Count - 1);
        _planning.UndoApproval(proposalId, blockIds);
        IsUndoToastVisible = false;
        Reload();
        DataChanged?.Invoke();
    }

    [ObservableProperty]
    public partial bool IsUndoToastVisible { get; private set; }

    [ObservableProperty]
    public partial string UndoToastText { get; private set; } = string.Empty;

    /// <summary>Hides the toast after its 10-second window; Ctrl+Z keeps working afterwards.</summary>
    public void ExpireUndoToast() => IsUndoToastVisible = false;

    private void ShowUndoToast(string text)
    {
        UndoToastText = text;
        IsUndoToastVisible = false; // retrigger the view timer even for back-to-back approvals
        IsUndoToastVisible = true;
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
    }

    public void ResizeBlockTo(CalendarBlockId id, TimeOnly end)
    {
        try
        {
            _calendar.ResizeBlock(id, end);
        }
        catch (DomainException)
        {
            // Resizing below the minimum keeps the previous size.
        }

        Reload();
    }

    public void RecordOutcome(CalendarBlockId id, BlockOutcome outcome, TimeSpan? remaining)
    {
        _calendar.RecordOutcome(id, outcome, remaining);
        Reload();
        DataChanged?.Invoke();
    }

    public void UnscheduleBlock(CalendarBlockId id)
    {
        _calendar.DeleteBlock(id);
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

        RefreshReviewNotice(titles);
        OnPropertyChanged(nameof(HeaderMeta));
        OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(DraftTitle));
        OnPropertyChanged(nameof(DraftSummaryText));
        OnPropertyChanged(nameof(DraftLeftoverNote));
        OnPropertyChanged(nameof(HasDraftLeftoverNote));
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
        var title = block.Title
            ?? (block.TaskId is { } taskId && titles.TryGetValue(taskId, out var info)
                ? info.Title
                : "(deleted task)");
        var isDone = block.Outcome == BlockOutcome.Done;
        var elapsed = occurrence.Date < today
            || (occurrence.Date == today && block.EndTime.ToTimeSpan().TotalMinutes <= nowMinutes);
        var needsOutcome = block.Kind == BlockKind.TaskBlock && block.Outcome == BlockOutcome.None && elapsed;
        return CalendarBlockViewModel.ForBlock(
            this, occurrence, title, conflicts.Contains(block.Id), isDone, needsOutcome);
    }

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
                needsOutcome: true));
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

/// <summary>Weekday toggle used by the commitment editor.</summary>
public sealed partial class DayToggleViewModel(DayOfWeek day) : ViewModelBase
{
    public DayOfWeek Day { get; } = day;

    public string Label => Day.ToString()[..2].ToUpperInvariant();

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
