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
    private readonly SqliteResourceGroupRepository _groups;
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
        _groups = new SqliteResourceGroupRepository(_database.Factory);
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
            new SqliteProjectMutations(_database.Factory),
            new SimpleLocalIndexer(_resources, storage, _clock),
            _tasks, new SqliteCalendarBlockRepository(_database.Factory),
            new SqliteOccurrenceCompletionRepository(_database.Factory), _clock,
            _groups, _service);

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

    /// <summary>
    /// A File takes every resource it held with it, so every answer derived from any of
    /// them must be flagged. Two separately cited notes catch a loop that invalidates
    /// only the first.
    /// </summary>
    [Fact]
    public async Task DeletingAFile_FlagsDerivedItemsOfEveryResourceItHeld()
    {
        _projectService.AddNote(_file.Id, "Leadership metrics", "Led three DECA teams.");
        _projectService.AddNote(_file.Id, "Robotics awards", "Won regional robotics championship.");
        var leadership = await _service.AskProjectAsync(
            _project.Id, "What leadership metrics do I have?", TestContext.Current.CancellationToken);
        var robotics = await _service.AskProjectAsync(
            _project.Id, "What robotics championship results do I have?", TestContext.Current.CancellationToken);
        Assert.Single(leadership.Citations);
        Assert.Single(robotics.Citations);

        _projectService.DeleteFile(_file.Id);

        Assert.True(_provenance.GetById(leadership.Answer.ProvenanceId)!.NeedsReview);
        Assert.True(_provenance.GetById(robotics.Answer.ProvenanceId)!.NeedsReview);
    }

    /// <summary>The same, one level up: a deleted project flags derivations across its Files.</summary>
    [Fact]
    public async Task DeletingAProject_FlagsDerivedItemsAcrossItsFiles()
    {
        var awards = _projectService.CreateFile(_project.Id, "Awards", null);
        _projectService.AddNote(_file.Id, "Leadership metrics", "Led three DECA teams.");
        _projectService.AddNote(awards.Id, "Robotics awards", "Won regional robotics championship.");
        var leadership = await _service.AskProjectAsync(
            _project.Id, "What leadership metrics do I have?", TestContext.Current.CancellationToken);
        var robotics = await _service.AskProjectAsync(
            _project.Id, "What robotics championship results do I have?", TestContext.Current.CancellationToken);
        Assert.Single(leadership.Citations);
        Assert.Single(robotics.Citations);

        _projectService.DeleteProject(_project.Id);

        Assert.True(_provenance.GetById(leadership.Answer.ProvenanceId)!.NeedsReview);
        Assert.True(_provenance.GetById(robotics.Answer.ProvenanceId)!.NeedsReview);
    }

    /// <summary>
    /// Two notes in one group, each cited by its own answer. The recording invalidator in
    /// ResourceGroupRemovalTests proves <c>DeleteGroup</c> makes the calls; this proves the
    /// calls mean something — both answers really do come back flagged "Needs review",
    /// through the live provenance repository rather than a double.
    ///
    /// Two of them, deliberately: one would pass against a loop that flags only the first.
    /// </summary>
    [Fact]
    public async Task DeletingAGroup_FlagsDerivedItemsOfEveryMemberItHeld()
    {
        var (group, leadershipNote, roboticsNote, leadership, robotics) =
            await SeedAGroupWithTwoCitedNotes();

        _projectService.DeleteGroup(group.Id);

        Assert.Null(_groups.GetById(group.Id));
        Assert.Null(_resources.GetById(leadershipNote.Id));
        Assert.Null(_resources.GetById(roboticsNote.Id));
        Assert.True(_provenance.GetById(leadership.Answer.ProvenanceId)!.NeedsReview);
        Assert.True(_provenance.GetById(robotics.Answer.ProvenanceId)!.NeedsReview);
    }

    /// <summary>
    /// The mirror image, and the reason the two removals are separate actions at all.
    /// Ungroup destroys nothing: the notes survive, loose in the File, with the same text
    /// every answer cited. Flagging them anyway would mark live answers "Needs review" for
    /// a filing change and leave the user clearing noise by hand.
    /// </summary>
    [Fact]
    public async Task UngroupingAGroup_LeavesEveryDerivedItemAlone()
    {
        var (group, leadershipNote, roboticsNote, leadership, robotics) =
            await SeedAGroupWithTwoCitedNotes();

        _projectService.UngroupGroup(group.Id);

        Assert.Null(_groups.GetById(group.Id));
        Assert.Null(_resources.GetById(leadershipNote.Id)!.GroupId);
        Assert.Null(_resources.GetById(roboticsNote.Id)!.GroupId);
        Assert.Equal("Led three DECA teams.", _resources.GetById(leadershipNote.Id)!.Content);
        Assert.False(_provenance.GetById(leadership.Answer.ProvenanceId)!.NeedsReview);
        Assert.False(_provenance.GetById(robotics.Answer.ProvenanceId)!.NeedsReview);
    }

    /// <summary>
    /// Deleting a File still takes everything in it, now that some of it may sit in a
    /// group: the group row goes with the File through the foreign-key cascade, and the
    /// grouped member is flagged exactly like the loose one. The File's own delete snapshot
    /// is taken from every resource it holds regardless of membership — a snapshot narrowed
    /// to loose resources would leave the grouped member's answer citing a source that no
    /// longer exists, never flagged.
    /// </summary>
    [Fact]
    public async Task DeletingAFile_WithAGroupedAndALooseResource_RemovesTheGroupAndFlagsBoth()
    {
        var group = _projectService.CreateGroup(_file.Id, "Unit 3");
        var grouped = _projectService.AddNote(_file.Id, "Leadership metrics", "Led three DECA teams.");
        _projectService.MoveResourceToGroup(grouped.Id, group.Id);
        var loose = _projectService.AddNote(
            _file.Id, "Robotics awards", "Won regional robotics championship.");
        var (leadership, robotics) = await AskAboutBoth();

        _projectService.DeleteFile(_file.Id);

        Assert.Null(_groups.GetById(group.Id));
        Assert.Empty(_groups.GetForFile(_file.Id));
        Assert.Null(_resources.GetById(grouped.Id));
        Assert.Null(_resources.GetById(loose.Id));
        Assert.True(_provenance.GetById(leadership.Answer.ProvenanceId)!.NeedsReview);
        Assert.True(_provenance.GetById(robotics.Answer.ProvenanceId)!.NeedsReview);
    }

    /// <summary>
    /// The same one level up, with the task contract that only the project delete carries:
    /// its groups and resources go, every member's derivations are flagged, and its tasks
    /// survive — unlinked, not deleted. Groups change none of that.
    /// </summary>
    [Fact]
    public async Task DeletingAProject_WithAGroupedAndALooseResource_FlagsBothAndUnlinksItsTasks()
    {
        var group = _projectService.CreateGroup(_file.Id, "Unit 3");
        var grouped = _projectService.AddNote(_file.Id, "Leadership metrics", "Led three DECA teams.");
        _projectService.MoveResourceToGroup(grouped.Id, group.Id);
        var loose = _projectService.AddNote(
            _file.Id, "Robotics awards", "Won regional robotics championship.");
        var task = TaskItem.Create("Essay", _clock.Now, projectId: _project.Id);
        _tasks.Add(task);
        var (leadership, robotics) = await AskAboutBoth();

        _projectService.DeleteProject(_project.Id);

        Assert.Null(_groups.GetById(group.Id));
        Assert.Null(_resources.GetById(grouped.Id));
        Assert.Null(_resources.GetById(loose.Id));
        Assert.True(_provenance.GetById(leadership.Answer.ProvenanceId)!.NeedsReview);
        Assert.True(_provenance.GetById(robotics.Answer.ProvenanceId)!.NeedsReview);
        var survivor = Assert.IsType<TaskItem>(_tasks.GetById(task.Id));
        Assert.Null(survivor.ProjectId);
    }

    /// <summary>
    /// One group holding two notes, each already cited by an answer of its own — the
    /// starting state both group-removal tests need.
    /// </summary>
    private async Task<(ResourceGroup Group, Resource Leadership, Resource Robotics,
        ProjectAnswerOutcome LeadershipAnswer, ProjectAnswerOutcome RoboticsAnswer)>
        SeedAGroupWithTwoCitedNotes()
    {
        var group = _projectService.CreateGroup(_file.Id, "Unit 3");
        var leadershipNote = _projectService.AddNote(
            _file.Id, "Leadership metrics", "Led three DECA teams.");
        var roboticsNote = _projectService.AddNote(
            _file.Id, "Robotics awards", "Won regional robotics championship.");
        _projectService.MoveResourceToGroup(leadershipNote.Id, group.Id);
        _projectService.MoveResourceToGroup(roboticsNote.Id, group.Id);
        var (leadership, robotics) = await AskAboutBoth();
        return (group, leadershipNote, roboticsNote, leadership, robotics);
    }

    /// <summary>
    /// One answer per note, each citing only its own. The single-citation assertions are
    /// what make the later "both flagged" assertions mean two separate derivations rather
    /// than one answer counted twice.
    /// </summary>
    private async Task<(ProjectAnswerOutcome Leadership, ProjectAnswerOutcome Robotics)> AskAboutBoth()
    {
        var leadership = await _service.AskProjectAsync(
            _project.Id, "What leadership metrics do I have?", TestContext.Current.CancellationToken);
        var robotics = await _service.AskProjectAsync(
            _project.Id, "What robotics championship results do I have?", TestContext.Current.CancellationToken);
        Assert.Single(leadership.Citations);
        Assert.Single(robotics.Citations);
        Assert.False(_provenance.GetById(leadership.Answer.ProvenanceId)!.NeedsReview);
        Assert.False(_provenance.GetById(robotics.Answer.ProvenanceId)!.NeedsReview);
        return (leadership, robotics);
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
