using BeBoosted.Application.Prioritization;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class PrioritySortViewModelTests
{
    private static readonly PlanningPeriod Period = PlanningPeriod.ForWeek(TestShell.DesignDate);

    private sealed record Context(
        PrioritySortViewModel Sort,
        InMemoryPrioritizationRepository Repository,
        List<TaskItem> Tasks,
        List<bool> Closed,
        List<IReadOnlyList<PriorityRank>> Saved);

    private static Context Create(int taskCount = 3)
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var repository = new InMemoryPrioritizationRepository();
        var service = new PrioritySortService(repository, clock);
        var tasks = new List<TaskItem>();
        for (var i = 0; i < taskCount; i++)
        {
            tasks.Add(TaskItem.Create(
                $"Task {i}",
                clock.Now,
                estimatedDuration: TimeSpan.FromMinutes(30 + (i * 15)),
                deadline: TestShell.DesignDate.AddDays(i + 1)));
        }

        var closed = new List<bool>();
        var saved = new List<IReadOnlyList<PriorityRank>>();
        var sort = new PrioritySortViewModel(
            Period, tasks, service, clock,
            onClosed: () => closed.Add(true),
            onSaved: saved.Add);
        return new Context(sort, repository, tasks, closed, saved);
    }

    [Fact]
    public void ShowsCardsWithDeadlineAndDuration()
    {
        var context = Create();

        Assert.False(context.Sort.IsFinished);
        Assert.Equal("Task 1", context.Sort.LeftCard!.Title);
        Assert.Equal("Task 0", context.Sort.RightCard!.Title);
        Assert.Equal("Tomorrow", context.Sort.RightCard.DueText); // Aug 12
        Assert.Equal("Thursday", context.Sort.LeftCard.DueText);  // Aug 13
        Assert.Equal("45 min estimated", context.Sort.LeftCard.DurationText);
        Assert.Contains("Comparison 1", context.Sort.StatusLabel);
        Assert.Contains("This week", context.Sort.StatusLabel);
    }

    [Fact]
    public void Tie_AdvancesImmediatelyToTheNextComparison()
    {
        var context = Create();

        context.Sort.ChooseTieCommand.Execute(null);

        Assert.False(context.Sort.IsFinished);
        Assert.Equal("Task 2", context.Sort.LeftCard!.Title);
        Assert.Contains("Comparison 2", context.Sort.StatusLabel);
    }

    [Fact]
    public void CompletingAllComparisons_PersistsAndShowsResults()
    {
        var context = Create();

        while (!context.Sort.IsFinished)
        {
            context.Sort.ChooseLeftCommand.Execute(null);
        }

        Assert.Single(context.Saved);
        Assert.Equal(3, context.Repository.GetRanks(Period.Key).Count);
        Assert.NotEmpty(context.Repository.GetDecisions(Period.Key));
        Assert.NotEmpty(context.Sort.ResultGroups);
        Assert.Equal(1, context.Sort.Progress);

        var protect = context.Sort.ResultGroups.First(g => g.Tier == PlanningTier.ProtectNow);
        Assert.Equal("#1", protect.Entries[0].RankText);
    }

    [Fact]
    public void BuildPlanNow_FinishesEarlyWithBestEffortRanks()
    {
        var context = Create(taskCount: 5);
        context.Sort.ChooseLeftCommand.Execute(null);

        context.Sort.BuildPlanNowCommand.Execute(null);

        Assert.True(context.Sort.IsFinished);
        Assert.Equal(5, context.Repository.GetRanks(Period.Key).Count);
        Assert.Single(context.Repository.GetDecisions(Period.Key)); // only the answered pair
    }

    [Fact]
    public void Undo_ReturnsToThePreviousQuestion()
    {
        var context = Create();
        Assert.False(context.Sort.CanUndo);

        context.Sort.ChooseLeftCommand.Execute(null);
        Assert.True(context.Sort.CanUndo);
        var secondQuestion = context.Sort.LeftCard;

        context.Sort.UndoCommand.Execute(null);
        Assert.Equal("Task 1", context.Sort.LeftCard!.Title);
        Assert.NotEqual(secondQuestion, context.Sort.LeftCard);
        Assert.Contains("Comparison 1", context.Sort.StatusLabel);
    }

    [Fact]
    public void Close_InvokesCallbackWithoutSavingWhenUnfinished()
    {
        var context = Create();

        context.Sort.CloseCommand.Execute(null);

        Assert.Single(context.Closed);
        Assert.Empty(context.Repository.GetRanks(Period.Key));
    }
}

public sealed class ShellPrioritySortTests
{
    [Fact]
    public void StartPrioritySort_RequiresTwoTasks()
    {
        var shell = TestShell.Create();
        Assert.False(shell.StartPrioritySortCommand.CanExecute(null));

        shell.Inbox.CaptureText = "One";
        shell.Inbox.CaptureCommand.Execute(null);
        Assert.False(shell.StartPrioritySortCommand.CanExecute(null));

        shell.Inbox.CaptureText = "Two";
        shell.Inbox.CaptureCommand.Execute(null);
        Assert.True(shell.StartPrioritySortCommand.CanExecute(null));
    }

    [Fact]
    public void StartPrioritySort_UsesThePeriodOfTheVisibleCalendarView()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var shell = TestShell.Create(tasks: TestShell.SeededTasks(clock));

        shell.StartPrioritySortCommand.Execute(null);
        Assert.Equal(PlanningPeriodKind.Today, shell.ActiveSort!.Period.Kind);
        shell.ActiveSort.CloseCommand.Execute(null);
        Assert.Null(shell.ActiveSort);

        shell.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;
        shell.StartPrioritySortCommand.Execute(null);
        Assert.Equal(PlanningPeriodKind.Week, shell.ActiveSort!.Period.Kind);
    }

    [Fact]
    public void FinishingSort_ShowsRankChipsOnInboxRows()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var shell = TestShell.Create(tasks: TestShell.SeededTasks(clock));

        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseLeftCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);
        Assert.All(shell.Inbox.Tasks, row => Assert.True(row.HasRank));
        Assert.Contains(shell.Inbox.Tasks, row => row.RankText == "#1");
    }

    [Fact]
    public void Escape_ClosesSortBeforeDrawer()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var shell = TestShell.Create(tasks: TestShell.SeededTasks(clock));
        shell.ToggleInboxCommand.Execute(null);
        shell.StartPrioritySortCommand.Execute(null);

        shell.EscapePressedCommand.Execute(null);
        Assert.Null(shell.ActiveSort);
        Assert.True(shell.IsInboxOpen);

        shell.EscapePressedCommand.Execute(null);
        Assert.False(shell.IsInboxOpen);
    }
}
