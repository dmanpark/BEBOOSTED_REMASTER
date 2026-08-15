using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Controls;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// BB-QA-001 regression coverage: both calendar modes must expose one continuous
/// 00:00–24:00 timeline — midnight reachable at the top, the complete 23:00 hour
/// reachable at the bottom, a valid offset across view switches, and the composer
/// fixed below the surface without reducing the logical scroll range.
/// </summary>
public sealed class TimelineSurfaceTests
{
    private const double FullDayHeight = 24 * TimelineSurfaceView.DefaultHourHeight;

    private static (MainWindow Window, ShellViewModel Shell) CreateShellWindow(
        double width = 1440, double height = 960)
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        TestShell.SeedDesignCalendar(tasks, blocks, clock);
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        window.CaptureRenderedFrame();
        return (window, shell);
    }

    private static (TimelineSurfaceView Surface, ScrollViewer Scroller) FindSurface(MainWindow window)
    {
        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        var scroller = surface.FindControl<ScrollViewer>("Scroller")!;
        return (surface, scroller);
    }

    [AvaloniaTheory]
    [InlineData(1440, 960)]
    [InlineData(1280, 800)]
    public void Timeline_CoversTheFullDay_InTodayAndWeek(double width, double height)
    {
        var (window, shell) = CreateShellWindow(width, height);
        var (surface, scroller) = FindSurface(window);

        Assert.Equal(0, surface.StartHour);
        Assert.Equal(24, surface.EndHour);
        Assert.True(
            scroller.Extent.Height >= FullDayHeight,
            $"Today extent {scroller.Extent.Height} misses the {FullDayHeight} px day");

        shell.Calendar.ViewKind = CalendarViewKind.Week;
        window.CaptureRenderedFrame();
        (_, scroller) = FindSurface(window);
        Assert.True(
            scroller.Extent.Height >= FullDayHeight,
            $"Week extent {scroller.Extent.Height} misses the {FullDayHeight} px day");
        window.Close();
    }

    [AvaloniaFact]
    public void TopScroll_ExposesMidnightAndItsFirstHour()
    {
        var (window, _) = CreateShellWindow();
        var (surface, scroller) = FindSurface(window);

        scroller.Offset = new Vector(0, 0);
        window.CaptureRenderedFrame();

        // The extent origin must be midnight, so offset 0 shows 12:00 AM and its hour.
        Assert.Equal(0, surface.StartHour);
        Assert.Equal(0, scroller.Offset.Y);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(CalendarViewKind.Today)]
    [InlineData(CalendarViewKind.Week)]
    public void BottomScroll_ExposesTheCompleteElevenPmHour(CalendarViewKind kind)
    {
        var (window, shell) = CreateShellWindow();
        shell.Calendar.ViewKind = kind;
        window.CaptureRenderedFrame();
        var (_, scroller) = FindSurface(window);

        scroller.ScrollToEnd();
        window.CaptureRenderedFrame();

        var viewportBottom = scroller.Offset.Y + scroller.Viewport.Height;
        Assert.True(
            viewportBottom >= scroller.Extent.Height - 0.5,
            $"Bottom offset unreachable: {viewportBottom} < extent {scroller.Extent.Height}");
        Assert.True(
            scroller.Extent.Height >= FullDayHeight,
            $"Extent {scroller.Extent.Height} ends before midnight");
        // The whole 23:00–24:00 row must fit in the viewport at the bottom position.
        Assert.True(
            scroller.Offset.Y <= 23 * TimelineSurfaceView.DefaultHourHeight,
            "The 11 PM row starts above the viewport at the bottom scroll position");
        window.Close();
    }

    [AvaloniaFact]
    public void Composer_StaysBelowTheTimelineViewport()
    {
        var (window, _) = CreateShellWindow();
        var (_, scroller) = FindSurface(window);
        scroller.ScrollToEnd();
        window.CaptureRenderedFrame();

        var composerInput = window.FindControl<TextBox>("ComposerInput")!;
        var composerBar = composerInput.GetVisualAncestors()
            .OfType<Border>()
            .First(border => border.Height == 44);
        var barTop = composerBar.TranslatePoint(default, window)!.Value.Y;
        var scrollerBottom = scroller.TranslatePoint(new Point(0, scroller.Bounds.Height), window)!.Value.Y;

        Assert.True(
            scrollerBottom <= barTop + 0.5,
            $"Timeline viewport bottom {scrollerBottom} runs under the composer at {barTop}");
        window.Close();
    }

    [AvaloniaFact]
    public void SwitchingViews_KeepsTheScrollOffsetValid()
    {
        var (window, shell) = CreateShellWindow();
        var (_, scroller) = FindSurface(window);
        scroller.ScrollToEnd();
        window.CaptureRenderedFrame();

        shell.Calendar.ViewKind = CalendarViewKind.Week;
        window.CaptureRenderedFrame();
        (_, scroller) = FindSurface(window);
        AssertOffsetWithinExtent(scroller);

        shell.Calendar.ViewKind = CalendarViewKind.Today;
        window.CaptureRenderedFrame();
        (_, scroller) = FindSurface(window);
        AssertOffsetWithinExtent(scroller);
        window.Close();
    }

    [AvaloniaFact]
    public void Blocks_AtDayExtremes_ArePositionedInsideTheDay()
    {
        var panel = new TimelinePanel { StartHour = 0, EndHour = 24 };
        var midnight = ChildAt(startMinutes: 0, durationMinutes: 60);
        var lateEvening = ChildAt(startMinutes: 23 * 60, durationMinutes: 60);
        var endsAtMidnight = ChildAt(startMinutes: (23 * 60) + 30, durationMinutes: 60);
        panel.Children.Add(midnight);
        panel.Children.Add(lateEvening);
        panel.Children.Add(endsAtMidnight);

        panel.Measure(new Size(400, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 400, panel.DesiredSize.Height));

        Assert.Equal(FullDayHeight, panel.DesiredSize.Height);
        Assert.Equal(0, midnight.Bounds.Y);
        Assert.Equal(56, midnight.Bounds.Height);
        Assert.Equal(1288, lateEvening.Bounds.Y);
        Assert.Equal(56, lateEvening.Bounds.Height);
        // A block running past midnight is clipped to the day's end, never beyond it.
        Assert.Equal(1316, endsAtMidnight.Bounds.Y);
        Assert.Equal(FullDayHeight, endsAtMidnight.Bounds.Bottom);
    }

    private static ContentPresenter ChildAt(double startMinutes, double durationMinutes)
    {
        var child = new ContentPresenter();
        TimelinePanel.SetStartMinutes(child, startMinutes);
        TimelinePanel.SetDurationMinutes(child, durationMinutes);
        return child;
    }

    private static void AssertOffsetWithinExtent(ScrollViewer scroller)
    {
        var maxOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        Assert.InRange(scroller.Offset.Y, 0, maxOffset + 0.5);
    }
}
