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

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// Real click paths for commitment completion: the calendar circle marks an occurrence
/// done without opening the editor, the Project page circle shares the same path,
/// accessible names flip between mark/reopen, and locked externals get no control.
/// </summary>
public sealed class CommitmentCompletionUiTests
{
    private sealed record Fixture(
        MainWindow Window,
        ShellViewModel Shell,
        InMemoryCalendarBlockRepository Blocks,
        CalendarBlockId StatsId);

    private static Fixture CreateShellWithLinkedStatsHw()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        TestShell.SeedDesignCalendar(tasks, blocks, clock);
        var projects = new InMemoryProjectRepository();
        var schoolwork = Project.Create("Schoolwork", "#5B8DEF", clock.Now);
        projects.Add(schoolwork);
        var stats = CalendarBlock.CreateFixedCommitment(
            "Stats HW", TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0),
            clock.Now, projectId: schoolwork.Id);
        blocks.Add(stats);
        blocks.Add(CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, null, "Imported standup", TestShell.DesignDate,
            new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.FixedCommitment, null,
            "google", "evt-1", 0, BlockOutcome.None, null, clock.Now, clock.Now));

        var shell = TestShell.Create(tasks: tasks, blocks: blocks, projects: projects);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        window.CaptureRenderedFrame();
        return new Fixture(window, shell, blocks, stats.Id);
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

    [AvaloniaFact]
    public void CalendarCircleClick_MarksDoneThenReopens_WithoutOpeningTheEditor()
    {
        var fixture = CreateShellWithLinkedStatsHw();
        var window = fixture.Window;
        ScrollCalendarTo(window, 780); // bring the 16:00 Stats HW block into view

        var view = FindBlockView(window, "Stats HW");
        var circle = view.FindControl<Button>("CommitmentDoneButton")!;
        Assert.True(circle.IsVisible);
        Assert.Equal("Mark Stats HW done", AutomationProperties.GetName(circle));

        Click(window, circle);

        Assert.Null(fixture.Shell.Calendar.CommitmentEditor); // never the editor
        var doneView = FindBlockView(window, "Stats HW");
        var doneVm = (CalendarBlockViewModel)doneView.DataContext!;
        Assert.True(doneVm.IsDone);
        // Restrained completed treatment: the block's done class drives subdued
        // opacity and the strike-through title.
        Assert.Contains("done", doneView.FindControl<Border>("BlockBorder")!.Classes);
        var doneCircle = doneView.FindControl<Button>("CommitmentDoneButton")!;
        Assert.Equal("Reopen Stats HW", AutomationProperties.GetName(doneCircle));

        Click(window, doneCircle);
        var reopened = FindBlockView(window, "Stats HW");
        Assert.False(((CalendarBlockViewModel)reopened.DataContext!).IsDone);
        Assert.DoesNotContain("done", reopened.FindControl<Border>("BlockBorder")!.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectPageCircleClick_TogglesTheSameCompletion_AndCalendarFollows()
    {
        var fixture = CreateShellWithLinkedStatsHw();
        var window = fixture.Window;
        fixture.Shell.NavigateCommand.Execute(AppSection.Projects);
        fixture.Shell.Projects.OpenProject(
            fixture.Blocks.GetById(fixture.StatsId)!.ProjectId!.Value);
        window.CaptureRenderedFrame();

        var circle = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Mark Stats HW done");
        Click(window, circle);

        var detail = fixture.Shell.Projects.Detail!;
        Assert.Empty(detail.ScheduledBlocks);
        var done = Assert.Single(detail.CompletedScheduledBlocks);
        Assert.True(done.IsDone);

        // The calendar surface reflects it immediately.
        fixture.Shell.NavigateCommand.Execute(AppSection.Calendar);
        window.CaptureRenderedFrame();
        ScrollCalendarTo(window, 780);
        var view = FindBlockView(window, "Stats HW");
        Assert.True(((CalendarBlockViewModel)view.DataContext!).IsDone);

        // Reopen from the Project page updates both again.
        fixture.Shell.NavigateCommand.Execute(AppSection.Projects);
        window.CaptureRenderedFrame();
        var reopen = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == "Reopen Stats HW");
        Click(window, reopen);
        Assert.Single(fixture.Shell.Projects.Detail!.ScheduledBlocks);
        window.Close();
    }

    [AvaloniaFact]
    public void ExternalCommitmentAndTaskBlocks_GetNoCompletionCircle()
    {
        var fixture = CreateShellWithLinkedStatsHw();
        var window = fixture.Window;
        ScrollCalendarTo(window, 700);

        var external = FindBlockView(window, "Imported standup");
        Assert.False(external.FindControl<Button>("CommitmentDoneButton")!.IsVisible);

        var taskBlock = FindBlockView(window, "Practice DECA role-play");
        Assert.False(taskBlock.FindControl<Button>("CommitmentDoneButton")!.IsVisible);
        // Task blocks keep their multi-outcome flyout control instead.
        Assert.True(taskBlock.FindControl<Button>("CompleteButton")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void EditorOpenedFromABlockClick_ShowsTheCompletionCheckbox()
    {
        var fixture = CreateShellWithLinkedStatsHw();
        var window = fixture.Window;
        ScrollCalendarTo(window, 780);

        Click(window, FindBlockView(window, "Stats HW")); // block body → editor
        var editor = fixture.Shell.Calendar.CommitmentEditor;
        Assert.NotNull(editor);
        var checkbox = window.GetVisualDescendants()
            .OfType<CheckBox>()
            .First(c => c.Name == "CommitmentCompletedBox");
        Assert.True(checkbox.IsEffectivelyVisible);
        Assert.False(checkbox.IsChecked ?? false);
        window.Close();
    }
}
