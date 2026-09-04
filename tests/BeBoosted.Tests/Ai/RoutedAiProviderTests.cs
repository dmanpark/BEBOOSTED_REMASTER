using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Settings;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Ai;

/// <summary>
/// The router over real repositories, the way every other test in this project works —
/// this project has no in-memory doubles, and the routing logic is what is under test,
/// not the storage beneath it.
/// </summary>
public sealed class RoutedAiProviderTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public RoutedAiProviderTests()
        => new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());

    public void Dispose() => _database.Dispose();

    private sealed class StubCaptureModel(
        Func<CaptureRequest, IReadOnlyList<CaptureDraft>> extract) : ICaptureModel
    {
        public int Calls { get; private set; }

        public CaptureRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<CaptureDraft>> ExtractAsync(
            CaptureRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(extract(request));
        }

        public Task<CaptureDraft?> SuggestMetadataAsync(
            CaptureRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var drafts = extract(request);
            return Task.FromResult(drafts.Count > 0 ? drafts[0] : null);
        }
    }

    private (RoutedAiProvider Provider, CaptureModelSettings Settings, SqliteProjectRepository Projects)
        Create(ICaptureModel? claude = null, ICaptureModel? ollama = null)
    {
        var projects = new SqliteProjectRepository(_database.Factory);
        var resources = new SqliteResourceRepository(_database.Factory);
        var heuristic = new LocalHeuristicAiProvider(resources, projects);
        var settings = new CaptureModelSettings(new SqliteSettingsStore(_database.Factory));
        var provider = new RoutedAiProvider(
            heuristic,
            source => source switch
            {
                CaptureModelSource.Claude => claude,
                CaptureModelSource.Ollama => ollama,
                _ => null,
            },
            settings, projects);
        return (provider, settings, projects);
    }

    private static AiContext Context(ProjectId? activeProjectId = null)
        => new(activeProjectId, new DateOnly(2026, 9, 3));

    [Fact]
    public async Task WithTheHeuristicSelected_NoBackendIsCalled()
    {
        var claude = new StubCaptureModel(_ => [new CaptureDraft("Model draft", null, null, null)]);
        var (provider, _, _) = Create(claude: claude);

        var result = await provider.ExtractTasksAsync(
            "Draft the essay outline", Context(), TestContext.Current.CancellationToken);

        Assert.Equal(0, claude.Calls);
        Assert.Null(result.DegradedNotice);
        Assert.NotEmpty(result.Drafts);
    }

    [Fact]
    public async Task WithClaudeSelected_ItsDraftsAreUsed()
    {
        var claude = new StubCaptureModel(_ => [new CaptureDraft("Finish DECA presentation", 90, null, null)]);
        var (provider, settings, _) = Create(claude: claude);
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync(
            "finish deca thing", Context(), TestContext.Current.CancellationToken);

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("Finish DECA presentation", draft.Title);
        Assert.Equal(TimeSpan.FromMinutes(90), draft.EstimatedDuration);
        Assert.Null(result.DegradedNotice);
    }

    [Fact]
    public async Task AFailingBackend_FallsBackToTheHeuristic_AndSaysSo()
    {
        var claude = new StubCaptureModel(_ => throw new HttpRequestException("offline"));
        var (provider, settings, _) = Create(claude: claude);
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync(
            "Draft the essay outline", Context(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Drafts); // the capture is not lost
        Assert.NotNull(result.DegradedNotice);
        Assert.Contains("Claude", result.DegradedNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNoticeNamesOllama_WhenOllamaIsTheConfiguredSource()
    {
        var ollama = new StubCaptureModel(_ => throw new FormatException("not json"));
        var (provider, settings, _) = Create(ollama: ollama);
        settings.Source = CaptureModelSource.Ollama;

        var result = await provider.ExtractTasksAsync(
            "Draft the essay outline", Context(), TestContext.Current.CancellationToken);

        Assert.Contains("Ollama", result.DegradedNotice!, StringComparison.Ordinal);
    }

    /// <summary>The notice is chat copy, not a log line.</summary>
    [Fact]
    public async Task TheNoticeNeverLeaksTheRawExceptionMessage()
    {
        var ollama = new StubCaptureModel(
            _ => throw new HttpRequestException("SocketException: ECONNREFUSED 127.0.0.1:11434"));
        var (provider, settings, _) = Create(ollama: ollama);
        settings.Source = CaptureModelSource.Ollama;

        var result = await provider.ExtractTasksAsync(
            "Draft the essay outline", Context(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("ECONNREFUSED", result.DegradedNotice!, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", result.DegradedNotice!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfiguredSourceWithNoRegisteredBackend_FallsBack()
    {
        var (provider, settings, _) = Create(); // no backends supplied
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync(
            "Draft the essay outline", Context(), TestContext.Current.CancellationToken);

        Assert.NotNull(result.DegradedNotice);
    }

    [Fact]
    public async Task ProjectNamesGoOutAndAProjectIdComesBack()
    {
        var backend = new StubCaptureModel(
            _ => [new CaptureDraft("Finish DECA presentation", null, null, "Schoolwork")]);
        var (provider, settings, projects) = Create(claude: backend);
        var schoolwork = Project.Create("Schoolwork", "#5B8DEF", DateTimeOffset.UtcNow);
        projects.Add(schoolwork);
        settings.Source = CaptureModelSource.Claude;

        var result = await provider.ExtractTasksAsync(
            "finish deca", Context(), TestContext.Current.CancellationToken);

        // Out as a name, back as an id: the backend never handles domain identity.
        Assert.Contains("Schoolwork", backend.LastRequest!.ProjectNames);
        Assert.Equal(schoolwork.Id, Assert.Single(result.Drafts).ProjectId);
    }

    /// <summary>
    /// Deliberately set with an active project already open — the normal case while
    /// capturing inside a project. A vacuous version of this test (active project
    /// null) would pass even if a hallucinated name silently fell back to the active
    /// project, because there'd be no active project to fall back to. Here there is
    /// one, so the assertion only holds if the unmatched name truly resolves to no
    /// project rather than being conflated with "no name given."
    /// </summary>
    [Fact]
    public async Task AnUnknownProjectName_BecomesNoProject_EvenWithAnActiveProjectOpen()
    {
        var backend = new StubCaptureModel(
            _ => [new CaptureDraft("Task", null, null, "Nonexistent Project")]);
        var (provider, settings, projects) = Create(claude: backend);
        settings.Source = CaptureModelSource.Claude;
        var active = Project.Create("Schoolwork", "#5B8DEF", DateTimeOffset.UtcNow);
        projects.Add(active);

        var result = await provider.ExtractTasksAsync(
            "something", Context(active.Id), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(result.Drafts).ProjectId);
    }

    /// <summary>
    /// The other half of the same rule: a draft that names no project at all is not
    /// "unknown" — it inherits whatever project is already open. Pinned separately so
    /// the fix for the case above can't be over-corrected into dropping this fallback
    /// entirely.
    /// </summary>
    [Fact]
    public async Task NoProjectNamedAtAll_InheritsTheActiveProject()
    {
        var backend = new StubCaptureModel(
            _ => [new CaptureDraft("Task", null, null, null)]);
        var (provider, settings, projects) = Create(claude: backend);
        settings.Source = CaptureModelSource.Claude;
        var active = Project.Create("Schoolwork", "#5B8DEF", DateTimeOffset.UtcNow);
        projects.Add(active);

        var result = await provider.ExtractTasksAsync(
            "something", Context(active.Id), TestContext.Current.CancellationToken);

        Assert.Equal(active.Id, Assert.Single(result.Drafts).ProjectId);
    }

    /// <summary>Metadata fills an optional hint; a notice for it would be noise.</summary>
    [Fact]
    public async Task SuggestMetadata_DegradesSilently()
    {
        var claude = new StubCaptureModel(_ => throw new HttpRequestException("offline"));
        var (provider, settings, _) = Create(claude: claude);
        settings.Source = CaptureModelSource.Claude;

        var suggestion = await provider.SuggestMetadataAsync(
            "Email Ms. Rivera", Context(), TestContext.Current.CancellationToken);

        Assert.NotNull(suggestion); // the heuristic answered
    }

    [Fact]
    public async Task ProjectQuestions_AlwaysReachTheHeuristic()
    {
        var claude = new StubCaptureModel(_ => throw new InvalidOperationException("must not be called"));
        var (provider, settings, projects) = Create(claude: claude);
        settings.Source = CaptureModelSource.Claude;
        var project = Project.Create("Schoolwork", "#5B8DEF", DateTimeOffset.UtcNow);
        projects.Add(project);

        var answer = await provider.AnswerQuestionAsync(
            project.Id, "What did I write down?", TestContext.Current.CancellationToken);

        Assert.NotNull(answer);
        Assert.Equal(0, claude.Calls);
    }
}
