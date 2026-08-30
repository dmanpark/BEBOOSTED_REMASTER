using BeBoosted.Application.Calendar;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class InboxViewModelTests
{
    private sealed record Context(
        InboxViewModel Inbox,
        InMemoryTaskRepository Repository,
        InMemoryCalendarBlockRepository Blocks,
        FakeClock Clock);

    private static Context Create(
        InMemoryTaskRepository? repository = null, ICalendarMutations? mutations = null)
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var repo = repository ?? new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository { Tasks = repo };
        var query = new InboxQueryService(repo, blocks);
        var completions = new InMemoryOccurrenceCompletionRepository();
        var calendar = new CalendarService(
            blocks, completions,
            mutations ?? new InMemoryCalendarMutations(blocks, completions, repo),
            repo, clock);
        var projectRepo = new InMemoryProjectRepository();
        var ai = new BeBoosted.Application.Ai.AiService(
            new BeBoosted.Infrastructure.Ai.LocalHeuristicAiProvider(new InMemoryResourceRepository(), projectRepo),
            new InMemoryAiProvenanceRepository(),
            repo,
            new BeBoosted.Application.Ai.AiPermissionSettings(new InMemorySettingsStore()),
            clock);
        return new Context(
            new InboxViewModel(new TaskService(repo, clock), calendar, query, projectRepo, ai, clock),
            repo, blocks, clock);
    }

    [Fact]
    public void LoadsExistingOpenTasks()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var inbox = Create(TestShell.SeededTasks(clock)).Inbox;

        Assert.Equal(4, inbox.OpenCount);
        Assert.True(inbox.HasTasks);
        Assert.Equal("Finish DECA presentation", inbox.Tasks[0].Title);
    }

    [Fact]
    public void Capture_AddsTaskAndClearsText()
    {
        var context = Create();
        var (inbox, repository) = (context.Inbox, context.Repository);

        inbox.CaptureText = "  Email recommendation request  ";
        inbox.CaptureCommand.Execute(null);

        Assert.Single(inbox.Tasks);
        Assert.Equal("Email recommendation request", inbox.Tasks[0].Title);
        Assert.Equal(string.Empty, inbox.CaptureText);
        Assert.Single(repository.GetOpen());
    }

    [Fact]
    public void Capture_IgnoresBlankInput()
    {
        var context = Create();
        var (inbox, repository) = (context.Inbox, context.Repository);

        inbox.CaptureText = "   ";
        inbox.CaptureCommand.Execute(null);

        Assert.Empty(inbox.Tasks);
        Assert.Empty(repository.GetOpen());
    }

    [Fact]
    public void Complete_RemovesRowAndPersists()
    {
        var context = Create();
        var (inbox, repository) = (context.Inbox, context.Repository);
        inbox.CaptureText = "Review economics chapter";
        inbox.CaptureCommand.Execute(null);
        var row = inbox.Tasks[0];

        row.CompleteCommand.Execute(null);

        Assert.Empty(inbox.Tasks);
        Assert.False(inbox.HasTasks);
        Assert.True(repository.GetAll().Single().IsCompleted);
    }

    [Fact]
    public void Delete_RemovesRowAndTask()
    {
        var context = Create();
        var (inbox, repository) = (context.Inbox, context.Repository);
        inbox.CaptureText = "Mistake";
        inbox.CaptureCommand.Execute(null);

        inbox.Tasks[0].DeleteCommand.Execute(null);

        Assert.Empty(inbox.Tasks);
        Assert.Empty(repository.GetAll());
    }

    [Fact]
    public void Edit_RequestsTheOneCanonicalTaskEditor()
    {
        var context = Create();
        var (inbox, repository) = (context.Inbox, context.Repository);
        inbox.CaptureText = "Draft essay outline";
        inbox.CaptureCommand.Execute(null);
        Domain.TaskId? requested = null;
        inbox.EditRequested += id => requested = id;

        inbox.Tasks[0].EditCommand.Execute(null);

        // The row keeps no form of its own — editing is the shared Task editor's job.
        Assert.Equal(repository.GetAll().Single().Id, requested);
    }

    /// <summary>An open inbox task whose earlier session resolved (Didn't happen).</summary>
    private static TaskItem AddTaskWithResolvedSession(Context context)
    {
        var task = TaskItem.Create("Abandoned errand", context.Clock.Now);
        context.Repository.Add(task);
        var session = CalendarBlock.CreateTaskSession(
            task.Id, TestShell.DesignDate.AddDays(-1), new TimeOnly(9, 0), new TimeOnly(10, 0),
            context.Clock.Now);
        session.RecordOutcome(BlockOutcome.DidntHappen, context.Clock.Now);
        context.Blocks.Add(session);
        context.Inbox.Reload();
        return task;
    }

    /// <summary>
    /// Deleting a task owns its whole aggregate: resolved historical sessions go
    /// with it (no orphans), and the shared mutation chain announces exactly once.
    /// </summary>
    [Fact]
    public void Delete_RemovesResolvedSessions_WithTheTask_AndAnnouncesOnce()
    {
        var context = Create();
        var task = AddTaskWithResolvedSession(context);
        var mutations = 0;
        context.Inbox.TasksMutated += () => mutations++;

        context.Inbox.Tasks.Single(r => r.Title == "Abandoned errand").DeleteCommand.Execute(null);

        Assert.Empty(context.Inbox.Tasks);
        Assert.Null(context.Repository.GetById(task.Id));
        Assert.Empty(context.Blocks.GetForTask(task.Id));
        Assert.Equal(1, mutations);
    }

    /// <summary>A failed delete keeps the row and announces nothing.</summary>
    [Fact]
    public void Delete_WhenTheTransactionFails_KeepsTheRow_AndStaysSilent()
    {
        var context = Create(mutations: new ThrowingMutations());
        var task = AddTaskWithResolvedSession(context);
        var mutations = 0;
        context.Inbox.TasksMutated += () => mutations++;
        var row = context.Inbox.Tasks.Single(r => r.Title == "Abandoned errand");

        Assert.ThrowsAny<InvalidOperationException>(() => row.DeleteCommand.Execute(null));

        Assert.Contains(context.Inbox.Tasks, r => r.Title == "Abandoned errand");
        Assert.NotNull(context.Repository.GetById(task.Id));
        Assert.NotEmpty(context.Blocks.GetForTask(task.Id));
        Assert.Equal(0, mutations);
    }

    private sealed class ThrowingMutations : ICalendarMutations
    {
        public void Execute(
            Action<Application.Calendar.ICalendarBlockRepository,
                IOccurrenceCompletionRepository, ITaskRepository,
                Application.Planning.IPlanningProposalRepository> mutation)
            => throw new InvalidOperationException("injected failure");
    }

    [Fact]
    public void MetaText_UsesRelativeDeadlinesAndDurations()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var inbox = Create(TestShell.SeededTasks(clock)).Inbox;

        Assert.Equal("Fri · 1 h 30 min", inbox.Tasks[0].MetaText); // deadline Aug 14
        Assert.Equal("Sun · 1 h", inbox.Tasks[1].MetaText);        // deadline Aug 16
        Assert.Equal("45 min", inbox.Tasks[2].MetaText);           // no deadline
    }
}
