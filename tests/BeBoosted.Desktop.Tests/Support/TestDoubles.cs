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

    /// <summary>Mirrors the SQL join that hides sessions of completed tasks.</summary>
    public ITaskRepository? Tasks { get; set; }

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
        => _blocks.Where(b => b.TaskId == taskId)
            .OrderBy(b => b.Date).ThenBy(b => b.StartTime).ToList();

    public IReadOnlyList<CalendarBlock> GetElapsedWithoutOutcome(DateOnly today, TimeOnly now)
        => _blocks
            .Where(b => b is { Kind: BlockKind.TaskSession, IsExternal: false, Recurrence: null }
                && b.Outcome == BlockOutcome.None
                && (b.TaskId is not { } taskId || Tasks?.GetById(taskId)?.IsCompleted != true)
                && (b.Date < today || (b.Date == today && b.EndTime <= now)))
            .OrderBy(b => b.Date).ThenBy(b => b.StartTime)
            .ToList();

    public IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks()
        => _blocks
            .Where(b => b.TaskId is not null && b.Outcome == BlockOutcome.None)
            .Select(b => b.TaskId!.Value)
            .ToHashSet();
}

public sealed class InMemoryOccurrenceCompletionRepository : IOccurrenceCompletionRepository
{
    private readonly Dictionary<(CalendarBlockId, DateOnly), OccurrenceCompletion> _completions = [];

    public void Add(OccurrenceCompletion completion)
        => _completions[(completion.BlockId, completion.OccurrenceDate)] = completion;

    public void Remove(CalendarBlockId blockId, DateOnly occurrenceDate)
        => _completions.Remove((blockId, occurrenceDate));

    public OccurrenceCompletion? Get(CalendarBlockId blockId, DateOnly occurrenceDate)
        => _completions.GetValueOrDefault((blockId, occurrenceDate));

    public IReadOnlyList<OccurrenceCompletion> GetForBlock(CalendarBlockId blockId)
        => _completions.Values
            .Where(c => c.BlockId == blockId)
            .OrderBy(c => c.OccurrenceDate)
            .ToList();

    public IReadOnlyList<OccurrenceCompletion> GetBetween(DateOnly from, DateOnly to)
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
    IOccurrenceCompletionRepository completions,
    ITaskRepository tasks,
    IPlanningProposalRepository? proposals = null) : ICalendarMutations
{
    private readonly IPlanningProposalRepository _proposals =
        proposals ?? new InMemoryPlanningProposalRepository();

    public void Execute(
        Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
            IPlanningProposalRepository> mutation)
        => mutation(blocks, completions, tasks, _proposals);
}

/// <summary>
/// Passes the shared in-memory repositories straight through — no transaction and no
/// rollback, so atomicity itself is proven against real SQLite in BeBoosted.Tests.
/// </summary>
public sealed class InMemoryProjectMutations(
    IProjectRepository projects,
    IProjectFileRepository files,
    IResourceRepository resources,
    ITaskRepository tasks,
    IResourceGroupRepository groups) : IProjectMutations
{
    public void Execute(
        Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository,
            IResourceGroupRepository> mutation)
        => mutation(projects, files, resources, tasks, groups);
}

/// <summary>
/// A mutations seam that refuses every unit of work routed through it, throwing before the
/// callback runs so nothing it would have written is even attempted.
///
/// Read that literally: it fails only what actually goes through <see cref="IProjectMutations"/>,
/// which in <see cref="ProjectService"/> is deleting a project, a File, a resource or a
/// group, and ungrouping. Creating a project, a File or a group, adding a note or a link,
/// importing, renaming and filing a resource into a group all write through the
/// repositories directly and still succeed under this double — which is what lets a test
/// build a real fixture and then fail exactly one action.
///
/// SQLite's real rollback is proven in BeBoosted.Tests; what this double is for is the
/// presentation boundary above it — a failed mutation must be reported as a failure, never
/// optimistically refreshed around.
/// </summary>
public sealed class FailingProjectMutations(string message = "The database is busy.") : IProjectMutations
{
    public void Execute(
        Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository,
            IResourceGroupRepository> mutation)
        => throw new DomainException(message);
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

    public IReadOnlyList<PlanningProposal> GetAll()
        => _proposals.Values.OrderBy(p => p.CreatedAt).ToList();

    public void Delete(PlanningProposalId id) => _proposals.Remove(id);
}

public sealed class InMemoryPrioritizationRepository : IPrioritizationRepository
{
    private readonly List<ComparisonDecision> _decisions = [];
    private readonly Dictionary<string, IReadOnlyList<PriorityRank>> _ranks = [];

    public void SaveSessionResult(
        string periodKey, IReadOnlyList<ComparisonDecision> decisions, IReadOnlyList<PriorityRank> ranks)
    {
        SaveDecisions(decisions);
        ReplaceRanks(periodKey, ranks);
    }

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

    /// <summary>Mirrors project_files.project_id ON DELETE CASCADE.</summary>
    public InMemoryProjectFileRepository? Files { get; set; }

    public void Add(Project project) => _projects[project.Id] = Clone(project);

    public void Update(Project project) => _projects[project.Id] = Clone(project);

    /// <summary>
    /// A detached copy, the way the SQLite repository necessarily hands one back. Storing
    /// and returning the caller's own instance makes every read a live view of it, so a
    /// mutation is visible through a stale reference and a test can pass without the
    /// refresh it means to pin ever happening.
    /// </summary>
    private static Project Clone(Project project)
        => Project.Rehydrate(
            project.Id, project.Name, project.AccentColor,
            project.CreatedAt, project.ModifiedAt, project.FolderSegment);

    public void Delete(ProjectId id)
    {
        if (Files is { } files)
        {
            foreach (var file in files.GetForProject(id))
            {
                files.Delete(file.Id);
            }
        }

        _projects.Remove(id);
    }

    public Project? GetById(ProjectId id)
        => _projects.GetValueOrDefault(id) is { } project ? Clone(project) : null;

    public IReadOnlyList<Project> GetAll()
        => _projects.Values.OrderBy(p => p.CreatedAt).Select(Clone).ToList();
}

public sealed class InMemoryProjectFileRepository : IProjectFileRepository
{
    private readonly Dictionary<ProjectFileId, ProjectFile> _files = [];

    /// <summary>Mirrors resources.file_id ON DELETE CASCADE.</summary>
    public InMemoryResourceRepository? Resources { get; set; }

    /// <summary>Mirrors resource_groups.file_id ON DELETE CASCADE.</summary>
    public InMemoryResourceGroupRepository? Groups { get; set; }

    public void Add(ProjectFile file) => _files[file.Id] = Clone(file);

    public void Update(ProjectFile file) => _files[file.Id] = Clone(file);

    /// <summary>
    /// A detached copy, for the same reason <see cref="InMemoryProjectRepository"/> makes
    /// one: handing back the caller's own instance turns every read into a live view of
    /// it, and an assertion about a refresh then passes whether or not the refresh ran.
    /// </summary>
    private static ProjectFile Clone(ProjectFile file)
        => ProjectFile.Rehydrate(
            file.Id, file.ProjectId, file.Title, file.Description,
            file.CreatedAt, file.ModifiedAt, file.FolderSegment);

    public void Delete(ProjectFileId id)
    {
        if (Resources is { } resources)
        {
            foreach (var resource in resources.GetForFile(id))
            {
                resources.Delete(resource.Id);
            }
        }

        if (Groups is { } groups)
        {
            foreach (var group in groups.GetForFile(id))
            {
                groups.Delete(group.Id);
            }
        }

        _files.Remove(id);
    }

    public ProjectFile? GetById(ProjectFileId id)
        => _files.GetValueOrDefault(id) is { } file ? Clone(file) : null;

    public IReadOnlyList<ProjectFile> GetForProject(ProjectId projectId)
        => _files.Values.Where(f => f.ProjectId == projectId).OrderBy(f => f.CreatedAt).Select(Clone).ToList();
}

/// <summary>
/// Mirrors resource_groups: a group is a row of its own, cloned in and out the way the
/// SQLite repository necessarily hands back detached instances.
/// </summary>
public sealed class InMemoryResourceGroupRepository : IResourceGroupRepository
{
    private readonly Dictionary<ResourceGroupId, ResourceGroup> _groups = [];

    /// <summary>
    /// Mirrors resources.group_id ON DELETE SET NULL. Deleting a group must leave its
    /// members alive and loose — modelling it as a cascade here would let a test pass
    /// against behaviour the database does not have.
    /// </summary>
    public InMemoryResourceRepository? Resources { get; set; }

    public void Add(ResourceGroup group)
    {
        RequireReservedSegment(group);
        _groups[group.Id] = Clone(group);
    }

    public void Update(ResourceGroup group)
    {
        RequireReservedSegment(group);
        if (!_groups.ContainsKey(group.Id))
        {
            throw new DomainException("That group no longer exists.");
        }

        _groups[group.Id] = Clone(group);
    }

    /// <summary>
    /// Both refusals mirror SqliteResourceGroupRepository. Without them a view-model test
    /// could pass on a write real SQLite would reject.
    /// </summary>
    private static void RequireReservedSegment(ResourceGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.FolderSegment))
        {
            throw new DomainException("A group needs a claimed folder segment.");
        }
    }

    public void Delete(ResourceGroupId id)
    {
        if (Resources is { } resources)
        {
            foreach (var member in resources.GetForGroup(id))
            {
                member.MoveToGroup(null, member.ModifiedAt);
                resources.Update(member);
            }
        }

        _groups.Remove(id);
    }

    public ResourceGroup? GetById(ResourceGroupId id)
        => _groups.GetValueOrDefault(id) is { } group ? Clone(group) : null;

    /// <summary>
    /// How many more <see cref="GetForFile"/> reads succeed before one throws; null
    /// disarms it, which is the default. A caller that does several reads in a row — the
    /// File surface reads resources and then groups — can only be caught half-rebuilt by
    /// failing one of them, and there is no other seam to inject that through. One shot,
    /// so the surface can be exercised again afterwards.
    /// </summary>
    public int? ReadsUntilFailure { get; set; }

    /// <summary>The repository's ORDER BY sort_order, created_at, id.</summary>
    public IReadOnlyList<ResourceGroup> GetForFile(ProjectFileId fileId)
    {
        if (ReadsUntilFailure is { } remaining)
        {
            if (remaining == 0)
            {
                ReadsUntilFailure = null;
                throw new IOException("The resource_groups table could not be read.");
            }

            ReadsUntilFailure = remaining - 1;
        }

        return _groups.Values
            .Where(g => g.FileId == fileId)
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.CreatedAt)
            .ThenBy(g => g.Id.ToString(), StringComparer.Ordinal)
            .Select(Clone)
            .ToList();
    }

    /// <summary>
    /// A detached copy, for the same reason the other project doubles make one: handing
    /// back the caller's own instance turns every read into a live view of it, and a
    /// failed write would still appear to have landed.
    /// </summary>
    private static ResourceGroup Clone(ResourceGroup group)
        => ResourceGroup.Rehydrate(
            group.Id, group.FileId, group.Title, group.SortOrder,
            group.CreatedAt, group.ModifiedAt, group.FolderSegment);
}

public sealed class InMemoryResourceRepository : IResourceRepository
{
    private readonly Dictionary<ResourceId, Resource> _resources = [];
    private readonly Dictionary<ResourceId, string> _indexText = [];

    public void Add(Resource resource) => _resources[resource.Id] = Clone(resource);

    public void Update(Resource resource) => _resources[resource.Id] = Clone(resource);

    public void Delete(ResourceId id)
    {
        _resources.Remove(id);
        _indexText.Remove(id);
    }

    public Resource? GetById(ResourceId id)
        => _resources.GetValueOrDefault(id) is { } resource ? Clone(resource) : null;

    public IReadOnlyList<Resource> GetForFile(ProjectFileId fileId)
        => _resources.Values.Where(r => r.FileId == fileId).OrderBy(r => r.AddedAt).Select(Clone).ToList();

    /// <summary>The members of one group, for the SET NULL the group double models.</summary>
    public IReadOnlyList<Resource> GetForGroup(ResourceGroupId groupId)
        => _resources.Values.Where(r => r.GroupId == groupId).OrderBy(r => r.AddedAt).Select(Clone).ToList();

    public int CountForFile(ProjectFileId fileId) => GetForFile(fileId).Count;

    public void SetIndexText(ResourceId id, string text) => _indexText[id] = text;

    public string? GetIndexText(ResourceId id) => _indexText.GetValueOrDefault(id);

    public IReadOnlyList<Resource> SearchInProject(ProjectId projectId, string query)
        => _resources.Values
            .Where(r => _indexText.GetValueOrDefault(r.Id, string.Empty)
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.AddedAt)
            .Select(Clone)
            .ToList();

    /// <summary>
    /// A detached copy, carrying GroupId with everything else. Storing the caller's own
    /// instance would let a failed update still mutate the cached row, so a test could
    /// pass on a write that never happened.
    /// </summary>
    private static Resource Clone(Resource resource)
        => Resource.Rehydrate(
            resource.Id, resource.FileId, resource.Kind, resource.Title, resource.Url,
            resource.Content, resource.OriginalFileName, resource.StoredPath, resource.AddedAt,
            resource.IndexState, resource.ModifiedAt, resource.GroupId);
}

public sealed class FakeResourceStorage : IResourceStorage
{
    private readonly HashSet<string> _stored = [];
    private readonly HashSet<string> _folders = [];

    public string Store(string relativeFolder, string preferredFileName, string sourcePath)
    {
        var storedPath = FreePath(relativeFolder, preferredFileName);
        _stored.Add(storedPath);
        return storedPath;
    }

    public string? MoveInto(string currentStoredPath, string relativeFolder, string preferredFileName)
    {
        if (!_stored.Remove(currentStoredPath))
        {
            return null;
        }

        var storedPath = FreePath(relativeFolder, preferredFileName);
        _stored.Add(storedPath);
        return storedPath;
    }

    public string ResolvePath(string storedPath) => Path.Combine("fake-resources", storedPath);

    public bool Exists(string storedPath) => _stored.Contains(storedPath);

    public void Delete(string storedPath) => _stored.Remove(storedPath);

    public string ReserveFolderSegment(
        string relativeParent, string preferredSegment, IReadOnlySet<string> claimed, string? ownedSegment = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? preferredSegment : $"{preferredSegment} ({attempt})";
            if (claimed.Contains(candidate))
            {
                continue;
            }

            // Matches LocalResourceStorage: an owned segment comes back in the spelling it
            // already has, so a case-only rename keeps naming the directory that exists.
            var reserved = candidate;
            var isOwned = false;
            if (ownedSegment is { } owned
                && string.Equals(candidate, owned, StringComparison.OrdinalIgnoreCase))
            {
                reserved = owned;
                isOwned = true;
            }

            var storedPath = Path.Combine(relativeParent, reserved);
            if (isOwned || (!_stored.Contains(storedPath) && !_folders.Contains(storedPath)))
            {
                _folders.Add(storedPath);
                return reserved;
            }
        }
    }

    private string FreePath(string relativeFolder, string preferredFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(preferredFileName);
        var extension = Path.GetExtension(preferredFileName);
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? preferredFileName : $"{stem} ({attempt}){extension}";
            var storedPath = Path.Combine(relativeFolder, candidate);
            if (!_stored.Contains(storedPath))
            {
                return storedPath;
            }
        }
    }
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
        InMemoryCalendarBlockRepository blocks,
        InMemoryTaskRepository tasks,
        IClock clock,
        InMemoryPlanningProposalRepository? proposals = null)
    {
        blocks.Tasks = tasks;
        var completions = new InMemoryOccurrenceCompletionRepository();
        return new CalendarService(
            blocks, completions,
            new InMemoryCalendarMutations(blocks, completions, tasks, proposals),
            tasks, clock);
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
            new InboxQueryService(tasks, blocks),
            new PrioritySortService(ranks ?? new InMemoryPrioritizationRepository(), clock),
            aiService);
    }

    public static ShellViewModel Create(
        InMemorySettingsStore? store = null,
        InMemoryTaskRepository? tasks = null,
        InMemoryCalendarBlockRepository? blocks = null,
        InMemoryProjectRepository? projects = null,
        InMemoryOccurrenceCompletionRepository? completions = null,
        DateOnly? today = null,
        InMemoryPrioritizationRepository? ranks = null,
        BeBoosted.Desktop.Platform.IFileRevealService? reveal = null,
        IResourceStorage? resourceStorage = null,
        IProjectMutations? projectMutations = null)
    {
        var settingsStore = store ?? new InMemorySettingsStore();
        var settings = new AppSettings(settingsStore);
        var clock = new FakeClock(today ?? DesignDate);
        var repository = tasks ?? new InMemoryTaskRepository();
        var blockRepository = blocks ?? new InMemoryCalendarBlockRepository();
        blockRepository.Tasks = repository;
        var completionRepository = completions ?? new InMemoryOccurrenceCompletionRepository();
        var taskService = new TaskService(repository, clock);
        var proposalRepository = new InMemoryPlanningProposalRepository();
        var calendarMutations = new InMemoryCalendarMutations(
            blockRepository, completionRepository, repository, proposalRepository);
        var calendarService = new CalendarService(
            blockRepository, completionRepository, calendarMutations, repository, clock);
        var inboxQuery = new InboxQueryService(repository, blockRepository);
        var prioritization = ranks ?? new InMemoryPrioritizationRepository();
        var prioritySort = new PrioritySortService(prioritization, clock);
        var planning = new PlanningService(
            proposalRepository,
            inboxQuery, prioritization, calendarService, calendarMutations, clock);
        var projectRepo = projects ?? new InMemoryProjectRepository();
        var fileRepo = new InMemoryProjectFileRepository();
        var resourceRepo = new InMemoryResourceRepository();
        var groupRepo = new InMemoryResourceGroupRepository();

        // The service now leaves child rows to the database's ON DELETE CASCADE, so the
        // doubles have to model it or they model the very bug this seam prevents.
        projectRepo.Files = fileRepo;
        fileRepo.Resources = resourceRepo;
        fileRepo.Groups = groupRepo;
        groupRepo.Resources = resourceRepo;
        var storage = resourceStorage ?? new FakeResourceStorage();
        var aiPermissions = new AiPermissionSettings(settingsStore);
        var aiProvider = new BeBoosted.Infrastructure.Ai.LocalHeuristicAiProvider(resourceRepo, projectRepo);
        var aiService = new AiService(
            aiProvider, new InMemoryAiProvenanceRepository(), repository, aiPermissions, clock);
        var projectService = new ProjectService(
            projectRepo, fileRepo, resourceRepo, storage,
            projectMutations
                ?? new InMemoryProjectMutations(projectRepo, fileRepo, resourceRepo, repository, groupRepo),
            new FakeIndexer(resourceRepo, clock), repository, blockRepository,
            completionRepository, clock, groupRepo, aiService);
        return new ShellViewModel(
            new CalendarViewModel(
                settings, clock, calendarService, repository, planning, projectRepo,
                inboxQuery, prioritySort, aiService),
            new InboxViewModel(taskService, calendarService, inboxQuery, projectRepo, aiService, clock),
            new ProjectsViewModel(
                projectService, projectRepo, fileRepo, resourceRepo,
                calendarService, reveal ?? new FakeFileReveal(), aiService),
            new SettingsViewModel(new FakePaths(), aiPermissions),
            new ChatViewModel(aiService, aiPermissions, clock),
            prioritySort,
            aiPermissions,
            new BeBoosted.Desktop.Platform.DefaultKeymapService(),
            clock);
    }

    /// <summary>
    /// Seeds the calendar content of design frames 01/02 on the unified model: a
    /// repeating weekday class, one-off scheduled tasks across the week, two work
    /// sessions on the design date, and one completed morning session.
    /// </summary>
    public static void SeedDesignCalendar(
        InMemoryTaskRepository tasks,
        InMemoryCalendarBlockRepository blocks,
        IClock clock)
    {
        var now = clock.Now;
        var tue = DesignDate;

        void AddScheduled(string title, DateOnly date, TimeOnly start, TimeOnly end,
            BeBoosted.Domain.Scheduling.RecurrenceRule? recurrence = null)
        {
            var task = TaskItem.Create(title, now);
            tasks.Add(task);
            blocks.Add(CalendarBlock.CreateTaskSession(task.Id, date, start, end, now, recurrence));
        }

        AddScheduled("AP Economics", tue.AddDays(-8), new TimeOnly(8, 30), new TimeOnly(9, 45),
            BeBoosted.Domain.Scheduling.RecurrenceRule.Weekly(
                1, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday));
        AddScheduled("Lunch", tue, new TimeOnly(12, 0), new TimeOnly(12, 45));
        AddScheduled("DECA club meeting", tue.AddDays(1), new TimeOnly(15, 30), new TimeOnly(17, 0));
        AddScheduled("SAT practice test", tue.AddDays(4), new TimeOnly(10, 0), new TimeOnly(12, 0));
        AddScheduled("Family dinner", tue.AddDays(5), new TimeOnly(18, 0), new TimeOnly(19, 30));

        var practice = TaskItem.Create("Practice DECA role-play", now, estimatedDuration: TimeSpan.FromMinutes(90));
        var statement = TaskItem.Create("Draft personal statement", now, estimatedDuration: TimeSpan.FromMinutes(60));
        var reading = TaskItem.Create("Morning reading — econ chapter 6", now, estimatedDuration: TimeSpan.FromMinutes(40));
        tasks.Add(practice);
        tasks.Add(statement);
        tasks.Add(reading);

        blocks.Add(CalendarBlock.CreateTaskSession(practice.Id, tue, new TimeOnly(15, 30), new TimeOnly(17, 0), now));
        blocks.Add(CalendarBlock.CreateTaskSession(statement.Id, tue, new TimeOnly(19, 0), new TimeOnly(20, 0), now));

        var readingBlock = CalendarBlock.CreateTaskSession(reading.Id, tue, new TimeOnly(7, 10), new TimeOnly(7, 50), now);
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
