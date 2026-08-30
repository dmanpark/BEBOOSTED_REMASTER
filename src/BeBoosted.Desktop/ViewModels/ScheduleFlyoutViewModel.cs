using BeBoosted.Application.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// The manual scheduling form shared by an unscheduled row's Schedule action and an
/// approved block's Change time action. Warnings inform; only domain errors block —
/// and those keep the flyout open with the message inline.
/// </summary>
public sealed partial class ScheduleFlyoutViewModel : ViewModelBase
{
    private readonly DailyListViewModel _owner;
    private readonly DailyRowViewModel _row;
    private readonly DateOnly _defaultDate;
    private readonly TimeOnly _defaultStart;
    private readonly int _defaultDurationMinutes;

    internal ScheduleFlyoutViewModel(
        DailyListViewModel owner,
        DailyRowViewModel row,
        string heading,
        string confirmLabel,
        DateOnly defaultDate,
        TimeOnly defaultStart,
        int defaultDurationMinutes)
    {
        _owner = owner;
        _row = row;
        Heading = heading;
        ConfirmLabel = confirmLabel;
        _defaultDate = defaultDate;
        _defaultStart = defaultStart;
        _defaultDurationMinutes = defaultDurationMinutes;
        Reset();
    }

    public string Heading { get; }

    /// <summary>"Schedule" or "Change time" — also the confirm button's text.</summary>
    public string ConfirmLabel { get; }

    public string ConfirmAccessibleName => $"{ConfirmLabel} {_row.Title}";

    [ObservableProperty]
    public partial DateTimeOffset? Date { get; set; }

    [ObservableProperty]
    public partial TimeSpan? Start { get; set; }

    [ObservableProperty]
    public partial decimal DurationMinutes { get; set; }

    /// <summary>Overlap/constraint warnings for deliberate manual scheduling — never blocking.</summary>
    [ObservableProperty]
    public partial string? WarningText { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; internal set; }

    /// <summary>Returns the form to its defaults (called when the flyout reopens).</summary>
    public void Reset()
    {
        Date = new DateTimeOffset(_defaultDate.ToDateTime(TimeOnly.MinValue));
        Start = _defaultStart.ToTimeSpan();
        DurationMinutes = _defaultDurationMinutes;
        Error = null;
        RefreshWarning();
    }

    partial void OnDateChanged(DateTimeOffset? value) => RefreshWarning();

    partial void OnStartChanged(TimeSpan? value) => RefreshWarning();

    partial void OnDurationMinutesChanged(decimal value) => RefreshWarning();

    private void RefreshWarning()
    {
        if (Date is { } date && Start is { } start)
        {
            WarningText = _owner.DescribeScheduleWarnings(
                _row,
                DateOnly.FromDateTime(date.Date),
                TimeOnly.FromTimeSpan(start),
                TimeSpan.FromMinutes((double)Math.Max(5, DurationMinutes)));
        }
        else
        {
            WarningText = null;
        }

        Error = null;
    }

    /// <summary>Applies the form. True closes the flyout; false keeps it open with Error set.</summary>
    public bool Confirm() => _owner.ConfirmSchedule(_row, this);

    /// <summary>Next 15-minute boundary when scheduling on the actual current date; 9:00 otherwise.</summary>
    internal static TimeOnly DefaultStartFor(DateOnly targetDate, IClock clock)
    {
        if (targetDate != clock.Today)
        {
            return new TimeOnly(9, 0);
        }

        var now = TimeOnly.FromDateTime(clock.Now.LocalDateTime);
        var minutes = (int)Math.Ceiling(now.ToTimeSpan().TotalMinutes / 15.0) * 15;
        if (minutes >= (24 * 60) - 15)
        {
            minutes = (24 * 60) - 15; // the day's last usable boundary; the end still clamps at 23:59
        }

        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes));
    }
}
