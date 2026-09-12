using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The drawer floats over an undimmed Today list, so the same unscheduled tasks
/// appear twice at once, and its footer offers to drag onto a calendar that is not
/// behind it.
/// </summary>
public sealed class InboxDrawerScrimTests
{
    private static (MainWindow Window, ShellViewModel Shell) Show()
    {
        var shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        window.CaptureRenderedFrame();
        return (window, shell);
    }

    private static Control? Scrim(MainWindow window)
        => window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == "InboxScrim");

    [AvaloniaFact]
    public void TheScrimIsAbsent_WhileTheDrawerIsClosed()
    {
        var (window, shell) = Show();
        Assert.False(shell.IsInboxOpen);

        Assert.True(Scrim(window) is null or { IsEffectivelyVisible: false });
    }

    [AvaloniaFact]
    public void TheScrimAppears_WithTheDrawer()
    {
        var (window, shell) = Show();

        shell.IsInboxOpen = true;
        window.CaptureRenderedFrame();

        var scrim = Scrim(window);
        Assert.NotNull(scrim);
        Assert.True(scrim.IsEffectivelyVisible, "the surface behind the drawer must recede");
    }

    [AvaloniaFact]
    public void TheDragHintIsHiddenOnToday_BecauseThereIsNoGridToDropOnto()
    {
        var (window, shell) = Show();
        shell.Calendar.ViewKind = CalendarViewKind.Today;
        shell.IsInboxOpen = true;
        window.CaptureRenderedFrame();

        Assert.False(shell.ShowDragHint);
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "drag onto the calendar");
    }

    [AvaloniaFact]
    public void TheDragHintShowsOnWeek()
    {
        var (window, shell) = Show();
        shell.Calendar.ViewKind = CalendarViewKind.Week;
        shell.IsInboxOpen = true;
        window.CaptureRenderedFrame();

        Assert.True(shell.ShowDragHint);
    }
}
