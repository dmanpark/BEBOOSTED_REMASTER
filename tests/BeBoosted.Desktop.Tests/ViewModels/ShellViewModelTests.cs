using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;

namespace BeBoosted.Desktop.Tests.ViewModels;

public sealed class ShellViewModelTests
{
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
