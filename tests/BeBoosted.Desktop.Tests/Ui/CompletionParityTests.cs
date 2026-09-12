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

    private static CalendarBlockView SessionView(MainWindow window, CalendarBlock session)
        => window.GetVisualDescendants().OfType<CalendarBlockView>()
            .First(v => ((CalendarBlockViewModel)v.DataContext!).Id == session.Id);

    private static CalendarBlockViewModel SessionViewModel(MainWindow window, CalendarBlock session)
        => (CalendarBlockViewModel)SessionView(window, session).DataContext!;

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

    /// <summary>
    /// The other half of parity: Today collapses both controls once a session is
    /// settled by something other than Done - completing that work again goes through
    /// the task's own row, not a checkbox on a session that already didn't happen.
    /// Week now reaches the same end state.
    /// </summary>
    [AvaloniaFact]
    public void ANonDoneOutcome_CollapsesBothControls_TheWayTodayDoes()
    {
        var (window, shell, session) = ShowWeekWithSession();

        SessionViewModel(window, session).RecordDidntHappenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        var block = SessionViewModel(window, session);
        Assert.False(block.ShowCompletionControl, "a settled session keeps no checkbox");
        Assert.False(block.ShowOutcomeAction, "nor the overflow it was settled from");

        // Now stand where Today stands and look at the very same session. Its rows are
        // only built for the Today view, and a settled session moves to that day's
        // completed history rather than staying scheduled.
        shell.Calendar.ViewKind = CalendarViewKind.Today;
        shell.Calendar.Reload();
        Dispatcher.UIThread.RunJobs();

        var daily = shell.Calendar.Daily;
        var row = daily.ScheduledRows
            .Concat(daily.CompletedRows)
            .Concat(daily.UnscheduledRows)
            .Single(r => r.BlockId == session.Id);
        Assert.False(row.ShowSessionCheck);
        Assert.False(row.ShowSessionOutcomeAction);
    }

    /// <summary>A proposal has no outcome and must not start reporting one.</summary>
    [AvaloniaFact]
    public void AProposalIsNeverTreatedAsSettled()
    {
        var (window, _, _) = ShowWeekWithSession();

        Assert.All(
            window.GetVisualDescendants().OfType<CalendarBlockView>()
                .Select(v => (CalendarBlockViewModel)v.DataContext!)
                .Where(vm => vm.IsProposal),
            vm => Assert.False(vm.HasRecordedOutcome));
    }

    /// <summary>
    /// Done belongs to the checkbox now. Offering it again one click away inside the
    /// overflow is the duplication this task exists to remove - Today's overflow has
    /// never carried it.
    /// </summary>
    [AvaloniaFact]
    public void TheOverflow_DoesNotOfferDoneBesideTheCheckbox()
    {
        var (window, _, session) = ShowWeekWithSession();
        var overflow = SessionView(window, session).FindControl<Button>("OutcomeButton")!;

        var entries = ((StackPanel)((Flyout)overflow.Flyout!).Content!)
            .Children.OfType<Button>().Select(b => b.Content as string).ToList();

        Assert.DoesNotContain("Done", entries);
        Assert.Contains("Didn't happen", entries);
        Assert.Contains("Remove from calendar", entries);
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
