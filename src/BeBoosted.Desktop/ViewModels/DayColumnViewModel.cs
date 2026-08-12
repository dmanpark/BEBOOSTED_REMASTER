using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>One day lane on the timeline surface (Today has one, Week has seven).</summary>
public sealed partial class DayColumnViewModel(DateOnly date, bool isToday, double nowMinutes) : ViewModelBase
{
    public DateOnly Date { get; } = date;

    public bool IsToday { get; } = isToday;

    /// <summary>Minutes since midnight for the current-time rule; -1 when not today.</summary>
    [ObservableProperty]
    public partial double NowMinutes { get; set; } = nowMinutes;

    public string Header
        => Date.ToString("ddd d", CultureInfo.CurrentCulture).ToUpperInvariant();

    public ObservableCollection<CalendarBlockViewModel> Blocks { get; } = [];
}
