using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
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

    public IReadOnlyList<TaskItem> GetOpen()
        => _tasks.Where(t => !t.IsCompleted).OrderBy(t => t.CreatedAt).ToList();
}

public sealed class InMemoryCalendarBlockRepository : ICalendarBlockRepository
{
    private readonly List<CalendarBlock> _blocks = [];

    public void Add(CalendarBlock block) => _blocks.Add(block);

    public void Update(CalendarBlock block)
    {
        var index = _blocks.FindIndex(b => b.Id == block.Id);
        if (index < 0)
        {
            throw new DomainException($"Calendar block {block.Id} no longer exists.");
        }

        _blocks[index] = block;
    }

    public void Delete(CalendarBlockId id) => _blocks.RemoveAll(b => b.Id == id);

    public CalendarBlock? GetById(CalendarBlockId id) => _blocks.FirstOrDefault(b => b.Id == id);

    public IReadOnlyList<CalendarBlock> GetAll()
        => _blocks.OrderBy(b => b.Date).ThenBy(b => b.StartTime).ToList();

    public IReadOnlyList<CalendarBlock> GetCandidatesBetween(DateOnly from, DateOnly to)
        => _blocks
            .Where(b => (b.Date >= from && b.Date <= to) || (b.Recurrence is not null && b.Date <= to))
            .OrderBy(b => b.Date).ThenBy(b => b.StartTime)
            .ToList();

    public IReadOnlyList<CalendarBlock> GetForTask(TaskId taskId)
        => _blocks.Where(b => b.TaskId == taskId).OrderBy(b => b.Date).ToList();

    public IReadOnlyList<CalendarBlock> GetElapsedWithoutOutcome(DateOnly today, TimeOnly now)
        => _blocks
            .Where(b => b.Kind == BlockKind.TaskBlock && b.Outcome == BlockOutcome.None
                && (b.Date < today || (b.Date == today && b.EndTime <= now)))
            .OrderBy(b => b.Date).ThenBy(b => b.StartTime)
            .ToList();

    public IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks()
        => _blocks
            .Where(b => b.TaskId is not null && b.Outcome == BlockOutcome.None)
            .Select(b => b.TaskId!.Value)
            .ToHashSet();
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
        InMemoryCalendarBlockRepository? blocks = null,
        DateOnly? today = null)
    {
        var settings = new AppSettings(store ?? new InMemorySettingsStore());
        var clock = new FakeClock(today ?? DesignDate);
        var repository = tasks ?? new InMemoryTaskRepository();
        var blockRepository = blocks ?? new InMemoryCalendarBlockRepository();
        var taskService = new TaskService(repository, clock);
        var calendarService = new CalendarService(blockRepository, repository, clock);
        var inboxQuery = new InboxQueryService(repository, blockRepository);
        return new ShellViewModel(
            new CalendarViewModel(settings, clock, calendarService, repository),
            new InboxViewModel(taskService, inboxQuery, clock),
            new ProjectsViewModel(),
            new SettingsViewModel(new FakePaths()));
    }

    /// <summary>
    /// Seeds the calendar content of design frames 01/02: weekday classes, fixed events,
    /// two scheduled task blocks on the design date, and one completed morning block.
    /// </summary>
    public static void SeedDesignCalendar(
        InMemoryTaskRepository tasks,
        InMemoryCalendarBlockRepository blocks,
        IClock clock)
    {
        var now = clock.Now;
        var tue = DesignDate;

        blocks.Add(CalendarBlock.CreateFixedCommitment(
            "AP Economics", tue.AddDays(-8), new TimeOnly(8, 30), new TimeOnly(9, 45), now,
            BeBoosted.Domain.Scheduling.RecurrenceRule.Weekly(
                1, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)));
        blocks.Add(CalendarBlock.CreateFixedCommitment(
            "Lunch", tue, new TimeOnly(12, 0), new TimeOnly(12, 45), now));
        blocks.Add(CalendarBlock.CreateFixedCommitment(
            "DECA club meeting", tue.AddDays(1), new TimeOnly(15, 30), new TimeOnly(17, 0), now));
        blocks.Add(CalendarBlock.CreateFixedCommitment(
            "SAT practice test", tue.AddDays(4), new TimeOnly(10, 0), new TimeOnly(12, 0), now));
        blocks.Add(CalendarBlock.CreateFixedCommitment(
            "Family dinner", tue.AddDays(5), new TimeOnly(18, 0), new TimeOnly(19, 30), now));

        var practice = TaskItem.Create("Practice DECA role-play", now, estimatedDuration: TimeSpan.FromMinutes(90));
        var statement = TaskItem.Create("Draft personal statement", now, estimatedDuration: TimeSpan.FromMinutes(60));
        var reading = TaskItem.Create("Morning reading — econ chapter 6", now, estimatedDuration: TimeSpan.FromMinutes(40));
        tasks.Add(practice);
        tasks.Add(statement);
        tasks.Add(reading);

        blocks.Add(CalendarBlock.CreateForTask(practice.Id, tue, new TimeOnly(15, 30), new TimeOnly(17, 0), now));
        blocks.Add(CalendarBlock.CreateForTask(statement.Id, tue, new TimeOnly(19, 0), new TimeOnly(20, 0), now));

        var readingBlock = CalendarBlock.CreateForTask(reading.Id, tue, new TimeOnly(7, 10), new TimeOnly(7, 50), now);
        readingBlock.RecordOutcome(BlockOutcome.Done, now);
        blocks.Add(readingBlock);
        reading.Complete(now);
        tasks.Update(reading);
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
