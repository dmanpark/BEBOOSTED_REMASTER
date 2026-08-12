using BeBoosted.Application.Ai;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Ai;

public sealed class LocalHeuristicAiProviderTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 8, 11); // Tuesday
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));

    private readonly TempDatabase _database = new();
    private readonly SqliteProjectRepository _projects;
    private readonly SqliteResourceRepository _resources;
    private readonly SqliteProjectFileRepository _files;
    private readonly LocalHeuristicAiProvider _provider;

    public LocalHeuristicAiProviderTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _projects = new SqliteProjectRepository(_database.Factory);
        _files = new SqliteProjectFileRepository(_database.Factory);
        _resources = new SqliteResourceRepository(_database.Factory);
        _provider = new LocalHeuristicAiProvider(_resources, _projects);
    }

    private AiContext Context(ProjectId? projectId = null) => new(projectId, Today);

    [Fact]
    public async Task ExtractTasks_ParsesTitlesDeadlinesAndDurations()
    {
        var drafts = await _provider.ExtractTasksAsync(
            "I need to finish my DECA presentation before Friday. "
            + "Also email the recommendation request to Ms. Rivera.",
            Context(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, drafts.Count);
        Assert.Equal("Finish my DECA presentation", drafts[0].Title);
        Assert.Equal(new DateOnly(2026, 8, 14), drafts[0].Deadline);        // this Friday
        Assert.Equal(TimeSpan.FromMinutes(90), drafts[0].EstimatedDuration); // "finish" heuristic
        Assert.Equal("Email the recommendation request to Ms. Rivera", drafts[1].Title);
        Assert.Equal(TimeSpan.FromMinutes(10), drafts[1].EstimatedDuration); // "email" heuristic
        Assert.Null(drafts[1].Deadline);
        Assert.Equal("from your message", drafts[0].SourceDescription);
    }

    [Theory]
    [InlineData("Review chapter for 45 min", 45)]
    [InlineData("Deep work for 2 hours", 120)]
    [InlineData("It needs two focused sessions", 180)]
    public void ParseDuration_ReadsExplicitDurations(string text, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), LocalHeuristicAiProvider.ParseDuration(text));

    [Theory]
    [InlineData("do it today", 2026, 8, 11)]
    [InlineData("submit tomorrow", 2026, 8, 12)]
    [InlineData("before Friday", 2026, 8, 14)]
    [InlineData("next Tuesday works", 2026, 8, 18)] // same weekday → next week
    public void ParseDeadline_ResolvesRelativeDays(string text, int y, int m, int d)
        => Assert.Equal(new DateOnly(y, m, d), LocalHeuristicAiProvider.ParseDeadline(text, Today));

    [Fact]
    public async Task ExtractTasks_MatchesKnownProjectNames()
    {
        var deca = Project.Create("DECA", ProjectPalette.Colors[0], Now);
        _projects.Add(deca);

        var drafts = await _provider.ExtractTasksAsync("Practice the DECA role-play", Context(), TestContext.Current.CancellationToken);

        Assert.Equal(deca.Id, Assert.Single(drafts).ProjectId);
    }

    [Fact]
    public async Task AnswerQuestion_CitesOnlyTheActiveProjectsResources()
    {
        var admissions = Project.Create("College Admissions", ProjectPalette.Colors[0], Now);
        var deca = Project.Create("DECA", ProjectPalette.Colors[1], Now);
        _projects.Add(admissions);
        _projects.Add(deca);
        var admissionsFile = ProjectFile.Create(admissions.Id, "Metric Proof", null, Now);
        var decaFile = ProjectFile.Create(deca.Id, "Event Prep", null, Now);
        _files.Add(admissionsFile);
        _files.Add(decaFile);

        var note = Resource.CreateNote(
            admissionsFile.Id, "Leadership metrics", "Led three DECA teams; 120 volunteer hours.", Now);
        _resources.Add(note);
        _resources.SetIndexText(note.Id, "Leadership metrics\nLed three DECA teams; 120 volunteer hours.");
        var decoy = Resource.CreateNote(decaFile.Id, "Leadership drills", "Other project leadership notes.", Now);
        _resources.Add(decoy);
        _resources.SetIndexText(decoy.Id, "Leadership drills\nOther project leadership notes.");

        var result = await _provider.AnswerQuestionAsync(admissions.Id, "What leadership metrics do I have?", TestContext.Current.CancellationToken);

        var citation = Assert.Single(result.Citations);
        Assert.Equal(note.Id, citation.Id);
        Assert.Contains("Leadership metrics", result.AnswerText);
        Assert.Contains("Led three DECA teams", result.AnswerText);
    }

    [Fact]
    public async Task AnswerQuestion_WithoutMatches_SaysSoAndCitesNothing()
    {
        var project = Project.Create("Empty", ProjectPalette.Colors[0], Now);
        _projects.Add(project);

        var result = await _provider.AnswerQuestionAsync(project.Id, "What about quantum physics?", TestContext.Current.CancellationToken);

        Assert.Empty(result.Citations);
        Assert.Contains("couldn't find anything", result.AnswerText);
    }

    [Fact]
    public async Task Provider_IsDeterministic()
    {
        const string message = "Draft the essay outline before Sunday and review economics for 45 min";
        var first = await _provider.ExtractTasksAsync(message, Context(), TestContext.Current.CancellationToken);
        var second = await _provider.ExtractTasksAsync(message, Context(), TestContext.Current.CancellationToken);

        Assert.Equal(
            first.Select(d => (d.Title, d.EstimatedDuration, d.Deadline)),
            second.Select(d => (d.Title, d.EstimatedDuration, d.Deadline)));
    }

    public void Dispose() => _database.Dispose();
}
