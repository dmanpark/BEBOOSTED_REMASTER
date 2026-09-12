using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// Real click paths for per-occurrence completion of repeating tasks: the calendar
/// circle marks only the clicked day's occurrence done without opening the editor, the
/// Project detail shares the same completion (through its own path — it renders one
/// row per task, not per occurrence), accessible names flip between mark/reopen, and
/// one-off sessions and locked externals get no occurrence circle.
/// </summary>
public sealed class OccurrenceCompletionUiTests
{
    private sealed record Fixture(
        MainWindow Window,
        ShellViewModel Shell,
        InMemoryCalendarBlockRepository Blocks,
        InMemoryOccurrenceCompletionRepository Completions,
        CalendarBlockId StatsSessionId,
        ProjectId ProjectId);

    /// <summary>"Stats HW": a Schoolwork task repeating every Tuesday, anchored today.</summary>
    private static Fixture CreateShellWithRepeatingStatsHw()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var completions = new InMemoryOccurrenceCompletionRepository();
        TestShell.SeedDesignCalendar(tasks, blocks, clock);
        var projects = new InMemoryProjectRepository();
        var schoolwork = Project.Create("Schoolwork", "#5B8DEF", clock.Now);
        projects.Add(schoolwork);
        var statsTask = TaskItem.Create("Stats HW", clock.Now, projectId: schoolwork.Id);
        tasks.Add(statsTask);
        var statsSession = CalendarBlock.CreateTaskSession(
            statsTask.Id, TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0),
            clock.Now, RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        blocks.Add(statsSession);
        blocks.Add(CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "Imported standup", TestShell.DesignDate,
            new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.ExternalEvent, null,
            "google", "evt-1", 0, BlockOutcome.None, null, clock.Now, clock.Now));

        var shell = TestShell.Create(
            tasks: tasks, blocks: blocks, projects: projects, completions: completions);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        // Timeline blocks live on the Week surface; Today shows the Daily list.
        shell.Calendar.ViewKind = BeBoosted.Application.Settings.CalendarViewKind.Week;
        window.CaptureRenderedFrame();
        return new Fixture(window, shell, blocks, completions, statsSession.Id, schoolwork.Id);
    }

    private static CalendarBlockView FindBlockView(MainWindow window, string title)
        => window.GetVisualDescendants()
            .OfType<CalendarBlockView>()
            .First(view => (view.DataContext as CalendarBlockViewModel)?.Title == title);

    private static void Click(MainWindow window, Visual target)
    {
        var point = target.TranslatePoint(
            new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();
    }

    private static void ScrollCalendarTo(MainWindow window, double offsetY)
    {
        var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
        surface.FindControl<ScrollViewer>("Scroller")!.Offset = new Vector(0, offsetY);
        window.CaptureRenderedFrame();
    }

    /// <summary>TDD phases 11 and 13: the circle completes only the clicked day.</summary>
    [AvaloniaFact]
    public void CalendarCircleClick_MarksOnlyThisOccurrenceDone_WithoutOpeningTheEditor()
    {
        var fixture = CreateShellWithRepeatingStatsHw();
        var window = fixture.Window;
        ScrollCalendarTo(window, 780); // bring the 16:00 Stats HW block into view

        var view = FindBlockView(window, "Stats HW");
        var circle = view.FindControl<Button>("OccurrenceDoneButton")!;
        Assert.True(circle.IsVisible);
        Assert.Equal("Mark Stats HW done", AutomationProperties.GetName(circle));

        Click(window, circle);

        Assert.Null(fixture.Shell.Calendar.ActiveTaskEditor); // never the editor
        var doneView = FindBlockView(window, "Stats HW");
        var doneVm = (CalendarBlockViewModel)doneView.DataContext!;
        Assert.True(doneVm.IsDone);
        // Only the clicked day's occurrence completed — future ones stay active.
        Assert.NotNull(fixture.Completions.Get(fixture.StatsSessionId, TestShell.DesignDate));
        Assert.Null(fixture.Completions.Get(fixture.StatsSessionId, TestShell.DesignDate.AddDays(7)));
        // Restrained completed treatment: the block's done class drives subdued
        // opacity and the strike-through title.
        Assert.Contains("done", doneView.FindControl<Border>("BlockBorder")!.Classes);
        var doneCircle = doneView.FindControl<Button>("OccurrenceDoneButton")!;
        Assert.Equal("Reopen Stats HW", AutomationProperties.GetName(doneCircle));

        Click(window, doneCircle);
        var reopened = FindBlockView(window, "Stats HW");
        Assert.False(((CalendarBlockViewModel)reopened.DataContext!).IsDone);
        Assert.DoesNotContain("done", reopened.FindControl<Border>("BlockBorder")!.Classes);
        window.Close();
    }

    /// <summary>
    /// The project page no longer renders a row — or a circle — per occurrence: a
    /// repeating task appears once, and its affix names today's occurrence. What this
    /// pins is that the project's single row follows a completion driven from the
    /// calendar: the rendered affix goes on naming the occurrence that was just ticked,
    /// now with its own circle checked, and unchecks again on reopen. It deliberately
    /// does NOT advance to next week's — that would make the completion irreversible
    /// from the project page, which is the whole reason the row keeps naming it.
    /// </summary>
    [AvaloniaFact]
    public void CompletingAnOccurrenceOnTheCalendar_ChecksTheProjectRowsCircle()
    {
        var fixture = CreateShellWithRepeatingStatsHw();
        var window = fixture.Window;
        fixture.Shell.NavigateCommand.Execute(AppSection.Projects);
        fixture.Shell.Projects.OpenProject(fixture.ProjectId);
        window.CaptureRenderedFrame();

        var detail = fixture.Shell.Projects.Detail!;
        var row = Assert.Single(detail.Tasks);
        Assert.Equal(TestShell.DesignDate, row.SessionDate);
        AssertAffixRendered(window, row.StatusText);

        ClickTheOccurrenceCircle(fixture);

        // Only the clicked day's occurrence completed, and the one row reports it in
        // place rather than splitting into a completed row of its own.
        Assert.NotNull(fixture.Completions.Get(fixture.StatsSessionId, TestShell.DesignDate));
        Assert.True(((CalendarBlockViewModel)FindBlockView(window, "Stats HW").DataContext!).IsDone);

        fixture.Shell.NavigateCommand.Execute(AppSection.Projects);
        window.CaptureRenderedFrame();
        var ticked = Assert.Single(fixture.Shell.Projects.Detail!.Tasks);
        Assert.Equal(TestShell.DesignDate, ticked.SessionDate);
        Assert.True(ticked.IsDone);
        AssertAffixRendered(window, ticked.StatusText);

        // Reopening through the same control updates both again.
        ClickTheOccurrenceCircle(fixture);
        Assert.Null(fixture.Completions.Get(fixture.StatsSessionId, TestShell.DesignDate));

        fixture.Shell.NavigateCommand.Execute(AppSection.Projects);
        window.CaptureRenderedFrame();
        var reopened = Assert.Single(fixture.Shell.Projects.Detail!.Tasks);
        Assert.Equal(TestShell.DesignDate, reopened.SessionDate);
        Assert.False(reopened.IsDone);
        AssertAffixRendered(window, reopened.StatusText);
        window.Close();
    }

    /// <summary>
    /// Stands on the Week timeline and clicks the rendered occurrence circle for
    /// today's "Stats HW" — the one control that still completes an occurrence.
    /// </summary>
    private static void ClickTheOccurrenceCircle(Fixture fixture)
    {
        fixture.Shell.NavigateCommand.Execute(AppSection.Calendar);
        fixture.Window.CaptureRenderedFrame();
        ScrollCalendarTo(fixture.Window, 780);
        var view = FindBlockView(fixture.Window, "Stats HW");
        Click(fixture.Window, view.FindControl<Button>("OccurrenceDoneButton")!);
    }

    /// <summary>The affix is the project row's only rendered word about its session.</summary>
    private static void AssertAffixRendered(MainWindow window, string affix)
        => Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == affix);

    [AvaloniaFact]
    public void ExternalEventsAndOneOffSessions_GetNoOccurrenceCircle()
    {
        var fixture = CreateShellWithRepeatingStatsHw();
        var window = fixture.Window;
        ScrollCalendarTo(window, 700);

        var external = FindBlockView(window, "Imported standup");
        Assert.False(external.FindControl<Button>("OccurrenceDoneButton")!.IsVisible);

        var oneOff = FindBlockView(window, "Practice DECA role-play");
        Assert.False(oneOff.FindControl<Button>("OccurrenceDoneButton")!.IsVisible);
        // One-off sessions get the equivalent pair instead: a checkbox, plus the
        // overflow holding the outcomes that are not a simple finish. The checkbox is
        // there at any width — this session shares its hour with Stats HW, which
        // squeezes it to 89px, and the checkbox needs 33.
        Assert.True(oneOff.FindControl<Button>("CompleteButton")!.IsVisible);

        // The overflow is read off a session with its hour to itself, because 89px is
        // below the width at which a block offers one at all: the outcomes would sit
        // beside a title trimmed to an ellipsis. "Draft personal statement" is the same
        // kind of block — a one-off local session — with room to show the pair.
        var roomyOneOff = FindBlockView(window, "Draft personal statement");
        Assert.True(roomyOneOff.Bounds.Width >= 123, "the roomy fixture is no longer roomy");
        Assert.False(roomyOneOff.FindControl<Button>("OccurrenceDoneButton")!.IsVisible);
        Assert.True(roomyOneOff.FindControl<Button>("CompleteButton")!.IsVisible);
        Assert.True(roomyOneOff.FindControl<Button>("OutcomeButton")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void EditorOpenedFromABlockClick_ScopesCompletionToTheOccurrence()
    {
        var fixture = CreateShellWithRepeatingStatsHw();
        var window = fixture.Window;
        ScrollCalendarTo(window, 780);

        Click(window, FindBlockView(window, "Stats HW")); // block body → session editor
        var editor = Assert.IsType<SessionEditorViewModel>(fixture.Shell.Calendar.ActiveTaskEditor);
        Assert.Equal(SessionEditorMode.Repeating, editor.Mode);
        Assert.Equal(TestShell.DesignDate, editor.OccurrenceDate);
        var checkbox = window.GetVisualDescendants()
            .OfType<CheckBox>()
            .First(c => Equals(c.Content, editor.OccurrenceCheckboxText));
        Assert.True(checkbox.IsEffectivelyVisible);
        Assert.False(checkbox.IsChecked ?? false);
        // The occurrence section names the concrete date the checkbox applies to.
        Assert.StartsWith("THIS OCCURRENCE", editor.OccurrenceSectionLabel);
        window.Close();
    }
}
