using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    /// <summary>Shows the Week timeline over whatever the caller has already seeded.</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowWeek(
        InMemoryTaskRepository tasks, InMemoryCalendarBlockRepository blocks)
    {
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.Calendar.ViewKind = CalendarViewKind.Week;
        shell.Calendar.Reload();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        return (window, shell);
    }

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

    /// <summary>Switches the shell to Today and lets its rows rebuild.</summary>
    private static DailyListViewModel ShowToday(ShellViewModel shell)
    {
        shell.Calendar.ViewKind = CalendarViewKind.Today;
        shell.Calendar.Reload();
        Dispatcher.UIThread.RunJobs();
        return shell.Calendar.Daily;
    }

    /// <summary>
    /// Two overlapping sessions at the app's smallest supported window (1100x720)
    /// squeeze a block to about 65px, narrower than the checkbox and the overflow
    /// together - so the overflow is laid out past the block's right edge, measured at
    /// x=100 inside a 65px block. It does not steal the neighbour's clicks, because the
    /// adjacent block paints over it, and that occlusion is what this pins: nothing is
    /// clipped here, so if z-order ever changed, an escaped control would start taking
    /// clicks meant for the block beside it.
    ///
    /// The escape itself is NOT fixed. Making the overflow reachable on a squeezed
    /// block is narrow-column work, which this pass was explicitly scoped out of and
    /// which the P2 backlog owns. Until then the outcomes stay reachable from Today and
    /// from the session editor. If you come here to change the block's layout, that is
    /// the thing to fix, and this test should become a containment assertion.
    /// </summary>
    [AvaloniaFact]
    public void TheOverflowOfASqueezedBlock_DoesNotTakeItsNeighboursClicks()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var sessions = new List<CalendarBlock>();
        foreach (var title in new[] { "Practice DECA role-play", "Prepare regional qualifier binder" })
        {
            var task = TaskItem.Create(title, DateTimeOffset.Now);
            tasks.Add(task);
            var session = CalendarBlock.CreateTaskSession(
                task.Id, Date, new TimeOnly(19, 0), new TimeOnly(20, 0), DateTimeOffset.Now);
            blocks.Add(session);
            sessions.Add(session);
        }

        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1100, Height = 720 };
        window.Show();
        shell.Calendar.ViewKind = CalendarViewKind.Week;
        shell.Calendar.Reload();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        foreach (var session in sessions)
        {
            var view = SessionView(window, session);
            var overflow = view.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Name == "OutcomeButton");

            // The premise, pinned: these blocks really are squeezed narrower than the
            // checkbox and the overflow together, which is what pushes the overflow past
            // the right edge in the first place. Without this the docstring above could
            // quietly become fiction — a layout change that stopped them overlapping
            // would leave nothing beyond the edge and the assertion would still pass.
            Assert.True(
                view.Bounds.Width < 100,
                $"the fixture no longer squeezes the block: it is {view.Bounds.Width} wide, "
                + "so the overflow is not escaping and this test is witnessing nothing");

            // Just past this block's right edge, level with the overflow. Whatever the
            // user hits there belongs to the neighbour, never to this block's controls.
            var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
            var beyond = new Point(
                origin.X + view.Bounds.Width + 4,
                origin.Y + overflow.Bounds.Center.Y);

            var hit = ((Visual)window).GetVisualsAt(beyond).FirstOrDefault();

            // Hitting nothing at all would satisfy the occlusion assertion below without
            // proving anything: the point has to land on the neighbouring block.
            Assert.True(hit is not null, "nothing was hit 4 px past the block's right edge");
            Assert.False(
                ReferenceEquals(hit, overflow) || hit!.GetVisualAncestors().Contains(overflow),
                $"a click {4} px past a {view.Bounds.Width}-wide block reached that block's own "
                + "overflow, so it is stealing the neighbouring block's clicks");
        }
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

        // The outcome itself, not just "still not done" - that was already true before
        // the command ran, so on its own it witnesses nothing.
        var afterwards = SessionViewModel(window, session);
        Assert.Equal(BlockOutcome.DidntHappen, afterwards.Block.Outcome);
        Assert.False(afterwards.IsDone);
    }

    /// <summary>
    /// The branch the undo work exists for: a session renders done because its parent
    /// task was completed as a whole, and the completed-task sweep then suppresses the
    /// task's own row - so the block's checkbox is the only way back. Clearing just the
    /// session would leave the task complete and strand an unresolved session on it.
    /// </summary>
    [AvaloniaFact]
    public void UncheckingASessionMadeDoneByItsTask_ReopensTheTask()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var task = TaskItem.Create("Practice DECA role-play", DateTimeOffset.Now);
        tasks.Add(task);
        var session = CalendarBlock.CreateTaskSession(
            task.Id, Date, new TimeOnly(15, 30), new TimeOnly(17, 0), DateTimeOffset.Now);
        blocks.Add(session);
        var (window, shell) = ShowWeek(tasks, blocks);

        // Completing the task as a whole - what the editor and the Inbox do - is what
        // makes this session render done without an outcome of its own.
        TestShell.CreateCalendarService(blocks, tasks, new FakeClock(Date)).CompleteTask(task.Id);
        shell.Calendar.Reload();
        Dispatcher.UIThread.RunJobs();

        var block = SessionViewModel(window, session);
        Assert.True(tasks.GetById(task.Id)!.IsCompleted);
        Assert.True(block.IsDone);
        Assert.True(block.ShowCompletionControl, "the checkbox is the only way back");

        block.ToggleSessionDoneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Clearing this session's outcome alone would have left the task complete, so
        // the block would re-render done off the task - an unresolved session stranded
        // on a completed task. The aggregate inverse reopens the task instead.
        Assert.False(tasks.GetById(task.Id)!.IsCompleted);
        var reopened = SessionViewModel(window, session);
        Assert.False(reopened.IsDone);
        Assert.Equal(BlockOutcome.None, reopened.Block.Outcome);
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
        var daily = ShowToday(shell);
        var row = daily.ScheduledRows
            .Concat(daily.CompletedRows)
            .Concat(daily.UnscheduledRows)
            .Single(r => r.BlockId == session.Id);
        Assert.False(row.ShowSessionCheck);
        Assert.False(row.ShowSessionOutcomeAction);
    }

    /// <summary>
    /// A proposal wraps no block at all, so it has no outcome and must not start
    /// reporting one now that two controls gate on HasRecordedOutcome.
    /// </summary>
    [AvaloniaFact]
    public void AProposalIsNeverTreatedAsSettled()
    {
        var tasks = new InMemoryTaskRepository();
        // An open, unscheduled task with an estimate is what the planner drafts from.
        tasks.Add(TaskItem.Create(
            "Draft essay outline", DateTimeOffset.Now, estimatedDuration: TimeSpan.FromMinutes(60)));
        var (window, shell) = ShowWeek(tasks, new InMemoryCalendarBlockRepository());

        shell.PlanCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        var proposals = window.GetVisualDescendants().OfType<CalendarBlockView>()
            .Select(v => (CalendarBlockViewModel)v.DataContext!)
            .Where(vm => vm.IsProposal)
            .ToList();

        // Without a drafted proposal on the timeline the assertions below would run
        // over an empty sequence and witness nothing.
        Assert.NotEmpty(proposals);
        Assert.All(proposals, vm => Assert.False(vm.HasRecordedOutcome));
        Assert.All(proposals, vm => Assert.False(vm.ShowCompletionControl));
        Assert.All(proposals, vm => Assert.False(vm.ShowOutcomeAction));
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

    /// <summary>
    /// Keyboard parity, which the checkbox arriving quietly broke: the key handler was
    /// re-pointed at the overflow, whose flyout leads with the "Needs more time" field,
    /// so Enter-Enter on a focused session recorded needs-more-time and Done needed a
    /// Tab into the block. Enter on a session that has a checkbox now does what the
    /// checkbox does.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void PressingEnterOnAFocusedSession_FinishesIt(Key key)
    {
        var (window, _, session) = ShowWeekWithSession();
        var view = SessionView(window, session);
        Assert.True(SessionViewModel(window, session).ShowCompletionControl);

        view.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(key, RawInputModifiers.None, PhysicalKeyOf(key), keySymbol: null);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        // The outcome itself, not merely "something happened": before the fix this key
        // opened a flyout whose first field is Needs more time.
        var afterwards = SessionViewModel(window, session);
        Assert.Equal(BlockOutcome.Done, afterwards.Block.Outcome);
        Assert.True(afterwards.IsDone);
    }

    /// <summary>
    /// The exception the handler has always carried: a session that is already done
    /// falls through to the editor. A stray Enter on a finished block must not quietly
    /// undo it - reopening stays a deliberate click on the checkbox, or a Tab into the
    /// block. The session here is done because its parent task was completed, which is
    /// the case that leaves the checkbox on screen with nothing else beside it.
    /// </summary>
    [AvaloniaFact]
    public void PressingEnterOnAnAlreadyDoneSession_OpensTheEditor_RatherThanUndoing()
    {
        var (window, shell, session) = ShowWeekWithSession();
        SessionViewModel(window, session).ToggleSessionDoneCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        var view = SessionView(window, session);
        Assert.True(SessionViewModel(window, session).IsDone);

        view.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, keySymbol: null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(SessionViewModel(window, session).IsDone, "Enter must not undo a finish");
        Assert.NotNull(shell.Calendar.ActiveTaskEditor);
    }

    private static PhysicalKey PhysicalKeyOf(Key key)
        => key == Key.Enter ? PhysicalKey.Enter : PhysicalKey.Space;

    /// <summary>
    /// The chip only renders on an elapsed session with no outcome yet, so the session
    /// here is a morning one - the clock reads 14:10 on this date. Asserting only that
    /// the old copy is gone would stay green if the chip were deleted outright.
    /// </summary>
    [AvaloniaFact]
    public void TheChipCopy_MatchesTodays()
    {
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var task = TaskItem.Create("Practice DECA role-play", DateTimeOffset.Now);
        tasks.Add(task);
        var elapsed = CalendarBlock.CreateTaskSession(
            task.Id, Date, new TimeOnly(9, 0), new TimeOnly(10, 0), DateTimeOffset.Now);
        blocks.Add(elapsed);
        var (window, _) = ShowWeek(tasks, blocks);

        Assert.True(SessionViewModel(window, elapsed).NeedsOutcome);
        Assert.Contains(
            SessionView(window, elapsed).GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Needs outcome");
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "outcome?");
    }
}
