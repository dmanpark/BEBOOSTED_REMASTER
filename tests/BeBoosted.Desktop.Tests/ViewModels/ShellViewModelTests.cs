using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Prioritization;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public void InboxRanks_FollowTheVisibleDate()
    {
        var ranks = new InMemoryPrioritizationRepository();
        var tasks = TestShell.SeededTasks(new FakeClock(TestShell.DesignDate));
        var shell = TestShell.Create(tasks: tasks, ranks: ranks);
        var first = shell.Inbox.Tasks[0];

        var tomorrow = PlanningPeriod.ForToday(TestShell.DesignDate.AddDays(1));
        ranks.ReplaceRanks(tomorrow.Key, [new PriorityRank(first.Task.Id, 1, PlanningTier.ProtectNow)]);

        Assert.Null(first.RankText); // the visible day (today) has no ranks
        Assert.Equal(TestShell.DesignDate, shell.CurrentPlanningPeriod.Anchor);

        shell.Calendar.GoNextCommand.Execute(null);

        Assert.Equal(tomorrow, shell.CurrentPlanningPeriod);
        Assert.Equal("#1", first.RankText);
    }

    [Fact]
    public void Defaults_ToCalendarSection()
    {
        var shell = TestShell.Create();

        Assert.Same(shell.Calendar, shell.CurrentSection);
        Assert.True(shell.IsCalendarActive);
        Assert.False(shell.IsInboxOpen);
    }

    [Fact]
    public void Navigate_SwitchesSectionAndFlags()
    {
        var shell = TestShell.Create();

        shell.NavigateCommand.Execute(AppSection.Projects);

        Assert.Same(shell.Projects, shell.CurrentSection);
        Assert.True(shell.IsProjectsActive);
        Assert.False(shell.IsCalendarActive);

        shell.NavigateCommand.Execute(AppSection.Settings);
        Assert.Same(shell.Settings, shell.CurrentSection);

        shell.NavigateCommand.Execute(AppSection.Calendar);
        Assert.Same(shell.Calendar, shell.CurrentSection);
    }

    [Fact]
    public void InboxDrawer_TogglesAndCloses()
    {
        var shell = TestShell.Create();

        shell.ToggleInboxCommand.Execute(null);
        Assert.True(shell.IsInboxOpen);

        shell.CloseInboxCommand.Execute(null);
        Assert.False(shell.IsInboxOpen);
    }

    [Fact]
    public void Navigation_DoesNotCloseInboxDrawer()
    {
        var shell = TestShell.Create();
        shell.ToggleInboxCommand.Execute(null);

        shell.NavigateCommand.Execute(AppSection.Projects);

        Assert.True(shell.IsInboxOpen);
    }
}
