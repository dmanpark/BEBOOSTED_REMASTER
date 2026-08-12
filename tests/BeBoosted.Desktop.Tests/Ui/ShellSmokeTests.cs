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

    [AvaloniaFact]
    public void InboxDrawer_ShowsCapturedTaskRows()
    {
        var shell = TestShell.Create(tasks: TestShell.SeededTasks(new FakeClock(TestShell.DesignDate)));
        var window = new MainWindow { DataContext = shell };
        window.Show();

        shell.ToggleInboxCommand.Execute(null);
        window.CaptureRenderedFrame();

        Assert.NotNull(FindText(window, "Finish DECA presentation"));
        Assert.NotNull(FindText(window, "Fri · 1 h 30 min"));

        shell.Inbox.CaptureText = "Practice interview answers";
        shell.Inbox.CaptureCommand.Execute(null);
        window.CaptureRenderedFrame();

        Assert.NotNull(FindText(window, "Practice interview answers"));
        Assert.Equal(5, shell.Inbox.OpenCount);
    }

    [AvaloniaFact]
    public void Calendar_RendersSeededBlocks_TodayAndWeek()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        TestShell.SeedDesignCalendar(tasks, blocks, clock);
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        window.CaptureRenderedFrame();

        var todayBlocks = window.GetVisualDescendants().OfType<CalendarBlockView>().ToList();
        Assert.Equal(5, todayBlocks.Count);

        var fixedBlock = todayBlocks.First(v =>
            (v.DataContext as CalendarBlockViewModel)?.Title == "AP Economics");
        Assert.False(((CalendarBlockViewModel)fixedBlock.DataContext!).IsInteractive);

        shell.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;
        window.CaptureRenderedFrame();
        var weekBlocks = window.GetVisualDescendants().OfType<CalendarBlockView>().ToList();
        // Mon–Fri classes (5) + Lunch + DECA meeting + SAT + dinner + 3 task blocks = 12
        Assert.Equal(12, weekBlocks.Count);
        Assert.NotNull(FindText(window, "TUE 11"));
    }

    private static T? FindDescendant<T>(Window window)
        where T : class
        => window.GetVisualDescendants().OfType<T>().FirstOrDefault();

    private static TextBlock? FindText(Window window, string text)
        => window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == text && block.IsEffectivelyVisible);
}
