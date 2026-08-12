using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Settings;

namespace BeBoosted.Tests.Settings;

public sealed class AppSettingsTests
{
    private sealed class InMemoryStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);
    }

    [Fact]
    public void LastCalendarView_DefaultsToToday()
        => Assert.Equal(CalendarViewKind.Today, new AppSettings(new InMemoryStore()).GetLastCalendarView());

    [Fact]
    public void LastCalendarView_RoundTripsWeek()
    {
        var settings = new AppSettings(new InMemoryStore());
        settings.SetLastCalendarView(CalendarViewKind.Week);
        Assert.Equal(CalendarViewKind.Week, settings.GetLastCalendarView());
    }

    [Fact]
    public void LastCalendarView_IgnoresCorruptStoredValue()
    {
        var store = new InMemoryStore();
        store.Set(SettingKeys.LastCalendarView, "garbage");
        Assert.Equal(CalendarViewKind.Today, new AppSettings(store).GetLastCalendarView());
    }
}
