using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Tests.Support;

public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value) => _values[key] = value;

    public void Remove(string key) => _values.Remove(key);
}

public sealed class FakeClock(DateOnly today) : IClock
{
    public DateTimeOffset Now => new(today.ToDateTime(new TimeOnly(14, 10)));

    public DateOnly Today => today;
}

public static class TestShell
{
    /// <summary>Tuesday, August 11, 2026 — the date used across the design frames.</summary>
    public static readonly DateOnly DesignDate = new(2026, 8, 11);

    public static ShellViewModel Create(InMemorySettingsStore? store = null, DateOnly? today = null)
    {
        var settings = new AppSettings(store ?? new InMemorySettingsStore());
        var clock = new FakeClock(today ?? DesignDate);
        return new ShellViewModel(
            new CalendarViewModel(settings, clock),
            new ProjectsViewModel(),
            new SettingsViewModel(new FakePaths()));
    }

    private sealed class FakePaths : IAppDataPaths
    {
        public string DataDirectory => Path.Combine(Path.GetTempPath(), "beboosted-tests");

        public string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");
    }
}
