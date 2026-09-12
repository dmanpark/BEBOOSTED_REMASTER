using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The same session used to finish three different ways: a checkbox on Today, a
/// flyout on Week for one-off sessions, and a toggle circle for repeating ones - and
/// the one-off flyout's control hid itself once used, so the case you meet most often
/// was the only one you could not undo. Today already had the right model; this pins
/// that Week now matches it.
/// </summary>
public sealed class CompletionParityTests
{
    private static readonly DateOnly Date = TestShell.DesignDate;

    private static (MainWindow Window, ShellViewModel Shell, CalendarBlock Session) ShowWeekWithSession()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);

        var task = TaskItem.Create("Practice DECA role-play", DateTimeOffset.Now);
        tasks.Add(task);
        var session = CalendarBlock.CreateTaskSession(
            task.Id, Date, new TimeOnly(15, 30), new TimeOnly(17, 0), DateTimeOffset.Now);
        blocks.Add(session);

        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.Calendar.ViewKind = CalendarViewKind.Week;
        shell.Calendar.Reload();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        return (window, shell, session);
    }

    private static CalendarBlockViewModel SessionViewModel(MainWindow window, CalendarBlock session)
        => window.GetVisualDescendants().OfType<CalendarBlockView>()
            .Select(v => (CalendarBlockViewModel)v.DataContext!)
            .First(vm => vm.Id == session.Id);

    /// <summary>The defect, stated directly: the control used to remove itself.</summary>
    [AvaloniaFact]
    public void AOneOffSessionsCheckbox_SurvivesBeingDone()
    {
        var (window, _, session) = ShowWeekWithSession();
        var block = SessionViewModel(window, session);
        Assert.True(block.ShowCompletionControl, "the control must exist before it is used");

        block.ToggleSessionDoneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        var afterwards = SessionViewModel(window, session);
        Assert.True(afterwards.IsDone);
        Assert.True(
            afterwards.ShowCompletionControl,
            "a done session must keep its checkbox, or there is no way to undo");
    }

    [AvaloniaFact]
    public void AOneOffSession_UnchecksBackToOpen()
    {
        var (window, _, session) = ShowWeekWithSession();

        SessionViewModel(window, session).ToggleSessionDoneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(SessionViewModel(window, session).IsDone);

        SessionViewModel(window, session).ToggleSessionDoneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(SessionViewModel(window, session).IsDone);
    }

    /// <summary>
    /// The richer outcomes stay reachable - they just move one level down, behind the
    /// same kind of quiet overflow Today uses.
    /// </summary>
    [AvaloniaFact]
    public void TheOtherOutcomes_AreStillReachableFromWeek()
    {
        var (window, _, session) = ShowWeekWithSession();
        var block = SessionViewModel(window, session);

        Assert.True(block.ShowOutcomeAction);

        block.RecordDidntHappenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(SessionViewModel(window, session).IsDone);
    }

    [AvaloniaFact]
    public void TheChipCopy_MatchesTodays()
    {
        var (window, _, _) = ShowWeekWithSession();

        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "outcome?");
    }
}
