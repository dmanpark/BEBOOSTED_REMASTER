using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

public sealed partial class CalendarViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly bool _initialized;

    public CalendarViewModel(AppSettings settings, IClock clock)
    {
        _settings = settings;
        _clock = clock;
        VisibleDate = clock.Today;
        ViewKind = settings.GetLastCalendarView();
        _initialized = true;
    }

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

    public string HeaderMeta
    {
        get
        {
            if (ViewKind == CalendarViewKind.Today)
            {
                return string.Empty;
            }

            var (monday, _) = WeekRange(VisibleDate);
            var week = ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue));
            return $"Week {week}";
        }
    }

    partial void OnViewKindChanged(CalendarViewKind value)
    {
        if (_initialized)
        {
            _settings.SetLastCalendarView(value);
        }

        OnPropertyChanged(nameof(IsTodayView));
        OnPropertyChanged(nameof(IsWeekView));
    }

    [RelayCommand]
    private void GoPrevious() => VisibleDate = VisibleDate.AddDays(ViewKind == CalendarViewKind.Week ? -7 : -1);

    [RelayCommand]
    private void GoNext() => VisibleDate = VisibleDate.AddDays(ViewKind == CalendarViewKind.Week ? 7 : 1);

    [RelayCommand]
    private void GoToToday() => VisibleDate = _clock.Today;

    /// <summary>Weeks start on Monday, matching the approved design frames.</summary>
    internal static (DateOnly Monday, DateOnly Sunday) WeekRange(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        var monday = date.AddDays(-offset);
        return (monday, monday.AddDays(6));
    }
}
