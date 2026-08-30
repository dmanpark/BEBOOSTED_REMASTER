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

    // ---- A repeat sort only asks about new work ----

    private static void Capture(ShellViewModel shell, string title)
    {
        shell.Inbox.CaptureText = title;
        shell.Inbox.CaptureCommand.Execute(null);
    }

    [Fact]
    public void SecondSort_OnlyAsksAboutNewlyCapturedTasks()
    {
        var shell = TestShell.Create();
        Capture(shell, "Essay outline");
        Capture(shell, "Vocab review");
        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseLeftCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);

        Capture(shell, "Lab report"); // the only unranked task
        shell.StartPrioritySortCommand.Execute(null);

        // Every question involves the new task; the settled pair is never re-asked.
        var sort = shell.ActiveSort!;
        var asked = 0;
        while (!sort.IsFinished)
        {
            Assert.Equal("Lab report", sort.LeftCard!.Title);
            asked++;
            sort.ChooseRightCommand.Execute(null);
        }

        Assert.True(asked is >= 1 and <= 2);
    }

    [Fact]
    public void StartPrioritySort_IsDisabled_WhenNothingIsNew()
    {
        var shell = TestShell.Create();
        Capture(shell, "Essay outline");
        Capture(shell, "Vocab review");
        Assert.True(shell.StartPrioritySortCommand.CanExecute(null));

        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseLeftCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);

        Assert.False(shell.StartPrioritySortCommand.CanExecute(null));
    }

    // ---- Re-ranking one task ----

    private static ShellViewModel SortedShell(params string[] titles)
    {
        var shell = TestShell.Create();
        foreach (var title in titles)
        {
            Capture(shell, title);
        }

        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            // The left card is always the task being inserted, so choosing the right
            // one means each new task loses and settles below — i.e. capture order.
            shell.ActiveSort.ChooseRightCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);
        return shell;
    }

    private static string[] RankedTitles(ShellViewModel shell)
        => [.. shell.Inbox.Tasks.OrderBy(r => r.RankText, StringComparer.Ordinal).Select(r => r.Title)];

    [Fact]
    public void Rerank_AsksOnlyAboutTheTargetTask_AndHidesTheEarlyExit()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        Assert.Equal(["Alpha", "Beta", "Gamma"], RankedTitles(shell));
        var gamma = shell.Inbox.Tasks.Single(r => r.Title == "Gamma").Task.Id; // currently #3

        shell.StartRerank(gamma);

        var sort = shell.ActiveSort!;
        Assert.True(sort.IsRerank);
        Assert.False(sort.ShowBuildPlanNow);
        Assert.Contains("Re-rank", sort.StatusLabel, StringComparison.Ordinal);
        Assert.Equal("Where does this belong now?", sort.Prompt);
        while (!sort.IsFinished)
        {
            Assert.Equal("Gamma", sort.LeftCard!.Title);
            Assert.NotEqual("Gamma", sort.RightCard!.Title);
            sort.ChooseLeftCommand.Execute(null); // Gamma wins everything -> #1
        }

        Assert.Equal("Gamma", shell.Inbox.Tasks.Single(r => r.RankText == "#1").Title);
    }

    /// <summary>
    /// The gate is period-scoped, so changing the visible date changes the answer —
    /// the button must be told to re-ask, not wait for an unrelated inbox change.
    /// </summary>
    [Fact]
    public void NavigatingToAnotherDay_RefreshesTheSortGate()
    {
        var shell = SortedShell("Alpha", "Beta"); // today fully ranked → disabled
        Assert.False(shell.StartPrioritySortCommand.CanExecute(null));
        var notified = 0;
        shell.StartPrioritySortCommand.CanExecuteChanged += (_, _) => notified++;

        shell.Calendar.GoNextCommand.Execute(null); // tomorrow holds no ranks yet

        Assert.True(shell.StartPrioritySortCommand.CanExecute(null));
        Assert.True(notified > 0, "navigation must raise CanExecuteChanged");
    }

    /// <summary>
    /// Completing scheduled work never touches the inbox count, yet it changes the
    /// live set — every calendar mutation refreshes the gate.
    /// </summary>
    [Fact]
    public void CalendarMutations_RefreshTheSortGate()
    {
        var shell = TestShell.Create();
        var notified = 0;
        shell.StartPrioritySortCommand.CanExecuteChanged += (_, _) => notified++;

        shell.Calendar.NotifyTasksMutated();

        Assert.True(notified > 0);
    }

    /// <summary>A stale-enabled button must degrade to a quiet no-op, never a throw.</summary>
    [Fact]
    public void StartPrioritySort_WithNothingToAsk_IsAQuietNoOp()
    {
        var shell = TestShell.Create(); // no tasks at all

        shell.StartPrioritySortCommand.Execute(null);

        Assert.Null(shell.ActiveSort);
    }

    [Fact]
    public void InvokingBuildPlanNow_DuringAnUnfinishedRerank_SavesNothing()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var before = RankedTitles(shell);
        var alpha = shell.Inbox.Tasks.Single(r => r.Title == "Alpha").Task.Id; // currently #1

        shell.StartRerank(alpha);
        var sort = shell.ActiveSort!;
        Assert.False(sort.IsFinished);
        sort.BuildPlanNowCommand.Execute(null); // the exit a re-rank must not offer

        Assert.False(sort.IsFinished);
        Assert.Equal(before, RankedTitles(shell));
    }

    /// <summary>
    /// Re-ranking the only ranked task completes instantly (empty seed, nothing to
    /// compare) — the results screen must say why, or it reads like a glitch.
    /// </summary>
    [Fact]
    public void RerankingTheOnlyRankedTask_ExplainsWhyNothingWasAsked()
    {
        var shell = SortedShell("Alpha", "Beta");
        shell.Inbox.Tasks.Single(r => r.Title == "Beta").DeleteCommand.Execute(null);
        var alpha = shell.Inbox.Tasks.Single(r => r.Title == "Alpha").Task.Id;

        shell.StartRerank(alpha);

        var sort = shell.ActiveSort!;
        Assert.True(sort.IsFinished);
        Assert.True(sort.HasResultsNote);
        Assert.Contains("No comparisons", sort.ResultsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ARerankThatAskedQuestions_ShowsNoResultsNote()
    {
        var shell = SortedShell("Alpha", "Beta");
        shell.StartRerank(shell.Inbox.Tasks.Single(r => r.Title == "Alpha").Task.Id);
        var sort = shell.ActiveSort!;
        while (!sort.IsFinished)
        {
            sort.ChooseLeftCommand.Execute(null);
        }

        Assert.False(sort.HasResultsNote);
    }

    [Fact]
    public void AbandoningARerank_LeavesTheSavedRankingUntouched()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var before = RankedTitles(shell);
        var gamma = shell.Inbox.Tasks.Single(r => r.Title == "Gamma").Task.Id;

        shell.StartRerank(gamma);
        shell.ActiveSort!.ChooseLeftCommand.Execute(null); // answer one, then walk away
        shell.ActiveSort.CloseCommand.Execute(null);

        Assert.Null(shell.ActiveSort);
        Assert.Equal(before, RankedTitles(shell));
    }

    [Fact]
    public void Rerank_MovesATaskDown_LeavingTheOthersInOrder()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var alpha = shell.Inbox.Tasks.Single(r => r.Title == "Alpha").Task.Id;

        shell.StartRerank(alpha);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseRightCommand.Execute(null); // Alpha loses everything
        }

        Assert.Equal(["Beta", "Gamma", "Alpha"], RankedTitles(shell));
    }

    // ---- Rank chips are the entry point ----

    [Fact]
    public void TheInboxRankChip_OpensARerank()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var row = shell.Inbox.Tasks.Single(r => r.Title == "Gamma");
        Assert.True(row.HasRank);

        row.RerankCommand.Execute(null);

        Assert.NotNull(shell.ActiveSort);
        Assert.True(shell.ActiveSort.IsRerank);
        Assert.Equal("Gamma", shell.ActiveSort.LeftCard!.Title);
    }

    [Fact]
    public void TheDailyPriorityMarker_OpensARerank()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        shell.Calendar.Reload();
        var row = shell.Calendar.Daily.UnscheduledRows.Single(r => r.Title == "Gamma");
        Assert.True(row.CanRerank);

        row.RerankCommand.Execute(null);

        Assert.NotNull(shell.ActiveSort);
        Assert.True(shell.ActiveSort.IsRerank);
        Assert.Equal("Gamma", shell.ActiveSort.LeftCard!.Title);
    }

    [Fact]
    public void AnUnrankedRow_OffersNoRerank()
    {
        var shell = SortedShell("Alpha", "Beta");
        Capture(shell, "Delta"); // captured after the sort - no rank yet
        shell.Calendar.Reload();

        var delta = shell.Calendar.Daily.UnscheduledRows.Single(r => r.Title == "Delta");
        Assert.False(delta.CanRerank);
        Assert.False(shell.Inbox.Tasks.Single(r => r.Title == "Delta").HasRank);
    }

    // ---- Finishing a sort refreshes the Daily list in place ----

    [Fact]
    public void FinishingSort_RefreshesTheDailyList_WithoutChangingDay()
    {
        var shell = TestShell.Create();
        Capture(shell, "Alpha");
        Capture(shell, "Beta");
        shell.Calendar.Reload(); // the user is looking at Today
        Assert.All(shell.Calendar.Daily.UnscheduledRows, row => Assert.Null(row.Rank));

        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseRightCommand.Execute(null);
        }

        // No day navigation and no manual reload: the ranks must already be there.
        var rows = shell.Calendar.Daily.UnscheduledRows;
        Assert.All(rows, row => Assert.NotNull(row.Rank));
        Assert.Equal(["Alpha", "Beta"], rows.Select(r => r.Title));
        Assert.Equal("P1", rows[0].PriorityText);
        Assert.Equal("P2", rows[1].PriorityText);
    }

    [Fact]
    public void FinishingARerank_ReordersTheDailyList_WithoutChangingDay()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        shell.Calendar.Reload();
        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            shell.Calendar.Daily.UnscheduledRows.Select(r => r.Title));
        var gamma = shell.Inbox.Tasks.Single(r => r.Title == "Gamma").Task.Id;

        shell.StartRerank(gamma);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseLeftCommand.Execute(null); // Gamma wins -> #1
        }

        Assert.Equal(
            ["Gamma", "Alpha", "Beta"],
            shell.Calendar.Daily.UnscheduledRows.Select(r => r.Title));
    }
}
