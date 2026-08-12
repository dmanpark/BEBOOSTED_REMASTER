using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

public sealed class ShellSmokeTests
{
    [AvaloniaFact]
    public void Shell_RendersRailSectionsAndComposer()
    {
        var shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var railButtons = window.GetVisualDescendants()
            .OfType<ToggleButton>()
            .Where(button => button.Classes.Contains("rail"))
            .ToList();
        Assert.Equal(4, railButtons.Count); // Calendar, Inbox, Projects, Settings

        var composer = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => text.Text?.StartsWith("Tell BeBoosted", StringComparison.Ordinal) == true);
        Assert.NotNull(composer);
    }

    [AvaloniaFact]
    public void Navigating_SwapsSectionView()
    {
        var shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Assert.NotNull(FindDescendant<CalendarView>(window));

        shell.NavigateCommand.Execute(AppSection.Projects);
        window.CaptureRenderedFrame();
        Assert.NotNull(FindDescendant<ProjectsView>(window));

        shell.NavigateCommand.Execute(AppSection.Settings);
        window.CaptureRenderedFrame();
        Assert.NotNull(FindDescendant<SettingsView>(window));
    }

    [AvaloniaFact]
    public void InboxDrawer_AppearsAndCloses()
    {
        var shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell };
        window.Show();

        shell.ToggleInboxCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.NotNull(FindText(window, "Your Inbox is empty."));

        shell.CloseInboxCommand.Execute(null);
        window.CaptureRenderedFrame();
        Assert.Null(FindText(window, "Your Inbox is empty."));
    }

    private static T? FindDescendant<T>(Window window)
        where T : class
        => window.GetVisualDescendants().OfType<T>().FirstOrDefault();

    private static TextBlock? FindText(Window window, string text)
        => window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == text && block.IsEffectivelyVisible);
}
