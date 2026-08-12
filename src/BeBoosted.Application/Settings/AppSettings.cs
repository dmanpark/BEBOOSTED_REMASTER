using BeBoosted.Application.Abstractions;

namespace BeBoosted.Application.Settings;

/// <summary>Typed facade over the raw settings store.</summary>
public sealed class AppSettings(ISettingsStore store)
{
    public CalendarViewKind GetLastCalendarView()
        => store.Get(SettingKeys.LastCalendarView) == "week" ? CalendarViewKind.Week : CalendarViewKind.Today;

    public void SetLastCalendarView(CalendarViewKind view)
        => store.Set(SettingKeys.LastCalendarView, view == CalendarViewKind.Week ? "week" : "today");

    public WindowPlacement? GetWindowPlacement()
        => WindowPlacement.TryParse(store.Get(SettingKeys.WindowPlacement));

    public void SetWindowPlacement(WindowPlacement placement)
        => store.Set(SettingKeys.WindowPlacement, placement.Serialize());
}
