using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
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

    public ProjectServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _projects = new SqliteProjectRepository(_database.Factory);
        _files = new SqliteProjectFileRepository(_database.Factory);
        _resources = new SqliteResourceRepository(_database.Factory);
        _storage = new LocalResourceStorage(_paths);
        _tasks = new SqliteTaskRepository(_database.Factory);
        var blocks = new SqliteCalendarBlockRepository(_database.Factory);
        _service = new ProjectService(
            _projects, _files, _resources, _storage,
            new SimpleLocalIndexer(_resources, _storage, _clock), _tasks, blocks, _clock);
    }

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
        Assert.Equal(resource.Id + ".pdf", resource.StoredPath);
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
    public void GetUpcomingBlocks_ReturnsFuturePendingBlocksForProjectTasks()
    {
        var project = _service.CreateProject("DECA");
        var task = TaskItem.Create("Practice", _clock.Now, estimatedDuration: TimeSpan.FromMinutes(60), projectId: project.Id);
        _tasks.Add(task);
        var calendar = new CalendarService(new SqliteCalendarBlockRepository(_database.Factory), _tasks, _clock);
        calendar.ScheduleTask(task.Id, _clock.Today, new TimeOnly(18, 0));         // future
        calendar.ScheduleTask(task.Id, _clock.Today, new TimeOnly(9, 0));          // elapsed

        var upcoming = _service.GetUpcomingBlocks(project.Id);

        Assert.Single(upcoming);
        Assert.Equal(new TimeOnly(18, 0), upcoming[0].Block.StartTime);
        Assert.Equal("Practice", upcoming[0].Task.Title);
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

    public void Dispose()
    {
        _database.Dispose();
        _paths.Dispose();
    }
}
