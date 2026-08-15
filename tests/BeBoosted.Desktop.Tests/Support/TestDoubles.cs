using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Projects;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Ai;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Projects;
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

    public IReadOnlyList<CalendarBlock> GetForProject(ProjectId projectId)
        => _blocks.Where(b => b.ProjectId == projectId)
            .OrderBy(b => b.Date).ThenBy(b => b.StartTime)
            .ToList();

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

public sealed class InMemoryCommitmentCompletionRepository : ICommitmentCompletionRepository
{
    private readonly Dictionary<(CalendarBlockId, DateOnly), CommitmentCompletion> _completions = [];

    public void Add(CommitmentCompletion completion)
        => _completions[(completion.BlockId, completion.OccurrenceDate)] = completion;

    public void Remove(CalendarBlockId blockId, DateOnly occurrenceDate)
        => _completions.Remove((blockId, occurrenceDate));

    public CommitmentCompletion? Get(CalendarBlockId blockId, DateOnly occurrenceDate)
        => _completions.GetValueOrDefault((blockId, occurrenceDate));

    public IReadOnlyList<CommitmentCompletion> GetForBlock(CalendarBlockId blockId)
        => _completions.Values
            .Where(c => c.BlockId == blockId)
            .OrderBy(c => c.OccurrenceDate)
            .ToList();

    public IReadOnlyList<CommitmentCompletion> GetBetween(DateOnly from, DateOnly to)
        => _completions.Values
            .Where(c => c.OccurrenceDate >= from && c.OccurrenceDate <= to)
            .OrderBy(c => c.OccurrenceDate)
            .ToList();
}

/// <summary>
/// Passes the shared in-memory repositories straight through — no rollback, so
/// atomicity itself is proven against real SQLite in BeBoosted.Tests.
/// </summary>
public sealed class InMemoryCalendarMutations(
    ICalendarBlockRepository blocks,
    ICommitmentCompletionRepository completions) : ICalendarMutations
{
    public void Execute(Action<ICalendarBlockRepository, ICommitmentCompletionRepository> mutation)
        => mutation(blocks, completions);
}

public sealed class InMemoryPlanningProposalRepository : IPlanningProposalRepository
{
    private readonly Dictionary<PlanningProposalId, PlanningProposal> _proposals = [];

    public void Save(PlanningProposal proposal) => _proposals[proposal.Id] = proposal;

    public PlanningProposal? GetById(PlanningProposalId id) => _proposals.GetValueOrDefault(id);

    public PlanningProposal? GetActiveDraft()
        => _proposals.Values
            .Where(p => p.State == ProposalState.Draft)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefault();

    public void Delete(PlanningProposalId id) => _proposals.Remove(id);
}

public sealed class InMemoryPrioritizationRepository : IPrioritizationRepository
{
    private readonly List<ComparisonDecision> _decisions = [];
    private readonly Dictionary<string, IReadOnlyList<PriorityRank>> _ranks = [];

    public void SaveDecisions(IReadOnlyList<ComparisonDecision> decisions) => _decisions.AddRange(decisions);

    public IReadOnlyList<ComparisonDecision> GetDecisions(string periodKey)
        => _decisions.Where(d => d.Period.Key == periodKey).ToList();

    public void ReplaceRanks(string periodKey, IReadOnlyList<PriorityRank> ranks)
        => _ranks[periodKey] = [.. ranks];

    public IReadOnlyList<PriorityRank> GetRanks(string periodKey)
        => _ranks.GetValueOrDefault(periodKey, []);
}

public sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly Dictionary<ProjectId, Project> _projects = [];

    public void Add(Project project) => _projects[project.Id] = project;

    public void Update(Project project) => _projects[project.Id] = project;

    public void Delete(ProjectId id) => _projects.Remove(id);

    public Project? GetById(ProjectId id) => _projects.GetValueOrDefault(id);

    public IReadOnlyList<Project> GetAll() => _projects.Values.OrderBy(p => p.CreatedAt).ToList();
}

public sealed class InMemoryProjectFileRepository : IProjectFileRepository
{
    private readonly Dictionary<ProjectFileId, ProjectFile> _files = [];

    public void Add(ProjectFile file) => _files[file.Id] = file;

    public void Update(ProjectFile file) => _files[file.Id] = file;

    public void Delete(ProjectFileId id) => _files.Remove(id);

    public ProjectFile? GetById(ProjectFileId id) => _files.GetValueOrDefault(id);

    public IReadOnlyList<ProjectFile> GetForProject(ProjectId projectId)
        => _files.Values.Where(f => f.ProjectId == projectId).OrderBy(f => f.CreatedAt).ToList();
}

public sealed class InMemoryResourceRepository : IResourceRepository
{
    private readonly Dictionary<ResourceId, Resource> _resources = [];
    private readonly Dictionary<ResourceId, string> _indexText = [];

    public void Add(Resource resource) => _resources[resource.Id] = resource;

    public void Update(Resource resource) => _resources[resource.Id] = resource;

    public void Delete(ResourceId id)
    {
        _resources.Remove(id);
        _indexText.Remove(id);
    }

    public Resource? GetById(ResourceId id) => _resources.GetValueOrDefault(id);

    public IReadOnlyList<Resource> GetForFile(ProjectFileId fileId)
        => _resources.Values.Where(r => r.FileId == fileId).OrderBy(r => r.AddedAt).ToList();

    public int CountForFile(ProjectFileId fileId) => GetForFile(fileId).Count;

    public void SetIndexText(ResourceId id, string text) => _indexText[id] = text;

    public string? GetIndexText(ResourceId id) => _indexText.GetValueOrDefault(id);

    public IReadOnlyList<Resource> SearchInProject(ProjectId projectId, string query)
        => _resources.Values
            .Where(r => _indexText.GetValueOrDefault(r.Id, string.Empty)
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.AddedAt)
            .ToList();
}

public sealed class FakeResourceStorage : IResourceStorage
{
    private readonly HashSet<string> _stored = [];

    public string Store(ResourceId id, string sourcePath)
    {
        var storedPath = id + Path.GetExtension(sourcePath);
        _stored.Add(storedPath);
        return storedPath;
    }

    public string ResolvePath(string storedPath) => Path.Combine("fake-resources", storedPath);

    public bool Exists(string storedPath) => _stored.Contains(storedPath);

    public void Delete(string storedPath) => _stored.Remove(storedPath);
}

public sealed class FakeIndexer(IResourceRepository resources, IClock clock) : IResourceIndexer
{
    public void Index(Resource resource)
    {
        resources.SetIndexText(resource.Id, $"{resource.Title}\n{resource.Content}\n{resource.Url}");
        resource.MarkIndexed(clock.Now);
    }
}

public sealed class InMemoryAiProvenanceRepository : IAiProvenanceRepository
{
    private readonly Dictionary<AiProvenanceId, AiProvenance> _records = [];
    private readonly List<AiAnswer> _answers = [];

    public void Add(AiProvenance provenance) => _records[provenance.Id] = provenance;

    public void Update(AiProvenance provenance) => _records[provenance.Id] = provenance;

    public AiProvenance? GetById(AiProvenanceId id) => _records.GetValueOrDefault(id);

    public IReadOnlyList<AiProvenance> GetBySourceResource(ResourceId resourceId)
        => _records.Values.Where(p => p.SourceResourceIds.Contains(resourceId)).ToList();

    public void AddAnswer(AiAnswer answer) => _answers.Add(answer);

    public IReadOnlyList<AiAnswer> GetAnswersForProject(ProjectId projectId)
        => _answers.Where(a => a.ProjectId == projectId).ToList();

    public AiAnswer? GetAnswerByProvenance(AiProvenanceId provenanceId)
        => _answers.FirstOrDefault(a => a.ProvenanceId == provenanceId);
}

public sealed class FakeFileReveal : BeBoosted.Desktop.Platform.IFileRevealService
{
    public List<string> Opened { get; } = [];

    public void OpenFile(string path) => Opened.Add(path);

    public void RevealInFolder(string path) => Opened.Add(path);

    public void OpenUrl(string url) => Opened.Add(url);
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

    /// <summary>A CalendarService over in-memory doubles with its own completion store.</summary>
    public static CalendarService CreateCalendarService(
        InMemoryCalendarBlockRepository blocks, InMemoryTaskRepository tasks, IClock clock)
    {
        var completions = new InMemoryCommitmentCompletionRepository();
        return new CalendarService(
            blocks, completions, new InMemoryCalendarMutations(blocks, completions), tasks, clock);
    }

    /// <summary>
    /// A CalendarViewModel over shared in-memory repositories, wiring the Daily list's
    /// full dependency set (task capture/complete, inbox query, ranks, AI review flags).
    /// </summary>
    public static CalendarViewModel CreateCalendarViewModel(
        InMemorySettingsStore store,
        FakeClock clock,
        InMemoryTaskRepository tasks,
        InMemoryCalendarBlockRepository blocks,
        InMemoryProjectRepository projects,
        CalendarService service,
        PlanningService planning,
        InMemoryPrioritizationRepository? ranks = null)
    {
        var aiPermissions = new AiPermissionSettings(store);
        var aiService = new AiService(
            new BeBoosted.Infrastructure.Ai.LocalHeuristicAiProvider(new InMemoryResourceRepository(), projects),
            new InMemoryAiProvenanceRepository(), tasks, aiPermissions, clock);
        return new CalendarViewModel(
            new AppSettings(store), clock, service, tasks, planning, projects,
            new TaskService(tasks, clock),
            new InboxQueryService(tasks, blocks),
            new PrioritySortService(ranks ?? new InMemoryPrioritizationRepository(), clock),
            aiService);
    }

    public static ShellViewModel Create(
        InMemorySettingsStore? store = null,
        InMemoryTaskRepository? tasks = null,
        InMemoryCalendarBlockRepository? blocks = null,
        InMemoryProjectRepository? projects = null,
        InMemoryCommitmentCompletionRepository? completions = null,
        DateOnly? today = null,
        InMemoryPrioritizationRepository? ranks = null)
    {
        var settingsStore = store ?? new InMemorySettingsStore();
        var settings = new AppSettings(settingsStore);
        var clock = new FakeClock(today ?? DesignDate);
        var repository = tasks ?? new InMemoryTaskRepository();
        var blockRepository = blocks ?? new InMemoryCalendarBlockRepository();
        var completionRepository = completions ?? new InMemoryCommitmentCompletionRepository();
        var taskService = new TaskService(repository, clock);
        var calendarService = new CalendarService(
            blockRepository, completionRepository,
            new InMemoryCalendarMutations(blockRepository, completionRepository), repository, clock);
        var inboxQuery = new InboxQueryService(repository, blockRepository);
        var prioritization = ranks ?? new InMemoryPrioritizationRepository();
        var prioritySort = new PrioritySortService(prioritization, clock);
        var planning = new PlanningService(
            new InMemoryPlanningProposalRepository(), blockRepository,
            inboxQuery, prioritization, calendarService, clock);
        var projectRepo = projects ?? new InMemoryProjectRepository();
        var fileRepo = new InMemoryProjectFileRepository();
        var resourceRepo = new InMemoryResourceRepository();
        var storage = new FakeResourceStorage();
        var aiPermissions = new AiPermissionSettings(settingsStore);
        var aiProvider = new BeBoosted.Infrastructure.Ai.LocalHeuristicAiProvider(resourceRepo, projectRepo);
        var aiService = new AiService(
            aiProvider, new InMemoryAiProvenanceRepository(), repository, aiPermissions, clock);
        var projectService = new ProjectService(
            projectRepo, fileRepo, resourceRepo, storage,
            new FakeIndexer(resourceRepo, clock), repository, blockRepository,
            completionRepository, clock, aiService);
        return new ShellViewModel(
            new CalendarViewModel(
                settings, clock, calendarService, repository, planning, projectRepo,
                taskService, inboxQuery, prioritySort, aiService),
            new InboxViewModel(taskService, inboxQuery, projectRepo, aiService, clock),
            new ProjectsViewModel(
                projectService, projectRepo, fileRepo, resourceRepo, taskService,
                calendarService, new FakeFileReveal(), aiService),
            new SettingsViewModel(new FakePaths(), aiPermissions),
            new ChatViewModel(aiService, aiPermissions, clock),
            prioritySort,
            aiPermissions,
            new BeBoosted.Desktop.Platform.DefaultKeymapService(),
            clock);
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
