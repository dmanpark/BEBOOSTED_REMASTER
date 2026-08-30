using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Projects;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Projects;

public sealed class ProjectServiceTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private sealed class TestPaths : IAppDataPaths, IDisposable
    {
        public TestPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"beboosted-projtest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DataDirectory);
        }

        public string DataDirectory { get; }

        public string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");

        public void Dispose()
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private readonly TempDatabase _database = new();
    private readonly TestPaths _paths = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteProjectRepository _projects;
    private readonly SqliteProjectFileRepository _files;
    private readonly SqliteResourceRepository _resources;
    private readonly LocalResourceStorage _storage;
    private readonly SqliteTaskRepository _tasks;
    private readonly ProjectService _service;

    private readonly SqliteOccurrenceCompletionRepository _completions;

    public ProjectServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _projects = new SqliteProjectRepository(_database.Factory);
        _files = new SqliteProjectFileRepository(_database.Factory);
        _resources = new SqliteResourceRepository(_database.Factory);
        _storage = new LocalResourceStorage(_paths);
        _tasks = new SqliteTaskRepository(_database.Factory);
        _completions = new SqliteOccurrenceCompletionRepository(_database.Factory);
        var blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _service = new ProjectService(
            _projects, _files, _resources, _storage, new SqliteProjectMutations(_database.Factory),
            new SimpleLocalIndexer(_resources, _storage, _clock), _tasks, blocks, _completions, _clock,
            provenanceInvalidator: null,
            reconciler: new ResourceLayoutReconciler(_projects, _files, _resources, _storage, _clock));
    }

    /// <summary>The same service, with the mutations seam swapped for a failing double.</summary>
    private ProjectService CreateServiceWith(
        IProjectMutations mutations, IProvenanceInvalidator? invalidator = null)
        => new(
            _projects, _files, _resources, _storage, mutations,
            new SimpleLocalIndexer(_resources, _storage, _clock), _tasks,
            new SqliteCalendarBlockRepository(_database.Factory), _completions, _clock,
            invalidator);

    /// <summary>
    /// Runs the real mutation inside the real transaction, then throws before commit —
    /// so the callback's writes are genuinely rolled back, not merely never attempted.
    /// </summary>
    private sealed class FailAfterMutation(SqliteConnectionFactory factory) : IProjectMutations
    {
        public void Execute(
            Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository> mutation)
        {
            using var connection = factory.Open();
            using var transaction = connection.BeginTransaction();
            mutation(
                new SqliteProjectRepository(connection, transaction),
                new SqliteProjectFileRepository(connection, transaction),
                new SqliteResourceRepository(connection, transaction),
                new SqliteTaskRepository(connection, transaction));
            throw new InvalidOperationException("injected failure");
        }
    }

    /// <summary>Records every invalidation so a test can assert none happened.</summary>
    private sealed class RecordingInvalidator : IProvenanceInvalidator
    {
        public List<ResourceId> Invalidated { get; } = [];

        public void InvalidateForResource(ResourceId resourceId) => Invalidated.Add(resourceId);
    }

    private CalendarService CreateCalendarService()
        => new(
            new SqliteCalendarBlockRepository(_database.Factory),
            new SqliteOccurrenceCompletionRepository(_database.Factory),
            new SqliteCalendarMutations(_database.Factory),
            _tasks, _clock);

    /// <summary>A project task scheduled through the unified editor path.</summary>
    private TaskId AddScheduledProjectTask(
        CalendarService calendar,
        string title,
        ProjectId projectId,
        DateOnly date,
        TimeOnly start,
        TimeOnly end,
        RecurrenceRule? recurrence = null)
        => calendar.CreateTask(
            new TaskDetailsRequest(title, projectId, null, null),
            new TaskScheduleRequest(date, start, end, recurrence)).Id;

    [Fact]
    public void CreateProject_AssignsPaletteAccentsRoundRobin()
    {
        var first = _service.CreateProject("College Admissions");
        var second = _service.CreateProject("DECA");

        Assert.Equal(ProjectPalette.Colors[0], first.AccentColor);
        Assert.Equal(ProjectPalette.Colors[1], second.AccentColor);
        Assert.Equal(2, _projects.GetAll().Count);
        Assert.Equal("College Admissions", _projects.GetById(first.Id)!.Name);
    }

    [Fact]
    public void FilesAndResources_RoundTripThroughSqlite()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", "Scores and certificates");

        var link = _service.AddLink(file.Id, "SAT Score Report", "https://collegeboard.org/scores");
        var note = _service.AddNote(file.Id, "Leadership metrics", "Led three DECA teams.");

        var loaded = _resources.GetForFile(file.Id);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(ResourceIndexState.Indexed, loaded[0].IndexState);
        Assert.Equal("https://collegeboard.org/scores", loaded.Single(r => r.Id == link.Id).Url);
        Assert.Equal("Led three DECA teams.", loaded.Single(r => r.Id == note.Id).Content);
        Assert.Equal(2, _service.CountResources(file.Id));
    }

    [Fact]
    public void ImportFile_CopiesBytesIntoStableStorage_AndDeleteRemovesThem()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");

        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);

        Assert.Equal("Transcript", resource.Title);
        Assert.Equal("Transcript.pdf", resource.OriginalFileName);
        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            resource.StoredPath);
        Assert.True(_storage.Exists(resource.StoredPath!));
        Assert.Equal(ResourceIndexState.Indexed, _resources.GetById(resource.Id)!.IndexState);

        _service.DeleteResource(resource.Id);
        Assert.False(_storage.Exists(resource.StoredPath!));
        Assert.Null(_resources.GetById(resource.Id));
    }

    [Fact]
    public void SearchInProject_FindsIndexedText_ScopedToTheProject()
    {
        var admissions = _service.CreateProject("College Admissions");
        var deca = _service.CreateProject("DECA");
        var admissionsFile = _service.CreateFile(admissions.Id, "Essay Research", null);
        var decaFile = _service.CreateFile(deca.Id, "Event Prep", null);
        _service.AddNote(admissionsFile.Id, "Essay themes", "Write about robotics leadership.");
        _service.AddNote(decaFile.Id, "Role-play notes", "Practice marketing scenarios and leadership.");

        var matches = _resources.SearchInProject(admissions.Id, "leadership");

        Assert.Single(matches);
        Assert.Equal("Essay themes", matches[0].Title);
    }

    [Fact]
    public void DeleteProject_CascadesFilesAndResources_AndUnlinksTasks()
    {
        var project = _service.CreateProject("DECA");
        var file = _service.CreateFile(project.Id, "Event Prep", null);
        var note = _service.AddNote(file.Id, "Notes", "content");
        var task = TaskItem.Create("Practice", _clock.Now, projectId: project.Id);
        _tasks.Add(task);

        _service.DeleteProject(project.Id);

        Assert.Null(_projects.GetById(project.Id));
        Assert.Empty(_files.GetForProject(project.Id));
        Assert.Null(_resources.GetById(note.Id));
        Assert.Null(_tasks.GetById(task.Id)!.ProjectId);
    }

    [Fact]
    public void GetProjectTasks_SplitsOpenAndRecentlyCompleted()
    {
        var project = _service.CreateProject("DECA");
        var open = TaskItem.Create("Open work", _clock.Now, projectId: project.Id);
        var done = TaskItem.Create("Done work", _clock.Now, projectId: project.Id);
        done.Complete(_clock.Now.AddHours(1));
        _tasks.Add(open);
        _tasks.Add(done);

        var (openTasks, recent) = _service.GetProjectTasks(project.Id);

        Assert.Single(openTasks);
        Assert.Equal("Open work", openTasks[0].Title);
        Assert.Single(recent);
        Assert.Equal("Done work", recent[0].Title);
    }

    [Fact]
    public void GetScheduledBlocks_ListsSessionsOfProjectTasks_UpcomingAndOverdue()
    {
        var project = _service.CreateProject("DECA");
        var task = TaskItem.Create("Practice", _clock.Now, estimatedDuration: TimeSpan.FromMinutes(60), projectId: project.Id);
        _tasks.Add(task);
        var calendar = CreateCalendarService();
        calendar.ScheduleTask(task.Id, _clock.Today, new TimeOnly(18, 0));         // future
        calendar.ScheduleTask(task.Id, _clock.Today, new TimeOnly(9, 0));          // elapsed

        var scheduled = _service.GetScheduledBlocks(project.Id);

        // Elapsed incomplete sessions never disappear — they show as Overdue,
        // sorted before the upcoming row by start time.
        Assert.Equal(2, scheduled.Count);
        Assert.Equal(new TimeOnly(9, 0), scheduled[0].Block.StartTime);
        Assert.Equal(ProjectBlockState.Overdue, scheduled[0].State);
        Assert.Equal(new TimeOnly(18, 0), scheduled[1].Block.StartTime);
        Assert.Equal("Practice", scheduled[1].Title);
        Assert.Equal(ProjectBlockState.Upcoming, scheduled[1].State);
    }

    [Fact]
    public void GetScheduledBlocks_ScopesToTheProjectsOwnTasks()
    {
        var project = _service.CreateProject("Schoolwork");
        var other = _service.CreateProject("DECA");
        var calendar = CreateCalendarService();
        AddScheduledProjectTask(
            calendar, "Stats HW", project.Id,
            _clock.Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0));
        AddScheduledProjectTask(
            calendar, "Other club", other.Id,
            _clock.Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0));
        calendar.CreateTask(
            new TaskDetailsRequest("Unlinked", null, null, null),
            new TaskScheduleRequest(
                _clock.Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0), null));

        var scheduled = _service.GetScheduledBlocks(project.Id);

        var row = Assert.Single(scheduled);
        Assert.Equal("Stats HW", row.Title);
        Assert.Equal(ProjectBlockState.Upcoming, row.State);
    }

    [Fact]
    public void GetScheduledBlocks_ShowsCompletedSessions_BelowActiveOnes()
    {
        var project = _service.CreateProject("Schoolwork");
        var calendar = CreateCalendarService();
        var doneTaskId = AddScheduledProjectTask(
            calendar, "Stats HW", project.Id, _clock.Today, new TimeOnly(9, 0), new TimeOnly(10, 0));
        calendar.UpdateTaskDetails(
            doneTaskId, new TaskDetailsRequest("Stats HW", project.Id, null, null),
            new TaskCompletionRequest(_clock.Today, Completed: true));
        AddScheduledProjectTask(
            calendar, "Essay draft", project.Id,
            _clock.Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0));

        var scheduled = _service.GetScheduledBlocks(project.Id);

        Assert.Equal(2, scheduled.Count);
        Assert.Equal("Essay draft", scheduled[0].Title);
        Assert.Equal(ProjectBlockState.Upcoming, scheduled[0].State);
        Assert.Equal("Stats HW", scheduled[1].Title);
        Assert.Equal(ProjectBlockState.Done, scheduled[1].State);
    }

    [Fact]
    public void GetScheduledBlocks_KeepsRepeatingSessionsSparse_PerOccurrence()
    {
        var project = _service.CreateProject("Schoolwork");
        var calendar = CreateCalendarService();

        // Anchored two weeks ago, every Wednesday. Today is Tuesday 2026-08-11:
        // elapsed occurrences on 7/29 and 8/5, next occurrence tomorrow (8/12).
        var taskId = AddScheduledProjectTask(
            calendar, "AP Economics", project.Id,
            _clock.Today.AddDays(-14), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        var sessionId = new SqliteCalendarBlockRepository(_database.Factory)
            .GetForTask(taskId).Single().Id;

        var scheduled = _service.GetScheduledBlocks(project.Id);

        // Only the most recent elapsed occurrence and the next upcoming one.
        Assert.Equal(2, scheduled.Count);
        Assert.Equal(_clock.Today.AddDays(-6), scheduled[0].Date);
        Assert.Equal(ProjectBlockState.Overdue, scheduled[0].State);
        Assert.Equal(_clock.Today.AddDays(1), scheduled[1].Date);
        Assert.Equal(ProjectBlockState.Upcoming, scheduled[1].State);

        // Completing one occurrence moves only that occurrence to Done.
        calendar.CompleteOccurrence(sessionId, _clock.Today.AddDays(-6));
        scheduled = _service.GetScheduledBlocks(project.Id);
        Assert.Equal(2, scheduled.Count);
        Assert.Equal(ProjectBlockState.Upcoming, scheduled[0].State);
        Assert.Equal(_clock.Today.AddDays(1), scheduled[0].Date);
        Assert.Equal(ProjectBlockState.Done, scheduled[1].State);
        Assert.Equal(_clock.Today.AddDays(-6), scheduled[1].Date);
    }

    [Fact]
    public void ProjectAssignment_SurvivesApplicationRestart()
    {
        // Session 1: create the project and its scheduled task, then drop every service.
        var project = _service.CreateProject("Schoolwork");
        AddScheduledProjectTask(
            CreateCalendarService(), "Stats HW", project.Id,
            _clock.Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0));

        // Session 2: a brand-new service graph over the same database file.
        var projects2 = new SqliteProjectRepository(_database.Factory);
        var blocks2 = new SqliteCalendarBlockRepository(_database.Factory);
        var tasks2 = new SqliteTaskRepository(_database.Factory);
        var service2 = new ProjectService(
            projects2, new SqliteProjectFileRepository(_database.Factory),
            new SqliteResourceRepository(_database.Factory), _storage,
            new SqliteProjectMutations(_database.Factory),
            new SimpleLocalIndexer(new SqliteResourceRepository(_database.Factory), _storage, _clock),
            tasks2, blocks2, new SqliteOccurrenceCompletionRepository(_database.Factory), _clock);

        var reloaded = projects2.GetAll().Single(p => p.Name == "Schoolwork");
        var scheduled = service2.GetScheduledBlocks(reloaded.Id);
        var row = Assert.Single(scheduled);
        Assert.Equal("Stats HW", row.Title);
        Assert.Equal(reloaded.Id, tasks2.GetById(row.Block.TaskId!.Value)!.ProjectId);
    }

    [Fact]
    public void DeleteProject_UnlinksTasks_WithoutDeletingThemOrTheirSessions()
    {
        var project = _service.CreateProject("Schoolwork");
        var blocks = new SqliteCalendarBlockRepository(_database.Factory);
        var calendar = CreateCalendarService();
        var taskId = AddScheduledProjectTask(
            calendar, "Stats HW", project.Id,
            _clock.Today.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 0));

        _service.DeleteProject(project.Id);

        var task = _tasks.GetById(taskId);
        Assert.NotNull(task);
        Assert.Null(task.ProjectId);
        Assert.Single(blocks.GetForTask(taskId));
    }

    [Fact]
    public void MissingStoredBytes_MarkIndexingFailed()
    {
        var project = _service.CreateProject("DECA");
        var file = _service.CreateFile(project.Id, "Event Prep", null);
        var source = Path.Combine(_paths.DataDirectory, "cert.png");
        File.WriteAllText(source, "img");
        var resource = _service.ImportFile(file.Id, ResourceKind.Image, source);

        // Simulate the bytes disappearing, then re-index.
        _storage.Delete(resource.StoredPath!);
        var indexer = new SimpleLocalIndexer(_resources, _storage, _clock);
        var reloaded = _resources.GetById(resource.Id)!;
        indexer.Index(reloaded);
        _resources.Update(reloaded);

        Assert.Equal(ResourceIndexState.Failed, _resources.GetById(resource.Id)!.IndexState);
    }

    /// <summary>
    /// Two Projects named the same thing sanitize identically, but each must claim its
    /// own directory — never share one, and never silently overwrite the other's files.
    /// </summary>
    [Fact]
    public void CreateProject_TwoProjectsSharingASanitizedName_ClaimDifferentFolders()
    {
        var first = _service.CreateProject("DECA");
        var second = _service.CreateProject("DECA");

        Assert.Equal("DECA", first.FolderSegment);
        Assert.Equal("DECA (2)", second.FolderSegment);
        Assert.NotEqual(first.FolderSegment, second.FolderSegment);

        var firstFile = _service.CreateFile(first.Id, "Notes", null);
        var secondFile = _service.CreateFile(second.Id, "Notes", null);
        var firstSource = Path.Combine(_paths.DataDirectory, "a.pdf");
        File.WriteAllText(firstSource, "first project bytes");
        var secondSource = Path.Combine(_paths.DataDirectory, "b.pdf");
        File.WriteAllText(secondSource, "second project bytes");

        var firstResource = _service.ImportFile(firstFile.Id, ResourceKind.Document, firstSource);
        var secondResource = _service.ImportFile(secondFile.Id, ResourceKind.Document, secondSource);

        Assert.Equal(Path.Combine("DECA", "Notes", "a.pdf"), firstResource.StoredPath);
        Assert.Equal(Path.Combine("DECA (2)", "Notes", "b.pdf"), secondResource.StoredPath);
        Assert.Equal("first project bytes", File.ReadAllText(_storage.ResolvePath(firstResource.StoredPath!)));
        Assert.Equal("second project bytes", File.ReadAllText(_storage.ResolvePath(secondResource.StoredPath!)));
    }

    [Fact]
    public void RenameProject_MovesTheFolder_AndKeepsStoredPathsResolvable()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);

        _service.RenameProject(project.Id, "College Apps");

        var reloaded = _resources.GetById(resource.Id)!;
        Assert.Equal(
            Path.Combine("College Apps", "Metric Proof", "Transcript.pdf"),
            reloaded.StoredPath);
        Assert.True(_storage.Exists(reloaded.StoredPath!));
        Assert.Equal("fake pdf bytes", File.ReadAllText(_service.ResolveStoredPath(reloaded)!));
    }

    [Fact]
    public void RenameFile_MovesTheFolder_AndKeepsStoredPathsResolvable()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);

        _service.RenameFile(file.Id, "Evidence");

        Assert.Equal("Evidence", _files.GetById(file.Id)!.Title);
        var reloaded = _resources.GetById(resource.Id)!;
        Assert.Equal(
            Path.Combine("College Admissions", "Evidence", "Transcript.pdf"),
            reloaded.StoredPath);
        Assert.True(_storage.Exists(reloaded.StoredPath!));
        Assert.Equal("fake pdf bytes", File.ReadAllText(_service.ResolveStoredPath(reloaded)!));
    }

    [Fact]
    public void RenameFile_RejectsAnEmptyTitle()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);

        Assert.Throws<DomainException>(() => _service.RenameFile(file.Id, "   "));
        Assert.Equal("Metric Proof", _files.GetById(file.Id)!.Title);
    }

    /// <summary>
    /// A stored document's on-disk name comes from its original file name, never its
    /// title, so retitling it must leave the bytes exactly where they are.
    /// </summary>
    [Fact]
    public void RenameResource_RetitlesWithoutMovingTheBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);

        _service.RenameResource(resource.Id, "Final transcript");

        var reloaded = _resources.GetById(resource.Id)!;
        Assert.Equal("Final transcript", reloaded.Title);
        Assert.Equal(
            Path.Combine("College Admissions", "Metric Proof", "Transcript.pdf"),
            reloaded.StoredPath);
        Assert.Equal("fake pdf bytes", File.ReadAllText(_service.ResolveStoredPath(reloaded)!));
    }

    [Fact]
    public void RenameResource_RejectsAnEmptyTitle()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var link = _service.AddLink(file.Id, "SAT Scores", "https://collegeboard.org/scores");

        Assert.Throws<DomainException>(() => _service.RenameResource(link.Id, ""));
        Assert.Equal("SAT Scores", _resources.GetById(link.Id)!.Title);
    }

    /// <summary>
    /// The transaction is real: work done inside a mutation that then throws must leave
    /// no trace. This pins SqliteProjectMutations itself, independently of the service.
    /// </summary>
    [Fact]
    public void ProjectMutations_WhenTheMutationThrows_RollsBackEveryWrite()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var link = _service.AddLink(file.Id, "SAT", "https://collegeboard.org");

        var mutations = new SqliteProjectMutations(_database.Factory);

        Assert.Throws<InvalidOperationException>(() =>
            mutations.Execute((_, fileRepo, resourceRepo, _) =>
            {
                resourceRepo.Delete(link.Id);
                fileRepo.Delete(file.Id);
                throw new InvalidOperationException("injected failure");
            }));

        Assert.NotNull(new SqliteResourceRepository(_database.Factory).GetById(link.Id));
        Assert.NotNull(new SqliteProjectFileRepository(_database.Factory).GetById(file.Id));
    }

    /// <summary>
    /// Bytes go only after the transaction commits. A failed mutation must leave the
    /// file on disk — orphaned bytes are recoverable, a row pointing at a deleted file
    /// is not.
    /// </summary>
    [Fact]
    public void DeleteResource_WhenTheMutationFails_LeavesTheRowAndTheBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        var service = CreateServiceWith(new FailAfterMutation(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteResource(resource.Id));

        Assert.NotNull(_resources.GetById(resource.Id));
        Assert.True(_storage.Exists(storedPath));
        Assert.Equal("fake pdf bytes", File.ReadAllText(_storage.ResolvePath(storedPath)));
    }

    [Fact]
    public void DeleteFile_WhenTheMutationFails_LeavesTheFileItsResourcesAndTheirBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        var service = CreateServiceWith(new FailAfterMutation(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteFile(file.Id));

        Assert.NotNull(_files.GetById(file.Id));
        Assert.NotNull(_resources.GetById(resource.Id));
        Assert.True(_storage.Exists(storedPath));
    }

    /// <summary>
    /// The widest rollback: a failed project delete must leave the project, its File,
    /// its resources, their bytes, AND the task's project assignment exactly as they
    /// were. The task unlink shares the transaction, so it must roll back too.
    /// </summary>
    [Fact]
    public void DeleteProject_WhenTheMutationFails_LeavesEveryRowTheAssignmentAndTheBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        var task = TaskItem.Create("Essay", _clock.Now, projectId: project.Id);
        _tasks.Add(task);

        var service = CreateServiceWith(new FailAfterMutation(_database.Factory));

        Assert.Throws<InvalidOperationException>(() => service.DeleteProject(project.Id));

        Assert.NotNull(_projects.GetById(project.Id));
        Assert.NotNull(_files.GetById(file.Id));
        Assert.NotNull(_resources.GetById(resource.Id));
        Assert.True(_storage.Exists(storedPath));
        Assert.Equal(project.Id, _tasks.GetById(task.Id)!.ProjectId);
    }

    /// <summary>The happy path still removes the bytes — after the commit, not before.</summary>
    [Fact]
    public void DeleteFile_OnSuccess_RemovesTheRowsAndTheBytes()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var source = Path.Combine(_paths.DataDirectory, "Transcript.pdf");
        File.WriteAllText(source, "fake pdf bytes");
        var resource = _service.ImportFile(file.Id, ResourceKind.Document, source);
        var storedPath = _resources.GetById(resource.Id)!.StoredPath!;

        _service.DeleteFile(file.Id);

        Assert.Null(_files.GetById(file.Id));
        Assert.Null(_resources.GetById(resource.Id));
        Assert.False(_storage.Exists(storedPath));
    }

    /// <summary>
    /// Invalidation must not run ahead of a commit that never happened, or a
    /// rolled-back delete permanently marks live items "Needs review".
    /// </summary>
    [Fact]
    public void DeleteFile_WhenTheMutationFails_InvalidatesNothing()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        _service.AddLink(file.Id, "SAT", "https://collegeboard.org");
        _service.AddLink(file.Id, "ACT", "https://act.org");

        var recorder = new RecordingInvalidator();
        var service = CreateServiceWith(new FailAfterMutation(_database.Factory), recorder);

        Assert.Throws<InvalidOperationException>(() => service.DeleteFile(file.Id));

        Assert.Empty(recorder.Invalidated);
    }

    /// <summary>
    /// Nothing in the service deletes resource rows for a doomed File any more — the
    /// foreign key does. If cascades were ever disabled this fails loudly instead of
    /// silently leaking rows.
    /// </summary>
    [Fact]
    public void DeleteFile_CascadesToItsResourceRows()
    {
        var project = _service.CreateProject("College Admissions");
        var file = _service.CreateFile(project.Id, "Metric Proof", null);
        var link = _service.AddLink(file.Id, "SAT", "https://collegeboard.org");
        var note = _service.AddNote(file.Id, "Leadership", "Led three DECA teams.");

        _service.DeleteFile(file.Id);

        Assert.Null(_files.GetById(file.Id));
        Assert.Null(_resources.GetById(link.Id));
        Assert.Null(_resources.GetById(note.Id));
    }

    public void Dispose()
    {
        _database.Dispose();
        _paths.Dispose();
    }
}
