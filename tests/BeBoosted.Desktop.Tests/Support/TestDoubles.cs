using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.Support;

public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value) => _values[key] = value;

    public void Remove(string key) => _values.Remove(key);
}

public sealed class InMemoryTaskRepository : ITaskRepository
{
    private readonly List<TaskItem> _tasks = [];

    public void Add(TaskItem task) => _tasks.Add(task);

    public void Update(TaskItem task)
    {
        var index = _tasks.FindIndex(t => t.Id == task.Id);
        if (index < 0)
        {
            throw new DomainException($"Task {task.Id} no longer exists.");
        }

        _tasks[index] = task;
    }

    public void Delete(TaskId id) => _tasks.RemoveAll(t => t.Id == id);

    public TaskItem? GetById(TaskId id) => _tasks.FirstOrDefault(t => t.Id == id);

    public IReadOnlyList<TaskItem> GetAll() => _tasks.OrderBy(t => t.CreatedAt).ToList();

    public IReadOnlyList<TaskItem> GetInbox()
        => _tasks.Where(t => !t.IsCompleted).OrderBy(t => t.CreatedAt).ToList();
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

    public static ShellViewModel Create(
        InMemorySettingsStore? store = null,
        InMemoryTaskRepository? tasks = null,
        DateOnly? today = null)
    {
        var settings = new AppSettings(store ?? new InMemorySettingsStore());
        var clock = new FakeClock(today ?? DesignDate);
        var repository = tasks ?? new InMemoryTaskRepository();
        var taskService = new TaskService(repository, clock);
        return new ShellViewModel(
            new CalendarViewModel(settings, clock),
            new InboxViewModel(taskService, repository, clock),
            new ProjectsViewModel(),
            new SettingsViewModel(new FakePaths()));
    }

    /// <summary>A repository pre-seeded with the four Inbox tasks shown in design frame 02.</summary>
    public static InMemoryTaskRepository SeededTasks(IClock clock)
    {
        var repository = new InMemoryTaskRepository();
        repository.Add(TaskItem.Create(
            "Finish DECA presentation", clock.Now,
            estimatedDuration: TimeSpan.FromMinutes(90), deadline: new DateOnly(2026, 8, 14)));
        repository.Add(TaskItem.Create(
            "Draft essay outline", clock.Now,
            estimatedDuration: TimeSpan.FromMinutes(60), deadline: new DateOnly(2026, 8, 16)));
        repository.Add(TaskItem.Create(
            "Review economics chapter", clock.Now,
            estimatedDuration: TimeSpan.FromMinutes(45)));
        repository.Add(TaskItem.Create(
            "Email recommendation request", clock.Now,
            estimatedDuration: TimeSpan.FromMinutes(10)));
        return repository;
    }

    private sealed class FakePaths : IAppDataPaths
    {
        public string DataDirectory => Path.Combine(Path.GetTempPath(), "beboosted-tests");

        public string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");
    }
}
