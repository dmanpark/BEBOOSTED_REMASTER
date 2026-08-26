using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Projects;
using BeBoosted.Application.Settings;
using BeBoosted.Domain.Ai;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Settings;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Ai;

public sealed class AiServiceTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    private sealed class TestPaths : IAppDataPaths
    {
        public TestPaths() => DataDirectory = Path.Combine(Path.GetTempPath(), $"bb-ai-{Guid.NewGuid():N}");

        public string DataDirectory { get; }

        public string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");
    }

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteResourceRepository _resources;
    private readonly SqliteAiProvenanceRepository _provenance;
    private readonly SqliteSettingsStore _settings;
    private readonly AiPermissionSettings _permissions;
    private readonly AiService _service;
    private readonly ProjectService _projectService;
    private readonly Project _project;
    private readonly ProjectFile _file;

    public AiServiceTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _tasks = new SqliteTaskRepository(_database.Factory);
        _resources = new SqliteResourceRepository(_database.Factory);
        _provenance = new SqliteAiProvenanceRepository(_database.Factory);
        _settings = new SqliteSettingsStore(_database.Factory);
        _permissions = new AiPermissionSettings(_settings);
        var projects = new SqliteProjectRepository(_database.Factory);
        var files = new SqliteProjectFileRepository(_database.Factory);
        var provider = new LocalHeuristicAiProvider(_resources, projects);
        _service = new AiService(provider, _provenance, _tasks, _permissions, _clock);
        var storage = new LocalResourceStorage(new TestPaths());
        _projectService = new ProjectService(
            projects, files, _resources, storage,
            new SimpleLocalIndexer(_resources, storage, _clock),
            _tasks, new SqliteCalendarBlockRepository(_database.Factory),
            new SqliteOccurrenceCompletionRepository(_database.Factory), _clock, _service);

        _project = _projectService.CreateProject("College Admissions");
        _file = _projectService.CreateFile(_project.Id, "Metric Proof", null);
    }

    private AiContext Context() => new(_project.Id, _clock.Today);

    [Fact]
    public async Task ReviewFirstIsTheDefault_NothingIsAddedWithoutApproval()
    {
        var outcome = await _service.ExtractTasksAsync("Draft the essay outline before Sunday", Context(), TestContext.Current.CancellationToken);

        Assert.False(outcome.AddedAutomatically);
        Assert.Single(outcome.Drafts);
        Assert.Empty(_tasks.GetAll());
    }

    [Fact]
    public async Task AcceptDrafts_CreatesAiTasksWithSharedProvenance()
    {
        var outcome = await _service.ExtractTasksAsync(
            "Draft the essay outline. Also email the recommendation request.", Context(), TestContext.Current.CancellationToken);
        var added = _service.AcceptDrafts(outcome.Drafts);

        Assert.Equal(2, added.Count);
        var persisted = _tasks.GetAll();
        Assert.All(persisted, task => Assert.Equal(TaskOrigin.Ai, task.Origin));
        Assert.All(persisted, task => Assert.NotNull(task.ProvenanceId));
        Assert.Single(persisted.Select(t => t.ProvenanceId).Distinct());
        Assert.NotNull(_provenance.GetById(persisted[0].ProvenanceId!.Value));
    }

    [Fact]
    public async Task AutoAddPermission_AddsImmediately_ButKeepsOriginAndProvenance()
    {
        _permissions.TaskCapture = TaskCapturePermission.AddAutomatically;

        var outcome = await _service.ExtractTasksAsync("Review the economics chapter", Context(), TestContext.Current.CancellationToken);

        Assert.True(outcome.AddedAutomatically);
        var task = Assert.Single(_tasks.GetAll());
        Assert.Equal(TaskOrigin.Ai, task.Origin);
        Assert.NotNull(task.ProvenanceId);
    }

    [Fact]
    public async Task AskProject_PersistsAnswerWithExactCitations()
    {
        var note = _projectService.AddNote(_file.Id, "Leadership metrics", "Led three DECA teams.");

        var outcome = await _service.AskProjectAsync(_project.Id, "What leadership metrics do I have?", TestContext.Current.CancellationToken);

        var citation = Assert.Single(outcome.Citations);
        Assert.Equal(note.Id, citation.Id);
        var stored = Assert.Single(_provenance.GetAnswersForProject(_project.Id));
        Assert.Equal(outcome.Answer.Id, stored.Id);
        var record = _provenance.GetById(stored.ProvenanceId)!;
        Assert.Equal(AiOperationKind.ProjectAnswer, record.Operation);
        Assert.Equal([note.Id], record.SourceResourceIds);
        Assert.False(record.NeedsReview);
    }

    [Fact]
    public async Task EditingACitedNote_FlagsTheAnswerNeedsReview()
    {
        var note = _projectService.AddNote(_file.Id, "Leadership metrics", "Led three DECA teams.");
        await _service.AskProjectAsync(_project.Id, "What leadership metrics do I have?", TestContext.Current.CancellationToken);

        _projectService.UpdateNote(note.Id, "Completely different content now.");

        var derivations = _service.GetDerivations(note.Id);
        var derivation = Assert.Single(derivations);
        Assert.StartsWith("Cited in", derivation.Title, StringComparison.Ordinal);
        Assert.True(derivation.NeedsReview);
    }

    [Fact]
    public async Task DeletingACitedResource_FlagsDerivedItems()
    {
        var note = _projectService.AddNote(_file.Id, "Leadership metrics", "Led three DECA teams.");
        var outcome = await _service.AskProjectAsync(_project.Id, "What leadership metrics do I have?", TestContext.Current.CancellationToken);

        _projectService.DeleteResource(note.Id);

        var record = _provenance.GetById(outcome.Answer.ProvenanceId)!;
        Assert.True(record.NeedsReview);
    }

    [Fact]
    public void TaskNeedsReview_FollowsProvenanceState()
    {
        var record = AiProvenance.Create(
            AiOperationKind.TaskExtraction, [BeBoosted.Domain.ResourceId.New()], _clock.Now);
        _provenance.Add(record);
        var task = TaskItem.Create("Derived", _clock.Now, TaskOrigin.Ai, provenanceId: record.Id);
        _tasks.Add(task);

        Assert.False(_service.TaskNeedsReview(task));

        _service.InvalidateForResource(record.SourceResourceIds[0]);
        Assert.True(_service.TaskNeedsReview(task));

        var derivation = Assert.Single(_service.GetDerivations(record.SourceResourceIds[0]));
        Assert.StartsWith("Used by", derivation.Title, StringComparison.Ordinal);
    }

    public void Dispose() => _database.Dispose();
}
