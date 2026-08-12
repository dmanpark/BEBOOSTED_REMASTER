using System.Collections.ObjectModel;
using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
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
    private readonly bool _initialized;

    public CalendarViewModel(
        AppSettings settings,
        IClock clock,
        CalendarService calendar,
        ITaskRepository tasks)
    {
        _settings = settings;
        _clock = clock;
        _calendar = calendar;
        _tasks = tasks;
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
        var start = block.Block.StartTime.AddMinutes(minutes);
        if (start < TimeOnly.MinValue.AddMinutes(0) || start.ToTimeSpan() + block.Block.Duration > TimeSpan.FromHours(24))
        {
            return;
        }

        MoveBlock(block.Block.Id, block.Date, start);
    }

    public void NudgeBlockDays(CalendarBlockViewModel block, int days)
        => MoveBlock(block.Block.Id, block.Date.AddDays(days), block.Block.StartTime);

    public void ResizeBlockBy(CalendarBlockViewModel block, int minutes)
    {
        var end = block.Block.EndTime.AddMinutes(minutes);
        if (end <= block.Block.StartTime || end.ToTimeSpan() >= TimeSpan.FromHours(24))
        {
            return;
        }

        ResizeBlockTo(block.Block.Id, end);
    }

    // ---- Loading ----

    public void Reload()
    {
        var (from, to) = ViewKind == CalendarViewKind.Week
            ? WeekRange(VisibleDate)
            : (VisibleDate, VisibleDate);

        var occurrences = _calendar.GetOccurrences(from, to);
        var conflicts = ConflictDetector.FindConflicts(occurrences);
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

            Days.Add(day);
        }

        RefreshReviewNotice(titles);
        OnPropertyChanged(nameof(HeaderMeta));
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
        return new CalendarBlockViewModel(this, occurrence, title, conflicts.Contains(block.Id), isDone, needsOutcome);
    }

    private void RefreshReviewNotice(Dictionary<TaskId, (string Title, bool IsDone)> titles)
    {
        ReviewBlocks.Clear();
        foreach (var block in _calendar.GetBlocksNeedingOutcome())
        {
            var title = block.TaskId is { } taskId && titles.TryGetValue(taskId, out var info)
                ? info.Title
                : block.Title ?? "(deleted task)";
            ReviewBlocks.Add(new CalendarBlockViewModel(
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
