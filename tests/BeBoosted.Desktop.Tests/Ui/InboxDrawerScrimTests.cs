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
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "drag onto the calendar");
    }

    /// <summary>
    /// The Inbox rail toggle is ungated, so the drawer opens over whatever section is
    /// current. Standing in Projects there is no grid behind it at all — the calendar's
    /// own view kind being Week says nothing about what the user is looking at. The
    /// order here matters: the section changes AFTER the hint was legitimately showing,
    /// so the property has to re-announce rather than merely be computed once.
    /// </summary>
    [AvaloniaFact]
    public void TheDragHintIsHiddenInProjects_EvenThoughTheCalendarIsOnWeek()
    {
        var (window, shell) = Show();
        shell.Calendar.ViewKind = CalendarViewKind.Week;
        shell.IsInboxOpen = true;
        window.CaptureRenderedFrame();
        Assert.True(shell.ShowDragHint, "the hint must be showing before the move sideways");

        var announcements = 0;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.ShowDragHint))
            {
                announcements++;
            }
        };

        shell.NavigateCommand.Execute(AppSection.Projects);
        window.CaptureRenderedFrame();

        Assert.Equal(CalendarViewKind.Week, shell.Calendar.ViewKind);
        Assert.True(shell.IsInboxOpen, "the drawer stayed open across the section change");
        Assert.False(shell.ShowDragHint);
        Assert.True(announcements > 0, "the hint must re-announce when the section changes");
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "drag onto the calendar");
    }
}
